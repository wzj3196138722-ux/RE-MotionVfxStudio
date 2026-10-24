-- Actual workbench and existing editor controllers, with deterministic layout/input.
-- Does not connect to a game, record a screen, open dialogs, or modify game files.
local project=arg[1]
local output=arg[2]
local args=arg
arg={project.."/game-plugin/MotionVfxLive.lua",nil,nil,"export-environment"}
local environment=assert(loadfile(project.."/tools/LuaValidation/live-ui-tests.lua"))()
arg=args
local passed=0
local function check(v,message) assert(v,message);passed=passed+1;print("PASS "..message) end
local function json_encode(value)
    if type(value)=="nil" then return "null" end
    if type(value)=="string" then return '"'..value:gsub('\\','\\\\'):gsub('"','\\"'):gsub('\n','\\n'):gsub('\r','\\r'):gsub('\t','\\t')..'"' end
    if type(value)~="table" then return tostring(value) end
    local array=#value>0
    local values={}
    if array then for _,v in ipairs(value) do values[#values+1]=json_encode(v) end
    else for k,v in pairs(value) do values[#values+1]=json_encode(k)..":"..json_encode(v) end end
    return (array and "[" or "{")..table.concat(values,",")..(array and "]" or "}")
end
local function setup(inherited_alpha)
    local c=environment()
    local env=c.env
    local api=env.imgui
    local stack={}
    local context={x=0,y=0,w=1440,h=900,cx=0,cy=0,indent=0}
    local next_size,next_pos,item_width,last_id,active
    local clicked,inputs={},{}
    inherited_alpha=inherited_alpha or 1
    local alpha,alpha_stack=inherited_alpha,{}
    local push_var,pop_var=api.push_style_var,api.pop_style_var
    api.push_style_var=function(id,value)
        push_var(id,value);alpha_stack[#alpha_stack+1]=alpha
        if id==0 then alpha=value end
    end
    api.pop_style_var=function(n)
        pop_var(n)
        for _=1,n or 1 do alpha=assert(table.remove(alpha_stack)) end
    end
    c.native_input_alpha={}
    c.input_ids={}
    c.item_ids={}
    c.host_alpha={}
    local function item(id,w,h)
        c.item_ids[id]=true
        last_id=id;active=c.active==id
        context.last={x=context.cx,y=context.cy,w=w,h=h}
        context.cx=context.x+context.indent
        context.cy=context.last.y+h+4
        local result=clicked[id];clicked[id]=nil;return result==true
    end
    api.set_next_window_pos=function(v) next_pos=v end
    api.set_next_window_size=function(v) next_size=v end
    api.begin_window=function(id)
        c.host_alpha[id]=alpha
        context={x=next_pos[1],y=next_pos[2],w=next_size[1],h=next_size[2],cx=next_pos[1],cy=next_pos[2],indent=0};return true
    end
    api.end_window=function() assert(#stack==0,"child stack balanced") end
    api.begin_child_window=function(id,size)
        stack[#stack+1]=context
        local scroll=(c.scroll or {})[id] or 0
        context={x=context.cx,y=context.cy,w=size[1],h=size[2],cx=context.cx,cy=context.cy-scroll,indent=0}
        return true
    end
    api.end_child_window=function()
        local child=context;context=assert(table.remove(stack))
        item("child",child.w,child.h)
    end
    api.get_window_pos=function() return {x=context.x,y=context.y} end
    api.get_window_size=function() return {x=context.w,y=context.h} end
    api.get_display_size=function() return {x=c.screen_w or 1440,y=c.screen_h or 900} end
    api.get_cursor_screen_pos=function() return {x=context.cx,y=context.cy} end
    api.get_cursor_pos=function() return {x=context.cx-context.x,y=context.cy-context.y} end
    api.set_cursor_screen_pos=function(v) context.cx,context.cy=v[1],v[2] end
    api.invisible_button=function(id,size) return item(id,size[1],size[2]) end
    api.same_line=function()
        if context.last then context.cx=context.last.x+context.last.w+8;context.cy=context.last.y end
    end
    api.indent=function(v) context.indent=context.indent+v;context.cx=context.cx+v end
    api.unindent=function(v) context.indent=context.indent-v;context.cx=context.cx-v end
    api.set_next_item_width=function(v) item_width=v end
    api.input_text=function(id,value)
        c.native_input_alpha[id]=alpha
        c.input_ids[id]=true
        item(id,item_width or 200,28)
        local text=inputs[id];inputs[id]=nil
        local range=c.selection or {#(text or value),#(text or value)}
        return text~=nil,text or value,range[1],range[2]
    end
    api.is_item_hovered=function() return false end
    api.is_item_active=function() return active end
    api.get_mouse=function() return c.mouse or {x=0,y=0} end
    api.push_style_color=function() end;api.pop_style_color=function() end
    env.os.clock=function() return c.clock or 0 end
    env.Vector2f={new=function(x,y)return {x=x,y=y}end}
    env.Vector4f={new=function(x,y,z,w)return {x=x,y=y,z=z,w=w}end}
    env.d2d={Font={new=function(_,size) return {measure=function(_,s)
        local width,max,rows=0,0,1
        for _,cp in utf8.codes(s) do
            if cp==10 then max=math.max(max,width);width=0;rows=rows+1
            else width=width+(cp>255 and size or size*.55) end
        end
        return math.max(max,width),size*1.4*rows
    end} end},
        Image={new=function(path) return {path=path} end},register=function(init,draw)c.init=init;c.render=draw end,
        text=function()end,fill_rounded_rect=function()end,line=function()end,image=function()end}
    c.widgets=assert(loadfile(project.."/game-plugin/MotionVfxLive/fx_d2d_widgets.lua","t",env))()
    c.factory=assert(loadfile(project.."/game-plugin/MotionVfxLive/fx_d2d_workbench.lua","t",env))()
    c.pfb_factory=assert(loadfile(project.."/game-plugin/MotionVfxLive/fx_pfb_page.lua","t",env))()
    c.dialog_factory=assert(loadfile(project.."/game-plugin/MotionVfxLive/fx_file_dialog.lua","t",env))()
    c.bench=c.factory.new(c.module,c.widgets,c.pfb_factory,c.dialog_factory)
    c.module.workbench=c.bench
    c.init()
    c.show_ui=true;c.module.state.window_open=true
    function c:draw()
        self.bench:frame()
        assert(not self.module.state.ui_error,self.module.state.ui_error)
        self.render()
        assert(alpha==inherited_alpha and #alpha_stack==0,"Input transparency leaked into other controls")
    end
    function c:click(id) clicked[id]=true;self:draw() end
    function c:input(id,value) inputs[id]=value;self:draw() end
    function c:export(name)
        local f=assert(io.open(output.."/"..name..".json","w"));f:write(json_encode({width=self.screen_w or 1440,height=self.screen_h or 900,commands=self.bench.widgets.commands}));f:close()
    end
    return c
end
local function fixture(c)
    local s=c.module.state
    s.document={name="11_it02_000.efx",source="fixture/11_it02_000.efx.5571972",revision=8,dirty=false,
        groups={{index=0,name="group0",note="鬼人化 · 开启",entries={0,1}},{index=1,name="group1",note="连斩",entries={2}},
            {index=2,name="group2",note="鬼人化 · 关闭",entries={3}}},actions={},entries={
            {index=0,name="haruka_gpup",attributes={{index=0,type="Life"},{index=1,type="RGBCommon"},{index=2,type="SpawnExpression"},{index=3,type="TypeGPUParticle"}}},
            {index=1,name="minori_mesh",attributes={{index=0,type="TypeMesh"},{index=1,type="Material"}}},
            {index=2,name="blade_slash",attributes={{index=0,type="TypeRibbonFollow"}}},
            {index=3,name="demon_out",attributes={{index=0,type="Life"}}}}}
    s.selected={kind="entry",index=0,attributeIndex=1}
    s.fields={}
    local channels={}
    for i,name in ipairs({"R","G","B","A"}) do
        local path="color."..name;local value=({125,106,255,255})[i]
        s.fields[#s.fields+1]={path=path,label=name,type="System.Byte",editable=true,value=tostring(value)}
        s.values[path]=tostring(value);channels[#channels+1]={component=name,path=path}
    end
    s.fields[#s.fields+1]={path="brightness",label="亮度 · Brightness",type="System.Single",editable=true,value="1.5"}
    s.values.brightness="1.5"
    s.color_groups={{path="color",label="粒子颜色",storage="rgba8",scale=255,editable=true,channels=channels}}
    s.message="文档已同步。选择属性修改，提交后应用到游戏。"
    s.field_view_key=nil
    s.capture_snapshot={recording=false,resources={{path="Art/VFX/EffectEditor/Weapon/it02/11_it02_000.efx",localConfirmed=true,count=14}},
        actions={{id="a1",componentKind="character",motion={layer=0,name="鬼人化 · 进入",bankId=0,motionId=401},firstMotionFrame=0,lastMotionFrame=42},
        {id="a2",componentKind="character",motion={layer=0,name="乱舞 · 第一段",bankId=0,motionId=510},firstMotionFrame=0,lastMotionFrame=66},
        {id="a3",componentKind="character",motion={layer=0,name="连斩 · 第一段",bankId=0,motionId=512},firstMotionFrame=0,lastMotionFrame=54}}}
end
local c=setup();fixture(c)
c.module.current_actions=function()return {recording=false,currentActions={{componentKind="character",motion={layer=0,bankId=5,motionId=777,frame=12.5,name="Motion 777"}}}} end
c:draw();check(#c.bench.widgets.commands>15,"D2D renders the unified shell and action records")
local function has_text(c,text)
    for _,command in ipairs(c.bench.widgets.commands) do if command.kind=="text" and command.text:find(text,1,true) then return true end end
end
check(has_text(c,"Motion 777") and has_text(c,"12.5"),"Current motion and frame remain visible while recording is stopped")
c.module.current_actions=function()return {recording=false,currentActions={{componentKind="character",motion={layer=0,bankId=5,motionId=778,frame=2,name="Motion 778"}}}} end
c:draw();check(has_text(c,"Motion 778") and not has_text(c,"Motion 777") and #c.module.state.capture_snapshot.actions==3,"Live motion refresh does not append or reuse historical records")
local before=c.bench.window_position.x
c.active="##mvl_title_drag";c.mouse={x=40,y=45};c:draw()
c.mouse={x=65,y=70};c:draw();c.active=nil;c:draw()
check(c.bench.window_position.x>before,"Title dragging moves the unified workbench")
check(c.files["MotionVfxLive/workbench-settings.json"].position.x==c.bench.window_position.x,"Released window position persists")
c.bench.window_position={x=.5,y=.5};c:draw()
c:click("##top_hide");c:draw()
check(c.module.state.window_hidden and #c.bench.widgets.commands<8,"Hide leaves only the edge logo, not a full input host")
c:export("hidden")
c.active="##mvl_logo_orb";c.mouse={x=1420,y=400};c:draw()
c.mouse={x=10,y=100};c:draw();c.active=nil;c:click("##mvl_logo_orb")
check(c.bench.orb_side=="left" and c.module.state.window_hidden,"Dragging the orb snaps left without opening the editor on release")
c:click("##mvl_logo_orb");c:draw()
check(not c.module.state.window_hidden and #c.bench.widgets.commands>15,"Clicking the logo restores the same workbench")
c.show_ui=false;c:draw()
check(#c.bench.widgets.commands>0 and #c.bench.widgets.commands<8,"Logo remains visible when REFramework mouse UI is hidden")
c.show_ui=true;c:draw()
c:export("actions")
local transparent=setup(0);fixture(transparent)
transparent:draw()
check(transparent.host_alpha["##MotionVfxD2DInputHost"]==1,"Main input host stays interactive under inherited zero Alpha")
transparent:click("##top_hide");transparent:draw()
check(transparent.host_alpha["##MotionVfxD2DOrbHost"]==1,"Orb input host owns nonzero Alpha without leaking theme changes")
transparent:click("##mvl_logo_orb");transparent:draw()
check(not transparent.module.state.window_hidden and transparent.module.state.window_drawn,"Restoring from transparent theme renders the full workbench")
local orb_error=setup();orb_error.bench:hide()
local old_mouse=orb_error.env.imgui.get_mouse
orb_error.env.imgui.get_mouse=function()error("simulated orb input failure")end
local ok=pcall(function()orb_error:draw()end)
check(not ok and orb_error.style_vars==0,"Orb input exceptions unwind their native style stack")
orb_error.env.imgui.get_mouse=old_mouse;orb_error:draw()
check(orb_error.bench.orb_status.interactive,"Orb recovers after a transient input error")
c:click("##nav_effects");check(c.bench.page=="effects","Effect recording is a separate left page")
c:click("##nav_edit")
c.bench.widgets.trees["mvl_group_0"]=true
c.bench.widgets.trees["mvl_entry0_member1"]=true
c:draw();check(c.module.state.selected.attributeIndex==1,"Existing selected attribute survives D2D navigation")
c:export("editor")
local selected_rects=0
for _,command in ipairs(c.bench.widgets.commands) do if command.kind=="rect" and command.color==0xFF423A73 then selected_rects=selected_rects+1 end end
check(selected_rects>=2,"Selected attribute and current page retain highlight without hovering")
check(has_text(c,"确认删除当前属性"),"Deletion confirmation is visible on the selected attribute")
local deletion=setup();fixture(deletion);deletion.bench.page="edit";deletion.module.poll();deletion:draw()
check(not deletion.item_ids["删除当前属性##mvl_remove"],"Destructive button stays unavailable before confirming the target")
deletion:click("确认删除当前属性##mvl_confirm_delete");deletion:export("deletion")
check(has_text(deletion,"将删除："),"Deletion shows the specific selected entry and attribute")
deletion:click("删除当前属性##mvl_remove")
local remove=deletion.files["MotionVfxLive/request.json"]
check(remove.action=="remove_attribute" and remove.target.index==0 and remove.target.attributeIndex==1,"Attribute deletion sends the selected entry and exact attribute index")
deletion:respond({ok=true,revision=9,document=deletion.module.state.document})
check(deletion.module.state.selected==nil and not deletion.module.state.confirm_delete,"Successful deletion clears stale selection and confirmation")
local red_id
for id in pairs(c.input_ids) do if id:find("##mvl_color_value_color.R_",1,true) then red_id=id;break end end
check(red_id~=nil,"RGBA is rendered as one semantic color control")
c:input(red_id,"128")
check(c.module.state.values["color.R"]=="128","Editing D2D RGBA input updates the same existing color draft")
c.module.state.values["color.R"]="125"
local grouping=assert(loadfile(project.."/game-plugin/MotionVfxLive/fx_parameter_ui.lua"))()
local grouped={}
local function scalar(path,owner,value)
    local f={path=path,label=path,type="System.Single",editable=true,value=value or "1",ownerType=owner}
    grouped[#grouped+1]=f;return f
end
for _,axis in ipairs({"X","Y","Z"}) do scalar("Position."..axis,"System.Numerics.Vector3") end
for _,axis in ipairs({"X","Y","Z"}) do
    scalar("Size"..axis..".s","ReeLib.via+Range");scalar("Size"..axis..".r","ReeLib.via+Range","0")
end
scalar("Lifetime.s","ReeLib.via+RangeI");scalar("Lifetime.r","ReeLib.via+RangeI")
scalar("Other.s","Unrelated");scalar("Other.r","Unrelated")
for _,axis in ipairs({"X","Y","Z","W"}) do scalar("Four."..axis,"System.Numerics.Vector4") end
scalar("Speed","Test");scalar("SpeedRandom","Test")
scalar("UV.X","System.Numerics.Vector2");scalar("UV.Y","System.Numerics.Vector2")
for _,axis in ipairs({"X","Y"}) do scalar("UVScale"..axis..".s","ReeLib.via+Range");scalar("UVScale"..axis..".r","ReeLib.via+Range","0") end
local members=grouping.groups(grouped,{})
check(members["Position.X"]==members["Position.Z"] and #members["Position.X"].fields==3,"Complete XYZ is one group")
check(#members["SizeX.s"].fields==6 and #members["SizeX.s"].rows==2,"XYZ ranges form two rows of three without losing random axes")
check(members["Lifetime.s"].rows[1].fields[1].path=="Lifetime.s","RangeI uses semantic s/r roles rather than reversed serialized order")
check(not members["Other.s"] and not members["Four.X"],"Similar field names and Vector4 are never misclassified")
check(members.Speed==members.SpeedRandom,"Explicit normal/random scalar companions stay together")
check(members["UV.X"]==members["UV.Y"] and #members["UV.X"].fields==2 and #members["UV.X"].axes==2,"XY-only parameters share one two-component control")
check(members["UVScaleX.s"]==members["UVScaleY.r"] and #members["UVScaleX.s"].fields==4,"XY normal/random parameters preserve all four components")
local noZ=grouping.groups(grouped,{["Position.Z"]=true})
check(not noZ["Position.X"],"A present but excluded Z never turns XYZ into an XY group")
local blocked=grouping.groups(grouped,{["Position.Y"]=true})
check(not blocked["Position.X"],"An incomplete or color-owned vector never consumes remaining fields")
check(grouping.search("SizeScalar","尺寸") and grouping.search("SizeScalar","sizescalar"),"Names are searchable in Chinese and English")
check(grouping.label("TypeRibbonFollow","en")=="TypeRibbonFollow" and grouping.label("TypeRibbonFollow","zh"):find("跟随拖尾",1,true),"Attribute language switch retains exact English identifiers")
check(grouping.label("unkn712","zh"):find("未知字段",1,true),"Unresearched fields are labelled unknown, never assigned a guessed meaning")
local vectors=setup();fixture(vectors)
vectors.module.state.fields=grouped;vectors.module.state.color_groups={};vectors.module.state.values={}
for _,f in ipairs(grouped) do vectors.module.state.values[f.path]=f.value end
vectors.module.state.field_view_source=nil;vectors.bench.page="edit";vectors:draw()
vectors:export("parameters")
vectors.module.set_parameter_language("en");vectors:draw()
check(vectors.module.state.parameter_language=="en" and vectors.module.state.values["SizeX.r"]=="0","Switching language preserves every draft and original path")
vectors.module.state.values["SizeX.s"]="3";vectors.module.state.values["Other.s"]="9"
local submit
for _,command in ipairs(vectors.bench.widgets.commands) do if command.kind=="text" and command.text:find("Size",1,true) then submit=true end end
check(submit,"Grouped parameters are drawn through the real D2D field controller")
local submit_id
for id in pairs(vectors.item_ids) do if id:find("##mvl_parameter_set_Size_",1,true) then submit_id=id;break end end
vectors.module.poll()
vectors:click(assert(submit_id))
local request=vectors.files["MotionVfxLive/request.json"]
check(request and request.action=="set_fields" and #request.fields==6,"One grouped submit sends all six exact scalar paths in one transaction")
check(vectors.module.state.values["Other.s"]=="9","Submitting a group does not discard another parameter's draft")
local xy=setup();fixture(xy);xy.module.state.fields={};xy.module.state.values={};xy.module.state.color_groups={}
for _,f in ipairs(grouped) do if f.path:find("UV",1,true)==1 then xy.module.state.fields[#xy.module.state.fields+1]=f;xy.module.state.values[f.path]=f.value end end
xy.bench.page="edit";xy:draw();xy:export("xy")
local xy_id
for id in pairs(xy.item_ids) do if id:find("##mvl_parameter_set_UVScale_",1,true) then xy_id=id;break end end
xy.module.poll();xy:click(assert(xy_id))
check(#xy.files["MotionVfxLive/request.json"].fields==4,"XY-range submit sends exactly four fields atomically")
local author=setup();author.bench.top="settings";author.scroll={["##mvl_d2d_top_workspace"]=420};author:draw();author:export("settings")
check(author.item_ids["##author_bilibili"] and author.item_ids["##author_caimogu"],"Both author homepage links are interactive")
author.module.poll();author:click("##author_bilibili")
check(author.files["MotionVfxLive/request.json"].mode=="author_link" and author.files["MotionVfxLive/request.json"].site=="bilibili","Author link dispatch uses a fixed site identifier, not arbitrary commands")
c:click("##top_cache")
c.bench.cache={{id="tex",alias="art/MyEffects/虹光.tex",kind="tex",revision=2,dependencies={}},
    {id="uvs",alias="art/MyEffects/rainbow_sequence.uvs",kind="uvs",revision=1,dependencies={"art/MyEffects/虹光.tex"}},
    {id="mesh",alias="art/MyEffects/blade_arc.mesh",kind="mesh",revision=1,dependencies={}}}
c.module.state.pending=nil
c:draw();c:export("precache")
c:input("##d2d_input_alias","art/我的特效/虹光.tex")
check(c.bench.cache_alias=="art/我的特效/虹光.tex","Custom virtual paths preserve Chinese Unicode input")
c.active="##d2d_input_alias";c.selection={4,7};c:draw()
local highlighted=false
for _,command in ipairs(c.bench.widgets.commands) do if command.kind=="rect" and command.color==0xFF454072 then highlighted=true end end
check(not highlighted,"D2D does not duplicate the native text selection")
check(c.native_input_alpha["##d2d_input_alias"]==1,"One native editor renders text, selection, and caret with the same metrics as hit testing")
local carets=0
for _,command in ipairs(c.bench.widgets.commands) do
    if command.kind=="rect" and command.color==c.widgets.colors.cyan and command.w==1 then carets=carets+1 end
end
check(carets==0,"D2D never invents a caret from REFramework selection callbacks")
c.active=nil;c.selection=nil
c:click("##nav_time");check(c.bench.top==nil and c.bench.page=="time","Scene time and recorded playback have their own page")
c:export("time")
c.bench.replay={fps=20,width=1280,height=720,frames={{time=0,path="f1.jpg"},{time=.1,path="f2.jpg"},{time=.3,path="f3.jpg"}}}
c.bench.replay_index=3;c.bench.replay_speed=-1;c.bench.replay_clock=0
c.clock=.21;c.bench:tick()
check(c.bench.replay_index==2,"Recorded reverse uses frame timestamps")
c.clock=.32;c.bench:tick()
check(c.bench.replay_index==1,"Recorded reverse reaches the first frame")
check(#c.global_writes==0 and #c.scene_writes==0,"Reverse playback does not mutate scene clocks")
c.bench.replay_speed=0;c.clock=10;c.bench:tick()
check(c.bench.replay_index==1,"Recorded pause holds a frame regardless of wall time")
c:click("##nav_edit");c.module.state.ai_busy=true
local called=false;c.module.publish_document=function() called=true end
c:click("##top_apply")
check(not called,"D2D disables apply while an AI transaction owns editing")
c.module.state.ai_busy=false;c.module.state.open_confirmation={path="other.efx"}
c:click("##top_apply");check(not called,"Document switch modal disables underlying editing")
c.module.state.open_confirmation=nil;c.module.state.window_open=false
c:draw();check(#c.bench.widgets.commands==0,"Hiding the overlay removes stale D2D commands")
c.module.state.window_open=true;c.screen_w=1280;c.screen_h=720;c:draw()
check(#c.bench.widgets.commands>15,"Small screens retain one fitted workbench")
local resized=setup();fixture(resized);resized.bench.page="edit";resized:draw()
local bounds=resized.bench.bounds
resized.active="##mvl_resize_se";resized.mouse={x=bounds.x+bounds.w,y=bounds.y+bounds.h};resized:draw()
resized.mouse={x=bounds.x+bounds.w-400,y=bounds.y+bounds.h-180};resized:draw();resized.active=nil;resized:draw()
check(resized.bench.bounds.w==980 and resized.bench.bounds.h==650,"Corner resizing independently changes window width and height")
check(resized.bench.bounds.x==bounds.x and resized.bench.bounds.y==bounds.y,"Bottom-right resizing anchors the opposite corner")
check(resized.files["MotionVfxLive/workbench-settings.json"].size.w==980,"Window dimensions persist on release")
check(resized.item_ids["##mvl_resize_w"] and resized.item_ids["##mvl_resize_n"],"All sides and corners expose resize handles")
resized:export("resized")
local within=true
for _,command in ipairs(resized.bench.widgets.commands) do
    if command.y and command.y+(command.h or 0)>resized.bench.bounds.y+resized.bench.bounds.h then within=false end
end
check(within,"Nested parameter controls are clipped to the resized workbench")
check(resized.item_ids["##narrow_structure"] and resized.item_ids["##narrow_properties"],"Narrow windows retain full-width structure and property pages")
resized:click("##narrow_structure");check(has_text(resized,"特效结构"),"Narrow structure remains reachable after choosing an attribute")
local rb=resized.bench.bounds;local anchor=rb.x+rb.w
resized.active="##mvl_resize_w";resized.mouse={x=rb.x,y=rb.y+200};resized:draw()
resized.mouse={x=rb.x+1000,y=rb.y+200};resized:draw();resized.active=nil;resized:draw()
check(resized.bench.bounds.w==900 and math.abs(resized.bench.bounds.x+resized.bench.bounds.w-anchor)<.001,"Left edge preserves right anchor and enforces readable minimum")
resized.bench=resized.factory.new(resized.module,resized.widgets);resized:draw()
check(resized.bench.window_size.w==900,"Reload restores the user's resized dimensions")
resized.screen_w=800;resized.screen_h=600;resized:draw()
check(resized.bench.bounds.x>=0 and resized.bench.bounds.x+resized.bench.bounds.w<=800,"Smaller displays keep saved windows on screen")

local opening=setup();fixture(opening);opening.module.poll();opening.bench.top="file"
opening:draw();opening:export("files")
local first=opening.module.state.document.source
opening.module.send("status",{})
opening.bench.desktop_jobs["second-pick"]={statusFile="desktop-status-second-pick.json"}
opening.files["MotionVfxLive/desktop-status-second-pick.json"]={id="second-pick",phase="complete",mode="pick_efx",files={"C:/中文/second.efx.5571972"}}
opening.files["MotionVfxLive/desktop-recording-status.json"]={id="recording",phase="recording",mode="record",message="录制中"}
opening.files["MotionVfxLive/desktop-status.json"]=opening.files["MotionVfxLive/desktop-recording-status.json"]
opening.bench.last_poll=-1;opening:draw()
check(opening.bench.open_pending and opening.module.state.document.source==first,"A second file picked during a pending request is retained without replacing the first")
check(opening.bench.desktop.id=="recording","A concurrent picker does not replace recording status")
opening:respond({ok=true});opening:draw()
check(opening.files["MotionVfxLive/request.json"].path=="C:/中文/second.efx.5571972","Queued picker request opens after the mailbox becomes available")
opening.module.state.field_filter="old";opening.module.state.structure_filter="old"
opening:respond({ok=true,document={source="C:/中文/second.efx.5571972",name="second.efx",revision=9,entries={},groups={}}});opening:draw()
check(opening.bench.page=="edit" and opening.bench.top==nil and has_text(opening,"second.efx"),"Successful opening replaces the edit view and exits the File top tab")
check(opening.module.state.selected==nil and next(opening.module.state.values)==nil and opening.module.state.field_filter=="" and opening.module.state.structure_filter=="","New file clears old selection, drafts, and search filters")
opening.bench:queue_open('  "C:/中文/third.efx"  ');opening:draw()
check(opening.files["MotionVfxLive/request.json"].path=="C:/中文/third.efx","Manual path accepts quoted Chinese paths and unversioned EFX")
opening:respond({ok=false,error="Invalid file"});opening:draw()
check(opening.module.state.document.name=="second.efx" and has_text(opening,"Invalid file"),"Failed opening preserves the previous document and visibly reports the error")
opening.module.state.document.dirty=true
opening.bench:queue_open("C:/fourth.efx.5571972");opening:draw()
check(opening.module.state.open_confirmation and opening.module.state.pending==nil,"Dirty EFX still requires explicit confirmation")
opening:click("##cancel_doc");opening:draw()
check(opening.module.state.document.name=="second.efx" and opening.bench.open_pending==nil and opening.module.state.pending==nil,"Cancelling a queued switch does not reopen it on the next frame")
opening.bench:queue_open("C:/fourth.efx.5571972");opening:draw();opening:click("##confirm_doc")
opening:respond({ok=true,document={source="C:/fourth.efx.5571972",name="fourth.efx",revision=10,entries={},groups={}}});opening:draw()
check(opening.module.state.document.name=="fourth.efx" and opening.bench.page=="edit","Confirming a switch navigates only after successful loading")
opening.module.state.document.dirty=true
opening.bench:queue_open("C:/中文/双刀.motlist.992");opening:draw()
check(opening.files["MotionVfxLive/request.json"].action=="open_motion","Local motion has a dedicated reader and does not discard dirty EFX")
opening:respond({ok=true,motionDocument={path="C:/中文/双刀.motlist.992",name="双刀动作",motions={{motionId=401,name="Demon",frameCount=42,frameRate=60,slots={{startFrame=0,endFrame=30,effectId="11-0",containerId=11,elementId=0,jointName="VFX_Attack",origin="MOT"}}}}}})
opening:draw();opening.bench.widgets.trees.local_motion_slots=true;opening:draw();opening:export("local-motion")
check(opening.bench.page=="actions" and opening.bench.action_source=="local" and has_text(opening,"双刀动作"),"Local action data opens its own visible page")
check(opening.module.state.document.name=="fourth.efx" and opening.module.state.document.dirty,"Opening motions keeps EFX content and unsaved state intact")
opening.bench:queue_open("C:/bad.mot");opening:draw();opening:respond({ok=false,error="Invalid motion"});opening:draw()
check(opening.bench.motion_document.name=="双刀动作" and has_text(opening,"Invalid motion"),"Invalid motion leaves the last successful motion view intact")
check(not opening.bench:queue_open("C:/file.txt"),"Unrelated local files are rejected before dispatch")
local captured=setup();captured.module.poll()
for i=1,2 do
    local resource={path="effect/current"..i..".efx",localConfirmed=true}
    captured.capture_state.resources={resource};captured.module.state.capture_snapshot.resources={resource}
    captured.bench.page="effects";captured.bench.top=nil
    captured:click("##d2d_effect_1");captured:draw()
    local request=captured.files["MotionVfxLive/request.json"]
    check(request.action=="open_game_resource" and request.sourceDescriptor.sharedTargetPath==resource.path,"Captured selection "..i.." uses the actual action provenance")
    captured:respond({ok=true,document={source="game-files://source/"..i..".efx.5571972",name="captured-"..i,sourceKind="game_files",gameSource=request.sourceDescriptor,revision=i,dirty=false,entries={},groups={}}})
    captured:draw()
    check(captured.bench.page=="edit" and captured.bench.top==nil and has_text(captured,"captured-"..i) and captured.module.state.target_resource_path==resource.path,"Captured selection "..i.." switches the edit page and its real-resource binding together")
end

local previous=setup()
previous.files["MotionVfxLive/desktop-status.json"]={id="old",phase="complete",mode="pick_efx",files={"old.efx"}}
previous.bench=previous.factory.new(previous.module,previous.widgets)
previous.bench:tick()
check(previous.module.state.pending==nil,"Reload never reopens an old completed file-picker request")
local restoring=setup()
restoring.bench.restoring=true
restoring.module.state.effect_update_original={enabled=true}
restoring.bench:tick()
check(restoring.bench.restoring,"Restore status waits for the effect update gate to recover")
restoring.module.state.effect_update_original=nil
restoring.module.state.bindings_snapshot.requiresRestoreBeforeReset=true
restoring.bench:tick()
check(restoring.bench.restoring,"Restore status retains archived resource recovery obligations")
restoring.module.state.bindings_snapshot.requiresRestoreBeforeReset=false
restoring.bench:tick()
check(not restoring.bench.restoring and restoring.module.state.message:find("已还原",1,true),"Restore completion is shown only after resource and time obligations finish")
restoring.bench.apply_pending=true;restoring.bench.reapply_after_warm=true
restoring:click("##top_restore")
check(not restoring.bench.apply_pending and not restoring.bench.reapply_after_warm,"Stop and restore cancels queued cache reapplication")

-- Native resource ownership is exercised without calling the engine.
local references,requests={},{}
local env=setmetatable({sdk={
    find_type_definition=function()return true end,
    create_resource=function(kind,path)
        requests[#requests+1]={kind=kind,path=path}
        if path:find("fail",1,true) then return nil end
        local r={count=0}
        function r:add_ref()self.count=self.count+1 end
        function r:release()self.count=self.count-1 end
        function r:create_holder()return nil end
        references[#references+1]=r;return r
    end
}},{__index=_G})
local preload=assert(loadfile(project.."/game-plugin/MotionVfxLive/fx_precache_runtime.lua","t",env))().new()
local entry={id="x",alias="art/a.tex",kind="tex"}
check(preload:prepare({entry},{["art/a.tex"]="art/MotionVfxLive/Precache/r1/asset.tex"}),"Preloader accepts only private engine paths")
preload:tick();check(references[1].count==1,"Preloader pins its accepted native resource")
preload:prepare({entry},{["art/a.tex"]="art/MotionVfxLive/Precache/fail/asset.tex"});preload:tick()
check(references[1].count==1 and preload:snapshot().x.phase=="error","Failed native reload retains the previous successful resource")
preload:close();check(references[1].count==0,"Closing preloader releases only owned references")
check(not preload:prepare({entry},{["art/a.tex"]="../escape"}),"Closed preloader cannot schedule a new request")
do
    local c=setup();fixture(c);c.bench.action_source="slots";c.module.state.selected_action_id="a1"
    c.screen_h=2400;c.bench.window_size={w=1380,h=2250}
    local doc={revision=4,name="Test motion",pfbPath="art/verified.pfb",frameCount=90,added={{id="new-slot",startFrame=3,endFrame=20,effectId="10-80",resourcePath="art/test.efx"}}}
    c.bench.action_document=doc
    local preview={phase="active",busy=false,requiresRestore=true,appliedRevision=3}
    c.module.action_preview_status=function()return preview end
    local called
    c.module.preview_action=function(d,r)called={d=d,r=r}end
    c.module.restore_action_preview=function()called="restore"end
    c:click("##d2d_preview_action_pair")
    check(called and called.d==doc and called.r.id=="a1","Hot reload uses the selected action and current action document")
    check(has_text(c,"有尚未热重载"),"The UI distinguishes new slot edits from the applied revision")
    called=nil;c:click("##top_apply")
    check(called and called.d==doc,"Top Apply publishes the action slot document on the slots page")
    c.module.remove_action_slot=function(d,id)called={d=d,id=id}end
    c:click("##d2d_delete_action_slot_new-slot")
    check(called and called.id=="new-slot" and called.d==doc,"Each added slot has its own delete command")
    c:click("##d2d_restore_action_pair");check(called=="restore","Dedicated action restore is wired to paired restoration")
    preview.busy=true;called=nil;c:click("##d2d_preview_action_pair")
    check(called==nil,"The hot reload button cannot queue a second transaction while busy")
    c.bench.restoring=true;c.bench:tick();check(c.bench.restoring,"Global restore waits for action/PFB recovery")
    preview.busy=false;preview.phase="restore_failed";preview.message="PFB recovery pending";c.bench:tick()
    check(not c.bench.restoring and c.module.state.message:find("PFB recovery pending",1,true),"Action restore failure is never shown as successful global recovery")
end
do
    local c=setup();fixture(c);c.bench.action_source="slots";c.module.state.selected_action_id="a1"
    c.screen_h=2200;c.bench.window_size={w=1380,h=2050}
    c.bench.action_document={revision=2,name="Lifetime test",pfbPath="art/verified.pfb",frameCount=58,
        independentLifetimeAvailable=true,added={},source={attachmentTargets={{key="self",kind="character",label="人物",relationChildId=-1,joints={"Root"}}}}}
    c.bench.slot_template="p#0";c.bench.slot_resource="art/test.efx";c.bench.slot_start="3";c.bench.slot_end="20";c.bench.slot_lifetime="120"
    c.bench.slot_groups_path="art/test.efx";c.bench.slot_groups={"group_0","group_1"}
    local request;c.module.send=function(action,data)request={action=action,data=data};return true end
    c:click("##d2d_add_action_slot")
    check(request and request.data.lifetimeMode=="independent" and request.data.lifetimeFrames==120 and request.data.endFrame==20,
        "Independent duration is submitted separately from the motion trigger end")
    request=nil;c:click("##d2d_slot_life_action");c:click("##d2d_add_action_slot")
    check(request and request.data.lifetimeMode=="action","Action-bound mode remains available explicitly")
end
do
    local c=setup();fixture(c);c.bench.action_source="groups"
    c.capture_state={actor={characterIdentity="actor",sceneEpoch=1,weaponType=2,weaponId=89},resources={},events={},actions={
        {id="wrong-bank",componentKind="character",actorIdentity="actor",sceneEpoch=1,weaponType=2,weaponId=89,motion={layer=0,bankId=0,motionId=37,bankResourcePath="motion/weapon.motlist"}},
        {id="right-bank",componentKind="character",actorIdentity="actor",sceneEpoch=1,weaponType=2,weaponId=89,motion={layer=0,bankId=0,motionId=37,bankResourcePath="motion/common.motlist"}}}}
    c.bench.action_groups={{id="sheathed",name="收刀状态下的走路/跑步",motions={{path="motion/common.motlist",bankId=0,motionId=37,name="common_037"}}}}
    c.bench.widgets.trees.action_group_sheathed=true
    c.module.state.capture_snapshot=c.capture_state;c:click("##d2d_group_action_sheathed_1")
    check(c.module.state.selected_action_id=="right-bank" and c.bench.action_source=="slots","Saved group selects the full bank path, not another motion with the same ID")
    c.bench.action_source="groups";c.capture_state.actor.sceneEpoch=2;c.module.state.selected_action_id=nil
    c:click("##d2d_group_action_sheathed_1")
    check(c.module.state.selected_action_id==nil,"Saved group cannot reuse records from an old scene")
end
do
    local c=setup();fixture(c);c.module.poll();c.bench:queue_open("C:/我的模组/装备.pfb.18");c:draw()
    check(c.files["MotionVfxLive/request.json"].action=="pfb_open","PFB files use an independent editor request")
    local original=c.module.state.document
    local equipment={path="C:/我的模组/装备.pfb.18",name="装备.pfb",type="equipment",revision=1,dirty=false,
        addKinds={{kind="effectPfb",label="添加特效槽位（PFB）"},{kind="chain",label="添加物理槽位（Chain）"},{kind="clsp",label="添加碰撞槽位（CLSP）"},{kind="jcns",label="添加约束槽位（JCNS）"}},
        targets={{key="root",label="装备根对象"}},slots={{key="slot1",kind="chain",label="物理",fields={{path="f1",label="启用",type="bool",value=true,editable=true}}}}}
    c:respond({ok=true,pfbDocument=equipment});c:draw()
    check(c.bench.page=="pfb" and c.bench.pfb.document==equipment and c.module.state.document==original,"Opening a PFB changes only the PFB page, preserving the EFX document")
    check(c.item_ids["##d2d_pfb_add_effectPfb"] and c.item_ids["##d2d_pfb_add_chain"] and c.item_ids["##d2d_pfb_add_clsp"] and c.item_ids["##d2d_pfb_add_jcns"],"Equipment classification exposes all four resource operations")
    c.bench.pfb.selected="slot1";c.bench.pfb.values["slot1:f1"]=false
    local request;c.module.send=function(action,data,callback)request={action=action,data=data};return true end
    c:click("##d2d_pfb_field_slot1:f1")
    check(request and request.action=="pfb_set" and request.data.value==false,"PFB boolean false is committed without falling back to the old true value")
    c.bench.pfb.document={path="art/effect.pfb",name="特效.pfb",type="effect",revision=2,canPaste=true,slots={}}
    c.item_ids={};c:draw()
    check(c.item_ids["##d2d_pfb_paste"] and not c.item_ids["##d2d_pfb_add_chain"],"Effect PFB presents copy/paste instead of equipment operations")
    c:click("##d2d_pfb_paste")
    check(request.action=="pfb_paste" and request.data.pfbRevision==2,"Paste uses the separate PFB revision")
    c.bench.pfb.document.type="unknown";c.item_ids={};c:draw()
    check(not c.item_ids["##d2d_pfb_paste"] and not c.item_ids["##d2d_pfb_add_chain"],"Unknown PFB types never offer incompatible mutations")
    c.bench.pfb.document.type="effect";c:click("##d2d_pfb_export")
    check(request.action=="prepare_export" and request.data.scope=="pfb","PFB export prepares its own file without launching a desktop dialog")
end
do
    local c=setup();fixture(c);c.module.poll()
    c.bench.action_source="slots";c.module.state.selected_action_id="a1"
    local proof={bankPath="Motion/Player/Weapon/Wp03/wp03_00/wp03_00.motlist",prefabs={
        {path="Art/VFX/EffectProvider/Player/pl_cm/epvr_hm_cm.pfb",externalIndex=-1,dataContainerId=0},
        {path="Art/VFX/EffectProvider/Weapon/it03/epvr_it03.pfb",externalIndex=0,dataContainerId=3}}}
    c.bench.action_proof=proof
    c.capture_state=c.module.state.capture_snapshot
    local record=c.capture_state.actions[1]
    c.module.find_action_record=function()return record end
    c.module.resolve_action_source=function()return proof end
    c.files["MotionVfxLive/status.json"]={ready=true,protocol=1,heartbeatUnixSeconds=c.time-30}
    c.time=c.time+1;c.module.poll();c:click("##d2d_open_action_pfb_1")
    check(c.files["MotionVfxLive/request.json"]==nil and has_text(c,"文件编辑服务未连接"),"Disconnected PFB opening shows a local explanation without dispatching a request")
    c.files["MotionVfxLive/status.json"]={ready=true,protocol=1,pid=123,heartbeatUnixSeconds=c.time+1}
    c.time=c.time+1;c.module.poll();c:click("##d2d_open_action_pfb_1")
    check(c.files["MotionVfxLive/request.json"].prefabIndex==0,"Character PFB opens after backend recovery without resetting records or reloading scripts")
    local first={name="Longsword idle",pfbPath=proof.prefabs[1].path,frameCount=90,revision=0,source=proof}
    c:respond({ok=true,actionDocument=first});c:draw()
    check(c.bench.action_document==first and has_text(c,"已打开动作槽位"),"Successful character PFB opening visibly confirms the chosen document")
    c:click("##d2d_open_action_pfb_2")
    local pending=c.module.state.pending.id
    check(c.files["MotionVfxLive/request.json"].prefabIndex==1,"Weapon PFB button submits its own verified provider index")
    c:click("##d2d_open_action_pfb_1")
    check(c.module.state.pending.id==pending,"A pending PFB opening cannot be replaced by a second click")
    local second={name="Longsword idle",pfbPath=proof.prefabs[2].path,frameCount=90,revision=0,source=proof}
    c:respond({ok=true,actionDocument=second});c:draw()
    check(c.bench.action_document==second,"The second PFB replaces the visible action slot document")
    c:click("##d2d_open_action_pfb_1");c:respond({ok=false,error="PFB test parse error"});c:draw()
    check(c.bench.action_document==second and c.bench.action_open_error:find("PFB test parse error",1,true) and has_text(c,"PFB test parse error"),"Failed opening preserves the previous document and shows the error beside the PFB controls")
    c.module.resolve_action_source=function()c.module.state.message="装备已经变化";return nil end
    c:click("##d2d_open_action_pfb_1")
    check(c.module.state.pending==nil and c.bench.action_open_error=="装备已经变化","Invalid source remains blocked and its reason is visible beside the button")
end
do
    local c=setup();fixture(c);c.module.poll()
    local s=c.module.state
    c.capture_state=s.capture_snapshot
    c.capture_state.recording=true
    c.capture_state.resources={
        {path="art/one.efx",localConfirmed=true,count=1},
        {path="art/two.efx",localConfirmed=true,count=2},
        {path="art/enemy.efx",localConfirmed=false,count=3}}
    c.capture_state.actions[1].sceneEpoch=1
    c.capture_state.events={{id="ev1",sceneEpoch=1,localConfirmed=true,created={{path="art/one.efx"}},
        actionLinks={{actionId="a1",association="direct_motion_request"}}}}
    c:click("##action_a1");c:draw()
    check(c.bench.page=="effects" and c.bench.effect_action_id=="a1" and has_text(c,"one.efx") and not has_text(c,"two.efx"),"Explicit action selection filters only resources associated with that action")
    c.capture_state.actions={c.capture_state.actions[2]};c:draw()
    check(c.bench.effect_action_id==nil and has_text(c,"已自动显示全部角色特效") and has_text(c,"one.efx") and has_text(c,"two.efx"),"Expired action selection returns to the labelled all-effects view instead of hiding new captures")
    check(not has_text(c,"enemy.efx") and s.capture_snapshot.recording,"Fallback retains confirmed ownership filtering and does not stop recording")
    c.capture_state.resources[#c.capture_state.resources+1]={path="art/three.efx",localConfirmed=true};c:draw()
    check(has_text(c,"three.efx") and c.bench.effect_view_status.visibleCount==3,"New resources appear while recording without requiring Stop")
    c.bench.effect_action_id="a2";c:draw()
    check(has_text(c,"该动作暂未记录到关联特效") and not has_text(c,"one.efx"),"A valid action with no effects remains empty rather than falsely claiming unrelated resources")
    s.selected_action_id="a2";c.bench.resource_filter="missing-search";c:click("##nav_effects")
    check(c.bench.effect_action_id==nil and c.bench.resource_filter=="" and has_text(c,"three.efx") and s.selected_action_id=="a2","Left Effect Records opens all current resources without discarding the selected slot-edit action")
    c:input("##d2d_effect_filter","missing-search")
    check(has_text(c,"没有匹配搜索的特效"),"Search misses are explained separately from a capture failure")
    c:click("##d2d_clear_effect_filter")
    check(has_text(c,"one.efx") and c.bench.resource_filter=="","Clear search restores captured resources immediately")
    c.bench.effect_action_id="a2";c.bench.resource_filter="hidden";c:click("##d2d_all_effects")
    check(has_text(c,"three.efx") and c.bench.effect_action_id==nil and c.bench.resource_filter=="","All Effects clears both the action and text filters")
    c.bench.effect_action_id="a2";c.bench.resource_filter="hidden";c.capture_state.recording=false
    c:click("##d2d_capture")
    check(c.capture_state.recording and c.bench.effect_action_id==nil and c.bench.resource_filter=="","Starting a new capture drops previous effect filters")
    c.bench.effect_action_id="a2";c.bench.resource_filter="hidden";c:click("##d2d_clear")
    check(c.bench.effect_action_id==nil and c.bench.resource_filter=="" and has_text(c,"正在等待角色特效"),"Clearing records clears effect filters and shows the correct recording empty state")
end
do
    local c=setup();fixture(c);c.module.poll();c.capture_state=c.module.state.capture_snapshot
    c.bench.page="effects";c:draw()
    check(has_text(c,"不生成画面录像") and c.item_ids["##d2d_effects_replay"],"Effect-call recording explains its scope and exposes the screen-replay entry")
    c:click("##d2d_effects_replay");c:draw()
    check(c.bench.page=="time" and has_text(c,"没有可回看的录像"),"Missing replay shows a clear message rather than pretending call records are video")
    c:click("##d2d_record_stop")
    check(c.module.state.pending==nil,"Stop recording is disabled when no screen recorder is running")
    c:click("##d2d_record")
    check(c.files["MotionVfxLive/request.json"].mode=="record","Screen recording launches the dedicated recorder")
    c:respond({ok=true,desktop={id="record-test",mode="record",statusFile="desktop-status-record-test.json"}})
    check(c.module.state.window_hidden and c.bench:recording_active(),"An accepted recording hides the workbench and exposes a pending recording state")
    c.bench:show();c:click("##d2d_record")
    check(c.module.state.pending==nil,"A starting recorder cannot be started twice")
    c:click("##d2d_record_stop")
    check(c.files["MotionVfxLive/request.json"].action=="desktop_stop","Stop and save targets the recorder")
    c:respond({ok=true})
    local replay={id="test-clip",outputDirectory="D:/Game/reframework/images/MotionVfxLive/replays/test-clip",width=1280,height=720,fps=20,
        frames={{path="MotionVfxLive/replays/test-clip/1.jpg",time=0},{path="MotionVfxLive/replays/test-clip/2.jpg",time=.1}}}
    c.files["MotionVfxLive/desktop-recording-status.json"]={id="record-test",mode="record",phase="complete",message="已保存 2 帧",replay=replay}
    c.files["MotionVfxLive/desktop-status-record-test.json"]=c.files["MotionVfxLive/desktop-recording-status.json"]
    c.bench.last_poll=-1;c:draw()
    check(c.bench.replay==replay and c.bench.page=="time" and not c.bench:recording_active(),"Completed recordings load automatically and take the workbench to playback")
    c.screen_h=2400;c.bench.window_size={w=1380,h=2250};c:draw()
    check(has_text(c,replay.outputDirectory) and c.item_ids["##d2d_replay_folder"],"Replay page shows the saved folder and an Open Folder action")
    c.bench.replay_index=2;c:click("##d2d_replay_forward")
    check(c.bench.replay_index==1 and c.bench.replay_speed==1,"Play at the end restarts the clip")
    c.bench.replay_index=1;c:click("##d2d_replay_reverse")
    check(c.bench.replay_index==2 and c.bench.replay_speed==-1,"Reverse at the beginning starts from the end")
    c:click("##d2d_replay_folder")
    check(c.files["MotionVfxLive/request.json"].mode=="open_replay_folder","Open Folder dispatches the bounded replay-directory operation")
    c:respond({ok=true,desktop={id="folder-test",mode="open_replay_folder",statusFile="desktop-status-folder-test.json"}});c.bench.replay_speed=0
    c.files["MotionVfxLive/desktop-status-folder-test.json"]={id="folder-test",mode="open_replay_folder",phase="complete",files={replay.outputDirectory}}
    c.bench.last_poll=-1;c:draw()
    check(c.bench.page=="time" and c.bench.top==nil and c.bench.selected_files==nil,"Opening the replay folder does not treat it as an asset import or leave playback")
    c.files["MotionVfxLive/last-replay.json"]={frames={}};c:click("##d2d_replay_refresh")
    check(c.bench.replay==replay and has_text(c,"没有可回看的录像"),"An empty or missing recording never discards a valid previous clip")
    c.files["MotionVfxLive/desktop-recording-status.json"]={id="failed-test",mode="record",phase="error",message="没有录到游戏画面"}
    c.bench.last_poll=-1;c:draw()
    check(has_text(c,"没有录到游戏画面") and c.bench.replay==replay,"Recording failure remains visible while the previous saved clip stays available")
    c.files["MotionVfxLive/last-replay.json"]=replay
    local restored=c.factory.new(c.module,c.widgets,c.pfb_factory)
    check(restored.replay==replay,"A script reload discovers the latest saved replay without starting a new capture")
end
do
    local c=setup();c.module.poll();c.bench.top="file"
    check(c.bench:choose_file("pick_efx"),"EFX opening uses the in-game dialog")
    c:draw()
    check(c.files["MotionVfxLive/request.json"].action=="file_browse","Opening a picker reads directories without starting any Windows window")
    c:respond({ok=true,fileBrowser={path="C:\\我的模组",parentPath="C:\\",roots={{name="C:\\",path="C:\\"},{name="D:\\",path="D:\\"}},
        shortcuts={{name="桌面",path="C:\\Users\\Test\\Desktop"},{name="下载",path="C:\\Users\\Test\\Downloads"}},
        entries={{name="彩虹双刀",path="C:\\我的模组\\彩虹双刀",isDirectory=true},
            {name="11_it02_000.efx.5571972",path="C:\\我的模组\\11_it02_000.efx.5571972",isDirectory=false,size=102400}},total=2,offset=0,limit=100}})
    c.item_ids={};c:draw()
    check(c.item_ids["##mvl_files_submit"] and not c.item_ids["##nav_edit"] and not c.item_ids["##d2d_capture"],"Active file dialog blocks all underlying editor controls")
    c:click("##mvl_files_entry_2");c:draw();c:export("file-browser")
    local highlighted=false
    for _,command in ipairs(c.bench.widgets.commands)do if command.kind=="rect"and command.color==0xFF423A73 then highlighted=true end end
    check(highlighted,"Selected file has a persistent purple highlight")
    local old_size=c.bench.window_size;c.screen_w=900;c.screen_h=600;c.bench.window_size={w=850,h=550};c:draw();c:export("file-browser-small")
    check(c.item_ids["##mvl_files_submit"]and c.item_ids["##mvl_files_path"],"Small windows retain directory navigation and confirmation")
    c.screen_w=nil;c.screen_h=nil;c.bench.window_size=old_size;c:draw()
    check(c.bench.file_dialog.selected_order[1]=="C:\\我的模组\\11_it02_000.efx.5571972","Themed file rows select full Chinese paths")
    c:click("##mvl_files_submit");c:draw()
    check(not c.bench.file_dialog.active and c.files["MotionVfxLive/request.json"].action=="open" and c.files["MotionVfxLive/request.json"].path=="C:\\我的模组\\11_it02_000.efx.5571972","Confirming a chosen EFX opens the selected file through the existing document flow")
end
do
    local c=setup();c.module.poll();c.bench:choose_file("pick_motion");c:draw()
    c:click("##mvl_files_cancel");c:respond({ok=true,fileBrowser={path="C:\\",entries={{name="walk.mot.43",path="C:\\walk.mot.43"}},total=1}})
    c:draw()
    check(not c.bench.file_dialog.active and c.bench.open_pending==nil,"Cancelling while reading ignores the late directory response")
    c.bench:choose_file("pick_pfb");c:draw()
    c:respond({ok=true,fileBrowser={path="C:\\",entries={{name="equipment.pfb.18",path="C:\\equipment.pfb.18"}},total=1}})
    c:click("##mvl_files_entry_1");c:click("##mvl_files_submit");c:draw()
    check(c.files["MotionVfxLive/request.json"].action=="pfb_open","PFB selection routes to the independent PFB editor")
end
do
    local c=setup();c.module.poll();c.bench:choose_file("pick");c:draw()
    c:respond({ok=true,fileBrowser={path="D:\\素材",entries={{name="火.tex.1",path="D:\\素材\\火.tex.1"},{name="光.uvs.1",path="D:\\素材\\光.uvs.1"}},total=2}})
    c:click("##mvl_files_entry_1");c.bench.file_dialog:select(c.bench.file_dialog.entries[2],{ctrl=true},2);c:click("##mvl_files_submit");c:draw()
    check(c.bench.top=="cache" and #c.bench.selected_files==2 and c.bench.selected_files[2]=="D:\\素材\\光.uvs.1","Multiple external files go to precache together without opening an OS dialog")
end
do
    local c=setup();c.module.poll();c.bench:begin_export("action");c:draw()
    check(c.files["MotionVfxLive/request.json"].action=="prepare_export","Action export prepares independent files without a desktop task")
    c:respond({ok=true,exportRequest={exportToken="export-pair",items={{key="motion",name="walk.mot.43",label="动作文件"},{key="pfb",name="player.pfb.18",label="角色 PFB"}}}})
    c:draw();c:draw()
    c:respond({ok=true,fileBrowser={path="D:\\导出",entries={},total=0}})
    c:draw();check(c.bench.file_dialog.options.save and c.bench.file_dialog.name=="walk.mot.43","The first action file has its own in-game destination dialog")
    c:click("##mvl_files_cancel");c:draw();c:draw()
    c:respond({ok=true,fileBrowser={path="D:\\导出",entries={{name="player.pfb.18",path="D:\\导出\\player.pfb.18",size=4096}},total=1}})
    c:draw();check(c.bench.file_dialog.name=="player.pfb.18","Skipping the motion file still offers the independent PFB destination")
    c:click("##mvl_files_submit");c:draw()
    check(c.files["MotionVfxLive/request.json"].action=="file_stat","Saving checks the destination before exporting")
    c:respond({ok=true,fileStat={path="D:\\导出\\player.pfb.18",exists=true,isDirectory=false,sha256="ABCDEF"}})
    c:draw();c:export("file-save")
    check(c.bench.file_dialog.active and has_text(c,"确认覆盖文件"),"Existing destination requires explicit in-game overwrite confirmation")
    c:click("##mvl_files_submit");c:draw();c:draw()
    local request=c.files["MotionVfxLive/request.json"]
    check(request.action=="finish_export" and #request.destinations==1 and request.destinations[1].key=="pfb" and request.destinations[1].expectedHash=="ABCDEF","Only the selected file exports with the verified existing-file hash")
end
print("RESULT "..passed.." D2D/precache assertions passed; no game calls")
