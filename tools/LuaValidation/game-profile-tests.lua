-- Offline tests for game-plugin/MotionVfxLive/fx_game_profile.lua. No game access.
-- Usage: lua-runner game-profile-tests.lua <path to fx_game_profile.lua>
local source = assert(arg[1], "pass the fx_game_profile.lua path")
local passed = 0
local function check(value, label) assert(value, label); passed = passed + 1; print("PASS " .. label) end

-- Loads a fresh copy with an optional REFramework stub and returns (module, ok, profile or error).
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

-- A fake object graph: every object records the method calls made on it.
local log = {}
local function object(name, methods)
    return { name = name, call = function(self, method, ...)
        log[#log + 1] = self.name .. ":" .. method
        local value = methods[method]
        if type(value) == "function" then return value(...) end
        return value
    end }
end
local function host_for(singletons, components)
    return {
        call = function(o, m, ...) if o == nil then return nil end return o:call(m, ...) end,
        singleton = function(name) log[#log + 1] = "singleton:" .. name; return singletons[name] end,
        typeof = function(name) return name end,
        address = function(o) return o and o.name end,
        scene_components = function(name) log[#log + 1] = "scan:" .. name; return components and components[name] or {} end,
    }
end

do
    local module, ok, profile = fresh(nil)
    check(ok and profile.id == "mhwilds" and module.detected == false, "Without REFramework the profile is MHWilds and marked undetected")
end
do
    local module, ok, profile = fresh("mhwilds")
    check(ok and profile.id == "mhwilds" and module.detected == true, "REFramework game name mhwilds selects the Wilds profile")
    check(profile.efx_suffix == "5571972", "Wilds EFX suffix")
    local weapon = object("weapon", {})
    local character = object("character", { get_Weapon = weapon, get_WeaponType = 3, get_WeaponID = 7, get_WeaponModelID = 9 })
    local master = object("master", { get_Character = character })
    local pm = object("pm", { getMasterPlayer = master })
    log = {}
    local host = host_for({ ["app.PlayerManager"] = pm })
    local found = profile.player.character(host)
    check(found == character and table.concat(log, ",") == "singleton:app.PlayerManager,pm:getMasterPlayer,master:get_Character",
        "Wilds character chain makes the same calls as before profiles")
    check(profile.player.weapon(host, character) == weapon and profile.player.weapon_type(host, character) == 3
        and profile.player.weapon_id(host, character) == 7 and profile.player.weapon_model_id(host, character) == 9,
        "Wilds weapon accessors read the hunter character's getters")
    check(profile.player.character(host_for({})) == nil, "A missing PlayerManager yields nil instead of an error")
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
    local module, ok, profile = fresh("re4")
    check(ok and profile.id == "re4" and module.detected == true, "REFramework game name re4 selects the RE4 profile")
    check(profile.efx_suffix == "3539837" and module.strip_efx_suffix("_Chainsaw/a.efx.3539837", profile) == "_Chainsaw/a.efx",
        "RE4 EFX suffix")
    local weapon = object("arms", {})
    local body = object("body", {})
    local context = object("context", { get_BodyUpdater = body })
    local npc_context = object("npc_context", {})
    local npc_equipment = object("npc_equipment", { get_Context = npc_context })
    local equipment = object("equipment", { getEquipWeapon = weapon, get_ActiveType = 0, get_EquipWeaponID = 4000, get_Context = context })
    local cm = object("cm", { ["getPlayerContextRef()"] = context })
    log = {}
    local host = host_for({ ["chainsaw.CharacterManager"] = cm }, { ["chainsaw.PlayerEquipment"] = { npc_equipment, equipment } })
    check(profile.player.character(host) == body
        and table.concat(log, ",") == "singleton:chainsaw.CharacterManager,cm:getPlayerContextRef(),context:get_BodyUpdater",
        "RE4 character is the player's body updater")
    check(profile.player.weapon(host, body) == weapon and profile.player.weapon_type(host, body) == 0
        and profile.player.weapon_id(host, body) == 4000 and profile.player.weapon_model_id(host, body) == 4000,
        "RE4 weapon accessors use the scene PlayerEquipment whose context is the local player")
    log = {}
    profile.player.weapon_id(host, body)
    local scanned = false
    for _, line in ipairs(log) do if line:find("^scan:") then scanned = true end end
    check(not scanned, "RE4 PlayerEquipment lookup is cached while its context still matches")
    check(profile.player.weapon(host, nil) == nil, "RE4 weapon accessors return nil without a character")
    check(#profile.resource_cache_flags == 1 and profile.resource_cache_flags[1] == "EffectCache", "RE4 has no IgnoreEffectCache flag")
    check(profile.features.equipment_rebuild == false and profile.features.action_slots == false and profile.features.pfb_editing == false,
        "RE4 starts with equipment rebuild, action slots and PFB editing off")
    local by_label = {}
    for _, s in ipairs(profile.capture.specs) do by_label[s[1] .. "." .. s[2]] = s end
    check(#profile.capture.specs == 13, "RE4 installs the 13 engine-level capture hooks")
    local range = by_label["via.effect.script.ObjectEffectManager2.requestEffectFromRangeSquence(via.effect.script.VFXRangeTrack, System.Boolean, System.UInt32, System.UInt32, System.String)"]
    check(range and (range[5] == nil or range[5].node == nil), "RE4 range-sequence hook uses the RE4 signature and has no motion node argument")
    check(by_label["via.effect.script.ObjectEffectManager2.requestEffectInternal(via.effect.script.EffectID, via.GameObject, System.Int32, via.effect.script.EffectManager.WwiseTriggerInfo, via.effect.script.EffectManager.RequestOverwriteParam)"] ~= nil
        and by_label["via.effect.script.ObjectEffectManagerBase.callEventMotionSequenceCreateEffect(via.effect.script.EffectID, via.effect.script.CreatedEffectContainer)"] ~= nil,
        "RE4 requestEffectInternal and motion-sequence hooks use the RE4 signatures")
    for label in pairs(by_label) do assert(not label:find("^app%."), "RE4 must not hook Wilds app.* types: " .. label) end
    check(true, "RE4 hooks no Wilds app.* types")
end
do
    local _, ok, err = fresh("dd2")
    check(not ok and tostring(err):find("does not support", 1, true) ~= nil, "An unsupported REFramework game fails instead of guessing Wilds")
end
print(("Game profile: %d passed; no game access."):format(passed))
