-- Cache correctness and complexity checks; no game, network or engine writes.
local project=arg[1]
local checks=0
local function check(value,label)assert(value,label);checks=checks+1;print("PASS "..label)end
local widgets=assert(loadfile(project.."/game-plugin/MotionVfxLive/fx_d2d_widgets.lua"))()
local measured=0
local function font(factor)
    return {measure=function(_,text)
        measured=measured+1
        local line,width,rows=0,0,1
        for _,cp in utf8.codes(text) do
            if cp==10 then width=math.max(width,line);line=0;rows=rows+1
            else line=line+(cp>255 and 2 or 1)*factor end
        end
        return math.max(width,line),rows*factor*2
    end}
end
local api={Font={new=function(_,size)return font(size/2)end},Image={new=function()end}}
local u=widgets.new({},api);u:init()
local function reference(text,maxWidth,f)
    local s=text
    if f:measure(s)>maxWidth then
        local offsets={};for pos in utf8.codes(s) do offsets[#offsets+1]=pos end
        while #offsets>0 and f:measure(s.."…")>maxWidth do s=s:sub(1,table.remove(offsets)-1) end
        s=s.."…"
    end
    return s
end
local cases={"", "plain", "中文属性 / TypeRibbonFollow / 挂点 L_Toe", "first line\nsecond longer line 中文",
    "😀组合 / e\204\129 / 参数路径 / expressions.expressions[24].components[1].data.value",string.rep("长路径 LongPath / ",60)}
for _,size in ipairs({6,14,19.5,44,52}) do
    local f=u.fonts[math.floor(size+.5)] or u.fonts[15]
    for _,text in ipairs(cases) do for _,width in ipairs({1,10,40,160,500,10000}) do
        local expected=reference(text,width,f)
        assert(u:fit_text(text,size,width,f)==expected,"Changed longest-fitting UTF-8 prefix")
    end end
end
check(true,"Binary trimming matches the original Unicode output across widths font sizes and multiline labels")
local long=string.rep("中文VeryLongParameter/",100)
measured=0;u:fit_text(long,15,179,u.fonts[15])
check(measured<=16,"First-use long-path clipping needs logarithmic font measurements")
measured=0
for _=1,100 do u:fit_text(long,15,179,u.fonts[15]);u:measure("cached label",15)end
check(measured==1,"Repeated text fit and measurement reuse immutable results")
local before=u:measure("replace font",15)
u.fonts[15]=font(13)
check(u:measure("replace font",15)~=before,"Replacing a font invalidates its existing metrics")
local narrow=u:fit_text(long,15,150,u.fonts[15])
local wide=u:fit_text(long,15,400,u.fonts[15])
check(#wide>#narrow,"Resizing recomputes clipping for the new width")
local fail=true;local calls=0
local recovering={measure=function(_,text)calls=calls+1;if fail then error("transient")end;return #text,10 end}
u:fit_text("recoverable font",15,50,recovering);local old=calls;fail=false
check(u:fit_text("recoverable font",15,50,recovering)=="recoverable font" and calls>old,"A transient measurement failure cannot freeze fallback clipping in cache")
local nan={measure=function()return 0/0,math.huge end}
local w,h=u:measure("finite fallback",15,nan)
check(w==w and h==h and w<math.huge and h<math.huge,"Invalid native font metrics never produce cached NaN or infinite dimensions")
for i=1,5000 do u:measure("varying "..i,15);u:fit_text("varying "..i,15,25,u.fonts[15])end
local function count(t)local n=0;for _ in pairs(t)do n=n+1 end;return n end
check(count(u.measure_cache.values)<=4096 and count(u.fit_cache.values)<=4096,"Dynamic labels cannot grow font or clipping caches without bound")
u:init();check(next(u.measure_cache.values)==nil and next(u.fit_cache.values)==nil,"D2D reinitialization releases all old font metrics")
u:begin(1);u.clips={{x=10,y=10,w=100,h=100}};u:rect(20,20,50,10)
u.clips[2]={x=30,y=30,w=20,h=20};u:rect(30,30,100,10)
check(u.commands[2].w==20,"Nested clip intersection constrains emitted geometry")
u.clips[2]=nil;u:rect(30,30,100,10)
check(u.commands[3].w==80,"Popping a nested clip restores its parent's bounds")
u:begin(1);u:rect(0,0,100,100)
check(#u.commands==1 and u.commands[1].w==100,"A new frame cannot inherit the prior frame's clipping")

local parameters=assert(loadfile(project.."/game-plugin/MotionVfxLive/fx_parameter_ui.lua"))()
local translations=0;local chinese=parameters.chinese
parameters.chinese=function(...)translations=translations+1;return chinese(...)end
local zh=parameters.label("TypeRibbonFollow","zh")
check(parameters.label("TypeRibbonFollow","en")=="TypeRibbonFollow" and zh~=parameters.label("TypeRibbonFollow","en"),"Translation cache separates Chinese and English labels")
parameters.search("TypeRibbonFollow","拖尾");translations=0
for _=1,50 do parameters.label("TypeRibbonFollow","zh");parameters.search("TypeRibbonFollow","拖尾")end
check(translations==0,"Stable labels and search text do not repeat translation work")
check(parameters.search("TypeRibbonFollow","ribbon") and not parameters.search("TypeRibbonFollow","nevermatches"),"Search cache stores text rather than a stale filter result")
check(parameters.label("Lerp.起始值 from","en")=="Lerp.from","Legacy bilingual arguments retain the original English normalization")

local saved=arg;arg={project.."/game-plugin/MotionVfxLive.lua",nil,nil,"export-environment"}
local environment=assert(loadfile(project.."/tools/LuaValidation/live-ui-tests.lua"))();arg=saved
local c=environment();c.module.poll();local s=c.module.state
s.document=assert(loadfile(project.."/tools/LuaValidation/playemitter-ui-fixture.lua"))()
s.selected=s.document.actions[1].attributes[1].embedded.entries[1].target
s.fields={{path="Size.s",type="System.Single",ownerType="via.Range",value="1",editable=true},{path="Size.r",type="System.Single",ownerType="via.Range",value="0",editable=true}}
s.values={["Size.s"]="1",["Size.r"]="0"};s.field_modified_only=true
local module=c.env.package.loaded["MotionVfxLive.fx_parameter_ui"]
local grouped=0;local groups=module.groups;module.groups=function(...)grouped=grouped+1;return groups(...)end
c.module.ui_fields();check(#s.field_view==0,"Modified-only view starts empty for unchanged grouped fields")
s.values["Size.r"]="8"
for _=1,20 do c.module.ui_fields()end
check(#s.field_view==1 and #s.field_view[1].fields==2 and grouped==1,"Changing drafts updates the view without rebuilding immutable parameter groups")
c.module.set_parameter_language("en");c.module.ui_fields()
check(s.values["Size.r"]=="8" and grouped==1,"Changing language preserves drafts and grouping references")
s.values["Size.r"]="0";c.module.ui_fields()
check(#s.field_view==0,"Reverting a draft immediately removes its modified-only row")
s.fields={s.fields[1],s.fields[2]};c.module.ui_fields()
check(grouped==2,"A fresh inspect response invalidates cached field group membership")
s.structure_filter="definitely_absent";c.module.ui_tree()
local node=s.document.actions[1].attributes[1].embedded.entries[1]
node.name="definitely_absent";s.document.revision=s.document.revision+1;c.drawn_buttons={};c.module.ui_tree()
local found=false;for _,item in ipairs(c.drawn_buttons)do if item.id=="##mvl_attr_entry0_5_in_action_0_0" then found=true end end
check(found,"Document revision changes invalidate cached negative nested search matches")
-- Exercise the actual main-script wrapping function through its D2D text
-- adapter, including continuously changing widths and font recovery.
local wrap,rendered
local metric_factor,reliable,identity=7,true,{}
local metrics=0
local function width_of(text)
    local width=0
    for _,cp in utf8.codes(text) do width=width+(cp>255 and 2 or 1)*metric_factor end
    return width
end
local adapter={_mvl_wrapped_text=true,
    draw_wrapped_text=function(_,_,fn)wrap=fn end,
    get_default_font_size=function()return 15 end,
    get_text_metrics_identity=function()return identity end,
    calc_text_size=function(text)metrics=metrics+1;return {x=width_of(text),y=15,reliable=reliable}end}
s.selected=nil;s.document=nil;c.module.with_ui(adapter,c.module.ui_fields)
local function wrapped_at(text,width)
    c.module.with_ui(adapter,function()rendered=wrap(text,width)end);return rendered
end
local function original_wrap(value,width)
    local lines={}
    for line in (value..'\n'):gmatch('(.-)\n') do
        while line~='' and width_of(line)>width do
            local offsets={};for pos in utf8.codes(line)do offsets[#offsets+1]=pos end;offsets[#offsets+1]=#line+1
            local low,high,fit=1,#offsets-1,1
            while low<=high do
                local mid=math.floor((low+high)/2)
                if width_of(line:sub(1,offsets[mid+1]-1))<=width then fit,low=mid,mid+1 else high=mid-1 end
            end
            lines[#lines+1]=line:sub(1,offsets[fit+1]-1);line=line:sub(offsets[fit+1])
        end
        lines[#lines+1]=line
    end
    return table.concat(lines,'\n')
end
for _,value in ipairs({'', 'A', '中文名称 / Long.Parameter.Field', 'line one\n\n中文 😀 e\204\129 tail',string.rep('LongPath中文.',25)}) do
    for width=1,450 do assert(wrapped_at(value,width)==original_wrap(value,width),'Range cache changed exact wrap at '..width)end
end
check(true,'Width intervals preserve exact original wrapping for 2250 continuously resized Unicode/multiline cases')
local value=string.rep('uniform',20);wrapped_at(value,140);metrics=0
for width=141,146 do wrapped_at(value,width)end
check(metrics==0,'One-pixel resize reuses exact wrapping until another glyph fits')
reliable=false;metric_factor=50;local temporary=wrapped_at('recoverable main label',150)
reliable=true;metric_factor=7
check(wrapped_at('recoverable main label',150)==original_wrap('recoverable main label',150) and wrapped_at('recoverable main label',150)~=temporary,
    'Main wrapping never retains a temporary fallback font measurement')
local previous=wrapped_at('font rebuild label 中文',150);identity={};metric_factor=14
check(wrapped_at('font rebuild label 中文',150)~=previous,'Rebuilding D2D fonts invalidates wrapping even at identical UI size')

-- Batch styling must unwind even if native InputText throws, and must not be
-- accidentally enabled by old ImGui tables with a permissive __index.
local old=arg;arg={project,nil,'export-environment'}
local setup=assert(loadfile(project..'/tools/LuaValidation/d2d-workbench-tests.lua'))();arg=old
local batch=setup();local w=batch.bench.widgets;local native=batch.env.imgui.input_text
batch.env.imgui.input_text=function()error('injected native input failure')end
local ok=pcall(function()w.adapter.with_input_batch(function()w:textbox('##error','draft',100)end)end)
check(not ok and not w.input_batch and not w.input_batch_styled and not w.input_batch_size and not w.input_batch_position,
    'Native input errors unwind the shared input style and geometry scope')
batch.env.imgui.input_text=native;batch:draw()
check(true,'The next full frame still renders after a failed batched input')
print("RESULT "..checks.." UI performance correctness checks passed; no game calls")
