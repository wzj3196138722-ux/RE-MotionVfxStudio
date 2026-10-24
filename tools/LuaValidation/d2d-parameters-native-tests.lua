-- Production 1,104-field PlayEmitter page with real headless ImGui input.
-- No game, OS events, renderer, or backend writes.
local project=assert(arg[1]);local saved=arg
arg={project,'export-environment',arg[2]}
local context=assert(loadfile(project..'/tools/LuaValidation/d2d-parameters-native-bench.lua'))()
arg=saved
local c,N,frame,snapshots=context.c,context.N,context.frame,context.snapshots
local s=c.module.state
local checks=0
local function check(value,label)assert(value,label);checks=checks+1;print('PASS '..label)end
local function idle(count)for _=1,count or 1 do frame()end end
local function active()
    for id,snapshot in pairs(snapshots)do if snapshot.active then return id,snapshot end end
end
local function key(name,modifier)
    if modifier then N.dialog_key_event(modifier,true)end
    N.dialog_key_event(name,true);frame();N.dialog_key_event(name,false)
    if modifier then N.dialog_key_event(modifier,false)end
    frame()
end
local function click(id,fraction)
    idle(25) -- separate clicks from the preceding click/drag
    local r=assert(snapshots[id],'missing input '..id)
    local x,y=r.x+r.w*(fraction or .5),r.y+r.h/2
    frame(x,y,false);frame(x,y,true);frame(x,y,false)
end
local function visible_fields()
    local list={}
    for id,r in pairs(snapshots)do
        local top,bottom=r.window_position.y,r.window_position.y+r.window_size.y
        if id:find('##mvl_value_',1,true) and r.y>top+4 and r.y+r.h<bottom-4 then
            list[#list+1]={id=id,r=r}
        end
    end
    table.sort(list,function(a,b)return a.r.y==b.r.y and a.r.x<b.r.x or a.r.y<b.r.y end)
    return list
end
check(#s.fields==1104,context.response.fixtureKind..' contains 1,104 fields')
c.request_scroll=200;idle(3)
local visible=visible_fields()
check(#visible>0,'production page exposes native parameter inputs')
local id=visible[1].id
local path=id:match('##mvl_value_(.*)')
local original=s.values[path]
check(original~=nil,'native field identity maps to the serialized parameter path')
click(id,.9)
check(active()==id,'mouse press/release activates the actual parameter input')
key('End')
N.text_event('23');frame()
check(s.values[path]==original..'23' and c.module.has_drafts(),'native typing updates the unsubmitted parameter draft')
local function caret(label)
    local current,r=active()
    check(current==id and r.caret_ink>0,label..': native caret is visible in the active field')
    check(math.abs((r.caret_minx+r.caret_maxx)/2-r.cursor_x)<1.6,label..': actual caret vertices match insertion position')
    check(r.cursor==#s.values[path],label..': insertion remains at the end of the draft')
    for _,cmd in ipairs(c.bench.widgets.commands)do
        assert(not(cmd.kind=='rect' and cmd.w==1 and cmd.color==c.widgets.colors.cyan),'duplicate D2D caret')
    end
end
caret('after typing')
c.request_scroll=2500;idle(3)
check(active()==id,'offscreen active field remains submitted to native ImGui')
check(s.values[path]==original..'23','scrolling away preserves the draft')
c.request_scroll=200;idle(3)
caret('after scrolling back')
local next_id,next_y
for candidate,r in pairs(snapshots)do
    if candidate:find('##mvl_value_',1,true) and r.y>snapshots[id].y and (not next_y or r.y<next_y)then next_id,next_y=candidate,r.y end
end
key('Tab')
check(active()==next_id,'Tab reaches the next native editable parameter across read-only rows')
key('Tab','LeftShift')
check(active()==id,'Shift+Tab returns to the previous parameter')
check(s.values[path]==original..'23','keyboard focus traversal preserves the unsubmitted draft')
key('End')
caret('after keyboard traversal')
-- Geometry can change while a native input owns focus; its cursor must remain
-- in the same coordinates as its editable text, including callback-resize.
local oldx,oldy=snapshots[id].x,snapshots[id].y
c.bench.window_position={x=.35,y=.35};idle(2)
check(snapshots[id].x~=oldx and snapshots[id].y~=oldy,'moving the workbench updates actual native input geometry')
caret('after position change')
local oldw=snapshots[id].w
c.bench.window_size={w=1480,h=900};idle(2)
check(snapshots[id].w>oldw,'resizing the workbench updates the actual native input width')
caret('after width change')
-- Exercise the real title/edge hit areas, not only assigning layout state.
local b=c.bench.bounds;local bx,by=b.x,b.y
frame(bx+80,by+50,false);frame(bx+80,by+50,true)
frame(bx+125,by+75,true);frame(bx+125,by+75,false);idle(2)
check(math.abs(c.bench.bounds.x-bx-45)<2 and math.abs(c.bench.bounds.y-by-25)<2,'native title drag moves the full workbench')
check(s.values[path]==original..'23','title dragging preserves the draft after focus leaves the input')
click(id,.9);key('End');caret('after title drag and refocus')
b=c.bench.bounds;local bw,bh=b.w,b.h
local ex,ey=b.x+b.w-5,b.y+b.h-5
frame(ex,ey,false);frame(ex,ey,true);frame(ex+25,ey+20,true);frame(ex+25,ey+20,false);idle(2)
check(c.bench.bounds.w>bw+20 and c.bench.bounds.h>bh+15,'native corner drag resizes the workbench')
check(s.values[path]==original..'23','edge resizing preserves the unsubmitted draft')
click(id,.9);key('End');caret('after edge resize and refocus')
-- Verify click-to-glyph placement on the production parameter input, rather
-- than accepting a visually plausible caret at the wrong insertion offset.
c.measure_request={id=id,text=s.values[path]:sub(1,1)};frame();c.measure_request=nil
local r=snapshots[id];local x=r.x+r.padding+assert(c.measured)+.1
idle(25);frame(x,r.y+r.h/2,false);frame(x,r.y+r.h/2,true);frame(x,r.y+r.h/2,false)
check(snapshots[id].cursor==1,'clicking the displayed first glyph places the native insertion cursor at byte one')
local prior=s.values[path];N.text_event('9');frame()
check(s.values[path]==prior:sub(1,1)..'9'..prior:sub(2),'typing after a glyph click inserts at the clicked position')
check(snapshots[id].cursor==2,'insertion advances the real caret without jumping visually to the start')
key('End');caret('after glyph insertion')
check(c.files['MotionVfxLive/request.json']==nil,'interaction-only test never submits a backend mutation')
print('Native parameter interaction checks: '..checks)
