local checks=0
local function check(v,message)assert(v,message);checks=checks+1 end
local function run(changes)
    local spec={code="C1082-AAA-AAA2",caller="app.NetworkErrorManager",count=1,guiId=2,id=-1,errorType=26,ended=false,selected=-1}
    for k,v in pairs(changes or {})do spec[k]=v end
    local calls,callbacks,latest=0,{},nil
    local function object(name,fields,methods)
        return {get_type_definition=function()return{get_full_name=function()return name end,get_method=function()return true end}end,
            get_field=function(_,key)return fields[key]end,call=function(_,key,...)assert(methods[key],key);return methods[key](...)end}
    end
    local param=object("app.NetworkErrorParamsString",{["<Value>k__BackingField"]=spec.code},{})
    local params=object("List",{},{get_Count=function()return 1 end,["get_Item(System.Int32)"]=function()return param end})
    local request=object("Error",{Params=params},{get_Type=function()return spec.errorType end,get_Close=function()return false end})
    local caller=object(spec.caller,{},{get_DisplayError=function()return request end})
    local choices=object("List",{},{get_Count=function()return spec.count end})
    local info=object("Info",{}, {get_NotifyWindowId=function()return spec.id end,get_IsEnd=function()return spec.ended end,
        get_SelectedIndex=function()return spec.selected end,get_DefaultIndex=function()return 0 end,
        get_ChoisesTextInfo=function()return choices end,get_Caller=function()return caller end})
    local moduleFields={_State=1,_CurInfo=info}
    local module=object("app.cGUISystemModuleNotifyWindowApp",moduleFields,{
        get_NotifyWindowGUIID=function()return spec.guiId end,["endNotifyWindow(System.Int32)"]=function(index)
            assert(index==0);calls=calls+1;if spec.invokeError then error("simulated error")end
            spec.ended=true;spec.selected=0
        end})
    local gui=object("app.GUI000003",{_BaseState=2},{})
    local manager=object("app.GUIManager",{_AllGUI={get_element=function(_,i)assert(i==2);return gui end}},
        {getNotifyWindowModule=function()return module end})
    local env=setmetatable({},{__index=_G});env._G=env
    env.sdk={get_managed_singleton=function()return manager end}
    env.re={on_pre_application_entry=function(name,callback)assert(name=="LockScene");callbacks[#callbacks+1]=callback end}
    env.json={dump_file=function(path,result)assert(path=="MotionVfxLive/menu-network080.json");latest=result end}
    local function load()assert(loadfile("tools/LiveEditor/Confirm-NetworkNotice080.lua","t",env))()end
    load();check(calls==0,"must wait for main-thread callback")
    for _=1,4 do for _,callback in ipairs(callbacks)do callback()end end
    load();check(#callbacks==1,"duplicate load must not rearm")
    return calls,latest
end
local calls,result=run()
check(calls==1 and result.ok and result.after.isEnd==true and result.after.selectedIndex==0,"confirmed observed error once")
for _,case in ipairs({{code="OTHER"},{caller="OtherCaller"},{count=2},{guiId=1},{id=565},{errorType=0},{ended=true},{selected=0}})do
    calls,result=run(case);check(calls==0 and result.phase=="refused","unexpected dialog must refuse")
end
calls,result=run({invokeError=true});check(calls==1 and result.phase=="error","invocation failure must not retry")
print("Network notice confirmation: "..checks.." checks passed")
