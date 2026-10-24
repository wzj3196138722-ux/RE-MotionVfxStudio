-- Real editor/controller with isolated UI/mailbox mocks; no game/file writes.
local project=arg[1]
local saved=arg
arg={project.."/game-plugin/MotionVfxLive.lua",nil,nil,"export-environment"}
local environment=assert(loadfile(project.."/tools/LuaValidation/live-ui-tests.lua"))()
arg=saved
local count=0
local function check(value,label) assert(value,label);count=count+1;print("PASS "..label) end
local function clone(value)
    if type(value)~="table" then return value end
    local result={};for k,v in pairs(value) do result[k]=clone(v) end;return result
end
local function setup()
    local c=environment();c.module.poll()
    c.module.state.document=assert(loadfile(project.."/tools/LuaValidation/playemitter-ui-fixture.lua"))()
    c.kinds={}
    c.env.imgui.set_button_kind=function(kind)c.last_kind=kind end
    local old=c.env.imgui.button
    c.env.imgui.button=function(label,...)
        c.kinds[label:match("##.*$") or label]=c.last_kind
        return old(label,...)
    end
    return c
end
local function request(c)return c.files["MotionVfxLive/request.json"] end
local function target(c,entry,attribute)
    local node=c.module.state.document.actions[1].attributes[1].embedded.entries[entry+1]
    return attribute~=nil and node.attributes[attribute+1].target or node.target
end
local function select(c,t,response)
    check(c.module.inspect(t),"Inspect queues full nested target")
    c:respond(response or {ok=true,fields={}})
end
local function seen(c,id)
    for _,item in ipairs(c.drawn_buttons) do if item.id==id then return true end end
    return false
end
local function drawn(c,prefix,controls)
    for _,item in ipairs(controls or c.drawn_buttons) do if item.id:sub(1,#prefix)==prefix then return item.id end end
    error("Missing control "..prefix)
end
local function click_fields(c,id)
    c.buttons[id]=true;c.module.ui_fields()
end
local field={path="testScalar",value="2",editable=true,type="System.Single"}

do
    local c=setup();local s=c.module.state
    local node=s.document.actions[1].attributes[1].embedded.entries[1]
    check(node.target.index==0 and #node.attributes==14,"Real backend PlayEmitter navigation uses array position zero, not stored native Entry ID 14")
    c.buttons["##mvl_attr_entry0_5_in_action_0_0"]=true;c.module.ui_tree()
    local r=request(c)
    check(r.action=="inspect" and r.target.kind=="entry" and r.target.index==0 and r.target.attributeIndex==5 and
        r.target.embeddedPath[1].kind=="action" and r.target.embeddedPath[1].index==0,"Nested TypePolygonTrail click addresses Action0/PlayEmitter/Entry0/Attribute5")
    c:respond({ok=true,fields={clone(field)}})
    c.module.ui_tree()
    check(c.kinds["##mvl_attr_entry0_5_in_action_0_0"]=="selected" and c.kinds["##mvl_attr_action0_0"]~="selected","Only the actual nested attribute highlights")
    c.module.ui_fields()
    local text=table.concat(c.messages,"|")
    check(text:find("234_ACT_PT",1,true) and text:find("234_PLG_trail",1,true),"Property heading identifies both containing Action and nested Entry")
    s.values.testScalar="7"
    click_fields(c,drawn(c,"##mvl_set_testScalar_"))
    r=request(c)
    check(r.action=="set" and r.target.embeddedPath[1].index==0 and r.target.index==0 and r.value=="7","Scalar submit retains exact embedded target")
    c:respond({ok=true,revision=5})
    check(request(c).action=="inspect" and request(c).target.embeddedPath[1].attributeIndex==0,"Post-edit refresh remains inside the embedded EFX")
    c:respond({ok=true,fields={{path="testScalar",value="7",editable=true}}})
    click_fields(c,"##mvl_parent_target")
    check(request(c).target.attributeIndex==nil and request(c).target.embeddedPath[1].index==0,"Back from nested attribute returns to its nested owner")
    c:respond({ok=true,fields={}})
    click_fields(c,"##mvl_parent_target")
    check(request(c).target.kind=="action" and request(c).target.index==0 and request(c).target.attributeIndex==0 and request(c).target.embeddedPath==nil,"Back from embedded Entry returns to containing PlayEmitter")
end

do
    local c=setup();local s=c.module.state
    local other=clone(s.document.actions[1]);other.index=1;other.name="synthetic_second_branch";other.target.index=1;other.attributes[1].target.index=1
    for _,node in ipairs(other.attributes[1].embedded.entries) do
        node.target.embeddedPath[1].index=1
        for _,attribute in ipairs(node.attributes) do attribute.target.embeddedPath[1].index=1 end
    end
    s.document.actions[2]=other
    local first=target(c,0,5);local second=other.attributes[1].embedded.entries[1].attributes[6].target
    select(c,first,{ok=true,fields={clone(field)}});c.module.ui_fields()
    local first_input=drawn(c,"##mvl_value_testScalar_",c.drawn_inputs)
    s.field_page=8;s.confirm_delete=true
    select(c,second,{ok=true,fields={clone(field)}})
    check(s.field_page==1 and not s.confirm_delete,"Changing outer branch resets pagination and delete confirmation even when inner indices match")
    c.drawn_inputs={};c.module.ui_fields()
    local second_input=drawn(c,"##mvl_value_testScalar_",c.drawn_inputs)
    check(first_input~=second_input and c.module.target_key(first)~=c.module.target_key(second),"Same inner index and scalar path have distinct input/selection keys across Actions")
    c.module.ui_tree()
    check(c.kinds["##mvl_attr_entry0_5_in_action_1_0"]=="selected" and c.kinds["##mvl_attr_entry0_5_in_action_0_0"]~="selected","Identical inner indices do not highlight both branches")
    s.values.testScalar="draft"
    check(not c.module.inspect(first) and s.selected.embeddedPath[1].index==1,"Unsaved child fields block switching branches without losing drafts")
    s.values.testScalar="2"
    local t=clone(first);c.module.inspect(t);t.embeddedPath[1].index=99
    check(request(c).target.embeddedPath[1].index==0,"Queued target owns an independent copy of all path hops")
    c:respond({ok=true,fields={}})
    check(s.selected.embeddedPath[1].index==0,"Later mutation of caller target cannot change selection")
end

do
    local c=setup();local s=c.module.state
    local opens=0;c.env.imgui.set_next_item_open=function(value)if value then opens=opens+1 end end
    s.structure_filter="TypeRibbonFollow";c.module.ui_tree()
    check(seen(c,"##mvl_attr_entry1_5_in_action_0_0") and not seen(c,"##mvl_attr_entry0_5_in_action_0_0"),"Searching nested attribute type reveals only matching nested branches")
    check(opens>=4,"Nested search opens file Action PlayEmitter and Entry ancestors")
    c.drawn_buttons={};s.structure_filter="234_PLG_trail";c.module.ui_tree()
    check(seen(c,"##mvl_attr_entry0_13_in_action_0_0") and not seen(c,"##mvl_attr_entry1_5_in_action_0_0"),"Searching embedded Entry name exposes its attributes without unrelated siblings")
end

do
    local c=setup();local s=c.module.state
    select(c,target(c,0));c.module.ui_fields()
    check(not seen(c,"##mvl_restore_entry") and not seen(c,"##mvl_duplicate") and not seen(c,"##mvl_remove"),"Nested Entry never exposes root-only restore duplicate or delete")
    s.attribute_type="Life";click_fields(c,"##mvl_add_attribute")
    check(request(c).action=="add_attribute" and request(c).target.embeddedPath[1].kind=="action" and request(c).target.attributeIndex==nil,"Add attribute preserves nested owner scope")
    c:respond({ok=true});check(s.selected==nil,"Structural mutation clears stale nested selection")
    select(c,target(c,1,13));s.confirm_delete=true;click_fields(c,"##mvl_remove")
    check(request(c).action=="remove_attribute" and request(c).target.index==1 and request(c).target.attributeIndex==13 and request(c).target.embeddedPath[1].index==0,"Nested attribute deletion cannot target root Entry or parent PlayEmitter")
    c:respond({ok=true});check(s.selected==nil,"Deleting a nested attribute clears its stale field panel")
end

do
    local c=setup();local s=c.module.state
    local path="expressions.expressions[0].components[0].data.value"
    select(c,target(c,0,2),{ok=true,fields={{path=path,value="1",editable=true}},expressionCurves={{
        index=0,channel="scale",assignment="Assign",formula="Lerp(1, 2, t)",axis="TIME",xMin=0,xMax=1,samples={1,1.5,2},
        values={{path=path,label="Lerp.from",value="1"}}}}})
    local lines=0
    c.env.imgui.get_cursor_screen_pos=function()return {x=0,y=0}end
    c.env.imgui.get_window_draw_list=function()return {add_line=function()lines=lines+1 end}end
    c.module.ui_fields()
    check(#s.expression_curves==1 and lines>0,"Selected embedded Transform3DExpression renders its returned expression plot")
    s.values[path]="3";click_fields(c,drawn(c,"##mvl_expression_set_"..path))
    check(request(c).action=="set" and request(c).target.attributeIndex==2 and request(c).target.embeddedPath[1].index==0,"Expression coefficient commits retain the nested Transform3DExpression target")
end

do
    local c=setup();local fields,channels={},{}
    for i,component in ipairs({"R","G","B","A"}) do
        fields[i]={path="Tint."..component,value="255",type="Byte",editable=true}
        channels[i]={path="Tint."..component,component=component,value=255}
    end
    select(c,target(c,1,13),{ok=true,fields=fields,colorGroups={{path="Tint",label="颜色",storage="rgba8",scale=255,editable=true,channels=channels}}})
    c.module.ui_fields();c.module.state.values["Tint.R"]="128"
    click_fields(c,drawn(c,"##mvl_color_set_"))
    check(request(c).action=="set_color" and request(c).values[1]==128 and request(c).target.index==1 and request(c).target.embeddedPath[1].index==0,"Grouped RGBA submission targets the selected embedded RgbCommon")
end

do
    local c=setup()
    select(c,target(c,0,2),{ok=true,fields={},curves={{path="clipData",editable=true,channels={{index=0,valueType="Float",keys={
        {index=0,time="0",value="1",interpolation="Type2"}}}}}}})
    c.module.state.curve_value="8";click_fields(c,"##mvl_curve_update")
    check(request(c).action=="curve_key" and request(c).value=="8" and request(c).target.attributeIndex==2 and request(c).target.embeddedPath[1].index==0,"Keyframe edit preserves the embedded attribute target")
end

do
    local c=setup();local root=c.module.state.document.actions[1].attributes[1]
    local nested=clone(root);nested.target={kind="entry",index=0,attributeIndex=14,embeddedPath={{kind="action",index=0,attributeIndex=0}}};nested.index=14
    for _,node in ipairs(nested.embedded.entries) do
        local chain={{kind="action",index=0,attributeIndex=0},{kind="entry",index=0,attributeIndex=14}}
        node.target.embeddedPath=clone(chain)
        for _,attribute in ipairs(node.attributes) do attribute.target.embeddedPath=clone(chain) end
    end
    root.embedded.entries[1].attributes[15]=nested
    c.buttons["##mvl_attr_entry1_13_in_action_0_0__entry_0_14"]=true;c.module.ui_tree()
    check(#request(c).target.embeddedPath==2 and request(c).target.embeddedPath[2].attributeIndex==14,"Recursive embedded PlayEmitter buttons carry every path hop")
    c:respond({ok=true,fields={}});click_fields(c,"##mvl_parent_target");c:respond({ok=true,fields={}})
    click_fields(c,"##mvl_parent_target")
    check(request(c).target.kind=="entry" and request(c).target.attributeIndex==14 and #request(c).target.embeddedPath==1,"Recursive Back removes exactly one embedded hop")
end

do
    local c=setup();local id=0
    local function ai(t)
        id=id+1;c.files["MotionVfxLive/ai-request.json"]={protocol=1,id="nested-"..id,action="inspect",arguments={target=t,expectedRevision=c.module.state.document.revision},expiresAtUnixSeconds=c.time+90}
        c:frame()
    end
    ai(target(c,1,13));check(request(c).target.embeddedPath[1].kind=="action","AI inspect preserves the same embedded target contract")
    c:respond({ok=true,fields={}})
    local r=c.files["MotionVfxLive/ai-response.json"]
    check(r.ok and r.result.target.embeddedPath[1].index==0,"AI inspection response includes full nested identity")
    local malformed=clone(target(c,0,2));malformed.embeddedPath[1].attributeIndex=nil;ai(malformed)
    r=c.files["MotionVfxLive/ai-response.json"]
    check(not r.ok and c.module.state.pending==nil,"Malformed embedded hop is rejected before backend dispatch")
    malformed=clone(target(c,0,2));malformed.kind="group";ai(malformed)
    check(not c.files["MotionVfxLive/ai-response.json"].ok,"Nested group note cannot accidentally route to top-level group metadata")
end
print("RESULT "..count.." embedded EFX UI checks passed; no game calls")
