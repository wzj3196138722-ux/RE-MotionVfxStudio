-- Temporary local validation only. Does not focus Windows or send input.
-- Tests the observed inactive-application hypothesis once, after the exact
-- compatibility notice was acknowledged. The original Active value is retained.
local key = "MotionVfxStartupActive080"
if rawget(_G, key) then return end
local state = { pending=true, restorePending=false, changed=false,
    report={phase="pending",startedAt=os.time(),attempted=false,ok=false} }
rawset(_G, key, state)

local function persist()
    local ok, err = pcall(json.dump_file, "MotionVfxLive/menu-active080.json", state.report)
    if not ok and log and log.error then log.error("Startup Active diagnostic: "..tostring(err)) end
end
local function native(method, ...)
    return sdk.call_native_func(state.application, state.typeDefinition, method, ...)
end
local function notice()
    local manager=assert(sdk.get_managed_singleton("app.GUIManager"),"GUI manager unavailable")
    local all=assert(manager:get_field("_AllGUI"),"GUI array unavailable")
    assert(all:get_size()>1,"Observed GUI unavailable")
    local gui=assert(all:get_element(1),"Observed GUI unavailable")
    assert(gui:get_type_definition():get_full_name()=="app.GUI000002","Unexpected GUI type")
    local module=assert(gui:get_field("_NotifyWindowApp"),"Notify module unavailable")
    local info=module:get_field("_CurInfo")
    local out={state=tonumber(module:get_field("_State")),hasCurrent=info~=nil}
    if info then
        out.currentId=tonumber(info:call("get_NotifyWindowId"))
        out.isEnd=info:call("get_IsEnd")
        out.selectedIndex=tonumber(info:call("get_SelectedIndex"))
        local caller=info:call("get_Caller")
        out.callerType=caller and caller:get_type_definition():get_full_name()
        if out.callerType=="app.LogoController.cPcSpecCheck" then out.callerRno=tonumber(caller:get_field("Rno")) end
    end
    return out
end
local function readback()
    local out={active=native("get_Active()"),hasFocus=native("get_HasFocus()")}
    local ok,value=pcall(notice)
    if ok then out.notice=value else out.noticeError=tostring(value) end
    return out
end
-- Public restore only schedules work; the mutation always runs in preLockScene.
state.restore=function()
    if state.changed then state.restorePending=true;return true end
    return false
end
persist()
re.on_pre_application_entry("LockScene",function()
    if state.restorePending then
        state.restorePending=false
        local ok,err=pcall(function()
            assert(state.changed and type(state.originalActive)=="boolean","No original Active value")
            assert(sdk.get_native_singleton("via.Application")==state.application,"Application identity changed; refusing restore")
            native("set_Active(System.Boolean)",state.originalActive)
            state.report.restored=readback()
            assert(state.report.restored.active==state.originalActive,"Active restore readback mismatch")
            state.changed=false;state.afterPending=false
        end)
        state.report.restoreOk=ok;state.report.restoreError=not ok and tostring(err) or nil
        state.report.phase=ok and "restored" or "restore_error";persist();return
    end
    if state.afterPending then
        state.afterPending=false
        local ok,value=pcall(readback)
        if ok then state.report.after=value else state.report.afterError=tostring(value) end
        state.report.phase="observed";persist();return
    end
    if not state.pending then return end
    state.pending=false
    local ok,err=pcall(function()
        state.application=assert(sdk.get_native_singleton("via.Application"),"Application unavailable")
        state.typeDefinition=assert(sdk.find_type_definition("via.Application"),"Application metadata unavailable")
        for _,method in ipairs({"get_Active()","get_HasFocus()","set_Active(System.Boolean)"})do
            assert(state.typeDefinition:get_method(method),"Missing Application method: "..method)
        end
        state.report.before=readback()
        local before=state.report.before
        assert(before.active==false and before.hasFocus==true,"Observed Active/HasFocus condition changed")
        local n=assert(before.notice,"Current notice unreadable")
        assert(n.state==1 and n.currentId==565 and n.isEnd==true and n.selectedIndex==0,
            "Compatibility notice was not acknowledged; refusing Active test")
        assert(n.callerType=="app.LogoController.cPcSpecCheck" and n.callerRno==30,"PC check caller/phase changed")
        state.originalActive=before.active
        state.changed=true -- Preserve restoration even if the setter throws.
        state.report.originalActive=state.originalActive
        state.report.attempted=true;state.report.phase="invoking";persist()
        native("set_Active(System.Boolean)",true)
        state.report.afterImmediate=readback()
        state.report.ok=state.report.afterImmediate.active==true
        state.report.phase="awaiting_observation";state.afterPending=true
    end)
    if not ok then state.report.error=tostring(err);state.report.phase=state.report.attempted and "error" or "refused" end
    persist()
end)
