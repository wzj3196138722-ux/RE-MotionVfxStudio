-- Offline tests for game-plugin/MotionVfxLive/fx_game_profile.lua. No game access.
-- Usage: lua-runner game-profile-tests.lua <path to fx_game_profile.lua>
local source = assert(arg[1], "pass the fx_game_profile.lua path")
local passed = 0
local function check(value, label) assert(value, label); passed = passed + 1; print("PASS " .. label) end

-- Loads a fresh copy with an optional REFramework stub and returns (module, profile or error).
local function fresh(game_name)
    _G.MotionVfxGameProfile = nil
    local saved = rawget(_G, "reframework")
    _G.reframework = game_name and { get_game_name = function() return game_name end } or nil
    local module = assert(loadfile(source))()
    local ok, profile = pcall(module.current)
    _G.reframework = saved
    _G.MotionVfxGameProfile = nil
    return module, ok, profile
end

do
    local module, ok, profile = fresh(nil)
    check(ok and profile.id == "mhwilds" and module.detected == false, "Without REFramework the profile is MHWilds and marked undetected")
end
do
    local module, ok, profile = fresh("mhwilds")
    check(ok and profile.id == "mhwilds" and module.detected == true, "REFramework game name mhwilds selects the Wilds profile")
    check(profile.efx_suffix == "5571972", "Wilds EFX suffix")
    check(profile.player.manager == "app.PlayerManager" and profile.player.master == "getMasterPlayer"
        and profile.player.character == "get_Character" and profile.player.weapon_id == "get_WeaponID"
        and profile.player.weapon_type == "get_WeaponType" and profile.player.weapon_model_id == "get_WeaponModelID",
        "Wilds local-player accessor names are unchanged")
    check(profile.resource_cache_flags[1] == "IgnoreEffectCache" and profile.resource_cache_flags[2] == "EffectCache",
        "Wilds resource cache flags are unchanged")
    check(profile.features.equipment_rebuild == true and profile.features.action_slots == true and profile.features.pfb_editing == true,
        "Wilds keeps every editor feature")
    local specs = profile.capture.specs
    check(#specs == 22, "Wilds installs the same 22 capture hooks")
    local labels = {}
    for i, s in ipairs(specs) do
        assert(type(s[1]) == "string" and type(s[2]) == "string" and type(s[3]) == "string" and type(s[4]) == "string", "spec " .. i)
        labels[i] = s[1] .. "." .. s[2]
    end
    check(labels[1] == "via.effect.script.ObjectEffectManager.requestEffectVFTriggerTrackByMotionNode(via.motion.MotionNodeCtrl, System.UInt32)"
        and labels[6] == "via.effect.script.ObjectEffectManager2.requestEffectVFTriggerTrackByMotionNode(via.motion.MotionNodeCtrl, System.UInt32)"
        and labels[22] == "app.mcShellCallEffect.callShellHitMarkEffect(app.HitInfo, via.vec3, via.Quaternion)",
        "Capture hook order matches the pre-profile install order")
    check(module.strip_efx_suffix("Art/VFX/a.efx.5571972", profile) == "Art/VFX/a.efx"
        and module.strip_efx_suffix("Art/VFX/a.efx.3539837", profile) == "Art/VFX/a.efx.3539837",
        "Only this game's EFX suffix is stripped")
end
do
    local _, ok, err = fresh("dd2")
    check(not ok and tostring(err):find("does not support", 1, true) ~= nil, "An unsupported REFramework game fails instead of guessing Wilds")
end
print(("Game profile: %d passed; no game access."):format(passed))
