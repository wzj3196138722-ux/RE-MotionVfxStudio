-- Temporary validation helper; never distributed with the editor.
-- Confirms only the one-button PC compatibility notice observed at startup.
-- The action runs once on preLockScene, never on the HTTP worker thread.
local guard = "MotionVfxStartupNotice080Attempt"
if rawget(_G, guard) then return end
local result = { startedAt = os.time(), attempted = false, ok = false, phase = "pending" }
rawset(_G, guard, result)
local pending = true
local inspectAfter = false
local notify

local function persist()
    local ok, err = pcall(json.dump_file, "MotionVfxLive/menu-confirm080.json", result)
    if not ok and log and log.error then log.error("Startup notice diagnostic: " .. tostring(err)) end
end

local function fullName(object)
    return object and object:get_type_definition():get_full_name() or nil
end

local function snapshot(module)
    local out = { state = tonumber(module:get_field("_State")) }
    local info = module:get_field("_CurInfo")
    out.hasCurrent = info ~= nil
    if info then
        out.currentId = tonumber(info:call("get_NotifyWindowId"))
        out.isEnd = info:call("get_IsEnd")
        out.selectedIndex = tonumber(info:call("get_SelectedIndex"))
        out.defaultIndex = tonumber(info:call("get_DefaultIndex"))
        local caller = info:call("get_Caller")
        out.callerType = fullName(caller)
        if out.callerType == "app.LogoController.cPcSpecCheck" then
            out.callerRno = tonumber(caller:get_field("Rno"))
        end
        local choices = info:call("get_ChoisesTextInfo")
        out.choiceCount = choices and tonumber(choices:call("get_Count")) or nil
    end
    return out
end

local function afterSnapshot()
    local ok, value = pcall(snapshot, notify)
    if ok then return value end
    return { error = tostring(value) }
end

persist()
re.on_pre_application_entry("LockScene", function()
    if inspectAfter then
        inspectAfter = false
        result.after = afterSnapshot()
        result.finishedAt = os.time()
        result.phase = "completed"
        persist()
        return
    end
    if not pending then return end
    -- Consume this attempt before inspecting: exceptions can never retry later.
    pending = false
    local ok, err = pcall(function()
        local manager = assert(sdk.get_managed_singleton("app.GUIManager"), "GUI manager unavailable")
        local all = assert(manager:get_field("_AllGUI"), "GUI array unavailable")
        assert(all:get_size() > 1, "GUI array does not contain the observed notice")
        local gui = assert(all:get_element(1), "Observed notice GUI unavailable")
        assert(fullName(gui) == "app.GUI000002", "GUI identity changed; refusing confirmation")
        assert(tonumber(gui:get_field("_BaseState")) == 2, "Notice GUI is not visible")
        notify = assert(gui:get_field("_NotifyWindowApp"), "Notify module unavailable")
        assert(fullName(notify) == "app.cGUISystemModuleNotifyWindowApp", "Unexpected notify module")
        result.before = snapshot(notify)
        local before = result.before
        assert(before.state == 1 and before.hasCurrent, "Notify module is not open")
        assert(before.currentId == 565, "Current notice is not PC compatibility ID 565")
        assert(before.callerType == "app.LogoController.cPcSpecCheck" and before.callerRno == 11,
            "PC compatibility caller or phase changed")
        assert(before.choiceCount == 1 and before.defaultIndex == 0, "Notice no longer has one default button")
        assert(before.isEnd == false and before.selectedIndex == -1, "Notice has already ended or been selected")
        assert(notify:get_type_definition():get_method("endNotifyWindow(System.Int32)"), "Expected endNotifyWindow signature unavailable")
        result.attempted = true
        result.phase = "invoking"
        persist()
        notify:call("endNotifyWindow(System.Int32)", 0)
        result.ok = true
        result.afterImmediate = afterSnapshot()
        result.phase = "awaiting_after_snapshot"
        inspectAfter = true
    end)
    if not ok then
        result.error = tostring(err)
        result.phase = result.attempted and "error" or "refused"
        result.finishedAt = os.time()
        if notify then result.after = afterSnapshot() end
    end
    persist()
end)
