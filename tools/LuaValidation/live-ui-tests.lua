-- Isolated mocks: no game, no filesystem writes, no backend process.
local script_path = arg[1] or "MotionVfxLive.lua"
local reload_source = arg[2]
local real_reload_fixture = arg[3] == "real-files"
local passed, failed, assertions = 0, 0, 0
local function control_id(label) return label:match("##.*$") or label end
local function take_control_value(values, label)
    local value = values[label]
    if value ~= nil then values[label] = nil; return value end
    -- Wrapped visible captions retain their explicit IDs. Test clicks target
    -- that stable suffix rather than depending on a particular line width.
    local id = control_id(label)
    for key, candidate in pairs(values) do
        if control_id(key) == id then values[key] = nil; return candidate end
    end
end
local function equal(actual, expected, label)
    assertions = assertions + 1
    assert(actual == expected, (label or "value") .. ": " .. tostring(actual) .. " ~= " .. tostring(expected))
end
local function test(name, fn)
    local ok, err = pcall(fn)
    if ok then passed = passed + 1; print("PASS " .. name)
    else failed = failed + 1; print("FAIL " .. name .. ": " .. tostring(err)) end
end
local function copy_values(value)
    local result = {}; for key, item in pairs(value or {}) do result[key] = item end; return result
end
local function source_path_key(path)
    return tostring(path or ""):lower():gsub("\\", "/"):gsub("^@/", ""):gsub("^natives/stm/", ""):gsub("%.efx%.%d+$", ".efx")
end
local function environment(initial_files, debug_native)
    local c = { time = 100, files = { ["MotionVfxLive/status.json"] = { ready = true, protocol = 1 } }, writes = {}, reads = {},
        callbacks = { frames = {}, draws = {}, resets = {}, lock = {} }, calls = {}, runtime_queue = {},
        scene_writes = {}, buttons = {}, inputs = {}, checks = {}, messages = {}, scene_address = 12,
        paused = false, speed = 0.8, simulated_time = 0, preview_speed = 1, preview_paused = false,
        application = {}, global_speed = 1, global_writes = {}, trail_time = 0,
        effect_manager = {}, effect_update = true, effect_update_writes = {}, frame_trail = 0,
        show_ui = false, windows_started = 0, windows_ended = 0, child_windows = 0, window_sizes = 0, drawn_fields = {},
        available_width = 1120, available_height = 820, display_width = 1920, display_height = 1080,
        window_x = 100, window_y = 100, window_positions = 0,
        child_history = {}, child_sizes = {}, drawn_buttons = {}, drawn_inputs = {}, tree_history = {},
        style_vars = 0, style_colors = 0, font_sizes = 0, item_widths = {}, tree_depth = 0 }
    for path, value in pairs(initial_files or {}) do c.files[path] = value end
    local env = setmetatable({}, { __index = _G }); env._G = env
    env.io = { open = function(path, mode)
        if c.files[path] ~= nil or c.existing_bad_json and c.existing_bad_json[path] then return { close = function() end } end
        if c.files["fixture:nil-only-io"] then return nil end
        return nil, "File not found", 2
    end }
    env.fs = { glob = function(pattern)
        equal(pattern, "^MotionVfxLive[/\\\\]ai-journal\\.json$", "bounded journal existence regex")
        if c.files["MotionVfxLive/ai-journal.json"] ~= nil then return { "MotionVfxLive\\ai-journal.json" } end
        return {}
    end }
    env.package = { loaded = {}, path = "./intentionally-missing/?.lua" }
    env.os = { time = function() return c.time end }
    env.log = { warn = function(v) c.messages[#c.messages + 1] = v end }
    env.json = {
        load_file = function(path)
            c.reads[path] = (c.reads[path] or 0) + 1
            if c.load_failure then error("read failure") end
            if c.files[path] == "fixture:invalid-json" then return nil end
            return c.files[path]
        end,
        dump_file = function(path, value)
            if c.write_failure == path then error("write failure") end
            if c.write_false == path then return false end
            if c.write_noop == path then return true end
            c.files[path] = value; c.writes[#c.writes + 1] = { path = path, value = value }; return true
        end,
    }
    env.re = {
        on_frame = function(fn) c.callbacks.frames[#c.callbacks.frames + 1] = fn end,
        on_draw_ui = function(fn) c.callbacks.draws[#c.callbacks.draws + 1] = fn end,
        on_pre_application_entry = function(name, fn) equal(name, "LockScene"); c.callbacks.lock[#c.callbacks.lock + 1] = fn end,
        on_script_reset = function(fn) c.callbacks.resets[#c.callbacks.resets + 1] = fn end,
    }
    env.reframework = { is_drawing_ui = function() return c.show_ui end }
    env.imgui = setmetatable({
        begin_window = function(_, open)
            equal(type(open), "boolean")
            c.windows_started = c.windows_started + 1
            if c.next_window_size then
                c.available_width, c.available_height = c.next_window_size[1], c.next_window_size[2]
                c.next_window_size = nil
            end
            if c.next_window_position then
                c.window_x, c.window_y = c.next_window_position[1], c.next_window_position[2]
                c.next_window_position = nil
            end
            return not c.close_window
        end,
        end_window = function() c.windows_ended = c.windows_ended + 1 end,
        begin_child_window = function(name, size, border)
            equal(type(size), "table"); equal(#size, 2); equal(border, true)
            c.child_history[#c.child_history + 1] = { name = name, width = size[1], height = size[2], depth = c.child_windows }
            c.child_windows = c.child_windows + 1
            local parent = c.child_sizes[c.child_windows - 1] or { x = c.available_width, y = c.available_height }
            c.child_sizes[c.child_windows] = { x = size[1] > 0 and size[1] or parent.x, y = size[2] > 0 and size[2] or parent.y }
            return c.clipped_child ~= name
        end,
        end_child_window = function()
            c.child_sizes[c.child_windows] = nil; c.child_windows = c.child_windows - 1
            assert(c.child_windows >= 0, "child stack underflow")
        end,
        set_next_window_size = function(size)
            assert(type(size[1]) == "number" and size[1] > 0); assert(type(size[2]) == "number" and size[2] > 0)
            c.window_sizes = c.window_sizes + 1; c.initial_window_size = { size[1], size[2] }
            c.next_window_size = { size[1], size[2] }
        end,
        set_next_window_pos = function(position)
            equal(type(position), "table"); equal(#position, 2)
            assert(type(position[1]) == "number" and type(position[2]) == "number")
            c.window_positions = c.window_positions + 1
            c.next_window_position = { position[1], position[2] }
        end,
        get_display_size = function() return { x = c.display_width, y = c.display_height } end,
        get_window_pos = function()
            if c.fail_window_position then error("mock window position failure") end
            return { x = c.window_x, y = c.window_y }
        end,
        get_window_size = function() return c.child_sizes[c.child_windows] or { x = c.available_width, y = c.available_height } end,
        get_cursor_pos = function() return { x = 12, y = 240 } end,
        calc_text_size = function(value)
            local length = 0
            for _, codepoint in utf8.codes(value) do length = length + (codepoint > 127 and 16 or 8) end
            return { x = length, y = 18 }
        end,
        get_default_font_size = function() return 16 end,
        set_next_item_width = function(width) assert(type(width) == "number"); c.item_widths[#c.item_widths + 1] = width end,
        push_style_var = function(index, value)
            assert(type(index) == "number")
            assert(type(value)=="number" or (type(value)=="table" and type(value.x)=="number" and type(value.y)=="number"),
                "REFramework style vars require a scalar or Vector2f")
            c.style_vars = c.style_vars + 1
        end,
        pop_style_var = function(count) c.style_vars = c.style_vars - (count or 1); assert(c.style_vars >= 0, "style var stack underflow") end,
        push_style_color = function(index, value)
            assert(type(index) == "number"); assert(math.type(value) == "integer", "REFramework colors require packed int or Vector4f")
            c.style_colors = c.style_colors + 1
        end,
        pop_style_color = function(count) c.style_colors = c.style_colors - (count or 1); assert(c.style_colors >= 0, "style color stack underflow") end,
        push_font_size = function(size) assert(type(size) == "number" and size > 0); c.font_sizes = c.font_sizes + 1 end,
        pop_font_size = function() c.font_sizes = c.font_sizes - 1; assert(c.font_sizes >= 0, "font stack underflow") end,
        tree_node = function(label)
            c.tree_history[#c.tree_history + 1] = { label = label, id = control_id(label) }
            if c.closed_tree == label then return false end
            c.tree_depth = c.tree_depth + 1; return true
        end,
        tree_pop = function() c.tree_depth = c.tree_depth - 1; assert(c.tree_depth >= 0, "tree stack underflow") end,
        button = function(label)
            c.drawn_buttons[#c.drawn_buttons + 1] = { label = label, id = control_id(label), child_depth = c.child_windows }
            return take_control_value(c.buttons, label) == true
        end,
        input_text = function(label, value)
            c.last_text_item = label
            c.drawn_inputs[#c.drawn_inputs + 1] = { label = label, id = control_id(label), value = value }
            if label:find("##mvl_value_", 1, true) then c.drawn_fields[#c.drawn_fields + 1] = label end
            if c.fail_input == label then error("mock input failure: " .. label) end
            local next_value = c.inputs[label]; c.inputs[label] = nil; return next_value ~= nil, next_value or value
        end,
        is_item_active = function() return c.active_text_item ~= nil and c.active_text_item == c.last_text_item end,
        combo = function(_, value) return false, value end,
        checkbox = function(label, value) local next_value = c.checks[label]; c.checks[label] = nil; if next_value ~= nil then return true, next_value end; return false, value end,
        text = function(value) c.messages[#c.messages + 1] = value end,
    }, { __index = function() return function() end end })
    local player = { call = function(_, method, value)
        if c.failed_effect_getter == method then error("mock getter failure") end
        if method == "set_PlaySpeed" then
            equal(math.type(value), "float", "EffectPlayer float argument")
            c.calls[#c.calls + 1] = { method = method, value = value }
            if not c.ignore_speed_write then c.preview_speed = value end
        elseif method == "get_PlaySpeed" then return c.preview_speed
        elseif method == "get_Paused" then return c.preview_paused
        elseif method == "get_ReadyResource" then return not c.resource_not_ready
        elseif method == "get_Running" then return true
        elseif method == "get_Valid" then return true
        elseif method == "get_EmitterInstanceNum" then return 7
        elseif method == "get_ResourcePath" then return c.instance.path
        elseif method == "get_State" then return 2
        elseif method == "get_Culled" then return false
        elseif method == "get_GroupEnabled" then return false
        else error("unexpected player method " .. method) end
    end }
    local client = {}
    function client:get_instance(id) equal(id, "preview"); return c.instance end
    function client:play(id, options, callback)
        equal(math.type(options.play_speed), "float", "Playback float speed")
        equal(math.type(options.scale), "float", "Playback float scale")
        equal(math.type(options.offset.x), "float", "Playback float position")
        c.runtime_queue[#c.runtime_queue + 1] = function()
            c.instance = { path = options.path, player = player }
            c.last_play_options = options; c.preview_speed = options.play_speed
            callback({ ok = true, operation = "play" })
        end
        return "play-1"
    end
    function client:stop(id, callback)
        c.runtime_queue[#c.runtime_queue + 1] = function() c.instance = nil; callback({ ok = true, operation = "stop" }) end
        return "stop-1"
    end
    function client:action(id, action, callback)
        c.runtime_queue[#c.runtime_queue + 1] = function()
            if not c.instance then callback({ ok = false, operation = "action", message = "no preview" }); return end
            if action == "Pause" then c.preview_paused = true elseif action == "Resume" then c.preview_paused = false end
            c.last_action = action; callback({ ok = true, operation = "action" })
        end
        return "action-1"
    end
    function client:shutdown(callback)
        c.runtime_queue[#c.runtime_queue + 1] = function()
            if c.shutdown_fail then callback({ ok = false, message = "mock cleanup failure" }); return end
            c.instance = nil; client.closed = true; callback({ ok = true, operation = "shutdown" })
        end
        return "shutdown-1"
    end
    c.runtime = { version = "2.1", new = function(owner) equal(owner, "MotionVfxLive"); return client end,
        get_player_transform = function() return "player-transform" end,
        get_weapon_transform = function() return "weapon-transform" end,
        clients = { MotionVfxLive = client } }
    env.fx_preview_runtime_v2 = c.runtime
    c.capture_state = { recording = false, sceneEpoch = 1, actor = { available = true, weaponType = "wp02", sceneEpoch = 1 },
        resources = {}, events = {}, counters = {}, errors = {}, installedHooks = 3, requestedHooks = 3 }
    c.capture = { version = "capture-test",
        get_resource_source = function(path, source_id)
            if c.source_error then return nil, c.source_error end
            for _, resource in ipairs(c.capture_state.resources or {}) do
                if source_path_key(resource.canonicalPath or resource.path) == source_path_key(path) and resource.localConfirmed == true then
                    local descriptor = resource.latestSource or { sourceId = "source-1", eventId = "event-1", sceneEpoch = 1,
                        actualPath = resource.path, sharedTargetPath = resource.canonicalPath or resource.path,
                        actualHolderIdentity = "1234", playerIdentity = "5678", localConfirmed = true,
                        sourceKind = "actual_effect_player_holder", availability = "retained_observation",
                        currentPlayerVerified = false, cacheBytesAvailable = false }
                    if source_id and source_id ~= descriptor.sourceId then return nil, "source expired" end
                    return descriptor, {}
                end
            end
            return nil, "source unavailable"
        end,
        start = function() c.capture_starts = (c.capture_starts or 0) + 1; c.capture_state.recording = true; return true end,
        stop = function() c.capture_stops = (c.capture_stops or 0) + 1; c.capture_state.recording = false; return true end,
        clear = function() c.capture_clears = (c.capture_clears or 0) + 1; c.capture_state.resources = {}; c.capture_state.events = {}; return true end,
        snapshot = function() return c.capture_state end }
    c.binding_state = { phase = "idle", busy = false, appliedCount = 0 }
    c.binding_queue, c.binding_applies = {}, {}
    c.bindings = {
        configure = function(options) equal(options.capture, c.capture); c.binding_configures = (c.binding_configures or 0) + 1; return true end,
        snapshot = function() return c.binding_state end,
        apply = function(original, generated, callback)
            if c.binding_state.busy then return false, "busy" end
            if c.binding_reject then return false, "unsupported resource holder" end
            c.binding_applies[#c.binding_applies + 1] = { original = original, generated = generated }
            c.binding_state.busy = true
            c.binding_queue[#c.binding_queue + 1] = function()
                c.binding_state.busy = false
                c.binding_state.appliedCount = c.binding_fail and 0 or 1
                c.binding_state.phase = c.binding_fail and "failed" or "awaiting_action"
                callback({ ok = not c.binding_fail, phase = c.binding_state.phase, message = "mock binding result" })
            end
            return true
        end,
        restore_all = function(callback)
            if c.binding_state.busy then return false, "busy" end
            c.binding_restores = (c.binding_restores or 0) + 1; c.binding_state.busy = true
            c.binding_queue[#c.binding_queue + 1] = function()
                c.binding_state.busy = false
                if not c.binding_restore_fail then c.binding_state.appliedCount = 0 end
                c.binding_state.phase = c.binding_restore_fail and "failed" or "restored"
                callback({ ok = not c.binding_restore_fail, phase = c.binding_state.phase, message = "mock restore result" })
            end
            return true
        end }
    env.MotionVfxActionCapture, env.MotionVfxActionBindings = c.capture, c.bindings
    env.MotionVfxDebugInput = debug_native
    env.require = function(name)
        if name == "MotionVfxLive.fx_ai_bridge" or name == "MotionVfxLive.fx_debug_hotkeys" or name == "MotionVfxLive.fx_precache_runtime" or name == "MotionVfxLive.fx_parameter_ui" or name == "MotionVfxLive.fx_efx_clipboard" then
            return assert(loadfile(script_path:match("^(.*)[/\\]") .. "/MotionVfxLive/" .. name:match("[^.]+$") .. ".lua", "t", env))()
        end
        equal(name, "MotionVfxLive.fx_preview_runtime")
        if c.missing_runtime then error("missing runtime") end
        return c.runtime
    end
    local methods = {
        ["get_Pause()"] = true, ["set_Pause(System.Boolean)"] = true,
        ["get_TimeScale()"] = true, ["set_TimeScale(System.Single)"] = true,
    }
    local td = { get_method = function(_, name) if c.missing_scene_methods then return nil end; return methods[name] and {} or nil end }
    c.scene = { get_address = function() return c.scene_address end,
        call = function(_, method, value)
            if method == "get_Pause()" then if c.bad_pause_readback then return nil end; return c.paused end
            if method == "get_TimeScale()" then
                if c.speed_read_error then error("mock scene speed read failure") end
                if c.bad_speed_readback then return "invalid" end
                return c.speed
            end
            if method == "get_FrameCount" then return 130 end
            if method == "set_Pause(System.Boolean)" then
                c.scene_writes[#c.scene_writes + 1] = { method = method, value = value, address = c.scene_address }
                if c.fail_pause_write then error("mock scene pause write failure") end
                if not c.ignore_pause_write then c.paused = value end; return
            end
            if method == "set_TimeScale(System.Single)" then
                equal(math.type(value), "float", "Scene float argument")
                c.scene_writes[#c.scene_writes + 1] = { method = method, value = value, address = c.scene_address }
                if c.fail_speed_write then error("mock scene speed write failure") end
                if not c.ignore_speed_write then
                    c.speed = value==0 and c.zero_write_replacement or value
                end
                if c.fail_speed_after_write then error("mock scene speed write failed after mutation") end
                return
            end
            error("unexpected scene method " .. method)
        end }
    local app_td = {get_method=function(_,name)
        if c.missing_global_methods then return nil end
        return (name=="get_GlobalSpeed()" or name=="set_GlobalSpeed(System.Single)") and {} or nil
    end}
    local effect_td = {get_method=function(_,name)
        if c.missing_effect_update_methods then return nil end
        return (name=="get_EnableUpdate()" or name=="set_EnableUpdate(System.Boolean)") and {} or nil
    end}
    env.sdk = {
        find_type_definition = function(name)
            if name=="via.Application" then return app_td end
            if name=="via.effect.detail.PlayerManager" then return effect_td end
            return td
        end,
        get_native_singleton = function(name)
            if name=="via.Application" then return c.application end
            if name=="via.effect.detail.PlayerManager" then
                if c.no_effect_manager then return nil end
                return c.effect_manager
            end
            return {}
        end,
        call_native_func = function(instance, _, method, value)
            if instance==c.effect_manager then
                if method=="get_EnableUpdate()" then
                    if c.bad_effect_update_readback then return nil end
                    return c.effect_update
                end
                if method=="set_EnableUpdate(System.Boolean)" then
                    equal(type(value),"boolean","Effect update boolean argument")
                    c.effect_update_writes[#c.effect_update_writes+1]=value
                    if c.fail_effect_update_write then error("mock effect update write failure") end
                    if not c.ignore_effect_update_write then c.effect_update=value end
                    if c.fail_effect_update_after_write then error("mock effect update partial write failure") end
                    return
                end
                error("unexpected effect update method "..method)
            end
            if instance==c.application then
                if method=="get_GlobalSpeed()" then
                    if c.global_read_error then error("mock global time read failure") end
                    return c.global_speed
                end
                if method=="get_DeltaTime" then return c.global_speed end
                if method=="set_GlobalSpeed(System.Single)" then
                    equal(math.type(value),"float","Application speed float argument")
                    c.global_writes[#c.global_writes+1]=value
                    if c.fail_global_write then error("mock global speed write failure") end
                    if not c.ignore_global_write then c.global_speed=value end
                    if c.fail_global_after_write then error("mock global setter failed after mutation") end
                    return
                end
                error("unexpected application method "..method)
            end
            equal(method, "get_CurrentScene")
            if c.scene_read_error then error("mock current scene failure") end
            if c.no_scene then return nil end
            return c.scene
        end }
    c.module = assert(loadfile(script_path, "t", env))()
    c.env = env
    function c:frame(count)
        self.time = self.time + 1
        for _ = 1, count or 1 do
            for _, callback in ipairs(self.callbacks.frames) do callback() end
            -- Reproduce the reported game behavior: the Pause flag may read
            -- back true while scene simulation continues. Only TimeScale is
            -- authoritative for this fixture's advancement.
            self.simulated_time = self.simulated_time + self.speed*self.global_speed
            -- Follow-trail fixture advances on application time, bypassing the
            -- scene multiplier. Setting only scene speed=0 reproduces the bug.
            self.trail_time = self.trail_time + self.global_speed
            -- A second fixture deliberately advances per update even at zero
            -- scene AND application time, as reported for the 0.5.3 candidate.
            if self.effect_update then self.frame_trail = self.frame_trail + 1 end
        end
    end
    function c:draw(tab) self.module.state.active_tab = tab or "edit"; self.module.draw() end
    function c:launcher() for _, callback in ipairs(self.callbacks.draws) do callback() end end
    function c:lock()
        local bindings_queue = self.binding_queue; self.binding_queue = {}
        for _, callback in ipairs(bindings_queue) do callback() end
        local queue = self.runtime_queue; self.runtime_queue = {}
        for _, callback in ipairs(queue) do callback() end
        for _, callback in ipairs(self.callbacks.lock) do callback() end
    end
    function c:respond(response)
        response.id = response.id or self.module.state.pending.id
        self.files["MotionVfxLive/response.json"] = response
        self:frame(6)
    end
    function c:reload()
        assert(reload_source, "Pass the manual reload helper source as argument 2")
        self.env.package = { loaded = { MotionVfxLive = self.module, ["MotionVfxLive.fx_preview_runtime"] = self.runtime, Unrelated = "keep" } }
        self.env.loadfile = function(path)
            equal(path, "reframework/autorun/MotionVfxLive.lua")
            if self.compile_failure then return nil, "mock missing deployment" end
            if self.real_reload then return loadfile(path, "t", self.env) end
            return function()
            self.reload_count = (self.reload_count or 0) + 1
            self.replacement = { active = true, version = "replacement",
                poll = function() self.replacement_polls = (self.replacement_polls or 0) + 1 end,
                synchronize = function()
                    self.readonly_syncs = (self.readonly_syncs or 0) + 1
                    if self.sync_failure then error("mock sync unavailable") end
                    return true
                end }
            self.env.MotionVfxLive = self.replacement
            return self.replacement
            end
        end
        return assert(load(reload_source, "@manual-reload", "t", self.env))()
    end
    c:frame()
    return c
end

if arg[4]=="export-environment" then return environment end

local function expression_fixture()
    local c = environment()
    c.module.state.document = { source = "11_it00_900.efx.5571972", revision = 9, entries = {}, actions = {} }
    c.expression_path = "expressions.expressions[0].components[0].data.value"
    c.module.inspect({ kind = "entry", index = 1, attributeIndex = 4 })
    c:respond({ ok = true, fields = { { path = c.expression_path, value = "1", editable = true } },
        expressionCurves = { { index = 0, channel = "spawnNum", assignment = "Assign", formula = "Lerp(1, 0, SmoothStep(1.5, 3, ext:302732036))",
            axis = "EM_SPEED", xMin = 0, xMax = 5, samples = {1, 0.5, 0},
            values = { {path = c.expression_path, label = "Lerp.起始值 from", value = "1"} } } } })
    return c
end

local function expression_control(c, kind, path)
    local prefix = "##mvl_expression_" .. (kind == "input" and "value_" or "set_") .. path
    local controls = kind == "input" and c.drawn_inputs or c.drawn_buttons
    for i = #controls, 1, -1 do
        if controls[i].id:sub(1, #prefix) == prefix then return controls[i] end
    end
    error("Expression control was not drawn: " .. prefix)
end

local function paired_expression_response(c, first, second)
    local fields, curves = {}, {}
    for i, channel in ipairs({ "spawnNum", "spawnNumRange" }) do
        local path, value = c.expression_paths[i], i == 1 and first or second
        fields[i] = { path = path, value = value, editable = true }
        curves[i] = { index = i - 1, channel = channel, assignment = "Assign",
            formula = "Lerp(" .. value .. ", 0, SmoothStep(1.5, 3, ext:302732036))", axis = "EM_SPEED", xMin = 0, xMax = 5,
            samples = { tonumber(value), tonumber(value) / 2, 0 },
            values = { { path = path, label = "Lerp.起始值 from", value = value } } }
    end
    return { ok = true, fields = fields, expressionCurves = curves }
end

local function paired_expression_fixture()
    local c = environment()
    c.module.state.document = { source = "samples/a/11_it00_900.efx.5571972", revision = 9, actions = {}, entries = {
        { index = 1, name = "0_smk", attributes = { { index = 4, type = "SpawnExpression" } } },
        { index = 4, name = "1_GPUP", attributes = { { index = 3, type = "SpawnExpression" }, { index = 4, type = "SpawnExpression" } } }
    } }
    c.expression_paths = { "expressions.expressions[0].components[0].data.value", "expressions.expressions[1].components[0].data.value" }
    c.module.inspect({ kind = "entry", index = 4, attributeIndex = 3 })
    c:respond(paired_expression_response(c, "1", "10"))
    return c
end

test("expression plot uses submitted samples with REFramework draw list calls and no game mutation", function()
    local c = expression_fixture(); local lines = 0
    c.env.imgui.get_cursor_screen_pos = function() return {x = 10, y = 20} end
    c.env.imgui.get_window_draw_list = function() return {add_line = function(_, a, b, color, thickness)
        equal(type(a[1]), "number"); equal(type(b[2]), "number"); equal(math.type(color), "integer")
        assert(thickness > 0); lines = lines + 1
    end} end
    local previous = c.files["MotionVfxLive/request.json"]; c:draw()
    equal(lines, 4); equal(c.module.state.ui_error, nil); equal(c.files["MotionVfxLive/request.json"], previous)
    equal(#c.scene_writes, 0); equal(#c.binding_applies, 0)
end)

test("labelled expression coefficient edits submit the actual serialized field and preserve failed drafts", function()
    local c = expression_fixture(); c:draw()
    c.inputs[expression_control(c, "input", c.expression_path).label] = "3"
    c.buttons[expression_control(c, "button", c.expression_path).label] = true; c:draw()
    local request = c.files["MotionVfxLive/request.json"]
    equal(request.action, "set"); equal(request.revision, 9); equal(request.target.index, 1); equal(request.target.attributeIndex, 4)
    equal(request.path, c.expression_path); equal(request.value, "3")
    c:respond({ok = false, error = "rejected"}); equal(c.module.state.values[c.expression_path], "3")
    local before = c.files["MotionVfxLive/request.json"]
    equal(c.module.inspect({kind = "entry", index = 2}), false); equal(c.module.publish_document(false), false)
    equal(c.files["MotionVfxLive/request.json"], before)
end)

test("older backend inspection clears expression plots instead of showing a stale curve", function()
    local c = expression_fixture(); equal(#c.module.state.expression_curves, 1)
    c.module.inspect({kind = "entry", index = 2}); c:respond({ok = true, fields = {}})
    equal(#c.module.state.expression_curves, 0); c:draw(); equal(c.module.state.ui_error, nil)
end)

test("expression coefficients remain usable when draw list is unavailable", function()
    local c = expression_fixture(); c.env.imgui.get_window_draw_list = false; c:draw()
    equal(c.module.state.ui_error, nil)
    assert(expression_control(c, "button", c.expression_path).label:find("spawnNum", 1, true), "Submission caption identifies its channel")
end)

test("same-labelled expression coefficients submit independently and retain the other channel draft", function()
    local c = paired_expression_fixture(); c:draw()
    local first, second = c.expression_paths[1], c.expression_paths[2]
    local first_input, second_input = expression_control(c, "input", first), expression_control(c, "input", second)
    local first_button, second_button = expression_control(c, "button", first), expression_control(c, "button", second)
    assert(first_input.id ~= second_input.id, "Channels must have distinct input IDs")
    assert(first_button.id ~= second_button.id, "Channels must have distinct submission IDs")
    assert(first_button.label:find("spawnNum", 1, true), "First submission identifies spawnNum")
    assert(second_button.label:find("spawnNumRange", 1, true), "Second submission identifies spawnNumRange")
    equal(first_input.value, "1"); equal(second_input.value, "10")
    c.inputs[first_input.label], c.inputs[second_input.label] = "5", "1"
    c.buttons[second_button.label] = true; c:draw()
    local request = c.files["MotionVfxLive/request.json"]
    equal(request.action, "set"); equal(request.target.kind, "entry"); equal(request.target.index, 4)
    equal(request.target.attributeIndex, 3); equal(request.path, second); equal(request.value, "1"); equal(request.revision, 9)
    c.module.state.document.revision = 10
    c:respond({ ok = true, revision = 10, document = c.module.state.document })
    equal(c.module.state.pending.action, "inspect")
    c:respond(paired_expression_response(c, "1", "1"))
    equal(c.module.state.values[first], "5", "Other channel draft survives the successful submission")
    equal(c.module.state.values[second], "1"); equal(c.module.state.fields[1].value, "1")
    c:draw(); c.buttons[expression_control(c, "button", first).label] = true; c:draw()
    request = c.files["MotionVfxLive/request.json"]
    equal(request.action, "set"); equal(request.path, first); equal(request.value, "5"); equal(request.revision, 10)
    equal(request.target.index, 4); equal(request.target.attributeIndex, 3)
    c.module.state.document.revision = 11
    c:respond({ ok = true, revision = 11, document = c.module.state.document })
    c:respond(paired_expression_response(c, "5", "1"))
    equal(c.module.state.fields[1].value, "5"); equal(c.module.state.fields[2].value, "1")
    c:frame(); c:frame()
    local diagnostics = c.files["MotionVfxLive/ui-status.json"]
    equal(diagnostics.selectedTarget.kind, "entry"); equal(diagnostics.selectedTarget.index, 4)
    equal(diagnostics.selectedTarget.attributeIndex, 3)
    assert(diagnostics.selectedTitle:find("1_GPUP", 1, true), "Diagnostics identify the selected entry by name")
    assert(diagnostics.selectedTitle:find("SpawnExpression", 1, true), "Diagnostics identify the selected attribute")
    equal(diagnostics.lastSubmittedField.source, "samples/a/11_it00_900.efx.5571972")
    equal(diagnostics.lastSubmittedField.target.index, 4); equal(diagnostics.lastSubmittedField.target.attributeIndex, 3)
    equal(diagnostics.lastSubmittedField.path, first); equal(diagnostics.lastSubmittedField.value, "5")
    equal(diagnostics.lastSubmittedField.revision, 11)
    equal(#c.binding_applies, 0); equal(#c.scene_writes, 0)
end)

test("expression expansion and control identities are scoped to source entry and attribute", function()
    local c = paired_expression_fixture()
    local function controls()
        c.tree_history, c.drawn_inputs, c.drawn_buttons, c.child_history = {}, {}, {}, {}
        c:draw(); equal(c.module.state.ui_error, nil)
        local result = { inputs = {}, buttons = {}, trees = {}, plots = {} }
        for _, item in ipairs(c.tree_history) do
            if item.id:find("##mvl_expression_", 1, true) == 1 then result.trees[#result.trees + 1] = item end
        end
        for _, item in ipairs(c.child_history) do
            if control_id(item.name):find("##mvl_expression_plot_", 1, true) == 1 then result.plots[#result.plots + 1] = control_id(item.name) end
        end
        for i, path in ipairs(c.expression_paths) do
            result.inputs[i] = expression_control(c, "input", path).id
            result.buttons[i] = expression_control(c, "button", path).id
        end
        equal(#result.trees, 3, "Outer expression section and both channels are drawn")
        return result
    end
    -- The real API is present for this fixture, so chart identities are covered too.
    c.env.imgui.get_cursor_screen_pos = function() return { x = 10, y = 20 } end
    c.env.imgui.get_window_draw_list = function() return { add_line = function() end } end
    local baseline = controls(); equal(#baseline.plots, 2)
    local function changed(previous, current, label)
        for i = 1, 2 do
            assert(previous.inputs[i] ~= current.inputs[i], label .. " changes input ID")
            assert(previous.buttons[i] ~= current.buttons[i], label .. " changes submission ID")
            assert(previous.plots[i] ~= current.plots[i], label .. " changes chart ID")
        end
        for i = 1, 3 do assert(previous.trees[i].id ~= current.trees[i].id, label .. " changes section expansion ID") end
    end
    -- A collapsed channel in one entry must not collapse the same numbered
    -- expression in a different entry. The old implementation reused this ID.
    c.closed_tree = baseline.trees[2].label
    c.module.inspect({ kind = "entry", index = 1, attributeIndex = 4 })
    c:respond(paired_expression_response(c, "1", "1"))
    local entry = controls(); changed(baseline, entry, "Entry selection")
    c.closed_tree = nil
    c.module.inspect({ kind = "entry", index = 4, attributeIndex = 4 })
    c:respond(paired_expression_response(c, "1", "10"))
    local attribute = controls(); changed(baseline, attribute, "Attribute selection")
    c.module.state.document.source = "samples/b/11_it00_900.efx.5571972"
    c.module.inspect({ kind = "entry", index = 4, attributeIndex = 4 })
    c:respond(paired_expression_response(c, "1", "10"))
    changed(attribute, controls(), "Different source with the same filename")
end)

local function curve_fixture()
    local c = environment()
    c.module.state.document = { source = "curve.efx.5571972", revision = 7, entries = {}, actions = {} }
    local curves = { { path = "clipData", editable = true, channels = { { index = 0, valueType = "Float", keys = {
        { index = 0, time = "0", value = "1", interpolation = "Type2" },
        { index = 1, time = "10", value = "2", interpolation = "Bezier", outX = "1", outY = "2", inX = "3", inY = "4" }
    } } } } }
    c.module.inspect({ kind = "entry", index = 2, attributeIndex = 3 })
    c:respond({ ok = true, document = c.module.state.document, fields = { { path = "Flags", value = "1", editable = true } }, curves = curves })
    c.curves = curves
    return c
end

test("curve inspection and UI rendering do not mutate resources", function()
    local c = curve_fixture(); local previous = c.files["MotionVfxLive/request.json"]
    c:draw(); equal(c.files["MotionVfxLive/request.json"], previous)
    equal(c.module.state.curve_value, "1"); equal(c.module.state.curve_time, "0")
    equal(#c.scene_writes, 0); equal(#c.binding_applies, 0); equal(#c.runtime_queue, 0)
end)

test("curve key update carries exact target revision time value and tangent fields", function()
    local c = curve_fixture(); equal(c.module.select_curve(1, 1, 2), true)
    c.module.state.curve_time = "12"; c.module.state.curve_out_y = "9"
    equal(c.module.curve_edit("update"), true)
    local request = c.files["MotionVfxLive/request.json"]
    equal(request.action, "curve_key"); equal(request.revision, 7); equal(request.path, "clipData")
    equal(request.target.index, 2); equal(request.target.attributeIndex, 3); equal(request.channel, 0); equal(request.keyIndex, 1)
    equal(request.time, "12"); equal(request.interpolation, "Bezier"); equal(request.outY, "9"); equal(request.inY, "4")
    c:respond({ ok = true, document = { source = "curve.efx.5571972", revision = 8 } })
    equal(c.module.state.pending.action, "inspect")
    c:respond({ ok = true, fields = {}, curves = c.curves })
    equal(c.module.state.curve_time, "0"); equal(c.module.state.curve_selection.key, 1)
end)

test("failed curve mutation retains the typed draft and original selection", function()
    local c = curve_fixture(); c.module.state.curve_value = "invalid"
    equal(c.module.curve_edit("add"), true)
    c:respond({ ok = false, error = "not a number", document = c.module.state.document })
    equal(c.module.state.curve_value, "invalid"); equal(c.module.state.curve_selection.key, 1)
    equal(c.module.state.pending, nil); equal(c.module.state.document.revision, 7)
end)

test("curve drafts block selection publication save and accidental scalar submission", function()
    local c = curve_fixture(); local previous = c.files["MotionVfxLive/request.json"]
    c.module.state.curve_value = "8"
    equal(c.module.select_curve(1, 1, 2), false)
    equal(c.module.inspect({ kind = "entry", index = 3 }), false)
    equal(c.module.publish_document(false), false)
    c.buttons["导出文件##mvl_save"] = true; c:draw()
    c.buttons["提交##mvl_set_Flags"] = true; c:draw()
    equal(c.files["MotionVfxLive/request.json"], previous); equal(c.module.state.curve_value, "8")
    c.buttons["还原本行输入##mvl_curve_revert"] = true; c:draw()
    equal(c.module.state.curve_value, "1"); equal(c.module.select_curve(1, 1, 2), true)
end)

test("curve edit cannot discard scalar drafts and deletion requires confirmation", function()
    local c = curve_fixture(); local previous = c.files["MotionVfxLive/request.json"]
    c.module.state.values.Flags = "2"; equal(c.module.curve_edit("add"), false)
    c.module.state.values.Flags = "1"; equal(c.module.curve_edit("remove"), false)
    equal(c.files["MotionVfxLive/request.json"], previous)
    c.module.state.curve_confirm_delete = true; c.module.state.curve_value = "3"
    equal(c.module.curve_edit("remove"), false)
    c.module.state.curve_value = "1"; equal(c.module.curve_edit("remove"), true)
    equal(c.files["MotionVfxLive/request.json"].operation, "remove")
end)

test("an old backend without curve data clears stale curve forms", function()
    local c = curve_fixture(); c.module.inspect({ kind = "entry", index = 2, attributeIndex = 4 })
    c:respond({ ok = true, fields = {} })
    equal(#c.module.state.curves, 0); equal(c.module.state.curve_baseline, nil)
    equal(c.module.curve_edit("update"), false); c:draw()
end)

test("curve list pages keep global key indices and typed inputs", function()
    local c = curve_fixture(); local keys = c.curves[1].channels[1].keys
    for i = 3, 85 do keys[i] = { index = i - 1, time = tostring(i), value = "1", interpolation = "Type2" } end
    c.module.state.curve_key_page = 2
    c.buttons["#40   时间 41   数值 1   Type2##mvl_curve_key_41"] = true; c:draw()
    equal(c.module.state.curve_selection.key, 41); equal(c.module.state.curve_key_page, 2)
    c.module.state.curve_value = "4"; equal(c.module.curve_edit("update"), true)
    equal(c.files["MotionVfxLive/request.json"].keyIndex, 40)
end)

test("load, UI and frame produce no edit or game mutation", function()
    local c = environment(); c:draw(); c:frame()
    equal(c.files["MotionVfxLive/request.json"], nil)
    equal(#c.runtime_queue, 0); equal(#c.scene_writes, 0)
    equal(c.files["MotionVfxLive/ui-status.json"].runtimeLoaded, true)
end)
test("offline backend rejects request without writing mailbox", function()
    local c = environment(); c.files["MotionVfxLive/status.json"] = nil; c:frame()
    equal(c.module.send("open", { path = "sample.efx" }), false)
    equal(c.files["MotionVfxLive/request.json"], nil)
end)
test("mailbox has unique IDs, revision and a single pending request", function()
    local c = environment(); c.module.state.document = { revision = 8 }
    equal(c.module.send("open", { path = "sample.efx" }), true)
    local first = c.files["MotionVfxLive/request.json"]
    equal(first.revision, 8); equal(first.path, "sample.efx")
    equal(c.module.send("undo"), false); equal(c.files["MotionVfxLive/request.json"].id, first.id)
    c:respond({ ok = true, document = { revision = 9 } })
    c.module.send("catalog"); equal(c.files["MotionVfxLive/request.json"].id ~= first.id, true)
end)
test("stale response is ignored and cannot overwrite current document", function()
    local c = environment(); c.module.state.document = { revision = 6 }; c.module.send("catalog")
    c:respond({ id = "stale", ok = true, document = { revision = 900 } })
    equal(c.module.state.document.revision, 6); equal(c.module.state.pending ~= nil, true)
end)
test("timeout preserves uncertain request until explicit status recovery", function()
    local c = environment(); c.module.send("set", { path = "x", value = "1" })
    local previous = c.module.state.pending.id
    c.time = c.time + 20; c:frame()
    equal(c.module.state.pending.timeout, true); equal(c.module.state.pending.id, previous)
    equal(c.module.synchronize(), true)
    equal(c.files["MotionVfxLive/request.json"].action, "status")
    c:respond({ id = previous, ok = true, document = { revision = 99 } })
    equal(c.module.state.document, nil)
    c:respond({ ok = true, document = { revision = 3 } })
    equal(c.module.state.document.revision, 3); equal(c.module.state.selected, nil)
end)
test("request write failures do not leave a fake pending command", function()
    local c = environment(); c.write_failure = "MotionVfxLive/request.json"
    equal(c.module.send("undo"), false); equal(c.module.state.pending, nil)
    c.write_failure = nil; c.write_false = "MotionVfxLive/request.json"
    equal(c.module.send("undo"), false); equal(c.module.state.pending, nil)
end)
test("inspect preserves zero based attribute target and exact string values", function()
    local c = environment(); c.module.inspect({ kind = "entry", index = 0, attributeIndex = 0 })
    equal(c.files["MotionVfxLive/request.json"].target.attributeIndex, 0)
    c:respond({ ok = true, fields = { { path = "Flags", label = "Flags", value = "18446744073709551615", type = "UInt64", editable = true } }, document = { revision = 1 } })
    equal(c.module.state.selected.index, 0); equal(c.module.state.values.Flags, "18446744073709551615")
    c.inputs["##mvl_value_Flags"] = "18446744073709551614"; c.buttons["提交##mvl_set_Flags"] = true; c:draw()
    local request = c.files["MotionVfxLive/request.json"]
    equal(request.action, "set"); equal(request.value, "18446744073709551614"); equal(type(request.value), "string")
    c:respond({ ok = true, document = { revision = 2 } })
    equal(c.files["MotionVfxLive/request.json"].action, "inspect")
    equal(c.files["MotionVfxLive/request.json"].revision, 2)
end)
test("read-only field cannot produce set request", function()
    local c = environment(); c.module.state.document = { revision = 1 }
    c.module.state.selected = { kind = "entry", index = 0 }
    c.module.state.fields = { { path = "Opaque", value = "x", type = "Blob", editable = false, reason = "unsupported" } }
    c.buttons["提交##mvl_set_Opaque"] = true; c:draw()
    equal(c.files["MotionVfxLive/request.json"], nil)
end)
test("submitting one field retains another unsubmitted draft", function()
    local c = environment(); c.module.state.document = { revision = 1 }
    c.module.state.selected = { kind = "entry", index = 0 }
    c.module.state.fields = {
        { path = "X", value = "1", type = "Single", editable = true },
        { path = "Y", value = "2", type = "Single", editable = true },
    }
    c.module.state.values = { X = "3", Y = "4" }
    c.buttons["提交##mvl_set_X"] = true; c:draw()
    c:respond({ ok = true, document = { revision = 2 } })
    c:respond({ ok = true, fields = {
        { path = "X", value = "3.0", type = "Single", editable = true },
        { path = "Y", value = "2", type = "Single", editable = true },
    } })
    equal(c.module.state.values.X, "3.0"); equal(c.module.state.values.Y, "4")
end)
test("structural edit invalidates old selection after success", function()
    local c = environment(); c.module.state.document = { revision = 1 }
    c.module.state.selected = { kind = "entry", index = 0 }
    c.module.ui_mutate("duplicate_entry", {index=0})
    equal(c.files["MotionVfxLive/request.json"].index, 0)
    c:respond({ ok = true, document = { revision = 2 } })
    equal(c.module.state.selected, nil); equal(#c.module.state.fields, 0)
end)
test("auxiliary publish requires separate explicit playback", function()
    local c = environment(); c.module.state.document = { revision = 1 }
    c.buttons["生成当前文档的辅助预览资源（不自动播放）##mvl_publish"] = true; c:draw("preview")
    equal(c.files["MotionVfxLive/request.json"].action, "publish"); equal(#c.runtime_queue, 0)
    c:respond({ id = "stale", ok = true, resourcePath = "wrong.efx" }); equal(#c.runtime_queue, 0)
    c:respond({ ok = true, resourcePath = "motionvfxlive/session/revision2.efx", revision = 2 })
    equal(#c.runtime_queue, 0); equal(#c.binding_applies, 0); equal(c.instance, nil)
    c.buttons["播放 / 从头重播##mvl_play"] = true; c:draw("preview"); c:lock()
    equal(c.instance.path, "motionvfxlive/session/revision2.efx")
    equal(c.last_play_options.resolve_parent(), "player-transform")
end)
test("failed publish does not restart an existing preview", function()
    local c = environment(); c.module.state.document = { revision = 1 }
    c.module.play("old.efx"); c:lock()
    c.buttons["生成当前文档的辅助预览资源（不自动播放）##mvl_publish"] = true; c:draw("preview")
    c:respond({ ok = false, error = "cannot serialize" }); equal(#c.runtime_queue, 0); equal(c.instance.path, "old.efx")
end)
test("preview speed changes queue, target owned instance, and verify readback", function()
    local c = environment(); c.module.play("effect.efx"); c:lock()
    c.module.queue_effect_speed("0.25"); equal(c.preview_speed, 1)
    c:lock(); equal(c.preview_speed, 0.25); equal(c.module.state.effect_actual.speed, 0.25)
    c.ignore_speed_write = true; c.module.queue_effect_speed("0.5"); c:lock()
    equal(c.module.state.preview_message:find("失败", 1, true) ~= nil, true)
    equal(c.module.queue_effect_speed("nan"), false); equal(c.module.queue_effect_speed("-1"), false)
end)
test("preview pause resume and experimental step dispatch real actions", function()
    local c = environment(); c.module.play("effect.efx"); c:lock()
    c.buttons["暂停特效##mvl_pause_effect"] = true; c:draw("preview"); equal(c.preview_paused, false); c:lock()
    equal(c.preview_paused, true); equal(c.module.state.effect_actual.paused, true)
    c.buttons["继续特效##mvl_resume_effect"] = true; c:draw("preview"); c:lock(); equal(c.preview_paused, false)
    c.buttons["特效单步（实验）##mvl_step_effect"] = true; c:draw("preview"); c:lock(); equal(c.last_action, "Step")
end)
test("scene pause stops actual simulation through zero timescale even without LockScene", function()
    local c = environment(); c.ignore_pause_write = true;c.module.queue_scene("pause", true); equal(c.speed, 0.8)
    c:frame(); equal(c.speed, 0); equal(c.module.state.scene_actual.paused, true)
    equal(c.paused, false);equal(c.module.state.scene_actual.native_paused, false)
    local elapsed=c.simulated_time;c:frame(4);equal(c.simulated_time,elapsed)
    c.module.queue_scene("pause", false); c:frame(); equal(c.speed, 0.8);equal(c.paused, false)
    equal(c.module.state.scene_original.paused, false)
    equal(c.module.state.scene_pause,nil);equal(c.simulated_time>elapsed,true)
end)
test("pause freezes the application-clock fixture and restores both clocks",function()
    local c=environment(); local scene_before,trail_before=c.simulated_time,c.trail_time
    c.speed=0; c:frame()
    equal(c.simulated_time,scene_before); equal(c.trail_time>trail_before,true)
    c.speed=0.8; c.global_speed=0.65
    c.module.queue_scene("speed",0.1); c:frame()
    c.module.queue_scene("pause",true); c:frame()
    equal(c.speed,0); equal(c.global_speed,0); equal(c.module.state.global_time_original.speed,0.65)
    local trail=c.trail_time; c:frame(5); equal(c.trail_time,trail)
    c.module.queue_scene("pause",true); c.module.queue_scene("speed",0.25); c:frame()
    equal(c.global_speed,0); equal(c.module.state.global_time_original.speed,0.65)
    c.module.queue_scene("pause",false); c:frame()
    equal(c.speed,0.25); equal(c.global_speed,0.65); equal(c.trail_time>trail,true)
    equal(c.module.state.global_time_original,nil)
    c.module.stop_and_restore(); c:frame(); equal(c.speed,0.8); equal(c.global_speed,0.65)
end)
test("effect update gate freezes the per-frame fixture that ignores both time scales",function()
    local c=environment()
    local start_frame,start_scene,start_global=c.frame_trail,c.simulated_time,c.trail_time
    c.speed=0;c.global_speed=0;c:frame(3)
    equal(c.frame_trail,start_frame+3);equal(c.simulated_time,start_scene);equal(c.trail_time,start_global)
    c.speed=0.8;c.global_speed=0.65;c.module.queue_scene("speed",0.1);c:frame()
    c.module.queue_scene("pause",true);c:frame()
    local trail=c.frame_trail
    equal(c.effect_update,false);equal(c.module.state.effect_update_original.enabled,true)
    c:frame(10);equal(c.frame_trail,trail)
    equal(#c.effect_update_writes,1,"no steady-state repeated setter")
    c.module.queue_scene("speed",0.25);c:frame();equal(c.frame_trail,trail)
    c.module.queue_scene("pause",false);c:frame()
    equal(c.effect_update,true);equal(c.speed,0.25);equal(c.global_speed,0.65)
    equal(c.frame_trail,trail+1);equal(c.module.state.effect_update_original,nil)
    c.module.stop_and_restore();c:frame();equal(c.speed,0.8)
end)
test("effect update drift is corrected and repeated pause preserves first baseline",function()
    local c=environment();c.module.queue_scene("pause",true);c:frame()
    c.effect_update=true;c:frame();equal(c.effect_update,false)
    equal(#c.effect_update_writes,2)
    c.module.queue_scene("pause",true);c:frame()
    equal(c.module.state.effect_update_original.enabled,true)
    c.module.queue_scene("pause",false);c:frame();equal(c.effect_update,true)
end)
test("preexisting disabled effects are never force-enabled on resume or restore",function()
    local c=environment();c.effect_update=false
    c.module.queue_scene("pause",true);c:frame()
    c.module.queue_scene("pause",false);c:frame()
    equal(c.effect_update,false);equal(#c.effect_update_writes,0)
    c.module.stop_and_restore();c:frame();equal(c.effect_update,false)
end)
test("effect restore failure retains backup and still restores independent global clock",function()
    local c=environment();c.module.queue_scene("pause",true);c:frame()
    c.ignore_effect_update_write=true
    equal(c.module.reset(),false);equal(c.module.active,true)
    equal(c.effect_update,false);equal(c.global_speed,1)
    equal(c.module.state.effect_update_original.enabled,true)
    local writes=#c.effect_update_writes;c:frame(3);equal(#c.effect_update_writes,writes)
    c.ignore_effect_update_write=false
    equal(c.module.reset(),true);equal(c.effect_update,true);equal(c.speed,0.8)
end)
test("global restore failure does not strand effect update gate",function()
    local c=environment();c.module.queue_scene("pause",true);c:frame()
    c.ignore_global_write=true;c.module.queue_scene("pause",false);c:frame()
    equal(c.effect_update,true);equal(c.module.state.effect_update_original,nil)
    equal(c.global_speed,0);equal(c.module.state.global_time_original.speed,1)
    c.ignore_global_write=false;c.module.stop_and_restore();c:frame()
    equal(c.global_speed,1);equal(c.speed,0.8)
end)
test("partial effect setter failure keeps original and stop can recover",function()
    local c=environment();c.fail_effect_update_after_write=true
    c.module.queue_scene("pause",true);c:frame()
    equal(c.effect_update,false);equal(c.module.state.effect_update_original.enabled,true)
    equal(c.module.state.scene_pause.suspended,true)
    c.fail_effect_update_after_write=false;c.module.stop_and_restore();c:frame()
    equal(c.effect_update,true);equal(c.global_speed,1);equal(c.speed,0.8)
end)
test("scene replacement releases effect gate without writing new scene",function()
    local c=environment();c.module.queue_scene("pause",true);c:frame()
    c.scene_address=100;c.speed=0.7;local writes=#c.scene_writes;c:frame()
    equal(c.effect_update,true);equal(c.global_speed,1);equal(c.speed,0.7)
    equal(#c.scene_writes,writes);equal(c.module.state.effect_update_original,nil)
end)
test("missing effect gate and invalid readback report incomplete pause with recoverable clocks",function()
    for _,flag in ipairs({"missing_effect_update_methods","no_effect_manager","bad_effect_update_readback"}) do
        local c=environment();c[flag]=true;c.module.queue_scene("pause",true);c:frame()
        equal(#c.effect_update_writes,0);equal(c.module.state.effect_update_original,nil)
        equal(c.module.state.scene_pause.suspended,true)
        equal(type(c.module.state.scene_control_error),"string")
        c.module.stop_and_restore();c:frame();equal(c.global_speed,1);equal(c.speed,0.8)
    end
end)
test("replaced effect manager is never overwritten by old restore data",function()
    local c=environment();c.module.queue_scene("pause",true);c:frame()
    c.effect_manager={};c.effect_update=true;local writes=#c.effect_update_writes
    c.module.queue_scene("pause",false);c:frame()
    equal(c.effect_update,true);equal(#c.effect_update_writes,writes)
    equal(c.global_speed,1);equal(c.module.state.effect_update_original.enabled,true)
    equal(c.module.active,true);equal(c.module.reset(),false)
end)
test("global pause drift is corrected without losing original value or steady-state writes",function()
    local c=environment(); c.module.queue_scene("pause",true); c:frame()
    local writes=#c.global_writes;c:frame(3);equal(#c.global_writes,writes)
    c.global_speed=0.5;c:frame();equal(c.global_speed,0);equal(#c.global_writes,writes+1)
    equal(c.module.state.global_time_original.speed,1)
    c.module.queue_scene("pause",false);c:frame();equal(c.global_speed,1)
end)
test("scene replacement releases global freeze without writing the new scene",function()
    local c=environment();c.module.queue_scene("pause",true);c:frame()
    c.scene_address=100;c.speed=0.7;local writes=#c.scene_writes;c:frame()
    equal(c.speed,0.7);equal(#c.scene_writes,writes);equal(c.global_speed,1)
    equal(c.module.state.scene_pause.suspended,true);equal(c.module.state.global_time_original,nil)
    equal(c.module.restore_scene(),false);equal(c.global_speed,1)
end)
test("global restore failure blocks reset and keeps a retryable backup",function()
    local c=environment();c.module.queue_scene("pause",true);c:frame()
    c.ignore_global_write=true;equal(c.module.reset(),false)
    equal(c.module.active,true);equal(c.global_speed,0);equal(c.module.state.global_time_original.speed,1)
    local writes=#c.global_writes;c:frame(3);equal(#c.global_writes,writes)
    c.ignore_global_write=false;equal(c.module.reset(),true);equal(c.global_speed,1)
    equal(c.module.state.global_time_original,nil)
end)
test("partial global setter failure retains recovery data until restoration succeeds",function()
    local c=environment();c.fail_global_after_write=true
    c.module.queue_scene("pause",true);c:frame()
    equal(c.global_speed,0);equal(c.module.state.global_time_original.speed,1)
    equal(c.module.state.scene_pause.suspended,true)
    c.fail_global_after_write=false;c.module.stop_and_restore();c:frame()
    equal(c.global_speed,1);equal(c.speed,0.8);equal(c.module.state.global_time_original,nil)
end)
test("preexisting global freeze is restored exactly instead of inventing playback",function()
    local c=environment();c.global_speed=0
    c.module.queue_scene("pause",true);c:frame()
    c.module.queue_scene("pause",false);c:frame()
    equal(c.global_speed,0);equal(c.speed,0.8);equal(c.module.state.global_time_original,nil)
end)
test("missing global pause API reports failure and still allows scene restoration",function()
    local c=environment();c.missing_global_methods=true
    c.module.queue_scene("pause",true);c:frame()
    equal(#c.global_writes,0);equal(c.module.state.global_time_original,nil)
    equal(c.module.state.scene_pause.suspended,true)
    equal(type(c.module.state.scene_control_error),"string")
    c.module.stop_and_restore();c:frame();equal(c.speed,0.8);equal(c.global_speed,1)
end)
test("global application identity change cannot overwrite a replacement singleton",function()
    local c=environment();c.module.queue_scene("pause",true);c:frame()
    c.application={};c.global_speed=0.6;local writes=#c.global_writes
    c.module.queue_scene("pause",false);c:frame()
    equal(c.global_speed,0.6);equal(#c.global_writes,writes)
    equal(c.module.state.global_time_original.speed,1);equal(c.module.active,true)
end)
test("scene restores exact initial speed and pause state", function()
    local c = environment(); c.paused = true
    c.module.queue_scene("speed", 0.1); c.module.queue_scene("pause", false); c:frame()
    equal(c.speed, 0.1); equal(c.paused, false)
    c.module.queue_scene("restore"); c:frame()
    equal(c.speed, 0.8); equal(c.paused, true); equal(c.module.state.scene_original, nil)
end)
test("unsupported scene API and unreadable pause never mutate game", function()
    local c = environment(); c.missing_scene_methods = true
    c.module.queue_scene("pause", true); c:frame(); equal(#c.scene_writes, 0)
    c.missing_scene_methods = false; c.bad_pause_readback = true
    c.module.queue_scene("speed", 0.1); c:frame(); equal(#c.scene_writes, 0)
end)
test("write ignored by engine is reported as failure", function()
    local c = environment(); c.ignore_speed_write = true
    c.module.queue_scene("pause", true); c:frame()
    equal(c.speed,0.8);equal(c.module.state.scene_actual.paused,false)
    equal(c.module.state.scene_message:find("失败", 1, true) ~= nil, true)
end)
test("restore refuses a different scene", function()
    local c = environment(); c.module.queue_scene("speed", 0.1); c:frame()
    c.scene_address = 80; c.speed = 0.9; local writes = #c.scene_writes
    equal(c.module.restore_scene(), false); equal(#c.scene_writes, writes); equal(c.speed, 0.9)
end)
test("emergency restore cancels pending local speed operations and stops only preview", function()
    local c = environment(); c.module.play("effect.efx"); c:lock()
    c.module.queue_scene("pause", true); c:frame(); c.module.queue_effect_speed("0.25")
    c.module.stop_and_restore(); c:frame(); c:lock()
    equal(c.speed,0.8);equal(c.paused, false); equal(c.instance, nil); equal(#c.module.state.effect_queue, 0)
end)
test("reset restores scene synchronously and deactivates callbacks", function()
    local c = environment(); c.module.queue_scene("speed", 0.1); c:frame()
    c.module.reset(); equal(c.speed, 0.8); equal(c.module.active, false); equal(c.env.MotionVfxLive, nil)
    c.module.queue_scene("pause", true); c:frame(); equal(c.paused, false);equal(c.speed,0.8)
end)

test("pause remembers the selected slow speed and changing speed while paused keeps simulation frozen",function()
    local c=environment();c.module.queue_scene("speed",0.1);c:frame()
    c.module.queue_scene("pause",true);c:frame();equal(c.speed,0);equal(c.module.state.scene_pause.resume_speed,0.1)
    local elapsed=c.simulated_time;c.module.queue_scene("speed",0.25);c:frame()
    equal(c.speed,0);equal(c.module.state.scene_pause.resume_speed,0.25);equal(c.simulated_time,elapsed)
    c.module.queue_scene("pause",false);c:frame();equal(c.speed,0.25);equal(c.module.state.scene_pause,nil)
    c.module.queue_scene("restore");c:frame();equal(c.speed,0.8);equal(c.module.state.scene_original,nil)
end)

test("repeated pause preserves the resume speed and repeated resume does not overwrite later changes",function()
    local c=environment();c.module.queue_scene("speed",0.1);c.module.queue_scene("pause",true);c:frame()
    c.module.queue_scene("pause",true);c:frame();equal(c.speed,0);equal(c.module.state.scene_pause.resume_speed,0.1)
    c.module.queue_scene("pause",false);c:frame();equal(c.speed,0.1)
    c.speed=0.6;local writes=#c.scene_writes;c.module.queue_scene("pause",false);c:frame()
    equal(c.speed,0.6);equal(#c.scene_writes,writes)
end)

test("native pause flag alone never reports actual scene freezing",function()
    local c=environment();c.paused=true;c.module.queue_scene("read");c:frame()
    equal(c.module.state.scene_actual.native_paused,true);equal(c.module.state.scene_actual.paused,false)
    equal(c.module.state.scene_actual.speed,0.8);equal(c.simulated_time>0,true)
    equal(#c.scene_writes,0)
end)

test("nonboolean pause commands never silently resume or write scene state",function()
    for _,case in ipairs({{}, {value=0}, {value="false"}, {value={}}}) do
        local c=environment();c.module.queue_scene("pause",true);c:frame()
        local writes=#c.scene_writes;c.module.queue_scene("pause",case.value);c:frame()
        equal(#c.scene_writes,writes);equal(c.speed,0)
        equal(c.module.state.scene_pause.resume_speed,0.8);equal(c.module.state.scene_pause.suspended,true)
        equal(type(c.module.state.scene_control_error),"string")
        c.module.queue_scene("pause",false);c:frame();equal(c.speed,0.8);equal(c.module.state.scene_pause,nil)
    end
end)

test("pausing an initially zero-speed scene never invents a resume speed",function()
    local c=environment();c.speed=0;c.paused=true
    c.module.queue_scene("pause",true);c:frame();equal(c.module.state.scene_pause.resume_speed,0)
    c.module.queue_scene("pause",false);c:frame();equal(c.speed,0);equal(c.paused,false)
    c.module.queue_scene("restore");c:frame();equal(c.speed,0);equal(c.paused,true)
    equal(c.module.state.scene_original,nil);equal(c.module.state.scene_pause,nil)
end)

test("owned pause and resume restore an originally true native pause flag exactly",function()
    local c=environment();c.paused=true
    c.module.queue_scene("pause",true);c:frame();equal(c.speed,0)
    c.module.queue_scene("pause",false);c:frame();equal(c.speed,0.8);equal(c.paused,false)
    c.module.queue_scene("restore");c:frame();equal(c.speed,0.8);equal(c.paused,true)
    equal(c.module.state.scene_actual.paused,false);equal(c.module.state.scene_actual.native_paused,true)
    equal(c.module.state.scene_original,nil);equal(c.module.state.scene_pause,nil)
end)

test("native resume setter failure preserves both baselines and never restarts automatic zero writes",function()
    local c=environment();c.paused=true;c.module.queue_scene("pause",true);c:frame()
    c.fail_pause_write=true;c.module.queue_scene("pause",false);c:frame()
    equal(c.speed,0.8);equal(c.paused,true);equal(c.module.state.scene_pause.suspended,true)
    equal(c.module.state.scene_original.speed,0.8);equal(c.module.state.scene_original.paused,true)
    local writes=#c.scene_writes;c:frame(3);equal(#c.scene_writes,writes);equal(c.speed,0.8)
    c.fail_pause_write=false;c.module.queue_scene("pause",false);c:frame()
    equal(c.speed,0.8);equal(c.paused,false);equal(c.module.state.scene_pause,nil)
    c.module.queue_scene("restore");c:frame();equal(c.speed,0.8);equal(c.paused,true)
end)

test("owned pause corrects game timescale drift without rewriting a steady paused scene",function()
    local c=environment();c.module.queue_scene("pause",true);c:frame()
    local elapsed=c.simulated_time;c.speed=0.6;local writes=#c.scene_writes;c:frame()
    equal(c.speed,0);equal(#c.scene_writes,writes+1);equal(c.simulated_time,elapsed)
    equal(c.scene_writes[#c.scene_writes].value,0);equal(c.scene_writes[#c.scene_writes].address,12)
    writes=#c.scene_writes;c:frame(5);equal(#c.scene_writes,writes);equal(c.simulated_time,elapsed)
end)

test("failed pause maintenance suspends automatic writes until an explicit retry",function()
    local c=environment();c.module.queue_scene("pause",true);c:frame()
    c.speed=0.6;c.fail_speed_write=true;c:frame()
    equal(c.module.state.scene_pause.suspended,true);equal(type(c.module.state.scene_control_error),"string")
    local writes=#c.scene_writes;c:frame(4);equal(#c.scene_writes,writes);equal(c.speed,0.6)
    c.fail_speed_write=false;c.module.queue_scene("pause",true);c:frame()
    equal(c.speed,0);equal(c.module.state.scene_pause.suspended,false);equal(c.module.state.scene_pause.resume_speed,0.8)
    c.module.queue_scene("pause",false);c:frame();equal(c.speed,0.8)
end)

test("ignored first pause write preserves baseline and avoids a per-frame retry loop",function()
    local c=environment();c.ignore_speed_write=true;c.module.queue_scene("pause",true);c:frame()
    equal(c.speed,0.8);equal(c.module.state.scene_pause.suspended,true)
    equal(c.module.state.scene_original.speed,0.8);local writes=#c.scene_writes;c:frame(4);equal(#c.scene_writes,writes)
    c.ignore_speed_write=false;c.module.queue_scene("pause",true);c:frame();equal(c.speed,0)
    equal(c.module.state.scene_pause.resume_speed,0.8)
    c.module.stop_and_restore();c:frame();equal(c.speed,0.8);equal(c.module.state.scene_original,nil)
end)

test("invalid speed while paused never changes the stored resume speed or writes to the scene",function()
    local c=environment();c.module.queue_scene("pause",true);c.module.queue_scene("speed",0.25);c:frame()
    local writes=#c.scene_writes;c.module.queue_scene("speed",0);c:frame()
    equal(c.speed,0);equal(c.module.state.scene_pause.resume_speed,0.25);equal(#c.scene_writes,writes)
    c.module.queue_scene("pause",false);c:frame();equal(c.speed,0.25)
end)

test("scene identity changes suspend control while preserving the original restoration baseline",function()
    local c=environment();c.module.queue_scene("pause",true);c:frame()
    local original=c.module.state.scene_original;c.scene_address=80;c.speed=0.9;local writes=#c.scene_writes;c:frame()
    equal(#c.scene_writes,writes);equal(c.speed,0.9);equal(c.module.state.scene_original,original)
    equal(c.module.state.scene_pause.suspended,true)
    c.module.queue_scene("speed",0.25);c.module.queue_scene("pause",false);c:frame()
    equal(#c.scene_writes,writes);equal(c.speed,0.9);equal(c.module.state.scene_original,original)
    c.module.queue_scene("read");c:frame();equal(#c.scene_writes,writes);equal(c.module.state.scene_actual.speed,0.9)
    equal(c.module.state.scene_message:find("时间倍率已设为零",1,true),nil)
    equal(type(c.module.state.scene_control_error),"string")
    equal(c.module.restore_scene(),false);equal(#c.scene_writes,writes);equal(c.module.state.scene_original,original)
    c.scene_address=12;c.speed=0;c.module.queue_scene("restore");c:frame()
    equal(c.speed,0.8);equal(c.module.state.scene_original,nil);equal(c.module.state.scene_pause,nil)
end)

test("temporary scene and readback failures preserve a paused scene for later recovery",function()
    for _,failure in ipairs({"no_scene","scene_read_error","speed_read_error","bad_speed_readback"}) do
        local c=environment();c.module.queue_scene("pause",true);c:frame()
        local original=c.module.state.scene_original;c[failure]=true;local writes=#c.scene_writes;c:frame()
        equal(c.module.state.scene_pause.suspended,true);equal(c.module.state.scene_original,original)
        equal(#c.scene_writes,writes);c:frame(3);equal(#c.scene_writes,writes)
        c[failure]=false;c.module.queue_scene("restore");c:frame()
        equal(c.speed,0.8);equal(c.module.state.scene_original,nil);equal(c.module.state.scene_pause,nil)
    end
end)

test("failed scene restoration keeps the pause suspended and the exact baseline available for retry",function()
    local c=environment();c.module.queue_scene("pause",true);c:frame()
    c.ignore_speed_write=true;equal(c.module.restore_scene(),false)
    equal(c.speed,0);equal(c.module.state.scene_original.speed,0.8);equal(c.module.state.scene_pause.suspended,true)
    local writes=#c.scene_writes;c:frame(3);equal(#c.scene_writes,writes)
    c.ignore_speed_write=false;equal(c.module.restore_scene(),true)
    equal(c.speed,0.8);equal(c.module.state.scene_original,nil);equal(c.module.state.scene_pause,nil)
end)

test("an original zero timescale requires exact zero restoration rather than a small positive approximation",function()
    local c=environment();c.speed=0;c.module.queue_scene("speed",0.25);c:frame();equal(c.speed,0.25)
    c.zero_write_replacement=0.000001;equal(c.module.restore_scene(),false)
    equal(c.speed,0.000001);equal(c.module.state.scene_original.speed,0)
    c.zero_write_replacement=nil;equal(c.module.restore_scene(),true)
    equal(c.speed,0);equal(c.module.state.scene_original,nil)
end)

test("setter failure after changing timescale still leaves a recoverable first baseline",function()
    local c=environment();c.fail_speed_after_write=true;c.module.queue_scene("pause",true);c:frame()
    equal(c.speed,0);equal(c.module.state.scene_original.speed,0.8);equal(c.module.state.scene_pause.suspended,true)
    c.fail_speed_after_write=false;equal(c.module.restore_scene(),true)
    equal(c.speed,0.8);equal(c.module.state.scene_original,nil);equal(c.module.state.scene_pause,nil)
end)

test("controlled reset refuses to deactivate the editor when restoring zero timescale fails",function()
    local c=environment();c.module.queue_scene("pause",true);c:frame();c.ignore_speed_write=true
    equal(c.module.reset(),false);equal(c.module.active,true);equal(c.env.MotionVfxLive,c.module)
    equal(c.speed,0);equal(c.module.state.scene_original.speed,0.8)
    c.ignore_speed_write=false;equal(c.module.reset(),true);equal(c.speed,0.8);equal(c.module.active,false)
end)

test("scene diagnostics retain bounded command history and verified zero-speed frame evidence",function()
    local c=environment();c.module.queue_scene("pause",true);c:frame(3)
    c.time=c.time+3;c.module.diagnostics();local output=c.files["MotionVfxLive/ui-status.json"]
    equal(output.scenePauseMode,"scene_global_and_effect_update");equal(output.scenePauseRequested,true)
    equal(output.scenePauseSuspended,false);equal(output.scenePaused,true);equal(output.timeScale,0)
    equal(output.sceneResumeSpeed,0.8);equal(output.sceneRestorePending,true)
    equal(output.globalTimeScale,0);equal(output.globalResumeSpeed,1)
    equal(output.globalTimeRestorePending,true);equal(output.globalDeltaTime,0)
    equal(output.effectUpdateEnabled,false);equal(output.effectUpdateRestorePending,true)
    equal(output.effectUpdateConfirmedFrames>=3,true)
    equal(output.scenePauseConfirmedFrames>=3,true)
    local confirmed=output.scenePauseConfirmedFrames
    c.module.queue_scene("pause",false);c:frame();c.time=c.time+3;c.module.diagnostics()
    output=c.files["MotionVfxLive/ui-status.json"]
    equal(output.scenePaused,false);equal(output.timeScale,0.8);equal(output.scenePauseRequested,false)
    equal(output.globalTimeScale,1);equal(output.globalTimeRestorePending,false)
    equal(output.effectUpdateEnabled,true);equal(output.effectUpdateRestorePending,false)
    equal(output.scenePauseConfirmedFrames,confirmed)
    for i=1,20 do c.module.queue_scene("speed",i%2==0 and 0.25 or 0.5);c:frame() end
    c.time=c.time+3;c.module.diagnostics();output=c.files["MotionVfxLive/ui-status.json"]
    equal(#output.sceneCommandHistory,16)
    equal(output.sceneCommandHistory[16].operation,"speed");equal(output.sceneCommandHistory[16].ok,true)
    equal(output.sceneCommandHistory[16].value,0.25);equal(output.sceneCommandHistory[16].speed,0.25)
end)

test("diagnostic failures remain nonfatal and output is throttled", function()
    local c = environment(); local before = #c.writes
    c.module.diagnostics(); equal(#c.writes, before)
    c.write_failure = "MotionVfxLive/ui-status.json"; c.time = c.time + 5
    equal(pcall(c.module.diagnostics), true)
end)

test("incompatible protocol rejects mailbox commands", function()
    local c = environment(); c.files["MotionVfxLive/status.json"] = { ready = true, protocol = 2 }; c:frame()
    equal(c.module.send("undo"), false)
    equal(c.files["MotionVfxLive/request.json"], nil)
    equal(c.module.state.status_error:find("protocol 1", 1, true) ~= nil, true)
end)

test("expired heartbeat blocks edits until the backend becomes live again", function()
    local c = environment()
    c.files["MotionVfxLive/status.json"] = { ready = true, protocol = 1, heartbeatUnixSeconds = c.time - 10 }
    c:frame(); equal(c.module.send("undo"), false)
    equal(c.module.state.status_error:find("心跳", 1, true) ~= nil, true)
    c.files["MotionVfxLive/status.json"].heartbeatUnixSeconds = c.time
    c:frame(); equal(c.module.backend_ready(), true)
    equal(c.module.state.message:find("服务已连接", 1, true) ~= nil, true)
    equal(c.module.send("catalog"), true)
    equal(c.module.state.status_error, nil)
end)

test("pending responses poll within the same second while status stays throttled", function()
    local c = environment(); c.module.send("catalog")
    c.module.poll()
    local before_status = c.reads["MotionVfxLive/status.json"]
    local before_response = c.reads["MotionVfxLive/response.json"]
    c.files["MotionVfxLive/response.json"] = { id = c.module.state.pending.id, ok = true, catalog = { "ready.efx" } }
    for _ = 1, 5 do c.module.poll() end
    equal(c.module.state.pending ~= nil, true)
    equal(c.reads["MotionVfxLive/response.json"], before_response)
    c.module.poll()
    equal(c.module.state.pending, nil); equal(c.module.state.catalog[1], "ready.efx")
    equal(c.reads["MotionVfxLive/status.json"], before_status)
    equal(c.reads["MotionVfxLive/response.json"], before_response + 1)
end)

test("standalone window follows framework visibility and balances close", function()
    local c = environment(); equal(c.windows_started, 0)
    c.show_ui = true; c:frame()
    equal(c.windows_started, 1); equal(c.windows_ended, 1)
    equal(c.window_sizes, 1); equal(c.module.state.window_drawn, true); equal(c.child_windows, 0)
    c.close_window = true; c:frame()
    equal(c.module.state.window_open, false); equal(c.windows_started, 2); equal(c.windows_ended, 2)
    c:frame(); equal(c.windows_started, 2)
    c.close_window = false; c.buttons["Open editor / 打开编辑器##mvl_open_window"] = true; c:launcher(); c:frame()
    equal(c.module.state.window_open, true); equal(c.windows_started, 3); equal(c.windows_ended, 3)
    equal(c.window_sizes, 1)
    c.show_ui = false; c:frame(); equal(c.windows_started, 3); equal(c.module.state.window_drawn, false)
end)

test("initial editor size fits a 720p screen without resizing every frame", function()
    local c = environment(); c.display_width = 1280; c.display_height = 720; c.show_ui = true; c:frame()
    equal(c.initial_window_size[1] < c.display_width, true); equal(c.initial_window_size[2] < c.display_height, true)
    equal(c.initial_window_size[1] > 0, true); equal(c.initial_window_size[2] > 0, true)
    equal(c.window_sizes, 1); c:frame(); equal(c.window_sizes, 1); equal(c.module.state.ui_error, nil)
end)

local function no_window_recovery_game_writes(c)
    equal(#c.scene_writes, 0); equal(#c.calls, 0); equal(#c.runtime_queue, 0)
    equal(#c.binding_applies, 0); equal(#c.binding_queue, 0); equal(c.binding_restores, nil)
    equal(c.capture_starts, nil); equal(c.capture_stops, nil)
    equal(c.files["MotionVfxLive/request.json"], nil)
end

test("persisted offscreen 1080p position is clamped after measuring the actual window", function()
    local c = environment(); c.window_x = 1180; c.window_y = 96; c.show_ui = true
    c:frame()
    equal(c.window_positions, 0, "position is measured only after Begin")
    equal(c.window_x, 1180); equal(c.available_width, 1120)
    c:frame()
    equal(c.window_x, 800); equal(c.window_y, 96); equal(c.window_positions, 1)
    equal(c.window_x + c.available_width <= c.display_width, true)
    equal(c.window_y + c.available_height <= c.display_height, true)
    c:frame(5); equal(c.window_positions, 1); equal(c.window_sizes, 1)
    equal(c.windows_started, c.windows_ended); equal(c.module.state.ui_error, nil)
    no_window_recovery_game_writes(c)
end)

test("fully visible positions including exact screen edges are preserved", function()
    for _, position in ipairs({ { 0, 0 }, { 380, 220 }, { 800, 260 } }) do
        local c = environment(); c.window_x, c.window_y = position[1], position[2]; c.show_ui = true
        c:frame(4)
        equal(c.window_x, position[1]); equal(c.window_y, position[2]); equal(c.window_positions, 0)
        equal(c.window_sizes, 1); no_window_recovery_game_writes(c)
    end
    local c = environment(); c.module.state.window_initialized = true
    c.window_x, c.window_y, c.available_width, c.available_height = 0, 0, 1920, 1080
    c.show_ui = true; c:frame(3)
    equal(c.window_positions, 0); equal(c.window_sizes, 0, "a fully visible fullscreen window is retained")
end)

test("ordinary dragging and resizing are not overridden on subsequent frames", function()
    local c = environment(); c.show_ui = true; c:frame(2)
    c.window_x, c.window_y, c.available_width, c.available_height = 2100, -90, 1300, 900
    c:frame(4)
    equal(c.window_x, 2100); equal(c.window_y, -90)
    equal(c.available_width, 1300); equal(c.available_height, 900)
    equal(c.window_positions, 0); equal(c.window_sizes, 1)
    no_window_recovery_game_writes(c)
end)

test("resolution reduction fits actual dimensions once and enlargement preserves placement", function()
    local c = environment(); c.window_x, c.window_y = 800, 260; c.show_ui = true; c:frame(2)
    c.display_width, c.display_height = 1280, 720
    c:frame(); equal(c.window_positions, 0); equal(c.window_sizes, 1)
    c:frame(); equal(c.window_x, 160); equal(c.window_y, 0)
    equal(c.available_width, 1120); equal(c.available_height, 720)
    equal(c.window_positions, 1); equal(c.window_sizes, 2)
    c.display_width, c.display_height = 1920, 1080; c:frame(4)
    equal(c.window_x, 160); equal(c.window_y, 0); equal(c.available_height, 720)
    equal(c.window_positions, 1); equal(c.window_sizes, 2)
    no_window_recovery_game_writes(c)
end)

test("resolution change discards a correction measured for the previous display", function()
    local c = environment(); c.window_x, c.window_y = 1180, -20; c.show_ui = true; c:frame()
    c.display_width, c.display_height = 1280, 720; c:frame()
    equal(c.window_positions, 0); equal(c.window_x, 1180)
    c:frame(); equal(c.window_x, 160); equal(c.window_y, 0); equal(c.available_height, 720)
    equal(c.window_positions, 1); no_window_recovery_game_writes(c)
end)

test("launcher recovery reopens and centers the current window without changing editing or game state", function()
    local c = environment(); c.show_ui = true; c:frame(2)
    c.module.state.values = { ["Color.R"] = "0.25" }
    c.module.state.window_open = false
    c.window_x, c.window_y, c.available_width, c.available_height = 2350, -80, 900, 600
    c.buttons["找回窗口 / 居中##mvl_recover_window"] = true; c:launcher()
    equal(c.module.state.window_open, true); c:frame(2)
    equal(c.window_x, 510); equal(c.window_y, 240)
    equal(c.available_width, 900); equal(c.available_height, 600)
    equal(c.module.state.values["Color.R"], "0.25")
    equal(c.window_positions, 1); equal(c.window_sizes, 1)
    c:frame(3); equal(c.window_positions, 1)
    no_window_recovery_game_writes(c)
end)

test("showing the editor again recovers a window dragged beyond the display", function()
    local c = environment(); c.show_ui = true; c:frame(2)
    c.window_x, c.window_y = -350, -70; c.show_ui = false; c:frame()
    equal(c.window_positions, 0)
    c.show_ui = true; c:frame(2)
    equal(c.window_x, 0); equal(c.window_y, 0); equal(c.window_positions, 1)
    no_window_recovery_game_writes(c)
end)

test("invalid minimized display defers initialization and recovery until valid dimensions return", function()
    local c = environment(); c.display_width, c.display_height = 0, 0
    c.window_x, c.window_y = 1180, 96; c.show_ui = true; c:frame(2)
    equal(c.window_sizes, 0); equal(c.window_positions, 0)
    equal(c.module.state.window_initialized, false)
    c.display_width, c.display_height = 1920, 1080; c:frame(2)
    equal(c.window_x, 800); equal(c.window_y, 96)
    equal(c.window_sizes, 1); equal(c.window_positions, 1); equal(c.module.state.ui_error, nil)
    no_window_recovery_game_writes(c)
end)

test("window geometry read failure still balances Begin End and the theme stack", function()
    local c = environment(); c.show_ui = true; c.fail_window_position = true; c:frame()
    equal(c.module.state.ui_error:find("mock window position failure", 1, true) ~= nil, true)
    equal(c.windows_started, c.windows_ended); equal(c.child_windows, 0)
    equal(c.style_colors, 0); equal(c.font_sizes, 0)
    c.fail_window_position = false; c.buttons["Retry UI##mvl_retry_ui"] = true; c:frame(2)
    equal(c.module.state.ui_error, nil); equal(c.windows_started, c.windows_ended)
    no_window_recovery_game_writes(c)
end)

test("window failure cannot disable frame-based scene recovery", function()
    local c = environment(); c.module.queue_scene("pause", true); c:frame(); equal(c.speed,0)
    c.show_ui = true
    local original = c.env.imgui.button
    c.env.imgui.button = function() error("mock UI failure") end
    c:frame(); equal(c.module.state.ui_error:find("mock UI failure", 1, true) ~= nil, true)
    equal(c.windows_started, c.windows_ended)
    c.env.imgui.button = original
    c.module.queue_scene("restore"); c:frame(); equal(c.paused, false);equal(c.speed,0.8)
    equal(c.module.active, true)
end)

test("tab switching and opening a document keep editor controls reachable", function()
    local c = environment()
    c.buttons["辅助预览##mvl_tab_preview"] = true; c:draw("resources")
    equal(c.module.state.active_tab, "preview")
    c.module.state.local_path = "sample.efx"; c.buttons["打开文件##mvl_open"] = true; c:draw("resources")
    equal(c.files["MotionVfxLive/request.json"].action, "open")
    c:respond({ ok = true, document = { revision = 1, entries = {}, actions = {} } })
    equal(c.module.state.active_tab, "edit")
end)

test("document entry inspects and edits file fields without invalid structural commands", function()
    local c = environment(); c.module.state.document = { revision = 7 }
    c.buttons["文件属性（骨骼、关系、表达式、分组）##mvl_document_fields"] = true; c:draw()
    equal(c.files["MotionVfxLive/request.json"].target.kind, "document")
    equal(c.files["MotionVfxLive/request.json"].target.index, 0)
    c:respond({ ok = true, fields = { { path = "Header.dimensionType", value = "1", editable = true, type = "UInt32" } } })
    local prior = c.files["MotionVfxLive/request.json"].id
    c.buttons["添加属性到当前块或动作##mvl_add_attribute"] = true
    c.buttons["删除当前选中项##mvl_remove"] = true; c.module.state.confirm_delete = true; c:draw()
    equal(c.files["MotionVfxLive/request.json"].id, prior)
    c.inputs["##mvl_value_Header.dimensionType"] = "2"
    c.buttons["提交##mvl_set_Header.dimensionType"] = true; c:draw()
    equal(c.files["MotionVfxLive/request.json"].action, "set")
    equal(c.files["MotionVfxLive/request.json"].target.kind, "document")
    equal(c.files["MotionVfxLive/request.json"].revision, 7)
end)

test("fields paginate 80 at a time and filtering resets to the matching first page", function()
    local c = environment(); local state = c.module.state
    state.document = { revision = 1 }; state.selected = { kind = "document", index = 0 }
    for i = 1, 181 do
        state.fields[i] = { path = "Field" .. i, value = tostring(i), editable = true, type = "Int32" }
        state.values["Field" .. i] = tostring(i)
    end
    c:draw(); equal(#c.drawn_fields, 80); equal(c.drawn_fields[80], "##mvl_value_Field80")
    state.values.Field2 = "draft"; c.drawn_fields = {}; c.buttons["下一页##mvl_fields_next"] = true; c:draw()
    equal(state.field_page, 2); equal(#c.drawn_fields, 80); equal(c.drawn_fields[1], "##mvl_value_Field81")
    c.drawn_fields = {}; c.buttons["下一页##mvl_fields_next"] = true; c:draw()
    equal(state.field_page, 3); equal(#c.drawn_fields, 21)
    c.drawn_fields = {}; c.inputs["筛选字段##mvl_field_filter"] = "field181"; c:draw()
    equal(state.field_page, 1); equal(#c.drawn_fields, 1); equal(c.drawn_fields[1], "##mvl_value_Field181")
    equal(state.values.Field2, "draft")
    c.drawn_fields = {}; c.inputs["筛选字段##mvl_field_filter"] = "not-a-field"; c:draw()
    equal(#c.drawn_fields, 0); equal(state.field_page, 1)
end)

local function field_filter_fixture(c)
    local state = c.module.state
    state.document = { revision = 11, source = "rgb-common.efx.1", entries = {}, actions = {} }
    state.selected = { kind = "entry", index = 0, attributeIndex = 0 }
    state.fields = {
        { path = "Color.R", label = "颜色", value = "1", editable = true, type = "Single" },
        { path = "Color.G", label = "颜色", value = "1", editable = true, type = "Single" },
        { path = "Brightness", label = "亮度", value = "2", editable = true, type = "Single" },
        { path = "IsTypeAttribute", value = "True", editable = false, type = "Boolean", reason = "metadata" },
    }
    state.values = { ["Color.R"] = "0.2", ["Color.G"] = "1", Brightness = "2" }
    return state
end

test("field display filters retain hidden drafts and recompute modified membership", function()
    local c = environment(); local state = field_filter_fixture(c)
    equal(state.field_editable_only, false); equal(state.field_modified_only, false); equal(state.field_show_paths, false)
    state.field_modified_only = true; c:draw()
    equal(#c.drawn_fields, 1); equal(c.drawn_fields[1], "##mvl_value_Color.R")
    state.field_filter = "Brightness"; c.drawn_fields = {}; c:draw()
    equal(#c.drawn_fields, 0); equal(state.values["Color.R"], "0.2")
    state.values.Brightness = "4"; c.drawn_fields = {}; c:draw()
    equal(#c.drawn_fields, 1); equal(c.drawn_fields[1], "##mvl_value_Brightness")
    state.field_filter = ""; state.field_editable_only = true; state.field_show_paths = true; c.drawn_fields = {}; c:draw()
    equal(#c.drawn_fields, 2); equal(state.values["Color.R"], "0.2"); equal(state.values.Brightness, "4")
    state.values["Color.R"] = "1"; c.drawn_fields = {}; c:draw()
    equal(#c.drawn_fields, 1); equal(c.drawn_fields[1], "##mvl_value_Brightness")
    state.field_modified_only = false; state.field_editable_only = false; state.field_show_paths = false
    c.drawn_fields = {}; c:draw(); equal(#c.drawn_fields, 3); equal(state.values.Brightness, "4")
    equal(state.selected.index, 0); equal(state.selected.attributeIndex, 0); equal(state.document.revision, 11)
    equal(c.files["MotionVfxLive/request.json"], nil)
end)

test("filtered duplicate labels submit the exact field path and preserve other drafts", function()
    local c = environment(); local state = field_filter_fixture(c)
    state.values["Color.G"] = "0.7"; state.field_modified_only = true; state.field_filter = "Color.G"
    c.inputs["##mvl_value_Color.G"] = "0.8"; c.buttons["提交##mvl_set_Color.G"] = true; c:draw()
    local request = c.files["MotionVfxLive/request.json"]
    equal(request.action, "set"); equal(request.path, "Color.G"); equal(request.value, "0.8")
    equal(request.target.index, 0); equal(request.target.attributeIndex, 0); equal(request.revision, 11)
    equal(#c.drawn_fields, 1); equal(state.values["Color.R"], "0.2")
    c:respond({ ok = true, document = { revision = 12, source = "rgb-common.efx.1", dirty = true } })
    c:respond({ ok = true, fields = {
        { path = "Color.R", label = "颜色", value = "1", editable = true, type = "Single" },
        { path = "Color.G", label = "颜色", value = "0.8", editable = true, type = "Single" },
    } })
    c.drawn_fields = {}; c:draw(); equal(#c.drawn_fields, 0); equal(state.values["Color.R"], "0.2")
    state.field_filter = ""; c.drawn_fields = {}; c:draw()
    equal(#c.drawn_fields, 1); equal(c.drawn_fields[1], "##mvl_value_Color.R")
end)

test("a draft hidden by filters still blocks real resource publication", function()
    local c = environment(); local state = field_filter_fixture(c)
    state.document.sourceKind = "game_files"
    state.target_resource_path = "effect/rgb-common.efx"; state.target_document_source = state.document.source
    state.field_filter = "Brightness"; state.field_modified_only = true
    c.buttons["应用到角色真实资源##mvl_apply_resource"] = true; c:draw()
    equal(#c.drawn_fields, 0); equal(state.values["Color.R"], "0.2"); equal(state.pending, nil)
    equal(c.files["MotionVfxLive/request.json"], nil); equal(#c.binding_applies, 0); equal(#c.runtime_queue, 0)
    equal(state.action_message:find("未提交", 1, true) ~= nil, true)
end)

test("manual selection and reinspection refuse to discard an unsubmitted field", function()
    local c = environment(); local state = field_filter_fixture(c)
    c.buttons["重新读取字段##mvl_inspect"] = true; c:draw()
    equal(state.pending, nil); equal(c.files["MotionVfxLive/request.json"], nil)
    equal(c.module.inspect({ kind = "entry", index = 7, attributeIndex = 2 }), false)
    c.buttons["文件属性（骨骼、关系、表达式、分组）##mvl_document_fields"] = true; c:draw()
    equal(state.pending, nil); equal(state.selected.kind, "entry"); equal(state.selected.index, 0); equal(state.selected.attributeIndex, 0)
    equal(state.values["Color.R"], "0.2"); equal(state.message:find("未提交", 1, true) ~= nil, true)
    state.values["Color.R"] = "1"
    equal(c.module.inspect({ kind = "entry", index = 7, attributeIndex = 2 }), true)
    c:respond({ ok = true, fields = { { path = "Enabled", value = "True", editable = true, type = "Boolean" } } })
    equal(state.selected.index, 7); equal(state.selected.attributeIndex, 2); equal(state.values.Enabled, "True")
end)

test("save blocks a filtered-out draft then accepts the submitted document", function()
    local c = environment(); local state = field_filter_fixture(c)
    state.field_filter = "Brightness"; state.field_modified_only = true
    c.buttons["保存副本##mvl_save"] = true; c:draw()
    equal(state.pending, nil); equal(c.files["MotionVfxLive/request.json"], nil)
    equal(state.values["Color.R"], "0.2"); equal(state.message:find("未提交", 1, true) ~= nil, true)
    state.values["Color.R"] = "1"; c.buttons["导出文件##mvl_save"] = true; c:draw()
    equal(c.files["MotionVfxLive/request.json"].action, "begin_export"); equal(state.document.revision, 11)
    equal(#c.runtime_queue, 0); equal(#c.binding_applies, 0)
end)

test("structure search preserves active edits and keeps original attribute indices", function()
    local c = environment(); local state = field_filter_fixture(c)
    state.document.entries = {
        { index = 7, name = "Fire", attributes = { { index = 0, type = "RgbCommon" }, { index = 3, type = "Transform" } } },
        { index = 9, name = "Dust", attributes = { { index = 1, type = "Transform" } } },
    }
    state.structure_filter = "rgbcommon"; c:draw()
    local visible = {}
    for _, button in ipairs(c.drawn_buttons) do visible[button.label] = true end
    equal(visible["属性 0: 通用颜色（RgbCommon）##mvl_attr_entry7_0"], true)
    equal(visible["属性 1: Transform##mvl_attr_entry9_1"], nil)
    equal(state.selected.index, 0); equal(state.selected.attributeIndex, 0); equal(state.values["Color.R"], "0.2")
    equal(c.files["MotionVfxLive/request.json"], nil)
    state.structure_filter = "no-such-attribute"; c:draw(); equal(state.values["Color.R"], "0.2"); equal(state.selected.index, 0)
    state.structure_filter = "rgbcommon"; state.values["Color.R"] = "1"
    c.buttons["属性 0: 通用颜色（RgbCommon）##mvl_attr_entry7_0"] = true; c:draw()
    local request = c.files["MotionVfxLive/request.json"]
    equal(request.action, "inspect"); equal(request.target.kind, "entry"); equal(request.target.index, 7); equal(request.target.attributeIndex, 0)
end)

local function grouped_document(c)
    c.module.state.document = { source = "game-files://fixture/grouped.efx.5571972", name = "grouped.efx", revision = 5,
        entries = {
            { index = 7, name = "Fire", attributes = { { index = 3, type = "RgbCommon" } } },
            { index = 9, name = "Dust", attributes = {} }, { index = 11, name = "Loose", attributes = {} },
        }, groups = { { index = 0, name = "Flame", entries = { 9, 7 } },
            { index = 1, name = "Empty", entries = {} }, { index = 2, name = "Shared", entries = { 7 } } },
        actions = { { index = 0, name = "Play", attributes = {} } } }
end
local function group_note_input_id(c)
    local state = c.module.state
    local source = state.document.source:gsub("[^%w_%-]", function(char) return string.format("%%%02X", string.byte(char)) end)
    return "##mvl_group_note_value_" .. source .. "_group_" .. state.selected.index .. "_nil"
end

test("group tree preserves membership order shared blocks empty groups and ungrouped blocks", function()
    local c = environment(); grouped_document(c); c:draw("edit")
    local order = {}; for _, item in ipairs(c.tree_history) do order[#order + 1] = item.id end
    local tree = table.concat(order, "|")
    equal(tree:find("##mvl_efx_file|###mvl_group_0|##mvl_entry9_member1|##mvl_entry7_member2|###mvl_group_1|###mvl_group_2|##mvl_entry7_member1|##mvl_ungrouped|##mvl_entry11|动作 Actions|##mvl_action0", 1, true) ~= nil, true)
    equal(c.tree_depth, 0); equal(c.module.state.pending, nil)
    c.tree_history = {}; c.module.state.structure_filter = "Flame"; c:draw("edit")
    local visible = {}; for _, item in ipairs(c.tree_history) do visible[item.id] = true end
    equal(visible["###mvl_group_0"], true); equal(visible["##mvl_entry9_member1"], true)
    equal(visible["###mvl_group_1"], nil); equal(visible["###mvl_group_2"], nil); equal(visible["##mvl_ungrouped"], nil)
    c.module.state.structure_filter = "rgbcommon"
    c.buttons["属性 3: RgbCommon##mvl_attr_entry7_3"] = true; c:draw("edit")
    local request = c.files["MotionVfxLive/request.json"]
    equal(request.target.kind, "entry"); equal(request.target.index, 7); equal(request.target.attributeIndex, 3)
end)

test("group notes preserve native names EFX revision and dirty state while protecting drafts", function()
    local c = environment(); grouped_document(c); local state = c.module.state
    state.field_filter = "SizeScalar"; state.field_modified_only = true
    state.document.dirty = false
    c.buttons["编辑分组备注##mvl_group_rename_2"] = true; c:draw("edit")
    equal(c.files["MotionVfxLive/request.json"].target.kind, "group")
    equal(c.files["MotionVfxLive/request.json"].target.index, 2)
    c:respond({ ok = true, fields = { { path = "note", label = "分组备注", type = "System.String", value = "", editable = true } } })
    equal(state.field_filter, ""); equal(state.field_modified_only, false)
    c.inputs[group_note_input_id(c)] = "共享火焰"; c:draw("edit")
    equal(state.values.note, "共享火焰")
    equal(c.module.inspect({ kind = "entry", index = 7 }), false)
    c.buttons["保存副本##mvl_save"] = true; c:draw("edit"); equal(state.pending, nil)
    c.buttons["添加特效块##mvl_add_entry"] = true; c:draw("edit"); equal(state.pending, nil)
    equal(state.values.note, "共享火焰")
    c.buttons["保存备注##mvl_set_note"] = true; c:draw("edit")
    local request = c.files["MotionVfxLive/request.json"]
    equal(request.action, "set_group_note"); equal(request.groupIndex, 2)
    equal(request.note, "共享火焰"); equal(request.revision, 5); equal(request.name, nil)
    c:respond({ ok = false, error = "sidecar write failed" })
    equal(state.values.note, "共享火焰"); equal(state.selected.kind, "group")
    c.buttons["保存备注##mvl_set_note"] = true; c:draw("edit")
    local updated = copy_values(state.document)
    updated.groups = { state.document.groups[1], state.document.groups[2], { index = 2, name = "Shared", note = "共享火焰", displayName = "共享火焰", entries = { 7 } } }
    c:respond({ ok = true, document = updated, revision = 5, noteSaved = true, noteStorage = "editor_sidecar" })
    equal(c.files["MotionVfxLive/request.json"].action, "inspect")
    c:respond({ ok = true, fields = { { path = "note", label = "分组备注", type = "System.String", value = "共享火焰", editable = true } } })
    equal(state.document.groups[3].name, "Shared"); equal(state.document.groups[3].note, "共享火焰"); equal(state.values.note, "共享火焰")
    equal(state.document.revision, 5); equal(state.document.dirty, false); equal(c.module.has_drafts(), false)
    c.drawn_buttons = {}; c:draw("edit")
    for _, item in ipairs(c.drawn_buttons) do equal(item.id == "##mvl_add_attribute" or item.id == "##mvl_remove", false) end
    equal(#c.binding_applies, 0); equal(#c.runtime_queue, 0)
end)

test("files without groups retain the complete flat entry tree", function()
    local c = environment(); grouped_document(c)
    c.module.state.document.groups = {}; c:draw("edit")
    local visible = {}; for _, item in ipairs(c.tree_history) do visible[item.id] = true end
    equal(visible["特效块 Entries"], true); equal(visible["##mvl_entry7"], true)
    equal(visible["##mvl_entry9"], true); equal(visible["##mvl_entry11"], true)
    equal(visible["###mvl_group_0"], nil); equal(c.tree_depth, 0)
end)

test("field rendering failure unwinds local child and theme stacks and keeps recovery usable", function()
    local c = environment(); local state = field_filter_fixture(c)
    state.active_tab = "edit"; c.show_ui = true
    c.module.queue_scene("pause", true); c:frame(); equal(c.speed,0)
    c.fail_input = "##mvl_value_Color.R"; c:frame()
    equal(state.ui_error:find("mock input failure", 1, true) ~= nil, true)
    equal(c.child_windows, 0); equal(c.style_vars, 0); equal(c.style_colors, 0); equal(c.font_sizes, 0)
    equal(c.tree_depth, 0); equal(c.windows_started, c.windows_ended)
    equal(state.values["Color.R"], "0.2"); equal(state.document.revision, 11)
    c.fail_input = nil; c.buttons["Stop preview / Restore scene##mvl_window_restore"] = true; c:frame(); c:lock(); c:frame()
    equal(c.paused, false);equal(c.speed,0.8); equal(c.module.active, true)
    c.buttons["Retry UI##mvl_retry_ui"] = true; c:frame(); c:frame()
    equal(state.ui_error, nil); equal(c.child_windows, 0); equal(c.style_vars, 0); equal(c.style_colors, 0); equal(c.font_sizes, 0)
    equal(c.windows_started, c.windows_ended); equal(state.values["Color.R"], "0.2")
end)

test("resizing keeps document actions outside scroll panes and preserves edits", function()
    local c = environment(); local state = field_filter_fixture(c)
    state.document.sourceKind = "game_files"
    state.target_resource_path = "effect/rgb-common.efx"; state.target_document_source = state.document.source
    state.active_tab = "edit"; c.show_ui = true
    for _, size in ipairs({ { width = 1200, layout = "columns" }, { width = 560, layout = "stacked" }, { width = 1200, layout = "columns" } }) do
        c.available_width = size.width; c.drawn_buttons = {}; c.drawn_fields = {}; c.child_history = {}; c:frame()
        equal(state.edit_layout, size.layout); equal(state.ui_error, nil)
        local depth = {}
        for _, button in ipairs(c.drawn_buttons) do depth[button.id] = button.child_depth end
        for _, label in ipairs({ "应用到角色真实资源##mvl_apply_resource", "保存副本##mvl_save", "撤销##mvl_undo",
            "重做##mvl_redo", "还原资源试改##mvl_restore_resources", "停止记录及预览，并还原资源与场景##mvl_emergency_restore" }) do
            equal(depth[control_id(label)], 0, label .. " stays outside scroll panes")
        end
        equal(type(depth["##mvl_set_Color.R"]), "number"); equal(depth["##mvl_set_Color.R"] > 0, true)
        equal(state.values["Color.R"], "0.2"); equal(state.selected.index, 0); equal(state.selected.attributeIndex, 0)
        equal(c.child_windows, 0); equal(c.style_vars, 0); equal(c.style_colors, 0); equal(c.font_sizes, 0)
        equal(c.windows_started, c.windows_ended); equal(c.files["MotionVfxLive/request.json"], nil)
    end
    c.available_width = 560; c.binding_state.appliedCount = 1; c.module.refresh_action_state()
    c.buttons["还原资源试改##mvl_restore_resources"] = true; c:frame(); c:lock(); c:frame()
    equal(c.binding_restores, 1); equal(c.binding_state.appliedCount, 0); equal(state.values["Color.R"], "0.2")
    equal(state.edit_layout, "stacked"); equal(state.ui_error, nil)
end)

test("a clipped edit child still closes its child stack", function()
    local c = environment(); local state = field_filter_fixture(c)
    state.active_tab = "edit"; c.show_ui = true; c.clipped_child = "结构导航##mvl_structure_pane"
    c:frame(); equal(c.child_windows, 0); equal(c.windows_started, c.windows_ended); equal(state.ui_error, nil)
    local shown = {}
    for _, button in ipairs(c.drawn_buttons) do shown[button.id] = true end
    equal(shown["##mvl_document_fields"], nil); equal(#c.drawn_fields, 3)
    c.clipped_child = "字段编辑##mvl_property_pane"; c.drawn_fields = {}; c:frame()
    equal(#c.drawn_fields, 0)
    equal(c.child_windows, 0); equal(c.style_colors, 0); equal(c.font_sizes, 0); equal(state.ui_error, nil)
    equal(state.values["Color.R"], "0.2"); equal(c.files["MotionVfxLive/request.json"], nil)
end)

test("integer input and exact-one restoration marshal to floating point", function()
    local c = environment(); c.speed = 1
    c.module.state.speed = "1"; c.module.play("float.efx"); c:lock()
    equal(c.last_play_options.play_speed, 1.0)
    c.module.queue_effect_speed(1); c:lock()
    equal(math.type(c.calls[#c.calls].value), "float")
    c.module.queue_scene("speed", 1); c:frame()
    equal(math.type(c.scene_writes[#c.scene_writes].value), "float")
    c.module.queue_scene("speed", 0.25); c:frame()
    c.module.queue_scene("restore"); c:frame()
    equal(c.speed, 1.0); equal(math.type(c.scene_writes[#c.scene_writes].value), "float")
end)

test("diagnostics distinguish running resources from visible rendering", function()
    local c = environment(); c.module.play("loaded.efx"); c:lock(); c.time = c.time + 3; c.module.diagnostics()
    local actual = c.files["MotionVfxLive/ui-status.json"]
    equal(actual.effectSpeed, 1.0); equal(actual.effectReadyResource, true)
    equal(actual.effectRunning, true); equal(actual.effectValid, true); equal(actual.effectEmitterInstanceNum, 7)
    equal(actual.effectResourcePath, "loaded.efx"); equal(actual.effectState, 2)
    equal(actual.effectCulled, false); equal(actual.effectGroupEnabled, false)
    equal(actual.runtimeVersion, "2.1"); equal(actual.runtimePointCloudSupported, false)
    equal(next(actual.effectReadErrors), nil)
    c.resource_not_ready = true; c.failed_effect_getter = "get_State"
    c.time = c.time + 3; c.module.diagnostics(); actual = c.files["MotionVfxLive/ui-status.json"]
    equal(actual.effectReadyResource, false); equal(actual.effectSpeed, 1.0)
    equal(actual.effectEmitterInstanceNum, 7); equal(actual.effectState, nil)
    equal(actual.effectReadErrors.player_state:find("mock getter failure", 1, true) ~= nil, true)
end)

test("point cloud remains absent for ordinary playback with the older runtime", function()
    local c = environment(); equal(c.module.state.point_cloud_enabled, false)
    c.module.state.point_cloud_indices = "invalid input that is not enabled"
    equal(c.module.play("ordinary.efx"), true); c:lock()
    equal(c.last_play_options.point_cloud, nil)
    equal(c.files["MotionVfxLive/request.json"], nil)
end)

test("point cloud request rejects a missing runtime capability before playback", function()
    local c = environment(); c.module.play("existing.efx"); c:lock()
    c.checks["提供点云发射位置（实验）##mvl_point_cloud"] = true; c:draw("preview")
    equal(c.module.state.point_cloud_enabled, true)
    equal(c.module.play("pointcloud.efx"), false); equal(#c.runtime_queue, 0)
    equal(c.instance.path, "existing.efx")
    equal(c.module.state.preview_message:find("重启游戏", 1, true) ~= nil, true)
    equal(c.module.state.preview_message:find("2.1", 1, true) ~= nil, true)
    c.runtime.capabilities = { point_cloud = false }
    equal(c.module.play("pointcloud.efx"), false)
end)

test("point cloud input passes ordered integer indices and selected space without editing EFX", function()
    local c = environment(); c.runtime.version = "2.2"; c.runtime.capabilities = { point_cloud = true }
    c.module.state.point_cloud_enabled = true
    equal(c.module.play("cloud.efx"), true); c:lock()
    local cloud = c.last_play_options.point_cloud
    equal(cloud.space, "local"); equal(#cloud.indices, 5)
    for i = 1, 5 do equal(cloud.indices[i], i - 1); equal(math.type(cloud.indices[i]), "integer") end
    c.module.state.point_cloud_space = 2; c.module.state.point_cloud_indices = "31, 0,  4"
    equal(c.module.play("cloud.efx"), true); c:lock(); cloud = c.last_play_options.point_cloud
    equal(cloud.space, "world"); equal(cloud.indices[1], 31); equal(cloud.indices[2], 0); equal(cloud.indices[3], 4)
    equal(c.files["MotionVfxLive/request.json"], nil)
    c.time = c.time + 3; c.module.diagnostics()
    equal(c.files["MotionVfxLive/ui-status.json"].runtimeVersion, "2.2")
    equal(c.files["MotionVfxLive/ui-status.json"].runtimePointCloudSupported, true)
end)

test("point cloud validates integer, range, empty items and duplicates before enqueuing", function()
    local c = environment(); c.runtime.capabilities = { point_cloud = true }; c.module.state.point_cloud_enabled = true
    for _, value in ipairs({ "", "1.5", "-1", "32", "999999999999999999999999", "0,,1", "0,", ",1", "0,0", "01,1", "1e1", "nan" }) do
        c.module.state.point_cloud_indices = value
        equal(c.module.play("cloud.efx"), false, value); equal(#c.runtime_queue, 0, value)
        equal(c.module.state.preview_message:find("点云索引", 1, true) ~= nil, true, value)
    end
    c.module.state.point_cloud_indices = "0,31"; c.module.state.point_cloud_space = 3
    equal(c.module.play("cloud.efx"), false); equal(#c.runtime_queue, 0)
end)

test("action page is default and capture continues while Insert hides UI", function()
    local c = environment()
    equal(c.module.state.active_tab, "actions"); equal(c.capture_starts, nil)
    c.buttons["开始记录角色动作##mvl_capture_start"] = true; c:draw("actions")
    equal(c.capture_state.recording, true); equal(c.capture_starts, 1)
    c.show_ui = false; c:frame(); equal(c.capture_state.recording, true)
    c.show_ui = true; c.close_window = true; c:frame(); equal(c.capture_state.recording, false)
    equal(c.capture_stops, 1); equal(#c.binding_applies, 0); equal(#c.runtime_queue, 0)
end)

test("action resources default to confirmed ownership while preserving unknown diagnostics and original row IDs", function()
    local c = environment(); local shown = {}
    c.capture_state.resources = {
        { path = "enemy/unknown.efx", localConfirmed = false },
        { path = "weapon/confirmed.efx", localConfirmed = true },
        { path = "guide/unresolved.efx" },
    }
    c.capture_state.hooks = { { label = "hook", static = false, returnType = "Container", pre = 3, post = 3 } }
    local original_button = c.env.imgui.button
    c.env.imgui.button = function(label) shown[label] = true; return original_button(label) end
    equal(c.module.state.show_unconfirmed_resources, false)
    c.module.refresh_action_state(); c:draw("actions")
    equal(shown["选择并编辑##mvl_capture_resource_2"], true)
    equal(shown["查看资源（角色归属未确认）##mvl_capture_resource_1"], nil)
    equal(shown["查看资源（角色归属未确认）##mvl_capture_resource_3"], nil)
    equal(table.concat(c.messages, "\n"):find("未确认 2 个", 1, true) ~= nil, true)
    c.checks["显示归属未确认资源##mvl_show_unconfirmed_resources"] = true; shown = {}; c:draw("actions")
    equal(shown["选择并编辑##mvl_capture_resource_2"], true)
    equal(shown["查看资源（角色归属未确认）##mvl_capture_resource_1"], true)
    equal(shown["查看资源（角色归属未确认）##mvl_capture_resource_3"], true)
    c.checks["显示归属未确认资源##mvl_show_unconfirmed_resources"] = false; shown = {}; c:draw("actions")
    equal(shown["查看资源（角色归属未确认）##mvl_capture_resource_1"], nil)
    c.buttons["选择并编辑##mvl_capture_resource_2"] = true; c:draw("actions")
    equal(c.files["MotionVfxLive/request.json"].sourceDescriptor.actualPath, "weapon/confirmed.efx")
    c.time = c.time + 3; c.module.diagnostics(); local actual = c.files["MotionVfxLive/ui-status.json"]
    equal(actual.captureResourceCount, 3); equal(#actual.captureResources, 3)
    equal(actual.captureConfirmedResourceCount, 1); equal(actual.captureUnconfirmedResourceCount, 2)
    equal(actual.captureResources[1].path, "enemy/unknown.efx"); equal(actual.captureHooks[1].static, false)
    equal(actual.captureHooks[1].returnType, "Container"); equal(actual.captureHooks[1].pre, 3); equal(actual.captureHooks[1].post, 3)
end)

local function open_captured(c, path, resolved)
    local resource = { path = path, canonicalPath = path, localConfirmed = true, count = 2, evidence = { "owner_is_local_weapon" } }
    c.capture_state.resources = { resource }
    resource.latestSource = copy_values(c.capture.get_resource_source(path))
    equal(c.module.resolve_captured_resource(resource), true)
    equal(c.files["MotionVfxLive/request.json"].action, "open_game_resource")
    equal(c.files["MotionVfxLive/request.json"].sourceDescriptor.actualPath, path)
    equal(c.files["MotionVfxLive/request.json"].path, nil)
    c:respond({ ok = true, document = { source = resolved, sourceKind = "game_files",
        gameSource = copy_values(c.files["MotionVfxLive/request.json"].sourceDescriptor), revision = 4, entries = {}, actions = {}, dirty = false } })
    equal(c.module.state.target_resource_path, path); equal(c.module.state.target_document_source, resolved)
end

test("game package lookup waits longer without repeating the request and accepts its eventual result", function()
    local c = environment()
    local resource = { path = "effect/selected.efx", localConfirmed = true }
    c.capture_state.resources = { resource }
    equal(c.module.resolve_captured_resource(resource), true)
    local request = copy_values(c.files["MotionVfxLive/request.json"])
    c.time = c.time + 20; c:frame(6)
    equal(c.module.state.pending.timeout, false)
    equal(c.module.resolve_captured_resource(resource), false)
    equal(c.files["MotionVfxLive/request.json"].id, request.id)
    c.time = c.time + 41; c:frame(6)
    equal(c.module.state.pending.timeout, true)
    c:respond({ ok = true, document = { source = "game-files/extracted.efx.5571972", sourceKind = "game_files",
        gameSource = request.sourceDescriptor, revision = 1, dirty = false } })
    equal(c.module.state.pending, nil)
    equal(c.module.state.target_resource_path, "effect/selected.efx")
    equal(#c.binding_applies, 0); equal(#c.runtime_queue, 0)
end)

test("game installation source remains visible and sync restores only a current captured target", function()
    for _, current in ipairs({ true, false }) do
        local c = environment(); open_captured(c, "effect/selected.efx", "C:/editor/game-files/selected.efx.5571972")
        local document = c.module.state.document
        document.gameSource.originKind = "pak_entry"
        document.gameSource.originPath = "D:/game/re_chunk_000.pak.sub_000.pak.patch_021.pak"
        document.gameSource.nativePath = "natives/stm/effect/selected.efx.5571972"
        c:draw("edit")
        equal(table.concat(c.messages, "\n"):find("游戏目录（按角色实际触发路径读取）", 1, true) ~= nil, true)
        c.module.state.target_resource_path, c.module.state.target_document_source = nil, nil
        if not current then c.capture_state.sceneEpoch = 2 end
        equal(c.module.synchronize(), true)
        c:respond({ ok = true, document = document })
        equal(c.module.state.target_resource_path, current and "effect/selected.efx" or nil)
        equal(c.module.state.target_document_source, current and document.source or nil)
        equal(#c.binding_applies, 0); equal(#c.runtime_queue, 0)
        c.time = c.time + 3; c.module.diagnostics()
        equal(c.files["MotionVfxLive/ui-status.json"].gameSource.originPath, document.gameSource.originPath)
    end
end)

test("captured action applies exact resource identity without independent playback", function()
    local c = environment(); local original = "effect/weapon/wp02/action000.efx"
    open_captured(c, original, "C:/material/exact/action000.efx.1769672")
    c.buttons["应用到角色真实资源##mvl_apply_resource"] = true; c:draw("edit")
    equal(c.files["MotionVfxLive/request.json"].action, "publish")
    equal(c.files["MotionVfxLive/request.json"].revision, 4)
    c:respond({ id = "old", ok = true, resourcePath = "wrong.efx", revision = 4 })
    equal(#c.binding_applies, 0)
    c:respond({ ok = true, resourcePath = "motionvfxlive/new/rev4.efx", revision = 4 })
    equal(#c.binding_applies, 1); equal(c.binding_applies[1].original, original)
    equal(c.binding_applies[1].generated, "motionvfxlive/new/rev4.efx")
    equal(c.module.state.apply_requested, true); equal(#c.runtime_queue, 0); equal(c.instance, nil)
    c:lock(); equal(c.module.state.apply_requested, false); equal(c.binding_state.phase, "awaiting_action")
    equal(c.module.state.action_message:find("所有匹配引用", 1, true) ~= nil, true)
    equal(c.module.state.action_message:find("验证", 1, true) ~= nil, true)
end)

test("ownership revoked after document selection blocks publishing and preserves the edited document", function()
    local c = environment(); open_captured(c, "effect/selected.efx", "selected.efx.1")
    c.module.refresh_action_state()
    local selected = { kind = "entry", index = 2, attributeIndex = 4 }
    local document = c.module.state.document; document.dirty = true
    c.module.state.selected = selected; c.module.state.values["SizeScalar.s"] = "1"
    local request_id = c.files["MotionVfxLive/request.json"].id
    c.capture_state = { resources = { { path = "effect/selected.efx", localConfirmed = false } }, events = {} }
    equal(c.module.state.capture_snapshot.resources[1].localConfirmed, true, "retained selection snapshot is stale")
    equal(c.module.publish_document(true), false)
    equal(c.files["MotionVfxLive/request.json"].id, request_id); equal(c.module.state.pending, nil)
    equal(c.module.state.document, document); equal(document.dirty, true); equal(c.module.state.selected, selected)
    equal(c.module.state.values["SizeScalar.s"], "1"); equal(c.module.state.target_resource_path, "effect/selected.efx")
    equal(c.module.state.action_message:find("重新记录角色动作", 1, true) ~= nil, true)
    equal(c.module.state.apply_requested, false); equal(#c.binding_applies, 0); equal(#c.runtime_queue, 0)
end)

test("removed records unrelated local paths and nonboolean confirmation cannot authorize an old target", function()
    for _, resources in ipairs({ {}, { { path = "effect/unrelated.efx", localConfirmed = true } },
        { { path = "effect/selected.efx", localConfirmed = 1 } },
        { { path = "effect/selected.efx", canonicalPath = "effect/different.efx", localConfirmed = true } } }) do
        local c = environment(); open_captured(c, "effect/selected.efx", "selected.efx.1")
        c.capture_state = { resources = resources, events = {} }
        equal(c.module.publish_document(true), false); equal(c.module.state.pending, nil)
        equal(c.module.state.document.source, "selected.efx.1"); equal(c.module.state.apply_requested, false)
        equal(#c.binding_applies, 0); equal(#c.runtime_queue, 0)
    end
end)

test("ownership revoked while asynchronous publication is in flight prevents native apply", function()
    local c = environment(); open_captured(c, "effect/selected.efx", "selected.efx.1")
    equal(c.module.publish_document(true), true); equal(c.module.state.apply_requested, true)
    local document = c.module.state.document
    c.module.state.module_frames = 0; c.module.state.last_diagnostic = c.time + 100
    c.capture_state = { resources = { { path = "effect/selected.efx", localConfirmed = false } }, events = {} }
    equal(c.module.state.capture_snapshot.resources[1].localConfirmed, true)
    c:respond({ ok = true, resourcePath = "generated/revoked.efx", revision = 4 })
    equal(c.module.state.pending, nil); equal(c.module.state.apply_requested, false)
    equal(c.module.state.document, document); equal(c.module.state.published_path, "generated/revoked.efx")
    equal(c.module.state.action_message:find("重新记录角色动作", 1, true) ~= nil, true)
    equal(#c.binding_applies, 0); equal(#c.binding_queue, 0); equal(#c.runtime_queue, 0)
    equal(c.capture_starts, nil); equal(c.module.state.verification, nil)
end)

test("failed current capture reads reject historical ownership before and after asynchronous publish", function()
    for _, fail_after_publish in ipairs({ false, true }) do
        local c = environment(); open_captured(c, "effect/selected.efx", "selected.efx.1")
        c.module.refresh_action_state()
        if fail_after_publish then equal(c.module.publish_document(true), true) end
        c.capture.snapshot = function() error("capture snapshot unavailable") end
        if fail_after_publish then c:respond({ ok = true, resourcePath = "generated/unconfirmed.efx", revision = 4 })
        else equal(c.module.publish_document(true), false) end
        equal(c.module.state.capture_snapshot.resources[1].localConfirmed, true, "old cached evidence remains for display only")
        equal(c.module.state.apply_requested, false); equal(c.module.state.pending, nil)
        equal(c.module.state.action_message:find("重新记录角色动作", 1, true) ~= nil, true)
        equal(c.module.state.document.source, "selected.efx.1"); equal(#c.binding_applies, 0); equal(#c.runtime_queue, 0)
    end
end)

test("normalized ownership paths permit application only while retained source identity stays unchanged", function()
    local c = environment(); open_captured(c, "effect/selected.efx", "selected.efx.1")
    local source = copy_values(c.module.state.document.gameSource)
    c.capture_state = { sceneEpoch = 1, resources = {
        { path = "effect/selected.efx", localConfirmed = false },
        { path = "generated/previous.efx", canonicalPath = "@/NATIVES/STM/Effect\\Selected.efx.5571972", localConfirmed = true, latestSource = source },
    }, events = {}, recording = false }
    equal(c.module.publish_document(true), true)
    -- The current snapshot uses a raw path when it has no canonical alias.
    c.capture_state = { sceneEpoch = 1, resources = { { path = "@/NATIVES/STM/Effect\\Selected.efx.5571972", localConfirmed = true,
        latestSource = copy_values(source) } }, events = {}, recording = false }
    c:respond({ ok = true, resourcePath = "generated/confirmed.efx", revision = 4 })
    equal(#c.binding_applies, 1); equal(c.binding_applies[1].original, "effect/selected.efx")
    c:lock(); equal(c.module.state.apply_requested, false); equal(c.module.state.verification.phase, "awaiting_action")
    equal(#c.runtime_queue, 0)
end)

test("recapturing the same original resource lets the edited document apply without reopening", function()
    local c = environment(); open_captured(c, "effect/selected.efx", "selected.efx.1")
    local document = c.module.state.document; document.dirty = true
    equal(c.module.publish_document(true), true)
    c.capture_state = { sceneEpoch = 1, resources = {}, events = {}, recording = false }
    c:respond({ ok = true, resourcePath = "generated/rejected.efx", revision = 4 }); equal(#c.binding_applies, 0)
    local new_source = copy_values(document.gameSource); new_source.sourceId = "source-2"; new_source.eventId = "event-2"
    c.capture_state.resources = { { path = "effect/selected.efx", canonicalPath = "effect/selected.efx", localConfirmed = true, latestSource = new_source } }
    equal(c.module.publish_document(true), true)
    c:respond({ok=true,resourcePath="generated/recaptured.efx",revision=4})
    equal(c.module.state.document, document); equal(document.dirty, true)
    equal(#c.binding_applies, 1)
    equal(#c.runtime_queue, 0)
end)

test("auxiliary publication is independent of revoked ownership and never implicitly plays or applies", function()
    local c = environment(); open_captured(c, "effect/selected.efx", "selected.efx.1")
    c.capture_state = { resources = { { path = "effect/selected.efx", localConfirmed = false } }, events = {} }
    equal(c.module.publish_document(false), true)
    c.capture.snapshot = function() error("capture unavailable after auxiliary publish") end
    c:respond({ ok = true, resourcePath = "generated/auxiliary.efx", revision = 4 })
    equal(c.module.state.published_path, "generated/auxiliary.efx"); equal(c.module.state.apply_requested, false)
    equal(c.module.state.preview_message:find("明确点击播放", 1, true) ~= nil, true)
    equal(#c.binding_applies, 0); equal(#c.runtime_queue, 0); equal(c.instance, nil)
end)

test("game installation read failures never fall back to local candidates", function()
    local c = environment()
    local resource = { path = "effect/original/fire.efx", localConfirmed = true }
    c.capture_state.resources = { resource }
    c.module.resolve_captured_resource(resource)
    c:respond({ ok = false, error = "游戏目录中未找到对应资源", candidates = { { path = "C:/different/fire.efx.1", match = "exact", source = "local_file" } } })
    equal(c.module.state.pending, nil); equal(c.module.state.document, nil)
    equal(c.files["MotionVfxLive/request.json"].action, "open_game_resource")
    equal(c.module.state.action_message, "游戏目录中未找到对应资源")
    c.buttons["选择这个候选文件##mvl_resource_candidate_1"] = true; c:draw("actions")
    equal(c.files["MotionVfxLive/request.json"].path, nil)
    equal(c.module.state.target_resource_path, nil)
end)

test("action resource selection preserves dirty values and rejects unavailable or stale identities", function()
    local c = environment(); local state = c.module.state
    local resource = { path = "effect/current.efx", localConfirmed = true }
    c.capture_state.resources = { resource }
    state.document = { source = "old.efx.1", sourceKind = "local_file", revision = 9, dirty = true }
    state.fields = { { path = "X", value = "1", editable = true } }; state.values.X = "2"
    equal(c.module.resolve_captured_resource(resource), false); equal(state.pending, nil)
    c.buttons["##mvl_confirm_open"] = true; c:draw("actions")
    equal(c.files["MotionVfxLive/request.json"].action, "open_game_resource")
    c:respond({ ok = false, error = "unavailable" })
    equal(state.document.revision, 9); equal(state.document.dirty, true); equal(state.values.X, "2")
    c.source_error = "source expired"; c.buttons["##mvl_confirm_open"] = true; c:draw("actions")
    equal(state.pending, nil); equal(state.document.revision, 9); equal(state.values.X, "2")
    c.source_error = nil; c.capture_state.sceneEpoch = 2
    c.buttons["##mvl_confirm_open"] = true; c:draw("actions")
    equal(state.pending, nil); equal(state.action_message:find("已过期", 1, true) ~= nil, true)
end)

test("successful game file opens with changed provenance clear old drafts and never bind the new backend document", function()
    for _, change in ipairs({ "sourceId", "sceneEpoch", "actualHolderIdentity", "actualPath", "responseIdentity" }) do
        local c = environment(); local state = c.module.state
        local resource = { path = "effect/current.efx", localConfirmed = true }
        c.capture_state.resources = { resource }
        resource.latestSource = copy_values(c.capture.get_resource_source(resource.path))
        state.document = { source = "old.efx.1", sourceKind = "local_file", revision = 9, dirty = true }
        state.selected = { kind = "attribute", index = 2, attributeIndex = 3 }
        state.fields = { { path = "X", value = "1", editable = true } }; state.values.X = "old draft"
        state.curves = { { name = "old curve" } }; state.expression_curves = { { name = "old expression" } }
        state.curve_selection = { curve = 1 }; state.curve_baseline = { curve_value = "old" }
        state.curve_value = "draft"; state.target_resource_path = "effect/previous.efx"
        state.target_document_source = "old.efx.1"
        equal(c.module.resolve_captured_resource(resource), false)
        c.buttons["##mvl_confirm_open"] = true; c:draw("actions")
        local requested = copy_values(c.files["MotionVfxLive/request.json"].sourceDescriptor)
        local new_document = { source = "C:/editor/game-files/source-1/current.efx.5571972", sourceKind = "game_files",
            revision = 10, gameSource = copy_values(requested), dirty = false }
        if change == "sceneEpoch" then
            c.capture_state.sceneEpoch = 2; resource.latestSource.sceneEpoch = 2
        elseif change == "responseIdentity" then new_document.gameSource.actualHolderIdentity = "9999"
        else resource.latestSource[change] = change == "actualPath" and "generated/replaced.efx" or "9999" end
        c:respond({ ok = true, document = new_document })
        equal(state.document, new_document, "successful backend open has already switched its document")
        equal(state.selected, nil); equal(next(state.fields), nil); equal(next(state.values), nil)
        equal(next(state.curves), nil); equal(next(state.expression_curves), nil)
        equal(state.curve_selection, nil); equal(state.curve_baseline, nil)
        equal(state.target_resource_path, nil); equal(state.target_document_source, nil)
        equal(state.open_confirmation, nil); equal(state.pending, nil)
        equal(state.action_message:find("未绑定角色资源", 1, true) ~= nil, true)
        if change ~= "responseIdentity" then equal(state.action_message:find("后台文档已切换", 1, true) ~= nil, true) end
        equal(c.module.publish_document(true), false); equal(#c.binding_applies, 0)
    end
end)

test("captured action identity changes fail closed before publishing and after asynchronous publication", function()
    for _, after_request in ipairs({ false, true }) do
        for _, change in ipairs({ "sceneEpoch", "actualPath", "sharedTargetPath", "missingEpoch", "missingDocumentSource", "missingAPI", "throwingAPI" }) do
            local c = environment(); open_captured(c, "effect/selected.efx", "C:/editor/game-files/source-1/selected.efx.5571972")
            local state = c.module.state; local document = state.document; document.dirty = true
            local selected = { kind = "attribute", index = 1, attributeIndex = 2 }; state.selected = selected
            local observed = c.capture_state.resources[1].latestSource
            if after_request then equal(c.module.publish_document(true), true) end
            if change == "sceneEpoch" then c.capture_state.sceneEpoch = 2; observed.sceneEpoch = 2
            elseif change == "missingEpoch" then c.capture_state.sceneEpoch = nil; c.capture_state.actor.sceneEpoch = nil
            elseif change == "missingDocumentSource" then document.gameSource = nil
            elseif change == "missingAPI" then c.capture.get_resource_source = nil
            elseif change == "throwingAPI" then c.capture.get_resource_source = function() error("source unavailable") end
            else observed[change] = change == "actualPath" and "generated/new.efx" or
                    change == "sharedTargetPath" and "effect/other.efx" or "9999" end
            if after_request then c:respond({ ok = true, resourcePath = "generated/rejected.efx", revision = 4 })
            else equal(c.module.publish_document(true), false) end
            equal(#c.binding_applies, 0); equal(#c.runtime_queue, 0)
            equal(state.pending, nil); equal(state.apply_requested, false)
            equal(state.document, document); equal(document.dirty, true); equal(state.selected, selected)
            equal(state.action_message:find("重新选择动作资源", 1, true) ~= nil, true)
        end
    end
end)

test("in-flight game file requests cannot be replaced by confirmation buttons", function()
    local c = environment(); local state = c.module.state
    local resource = { path = "effect/current.efx", localConfirmed = true }; c.capture_state.resources = { resource }
    state.document = { source = "old.efx.1", revision = 9, dirty = true }
    equal(c.module.resolve_captured_resource(resource), false)
    c.buttons["##mvl_confirm_open"] = true; c:draw("actions")
    local pending = state.pending
    c.buttons["##mvl_confirm_open"] = true; c:draw("actions")
    equal(state.pending, pending)
    state.open_confirmation = { path = "C:/other.efx.1" }
    c.buttons["##mvl_confirm_open"] = true; c:draw("actions")
    equal(state.pending, pending); equal(c.files["MotionVfxLive/request.json"].action, "open_game_resource")
end)

test("legacy or local documents cannot apply to real resources", function()
    for _, kind in ipairs({ "local_file", "legacy" }) do
        local c = environment(); local state = c.module.state
        state.document = { source = "same.efx.1", revision = 3, sourceKind = kind == "local_file" and kind or nil }
        state.target_document_source = state.document.source; state.target_resource_path = "effect/same.efx"
        c.capture_state.resources = { { path = state.target_resource_path, localConfirmed = true } }
        equal(c.module.publish_document(true), false); equal(state.pending, nil); equal(#c.binding_applies, 0)
        equal(state.action_message:find("请从角色动作列表选择", 1, true) ~= nil, true)
    end
end)

test("applied version verification requires fresh local event paths rather than aggregate counts", function()
    local c = environment(); open_captured(c, "effect/source.efx", "source.efx.1")
    c.capture_state.events = { { id = "old", localConfirmed = true, created = { { path = "generated/new.efx" } } } }
    c.capture_state.resources = { { canonicalPath = "effect/source.efx", localConfirmed = true,
        createdCount = 300, originalPaths = { "generated/new.efx" }, latestSource = copy_values(c.module.state.document.gameSource) } }
    c.module.publish_document(true); c:respond({ ok = true, resourcePath = "generated/new.efx", revision = 4 }); c:lock()
    equal(c.capture_state.recording, true); equal(c.capture_starts, 1); equal(c.module.state.verification.phase, "awaiting_action")
    c.module.refresh_action_state(); equal(c.module.state.verification.phase, "awaiting_action")
    c.capture_state.events[2] = { id = "unknown", localConfirmed = false, created = { { path = "generated/new.efx" } } }
    c.module.refresh_action_state(); equal(c.module.state.verification.phase, "awaiting_action")
    c.capture_state.events[3] = { id = "request", localConfirmed = true,
        requested = { { path = "@/NATIVES/STM/Generated\\New.efx.1769672", canonicalPath = "effect/source.efx" } } }
    c.module.refresh_action_state(); equal(c.module.state.verification.phase, "requested")
    equal(c.module.state.verification.message:find("尚未确认实例", 1, true) ~= nil, true)
    c.module.capture_command("stop"); equal(c.module.state.verification.message:find("再次开始记录", 1, true) ~= nil, true)
    c.module.refresh_action_state(); equal(c.module.state.verification.message:find("再次开始记录", 1, true) ~= nil, true)
    c.module.capture_command("start")
    c.capture_state.events[4] = { id = "create", localConfirmed = true, motion = { motionId = 7 }, motionAssociation = "direct_motion_request",
        created = { { path = "generated/new.efx", canonicalPath = "effect/source.efx", playerIdentity = "player2" } } }
    c.module.refresh_action_state(); equal(c.module.state.verification.phase, "created")
    equal(c.module.state.verification.eventId, "create"); equal(c.module.state.verification.motion.motionId, 7)
    equal(c.module.state.verification.message:find("画面效果请观察", 1, true) ~= nil, true)
    c.time = c.time + 3; c.module.diagnostics(); local actual = c.files["MotionVfxLive/ui-status.json"].verification
    equal(actual.phase, "created"); equal(actual.observedPath, "generated/new.efx"); equal(actual.baseline, nil)
    c.module.stop_and_restore(); equal(c.module.state.verification, nil)
end)

test("later application verifies only its own generated revision and normalized original selection", function()
    local c = environment()
    local resource = { path = "generated/old.efx", canonicalPath = "effect/original.efx", localConfirmed = true }
    c.capture_state.resources = { resource }
    c.module.resolve_captured_resource(resource)
    equal(c.files["MotionVfxLive/request.json"].sourceDescriptor.actualPath, "generated/old.efx")
    equal(c.files["MotionVfxLive/request.json"].sourceDescriptor.sharedTargetPath, "effect/original.efx")
    c:respond({ ok = true, document = { source = "game-files-observation-1", sourceKind = "game_files", revision = 1,
        gameSource = copy_values(c.files["MotionVfxLive/request.json"].sourceDescriptor) } })
    c.module.publish_document(true); c:respond({ ok = true, resourcePath = "generated/one.efx", revision = 1 }); c:lock()
    c.module.publish_document(true); c:respond({ ok = true, resourcePath = "generated/two.efx", revision = 1 }); c:lock()
    c.capture_state.events = { { id = "prior", localConfirmed = true, created = { { path = "generated/one.efx" } } } }
    c.module.refresh_action_state(); equal(c.module.state.verification.phase, "awaiting_action")
    equal(c.module.state.verification.generatedPath, "generated/two.efx")
    c.capture_state.events[2] = { id = "current", localConfirmed = true, created = { { path = "generated/two.efx" } } }
    c.module.refresh_action_state(); equal(c.module.state.verification.phase, "created")
    equal(#c.runtime_queue, 0); equal(c.binding_applies[2].original, "effect/original.efx")
end)

test("unconfirmed resource cannot become a action editing source", function()
    local c = environment()
    c.module.resolve_captured_resource({ path = "effect/unknown.efx", localConfirmed = false })
    equal(c.module.state.pending, nil)
    equal(c.module.state.action_message:find("未确认", 1, true) ~= nil, true)
    equal(c.module.state.target_resource_path, nil)
    equal(c.module.publish_document(true), false); equal(#c.binding_applies, 0); equal(#c.runtime_queue, 0)
end)

test("opening a different resource preserves dirty edits until explicit confirmation", function()
    local c = environment(); local state = c.module.state
    state.document = { source = "C:/old.efx", revision = 9, dirty = true }
    equal(c.module.request_open_document("C:/new.efx", "effect/new.efx"), false)
    equal(state.pending, nil); equal(state.document.revision, 9)
    c.buttons["取消切换##mvl_cancel_open"] = true; c:draw("actions")
    equal(state.open_confirmation, nil); equal(state.document.source, "C:/old.efx")
    state.document.dirty = false; state.fields = { { path = "X", value = "1", editable = true } }; state.values.X = "draft"
    equal(c.module.request_open_document("C:/new.efx", "effect/new.efx"), false)
    c.buttons["确认切换文档（放弃当前未保存编辑）##mvl_confirm_open"] = true; c:draw("actions")
    equal(c.files["MotionVfxLive/request.json"].action, "open")
    c:respond({ ok = false, error = "parse failed" }); equal(state.document.source, "C:/old.efx"); equal(state.values.X, "draft")
    c.buttons["确认切换文档（放弃当前未保存编辑）##mvl_confirm_open"] = true; c:draw("actions")
    c:respond({ ok = true, document = { source = "C:/new.efx", revision = 1 } })
    equal(state.target_resource_path, nil); equal(state.open_confirmation, nil); equal(next(state.values), nil)
end)

test("same local path cannot be promoted to an action source, preserving drafts", function()
    local c = environment(); local state = c.module.state
    state.document = { source = "same.efx", revision = 6, dirty = true }; state.values.X = "draft"
    equal(c.module.request_open_document("same.efx", "effect/same.efx"), true)
    equal(state.target_resource_path, nil); equal(state.values.X, "draft"); equal(state.document.revision, 6)
    equal(state.pending, nil)
    c.module.request_open_document("same.efx", nil); equal(state.target_resource_path, nil)
end)

test("publish rejects drafts, stale revisions and failed resource bindings without spawning", function()
    local c = environment(); open_captured(c, "effect/source.efx", "source.efx.1")
    c.module.state.fields = { { path = "X", editable = true, value = "1" } }; c.module.state.values.X = "2"
    equal(c.module.publish_document(true), false); equal(c.module.state.pending, nil)
    c.module.state.values.X = "1"; equal(c.module.publish_document(true), true)
    c:respond({ ok = true, resourcePath = "new.efx", revision = 3 })
    equal(#c.binding_applies, 0); equal(c.module.state.apply_requested, false)
    c.binding_reject = true; c.module.publish_document(true)
    c:respond({ ok = true, resourcePath = "new.efx", revision = 4 })
    equal(c.module.state.apply_requested, false); equal(#c.binding_applies, 0); equal(#c.runtime_queue, 0)
    equal(c.module.state.action_message:find("unsupported resource holder", 1, true) ~= nil, true)
end)

test("restoration cancels an in-flight publish before it can bind a new resource", function()
    local c = environment(); open_captured(c, "effect/source.efx", "source.efx.1")
    c.module.publish_document(true); equal(c.module.restore_bindings(), true)
    equal(c.module.state.bindings_restore_pending, true); equal(c.binding_restores, nil)
    c:respond({ ok = true, resourcePath = "new.efx", revision = 4 })
    equal(#c.binding_applies, 0); equal(c.binding_restores, 1)
    equal(c.module.reset(), false); equal(c.module.active, true)
    c:lock(); equal(c.module.state.bindings_restore_pending, false); equal(c.module.reset(), true)
end)

test("restore defers until a busy native apply completes and failed restore blocks reset", function()
    local c = environment(); open_captured(c, "effect/source.efx", "source.efx.1")
    c.module.publish_document(true); c:respond({ ok = true, resourcePath = "new.efx", revision = 4 })
    c.module.stop_and_restore(); equal(c.module.state.bindings_restore_pending, true); equal(c.binding_restores, nil)
    c:lock(); equal(c.binding_state.appliedCount, 1); c:frame(); equal(c.binding_restores, 1)
    c.binding_restore_fail = true; c:lock()
    equal(c.module.state.bindings_restore_pending, false); equal(type(c.module.state.bindings_restore_failed), "string")
    equal(c.module.reset(), false); equal(c.module.active, true); equal(c.env.MotionVfxActionBindings, c.bindings)
    c.binding_restore_fail = false; c.module.restore_bindings(); c:lock()
    equal(c.module.reset(), true); equal(c.env.MotionVfxActionBindings, c.bindings)
end)

test("archived restoration requirements with no active slots still block controlled reset",function()
    local c=environment();c.binding_state.appliedCount=0;c.binding_state.requiresRestoreBeforeReset=true
    equal(c.module.reset(),false);equal(c.module.active,true);equal(c.env.MotionVfxLive,c.module)
    equal(c.binding_state.requiresRestoreBeforeReset,true)
end)

test("stop and restore requests archived binding recovery even when active slot count is zero",function()
    local c=environment();c.binding_state.appliedCount=0;c.binding_state.requiresRestoreBeforeReset=true
    c.binding_restore_fail=true;c.module.stop_and_restore()
    equal(c.binding_restores,1);equal(c.module.state.bindings_restore_pending,true)
    c:lock();c:frame();equal(c.binding_state.appliedCount,0);equal(c.binding_state.requiresRestoreBeforeReset,true)
    equal(c.module.state.bindings_restore_failed~=nil,true);equal(c.module.reset(),false);equal(c.module.active,true)
end)

test("clear cannot discard captured references until resource edits are restored", function()
    local c = environment(); c.binding_state.appliedCount = 1
    equal(c.module.capture_command("clear"), false); equal(c.capture_clears, nil)
    c.binding_state.appliedCount = 0; c.binding_state.busy = true
    equal(c.module.capture_command("clear"), false)
    c.binding_state.busy = false; equal(c.module.capture_command("clear"), true); equal(c.capture_clears, 1)
end)

test("timed-out publish recovery cancels pending application and never spawns", function()
    local c = environment(); open_captured(c, "effect/source.efx", "source.efx.1")
    local document = copy_values(c.module.state.document)
    c.module.publish_document(true); local old_id = c.module.state.pending.id
    c.time = c.time + 20; c:frame(6); equal(c.module.state.pending.timeout, true)
    equal(c.module.synchronize(), true); equal(c.module.state.apply_requested, false)
    c:respond({ id = old_id, ok = true, resourcePath = "late.efx", revision = 4 })
    equal(#c.binding_applies, 0); equal(#c.runtime_queue, 0)
    c:respond({ ok = true, document = document })
    equal(c.module.state.target_resource_path, "effect/source.efx")
end)

test("all pages expose scene time controls and diagnostics include action evidence summaries", function()
    local c = environment()
    c.buttons["暂停当前场景##mvl_scene_pause"] = true; c:draw("actions"); c:frame(); equal(c.speed,0)
    c.buttons["0.25×##mvl_scene_rate_0.25"] = true; c:draw("edit"); c:frame(); equal(c.speed,0)
    equal(c.module.state.scene_pause.resume_speed,0.25)
    c.capture_state.resources = { { path = "one.efx" } }; c.capture_state.events = { { unresolved = true } }
    c.capture_state.counters.unresolved = 1; c.capture_state.errors = { "one hook unavailable" }
    c.time = c.time + 3; c.module.diagnostics(); local output = c.files["MotionVfxLive/ui-status.json"]
    equal(output.captureResourceCount, 1); equal(output.captureEventCount, 1); equal(output.captureCounters.unresolved, 1)
    equal(output.captureErrors[1], "one hook unavailable"); equal(output.bindings.phase, "idle")
    equal(output.captureResources[1].path, "one.efx"); equal(output.captureRecentEvents[1].unresolved, true)
    equal(output.captureResourcesTruncated, false); equal(output.captureEventsTruncated, false)
    equal(output.captureInstalledHooks, 3); equal(output.captureRequestedHooks, 3)
    equal(output.ownedPreview, false); equal(output.version, c.module.version)
    for i = 1, 110 do c.capture_state.resources[i] = { path = "effect/" .. i }; c.capture_state.events[i] = { sequence = i } end
    c.time = c.time + 3; c.module.diagnostics(); output = c.files["MotionVfxLive/ui-status.json"]
    equal(output.captureResourceCount, 110); equal(#output.captureResources, 100); equal(output.captureResourcesTruncated, true)
    equal(output.captureEventCount, 110); equal(#output.captureRecentEvents, 20); equal(output.captureEventsTruncated, true)
    equal(output.captureRecentEvents[1].sequence, 91); equal(output.captureRecentEvents[20].sequence, 110)
end)

if reload_source then
    test("manual reload cannot discard archived binding recovery after a superficially successful callback",function()
        local c=environment();c.binding_state.appliedCount=0;c.binding_state.requiresRestoreBeforeReset=true
        local reload=c:reload();equal(c.binding_restores,1)
        c:lock();c:frame()
        equal(c.module.state.bindings_restore_failed,nil);equal(c.binding_state.busy,false)
        equal(c.binding_state.appliedCount,0);equal(reload.phase,"restoring")
        equal(c.module.active,true);equal(c.reload_count,nil);equal(c.runtime.clients.MotionVfxLive.closed,nil)
        c.time=c.time+21;c:frame()
        equal(reload.phase,"failed");equal(c.module.active,true);equal(c.reload_count,nil)
        equal(c.module.state.reloading,false);equal(c.module.state.window_open,true)
        equal(c.binding_state.requiresRestoreBeforeReset,true)
    end)

    test("manual reload waits for real resource restoration and preserves capture singleton", function()
        local c = environment(); c.binding_state.appliedCount = 1
        c.module.capture_command("start")
        local reload = c:reload(); c:frame()
        equal(reload.phase, "restoring"); equal(c.capture_state.recording, false)
        equal(c.binding_restores, 1); equal(c.module.active, true); equal(c.reload_count, nil)
        equal(c.runtime.clients.MotionVfxLive.closed, nil)
        c:lock(); c:frame(); equal(reload.phase, "stopping")
        c:lock(); c:frame(); equal(reload.phase, "complete")
        equal(c.env.MotionVfxActionCapture, c.capture); equal(c.env.MotionVfxActionBindings, c.bindings)
        equal(c.binding_state.appliedCount, 0)
    end)

    test("manual reload preserves editor when real resource restoration fails", function()
        local c = environment(); c.binding_state.appliedCount = 1; c.binding_restore_fail = true
        local reload = c:reload(); c:lock(); c:frame()
        equal(reload.phase, "failed"); equal(c.module.active, true); equal(c.reload_count, nil)
        equal(c.binding_state.appliedCount, 1); equal(c.runtime.clients.MotionVfxLive.closed, nil)
        equal(c.module.state.reloading, false)
    end)

    test("manual reload waits for restore and owned cleanup without resetting shared state", function()
        local c = environment(); c.module.play("owned.efx"); c:lock()
        c.module.queue_scene("pause", true); c.module.queue_scene("speed", 0.25); c:frame()
        local reload = c:reload()
        equal(reload.phase, "restoring"); equal(c.module.active, true); equal(c.reload_count, nil)
        c:frame(); equal(c.paused, false); equal(c.speed, 0.8); equal(reload.phase, "stopping")
        equal(c.module.active, true); equal(c.reload_count, nil)
        c:lock(); equal(c.instance, nil); c:frame()
        equal(reload.phase, "complete"); equal(reload.active, false); equal(c.module.active, false)
        equal(c.reload_count, 1); equal(c.env.fx_preview_runtime_v2, c.runtime)
        equal(c.env.package.loaded["MotionVfxLive.fx_preview_runtime"], c.runtime)
        equal(c.env.package.loaded.Unrelated, "keep")
        equal(c.replacement_polls, 1); equal(c.readonly_syncs, 1)
        c:frame(); equal(c.reload_count, 1)
    end)

    test("manual reload preserves unsubmitted scalar group and legacy curve inputs", function()
        for _, kind in ipairs({ "scalar", "group", "legacy_curve" }) do
            local c = environment(); local state = c.module.state
            if kind == "legacy_curve" then
                c.module.has_drafts = nil; state.curve_baseline = { curve_value = "1" }; state.curve_value = "2"
            else
                state.selected = { kind = kind == "group" and "group" or "entry", index = 0 }
                state.fields = { { path = "name", value = "old", editable = true } }; state.values.name = "unsaved draft"
            end
            local reload = c:reload(); c:frame()
            equal(reload.phase, "failed"); equal(c.module.active, true); equal(state.window_open, true)
            equal(c.reload_count, nil); equal(#c.runtime_queue, 0)
            if kind ~= "legacy_curve" then equal(state.values.name, "unsaved draft") else equal(state.curve_value, "2") end
        end
    end)

    test("manual reload refuses uncertain backend edits and keeps existing UI alive", function()
        local c = environment(); c.module.send("undo"); local reload = c:reload()
        equal(reload.phase, "failed"); equal(reload.active, false); equal(c.module.active, true)
        equal(#c.runtime_queue, 0); equal(c.reload_count, nil); equal(c.module.state.window_open, true)
    end)

    test("manual reload does not replace a scene whose restoration failed", function()
        local c = environment(); c.module.queue_scene("pause", true); c:frame()
        c.ignore_speed_write = true; local reload = c:reload(); c:frame()
        c.time = c.time + 20; c:frame()
        equal(reload.phase, "failed"); equal(c.module.active, true); equal(c.reload_count, nil)
        equal(c.module.state.reloading, false); equal(c.module.state.window_open, true)
        equal(c.runtime.clients.MotionVfxLive.closed, nil)
    end)

    test("manual reload reports failed owner cleanup and preserves old controller", function()
        local c = environment(); c.shutdown_fail = true; local reload = c:reload()
        c:frame(); c:lock(); c:frame()
        equal(reload.phase, "failed"); equal(c.module.active, true); equal(c.reload_count, nil)
        equal(c.env.fx_preview_runtime_v2, c.runtime); equal(c.module.state.window_open, true)
    end)

    test("backend unavailable during post-reload sync does not fail a good reload", function()
        local c = environment(); c.sync_failure = true; local reload = c:reload()
        c:frame(); c:lock(); c:frame()
        equal(reload.phase, "complete"); equal(reload.active, false)
        equal(c.replacement.active, true); equal(c.readonly_syncs, 1)
        equal(c.env.fx_preview_runtime_v2, c.runtime)
    end)

    test("reload compiles replacement before stopping or hiding the old editor", function()
        local c = environment(); c.compile_failure = true
        c.module.play("still-owned.efx"); c:lock()
        local reload = c:reload()
        equal(reload.phase, "failed"); equal(c.module.active, true); equal(c.module.state.window_open, true)
        equal(c.instance.path, "still-owned.efx"); equal(#c.runtime_queue, 0)
        equal(c.runtime.clients.MotionVfxLive.closed, nil); equal(c.reload_count, nil)
    end)

    if real_reload_fixture then
        test("real deployed Lua reloads when package.path does not contain autorun", function()
            local old_path, old_loaded = package.path, package.loaded.MotionVfxLive
            package.path = "./intentionally-missing/?.lua"; package.loaded.MotionVfxLive = nil
            local required = pcall(require, "MotionVfxLive")
            package.path, package.loaded.MotionVfxLive = old_path, old_loaded
            equal(required, false, "ordinary require cannot resolve the deployed file")
            local c = environment(); c.real_reload = true
            local reload = c:reload(); c:frame(); c:lock(); c:frame()
            equal(reload.phase, "complete"); equal(c.module.active, false)
            equal(c.env.MotionVfxLive.version, c.module.version); equal(c.env.MotionVfxLive.active, true)
            equal(c.env.fx_preview_runtime_v2, c.runtime)
            equal(c.env.package.loaded.MotionVfxLive, c.env.MotionVfxLive)
            equal(c.env.package.loaded.Unrelated, "keep")
        end)

        test("real syntax error is rejected before deactivating old UI", function()
            local path = "reframework/autorun/MotionVfxLive.lua"
            local input = assert(io.open(path, "rb")); local original = input:read("*a"); input:close()
            local output = assert(io.open(path, "wb")); output:write("this is invalid Lua !!!"); output:close()
            local c = environment(); c.real_reload = true
            local ok, reload = pcall(c.reload, c)
            output = assert(io.open(path, "wb")); output:write(original); output:close()
            equal(ok, true); equal(reload.phase, "failed"); equal(c.module.active, true)
            equal(c.module.state.window_open, true); equal(#c.runtime_queue, 0)
        end)

        test("real runtime file fallback loads without an autorun search path", function()
            local c = environment(); c.module.reset()
            c.env.fx_preview_runtime_v2 = nil
            c.env.package.loaded["MotionVfxLive.fx_preview_runtime"] = nil
            c.env.loadfile = function(path) return loadfile(path, "t", c.env) end
            c.env.require = require
            local key = "MotionVfxLive.fx_preview_runtime"
            local saved_path, saved_loaded = package.path, package.loaded[key]
            package.path = "./intentionally-missing/?.lua"; package.loaded[key] = nil
            local ok, actual = pcall(function() return assert(loadfile("reframework/autorun/MotionVfxLive.lua", "t", c.env))() end)
            package.path, package.loaded[key] = saved_path, saved_loaded
            equal(ok, true); equal(actual.active, true)
            equal(type(c.env.fx_preview_runtime_v2), "table")
            equal(c.env.fx_preview_runtime_v2 ~= c.runtime, true)
            equal(c.env.package.loaded[key], c.env.fx_preview_runtime_v2)
            equal(c.env.fx_preview_runtime_v2.clients.MotionVfxLive.owner, "MotionVfxLive")
        end)
    end
end

-- Continuous editing keeps the original document provenance, while real
-- actions replace the capture module's latest source ID and retained holder.
do
    local target = "effect/continuous.efx"
    local function observe_revision(c, generated, sequence)
        local source = copy_values(c.module.state.document.gameSource)
        source.sourceId, source.eventId = "new-source-" .. sequence, "new-event-" .. sequence
        source.actualHolderIdentity, source.playerIdentity = tostring(10000 + sequence), tostring(20000 + sequence)
        source.actualPath, source.sharedTargetPath = generated, target
        local resource = c.capture_state.resources[1]
        resource.path, resource.canonicalPath, resource.latestSource = generated, target, source
        c.capture_state.events[#c.capture_state.events + 1] = { id = source.eventId, localConfirmed = true,
            created = { { path = generated, canonicalPath = target, playerIdentity = source.playerIdentity } } }
        c.module.refresh_action_state()
        equal(c.module.state.verification.phase, "created")
        return source
    end
    local function confirmed_first_application()
        local c = environment(); open_captured(c, target, "game-files/continuous.efx.5571972")
        equal(c.module.publish_document(true), true)
        c:respond({ ok = true, resourcePath = "generated/own_1.efx", revision = 4 }); c:lock()
        equal(#c.binding_applies, 1); equal(c.module.state.verification.phase, "awaiting_action")
        observe_revision(c, "generated/own_1.efx", 1)
        equal(c.capture.get_resource_source(target, c.module.state.document.gameSource.sourceId), nil)
        return c
    end

    test("second and third applications accept only the confirmed own revision after real holder changes", function()
        local c = confirmed_first_application()
        local original_source = copy_values(c.module.state.document.gameSource)
        for iteration = 2, 3 do
            local revision, generated = iteration + 3, "generated/own_" .. iteration .. ".efx"
            c.module.state.document.revision, c.module.state.document.dirty = revision, true
            equal(c.module.publish_document(true), true, "apply before new publish response")
            -- Another real action during the asynchronous publish retains yet
            -- another holder of the same verified revision.
            observe_revision(c, "@/NATIVES/STM/GENERATED\\OWN_" .. (iteration - 1) .. ".efx.5571972", iteration * 10)
            c:respond({ ok = true, resourcePath = generated, revision = revision })
            equal(#c.binding_applies, iteration, "apply after new publish response")
            equal(c.binding_applies[iteration].original, target)
            equal(c.binding_applies[iteration].generated, generated)
            c:lock(); equal(c.module.state.verification.phase, "awaiting_action")
            equal(c.module.state.verification.generatedPath, generated)
            observe_revision(c, generated, iteration * 10 + 1)
            for key, value in pairs(original_source) do equal(c.module.state.document.gameSource[key], value, key) end
            equal(c.module.state.apply_requested, false); equal(#c.runtime_queue, 0)
        end
    end)

    test("continuous apply rejects unrelated or unverified latest sources before and after publishing", function()
        local cases = {
            { "another scene", function(c)
                c.capture_state.sceneEpoch, c.capture_state.actor.sceneEpoch = 2, 2
                c.capture_state.resources[1].latestSource.sceneEpoch = 2
            end },
            { "source scene mismatch", function(c) c.capture_state.resources[1].latestSource.sceneEpoch = 2 end },
            { "unconfirmed holder", function(c) c.capture_state.resources[1].latestSource.localConfirmed = false end },
            { "expired holder", function(c) c.capture_state.resources[1].latestSource.availability = "expired" end },
            { "other generated resource", function(c) c.capture_state.resources[1].latestSource.actualPath = "generated/someone_else.efx" end },
            { "other shared target", function(c) c.capture_state.resources[1].latestSource.sharedTargetPath = "effect/other.efx" end },
            { "unverified publication", function(c) c.module.state.verification.phase = "requested" end },
            { "verification cleared", function(c) c.module.state.verification = nil end },
            { "other verified target", function(c) c.module.state.verification.originalPath = "effect/other.efx" end },
            { "other verified revision", function(c) c.module.state.verification.generatedPath = "generated/other.efx" end },
        }
        for _, after_request in ipairs({ false, true }) do
            for _, case in ipairs(cases) do
                local c = confirmed_first_application()
                local original_document = c.module.state.document
                if after_request then equal(c.module.publish_document(true), true, case[1]) end
                case[2](c)
                if after_request then c:respond({ ok = true, resourcePath = "generated/own_2.efx", revision = 4 })
                else equal(c.module.publish_document(true), false, case[1]) end
                equal(#c.binding_applies, 1, case[1]); equal(#c.binding_queue, 0, case[1])
                equal(c.module.state.document, original_document, case[1])
                equal(c.module.state.apply_requested, false, case[1]); equal(#c.runtime_queue, 0, case[1])
            end
        end
    end)

    test("confirmed revision cannot bypass asynchronous document provenance changes", function()
        local c = confirmed_first_application()
        equal(c.module.publish_document(true), true)
        c.module.state.document.gameSource.sourceId = "different-document-source"
        c:respond({ ok = true, resourcePath = "generated/own_2.efx", revision = 4 })
        equal(#c.binding_applies, 1); equal(#c.binding_queue, 0)
        equal(c.module.state.apply_requested, false)
    end)

    test("opening a confirmed own revision still requires the exact asynchronous capture identity", function()
        local c = confirmed_first_application()
        equal(c.module.resolve_captured_resource(c.capture_state.resources[1]), true)
        local requested_source = copy_values(c.files["MotionVfxLive/request.json"].sourceDescriptor)
        observe_revision(c, "generated/own_1.efx", 30)
        c:respond({ ok = true, document = { source = "game-files/new-observation.efx.5571972", sourceKind = "game_files",
            gameSource = requested_source, revision = 1, entries = {}, actions = {}, dirty = false } })
        equal(c.module.state.target_resource_path, nil)
        equal(c.module.state.target_document_source, nil)
        equal(c.module.state.action_message:find("读取期间动作来源已变化", 1, true) ~= nil, true)
        equal(#c.binding_applies, 1)
    end)
end

-- Semantic RGBA groups use the real REFramework Vector4f color API shape.
do
    local components = { "R", "G", "B", "A" }
    local scope = "_colors_entry_0_0"
    local function color_response(red)
        local fields, groups = {}, {}
        for _, path in ipairs({ "Tint", "Edge" }) do
            local values = { path == "Tint" and (red or 255) or 64, 128, 0, 255 }
            local group = { path = path, label = "颜色", storage = "rgba8", scale = 255, hdr = false, editable = true, channels = {} }
            for index, component in ipairs(components) do
                local field_path = path .. "." .. component
                fields[#fields + 1] = { path = field_path, label = component, type = "Byte", value = tostring(values[index]), editable = true }
                group.channels[index] = { component = component, path = field_path, value = values[index] }
            end
            groups[#groups + 1] = group
        end
        fields[#fields + 1] = { path = "Intensity", label = "亮度", type = "Single", value = "12.75", editable = true }
        for _, component in ipairs({ "x", "y", "z", "w" }) do
            fields[#fields + 1] = { path = "Position." .. component, type = "Single", value = "-2.5", editable = true }
        end
        return { ok = true, fields = fields, colorGroups = groups }
    end
    local function color_fixture()
        local c = environment()
        c.module.state.document = { source = "colors", revision = 7, entries = {}, actions = {}, dirty = false }
        equal(c.module.inspect({ kind = "entry", index = 0, attributeIndex = 0 }), true)
        c:respond(color_response())
        c.color_draws, c.picker_changes, c.vector_calls = {}, {}, {}
        c.env.Vector4f = { new = function(r, g, b, a)
            for _, value in ipairs({ r, g, b, a }) do equal(math.type(value), "float", "Vector4f float component") end
            local vector = { x = r, y = g, z = b, w = a, is_vector = true }
            c.vector_calls[#c.vector_calls + 1] = vector
            return vector
        end }
        c.env.imgui.color_edit4 = function(label, value, flags)
            equal(value.is_vector, true, "color_edit4 receives constructed Vector4f")
            c.color_draws[#c.color_draws + 1] = { label = label, value = value, flags = flags }
            local next_value = c.picker_changes[label]; c.picker_changes[label] = nil
            if next_value then return true, next_value end
            return false, value
        end
        return c
    end
    local function color_inputs(c)
        local result = {}
        for _, input in ipairs(c.drawn_inputs) do
            if input.label:find("##mvl_color_value_", 1, true) then result[#result + 1] = input end
        end
        return result
    end
    local function has_generic_input(c, path)
        for _, label in ipairs(c.drawn_fields) do if label == "##mvl_value_" .. path then return true end end
        return false
    end

    test("RGBA groups show one color picker above four raw channels without duplicating ordinary fields", function()
        local c = color_fixture(); local prior = #c.writes
        c:draw(); equal(c.module.state.ui_error, nil)
        equal(#c.color_draws, 2); equal(#color_inputs(c), 8)
        equal(c.color_draws[1].value.x, 1.0); equal(c.color_draws[1].value.y, 128 / 255.0)
        equal(c.color_draws[1].value.z, 0.0); equal(c.color_draws[1].value.w, 1.0)
        equal(c.color_draws[1].flags & 32, 32, "NoInputs avoids duplicate channel editors")
        equal(c.color_draws[1].flags & 16777216, 16777216, "float picker values")
        for _, path in ipairs({ "Tint", "Edge" }) do
            for _, component in ipairs(components) do equal(has_generic_input(c, path .. "." .. component), false) end
        end
        equal(has_generic_input(c, "Intensity"), true); equal(has_generic_input(c, "Position.w"), true)
        equal(c.module.state.values.Intensity, "12.75"); equal(c.module.state.values["Position.x"], "-2.5")
        equal(#c.writes, prior); equal(#c.runtime_queue, 0); equal(#c.binding_applies, 0)
    end)

    test("color picker and raw RGBA inputs share drafts while preserving unrelated HDR values", function()
        local c = color_fixture(); local state = c.module.state; local prior = #c.writes
        c.picker_changes["颜色##mvl_color_picker_Tint" .. scope] = { x = 0.2, y = 0.4, z = 0.6, w = 0.8 }
        c:draw()
        for index, value in ipairs({ "51", "102", "153", "204" }) do equal(state.values["Tint." .. components[index]], value) end
        equal(state.values["Edge.R"], "64"); equal(state.values.Intensity, "12.75")
        equal(#c.writes, prior); equal(c.module.publish_document(false), false)
        equal(c.module.inspect({ kind = "entry", index = 2 }), false)
        c.inputs["R##mvl_color_value_Tint.R" .. scope] = "17"; c:draw()
        c.color_draws = {}; c:draw(); equal(c.color_draws[1].value.x, 17 / 255.0)
        equal(state.values["Tint.R"], "17"); equal(state.values.Intensity, "12.75")
        state.field_filter = "Intensity"; c.buttons["保存副本##mvl_save"] = true; c:draw()
        equal(#c.writes, prior); equal(state.values["Tint.R"], "17")
    end)

    test("RGBA group submission is one exact atomic request and retains other groups and scalar drafts", function()
        local c = color_fixture(); local state = c.module.state
        state.values["Tint.R"], state.values["Edge.G"], state.values.Intensity = "12", "99", "42.5"
        local prior = #c.writes
        c.buttons["提交整组颜色##mvl_color_set_Tint" .. scope] = true; c:draw()
        local request = c.files["MotionVfxLive/request.json"]
        equal(request.action, "set_color"); equal(request.path, "Tint"); equal(request.revision, 7)
        equal(request.target.kind, "entry"); equal(request.target.index, 0); equal(request.target.attributeIndex, 0)
        for index, value in ipairs({ 12, 128, 0, 255 }) do equal(request.values[index], value) end
        local commands = 0
        for index = prior + 1, #c.writes do if c.writes[index].path == "MotionVfxLive/request.json" then commands = commands + 1 end end
        equal(commands, 1); equal(state.values["Tint.R"], "12")
        c:respond({ ok = true, revision = 8, document = { source = "colors", revision = 8, dirty = true, entries = {}, actions = {} } })
        equal(c.files["MotionVfxLive/request.json"].action, "inspect")
        c:respond(color_response(12))
        equal(state.fields[1].value, "12"); equal(state.values["Tint.R"], "12")
        equal(state.values["Edge.G"], "99"); equal(state.values.Intensity, "42.5")
        state.field_filter, state.field_modified_only = "Tint.R", true
        c.drawn_inputs = {}; c.color_draws = {}; c:draw(); equal(#color_inputs(c), 0); equal(#c.color_draws, 0)
    end)

    test("failed RGBA submission preserves all four inputs and invalid byte values never submit", function()
        local c = color_fixture(); local state = c.module.state
        state.values["Tint.R"], state.values["Tint.G"], state.values["Tint.B"], state.values["Tint.A"] = "1", "2", "3", "4"
        c.buttons["提交整组颜色##mvl_color_set_Tint" .. scope] = true; c:draw()
        c:respond({ ok = false, error = "transaction rejected" })
        for index, value in ipairs({ "1", "2", "3", "4" }) do equal(state.values["Tint." .. components[index]], value) end
        equal(state.fields[1].value, "255"); equal(state.pending, nil)
        local prior = c.files["MotionVfxLive/request.json"].id
        for _, invalid in ipairs({ "-1", "256", "0.5", "not-a-number", "nan", "1e400" }) do
            state.values["Tint.R"] = invalid
            c.buttons["提交整组颜色##mvl_color_set_Tint" .. scope] = true; c:draw()
            equal(state.pending, nil); equal(c.files["MotionVfxLive/request.json"].id, prior)
            equal(state.values["Tint.R"], invalid); equal(state.values["Tint.G"], "2")
            equal(state.message:find("0～255", 1, true) ~= nil, true)
        end
    end)

    test("RGBA filtering preserves the entire group and its hidden drafts", function()
        local c = color_fixture(); local state = c.module.state
        state.field_filter = "Tint.G"; c:draw()
        equal(#color_inputs(c), 4); equal(#c.color_draws, 1); equal(#c.drawn_fields, 0)
        state.values["Tint.R"], state.field_modified_only = "7", true
        c.drawn_inputs = {}; c.color_draws = {}; c:draw()
        equal(#color_inputs(c), 4); equal(#c.color_draws, 1)
        state.field_filter = "Intensity"; c.drawn_inputs = {}; c.color_draws = {}; c:draw()
        equal(#color_inputs(c), 0); equal(state.values["Tint.R"], "7")
        state.field_filter = "Tint.A"; c.buttons["还原颜色输入##mvl_color_revert_Tint" .. scope] = true; c:draw()
        equal(state.values["Tint.R"], "255"); equal(state.values.Intensity, "12.75")
        equal(state.pending, nil)
    end)

    test("unavailable or incompatible color APIs retain usable raw RGBA inputs without UI failure", function()
        for _, mode in ipairs({ "missing-vector", "missing-picker", "throwing-vector", "throwing-picker", "invalid-result" }) do
            local c = color_fixture()
            if mode == "missing-vector" then c.env.Vector4f = nil
            elseif mode == "missing-picker" then c.env.imgui.color_edit4 = false
            elseif mode == "throwing-vector" then c.env.Vector4f.new = function() error("unavailable constructor") end
            elseif mode == "throwing-picker" then c.env.imgui.color_edit4 = function() error("unavailable color API") end
            else c.env.imgui.color_edit4 = function() return true, { x = 0.2, y = 0.3, z = math.huge, w = 1 } end end
            c:draw(); equal(c.module.state.ui_error, nil, mode); equal(#color_inputs(c), 8, mode)
            equal(c.module.state.values["Tint.R"], "255", mode); equal(c.module.state.values["Tint.G"], "128", mode)
            c.inputs["A##mvl_color_value_Tint.A" .. scope] = "42"; c:draw()
            equal(c.module.state.values["Tint.A"], "42", mode)
        end
    end)

    test("older or malformed color metadata never hides ordinary fields or guesses Vector4 colors", function()
        local c = color_fixture(); local state = c.module.state
        table.remove(state.color_groups[1].channels, 4)
        c:draw(); equal(#c.color_draws, 1); equal(#color_inputs(c), 4)
        equal(has_generic_input(c, "Tint.R"), true); equal(has_generic_input(c, "Tint.A"), true)
        equal(has_generic_input(c, "Position.x"), true)
        equal(c.module.inspect({ kind = "entry", index = 0, attributeIndex = 0 }), true)
        local response = color_response(); response.colorGroups = nil; c:respond(response)
        equal(#state.color_groups, 0)
        c.drawn_fields = {}; c.color_draws = {}; c.drawn_inputs = {}; c:draw()
        equal(#c.color_draws, 0); equal(#color_inputs(c), 0)
        equal(has_generic_input(c, "Tint.R"), true); equal(has_generic_input(c, "Edge.A"), true)
    end)
end

-- Real bridge source + mocked game/backend. AI never writes the backend mailbox
-- itself: the main editor performs all dispatch and consumes the response.
do
    local ai_request, ai_response, ai_journal = "MotionVfxLive/ai-request.json", "MotionVfxLive/ai-response.json", "MotionVfxLive/ai-journal.json"
    local function request(c, id, action, arguments, expiry)
        c.files[ai_request] = { protocol = 1, id = id, action = action, arguments = arguments or {}, expiresAtUnixSeconds = expiry or c.time + 90 }
        c:frame()
    end
    local function response(c, id)
        local result = c.files[ai_response]
        return result and result.id == id and result or nil
    end
    local function doc(c, revision)
        c.module.state.document = { source = "ai-document", revision = revision or 3, dirty = false, entries = {}, actions = {} }
    end
    local function backend_count(c)
        local count = 0
        for _, write in ipairs(c.writes) do if write.path == "MotionVfxLive/request.json" then count = count + 1 end end
        return count
    end
    local target = { kind = "entry", index = 0, attributeIndex = 1 }

    test("AI status diagnostics and confirmed effect listing are read-only with the documented protocol", function()
        local c = environment(); doc(c)
        c.capture_state.resources = { { path = "effect/yes.efx", localConfirmed = true }, { path = "effect/no.efx", localConfirmed = false } }
        request(c, "status-1", "status")
        local result = response(c, "status-1"); equal(result.protocol, 1); equal(result.ok, true)
        equal(result.result.document.revision, 3); equal(result.result.hasDrafts, false)
        request(c, "list-1", "list_effects"); equal(#response(c, "list-1").result.resources, 1)
        request(c, "list-2", "list_effects", { includeUnconfirmed = true }); equal(#response(c, "list-2").result.resources, 2)
        equal(backend_count(c), 0); equal(#c.binding_applies, 0); equal(#c.scene_writes, 0); equal(#c.runtime_queue, 0)
        c.time = c.time + 3; c.module.diagnostics()
        local diagnostic = c.files["MotionVfxLive/ui-status.json"]
        equal(diagnostic.aiProtocol, 1); equal(diagnostic.aiReady, true); equal(diagnostic.aiBusy, false); equal(diagnostic.hasDrafts, false)
    end)

    test("AI rejects expired unknown malformed and stale requests without invoking backend or SDK", function()
        local c = environment(); doc(c)
        request(c, "expired-1", "capture_start", {}, c.time)
        equal(response(c, "expired-1").error.code, "expired"); equal(c.capture_starts, nil)
        request(c, "unsafe-1", "execute_lua", { code = "error('must not execute')" })
        equal(response(c, "unsafe-1").error.code, "unknown_action")
        request(c, "revision-1", "set_field", { target = target, path = "X", value = "2", expectedRevision = 2 })
        equal(response(c, "revision-1").error.code, "stale_revision")
        request(c, "revision-2", "inspect", { target = target })
        equal(response(c, "revision-2").error.code, "stale_revision")
        request(c, "target-1", "inspect", { target = { kind = "sdk_object", index = 0 }, expectedRevision = 3 })
        equal(response(c, "target-1").error.code, "invalid_arguments")
        c.files[ai_request] = { protocol = 2, id = "protocol-2", action = "capture_start", arguments = {}, expiresAtUnixSeconds = c.time + 10 }
        c:frame(); equal(response(c, "protocol-2").error.code, "invalid_request")
        request(c, "expiry-2", "capture_start", {}, math.huge)
        equal(response(c, "expiry-2").error.code, "invalid_request")
        equal(backend_count(c), 0); equal(#c.scene_writes, 0); equal(#c.binding_applies, 0)
    end)

    test("AI document mutations refuse current UI drafts and busy requests without discarding input", function()
        local c = environment(); doc(c); local state = c.module.state
        state.fields = { { path = "Color.R", value = "1", editable = true } }; state.values["Color.R"] = "2"
        request(c, "draft-1", "set_field", { target = target, path = "X", value = "2", expectedRevision = 3 })
        equal(response(c, "draft-1").error.code, "drafts_present"); equal(state.values["Color.R"], "2")
        state.values["Color.R"] = "1"
        equal(c.module.send("inspect", { target = target }), true)
        local original_id = state.pending.id
        request(c, "busy-1", "undo", { expectedRevision = 3 })
        equal(response(c, "busy-1").error.code, "busy"); equal(state.pending.id, original_id)
        equal(backend_count(c), 1); equal(state.values["Color.R"], "1")
    end)

    test("AI field mutation waits for actual backend response and duplicate IDs never repeat the edit", function()
        local c = environment(); doc(c)
        local arguments = { target = target, path = "X", value = "12.5", expectedRevision = 3 }
        request(c, "edit-1", "set_field", arguments)
        equal(response(c, "edit-1"), nil); equal(c.module.state.ai_busy, true)
        local command = c.files["MotionVfxLive/request.json"]
        equal(command.action, "set"); equal(command.path, "X"); equal(command.value, "12.5"); equal(command.revision, 3)
        request(c, "edit-1", "set_field", arguments); equal(backend_count(c), 1)
        c:respond({ ok = true, revision = 4, document = { source = "ai-document", revision = 4, dirty = true } })
        equal(response(c, "edit-1").ok, true); equal(response(c, "edit-1").result.revision, 4); equal(c.module.state.ai_busy, false)
        request(c, "between-1", "status")
        c.module.state.document.revision = 5
        request(c, "edit-1", "set_field", arguments)
        equal(response(c, "edit-1").result.document.revision, 4); equal(backend_count(c), 1)
        request(c, "edit-1", "set_field", { target = target, path = "X", value = "99", expectedRevision = 5 })
        equal(response(c, "edit-1").error.code, "id_conflict"); equal(backend_count(c), 1)
    end)

    test("AI inspect returns final semantic fields colors and curves after UI state updates", function()
        local c = environment(); doc(c)
        request(c, "inspect-1", "inspect", { target = target, expectedRevision = 3 })
        equal(response(c, "inspect-1"), nil)
        c:respond({ ok = true, fields = { { path = "X", editable = true, value = "1" } },
            colorGroups = { { path = "Color" } }, curves = {}, expressionCurves = { { channel = "spawnNum" } } })
        local result = response(c, "inspect-1"); equal(result.ok, true)
        equal(result.result.fields[1].path, "X"); equal(result.result.colorGroups[1].path, "Color")
        equal(result.result.expressionCurves[1].channel, "spawnNum"); equal(result.result.target.attributeIndex, 1)
        equal(c.module.state.values.X, "1")
    end)

    test("AI opens only an exact confirmed captured path and waits for strict source verification", function()
        local c = environment()
        c.capture_state.resources = { { path = "weapon/full/path.efx", canonicalPath = "weapon/full/path.efx", localConfirmed = true } }
        request(c, "open-short", "open_effect", { path = "path.efx" })
        equal(response(c, "open-short").error.code, "effect_not_found")
        request(c, "open-full", "open_effect", { path = "weapon/full/path.efx" })
        equal(response(c, "open-full"), nil)
        local descriptor = copy_values(c.files["MotionVfxLive/request.json"].sourceDescriptor)
        equal(c.files["MotionVfxLive/request.json"].action, "open_game_resource")
        c:respond({ ok = true, document = { source = "installed-game-file", sourceKind = "game_files", gameSource = descriptor, revision = 1, dirty = false } })
        equal(response(c, "open-full").ok, true); equal(response(c, "open-full").result.targetResourcePath, "weapon/full/path.efx")
        c.module.state.document.dirty = true
        request(c, "open-dirty", "open_effect", { path = "weapon/full/path.efx" })
        equal(response(c, "open-dirty").error.code, "unsaved_document"); equal(backend_count(c), 1)
    end)

    test("AI color group rename undo redo and save commands map to the existing backend actions", function()
        local cases = {
            { "set_color", { target = target, path = "Tint", values = { 1, 2, 3, 255 } }, "set_color" },
            { "rename_group", { index = 2, name = "AI 备注" }, "set_group_note" },
            { "set_group_note", { index = 2, note = "中文备注" }, "set_group_note" },
            { "undo", {}, "undo" }, { "redo", {}, "redo" }, { "save_copy", { path = "copy.efx.5571972" }, "save" },
        }
        for index, case in ipairs(cases) do
            local c = environment(); doc(c); case[2].expectedRevision = 3
            request(c, "command-" .. index, case[1], case[2])
            equal(response(c, "command-" .. index), nil)
            local command = c.files["MotionVfxLive/request.json"]; equal(command.action, case[3]); equal(command.revision, 3)
            if case[1] == "set_color" then equal(command.values[4], 255); equal(command.path, "Tint") end
            if case[1] == "rename_group" or case[1] == "set_group_note" then
                equal(command.groupIndex, 2); equal(command.note, case[2].note or case[2].name); equal(command.name, nil)
            end
            local is_note = case[1] == "rename_group" or case[1] == "set_group_note"
            c:respond({ ok = true, revision = is_note and 3 or 4, outputPath = case[1] == "save_copy" and "copy.efx.5571972" or nil,
                document = { source = "ai-document", revision = is_note and 3 or 4, dirty = not is_note and case[1] ~= "save_copy" } })
            equal(response(c, "command-" .. index).ok, true); equal(backend_count(c), 1)
        end
    end)

    test("AI backend errors do not report accepted edits and preserve current document", function()
        local c = environment(); doc(c)
        request(c, "backend-fail", "set_field", { target = target, path = "X", value = "invalid", expectedRevision = 3 })
        c:respond({ ok = false, error = "Bad field" })
        equal(response(c, "backend-fail").ok, false); equal(response(c, "backend-fail").error.code, "backend_error")
        equal(c.module.state.document.revision, 3); equal(c.module.state.ai_busy, false)
    end)

    test("AI apply waits for native binding completion and reports acceptance without asserting visible rendering", function()
        local c = environment(); open_captured(c, "effect/ai.efx", "ai-game-file")
        request(c, "apply-1", "apply", { expectedRevision = 4 })
        equal(response(c, "apply-1"), nil)
        c:respond({ ok = true, resourcePath = "generated/ai_1.efx", revision = 4 })
        equal(response(c, "apply-1"), nil); equal(#c.binding_applies, 1)
        c:lock(); c:frame()
        local result = response(c, "apply-1"); equal(result.ok, true); equal(result.result.accepted, true)
        equal(result.result.requiresAction, true); equal(result.result.verification.phase, "awaiting_action")
        equal(result.result.visible, nil); equal(result.result.resourcePath, "generated/ai_1.efx")
        request(c, "apply-1", "apply", { expectedRevision = 4 }); equal(#c.binding_applies, 1)
    end)

    test("AI failed or uncertain apply never claims success and never duplicates the pending publish", function()
        local c = environment(); open_captured(c, "effect/ai.efx", "ai-game-file")
        request(c, "apply-timeout", "apply", { expectedRevision = 4 })
        local count = backend_count(c)
        c.time = c.time + 121; c:frame()
        equal(response(c, "apply-timeout").error.code, "operation_uncertain")
        equal(response(c, "apply-timeout").error.uncertain, true); equal(c.module.state.ai_busy, true)
        request(c, "apply-timeout", "apply", { expectedRevision = 4 }); equal(backend_count(c), count)
        c.binding_fail = true
        c:respond({ ok = true, resourcePath = "generated/ai_fail.efx", revision = 4 }); c:lock(); c:frame()
        equal(response(c, "apply-timeout").ok, false); equal(response(c, "apply-timeout").error.code, "apply_failed")
        equal(c.module.state.ai_busy, false); equal(#c.binding_applies, 1)
    end)

    test("AI restore replies only after resource preview capture and scene cleanup actually completes", function()
        local c = environment()
        c.binding_state.appliedCount, c.capture_state.recording = 2, true
        c.module.queue_scene("pause", true); c:frame(); equal(c.speed, 0.0)
        request(c, "restore-1", "restore")
        equal(response(c, "restore-1"), nil); equal(c.module.state.restore_preview_pending, true)
        c:frame(); equal(response(c, "restore-1"), nil)
        c:lock(); c:frame()
        equal(response(c, "restore-1").ok, true); equal(response(c, "restore-1").result.restored, true)
        equal(c.binding_state.appliedCount, 0); equal(c.capture_state.recording, false)
        equal(c.module.state.scene_original, nil); equal(c.speed, 0.8)
    end)

    test("AI restoration failure reports the remaining resource state without claiming cleanup", function()
        local c = environment(); c.binding_state.appliedCount = 2; c.binding_restore_fail = true
        request(c, "restore-fail", "restore"); c:frame(); c:lock(); c:frame()
        equal(response(c, "restore-fail").ok, false); equal(response(c, "restore-fail").error.code, "restore_failed")
        equal(c.binding_state.appliedCount, 2)
    end)

    test("AI scene time and capture controls wait for verified existing UI operations", function()
        local c = environment()
        for index, operation in ipairs({ "pause", "resume", "speed" }) do
            request(c, "scene-" .. index, "scene_time", { operation = operation, speed = 0.25 })
            equal(response(c, "scene-" .. index), nil); c:frame()
            equal(response(c, "scene-" .. index).ok, true)
            if operation == "pause" then equal(c.speed, 0.0) elseif operation == "speed" then equal(c.speed, 0.25) end
        end
        request(c, "capture-1", "capture_start"); equal(response(c, "capture-1").result.recording, true)
        request(c, "capture-2", "capture_stop"); equal(response(c, "capture-2").result.recording, false)
        c.ignore_speed_write = true
        request(c, "scene-fail", "scene_time", { operation = "speed", speed = 0.5 }); c:frame()
        equal(response(c, "scene-fail").error.code, "scene_failed")
    end)

    test("AI concurrent request is rejected while the original remains pending and UI input is locked", function()
        local c = environment(); doc(c)
        c.module.state.selected = target; c.module.state.fields = { { path = "X", value = "1", editable = true } }; c.module.state.values.X = "1"
        request(c, "first-pending", "set_field", { target = target, path = "X", value = "2", expectedRevision = 3 })
        local backend_id = c.module.state.pending.id
        c.inputs["##mvl_value_X"] = "user typing"; c:draw(); equal(c.module.state.values.X, "1")
        request(c, "second-pending", "undo", { expectedRevision = 3 })
        equal(response(c, "second-pending").error.code, "busy"); equal(c.module.state.pending.id, backend_id)
        equal(backend_count(c), 1); equal(c.module.state.ai_busy, true)
        c:respond({ ok = true, document = { source = "ai-document", revision = 4 } })
        equal(response(c, "first-pending").ok, true); equal(c.module.state.ai_busy, false)
    end)

    test("AI startup ignores old commands and persistent IDs prevent replay across script resets", function()
        local old = { protocol = 1, id = "old-startup", action = "capture_start", arguments = {}, expiresAtUnixSeconds = 1000 }
        local c = environment({ [ai_request] = old })
        equal(c.capture_starts, nil); equal(response(c, "old-startup").error.code, "editor_reloaded")
        request(c, "new-after-boot", "status")
        request(c, "old-startup", "capture_start")
        equal(response(c, "old-startup").error.code, "editor_reloaded"); equal(c.capture_starts, nil)
        doc(c)
        request(c, "reset-pending", "set_field", { target = target, path = "X", value = "3", expectedRevision = 3 })
        equal(c.module.reset(true), true)
        equal(response(c, "reset-pending").error.code, "editor_reloaded"); equal(response(c, "reset-pending").error.uncertain, true)
        local next_editor = environment({ [ai_request] = c.files[ai_request], [ai_response] = c.files[ai_response], [ai_journal] = c.files[ai_journal] })
        equal(backend_count(next_editor), 0)
        request(next_editor, "next-session-status", "status")
        request(next_editor, "reset-pending", "set_field", { target = target, path = "X", value = "3", expectedRevision = 3 })
        equal(response(next_editor, "reset-pending").error.code, "already_processed"); equal(backend_count(next_editor), 0)
    end)

    test("AI journal failure disables execution instead of risking duplicate mutations", function()
        local c = environment(); c.write_false = ai_journal
        request(c, "journal-fail", "capture_start")
        equal(response(c, "journal-fail").error.code, "journal_unavailable"); equal(c.capture_starts, nil)
        c.time = c.time + 3; c.module.diagnostics(); equal(c.files["MotionVfxLive/ui-status.json"].aiReady, false)
        local bad = environment({ [ai_journal] = { protocol = 999, entries = {} } })
        request(bad, "bad-journal", "capture_start")
        equal(response(bad, "bad-journal").error.code, "journal_unavailable"); equal(bad.capture_starts, nil)
    end)

    test("AI journal validates real nil-only IO errors corrupted JSON array shape and silent write failures", function()
        local fresh = environment({ ["fixture:nil-only-io"] = true })
        request(fresh, "first-boot-real-io", "capture_start")
        equal(response(fresh, "first-boot-real-io").ok, true); equal(fresh.capture_starts, 1)
        for index, invalid in ipairs({ "fixture:invalid-json", { protocol = 1, entries = { hidden = { id = "lost" } } },
            { protocol = 1, entries = { [2] = { id = "lost", fingerprint = "x", state = "completed" } } } }) do
            local c = environment({ [ai_journal] = invalid })
            request(c, "corrupt-journal-" .. index, "capture_start")
            equal(response(c, "corrupt-journal-" .. index).error.code, "journal_unavailable")
            equal(c.capture_starts, nil); equal(backend_count(c), 0)
        end
        local silent = environment(); silent.write_noop = ai_journal
        request(silent, "false-durable", "capture_start")
        equal(response(silent, "false-durable").error.code, "journal_unavailable")
        equal(silent.capture_starts, nil)
    end)
end

do
    local function note_fixture(note)
        local c = environment(); grouped_document(c)
        c.module.state.document.dirty = false
        c.module.state.document.groups[3].note = note or ""
        equal(c.module.inspect({ kind = "group", index = 2 }), true)
        c:respond({ ok = true, fields = { { path = "note", label = "分组备注", value = note or "", type = "System.String", editable = true } } })
        c.clipboard_reads = 0
        c.env.imgui.get_clipboard = function() c.clipboard_reads = c.clipboard_reads + 1; return c.clipboard end
        return c
    end
    test("Chinese group notes remain searchable alongside the native name and cannot change tree IDs", function()
        local c = note_fixture("火焰备注###用户文字")
        c.module.state.structure_filter = "火焰备注"; c:draw("edit")
        local group_label
        for _, item in ipairs(c.tree_history) do if item.id == "###mvl_group_2" then group_label = item.label end end
        equal(group_label ~= nil, true)
        equal(group_label:find("火焰备注＃＃＃用户文字", 1, true) ~= nil, true)
        equal(group_label:find("原名：Shared", 1, true) ~= nil, true)
        c.tree_history = {}; c.module.state.structure_filter = "Shared"; c:draw("edit")
        local found = false; for _, item in ipairs(c.tree_history) do if item.id == "###mvl_group_2" then found = true end end
        equal(found, true); equal(c.module.state.document.groups[3].name, "Shared")
        equal(c.clipboard_reads, 0)
    end)

    test("saving a group note preserves the already expanded group's visible members", function()
        local c = note_fixture("旧备注"); c:draw("edit")
        local old_label
        for _, item in ipairs(c.tree_history) do if item.id == "###mvl_group_2" then old_label = item.label end end
        equal(type(old_label), "string")
        -- Model ImGui's persistent open state: ## labels include visible text
        -- in their hash; ### labels reset the identity to the suffix.
        local function identity(label) return label:match("###.*$") or label end
        local opened = { [identity(old_label)] = true }
        local original_tree_node = c.env.imgui.tree_node
        c.env.imgui.tree_node = function(label)
            local visible = original_tree_node(label)
            if visible and label:find("##mvl_group_", 1, true) and not opened[identity(label)] then
                c.env.imgui.tree_pop(); return false
            end
            return visible
        end
        c.inputs[group_note_input_id(c)] = "保存后的中文备注"
        c.buttons["保存备注##mvl_set_note"] = true; c:draw("edit")
        local updated = copy_values(c.module.state.document)
        updated.groups = { updated.groups[1], updated.groups[2], { index = 2, name = "Shared", note = "保存后的中文备注", entries = { 7 } } }
        c:respond({ ok = true, document = updated, revision = 5, noteSaved = true })
        c:respond({ ok = true, fields = { { path = "note", value = "保存后的中文备注", editable = true, type = "System.String" } } })
        c.drawn_buttons = {}; c.tree_history = {}; c:draw("edit")
        local member_visible, updated_label = false, nil
        for _, item in ipairs(c.drawn_buttons) do if item.id == "##mvl_attr_entry7_3" then member_visible = true end end
        for _, item in ipairs(c.tree_history) do if item.id == "###mvl_group_2" then updated_label = item.label end end
        equal(member_visible, true); equal(updated_label ~= old_label, true); equal(c.tree_depth, 0)
    end)

    test("group note panel explains backend warnings only when notes could not be applied", function()
        local c = note_fixture()
        c.module.state.document.groupNotesWarning = "备注文件无法读取"
        c.module.state.document.groups[3].noteWarning = "分组结构已变化，本条备注未套用"
        c:draw("edit")
        local messages = table.concat(c.messages, " ")
        equal(messages:find("备注文件无法读取", 1, true) ~= nil, true)
        equal(messages:find("分组结构已变化，本条备注未套用", 1, true) ~= nil, true)
        c.module.state.document.groupNotesWarning, c.module.state.document.groups[3].noteWarning = nil, nil
        c.messages = {}; c:draw("edit"); messages = table.concat(c.messages, " ")
        equal(messages:find("备注提示：", 1, true), nil)
        equal(messages:find("分组结构已变化", 1, true), nil)
    end)

    test("explicit Chinese clipboard paste updates only the note draft and saves original UTF8 bytes", function()
        local c = note_fixture("旧备注"); local state = c.module.state
        c:draw("edit"); equal(c.clipboard_reads, 0)
        c.clipboard = "双刀 · 鬼人化火焰 🔥"
        c.buttons["粘贴中文备注##mvl_paste_group_note"] = true; c:draw("edit")
        equal(c.clipboard_reads, 1); equal(state.values.note, c.clipboard)
        equal(state.pending, nil); equal(state.document.revision, 5); equal(state.document.dirty, false)
        equal(state.document.groups[3].name, "Shared"); equal(state.document.groups[3].note, "旧备注")
        equal(c.module.inspect({ kind = "entry", index = 7 }), false)
        c.buttons["保存备注##mvl_set_note"] = true; c:draw("edit")
        local command = c.files["MotionVfxLive/request.json"]
        equal(command.action, "set_group_note"); equal(command.note, "双刀 · 鬼人化火焰 🔥"); equal(command.groupIndex, 2)
        equal(command.name, nil); equal(#c.binding_applies, 0); equal(#c.runtime_queue, 0)
    end)

    test("invalid clipboard data and missing clipboard APIs preserve the existing Chinese draft", function()
        for _, value in ipairs({ "有换行\n文字", "有\tTab", "控制" .. string.char(0), string.char(0xff), string.rep("中", 1366), "" }) do
            local c = note_fixture("保留备注"); c.clipboard = value
            c.buttons["粘贴中文备注##mvl_paste_group_note"] = true; c:draw("edit")
            equal(c.module.state.values.note, "保留备注"); equal(c.module.state.pending, nil)
            equal(c.module.state.ui_error, nil); equal(c.clipboard_reads, 1)
        end
        for _, mode in ipairs({ "missing", "throwing" }) do
            local c = note_fixture("保留备注")
            c.env.imgui.get_clipboard = mode == "missing" and false or function() error("clipboard locked") end
            c.buttons["粘贴中文备注##mvl_paste_group_note"] = true; c:draw("edit")
            equal(c.module.state.values.note, "保留备注"); equal(c.module.state.ui_error, nil); equal(c.module.state.pending, nil)
        end
    end)

    test("note typing and clearing are explicit drafts and UTF8 byte limit is enforced without truncation", function()
        local c = note_fixture("中文备注"); local state = c.module.state
        c.inputs[group_note_input_id(c)] = "直接输入中文"; c:draw("edit")
        equal(state.values.note, "直接输入中文"); equal(state.pending, nil)
        c.buttons["还原备注输入##mvl_revert_group_note"] = true; c:draw("edit"); equal(state.values.note, "中文备注")
        c.buttons["清空备注输入##mvl_clear_group_note"] = true; c:draw("edit")
        equal(state.values.note, ""); equal(state.pending, nil)
        c.buttons["保存备注##mvl_set_note"] = true; c:draw("edit")
        equal(c.files["MotionVfxLive/request.json"].note, "")
        local bounded = note_fixture(); bounded.clipboard = string.rep("中", 1365) .. "a"
        bounded.buttons["粘贴中文备注##mvl_paste_group_note"] = true; bounded:draw("edit")
        equal(#bounded.module.state.values.note, 4096)
        bounded.module.state.values.note = bounded.module.state.values.note .. "a"
        bounded.buttons["保存备注##mvl_set_note"] = true; bounded:draw("edit")
        equal(bounded.module.state.pending, nil); equal(#bounded.module.state.values.note, 4097)
    end)

    test("new UI refuses legacy native group name editors instead of changing EFX names", function()
        local c = environment(); grouped_document(c)
        c.module.state.selected = { kind = "group", index = 2 }
        c.module.state.fields = { { path = "name", value = "Shared", editable = true, type = "System.String" } }
        c.module.state.values.name = "Shared"
        c.inputs["##mvl_value_name"] = "不该写入EFX"
        c.buttons["提交##mvl_set_name"] = true; c:draw("edit")
        equal(c.module.state.pending, nil); equal(c.module.state.values.name, "Shared")
        equal(c.module.state.document.groups[3].name, "Shared")
        equal(table.concat(c.messages, " "):find("后台尚未提供独立分组备注", 1, true) ~= nil, true)
    end)

    test("AI note actions and legacy alias preserve revision dirty and native identity and expose sidecar receipt", function()
        for _, action in ipairs({ "set_group_note", "rename_group", "set_field" }) do
            local c = note_fixture("旧备注"); local state = c.module.state
            state.document.dirty = true
            local args = { index = 2, note = "测试中文备注", name = "测试中文备注", expectedRevision = 5,
                target = { kind = "group", index = 2 }, path = "note", value = "测试中文备注" }
            c.files["MotionVfxLive/ai-request.json"] = { protocol = 1, id = "note-" .. action, action = action,
                arguments = args, expiresAtUnixSeconds = c.time + 30 }
            c:frame()
            local command = c.files["MotionVfxLive/request.json"]
            equal(command.action, "set_group_note"); equal(command.groupIndex, 2); equal(command.note, "测试中文备注")
            local updated = copy_values(state.document)
            updated.groups = { state.document.groups[1], state.document.groups[2], { index = 2, name = "Shared", note = "测试中文备注", entries = { 7 } } }
            c:respond({ ok = true, document = updated, revision = 5, noteSaved = true, noteStorage = "editor_sidecar", noteMessage = "备注已独立保存" })
            local reply = c.files["MotionVfxLive/ai-response.json"]
            equal(reply.ok, true); equal(reply.result.noteSaved, true); equal(reply.result.noteStorage, "editor_sidecar")
            equal(reply.result.noteMessage, "备注已独立保存"); equal(reply.result.document.revision, 5); equal(reply.result.document.dirty, true)
            equal(reply.result.document.groups[3].name, "Shared"); equal(#c.binding_applies, 0)
        end
        local c = note_fixture()
        c.files["MotionVfxLive/ai-request.json"] = { protocol = 1, id = "reject-native-name", action = "set_field",
            arguments = { target = { kind = "group", index = 2 }, path = "name", value = "bad", expectedRevision = 5 }, expiresAtUnixSeconds = c.time + 30 }
        c:frame()
        equal(c.files["MotionVfxLive/ai-response.json"].error.code, "readonly_group_name")
        equal(c.module.state.pending, nil); equal(c.module.state.document.groups[3].name, "Shared")
    end)
end

do
    local function motion_fixture()
        local c = environment()
        c.capture_state.actions = {
            { id = "a1", sceneEpoch = 1, componentKind = "character",
                motion = { layer = 0, bankId = 10, motionId = 21 }, firstMotionFrame = 0, lastMotionFrame = 32, closed = true },
            { id = "a2", sceneEpoch = 1, componentKind = "character",
                motion = { layer = 0, bankId = 10, motionId = 22 }, firstMotionFrame = 2, lastMotionFrame = 15 },
        }
        c.capture_state.currentActions = { c.capture_state.actions[2] }
        c.capture_state.events = {
            { id = "direct", localConfirmed = true, sceneEpoch = 1, containerId = 7, elementId = 2,
                requested = { { path = "@/NATIVES/STM/Effect\\One.efx.5571972" } },
                actionLinks = { { actionId = "a1", association = "direct_motion_request", motionFrame = 12.5 } } },
            { id = "temporal", localConfirmed = true, sceneEpoch = 1, sourceKind = "shell",
                created = { { path = "effect/two.efx" } },
                actionLinks = { { actionId = "a1", association = "contemporaneous_only", motionFrame = 28 } } },
            { id = "other-owner", localConfirmed = false, sceneEpoch = 1, created = { { path = "effect/other.efx" } },
                actionLinks = { { actionId = "a1", association = "direct_motion_request", motionFrame = 15 } } },
            { id = "other-scene", localConfirmed = true, sceneEpoch = 2, created = { { path = "effect/other.efx" } },
                actionLinks = { { actionId = "a1", association = "direct_motion_request", motionFrame = 16 } } },
        }
        c.capture_state.resources = {
            { path = "effect/one.efx", localConfirmed = true },
            { path = "effect/two.efx", localConfirmed = true },
            { path = "effect/other.efx", localConfirmed = true },
        }
        c.module.refresh_action_state()
        return c
    end
    local function visible_resource(c, index)
        for _, item in ipairs(c.drawn_buttons) do
            if item.id == "##mvl_capture_resource_" .. index then return true end
        end
        return false
    end
    test("action selection filters paths while distinguishing direct triggers from temporal shells", function()
        local c = motion_fixture()
        c.buttons["##mvl_action_select_a1"] = true; c:draw("actions")
        equal(c.module.state.selected_action_id, "a1")
        equal(visible_resource(c, 1), true); equal(visible_resource(c, 2), true); equal(visible_resource(c, 3), false)
        local selected, links, paths = c.module.action_details(c.capture_state, "a1")
        equal(selected.motion.motionId, 21); equal(#links, 2); equal(paths["effect/one.efx"], true)
        equal(links[1].association, "direct_motion_request"); equal(links[1].motionFrame, 12.5)
        equal(links[1].containerId, 7); equal(links[1].elementId, 2)
        equal(links[2].association, "contemporaneous_only"); equal(links[2].sourceKind, "shell")
        local messages = table.concat(c.messages, " ")
        equal(messages:find("同期出现", 1, true) ~= nil, true)
        equal(messages:find("Bank 10 / Motion 22", 1, true) ~= nil, true)
        equal(c.module.state.pending, nil); equal(#c.binding_applies, 0); equal(#c.runtime_queue, 0)
        c.drawn_buttons = {}; c.buttons["##mvl_action_all"] = true; c:draw("actions")
        equal(c.module.state.selected_action_id, nil); equal(visible_resource(c, 3), true)
    end)
    test("no-effect and evicted actions never accidentally show unrelated resources", function()
        local c = motion_fixture()
        c.buttons["##mvl_action_select_a2"] = true; c:draw("actions")
        equal(visible_resource(c, 1), false); equal(visible_resource(c, 2), false)
        equal(table.concat(c.messages, " "):find("此片段未记录到关联特效请求", 1, true) ~= nil, true)
        c.capture_state.actions = {}; c.module.refresh_action_state(); c.drawn_buttons = {}; c.messages = {}
        c:draw("actions")
        equal(visible_resource(c, 1), false); equal(visible_resource(c, 3), false)
        equal(table.concat(c.messages, " "):find("所选动作已清空", 1, true) ~= nil, true)
    end)
    test("weapon and secondary layer records are opt-in without removing captured evidence", function()
        local c = motion_fixture()
        c.capture_state.actions[3] = { id = "weapon-loop", componentKind = "weapon",
            motion = { layer = 0, bankId = 0, motionId = 1 }, loopWraps = 300 }
        c.capture_state.actions[4] = { id = "blend", componentKind = "character",
            motion = { layer = 3, bankId = 10, motionId = 25 } }
        c:draw("actions")
        local function shown(id)
            for _, item in ipairs(c.drawn_buttons) do if item.id == "##mvl_action_select_" .. id then return true end end
            return false
        end
        equal(shown("a1"), true); equal(shown("weapon-loop"), false); equal(shown("blend"), false)
        c.checks["显示武器动画及附加层##mvl_all_action_layers"] = true
        c.drawn_buttons = {}; c:draw("actions")
        equal(shown("weapon-loop"), true); equal(shown("blend"), true); equal(#c.capture_state.actions, 4)
    end)
    test("AI action queries remain read-only, expose evidence, and reject expired selection", function()
        local c = motion_fixture()
        c.module.state.pending = { id = "existing-operation", action = "inspect" }
        c.module.state.values = { draft = "preserve" }
        local ok, response
        local function receive(success, value) ok, response = success, value end
        c.module.execute_ai("list_actions", {}, receive)
        equal(ok, true); equal(#response.actions, 2); equal(#response.currentActions, 1)
        c.module.execute_ai("list_actions", { actionId = "a1" }, receive)
        equal(ok, true); equal(response.action.id, "a1"); equal(#response.events, 2)
        equal(response.events[2].association, "contemporaneous_only")
        c.module.execute_ai("list_actions", { actionId = "expired" }, receive)
        equal(ok, false); equal(response.code, "action_not_found")
        c.module.execute_ai("list_actions", { actionId = 2 }, receive)
        equal(ok, false); equal(response.code, "invalid_arguments")
        equal(c.module.state.pending.id, "existing-operation"); equal(c.module.state.values.draft, "preserve")
        equal(#c.binding_applies, 0); equal(#c.runtime_queue, 0); equal(c.files["MotionVfxLive/request.json"], nil)
    end)
end

test("debug shortcut uses existing pause resume and restores the prior slow motion", function()
    local sample={triggers=0,focused=true,pressed={}}
    local native={version="fixture",configure=function() return true end,
        sample=function() return sample end,disable=function() end}
    local c=environment(nil,native); local debug=c.module.debug_hotkeys
    local original_speed=c.speed
    equal(debug.enabled,false); equal(debug.available,true)
    c.module.queue_scene("speed",0.25); c:frame()
    c.show_ui=false; debug:enable(true); c:frame()
    sample.triggers=1; c:frame(); c:frame(); equal(c.speed,0.0); equal(debug.toggles,1)
    c:frame(); equal(c.speed,0.0); equal(debug.toggles,1)
    sample.triggers=2; c:frame(); c:frame(); equal(c.speed,0.25); equal(debug.toggles,2)
    c.show_ui=true; sample.triggers=3; c:frame(); c:frame(); equal(debug.toggles,3); equal(c.speed,0)
    c.active_text_item="组合键##mvl_debug_chord"
    sample.triggers=4; c:frame(); c:frame(); equal(debug.toggles,3)
    equal(debug:status().gate,"text_input")
    -- Closing the editor alone leaves REFramework open: this must still work.
    c.module.state.window_open=false
    sample.triggers=5; c:frame(); c:frame(); equal(debug.toggles,4); equal(c.speed,0.25)
    c.module.stop_and_restore(); c:frame()
    equal(debug.enabled,false); equal(c.speed,original_speed)
    equal(#c.binding_applies,0)
end)

print(string.format("RESULT %d passed, %d failed, %d assertions (%s, no game calls)", passed, failed, assertions,
    real_reload_fixture and "mock engine + real filesystem" or "mock-only"))
if failed > 0 then os.exit(1) end
