#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#define DIRECTINPUT_VERSION 0x0800
#include <dinput.h>
#include <mutex>
#include "InputState.hpp"
#include "ImportHook.hpp"
#include <reframework/API.h>
extern "C" {
#include <lua.h>
#include <lauxlib.h>
}

namespace {
std::mutex guard;
mvl::InputState input;
lua_State* owner = nullptr;
bool foreground() {
    DWORD process = 0;
    GetWindowThreadProcessId(GetForegroundWindow(), &process);
    return process == GetCurrentProcessId();
}
#include "PollingInput.hpp"
int configure(lua_State* state) {
    // Validate Lua arguments before taking the mutex: luaL_error uses longjmp.
    bool enabled = lua_toboolean(state, 1), recording = lua_toboolean(state, 3);
    luaL_checktype(state, 2, LUA_TTABLE);
    size_t count = lua_rawlen(state, 2);
    if (count < 1 || count > 8) { lua_pushboolean(state, false); lua_pushliteral(state, "Use 1 to 8 keys."); return 2; }
    std::vector<int> keys;
    for (size_t i = 1; i <= count; ++i) {
        lua_rawgeti(state, 2, static_cast<lua_Integer>(i));
        if (!lua_isinteger(state, -1)) { lua_pop(state, 1); lua_pushboolean(state, false); lua_pushliteral(state, "Keys must be integers."); return 2; }
        auto value = lua_tointeger(state, -1); lua_pop(state, 1);
        if (value < 8 || value > 254) { lua_pushboolean(state, false); lua_pushliteral(state, "Invalid key."); return 2; }
        keys.push_back(static_cast<int>(value));
    }
    try {
        std::lock_guard lock(guard);
        if (owner && owner != state && (input.enabled || input.recording)) throw std::runtime_error("Input is owned by another Lua state.");
        input.configure(enabled, recording, keys);
        owner = state;
    } catch (const std::exception& error) { lua_pushboolean(state, false); lua_pushstring(state, error.what()); return 2; }
    lua_pushboolean(state, true); return 1;
}
void boolean(lua_State* s, const char* key, bool value) { lua_pushboolean(s, value); lua_setfield(s, -2, key); }
void number(lua_State* s, const char* key, uint64_t value) { lua_pushinteger(s, static_cast<lua_Integer>(value)); lua_setfield(s, -2, key); }
int sample(lua_State* state) {
    mvl::InputState snapshot;
    {
        std::lock_guard lock(guard);
        std::array<bool, 256> physical{};
        bool focus = foreground();
        if (focus && (input.enabled || input.recording)) for (int k = 8; k < 255; ++k)
            if (mvl::normalize(k) == k) physical[k] = (GetAsyncKeyState(k) & 0x8000) != 0;
        if (owner == state) input.sample(physical, focus, GetTickCount64());
        snapshot = input;
        // Recording also works before enabling debug mode.
        snapshot.down = physical; snapshot.focused = focus;
    }
    lua_newtable(state);
    boolean(state, "focused", snapshot.focused);
    boolean(state, "enabled", snapshot.enabled);
    number(state, "triggers", snapshot.triggers);
    number(state, "blockedRaw", snapshot.blockedRaw);
    number(state, "blockedLegacy", snapshot.blockedLegacy);
    number(state, "blockedPolling", snapshot.blockedPolling);
    number(state, "blockedBuffered", snapshot.blockedBuffered);
    number(state, "asyncCalls", snapshot.asyncCalls);
    number(state, "keyCalls", snapshot.keyCalls);
    number(state, "keyboardCalls", snapshot.keyboardCalls);
    number(state, "rawCalls", snapshot.rawCalls);
    number(state, "directStateCalls", snapshot.directStateCalls);
    number(state, "directDataCalls", snapshot.directDataCalls);
    number(state, "win32Hooks", win32Hooks);
    number(state, "directHooks", directHooks);
    lua_newtable(state); int index = 0;
    for (int k = 8; k < 255; ++k) if (snapshot.down[k]) { lua_pushinteger(state, k); lua_rawseti(state, -2, ++index); }
    lua_setfield(state, -2, "pressed");
    return 1;
}
int disable(lua_State* state) {
    std::lock_guard lock(guard);
    if (owner == state) { input.disable(); owner = nullptr; }
    return 0;
}
void created(lua_State* state) {
    lua_newtable(state);
    lua_pushliteral(state, "1.1.0"); lua_setfield(state, -2, "version");
    lua_pushcfunction(state, configure); lua_setfield(state, -2, "configure");
    lua_pushcfunction(state, sample); lua_setfield(state, -2, "sample");
    lua_pushcfunction(state, disable); lua_setfield(state, -2, "disable");
    lua_setglobal(state, "MotionVfxDebugInput");
}
void destroyed(lua_State* state) {
    std::lock_guard lock(guard);
    if (owner == state) { input.disable(); owner = nullptr; }
}
bool message(void*, unsigned int msg, unsigned long long wp, long long lp) {
    int key = -1, channel = 0; bool up = false;
    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN || msg == WM_KEYUP || msg == WM_SYSKEYUP) {
        key = static_cast<int>(wp); up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
    } else if (msg == WM_INPUT) {
        RAWINPUT raw{}; UINT size = sizeof(raw);
        UINT read = GetRawInputData(reinterpret_cast<HRAWINPUT>(lp), RID_INPUT, &raw, &size, sizeof(RAWINPUTHEADER));
        if (read == static_cast<UINT>(-1) || raw.header.dwType != RIM_TYPEKEYBOARD) return true;
        key = raw.data.keyboard.VKey; up = (raw.data.keyboard.Flags & RI_KEY_BREAK) != 0; channel = 1;
    } else return true;
    std::lock_guard lock(guard);
    return input.allow(key, up, channel, GetTickCount64(), foreground());
}
}
extern "C" __declspec(dllexport) void reframework_plugin_required_version(REFrameworkPluginVersion* version) {
    version->major = REFRAMEWORK_PLUGIN_VERSION_MAJOR;
    version->minor = REFRAMEWORK_PLUGIN_VERSION_MINOR;
    version->patch = REFRAMEWORK_PLUGIN_VERSION_PATCH;
}
extern "C" __declspec(dllexport) bool reframework_plugin_initialize(const REFrameworkPluginInitializeParam* parameter) {
    const auto f = parameter->functions;
    if (!f->on_message(message) || !f->on_lua_state_created(created) || !f->on_lua_state_destroyed(destroyed)) {
        // Some callbacks may already be registered: keep their DLL alive.
        f->log_error("[MotionVfx Debug Input] callback registration failed; polling hooks were not installed.");
        return true;
    }
    install_polling();
    f->log_info("[MotionVfx Debug Input] 1.1.0 loaded; Win32 import hooks=%llu, DirectInput hooks=%llu. Disabled until explicitly enabled.",
        static_cast<unsigned long long>(win32Hooks),static_cast<unsigned long long>(directHooks));
    return true;
}
