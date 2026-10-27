-- Exercise visible clipboard controls through the real editor, without a game.
local project=arg[1]
local saved=arg
arg={project.."/game-plugin/MotionVfxLive.lua",nil,nil,"export-environment"}
local environment=assert(loadfile(project.."/tools/LuaValidation/live-ui-tests.lua"))()
arg=saved
local n=0
local function check(value,name)assert(value,name);n=n+1;print("PASS "..name)end
local function setup()
    local c=environment();c.module.poll()
    c.module.state.document=assert(loadfile(project.."/tools/LuaValidation/playemitter-ui-fixture.lua"))()
    c.module.state.document.groups={{index=0,name="group0",displayName="开启",entries={1}}}
    return c,c.module.state
end
local function click(c,id)c.buttons["##mvl_"..id]=true;c.module.ui_fields()end
local function request(c)return c.files["MotionVfxLive/request.json"]end
local function response(c,body)
    body=body or {ok=true};body.copyBuffer=body.copyBuffer or c.module.state.copy_buffer;c:respond(body)
end
local function select(c,target)
    check(c.module.inspect(target),"Selection accepts exact target");response(c,{ok=true,fields={}})
end
local function text(c)return table.concat(c.messages,"|")end
local function seen(c,id)
    for _,b in ipairs(c.drawn_buttons)do if b.id=="##mvl_"..id then return true end end
end
local function entry(index,attribute)return {kind="entry",index=index or 1,attributeIndex=attribute}end
local ec={id="entry-copy",kind="entry",name="源块",sourceName="source.efx.5571972",version=5571972}
local ac={id="attribute-copy",kind="attribute",name="Life",attributeType="Life",sourceName="source.efx.5571972",version=5571972}

do
    local c,s=setup();c.module.ui_fields()
    check(seen(c,"copy_entry") and seen(c,"copy_attribute") and text(c):find("先选择来源并复制",1,true),"Copy controls and first step are visible before selection")
    local old=request(c);click(c,"copy_entry");check(request(c)==old,"Disabled copy cannot submit a request without a source")
    select(c,entry(1,6));click(c,"copy_entry")
    check(request(c).action=="copy_efx" and request(c).target.index==1 and request(c).target.attributeIndex==nil,"Copy block from an attribute selects its owner")
    response(c,{ok=true,copyBuffer=ec});c.module.ui_fields()
    check(text(c):find("source.efx",1,true) and text(c):find("Group [0] 开启",1,true),"Copied source and exact destination Group are visible")
    select(c,{kind="group",index=0});click(c,"paste_efx")
    check(request(c).action=="paste_efx" and request(c).target.kind=="group" and request(c).clipboardId==ec.id,"Paste routes to the selected native Group")
    s.structure_filter="filtered";s.field_filter="Life";s.field_modified_only=true
    response(c,{ok=true,selection=entry(2),message="已粘贴新特效块并选中"})
    check(request(c).action=="inspect" and request(c).target.index==2 and s.structure_filter=="" and s.field_filter=="" and not s.field_modified_only and s.copy_reveal,"Paste automatically reveals and inspects the inserted block")
    response(c,{ok=true,fields={}});c.module.ui_tree();check(not s.copy_reveal,"Tree reveal is a single frame, not permanent forced expansion")
    c.module.ui_fields();check(text(c):find("已粘贴新特效块",1,true),"Success remains visible after automatic inspection")
    click(c,"clear_efx_copy");c:respond({ok=true});check(s.copy_buffer==nil and s.copy_notice==nil,"Clear clipboard removes its summary and notice")
end
do
    local c,s=setup();select(c,entry(1,6));click(c,"copy_attribute")
    check(request(c).target.attributeIndex==6,"Copy attribute retains the exact source index")
    response(c,{ok=true,copyBuffer=ac});select(c,entry(2));click(c,"paste_efx")
    check(request(c).action=="inspect" and seen(c,"confirm_paste_attribute"),"Existing same-type attribute requires an explicit replacement confirmation")
    click(c,"cancel_paste_attribute");c.drawn_buttons={};c.module.ui_fields();check(not seen(c,"confirm_paste_attribute"),"Cancel closes confirmation without mutation")
    click(c,"paste_efx");click(c,"confirm_paste_attribute")
    check(request(c).action=="paste_efx" and request(c).replace and request(c).replaceAttributeIndex==6 and request(c).target.index==2,"Confirm replaces the exact same-type target attribute")
    response(c,{ok=false,error="验证失败"});check(s.copy_buffer.id==ac.id and s.selected.index==2 and s.copy_notice:find("粘贴失败",1,true),"Paste failure keeps both clipboard and destination with a readable error")
    select(c,entry(0));click(c,"paste_efx")
    check(request(c).action=="paste_efx" and request(c).replace==false and request(c).target.index==0,"Missing attribute is inserted directly without overwrite confirmation")
end
do
    local c,s=setup();s.copy_buffer=ac;select(c,entry(1));click(c,"paste_efx")
    select(c,entry(2));c.drawn_buttons={};c.module.ui_fields();check(not seen(c,"confirm_paste_attribute"),"Changing destination cancels stale replacement confirmation")
    s.fields={{path="life",value="1",editable=true}};s.values.life="2"
    local old=request(c);click(c,"copy_attribute");click(c,"paste_efx")
    check(request(c)==old and text(c):find("有未提交的参数",1,true),"Unsubmitted field edits cannot be silently lost by copy or paste")
end
do
    local c,s=setup();s.copy_buffer=ac
    local t=s.document.actions[1].attributes[1].embedded.entries[2].attributes[7].target
    select(c,t);click(c,"copy_attribute")
    check(request(c).target.embeddedPath[1].kind=="action" and request(c).target.attributeIndex==6,"Nested Action attribute copy preserves full path")
    response(c,{ok=true,copyBuffer=ac});click(c,"paste_efx");click(c,"confirm_paste_attribute")
    check(request(c).target.embeddedPath[1].attributeIndex==0 and request(c).target.index==1 and request(c).replaceAttributeIndex==6,"Nested replacement cannot resolve to a same-index root block")
end
print("PASS: "..n.." clipboard UI checks")
