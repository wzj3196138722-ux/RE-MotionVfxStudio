-- Deterministic offline UI cost sample; timings exclude the actual game/GPU.
local project=arg[1]
local saved=arg;arg={project,nil,"export-environment"}
local setup=assert(loadfile(project.."/tools/LuaValidation/d2d-workbench-tests.lua"))();arg=saved
local c=setup();c.module.poll();c.bench.page="edit"
c.module.state.document=assert(loadfile(project.."/tools/LuaValidation/playemitter-ui-fixture.lua"))()
c.module.state.structure_filter="TypeRibbonFollow" -- searches nested real Actions and opens ancestors
local metrics={measure=0,chinese=0,label=0,search=0}
for _,font in pairs(c.bench.widgets.fonts) do
    local previous=font.measure
    font.measure=function(self,...)metrics.measure=metrics.measure+1;return previous(self,...)end
end
local parameters=c.env.package.loaded["MotionVfxLive.fx_parameter_ui"]
if parameters then for _,key in ipairs({"chinese","label","search"}) do
    local previous=parameters[key]
    parameters[key]=function(...)metrics[key]=metrics[key]+1;return previous(...)end
end end
local function run(name,frames,draw)
    for key in pairs(metrics) do metrics[key]=0 end
    collectgarbage("collect");local before=collectgarbage("count");local start=os.clock()
    for _=1,frames do draw() end
    local elapsed=os.clock()-start;local after=collectgarbage("count")
    print(string.format("BENCH %s frames=%d ms/frame=%.4f measure/frame=%.2f translate/frame=%.2f label/frame=%.2f search/frame=%.2f heapDeltaKB=%.1f",
        name,frames,elapsed*1000/frames,metrics.measure/frames,metrics.chinese/frames,metrics.label/frames,metrics.search/frames,after-before))
end
c:draw()
run("real-document-search",50,function()c:draw()end)
c.module.state.structure_filter=""
c.module.state.selected=c.module.state.document.actions[1].attributes[1].embedded.entries[1].attributes[3].target
c.module.state.fields={};c.module.state.values={}
for i=1,80 do
    local path="expressions.expressions["..i.."].components[0].data.value"
    c.module.state.fields[i]={path=path,label=path,value=tostring(i),editable=true,type="System.Single"}
    c.module.state.values[path]=tostring(i)
end
c:draw()
run("80-field-editor",50,function()c:draw()end)
local u=c.bench.widgets
local labels={}
for i=1,20 do labels[i]="234_ACT_PT / PlayEmitter / 234_PLG_trail [0] / Transform3DExpression / expressions.expressions["..i.."].components[0].data.value · 中文参数" end
local function long_labels()
    u:begin(1)
    for i,label in ipairs(labels) do u:label(label,0,i*20,15,nil,160) end
    u:render()
end
long_labels()
run("narrow-long-labels",50,long_labels)
