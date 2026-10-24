-- Runs the real bridge against a deterministic in-memory transport. No SDK,
-- filesystem mailbox, or game state is touched.
local project=assert(arg[1],"project path required")
local module=assert(loadfile(project.."/game-plugin/MotionVfxLive/fx_ai_bridge.lua"))()
local checks=0
local function check(v,label) assert(v,label);checks=checks+1;print("PASS "..label) end
local function copy(v)
    if type(v)~="table" then return v end
    local out={};for k,x in pairs(v)do out[k]=copy(x)end;return out
end
local request_path="MotionVfxLive/ai-request.json"
local response_path="MotionVfxLive/ai-response.json"
local function setup(factory,with_clock)
    local c={time=0,wall=100,files={},reads=0,starts=0,waits=0,finish=false}
    local host={
        now=function()return c.wall end,
        exists=function(path)return c.files[path]~=nil end,
        read=function(path)if path==request_path then c.reads=c.reads+1 end;return copy(c.files[path])end,
        write=function(path,value)c.files[path]=copy(value);return true end,
        busy=function(value)c.busy=value end,
        start=function(_,_,done)c.starts=c.starts+1;c.done=done;return function()
            c.waits=c.waits+1;if c.finish then done(true,{done=true})end
        end end,
    }
    if with_clock then host.clock=function()if c.clock_error then error("clock failure")end;return c.time end end
    c.bridge=factory.new(host);c.reads=0
    function c:request(id,args)
        self.files[request_path]={protocol=1,id=id,action="inspect",arguments=args or {},expiresAtUnixSeconds=200}
    end
    return c
end
local c=setup(module,true)
for i=0,599 do c.time=i/1000;c.bridge:tick()end
check(c.reads>=5 and c.reads<=6,"Idle disk polling is bounded independently of render FPS")
c.time=.7;c:request("first");c.bridge:tick()
check(c.starts==1 and c.busy,"A fresh request executes once when due")
for i=1,30 do c.time=.7+i/1000;c.bridge:tick()end
check(c.waits==30,"Active operation waiters still run on every frame")
c.finish=true;c.time=.731;c.bridge:tick()
check(c.files[response_path].ok and not c.busy,"Completion is delivered inside the disk-poll interval")
c.time=.9;c.bridge:tick()
check(c.starts==1,"Repeated requests never repeat an operation")
c:request("first",{path="different"});c.time=1.01;c.bridge:tick()
check(c.files[response_path].error.code=="id_conflict" and c.starts==1,"Same ID with changed arguments is still rejected")
c:request("second");c.time=1.12;c.finish=false;c.bridge:tick()
check(c.starts==2,"Next request is accepted without waiting on the completed operation")
c.wall=221;c.time=1.13;c.bridge:tick()
check(c.files[response_path].error.code=="operation_uncertain" and c.busy,"Timeout reporting remains independent of the poll interval")
c.bridge:shutdown()
check(not c.busy and not c.bridge.ready,"Shutdown journals an active operation and clears busy")
local restarted=module.new({now=function()return 100 end,clock=function()return 0 end,
    exists=function(path)return c.files[path]~=nil end,read=function(path)return copy(c.files[path])end,
    write=function(path,v)c.files[path]=copy(v);return true end,busy=function()end,
    start=function()error("Reload must not replay the existing request")end})
restarted:tick()
check(restarted.ready and not restarted.active,"Reload never replays a request already on disk")
local fallback=setup(module,false)
for i=1,3 do fallback.bridge:tick()end
check(fallback.reads==3,"Hosts without a clock retain the existing polling behavior")
local broken=setup(module,true);broken.bridge:tick();broken.clock_error=true;broken.bridge:tick()
check(broken.reads==2,"Clock errors fail open for transport progress")
broken.clock_error=false;broken.time=2;broken.bridge:tick();broken.time=1;broken.bridge:tick()
check(broken.reads==4,"A backwards clock does not starve the mailbox")
broken.time=0/0;broken.bridge:tick()
check(broken.reads==5,"An invalid clock does not starve the mailbox")
local function benchmark(factory,label)
    local s=setup(factory,true)
    local began=os.clock()
    for i=1,7200 do s.time=i/120;s.bridge:tick()end
    print(string.format("BENCH %s: 7200 frames over simulated 60s; request reads=%d; harness ms=%.3f",label,s.reads,(os.clock()-began)*1000))
    return s.reads
end
local optimized=benchmark(module,"optimized")
check(optimized<=601,"120 FPS idle polling stays at no more than 10 reads per second")
if arg[2]then
    local before=assert(loadfile(arg[2]))()
    local original=benchmark(before,"baseline")
    check(original==7200 and optimized<original/10,"Baseline comparison removes over 90% of idle request-file reads")
end
print("AI transport performance checks: "..checks.." passed; no game writes.")
