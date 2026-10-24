-- Session-only cleanup. Request the game's normal exit after editor restoration.
-- Never installs in autorun or changes the user's saved game files.
if _G.MotionVfxValidationClose080 then return end
local result={phase="pending",startedAt=os.time()}
_G.MotionVfxValidationClose080=result
local started,finished=false,false
local function report()json.dump_file("MotionVfxLive/validation-close080.json",result)end
report()
re.on_pre_application_entry("LockScene",function()
    if finished then return end
    local ok,err=pcall(function()
        local core=assert(_G.MotionVfxLive,"Editor unavailable for cleanup")
        if not started then
            started=true;core.stop_and_restore(true)
            local active=_G.MotionVfxStartupActive080
            if active and active.changed then active.restore()end
            result.phase="restoring";report();return
        end
        assert(os.time()-result.startedAt<60,"Editor restoration timed out; game was not forced to quit")
        local state=core.state
        local a,p=core.action_preview_status(),core.pfb_preview_status()
        local b=state.bindings_snapshot or {}
        local active=_G.MotionVfxStartupActive080
        if state.pending or state.restore_preview_pending or state.bindings_restore_pending or
            state.scene_original or state.global_time_original or state.effect_update_original or
            #(state.scene_queue or {})>0 or a.busy or a.requiresRestore or p.busy or p.requiresRestore or
            b.busy or b.requiresRestoreBeforeReset or (b.appliedCount or 0)>0 or active and active.changed then return end
        assert(not state.restore_preview_error and not state.bindings_restore_failed,"Editor cleanup reported a failure")
        result.verified={action=a,pfb=p,bindings=b,sceneRestored=true,activeRestored=not(active and active.changed)}
        result.phase="exit_requested";result.finishedAt=os.time();report()
        finished=true
        sdk.call_native_func(sdk.get_native_singleton("via.Application"),
            sdk.find_type_definition("via.Application"),"exit(System.Int32)",0)
    end)
    if not ok then finished=true;result.phase="failed";result.error=tostring(err);report()end
end)
