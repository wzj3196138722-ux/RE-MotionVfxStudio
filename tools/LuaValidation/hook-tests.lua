-- Offline regression only: no REFramework/game calls are made.
-- Working directory: the Lua materials directory containing fx_hook.lua.
-- argv[1]: exact contents of fx_hook - copy.lua (pass the Unicode-named copy
-- with Get-Content -Raw because stock Lua file I/O does not use Windows UTF-16).
local checks = 0
local function check(condition, message)
    assert(condition, message)
    checks = checks + 1
end
local hooks, frames, resets, messages = {}, {}, {}, {}
local installs, current_thread, storage_stacks = 0, 1, {}
log = { info = function(msg) messages[#messages + 1] = msg end,
    warn = function(msg) messages[#messages + 1] = msg end }
local function make_type(name, parent)
    return { get_full_name = function() return name end,
        get_parent_type = function() return parent end,
        get_fields = function() return {} end,
        get_methods = function() return {} end }
end
local function method(signature, result)
    return { signature = signature,
        get_name = function() return signature:match("^([^%(]+)") end,
        get_num_params = function() return signature == "set_Resource" and 1 or 0 end,
        get_return_type = function() return make_type(result or "via.effect.script.CreatedEffectContainer") end }
end
local setter = method("set_Resource", "System.Void")
sdk = {
    find_type_definition = function(name)
        local td = make_type(name)
        td.get_method = function(_, signature)
            local result = signature:find("registerCreated", 1, true) and "System.Void" or nil
            return method(signature, result)
        end
        if name == "via.effect.EffectPlayer" then td.get_methods = function() return { setter } end end
        return td
    end,
    to_int64 = function(value) return value end,
    to_managed_object = function(value) return type(value) == "table" and value or nil end,
    PreHookResult = { CALL_ORIGINAL = 0 },
    hook = function(m, pre, post)
        installs = installs + 1
        hooks[m.signature] = { pre = pre, post = post }
    end,
}
thread = { get_id = function() return current_thread end,
    get_hook_storage = function()
        local stack = storage_stacks[current_thread]
        assert(stack and #stack > 0, "hook storage requested outside a hook")
        return stack[#stack]
    end }
re = { on_frame = function(fn) frames[#frames + 1] = fn end,
    on_draw_ui = function(_) end,
    on_script_reset = function(fn) resets[#resets + 1] = fn end }
local function begin(signature, args, tid)
    current_thread = tid or 1
    storage_stacks[current_thread] = storage_stacks[current_thread] or {}
    local stack = storage_stacks[current_thread]
    local token = { signature = signature, tid = current_thread, storage = {} }
    stack[#stack + 1] = token.storage
    check(hooks[signature].pre(args or {}) == 0, "collector must call original")
    return token
end
local function finish(token, retval)
    current_thread = token.tid
    local stack = storage_stacks[current_thread]
    assert(stack[#stack] == token.storage, "test simulation requires stack-correct post")
    check(hooks[token.signature].post(retval) == retval, "collector must preserve return value")
    table.remove(stack)
end
local function invoke(signature, args, retval, tid)
    local token = begin(signature, args, tid)
    finish(token, retval)
end
local function run_frames(count)
    for _ = 1, count do for _, fn in ipairs(frames) do fn() end end
end
local function object(name, fields, calls, td)
    local obj = { refs = 0, adds = 0, releases = 0 }
    local typ = td or make_type(name)
    function obj:get_type_definition() return typ end
    function obj:get_field(key) return (fields or {})[key] end
    function obj:call(key, ...)
        local value = (calls or {})[key]
        if type(value) == "function" then return value(...) end
        return value
    end
    function obj:add_ref() self.refs = self.refs + 1; self.adds = self.adds + 1; return self end
    function obj:release()
        self.refs = self.refs - 1; self.releases = self.releases + 1
        assert(self.refs >= 0, "unbalanced release")
    end
    return obj
end
local function collection(items)
    return object("List", nil, { get_Count = #items, get_Item = function(index) return items[index + 1] end })
end
local function resource(path)
    return object("via.effect.EffectResourceHolder", nil, { get_ResourcePath = path })
end
local function container(fields)
    return object("via.effect.script.CreatedEffectContainer", fields)
end
local PLAY = "playEffect(app.EffectID_Common.ID, via.GameObject, app.cEffectOverwriteParams)"
local CORE = "playEffectCore(System.UInt32, app.EffectID_Common.ID, via.GameObject, app.cEffectOverwriteParams, via.GameObject)"
local REGISTER = "registerCreatedEffectDataList(via.effect.script.CreatedEffectContainer, System.Int32, System.UInt32, System.UInt32)"
local DAMAGE = "playDamageEffect(app.EffectID_Damage.ID, via.vec3)"

local runtime = dofile("fx_hook.lua")
check(installs == 11, "all configured hooks register in mock")
check(dofile("fx_hook.lua") == runtime and installs == 11, "repeat load must not duplicate hooks")
assert(arg[1], "pass the exact Unicode copy file contents as argv[1]")
assert(load(arg[1], "fx_hook duplicate compatibility entry"))()
check(installs == 11 and #frames == 1, "copy must not install callbacks/hooks")

-- Interleaved threads plus recursively nested same function must pair exact frames.
local a = begin(PLAY, { [3] = 11 }, 1)
local b = begin(PLAY, { [3] = 22 }, 2)
local inner = begin(PLAY, { [3] = 33 }, 1)
local c33 = container({ AllPlayers = collection({ resource("effects/33.efx") }) })
invoke(REGISTER, { [3] = c33, [5] = 133, [6] = 3 }, nil, 1)
finish(inner, nil)
local c11 = container({ AllPlayers = collection({ resource("effects/11.efx") }) })
invoke(REGISTER, { [3] = c11, [5] = 111, [6] = 1 }, nil, 1)
local c22 = container({ AllPlayers = collection({ resource("effects/22.efx") }) })
invoke(REGISTER, { [3] = c22, [5] = 122, [6] = 2 }, nil, 2)
finish(b, nil); finish(a, nil)
run_frames(5)
local regs = {}
for _, ev in ipairs(runtime.snapshot().events) do
    if ev.label == "OEM.register" then regs[ev.container_id] = ev end
end
check(regs[133].effect_id == 33, "nested recursive call must own its register")
check(regs[111].effect_id == 11, "outer frame restored after recursive post")
check(regs[122].effect_id == 22, "other thread retains its own context")
check(regs[133].association == "synchronous_context", "sync context must not claim exact resource ownership")

-- An asynchronous register after all plays have ended must stay unknown.
local unknown = container({ AllPlayers = collection({ resource("effects/async.efx") }) })
invoke(REGISTER, { [3] = unknown, [5] = 999, [6] = 9 })
run_frames(5)
local snapshot = runtime.snapshot()
local async = snapshot.events[#snapshot.events]
check(async.effect_id == nil and async.id_domain == "unknown" and async.association == "unknown", "async register must not use last-play fallback")
check(#async.paths == 1 and async.paths[1].path == "effects/async.efx", "unknown association still retains observed path")
check(snapshot.id_observations["Common:22"][1].path ~= "effects/async.efx", "unknown path must not contaminate previous ID cache")
runtime.clear()
check(c33.refs == 0 and c11.refs == 0 and c22.refs == 0 and unknown.refs == 0, "clear must release every retained container")

local return1 = container({ AllPlayers = collection({ resource("effects/return81.efx") }) })
local return2 = container({ AllPlayers = collection({ resource("effects/return82.efx") }) })
local return3 = container({ AllPlayers = collection({ resource("effects/return83.efx") }) })
local ret_outer = begin(PLAY, { [3] = 81 }, 1)
local ret_other_thread = begin(PLAY, { [3] = 82 }, 2)
local ret_inner = begin(PLAY, { [3] = 83 }, 1)
finish(ret_inner, return3); finish(ret_other_thread, return2); finish(ret_outer, return1)
run_frames(3)
for _, ev in ipairs(runtime.snapshot().events) do
    check(ev.paths[1].path == "effects/return" .. ev.effect_id .. ".efx", "post return must use exact pre frame across recursion/threads")
end
runtime.clear()
check(return1.refs == 0 and return2.refs == 0 and return3.refs == 0, "recursive post sources release independently")

-- Later collection entries and multiple players/requests must all be inspected.
local multi = container({ AllPlayers = collection({ resource(nil), resource("effects/player2.efx"),
    object("via.effect.EffectPlayer", nil, { get_Resource = resource("effects/player3.efx") }) }),
    Request = collection({ object("Request", { ParamString = "effects/request1.efx" }),
        object("Request", { ParamString = "effects/request2.efx", TargetData = resource("effects/target2.efx") }) }) })
invoke(CORE, { [4] = 44 }, multi)
run_frames(2)
local found = {}
for _, item in ipairs(runtime.snapshot().events[1].paths) do found[item.path] = true end
for _, path in ipairs({ "player2", "player3", "request1", "request2", "target2" }) do
    check(found["effects/" .. path .. ".efx"], "missing later path: " .. path)
end
check(multi.refs == 1, "returned container held exactly once")

-- Same ID can observe multiple resources; no latest-path overwrite or guess.
local second = container({ AllPlayers = collection({ resource("effects/other44.efx") }) })
invoke(CORE, { [4] = 44 }, second)
run_frames(2)
local observed = {}
for _, item in ipairs(runtime.snapshot().id_observations["Common:44"]) do observed[item.path] = true end
check(observed["effects/player2.efx"] and observed["effects/other44.efx"], "cache must preserve multiple paths for one ID")
invoke(DAMAGE, { [3] = 44 }, nil)
check(runtime.snapshot().events[3].id_domain == "Damage", "Damage.ID must remain distinct from Common.ID")
runtime.clear()
check(multi.refs == 0 and second.refs == 0, "return-value holds release on clear")

-- Forward declarations + balanced visited set allow fields after getter traversal.
local reflected_type = make_type("via.effect.EffectResourceHolder")
reflected_type.get_fields = function() return {
    { is_static = function() return false end, get_data = function() return "effects/field_only.efx" end }
} end
local reflected = object("via.effect.EffectResourceHolder", nil, nil, reflected_type)
local owner = object("via.effect.EffectPlayer")
invoke("set_Resource", { [2] = owner, [3] = reflected })
run_frames(2)
local reflected_event = runtime.snapshot().events[1]
check(reflected_event.paths[1].path == "effects/field_only.efx", "fields fallback must run after empty methods pass")
check(reflected_event.paths[1].evidence == "reflection_heuristic", "reflection discovery must remain qualified")
check(reflected_event.effect_id == nil, "setter outside play must remain unknown")
runtime.clear()
check(reflected.refs == 0, "resource-holder reference must release")

local method_type = make_type("via.effect.EffectResourceHolder")
method_type.get_methods = function() return { {
    get_name = function() return "get_Resource" end, get_num_params = function() return 0 end,
    call = function() return reflected end,
} } end
local method_holder = object("via.effect.EffectResourceHolder", nil, nil, method_type)
invoke("set_Resource", { [2] = owner, [3] = method_holder })
run_frames(2)
check(runtime.snapshot().events[1].paths[1].path == "effects/field_only.efx", "method recursion must resolve the forward-declared fields function")
runtime.clear()
check(method_holder.refs == 0, "method-holder reference released")

local wrong_holder = resource("effects/wrong_owner.efx")
invoke("set_Resource", { [2] = object("Unrelated.Type"), [3] = wrong_holder })
check(wrong_holder.refs == 0 and runtime.snapshot().events[1].association == "unverified_setter_owner", "shared setter address cannot accept unrelated owners")
runtime.clear()

-- Display throttling never drops capture; bounded ring loss is explicit.
runtime.cfg.max_events, runtime.cfg.min_interval = 2, 999
local before = runtime.stats.evicted
local old = container({ AllPlayers = collection({ resource("effects/old.efx") }) })
invoke(CORE, { [4] = 55 }, old)
invoke(CORE, { [4] = 55 }, nil)
invoke(CORE, { [4] = 55 }, nil)
snapshot = runtime.snapshot()
check(#snapshot.events == 2 and runtime.stats.evicted == before + 1, "ring eviction must be counted")
check(snapshot.events[1].display_throttled and snapshot.events[2].display_throttled, "hidden rapid calls must remain in export")
check(old.refs == 0, "eviction releases held source immediately")
runtime.clear()

-- Deferred observation expiry and script reset both balance references.
runtime.cfg.max_source_frames = 1
local expires = container({ AllPlayers = collection({ resource("effects/expires.efx") }) })
invoke(CORE, { [4] = 66 }, expires)
run_frames(2)
check(expires.refs == 0 and runtime.stats.source_timeouts > 0, "observation timeout releases source and is counted")
runtime.clear()
runtime.cfg.max_source_frames = 300
local resets_source = container({ AllPlayers = collection({ resource("effects/reset.efx") }) })
invoke(CORE, { [4] = 77 }, resets_source)
for _, reset in ipairs(resets) do reset() end
check(resets_source.refs == 0, "script reset releases pending references")
check(runtime.stats.callback_errors == 0 and runtime.stats.context_mismatch == 0, "mock callbacks should remain error-free")

runtime.cfg.enabled = false
local paused_source = container({ AllPlayers = collection({ resource("effects/paused.efx") }) })
invoke(CORE, { [4] = 88 }, paused_source)
check(paused_source.refs == 0 and #runtime.snapshot().events == 0 and runtime.stats.paused_calls == 1, "paused capture must not acquire references or leave events")

-- Simulate a fresh Lua state with one missing runtime type, then its availability.
-- These are test globals only, not an attempt to remove live engine hooks.
_G.__fx_hook_runtime, _G.fx_hook_loaded = nil, nil
frames, resets = {}, {}
local previous_finder = sdk.find_type_definition
local missing_player = true
sdk.find_type_definition = function(name)
    if missing_player and name == "via.effect.EffectPlayer" then return nil end
    return previous_finder(name)
end
local before_retry = installs
local retry_runtime = dofile("fx_hook.lua")
check(installs == before_retry + 10, "missing runtime type must leave only its hook pending")
missing_player = false
retry_runtime.retry_install()
check(installs == before_retry + 11, "retry installs the newly available hook only")
retry_runtime.retry_install()
check(installs == before_retry + 11, "retry must not reinstall successful hooks")
_G.__fx_hook_runtime, _G.fx_hook_loaded = nil, "v9.1"
check(dofile("fx_hook.lua") == nil and installs == before_retry + 11, "older active version must require reset instead of stacking hooks")
print(("fx_hook offline regression: %d checks passed; no game methods executed"):format(checks))
