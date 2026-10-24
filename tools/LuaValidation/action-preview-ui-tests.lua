-- Runs the real editor controller/mailbox with a fake engine transaction.
local project=arg[1];local args=arg
arg={project.."/game-plugin/MotionVfxLive.lua",nil,nil,"export-environment"}
local environment=assert(loadfile(project.."/tools/LuaValidation/live-ui-tests.lua"))();arg=args
local count=0
local function check(v,m)assert(v,m);count=count+1;print("PASS "..m)end
local function setup()
    local c=environment();local env=c.env
    c.module.active=false;env.MotionVfxLive=nil
    c.bench={frame=function()end,save_settings=function()end,widgets={commands={}}}
    env.d2d={register=function()end}
    env.MotionVfxD2DWidgets={}
    env.MotionVfxD2DWorkbench={new=function()return c.bench end}
    local old_require=env.require
    c.preview={state={phase="idle",busy=false,requiresRestore=false},applies=0,ticks=0,restores=0}
    function c.preview:snapshot()return self.state end
    function c.preview:aliases()return {}end
    function c.preview:apply(plan)self.applies=self.applies+1;self.plan=plan;self.state={phase="loading",busy=true};return true end
    function c.preview:restore()self.restores=self.restores+1;self.state={phase="restore",busy=true,requiresRestore=true};return true end
    function c.preview:tick()
        self.ticks=self.ticks+1
        if self.state.phase=="loading" then self.state={phase="active",requiresRestore=true}
        elseif self.state.phase=="restore" then self.state={phase="idle",requiresRestore=false}end
    end
    env.require=function(name)
        if name=="MotionVfxLive.fx_action_preview_runtime" then return {new=function()return c.preview end}
        elseif name=="MotionVfxLive.fx_action_preview_transaction" then return {}end
        return old_require(name)
    end
    c.module=assert(loadfile(project.."/game-plugin/MotionVfxLive.lua","t",env))()
    c.module.poll()
    c.module.resolve_action_source=function(record)c.source_record=record;return {proof="fixture-current-source"}end
    return c
end
do
    local c=setup();local record={id="attack"}
    check(c.module.preview_action({revision=9},record),"Preview publishes the current action revision through the editor mailbox")
    local request=c.files["MotionVfxLive/request.json"]
    check(request.action=="publish_action_preview" and request.actionRevision==9 and c.source_record==record,"Publication refreshes provenance for the selected action")
    check(c.preview.applies==0,"The controller does not apply before backend publication completes")
    c:respond({ok=true,actionPreview={id="published"}})
    check(c.preview.applies==1 and c.preview.plan.id=="published","Only the returned immutable revision reaches the engine transaction")
    c:lock();check(c.module.action_preview_status().phase=="active","Game writes run at the scene boundary")
    local ok=c.module.reset();check(ok==false and c.module.active,"A controlled UI reset cannot discard action restoration backups")
    c.module.stop_and_restore();c:lock()
    check(c.module.action_preview_status().phase=="idle","Global restoration includes the action/PFB transaction")
end
do
    local c=setup();c.module.preview_action({revision=1},{id="attack"});c.module.stop_and_restore()
    c:respond({ok=true,actionPreview={id="late"}})
    check(c.preview.applies==0,"A late publication response cannot undo the user's stop request")
end
do
    local c=setup();c.module.preview_action({revision=1},{id="attack"})
    c:respond({ok=false,error="missing provider"})
    check(c.preview.applies==0,"Backend validation failure never activates a half preview")
end
do
    local c=setup();c.preview.state={phase="activate",busy=true}
    check(not c.module.preview_action({revision=1},{id="attack"}),"Concurrent action publication is rejected")
    check(not c.module.publish_document(true),"EFX replacement is rejected during action/PFB activation")
end
do
    local c=setup();local doc={revision=4,previewSlotCount=2,added={{id="one"},{id="two"}}}
    c.bench.action_document=doc;c.module.state.action_document=doc
    c.preview.state={phase="active",requiresRestore=true}
    check(c.module.remove_action_slot(doc,"one"),"Removing an applied slot first requests native restoration")
    c:frame();check(c.module.state.pending==nil,"Slot bytes cannot change while native references remain applied")
    check(not c.module.preview_action(doc,{id="attack"}),"Queued deletion prevents concurrent preview publication")
    c:lock();for _=1,4 do c:frame()end
    local request=c.files["MotionVfxLive/request.json"]
    check(request.action=="remove_action_slot" and request.slotId=="one" and request.actionRevision==4,"Deletion uses the exact slot ID after native recovery")
    local remaining={revision=5,previewSlotCount=1,added={{id="two"}}}
    c:respond({ok=true,actionDocument=remaining})
    check(c.bench.action_document==remaining and c.files["MotionVfxLive/request.json"].action=="publish_action_preview","Successful deletion republishes only the surviving slots")
end
do
    local c=setup();local doc={revision=9,added={{id="one"}},totalAddedSlots=1}
    c.module.state.action_document=doc -- another action is selected, so the visible form is empty
    c.module.stop_and_restore();c:lock();for _=1,4 do c:frame()end
    check(c.files["MotionVfxLive/request.json"].action=="reset_action_slots","Global restore clears added slots even after changing the selected action")
    c:respond({ok=true,actionDocument={revision=10,added={},previewSlotCount=0,totalAddedSlots=0}})
    check(not c.module.action_edit_pending() and c.module.state.action_document.totalAddedSlots==0,"Global restore waits for the backend to clear editable slots")
end
do
    local c=setup();local doc={revision=2,added={{id="one"}}};c.bench.action_document=doc
    c.module.stop_and_restore(true);c:lock();for _=1,4 do c:frame()end
    check(not c.module.action_edit_pending() and c.module.state.pending==nil,"Script-only reload preserves editable slots")
    c.preview.state={phase="restore_failed",requiresRestore=true,message="original reference unavailable"}
    c.module.restore_action_preview();c.preview.state={phase="restore_failed",requiresRestore=true,message="original reference unavailable"}
    for _=1,5 do c:frame()end
    check(c.module.action_edit_pending() and c.module.state.pending==nil,"Failed native restore retains slot edits for recovery")
    c:lock();for _=1,4 do c:frame()end
end
print("RESULT "..count.." editor/action-preview integration checks; no game calls")
