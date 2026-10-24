-- Native ImGui hit testing with the actual workbench (no fake button clicks).
local project=arg[1]
local legacy=arg[2]=="legacy"
arg={project.."/game-plugin/MotionVfxLive.lua",nil,nil,"export-environment"}
local environment=assert(loadfile(project.."/tools/LuaValidation/live-ui-tests.lua"))()
local passed,failed=0,0
local cases={
    {name="default",w=1920,h=1080,font=16,pad={2,0},min={32,32},border=2,alpha=1},
    {name="large padding",w=1920,h=1080,font=32,pad={64,64},min={32,32},border=2,alpha=1},
    {name="large minimum",w=1920,h=1080,font=48,pad={8,8},min={320,240},border=8,alpha=1},
    {name="4K scaled",w=3840,h=2160,font=64,pad={24,24},min={128,128},border=4,alpha=1},
    {name="720p",w=1280,h=720,font=32,pad={16,16},min={64,64},border=4,alpha=1},
    {name="inherited transparent",w=1920,h=1080,font=16,pad={2,0},min={32,32},border=2,alpha=0},
}
for _,config in ipairs(cases) do
    for _,side in ipairs({"left","right","top","bottom"}) do
      for _,scale in ipairs(legacy and {1} or {.75,1,1.3}) do
        native_imgui.reset({config.w,config.h},config.font,config.pad,config.min,config.border,config.alpha)
        local c=environment();local env=c.env
        env.os.clock=function()return 0 end
        for k,v in pairs(native_imgui) do env.imgui[k]=v end
        env.Vector2f={new=function(x,y)return {x=x,y=y}end}
        c.show_ui=true;c.module.state.window_open=true
        local commands={}
        local widgets={colors={},new=function()
            return {begin=function()end,rect=function()end,init=function()end,
                at=function(_,x,y)env.imgui.set_cursor_screen_pos({x,y})end,
                emit=function(_,kind,data)commands[#commands+1]={kind=kind,data=data}end}
        end}
        env.d2d={register=function()end}
        local bench=assert(loadfile(project.."/game-plugin/MotionVfxLive/fx_d2d_workbench.lua","t",env))().new(c.module,widgets)
        bench.orb_side=side;bench.scale=scale;bench:hide()
        local function frame(x,y,down)
            native_imgui.begin_frame(x,y,down);bench:frame();native_imgui.end_frame()
            local a,px,py,mx,my,b=native_imgui.style()
            assert(a==config.alpha and px==config.pad[1] and py==config.pad[2] and mx==config.min[1] and my==config.min[2] and b==config.border,"host style leaked")
        end
        local function verify(ok,label)
            print((ok and "PASS " or "FAIL ")..config.name.." / "..side.." / "..scale.." / "..label)
            if ok then passed=passed+1 else failed=failed+1 end
        end
        local function center()
            local logo=commands[#commands].data
            return logo.x+logo.w/2,logo.y+logo.h/2
        end
        for cycle=1,legacy and 1 or 3 do
            bench:hide();frame(-100,-100,false);frame(-100,-100,false)
            local x,y=center()
            frame(x,y,false);frame(x,y,true);frame(x,y,false)
            verify(not c.module.state.window_hidden,"hide/reopen "..cycle)
        end
        if not legacy then
            bench:hide();frame(-100,-100,false);frame(-100,-100,false)
            frame(config.w/2,config.h/2,false);frame(config.w/2,config.h/2,true);frame(config.w/2,config.h/2,false)
            verify(c.module.state.window_hidden,"outside click ignored")
            local x,y=center()
            frame(x,y,false);frame(x,y,true)
            frame(config.w*.25,config.h*.25,true);frame(config.w*.25,config.h*.25,true);frame(config.w*.25,config.h*.25,false)
            verify(c.module.state.window_hidden and c.files["MotionVfxLive/workbench-settings.json"]~=nil,"drag release stays hidden and persists")
            frame(-100,-100,false);x,y=center()
            frame(x,y,false);frame(x,y,true);frame(x,y,false)
            verify(not c.module.state.window_hidden,"dragged orb reopens")
        end
      end
    end
end
print(string.format("Native orb results: %d passed, %d failed",passed,failed))
if legacy then assert(failed>0,"Expected to reproduce a legacy click failure") else assert(failed==0,"Native logo click failed") end
