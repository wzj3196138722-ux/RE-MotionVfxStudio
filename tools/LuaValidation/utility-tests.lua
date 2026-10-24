-- Offline behavioral checks only. Run from the Lua material directory.
-- Does not load REFramework, touch the game, or execute native methods.
local checks = 0
local function check(value, message)
    assert(value, message)
    checks = checks + 1
end
local function field(name, value)
    return { get_name = function() return name end, is_static = function() return true end, get_data = function() return value end }
end
local function list(items)
    return { call = function(_, name, index)
        if name == "get_Count" then return #items end
        if name == "get_Item" then return items[index + 1] end
        error("Unknown list method " .. name)
    end }
end
local function player(rgba, group, mask)
    local p = { rgba = rgba, group = group, mask = mask, refs = 0, actions = {} }
    function p:add_ref() self.refs = self.refs + 1 end
    function p:release() self.refs = self.refs - 1 end
    function p:call(name, value)
        if name == "get_Color" then
            local color = { value = self.rgba }
            function color:get_field(key) assert(key == "rgba"); return self.value end
            function color:set_field(key, v) assert(key == "rgba"); self.value = v end
            return color
        end
        if name == "set_Color" then self.rgba = value.value; return end
        if name == "get_GroupEnabled" then return self.group end
        if name == "set_GroupEnabled" then self.group = value; return end
        if name == "get_EmitMaskBit" then
            if self.no_mask_getter then error("missing getter") end
            return self.mask
        end
        if name == "set_EmitMaskBit" then
            if self.fail_restore and value ~= 0 then error("restore failed") end
            self.mask = value; return
        end
        if name == "set_Action(via.effect.PlayerAction)" then self.actions[#self.actions + 1] = value; return end
        error("Unknown player method " .. name)
    end
    return p
end
local function container(items)
    local c = { items = items, refs = 0, finished = false }
    function c:add_ref() self.refs = self.refs + 1 end
    function c:release() self.refs = self.refs - 1 end
    function c:get_type_definition() return { get_full_name = function() return "via.effect.script.CreatedEffectContainer" end } end
    function c:get_field(name)
        if name == "AllPlayers" then return list(self.items) end
        if name == "CreatedEffectControlers" then return nil end
        error("Unknown container field " .. name)
    end
    function c:call(name) if name == "get_isFinished" then return self.finished end; error(name) end
    return c
end
local function environment()
    local ctx = { callbacks = {}, hooks = {}, storage = {}, buttons = {}, inputs = {}, checks = {}, logs = {}, texts = {}, files = {}, ready = true, spawn_calls = 0, dt = 0.1 }
    local env = setmetatable({}, { __index = _G }); env._G = env
    env.log = {}
    for _, name in ipairs({ "info", "warn", "error" }) do env.log[name] = function(message) ctx.logs[#ctx.logs + 1] = tostring(message) end end
    env.re = {
        on_pre_application_entry = function(name, fn) ctx.callbacks[name] = fn end,
        on_frame = function(fn) ctx.callbacks.frame = fn end,
        on_draw_ui = function(fn) ctx.callbacks.ui = fn end,
        on_script_reset = function(fn) ctx.callbacks.reset = fn end,
    }
    local function take(map, label)
        for key, value in pairs(map) do
            if label:find(key, 1, true) then map[key] = nil; return true, value end
        end
        return false, nil
    end
    env.imgui = {
        tree_node = function() return true end, tree_pop = function() end,
        separator = function() end, same_line = function() end,
        text = function(text) ctx.texts[#ctx.texts + 1] = tostring(text) end,
        button = function(label) local found = take(ctx.buttons, label); return found end,
        checkbox = function(label, value) local found, new = take(ctx.checks, label); return found, found and new or (found and false or value) end,
        input_text = function(label, value) local found, new = take(ctx.inputs, label); return found, found and new or value end,
    }
    local common = { field("EffectA", 35193197), field("EffectB", 35193198), field('Escaped"\\\n\t', 42), field("_INVALID", 0), field("_MAX", 0xFFFFFFFF) }
    local damage = { field("DamageA", 701), field("DamageB", 702) }
    local hit = { field("HitA", 801), field("HitB", 802) }
    local action_fields = { field("Start", 1), field("Kill", 2), field("Pause", 3), field("Resume", 4), field("Step", 5), field("Finish", 6) }
    local types = {}
    local function td(name)
        if types[name] then return types[name] end
        local object = {}
        function object:get_full_name() return name end
        function object:get_parent_type() return nil end
        function object:get_fields()
            if name == "app.EffectID_Common.ID" then return common end
            if name == "app.EffectID_Damage.ID" then return damage end
            if name == "app.EffectID_Hit.ID" then return hit end
            if name == "via.effect.PlayerAction" then return action_fields end
            if name == "via.hid.KeyboardKey" then return { field("F9", 120) } end
            return {}
        end
        function object:get_field(key)
            for _, f in ipairs(self:get_fields()) do if f:get_name() == key then return f end end
        end
        function object:get_method(signature)
            if name == "via.gui.message" then return nil end
            local method = { signature = signature }
            function method:get_return_type() return td(ctx.wrong_return_type and "System.Boolean" or "via.effect.script.CreatedEffectContainer") end
            function method:call(_, ...)
                if signature:find("play", 1, true) then
                    ctx.spawn_calls = ctx.spawn_calls + 1
                    if ctx.spawn_error then error(ctx.spawn_error) end
                    return ctx.returned
                end
                if signature == ".ctor()" then return end
                error("Unknown method " .. signature)
            end
            return method
        end
        types[name] = object
        return object
    end
    local he = { get_field = function() return {} end }
    local hs = { get_field = function() return he end }
    local go = { call = function() return {} end }
    local ch = { call = function(_, name)
        if name == "get_HunterStatus" then return hs end
        if name == "get_GameObject" then return go end
        if name == "get_Weapon" then return nil end
        error("Unknown character method " .. name)
    end }
    local mp = { call = function() return ch end }
    local pm = { call = function() return mp end }
    env.sdk = {
        PreHookResult = { CALL_ORIGINAL = 0 },
        find_type_definition = function(name)
            if ctx.types_unready then return nil end
            return td(name)
        end,
        hook = function(method, pre, post) ctx.hooks[method.signature] = { pre = pre, post = post } end,
        to_int64 = function(value) return type(value) == "table" and value.ptr or value end,
        to_ptr = function(value) return { ptr = value } end,
        to_managed_object = function(value) ctx.to_managed_calls = (ctx.to_managed_calls or 0) + 1; return value end,
        is_managed_object = function(value) return type(value) == "table" and not value.invalid end,
        get_managed_singleton = function() return ctx.ready and pm or nil end,
        get_native_singleton = function(name) return { native_name = name } end,
        call_native_func = function(object, _, method)
            check(object.native_name ~= nil, "native singleton must use native API")
            if method == "get_DeltaTime" then return ctx.dt end
            if method == "isDown" then return ctx.key_down == true end
            error(method)
        end,
        typeof = function(name) return td(name) end,
    }
    env.thread = { get_hook_storage = function() return ctx.storage[#ctx.storage] end }
    env.require = function(name)
        assert(name == "fx_preview_runtime")
        if ctx.no_runtime then error("module unavailable") end
        return { new = function(owner)
            return {
                set_hotkeys = function(_, enabled) if enabled then ctx.hotkey_owner = owner elseif ctx.hotkey_owner == owner then ctx.hotkey_owner = nil end end,
                owns_hotkeys = function() return ctx.hotkey_owner == owner end,
            }
        end }
    end
    env.Vector3f = { new = function(x, y, z) return { x = x, y = y, z = z } end }
    env.io = { open = function(path)
        local file = { text = "" }
        function file:write(text) self.text = self.text .. text; return self end
        function file:close() ctx.files[path] = self.text; return true end
        return file
    end }
    function ctx:load(file) assert(loadfile(file, "t", env))() end
    function ctx:ui() self.callbacks.ui() end
    function ctx:tick() if self.callbacks.LockScene then self.callbacks.LockScene() end end
    function ctx:press(key) self.buttons[key] = true; self:ui() end
    function ctx:fire(hook, id, result)
        self.storage[#self.storage + 1] = {}
        hook.pre({ [4] = id })
        hook.post(result)
        table.remove(self.storage)
    end
    ctx.env = env
    return ctx
end
local core = "playEffectCore(System.UInt32, app.EffectID_Common.ID, via.GameObject, app.cEffectOverwriteParams, via.GameObject)"
local function enable_hide(ctx)
    ctx.inputs["Common.ID 或完整枚举名"] = "35193197"
    ctx.checks["启用实例观察与隐藏"] = true
    ctx.buttons["添加管理##"] = true
    ctx:ui()
end

-- Recursive matched/unmatched calls must not steal each other's container.
do
    local c = environment(); c:load("fx_hide.lua"); enable_hide(c)
    local p = player(0x12345678, false, 37); local outer = container({ p })
    local inner_player = player(99, true, 15); local inner = container({ inner_player })
    local h = c.hooks[core]
    c.storage[1] = {}; h.pre({ [4] = 35193197 })
    c.storage[2] = {}; h.pre({ [4] = 999 }); h.post(inner); c.storage[2] = nil
    h.post(outer); c.storage[1] = nil
    c:press("隐藏##fxh_toggle"); c:tick()
    check(p.rgba == 0 and p.group == false and p.mask == 0, "matching outer instance hidden")
    check(inner_player.rgba == 99 and inner.refs == 0, "unmatched nested instance untouched")
    check(outer.refs == 1 and p.refs == 1, "tracked objects explicitly retained once")
    c:press("恢复可视属性##fxh_toggle")
    check(p.rgba == 0, "UI enqueues restoration without game mutation")
    c:tick()
    check(p.rgba == 0x12345678 and p.group == false and p.mask == 37, "restores exact nondefault values")
    check(#p.actions == 0, "reversible hiding never kills or starts the effect")
    c:press("隐藏##fxh_toggle"); c:tick(); p.fail_restore = true
    c:press("恢复可视属性##fxh_toggle"); c:tick()
    check(p.mask == 0 and p.refs == 1, "failed restoration retains snapshot and player")
    p.fail_restore = false; c:press("重试恢复全部可视属性"); c:tick()
    check(p.mask == 37, "restoration retry uses original mask")
    outer.finished = true; c:tick()
    check(outer.refs == 0 and p.refs == 0, "completed containers and players released")
end
-- If reversible getters are unavailable, no visual fields may be changed.
do
    local c = environment(); c:load("fx_hide.lua"); enable_hide(c)
    local p = player(123, true, 29); p.no_mask_getter = true
    c:fire(c.hooks[core], 35193197, container({ p }))
    c:press("隐藏##fxh_toggle"); c:tick()
    check(p.rgba == 123 and p.group and p.mask == 29, "missing getter refuses partial hiding")
end
-- Return type verification must prevent bool/int conversion into a managed pointer.
do
    local c = environment(); c.wrong_return_type = true; c:load("fx_hide.lua")
    check(next(c.hooks) == nil, "non-container hook return type rejected")
end
-- Block is disabled by default, namespace-aware, validates all tokens, uses pointer values.
do
    local c = environment(); c:load("fx_block.lua")
    c.inputs["源 ID"] = "35193197"; c.inputs["目标 ID"] = "35193198"; c:press("添加替换规则")
    local args = { [4] = 35193197 }; c.hooks[core].pre(args)
    check(args[4] == 35193197, "replacement disabled by default")
    c.checks["启用 ID 替换"] = true; c:ui(); c.hooks[core].pre(args)
    check(type(args[4]) == "table" and args[4].ptr == 35193198, "replacement encoded as pointer")
    c.inputs["目标 ID"] = "0"; c:press("添加替换规则")
    args = { [4] = 35193197 }; c.hooks[core].pre(args)
    check(args[4].ptr == 35193198, "zero target cannot override valid rule")
    local hit_args = { [3] = 35193197 }
    c.hooks["playEffectCore(app.EffectID_Hit.ID, via.GameObject, app.cEffectOverwriteParams, via.GameObject)"].pre(hit_args)
    check(hit_args[3] == 35193197, "Common rule does not leak into Hit namespace")
end
-- Spawn hotkey must be opt-in and native; no nil-return false success; retry delayed players.
do
    local c = environment(); local p = player(1, true, 3); c.returned = container({ p }); c:load("fx_spawn.lua")
    c.key_down = true; c.callbacks.frame(); c:tick()
    check(c.spawn_calls == 0, "F9 disabled by default")
    c.checks["启用 F9"] = true; c:ui(); c.callbacks.frame(); c:tick()
    check(c.spawn_calls == 1 and c.returned.refs == 1, "native F9 queues creation and retains returned container")
    c.callbacks.frame(); c:tick(); check(c.spawn_calls == 1, "held F9 does not retrigger")
    c:press("停止本工具返回容器"); check(#p.actions == 0, "stop only queued in UI"); c:tick()
    check(#p.actions == 1 and c.returned.refs == 0, "only owned players receive Kill and container released")
    c.returned = nil; c:press("触发一次"); c:tick()
    check(c.logs[#c.logs]:find("返回 nil", 1, true) ~= nil, "nil return reported as failed creation")
    c.returned = container({}); c:press("触发一次"); c:tick(); c:press("停止本工具返回容器"); c:tick()
    check(c.returned.refs == 1, "empty unfinished container retained for stop retry")
    c.returned.items = { player(2, true, 7) }; c:press("停止本工具返回容器"); c:tick()
    check(c.returned.refs == 0 and #c.returned.items[1].actions == 1, "delayed players stopped on retry")
    local before = c.spawn_calls; c.checks["打击世界坐标"] = true; c.inputs["Damage.ID"] = "35193197"; c:ui(); c:press("触发一次"); c:tick()
    check(c.spawn_calls == before, "Common ID cannot be used as Damage ID")
end
-- Timer uses native delta; missing player does not disable future manual retries.
do
    local c = environment(); c.ready = false; c.returned = container({ player(1, true, 1) }); c:load("fx_spawn.lua")
    c:press("触发一次"); c:tick(); check(c.spawn_calls == 0, "early scene spawn rejected")
    c.ready = true; c:press("触发一次"); c:tick(); check(c.spawn_calls == 1, "scene-ready spawn can retry")
    c.checks["持续触发"] = true; c.inputs["间隔秒"] = "0.5"; c:ui()
    c.dt = 0.25; c:tick(); check(c.spawn_calls == 1, "repeat waits specified elapsed delta")
    c:tick(); check(c.spawn_calls == 2, "repeat uses engine delta")
    c.callbacks.reset()
end
-- Export can retry after a premature load and creates syntactically strict JSON objects.
do
    local c = environment(); c.types_unready = true; c:load("fx_dump_ids.lua")
    c:press("导出/重新导出"); c:tick(); check(next(c.files) == nil, "unready export does not overwrite old output")
    c.types_unready = false; c:press("导出/重新导出"); c:tick()
    local payload = c.files["fx_effect_ids.json"]
    check(payload and payload:find('"Common.ID"', 1, true) ~= nil, "ready export works without script reload")
    check(not payload:find(",%s*}"), "JSON does not contain trailing comma")
    check(payload:find("\\u000a", 1, true) and payload:find("\\u0009", 1, true), "JSON escapes newline and tab control characters")
    local constants = assert(load(c.files["fx_effect_ids.lua"], "generated constants", "t", {}))()
    check(constants["Common.ID"].EffectA == 35193197, "generated Lua constants parse and preserve values")
    local output = arg[1]
    if output then
        local f = assert(io.open(output, "w")); f:write(payload); f:close()
    end
end
-- Shared runtime ownership suppresses conflicting F9; missing runtime preserves button work.
do
    local c = environment(); c.returned = container({ player(1, true, 1) }); c:load("fx_spawn.lua")
    c.checks["启用 F9"] = true; c:ui(); c.hotkey_owner = "fx_master"; c.key_down = true
    c.callbacks.frame(); c:tick(); check(c.spawn_calls == 0, "F9 suppressed after another client takes ownership")
    local absent = environment(); absent.no_runtime = true; absent.returned = container({ player(1, true, 1) }); absent:load("fx_spawn.lua")
    absent.checks["启用 F9"] = true; absent:ui(); absent.key_down = true; absent.callbacks.frame(); absent:tick()
    check(absent.spawn_calls == 0, "missing shared runtime keeps F9 disabled")
    absent:press("触发一次"); absent:tick(); check(absent.spawn_calls == 1, "missing shared runtime preserves button triggering")
end
-- Scene-invalid objects must not receive stop calls; retain failures and report them.
do
    local c = environment(); local p = player(1, true, 1); c.returned = container({ p }); c:load("fx_spawn.lua")
    c:press("触发一次"); c:tick(); c.returned.invalid = true; c.ready = false; c:tick()
    check(#p.actions == 0, "scene-invalid container is not dereferenced to kill players")
    check(c.logs[#c.logs]:find("已失效", 1, true), "invalid container cleanup failure is reported")
end
-- Diagnostic UI persists across unavailable-player state instead of returning from script.
do
    local c = environment(); c.ready = false; c:load("fx_bone_debug.lua")
    check(c.callbacks.ui ~= nil, "bone diagnostic available before scene readiness")
    c:press("运行/重新运行诊断"); c:tick()
    check(c.files["fx_bone_debug.txt"]:find("当前没有主玩家", 1, true) ~= nil or c.files["fx_bone_debug.txt"]:find("未就绪", 1, true) ~= nil, "bone diagnostic reports unavailable player")
    c.ready = true; c:press("运行/重新运行诊断"); c:tick()
    check(c.files["fx_bone_debug.txt"]:find("主武器未就绪", 1, true) ~= nil, "bone diagnostic retries and reaches weapon lookup")
end
print("utility-tests: " .. checks .. " checks passed")
