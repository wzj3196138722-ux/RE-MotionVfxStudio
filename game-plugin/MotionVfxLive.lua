-- MotionVfx Live: REFramework UI + a local, revisioned file mailbox.
-- Install with MotionVfxLive/fx_preview_runtime.lua. No editing request,
-- playback, or scene mutation happens on script load. The backend owns parsing/serialization and publishes
-- unique game resource paths; this script never overwrites an original EFX.
if _G.MotionVfxLive then return _G.MotionVfxLive end

local M = { version = "0.8.7", active = false }
local imgui = imgui -- Renderer substitution is local to this editor only.
local workbench, precache_runtime, action_preview, pfb_preview
local pfb_preview_generation=0
local function pfb_preview_status()return pfb_preview and pfb_preview:snapshot() or {phase="unavailable",message="PFB 热重载模块未就绪"}end
M.pfb_preview_status=pfb_preview_status
local action_preview_generation=0
local action_edit
local function action_preview_status()return action_preview and action_preview:snapshot() or {phase="unavailable",message="动作热重载模块未就绪"}end
M.action_preview_status=action_preview_status
local runtime_ok, runtime = true, _G.fx_preview_runtime_v2
if type(runtime) ~= "table" or type(runtime.new) ~= "function" then
    runtime_ok, runtime = pcall(require, "MotionVfxLive.fx_preview_runtime")
    if not runtime_ok then
        -- Manual Run Script can originate in data/, with no autorun entry in
        -- package.path. Load the deployed dependency directly in that case.
        local loader, load_error = loadfile("reframework/autorun/MotionVfxLive/fx_preview_runtime.lua")
        if loader then runtime_ok, runtime = pcall(loader)
        else runtime = tostring(runtime) .. "; " .. tostring(load_error) end
    end
end
if runtime_ok and type(runtime) == "table" then package.loaded["MotionVfxLive.fx_preview_runtime"] = runtime end
local client, runtime_error
if runtime_ok and type(runtime) == "table" and type(runtime.new) == "function" then
    local ok, result = pcall(runtime.new, "MotionVfxLive")
    if ok then client = result else runtime_error = tostring(result) end
else runtime_error = tostring(runtime) end
local function load_action_module(global_name, module_name, filename)
    if type(_G[global_name]) == "table" then return _G[global_name] end
    local ok, result = pcall(require, module_name)
    if not ok or type(result) ~= "table" then
        local compiled, loader, load_error = pcall(loadfile, "reframework/autorun/MotionVfxLive/" .. filename)
        if not compiled or not loader then return nil, tostring(compiled and load_error or loader) end
        ok, result = pcall(loader)
    end
    if not ok or type(result) ~= "table" then return nil, tostring(result) end
    package.loaded[module_name] = result
    return result
end
local capture, capture_error = load_action_module("MotionVfxActionCapture", "MotionVfxLive.fx_action_capture", "fx_action_capture.lua")
local bindings, bindings_error = load_action_module("MotionVfxActionBindings", "MotionVfxLive.fx_action_bindings", "fx_action_bindings.lua")
local parameters = load_action_module("MotionVfxParameterUI", "MotionVfxLive.fx_parameter_ui", "fx_parameter_ui.lua")
if bindings and type(bindings.configure) == "function" then
    local ok, result, err = pcall(bindings.configure, { capture = capture })
    if not ok or result == false then bindings_error = tostring(ok and err or result) end
end
local request_sequence = 0
local session = tostring(os.time()) .. "-" .. tostring({}):gsub("[^%w]", "")
local request_path, response_path, status_path = "MotionVfxLive/request.json", "MotionVfxLive/response.json", "MotionVfxLive/status.json"
local state = {
    status = nil, status_error = nil, last_poll = -1, response_frames = 0, pending = nil,
    message = "等待后台就绪；所有编辑、播放和暂停均由按钮触发。", preview_message = "尚未创建预览实例。",
    document = nil, catalog = {}, fields = {}, selected = nil, values = {}, curves = {}, color_groups = {},
    curve_selection = nil, curve_baseline = nil, curve_confirm_delete = false, curve_key_page = 1,
    curve_time = "0", curve_value = "0", curve_interpolation = "Type2",
    curve_out_x = "0", curve_out_y = "0", curve_in_x = "0", curve_in_y = "0",
    local_path = "", filter = "", field_filter = "", field_page = 1, attribute_type = "", entry_name = "New Entry",
    save_path = "", resource_path = "", published_path = nil, published_revision = nil,
    target_resource_path = nil, target_document_source = nil, resource_candidates = {}, resolving_resource_path = nil,
    open_confirmation = nil, action_message = "记录角色实际动作，选择本次请求的资源进行编辑。",
    capture_snapshot = {}, bindings_snapshot = {}, module_frames = 15, show_unconfirmed_resources = false,
    action_filter = "", selected_action_id = nil, show_all_action_layers = false,
    bindings_restore_pending = false, bindings_restore_failed = nil, restore_after_busy = false,
    apply_requested = false, apply_generation = 0,
    scale = "1", x = "0", y = "0", z = "0", speed = "1", attachment = 2, bone = "",
    point_cloud_enabled = false, point_cloud_space = 1, point_cloud_indices = "0,1,2,3,4",
    confirm_delete = false, scene_original = nil, scene_message = "尚未接管场景时间。",
    scene_queue = {}, effect_queue = {}, scene_actual = nil, effect_actual = nil,
    scene_pause = nil, scene_control_error = nil, scene_history = {}, scene_pause_frames = 0,
    loaded_at = os.time(), last_diagnostic = -1,
    window_open = true, window_initialized = false, active_tab = "actions", ui_error = nil, window_drawn = false,
    window_visible = false, window_fit_requested = true, window_center_requested = false,
    structure_filter = "", field_editable_only = false, field_modified_only = false, field_show_paths = false,
    font_scale = 1.15, parameter_language = "zh", window_hidden = false,
}
M.state = state
local function parameter_label(value) return parameters and parameters.label(value,state.parameter_language) or tostring(value) end
local function parameter_matches(value,filter) return parameters and parameters.search(value,filter) or tostring(value):lower():find(filter,1,true) end
M.parameter_label = parameter_label
function M.set_parameter_language(value)
    state.parameter_language=value=="en" and "en" or "zh"
    state.field_view_source=nil
    if workbench then workbench:save_settings() end
end
local ai_bridge, ai_error
local debug_hotkeys, debug_error
-- Keep capture local to this editor; never replace another script's ImGui API.
local function text_input(...)
    local changed, value = imgui.input_text(...)
    if imgui.is_item_active and imgui.is_item_active() then state.debug_text_input_active = true end
    return changed, value
end

-- REFramework exposes ImGui colors by numeric ID. These core IDs predate the
-- newer tab colors and remain stable. Colors are signed ImU32 (ABGR), not tables.
local colors = { text = 0, window = 2, child = 3, border = 5, frame = 7,
    frame_hover = 8, frame_active = 9, button = 21, button_hover = 22, button_active = 23, header = 24 }
local function rgb(r, g, b) return r + g * 256 + b * 65536 - 16777216 end
local palette = { text = rgb(237, 242, 252), muted = rgb(173, 188, 211), accent = rgb(117, 195, 255),
    warning = rgb(255, 207, 112), success = rgb(130, 225, 179), button = rgb(51, 70, 99), primary = rgb(37, 89, 166) }
local function dimension(value, key, fallback)
    if value ~= nil and (type(value) == "table" or type(value) == "userdata") then
        local ok, result = pcall(function() return value[key] end)
        if ok and type(result) == "number" then return result end
    end
    return fallback
end
local function available_width()
    local size = imgui.get_window_size and imgui.get_window_size()
    local cursor = imgui.get_cursor_pos and imgui.get_cursor_pos()
    return math.max(120, dimension(size, "x", 820) - dimension(cursor, "x", 12) - 28)
end
local wrap_cache, wrap_order, wrap_next = {}, {}, 1
local function wrapped(value, limit)
    local width = math.floor(limit or available_width())
    if not imgui.calc_text_size or not utf8 then return value end
    local font_size = imgui.get_default_font_size and imgui.get_default_font_size() or state.font_scale
    local font_identity=imgui._mvl_wrapped_text==true and imgui.get_text_metrics_identity() or font_size
    local key = state.font_scale .. ":" .. tostring(imgui) .. ":" .. tostring(font_size) .. ":" .. tostring(font_identity) .. ":" .. value
    local variants=wrap_cache[key]
    if variants then for _,variant in ipairs(variants) do
        if width>=variant.minimum and width<variant.maximum then return variant.text end
    end end
    local reliable=true
    local minimum,maximum=0,math.huge
    local function measure(line)
        local result=imgui.calc_text_size(line)
        if type(result)=="table" and result.reliable==false then reliable=false end
        return dimension(result,"x",0)
    end
    local output = {}
    for line in (value .. "\n"):gmatch("(.-)\n") do
        local line_width=measure(line)
        while line ~= "" and line_width > width do
            maximum=math.min(maximum,line_width)
            local offsets = {}
            local valid = pcall(function() for pos in utf8.codes(line) do offsets[#offsets + 1] = pos end end)
            if not valid then break end
            offsets[#offsets + 1] = #line + 1
            local low, high, fit = 1, #offsets - 1, 1
            while low <= high do
                local mid = math.floor((low + high) / 2)
                if measure(line:sub(1, offsets[mid + 1] - 1)) <= width then
                    fit, low = mid, mid + 1
                else high = mid - 1 end
            end
            -- This wrap is valid until a further glyph fits. Reuse that exact
            -- interval during one-pixel resize drags instead of rebuilding all
            -- long, off-screen parameter labels for every width.
            if fit>1 then minimum=math.max(minimum,measure(line:sub(1,offsets[fit+1]-1))) end
            if fit<#offsets-1 then maximum=math.min(maximum,measure(line:sub(1,offsets[fit+2]-1))) end
            output[#output + 1] = line:sub(1, offsets[fit + 1] - 1)
            line = line:sub(offsets[fit + 1])
            line_width=measure(line)
        end
        minimum=math.max(minimum,line_width)
        output[#output + 1] = line
    end
    local result = table.concat(output, "\n")
    if reliable and #key <= 8192 then
        if not variants and #wrap_order < 4096 then wrap_order[#wrap_order + 1] = key
        elseif not variants then
            wrap_cache[wrap_order[wrap_next]] = nil
            wrap_order[wrap_next] = key; wrap_next = wrap_next % 4096 + 1
        end
        variants=variants or {}
        if #variants>=8 then table.remove(variants,1) end
        variants[#variants+1]={minimum=minimum,maximum=maximum,text=result}
        wrap_cache[key] = variants
    end
    return result
end
local function text(v, color)
    if imgui._mvl_wrapped_text==true then return imgui.draw_wrapped_text(tostring(v or ""),color,wrapped) end
    if color and imgui.push_style_color then imgui.push_style_color(colors.text, color) end
    local ok, err = pcall(function() imgui.text(wrapped(tostring(v or ""))) end)
    if color and imgui.push_style_color then imgui.pop_style_color(1) end
    if not ok then error(err, 0) end
end
local function child(label, size, fn)
    local visible = imgui.begin_child_window(label, size, true)
    local ok, err = true, nil
    if visible ~= false then ok, err = pcall(fn) end
    imgui.end_child_window()
    if not ok then error(err, 0) end
end
local function section(label, fn)
    if not imgui.tree_node(label) then return end
    local ok, err = pcall(fn)
    imgui.tree_pop()
    if not ok then error(err, 0) end
end
local function with_theme(fn)
    local pushed, font = 0, false
    local ok, err = pcall(function()
        if imgui.push_style_color then
            for _, item in ipairs({ { colors.text, palette.text }, { colors.window, rgb(18, 24, 36) },
                { colors.child, rgb(23, 31, 46) }, { colors.border, rgb(69, 86, 111) },
                { colors.frame, rgb(39, 52, 73) }, { colors.frame_hover, rgb(52, 72, 101) },
                { colors.frame_active, rgb(58, 85, 125) }, { colors.button, palette.button },
                { colors.button_hover, rgb(67, 99, 147) }, { colors.button_active, rgb(45, 107, 191) },
                { colors.header, rgb(42, 71, 115) } }) do
                imgui.push_style_color(item[1], item[2]); pushed = pushed + 1
            end
        end
        if imgui.push_font_size and imgui.get_default_font_size then
            local base = imgui.get_default_font_size()
            if type(base) == "number" and base > 0 then
                imgui.push_font_size(math.max(18, base * state.font_scale)); font = true
            end
        end
        fn()
    end)
    if font then imgui.pop_font_size() end
    if pushed > 0 then imgui.pop_style_color(pushed) end
    if not ok then error(err, 0) end
end
local function fail(message) error(message, 0) end
local function finite(value, label, minimum)
    local number = tonumber(value)
    if not number or number ~= number or number == math.huge or number == -math.huge then fail(label .. "必须为有限数字") end
    if minimum and number < minimum then fail(label .. "不能小于 " .. minimum) end
    -- REFramework marshals Lua integers as raw argument bits. Even a textual
    -- "1" must become a Lua float before calling a System.Single setter.
    return number + 0.0
end
local function copy_target(target)
    if not target then return nil end
    local copied = { kind = target.kind, index = target.index, attributeIndex = target.attributeIndex }
    if target.embeddedPath and #target.embeddedPath > 0 then
        copied.embeddedPath = {}
        for i, hop in ipairs(target.embeddedPath) do
            copied.embeddedPath[i] = { kind = hop.kind, index = hop.index, attributeIndex = hop.attributeIndex }
        end
    end
    return copied
end
local function embedded_key(target)
    local parts = {}
    for _, hop in ipairs(target and target.embeddedPath or {}) do
        parts[#parts + 1] = tostring(hop.kind) .. "_" .. tostring(hop.index) .. "_" .. tostring(hop.attributeIndex)
    end
    return #parts > 0 and ("_in_" .. table.concat(parts, "__")) or ""
end
local function same_target(a, b)
    return a ~= nil and b ~= nil and a.kind == b.kind and a.index == b.index and
        a.attributeIndex == b.attributeIndex and embedded_key(a) == embedded_key(b)
end
local function target_key(target)
    target = target or {}
    local source = tostring(state.document and state.document.source or "")
    -- Keep ImGui's hidden IDs distinct across files and attributes. Escape the
    -- path so a filename containing ## cannot alter the label/ID separator.
    source = source:gsub("[^%w_%-]", function(c) return string.format("%%%02X", string.byte(c)) end)
    return source .. "_" .. tostring(target.kind) .. "_" .. tostring(target.index) .. "_" .. tostring(target.attributeIndex) .. embedded_key(target)
end
M.target_key = target_key
local function log_warning(message)
    if log and log.warn then log.warn("[MotionVfxLive] " .. tostring(message)) end
end
local function normalized_effect_path(path)
    if type(path) ~= "string" then return nil end
    return path:gsub("^%s+", ""):gsub("%s+$", ""):gsub("^@+", ""):gsub("\\", "/")
        :gsub("/+", "/"):gsub("^/+", ""):lower():gsub("^natives/stm/", ""):gsub("^stm/", ""):gsub("%.efx%.%d+$", ".efx")
end
local function verification_evidence(snapshot, generated, visit)
    for event_index, event in ipairs(snapshot.events or {}) do
        if event.localConfirmed == true then
            for _, kind in ipairs({ "requested", "created" }) do
                for item_index, item in ipairs(event[kind] or {}) do
                    -- canonicalPath may alias back to the original resource. The
                    -- observed raw path is the evidence that this revision ran.
                    if normalized_effect_path(item.path) == generated then
                        local key = tostring(event.id or event.seq or event_index) .. "|" .. kind .. "|" ..
                            tostring(item_index) .. "|" .. tostring(item.path) .. "|" .. tostring(item.playerIdentity)
                        visit(key, kind, event, item)
                    end
                end
            end
        end
    end
end
local function update_verification()
    local verification = state.verification
    if not verification or verification.phase == "created" then return end
    verification_evidence(state.capture_snapshot, normalized_effect_path(verification.generatedPath), function(key, kind, event, item)
        if verification.baseline[key] or verification.seen[key] or (verification.phase == "created" and kind ~= "created") then return end
        verification.seen[key] = true
        verification.phase = kind
        verification.eventId, verification.observedPath = event.id or event.seq, item.path
        verification.motion, verification.motionAssociation = event.motion, event.motionAssociation
        verification.message = kind == "created" and "已确认角色实际创建新版本；画面效果请观察。" or
            "角色已请求新版本，尚未确认实例创建；请继续观察或重做原动作。"
    end)
end
local action_refresh = { frame = 0, full_frame = -1, history_visible = false, in_frame = false }
local function refresh_action_state()
    for _, item in ipairs({ { capture, "capture_snapshot", "capture_error" }, { bindings, "bindings_snapshot", "bindings_error" } }) do
        if item[1] and type(item[1].snapshot) == "function" then
            local ok, result = pcall(item[1].snapshot)
            if ok and type(result) == "table" then state[item[2]], state[item[3]] = result, nil
            else state[item[3]] = tostring(result) end
        end
    end
    action_refresh.full_frame = action_refresh.frame
    update_verification()
end
M.refresh_action_state = refresh_action_state
local function action_history_visible()
    if not state.window_open or state.window_hidden then return false end
    if reframework and type(reframework.is_drawing_ui) == "function" then
        local ok, visible = pcall(reframework.is_drawing_ui, reframework)
        if not ok or not visible then return false end
    end
    if workbench then
        return workbench.active and not workbench.top and
            (workbench.page == "actions" or workbench.page == "effects") or false
    end
    return state.active_tab == "actions"
end
local function refresh_regular_action_state(due)
    local visible = action_history_visible()
    local entered = visible and not action_refresh.history_visible
    action_refresh.history_visible = visible
    local current, scene_changed
    if capture and type(capture.current_snapshot) == "function" then
        local ok, result = pcall(capture.current_snapshot)
        if ok and type(result) == "table" then
            current = result
            local previous = state.capture_snapshot or {}
            scene_changed = previous.sceneEpoch ~= nil and current.sceneEpoch ~= nil and previous.sceneEpoch ~= current.sceneEpoch
            if scene_changed then
                -- Do not retain old ownership if the subsequent full read fails.
                previous = {resources = {}, events = {}, actions = {},
                    actor = {available = false, sceneEpoch = current.sceneEpoch}}
                state.capture_snapshot = previous
            end
            previous.currentActions, previous.actionStatus = current.currentActions or {}, current.actionStatus
            previous.recording, previous.sceneEpoch = current.recording, current.sceneEpoch
        else
            state.capture_error = "当前动作读取失败：" .. tostring(result)
            state.capture_snapshot.currentActions = {}
            state.capture_snapshot.actionStatus = state.capture_error
        end
    end
    local waiting = state.verification and state.verification.phase ~= "created"
    -- Capture hooks collect independently of these UI copies. Only visible
    -- history and apply verification need frequent full snapshots; hidden/edit
    -- pages, even while recording, use the two-second diagnostics refresh.
    -- Explicit operations continue to call refresh_action_state themselves.
    if entered or scene_changed or (due and (not current or visible or waiting)) then
        refresh_action_state()
    elseif due and bindings and type(bindings.snapshot) == "function" then
        -- Preserve transaction messages/progress on the original refresh cadence.
        local ok, result = pcall(bindings.snapshot)
        if ok and type(result) == "table" then state.bindings_snapshot, state.bindings_error = result, nil
        else state.bindings_error = tostring(result) end
    end
end
local function current_resource_is_local(path)
    -- A selected document records historical identity, not ongoing ownership.
    -- Never authorize from a retained snapshot after a snapshot read failure.
    if not capture or type(capture.snapshot) ~= "function" or state.capture_error then return false end
    local target = normalized_effect_path(path)
    if not target or target == "" or type(state.capture_snapshot.resources) ~= "table" then return false end
    for _, resource in ipairs(state.capture_snapshot.resources) do
        if type(resource) == "table" and resource.localConfirmed == true and
            normalized_effect_path(resource.canonicalPath or resource.path) == target then return true end
    end
    return false
end
local function ownership_required_message()
    state.action_message = "当前记录未能确认该资源仍属于本角色，未应用到角色真实资源。文档已保留；请重新记录角色动作并确认该资源后再应用。"
end
local source_identity_fields = { "sourceKind", "sourceId", "eventId", "sceneEpoch", "actualPath",
    "actualHolderIdentity", "playerIdentity", "sharedTargetPath", "localConfirmed", "availability" }
local function valid_capture_source(source)
    if type(source) ~= "table" or source.sourceKind ~= "actual_effect_player_holder" or
        source.localConfirmed ~= true or source.availability ~= "retained_observation" or
        type(source.sceneEpoch) ~= "number" or source.sceneEpoch < 1 or source.sceneEpoch % 1 ~= 0 then return false end
    for _, name in ipairs({ "sourceId", "eventId", "actualPath", "actualHolderIdentity", "playerIdentity", "sharedTargetPath" }) do
        if type(source[name]) ~= "string" or source[name] == "" then return false end
    end
    return true
end
local function copy_capture_source(source)
    if type(source) ~= "table" then return nil end
    local result = {}
    for _, name in ipairs(source_identity_fields) do result[name] = source[name] end
    result.currentPlayerVerified, result.cacheBytesAvailable = source.currentPlayerVerified, source.cacheBytesAvailable
    return result
end
local function same_capture_source(expected, actual)
    if not valid_capture_source(expected) or not valid_capture_source(actual) then return false end
    for _, name in ipairs(source_identity_fields) do if expected[name] ~= actual[name] then return false end end
    return true
end
local function current_capture_source_matches(expected, target)
    if not valid_capture_source(expected) or state.capture_error or not capture or
        type(capture.get_resource_source) ~= "function" then return false end
    if normalized_effect_path(expected.sharedTargetPath) ~= normalized_effect_path(target) then return false end
    local snapshot = state.capture_snapshot or {}
    local epoch = snapshot.sceneEpoch or (snapshot.actor or {}).sceneEpoch
    if epoch ~= expected.sceneEpoch then return false end
    local ok, actual = pcall(capture.get_resource_source, target, expected.sourceId)
    return ok and same_capture_source(expected, actual)
end
local function current_edit_source_matches(expected, target)
    if current_capture_source_matches(expected, target) then return true end
    if not valid_capture_source(expected) or state.capture_error or not capture or
        type(capture.get_resource_source) ~= "function" then return false end
    local target_path = normalized_effect_path(target)
    if normalized_effect_path(expected.sharedTargetPath) ~= target_path then return false end
    local snapshot = state.capture_snapshot or {}
    local epoch = snapshot.sceneEpoch or (snapshot.actor or {}).sceneEpoch
    if epoch ~= expected.sceneEpoch then return false end
    local ok, actual = pcall(capture.get_resource_source, target)
    if not ok or not valid_capture_source(actual) or actual.sceneEpoch~=epoch or
        normalized_effect_path(actual.sharedTargetPath)~=target_path then return false end
    -- Recording again replaces observation IDs and pooled effect players.
    -- The same freshly confirmed original resource remains a valid target.
    if normalized_effect_path(actual.actualPath)==target_path and
        normalized_effect_path(expected.actualPath)==target_path then return true end
    -- A real creation of our applied revision replaces the latest retained
    -- holder. Permit that specific revision to continue the same document;
    -- a newer observation of an unrelated resource is not authorization.
    local verification = state.verification
    if not verification or verification.phase ~= "created" or
        normalized_effect_path(verification.originalPath) ~= target_path or
        type(verification.generatedPath) ~= "string" or verification.generatedPath == "" then return false end
    return ok and valid_capture_source(actual) and actual.sceneEpoch == epoch and
        normalized_effect_path(actual.sharedTargetPath) == target_path and
        normalized_effect_path(actual.actualPath) == normalized_effect_path(verification.generatedPath)
end
local function capture_source_required_message()
    state.action_message = "动作来源已变化或不再可验证，未替换角色资源。当前编辑文档已保留；请重新记录角色动作并重新选择动作资源。"
end
local function capture_command(operation)
    if not capture or type(capture[operation]) ~= "function" then state.action_message = "动作记录模块不可用：" .. tostring(capture_error); return false end
    refresh_action_state()
    if operation == "clear" and (state.bindings_snapshot.busy or (state.bindings_snapshot.appliedCount or 0) > 0) then
        state.action_message = "请先还原资源试改，再清空用于定位原始引用的记录。"; return false
    end
    local ok, result, err = pcall(capture[operation])
    if not ok or result == false then state.action_message = "记录操作失败：" .. tostring(ok and err or result); return false end
    if (operation == "start" or operation == "clear") and workbench then workbench:reset_effect_filter() end
    state.action_message = operation == "start" and "正在记录：请在游戏中完成动作，然后结束记录。" or
        operation == "stop" and "已结束记录；请查看实际资源请求与未解析事件。" or "已清空动作记录。"
    refresh_action_state()
    if state.verification and state.verification.phase ~= "created" then
        state.verification.message = operation == "stop" and "验证记录已停止；请再次开始记录并重做原动作，才能确认新版本创建。" or
            operation == "start" and "正在等待角色重做原动作，核对新版本资源请求和实际创建。" or state.verification.message
    end
    return true
end
M.capture_command = capture_command
local function begin_verification(original, generated)
    refresh_action_state()
    state.verification = { originalPath = original, generatedPath = generated, phase = "awaiting_action", baseline = {}, seen = {},
        message = "正在等待角色重做原动作，核对新版本资源请求和实际创建。" }
    verification_evidence(state.capture_snapshot, normalized_effect_path(generated), function(key)
        state.verification.baseline[key] = true
    end)
    if not capture_command("start") then
        state.verification.message = "资源已应用，但验证记录未启动；请恢复动作记录模块后开始记录并重做原动作。"
    end
end
local function restore_bindings()
    refresh_action_state()
    state.verification = nil
    if not bindings or type(bindings.restore_all) ~= "function" then
        state.bindings_restore_failed = bindings_error or "资源还原模块不可用"
        state.action_message = state.bindings_restore_failed; return false
    end
    if state.bindings_restore_pending then return true end
    -- A publish already in flight must not apply after the user asks to restore.
    state.apply_generation = state.apply_generation + 1
    state.bindings_restore_pending, state.bindings_restore_failed = true, nil
    if state.apply_requested or state.bindings_snapshot.busy then
        state.restore_after_busy = true
        state.action_message = "等待当前资源操作结束后还原原始引用。"
        return true
    end
    state.restore_after_busy = false
    local ok, accepted, err = pcall(bindings.restore_all, function(result)
        state.bindings_restore_pending = false
        if result and result.ok then
            state.bindings_restore_failed = nil; state.action_message = "已还原资源试改；可重新执行原动作核对。"
        else
            state.bindings_restore_failed = tostring(result and result.message or "模块未返回还原结果")
            state.action_message = "资源还原失败：" .. state.bindings_restore_failed
        end
        refresh_action_state()
    end)
    if not ok or not accepted then
        state.bindings_restore_pending = false
        state.bindings_restore_failed = tostring(ok and err or accepted)
        state.action_message = "资源还原未排队：" .. state.bindings_restore_failed; return false
    end
    refresh_action_state()
    return true
end
M.restore_bindings = restore_bindings
local function load_json(path)
    if not json or not json.load_file then return nil, "REFramework JSON 接口不可用" end
    local ok, result = pcall(json.load_file, path)
    if not ok then return nil, tostring(result) end
    return type(result) == "table" and result or nil
end
local function is_ready()
    if not state.status or state.status.ready ~= true or state.status.protocol ~= 1 then return false end
    local heartbeat = state.status.heartbeatUnixSeconds
    if heartbeat == nil then return true end -- Protocol 1 backends predating heartbeat support.
    if type(heartbeat) ~= "number" then return false end
    local age = os.time() - heartbeat
    return age >= -5 and age <= 8
end
M.backend_ready = is_ready
local backend_unavailable_message = "文件编辑服务未连接。请运行 MotionVfx Live 启动入口；连接恢复后可直接重试，无需重新记录或 Reset。"
local function send(action, data, callback)
    if action=="begin_export" and workbench then return workbench:begin_export(data and data.scope or "efx")end
    if state.pending then state.message = "请等待当前请求；不能并发编辑同一文档。"; return false end
    if not is_ready() then state.message = backend_unavailable_message; return false end
    if not json or not json.dump_file then state.message = "REFramework JSON 写入接口不可用。"; return false end
    request_sequence = request_sequence + 1
    local request = { id = session .. "-" .. request_sequence, action = action }
    for k, value in pairs(data or {}) do request[k] = value end
    if state.document then request.revision = state.document.revision end
    local ok, result = pcall(json.dump_file, request_path, request)
    if not ok or result == false then state.message = "请求写入失败：" .. tostring(result); return false end
    state.pending = { id = request.id, action = action, started = os.time(), callback = callback, timeout = false,
        timeout_seconds = action == "open_game_resource" and 60 or 15 }
    state.response_frames = 5
    local operation_names = {cache_list="读取素材库",cache_import="导入素材",cache_reload="更新素材",
        cache_materialize="准备预加载",desktop_start="打开文件选择或录制",desktop_stop="结束录制"}
    if action~="file_browse"and action~="file_stat"then state.message = "正在处理：" .. (operation_names[action] or action)end
    return true
end
M.send = send

local function clear_selection()
    state.selected, state.fields, state.values = nil, {}, {}
    state.curves, state.curve_selection, state.curve_baseline = {}, nil, nil
    state.expression_curves = {}
    state.color_groups = {}
    state.curve_confirm_delete = false
    state.confirm_delete, state.field_page = false, 1
end
local curve_form_fields = { "curve_time", "curve_value", "curve_interpolation", "curve_out_x", "curve_out_y", "curve_in_x", "curve_in_y" }
local function curve_has_drafts()
    if not state.curve_baseline then return false end
    for _, key in ipairs(curve_form_fields) do if state[key] ~= state.curve_baseline[key] then return true end end
    return false
end
local function field_has_drafts()
    for _, field in ipairs(state.fields) do
        if field.editable == true and state.values[field.path] ~= nil and state.values[field.path] ~= tostring(field.value or "") then return true end
    end
    return false
end
local function load_curve_form(curve_index, channel_index, key_index)
    local curve = state.curves[curve_index]
    local channel = curve and (curve.channels or {})[channel_index]
    if not curve or not curve.editable or not channel then state.curve_selection, state.curve_baseline = nil, nil; return false end
    local key = (channel.keys or {})[key_index]
    state.curve_selection = { curve = curve_index, channel = channel_index, key = key and key_index or nil }
    state.curve_key_page = math.max(1, math.ceil(key_index / 40))
    state.curve_time, state.curve_value, state.curve_interpolation = key and key.time or "0", key and key.value or "0", key and key.interpolation or "Type2"
    state.curve_out_x, state.curve_out_y = key and key.outX or "0", key and key.outY or "0"
    state.curve_in_x, state.curve_in_y = key and key.inX or "0", key and key.inY or "0"
    state.curve_baseline = {}; for _, name in ipairs(curve_form_fields) do state.curve_baseline[name] = state[name] end
    state.curve_confirm_delete = false
    return true
end
local function select_curve(curve_index, channel_index, key_index)
    if field_has_drafts() or curve_has_drafts() then state.message = "请先提交或还原当前输入，再切换曲线关键帧。"; return false end
    return load_curve_form(curve_index, channel_index, key_index)
end
M.select_curve = select_curve
local function inspect(target, drafts)
    -- Internal refresh after a successful set passes an explicit drafts table.
    -- A manual selection/reload must never silently discard typed input.
    if drafts == nil then
        if curve_has_drafts() then state.message = "曲线有未提交输入；请先提交或还原，再切换 / 重新读取。"; return false end
        for _, field in ipairs(state.fields) do
            if field.editable == true and state.values[field.path] ~= nil and state.values[field.path] ~= tostring(field.value or "") then
                state.message = "当前属性有未提交输入；请先提交，或将输入改回原值后再切换 / 重新读取。"
                return false
            end
        end
    end
    local selected = copy_target(target)
    return send("inspect", { target = selected }, function(response)
        if response.ok then
            local previous = state.selected
            if not same_target(previous, selected) then
                state.field_page = 1
                state.confirm_delete=false
            end
            state.selected, state.fields, state.values = selected, response.fields or {}, {}
            if selected.kind == "group" then state.field_filter, state.field_modified_only = "", false end
            state.curves, state.curve_selection, state.curve_baseline = response.curves or {}, nil, nil
            state.expression_curves = response.expressionCurves or {}
            state.color_groups = response.colorGroups or {}
            for i, curve in ipairs(state.curves) do
                if curve.editable and #(curve.channels or {}) > 0 then load_curve_form(i, 1, 1); break end
            end
            for _, field in ipairs(state.fields) do
                state.values[field.path] = tostring(field.value or "")
                if drafts and drafts[field.path] ~= nil and field.editable == true then state.values[field.path] = drafts[field.path] end
            end
        end
    end)
end
M.inspect = inspect

local function has_drafts()
    return field_has_drafts() or curve_has_drafts()
end
M.has_drafts = has_drafts
local function curve_edit(operation)
    if field_has_drafts() then state.message = "普通字段有未提交输入；请先提交，或还原输入后再编辑曲线。"; return false end
    local selection = state.curve_selection
    local curve = selection and state.curves[selection.curve]
    local channel = curve and curve.channels[selection.channel]
    if not state.selected or not channel or not curve.editable then return false end
    local key = selection.key and channel.keys[selection.key]
    if operation ~= "add" and not key then state.message = "请先选择一个已有关键帧。"; return false end
    if operation == "remove" and (not state.curve_confirm_delete or curve_has_drafts()) then state.message = "请先还原本行输入并勾选确认删除。"; return false end
    local selected = copy_target(state.selected)
    return send("curve_key", { target = selected, path = curve.path, channel = channel.index,
        keyIndex = key and key.index, operation = operation, time = state.curve_time, value = state.curve_value,
        interpolation = state.curve_interpolation, outX = state.curve_out_x, outY = state.curve_out_y,
        inX = state.curve_in_x, inY = state.curve_in_y }, function(response)
        if response.ok then inspect(selected, {}) end
    end)
end
M.curve_edit = curve_edit
local function document_opened(reset_filters)
    state.active_tab = "edit"
    state.document_open_generation = (state.document_open_generation or 0) + 1
    if reset_filters then
        state.structure_filter, state.field_filter = "", ""
        state.field_modified_only = false
    end
end
local function open_document_now(path, logical_path)
    if state.pending or state.apply_requested then state.action_message = "请等待当前文档请求或资源应用完成。"; return false end
    return send("open", { path = path }, function(response)
        if not response.ok then return end
        clear_selection()
        state.published_path, state.published_revision = nil, nil
        -- Explicit disk imports are offline documents, never evidence of the
        -- resource bytes held by an actual EffectPlayer.
        state.target_resource_path, state.target_document_source = nil, nil
        state.resource_candidates, state.open_confirmation = {}, nil
        document_opened(true)
    end)
end
local function request_open_document(path, logical_path)
    if type(path) ~= "string" or path == "" then state.action_message = "没有可打开的本地文件路径。"; return false end
    if state.pending then state.action_message = "请等待当前文档请求完成。"; return false end
    if state.apply_requested then state.action_message = "请等待资源应用完成后再切换文档。"; return false end
    if state.document and state.document.source == path then
        state.target_resource_path, state.target_document_source = nil, nil
        state.active_tab, state.resource_candidates = "edit", {}
        document_opened(false)
        return true
    end
    if state.document and ((state.document.dirty and not state.document.sessionRetained) or has_drafts()) then
        state.open_confirmation = { path = path, resource_path = logical_path }
        state.action_message = "当前文档有未保存修改或未提交草稿；请保存后再打开，或明确确认切换。"
        return false
    end
    return open_document_now(path, logical_path)
end
M.request_open_document = request_open_document
local function open_game_resource_now(resource)
    if state.pending or state.apply_requested then state.action_message = "请等待当前文档请求或资源应用完成。"; return false end
    if not capture or type(capture.get_resource_source) ~= "function" then
        state.action_message = "当前捕获模块不支持动作来源；请完成还原后 Reset scripts，并重新记录角色动作。未打开本地文件。"
        return false
    end
    local path = resource and (resource.canonicalPath or resource.path)
    local ok, descriptor, holder_or_error = pcall(capture.get_resource_source, path, resource.latestSource and resource.latestSource.sourceId)
    if not ok then holder_or_error, descriptor = descriptor, nil end
    if not descriptor then
        state.action_message = "无法取得本次动作的资源路径：" .. tostring(holder_or_error) .. "。请重新记录动作。"
        return false
    end
    refresh_action_state()
    local epoch = state.capture_snapshot.sceneEpoch or (state.capture_snapshot.actor or {}).sceneEpoch
    if state.capture_error or not valid_capture_source(descriptor) or descriptor.sceneEpoch ~= epoch then
        state.action_message = "动作来源已过期或角色归属未确认，请重新记录动作。未打开本地文件。"
        return false
    end
    descriptor = copy_capture_source(descriptor)
    -- Resolve only the actual action path inside the game installation.
    -- The backend records the selected loose file or PAK entry; project
    -- materials and same-name files are never an action-editing fallback.
    return send("open_game_resource", { sourceDescriptor = descriptor, currentSceneEpoch = epoch }, function(response)
        if not response.ok then
            state.action_message = tostring(response.error or "未能读取游戏目录中的资源；当前文档已保留。")
            return
        end
        -- A successful backend open already committed its new document. Drop
        -- every old field/draft before validating asynchronous provenance so
        -- an old selection cannot write into that new document's revision.
        clear_selection()
        state.published_path, state.published_revision = nil, nil
        state.target_resource_path, state.target_document_source = nil, nil
        state.resource_candidates, state.open_confirmation = {}, nil
        document_opened(true)
        refresh_action_state()
        if not current_capture_source_matches(descriptor, descriptor.sharedTargetPath) then
            state.action_message = "读取期间动作来源已变化。后台文档已切换，旧字段选择与草稿已清除；未绑定角色资源，请重新选择当前动作记录。"
            return
        end
        if not response.document or response.document.sourceKind ~= "game_files" or
            not same_capture_source(descriptor, response.document.gameSource) then
            state.action_message = "后台返回的文档与请求的动作来源不一致；旧字段选择已清除，未绑定角色资源，请重新选择当前动作记录。"
            return
        end
        state.target_resource_path = descriptor.sharedTargetPath
        state.target_document_source = response.document.source
        state.resource_candidates, state.open_confirmation = {}, nil
        state.active_tab = "edit"
        state.action_message = "已按本次动作触发路径，从游戏目录读取特效文件。"
    end)
end
local function resolve_captured_resource(resource)
    if not resource or resource.localConfirmed ~= true then
        state.action_message = "角色归属未确认，不能用作角色资源编辑来源。"
        return false
    end
    if state.pending or state.apply_requested then state.action_message = "请等待当前请求完成。"; return false end
    if state.document and ((state.document.dirty and not state.document.sessionRetained) or has_drafts()) then
        state.open_confirmation = { captured_resource = resource, path = resource.path or resource.canonicalPath }
        state.action_message = "当前文档有未保存修改或未提交草稿；请先保存，或确认切换。游戏文件读取失败会保留当前文档。"
        return false
    end
    return open_game_resource_now(resource)
end
M.resolve_captured_resource = resolve_captured_resource

local function preview_result(result)
    state.preview_message = result.ok and ("引擎已接受：" .. result.operation .. "；请观察实际画面。") or ("预览失败：" .. tostring(result.message))
end
local function has_point_cloud()
    return type(runtime) == "table" and type(runtime.capabilities) == "table" and runtime.capabilities.point_cloud == true
end
local function point_cloud_options()
    if not state.point_cloud_enabled then return nil end
    if not has_point_cloud() then
        fail("当前运行时 " .. tostring(type(runtime) == "table" and runtime.version or "未知") ..
            " 不支持点云预览；需重置 REFramework 脚本或重启游戏以载入更新。仅重载编辑器不会更新共享运行时。")
    end
    if state.point_cloud_space ~= 1 and state.point_cloud_space ~= 2 then fail("点云空间必须为相对坐标或世界坐标") end
    local indices, seen = {}, {}
    for token in (state.point_cloud_indices .. ","):gmatch("(.-),") do
        local trimmed = token:match("^%s*(.-)%s*$")
        if not trimmed:match("^%d+$") then fail("点云索引须为逗号分隔的整数（0–31），不能有空项：" .. token) end
        local index = math.tointeger(tonumber(trimmed))
        if not index or index < 0 or index > 31 then fail("点云索引超出 0–31 范围：" .. trimmed) end
        if seen[index] then fail("点云索引重复：" .. index .. "；请每个索引只填写一次") end
        indices[#indices + 1], seen[index] = index, true
        if #indices > 32 then fail("最多提供 32 个点云索引") end
    end
    return { space = state.point_cloud_space == 1 and "local" or "world", indices = indices }
end
local function play(path)
    if not client then state.preview_message = "共享播放模块未加载：" .. tostring(runtime_error); return false end
    local ok, options = pcall(function()
        local scale = finite(state.scale, "缩放", 0.000001)
        local speed = finite(state.speed, "速度", 0)
        local parent = state.attachment == 2 and runtime.get_player_transform or state.attachment == 3 and runtime.get_weapon_transform or nil
        return { path = path, scale = scale, play_speed = speed,
            offset = { x = finite(state.x, "X"), y = finite(state.y, "Y"), z = finite(state.z, "Z") },
            resolve_parent = parent, bone_name = state.bone ~= "" and state.bone or nil,
            point_cloud = point_cloud_options() }
    end)
    if not ok then state.preview_message = tostring(options); return false end
    local id, err = client:play("preview", options, preview_result)
    if not id then state.preview_message = tostring(err); return false end
    state.preview_message = "预览已排队，等待引擎场景回调。"
    return true
end
M.play = play

local function poll()
    local now = os.time()
    if now ~= state.last_poll then
        state.last_poll = now
        state.status, state.status_error = load_json(status_path)
        if state.pfb_document_pid and state.status and state.status.pid and state.pfb_document_pid~=state.status.pid then
            state.pfb_document_pid=nil
            if workbench and workbench.pfb then
                workbench.pfb.document=nil;workbench.pfb.selected=nil;workbench.pfb.values={}
            end
            state.message="后台会话已变化，请重新打开 PFB 文件后继续编辑。"
        end
        if state.action_document and state.action_document_pid and state.status and state.status.pid and state.action_document_pid~=state.status.pid then
            state.action_document=nil;state.action_document_pid=nil
            if workbench then workbench.action_document=nil;workbench.action_proof=nil;workbench.slot_template=nil end
            if action_edit then action_edit=nil;state.action_edit_error="后台会话已变化，旧槽位文档不能继续提交。"end
        end
        if state.status and state.status.protocol ~= 1 then
            state.status_error = "后台协议不兼容；需要 protocol 1，收到 " .. tostring(state.status.protocol)
        elseif state.status and state.status.ready == true and not is_ready() then
            state.status_error = "后台心跳已过期或无效；请检查后台进程，当前不能提交编辑。"
        end
        if is_ready() and state.message == backend_unavailable_message then
            state.message = "文件编辑服务已连接，请重试刚才的打开操作。动作记录仍保留。"
        end
    end
    if not state.pending then return end
    local pending = state.pending
    state.response_frames = state.response_frames + 1
    if state.response_frames < 6 then return end
    state.response_frames = 0
    local response = load_json(response_path)
    if response and response.id == pending.id then
        state.pending = nil
        if response.document then state.document = response.document end
        if response.ok and response.desktop and response.desktop.id and response.desktop.statusFile and workbench then
            workbench.desktop_jobs=workbench.desktop_jobs or {}
            workbench.desktop_jobs[response.desktop.id]=response.desktop
        end
        if response.ok and response.actionDocument then
            state.action_document=response.actionDocument;state.action_document_pid=state.status and state.status.pid
        end
        if response.ok and response.pfbDocument then state.pfb_document_pid=state.status and state.status.pid end
        if response.catalog then state.catalog = response.catalog end
        if not response.ok then state.message="操作失败：" .. tostring(response.error or "后台未提供错误原因")
        elseif pending.action~="file_browse"and pending.action~="file_stat"then state.message="已完成：" .. pending.action end
        if response.ok and response.noteSaved then state.message = response.noteMessage or "分组备注已独立保存，不修改特效文件。" end
        if response.outputPath then state.message = state.message .. "\n输出：" .. response.outputPath end
        if pending.callback then
            local ok, err = pcall(pending.callback, response)
            if not ok then state.message = "响应处理失败：" .. tostring(err); log_warning(state.message) end
        end
    elseif now - pending.started >= pending.timeout_seconds and not pending.timeout then
        pending.timeout = true
        state.message = "请求超过 " .. pending.timeout_seconds .. " 秒，结果未知。仍在等待对应响应，不会自动重复编辑。请检查后台。"
    end
end
M.poll = poll

local function synchronize()
    -- Explicit user recovery after a timeout. Never resend an uncertain edit.
    if state.pending and not state.pending.timeout then return false end
    if state.pending and state.pending.action == "publish" and state.apply_requested then
        state.apply_requested = false
        state.apply_generation = state.apply_generation + 1
        state.action_message = "已取消超时发布的后续资源绑定；同步不会自动应用或播放。"
    end
    state.pending = nil
    return send("status", {}, function(response)
        if response.ok then
            state.target_resource_path, state.target_document_source = nil, nil
            state.document = response.document
            clear_selection()
            refresh_action_state()
            local source = response.document and response.document.gameSource
            if response.document and response.document.sourceKind == "game_files" and valid_capture_source(source) and
                current_resource_is_local(source.sharedTargetPath) and current_capture_source_matches(source, source.sharedTargetPath) then
                state.target_resource_path, state.target_document_source = source.sharedTargetPath, response.document.source
            end
            state.message = "已重新同步后台文档；超时编辑是否生效以当前文档为准。"
        end
    end)
end
M.synchronize = synchronize

local function require_method(td, names)
    if not td then fail("游戏类型不可用") end
    for _, name in ipairs(names) do
        local ok, method = pcall(function() return td:get_method(name) end)
        if ok and method then return name end
    end
    fail("当前游戏未提供接口：" .. table.concat(names, " / "))
end
local function get_scene()
    local manager = sdk.get_native_singleton("via.SceneManager")
    local td = sdk.find_type_definition("via.SceneManager")
    if not manager or not td then fail("SceneManager 不可用") end
    local scene = sdk.call_native_func(manager, td, "get_CurrentScene")
    if not scene then fail("当前没有可控制的场景") end
    return scene, tostring(scene:get_address())
end
local function scene_accessors()
    local td = sdk.find_type_definition("via.Scene")
    return { get_speed = require_method(td, { "get_TimeScale()", "get_TimeScale" }),
        set_speed = require_method(td, { "set_TimeScale(System.Single)", "set_TimeScale" }),
        get_pause = require_method(td, { "get_Pause()", "get_Pause", "get_Paused()", "get_Paused" }),
        set_pause = require_method(td, { "set_Pause(System.Boolean)", "set_Pause" }) }
end
local function read_scene(scene, methods)
    local native_paused = scene:call(methods.get_pause)
    if type(native_paused) ~= "boolean" then fail("场景暂停状态无法回读") end
    local speed = finite(scene:call(methods.get_speed), "实际场景速度", 0)
    -- Wilds can report its Pause flag without freezing the action/effect clock.
    -- Report zero simulation speed separately from that native flag.
    local result = { speed = speed, paused = speed == 0, native_paused = native_paused }
    local frame_ok, frame = pcall(function() return scene:call("get_FrameCount") end)
    if frame_ok and type(frame) == "number" then result.frame = frame end
    return result
end
-- Some effect work (including follow-trail/UV work) can advance outside the
-- scene clock. Pause the application's simulation clock as well, preserving
-- its independent baseline. Rendering and our input callbacks remain active.
local function application_clock()
    local app = sdk.get_native_singleton("via.Application")
    local td = sdk.find_type_definition("via.Application")
    if not app or not td then fail("全局时间接口不可用") end
    return { instance=app, td=td, identity=tostring(app),
        get_speed=require_method(td, {"get_GlobalSpeed()", "get_GlobalSpeed"}),
        set_speed=require_method(td, {"set_GlobalSpeed(System.Single)", "set_GlobalSpeed"}) }
end
local function read_application_speed(clock)
    return finite(sdk.call_native_func(clock.instance, clock.td, clock.get_speed), "实际全局速度", 0)
end
local function hold_application_time()
    local clock = application_clock()
    local original = state.global_time_original
    if original and original.identity ~= clock.identity then fail("全局时间对象已变化，保留原恢复记录") end
    local speed = read_application_speed(clock)
    if not original then
        clock.speed = speed
        state.global_time_original = clock -- Backup before a possibly partial setter.
    end
    if speed ~= 0 then
        sdk.call_native_func(clock.instance, clock.td, clock.set_speed, 0.0)
        speed = read_application_speed(clock)
    end
    state.global_time_actual = speed
    if speed ~= 0 then fail("全局速度未保持为零，不能确认拖尾暂停") end
    local ok, delta = pcall(sdk.call_native_func, clock.instance, clock.td, "get_DeltaTime")
    if ok and type(delta) == "number" then state.global_delta_time = delta end
end
local function release_application_time()
    local original = state.global_time_original
    if not original then return end
    local clock = application_clock()
    if original.identity ~= clock.identity then fail("全局时间对象已变化，不向新对象写入旧值") end
    local speed = read_application_speed(clock)
    if speed ~= original.speed then
        sdk.call_native_func(clock.instance, clock.td, clock.set_speed, original.speed + 0.0)
        speed = read_application_speed(clock)
    end
    state.global_time_actual = speed
    if original.speed == 0 and speed ~= 0 or math.abs(speed - original.speed) > 0.0001 then
        fail("全局速度恢复后与原值不一致；恢复记录仍保留")
    end
    state.global_time_original = nil
    state.global_delta_time = nil
end
-- TimeScale/GlobalSpeed zero does not stop every RibbonFollow update in Wilds.
-- Use the engine's update gate, without hiding, killing or restarting players.
local function effect_update_accessors()
    local name = "via.effect.detail.PlayerManager"
    local manager = sdk.get_native_singleton(name)
    local td = sdk.find_type_definition(name)
    if not manager or not td then fail("当前游戏没有可用的特效更新管理器") end
    return { instance = manager, td = td, identity = tostring(manager),
        get_enabled = require_method(td, {"get_EnableUpdate()", "get_EnableUpdate"}),
        set_enabled = require_method(td, {"set_EnableUpdate(System.Boolean)", "set_EnableUpdate"}) }
end
local function read_effect_update(access)
    local enabled = sdk.call_native_func(access.instance, access.td, access.get_enabled)
    if type(enabled) ~= "boolean" then fail("无法回读特效更新开关") end
    state.effect_update_actual = enabled
    return enabled
end
local function hold_effect_update()
    local access = effect_update_accessors()
    local original = state.effect_update_original
    if original and original.identity ~= access.identity then fail("特效管理器已变化，保留原恢复记录") end
    local enabled = read_effect_update(access)
    if not original then
        access.enabled = enabled
        state.effect_update_original = access -- Save before a potentially partial setter.
    end
    if enabled then
        sdk.call_native_func(access.instance, access.td, access.set_enabled, false)
        enabled = read_effect_update(access)
    end
    if enabled then fail("特效更新开关仍开启，不能确认拖尾暂停") end
    state.effect_update_frames = (state.effect_update_frames or 0) + 1
end
local function release_effect_update()
    local original = state.effect_update_original
    if not original then return end
    local access = effect_update_accessors()
    if original.identity ~= access.identity then fail("特效管理器已变化，不向新对象写入旧值") end
    if read_effect_update(access) ~= original.enabled then
        sdk.call_native_func(access.instance, access.td, access.set_enabled, original.enabled)
    end
    if read_effect_update(access) ~= original.enabled then fail("特效更新恢复后与原值不一致；恢复记录仍保留") end
    state.effect_update_original = nil
end
local function release_pause_systems()
    -- Independent recovery: a failed effect restore must not prevent releasing
    -- the application clock, and vice versa. Each failed backup stays retryable.
    local errors = {}
    for _, release in ipairs({release_effect_update, release_application_time}) do
        local ok, err = pcall(release)
        if not ok then errors[#errors + 1] = tostring(err) end
    end
    if #errors > 0 then fail(table.concat(errors, "；")) end
end
local function restore_scene()
    local original = state.scene_original
    -- A failed restoration must not be followed by the hold writing zero again.
    if state.scene_pause then state.scene_pause.suspended = true end
    local ok, err = pcall(function()
        -- Restore the global clock even when the old scene has been destroyed.
        -- A stale scene must never leave the entire new scene globally frozen.
        release_pause_systems()
        if not original then return end
        local scene, address = get_scene()
        if address ~= original.address then fail("场景已切换，不向新场景写入旧状态") end
        if original.changed_speed then scene:call(original.methods.set_speed, original.speed + 0.0) end
        if original.changed_pause then scene:call(original.methods.set_pause, original.paused) end
        local actual = read_scene(scene, original.methods)
        if (original.changed_speed and (math.abs(actual.speed - original.speed) > 0.0001 or original.speed == 0 and actual.speed ~= 0)) or
            (original.changed_pause and actual.native_paused ~= original.paused) then fail("恢复后状态与原值不一致") end
        state.scene_actual = actual
    end)
    if ok then
        state.scene_original, state.scene_pause, state.scene_control_error = nil, nil, nil
        state.scene_message = "已恢复接管前的场景时间状态。"
    else
        state.scene_control_error = tostring(err)
        state.scene_message = "场景恢复失败：" .. tostring(err); log_warning(state.scene_message)
    end
    return ok
end
M.restore_scene = restore_scene
local function change_scene(command)
    if command.operation == "restore" then restore_scene(); return end
    local ok, err = pcall(function()
        local scene, address = get_scene()
        local methods = scene_accessors()
        local before = read_scene(scene, methods)
        if command.operation == "read" then state.scene_actual = before; return end
        if state.scene_original and state.scene_original.address ~= address then
            fail("场景已切换；旧场景的恢复记录仍保留，未向新场景写入旧状态")
        end
        if command.operation ~= "speed" and command.operation ~= "pause" then fail("未知场景操作") end
        if command.operation == "pause" and type(command.value) ~= "boolean" then fail("暂停状态必须为布尔值") end
        local speed = command.operation == "speed" and finite(command.value, "场景速度", 0.01) or nil
        if not state.scene_original then
            state.scene_original = { address = address, methods = methods, speed = before.speed, paused = before.native_paused }
        end
        local expected_speed, clear_hold
        if command.operation == "speed" then
            if state.scene_pause then state.scene_pause.resume_speed = speed end
            expected_speed = state.scene_pause and 0 or speed
            state.scene_original.changed_speed = true
            scene:call(methods.set_speed, expected_speed + 0.0)
        elseif command.value == true then
            -- Keep the first resume speed on repeated Pause, including when
            -- the game temporarily overwrote zero in a subsequent frame.
            if not state.scene_pause then
                state.scene_pause = { address = address, resume_speed = before.speed, suspended = true }
                state.scene_pause_frames = 0
                state.effect_update_frames = 0
            end
            expected_speed = 0
            state.scene_original.changed_speed = true
            scene:call(methods.set_speed, 0.0)
        else
            release_pause_systems()
            if state.scene_pause then
                expected_speed = state.scene_pause.resume_speed
                state.scene_original.changed_speed = true
                scene:call(methods.set_speed, expected_speed + 0.0)
                clear_hold = true
            end
            if before.native_paused then
                state.scene_original.changed_pause = true
                scene:call(methods.set_pause, false)
            end
        end
        local actual = read_scene(scene, methods)
        state.scene_actual = actual
        if expected_speed ~= nil and (expected_speed == 0 and actual.speed ~= 0 or
            math.abs(actual.speed - expected_speed) > 0.0001) then fail("场景速度未变为请求值") end
        if command.operation == "pause" and command.value == false and actual.native_paused then fail("场景原生暂停标志未解除") end
        if command.operation == "pause" and command.value == true then
            hold_application_time()
            hold_effect_update()
        end
        if clear_hold then state.scene_pause = nil
        elseif state.scene_pause then state.scene_pause.suspended = false end
        state.scene_control_error = nil
    end)
    if not ok then
        if state.scene_pause then state.scene_pause.suspended = true end
        state.scene_control_error = tostring(err)
        state.scene_message = "场景操作失败：" .. tostring(err)
    elseif state.scene_pause and state.scene_pause.suspended then
        state.scene_message = "暂停保持已停止；请重试暂停、继续或恢复场景。" .. (state.scene_control_error and (" " .. state.scene_control_error) or "")
    elseif state.scene_pause then
        state.scene_message = "场景时间已暂停，特效更新已停止；继续时恢复 " .. tostring(state.scene_pause.resume_speed) .. "×。"
    else state.scene_message = "场景速度已回读；动作与特效是否同步请观察画面。" end
    state.scene_history[#state.scene_history + 1] = { operation = command.operation, value = command.value,
        ok = ok, observedAt = os.time(), speed = state.scene_actual and state.scene_actual.speed,
        nativePaused = state.scene_actual and state.scene_actual.native_paused,
        globalSpeed = state.global_time_actual,
        effectUpdateEnabled = state.effect_update_actual,
        resumeSpeed = state.scene_pause and state.scene_pause.resume_speed,
        confirmedZeroFrames = state.scene_pause_frames, error = not ok and tostring(err) or nil }
    if #state.scene_history > 16 then table.remove(state.scene_history, 1) end
end
local function maintain_scene_pause()
    local hold = state.scene_pause
    if not hold or hold.suspended then return end
    local ok, err = pcall(function()
        local scene, address = get_scene()
        if address ~= hold.address then fail("场景已切换，已停止维持旧场景暂停；恢复记录仍保留") end
        local methods = state.scene_original.methods
        local actual = read_scene(scene, methods)
        if actual.speed ~= 0 then
            scene:call(methods.set_speed, 0.0)
            actual = read_scene(scene, methods)
        end
        state.scene_actual = actual
        if actual.speed ~= 0 then fail("暂停期间时间倍率未保持为零") end
        hold_application_time()
        hold_effect_update()
        state.scene_pause_frames = state.scene_pause_frames + 1
    end)
    if not ok then
        hold.suspended = true
        -- Don't keep a new scene or an unsupported scene globally frozen.
        -- Preserve a failed global restoration instead of dropping its backup.
        local released, release_error = pcall(release_pause_systems)
        if not released then err = tostring(err) .. "；暂停接管恢复失败：" .. tostring(release_error) end
        state.scene_control_error = tostring(err)
        state.scene_message = "暂停保持失败：" .. tostring(err)
        log_warning(state.scene_message)
    end
end
local function queue_scene(operation, value)
    if #state.scene_queue >= 16 then state.scene_message = "场景操作队列已满。"; return false end
    state.scene_queue[#state.scene_queue + 1] = { operation = operation, value = value }
    return true
end
M.queue_scene = queue_scene

local function read_effect(instance)
    local actual = { read_errors = {} }
    for _, field in ipairs({
        { "speed", "get_PlaySpeed", "number" }, { "paused", "get_Paused", "boolean" },
        { "ready_resource", "get_ReadyResource", "boolean" }, { "running", "get_Running", "boolean" },
        { "valid", "get_Valid", "boolean" }, { "emitters", "get_EmitterInstanceNum", "number" },
        { "resource_path", "get_ResourcePath", "string" }, { "player_state", "get_State", "number" },
        { "culled", "get_Culled", "boolean" }, { "group_enabled", "get_GroupEnabled", "boolean" },
    }) do
        local ok, value = pcall(function() return instance.player:call(field[2]) end)
        if ok and type(value) == field[3] then
            actual[field[1]] = value
        else
            actual.read_errors[field[1]] = ok and ("Unexpected " .. type(value)) or tostring(value)
        end
    end
    return actual
end

local function effect_action(name)
    if not client then state.preview_message = "共享播放模块未加载。"; return end
    local id, err = client:action("preview", name, function(result)
        preview_result(result)
        if result.ok then
            local ok, actual = pcall(function() return read_effect(client:get_instance("preview")) end)
            if ok then state.effect_actual = actual else state.preview_message = state.preview_message .. " 状态回读失败：" .. tostring(actual) end
        end
    end)
    if not id then state.preview_message = tostring(err) end
end
local function queue_effect_speed(value)
    local ok, speed = pcall(finite, value, "预览速度", 0)
    if not ok then state.preview_message = tostring(speed); return false end
    if #state.effect_queue >= 16 then state.preview_message = "预览操作队列已满。"; return false end
    state.speed = tostring(speed)
    state.effect_queue[#state.effect_queue + 1] = { speed = speed }
    return true
end
M.queue_effect_speed = queue_effect_speed
local function apply_effect_commands()
    local commands = state.effect_queue
    state.effect_queue = {}
    for _, command in ipairs(commands) do
        local ok, err = pcall(function()
            local instance = client and client:get_instance("preview")
            if not instance or not instance.player then fail("没有当前预览实例；该速度将用于下次播放") end
            instance.player:call("set_PlaySpeed", command.speed + 0.0)
            local speed = finite(instance.player:call("get_PlaySpeed"), "实际特效速度", 0)
            if math.abs(speed - command.speed) > 0.0001 then fail("特效速度未变为请求值") end
            state.effect_actual = read_effect(instance)
        end)
        state.preview_message = ok and "当前预览速度已更新并回读。" or ("速度调整失败：" .. tostring(err))
    end
end
M.apply_effect_commands = apply_effect_commands

local function input(label, key)
    if imgui.set_next_item_width then imgui.set_next_item_width(math.max(120, available_width() - 240)) end
    local changed, value = text_input(label, state[key])
    if changed then state[key] = value end
end
local function button(label, fn, kind, width)
    local tint = kind == "primary" and palette.primary or kind == "selected" and rgb(48, 95, 139) or
        kind == "warning" and rgb(106, 70, 35) or nil
    local visible, suffix = label:match("^(.-)(##.*)$")
    visible, suffix = visible or label, suffix or ""
    local display = wrapped(visible, math.max(80, (width or available_width()) - 24))
    local _, breaks = display:gsub("\n", "")
    if imgui.set_button_kind then imgui.set_button_kind(kind) end
    if tint and imgui.push_style_color then imgui.push_style_color(colors.button, tint) end
    local ok, clicked = pcall(imgui.button, display .. suffix, { width or 0, math.max(32, 28 * state.font_scale * (breaks + 1)) })
    if tint and imgui.push_style_color then imgui.pop_style_color(1) end
    if not ok then error(clicked, 0) end
    if clicked then fn() end
end
local function button_row(items)
    local width, used = available_width(), 0
    for _, item in ipairs(items) do
        local visible = item[1]:match("^(.-)##") or item[1]
        local measured = imgui.calc_text_size and dimension(imgui.calc_text_size(visible), "x", 120) or 120
        local needed = item[4] or measured + 24
        if used > 0 and used + needed + 8 <= width then imgui.same_line(); used = used + 8
        else used = 0 end
        button(item[1], item[2], item[3], item[4])
        used = used + needed
    end
end
local function mutate(action, data, refresh_fields, committed_paths)
    if curve_has_drafts() then state.message = "曲线有未提交输入；请先提交或还原本行输入。"; return false end
    if not refresh_fields and field_has_drafts() then
        state.message = "有未提交的字段或分组名称；请先提交或还原，再执行结构操作或撤销。"; return false
    end
    local selected = copy_target(state.selected)
    local drafts = {}
    if refresh_fields then
        for _, field in ipairs(state.fields) do
            local value = state.values[field.path]
            if field.editable == true and value ~= nil and value ~= tostring(field.value or "") and
                (not data or field.path ~= data.path) and not (committed_paths and committed_paths[field.path]) then
                drafts[field.path] = value
            end
        end
    end
    return send(action, data, function(response)
        if response.ok then
            if action == "set" and data then
                state.last_submitted_field = { source = state.document and state.document.source,
                    target = copy_target(data.target), path = data.path, value = data.value, revision = response.revision }
            end
            if refresh_fields and selected then inspect(selected, drafts) else clear_selection() end
            if action=="restore_entry" and state.target_resource_path then
                local applied=(state.bindings_snapshot or {}).appliedResources or {}
                if applied[state.target_resource_path:lower()] and M.publish_document and not M.publish_document(true) then
                    state.message="特效块已还原，但游戏更新未完成："..tostring(state.action_message)
                end
            end
        end
    end)
end
local function draw_catalog()
    text("本地材料：要编辑角色实际触发的特效，请从「角色动作」列表选择。", palette.warning)
    button("刷新资源列表##mvl_catalog", function() send("catalog") end)
    input("筛选资源##mvl_filter", "filter")
    input("EFX 本地文件路径##mvl_local_path", "local_path")
    button("打开文件##mvl_open", function()
        request_open_document(state.local_path, nil)
    end)
    text("打开另一文件会切换后台当前文档；先保存需要保留的改动。")
    child("资源列表##mvl_files", { 0, 280 }, function()
    local matched = 0
    for i, item in ipairs(state.catalog) do
        local path = type(item) == "table" and item.path or tostring(item)
        local name = type(item) == "table" and (item.name or path) or path
        if path and (state.filter == "" or (name .. " " .. path):lower():find(state.filter:lower(), 1, true)) then
            matched = matched + 1
            button(name .. "##mvl_file_" .. i, function() state.local_path = path end)
        end
    end
    if matched == 0 then text("没有匹配资源。可直接输入本地文件路径。") end
    end)
end
local function describe(value)
    if type(value) ~= "table" then return tostring(value or "未知") end
    local parts = {}
    for key, item in pairs(value) do
        if type(item) ~= "table" and type(item) ~= "function" and type(item) ~= "userdata" then
            parts[#parts + 1] = tostring(key) .. "=" .. tostring(item)
        end
    end
    table.sort(parts)
    return table.concat(parts, "; ")
end
local function action_label(action)
    local m = action.motion or {}
    return (action.componentKind == "weapon" and "武器" or "角色") .. " · 层 " .. tostring(m.layer or "?") ..
        " · Bank " .. tostring(m.bankId or "?") .. " / Motion " .. tostring(m.motionId or "?") ..
        (m.name and m.name ~= "" and (" · " .. m.name) or "")
end
local function action_details(snapshot, id)
    local selected, linked, paths = nil, {}, {}
    for _, record in ipairs(snapshot.actions or {}) do if record.id == id then selected = record; break end end
    if not selected then return nil, linked, paths end
    for _, ev in ipairs(snapshot.events or {}) do
        if ev.localConfirmed == true and ev.sceneEpoch == selected.sceneEpoch then
            for _, link in ipairs(ev.actionLinks or {}) do
                if link.actionId == id then
                    linked[#linked + 1] = {eventId = ev.id, association = link.association, motionFrame = link.motionFrame,
                        lastMotionFrame = link.lastMotionFrame, observations = link.observations,
                        observedFrame = link.observedFrame, effectDomain = ev.effectDomain, effectId = ev.effectId,
                        containerId = ev.containerId, elementId = ev.elementId, externalIndex = ev.externalIndex,
                        sourceKind = ev.sourceKind, requested = ev.requested or {}, created = ev.created or {},
                        motion = ev.motion}
                    for _, list in ipairs({ev.requested or {}, ev.created or {}}) do
                        for _, resource in ipairs(list) do
                            local path = normalized_effect_path(resource.canonicalPath or resource.path)
                            if path and path ~= "" then paths[path] = true end
                        end
                    end
                end
            end
        end
    end
    return selected, linked, paths
end
M.action_details = action_details
local function draw_motion_history(snapshot)
    text("当前动作", palette.accent)
    local primary
    for _, current in ipairs(snapshot.currentActions or {}) do
        if current.componentKind == "character" and (current.motion or {}).layer == 0 then primary = current; break end
    end
    if primary then
        text(action_label(primary))
        text("动画帧：" .. tostring(primary.motion.frame or "未知"))
    else text(snapshot.actionStatus or "更新捕获模块至 1.4.0 后显示当前动作。", palette.muted) end
    section("全部当前动画层##mvl_current_motion_layers", function()
        for _, current in ipairs(snapshot.currentActions or {}) do
            text(action_label(current) .. "；帧 " .. tostring((current.motion or {}).frame or "?"))
        end
    end)
    input("筛选动作编号 / 原始名称##mvl_action_filter", "action_filter")
    local changed, show_all = imgui.checkbox("显示武器动画及附加层##mvl_all_action_layers", state.show_all_action_layers)
    if changed then state.show_all_action_layers = show_all end
    button("查看全部特效资源##mvl_action_all", function() state.selected_action_id = nil end)
    local history = snapshot.actions or {}
    local main_count = 0
    for _, record in ipairs(history) do
        if record.componentKind == "character" and (record.motion or {}).layer == 0 then main_count = main_count + 1 end
    end
    text("角色主动作 " .. main_count .. " 条；全部动画 " .. #history .. " 条。连续循环合并，其他动作重播分别记录。", palette.muted)
    child("动作顺序##mvl_action_history", {0, 200}, function()
        for i = #history, 1, -1 do
            local record = history[i]
            local label = action_label(record)
            if (state.show_all_action_layers or (record.componentKind == "character" and (record.motion or {}).layer == 0)) and
                (state.action_filter == "" or label:lower():find(state.action_filter:lower(), 1, true)) then
                button("#" .. i .. "  " .. label .. "##mvl_action_select_" .. record.id,
                    function() state.selected_action_id = record.id end,
                    state.selected_action_id == record.id and "selected" or nil)
            end
        end
    end)
    if not state.selected_action_id then return nil end
    local selected, links, paths = action_details(snapshot, state.selected_action_id)
    if not selected then
        text("所选动作已清空或超过记录上限，请重新选择。", palette.warning)
        return {}
    end
    text("已选：" .. action_label(selected), palette.accent)
    text("记录到的动画帧：" .. tostring(selected.firstMotionFrame or "?") .. " → " .. tostring(selected.lastMotionFrame or "?") ..
        "；" .. (selected.closed and "片段已结束" or "仍在播放"))
    text("动作文件：" .. tostring((selected.motion or {}).bankResourcePath or "尚未读到路径"), palette.muted)
    if (selected.loopWraps or 0) > 0 then text("此连续循环片段已合并 " .. selected.loopWraps .. " 次帧回绕。", palette.muted) end
    text("编号来自游戏动画层，尚未自动对应中文招式名；资源修改仍影响所有引用它的动作。", palette.warning)
    if #links == 0 then text("此片段未记录到关联特效请求；可保留该动作编号用于后续添加。") end
    section("触发帧与调用编号##mvl_action_link_details", function()
        for _, link in ipairs(links) do
            text((link.association == "direct_motion_request" and "动作直接触发" or "同期出现 · 尚未证明由此动作触发") ..
                "；动画帧 " .. tostring(link.motionFrame or "?"), link.association == "direct_motion_request" and palette.success or palette.warning)
            if (link.observations or 1) > 1 then
                text("同一容器观察 " .. link.observations .. " 次；末次帧 " .. tostring(link.lastMotionFrame or "?") ..
                    "（观察次数不等于生成次数）", palette.muted)
            end
            text("事件 " .. tostring(link.eventId) .. "；容器 " .. tostring(link.containerId or "?") ..
                " / 元素 " .. tostring(link.elementId or "?") .. "；状态域 " .. tostring(link.effectDomain or "?") ..
                " / 调用 " .. tostring(link.effectId or "?"))
            for _, resource in ipairs(link.created) do text("已创建：" .. tostring(resource.path)) end
            if #link.created == 0 then
                for _, resource in ipairs(link.requested) do text("已请求：" .. tostring(resource.path)) end
            end
        end
    end)
    return paths
end
local function draw_actions()
    text("记录角色实际动作 → 选择实际请求的 EFX → 编辑 → 应用资源 → 重做动作验证。")
    if not capture then text("动作记录模块未加载：" .. tostring(capture_error)); return end
    local snapshot = state.capture_snapshot or {}
    text("记录状态：" .. (snapshot.recording and "正在记录" or "已停止"), snapshot.recording and palette.success or palette.muted)
    if capture.version == "1.0.0" then
        text("捕获模块待更新：先还原资源试改并恢复场景，确认完成后用 REFramework 的 Reset scripts 载入 1.1.0。只重载界面不会更新捕获模块。")
    end
    button("开始记录角色动作##mvl_capture_start", function() capture_command("start") end, "primary")
    imgui.same_line()
    button("结束记录##mvl_capture_stop", function() capture_command("stop") end)
    imgui.same_line()
    button("清空记录##mvl_capture_clear", function() capture_command("clear") end)
    text(state.action_message)
    local action_paths = draw_motion_history(snapshot)
    for _, err in ipairs(snapshot.errors or {}) do text("采集诊断：" .. tostring(err)) end
    if state.capture_error then text("快照读取失败：" .. state.capture_error) end
    section("角色与采集诊断##mvl_capture_diagnostics", function()
        text("角色与装备：" .. describe(snapshot.actor))
        text("捕获模块 " .. tostring(capture.version or "未知") .. "；观察入口 " .. tostring(snapshot.installedHooks or "?") .. "/" .. tostring(snapshot.requestedHooks or "?"))
        text("资源按路径合并显示次数；未解析、未确认角色归属及采集损失不会当成完整调用。")
        text("采集计数：" .. describe(snapshot.counters))
    end)
    local confirmed, unconfirmed = 0, 0
    for _, resource in ipairs(snapshot.resources or {}) do
        if resource.localConfirmed == true then confirmed = confirmed + 1 else unconfirmed = unconfirmed + 1 end
    end
    local changed, show_unconfirmed = imgui.checkbox("显示归属未确认资源##mvl_show_unconfirmed_resources", state.show_unconfirmed_resources)
    if changed then state.show_unconfirmed_resources = show_unconfirmed end
    text("角色归属已确认 " .. confirmed .. " 个；未确认 " .. unconfirmed .. " 个（记录与诊断均保留）。")
    child("角色动作资源##mvl_action_resources", { 0, 280 }, function()
    for i, resource in ipairs(snapshot.resources or {}) do
        if (resource.localConfirmed == true or state.show_unconfirmed_resources) and
            (not action_paths or action_paths[normalized_effect_path(resource.canonicalPath or resource.path)]) then
            local path = tostring((resource.latestSource or {}).actualPath or resource.path or resource.canonicalPath or "未解析资源路径")
            text(path:match("[^/\\]+$") or path, palette.accent)
            text("次数 " .. tostring(resource.count or 0) .. "；角色归属 " .. (resource.localConfirmed == true and "已直接确认" or "未确认"))
            button((resource.localConfirmed == true and "选择并编辑" or "查看资源（角色归属未确认）") .. "##mvl_capture_resource_" .. i,
                function() resolve_captured_resource(resource) end, resource.localConfirmed == true and "primary" or nil)
            section("路径与触发信息##mvl_capture_info_" .. i, function()
                text(path, palette.muted)
                text("证据：" .. (type(resource.evidence) == "table" and table.concat(resource.evidence, "; ") or tostring(resource.evidence or "未提供")))
                local source = resource.latestSource
                text(source and ("动作触发路径已记录；选择后从游戏目录读取：" .. tostring(source.sourceId)) or "尚无可用的动作来源，请重新记录动作。")
                text("动作：" .. describe(resource.motion) .. "；关联 " .. tostring(resource.motionAssociation or "unknown"))
            end)
            imgui.separator()
        end
    end
    if #(snapshot.resources or {}) == 0 then text("尚无实际资源请求；先完成一次动作并结束记录。") end
    if confirmed == 0 and unconfirmed > 0 and not state.show_unconfirmed_resources then
        text("尚无已确认角色归属的资源；可勾选上方开关查看未确认项及其证据。")
    end
    end)
    section("原始事件与未解析项##mvl_capture_events", function()
        local events = snapshot.events or {}
        text("共 " .. #events .. " 条；此面板显示最近 80 条，完整采集数量与损失见上方计数。")
        for i = math.max(1, #events - 79), #events do text(describe(events[i])) end
    end)
end
local tree_match_cache = {}
local function draw_tree()
    local document = state.document
    if not document then text("先打开一个 EFX 文件。") return end
    text("特效结构", palette.accent)
    input("查找分组 / 块 / 属性##mvl_structure_filter", "structure_filter")
    button("文件属性（骨骼、关系、表达式、分组）##mvl_document_fields", function() inspect({ kind = "document", index = 0 }) end)
    section("添加特效块##mvl_manage_entries", function()
        input("名称##mvl_entry_name", "entry_name")
        button("添加特效块##mvl_add_entry", function() mutate("add_entry", { name = state.entry_name }) end)
    end)
    imgui.separator()
    local filter, matched = state.structure_filter:lower(), 0
    if tree_match_cache.document ~= document or tree_match_cache.revision ~= document.revision or tree_match_cache.filter ~= filter then
        tree_match_cache = { document = document, revision = document.revision, filter = filter, nodes = {} }
    end
    local matches
    local function embedded_matches(embedded)
        if not embedded then return false end
        for _, kind in ipairs({ "entries", "actions" }) do
            for _, node in ipairs(embedded[kind] or {}) do if matches(node) then return true end end
        end
        return false
    end
    matches = function(node, inherited)
        if inherited or filter == "" then return true, true end
        local cached = tree_match_cache.nodes[node]
        if cached then return cached[1], cached[2] end
        local whole = (tostring(node.index) .. " " .. tostring(node.name or "")):lower():find(filter, 1, true)
        if whole then tree_match_cache.nodes[node] = { true, true }; return true, true end
        for _, attribute in ipairs(node.attributes or {}) do
            if parameter_matches(attribute.type,filter) or embedded_matches(attribute.embedded) then
                tree_match_cache.nodes[node] = { true, false }; return true, false
            end
        end
        tree_match_cache.nodes[node] = { false, false }
        return false, false
    end
    local function reveal_search()
        if filter ~= "" and imgui.set_next_item_open then imgui.set_next_item_open(true) end
    end
    local function draw_node(kind, node, inherited, occurrence)
        local visible, whole = matches(node, inherited)
        if not visible then return end
        matched = matched + 1
        reveal_search()
        local suffix = occurrence and ("_member" .. occurrence) or ""
        local owner = node.target or { kind = kind, index = node.index }
        local scope = kind .. node.index .. embedded_key(owner)
        section("[" .. tostring(node.index) .. "] " .. tostring(node.name or kind) .. "##mvl_" .. scope .. suffix, function()
            local selected=state.selected
            button("选择并编辑字段##mvl_select_" .. scope, function() inspect(owner) end,
                same_target(selected, owner) and "selected" or nil)
            for _, attribute in ipairs(node.attributes or {}) do
                local attribute_match = whole or parameter_matches(attribute.type,filter)
                if attribute_match or embedded_matches(attribute.embedded) then
                    local selected = state.selected
                    local target = attribute.target or copy_target(owner)
                    if not attribute.target then target.attributeIndex = attribute.index end
                    local active = same_target(selected, target)
                    local attribute_scope = kind .. node.index .. "_" .. attribute.index .. embedded_key(target)
                    button("属性 " .. attribute.index .. ": " .. parameter_label(attribute.type) .. "##mvl_attr_" .. attribute_scope,
                        function() inspect(target) end, active and "selected" or nil)
                    if attribute.embedded then
                        reveal_search()
                        section("内嵌特效 · " .. parameter_label(attribute.type) .. "##mvl_embedded_" .. attribute_scope, function()
                            for _, nested in ipairs(attribute.embedded.entries or {}) do draw_node("entry", nested, attribute_match) end
                            if #(attribute.embedded.actions or {}) > 0 then
                                reveal_search()
                                section("内嵌动作 Actions##mvl_embedded_actions_" .. attribute_scope, function()
                                    for _, nested in ipairs(attribute.embedded.actions) do draw_node("action", nested, attribute_match) end
                                end)
                            end
                        end)
                    end
                end
            end
        end)
    end
    if state.structure_document_source ~= document.source then
        state.structure_document_source = document.source
        if imgui.set_next_item_open then imgui.set_next_item_open(true) end
    else reveal_search() end
    section((document.name or "特效文件 EFX") .. "##mvl_efx_file", function()
        if #(document.groups or {}) > 0 then
            local entries, assigned = {}, {}
            for _, node in ipairs(document.entries or {}) do entries[node.index] = node end
            for _, group in ipairs(document.groups) do
                local searchable = tostring(group.name or "") .. " " .. tostring(group.note or "") .. " " .. tostring(group.displayName or "")
                local group_match = filter ~= "" and searchable:lower():find(filter, 1, true)
                local visible = filter == "" or group_match
                for _, index in ipairs(group.entries or {}) do
                    if entries[index] then
                        assigned[index] = true
                        if matches(entries[index], group_match) then visible = true end
                    end
                end
                if visible then
                    if group_match then matched = matched + 1 end
                    reveal_search()
                    local native_name = tostring(group.name or "未命名")
                    local caption = group.note and group.note ~= "" and (group.note .. "（原名：" .. native_name .. "）") or native_name
                    -- Notes are display text, never ImGui ID syntax or native group identifiers.
                    section("Group [" .. group.index .. "] " .. caption:gsub("#", "＃") .. " · " .. #(group.entries or {}) .. " 个块###mvl_group_" .. group.index, function()
                        button("编辑分组备注##mvl_group_rename_" .. group.index, function() inspect({ kind = "group", index = group.index }) end)
                        if #(group.entries or {}) == 0 then text("空分组", palette.muted) end
                        for member, index in ipairs(group.entries or {}) do
                            if entries[index] then draw_node("entry", entries[index], group_match, member) end
                        end
                    end)
                end
            end
            local remaining = {}
            for _, node in ipairs(document.entries or {}) do
                if not assigned[node.index] and matches(node) then remaining[#remaining + 1] = node end
            end
            if #remaining > 0 then
                reveal_search()
                section("未分组 · " .. #remaining .. " 个块##mvl_ungrouped", function()
                    for _, node in ipairs(remaining) do draw_node("entry", node) end
                end)
            end
        else
            reveal_search()
            section("特效块 Entries", function()
                for _, node in ipairs(document.entries or {}) do draw_node("entry", node) end
            end)
        end
        if #(document.actions or {}) > 0 then
            reveal_search()
            section("动作 Actions", function()
                for _, node in ipairs(document.actions) do draw_node("action", node) end
            end)
        end
    end)
    if filter ~= "" and matched == 0 then text("没有匹配的分组、块或属性。", palette.muted) end
end
local function field_modified(field)
    return field.editable == true and state.values[field.path] ~= nil and state.values[field.path] ~= tostring(field.value or "")
end
local function selected_title(selected)
    if selected.kind == "document" then return "文件属性" end
    if selected.kind == "group" then
        for _, group in ipairs(state.document and state.document.groups or {}) do
            if group.index == selected.index then
                local native_name = tostring(group.name or "未命名")
                return "Group [" .. selected.index .. "] " .. (group.note and group.note ~= "" and (group.note .. "（原名：" .. native_name .. "）") or native_name)
            end
        end
        return "Group [" .. selected.index .. "]"
    end
    local document, ancestors = state.document, {}
    for _, hop in ipairs(selected.embeddedPath or {}) do
        local next_document
        local nodes = document and (hop.kind == "entry" and document.entries or document.actions) or {}
        for _, node in ipairs(nodes or {}) do
            if node.index == hop.index then
                ancestors[#ancestors + 1] = tostring(node.name or hop.kind) .. " [" .. hop.index .. "]"
                for _, attribute in ipairs(node.attributes or {}) do
                    if attribute.index == hop.attributeIndex then
                        ancestors[#ancestors + 1] = parameter_label(attribute.type)
                        next_document = attribute.embedded
                        break
                    end
                end
                break
            end
        end
        document = next_document
    end
    local prefix = #ancestors > 0 and (table.concat(ancestors, " / ") .. " / ") or ""
    local nodes = document and (selected.kind == "entry" and document.entries or document.actions) or {}
    for _, node in ipairs(nodes or {}) do
        if node.index == selected.index then
            local title = prefix .. tostring(node.name or selected.kind) .. " [" .. selected.index .. "]"
            if selected.attributeIndex ~= nil then
                for _, attribute in ipairs(node.attributes or {}) do
                    if attribute.index == selected.attributeIndex then return title .. " / " .. parameter_label(attribute.type) .. " [" .. attribute.index .. "]" end
                end
            end
            return title
        end
    end
    return prefix .. selected.kind .. " [" .. selected.index .. "]" .. (selected.attributeIndex ~= nil and (" / 属性 [" .. selected.attributeIndex .. "]") or "")
end
local function draw_curves()
    if #state.curves == 0 then return end
    section("曲线关键帧 · 时间 / 数值 / 插值##mvl_curve_editor", function()
        text("选择通道和关键帧后修改下方输入。新增会按时间排序；不会改变通道与属性的对应关系。", palette.muted)
        for i, curve in ipairs(state.curves) do
            if not curve.editable then text(curve.path .. "：" .. tostring(curve.reason), palette.warning)
            else
                text(parameter_label(curve.path) .. " · " .. #(curve.channels or {}) .. " 个通道", palette.accent)
                for j, channel in ipairs(curve.channels or {}) do
                    local selected = state.curve_selection
                    button("通道 " .. channel.index .. " · " .. channel.valueType .. " · " .. #channel.keys .. " 个关键帧##mvl_curve_channel_" .. i .. "_" .. j,
                        function() select_curve(i, j, 1) end, selected and selected.curve == i and selected.channel == j and "selected" or nil)
                end
                if #(curve.channels or {}) == 0 then text("该属性尚无已映射通道；本面板编辑已有通道的关键帧。", palette.muted) end
            end
        end
        local selection = state.curve_selection
        local curve = selection and state.curves[selection.curve]
        local channel = curve and curve.channels[selection.channel]
        if not channel then return end
        local keys = channel.keys or {}
        local pages = math.max(1, math.ceil(#keys / 40))
        state.curve_key_page = math.max(1, math.min(state.curve_key_page, pages))
        button("上一页关键帧##mvl_curve_previous", function() state.curve_key_page = math.max(1, state.curve_key_page - 1) end)
        imgui.same_line()
        button("下一页关键帧##mvl_curve_next", function() state.curve_key_page = math.min(pages, state.curve_key_page + 1) end)
        text(state.curve_key_page .. " / " .. pages .. " 页", palette.muted)
        child("曲线关键帧列表##mvl_curve_keys", { 0, 150 }, function()
            for i = (state.curve_key_page - 1) * 40 + 1, math.min(#keys, state.curve_key_page * 40) do
                local key = keys[i]
                button("#" .. key.index .. "   时间 " .. key.time .. "   数值 " .. key.value .. "   " .. key.interpolation .. "##mvl_curve_key_" .. i,
                    function() select_curve(selection.curve, selection.channel, i) end, selection.key == i and "selected" or nil)
            end
        end)
        text((selection.key and "编辑关键帧 #" .. keys[selection.key].index or "新增首个关键帧") ..
            (curve_has_drafts() and " · 未提交" or ""), curve_has_drafts() and palette.warning or palette.accent)
        input("时间##mvl_curve_time", "curve_time")
        input("数值（" .. channel.valueType .. "）##mvl_curve_value", "curve_value")
        input("插值类型##mvl_curve_interpolation", "curve_interpolation")
        text("类型编号保持原格式；可输入 Type1 / Type2 / Type3 / Bezier。", palette.muted)
        if state.curve_interpolation == "Bezier" then
            input("出切线 X##mvl_curve_out_x", "curve_out_x"); input("出切线 Y##mvl_curve_out_y", "curve_out_y")
            input("入切线 X##mvl_curve_in_x", "curve_in_x"); input("入切线 Y##mvl_curve_in_y", "curve_in_y")
        end
        button_row({ { "更新选中关键帧##mvl_curve_update", function() curve_edit("update") end, "primary" },
            { "在此时间新增关键帧##mvl_curve_add", function() curve_edit("add") end },
            { "还原本行输入##mvl_curve_revert", function() load_curve_form(selection.curve, selection.channel, selection.key or 1) end } })
        local changed, value = imgui.checkbox("确认删除选中关键帧##mvl_curve_confirm_delete", state.curve_confirm_delete)
        if changed then state.curve_confirm_delete = value end
        if state.curve_confirm_delete then button("删除选中关键帧##mvl_curve_remove", function() curve_edit("remove") end, "warning") end
        text("每个已映射通道至少保留一个关键帧。修改提交后可用顶部撤销 / 重做。", palette.muted)
    end)
end
local function draw_expression_curves()
    if #(state.expression_curves or {}) == 0 then return end
    local scope = "_" .. target_key(state.selected)
    section("表达式曲线 · 公式与数值##mvl_expression_curves" .. scope, function()
        text("曲线随输入变量变化。下图来自已提交公式的静态采样；未读取游戏当前输入，也不是动作帧时间。", palette.muted)
        for _, expression in ipairs(state.expression_curves) do
            section("曲线 [" .. expression.index .. "] " .. parameter_label(expression.channel) .. " · " .. expression.assignment .. "##mvl_expression_" .. expression.index .. scope, function()
                if expression.reason then text(expression.reason, palette.warning); return end
                text(expression.formula, palette.accent)
                local samples = expression.samples or {}
                if #samples > 1 then
                    text("横轴 " .. tostring(expression.axis) .. "：" .. expression.xMin .. " → " .. expression.xMax, palette.muted)
                    local low, high = samples[1], samples[1]
                    for _, value in ipairs(samples) do low, high = math.min(low, value), math.max(high, value) end
                    text(string.format("纵轴：公式输出 %.5g → %.5g", low, high), palette.muted)
                    if imgui.get_window_draw_list and imgui.get_cursor_screen_pos then
                        child("公式采样图##mvl_expression_plot_" .. expression.index .. scope, { 0, 140 }, function()
                            local draw = imgui.get_window_draw_list()
                            local cursor = imgui.get_cursor_screen_pos()
                            if not draw or not cursor then return end
                            local x, y = dimension(cursor, "x", 0) + 8, dimension(cursor, "y", 0) + 8
                            local width, height = math.max(80, available_width() - 24), 100
                            local pad = high == low and math.max(1, math.abs(high) * 0.1) or (high - low) * 0.1
                            local bottom, top = low - pad, high + pad
                            draw:add_line({x, y}, {x, y + height}, 0xffa0a0a0, 1)
                            draw:add_line({x, y + height}, {x + width, y + height}, 0xffa0a0a0, 1)
                            for i = 2, #samples do
                                draw:add_line({x + (i - 2) / (#samples - 1) * width, y + (top - samples[i - 1]) / (top - bottom) * height},
                                    {x + (i - 1) / (#samples - 1) * width, y + (top - samples[i]) / (top - bottom) * height}, 0xffffce65, 2)
                            end
                        end)
                    else text("当前 ImGui 不提供绘图接口；公式与数值编辑仍可使用。", palette.muted) end
                elseif expression.plotReason then text(expression.plotReason, palette.muted) end
                text("正在编辑：" .. selected_title(state.selected) .. " / " .. expression.channel, palette.accent)
                for _, value in ipairs(expression.values or {}) do
                    local field
                    for _, candidate in ipairs(state.fields) do if candidate.path == value.path then field = candidate; break end end
                    text(parameter_label(value.label) .. " · " .. parameter_label(expression.channel), field and field_modified(field) and palette.warning or palette.text)
                    if field and field.editable then
                        text("已提交值：" .. tostring(field.value or ""), palette.muted)
                        local changed, input_value = text_input("##mvl_expression_value_" .. value.path .. scope, state.values[value.path] or value.value)
                        if changed then state.values[value.path] = input_value end
                        button("提交 " .. expression.channel .. " 数值##mvl_expression_set_" .. value.path .. scope, function()
                            mutate("set", { target = copy_target(state.selected), path = value.path, value = state.values[value.path] }, true)
                        end, field_modified(field) and "primary" or nil)
                    else text(value.value .. " · 此字段没有可用写入接口", palette.muted) end
                end
                text("提交后重绘曲线；顶部撤销 / 重做可恢复。函数结构与其他参数仍在下方完整字段中，新增 / 删除表达式节点尚未接入。", palette.muted)
            end)
        end
    end)
end
local color_components = { "R", "G", "B", "A" }
local function color_field_groups()
    local by_path, members = {}, {}
    for _, field in ipairs(state.fields) do by_path[field.path] = field end
    for _, group in ipairs(state.color_groups or {}) do
        -- Only the backend's semantic via.Color mapping authorizes grouping.
        -- Vector4 / intensity fields remain independent, with their full range.
        if type(group) == "table" and type(group.path) == "string" and group.storage == "rgba8" and
            group.scale == 255 and type(group.channels) == "table" and #group.channels == 4 then
            local entry = { color_group = group, color_fields = {}, editable = group.editable == true }
            local valid, paths = true, {}
            for index, channel in ipairs(group.channels) do
                local field = type(channel) == "table" and by_path[channel.path]
                if not field or channel.component ~= color_components[index] or members[channel.path] or paths[channel.path] then
                    valid = false; break
                end
                entry.color_fields[index], paths[channel.path] = field, true
                entry.editable = entry.editable and field.editable == true
            end
            if valid then for path in pairs(paths) do members[path] = entry end end
        end
    end
    return members
end
local function color_modified(entry)
    for _, field in ipairs(entry.color_fields) do if field_modified(field) then return true end end
    return false
end
local function color_values(entry)
    local values = {}
    for index, field in ipairs(entry.color_fields) do
        local value = tonumber(state.values[field.path] or field.value)
        if not value or value ~= value or value < 0 or value > 255 or value % 1 ~= 0 then return nil end
        values[index] = math.tointeger(value)
    end
    return values
end
local function draw_color_group(entry)
    local group, modified = entry.color_group, color_modified(entry)
    local scope = group.path .. "_" .. target_key(state.selected)
    text(parameter_label(group.path~="" and group.path or group.label) .. " · RGBA" .. (modified and " · 未提交" or ""), modified and palette.warning or palette.accent)
    local values = color_values(entry)
    if entry.editable and values and type(imgui.color_edit4) == "function" and Vector4f and type(Vector4f.new) == "function" then
        -- REFramework takes/returns Vector4f, not a Lua array or packed color.
        -- NoInputs keeps the raw byte editors below as the only numeric inputs.
        -- Float avoids an extra 0..255 UI conversion; independent HDR intensity
        -- fields never pass through this byte-color picker.
        local flags = 8 + 32 + 65536 + 1048576 + 16777216 + 134217728
        local ok, changed, color = pcall(function()
            return imgui.color_edit4("颜色##mvl_color_picker_" .. scope,
                Vector4f.new(values[1] / 255.0, values[2] / 255.0, values[3] / 255.0, values[4] / 255.0), flags)
        end)
        if ok and changed == true then
            local converted_ok, converted = pcall(function()
                local result = {}
                for index, component in ipairs({ "x", "y", "z", "w" }) do
                    local value = color[component]
                    if type(value) ~= "number" or value ~= value or value < 0 or value > 1 then return nil end
                    result[index] = tostring(math.floor(value * 255 + 0.5))
                end
                return result
            end)
            if converted_ok and converted then
                for index, field in ipairs(entry.color_fields) do state.values[field.path] = converted[index] end
            else text("调色结果不可用；请使用下方 RGBA 数值。", palette.warning) end
        elseif not ok then text("当前调色器不可用；下方 RGBA 数值仍可编辑。", palette.muted) end
        text("点击色块调色；RGBA 原始数值在下方，整组提交后生效。", palette.muted)
    elseif entry.editable then
        text(values and "当前界面未提供调色器；可直接编辑下方 RGBA 数值。" or "RGBA 需为 0～255 的整数；请修正输入后调色或提交。", palette.muted)
    end
    if state.field_show_paths then text(group.path ~= "" and group.path or "根颜色", palette.muted) end
    local width = available_width()
    local columns = width >= 420 and 4 or 2
    for index, field in ipairs(entry.color_fields) do
        if (index - 1) % columns ~= 0 then imgui.same_line() end
        if entry.editable then
            if imgui.set_next_item_width then imgui.set_next_item_width(math.max(54, width / columns - 34)) end
            local changed, value = text_input(color_components[index] .. "##mvl_color_value_" .. field.path .. "_" .. target_key(state.selected),
                state.values[field.path] or tostring(field.value or ""))
            if changed then state.values[field.path] = value end
        else text(color_components[index] .. ": " .. tostring(field.value or ""), palette.muted) end
    end
    if entry.editable then
        button_row({ { "提交整组颜色##mvl_color_set_" .. scope, function()
            local submitted = color_values(entry)
            if not submitted then state.message = "颜色未提交：RGBA 四个分量都必须是 0～255 的整数。"; return end
            local committed = {}; for _, field in ipairs(entry.color_fields) do committed[field.path] = true end
            mutate("set_color", { target = copy_target(state.selected), path = group.path, values = submitted }, true, committed)
        end, color_modified(entry) and "primary" or nil },
        { "还原颜色输入##mvl_color_revert_" .. scope, function()
            for _, field in ipairs(entry.color_fields) do state.values[field.path] = tostring(field.value or "") end
        end } })
    else text("只读原因：" .. tostring(group.reason or "此颜色没有完整写入接口"), palette.muted) end
    imgui.separator()
end
local function parameter_modified(group)
    for _,field in ipairs(group.fields) do if field_modified(field) then return true end end
    return false
end
local function draw_parameter_group(group)
    local scope=group.path.."_"..target_key(state.selected)
    local modified=parameter_modified(group)
    text(parameter_label(group.path)..(group.axes and (" · "..table.concat(group.axes)) or "")..(modified and " · 未提交" or ""),modified and palette.warning or palette.accent)
    local width=available_width()
    local scalar_pair=not group.axes and #group.rows==2 and #group.rows[1].fields==1 and #group.rows[2].fields==1
    for row_index,row in ipairs(group.rows) do
        local label=state.parameter_language=="en" and (row.kind=="random" and "Random" or "Base") or (row.kind=="random" and "随机值" or "普通值")
        if scalar_pair and row_index==2 then imgui.same_line() end
        if #group.rows>1 and not scalar_pair then text(label,palette.muted) end
        for index,field in ipairs(row.fields) do
            if index>1 then imgui.same_line() end
            local columns=scalar_pair and 2 or #row.fields
            if imgui.set_next_item_width then imgui.set_next_item_width(math.max(50,(width-20*(columns-1))/columns-6)) end
            local caption=group.axes and group.axes[index] or label
            if field.editable then
                local changed,value=text_input(caption.."##mvl_parameter_"..field.path.."_"..target_key(state.selected),state.values[field.path] or tostring(field.value or ""))
                if changed then state.values[field.path]=value end
            else text(caption..": "..tostring(field.value or ""),palette.muted) end
        end
    end
    if state.field_show_paths then for _,f in ipairs(group.fields) do text(f.path,palette.muted) end end
    for _,f in ipairs(group.fields) do if not f.editable then text(f.path.." · 只读："..tostring(f.reason or "无写入接口"),palette.muted) end end
    if group.editable then
        button_row({{"提交整组参数##mvl_parameter_set_"..scope,function()
            local edits,committed={},{}
            for _,field in ipairs(group.fields) do if field.editable then
                edits[#edits+1]={path=field.path,value=state.values[field.path] or tostring(field.value or "")};committed[field.path]=true
            end end
            mutate("set_fields",{target=copy_target(state.selected),fields=edits},true,committed)
        end,modified and "primary" or nil},{"还原整组输入##mvl_parameter_revert_"..scope,function()
            for _,field in ipairs(group.fields) do state.values[field.path]=tostring(field.value or "") end
        end}})
    end
    imgui.separator()
end
local function valid_group_note(value)
    if type(value) ~= "string" or #value > 4096 then return false, "备注必须是 UTF-8 文本，长度不能超过 4096 字节。" end
    local ok, valid = pcall(function()
        for _, codepoint in utf8.codes(value) do
            if codepoint < 32 or codepoint >= 127 and codepoint <= 159 then return false end
        end
        return true
    end)
    if not ok then return false, "备注包含无效的 UTF-8 字符；当前输入已保留。" end
    if not valid then return false, "备注只支持单行文字，不能包含换行、Tab 或控制字符。" end
    return true
end
local function draw_group_note(selected)
    text("分组备注仅供编辑器识别；保存后立即记住，无需保存或应用特效文件。", palette.muted)
    text("游戏原分组名称、调用关系、特效参数与撤销记录均不改变。", palette.muted)
    local document_warning = state.document and state.document.groupNotesWarning
    if type(document_warning) == "string" and document_warning ~= "" then text("备注提示：" .. document_warning, palette.warning) end
    for _, group in ipairs(state.document and state.document.groups or {}) do
        if group.index == selected.index and type(group.noteWarning) == "string" and group.noteWarning ~= "" and group.noteWarning ~= document_warning then
            text("当前分组备注提示：" .. group.noteWarning, palette.warning)
            break
        end
    end
    local field
    for _, candidate in ipairs(state.fields) do if candidate.path == "note" and candidate.editable == true then field = candidate; break end end
    if not field then
        text("后台尚未提供独立分组备注；请更新后台后再编辑。原生分组名称不会在这里修改。", palette.warning)
        return
    end
    text("分组备注（支持中文）" .. (field_modified(field) and " · 未保存" or ""), field_modified(field) and palette.warning or palette.accent)
    if imgui.set_next_item_width then imgui.set_next_item_width(math.max(160, available_width() - 24)) end
    -- The binding carries UTF-8 std::string values with no ASCII/number filter.
    local changed, value = text_input("##mvl_group_note_value_" .. target_key(selected), state.values.note or "", 0)
    if changed then state.values.note = value end
    button_row({ { "保存备注##mvl_set_note", function()
        local valid, reason = valid_group_note(state.values.note or "")
        if not valid then state.message = reason; return end
        mutate("set_group_note", { groupIndex = selected.index, note = state.values.note or "" }, true, { note = true })
    end, field_modified(field) and "primary" or nil },
    { "粘贴中文备注##mvl_paste_group_note", function()
        if type(imgui.get_clipboard) ~= "function" then state.message = "当前 REFramework 没有剪贴板接口；可在备注输入框使用 Ctrl+V。"; return end
        -- Explicit user action only: never read clipboard content on a frame or load.
        local ok, pasted = pcall(imgui.get_clipboard)
        if not ok or type(pasted) ~= "string" or pasted == "" then state.message = "剪贴板没有可读取的文字，当前输入已保留。"; return end
        local valid, reason = valid_group_note(pasted)
        if not valid then state.message = reason; return end
        state.values.note = pasted
        state.message = "已粘贴到备注输入框；检查文字后点击「保存备注」。"
    end },
    { "清空备注输入##mvl_clear_group_note", function() state.values.note = "" end },
    { "还原备注输入##mvl_revert_group_note", function() state.values.note = tostring(field.value or "") end } })
    text("可直接使用中文输入法。若游戏内候选窗不便操作，先在记事本输入并复制，再点击「粘贴中文备注」。", palette.muted)
    text("空备注保存后恢复显示游戏原名；相同备注可用于多个分组。", palette.muted)
end
local field_group_cache = {}
local function draw_fields()
    local selected = state.selected
    if not selected then
        text("属性编辑", palette.accent)
        text("从结构列表选择特效块或属性。可在结构搜索中输入 RGBCommon 等属性名称。", palette.muted); return
    end
    text("属性编辑 · " .. selected_title(selected), palette.accent)
    local embedded = selected.embeddedPath and #selected.embeddedPath > 0
    if selected.attributeIndex ~= nil then
        button("返回所属 " .. (selected.kind == "entry" and "Entry" or "Action") .. "##mvl_parent_target", function()
            local owner = copy_target(selected); owner.attributeIndex = nil; inspect(owner)
        end)
    elseif embedded then
        button("返回所属 PlayEmitter##mvl_parent_target", function()
            local owner = copy_target(selected)
            local hop = table.remove(owner.embeddedPath)
            owner.kind, owner.index, owner.attributeIndex = hop.kind, hop.index, hop.attributeIndex
            if #owner.embeddedPath == 0 then owner.embeddedPath = nil end
            inspect(owner)
        end)
    end
    button("中文##mvl_language_zh",function() M.set_parameter_language("zh") end,state.parameter_language=="zh" and "selected" or nil)
    imgui.same_line()
    button("English##mvl_language_en",function() M.set_parameter_language("en") end,state.parameter_language=="en" and "selected" or nil)
    button("重新读取字段##mvl_inspect", function() inspect(selected) end)
    if not embedded and selected.kind=="entry" and state.document and state.document.canRestoreEntry then
        button("还原当前特效块##mvl_restore_entry",function()
            mutate("restore_entry",{index=selected.index})
        end)
    end
    draw_curves()
    draw_expression_curves()
    if selected.kind == "group" then
        draw_group_note(selected)
        return
    elseif selected.kind ~= "document" then
      local can_delete=(not embedded and selected.kind=="entry") or selected.attributeIndex~=nil
      if can_delete then
        local deletion_label=selected.attributeIndex~=nil and "删除当前属性" or "删除当前特效块"
        local changed,confirm=imgui.checkbox("确认"..deletion_label.."##mvl_confirm_delete",state.confirm_delete)
        if changed then state.confirm_delete=confirm end
        if state.confirm_delete then
            text("将删除："..selected_title(selected).."；可用撤销恢复。",palette.warning)
            button(deletion_label.."##mvl_remove",function()
                if selected.attributeIndex~=nil then mutate("remove_attribute",{target=copy_target(selected)})
                else mutate("remove_entry",{index=selected.index}) end
            end,"warning")
            button("取消删除##mvl_cancel_remove",function() state.confirm_delete=false end)
        end
      end
      section("结构操作：添加 / 复制 / 删除##mvl_structure_operations", function()
        if not embedded and selected.kind == "entry" and selected.attributeIndex == nil then
            button("复制特效块##mvl_duplicate", function() mutate("duplicate_entry", { index = selected.index }) end)
        end
        input("新增属性类型##mvl_attr_type", "attribute_type")
        local types = state.document and state.document.attributeTypes or {}
        if #types > 0 then
            section("可用属性类型##mvl_types", function()
                child("属性类型列表##mvl_types_list", { 0, 160 }, function()
                    for i, name in ipairs(types) do
                        if state.attribute_type == "" or parameter_matches(name,state.attribute_type:lower()) then
                            button(parameter_label(name) .. "##mvl_type_" .. i, function() state.attribute_type = name end)
                        end
                    end
                end)
            end)
        end
        button("添加属性到当前块或动作##mvl_add_attribute", function()
            local owner = copy_target(selected); owner.attributeIndex = nil
            mutate("add_attribute", { target = owner, type = state.attribute_type })
        end)
      end)
    end
    input("筛选字段##mvl_field_filter", "field_filter")
    for _, option in ipairs({ { "只看可编辑##mvl_editable_only", "field_editable_only" },
        { "只看未提交##mvl_modified_only", "field_modified_only" }, { "显示完整路径##mvl_field_paths", "field_show_paths" } }) do
        local changed, value = imgui.checkbox(option[1], state[option[2]])
        if changed then state[option[2]] = value end
        if option[2] ~= "field_show_paths" and available_width() > 620 then imgui.same_line() end
    end
    -- Cache the filtered index list; a full document may contain thousands of
    -- scalar fields, but only 80 controls need to be submitted each frame.
    if field_group_cache.fields ~= state.fields or field_group_cache.colors ~= state.color_groups then
        local colors = color_field_groups()
        field_group_cache = { fields = state.fields, colors = state.color_groups, color_members = colors,
            parameter_members = parameters and parameters.groups(state.fields, colors) or {} }
    end
    if state.field_view_source ~= state.fields or state.field_view_colors ~= state.color_groups or state.field_view_filter ~= state.field_filter or
        state.field_view_editable ~= state.field_editable_only or state.field_view_modified ~= state.field_modified_only or state.field_modified_only then
        if state.field_view_filter ~= state.field_filter or state.field_view_editable ~= state.field_editable_only or
            state.field_view_modified ~= state.field_modified_only then state.field_page = 1 end
        state.field_view_source, state.field_view_filter, state.field_view = state.fields, state.field_filter, {}
        state.field_view_colors = state.color_groups
        state.field_view_editable, state.field_view_modified = state.field_editable_only, state.field_modified_only
        local filter = state.field_filter:lower()
        local color_members, added_colors = field_group_cache.color_members, {}
        local parameter_members=field_group_cache.parameter_members
        local added_parameters={}
        for _, field in ipairs(state.fields) do
            local color_entry = color_members[field.path]
            local parameter_entry=parameter_members[field.path]
            if color_entry and not added_colors[color_entry] then
                added_colors[color_entry] = true
                local group = color_entry.color_group
                local searchable = tostring(group.label or group.path) .. " " .. group.path .. " RGBA"
                for _, channel in ipairs(color_entry.color_fields) do searchable = searchable .. " " .. (channel.label or channel.path) .. " " .. channel.path end
                if (not state.field_editable_only or color_entry.editable) and (not state.field_modified_only or color_modified(color_entry)) and
                    (filter == "" or parameter_matches(searchable,filter)) then state.field_view[#state.field_view + 1] = color_entry end
            elseif parameter_entry and not added_parameters[parameter_entry] then
                added_parameters[parameter_entry]=true
                local searchable=parameter_entry.path
                for _,f in ipairs(parameter_entry.fields) do searchable=searchable.." "..f.path.." "..(f.label or "") end
                if (not state.field_editable_only or parameter_entry.editable) and (not state.field_modified_only or parameter_modified(parameter_entry)) and
                    (filter=="" or parameter_matches(searchable,filter)) then state.field_view[#state.field_view+1]=parameter_entry end
            elseif not color_entry and not parameter_entry and (not state.field_editable_only or field.editable == true) and (not state.field_modified_only or field_modified(field)) and
                (filter == "" or parameter_matches((field.label or field.path).." "..field.path,filter)) then
                state.field_view[#state.field_view + 1] = field
            end
        end
    end
    local count, page_size = #state.field_view, 80
    local pages = math.max(1, math.ceil(count / page_size))
    state.field_page = math.max(1, math.min(state.field_page, pages))
    button("上一页##mvl_fields_previous", function() state.field_page = math.max(1, state.field_page - 1) end)
    imgui.same_line()
    button("下一页##mvl_fields_next", function() state.field_page = math.min(pages, state.field_page + 1) end)
    if available_width() > 480 then imgui.same_line() end
    text(state.field_page .. " / " .. pages .. " 页 · " .. count .. " / " .. #state.fields .. " 个字段", palette.muted)
    child("属性完整字段##mvl_fields", { 0, math.max(220, dimension(imgui.get_window_size(), "y", 650) - dimension(imgui.get_cursor_pos(), "y", 250) - 16) }, function()
    local function rows()
    if #state.fields == 0 then text("后台未返回字段；请检查解析结果，不能将空面板视为完成。") end
    if #state.fields > 0 and count == 0 then text("没有匹配字段；清空筛选可查看全部。") end
    for i = (state.field_page - 1) * page_size + 1, math.min(state.field_page * page_size, count) do
        local field = state.field_view[i]
        if field.color_group then draw_color_group(field)
        elseif field.parameter_group then draw_parameter_group(field)
        else
        local path, label = field.path, parameter_label(field.path)
            text(label, field_modified(field) and palette.warning or palette.text)
            text(tostring(field.type) .. (field.editable == true and (field_modified(field) and " · 未提交" or " · 可编辑") or " · 只读"),
                field_modified(field) and palette.warning or palette.muted)
            if state.field_show_paths and path ~= label then text(path, palette.muted) end
            if field.editable == true then
                local current = state.values[path] or ""
                local scope = embedded and ("_" .. target_key(selected)) or ""
                if imgui.set_next_item_width then imgui.set_next_item_width(math.max(100, available_width() - 92)) end
                local changed, value = text_input("##mvl_value_" .. path .. scope, current)
                if changed then state.values[path] = value end
                imgui.same_line()
                button("提交##mvl_set_" .. path .. scope, function()
                    mutate("set", { target = copy_target(selected), path = path, value = state.values[path] or "" }, true)
                end, field_modified(field) and "primary" or nil, 78)
                if field.choices and #field.choices > 0 then text("可用值：" .. table.concat(field.choices, ", "), palette.muted) end
            else
                text(tostring(field.value or ""), palette.muted)
                text("只读原因：" .. tostring(field.reason or "后台未提供编辑能力说明"), palette.muted)
            end
            imgui.separator()
        end
    end
    end
    if imgui._mvl_input_batch==true then imgui.with_input_batch(rows) else rows() end
    end)
end
local function publish_document(apply_to_resource)
    if action_preview_status().busy then state.action_message="请等待动作槽位热重载或还原完成。";return false end
    if not state.document then return false end
    if has_drafts() then state.action_message = "存在未提交字段草稿；请先提交各字段后再生成或应用。"; return false end
    local target, source, revision = state.target_resource_path, state.document.source, state.document.revision
    local expected_source = copy_capture_source(state.document.gameSource)
    refresh_action_state()
    if state.apply_requested or state.bindings_restore_pending then
        state.action_message = "请等待当前资源应用或还原完成。"; return false
    end
    local generation = state.apply_generation
    if apply_to_resource then
        if state.document.sourceKind ~= "game_files" then
            state.action_message = "当前文档来自手动打开的本地文件；请从角色动作列表选择资源后再应用。可先保存副本保留修改。"
            return false
        end
        if not target or source ~= state.target_document_source then state.action_message = "请从已确认的角色动作资源选择编辑目标。"; return false end
        if not current_resource_is_local(target) then ownership_required_message(); return false end
        if not current_edit_source_matches(expected_source, target) then capture_source_required_message(); return false end
        if not bindings or bindings_error or type(bindings.apply) ~= "function" then
            state.action_message = "资源应用模块不可用：" .. tostring(bindings_error); return false
        end
        if state.apply_requested or state.bindings_snapshot.busy or state.bindings_restore_pending then
            state.action_message = "请等待当前资源应用或还原完成。"; return false
        end
        state.apply_requested = true
    end
    local queued = send("publish", {}, function(response)
            if not response.ok then state.apply_requested = false; return end
            if type(response.resourcePath) ~= "string" or response.resourcePath == "" then
                state.apply_requested = false; state.action_message = "后台发布未返回游戏资源路径。"; return
            end
            state.published_path, state.published_revision = response.resourcePath, response.revision
            state.resource_path = response.resourcePath
            if not apply_to_resource then
                state.preview_message = "已生成辅助预览资源；需在辅助预览页明确点击播放。"; return
            end
            if state.apply_generation ~= generation or state.target_resource_path ~= target or not state.document or state.document.source ~= source or
                state.document.revision ~= revision or (response.revision ~= nil and response.revision ~= revision) then
                state.apply_requested = false; state.action_message = "目标或修订已变化，未替换角色资源。"; return
            end
            -- Publishing is asynchronous. Ownership may have been revoked or
            -- the record cleared since the user pressed Apply.
            refresh_action_state()
            if state.document.sourceKind ~= "game_files" then
                state.apply_requested = false; state.action_message = "文档来源已变化，未替换角色资源。"; return
            end
            if not current_resource_is_local(target) then
                state.apply_requested = false; ownership_required_message(); return
            end
            if not same_capture_source(expected_source, state.document.gameSource) or
                not current_edit_source_matches(expected_source, target) then
                state.apply_requested = false; capture_source_required_message(); return
            end
            local ok, accepted, err = pcall(bindings.apply, target, response.resourcePath, function(result)
                state.apply_requested = false
                if result and result.ok and state.apply_generation == generation and not state.bindings_restore_pending then
                    begin_verification(target, response.resourcePath)
                end
                state.action_message = result and result.ok and
                    "资源引用已替换；请再次执行角色原动作验证。当前场景已加载的所有匹配引用共同变化，后续新加载引用需再次应用。" or
                    ("资源应用失败：" .. tostring(result and result.message or "模块未返回结果"))
                refresh_action_state()
            end)
            if not ok or not accepted then
                state.apply_requested = false; state.action_message = "资源应用未排队：" .. tostring(ok and err or accepted)
            else state.action_message = "资源应用已排队；等待原始引用替换结果。" end
            refresh_action_state()
    end)
    if not queued then state.apply_requested = false end
    return queued
end
M.publish_document = publish_document
local function draw_save()
    if not state.document then return end
    local document = state.document
    text((document.name or (document.source or "EFX"):match("[^/\\]+$")) .. " | 修订 " .. tostring(document.revision) ..
        (document.dirty and " | 未保存" or ""), palette.accent)
    text(document.sourceKind == "game_files" and "编辑来源：游戏目录（按角色实际触发路径读取）" or
        "编辑来源：手动打开的本地文件", document.sourceKind == "game_files" and palette.success or palette.warning)
    if state.target_resource_path and document.sourceKind == "game_files" then
        button("应用到角色真实资源##mvl_apply_resource", function() publish_document(true) end, "primary")
        if available_width() > 640 then imgui.same_line() end
    end
    button("导出文件##mvl_save", function()
        if has_drafts() then state.message = "存在未提交字段草稿；请先提交后再导出。"; return end
        send("begin_export", {scope="efx"})
    end)
    imgui.same_line()
    button("撤销##mvl_undo", function() mutate("undo") end)
    imgui.same_line()
    button("重做##mvl_redo", function() mutate("redo") end)
    local draft_count = 0
    for _, field in ipairs(state.fields) do if field_modified(field) then draft_count = draft_count + 1 end end
    if draft_count > 0 then text("还有 " .. draft_count .. " 个字段未提交；请先在字段旁点击提交，再保存或应用。", palette.warning) end
    if state.target_resource_path then text(state.action_message)
    else text("从角色动作记录选择资源后，即可在这里应用到角色。", palette.muted) end
    section("文件路径、保存位置与编辑范围##mvl_document_details", function()
        text("源文件：" .. tostring(document.source), palette.muted)
        if document.sourceKind == "game_files" and document.gameSource then
            local origin = document.gameSource
            text("读取自：" .. tostring(origin.originPath), palette.muted)
            text("完整资源路径：" .. tostring(origin.nativePath), palette.muted)
        end
        if state.target_resource_path then text("角色资源：" .. state.target_resource_path) end
        text("点击导出文件后选择保存位置；游戏中应用不会覆盖原文件。")
        text("共享资源：当前场景已加载的匹配引用会一起变化；后续新加载引用需再次应用。", palette.muted)
        if state.published_path then text("已发布：" .. state.published_path .. "（修订 " .. tostring(state.published_revision) .. "）") end
        for _, limitation in ipairs(document.limitations or {}) do text(limitation, palette.muted) end
    end)
end
local function draw_editor()
    if not state.document then text("先从角色动作记录选择资源，或打开一个本地 EFX 文件。", palette.muted); return end
    local width = available_width()
    local height = math.max(300, dimension(imgui.get_window_size(), "y", 650) - dimension(imgui.get_cursor_pos(), "y", 150) - 16)
    state.edit_layout = width >= 900 and "columns" or "stacked"
    if state.edit_layout == "columns" then
        child("结构导航##mvl_structure_pane", { math.floor(width * 0.34), height }, draw_tree)
        imgui.same_line()
        child("字段编辑##mvl_property_pane", { 0, height }, draw_fields)
    else
        child("结构导航##mvl_structure_pane", { 0, 220 }, draw_tree)
        child("字段编辑##mvl_property_pane", { 0, math.max(350, height - 232) }, draw_fields)
    end
end
local function draw_preview()
    text("辅助预览会创建独立实例；角色动作的资源应用请使用编辑页。")
    if state.document then button("生成当前文档的辅助预览资源（不自动播放）##mvl_publish", function() publish_document(false) end) end
    if not client then text("播放模块加载失败：" .. tostring(runtime_error)); return end
    input("游戏资源路径（不是磁盘绝对路径）##mvl_resource", "resource_path")
    local changed, attachment = imgui.combo("挂接位置##mvl_attach", state.attachment, { "世界坐标", "当前角色", "当前武器" })
    if changed then state.attachment = attachment end
    input("骨骼名称（可选）##mvl_bone", "bone")
    input("缩放##mvl_scale", "scale")
    input("位置 X##mvl_x", "x"); input("位置 Y##mvl_y", "y"); input("位置 Z##mvl_z", "z")
    local cloud_changed, cloud_enabled = imgui.checkbox("提供点云发射位置（实验）##mvl_point_cloud", state.point_cloud_enabled)
    if cloud_changed then state.point_cloud_enabled = cloud_enabled end
    if state.point_cloud_enabled then
        local space_changed, space = imgui.combo("点云空间##mvl_point_cloud_space", state.point_cloud_space, { "相对坐标", "世界坐标" })
        if space_changed then state.point_cloud_space = space end
        input("点云索引（逗号分隔，0–31）##mvl_point_cloud_indices", "point_cloud_indices")
        text("仅向当前预览实例提供发射输入，不修改 EFX 文件；重新播放时应用。")
        if not has_point_cloud() then text("当前运行时不支持点云输入；需重置脚本或重启游戏载入更新后才能启用。") end
    end
    text("播放运行时：" .. tostring(type(runtime) == "table" and runtime.version or "未加载"))
    button("播放 / 从头重播##mvl_play", function() play(state.resource_path) end)
    imgui.same_line()
    button("停止独立预览##mvl_stop", function()
        local id, err = client:stop("preview", preview_result)
        if not id then state.preview_message = tostring(err) end
    end)
    text("单独控制本编辑器创建的特效：")
    button("暂停特效##mvl_pause_effect", function() effect_action("Pause") end)
    imgui.same_line()
    button("继续特效##mvl_resume_effect", function() effect_action("Resume") end)
    imgui.same_line()
    button("特效单步（实验）##mvl_step_effect", function() effect_action("Step") end)
    input("特效速度##mvl_effect_speed", "speed")
    button("设置特效速度##mvl_set_speed", function() queue_effect_speed(state.speed) end)
    for _, speed in ipairs({ 0.1, 0.25, 0.5, 1 }) do
        imgui.same_line()
        button(tostring(speed) .. "×##mvl_effect_rate_" .. speed, function() queue_effect_speed(speed) end)
    end
    text(state.preview_message)
    if state.effect_actual then
        local actual = state.effect_actual
        text("最近回读：速度 " .. tostring(actual.speed) .. "；暂停 " .. tostring(actual.paused))
        text("资源就绪 " .. tostring(actual.ready_resource) .. "；运行 " .. tostring(actual.running) .. "；有效 " .. tostring(actual.valid) .. "；发射器 " .. tostring(actual.emitters))
        text("引擎资源：" .. tostring(actual.resource_path or "未回读"))
        text("PlayerState " .. tostring(actual.player_state) .. "；剔除 " .. tostring(actual.culled) .. "；分组开启 " .. tostring(actual.group_enabled))
        for field, err in pairs(actual.read_errors or {}) do text("回读失败 " .. field .. ": " .. err) end
        text("这些是引擎状态；资源运行并不等于画面可见，仍需观察实际效果。")
    end
end
local function draw_scene_toolbar()
    local items = {
        { "暂停当前场景##mvl_scene_pause", function() queue_scene("pause", true) end },
        { "继续当前场景##mvl_scene_resume", function() queue_scene("pause", false) end },
    }
    for _, speed in ipairs({ 0.1, 0.25, 0.5, 1 }) do
        items[#items + 1] = { tostring(speed) .. "×##mvl_scene_rate_" .. speed, function() queue_scene("speed", speed) end,
            (state.scene_pause and state.scene_pause.resume_speed == speed or
                not state.scene_pause and state.scene_actual and state.scene_actual.speed == speed) and "selected" or nil, 62 }
    end
    button_row(items)
end
local function draw_debug_controls()
    if not debug_hotkeys then
        text("调试快捷键未加载：" .. tostring(debug_error), palette.warning); return
    end
    local changed, enabled = imgui.checkbox("调试模式：启用暂停快捷键##mvl_debug_enabled", debug_hotkeys.enabled)
    if changed then debug_hotkeys:enable(enabled) end
    if debug_hotkeys.enabled then
        text("暂停／继续：" .. debug_hotkeys:status().label .. "；编辑文本时暂不触发。", palette.accent)
    end
    section("设置暂停快捷键（支持多键组合）##mvl_debug_settings", function()
        text("支持 1～8 个键，例如 Ctrl + Shift + Alt + P。")
        text("调试模式启用时，组合内各键由编辑器占用；关闭调试模式后恢复游戏按键。", palette.warning)
        text("Insert 留给 REFramework；Windows 系统组合键不可绑定。")
        local edited, value = text_input("组合键##mvl_debug_chord", debug_hotkeys.draft)
        if edited then debug_hotkeys.draft = value end
        if debug_hotkeys.recording then
            button("取消录入##mvl_debug_cancel", function() debug_hotkeys:cancel_record() end)
        else
            button("按键录入##mvl_debug_record", function() debug_hotkeys:begin_record() end)
            imgui.same_line()
            button("保存快捷键##mvl_debug_save", function() debug_hotkeys:save() end)
        end
        text("保存后下次仍保留组合键；每次加载编辑器时调试模式默认关闭。", palette.muted)
        local status = debug_hotkeys:status()
        local reasons = { active="已接管", unfocused="等待游戏回到前台", text_input="正在编辑文本",
            busy="等待当前操作完成", recording="正在录入", disabled="未启用", native_disabled="组件未接管" }
        text("按键状态：" .. (reasons[status.gate] or "等待采样") ..
            "；触发 " .. tostring(status.toggles or 0) .. " 次；拦截 " ..
            tostring((status.blockedRaw or 0) + (status.blockedLegacy or 0) +
                (status.blockedPolling or 0) + (status.blockedBuffered or 0)) .. " 次", palette.muted)
        text("输入组件：" .. tostring(status.nativeVersion or "未加载") ..
            "；键盘读取拦截 " .. tostring(status.blockedPolling or 0) ..
            "；缓冲输入拦截 " .. tostring(status.blockedBuffered or 0), palette.muted)
        if not debug_hotkeys.available then text("输入拦截组件尚未加载，首次安装后需要重启游戏。", palette.warning) end
        if debug_hotkeys.message ~= "" then text(debug_hotkeys.message) end
    end)
end
local function stop_and_restore(preserve_action_edits)
    pfb_preview_generation=pfb_preview_generation+1
    if pfb_preview then pfb_preview:restore()end
    state.action_edit_error=nil
    action_preview_generation=action_preview_generation+1
    if action_preview then action_preview:restore() end
    if not preserve_action_edits and (state.action_document or workbench and workbench.action_document) then action_edit={operation="reset_action_slots"}end
    if debug_hotkeys then debug_hotkeys:disable() end
    state.verification = nil
    state.effect_queue = {}
    state.scene_queue = { { operation = "restore" } }
    if capture then capture_command("stop") end
    refresh_action_state()
    if bindings and (state.apply_requested or (state.bindings_snapshot.appliedCount or 0) > 0 or
        state.bindings_snapshot.busy or state.bindings_snapshot.requiresRestoreBeforeReset) then restore_bindings() end
    state.restore_preview_pending, state.restore_preview_error = false, nil
    if client then
        state.restore_preview_pending = true
        local id, err = client:stop("preview", function(result)
            state.restore_preview_pending = false
            state.restore_preview_error = (not result or not result.ok) and tostring(result and result.message or "No preview cleanup result") or nil
            preview_result(result)
        end)
        if not id then
            state.restore_preview_pending, state.restore_preview_error = false, tostring(err)
            state.preview_message = tostring(err)
        end
    end
end
M.stop_and_restore = stop_and_restore
local function draw()
    if state.reloading then
        text("正在安全重载本编辑器；等待资源试改还原、预览停止和场景恢复。")
        text("Reloading MotionVfx Live. Other scripts remain active.")
        text(state.scene_message)
        return
    end
    if state.ai_busy then
        text("AI 正在通过编辑器操作：" .. tostring(state.ai_action), palette.accent)
        text("等待当前操作完成；编辑输入暂时锁定，避免与 AI 的修改相互覆盖。", palette.muted)
        text(state.message)
        button("停止记录及预览，并还原资源与场景##mvl_ai_restore", stop_and_restore, "warning")
        return
    end
    text("MotionVfx Live " .. M.version .. "  |  后台" .. (is_ready() and "就绪" or "未就绪"), is_ready() and palette.success or palette.warning)
    text(state.message, palette.muted)
    draw_scene_toolbar()
    draw_debug_controls()
    button_row({ { "还原资源试改##mvl_restore_resources", restore_bindings },
        { "停止记录及预览，并还原资源与场景##mvl_emergency_restore", stop_and_restore } })
    if state.verification then text(state.verification.message, state.verification.phase == "created" and palette.success or palette.warning) end
    if state.bindings_restore_pending then text("正在还原原始资源引用。", palette.warning) end
    if state.bindings_restore_failed then text("还原失败：" .. state.bindings_restore_failed, palette.warning) end
    if state.scene_message ~= "尚未接管场景时间。" then
        text(state.scene_message .. (state.scene_actual and (" | 速度 " .. state.scene_actual.speed .. " | " .. (state.scene_actual.paused and "时间倍率为零" or "运行中")) or ""), state.scene_control_error and palette.warning or palette.muted)
    end
    section("状态详情 / 字号 / 恢复场景##mvl_status_details", function()
        if state.status_error then text("状态读取：" .. state.status_error) end
        if state.pending then text("待处理请求：" .. state.pending.action .. " / " .. state.pending.id) end
        if not state.pending or state.pending.timeout then button("重新同步后台当前文档##mvl_sync", synchronize) end
        text("资源状态：" .. tostring(state.bindings_snapshot.phase or "尚未应用") .. "；" .. tostring(state.bindings_snapshot.message or ""))
        if state.bindings_snapshot.scopeMessage then text(state.bindings_snapshot.scopeMessage) end
        if (state.bindings_snapshot.appliedCount or 0) > 0 then text("换装备、切场景或重置脚本前，请先还原资源试改。", palette.warning) end
        button("读取场景状态##mvl_scene_read", function() queue_scene("read") end)
        button("恢复接管前的场景状态##mvl_scene_restore", function() queue_scene("restore") end)
        text("字号：")
        for i, scale in ipairs({ 1.0, 1.15, 1.3 }) do
            if i > 1 then imgui.same_line() end
            button(({ "标准", "大字", "更大" })[i] .. "##mvl_font_" .. i, function() state.font_scale = scale end,
                state.font_scale == scale and "selected" or nil)
        end
    end)
    if state.open_confirmation then
        local choice = state.open_confirmation
        text("当前修改尚未保存。拟打开：" .. choice.path)
        button("确认切换文档（放弃当前未保存编辑）##mvl_confirm_open", function()
            if choice.captured_resource then open_game_resource_now(choice.captured_resource)
            else open_document_now(choice.path, nil) end
        end)
        imgui.same_line()
        button("取消切换##mvl_cancel_open", function() state.open_confirmation = nil end)
    end
    imgui.separator()
    local tabs = {}
    for _, tab in ipairs({ { id = "actions", label = "角色动作" }, { id = "resources", label = "本地资源" }, { id = "edit", label = "编辑 Edit" }, { id = "preview", label = "辅助预览" } }) do
        tabs[#tabs + 1] = { (state.active_tab == tab.id and "[ " .. tab.label .. " ]" or tab.label) .. "##mvl_tab_" .. tab.id,
            function() state.active_tab = tab.id end, state.active_tab == tab.id and "primary" or nil }
    end
    button_row(tabs)
    imgui.separator()
    if state.active_tab == "edit" then draw_save(); imgui.separator() end
    child("当前工作页##mvl_workspace", { 0, 0 }, function()
        if state.active_tab == "actions" then draw_actions()
        elseif state.active_tab == "resources" then draw_catalog()
        elseif state.active_tab == "edit" then draw_editor()
        else draw_preview() end
    end)
end
M.draw = draw
local function finite_dimension(value)
    return type(value) == "number" and value == value and value > -math.huge and value < math.huge
end
local function display_bounds()
    local display = imgui.get_display_size and imgui.get_display_size()
    local width, height = dimension(display, "x"), dimension(display, "y")
    -- A minimized game can report a zero viewport. Wait for a real display
    -- instead of saving a one-pixel window or consuming a recovery request.
    if not finite_dimension(width) or not finite_dimension(height) or width <= 0 or height <= 0 then return end
    local margin = math.min(24, width / 4, height / 4)
    return { width = width, height = height, margin = margin }
end
local function prepare_window(bounds)
    if not bounds then return end
    local previous = state.window_display
    if not previous or previous.width ~= bounds.width or previous.height ~= bounds.height then
        state.window_display = bounds
        state.window_fit_requested, state.window_adjustment = true, nil
    end
    if not state.window_initialized then
        imgui.set_next_window_size({ math.min(1120, bounds.width - 2 * bounds.margin),
            math.min(820, bounds.height - 2 * bounds.margin) })
        state.window_initialized = true
    end
    local adjustment = state.window_adjustment
    if adjustment then
        -- These bindings affect the next Begin, not the current window. Apply
        -- the measured correction exactly once, so ordinary dragging is free.
        if adjustment.size then imgui.set_next_window_size(adjustment.size) end
        if adjustment.position then imgui.set_next_window_pos(adjustment.position) end
        state.window_adjustment = nil
    end
end
local function fit_current_window(bounds)
    if not bounds or not state.window_fit_requested then return end
    local size = imgui.get_window_size()
    local position = imgui.get_window_pos()
    local width, height = dimension(size, "x"), dimension(size, "y")
    local x, y = dimension(position, "x"), dimension(position, "y")
    if not finite_dimension(width) or not finite_dimension(height) or width <= 0 or height <= 0
        or not finite_dimension(x) or not finite_dimension(y) then return end
    local target_width = math.min(width, bounds.width)
    local target_height = math.min(height, bounds.height)
    -- A position that is already fully on screen is deliberately preserved,
    -- even if the user chose to align the window with a screen edge.
    local target_x = math.max(0, math.min(x, bounds.width - target_width))
    local target_y = math.max(0, math.min(y, bounds.height - target_height))
    if state.window_center_requested then
        target_x, target_y = (bounds.width - target_width) / 2, (bounds.height - target_height) / 2
    end
    local adjustment = {}
    if width ~= target_width or height ~= target_height then adjustment.size = { target_width, target_height } end
    if x ~= target_x or y ~= target_y then adjustment.position = { target_x, target_y } end
    state.window_adjustment = (adjustment.size or adjustment.position) and adjustment or nil
    state.window_fit_requested, state.window_center_requested = false, false
end
local function draw_window()
    state.window_drawn = false
    if not state.window_open then state.window_visible = false; return end
    -- on_draw_ui runs inside REFramework's narrow Script Generated UI panel.
    -- Draw our own window from on_frame; keep Insert as the framework's mouse/input toggle.
    if reframework and type(reframework.is_drawing_ui) == "function" and not reframework:is_drawing_ui() then
        state.window_visible = false; return
    end
    if not state.window_visible then state.window_fit_requested = true end
    state.window_visible = true
    local bounds = display_bounds()
    prepare_window(bounds)
    -- This binding returns the open flag, not ImGui::Begin's visibility result.
    -- Begin has still been called when the close button changes the flag to false.
    local ok, err = pcall(with_theme, function()
      state.window_open = imgui.begin_window("MotionVfx Live - 游戏内特效编辑器", state.window_open)
      state.window_drawn = true
      if not state.window_open and capture then capture_command("stop") end
      local drawn, draw_error = pcall(function()
        fit_current_window(bounds)
        if state.ui_error then
            text("UI error: " .. state.ui_error)
            button("Retry UI##mvl_retry_ui", function() state.ui_error = nil end)
            button("Stop preview / Restore scene##mvl_window_restore", stop_and_restore)
        else draw() end
      end)
      imgui.end_window()
      if not drawn then error(draw_error, 0) end
    end)
    if not ok then state.ui_error = tostring(err); log_warning("界面绘制失败：" .. state.ui_error) end
end
M.draw_window = draw_window
local function draw_launcher()
    if not imgui.tree_node("MotionVfx Live") then return end
    button("Open editor / 打开编辑器##mvl_open_window", function()
        state.window_open, state.window_fit_requested = true, true
        state.window_hidden=false
    end)
    button("找回窗口 / 居中##mvl_recover_window", function()
        state.window_open, state.window_fit_requested, state.window_center_requested = true, true, true
        state.window_hidden=false
    end)
    button("Stop preview / Restore scene##mvl_launcher_restore", stop_and_restore)
    text("Insert: show/hide REFramework mouse UI. 编辑器使用独立可缩放窗口。")
    if state.ui_error then text("UI error: " .. state.ui_error) end
    imgui.tree_pop()
end
M.draw_launcher = draw_launcher
M.ui_tree, M.ui_fields, M.ui_save, M.ui_debug = draw_tree, draw_fields, draw_save, draw_debug_controls
M.ui_mutate = mutate
function M.current_actions()
    if capture and type(capture.current_snapshot)=="function" then
        local ok,result=pcall(capture.current_snapshot)
        if ok and type(result)=="table" then return result end
        return {currentActions={},actionStatus="当前动作读取失败："..tostring(result)}
    end
    return state.capture_snapshot or {}
end
function M.with_ui(adapter, fn)
    local previous = imgui; imgui = adapter
    local result = table.pack(pcall(fn))
    imgui = previous
    if not result[1] then error(result[2], 0) end
    return table.unpack(result, 2, result.n)
end
function M.save_copy()
    if has_drafts() then state.message = "请先提交或还原当前输入，再导出。"; return false end
    return send("begin_export", {scope="efx"})
end
function M.preview_pfb(document,path)
    if not pfb_preview then state.message="PFB 热重载模块未就绪。";return false end
    local a=action_preview_status();local b=state.bindings_snapshot or {};local p=pfb_preview_status()
    if state.pending or a.busy or a.requiresRestore or b.busy or (b.appliedCount or 0)>0 or state.bindings_restore_pending or p.busy then
        state.message="请先结束当前请求并还原动作槽位、EFX 资源试改，再热重载独立 PFB。";return false
    end
    refresh_action_state()
    local proof,err=pfb_preview:resolve(path,document.type)
    if not proof then state.message=tostring(err);return false end
    local generation=pfb_preview_generation
    return send("pfb_publish",{pfbRevision=document.revision,proof=proof},function(r)
        if r.ok and generation==pfb_preview_generation then
            local ok,message=pfb_preview:apply(r.pfbPreview);state.message=ok and "正在热重载 PFB，请等待完成。" or tostring(message)
        end
    end)
end
function M.restore_pfb_preview()
    pfb_preview_generation=pfb_preview_generation+1
    return pfb_preview and pfb_preview:restore()
end
function M.resolve_action_source(record)
    local ok,result=pcall(function()
        local module=load_action_module("MotionVfxActionSources","MotionVfxLive.fx_action_sources","fx_action_sources.lua")
        local attachments=load_action_module("MotionVfxAttachmentTargets","MotionVfxLive.fx_attachment_targets","fx_attachment_targets.lua")
        return assert(module,"动作来源模块未安装").resolve(record,capture.snapshot(),action_preview and action_preview:aliases(),attachments)
    end)
    if not ok then state.message=tostring(result);return nil end
    return result
end
function M.find_action_record(document)
    refresh_action_state()
    local module=load_action_module("MotionVfxActionSources","MotionVfxLive.fx_action_sources","fx_action_sources.lua")
    return module and module.find_record(document and document.source,state.capture_snapshot,action_preview and action_preview:aliases())
end
function M.action_edit_pending()return action_edit~=nil end
function M.preview_action(document,record)
    if pfb_preview_status().busy or pfb_preview_status().requiresRestore then state.message="请先还原独立 PFB 预览，再应用动作槽位。";return false end
    if not action_preview then state.message="动作热重载模块未就绪。";return false end
    if action_edit then state.message="请等待槽位删除或还原完成。";return false end
    refresh_action_state()
    if state.pending or state.apply_requested or state.bindings_snapshot.busy or state.bindings_restore_pending or
        state.bindings_snapshot.requiresRestoreBeforeReset or (state.bindings_snapshot.appliedCount or 0)>0 or action_preview_status().busy then
        state.message="请先还原当前 EFX 资源试改并等待完成，再预览动作槽位；编辑内容会保留。";return false
    end
    if document.source then
        record=M.find_action_record(document)
        if not record then state.message="当前记录中没有此文档对应的动作；请做一次「"..tostring(document.name or document.motionId).."」后重试，已添加槽位会保留。";return false end
        state.selected_action_id=record.id
    end
    local proof=M.resolve_action_source(record)
    if not proof then return false end
    local generation=action_preview_generation
    return send("publish_action_preview",{actionRevision=document.revision,source=proof},function(response)
        if generation~=action_preview_generation then state.message="动作预览已取消。";return end
        if response.ok then
            local ok,err=action_preview:apply(response.actionPreview)
            state.message=ok and "已提交动作热重载，正在等待游戏资源就绪。" or tostring(err)
        end
    end)
end
function M.restore_action_preview()
    refresh_action_state()
    if state.apply_requested or state.bindings_snapshot.busy or state.bindings_restore_pending or
        state.bindings_snapshot.requiresRestoreBeforeReset or (state.bindings_snapshot.appliedCount or 0)>0 then
        state.message="请先还原 EFX 资源试改，或使用顶部的停止并还原。";return false
    end
    action_preview_generation=action_preview_generation+1
    state.action_edit_error=nil
    if state.action_document or workbench and workbench.action_document then action_edit={operation="reset_action_slots"}end
    return action_preview and action_preview:restore()
end
function M.remove_action_slot(document,slotId)
    if state.pending or action_edit or action_preview_status().busy then state.message="请等待当前槽位操作完成。";return false end
    if not workbench or workbench.action_document~=document then state.message="槽位文档已切换，请重试。";return false end
    refresh_action_state()
    if state.apply_requested or state.bindings_snapshot.busy or state.bindings_restore_pending or
        state.bindings_snapshot.requiresRestoreBeforeReset or (state.bindings_snapshot.appliedCount or 0)>0 then
        state.message="请先还原 EFX 参数试改，再删除动作槽位。";return false
    end
    action_edit={operation="remove_action_slot",slotId=slotId,reapply=action_preview_status().requiresRestore==true}
    state.action_edit_error=nil
    action_preview_generation=action_preview_generation+1
    if action_preview then action_preview:restore()end
    state.message="正在还原游戏引用并删除新增槽位。";return true
end
local function update_action_edit()
    local edit=action_edit
    if not edit or edit.sent or state.pending then return end
    local status=action_preview_status()
    if status.phase=="restore_failed" then state.message=status.message;return end
    if status.busy or status.requiresRestore or state.bindings_restore_pending or state.apply_requested or state.bindings_snapshot.busy then return end
    local doc=state.action_document or workbench and workbench.action_document
    if not doc then action_edit=nil;return end
    edit.idleAt=edit.idleAt or os.time()
    -- Diagnostics is flushed every two seconds; let the backend observe the
    -- completed native restoration before it changes the editable files.
    if os.time()-edit.idleAt<3 then return end
    edit.sent=true
    if not send(edit.operation,{actionRevision=doc.revision,slotId=edit.slotId},function(response)
        if response.ok and workbench then workbench.action_document=response.actionDocument end
        if action_edit~=edit then return end
        action_edit=nil
        if not response.ok then state.action_edit_error=response.error;return end
        local changed=response.actionDocument
        if edit.reapply and (changed.previewSlotCount or #(changed.added or {}))>0 then
            M.preview_action(changed)
        else state.message=edit.operation=="reset_action_slots" and "已还原游戏引用并清除本次新增的动作槽位；原始动作与 PFB 已恢复。" or "已删除槽位及对应 PFB 条目。" end
    end)then edit.sent=false end
end
function M.confirm_open()
    local choice = state.open_confirmation
    if not choice then return end
    if choice.captured_resource then open_game_resource_now(choice.captured_resource)
    else open_document_now(choice.path, nil) end
end
function M.copy_text(value) return send("desktop_start", {mode="copy", text=value}) end
function M.precache_load(entries,paths)
    if not precache_runtime then state.message="预加载模块未就绪";return false end
    local ok,err=precache_runtime:prepare(entries,paths)
    if not ok then state.message=tostring(err) end
    return ok
end
function M.precache_status() return precache_runtime and precache_runtime:snapshot() or {} end
local function ai_snapshot()
    refresh_action_state()
    local verification = state.verification
    return { version = M.version, aiProtocol = 1, observedAt = os.time(), loadedAt = state.loaded_at,
        backendReady = is_ready(), document = state.document, selectedTarget = copy_target(state.selected),
        targetResourcePath = state.target_resource_path, hasDrafts = has_drafts(),
        capture = { recording = state.capture_snapshot.recording, sceneEpoch = state.capture_snapshot.sceneEpoch,
            actor = state.capture_snapshot.actor, resources = state.capture_snapshot.resources or {}, error = state.capture_error,
            currentActions = state.capture_snapshot.currentActions or {}, actionCount = #(state.capture_snapshot.actions or {}),
            actionStatus = state.capture_snapshot.actionStatus, actionCoverage = state.capture_snapshot.actionCoverage },
        bindings = state.bindings_snapshot, actionPreview=action_preview_status(), applyRequested = state.apply_requested,
        bindingsRestorePending = state.bindings_restore_pending, bindingsRestoreFailed = state.bindings_restore_failed,
        verification = verification and { phase = verification.phase, originalPath = verification.originalPath,
            generatedPath = verification.generatedPath, observedPath = verification.observedPath, message = verification.message },
        scene = state.scene_actual, sceneStatus = state.scene_message, actionStatus = state.action_message,
        globalTimeScale = state.global_time_actual, globalTimeRestorePending = state.global_time_original ~= nil,
        effectUpdateEnabled = state.effect_update_actual, effectUpdateRestorePending = state.effect_update_original ~= nil,
        debugInput = debug_hotkeys and debug_hotkeys:status(),
        pending = state.pending and { id = state.pending.id, action = state.pending.action, timeout = state.pending.timeout } }
end
local function ai_execute(action, arguments, done)
    local function reject(code, message, uncertain) done(false, { code = code, message = message, uncertain = uncertain }); return nil end
    local function integer(value) return type(value) == "number" and value >= 0 and value < math.huge and value % 1 == 0 end
    local function valid_target(target)
        if type(target) ~= "table" or not (target.kind == "document" or target.kind == "entry" or target.kind == "action" or target.kind == "group") or
            not integer(target.index or 0) or not (target.attributeIndex == nil or integer(target.attributeIndex)) then return false end
        if target.embeddedPath == nil then return true end
        if type(target.embeddedPath) ~= "table" or #target.embeddedPath > 16 then return false end
        local count = 0
        for index, hop in pairs(target.embeddedPath) do
            if not integer(index) or index < 1 or index > #target.embeddedPath or type(hop) ~= "table" or
                not (hop.kind == "entry" or hop.kind == "action") or not integer(hop.index) or not integer(hop.attributeIndex) then return false end
            count = count + 1
        end
        return count == #target.embeddedPath and (count == 0 or target.kind == "entry" or target.kind == "action")
    end
    local function wrap_pending(callback)
        local pending = state.pending
        if not pending then return reject("not_queued", state.action_message or state.message) end
        local previous, finished = pending.callback, false
        pending.callback = function(response)
            finished = true
            if previous then
                local ok, err = pcall(previous, response)
                if not ok then reject("response_failed", tostring(err), true); return end
            end
            callback(response)
        end
        return function()
            if not finished and state.pending ~= pending then reject("operation_interrupted", "The editor replaced the pending request; inspect current status.", true) end
        end
    end
    if action == "status" then done(true, ai_snapshot()); return end
    if action == "list_actions" then
        refresh_action_state()
        if arguments.actionId ~= nil then
            if type(arguments.actionId) ~= "string" then return reject("invalid_arguments", "actionId must be a string") end
            local selected, links = action_details(state.capture_snapshot, arguments.actionId)
            if not selected then return reject("action_not_found", "The action was cleared or evicted; list actions again") end
            done(true, {action = selected, events = links, coverage = state.capture_snapshot.actionCoverage})
        else
            done(true, {actions = state.capture_snapshot.actions or {}, currentActions = state.capture_snapshot.currentActions or {},
                recording = state.capture_snapshot.recording, sceneEpoch = state.capture_snapshot.sceneEpoch,
                coverage = state.capture_snapshot.actionCoverage, counters = state.capture_snapshot.counters})
        end
        return
    end
    if action == "list_effects" then
        refresh_action_state()
        local resources = {}
        for _, resource in ipairs(state.capture_snapshot.resources or {}) do
            if arguments.includeUnconfirmed == true or resource.localConfirmed == true then resources[#resources + 1] = resource end
        end
        done(true, { resources = resources, recording = state.capture_snapshot.recording, sceneEpoch = state.capture_snapshot.sceneEpoch }); return
    end
    local supported = { open_effect = true, inspect = true, set_field = true, set_color = true, rename_group = true, set_group_note = true,
        undo = true, redo = true, save_copy = true, apply = true, restore = true, scene_time = true, capture_start = true, capture_stop = true }
    if not supported[action] then return reject("unknown_action", "This action is not available; arbitrary Lua and SDK execution are not supported") end
    refresh_action_state()
    if state.reloading or not M.active then return reject("editor_reloading", "The editor is reloading") end
    -- Restoration is the sole operation allowed to interrupt an existing apply.
    if action ~= "restore" and (action_preview_status().busy or state.pending or state.apply_requested or state.bindings_restore_pending or state.restore_preview_pending or
        state.bindings_snapshot.busy or #state.scene_queue > 0 or #state.effect_queue > 0) then return reject("busy", "Wait for the current editor operation to complete") end
    if action ~= "restore" and has_drafts() then return reject("drafts_present", "Submit or revert the editor's typed field, color, or curve inputs first") end
    if action == "inspect" or action == "set_field" or action == "set_color" or action == "rename_group" or action == "set_group_note" or
        action == "undo" or action == "redo" or action == "save_copy" or action == "apply" then
        if not state.document then return reject("no_document", "Open a captured effect first") end
        if not integer(arguments.expectedRevision) or arguments.expectedRevision ~= state.document.revision then
            return reject("stale_revision", "expectedRevision must equal the editor's current document revision")
        end
    end
    if action == "open_effect" then
        if type(arguments.path) ~= "string" or arguments.path == "" then return reject("invalid_arguments", "A complete captured effect path is required") end
        if state.document and state.document.dirty then return reject("unsaved_document", "Save the current edited document before opening another effect") end
        local wanted, selected = normalized_effect_path(arguments.path), nil
        for _, resource in ipairs(state.capture_snapshot.resources or {}) do
            if resource.localConfirmed == true and (normalized_effect_path(resource.path) == wanted or
                normalized_effect_path(resource.canonicalPath) == wanted or
                normalized_effect_path(resource.latestSource and resource.latestSource.actualPath) == wanted) then
                if selected then return reject("ambiguous_path", "More than one capture matches this complete path") end
                selected = resource
            end
        end
        if not selected then return reject("effect_not_found", "No locally confirmed captured resource has this complete path") end
        if not resolve_captured_resource(selected) then return reject("open_rejected", state.action_message) end
        return wrap_pending(function(response)
            if not response.ok then reject("backend_error", tostring(response.error))
            elseif not state.target_resource_path then reject("source_changed", state.action_message)
            else done(true, { document = state.document, targetResourcePath = state.target_resource_path }) end
        end)
    elseif action == "inspect" then
        if not valid_target(arguments.target) then return reject("invalid_arguments", "A valid document, group, entry, or action target is required") end
        if not inspect(arguments.target) then return reject("inspect_rejected", state.message) end
        return wrap_pending(function(response)
            if response.ok then done(true, { document = state.document, target = copy_target(state.selected), fields = state.fields,
                colorGroups = state.color_groups, curves = state.curves, expressionCurves = state.expression_curves })
            else reject("backend_error", tostring(response.error)) end
        end)
    elseif action == "apply" then
        local previous_verification, generation = state.verification, state.apply_generation
        if not publish_document(true) then return reject("apply_rejected", state.action_message) end
        local published, response_seen = nil, false
        local interrupted = wrap_pending(function(response)
            response_seen = true
            if not response.ok then reject("backend_error", tostring(response.error)); return end
            published = response.resourcePath
        end)
        return function()
            interrupted()
            if state.apply_generation ~= generation then reject("apply_interrupted", "Application was cancelled by editor restoration; inspect status.", true); return end
            if not response_seen or state.apply_requested or state.bindings_snapshot.busy then return end
            local verification = state.verification
            if published and verification and verification ~= previous_verification and verification.generatedPath == published then
                done(true, { accepted = true, resourcePath = published, verification = ai_snapshot().verification,
                    requiresAction = true, message = "Resource references were applied. Repeat the character action and check verification; visible rendering is not asserted." })
            else reject("apply_failed", state.action_message) end
        end
    elseif action == "restore" then
        stop_and_restore()
        return function()
            refresh_action_state()
            if state.bindings_restore_failed then reject("restore_failed", state.bindings_restore_failed); return end
            if state.restore_preview_error then reject("restore_failed", state.restore_preview_error); return end
            local action_status=action_preview_status()
            if state.action_edit_error then reject("restore_failed",state.action_edit_error);return end
            if action_edit then return end
            if action_status.phase=="restore_failed" then reject("restore_failed",action_status.message);return end
            if action_status.busy then return end
            if state.scene_control_error and #state.scene_queue == 0 then reject("restore_failed", state.scene_message); return end
            if state.apply_requested or state.bindings_restore_pending or state.bindings_snapshot.busy or state.restore_preview_pending or
                #state.scene_queue > 0 then return end
            local instance = client and client:get_instance("preview")
            if state.scene_original or state.global_time_original or state.effect_update_original or instance or (state.bindings_snapshot.appliedCount or 0) > 0 or
                state.bindings_snapshot.requiresRestoreBeforeReset or state.capture_snapshot.recording or action_status.requiresRestore then
                reject("restore_incomplete", "Restoration finished without confirming that every resource, preview, capture, and scene state was released"); return
            end
            done(true, { restored = true, status = ai_snapshot() })
        end
    elseif action == "scene_time" then
        local operation, value
        if arguments.operation == "pause" then operation, value = "pause", true
        elseif arguments.operation == "resume" then operation, value = "pause", false
        elseif arguments.operation == "speed" and type(arguments.speed) == "number" and arguments.speed == arguments.speed and
            arguments.speed >= 0.01 and arguments.speed <= 10 then operation, value = "speed", arguments.speed
        else return reject("invalid_arguments", "Use pause, resume, or speed with a finite multiplier from 0.01 through 10") end
        local previous = state.scene_history[#state.scene_history]
        if not queue_scene(operation, value) then return reject("scene_rejected", state.scene_message) end
        return function()
            if #state.scene_queue > 0 then return end
            local latest = state.scene_history[#state.scene_history]
            if latest == previous or not latest or latest.operation ~= operation or latest.value ~= value then
                reject("scene_interrupted", "The scene command was replaced; inspect current status", true)
            elseif not latest.ok then reject("scene_failed", state.scene_message)
            else done(true, { scene = state.scene_actual, message = state.scene_message }) end
        end
    elseif action == "capture_start" or action == "capture_stop" then
        local operation = action == "capture_start" and "start" or "stop"
        if not capture_command(operation) then return reject("capture_failed", state.action_message) end
        if state.capture_snapshot.recording ~= (operation == "start") then return reject("capture_failed", "Capture did not confirm the requested state") end
        done(true, { recording = state.capture_snapshot.recording }); return
    end
    local backend_action, data = action, {}
    if action == "set_field" or action == "set_color" then
        if not valid_target(arguments.target) or type(arguments.path) ~= "string" then return reject("invalid_arguments", "A target and exact field/color path are required") end
        data.target, data.path = copy_target(arguments.target), arguments.path
        if action == "set_field" then
            if type(arguments.value) ~= "string" then return reject("invalid_arguments", "Field value must be its exact text representation") end
            backend_action, data.value = "set", arguments.value
            if arguments.target.kind == "group" then
                if arguments.path ~= "note" then return reject("readonly_group_name", "Native group names are read-only; use the note field") end
                local valid, reason = valid_group_note(arguments.value)
                if not valid then return reject("invalid_arguments", reason) end
                backend_action, data = "set_group_note", { groupIndex = arguments.target.index, note = arguments.value }
            end
        else
            if type(arguments.values) ~= "table" or #arguments.values ~= 4 then return reject("invalid_arguments", "RGBA requires four values") end
            data.values = arguments.values
        end
    elseif action == "rename_group" or action == "set_group_note" then
        local note = action == "rename_group" and arguments.name or arguments.note
        if not integer(arguments.index) or type(note) ~= "string" then return reject("invalid_arguments", "A group index and note are required") end
        local valid, reason = valid_group_note(note)
        if not valid then return reject("invalid_arguments", reason) end
        backend_action, data.groupIndex, data.note = "set_group_note", arguments.index, note
    elseif action == "save_copy" then
        if arguments.path ~= nil and type(arguments.path) ~= "string" then return reject("invalid_arguments", "Save path must be a string") end
        backend_action, data.path = "save", arguments.path ~= "" and arguments.path or nil
    end
    if not send(backend_action, data, function(response)
        if response.ok then
            if action ~= "save_copy" then clear_selection() end
            done(true, { document = state.document, outputPath = response.outputPath, revision = response.revision or state.document and state.document.revision,
                noteSaved = response.noteSaved, noteStorage = response.noteStorage, noteMessage = response.noteMessage })
        else reject("backend_error", tostring(response.error)) end
    end) then return reject("backend_not_ready", state.message) end
    -- Preserve the existing completion callback while also detecting a manual
    -- synchronization which displaced this uncertain backend operation.
    return wrap_pending(function() end)
end
M.execute_ai = ai_execute
local function diagnostics()
    local now = os.time()
    if now - state.last_diagnostic < 2 then return end
    state.last_diagnostic = now
    if not json or not json.dump_file then return end
    local ok, err = pcall(function()
        if not action_refresh.in_frame or action_refresh.full_frame ~= action_refresh.frame then refresh_action_state() end
        local instance = client and client:get_instance("preview")
        local owned = instance ~= nil
        if instance then
            local read_ok, actual = pcall(function() return read_effect(instance) end)
            if read_ok then state.effect_actual = actual end
        else state.effect_actual = nil end
        if state.scene_actual then
            local read_ok, actual = pcall(function()
                local scene = get_scene()
                return read_scene(scene, scene_accessors())
            end)
            if read_ok then state.scene_actual = actual end
        end
        local resources, events = state.capture_snapshot.resources or {}, state.capture_snapshot.events or {}
        local resource_sample, event_sample = {}, {}
        local confirmed_resources = 0
        for _, resource in ipairs(resources) do if resource.localConfirmed == true then confirmed_resources = confirmed_resources + 1 end end
        for i = 1, math.min(#resources, 100) do resource_sample[#resource_sample + 1] = resources[i] end
        for i = math.max(1, #events - 19), #events do event_sample[#event_sample + 1] = events[i] end
        local payload = { version = M.version, loadedAt = state.loaded_at, observedAt = now,
            aiProtocol = 1, aiReady = M.active and ai_bridge ~= nil and ai_bridge.ready == true,
            aiBusy = state.ai_busy == true, aiRequestId = state.ai_request_id, aiAction = state.ai_action,
            aiError = ai_error or ai_bridge and ai_bridge.error, hasDrafts = has_drafts(),
            loaded = M.active, backendReady = is_ready(), runtimeLoaded = client ~= nil,
            runtimeVersion = type(runtime) == "table" and runtime.version or nil,
            runtimePointCloudSupported = has_point_cloud(), pointCloudRequested = state.point_cloud_enabled,
            targetResourcePath = state.target_resource_path, actionStatus = state.action_message,
            captureVersion = capture and capture.version, captureRecording = state.capture_snapshot.recording,
            captureActor = state.capture_snapshot.actor, captureResourceCount = #(state.capture_snapshot.resources or {}),
            captureCurrentActions = state.capture_snapshot.currentActions, captureActions = state.capture_snapshot.actions,
            captureActionStatus = state.capture_snapshot.actionStatus, captureActionCoverage = state.capture_snapshot.actionCoverage,
            captureConfirmedResourceCount = confirmed_resources, captureUnconfirmedResourceCount = #resources - confirmed_resources,
            captureEventCount = #(state.capture_snapshot.events or {}), captureCounters = state.capture_snapshot.counters,
            captureResources = resource_sample, captureResourcesTruncated = #resources > #resource_sample,
            captureRecentEvents = event_sample, captureEventsTruncated = #events > #event_sample,
            captureInstalledHooks = state.capture_snapshot.installedHooks, captureRequestedHooks = state.capture_snapshot.requestedHooks,
            captureHooks = state.capture_snapshot.hooks,
            captureErrors = state.capture_snapshot.errors, captureError = capture_error or state.capture_error,
            bindings = state.bindings_snapshot, bindingsVersion = bindings and bindings.version, bindingsError = bindings_error or state.bindings_error,
            actionPreview=action_preview_status(),pfbPreview=pfb_preview_status(),actionSlotEditBusy=action_edit~=nil,actionSlotEditError=state.action_edit_error,
            bindingsRestorePending = state.bindings_restore_pending, bindingsRestoreFailed = state.bindings_restore_failed,
            applyRequested = state.apply_requested,
            verification = state.verification and { phase = state.verification.phase, originalPath = state.verification.originalPath,
                generatedPath = state.verification.generatedPath, observedPath = state.verification.observedPath,
                eventId = state.verification.eventId, motion = state.verification.motion,
                motionAssociation = state.verification.motionAssociation, message = state.verification.message },
            lastStatus = state.message, lastError = runtime_error or state.status_error, pendingId = state.pending and state.pending.id,
            source = state.document and state.document.source, revision = state.document and state.document.revision,
            sourceKind = state.document and state.document.sourceKind or "local_file",
            gameSource = state.document and state.document.gameSource,
            selectedTarget = copy_target(state.selected), selectedTitle = state.selected and selected_title(state.selected),
            lastSubmittedField = state.last_submitted_field,
            previewPath = instance and instance.path or state.published_path, ownedPreview = owned,
            previewStatus = state.preview_message, sceneStatus = state.scene_message,
            windowOpen = state.window_open, windowDrawn = state.window_drawn, activeTab = state.active_tab, uiError = state.ui_error,
            uiRenderer = workbench and "REFramework D2D" or "ImGui fallback",
            workbenchPage = workbench and (workbench.top or workbench.page),
            effectRecordView = workbench and workbench.effect_view_status,
            windowHidden = state.window_hidden, parameterLanguage = state.parameter_language,
            orb = workbench and workbench.orb_status,
            precache = M.precache_status(),
            editLayout = state.edit_layout, fontScale = state.font_scale,
            scenePaused = state.scene_actual and state.scene_actual.paused, timeScale = state.scene_actual and state.scene_actual.speed,
            sceneNativePaused = state.scene_actual and state.scene_actual.native_paused,
            scenePauseRequested = state.scene_pause ~= nil,
            scenePauseSuspended = state.scene_pause and state.scene_pause.suspended or false,
            scenePauseMode = "scene_global_and_effect_update", sceneResumeSpeed = state.scene_pause and state.scene_pause.resume_speed,
            sceneControlError = state.scene_control_error,
            sceneRestorePending = state.scene_original ~= nil or state.global_time_original ~= nil or state.effect_update_original ~= nil,
            globalTimeScale = state.global_time_actual, globalDeltaTime = state.global_delta_time,
            globalTimeRestorePending = state.global_time_original ~= nil,
            globalResumeSpeed = state.global_time_original and state.global_time_original.speed,
            effectUpdateEnabled = state.effect_update_actual, effectUpdateRestorePending = state.effect_update_original ~= nil,
            effectUpdateConfirmedFrames = state.effect_update_frames or 0,
            sceneCommandHistory = state.scene_history, scenePauseConfirmedFrames = state.scene_pause_frames,
            debugInput = debug_hotkeys and debug_hotkeys:status(), debugInputError = debug_error,
            sceneFrame = state.scene_actual and state.scene_actual.frame,
            effectPaused = state.effect_actual and state.effect_actual.paused, effectSpeed = state.effect_actual and state.effect_actual.speed,
            effectReadyResource = state.effect_actual and state.effect_actual.ready_resource,
            effectRunning = state.effect_actual and state.effect_actual.running,
            effectValid = state.effect_actual and state.effect_actual.valid,
            effectEmitterInstanceNum = state.effect_actual and state.effect_actual.emitters,
            effectResourcePath = state.effect_actual and state.effect_actual.resource_path,
            effectState = state.effect_actual and state.effect_actual.player_state,
            effectCulled = state.effect_actual and state.effect_actual.culled,
            effectGroupEnabled = state.effect_actual and state.effect_actual.group_enabled,
            effectReadErrors = state.effect_actual and state.effect_actual.read_errors }
        local written = json.dump_file("MotionVfxLive/ui-status.json", payload)
        if written == false then fail("UI 状态文件写入失败") end
    end)
    if not ok then log_warning("诊断写入失败：" .. tostring(err)) end
end
M.diagnostics = diagnostics
local function frame()
    action_refresh.frame = action_refresh.frame + 1
    action_refresh.in_frame = true
    poll()
    state.module_frames = state.module_frames + 1
    local action_refresh_due = state.module_frames >= 15
    if action_refresh_due then state.module_frames = 0 end
    refresh_regular_action_state(action_refresh_due)
    if state.restore_after_busy then
        refresh_action_state()
        if not state.apply_requested and not state.bindings_snapshot.busy then
            state.bindings_restore_pending, state.restore_after_busy = false, false
            restore_bindings()
        end
    end
    update_action_edit()
    local commands = state.scene_queue
    state.scene_queue = {}
    for _, command in ipairs(commands) do change_scene(command) end
    maintain_scene_pause()
    if ai_bridge then
        local ai_ok, err = pcall(function() ai_bridge:tick() end)
        if not ai_ok then ai_error = tostring(err); log_warning("AI 桥错误：" .. ai_error) end
    end
    state.debug_text_input_active = false
    local ui_ok, ui_error = pcall(function()
        if workbench then workbench:frame() else draw_window() end
    end)
    if not ui_ok then state.ui_error = tostring(ui_error); log_warning("窗口绘制失败：" .. state.ui_error) end
    -- Sample after drawing so the current frame's text focus is respected,
    -- including the first frame of typing. Like buttons, toggles use the queue.
    if debug_hotkeys then debug_hotkeys:tick() end
    diagnostics()
    action_refresh.in_frame = false
end
M.frame = frame
local function reset(full_script_reset)
    if not M.active then return true end
    refresh_action_state()
    local outstanding = pfb_preview_status().busy or pfb_preview_status().requiresRestore or action_edit~=nil or action_preview_status().busy or action_preview_status().requiresRestore or state.apply_requested or state.bindings_restore_pending or
        state.bindings_restore_failed ~= nil or state.bindings_snapshot.busy or
        state.bindings_snapshot.requiresRestoreBeforeReset or (state.bindings_snapshot.appliedCount or 0) > 0
    if outstanding and not full_script_reset then
        state.action_message = "请先还原资源试改并等待完成，再重载编辑器。"
        return false, state.action_message
    end
    if outstanding then
        log_warning("全部脚本已重置，但资源试改尚未确认还原；不能在脚本销毁回调中调用原生引用替换。")
    end
    local scene_restored = restore_scene()
    if not scene_restored and not full_script_reset then return false, state.scene_message end
    if debug_hotkeys then debug_hotkeys:disable() end
    if workbench then workbench.active=false;workbench.widgets.commands={} end
    if precache_runtime then precache_runtime:close() end
    if capture then capture_command("stop") end
    if ai_bridge then ai_bridge:shutdown() end
    M.active = false
    state.scene_queue, state.effect_queue = {}, {}
    -- The shared runtime owns synchronous reset cleanup of all its instances.
    -- Do not enqueue a shutdown which cannot be drained after script teardown.
    _G.MotionVfxLive = nil
    return true
end
M.reset = reset

local registered, registration_error = pcall(function()
    re.on_frame(function() if M.active then frame() end end)
    re.on_draw_ui(function() if M.active then draw_launcher() end end)
    re.on_pre_application_entry("LockScene", function()
        if M.active then
            apply_effect_commands()
            -- EFX holders must be restored before rebuilding their PFB owners.
            local b=bindings and ((bindings.current_status and bindings.current_status()) or (bindings.snapshot and bindings.snapshot())) or {}
            if action_preview and not state.apply_requested and not state.bindings_restore_pending and
                not b.busy and not b.requiresRestoreBeforeReset and (b.appliedCount or 0)==0 then action_preview:tick() end
            if pfb_preview and not state.apply_requested and not state.bindings_restore_pending and
                not b.busy and not b.requiresRestoreBeforeReset and (b.appliedCount or 0)==0 then pfb_preview:tick() end
            if precache_runtime then precache_runtime:tick() end
        end
    end)
    re.on_script_reset(function() reset(true) end)
end)
if not registered then log_warning("注册失败：" .. tostring(registration_error)); return nil end
M.active = true
_G.MotionVfxLive = M
do
    local transaction=load_action_module("MotionVfxActionPreviewTransaction","MotionVfxLive.fx_action_preview_transaction","fx_action_preview_transaction.lua")
    local adapter=load_action_module("MotionVfxActionPreviewRuntime","MotionVfxLive.fx_action_preview_runtime","fx_action_preview_runtime.lua")
    if transaction and adapter then
        action_preview=adapter.new(transaction,{capture=function()return capture and capture.snapshot() or {}end,
            blocked=function()
                local b=bindings and bindings.snapshot and bindings.snapshot() or {}
                return pfb_preview_status().busy or pfb_preview_status().requiresRestore or state.apply_requested or state.bindings_restore_pending or b.busy or b.requiresRestoreBeforeReset or (b.appliedCount or 0)>0
            end})
    end
    local pfb_runtime=load_action_module("MotionVfxPfbPreviewRuntime","MotionVfxLive.fx_pfb_preview_runtime","fx_pfb_preview_runtime.lua")
    if pfb_runtime then pfb_preview=pfb_runtime.new({capture=function()return capture and capture.snapshot() or {}end,
        aliases=function()return action_preview and action_preview:aliases() or {}end,
        blocked=function()
            local b=bindings and bindings.snapshot and bindings.snapshot() or {};local a=action_preview_status()
            return a.busy or a.requiresRestore or state.apply_requested or state.bindings_restore_pending or b.busy or b.requiresRestoreBeforeReset or (b.appliedCount or 0)>0
        end})end
    local module, err = load_action_module("MotionVfxPrecacheRuntime", "MotionVfxLive.fx_precache_runtime", "fx_precache_runtime.lua")
    if module and type(module.new)=="function" then precache_runtime=module.new() end
    if d2d and type(d2d.register)=="function" then
        local widgets=load_action_module("MotionVfxD2DWidgets","MotionVfxLive.fx_d2d_widgets","fx_d2d_widgets.lua")
        local factory=load_action_module("MotionVfxD2DWorkbench","MotionVfxLive.fx_d2d_workbench","fx_d2d_workbench.lua")
        local pfb_factory=load_action_module("MotionVfxPfbPage","MotionVfxLive.fx_pfb_page","fx_pfb_page.lua")
        local file_dialog_factory=load_action_module("MotionVfxFileDialog","MotionVfxLive.fx_file_dialog","fx_file_dialog.lua")
        if widgets and factory then
            local ok,value=pcall(factory.new,M,widgets,pfb_factory,file_dialog_factory)
            if ok then workbench=value;M.workbench=value else state.ui_error=tostring(value) end
        end
    end
end
do
    local factory, err = load_action_module("MotionVfxDebugHotkeys", "MotionVfxLive.fx_debug_hotkeys", "fx_debug_hotkeys.lua")
    if factory and type(factory.new) == "function" then
        local ok, result = pcall(factory.new, {
            native = _G.MotionVfxDebugInput,
            read = function(path) return json.load_file(path) end,
            write = function(path, value) return json.dump_file(path, value) end,
            menu_open = function() return reframework:is_drawing_ui() end,
            text_input_active = function() return state.debug_text_input_active == true end,
            blocked = function() return not M.active or state.reloading or state.ai_busy end,
            toggle = function()
                if #state.scene_queue > 0 or state.bindings_restore_pending then return false, "场景或资源操作尚未完成。" end
                return queue_scene("pause", state.scene_pause == nil)
            end,
        })
        if ok then debug_hotkeys = result; M.debug_hotkeys = result else debug_error = tostring(result) end
    else debug_error = tostring(err or "module unavailable") end
end
do
    local factory, err = load_action_module("MotionVfxAiBridge", "MotionVfxLive.fx_ai_bridge", "fx_ai_bridge.lua")
    if factory and type(factory.new) == "function" then
        local ok, result = pcall(factory.new, { read = function(path) return json.load_file(path) end,
            write = function(path, value) return json.dump_file(path, value) end, now = os.time, clock = os.clock, start = ai_execute,
            exists = function(path)
                -- REFramework's io.open is restricted to its data directory.
                -- JSON parsing alone cannot distinguish a missing receipt from
                -- a truncated one: its load_file returns nil in both cases.
                local file, err, code = io.open(path, "rb")
                if file then file:close(); return true end
                if code == 2 then return false end
                -- Some REFramework builds wrap io.open as sol::object and
                -- preserve only nil, losing errno. fs.glob uses a regex over
                -- the same data directory and provides independent existence.
                if fs and type(fs.glob) == "function" then
                    local paths = fs.glob("^MotionVfxLive[/\\\\]ai-journal\\.json$")
                    if type(paths) ~= "table" then error("AI journal existence lookup returned no file list") end
                    for _, found in ipairs(paths) do
                        if type(found) == "string" and found:gsub("\\", "/"):lower() == path:lower() then return true end
                    end
                    return false
                end
                error("Cannot verify AI journal existence: " .. tostring(err))
            end,
            busy = function(busy, id, action) state.ai_busy, state.ai_request_id, state.ai_action = busy, id, action end })
        if ok then ai_bridge = result else ai_error = tostring(result) end
    else ai_error = tostring(err or "AI bridge module is unavailable") end
end
return M
