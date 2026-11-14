-- Per-game constants for MotionVfx Live (the Lua half of VfxStudio.Core/Games/GameProfile.cs).
-- Mostly data. The local-player chain differs structurally between games, so it is a set of
-- small accessor functions that only use the caller's `host` (its own sdk/call wrappers), so
-- offline tests that mock sdk see exactly the calls the module made before profiles existed.
-- Detection uses REFramework's game name ("mhwilds", "re4"; re2_framework_log.txt "Game name:").
if _G.MotionVfxGameProfile then return _G.MotionVfxGameProfile end
local M = { version = "1.1.0" }

local OEM, OEM2 = "via.effect.script.ObjectEffectManager", "via.effect.script.ObjectEffectManager2"
local BASE, MANAGER = "via.effect.script.ObjectEffectManagerBase", "via.effect.script.EffectManager"

local profiles = {}

-- host = { call = function(obj, method, ...) end, singleton = function(type_name) end, typeof = function(type_name) end }
-- Every accessor returns nil when a link in the chain is missing; callers decide whether that is fatal.

profiles.mhwilds = {
    id = "mhwilds",
    display_name = "Monster Hunter Wilds",
    efx_suffix = "5571972",
    player = {
        -- app.PlayerManager -> master player -> app.HunterCharacter (a component).
        character = function(host)
            return host.call(host.call(host.singleton("app.PlayerManager"), "getMasterPlayer"), "get_Character")
        end,
        weapon = function(host, character) return host.call(character, "get_Weapon") end,
        weapon_type = function(host, character) return host.call(character, "get_WeaponType") end,
        weapon_id = function(host, character) return host.call(character, "get_WeaponID") end,
        weapon_model_id = function(host, character) return host.call(character, "get_WeaponModelID") end,
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
            { BASE, "callEventMotionSequenceCreateEffect(via.effect.script.EffectID, via.effect.script.CreatedEffectContainer, via.effect.script.ObjectEffectManagerBase.CreateMotionSequenceEffectEventInfo.CallSequenceType)", "event", "oem" },
            { MANAGER, "createEffectInternal(via.GameObject, via.effect.script.EffectManager.RequestInfo)", "engine", "creator", { returns = "objects" } },
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

-- RE4 remake. Type and method names verified against the running game's TDB (2026-11-14).
local function re4_equipment(host, character)
    local go = host.call(character, "get_GameObject")
    return go and host.call(go, "getComponent(System.Type)", host.typeof("chainsaw.PlayerEquipment"))
end
profiles.re4 = {
    id = "re4",
    display_name = "Resident Evil 4",
    efx_suffix = "3539837",
    player = {
        -- chainsaw.CharacterManager -> PlayerBaseContext -> body updater (a component on the body GameObject).
        character = function(host)
            return host.call(host.call(host.singleton("chainsaw.CharacterManager"), "getPlayerContextRef()"), "get_BodyUpdater")
        end,
        -- The equipped weapon (chainsaw.Arms component) lives on the body's chainsaw.PlayerEquipment.
        weapon = function(host, character) return host.call(re4_equipment(host, character), "getEquipWeapon") end,
        weapon_type = function(host, character) return host.call(re4_equipment(host, character), "get_ActiveType") end,
        weapon_id = function(host, character) return host.call(re4_equipment(host, character), "get_EquipWeaponID") end,
        -- RE4 has no separate weapon model id; the weapon id identifies the model.
        weapon_model_id = function(host, character) return host.call(re4_equipment(host, character), "get_EquipWeaponID") end,
    },
    -- RE4 EffectResourceHolder has EffectCache (always false in practice) and no IgnoreEffectCache.
    resource_cache_flags = { "EffectCache" },
    capture = {
        -- Single-player: enemies are not "remote players", so no remote-character test.
        remote_character_type = nil,
        hunter_character_getter = nil,
        shell_base_type = nil,
        -- Engine-level hooks only. RE4 signatures differ from Wilds: requestEffectFromRangeSquence has no
        -- MotionNodeCtrl, requestEffectInternal ends in RequestOverwriteParam, and
        -- callEventMotionSequenceCreateEffect takes two arguments. Game-layer (chainsaw.*) requests are
        -- observed through EffectManager.createEffectInternal until dedicated hooks are verified in game.
        specs = {
            { OEM, "requestEffectVFTriggerTrackByMotionNode(via.motion.MotionNodeCtrl, System.UInt32)", "motion", "oem", { node = 3 } },
            { OEM, "requestEffectVRangeTrackByMotionNode(via.motion.MotionNodeCtrl, System.UInt32)", "motion", "oem", { node = 3 } },
            { OEM, "requestEffectFromRangeSquence(via.effect.script.VFXRangeTrack, System.Boolean, System.UInt32, System.UInt32, System.String)", "motion", "oem" },
            { OEM, "registerCreatedEffectDataList(via.effect.script.CreatedEffectContainer, System.Int32, System.UInt32, System.UInt32)", "register", "oem" },
            { OEM, "requestEffectInternal(via.effect.script.EffectID, via.GameObject, System.Int32, via.effect.script.EffectManager.WwiseTriggerInfo, via.effect.script.EffectManager.RequestOverwriteParam)", "request", "oem", { returns = "container" } },
            { OEM2, "requestEffectVFTriggerTrackByMotionNode(via.motion.MotionNodeCtrl, System.UInt32)", "motion", "oem", { node = 3 } },
            { OEM2, "requestEffectVRangeTrackByMotionNode(via.motion.MotionNodeCtrl, System.UInt32)", "motion", "oem", { node = 3 } },
            { OEM2, "requestEffectFromRangeSquence(via.effect.script.VFXRangeTrack, System.Boolean, System.UInt32, System.UInt32, System.String)", "motion", "oem" },
            { OEM2, "registerCreatedEffectDataList(via.effect.script.CreatedEffectContainer, System.Int32, System.UInt32, System.UInt32)", "register", "oem" },
            { OEM2, "requestEffectInternal(via.effect.script.EffectID, via.GameObject, System.Int32, via.effect.script.EffectManager.WwiseTriggerInfo, via.effect.script.EffectManager.RequestOverwriteParam)", "request", "oem", { returns = "container" } },
            { BASE, "callEventMotionSequenceCreateEffect(via.effect.script.EffectID, via.effect.script.CreatedEffectContainer)", "event", "oem" },
            { MANAGER, "createEffectInternal(via.GameObject, via.effect.script.EffectManager.RequestInfo)", "engine", "creator", { returns = "objects" } },
        },
    },
    features = {
        equipment_rebuild = false,
        -- Planned for RE4 (weapon-resident EFX, effects on actions); not implemented yet.
        action_slots = false,
        pfb_editing = false,
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

-- Builds the `host` the player accessors expect from a module's own call wrapper.
-- `env_sdk` is a function returning the module's sdk at call time (tests swap it per case).
function M.host(call, env_sdk)
    return {
        call = call,
        singleton = function(name) return env_sdk().get_managed_singleton(name) end,
        typeof = function(name) return env_sdk().typeof(name) end,
    }
end

_G.MotionVfxGameProfile = M
return M
