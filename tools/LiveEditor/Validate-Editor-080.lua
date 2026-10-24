-- Temporary live UI exercise. Changes only editor presentation/draft text,
-- then restores it. No backend mutation, resource application or game input.
local previous=_G.MotionVfxEditorValidation080
if previous and previous.stop then previous.stop("replaced")end
local run={started=os.time(),steps={},errors={},running=true,frames=0}
_G.MotionVfxEditorValidation080=run
local core,wb,state,widgets,original_textbox,installed_textbox,saved,stage,stage_started,stage_frames
local loading,opened_documents=false,false
local destination="MotionVfxLive/editor-080-validation.json"
local function plain(v,depth)
    depth=depth or 0;if depth>6 then return "[depth limit]"end
    if type(v)=="table"then local out={};for k,x in pairs(v)do if type(k)=="string"or type(k)=="number"then out[k]=plain(x,depth+1)end end;return out end
    if type(v)=="number"or type(v)=="string"or type(v)=="boolean"then return v end
end
local function snapshot()
    if not core then return {}end
    return {action=plain(core.action_preview_status and core.action_preview_status()),
        pfb=plain(core.pfb_preview_status and core.pfb_preview_status()),bindings=plain(state.bindings_snapshot),
        sceneOwned=state.scene_original~=nil or state.global_time_original~=nil or state.effect_update_original~=nil,
        pending=state.pending and state.pending.operation,recording=(state.capture_snapshot or {}).recording}
end
local function report()run.observedAt=os.time();pcall(json.dump_file,destination,plain(run))end
local function restore()
    if not saved then return end
    if widgets and widgets.textbox==installed_textbox then widgets.textbox=original_textbox end
    for k,v in pairs(saved.workbench)do wb[k]=v end
    for _,k in ipairs(saved.nil_workbench)do wb[k]=nil end
    state.window_open=saved.window_open;state.window_hidden=saved.window_hidden
    if saved.pfb and saved.pfb.fields and run.previousPfbRestored then
        for k,v in pairs(saved.pfb.fields)do wb.pfb[k]=v end
        for _,k in ipairs(saved.pfb.nil_fields)do wb.pfb[k]=nil end
    end
    saved=nil
end
local function finalize(reason)
    if not run.running then return end
    run.running=false;run.finishedAt=os.time();run.reason=reason;run.finalState=snapshot()
    local probe_ok,probe=pcall(json.load_file,"MotionVfxLive/attachment-probe.json")
    if probe_ok and type(probe)=="table"then run.attachmentProbe=probe end
    local ok,err=pcall(restore);if not ok then run.errors[#run.errors+1]="restore: "..tostring(err)end
    local rendered=0;for _,s in ipairs(run.steps)do if (s.drawnFrames or 0)>0 then rendered=rendered+1 end end
    run.renderedSteps=rendered
    local unchanged=true
    for _,kind in ipairs({"action","pfb","bindings"})do
        local before,after=(run.baseline or {})[kind]or {},run.finalState[kind]or {}
        if before.appliedCount~=after.appliedCount or before.requiresRestore~=after.requiresRestore then unchanged=false end
    end
    if run.baseline and (run.baseline.sceneOwned~=run.finalState.sceneOwned or run.baseline.recording~=run.finalState.recording)then unchanged=false end
    run.resourceStateUnchanged=unchanged
    run.passed=reason=="complete"and #run.errors==0 and rendered==#run.steps and unchanged
    run.limitations={"Text values were supplied as local UI drafts; this does not prove physical mouse hit positions, typing, IME or caret motion.",
        "No resource was applied or effect spawned by this test. Native preview snapshots are diagnostics only.",
        "Equipment/effect PFBs were opened read-only in the normal editor and remain in its backend document cache; original file bytes were not written."}
    run.finalPfbDocument=wb and wb.pfb and wb.pfb.document and {path=wb.pfb.document.path,sourceKind=wb.pfb.document.sourceKind,type=wb.pfb.document.type}
    report()
end
local function finish(reason)
    if not run.running or run.finishing then return end
    if opened_documents and saved and saved.pfb then
        run.finishing=true;run.finishReason=reason;run.restoreStarted=os.time();report()
    else finalize(reason)end
end
run.stop=finish
local stages={
    {name="actions-live",page="actions",source="live"},
    {name="actions-local",page="actions",source="local"},
    {name="actions-slots",page="actions",source="slots"},
    {name="actions-groups",page="actions",source="groups"},
    {name="effects",page="effects"},{name="effect-edit",page="edit"},
    {name="pfb-edit",page="pfb"},{name="time",page="time"},
    {name="cache",page="actions",top="cache"},{name="file",page="actions",top="file"},
    {name="edit-tools",page="actions",top="edit_tools"},{name="settings",page="actions",top="settings"},
    {name="small-layout",page="pfb",size={w=920,h=590}},
    {name="large-layout",page="actions",top="file",size={w=1380,h=830}},
    {name="hidden-orb",page="actions",hidden=true},
    {name="restored-window",page="actions"},
    {name="equipment-pfb-content",page="pfb",document="GameDesign/Equip/_Prefab/Weapon/Wp03/00/0025/it0300_0025_0.pfb",documentType="equipment"},
    {name="effect-pfb-content",page="pfb",document="Art/VFX/EffectProvider/Weapon/it02/epvr_it02.pfb",documentType="effect"}}
local function select_stage(index)
    local spec=stages[index];if not spec then finish("complete");return end
    stage=index;stage_started=os.time();stage_frames=0
    wb.page=spec.page;wb.top=spec.top;wb.action_source=spec.source or "live"
    wb.window_size=spec.size or saved.workbench.window_size
    if spec.hidden then wb:hide()else wb:show()end
    run.steps[index]={name=spec.name,frames=0,drawnFrames=0,textboxes={},textCalls=0,maxCommands=0,
        overlayVisible=reframework:is_drawing_ui()}
    loading=spec.document~=nil
    if loading then run.steps[index].loading=true;run.steps[index].requestedDocument=spec.document;run.steps[index].loadStarted=os.time()end
    report()
end
local function initialize()
    core=_G.MotionVfxLive
    if not core or not core.active or not core.workbench then return false end
    wb=core.workbench;state=core.state;widgets=wb.widgets
    if not widgets or not widgets.textbox then return false end
    saved={workbench={},nil_workbench={},window_open=state.window_open,window_hidden=state.window_hidden}
    for _,k in ipairs({"page","top","action_source","window_size","window_position","local_path","cache_alias","scale","replay_speed"})do
        if wb[k]==nil then saved.nil_workbench[#saved.nil_workbench+1]=k else saved.workbench[k]=wb[k]end
    end
    if wb.pfb and wb.pfb.document then
        local doc=wb.pfb.document
        saved.pfb={path=doc.path,sourceKind=doc.sourceKind,fields={},nil_fields={}}
        for _,k in ipairs({"selected","filter","values","game_path","target","target_path","resource"})do
            if wb.pfb[k]==nil then saved.pfb.nil_fields[#saved.pfb.nil_fields+1]=k else saved.pfb.fields[k]=wb.pfb[k]end
        end
        run.previousPfb={path=doc.path,sourceKind=doc.sourceKind,revision=doc.revision,dirty=doc.dirty}
    else
        -- There is no previous PFB document to restore. Leave the inspected
        -- effect page open for a screenshot with frontend/backend in agreement.
        saved.workbench.page="pfb";saved.workbench.top=nil
        saved.nil_workbench[#saved.nil_workbench+1]="top"
        saved.window_open=true;saved.window_hidden=false
        run.leavesEffectPfbOpen=true
    end
    run.version=core.version;run.baseline=snapshot();run.initialUiError=state.ui_error
    if state.ui_error then run.errors[#run.errors+1]="initial UI error: "..tostring(state.ui_error)end
    wb.local_path="C:\\MotionVfx_Test\\中文目录\\art_vfx_abcdefghijklmnopqrstuvwxyz_0123456789.efx.5571972"
    wb.cache_alias="art/MotionVfxLive/Test/abcdefghijklmnopqrstuvwxyz_0123456789.tex"
    original_textbox=widgets.textbox
    installed_textbox=function(self,id,value,width,prefix)
        local x,y=self:pos()
        local changed,next_value=original_textbox(self,id,value,width,prefix)
        local row=run.steps[stage or 0]
        if row then
            row.textCalls=row.textCalls+1
            local entry={id=tostring(id),characters=utf8.len(tostring(value or "")),x=x,y=y,width=width,
                active=imgui.is_item_active(),changed=changed==true,nativeText=true}
            if entry.characters==nil then error("Text field contains invalid UTF-8")end
            if type(x)~="number"or type(y)~="number"or x~=x or y~=y then error("Textbox has invalid screen coordinates")end
            row.textboxes[tostring(id)]=entry
        end
        return changed,next_value
    end
    widgets.textbox=installed_textbox
    select_stage(1);return true
end
-- Use a separate local reference, so the report never serializes callbacks.
local initialized=false
re.on_frame(function()
    if not run.running then return end
    local ok,err=pcall(function()
        run.frames=run.frames+1
        if not initialized then
            if os.time()-run.started>45 then finish("editor_not_ready");return end
            initialized=initialize();return
        end
        if _G.MotionVfxLive~=core or not core.active then finalize("editor_reloaded");return end
        if run.finishing then
            if os.time()-run.restoreStarted>120 then run.errors[#run.errors+1]="Previous PFB restore timed out";finalize("pfb_restore_timeout");return end
            if not run.restoreRequested and not state.pending then
                run.restoreRequested=true
                wb.pfb:open(saved.pfb.path,saved.pfb.sourceKind,function(r)
                    if not run.running then return end
                    if not r.ok then run.errors[#run.errors+1]="Previous PFB reopen failed: "..tostring(r.error or r.message);finalize("pfb_restore_failed");return end
                    local doc=wb.pfb.document
                    run.previousPfbRestored=doc and doc.path==saved.pfb.path and doc.sourceKind==saved.pfb.sourceKind and doc.revision==run.previousPfb.revision and doc.dirty==run.previousPfb.dirty
                    if not run.previousPfbRestored then run.errors[#run.errors+1]="Previous PFB identity/revision changed during restore"end
                    finalize(run.finishReason)
                end)
            end
            return
        end
        if loading then
            local row,spec=run.steps[stage],stages[stage]
            if os.time()-row.loadStarted>120 then run.errors[#run.errors+1]="PFB open timed out: "..spec.document;finish("pfb_open_timeout");return end
            if not row.loadRequested and not state.pending then
                assert(wb.pfb and wb.pfb.open,"PFB editor is unavailable")
                row.loadRequested=true;opened_documents=true
                wb.pfb:open(spec.document,"game_files",function(r)
                    if not run.running or run.finishing then return end
                    local doc=wb.pfb.document
                    if not r.ok or not doc or doc.type~=spec.documentType or not doc.slots or not doc.slots[1] then
                        run.errors[#run.errors+1]="PFB open/type/slot verification failed: "..tostring(r.error or r.message or (doc and doc.type));finish("pfb_content_failed");return
                    end
                    wb.pfb.selected=doc.slots[1].key;wb.pfb.filter="";wb.pfb.values={}
                    row.document={path=doc.path,sourceKind=doc.sourceKind,type=doc.type,revision=doc.revision,dirty=doc.dirty,
                        slotCount=#doc.slots,selectedSlot=wb.pfb.selected,selectedFieldCount=#(doc.slots[1].fields or {})}
                    row.loading=false;loading=false;stage_started=os.time();stage_frames=0;report()
                end)
            end
            return
        end
        local row=run.steps[stage];stage_frames=stage_frames+1;row.frames=stage_frames
        row.maxCommands=math.max(row.maxCommands,#(widgets.commands or {}))
        local hidden=stages[stage].hidden
        if hidden and wb.orb_status or not hidden and state.window_drawn then row.drawnFrames=row.drawnFrames+1 end
        row.bounds=plain(wb.bounds);row.orb=plain(wb.orb_status)
        row.textActive=widgets.text_active==true;row.uiError=state.ui_error
        if state.ui_error then run.errors[#run.errors+1]=row.name..": "..tostring(state.ui_error);finish("ui_error");return end
        if stage_frames>=10 and os.time()-stage_started>=1 then
            row.preview=snapshot();select_stage(stage+1)
        end
    end)
    if not ok then run.errors[#run.errors+1]=tostring(err);finish("exception")end
end)
report()
