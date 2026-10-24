-- Production workbench/fields with a real ImGui frame and actual 1,104-field
-- PlayEmitter response. Offline only: no game, GPU, OS input or mailbox writes.
local project=assert(arg[1]);local mode=arg[2] or 'bench';local fixture_path=arg[3]
local saved=arg;arg={project,nil,'export-environment'}
local setup=assert(loadfile(project..'/tools/LuaValidation/d2d-workbench-tests.lua'))();arg=saved
local c=setup();local N=native_imgui
N.text_reset({1920,1080},16,{8,8},{32,32},1,1,'C:/Windows/Fonts/msyh.ttc')
local calls={};local input_snapshots={}
for key,fn in pairs(N) do
    if type(fn)=='function' then c.env.imgui[key]=function(...)
        calls[key]=(calls[key] or 0)+1
        if key=='input_text' then
            local changed,value,a,b=fn(...)
            local id=select(1,...)
            if N.is_item_active() or mode=='tests' or mode=='export-environment' then
                local snapshot=N.input_snapshot()
                snapshot.active=N.is_item_active();snapshot.focused=N.is_item_focused()
                snapshot.hovered=N.is_item_hovered()
                snapshot.window_position=N.get_window_pos();snapshot.window_size=N.get_window_size()
                input_snapshots[id]=snapshot
                if c.measure_request and id==c.measure_request.id then
                    c.measured=N.measure(c.measure_request.text,snapshot.font_size)
                end
            end
            return changed,value,a,b
        end
        return fn(...)
    end else c.env.imgui[key]=fn end
end
local native_begin=c.env.imgui.begin_child_window
c.env.imgui.begin_child_window=function(label,...)
    local visible=native_begin(label,...)
    if label:find('mvl_fields',1,true) then
        c.scroll_max=N.get_scroll_max_y();c.scroll_actual=N.get_scroll_y()
        if c.request_scroll then N.set_scroll_y(c.request_scroll) end
    end
    return visible
end
-- Widget adapters capture functions during construction. Recreate after the
-- native API substitution so geometry/scroll queries are not leftover mocks.
c.bench=c.factory.new(c.module,c.widgets,c.pfb_factory,c.dialog_factory)
c.module.workbench=c.bench
c.init()
local native_font_calls=0
for size,font in pairs(c.bench.widgets.fonts) do font.measure=function(_,text)
    native_font_calls=native_font_calls+1;return N.measure(text,size),size*1.4
end end
local fixture=fixture_path~='synthetic' and loadfile(fixture_path or project..'/tools/LuaValidation/test-output/native-ui-perf/playemitter-full-response.lua')
if fixture_path and fixture_path~='synthetic' then assert(fixture,'Cannot read the explicitly requested field fixture')end
local response
if fixture then response=fixture();response.fixtureKind='real PlayEmitter response'
else
    -- Portable metadata-only stress fixture. Real game data remains confined
    -- to excluded test-output; CI can exercise the same 1,104-field workload.
    response={fixtureKind='synthetic portable metadata',fields={},colorGroups={},
        document={source='portable.efx',revision=1,entries={},groups={},actions={{index=0,name='Benchmark',attributes={{index=0,type='PlayEmitter'}}}}}}
    for i=1,1104 do
        response.fields[i]={path='efxrData.Entries[0].parameters['..i..'].value',type='System.Single',value=tostring(i),editable=i%5~=0,
            reason=i%5==0 and 'Fixture read-only metadata' or nil}
    end
end
local s=c.module.state
c.module.poll();c.bench.page='edit'
s.document=response.document;s.selected={kind='action',index=0,attributeIndex=0}
s.fields=response.fields;s.color_groups=response.colorGroups;s.values={}
for _,field in ipairs(s.fields) do s.values[field.path]=tostring(field.value or '') end
local function frame(x,y,down)
    N.begin_frame(x or -100,y or -100,down or false)
    c:draw();N.end_frame()
end
for _=1,4 do frame()end
if mode=='export-environment' then return {c=c,frame=frame,N=N,response=response,snapshots=input_snapshots}end
local function bench(name,frames,update)
    calls={};native_font_calls=0;collectgarbage('collect')
    local times={};local total=0;local before=collectgarbage('count')
    for i=1,frames do
        if update then update(i)end
        local start=os.clock();frame();local elapsed=(os.clock()-start)*1000
        times[i]=elapsed;total=total+elapsed
    end
    table.sort(times)
    local crossings=0;for _,count in pairs(calls)do crossings=crossings+count end
    print(string.format('NATIVE %s fields=%d rows=%d frames=%d meanMs=%.4f p95Ms=%.4f calls/frame=%.1f input/frame=%.1f font/frame=%.2f heapKB=%.1f scrollMax=%.1f',
        name,#s.fields,#(s.field_view or {}),frames,total/frames,times[math.ceil(frames*.95)],crossings/frames,(calls.input_text or 0)/frames,native_font_calls/frames,collectgarbage('count')-before,c.scroll_max or 0))
end
bench('stationary',120)
bench('scroll',180,function(i)c.request_scroll=(i*31)%math.max(1,(c.scroll_max or 2000)-100)end)
c.request_scroll=1500
bench('drag',180,function(i)c.bench.window_position={x=.2+(i%80)/160,y=.2+(i%60)/120}end)
bench('resize',180,function(i)c.bench.window_size={w=1170+(i%120),h=790+(i%50)}end)
