-- Fixed local format validation. Load only seven privately named prepared
-- PFBs as detached via.Prefab values. Never instantiate, update a manager,
-- replace any actor resource, or call an editor mutation API.
local existing=_G.MotionVfxPfbLoadProbe080
assert(not existing or not existing.running,"A PFB load probe is already running")
local report={started=os.time(),running=true,cases={},instantiated=false,actorWrites=false,errors={}}
_G.MotionVfxPfbLoadProbe080=report
local manifest,objects,index=nil,{},1
local function snapshot()
    local core=_G.MotionVfxLive
    if not core or not core.active then return {editorAvailable=false}end
    local s=core.state;local action=core.action_preview_status and core.action_preview_status()or {}
    local pfb=core.pfb_preview_status and core.pfb_preview_status()or {}
    return {editorAvailable=true,actionCount=action.appliedCount,pfbCount=pfb.appliedCount,
        bindingCount=(s.bindings_snapshot or {}).appliedCount,recording=(s.capture_snapshot or {}).recording,
        sceneOwned=s.scene_original~=nil or s.global_time_original~=nil or s.effect_update_original~=nil}
end
local function write()report.observedAt=os.time();json.dump_file("MotionVfxLive/pfb-load-probe.json",report)end
local function finish(reason)
    report.running=false;report.finishedAt=os.time();report.reason=reason;report.finalState=snapshot()
    local unchanged=true
    for k,v in pairs(report.baseline or {})do if report.finalState[k]~=v then unchanged=false end end
    for k,v in pairs(report.finalState)do if (report.baseline or {})[k]~=v then unchanged=false end end
    report.editorStateUnchanged=unchanged
    report.passed=reason=="complete"and #report.errors==0 and #report.cases==7 and unchanged
    report.limitations="Ready/Valid proves prefab resource loading only; no component instantiation, skeleton compatibility, live replacement or visual effect playback was tested."
    objects={};write()
end
local function bytes(path)
    local data=string.pack("<I4I4",1,#path+1)
    for i=1,#path do data=data..string.pack("<I2",path:byte(i))end
    return { (data..string.pack("<I2",0)):byte(1,#data+2) }
end
local function begin()
    manifest=assert(json.load_file("MotionVfxLive/pfb-load-manifest.json"),"Missing prepared PFB manifest")
    assert(manifest.caseCount==7 and #manifest.cases==7 and manifest.instantiationAllowed==false,"Unexpected PFB manifest contract")
    assert(type(manifest.privateRoot)=="string"and manifest.privateRoot:match("^art/MotionVfxLive/Validation/Pfb080/[%w_]+$"),"Non-private PFB prefix rejected")
    local names={effect_original=true,effect_copy=true,equipment_original=true,equipment_add_effectpfb=true,
        equipment_add_chain=true,equipment_add_clsp=true,equipment_add_jcns=true}
    for _,spec in ipairs(manifest.cases)do
        assert(names[spec.name]and spec.path==manifest.privateRoot.."/"..spec.name..".pfb","Unexpected PFB load target")
        names[spec.name]=nil
    end
    report.privateRoot=manifest.privateRoot;report.baseline=snapshot()
end
re.on_pre_application_entry("LockScene",function()
    if not report.running then return end
    local ok,err=pcall(function()
        if not manifest then begin();write();return end
        local spec=manifest.cases[index]
        if not spec then finish("complete");return end
        local row=report.cases[index]
        if not row then
            row={name=spec.name,path=spec.path,sha256=spec.sha256,sourcePath=spec.sourcePath,started=os.time(),ready=false,valid=false}
            report.cases[index]=row
            local value=assert(sdk.create_instance("via.Prefab"),"Cannot create detached prefab")
            value:add_ref();objects[index]=value
            value:deserialize_native(bytes(spec.path),{})
            assert(value:call("get_Path"):lower()==spec.path:lower(),"Detached prefab path readback differs")
            write();return
        end
        local value=objects[index]
        row.ready=value:call("get_Ready")==true;row.valid=value:call("get_Valid")==true
        if row.ready and row.valid then row.passed=true;row.finishedAt=os.time();index=index+1;write()
        elseif os.time()-row.started>60 then
            row.passed=false;row.error="Prefab Ready/Valid timeout";report.errors[#report.errors+1]=spec.name..": "..row.error
            index=index+1;write()
        end
    end)
    if not ok then report.errors[#report.errors+1]=tostring(err);finish("exception")end
end)
write()
