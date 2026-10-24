-- Actual production workbench and native Dear ImGui input. One outer pcall
-- models REFramework's callback loop; MDF failures must not starve later UI.
-- A local MDF source argument is read for its final on_frame only. No game,
-- native OS input, third-party source redistribution, or installation writes.
local project=assert(arg[1],"project path required")
local source_path=arg[2]
local function read(path)
    local f=assert(io.open(path,"rb"));local s=f:read("*a");f:close();return s
end
local guard=read(project.."/tools/MdfxlCompatibility/MDF-XL-frame-guard.lua")
local mdf_frame
if source_path then
    local source=read(source_path)
    local first=assert(source:find("--MARK:On Frame",1,true))
    local last=assert(source:find("--MARK:On Draw UI",first,true))
    mdf_frame=source:sub(first,last-1)
else
    mdf_frame=[[re.on_frame(function()
if reframework.get_game_name()=="mhwilds" then
update_MDFXLViaHotkeys_MHWS()
manage_MasterMaterialData_MHWS(MDFXL,MDFXLSub,MDFXLSaveDataChunks)
isUpdaterBypass=false;changed=false;wc=false
for _,entry in pairs(MDFXL) do entry.isUpdated=false end
end
end)]]
    print("NOTE local MDF path omitted; using portable material-update failure fixture")
end
arg={project.."/game-plugin/MotionVfxLive.lua",nil,nil,"export-environment"}
local environment=assert(loadfile(project.."/tools/LuaValidation/live-ui-tests.lua"))()
local N=native_imgui
local checks=0
local function check(value,label)
    assert(value,label);checks=checks+1;print("PASS "..label)
end
local cases={
    {name="1080p",w=1920,h=1080,font=16,pad={2,0},min={32,32},border=2,alpha=1,scale=1},
    {name="720p",w=1280,h=720,font=32,pad={16,16},min={64,64},border=4,alpha=1,scale=.75},
    {name="4K",w=3840,h=2160,font=64,pad={24,24},min={128,128},border=4,alpha=1,scale=1.3},
    {name="inherited styles",w=1920,h=1080,font=32,pad={64,64},min={320,240},border=8,alpha=0,scale=1},
}
for _,config in ipairs(cases) do
 for _,side in ipairs({"left","right","top","bottom"}) do
  for _,protected in ipairs({false,true}) do
    local label=config.name.." / "..side.." / "..(protected and "protected" or "baseline")
    N.reset({config.w,config.h},config.font,config.pad,config.min,config.border,config.alpha)
    local c=environment();local env=c.env
    local elapsed=0;env.os.clock=function()return elapsed end
    for key,value in pairs(N) do env.imgui[key]=value end
    env.Vector2f={new=function(x,y)return {x=x,y=y}end}
    local drawings={}
    env.d2d={register=function(_,draw)drawings[#drawings+1]=draw end,
        surface_size=function()return config.w,config.h end}
    local widgets=assert(loadfile(project.."/game-plugin/MotionVfxLive/fx_d2d_widgets.lua","t",env))()
    local bench=assert(loadfile(project.."/game-plugin/MotionVfxLive/fx_d2d_workbench.lua","t",env))().new(c.module,widgets)
    c.module.workbench=bench;c.show_ui=true;c.module.state.window_open=true
    bench.orb_side=side;bench.scale=config.scale;bench:hide()
    -- Only these callbacks are dispatched in this fixture. The actual core
    -- constructor has already run; its mocked backend is not under test here.
    c.callbacks.frames={}
    env.reframework.get_game_name=function()return "mhwilds" end
    env.MDFXL={{isUpdated=true}};env.MDFXLSub={};env.MDFXLSaveDataChunks={}
    local stale=false;local mdf_calls,ui_calls,d2d_calls,failed_frames,frames=0,0,0,0,0
    local component={get_GameObject=function()
        if stale then error("via.Component.get_GameObject: System.InvalidOperationException") end
        return {}
    end}
    env.update_MDFXLViaHotkeys_MHWS=function()end
    env.manage_MasterMaterialData_MHWS=function()mdf_calls=mdf_calls+1;component:get_GameObject()end
    assert(load((protected and guard.."\n" or "")..mdf_frame,"@MDF-XL-native-fixture.lua","t",env))()
    env.re.on_frame(function()ui_calls=ui_calls+1;bench:frame()end)
    local function frame(x,y,down)
        elapsed=elapsed+1/60;frames=frames+1;c.time=100+math.floor(elapsed)
        N.begin_frame(x or -100,y or -100,down==true)
        local ok,err=pcall(function()for _,callback in ipairs(c.callbacks.frames) do callback() end end)
        if not ok then
            assert(not protected and tostring(err):find("get_GameObject",1,true),"unexpected fixture failure: "..tostring(err))
            failed_frames=failed_frames+1
        end
        -- D2D dispatch is independent and can continue while ordinary callbacks
        -- are starved, exactly the distinction in the affected-user reports.
        for _,draw in ipairs(drawings) do draw();d2d_calls=d2d_calls+1 end
        N.end_frame()
        local alpha,px,py,mx,my,b=N.style()
        assert(alpha==config.alpha and px==config.pad[1] and py==config.pad[2] and
            mx==config.min[1] and my==config.min[2] and b==config.border,"native host styles leaked")
        assert(ui_calls+failed_frames==frames and mdf_calls==frames,"a callback was duplicated or a healthy UI frame skipped")
    end
    local function idle()frame();frame()end
    local function center()
        local orb=assert(bench.orb_status,"orb has not been laid out")
        return orb.x+orb.size/2,orb.y+orb.size/2
    end
    local function click()
        local x,y=center();frame(x,y,false);frame(x,y,true);frame(x,y,false)
    end
    idle()
    check(bench.orb_status.interactive==true,label.." / production orb hit target starts active")
    stale=true
    local old_ui,old_draws=ui_calls,d2d_calls
    click()
    if not protected then
        check(c.module.state.window_hidden and ui_calls==old_ui and d2d_calls>old_draws and failed_frames==3,
            label.." / exact MDF error reproduces visible but unclickable orb")
        stale=false;idle();click()
        check(not c.module.state.window_hidden and ui_calls==old_ui+5,
            label.." / removing the injected MDF fault restores ordinary clicking")
    else
        check(not c.module.state.window_hidden and ui_calls==old_ui+3 and failed_frames==0,
            label.." / native click opens despite MDF failing on every frame")
        bench:hide();idle()
        frame(config.w/2,config.h/2,false);frame(config.w/2,config.h/2,true);frame(config.w/2,config.h/2,false)
        check(c.module.state.window_hidden,label.." / outside click remains ignored")
        local x,y=center();frame(x,y,false);frame(x,y,true)
        frame(config.w*.25,config.h*.25,true);frame(config.w*.25,config.h*.25,true);frame(config.w*.25,config.h*.25,false)
        check(c.module.state.window_hidden and c.files["MotionVfxLive/workbench-settings.json"]~=nil,
            label.." / dragging stays hidden and saves the dock position")
        idle();click()
        check(not c.module.state.window_hidden,label.." / dragged orb opens while MDF still fails")
        local s=assert(env.MotionVfxMdfxlCompatibility);local previous_errors=s.errorCount
        stale=false;bench:hide();idle();click()
        check(not c.module.state.window_hidden and s.errorCount==previous_errors and s.consecutiveErrors==0,
            label.." / MDF recovery keeps editor clicks and resets consecutive errors")
        stale=true;bench:hide();idle();click()
        check(not c.module.state.window_hidden and s.errorCount>previous_errors and s.callbackFrames==frames,
            label.." / a later MDF fault stays isolated without duplicate editor updates")
        check(#c.calls==0 and #c.scene_writes==0 and #c.global_writes==0 and #c.effect_update_writes==0,
            label.." / compatibility test does not change resources or game time")
    end
  end
 end
end
print("MDF-XL native orb checks: "..checks.." passed; real ImGui input, no game or OS input.")
