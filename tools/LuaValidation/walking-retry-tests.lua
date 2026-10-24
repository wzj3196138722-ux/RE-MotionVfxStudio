local project=arg[1]
local function setup(existing)
    local requests,writes,frames={},{},{}
    local slot={resourcePath="art/foot.efx",groupName="Group_0",jointName="",startFrame=0,endFrame=58}
    local record={id="walk"}
    local core={state={capture_snapshot={actions={record}},bindings_snapshot={appliedCount=0}},
        workbench={slot_template="root#0",action_document={source={actionId="walk"},pfbPath="art/player.pfb",dataContainerId=0,added={slot}}}}
    core.has_drafts=function()return false end
    core.poll=function()core.state.status={ready=true,version="0.5.3"}end
    core.action_preview_status=function()return core.preview or {phase="idle"}end
    core.resolve_action_source=function(r)assert(r==record);return {prefabs={{path="art/player.pfb",dataContainerId=0}}}end
    core.send=function(action,args,done)
        requests[#requests+1]={action=action,args=args}
        if action=="status" then done({ok=true})
        elseif action=="open_action" then done({ok=true,actionDocument={revision=0,added=existing and {slot} or {},templates={{key="root#0"}}}})
        elseif action=="add_action_slot" then done({ok=true,actionDocument={revision=1,added={slot}}})end
        return true
    end
    core.preview_action=function(doc,r)assert(doc.revision==1 and r==record);core.preview={phase="active",appliedRevision=1};return true end
    local env=setmetatable({_G={MotionVfxLive=core},json={load_file=function()return {version="0.5.3"}end,dump_file=function(path,value)writes[path]=value end},
        re={on_frame=function(f)frames[#frames+1]=f end},loadfile=function()return function()core.diagnostic=true end end},{__index=_G})
    assert(loadfile(project.."/tools/LiveEditor/Retry-WalkingSlot.lua","t",env))()
    for _=1,2 do for _,f in ipairs(frames)do f()end end
    return core,requests,writes
end
local core,requests=setup(false)
assert(#requests==3 and requests[3].args.startFrame==0 and requests[3].args.endFrame==58)
assert(requests[3].args.groupName=="Group_0" and requests[3].args.templateKey=="root#0")
assert(core.preview.phase=="active" and core.diagnostic and core.workbench.action_document.revision==1)
local duplicate,requests2=setup(true)
assert(#requests2==2 and not duplicate.preview and not duplicate.diagnostic)
print("PASS retry keeps the original slot, uses fresh proof, waits for publication and refuses duplicate edits")
