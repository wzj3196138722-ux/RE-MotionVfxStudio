-- Per-game constants for MotionVfx Live (the Lua half of VfxStudio.Core/Games/GameProfile.cs).
-- Data only: callers keep making their own sdk calls with these names, so offline tests that
-- mock sdk see exactly the same calls as before. Detection uses REFramework's game name
-- ("mhwilds", "re4"; see re2_framework_log.txt "Game name:").
if _G.MotionVfxGameProfile then return _G.MotionVfxGameProfile end
local M = { version = "1.0.0" }

local OEM, OEM2 = "via.effect.script.ObjectEffectManager", "via.effect.script.ObjectEffectManager2"

local profiles = {}

profiles.mhwilds = {
    id = "mhwilds",
    display_name = "Monster Hunter Wilds",
    efx_suffix = "5571972",
    -- Local player chain: PlayerManager singleton -> master player -> character -> weapon.
    player = {
        manager = "app.PlayerManager",
        master = "getMasterPlayer",
        character = "get_Character",
        weapon = "get_Weapon",
        weapon_type = "get_WeaponType",
        weapon_id = "get_WeaponID",
        weapon_model_id = "get_WeaponModelID",
    },
    -- EffectResourceHolder-side flags snapshotted/restored around a hot swap.
    resource_cache_flags = { "IgnoreEffectCache", "EffectCache" },
    capture = {
        -- A different player character on the hit object marks the request as remote.
        remote_character_type = "app.HunterCharacter",
        hunter_character_getter = "get_HunterCharacter",
        -- Projectiles: walk ShellBase -> ShellOwner to find the shooter.
        shell_base_type = "ace.ShellBase",
        -- Hook specs in install order: {type, signature, kind, owner, extra}.
        specs = {
            { OEM, "requestEffectVFTriggerTrackByMotionNode(via.motion.MotionNodeCtrl, System.UInt32)", "motion", "oem", { node = 3 } },
            { OEM, "requestEffectVRangeTrackByMotionNode(via.motion.MotionNodeCtrl, System.UInt32)", "motion", "oem", { node = 3 } },
            { OEM, "requestEffectFromRangeSquence(via.effect.script.VFXRangeTrack, via.motion.MotionNodeCtrl, System.UInt32, System.UInt32)", "motion", "oem", { node = 4 } },
            { OEM, "registerCreatedEffectDataList(via.effect.script.CreatedEffectContainer, System.Int32, System.UInt32, System.UInt32)", "register", "oem" },
            { OEM, "requestEffectInternal(via.effect.script.EffectID, via.GameObject, System.Int32, via.effect.script.EffectManager.WwiseTriggerInfo, via.effect.script.EffectManager.RequestParam)", "request", "oem", { returns = "container" } },
            { OEM2, "requestEffectVFTriggerTrackByMotionNode(via.motion.MotionNodeCtrl, System.UInt32)", "motion", "oem", { node = 3 } },
            { OEM2, "requestEffectVRangeTrackByMotionNode(via.motion.MotionNodeCtrl, System.UInt32)", "motion", "oem", { node = 3 } },
            { OEM2, "requestEffectFromRangeSquence(via.effect.script.VFXRangeTrack, via.motion.MotionNodeCtrl, System.UInt32, System.UInt32)", "motion", "oem", { node = 4 } },
            { OEM2, "registerCreatedEffectDataList(via.effect.script.CreatedEffectContainer, System.Int32, System.UInt32, System.UInt32)", "register", "oem" },
            { OEM2, "requestEffectInternal(via.effect.script.EffectID, via.GameObject, System.Int32, via.effect.script.EffectManager.WwiseTriggerInfo, via.effect.script.EffectManager.RequestParam)", "request", "oem", { returns = "container" } },
            { "via.effect.script.ObjectEffectManagerBase", "callEventMotionSequenceCreateEffect(via.effect.script.EffectID, via.effect.script.CreatedEffectContainer, via.effect.script.ObjectEffectManagerBase.CreateMotionSequenceEffectEventInfo.CallSequenceType)", "event", "oem" },
            { "via.effect.script.EffectManager", "createEffectInternal(via.GameObject, via.effect.script.EffectManager.RequestInfo)", "engine", "creator", { returns = "objects" } },
            { "app.cEffectController", "playEffectCore(System.UInt32, app.EffectID_Common.ID, via.GameObject, app.cEffectOverwriteParams, via.GameObject)", "play", "controller", { domain = "Common", id = 4, returns = "container" } },
            { "app.cEffectController", "playEffectLoopCore(System.UInt32, app.EffectID_Common.ID, System.UInt64, via.GameObject, app.cEffectOverwriteParams, via.GameObject)", "play", "controller", { domain = "Common", id = 4, returns = "container" } },
            { "app.cEffectController", "playEffectCore(app.EffectID_Hit.ID, via.GameObject, app.cEffectOverwriteParams, via.GameObject)", "play", "controller", { domain = "Hit", id = 3, returns = "container" } },
            { "app.cEffectController", "playEffectCore(app.EffectID_Damage.ID, via.GameObject, app.cEffectOverwriteParams, via.GameObject)", "play", "controller", { domain = "Damage", id = 3, returns = "container" } },
            -- Source-specific shell methods, not the shared effect controller. No scope is retained
            -- after return, and no async attribution is guessed.
            { "app.mcShellCallEffect", "callEffect(app.EffectID_Common.ID, via.vec3, via.Quaternion, System.UInt32, System.Action`1<via.effect.script.CreatedEffectContainer>, app.cEffectOverwriteParams)", "scope", "shell" },
            { "app.mcShellCallEffect", "callEffect(app.EffectID_Common.ID, via.GameObject, System.UInt32, System.Action`1<via.effect.script.CreatedEffectContainer>)", "scope", "shell" },
            { "app.mcShellCallEffect", "callEffectLoop(app.EffectID_Common.ID, via.GameObject, System.UInt32, System.Action`1<via.effect.script.CreatedEffectContainer>)", "scope", "shell" },
            { "app.mcShellCallEffect", "callEffectLoop(app.EffectID_Common.ID, via.vec3, via.Quaternion, System.UInt32, System.UInt32, System.Action`1<via.effect.script.CreatedEffectContainer>, app.cEffectOverwriteParams)", "scope", "shell" },
            { "app.mcShellCallEffect", "callShellExEffect(app.AppShellDef.EX_EFFECT_TYPE, via.vec3, via.Quaternion, app.cEffectOverwriteParams, System.Boolean, System.UInt32)", "play", "shell", { returns = "container" } },
            { "app.mcShellCallEffect", "callShellHitMarkEffect(app.HitInfo, via.vec3, via.Quaternion)", "play", "shell", { returns = "container" } },
        },
    },
    features = {
        -- Weapon/armour PFB preview rebuilds the hunter via changeWeapon/changeParts (Wilds-only APIs).
        equipment_rebuild = true,
        action_slots = true,
        pfb_editing = true,
    },
}

M.profiles = profiles

-- REFramework's own name for the running game, or nil outside REFramework (offline tests).
local function running_game_name()
    local rf = rawget(_G, "reframework")
    if rf == nil then return nil end
    local ok, name = pcall(function() return rf:get_game_name() end)
    if ok and type(name) == "string" and name ~= "" then return name:lower() end
    return nil
end

function M.for_game(name)
    local profile = profiles[name]
    if not profile then error("MotionVfx Live does not support this game: " .. tostring(name)) end
    return profile
end

-- The active profile. Inside REFramework an unsupported game is an error; without
-- REFramework (offline tests) it is MHWilds, the only game supported before profiles.
function M.current()
    if M.active then return M.active end
    local name = running_game_name()
    M.active = M.for_game(name or "mhwilds")
    M.detected = name ~= nil
    return M.active
end

-- Strips this game's EFX version suffix from a resource path.
function M.strip_efx_suffix(path, profile)
    profile = profile or M.current()
    return (path:gsub("%." .. profile.efx_suffix .. "$", ""))
end

_G.MotionVfxGameProfile = M
return M
