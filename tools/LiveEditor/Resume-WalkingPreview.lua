-- Session recovery + rebuild of the user's existing single walking slot.
-- The backend is updated separately; no new slot settings are invented here.
local core=assert(_G.MotionVfxLive)
assert(core.workbench and core.workbench.action_document and #(core.workbench.action_document.added or {})==1,
    "Keep the previous single-slot action document open")
assert(not core.has_drafts() and not core.state.pending,"Submit pending input first")
assert(not (_G.MotionVfxWalkingResume and _G.MotionVfxWalkingResume.running),"Resume is already running")
assert(loadfile("reframework/data/MotionVfxLive/Repair-ActionRestore.lua"))()
local task={running=true,phase="repair",started=os.time()}
_G.MotionVfxWalkingResume=task
local function report()json.dump_file("MotionVfxLive/walking-resume-status.json",task)end
report()
re.on_frame(function()
    if not task.running then return end
    local recovery=_G.MotionVfxActionRecovery
    if not recovery then return end
    if recovery.phase=="failed" then
        task.running=false;task.phase="failed";task.error=recovery.error;report();return
    end
    if recovery.phase~="restored" then return end
    if task.phase~="waiting_backend" then task.phase="waiting_backend";report()end
    if os.time()-task.started>600 then
        task.running=false;task.phase="timeout";task.error="Restore finished; retry can be run separately after the backend update";report();return
    end
    local backend=json.load_file("MotionVfxLive/status.json")
    if not backend or not backend.ready or backend.version~="0.5.3" or os.time()-(backend.heartbeatUnixSeconds or 0)>8 or core.state.pending then return end
    task.running=false
    local ok,err=pcall(function()assert(loadfile("reframework/data/MotionVfxLive/Retry-WalkingSlot.lua"))()end)
    task.phase=ok and "retry_queued" or "failed";task.error=not ok and tostring(err) or nil;report()
end)
