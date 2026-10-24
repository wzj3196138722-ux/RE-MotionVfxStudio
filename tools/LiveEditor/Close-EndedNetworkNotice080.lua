-- Close only the already acknowledged startup network notice, on the game thread.
if _G.MotionVfxEndedNotice080 then return end
local result={startedAt=os.time(),attempted=false};_G.MotionVfxEndedNotice080=result
local pending=true
re.on_pre_application_entry("LockScene",function()
    if not pending then return end;pending=false
    local ok,err=pcall(function()
        local gui=assert(sdk.get_managed_singleton("app.GUIManager"):get_field("_AllGUI"):get_element(2))
        assert(gui:get_type_definition():get_full_name()=="app.GUI000003","Network notice GUI changed")
        assert(tonumber(gui:get_field("_BaseState"))==2,"Notice is no longer visible")
        local notify=assert(gui:get_field("_NotifyWindowApp"));local info=assert(notify:get_field("_CurInfo"))
        assert(tonumber(info:call("get_NotifyWindowId"))==-1 and info:call("get_IsEnd")==true and
            tonumber(info:call("get_SelectedIndex"))==0,"Notice has not been acknowledged")
        assert(info:call("get_Caller"):get_type_definition():get_full_name()=="app.NetworkErrorManager","Notice caller changed")
        assert(gui:call("get_IsHasCloseReq")==false and gui:call("get_IsFadeInOut")==false,"Notice is already closing")
        result.attempted=true
        gui:call("requestClose(System.Boolean)",false)
        result.closeRequested=gui:call("get_IsHasCloseReq")
    end)
    result.ok=ok;result.error=not ok and tostring(err) or nil
    json.dump_file("MotionVfxLive/menu-ended-close080.json",result)
end)
