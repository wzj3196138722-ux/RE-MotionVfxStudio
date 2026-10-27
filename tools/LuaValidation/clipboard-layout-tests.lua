-- Production D2D command stream, rendered separately for visual inspection.
local project,output=arg[1],arg[2]
local saved=arg
arg={project,output,"export-environment"}
local setup=assert(loadfile(project.."/tools/LuaValidation/d2d-workbench-tests.lua"))()
arg=saved
local c=setup();c.module.poll();c.bench.page="edit"
local s=c.module.state
s.document={name="11_it02_000.efx.5571972",revision=8,version=5571972,attributeTypes={"Life","RgbCommon"},
    groups={{index=0,name="group0",displayName="鬼人化 · 开启",entries={0}},{index=1,name="group1",displayName="连斩",entries={1}}},
    entries={{index=0,name="haruka_gpup",attributes={{index=0,type="Life"},{index=1,type="RgbCommon"}}},
        {index=1,name="blade_slash",attributes={{index=0,type="Life"}}}},actions={}}
s.selected={kind="entry",index=0,attributeIndex=0}
s.fields={{path="KeepFrame.r",value="60",editable=true,type="System.Int32"},{path="KeepFrame.s",value="0",editable=true,type="System.Int32"}}
s.values={['KeepFrame.r']="60",['KeepFrame.s']="0"}
s.copy_buffer={id="attribute",kind="attribute",name="Life",attributeType="Life",sourceName="11_it02_037.efx.5571972",version=5571972}
c:draw();c:click("替换同类型属性…##mvl_paste_efx");c:draw()
local count=0
for _,item in ipairs(c.bench.widgets.commands)do
    if item.kind=="text" and (item.text:find("确认替换属性",1,true) or item.text:find("复制此属性",1,true) or item.text:find("来源：",1,true))then count=count+1 end
end
if output then c:export("clipboard-attribute")end
assert(count>=3,"D2D renders copy/source/replacement controls above fields ("..count..")")
s.selected={kind="group",index=1};s.fields={};s.values={}
s.copy_buffer={id="entry",kind="entry",name="haruka_gpup",sourceName="11_it02_037.efx.5571972",version=5571972}
c:draw();if output then c:export("clipboard-entry")end
print("PASS: D2D clipboard layout and replacement controls")
