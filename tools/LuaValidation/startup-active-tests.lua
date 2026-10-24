local checks=0
local function check(value,message) assert(value,message);checks=checks+1 end
local function scenario(overrides)
    local spec={active=false,focus=true,id=565,ended=true,selected=0,rno=30,caller="app.LogoController.cPcSpecCheck"}
    for k,v in pairs(overrides or {})do spec[k]=v end
    local calls,callbacks,latest={}, {}, nil
    local function typed(name,fields,methods)
        return {get_type_definition=function()return{get_full_name=function()return name end}end,
            get_field=function(_,n)return fields[n]end,call=function(_,n,...)return methods[n](...)end}
    end
    local caller=typed(spec.caller,{Rno=spec.rno},{})
    local info=typed("Info",{}, {get_NotifyWindowId=function()return spec.id end,get_IsEnd=function()return spec.ended end,
        get_SelectedIndex=function()return spec.selected end,get_Caller=function()return caller end})
    local module=typed("Module",{_State=1,_CurInfo=info},{})
    local gui=typed("app.GUI000002",{_NotifyWindowApp=module},{})
    local all={get_size=function()return 3 end,get_element=function(_,i)assert(i==1);return gui end}
    local manager=typed("app.GUIManager",{_AllGUI=all},{})
    local application={}
    local td={get_method=function(_,name)return name end}
    local env=setmetatable({},{__index=_G});env._G=env
    env.sdk={get_managed_singleton=function()return manager end,get_native_singleton=function()return application end,
        find_type_definition=function()return td end,call_native_func=function(app,typeDef,method,value)
            assert(app==application and typeDef==td)
            if method=="get_Active()" then return spec.active end
            if method=="get_HasFocus()" then return spec.focus end
            assert(method=="set_Active(System.Boolean)");calls[#calls+1]=value
            if spec.throw then error("setter failed") end
            spec.active=value
        end}
    env.re={on_pre_application_entry=function(name,fn)assert(name=="LockScene");callbacks[#callbacks+1]=fn end}
    env.json={dump_file=function(path,value)assert(path=="MotionVfxLive/menu-active080.json");latest=value end}
    local function load()assert(loadfile("tools/LiveEditor/Test-StartupActive080.lua","t",env))()end
    load();check(#calls==0,"no mutation before main-thread callback")
    for _=1,4 do for _,fn in ipairs(callbacks)do fn()end end
    load();check(#callbacks==1,"duplicate load must not rearm")
    return {calls=calls,result=latest,state=env.MotionVfxStartupActive080,tick=callbacks[1],spec=spec}
end
local s=scenario()
check(#s.calls==1 and s.calls[1]==true and s.result.ok,"one Active setter invocation")
check(s.result.originalActive==false and s.result.after.active==true,"baseline and after retained")
check(s.state.restore() and #s.calls==1,"restore only queues main-thread operation")
s.tick();check(#s.calls==2 and s.calls[2]==false and s.result.restoreOk,"restore baseline and verify")
s.tick();check(#s.calls==2 and not s.state.restore(),"restore is idempotent")
for _,v in ipairs({{active=true},{focus=false},{id=99},{ended=false},{selected=-1},{rno=11},{caller="OtherMenu"}})do
    s=scenario(v);check(#s.calls==0 and s.result.phase=="refused","changed prerequisite refuses without mutation")
end
s=scenario({throw=true});check(#s.calls==1 and s.result.phase=="error" and s.state.changed,"setter error retains restore record and never retries")
print("Startup Active test: "..checks.." checks passed")
