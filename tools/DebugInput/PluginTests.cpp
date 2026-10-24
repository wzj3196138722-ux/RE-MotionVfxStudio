#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <iostream>
#include <stdexcept>
#include <reframework/API.h>
extern "C" {
#include <lua.h>
#include <lauxlib.h>
#include <lualib.h>
}
static REFLuaStateCreatedCb createCallback;
static REFLuaStateDestroyedCb destroyCallback;
static REFOnMessageCb messageCallback;
static void run(lua_State* s,const char* code) {
    if (luaL_dostring(s,code)!=LUA_OK) throw std::runtime_error(lua_tostring(s,-1));
}
int main() {
    auto dll=LoadLibraryW(L"MotionVfxDebugInput.dll");
    if(!dll) throw std::runtime_error("Cannot load the built input DLL");
    REFrameworkPluginFunctions f{};
    f.on_lua_state_created=[](REFLuaStateCreatedCb cb){createCallback=cb;return true;};
    f.on_lua_state_destroyed=[](REFLuaStateDestroyedCb cb){destroyCallback=cb;return true;};
    f.on_message=[](REFOnMessageCb cb){messageCallback=cb;return true;};
    f.log_info=[](const char*,...){};
    REFrameworkPluginInitializeParam p{}; p.functions=&f;
    auto initialize=reinterpret_cast<REFPluginInitializeFn>(GetProcAddress(dll,"reframework_plugin_initialize"));
    if(!initialize || !initialize(&p) || !messageCallback) throw std::runtime_error("Plugin callback registration failed");
    auto a=luaL_newstate(), b=luaL_newstate(); luaL_openlibs(a); luaL_openlibs(b);
    createCallback(a); createCallback(b);
    // Real executable import slots must route through the loaded DLL while
    // leaving physical keyboard state unchanged when the adapter is disabled.
    volatile SHORT observed=GetAsyncKeyState(VK_F24);
    observed=GetKeyState(VK_F24);
    BYTE keyboard[256]{}; GetKeyboardState(keyboard);
    run(a,"local s=MotionVfxDebugInput.sample(); assert(s.win32Hooks>=3); assert(s.asyncCalls>0 and s.keyCalls>0 and s.keyboardCalls>0); assert(s.directHooks>=2)");
    run(a,"assert(MotionVfxDebugInput.version=='1.1.0'); assert(MotionVfxDebugInput.configure(true,{17,16,18,80},false)); assert(MotionVfxDebugInput.sample().enabled)");
    run(b,"assert(not MotionVfxDebugInput.configure(true,{80},false))");
    run(a,"assert(not MotionVfxDebugInput.configure(true,{17,17},false)); assert(MotionVfxDebugInput.sample().enabled)");
    destroyCallback(a); lua_close(a);
    run(b,"assert(not MotionVfxDebugInput.sample().enabled); assert(MotionVfxDebugInput.configure(true,{80},false)); MotionVfxDebugInput.disable(); assert(not MotionVfxDebugInput.sample().enabled)");
    destroyCallback(b); lua_close(b);
    FreeLibrary(dll);
    observed=GetAsyncKeyState(VK_F24); // Import restoration: no unloaded callback.
    std::cout << "PASS: actual DLL registration, cross-module Lua bindings, validation, ownership and teardown\n";
}
