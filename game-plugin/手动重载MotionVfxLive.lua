-- Manual Run Script helper. Do NOT install this file in autorun.
-- Reloads only MotionVfxLive after its own instances have stopped and its
-- scene changes have been restored. Other scripts and shared runtime survive.
local previous_reload = _G.MotionVfxLiveReload
if previous_reload and previous_reload.active then
    if log and log.warn then log.warn("[MotionVfxLive] A reload is already pending; not scheduling another.") end
    return previous_reload
end

local old = _G.MotionVfxLive
local shared = _G.fx_preview_runtime_v2
local reload = { active = true, phase = "prepare", startedAt = os.time(), lastReport = -1 }
local previous_window = old and old.state and old.state.window_open
local replacement_loader
_G.MotionVfxLiveReload = reload

local function report(phase, message, finished)
    reload.phase, reload.message = phase, message
    if finished then reload.active = false end
    if log and log.info then log.info("[MotionVfxLive reload] " .. phase .. ": " .. message) end
    if json and json.dump_file then
        pcall(json.dump_file, "MotionVfxLive/reload-status.json", {
            phase = phase, message = message, active = reload.active,
            startedAt = reload.startedAt, observedAt = os.time(),
            version = _G.MotionVfxLive and _G.MotionVfxLive.version,
        })
    end
end
local function fail(message)
    if old and old.active and old.state then
        old.state.reloading = false
        old.state.window_open = previous_window
    end
    report("failed", message, true)
end
local function load_new()
    local bindings=_G.MotionVfxActionBindings
    if bindings and bindings.version=="1.2.1" then
        if not reload.bindingUpgrade then
            local loader,error_message=loadfile("reframework/autorun/MotionVfxLive/fx_action_bindings.lua")
            if not loader then error(error_message) end
            reload.bindingUpgrade=true
            local accepted,reason=bindings.shutdown(function(result) reload.bindingUpgradeResult=result end)
            if not accepted then error(reason) end
            report("upgrading_bindings","Waiting for the restored binding module to shut down safely.")
            return
        end
        local result=reload.bindingUpgradeResult
        if not result then return end
        if not result.ok or result.requiresRestoreBeforeReset then error("Binding module upgrade could not release its original backups") end
        _G.MotionVfxActionBindings=nil
        package.loaded["MotionVfxLive.fx_action_bindings"]=nil
    end
    if old and old.active then
        old.reset()
        if old.active or _G.MotionVfxLive == old then error("Old editor did not deactivate") end
    end
    -- Preserve both the shared module's cache and its global singleton.
    package.loaded["MotionVfxLive"] = nil
    -- This factory owns no shared captures/backups. Reload it after the old
    -- controller releases input, so fixes do not need Reset scripts.
    package.loaded["MotionVfxLive.fx_debug_hotkeys"] = nil
    package.loaded["MotionVfxLive.fx_d2d_widgets"] = nil
    package.loaded["MotionVfxLive.fx_d2d_workbench"] = nil
    package.loaded["MotionVfxLive.fx_ai_bridge"] = nil
    _G.MotionVfxAiBridge = nil
    package.loaded["MotionVfxLive.fx_precache_runtime"] = nil
    package.loaded["MotionVfxLive.fx_parameter_ui"] = nil
    package.loaded["MotionVfxLive.fx_action_sources"] = nil
    package.loaded["MotionVfxLive.fx_attachment_targets"] = nil
    package.loaded["MotionVfxLive.fx_pfb_page"] = nil
    package.loaded["MotionVfxLive.fx_file_dialog"] = nil
    _G.MotionVfxFileDialog=nil
    package.loaded["MotionVfxLive.fx_pfb_preview_runtime"] = nil
    package.loaded["MotionVfxLive.fx_equipment_sources"] = nil
    package.loaded["MotionVfxLive.fx_equipment_pfb_preview_runtime"] = nil
    package.loaded["MotionVfxLive.fx_action_preview_transaction"] = nil
    package.loaded["MotionVfxLive.fx_action_preview_runtime"] = nil
    _G.MotionVfxActionSources=nil
    _G.MotionVfxAttachmentTargets=nil
    _G.MotionVfxActionPreviewRuntime=nil
    _G.MotionVfxPfbPreviewRuntime=nil
    _G.MotionVfxEquipmentSources=nil
    _G.MotionVfxEquipmentPfbPreviewRuntime=nil
    local current = replacement_loader()
    if type(current) ~= "table" or not current.active or _G.MotionVfxLive ~= current then
        error("Replacement editor did not initialize; load the corrected main script manually")
    end
    if shared and _G.fx_preview_runtime_v2 ~= shared then error("Shared runtime unexpectedly changed") end
    package.loaded["MotionVfxLive"] = current
    local polled=current.poll and pcall(current.poll)
    local same_backend=old and old.state and current.state and old.state.action_document and
        old.state.action_document_pid==(current.state.status or {}).pid
    if same_backend then
        current.state.action_document=old.state.action_document;current.state.action_document_pid=old.state.action_document_pid
    end
    if same_backend and old.workbench and current.workbench then
        -- These are plain document/form values, never engine references. The
        -- backend keeps the same workspace during a script-only reload.
        for _,key in ipairs({"action_document","action_proof","slot_template","slot_template_label",
            "slot_template_filter","slot_resource","slot_group","slot_joint","slot_start","slot_end","slot_lifetime_mode","slot_lifetime",
            "slot_groups","slot_groups_path","slot_attach_kind","slot_attach_key","slot_attachment_mode"}) do
            current.workbench[key]=old.workbench[key]
        end
    end
    if old and old.workbench and current.workbench then
        current.workbench.desktop_jobs=old.workbench.desktop_jobs or {}
        if old.state.status and current.state.status and old.state.status.pid==current.state.status.pid and old.workbench.pfb and current.workbench.pfb then
            current.state.pfb_document_pid=old.state.pfb_document_pid or (old.workbench.pfb.document and old.state.status.pid)
            for _,key in ipairs({"document","selected","values","filter","resource","target_path","game_path","target"})do
                current.workbench.pfb[key]=old.workbench.pfb[key]
            end
        end
    end
    report("complete", "Only MotionVfxLive was reloaded; shared runtime and other scripts were preserved.", true)
    -- Recover the backend's current in-memory document without reopening a
    -- file or changing its edits. A backend outage must not undo a good reload.
    if current.poll and current.synchronize then
        local sync_ok, sync_result = pcall(function() if not polled then current.poll()end; return current.synchronize() end)
        if (not sync_ok or not sync_result) and log and log.warn then
            log.warn("[MotionVfxLive reload] Editor loaded; document sync was unavailable. Use the manual sync button.")
        end
    end
end

-- Resolve and compile the deployed main script BEFORE hiding the old UI,
-- changing the scene, or shutting down its client. Never rely on package.path
-- from the directory where this manual helper happened to be selected.
local compile_ok, loader, compile_error = pcall(loadfile, "reframework/autorun/MotionVfxLive.lua")
if not compile_ok or type(loader) ~= "function" then
    fail("Replacement script could not be compiled; old editor was left active: " .. tostring(compile_ok and compile_error or loader))
    return reload
end
replacement_loader = loader

if old and old.active then
    if not old.state or not old.stop_and_restore or not old.reset then fail("Loaded editor does not support controlled reload"); return reload end
    if old.state.pending then fail("An editor request is still pending; wait for its response before reloading"); return reload end
    -- Unsubmitted text and picker values live only in Lua. A backend save
    -- cannot preserve them, so keep the current editor visible until committed.
    local drafts = false
    if type(old.has_drafts) == "function" then
        local ok, result = pcall(old.has_drafts)
        if not ok then fail("Could not verify pending inputs; the existing editor was kept active."); return reload end
        drafts = result
    else
        for _, field in ipairs(old.state.fields or {}) do
            local value = (old.state.values or {})[field.path]
            if field.editable == true and value ~= nil and value ~= tostring(field.value or "") then drafts = true end
        end
        for key, baseline in pairs(old.state.curve_baseline or {}) do
            if old.state[key] ~= baseline then drafts = true end
        end
    end
    if drafts then fail("有未提交输入；请先提交或还原字段、颜色及曲线，再重载编辑器。当前输入已保留。"); return reload end
    old.state.reloading = true
    old.state.window_open = false
    -- Reload only releases native references; it is not a request to discard
    -- the user's editable action slots.
    old.stop_and_restore(true)
    report("restoring", "Waiting for this editor's scene changes to be restored.")
else
    report("loading", "No active editor; loading the main script.")
end

re.on_frame(function()
    if not reload.active then return end
    local ok, err = pcall(function()
        if not old or not old.active then load_new(); return end
        if reload.phase == "restoring" then
            if old.state.action_edit_error then fail("Action slot edit failed: "..tostring(old.state.action_edit_error));return end
            if old.action_edit_pending and old.action_edit_pending() then return end
            if old.state.bindings_restore_failed then
                fail("Resource restore did not complete: " .. tostring(old.state.bindings_restore_failed)); return
            end
            local binding_module = _G.MotionVfxActionBindings
            local action_status=old.action_preview_status and old.action_preview_status() or {}
            local pfb_status=old.pfb_preview_status and old.pfb_preview_status() or {}
            if pfb_status.busy or pfb_status.requiresRestore then
                if os.time()-reload.startedAt>25 then fail("Waiting for independent PFB restore: "..tostring(pfb_status.message))end
                return
            end
            if action_status.busy or action_status.requiresRestore then
                if os.time()-reload.startedAt>25 then fail("Waiting for action/PFB restore: "..tostring(action_status.message))end
                return
            end
            local binding_status = binding_module and binding_module.snapshot and binding_module.snapshot()
            if old.state.bindings_restore_pending or (binding_status and (binding_status.busy or
                binding_status.requiresRestoreBeforeReset or (binding_status.appliedCount or 0) > 0)) then
                if os.time() - reload.startedAt > 20 then
                    fail("Waiting for real-action resource restore; editor kept active. " .. tostring(binding_status and binding_status.message))
                end
                return
            end
            if old.state.scene_original or old.state.global_time_original or old.state.effect_update_original then
                if os.time() - reload.startedAt > 15 then fail("Scene restore did not complete: " .. tostring(old.state.scene_message)) end
                return
            end
            local client = shared and shared.clients and shared.clients.MotionVfxLive
            if not client or client.closed then load_new(); return end
            local id, error_message = client:shutdown(function(result)
                reload.shutdownResult = result
            end)
            if not id then fail("Could not queue owner cleanup: " .. tostring(error_message)); return end
            report("stopping", "Waiting for the shared runtime to stop only the MotionVfxLive owner's instances.")
        elseif reload.phase == "stopping" then
            local result = reload.shutdownResult
            if result then
                if not result.ok then fail("Owned instance cleanup failed: " .. tostring(result.message)); return end
                load_new()
            elseif os.time() - reload.startedAt > 15 and os.time() - reload.lastReport > 10 then
                reload.lastReport = os.time()
                report("stopping", "Cleanup is still pending; waiting for LockScene. No replacement has been loaded.")
            end
        elseif reload.phase == "loading" or reload.phase == "upgrading_bindings" then load_new() end
    end)
    if not ok then fail(tostring(err)) end
end)
return reload
