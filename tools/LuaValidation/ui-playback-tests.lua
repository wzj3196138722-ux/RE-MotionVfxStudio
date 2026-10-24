-- Run from the Lua materials directory. Uses REFramework mocks; no game calls.
local passed, failed = 0, 0
local function eq(a, b, name) assert(a == b, (name or "value") .. ": " .. tostring(a) .. " ~= " .. tostring(b)) end
local function test(name, fn)
    local ok, err = pcall(fn)
    if ok then passed = passed + 1; print("PASS " .. name)
    else failed = failed + 1; print("FAIL " .. name .. ": " .. tostring(err)) end
end
local function environment()
    local c = { objects = {}, hooks = {}, updates = {}, draws = {}, frames = {}, resets = {},
        buttons = {}, inputs = {}, checks = {}, shown_checks = {}, keys = {}, logs = {}, time = 10, dt = 0.016 }
    local env = setmetatable({}, { __index = _G }); env._G = env
    env.log = { info = function(v) c.logs[#c.logs+1] = v end, warn = function(v) c.logs[#c.logs+1] = v end }
    env.os = { clock = function() return c.time end }
    env.re = {
        on_pre_application_entry = function(_, fn) c.updates[#c.updates+1] = fn end,
        on_frame = function(fn) c.frames[#c.frames+1] = fn end,
        on_draw_ui = function(fn)
            if c.fail_registration then c.fail_registration = false; error("injected UI registration failure") end
            c.draws[#c.draws+1] = fn
        end,
        on_script_reset = function(fn) c.resets[#c.resets+1] = fn end,
    }
    env.Vector3f = { new = function(x,y,z) return { x=x,y=y,z=z } end }
    env.imgui = setmetatable({
        tree_node = function() return true end,
        button = function(label) local value=c.buttons[label]; c.buttons[label]=nil; return value==true end,
        input_text = function(label, value)
            if c.inputs[label] ~= nil then local new=c.inputs[label]; c.inputs[label]=nil; return true,new end
            return false,value
        end,
        checkbox = function(label, value)
            c.shown_checks[label] = value
            if c.checks[label] ~= nil then local new=c.checks[label]; c.checks[label]=nil; return true,new end
            return false,value
        end,
        slider_float = function(_, value) return false,value end,
    }, { __index = function() return function() end end })
    local function enum(values)
        local fields = {}
        for name,value in pairs(values) do
            fields[#fields+1] = { get_name=function()return name end, get_data=function()return value end, is_static=function()return true end }
        end
        return { get_fields=function()return fields end, get_field=function(_,name)
            for _,field in ipairs(fields) do if field:get_name()==name then return field end end
        end }
    end
    local actions = enum({ Start=17, Kill=23, Pause=31, Resume=37, Step=41, Finish=43 })
    local ids = enum({ Source=10, Target=20 })
    local keys = enum({ F9=120, F10=121 })
    local transform = { call = function(_, name)
        if name == "getJointByName" then return {} end
        return true
    end }
    local go = { call=function(_, name) if name=="get_Transform" then return transform end end }
    local character = { call=function(_, name)
        if name=="get_GameObject" then return go end
        if name=="get_Weapon" then return {call=function()return go end} end
    end }
    local master = { get_Character=function()return character end, call=function()return character end }
    local manager = { getMasterPlayer=function()return master end, call=function()return master end }
    local function new_object()
        local object = { destroyed=false, calls={} }
        local color = { channels={ r=0,g=0,b=0,a=0 } }
        function color:call(name, value)
            local setter = name:match("^set_([rgba])%(System.Byte%)$")
            if setter then self.channels[setter]=value; return end
            local getter = name:match("^get_([rgba])$")
            if getter then return self.channels[getter] end
            error("unmocked color method: " .. name)
        end
        object.player = { calls={}, call=function(self,name,value)
            if name=="getExternParametersCount" then return 0 end
            if name=="get_Color" then return color end
            if name=="set_PlaySpeed" then eq(math.type(value),"float","System.Single speed ABI") end
            if name=="get_PlaySpeed" then return self.calls.set_PlaySpeed or 1.0 end
            self.calls[name]=value; return true
        end }
        object.transform = { calls={}, call=function(self,name,value) self.calls[name]=value; return true end }
        function object:call(name)
            if name=="createComponent(System.Type)" then return self.player end
            if name=="get_Transform" then return self.transform end
            if name=="destroy" then self.destroyed=true; return end
            error("unmocked object method: " .. name)
        end
        c.objects[#c.objects+1]=object
        return object
    end
    env.sdk = {
        find_type_definition=function(name)
            if name=="via.effect.PlayerAction" then return actions end
            if name=="app.EffectID_Common.ID" then return ids end
            if name=="app.EffectID_Hit.ID" then return enum({HitSpecific=10}) end
            if name=="app.EffectID_Damage.ID" then return enum({DamageSpecific=10}) end
            if name=="via.hid.KeyboardKey" then return keys end
            return { name=name, get_method=function(_, signature) return {name=name, signature=signature} end }
        end,
        get_managed_singleton=function()return manager end,
        get_native_singleton=function()return {} end,
        call_native_func=function(_, _, name, value)
            if name=="isDown" then return c.keys[value]==true end
            if name=="get_DeltaTime" then if c.bad_delta then return nil end; return c.dt end
            if name=="get_CurrentScene" then return {get_address=function()return 101 end} end
            if name=="create(System.String)" then return new_object() end
            error("unmocked native method: " .. name)
        end,
        typeof=function(name)return name end,
        create_resource=function(_, path)
            if c.fail_resource then return nil end
            return {path=path,add_ref=function()end,release=function()end,
                create_holder=function()return {add_ref=function()end}end}
        end,
        hook=function(method, pre, post) c.hooks[#c.hooks+1]={method=method,pre=pre,post=post} end,
        to_ptr=function(value)return "ptr:"..tostring(value)end,
        to_int64=function(value)return value end,
        copy_to_clipboard=function()end,
    }
    env.require=function(name)
        if name=="fx_preview_runtime" then
            if not env.fx_preview_runtime_v2 then assert(loadfile(name..".lua","t",env))() end
            return env.fx_preview_runtime_v2
        end
        error("unexpected require: " .. name)
    end
    function c:load(name) assert(loadfile(name..".lua","t",env))() end
    function c:draw() for _,fn in ipairs(self.draws)do fn()end end
    function c:frame() for _,fn in ipairs(self.frames)do fn()end end
    function c:drain() for _,fn in ipairs(self.updates)do fn()end end
    function c:click(label) self.buttons[label]=true; self:draw() end
    function c:hook(domain)
        for _,h in ipairs(self.hooks)do if h.method.signature:find(domain,1,true)then return h end end
        error("hook not found: "..domain)
    end
    c.env=env
    return c
end

test("master first startup handles nonempty enum before count_pairs use",function()
    local c=environment(); c:load("fx_master"); assert(c.env.fx_master_state)
    local found=false; for _,line in ipairs(c.logs)do if line:find("ID表构建完成: 2",1,true)then found=true end end
    assert(found,"ID table was not built"); eq(#c.hooks,0,"monitor disabled by default")
end)
test("UI playback only mutates after LockScene and reports applied",function()
    local c=environment(); c:load("fx_master"); c:click("播放##fxm_go")
    eq(#c.objects,0); eq(#c.env.fx_preview_runtime_v2.queue,1)
    c:drain(); eq(#c.objects,1); eq(c.env.fx_preview_runtime_v2.clients.fx_master.last_result.ok,true)
    eq(c.objects[1].player.calls["set_Action(via.effect.PlayerAction)"],17)
end)
test("UI receives failed resource load instead of success",function()
    local c=environment(); c:load("fx_master"); c.fail_resource=true; c:click("播放##fxm_go"); c:drain()
    eq(c.env.fx_preview_runtime_v2.clients.fx_master.last_result.ok,false); eq(#c.objects,0)
end)
test("UI edit after enqueue does not change queued attachment offset",function()
    local c=environment(); c:load("fx_master")
    c.inputs["##fxm_ox"]="2"; c:click("播放##fxm_go")
    c.inputs["##fxm_ox"]="9"; c:draw(); c:drain()
    eq(c.objects[1].transform.calls["set_LocalPosition(via.vec3)"].x,2)
end)
test("F9 F10 are disabled initially then operate only for exclusive owner",function()
    local c=environment(); c:load("fx_play"); c:load("fx_master")
    c.keys[120]=true; c:frame(); eq(#c.env.fx_preview_runtime_v2.queue,0)
    c.keys[120]=false; c:frame()
    c.checks["启用 F9/F10（独占）##fx_play_keys"]=true; c:draw()
    c.keys[120]=true; c:frame(); eq(#c.env.fx_preview_runtime_v2.queue,1); c:drain()
    local runtime=c.env.fx_preview_runtime_v2; assert(runtime.clients.fx_play:get_instance("main"))
    eq(runtime.clients.fx_master:get_instance("main"),nil)
    c.keys[120]=false; c:frame()
    c.checks["启用 F9/F10（独占）##fx_master_keys"]=true; c:draw()
    c.keys[120]=true; c:frame(); eq(#runtime.queue,1); c:drain()
    eq(#c.objects,2); assert(runtime.clients.fx_master:get_instance("main"))
    c.keys[121]=true; c:frame(); eq(c.objects[2].destroyed,false); c:drain()
    eq(c.objects[1].destroyed,false); eq(c.objects[2].destroyed,true)
end)
local function enable_rule_ui(c)
    c:load("fx_master")
    c.checks["启用监控##fxm_monitor"]=true
    c.checks["启用已配置规则（仅 Common 枚举）##fxm_rules_on"]=true
    c.inputs["##fxm_ids"]="10"; c.inputs["##fxm_rep_target"]="20"; c:draw(); c:drain()
end
test("Common enum replacement writes pointer and preserves original return",function()
    local c=environment(); enable_rule_ui(c); c:click("加入替换ID(填下方目标)##fxm_add_rep")
    local h=c:hook("System.UInt32, app.EffectID_Common.ID")
    local args={[4]=10}; h.pre(args); eq(args[4],"ptr:20"); eq(h.post("original-return"),"original-return")
end)
test("same numeric Hit ID is never rewritten with a Common rule",function()
    local c=environment(); enable_rule_ui(c); c:click("加入替换ID(填下方目标)##fxm_add_rep")
    local h=c:hook("app.EffectID_Hit.ID"); local args={[3]=10}; h.pre(args); eq(args[3],10)
end)
test("path observation queues separate instance without suppressing original",function()
    local c=environment(); enable_rule_ui(c)
    c.inputs["##fxm_rep_path"]="test/extra.efx"; c:draw(); c:click("加入附加路径(填下方路径)##fxm_add_rp")
    local h=c:hook("System.UInt32, app.EffectID_Common.ID"); local args={[4]=10}; h.pre(args)
    eq(args[4],10); eq(#c.objects,0); eq(#c.env.fx_preview_runtime_v2.queue,1)
    c:drain(); assert(c.env.fx_preview_runtime_v2.clients.fx_master:get_instance("observed-10"))
    eq(c.env.fx_preview_runtime_v2.clients.fx_master:get_instance("main"),nil)
end)
test("removed zero-ID block does not mutate intercepted call",function()
    local c=environment(); enable_rule_ui(c); c:click("拦截原调用已停用##fxm_add_block")
    local h=c:hook("System.UInt32, app.EffectID_Common.ID"); local args={[4]=10}; h.pre(args); eq(args[4],10)
end)
test("failed UI registration permits retry and old callbacks stay inactive",function()
    local c=environment(); c.fail_registration=true
    local ok=pcall(function()c:load("fx_master")end); eq(ok,false); eq(c.env.fx_master_state,nil)
    c:load("fx_master"); c:click("播放##fxm_go"); eq(#c.env.fx_preview_runtime_v2.queue,1)
    c:drain(); eq(#c.objects,1)
end)
test("UI color uses actual byte channel methods and readback",function()
    local c=environment(); c:load("fx_master")
    c.checks["染色##fxm_col_en"]=true
    c.inputs["##fxm_cr"]="15"; c.inputs["##fxm_cg"]="28"; c.inputs["##fxm_cb"]="37"
    c:click("播放##fxm_go"); c:drain()
    eq(c.env.fx_preview_runtime_v2.clients.fx_master.last_result.ok,true)
    local color=c.objects[1].player.calls.set_Color
    eq(color.channels.r,15); eq(color.channels.g,28); eq(color.channels.b,37); eq(color.channels.a,255)
end)
test("malformed integer tokens reject the entire ID rule batch",function()
    for _,input in ipairs({"10,-20","10,20.0","","10,4294967296"})do
        local c=environment(); enable_rule_ui(c)
        c.inputs["##fxm_ids"]=input; c:click("加入替换ID(填下方目标)##fxm_add_rep")
        local h=c:hook("System.UInt32, app.EffectID_Common.ID"); local args={[4]=10}; h.pre(args); eq(args[4],10,input)
    end
end)
test("Hit event names come from Hit enum rather than Common numeric overlap",function()
    local c=environment(); enable_rule_ui(c)
    c:hook("app.EffectID_Hit.ID").pre({[3]=10})
    local found=false
    for _,message in ipairs(c.logs)do
        if message:find("EffectID_Hit",1,true) and message:find("HitSpecific",1,true) then found=true end
    end
    assert(found,"typed Hit event name missing")
end)
test("instance cap rejects extra observed IDs while keeping current preview",function()
    local c=environment(); enable_rule_ui(c)
    local runtime=c.env.fx_preview_runtime_v2; runtime.max_per_owner=1
    c:click("播放##fxm_go"); c:drain()
    c.inputs["##fxm_rep_path"]="extra.efx"; c:draw(); c:click("加入附加路径(填下方路径)##fxm_add_rp")
    c:hook("System.UInt32, app.EffectID_Common.ID").pre({[4]=10}); c:drain()
    eq(runtime.clients.fx_master.last_result.ok,false); eq(#c.objects,1)
    assert(runtime.clients.fx_master:get_instance("main")); eq(runtime.clients.fx_master:get_stats().instances,1)
end)
for _, spec in ipairs({ {"fx_play","fxplay"}, {"fx_play_bone","fxpb"}, {"fx_master","fxm"} }) do
    local script, label = spec[1], spec[2]
    test(script .. " repeats with game delta while CPU clock remains constant",function()
        local c=environment(); c:load(script); c:click("播放##"..label.."_go"); c:drain()
        eq(#c.objects,1); local clock_before=c.time
        c.checks["自动重播##"..label.."_rep"]=true; c:draw(); c.dt=1
        c:drain(); c:drain(); eq(#c.env.fx_preview_runtime_v2.queue,0)
        c:drain(); eq(#c.env.fx_preview_runtime_v2.queue,1); eq(#c.objects,1)
        c:drain(); eq(#c.objects,2); eq(c.time,clock_before)
    end)
    test(script .. " zero game delta pauses repeating even when CPU time advances",function()
        local c=environment(); c:load(script); c:click("播放##"..label.."_go"); c:drain()
        c.checks["自动重播##"..label.."_rep"]=true; c:draw(); c.dt=0
        for _=1,10 do c.time=c.time+10; c:drain(); c:frame() end
        eq(#c.objects,1); eq(#c.env.fx_preview_runtime_v2.queue,0)
    end)
    test(script .. " missing delta disables auto repeat without fallback CPU timer",function()
        local c=environment(); c:load(script); c:click("播放##"..label.."_go"); c:drain()
        c.checks["自动重播##"..label.."_rep"]=true; c:draw(); c.bad_delta=true; c:drain(); c:draw()
        eq(c.shown_checks["自动重播##"..label.."_rep"],false)
        c.bad_delta=false; c.dt=4; c.time=1000; c:drain(); c:drain()
        eq(#c.objects,1); eq(#c.env.fx_preview_runtime_v2.queue,0)
    end)
    test(script .. " repeat toggle clears partial elapsed game time immediately",function()
        local c=environment(); c:load(script); c:click("播放##"..label.."_go"); c:drain()
        c.checks["自动重播##"..label.."_rep"]=true; c:draw(); c.dt=1.5; c:drain()
        c.checks["自动重播##"..label.."_rep"]=false; c:draw()
        c.checks["自动重播##"..label.."_rep"]=true; c:draw()
        c:drain(); eq(#c.env.fx_preview_runtime_v2.queue,0)
        c:drain(); eq(#c.env.fx_preview_runtime_v2.queue,1)
    end)
end
print(("RESULT ui-playback %d passed, %d failed"):format(passed,failed))
if failed > 0 then error("UI playback regression failures") end
