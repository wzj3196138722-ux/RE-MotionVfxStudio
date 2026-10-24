-- Temporary validation: close only the observed single-button startup error.
-- No network/account configuration changes, retries, or arbitrary dialog input.
local key="MotionVfxNetworkNotice080Attempt"
if rawget(_G,key) then return end
local result={phase="pending",startedAt=os.time(),attempted=false,ok=false}
rawset(_G,key,result)
local pending,afterPending=true,false
local notify
local function persist()
    local ok,err=pcall(json.dump_file,"MotionVfxLive/menu-network080.json",result)
    if not ok and log and log.error then log.error(tostring(err)) end
end
local function class(object) return object and object:get_type_definition():get_full_name() end
local function snapshot()
    local out={state=tonumber(notify:get_field("_State")),guiId=tonumber(notify:call("get_NotifyWindowGUIID"))}
    local info=notify:get_field("_CurInfo");out.hasCurrent=info~=nil
    if not info then return out end
    out.currentId=tonumber(info:call("get_NotifyWindowId"))
    out.isEnd=info:call("get_IsEnd");out.selectedIndex=tonumber(info:call("get_SelectedIndex"))
    out.defaultIndex=tonumber(info:call("get_DefaultIndex"))
    local choices=info:call("get_ChoisesTextInfo");out.choiceCount=choices and tonumber(choices:call("get_Count"))
    local caller=info:call("get_Caller");out.callerType=class(caller)
    if out.callerType=="app.NetworkErrorManager" then
        local request=caller:call("get_DisplayError")
        if request then
            out.errorType=tonumber(request:call("get_Type"));out.errorClosed=request:call("get_Close")
            local params=request:get_field("Params")
            if params then
                local count=tonumber(params:call("get_Count"));assert(count and count>=0 and count<=16,"Unexpected error-parameter count")
                out.errorStrings={}
                for i=0,count-1 do
                    local p=params:call("get_Item(System.Int32)",i)
                    if class(p)=="app.NetworkErrorParamsString" then
                        out.errorStrings[#out.errorStrings+1]=p:get_field("<Value>k__BackingField")
                    end
                end
            end
        end
    end
    return out
end
local function safeSnapshot()
    local ok,value=pcall(snapshot)
    return ok and value or {error=tostring(value)}
end
persist()
re.on_pre_application_entry("LockScene",function()
    if afterPending then
        afterPending=false;result.after=safeSnapshot();result.phase="completed";persist();return
    end
    if not pending then return end;pending=false
    local ok,err=pcall(function()
        local manager=assert(sdk.get_managed_singleton("app.GUIManager"),"GUI manager unavailable")
        notify=assert(manager:call("getNotifyWindowModule"),"Notify module unavailable")
        assert(class(notify)=="app.cGUISystemModuleNotifyWindowApp","Unexpected notify module")
        result.before=snapshot();local b=result.before
        assert(b.state==1 and b.guiId==2 and b.currentId==-1,"Current notice is not the observed network dialog")
        local all=assert(manager:get_field("_AllGUI"),"GUI array unavailable")
        local gui=all:get_element(2)
        assert(class(gui)=="app.GUI000003" and tonumber(gui:get_field("_BaseState"))==2,"Network notice GUI is not visible")
        assert(b.callerType=="app.NetworkErrorManager" and b.errorType==26 and b.errorClosed==false,"Network error source changed")
        assert(b.errorStrings and #b.errorStrings==1 and b.errorStrings[1]=="C1082-AAA-AAA2","Network error code changed")
        assert(b.choiceCount==1 and b.defaultIndex==0 and b.selectedIndex==-1 and b.isEnd==false,"Notice is no longer an unselected single close button")
        assert(notify:get_type_definition():get_method("endNotifyWindow(System.Int32)"),"Notification end signature unavailable")
        result.attempted=true;result.phase="invoking";persist()
        notify:call("endNotifyWindow(System.Int32)",0)
        result.ok=true;result.afterImmediate=safeSnapshot();result.phase="awaiting_after_snapshot";afterPending=true
    end)
    if not ok then result.error=tostring(err);result.phase=result.attempted and "error" or "refused" end
    persist()
end)
