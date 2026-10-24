-- Main-loop snapshot scheduling with the real editor and isolated engine mocks.
local project=assert(arg[1]);local saved=arg
arg={project.."/game-plugin/MotionVfxLive.lua",nil,nil,"export-environment"}
local environment=assert(loadfile(project.."/tools/LuaValidation/live-ui-tests.lua"))();arg=saved
local passed,checks=0,0
local function check(value,label)checks=checks+1;assert(value,label)end
local function test(name,fn)fn();passed=passed+1;print("PASS "..name)end
local function copy(v)if type(v)~="table"then return v end;local r={};for k,x in pairs(v)do r[k]=copy(x)end;return r end
local function cell(fn,key)
    for i=1,100 do local name=debug.getupvalue(fn,i);if not name then break end;if name==key then return i end end
    error("missing upvalue "..key)
end
local function setup(with_workbench)
    local c=environment();local s=c.module.state
    -- The shared fixture advances one second per frame() call. This test owns
    -- wall time explicitly so sixty frames can represent one second.
    local frame=c.frame
    c.frame=function(self,count)self.time=self.time-1;return frame(self,count)end
    c.time=100;s.last_diagnostic=100
    c.counts={full=0,light=0,bindings=0}
    c.capture_state.currentActions={{motion={motionId=37,frame=0}}}
    c.capture_state.actionStatus="current"
    c.capture_state.resources={{path="old.efx",localConfirmed=true}}
    c.capture.snapshot=function()
        c.counts.full=c.counts.full+1
        if c.fail_full then error("full unavailable")end
        return copy(c.capture_state)
    end
    c.capture.current_snapshot=function()
        c.counts.light=c.counts.light+1
        if c.fail_light then error("light unavailable")end
        return {currentActions=copy(c.capture_state.currentActions),actionStatus=c.capture_state.actionStatus,
            sceneEpoch=c.capture_state.sceneEpoch,recording=c.capture_state.recording}
    end
    c.bindings.snapshot=function()c.counts.bindings=c.counts.bindings+1;return copy(c.binding_state)end
    s.window_hidden=true;s.active_tab="edit";s.module_frames=0
    if with_workbench then
        c.bench={active=true,page="edit",frame=function()end}
        debug.setupvalue(c.module.frame,cell(c.module.frame,"workbench"),c.bench)
    end
    c.module.refresh_action_state();c.counts={full=0,light=0,bindings=0}
    return c,s
end
test("hidden idle history refreshes every two seconds while current motion updates every frame",function()
    local c,s=setup(true)
    for i=1,600 do
        c.time=100+math.floor(i/60);c.capture_state.currentActions[1].motion.frame=i
        if i==61 then c.capture_state.resources[2]={path="late.efx",localConfirmed=true}end
        c:frame()
        check(s.capture_snapshot.currentActions[1].motion.frame==i,"latest current motion on every frame")
        if i==119 then check(#s.capture_snapshot.resources==1,"history waits for scheduled full refresh")end
        if i==120 then check(#s.capture_snapshot.resources==2,"stopped async source arrives on diagnostic refresh")end
    end
    check(c.counts.full==5,"600 frames over ten seconds require five full snapshots")
    check(c.counts.light==600,"capture sampling is not reduced")
    check(c.counts.bindings==45,"binding progress keeps forty regular reads plus five diagnostic reads")
    print("PERF hidden_600_frames full_snapshots_before=45 full_snapshots_after="..c.counts.full.." light_reads="..c.counts.light)
end)
test("hidden recording avoids frequent history copies while pending verification retains its cadence",function()
    for _,recording in ipairs({true,false})do
        local c,s=setup(true);c.capture_state.recording=recording
        if not recording then s.verification={phase="waiting",generatedPath="generated.efx",baseline={},seen={}}end
        for i=1,300 do c.time=100+math.floor(i/60);c:frame()end
        check(c.counts.full==(recording and 2 or 20),"recording uses diagnostics; verification retains twenty full reads in 300 frames")
        check(s.capture_snapshot.recording==recording,"recording flag remains live")
    end
end)
test("hidden recording retains every real capture-hook event and entering its list displays the full history",function()
    local fixture=assert(loadfile(project.."/game-plugin/tests/action-capture-tests.lua","t",
        setmetatable({arg={project.."/game-plugin/MotionVfxLive/fx_action_capture.lua","export-environment"}},{__index=_G})))()
    local recorded=fixture({track_motion=true}):start();recorded:update()
    local c,s=setup(true)
    c.capture.current_snapshot=function()c.counts.light=c.counts.light+1;return recorded.module.current_snapshot()end
    c.capture.snapshot=function()c.counts.full=c.counts.full+1;return recorded.module.snapshot()end
    c.module.refresh_action_state();c.counts.full=0
    for i=1,175 do
        recorded.node1.methods.get_MotionFrame=i;recorded:update()
        local path="art/vfx/capture_refresh_"..((i-1)%17+1)..".efx"
        recorded:engine({path},{recorded:player(path)})
        c.time=100+math.floor(i/60);c:frame()
        check(s.capture_snapshot.currentActions[1].motion.frame==i,"real capture current frame stays live")
    end
    local captured=recorded.module.snapshot()
    check(#captured.events==175 and #captured.resources==17 and captured.recording,"all real hook events retained while hidden")
    check(c.counts.full==1 and #s.capture_snapshot.events==120,"hidden recording only copies history at two seconds")
    c.bench.page="effects";s.window_hidden=false;c.show_ui=true;c:frame()
    check(c.counts.full==2 and #s.capture_snapshot.events==175,"entering the list immediately exposes all 175 events")
end)
test("workbench list entry refreshes immediately and covered closed or hidden pages avoid full history",function()
    local c,s=setup(true);s.window_hidden=false;c.show_ui=true
    c:frame(30);check(c.counts.full==0,"edit page does not rebuild full capture history")
    c.bench.page="effects";c:frame();check(c.counts.full==1,"entering effect list refreshes before rendering")
    c:frame(14);check(c.counts.full==2,"visible list retains normal cadence")
    c.bench.top="settings";c:frame(30);check(c.counts.full==2,"top workspace masks history page")
    c.bench.top=nil;c.bench.page="actions";c:frame();check(c.counts.full==3,"entering action list refreshes immediately")
    s.window_hidden=true;c:frame(30);check(c.counts.full==3,"hidden list does not copy history")
    s.window_hidden=false;s.window_open=false;c:frame(30);check(c.counts.full==3,"closed list does not copy history")
    s.window_open=true;c.show_ui=false;c:frame(30);check(c.counts.full==3,"noninteractive orb does not copy history")
end)
test("fallback ImGui list visibility and old capture modules retain compatible behavior",function()
    local c,s=setup(false);c.show_ui=true;s.window_hidden=false
    c:frame(15);check(c.counts.full==0,"fallback editor is not a history consumer")
    s.active_tab="actions";c:frame();check(c.counts.full==1,"fallback action tab refreshes immediately")
    c.capture.current_snapshot=nil;s.window_hidden=true;s.active_tab="edit";s.module_frames=0;c.counts.full=0
    c:frame(150);check(c.counts.full==10,"old capture API retains fifteen-frame full refresh")
end)
test("scene changes replace history immediately and a failed full read cannot retain prior ownership",function()
    local c,s=setup(true)
    c.capture_state.sceneEpoch=2;c.capture_state.actor.sceneEpoch=2
    c.capture_state.resources={{path="new.efx",localConfirmed=true}}
    c:frame();check(c.counts.full==1,"scene change bypasses cadence")
    check(s.capture_snapshot.resources[1].path=="new.efx" and s.capture_snapshot.actor.sceneEpoch==2,"new scene's complete snapshot replaces old history")
    c.capture_state.sceneEpoch=3;c.fail_full=true;c:frame()
    check(c.counts.full==2 and #s.capture_snapshot.resources==0 and #s.capture_snapshot.events==0,"failed scene refresh clears old resource and event evidence")
    check(s.capture_snapshot.actor.available==false and s.capture_error~=nil,"failed scene refresh reports unavailable ownership")
    c.fail_full=false;c.time=102;c:frame()
    check(c.counts.full==3 and s.capture_error==nil,"scheduled full refresh recovers after the failure")
end)
test("light read failures preserve full-refresh fallback and explicit operations never use lightweight authorization",function()
    local c,s=setup(true);c.fail_light=true
    c:frame(15);check(c.counts.full==1,"failed lightweight reads fall back on the original cadence")
    c.module.refresh_action_state();c.module.refresh_action_state()
    check(c.counts.full==3,"explicit refresh calls remain full and are not coalesced")
    c.module.capture_command("stop")
    check(c.counts.full==5,"capture stop still reads before and after the operation")
    c.time=102;c.module.diagnostics()
    check(c.counts.full==6,"diagnostics invoked outside the frame still refresh independently")
end)
print(("RESULT capture refresh %d passed, %d checks; mock engine only"):format(passed,checks))
