#pragma once
#include <dinput.h>
#include "ImportHook.hpp"

// Included inside the adapter's anonymous namespace, after guard/input/focus.
// Each hook calls the previous function first and changes only the keyboard
// result returned to the game. The plugin's physical sampler stays untouched.
std::vector<mvl::PointerPatch> pollingPatches;
void* originalAsync = nullptr;
void* originalKey = nullptr;
void* originalKeyboard = nullptr;
void* originalRaw = nullptr;
size_t win32Hooks=0, directHooks=0;
struct DeviceHooks {
    void** vtable=nullptr;
    void* state=nullptr;
    void* data=nullptr;
    bool wide=false;
};
std::array<DeviceHooks,2> devices{};
using StateFunction = HRESULT (WINAPI*)(void*,DWORD,void*);
using DataFunction = HRESULT (WINAPI*)(void*,DWORD,DIDEVICEOBJECTDATA*,DWORD*,DWORD);

SHORT WINAPI async_key(int key) {
    const SHORT value = reinterpret_cast<decltype(&GetAsyncKeyState)>(originalAsync)(key);
    std::lock_guard lock(guard); ++input.asyncCalls;
    return input.mask_polled(key,(value & 0x8000)!=0,GetTickCount64(),foreground()) ? 0 : value;
}
SHORT WINAPI key_state(int key) {
    const SHORT value = reinterpret_cast<decltype(&GetKeyState)>(originalKey)(key);
    std::lock_guard lock(guard); ++input.keyCalls;
    return input.mask_polled(key,(value & 0x8000)!=0,GetTickCount64(),foreground(),1) ? 0 : value;
}
BOOL WINAPI keyboard_state(PBYTE values) {
    const BOOL result = reinterpret_cast<decltype(&GetKeyboardState)>(originalKeyboard)(values);
    if (!result || !values) return result;
    std::lock_guard lock(guard); ++input.keyboardCalls;
    const auto now=GetTickCount64(); const bool focus=foreground();
    for(int key=8;key<255;++key)
        if(input.mask_polled(key,(values[key]&0x80)!=0,now,focus,2)) values[key]=0;
    return result;
}
UINT WINAPI raw_data(HRAWINPUT handle,UINT command,void* data,UINT* size,UINT headerSize) {
    const UINT result = reinterpret_cast<decltype(&GetRawInputData)>(originalRaw)(handle,command,data,size,headerSize);
    if (result==static_cast<UINT>(-1) || command!=RID_INPUT || !data ||
        result<sizeof(RAWINPUTHEADER)+sizeof(RAWKEYBOARD)) return result;
    auto raw=static_cast<RAWINPUT*>(data);
    if(raw->header.dwType!=RIM_TYPEKEYBOARD) return result;
    std::lock_guard lock(guard); ++input.rawCalls;
    if(!input.allow(raw->data.keyboard.VKey,(raw->data.keyboard.Flags&RI_KEY_BREAK)!=0,1,GetTickCount64(),foreground())) {
        // Keep the valid packet/header length; 255 is the fake key marker
        // discarded by keyboard readers. Never alter mouse or HID packets.
        raw->data.keyboard={};
        raw->data.keyboard.VKey=255;
        raw->data.keyboard.Flags=RI_KEY_BREAK;
        raw->data.keyboard.Message=WM_KEYUP;
    }
    return result;
}
DeviceHooks device_hooks(void* device) {
    std::lock_guard lock(guard);
    auto table=*static_cast<void***>(device);
    for(const auto& item:devices) if(item.vtable==table) return item;
    return {};
}
bool is_keyboard(void* device) {
    DIDEVCAPS caps{}; caps.dwSize=sizeof(caps);
    auto getCaps=reinterpret_cast<HRESULT (WINAPI*)(void*,DIDEVCAPS*)>((*static_cast<void***>(device))[3]);
    return SUCCEEDED(getCaps(device,&caps)) && GET_DIDEVICE_TYPE(caps.dwDevType)==DI8DEVTYPE_KEYBOARD;
}
int scan_key(DWORD scan,HKL layout=GetKeyboardLayout(0)) {
    if(scan>255) return 0;
    if(scan==DIK_PAUSE) return VK_PAUSE;
    const UINT extended=(scan&0x80) ? 0xe000 : 0;
    return static_cast<int>(MapVirtualKeyExW((scan&0x7f)|extended,MAPVK_VSC_TO_VK_EX,layout));
}
const std::array<int,256>& keyboard_map() {
    // Called under guard; avoid 256 layout/API queries on every game poll.
    static HKL cached=nullptr;
    static std::array<int,256> keys{};
    const auto layout=GetKeyboardLayout(0);
    if(layout!=cached) {
        for(DWORD scan=0;scan<256;++scan) keys[scan]=scan_key(scan,layout);
        cached=layout;
    }
    return keys;
}
HRESULT WINAPI direct_state(void* device,DWORD bytes,void* data) {
    auto hooks=device_hooks(device);
    if(!hooks.state) return DIERR_GENERIC;
    auto result=reinterpret_cast<StateFunction>(hooks.state)(device,bytes,data);
    if(FAILED(result) || bytes!=256 || !data || !is_keyboard(device)) return result;
    std::lock_guard lock(guard); ++input.directStateCalls;
    auto values=static_cast<unsigned char*>(data);
    const auto now=GetTickCount64(); const bool focus=foreground();
    // c_dfDIKeyboard's 256 bytes use DIK scan codes, not Windows VK codes.
    std::array<bool,256> held{},mask{};
    const auto& keys=keyboard_map();
    for(DWORD scan=0;scan<256;++scan) {
        if(keys[scan]>0 && keys[scan]<256) held[keys[scan]]|=(values[scan]&0x80)!=0;
    }
    for(int key=8;key<255;++key) mask[key]=input.mask_polled(key,held[key],now,focus,3);
    for(DWORD scan=0;scan<256;++scan) {
        const int key=keys[scan];
        if(key>0 && key<256 && mask[key]) values[scan]=0;
    }
    return result;
}
HRESULT WINAPI direct_data(void* device,DWORD stride,DIDEVICEOBJECTDATA* data,DWORD* count,DWORD flags) {
    auto hooks=device_hooks(device);
    if(!hooks.data) return DIERR_GENERIC;
    const DWORD capacity=count?*count:0;
    auto result=reinterpret_cast<DataFunction>(hooks.data)(device,stride,data,count,flags);
    if(FAILED(result) || !data || !count || stride<sizeof(DWORD)*2 || *count>capacity || !is_keyboard(device)) return result;
    // Resolve each object by offset; buffered data can use a custom format.
    auto getObject=reinterpret_cast<HRESULT (WINAPI*)(void*,void*,DWORD,DWORD)>((*static_cast<void***>(device))[14]);
    DWORD written=0;
    for(DWORD index=0;index<*count;++index) {
        auto event=reinterpret_cast<unsigned char*>(data)+static_cast<size_t>(index)*stride;
        auto values=reinterpret_cast<DWORD*>(event);
        DIDEVICEOBJECTINSTANCEW info{};
        info.dwSize=hooks.wide?sizeof(DIDEVICEOBJECTINSTANCEW):sizeof(DIDEVICEOBJECTINSTANCEA);
        int key=0;
        if(SUCCEEDED(getObject(device,&info,values[0],DIPH_BYOFFSET)) && info.guidType==GUID_Key)
            key=scan_key(DIDFT_GETINSTANCE(info.dwType));
        bool allowed=true;
        {
            std::lock_guard lock(guard);
            if(key) allowed=input.allow(key,(values[1]&0x80)==0,2,GetTickCount64(),foreground(),(flags&DIGDD_PEEK)!=0);
        }
        if(allowed) {
            if(written!=index) memmove(reinterpret_cast<unsigned char*>(data)+static_cast<size_t>(written)*stride,event,stride);
            ++written;
        }
    }
    *count=written;
    {std::lock_guard lock(guard); ++input.directDataCalls;}
    return result; // Preserve success/overflow semantics and other event order.
}
void install_direct_input(bool wide) {
    // The game's dinput8 proxy forwards DirectInput8Create. Like REFramework,
    // create an unacquired keyboard to discover the COM vtable; no raw offsets.
    static HMODULE library=LoadLibraryW(L"dinput8.dll");
    if(!library) return;
    auto create=reinterpret_cast<HRESULT (WINAPI*)(HINSTANCE,DWORD,REFIID,void**,IUnknown*)>(
        GetProcAddress(library,"DirectInput8Create"));
    if(!create) return;
    IDirectInput8W* factory=nullptr;
    if(FAILED(create(GetModuleHandleW(nullptr),DIRECTINPUT_VERSION,wide?IID_IDirectInput8W:IID_IDirectInput8A,
        reinterpret_cast<void**>(&factory),nullptr))) return;
    IDirectInputDevice8W* device=nullptr;
    auto result=factory->CreateDevice(GUID_SysKeyboard,&device,nullptr);
    if(SUCCEEDED(result) && device) {
        auto table=*reinterpret_cast<void***>(device);
        {
            std::lock_guard lock(guard);
            bool existing=false;
            for(auto& item:devices) if(item.vtable==table) existing=true;
            if(!existing) for(auto& item:devices) if(!item.vtable) {
                item={table,table[9],table[10],wide};
                if(mvl::replace_pointer(table+9,item.state,reinterpret_cast<void*>(direct_state))) {
                    pollingPatches.push_back({table+9,item.state,reinterpret_cast<void*>(direct_state)}); ++directHooks;
                }
                if(mvl::replace_pointer(table+10,item.data,reinterpret_cast<void*>(direct_data))) {
                    pollingPatches.push_back({table+10,item.data,reinterpret_cast<void*>(direct_data)}); ++directHooks;
                }
                break;
            }
        }
        device->Release();
    }
    factory->Release();
}
void install_polling() {
    auto main=GetModuleHandleW(nullptr);
    win32Hooks+=mvl::hook_import(main,"GetAsyncKeyState",reinterpret_cast<void*>(async_key),originalAsync,pollingPatches);
    win32Hooks+=mvl::hook_import(main,"GetKeyState",reinterpret_cast<void*>(key_state),originalKey,pollingPatches);
    win32Hooks+=mvl::hook_import(main,"GetKeyboardState",reinterpret_cast<void*>(keyboard_state),originalKeyboard,pollingPatches);
    win32Hooks+=mvl::hook_import(main,"GetRawInputData",reinterpret_cast<void*>(raw_data),originalRaw,pollingPatches);
    install_direct_input(false);
    install_direct_input(true);
}
// On unload restore only slots still owned by us; never overwrite a hook
// another plugin has installed later. No COM calls under the loader lock.
struct RestorePolling { ~RestorePolling(){mvl::restore_pointers(pollingPatches);} } restorePolling;
