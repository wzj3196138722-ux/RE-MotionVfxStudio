-- Exercise UI, retry and reset callbacks with no player loaded and no user input.
local scripts={"fx_play.lua","fx_play_bone.lua","fx_master.lua","fx_hook.lua","fx_block.lua","fx_hide.lua","fx_spawn.lua","fx_bone_debug.lua","fx_dump_ids.lua"}
local passed,failed=0,0
local function env_new(enums)
    local callbacks,loaded={},{}
    local env=setmetatable({}, {__index=_G}); env._G=env
    env.log={info=function()end,warn=function()end,error=function()end,debug=function()end}
    env.io={open=function() error("unexpected filesystem write during idle startup") end}
    env.json={dump_file=function() error("unexpected JSON write during idle startup") end}
    env.fs={create_directory=function() error("unexpected directory write during idle startup") end}
    env.re=setmetatable({}, {__index=function(_,name)
        return function(a,b) callbacks[#callbacks+1]={name=name,fn=b or a} end
    end})
    env.imgui=setmetatable({}, {__index=function(_,name)
        if name=="tree_node" or name=="begin_window" then return function()return true end end
        if name=="checkbox" or name=="input_text" or name=="slider_float" or name=="slider_int" or name=="combo" or name=="drag_float" then
            return function(_,value)return false,value end
        end
        if name=="button" or name=="is_key_down" or name=="is_key_pressed" or name=="selectable" then return function()return false end end
        return function()end
    end})
    env.reframework={is_drawing_ui=function()return false end}
    env.thread={get_id=function()return 1 end,get_hook_storage=function()return {} end}
    env.Vector3f={new=function(x,y,z)return {x=x,y=y,z=z} end}
    env.sdk={
        find_type_definition=function(name)
            if not enums or not (name:find("EffectID_",1,true) or name=="via.effect.PlayerAction") then return nil end
            local values=name=="via.effect.PlayerAction" and {Start=17,Kill=23,Pause=31,Resume=37,Step=41,Finish=43} or {TestEffect=12345}
            local fields={}
            for key,value in pairs(values) do fields[#fields+1]={get_name=function()return key end,get_data=function()return value end,is_static=function()return true end} end
            return {get_fields=function()return fields end,get_field=function(_,key)
                for _,f in ipairs(fields)do if f:get_name()==key then return f end end
            end}
        end,
        get_native_singleton=function()return nil end,get_managed_singleton=function()return nil end,
        hook=function()error("unexpected hook without a method")end,
        call_native_func=function()return 0.016 end,PreHookResult={CALL_ORIGINAL=0},
        create_resource=function()error("unexpected playback during idle startup")end,
    }
    env.require=function(name)
        if loaded[name]then return loaded[name]end
        assert(name=="fx_preview_runtime","unexpected require "..name)
        loaded[name]=assert(loadfile(name..".lua","t",env))(); return loaded[name]
    end
    return env,callbacks
end
local function run(name,files,enums)
    local ok,err=pcall(function()
        local env,callbacks=env_new(enums)
        for _,file in ipairs(files)do assert(loadfile(file,"t",env))() end
        local count=#callbacks
        for _,file in ipairs(files)do assert(loadfile(file,"t",env))() end
        assert(#callbacks==count,"duplicate load registered extra callbacks")
        for round=1,3 do
            for _,entry in ipairs(callbacks)do if entry.name~="on_script_reset" then entry.fn() end end
        end
        for _,entry in ipairs(callbacks)do if entry.name=="on_script_reset" then entry.fn() end end
    end)
    if ok then passed=passed+1;print("PASS "..name)else failed=failed+1;print("FAIL "..name..": "..tostring(err))end
end
for _,file in ipairs(scripts)do
    run(file.." no enums/player",{file},false)
    run(file.." enum UI / no player",{file},true)
end
run("all scripts together, duplicate loads and reset",scripts,true)
print(("RESULT startup %d passed, %d failed"):format(passed,failed))
if failed>0 then error("startup regression failures")end
