-- Offline evidence tests. No game, network, hook injection, or native writes.
-- Run with game-plugin as cwd using tools/LuaValidation/lua-runner.exe.
local passed, failed, checks = 0, 0, 0
local capture_source = arg[1] or "MotionVfxLive/fx_action_capture.lua"
local function check(value, message) checks = checks + 1; assert(value, message or "assertion failed") end
local function eq(a,b,message) check(a == b, (message or "value") .. ": " .. tostring(a) .. " ~= " .. tostring(b)) end
local function test(name, fn)
    local ok, err = pcall(fn)
    if ok then passed = passed + 1; print("PASS " .. name)
    else failed = failed + 1; print("FAIL " .. name .. ": " .. tostring(err)) end
end
local OEM, OEM2 = "via.effect.script.ObjectEffectManager", "via.effect.script.ObjectEffectManager2"
local BASE, MANAGER = "via.effect.script.ObjectEffectManagerBase", "via.effect.script.EffectManager"
local MOTION = "requestEffectVFTriggerTrackByMotionNode(via.motion.MotionNodeCtrl, System.UInt32)"
local CREATE = "createEffectInternal(via.GameObject, via.effect.script.EffectManager.RequestInfo)"
local EVENT = "callEventMotionSequenceCreateEffect(via.effect.script.EffectID, via.effect.script.CreatedEffectContainer, via.effect.script.ObjectEffectManagerBase.CreateMotionSequenceEffectEventInfo.CallSequenceType)"
local REGISTER = "registerCreatedEffectDataList(via.effect.script.CreatedEffectContainer, System.Int32, System.UInt32, System.UInt32)"
local SHELL = "app.mcShellCallEffect"
local SHELL_COMMON = "callEffect(app.EffectID_Common.ID, via.GameObject, System.UInt32, System.Action`1<via.effect.script.CreatedEffectContainer>)"
local SHELL_HIT = "callShellHitMarkEffect(app.HitInfo, via.vec3, via.Quaternion)"
local COMMON = "playEffectCore(System.UInt32, app.EffectID_Common.ID, via.GameObject, app.cEffectOverwriteParams, via.GameObject)"
local function environment(options)
    options = options or {}
    local c = {hooks = {}, callbacks = {}, resets = {}, installs = 0, calls = {}, tid = 1, stores = {}, nextAddress = 100, scene = 10,
        metrics = {threadIds = 0, sceneTypeLookups = 0, sceneReads = 0}}
    local env = setmetatable({}, {__index = _G}); env._G = env
    local parent_types = {[OEM] = BASE, [OEM2] = BASE, ["via.effect.script.EPVDataElement"] = "via.effect.ProviderData",
        ["app.AppShell"] = "ace.ShellBase"}
    local function td(name)
        return {get_full_name = function() return name end, get_parent_type = function() return parent_types[name] and td(parent_types[name]) end,
            get_method = function(_, signature)
                if options.missing == signature then return nil end
                local method = {name = name, signature = signature,
                    get_function = function()
                        if options.missing_address == signature then return nil end
                        local key = name .. "|" .. signature
                        return options.native_aliases and options.native_aliases[key] or key
                    end,
                    is_static = function()
                        if options.invalid_static == signature then return nil end
                        return options.static_engine == true and name == MANAGER and signature == CREATE
                    end,
                    get_return_type = function()
                        if signature == CREATE then return td("via.GameObject[]") end
                        if signature:match("^play") or signature:match("^requestEffectInternal") or
                            signature:match("^callShellExEffect%(") or signature == SHELL_HIT then
                            return td("via.effect.script.CreatedEffectContainer")
                        end
                        return td("System.Void")
                    end}
                if options.missing_static == signature then method.is_static = nil end
                return method
            end}
    end
    function c:object(name, fields, methods)
        self.nextAddress = self.nextAddress + 1
        local object = {address = self.nextAddress, fields = fields or {}, methods = methods or {}, typeName = name, refs = 0}
        function object:get_address() return self.address end
        function object:get_type_definition() return td(self.typeName) end
        function object:get_field(key) return self.fields[key] end
        function object:add_ref() self.refs = self.refs + 1; return self end
        function object:call(method, ...)
            check(not method:match("^set_") and not method:match("^play") and method ~= "destroy", "capture attempted a mutation: " .. method)
            c.calls[#c.calls + 1] = method
            local value = self.methods[method]
            if type(value) == "function" then return value(...) end
            return value
        end
        return object
    end
    function c:list(values)
        return self:object("List", {}, {get_Count = function() return #values end,
            get_Item = function(index) return values[index + 1] end})
    end
    function c:generic_array(values, declared_length, fail_read)
        local array = self:object("System.Array")
        function array:call(method, index)
            if method == "get_Length" then return declared_length or #values end
            if method == "GetValue(System.Int32)" then
                eq(math.type(index), "integer")
                if fail_read then error("simulated typed read failure") end
                return values[index + 1]
            end
            error("generic array does not expose " .. method)
        end
        return array
    end
    function c:holder(path) return self:object("via.effect.EffectResourceHolder", {}, {get_ResourcePath = path}) end
    function c:player(path)
        local holder = self:holder(path)
        return self:object("via.effect.EffectPlayer", {}, {get_ResourcePath = path, get_Resource = holder})
    end
    function c:provider(paths)
        local holders = {}; for _, path in ipairs(paths) do holders[#holders + 1] = self:holder(path) end
        return self:object("via.effect.script.EPVDataElement", {}, {getResourcesCount = #holders,
            ["getResources(System.UInt64)"] = function(index) eq(math.type(index), "integer"); return holders[index + 1] end}), holders
    end
    function c:container(players, owner)
        local container = self:object("via.effect.script.CreatedEffectContainer", {AllPlayers = self:list(players), Creator = owner or self.go, SerialNo = self.nextAddress})
        return container
    end
    function c:shell(shooter)
        local go = self:object("via.GameObject")
        local shell = self:object("app.AppShell", {}, {get_ShellOwner = shooter, get_GameObject = go})
        go.methods["getComponent(System.Type)"] = function(name) if name == "ace.ShellBase" then return shell end end
        return self:object(SHELL, {["<Shell>k__BackingField"] = shell}), shell, go
    end
    function c:node(id)
        return self:object("via.motion.MotionNodeCtrl", {}, {get_MotionID = id, get_MotionBankID = 7, get_MotionBankType = 1,
            get_MotionName = "motion_" .. id, get_MotionFrame = 12.5, get_MotionPrevFrame = 11.5, get_Weight = 0.5,
            get_MotionResourceDataLongId = "18446744073709551610"})
    end
    function c:motion(go, layers)
        return self:object("via.motion.Motion", {}, {get_GameObject = go, getLayerCount = #layers,
            ["getLayer(System.UInt32)"] = function(index)
                return self:object("via.motion.TreeLayer", {}, {getMotionNodeCount = #layers[index + 1],
                    get_MotionBankID = function() return layers[index + 1][1].methods.get_MotionBankID end,
                    get_MotionID = function() return layers[index + 1][1].methods.get_MotionID end,
                    get_Frame = function() return layers[index + 1][1].methods.get_MotionFrame end,
                    ["getMotionNode(System.UInt32)"] = function(i) return layers[index + 1][i + 1] end})
            end})
    end
    c.go = c:object("via.GameObject")
    c.weaponGo = c:object("via.GameObject")
    c.weapon = c:object("app.Weapon", {}, {get_GameObject = c.weaponGo})
    c.character = c:object("app.HunterCharacter", {}, {get_GameObject = c.go, get_Weapon = c.weapon, get_WeaponType = 2, get_WeaponID = 123})
    c.go.methods["getComponent(System.Type)"] = function(name) if name == "app.HunterCharacter" then return c.character end end
    c.master = c:object("Master", {}, {get_Character = c.character})
    c.playerManager = c:object("app.PlayerManager", {}, {getMasterPlayer = c.master})
    c.node1 = c:node(30); c.node2 = c:node(31)
    c.motion1 = c:motion(c.go, {{c.node1}, {c.node2}})
    if options.track_motion then c.character.methods.get_MotionComponent = c.motion1 end
    c.oem = c:object(OEM, {Motion = c.motion1}); c.oem2 = c:object(OEM2, {Motion = c.motion1})
    c.manager = c:object(MANAGER)
    c.effectId = c:object("via.effect.script.EffectID", {}, {get_containerID = 42, get_elementID = 9, get_dataContainerIndex = 2})
    env.sdk = {
        find_type_definition = function(name)
            if name == "via.SceneManager" then c.metrics.sceneTypeLookups = c.metrics.sceneTypeLookups + 1 end
            return td(name)
        end, typeof = function(name) return name end,
        get_managed_singleton = function() return options.no_actor and nil or c.playerManager end,
        get_native_singleton = function() return {} end,
        call_native_func = function(_, _, name)
            eq(name, "get_CurrentScene")
            c.metrics.sceneReads = c.metrics.sceneReads + 1
            return {get_address = function() return c.scene end}
        end,
        to_managed_object = function(value) return type(value) == "table" and value or nil end,
        to_int64 = function(value) return value end,
        is_managed_object = function(value) return not value.invalid end,
        hook = function(method, pre, post, ignore_jump)
            eq(ignore_jump, true, "native hooks must not follow ABI-changing jump wrappers")
            c.installs = c.installs + 1
            c.hooks[method.name .. "|" .. method.signature] = {pre = pre, post = post}
        end,
        PreHookResult = {CALL_ORIGINAL = 0},
    }
    env.thread = {get_id = function() c.metrics.threadIds = c.metrics.threadIds + 1; return c.tid end,
        get_hook_storage = function() local stack = c.stores[c.tid]; return stack[#stack] end}
    if options.no_thread then env.thread = false end
    env.re = {on_frame = function(fn) c.callbacks[#c.callbacks + 1] = fn end,
        on_script_reset = function(fn) c.resets[#c.resets + 1] = fn end}
    c.env = env
    c.module = assert(loadfile(capture_source, "t", env))()
    function c:start() local ok, err = self.module.start(); check(ok, err); return self end
    function c:begin(typ, sig, args, tid)
        self.tid = tid or 1
        self.stores[self.tid] = self.stores[self.tid] or {}
        local storage = {}; table.insert(self.stores[self.tid], storage)
        local hook = assert(self.hooks[typ .. "|" .. sig], "missing mock hook")
        eq(hook.pre(args), 0, "original call must proceed")
        return {hook = hook, tid = self.tid, storage = storage}
    end
    function c:finish(token, result)
        self.tid = token.tid
        eq(self.stores[self.tid][#self.stores[self.tid]], token.storage, "hook storage stack")
        eq(token.hook.post(result), result, "return value preserved")
        table.remove(self.stores[self.tid])
    end
    function c:invoke(typ, sig, args, result, tid)
        local token = self:begin(typ, sig, args, tid); self:finish(token, result)
    end
    function c:update(count) for _ = 1, count or 1 do for _, fn in ipairs(self.callbacks) do fn() end end end
    function c:engine(paths, players, owner, tid)
        local provider, holders = self:provider(paths)
        local request = self:object("via.effect.script.EffectManager.RequestInfo", {Data = provider, FollowTarget = self.weaponGo})
        local objects = {}
        for _, player in ipairs(players or {}) do objects[#objects + 1] = self:object("via.GameObject", {}, {["getComponent(System.Type)"] = function() return player end}) end
        local args = options.static_engine and {[2] = owner or self.go, [3] = request} or
            {[2] = self.manager, [3] = owner or self.go, [4] = request}
        self:invoke(MANAGER, CREATE, args, self:list(objects), tid)
        return provider, holders
    end
    return c
end

if arg[2] == "export-environment" then return environment end

test("load is inert and repeated start installs each hook once", function()
    local c = environment(); eq(c.installs, 0)
    eq(assert(loadfile(capture_source, "t", c.env))(), c.module)
    c:start(); eq(c.installs, 22); c.module.stop(); c:start(); eq(c.installs, 22)
end)
test("direct local motion request links every provider resource and actual player", function()
    local c = environment():start(); local path = "Art/VFX/slash.efx"
    local token = c:begin(OEM, MOTION, {[2] = c.oem, [3] = c.node1, [4] = 0})
    local player = c:player(path); local provider, holders = c:engine({path, "Art/VFX/spark.efx"}, {player})
    c:finish(token)
    local snapshot = c.module.snapshot(); eq(#snapshot.events, 1); eq(#snapshot.resources, 2)
    local ev = snapshot.events[1]; eq(ev.localConfirmed, true); eq(ev.motion.motionId, 30); eq(ev.motion.layer, 0)
    eq(ev.motion.resourceLongId, "18446744073709551610"); eq(ev.motionAssociation, "direct_motion_request")
    eq(#ev.requested, 2); eq(#ev.created, 1); eq(ev.created[1].path, path)
    local binding = c.module.list_bindings(path)[1]
    eq(binding.provider, provider); eq(binding.index, 0); eq(binding.holder, holders[1]); eq(binding.players[1], player)
end)
test("OEM2 callback captures all real container players", function()
    local c = environment():start()
    local token = c:begin(OEM2, MOTION, {[2] = c.oem2, [3] = c.node2, [4] = 1})
    local container = c:container({c:player("a.efx"), c:player("b.efx")})
    c:invoke(BASE, EVENT, {[2] = c.oem2, [3] = c.effectId, [4] = container})
    c:finish(token)
    local ev = c.module.snapshot().events[1]
    eq(ev.motion.motionId, 31); eq(ev.motion.layer, 1); eq(ev.containerId, 42); eq(ev.elementId, 9)
    eq(#ev.created, 2); eq(ev.localConfirmed, true)
end)
test("asynchronously populated container retains its original motion", function()
    local c = environment():start(); local players = {}; local container = c:container(players)
    local token = c:begin(OEM, MOTION, {[2] = c.oem, [3] = c.node1})
    c:invoke(OEM, REGISTER, {[2] = c.oem, [3] = container, [4] = 0, [5] = 42, [6] = 9})
    c:finish(token); c.node1.methods.get_MotionID = 99
    players[1] = c:player("late.efx"); c.module.stop(); c:update()
    local ev = c.module.snapshot().events[1]
    eq(ev.motion.motionId, 30); eq(ev.motion.frame, 12.5); eq(ev.created[1].path, "late.efx")
end)
test("another thread cannot inherit a nearby local motion request", function()
    local c = environment():start(); local token = c:begin(OEM, MOTION, {[2] = c.oem, [3] = c.node1}, 1)
    c:engine({"other.efx"}, {}, c.go, 2); c:finish(token)
    local ev = c.module.snapshot().events[1]
    eq(ev.localConfirmed, true); eq(ev.motion, nil); eq(ev.motionAssociation, "unknown"); eq(ev.threadId, "2")
end)
test("confirmed remote hunter effects are excluded", function()
    local c = environment():start(); local remote_go = c:object("via.GameObject")
    local remote = c:object("app.HunterCharacter")
    remote_go.methods["getComponent(System.Type)"] = remote
    local motion = c:motion(remote_go, {{c.node1}}); local oem = c:object(OEM, {Motion = motion})
    local token = c:begin(OEM, MOTION, {[2] = oem, [3] = c.node1})
    c:engine({"remote.efx"}, {}, remote_go); c:finish(token)
    local snapshot = c.module.snapshot(); eq(#snapshot.events, 0); eq(snapshot.counters.remoteIgnored, 1)
end)
test("different unknown owner creates a causality barrier", function()
    local c = environment():start(); local token = c:begin(OEM, MOTION, {[2] = c.oem, [3] = c.node1})
    local unknown_go = c:object("via.GameObject")
    local controller = c:object("app.cEffectController", {_Owner = unknown_go})
    local sig = "playEffectCore(app.EffectID_Hit.ID, via.GameObject, app.cEffectOverwriteParams, via.GameObject)"
    local inner = c:begin("app.cEffectController", sig, {[2] = controller, [3] = 5})
    c:engine({"environment.efx"}, {}, c.go); c:finish(inner); c:finish(token)
    local ev = c.module.snapshot().events[1]
    eq(ev.localConfirmed, false); eq(ev.motion, nil); eq(ev.ownership, "unknown")
end)
test("local hit initiator is retained when the effect follows another hunter", function()
    local c = environment():start(); local target = c:object("via.GameObject")
    target.methods["getComponent(System.Type)"] = c:object("app.HunterCharacter")
    local controller = c:object("app.cEffectController", {_Owner = c.go})
    local sig = "playEffectCore(app.EffectID_Hit.ID, via.GameObject, app.cEffectOverwriteParams, via.GameObject)"
    local token = c:begin("app.cEffectController", sig, {[2] = controller, [3] = 5})
    c:engine({"hit.efx"}, {}, target); c:finish(token)
    local ev = c.module.snapshot().events[2]
    eq(ev.localConfirmed, true); eq(ev.ownerIdentity, tostring(c.go.address)); eq(ev.creatorIdentity, tostring(target.address))
end)
test("remote controller cannot become local merely by hitting the local hunter", function()
    local c = environment():start(); local remote_go = c:object("via.GameObject")
    remote_go.methods["getComponent(System.Type)"] = c:object("app.HunterCharacter")
    local controller = c:object("app.cEffectController", {_Owner = remote_go})
    local sig = "playEffectCore(app.EffectID_Hit.ID, via.GameObject, app.cEffectOverwriteParams, via.GameObject)"
    local token = c:begin("app.cEffectController", sig, {[2] = controller, [3] = 5})
    c:engine({"remote.efx"}, {}, c.go); c:finish(token)
    eq(#c.module.snapshot().events, 0)
end)
test("container observations merge by actual identity and serial number", function()
    local c = environment():start(); local container = c:container({c:player("shared.efx")})
    c:invoke(OEM, REGISTER, {[2] = c.oem, [3] = container, [4] = 0, [5] = 42, [6] = 9})
    local token = c:begin(OEM, MOTION, {[2] = c.oem, [3] = c.node1})
    c:invoke(BASE, EVENT, {[2] = c.oem, [3] = c.effectId, [4] = container}); c:finish(token)
    local s = c.module.snapshot(); eq(#s.events, 1); eq(s.events[1].observationCount, 2)
    eq(s.events[1].motion.motionId, 30); eq(s.resources[1].count, 1); eq(s.counters.mergedContainerObservations, 1)
    eq(s.resources[1].motion.motionId, 30)
end)
test("player retention is bounded even when one provider is used repeatedly", function()
    local c = environment():start(); c.module.limits.players = 2
    c:engine({"shared.efx"}, {c:player("shared.efx"), c:player("shared.efx"), c:player("shared.efx")})
    eq(#c.module.list_bindings("shared.efx")[1].players, 2)
    check(c.module.snapshot().counters.playerOverflow > 0)
end)
test("clearing an in-flight request cannot reintroduce stale observations", function()
    local c = environment():start(); local controller = c:object("app.cEffectController", {_Owner = c.go})
    local sig = "playEffectCore(app.EffectID_Hit.ID, via.GameObject, app.cEffectOverwriteParams, via.GameObject)"
    local token = c:begin("app.cEffectController", sig, {[2] = controller, [3] = 1})
    c.module.clear(); c:finish(token, c:container({c:player("stale.efx")}))
    eq(#c.module.snapshot().resources, 0); eq(c.module.snapshot().pendingSources, 0)
end)
test("same motion id on another layer is resolved by node identity", function()
    local c = environment():start(); c.node2.methods.get_MotionID = 30
    local token = c:begin(OEM, MOTION, {[2] = c.oem, [3] = c.node2})
    c:engine({"layer.efx"}); c:finish(token)
    eq(c.module.snapshot().events[1].motion.layer, 1)
end)
test("state and hit calls keep separate domains without invented motion", function()
    local c = environment():start(); local controller = c:object("app.cEffectController", {_Owner = c.go})
    for _, domain in ipairs({"Hit", "Damage"}) do
        local sig = "playEffectCore(app.EffectID_" .. domain .. ".ID, via.GameObject, app.cEffectOverwriteParams, via.GameObject)"
        c:invoke("app.cEffectController", sig, {[2] = controller, [3] = 50}, c:container({c:player(domain .. ".efx")}))
    end
    local events = c.module.snapshot().events
    eq(events[1].effectDomain, "Hit"); eq(events[2].effectDomain, "Damage"); eq(events[1].effectId, 50)
    eq(events[1].motion, nil); eq(events[2].localConfirmed, true)
end)
test("aliases preserve actual requested and created paths", function()
    local c = environment():start(); check(c.module.set_alias("Art/New/Rev_2.efx", "Art/Original.efx"))
    c:engine({"Art/New/Rev_2.efx"}, {c:player("Art/New/Rev_2.efx")})
    local snapshot = c.module.snapshot()
    eq(snapshot.resources[1].canonicalPath, "art/original.efx")
    eq(snapshot.events[1].created[1].path, "Art/New/Rev_2.efx")
    eq(#c.module.list_bindings(" @/ART/ORIGINAL.EFX.5571972 "), 1)
    eq(c.module.set_alias("texture.tex", "a.efx"), false)
end)
test("all complete resource paths are kept and unrelated suffixes ignored", function()
    local c = environment():start(); c:engine({"natives\\STM\\Art\\ONE.efx.5571972", "Art/two.efx", "Art/texture.tex"})
    local snapshot = c.module.snapshot(); eq(#snapshot.resources, 2)
    eq(snapshot.resources[1].path, "natives\\STM\\Art\\ONE.efx.5571972")
    eq(snapshot.resources[1].canonicalPath, "art/one.efx")
end)
test("snapshots contain only JSON-safe values and are independent copies", function()
    local c = environment():start(); c:engine({"a.efx"}, {c:player("a.efx")})
    local function jsonsafe(value)
        local kind = type(value)
        check(kind == "table" or kind == "string" or kind == "number" or kind == "boolean" or kind == "nil", "non-JSON value")
        if kind == "table" then
            check(value.get_address == nil, "managed object escaped snapshot")
            for _, item in pairs(value) do jsonsafe(item) end
        end
    end
    local snapshot = c.module.snapshot(); jsonsafe(snapshot)
    snapshot.events[1].requested[1].path = "changed"; eq(c.module.snapshot().events[1].requested[1].path, "a.efx")
end)
test("stop prevents new observations while leaving hooks installed", function()
    local c = environment():start(); c.module.stop(); c:engine({"a.efx"}); eq(#c.module.snapshot().events, 0); eq(c.installs, 22)
end)
test("collection and retention limits report incomplete coverage", function()
    local c = environment():start(); c.module.limits.collection = 1; c.module.limits.events = 1; c.module.limits.resources = 1
    c:engine({"a.efx", "b.efx"}); c:engine({"c.efx"})
    local s = c.module.snapshot(); eq(#s.events, 1); eq(#s.resources, 1)
    check(s.counters.collectionTruncated > 0); check(s.counters.evictedEvents > 0); check(s.counters.resourceOverflow > 0)
end)
test("scene change drops stale resource bindings", function()
    local c = environment():start(); c:update(); c:engine({"a.efx"}); eq(#c.module.list_bindings("a.efx"), 1)
    c.scene = 20; c:update(); eq(#c.module.list_bindings("a.efx"), 0); eq(c.module.snapshot().sceneEpoch, 2)
end)
test("missing hook is explicit and partial coverage remains usable", function()
    local c = environment({missing = MOTION}):start(); local s = c.module.snapshot()
    eq(s.installedHooks, 20); eq(s.requestedHooks, 22); eq(#s.errors, 2)
end)
test("missing thread support refuses capture without a global fallback", function()
    local c = environment({no_thread = true}); local ok, err = c.module.start()
    eq(ok, false); check(err:find("thread", 1, true)); eq(c.installs, 0)
end)
test("clear removes aggregate history and all binding references", function()
    local c = environment():start(); c:engine({"a.efx"}); c.module.clear()
    eq(#c.module.snapshot().events, 0); eq(#c.module.snapshot().resources, 0); eq(#c.module.list_bindings("a.efx"), 0)
end)
test("reset makes installed callbacks inert", function()
    local c = environment():start(); c:engine({"a.efx"})
    for _, reset in ipairs(c.resets) do reset() end
    c:engine({"b.efx"}); eq(#c.module.snapshot().events, 1); eq(c.module.start(), false)
end)

local function hook_snapshot(snapshot, type_name, signature)
    for _, hook in ipairs(snapshot.hooks) do
        if hook.type == type_name and hook.signature == signature then return hook end
    end
    error("missing hook diagnostics")
end
local LOOP = "playEffectLoopCore(System.UInt32, app.EffectID_Common.ID, System.UInt64, via.GameObject, app.cEffectOverwriteParams, via.GameObject)"
local HIT = "playEffectCore(app.EffectID_Hit.ID, via.GameObject, app.cEffectOverwriteParams, via.GameObject)"

test("static creation uses metadata layout and preserves motion and real paths", function()
    local c = environment({static_engine = true}):start()
    local token = c:begin(OEM, MOTION, {[2] = c.oem, [3] = c.node2})
    local player = c:player("new.efx")
    local provider = c:engine({"original.efx"}, {player}); c:finish(token)
    local s = c.module.snapshot(); eq(s.version, "1.4.2"); eq(#s.events, 1); eq(#s.resources, 2)
    local ev = s.events[1]
    eq(ev.localConfirmed, true); eq(ev.motion.motionId, 31); eq(ev.requested[1].path, "original.efx")
    eq(ev.created[1].path, "new.efx"); eq(ev.creatorIdentity, tostring(c.go.address))
    eq(c.module.list_bindings("original.efx")[1].provider, provider)
    local hook = hook_snapshot(s, MANAGER, CREATE)
    eq(hook.isStatic, true); eq(hook.returnType, "via.GameObject[]"); eq(hook.firstArgumentIndex, 2)
    eq(hook.preCalls, 1); eq(hook.postCalls, 1); eq(hook.recordedCalls, 1); eq(hook.ownerMismatch, 0)
    eq(s.counters.ownerMismatch, 0); eq(hook.installed, true); eq(hook.errors, 0)
end)

test("static request Owner is read independently of its creator", function()
    local c = environment({static_engine = true}):start()
    local creator = c:object("via.GameObject")
    local provider = c:provider({"owned.efx"})
    local request = c:object("via.effect.script.EffectManager.RequestInfo", {Data = provider, Owner = c.go})
    c:invoke(MANAGER, CREATE, {[2] = creator, [3] = request}, c:list({}))
    local ev = c.module.snapshot().events[1]
    eq(ev.localConfirmed, true); eq(ev.ownerEvidence, "request_owner")
    eq(ev.ownerIdentity, tostring(c.go.address)); eq(ev.creatorIdentity, tostring(creator.address))
    eq(ev.requestOwnerIdentity, tostring(c.go.address)); eq(ev.requested[1].path, "owned.efx")
end)

test("static hit creation retains its actual initiator without remote promotion", function()
    local c = environment({static_engine = true}):start()
    local remote_go = c:object("via.GameObject")
    remote_go.methods["getComponent(System.Type)"] = c:object("app.HunterCharacter")
    local local_controller = c:object("app.cEffectController", {_Owner = c.go})
    local token = c:begin("app.cEffectController", HIT, {[2] = local_controller, [3] = 5})
    c:engine({"local_hit.efx"}, {}, remote_go); c:finish(token)
    local ev = c.module.snapshot().events[2]
    eq(ev.localConfirmed, true); eq(ev.ownerIdentity, tostring(c.go.address)); eq(ev.creatorIdentity, tostring(remote_go.address))
    local remote_controller = c:object("app.cEffectController", {_Owner = remote_go})
    token = c:begin("app.cEffectController", HIT, {[2] = remote_controller, [3] = 5})
    c:engine({"remote_hit.efx"}, {}, c.go); c:finish(token)
    local s = c.module.snapshot(); eq(#s.events, 2); eq(#s.resources, 1); eq(s.counters.remoteIgnored, 2)
end)

test("instance layout remains checked and diagnostic counters identify wrong receiver", function()
    local c = environment():start()
    c:invoke(MANAGER, CREATE, {[2] = c.go}, c:list({}))
    local s = c.module.snapshot(); local hook = hook_snapshot(s, MANAGER, CREATE)
    eq(hook.isStatic, false); eq(hook.firstArgumentIndex, 3)
    eq(hook.preCalls, 1); eq(hook.postCalls, 1); eq(hook.ownerMismatch, 1); eq(s.counters.ownerMismatch, 1)
    eq(#s.events, 0); eq(#s.resources, 0)
end)

test("one thousand empty unknown loops cannot evict a local action", function()
    local c = environment():start(); c.module.limits.events = 2
    c:engine({"local.efx"})
    local unknown = c:object("via.GameObject")
    local controller = c:object("app.cEffectController", {_Owner = unknown})
    for _ = 1, 1000 do c:invoke("app.cEffectController", LOOP, {[2] = controller, [3] = 0, [4] = 500}) end
    local s = c.module.snapshot(); eq(#s.events, 1); eq(s.events[1].requested[1].path, "local.efx")
    eq(s.counters.captured, 1); eq(s.counters.emptyUnknownCalls, 1000); eq(s.counters.evictedEvents, 0)
    local hook = hook_snapshot(s, "app.cEffectController", LOOP)
    eq(hook.preCalls, 1000); eq(hook.postCalls, 1000); eq(hook.recordedCalls, 1000); eq(hook.emptyUnknownCalls, 1000)
    c.module.stop(); c:invoke("app.cEffectController", LOOP, {[2] = controller, [3] = 0, [4] = 500})
    hook = hook_snapshot(c.module.snapshot(), "app.cEffectController", LOOP)
    eq(hook.preCalls, 1001); eq(hook.postCalls, 1001); eq(hook.recordedCalls, 1000); eq(hook.emptyUnknownCalls, 1000)
end)

test("real unknown resource output is published while empty creation stays deferred", function()
    local c = environment({static_engine = true}):start(); local unknown = c:object("via.GameObject")
    c:engine({}, {}, unknown); eq(#c.module.snapshot().events, 0)
    c:engine({"environment.efx"}, {c:player("environment.efx")}, unknown)
    local s = c.module.snapshot(); eq(#s.events, 1); eq(#s.resources, 1)
    eq(s.events[1].localConfirmed, false); eq(s.events[1].ownership, "unknown")
    eq(s.events[1].requested[1].path, "environment.efx"); eq(#s.events[1].created, 1)
    eq(s.counters.emptyUnknownCalls, 1); eq(s.counters.captured, 1)
end)

test("unknown real containers publish once and still receive delayed players", function()
    local c = environment():start(); local unknown = c:object("via.GameObject")
    local controller = c:object("app.cEffectController", {_Owner = unknown})
    local players = {}; local container = c:container(players, unknown)
    for _ = 1, 2 do c:invoke("app.cEffectController", LOOP, {[2] = controller, [3] = 0, [4] = 5}, container) end
    local s = c.module.snapshot(); eq(#s.events, 1); eq(s.counters.captured, 1); eq(s.events[1].observationCount, 2)
    eq(s.counters.mergedContainerObservations, 1); eq(s.counters.emptyUnknownCalls, 0)
    eq(s.events[1].pending, true); eq(s.events[1].localConfirmed, false)
    players[1] = c:player("later.efx"); c:update()
    s = c.module.snapshot(); eq(s.events[1].created[1].path, "later.efx"); eq(s.resources[1].localConfirmed, false)
end)

test("event ring evicts unknown observations before local history", function()
    local c = environment():start(); c.module.limits.events = 2
    local unknown = c:object("via.GameObject")
    c:engine({"local_first.efx"}); c:engine({"unknown.efx"}, {}, unknown); c:engine({"local_second.efx"})
    local s = c.module.snapshot(); eq(#s.events, 2); eq(s.counters.evictedEvents, 1)
    eq(s.events[1].requested[1].path, "local_first.efx"); eq(s.events[2].requested[1].path, "local_second.efx")
end)

test("full local history rejects unknown events but keeps their real resource evidence", function()
    local c = environment():start(); c.module.limits.events = 1
    local unknown = c:object("via.GameObject")
    c:engine({"local.efx"}); c:engine({"unknown.efx"}, {c:player("unknown.efx")}, unknown)
    local s = c.module.snapshot(); eq(#s.events, 1); eq(s.events[1].requested[1].path, "local.efx")
    eq(s.counters.evictedEvents, 0); eq(s.counters.suppressedUnknownEvents, 1); eq(#s.resources, 2)
    eq(s.resources[2].localConfirmed, false); eq(s.resources[2].requestedCount, 1); eq(s.resources[2].createdCount, 1)
    eq(#c.module.list_bindings("unknown.efx"), 1)
end)

test("missing or invalid static metadata never guesses an ABI layout", function()
    for _, option in ipairs({"missing_static", "invalid_static"}) do
        local c = environment({[option] = CREATE}):start(); local s = c.module.snapshot()
        eq(s.installedHooks, 21); eq(#s.errors, 1)
        local hook = hook_snapshot(s, MANAGER, CREATE)
        eq(hook.installed, false); eq(hook.errors, 1); check(type(hook.lastError) == "string")
        eq(c.hooks[MANAGER .. "|" .. CREATE], nil)
    end
end)

test("an existing old singleton is not silently replaced or double hooked", function()
    local c = environment():start(); local old = {version = "1.0.0"}
    c.env.MotionVfxActionCapture = old
    eq(assert(loadfile("MotionVfxLive/fx_action_capture.lua", "t", c.env))(), old)
    eq(c.installs, 22); eq(old.version, "1.0.0")
end)

local function static_return(c, result)
    local provider = c:provider({"requested.efx"})
    local request = c:object("via.effect.script.EffectManager.RequestInfo", {Data = provider})
    c:invoke(MANAGER, CREATE, {[2] = c.go, [3] = request}, result)
end
local function player_object(c, path)
    local player = c:player(path)
    return c:object("via.GameObject", {}, {["getComponent(System.Type)"] = function() return player end})
end

test("static creation reads actual players from a generic managed System.Array", function()
    local c = environment({static_engine = true}):start()
    static_return(c, c:generic_array({player_object(c, "created_a.efx"), player_object(c, "created_b.efx")}))
    local s = c.module.snapshot(); eq(#s.events, 1); eq(#s.resources, 3)
    eq(s.events[1].requested[1].path, "requested.efx"); eq(#s.events[1].created, 2)
    eq(s.events[1].created[1].path, "created_a.efx"); eq(s.events[1].created[2].path, "created_b.efx")
    eq(s.counters.collectionReadErrors, 0); eq(s.counters.errors, 0)
end)

test("generic array null entries and empty arrays are valid reads", function()
    local c = environment({static_engine = true}):start()
    static_return(c, c:generic_array({player_object(c, "created.efx")}, 2))
    static_return(c, c:generic_array({}))
    local s = c.module.snapshot(); eq(#s.events[1].created, 1); eq(#s.events[2].created, 0)
    eq(s.counters.collectionReadErrors, 0); eq(s.counters.errors, 0)
end)

test("generic array failures and truncation have distinct precise counters", function()
    local c = environment({static_engine = true}):start(); c.module.limits.collection = 1
    static_return(c, c:generic_array({player_object(c, "a.efx"), player_object(c, "b.efx")}))
    static_return(c, c:generic_array({player_object(c, "unreadable.efx")}, 1, true))
    static_return(c, c:generic_array({}, -1))
    local s = c.module.snapshot(); eq(#s.events[1].created, 1); eq(#s.events[2].created, 0)
    eq(s.counters.collectionTruncated, 1); eq(s.counters.collectionReadErrors, 2)
    eq(s.counters.errors, 0)
end)

test("dense zero and one based Lua arrays capture real players", function()
    local c = environment({static_engine = true}):start()
    static_return(c, {[0] = player_object(c, "zero_a.efx"), [1] = player_object(c, "zero_b.efx")})
    static_return(c, {player_object(c, "one_a.efx"), player_object(c, "one_b.efx")})
    static_return(c, {})
    local s = c.module.snapshot()
    eq(s.events[1].created[1].path, "zero_a.efx"); eq(s.events[1].created[2].path, "zero_b.efx")
    eq(s.events[2].created[1].path, "one_a.efx"); eq(s.events[2].created[2].path, "one_b.efx")
    eq(#s.events[3].created, 0); eq(s.counters.collectionReadErrors, 0); eq(s.counters.errors, 0)
end)

test("Lua array validation rejects holes and mixed keys without partial observations", function()
    local c = environment({static_engine = true}):start()
    static_return(c, {[1] = player_object(c, "hole_a.efx"), [3] = player_object(c, "hole_b.efx")})
    static_return(c, {[1] = player_object(c, "mixed.efx"), extra = "not an array"})
    local s = c.module.snapshot(); eq(#s.events[1].created, 0); eq(#s.events[2].created, 0)
    eq(s.counters.collectionReadErrors, 2); eq(s.counters.errors, 0)
end)

test("Lua player arrays in containers use the same verified sequence reader", function()
    local c = environment():start(); c.module.limits.collection = 1
    local container = c:container({})
    container.fields.AllPlayers = {[0] = c:player("first.efx"), [1] = c:player("truncated.efx")}
    c:invoke(OEM, REGISTER, {[2] = c.oem, [3] = container, [4] = 0, [5] = 42, [6] = 9})
    local s = c.module.snapshot(); eq(s.events[1].created[1].path, "first.efx"); eq(#s.events[1].created, 1)
    eq(s.counters.collectionTruncated, 1); eq(s.counters.collectionReadErrors, 0)
end)
test("projectile source attributes shared bowgun controllers through typed ShellOwner", function()
    local c = environment():start(); local source, shell = c:shell(c.go)
    local shared = c:object("via.GameObject")
    local controller = c:object("app.cEffectController", {_Owner = shared})
    local token = c:begin(SHELL, SHELL_COMMON, {[2] = source})
    c:invoke("app.cEffectController", COMMON, {[2] = controller, [3] = 0, [4] = 5}, c:container({c:player("it12_010.efx")}, shared))
    c:finish(token)
    local s = c.module.snapshot(); eq(#s.events, 1); eq(#s.resources, 1)
    local ev = s.events[1]
    eq(ev.localConfirmed, true); eq(ev.ownerEvidence, "shell_owner"); eq(ev.ownerIdentity, tostring(c.go.address))
    eq(ev.sourceShellIdentity, tostring(shell.address)); eq(ev.sourceOwnerIdentity, tostring(c.go.address))
    eq(ev.creatorIdentity, tostring(shared.address)); eq(ev.shellOwnerDepth, 1)
    eq(ev.motionAssociation, "unknown"); eq(ev.motion, nil); eq(s.resources[1].localConfirmed, true)
end)

test("shell hit uses the shooter even when the shared controller targets another hunter", function()
    local c = environment():start(); local source = c:shell(c.go)
    local shared = c:object("via.GameObject")
    local target = c:object("via.GameObject")
    target.methods["getComponent(System.Type)"] = c:object("app.HunterCharacter")
    local controller = c:object("app.cEffectController", {_Owner = shared})
    local container = c:container({c:player("hit_gun.efx")}, target)
    local token = c:begin(SHELL, SHELL_HIT, {[2] = source})
    c:invoke("app.cEffectController", HIT, {[2] = controller, [3] = 5}, container)
    c:finish(token, container)
    local s = c.module.snapshot(); eq(#s.events, 1); eq(s.events[1].localConfirmed, true)
    eq(s.events[1].sourceOwnerIdentity, tostring(c.go.address)); eq(s.events[1].creatorIdentity, tostring(target.address))
    eq(s.resources[1].count, 1); eq(s.resources[1].localConfirmed, true)
end)

test("remote projectile hitting the local hunter cannot become local", function()
    local c = environment({static_engine = true}):start()
    local remote = c:object("via.GameObject")
    remote.methods["getComponent(System.Type)"] = c:object("app.HunterCharacter")
    local source = c:shell(remote)
    local shared = c:object("via.GameObject")
    local controller = c:object("app.cEffectController", {_Owner = shared})
    local token = c:begin(SHELL, SHELL_HIT, {[2] = source})
    local inner = c:begin("app.cEffectController", HIT, {[2] = controller, [3] = 5})
    c:engine({"remote_hit.efx"}, {c:player("remote_hit.efx")}, c.go)
    local container = c:container({c:player("remote_hit.efx")}, c.go)
    c:finish(inner, container); c:finish(token, container)
    local s = c.module.snapshot(); eq(#s.events, 0); eq(#s.resources, 0); eq(s.counters.remoteIgnored, 3)
end)

test("unknown shell source stays unknown when its container creator is the local hunter", function()
    local c = environment():start(); local unknown = c:object("via.GameObject"); local source = c:shell(unknown)
    local controller = c:object("app.cEffectController", {_Owner = c.go})
    local container = c:container({c:player("unknown_hit.efx")}, c.go)
    local token = c:begin(SHELL, SHELL_HIT, {[2] = source})
    c:invoke("app.cEffectController", HIT, {[2] = controller, [3] = 5}, container)
    c:finish(token, container)
    -- A subsequent observation outside the initiating scope must not promote it.
    c:invoke("app.cEffectController", HIT, {[2] = controller, [3] = 5}, container)
    local s = c.module.snapshot(); eq(#s.events, 1); eq(s.events[1].ownership, "unknown")
    eq(s.events[1].localConfirmed, false); eq(s.resources[1].localConfirmed, false)
    eq(s.events[1].ownerEvidence, "shell_owner_unresolved"); eq(s.counters.shellOwnerUnresolved, 1)
end)

test("shared controllers without a live source scope remain unknown", function()
    local c = environment():start(); local source = c:shell(c.go)
    local shared = c:object("via.GameObject")
    local controller = c:object("app.cEffectController", {_Owner = shared})
    c:invoke(SHELL, SHELL_COMMON, {[2] = source})
    c:invoke("app.cEffectController", COMMON, {[2] = controller, [3] = 0, [4] = 5}, c:container({c:player("it12_010.efx")}, shared))
    local s = c.module.snapshot(); eq(#s.events, 1); eq(s.events[1].localConfirmed, false)
    eq(s.events[1].sourceKind, nil); eq(s.resources[1].localConfirmed, false)
end)

test("a source scope cannot leak across threads", function()
    local c = environment():start(); local source = c:shell(c.go)
    local shared = c:object("via.GameObject")
    local controller = c:object("app.cEffectController", {_Owner = shared})
    local token = c:begin(SHELL, SHELL_COMMON, {[2] = source}, 1)
    c:invoke("app.cEffectController", COMMON, {[2] = controller, [3] = 0, [4] = 5}, c:container({c:player("cross_thread.efx")}, shared), 2)
    c:finish(token)
    local ev = c.module.snapshot().events[1]; eq(ev.localConfirmed, false); eq(ev.sourceKind, nil)
end)

test("typed child shell ownership follows only its ShellOwner chain", function()
    local c = environment():start(); local _, _, parent_go = c:shell(c.weaponGo)
    local source, shell = c:shell(parent_go)
    local token = c:begin(SHELL, SHELL_HIT, {[2] = source})
    c:finish(token, c:container({c:player("child.efx")}, parent_go))
    local ev = c.module.snapshot().events[1]
    eq(ev.localConfirmed, true); eq(ev.shellOwnerDepth, 2); eq(ev.ownerIdentity, tostring(c.weaponGo.address))
    eq(ev.sourceShellIdentity, tostring(shell.address))
end)

test("shell cycles and excessive depth stay bounded and unresolved", function()
    local c = environment():start(); local source, shell, go = c:shell(nil)
    shell.methods.get_ShellOwner = go
    c:invoke(SHELL, SHELL_HIT, {[2] = source}, c:container({c:player("cycle.efx")}, c.go))
    local child_owner = c.go
    for _ = 1, 9 do source, shell, child_owner = c:shell(child_owner) end
    c:invoke(SHELL, SHELL_HIT, {[2] = source}, c:container({c:player("depth.efx")}, c.go))
    local s = c.module.snapshot(); eq(#s.events, 2)
    eq(s.events[1].localConfirmed, false); eq(s.events[2].localConfirmed, false)
    eq(s.counters.shellOwnerCycles, 1); eq(s.counters.shellOwnerDepthExceeded, 1)
    eq(s.counters.shellOwnerUnresolved, 2)
end)

test("a missing or wrong typed Shell field never inherits an outer local motion", function()
    local c = environment():start()
    for _, source in ipairs({c:object(SHELL), c:object(SHELL, {["<Shell>k__BackingField"] = c:object("unrelated", {}, {get_ShellOwner = c.go})})}) do
        local outer = c:begin(OEM, MOTION, {[2] = c.oem, [3] = c.node1})
        c:invoke(SHELL, SHELL_HIT, {[2] = source}, c:container({c:player("missing.efx")}, c.go))
        c:finish(outer)
    end
    local s = c.module.snapshot(); eq(#s.events, 2)
    for _, ev in ipairs(s.events) do eq(ev.localConfirmed, false); eq(ev.motion, nil); eq(ev.sourceKind, "shell") end
    eq(s.counters.shellOwnerUnresolved, 2)
end)

test("nested remote shell scope is a barrier to an outer local projectile", function()
    local c = environment():start(); local local_source = c:shell(c.go)
    local remote = c:object("via.GameObject")
    remote.methods["getComponent(System.Type)"] = c:object("app.HunterCharacter")
    local remote_source = c:shell(remote)
    local outer = c:begin(SHELL, SHELL_COMMON, {[2] = local_source})
    c:invoke(SHELL, SHELL_HIT, {[2] = remote_source}, c:container({c:player("remote.efx")}, c.go))
    c:finish(outer); eq(#c.module.snapshot().events, 0)
end)

test("projectile ownership snapshots survive deferred player population without reattribution", function()
    local c = environment():start(); local source, shell = c:shell(c.go)
    local shared = c:object("via.GameObject")
    local players = {}; local container = c:container(players, shared)
    c:invoke(SHELL, SHELL_HIT, {[2] = source}, container)
    shell.methods.get_ShellOwner = shared
    players[1] = c:player("late_shell.efx"); c.module.stop(); c:update()
    local s = c.module.snapshot(); eq(s.events[1].localConfirmed, true)
    eq(s.events[1].created[1].path, "late_shell.efx"); eq(s.events[1].sourceOwnerIdentity, tostring(c.go.address))
end)

test("missing source hook is reported while existing capture remains available", function()
    local c = environment({missing = SHELL_HIT}):start()
    local s = c.module.snapshot(); eq(s.installedHooks, 21); eq(s.requestedHooks, 22); eq(#s.errors, 1)
    c:engine({"existing.efx"}); eq(c.module.snapshot().resources[1].localConfirmed, true)
end)

test("later unknown shell evidence revokes an earlier local container resource", function()
    local c = environment():start(); local controller = c:object("app.cEffectController", {_Owner = c.go})
    local container = c:container({c:player("revoked.efx")}, c.go)
    c:invoke("app.cEffectController", HIT, {[2] = controller, [3] = 5}, container)
    eq(c.module.snapshot().resources[1].localConfirmed, true)
    local source = c:shell(c:object("via.GameObject"))
    c:invoke(SHELL, SHELL_HIT, {[2] = source}, container)
    local s = c.module.snapshot(); eq(#s.events, 1); eq(s.events[1].localConfirmed, false)
    eq(s.resources[1].localConfirmed, false); eq(s.resources[1].count, 1)
    c:update(); eq(c.module.snapshot().resources[1].localConfirmed, false)
end)

test("revocation preserves independent confirmed evidence for the same resource", function()
    for _, kind in ipairs({"unknown", "remote"}) do
        local c = environment():start(); local controller = c:object("app.cEffectController", {_Owner = c.go})
        local container = c:container({c:player("shared.efx")}, c.go)
        c:invoke("app.cEffectController", HIT, {[2] = controller, [3] = 5}, container)
        c:invoke("app.cEffectController", HIT, {[2] = controller, [3] = 5}, c:container({c:player("shared.efx")}, c.go))
        local shooter = c:object("via.GameObject")
        if kind == "remote" then shooter.methods["getComponent(System.Type)"] = c:object("app.HunterCharacter") end
        local source = c:shell(shooter)
        c:invoke(SHELL, SHELL_HIT, {[2] = source}, container)
        local s = c.module.snapshot(); eq(s.resources[1].localConfirmed, true)
        eq(#s.events, kind == "remote" and 1 or 2)
    end
end)

test("remote source revokes old local evidence and blocks later target-only promotion", function()
    local c = environment():start(); local controller = c:object("app.cEffectController", {_Owner = c.go})
    local container = c:container({c:player("remote.efx")}, c.go)
    c:invoke("app.cEffectController", HIT, {[2] = controller, [3] = 5}, container)
    eq(c.module.snapshot().resources[1].localConfirmed, true)
    local remote = c:object("via.GameObject")
    remote.methods["getComponent(System.Type)"] = c:object("app.HunterCharacter")
    local source = c:shell(remote)
    c:invoke(SHELL, SHELL_HIT, {[2] = source}, container)
    local s = c.module.snapshot(); eq(#s.events, 0); eq(s.resources[1].localConfirmed, false); eq(s.pendingSources, 0)
    c:invoke("app.cEffectController", HIT, {[2] = controller, [3] = 5}, container)
    s = c.module.snapshot(); eq(#s.events, 0)
    eq(s.resources[1].localConfirmed, false); eq(s.pendingSources, 0)
end)

test("container source evidence is bounded and reset clears old serial identities", function()
    local c = environment():start(); c.module.limits.events = 2
    local source = c:shell(c:object("via.GameObject"))
    for _ = 1, 3 do c:invoke(SHELL, SHELL_HIT, {[2] = source}, c:container({}, c.go)) end
    eq(c.module.snapshot().counters.sourceOwnershipEvictions, 1)
    c.module.clear(); c:invoke(SHELL, SHELL_HIT, {[2] = source}, c:container({}, c.go))
    eq(c.module.snapshot().counters.sourceOwnershipEvictions, 1)
end)

test("native aliases install once and dispatch OEM receivers without duplicate capture", function()
    local aliases = {[OEM .. "|" .. REGISTER] = "same-entry", [OEM2 .. "|" .. REGISTER] = "same-entry"}
    local c = environment({native_aliases = aliases}):start()
    eq(c.installs, 21); local s = c.module.snapshot(); eq(s.installedHooks, 22); eq(s.nativeHooks, 21)
    c:invoke(OEM, REGISTER, {[2] = c.oem2, [3] = c:container({c:player("alias.efx")}), [5] = 3, [6] = 4})
    s = c.module.snapshot(); eq(#s.events, 1); eq(s.events[1].localConfirmed, true)
    eq(s.events[1].label, OEM2 .. "." .. REGISTER); eq(s.counters.ownerMismatch, 0)
    eq(hook_snapshot(s, OEM2, REGISTER).preCalls, 1); eq(hook_snapshot(s, OEM, REGISTER).preCalls, 0)
    check(hook_snapshot(s, OEM2, REGISTER).sharedNativeHook ~= nil)
end)

test("incompatible native ABI aliases are all skipped with explicit coverage errors", function()
    local aliases = {[MANAGER .. "|" .. CREATE] = "conflict", [SHELL .. "|" .. SHELL_COMMON] = "conflict"}
    local c = environment({native_aliases = aliases, static_engine = true}):start()
    local s = c.module.snapshot(); eq(c.installs, 20); eq(s.installedHooks, 20); eq(s.requestedHooks, 22)
    eq(hook_snapshot(s, MANAGER, CREATE).installed, false); eq(hook_snapshot(s, SHELL, SHELL_COMMON).installed, false)
    eq(hook_snapshot(s, MANAGER, CREATE).aliasConflict, true); eq(hook_snapshot(s, SHELL, SHELL_COMMON).aliasConflict, true)
    eq(#s.errors, 1)
end)

test("missing native address fails closed instead of guessing an entry", function()
    local c = environment({missing_address = SHELL_HIT}):start(); local s = c.module.snapshot()
    eq(s.installedHooks, 21); eq(s.nativeHooks, 21); eq(hook_snapshot(s, SHELL, SHELL_HIT).installed, false)
    eq(#s.errors, 1)
end)

test("actual source comes from the created player holder, never the provider slot or a path load", function()
    local c = environment():start(); local path = "Art/VFX/source.efx"
    local player = c:player(path); local actual = player.methods.get_Resource
    local _, requested = c:engine({path}, {player})
    local descriptor, holder = c.module.get_resource_source(path)
    eq(holder, actual); check(holder ~= requested[1]); check(actual.refs > 0)
    eq(descriptor.actualPath, path); eq(descriptor.actualHolderIdentity, tostring(actual.address))
    eq(descriptor.playerIdentity, tostring(player.address)); eq(descriptor.localConfirmed, true)
    eq(descriptor.currentPlayerVerified, false); eq(descriptor.cacheBytesAvailable, false)
    eq(descriptor.sourceKind, "actual_effect_player_holder"); eq(descriptor.availability, "retained_observation")
    local snapshot = c.module.snapshot(); eq(snapshot.retainedResourceSources, 1)
    eq(descriptor.eventId, snapshot.events[1].id)
    eq(snapshot.events[1].actualSources[1].sourceId, descriptor.sourceId)
    eq(snapshot.resources[1].latestSource.sourceId, descriptor.sourceId)
    descriptor.actualPath = "changed.efx"
    eq(c.module.get_resource_source(path).actualPath, path, "source consumers receive an independent descriptor")
end)

test("latest actual alias is separate from the original shared target and old selections expire", function()
    local c = environment():start(); local original, generated = "Art/VFX/original.efx", "Art/VFX/MotionVfxLive/rev_2.efx"
    c:engine({original}, {c:player(original)})
    local old = c.module.get_resource_source(original)
    check(c.module.set_alias(generated, original))
    local newer = c:player(generated); c:engine({generated}, {newer})
    local descriptor, holder = c.module.get_resource_source(original)
    eq(descriptor.actualPath, generated); eq(holder, newer.methods.get_Resource)
    eq(descriptor.sharedTargetPath, original:lower())
    eq(c.module.get_resource_source(generated).sourceId, descriptor.sourceId)
    eq(c.module.get_resource_source(original, old.sourceId), nil, "old retained history is not the latest selected source")
    local snapshot = c.module.snapshot(); eq(#snapshot.resources, 1)
    eq(snapshot.resources[1].path, original, "historical path stays historical")
    eq(snapshot.resources[1].latestSource.actualPath, generated)
end)

test("selection and deferred scans never reread an already observed pooled player", function()
    local c = environment():start(); local player = c:player("captured.efx")
    local container = c:container({player})
    c:invoke(OEM, REGISTER, {[2] = c.oem, [3] = container, [5] = 3, [6] = 4})
    c:update()
    local descriptor, holder = c.module.get_resource_source("captured.efx")
    check(descriptor ~= nil)
    local reads = 0
    player.methods.get_Resource = function() reads = reads + 1; error("pooled player was reused") end
    player.methods.get_ResourcePath = function() reads = reads + 1; error("pooled player was reused") end
    c:update(3)
    local next_descriptor, next_holder = c.module.get_resource_source("captured.efx", descriptor.sourceId)
    eq(reads, 0); eq(next_holder, holder); eq(next_descriptor.sourceId, descriptor.sourceId)
    eq(#c.module.snapshot().events[1].actualSources, 1)
end)

test("event merges migrate captured holders without refreshing their player", function()
    local c = environment():start(); local player = c:player("merged_source.efx")
    local container = c:container({player})
    c:invoke(OEM, REGISTER, {[2] = c.oem, [3] = container, [5] = 3, [6] = 4})
    local descriptor, holder = c.module.get_resource_source("merged_source.efx")
    local reads = 0
    player.methods.get_Resource = function() reads = reads + 1; return c:holder("wrong_reused.efx") end
    player.methods.get_ResourcePath = function() reads = reads + 1; return "wrong_reused.efx" end
    local outer = c:begin(OEM, MOTION, {[2] = c.oem, [3] = c.node1})
    c:invoke(BASE, EVENT, {[2] = c.oem, [3] = c.effectId, [4] = container})
    c:finish(outer)
    local actual, actual_holder = c.module.get_resource_source("merged_source.efx")
    check(actual ~= nil); eq(actual_holder, holder); eq(actual.sourceId, descriptor.sourceId)
    eq(reads, 0)
end)

test("a path alone or a mismatched holder cannot supply cache provenance", function()
    local c = environment():start()
    local missing = c:player("missing_holder.efx"); missing.methods.get_Resource = nil
    c:engine({"missing_holder.efx"}, {missing})
    eq(c.module.get_resource_source("missing_holder.efx"), nil)
    local mismatch = c:player("player_path.efx"); mismatch.methods.get_Resource = c:holder("different_holder.efx")
    c:engine({"player_path.efx"}, {mismatch})
    eq(c.module.get_resource_source("different_holder.efx"), nil)
    eq(c.module.get_resource_source("player_path.efx"), nil)
    eq(c.module.snapshot().retainedResourceSources, 0)
    eq(c.module.snapshot().counters.resourceSourceReadErrors, 2)
end)

test("holder path still supplies actual provenance if the player's path getter is absent", function()
    local c = environment():start(); local player = c:player("holder_only.efx")
    player.methods.get_ResourcePath = nil
    c:engine({}, {player})
    local descriptor, holder = c.module.get_resource_source("holder_only.efx")
    eq(holder, player.methods.get_Resource); eq(descriptor.actualPath, "holder_only.efx")
end)

test("revoked local ownership discards its holders and exposes no latest local source", function()
    local c = environment():start(); local controller = c:object("app.cEffectController", {_Owner = c.go})
    local container = c:container({c:player("revoked_source.efx")}, c.go)
    c:invoke("app.cEffectController", HIT, {[2] = controller, [3] = 5}, container); c:update()
    local descriptor = c.module.get_resource_source("revoked_source.efx"); check(descriptor ~= nil)
    local unknown_source = c:shell(c:object("via.GameObject"))
    c:invoke(SHELL, SHELL_HIT, {[2] = unknown_source}, container)
    eq(c.module.get_resource_source("revoked_source.efx", descriptor.sourceId), nil)
    local snapshot = c.module.snapshot(); eq(snapshot.resources[1].latestSource, nil)
    eq(snapshot.retainedResourceSources, 0); eq(snapshot.counters.resourceSourceRevocations, 1)
    eq(snapshot.events[1].actualSources[1].availability, "unavailable")
    eq(snapshot.events[1].actualSources[1].invalidReason, "ownership_revoked")
end)

test("unknown holder observations cannot be consumed as confirmed local sources", function()
    local c = environment():start(); local unknown = c:object("via.GameObject")
    c:engine({}, {c:player("unknown_source.efx")}, unknown)
    eq(c.module.get_resource_source("unknown_source.efx"), nil)
    eq(c.module.snapshot().resources[1].latestSource, nil)
end)

test("resource source capacity evicts holder references and invalidates old source tokens", function()
    local c = environment():start(); c.module.limits.resourceSources = 2
    c:engine({}, {c:player("first.efx")}); local old = c.module.get_resource_source("first.efx")
    c:engine({}, {c:player("second.efx")}); c:engine({}, {c:player("third.efx")})
    eq(c.module.get_resource_source("first.efx", old.sourceId), nil)
    local snapshot = c.module.snapshot(); eq(snapshot.retainedResourceSources, 2)
    eq(snapshot.counters.resourceSourceEvictions, 1)
    eq(snapshot.events[1].actualSources[1].invalidReason, "source_limit")
end)

test("history eviction, scene change, clear and reset invalidate captured source evidence", function()
    local c = environment():start(); c.module.limits.events = 1
    c:engine({}, {c:player("old_event.efx")}); local old = c.module.get_resource_source("old_event.efx")
    c:engine({}, {c:player("new_event.efx")})
    eq(c.module.get_resource_source("old_event.efx", old.sourceId), nil)
    eq(c.module.snapshot().resources[1].latestSource, nil)
    c:update(); c.scene = 999; c:update()
    eq(c.module.get_resource_source("new_event.efx"), nil); eq(c.module.snapshot().retainedResourceSources, 0)
    c:engine({}, {c:player("clear_source.efx")}); check(c.module.get_resource_source("clear_source.efx") ~= nil)
    c.module.clear(); eq(c.module.get_resource_source("clear_source.efx"), nil)
    c:engine({}, {c:player("reset_source.efx")}); check(c.module.get_resource_source("reset_source.efx") ~= nil)
    for _, callback in ipairs(c.resets) do callback() end
    eq(c.module.get_resource_source("reset_source.efx"), nil); eq(c.module.snapshot().retainedResourceSources, 0)
end)

test("animation records exist without an effect and preserve bank/layer identity", function()
    local c = environment({track_motion = true}):start()
    local s = c.module.snapshot()
    eq(#s.actions, 2); eq(#s.resources, 0); eq(#s.events, 0)
    eq(s.currentActions[1].motion.motionId, 30); eq(s.actions[2].motion.layer, 1)
    local first = s.actions[1].id
    c.node1.methods.get_MotionFrame = 20; c:update()
    s = c.module.snapshot(); eq(#s.actions, 2); eq(s.actions[1].lastMotionFrame, 20)
    c.node1.methods.get_MotionBankID = 8; c:update()
    s = c.module.snapshot(); eq(#s.actions, 3); eq(s.actions[3].motion.motionId, 30)
    check(s.actions[3].key ~= s.actions[1].key); check(s.actions[1].closed)
    check(s.actions[3].id ~= first)
end)
test("paused frames do not invent repeated actions; rewinds split observations", function()
    local c = environment({track_motion = true}):start()
    c:update(10); eq(#c.module.snapshot().actions, 2)
    c.node1.methods.get_MotionFrame = 2; c:update()
    local s = c.module.snapshot(); eq(#s.actions, 3); eq(s.actions[3].reason, "frame_rewound")
    c.module.stop(); c.node1.methods.get_MotionID = 50; c:update(12)
    s = c.module.snapshot(); eq(#s.actions, 3); check(s.actions[3].closed)
    eq(s.currentActions[1].motion.motionId, 50); eq(s.currentActions[1].recordId, nil)
    local live = c.module.current_snapshot()
    eq(live.recording, false); eq(live.currentActions[1].motion.motionId, 50)
    eq(live.actions, nil); eq(live.events, nil)
    live.currentActions[1].motion.motionId = -1
    eq(c.module.current_snapshot().currentActions[1].motion.motionId, 50)
    c.module.start(); eq(#c.module.snapshot().actions, 5)
end)
test("direct motion evidence and temporal context remain distinct", function()
    local c = environment({track_motion = true}):start()
    local token = c:begin(OEM, MOTION, {[2] = c.oem, [3] = c.node1})
    c:engine({"direct.efx"}, {c:player("direct.efx")}); c:finish(token)
    c:engine({"context.efx"}, {c:player("context.efx")})
    local s = c.module.snapshot()
    eq(s.events[1].actionLinks[1].association, "direct_motion_request")
    eq(s.events[1].actionLinks[1].motionFrame, 12.5)
    eq(s.events[2].actionLinks[1].association, "contemporaneous_only")
    eq(s.events[2].motionAssociation, "unknown")
    eq(s.events[1].actionLinks[1].actionId, s.actions[1].id)
    local unknown = c:object("via.GameObject")
    c:engine({}, {c:player("unconfirmed.efx")}, unknown)
    eq(#c.module.snapshot().events[3].actionLinks, 0)
end)
test("scene and equipment transitions close clips; history is bounded and clearable", function()
    local c = environment({track_motion = true}):start()
    c.module.limits.actions = 4
    c:update(); c.scene = 900; c:update()
    local s = c.module.snapshot()
    eq(s.actions[1].endReason, "scene_changed")
    check(s.currentActions[1].recordId ~= s.actions[1].id)
    c.character.methods.get_WeaponType = 13; c:update()
    s = c.module.snapshot(); eq(#s.actions, 4); check(s.counters.actionEvictions > 0)
    eq(s.actions[#s.actions].weaponType, 13)
    c.module.clear(); eq(#c.module.snapshot().actions, 0)
    c:update(); eq(#c.module.snapshot().actions, 2)
    for _, reset in ipairs(c.resets) do reset() end
    local count = #c.module.snapshot().actions; c:update(12); eq(#c.module.snapshot().actions, count)
end)

test("short named weapon loops coalesce without losing the hunter action", function()
    local c = environment({track_motion = true})
    local weapon_node = c:node(1); weapon_node.methods.get_MotionName = "wpg02left_01_001_Loop"
    weapon_node.methods.get_MotionFrame = 0
    c.weapon.methods.get_MotionComponent = c:motion(c.weaponGo, {{weapon_node}})
    c:start(); c.module.limits.actions = 4
    local initial = c.module.snapshot(); local first = initial.actions[1].id
    for i = 1, 300 do
        weapon_node.methods.get_MotionFrame = 5.9; c:update()
        weapon_node.methods.get_MotionFrame = 0.2; c:update()
    end
    local s = c.module.snapshot(); eq(#s.actions, 3); eq(s.actions[1].id, first)
    eq(s.actions[3].loopWraps, 300); eq(s.counters.actionEvictions, 0)
    eq(s.counters.actionLoopWraps, 300); eq(s.counters.errors, 0)
    weapon_node.methods.get_MotionID = 2; weapon_node.methods.get_MotionName = "non_loop_attack"
    c:update(); weapon_node.methods.get_MotionFrame = 20; c:update()
    weapon_node.methods.get_MotionFrame = 0; c:update()
    s = c.module.snapshot(); eq(#s.actions, 4); eq(s.actions[1].id, first)
    eq(s.actions[4].reason, "frame_rewound")
end)
test("repeated container observation summarizes frames without exhausting action links", function()
    local c = environment({track_motion = true}):start()
    local container = c:container({c:player("loop_observation.efx")})
    for i = 1, 40 do
        c.node1.methods.get_MotionFrame = i; c:update()
        local token = c:begin(OEM, MOTION, {[2] = c.oem, [3] = c.node1})
        c:invoke(BASE, EVENT, {[2] = c.oem, [3] = c.effectId, [4] = container})
        c:finish(token)
    end
    local s = c.module.snapshot(); eq(#s.events, 1); eq(#s.events[1].actionLinks, 1)
    local link = s.events[1].actionLinks[1]
    eq(link.motionFrame, 1); eq(link.lastMotionFrame, 40); eq(link.observations, 40)
    eq(link.association, "direct_motion_request"); eq(s.counters.actionLinkOverflow, 0)
end)
test("inactive animation layers are not reported as read errors", function()
    local c = environment({track_motion = true})
    c.node2.methods.get_MotionID = 4294967295; c:start(); c:update(10)
    local s = c.module.snapshot(); eq(#s.actions, 1); eq(s.counters.actionReadErrors, 0)
    c.node2.methods.get_MotionID = nil; c:update()
    eq(c.module.snapshot().counters.actionReadErrors, 1)
end)

test("stopped hooks preserve accounting and never create captured events", function()
    local c = environment():start(); c.module.stop()
    for _ = 1, 100 do c:invoke(OEM, MOTION, {[2] = c.oem, [3] = c.node1}) end
    local s = c.module.snapshot()
    eq(#s.events, 0); eq(s.counters.calls, 0); eq(s.counters.contextMismatch, 0)
    local info
    for _, item in ipairs(s.hooks) do if item.type == OEM and item.signature == MOTION then info = item end end
    eq(info.preCalls, 100); eq(info.postCalls, 100); eq(info.recordedCalls, 0)
end)
test("recording toggles inside an active hook keep the stopped scope as a causality barrier", function()
    local c = environment():start()
    local outer = c:begin(OEM, MOTION, {[2] = c.oem, [3] = c.node1})
    c.module.stop()
    local idle = c:begin(OEM, MOTION, {[2] = c.oem, [3] = c.node2})
    c.module.start(); c:engine({"boundary.efx"}, {c:player("boundary.efx")})
    c:finish(idle); c:finish(outer)
    local s = c.module.snapshot()
    eq(#s.events, 1); eq(s.events[1].motion, nil); eq(s.counters.contextMismatch, 0)
end)
test("an idle outer hook can contain a newly started recording without stealing its stack", function()
    local c = environment():start(); c.module.stop()
    local idle = c:begin(OEM, MOTION, {[2] = c.oem, [3] = c.node1})
    c.module.start(); c:engine({"start_boundary.efx"}, {c:player("start_boundary.efx")})
    c:finish(idle)
    local s = c.module.snapshot()
    eq(#s.events, 1); eq(s.events[1].motion, nil); eq(s.counters.contextMismatch, 0)
end)
test("stopped native aliases keep receiver-specific pre and post accounting", function()
    local aliases = {[OEM .. "|" .. REGISTER] = "same-entry", [OEM2 .. "|" .. REGISTER] = "same-entry"}
    local c = environment({native_aliases = aliases}):start(); c.module.stop()
    c:invoke(OEM, REGISTER, {[2] = c.oem2})
    local s = c.module.snapshot()
    eq(hook_snapshot(s, OEM2, REGISTER).preCalls, 1); eq(hook_snapshot(s, OEM2, REGISTER).postCalls, 1)
    eq(hook_snapshot(s, OEM, REGISTER).preCalls, 0); eq(hook_snapshot(s, OEM, REGISTER).postCalls, 0)
    eq(s.counters.contextMismatch, 0); eq(#s.events, 0)
end)
test("unavailable scene metadata is retried and live scene changes still invalidate immediately", function()
    local c = environment():start(); local find = c.env.sdk.find_type_definition
    c.env.sdk.find_type_definition = function(name)
        if name == "via.SceneManager" then return nil end
        return find(name)
    end
    c:update(); eq(c.metrics.sceneReads, 0)
    c.env.sdk.find_type_definition = find
    c:update(); eq(c.metrics.sceneReads, 1)
    local epoch = c.module.snapshot().sceneEpoch
    c.scene = c.scene + 1; c:update()
    eq(c.metrics.sceneReads, 2); eq(c.module.snapshot().sceneEpoch, epoch + 1)
end)

-- Keep the same workload available against arg[1], an archived baseline module.
-- Allocation is measured with GC paused; these are Lua mock costs, not game FPS.
local function measure(name, c, count, run)
    collectgarbage("collect"); collectgarbage("stop")
    local memory, started = collectgarbage("count"), os.clock()
    local before_thread, before_scene, before_reads = c.metrics.threadIds, c.metrics.sceneTypeLookups, c.metrics.sceneReads
    local ok, err = pcall(run)
    local seconds, allocated = os.clock() - started, collectgarbage("count") - memory
    collectgarbage("restart")
    check(ok, err)
    print(("PERF %s iterations=%d lua_seconds=%.6f allocated_kib=%.3f thread_ids=%d scene_type_lookups=%d scene_reads=%d")
        :format(name, count, seconds, allocated, c.metrics.threadIds-before_thread,
            c.metrics.sceneTypeLookups-before_scene, c.metrics.sceneReads-before_reads))
end
test("runtime performance workload retains all frame observations", function()
    local c = environment({track_motion = true}):start()
    local frames = 600
    measure("recording_frames", c, frames, function()
        for i = 1, frames do c.node1.methods.get_MotionFrame = i; c:update() end
    end)
    local s = c.module.snapshot()
    eq(c.metrics.sceneReads, frames)
    -- The first update rewinds the fixture's initial frame, closing its first
    -- history item. Check the live layer rather than that closed first item.
    local latest
    for _, action in ipairs(s.actions) do if action.motion.motionId == 30 then latest = action end end
    check(latest ~= nil); eq(latest.lastMotionFrame, frames)
    eq(s.currentActions[1].motion.frame, frames); eq(s.counters.errors, 0)
end)
test("runtime performance workload retains stopped-hook pairing", function()
    local c = environment():start(); c.module.stop()
    local count, args = 10000, {[2] = c.oem, [3] = c.node1}
    measure("stopped_hooks", c, count, function()
        for _ = 1, count do c:invoke(OEM, MOTION, args) end
    end)
    local s = c.module.snapshot()
    eq(#s.events, 0); eq(s.counters.contextMismatch, 0); eq(s.counters.calls, 0)
end)

print(("RESULT action capture %d passed, %d failed, %d checks"):format(passed, failed, checks))
if failed > 0 then error("action capture regressions failed") end
