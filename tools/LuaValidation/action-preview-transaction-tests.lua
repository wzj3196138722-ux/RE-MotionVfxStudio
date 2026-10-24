local factory=assert(loadfile(arg[1]))()
local passed=0
local function check(v,label)assert(v,label);passed=passed+1;print("PASS "..label)end
local function fixture()
    local c={values={motion="original-motion",pfb="original-pfb"},pins={},writes={},clock=0,ready=true}
    local function retain(v)c.pins[v]=(c.pins[v] or 0)+1;return v end
    local function release(v)c.pins[v]=assert(c.pins[v])-1;assert(c.pins[v]>=0,"release underflow")end
    local adapter={retain=retain,release=release,now=function()return c.clock end,
        resolve=function(plan)if c.context_changed then error("context changed")end;return {plan=plan}end,
        validate=function()if c.context_changed then error("context changed")end end,
        prepare=function(plan)
            local p={}
            for _,r in ipairs(plan.resources)do p[#p+1]={key=r.kind=="motlist" and "motion" or "pfb",kind=r.kind,value=retain(r.path)}end
            return p
        end,
        release_prepared=function(p)for _,r in ipairs(p)do release(r.value)end end,
        ready=function()return c.ready end,
        targets=function(_,p)return p end,
        read=function(t)return c.values[t.key]end,
        write=function(t,v,before)
            assert(c.values[t.key]==before);c.values[t.key]=v;c.writes[#c.writes+1]=t.key
            if c.fail_write==t.key then error("post-write failure")end
        end,
        verify=function(t,v)assert(c.values[t.key]==v)end,
        restore=function(t,v,expected)
            if c.fail_restore==t.key then error("restore unavailable")end
            assert(c.values[t.key]==v or c.values[t.key]==expected,"unowned modification")
            c.values[t.key]=v
        end}
    c.runtime=factory.new(adapter)
    function c:plan(n)
        local id=string.rep(tostring(n or 1),32)
        return {schema=1,id=id,source={},resources={
            {kind="motlist",path="art/MotionVfxLive/ActionPreview/"..id.."/resource_0.motlist"},
            {kind="pfb",path="art/MotionVfxLive/ActionPreview/"..id.."/resource_1.pfb"}}}
    end
    function c:drain()for _=1,8 do if not self.runtime.state.busy then break end;self.runtime:tick()end end
    function c:clean()for k,v in pairs(self.pins)do assert(v==0,k.." retained: "..v)end end
    return c
end
do
    local c=fixture();check(c.runtime:apply(c:plan()),"Paired preview is queued")
    check(#c.writes==0,"Queueing never changes engine references")
    c.ready=false;c.runtime:tick();c.runtime:tick();check(#c.writes==0,"Both resources must load before activation")
    c.ready=true;c:drain()
    check(c.runtime.state.phase=="active" and c.writes[1]=="pfb" and c.writes[2]=="motion","PFB is activated before the new motion clip")
    check(c.runtime.state.appliedCount==2 and c.runtime.state.requiresRestore,"Successful preview retains recovery obligations")
    c.runtime:apply(c:plan(2));c:drain();check(c.values.motion:find(string.rep("2",32),1,true),"Second preview updates both references")
    c.runtime:restore();c:drain()
    check(c.values.motion=="original-motion" and c.values.pfb=="original-pfb","Repeated preview restores the first baseline")
    check(not c.runtime.state.requiresRestore,"Completed restoration releases recovery obligations");c:clean()
end
do
    local c=fixture();c.fail_write="motion";c.runtime:apply(c:plan());c:drain()
    check(c.runtime.state.phase=="error" and c.values.motion=="original-motion" and c.values.pfb=="original-pfb","A setter that mutates then fails rolls back both files")
    c.runtime:restore();c:drain();c:clean()
end
do
    local c=fixture();c.runtime:apply(c:plan());c:drain()
    local previous=c.values.motion;c.fail_write="motion";c.fail_restore="motion"
    c.runtime:apply(c:plan(2));c:drain()
    check(c.runtime.state.phase=="restore_failed" and c.runtime.state.requiresRestore,"Rollback failure retains the original backup and pending changes")
    check(not c.runtime:apply(c:plan(3)),"Unrestored preview blocks a new application")
    c.fail_restore=nil;c.fail_write=nil;c.runtime:restore();c:drain()
    check(c.values.motion=="original-motion" and c.values.pfb=="original-pfb","Retry completes restoration after partial failure");c:clean()
end
do
    local c=fixture();c.ready=false;c.runtime:apply(c:plan());c.runtime:tick();c.clock=16;c:drain()
    check(c.runtime.state.phase=="error" and #c.writes==0,"Loading timeout cannot activate half of a preview");c:clean()
end
do
    local c=fixture();c.runtime:apply(c:plan());c.runtime:tick();c.context_changed=true;c:drain()
    check(c.runtime.state.phase=="error" and #c.writes==0,"Scene or equipment changes cancel before activation");c:clean()
end
do
    local c=fixture();c.runtime:apply(c:plan());c.runtime:tick();c.runtime:restore();c:drain()
    check(c.runtime.state.phase=="idle" and #c.writes==0,"Stopping during loading cancels the pending preview");c:clean()
    local invalid=c:plan();invalid.id="../escape"
    check(not c.runtime:apply(invalid),"Only private revision identities are accepted")
    invalid=c:plan();invalid.resources[1].path="art/foreign/motion.motlist"
    check(not c.runtime:apply(invalid),"Non-private resource paths are rejected")
end
print("RESULT "..passed.." action preview transaction checks; mocked engine, no game calls")
