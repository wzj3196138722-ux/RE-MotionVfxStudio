-- Rebuild exactly the single open action slot after the 0.5.3 backend update.
-- Uses the editor's normal source validation, mailbox and queued preview APIs.
-- Does not overwrite installed MOTLIST/PFB/EFX originals or reset scripts.
local core=assert(_G.MotionVfxLive,"MotionVfx Live is not loaded")
local w=assert(core.workbench,"Workbench unavailable")
local old=assert(w.action_document,"Open the previous action-slot document first")
local backend=json.load_file("MotionVfxLive/status.json")
assert(backend and backend.version=="0.5.3","Backend 0.5.3 is required")
assert(#(old.added or {})==1,"This retry helper requires exactly one previous slot")
assert(not core.has_drafts() and not core.state.pending,"Finish current input/request first")
local status=core.action_preview_status()
assert(not status.busy and not status.requiresRestore,"Restore the previous preview first")
assert((core.state.bindings_snapshot.appliedCount or 0)==0,"Restore EFX overrides first")
local record
for _,r in ipairs((core.state.capture_snapshot or {}).actions or {})do
    if r.id==old.source.actionId then record=r;break end
end
assert(record,"The selected action record is no longer available")
local proof=assert(core.resolve_action_source(record),"Could not verify the current actor and PFB")
local index
for i,p in ipairs(proof.prefabs or {})do
    if p.path:lower()==old.pfbPath:lower() and p.dataContainerId==old.dataContainerId then index=i-1;break end
end
assert(index,"The actor no longer uses the edited PFB")
local slot=old.added[1]
local template=assert(w.slot_template,"Keep the original attachment template selected")
local result={started=os.time(),phase="waiting_backend",original=slot}
local function report()json.dump_file("MotionVfxLive/walking-retry-status.json",result)end
local function fail(message)result.phase="failed";result.error=tostring(message);core.state.message=result.error;report()end
report()
local function rebuild()
result.phase="open";report()
if not core.send("open_action",{source=proof,prefabIndex=index},function(r)
    if not r.ok then fail(r.error);return end
    local doc=r.actionDocument
    if #(doc.added or {})~=0 then fail("New backend already has action edits; stopped to avoid duplicating them");return end
    local found=false;for _,t in ipairs(doc.templates or {})do if t.key==template then found=true;break end end
    if not found then fail("The original template is absent from the verified PFB");return end
    w.action_proof=proof;w.action_document=doc
    result.phase="add";report()
    if not core.send("add_action_slot",{actionRevision=doc.revision,templateKey=template,resourcePath=slot.resourcePath,
        groupName=slot.groupName or "",jointName=slot.jointName or "",startFrame=slot.startFrame,endFrame=slot.endFrame},function(added)
        if not added.ok then fail(added.error);return end
        w.action_document=added.actionDocument;core.state.selected_action_id=record.id
        result.phase="preview";result.revision=added.actionDocument.revision;report()
        if not core.preview_action(added.actionDocument,record)then fail(core.state.message);return end
    end)then fail(core.state.message)end
end)then fail(core.state.message)end
end
re.on_frame(function()
    if result.phase=="waiting_backend" then
        -- The file heartbeat can be ready before the controller's cached one.
        -- Poll through its public API, then sync the preserved EFX document
        -- before sending action requests with the old document revision.
        core.poll()
        local s=core.state.status
        if s and s.ready and s.version=="0.5.3" and not core.state.pending then
            result.phase="sync";report()
            if not core.send("status",{},function(r)if r.ok then rebuild()else fail(r.error)end end)then fail(core.state.message)end
        elseif os.time()-result.started>20 then fail("The updated backend is not ready in the controller")end
        return
    end
    if result.phase~="preview" then return end
    local s=core.action_preview_status()
    if s.phase=="active" and s.appliedRevision==result.revision and not s.busy then
        result.phase="ready";result.message="原槽位已重新生成并热重载，请行走测试。";report()
        local loader,err=loadfile("reframework/data/MotionVfxLive/Probe-WalkingTracks.lua")
        if loader then local ok,e=pcall(loader);if not ok then result.diagnosticError=tostring(e);report()end
        else result.diagnosticError=tostring(err);report()end
    elseif s.phase=="error" or s.phase=="failed" or s.phase=="restore_failed" then fail(s.message)
    elseif os.time()-result.started>60 then fail("Preview did not complete; inspect the editor status before retrying")end
end)
