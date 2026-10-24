local script = "tools/LiveEditor/Confirm-StartupNotice080.lua"
local checks = 0
local function check(value, message) assert(value, message); checks = checks + 1 end

local function run(overrides)
    local spec = { id=565, caller="app.LogoController.cPcSpecCheck", rno=11,
        count=1, default=0, selected=-1, ended=false, state=1, visible=2 }
    for k,v in pairs(overrides or {}) do spec[k]=v end
    local calls, callbacks, latest = 0, {}, nil
    local function typed(name, fields, methods)
        local obj = {}
        function obj:get_type_definition()
            return { get_full_name=function() return name end,
                get_method=function(_, signature) return not spec.noMethod and signature=="endNotifyWindow(System.Int32)" end }
        end
        function obj:get_field(key) return fields and fields[key] end
        function obj:call(key, ...)
            assert(methods and methods[key], "Unexpected call: "..key)
            return methods[key](...)
        end
        return obj
    end
    local caller = typed(spec.caller, {Rno=spec.rno})
    local choices = typed("List", nil, {get_Count=function() return spec.count end})
    local info = typed("app.cGUINotifyWindowInfoApp", nil, {
        get_NotifyWindowId=function() return spec.id end,
        get_IsEnd=function() return spec.ended end,
        get_SelectedIndex=function() return spec.selected end,
        get_DefaultIndex=function() return spec.default end,
        get_Caller=function() return caller end,
        get_ChoisesTextInfo=function() return choices end,
    })
    local moduleFields={_State=spec.state, _CurInfo=info}
    local module = typed("app.cGUISystemModuleNotifyWindowApp", moduleFields, {
        ["endNotifyWindow(System.Int32)"]=function(index)
            calls=calls+1; assert(index==0)
            if spec.invokeError then error("simulated close error") end
            moduleFields._State=0; moduleFields._CurInfo=nil
        end
    })
    local gui = typed("app.GUI000002", {_BaseState=spec.visible,_NotifyWindowApp=module})
    local all = {get_size=function() return 3 end,get_element=function(_,i) assert(i==1); return gui end}
    local manager = typed("app.GUIManager", {_AllGUI=all})
    local env = setmetatable({}, {__index=_G}); env._G=env
    env.sdk={get_managed_singleton=function(name) assert(name=="app.GUIManager"); return manager end}
    env.re={on_pre_application_entry=function(name,callback) assert(name=="LockScene"); callbacks[#callbacks+1]=callback end}
    env.json={dump_file=function(path, value) assert(path=="MotionVfxLive/menu-confirm080.json"); latest=value end}
    local function load() assert(loadfile(script,"t",env))() end
    load()
    check(calls==0,"must not mutate before scene callback")
    for _=1,4 do for _,callback in ipairs(callbacks) do callback() end end
    local previousCallbacks=#callbacks
    load()
    check(#callbacks==previousCallbacks,"repeat load must not arm another confirmation")
    for _,callback in ipairs(callbacks) do callback() end
    return calls,latest
end

local calls,result=run()
check(calls==1 and result.ok and result.phase=="completed","valid notice confirms exactly once")
check(result.before.currentId==565 and result.before.choiceCount==1,"records matched before state")
check(result.after.state==0 and result.after.hasCurrent==false,"records next-frame after state")
for _,case in ipairs({{id=99},{caller="app.OtherMenu"},{rno=12},{count=2},{default=1},
    {selected=0},{ended=true},{state=0},{visible=0},{noMethod=true}}) do
    calls,result=run(case)
    check(calls==0 and not result.ok and result.phase=="refused" and result.error,"unexpected menu must refuse without retry")
end
calls,result=run({invokeError=true})
check(calls==1 and result.phase=="error" and not result.ok,"failed invocation must never retry")
print("Startup notice confirmation: "..checks.." checks passed")
