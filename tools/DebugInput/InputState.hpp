#pragma once
#include <array>
#include <algorithm>
#include <cstdint>
#include <vector>
#include <stdexcept>

namespace mvl {
inline int normalize(int key) {
    if (key == 160 || key == 161) return 16;
    if (key == 162 || key == 163) return 17;
    if (key == 164 || key == 165) return 18;
    return key;
}
inline std::vector<int> validate(std::vector<int> keys) {
    if (keys.empty() || keys.size() > 8) throw std::runtime_error("Use 1 to 8 keys.");
    for (int& key : keys) {
        key = normalize(key);
        if (key < 8 || key > 254 || key == 45 || key == 91 || key == 92)
            throw std::runtime_error("Unsupported key; Insert and Windows keys are reserved.");
    }
    std::sort(keys.begin(), keys.end());
    if (std::adjacent_find(keys.begin(), keys.end()) != keys.end()) throw std::runtime_error("Duplicate key.");
    auto has = [&](int k) { return std::find(keys.begin(), keys.end(), k) != keys.end(); };
    if ((has(18) && (has(9) || has(115))) || (has(17) && has(27)) ||
        (has(17) && has(18) && has(46))) throw std::runtime_error("This combination is reserved by Windows.");
    return keys;
}
struct InputState {
    std::vector<int> keys{16, 17, 80};
    std::array<bool, 256> down{};
    // Window messages, raw packets and buffered DirectInput are separate
    // consumers. A release seen on one path must not release another's latch.
    std::array<std::array<bool, 256>, 3> forwarded{}, swallowed{};
    std::array<std::array<bool, 256>, 4> polledSuppressed{};
    bool enabled = false, recording = false, focused = false, armed = false, latched = false;
    uint64_t heartbeat = 0, triggers = 0, blockedLegacy = 0, blockedRaw = 0,
        blockedPolling = 0, blockedBuffered = 0, asyncCalls = 0, keyCalls = 0,
        keyboardCalls = 0, rawCalls = 0, directStateCalls = 0, directDataCalls = 0;
    void configure(bool enable, bool record, std::vector<int> next) {
        next = validate(std::move(next));
        if (enabled != enable || recording != record || next != keys) armed = latched = false;
        keys = std::move(next); enabled = enable; recording = record;
    }
    void disable() { enabled = recording = armed = latched = false; }
    void sample(std::array<bool, 256> physical, bool focus, uint64_t now) {
        heartbeat = now; focused = focus; down = physical;
        if (!focus) { armed = latched = false; down.fill(false); return; }
        bool any = false, all = true;
        for (int key : keys) { any |= down[key]; all &= down[key]; }
        if (!enabled || recording) { armed = latched = false; return; }
        // Every component must be released before the next activation; a held
        // modifier or keyboard repeat cannot flip pause twice.
        if (!any) { armed = true; latched = false; }
        if (armed && !latched && all) { ++triggers; latched = true; }
    }
    bool reserved(int rawKey, uint64_t now, bool foreground) const {
        const int key = normalize(rawKey);
        if (key < 8 || key > 254) return false; // Never take mouse buttons.
        const bool alive = now >= heartbeat && now - heartbeat < 2000;
        return foreground && focused && alive && (recording ||
            (enabled && std::find(keys.begin(), keys.end(), key) != keys.end()));
    }
    bool mask_polled(int rawKey, bool held, uint64_t now, bool foreground, int channel = 0) {
        if (rawKey < 8 || rawKey > 254 || channel < 0 || channel >= 4) return false;
        bool& suppressed = polledSuppressed[channel][rawKey];
        const bool deny = reserved(rawKey, now, foreground) || suppressed;
        // A key consumed while enabled must not reappear as a new keydown
        // when debug mode is disabled while that key is still physically held.
        suppressed = deny && held;
        if (deny && held) ++blockedPolling;
        return deny;
    }
    bool allow(int rawKey, bool up, int channel, uint64_t now, bool foreground, bool peek = false) {
        int key = normalize(rawKey);
        if (key < 0 || key > 255 || channel < 0 || channel > 2) return true;
        const bool reserve = reserved(rawKey, now, foreground);
        auto& passed = forwarded[channel][key];
        auto& blocked = swallowed[channel][key];
        bool deny = false;
        if (up) {
            // Never swallow the release of a key already delivered to the game.
            deny = !passed && (reserve || blocked);
            if (!peek) passed = blocked = false;
        } else if (reserve || blocked) {
            deny = true; blocked = true;
        } else passed = true;
        if (deny) { if (channel == 2) ++blockedBuffered; else if (channel == 1) ++blockedRaw; else ++blockedLegacy; }
        return !deny;
    }
};
}
