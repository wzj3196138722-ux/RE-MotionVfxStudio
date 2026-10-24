#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define DIRECTINPUT_VERSION 0x0800
#include <windows.h>
#include <dinput.h>
#include <mutex>
#include <iostream>
#include <string>
#include "InputState.hpp"
#include "ImportHook.hpp"
namespace {
std::mutex guard;
mvl::InputState input;
bool foreground(){return true;}
#include "PollingInput.hpp"
int checks=0;
void check(bool value){if(!value) throw std::runtime_error("Polling adapter check "+std::to_string(checks+1)); ++checks;}
SHORT WINAPI physical(int key){return key==VK_SPACE || key=='W' ? static_cast<SHORT>(0x8001) : 0;}
HRESULT WINAPI state_fixture(void*,DWORD size,void* data){
    memset(data,0,size);
    if(size==256){static_cast<BYTE*>(data)[DIK_SPACE]=0x80;static_cast<BYTE*>(data)[DIK_W]=0x80;}
    return DI_OK;
}
HRESULT WINAPI chord_fixture(void*,DWORD size,void* data){
    memset(data,0,size);
    for(int key:{DIK_LCONTROL,DIK_RSHIFT,DIK_LMENU,DIK_P,DIK_W}) static_cast<BYTE*>(data)[key]=0x80;
    return DI_OK;
}
HRESULT WINAPI caps_fixture(void*,DIDEVCAPS* caps){caps->dwDevType=DI8DEVTYPE_KEYBOARD;return DI_OK;}
HRESULT WINAPI object_fixture(void*,void* object,DWORD offset,DWORD){
    auto info=static_cast<DIDEVICEOBJECTINSTANCEW*>(object);
    info->guidType=GUID_Key;info->dwType=DIDFT_BUTTON|DIDFT_MAKEINSTANCE(offset);return DI_OK;
}
HRESULT WINAPI data_fixture(void*,DWORD stride,DIDEVICEOBJECTDATA* data,DWORD* count,DWORD){
    if(!data) return DI_OK;
    const DWORD offsets[]={DIK_SPACE,DIK_W,DIK_SPACE,DIK_W};
    const DWORD values[]={0x80,0x80,0,0};
    *count=4;
    for(int i=0;i<4;++i){
        auto event=reinterpret_cast<DWORD*>(reinterpret_cast<BYTE*>(data)+i*stride);
        event[0]=offsets[i];event[1]=values[i];event[2]=100+i;event[3]=200+i;
    }
    return DI_BUFFEROVERFLOW;
}
UINT WINAPI raw_fixture(HRAWINPUT,UINT,void* data,UINT*,UINT){
    auto raw=static_cast<RAWINPUT*>(data); *raw={};
    raw->header.dwType=RIM_TYPEKEYBOARD;raw->header.dwSize=sizeof(*raw);
    raw->data.keyboard.VKey=VK_SPACE;raw->data.keyboard.Message=WM_KEYDOWN;
    return sizeof(*raw);
}
}
int main(){
    input.configure(true,false,{VK_SPACE}); input.sample({},true,GetTickCount64());
    originalAsync=reinterpret_cast<void*>(physical);
    originalKey=reinterpret_cast<void*>(physical);
    check(async_key(VK_SPACE)==0); check(key_state(VK_SPACE)==0);
    check(async_key('W')==physical('W')); check(key_state('W')==physical('W'));
    // Our physical sampler bypasses the game-facing wrappers.
    auto down=std::array<bool,256>{}; down[VK_SPACE]=true;
    input.sample(down,true,GetTickCount64()); check(input.triggers==1);
    void* vtable[16]{}; vtable[3]=reinterpret_cast<void*>(caps_fixture);
    vtable[14]=reinterpret_cast<void*>(object_fixture);
    void** fake=vtable;
    devices[0]={vtable,reinterpret_cast<void*>(state_fixture),reinterpret_cast<void*>(data_fixture),true};
    BYTE state[256]{};
    check(SUCCEEDED(direct_state(&fake,256,state)));
    check(state[DIK_SPACE]==0); check(state[DIK_W]==0x80);
    DIDEVICEOBJECTDATA events[4]{}; DWORD count=4;
    check(direct_data(&fake,sizeof(events[0]),events,&count,0)==DI_BUFFEROVERFLOW);
    check(count==2); check(events[0].dwOfs==DIK_W && events[0].dwData==0x80 && events[0].dwTimeStamp==101);
    check(events[1].dwOfs==DIK_W && events[1].dwData==0 && events[1].dwTimeStamp==103);
    // PEEK does not consume releases or expose the filtered entries.
    count=4; check(SUCCEEDED(direct_data(&fake,sizeof(events[0]),events,&count,DIGDD_PEEK))); check(count==2);
    count=4; check(SUCCEEDED(direct_data(&fake,sizeof(events[0]),events,&count,0))); check(count==2);
    originalRaw=reinterpret_cast<void*>(raw_fixture);
    RAWINPUT raw{}; UINT size=sizeof(raw);
    check(raw_data(nullptr,RID_INPUT,&raw,&size,sizeof(RAWINPUTHEADER))==sizeof(raw));
    check(raw.header.dwType==RIM_TYPEKEYBOARD && raw.data.keyboard.VKey==255);
    input.disable();
    check(async_key(VK_SPACE)==0); // held Space must remain consumed
    // Actual release clears each reader's latch independently.
    originalAsync=reinterpret_cast<void*>(+[](int)->SHORT{return 0;});
    check(async_key(VK_SPACE)==0); originalAsync=reinterpret_cast<void*>(physical);
    check(async_key(VK_SPACE)==physical(VK_SPACE));
    check(input.blockedPolling>0 && input.blockedBuffered>0 && input.blockedRaw>0);
    input.configure(true,false,{VK_CONTROL,VK_SHIFT,VK_MENU,'P'}); input.sample({},true,GetTickCount64());
    devices[0].state=reinterpret_cast<void*>(chord_fixture);
    check(SUCCEEDED(direct_state(&fake,256,state)));
    for(int key:{DIK_LCONTROL,DIK_RSHIFT,DIK_LMENU,DIK_P}) check(state[key]==0);
    check(state[DIK_W]==0x80);
    input.disable(); // long-held modifiers stay consumed until released
    check(SUCCEEDED(direct_state(&fake,256,state)));
    for(int key:{DIK_LCONTROL,DIK_RSHIFT,DIK_LMENU,DIK_P}) check(state[key]==0);
    devices[0].state=reinterpret_cast<void*>(+[](void*,DWORD bytes,void* data)->HRESULT{memset(data,0,bytes);return DI_OK;});
    check(SUCCEEDED(direct_state(&fake,256,state)));
    devices[0].state=reinterpret_cast<void*>(chord_fixture);
    check(SUCCEEDED(direct_state(&fake,256,state)));
    for(int key:{DIK_LCONTROL,DIK_RSHIFT,DIK_LMENU,DIK_P}) check(state[key]==0x80);
    std::cout<<"PASS: "<<checks<<" game-facing keyboard polling and buffered-event checks\n";
}
