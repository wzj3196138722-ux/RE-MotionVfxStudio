-- One-shot resource construction probe. Never replaces an actor reference.
local probe={phase="queued",started=os.time(),objects={}}
local function keep(o) assert(o,"resource construction returned nil");o:add_ref();probe.objects[#probe.objects+1]=o;return o end
local function report() json.dump_file("MotionVfxLive/action-preview-probe.json",probe.result) end
local function finish(err)
    probe.phase="done";probe.result.error=err and tostring(err) or nil
    probe.result.completedAt=os.time();report()
    for i=#probe.objects,1,-1 do pcall(function()probe.objects[i]:release()end)end
    probe.objects={}
end
re.on_pre_application_entry("LockScene",function()
    if probe.phase=="done" then return end
    local ok,err=pcall(function()
        if probe.phase=="queued" then
            probe.result={startedAt=probe.started}
            local ch=sdk.get_managed_singleton("app.PlayerManager"):call("getMasterPlayer"):call("get_Character")
            local manager=ch:call("get_GameObject"):call("getComponent(System.Type)",sdk.typeof("via.effect.script.ObjectEffectManager2"))
            local motion=manager:get_field("Motion")
            local bank=motion:call("findMotionBank(System.UInt32)",0)
            local motionpath=bank:call("get_MotionList"):call("get_ResourcePath")
            local pfbpath=manager:get_field("DataContainer"):call("get_Path")
            probe.result.motionPath=motionpath;probe.result.prefabPath=pfbpath
            -- Wilds RSZ via.Prefab: bool Standby (align 1), Resource Path
            -- (align 4), uint32 UTF-16 code-unit count including the terminator.
            local units={};for _,cp in utf8.codes(pfbpath) do
                if cp<65536 then units[#units+1]=cp else cp=cp-65536;units[#units+1]=0xD800+math.floor(cp/1024);units[#units+1]=0xDC00+cp%1024 end
            end
            local data=string.pack("<I4I4",1,#units+1)
            for _,cp in ipairs(units)do data=data..string.pack("<I2",cp)end
            data=data..string.pack("<I2",0)
            local bytes={data:byte(1,#data)}
            probe.prefab=keep(sdk.create_instance("via.Prefab"))
            probe.prefab:deserialize_native(bytes,{})
            assert(probe.prefab:call("get_Path"):lower()==pfbpath:lower(),"Prefab deserialization path mismatch")
            probe.raw=keep(sdk.create_resource("via.motion.MotionListResource",motionpath))
            probe.holder=keep(probe.raw:create_holder("via.motion.MotionListResourceHolder"))
            probe.bank=keep(sdk.create_instance("via.motion.MotionBank"))
            probe.bank:call("set_MotionList(via.motion.MotionListBaseResourceHolder)",probe.holder)
            probe.phase="loading";report()
        elseif probe.phase=="loading" then
            probe.result.prefabReady=probe.prefab:call("get_Ready")
            probe.result.prefabValid=probe.prefab:call("get_Valid")
            probe.result.motionValid=probe.bank:call("get_MotionListValid")
            probe.result.motionEmpty=probe.bank:call("get_MotionListEmpty")
            if probe.result.prefabReady and probe.result.prefabValid and probe.result.motionValid then finish()
            elseif os.time()-probe.started>20 then finish("readiness timed out") end
        end
    end)
    if not ok then probe.result=probe.result or {};finish(err)end
end)
