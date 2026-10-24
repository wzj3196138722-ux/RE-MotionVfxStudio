-- Read-only motion/track diagnostics. No resource replacement or effect spawn.
-- Re-running stops the previous sampler. Wait for movement before the timed
-- capture, so reading the UI cannot use up the whole diagnostic interval.
if _G.MotionVfxWalkingProbe then _G.MotionVfxWalkingProbe.stop=true end
local p={started=os.time(),samples={},seen={},errors={},owned={},ticks=0}
_G.MotionVfxWalkingProbe=p
local function own(o) assert(o,"diagnostic object unavailable");o:add_ref();p.owned[#p.owned+1]=o;return o end
local function report()json.dump_file("MotionVfxLive/walking-tracks-probe.json",{started=p.started,movementStarted=p.movementStarted,observedAt=os.time(),samples=p.samples,errors=p.errors,finished=p.finished,bankInfo=p.bankInfo})end
local function done()p.finished=true;report();for _,o in ipairs(p.owned)do pcall(o.release,o)end;p.owned={}end
local function call(o,m,...) if not o then return nil end;local ok,v=pcall(o.call,o,m,...);if ok then return v end;return nil end
local function field(o,n)local ok,v=pcall(o.get_field,o,n);if ok and (type(v)=="string" or type(v)=="number" or type(v)=="boolean")then return v end end
re.on_pre_application_entry("LockScene",function()
    if p.finished then return end
    if p.stop or os.time()-p.started>300 or (p.movementStarted and os.time()-p.movementStarted>45) then done();return end
    p.ticks=p.ticks+1;if p.ticks%8~=0 then return end
    local ok,err=pcall(function()
        local pm=sdk.get_managed_singleton("app.PlayerManager")
        local ch=call(call(pm,"getMasterPlayer"),"get_Character")
        local motion=call(ch,"get_MotionComponent")
        if not motion then return end
        p.category=p.category or sdk.find_type_definition("via.motion.SequenceCategory"):get_field("VFX"):get_data(nil)
        if not p.bankInfo then
            local info=own(sdk.create_instance("via.motion.MotionInfo"))
            local got=call(motion,"getMotionInfo(System.UInt32, System.UInt32, via.motion.MotionInfo)",20,37,info)
            p.bankInfo={bank=20,motion=37,read=got,trackHashes={}}
            if got then
                p.bankInfo.name=call(info,"get_MotionName")
                p.bankInfo.count=call(info,"getTracksCount(via.motion.SequenceCategory)",p.category)
                for t=0,math.min(p.bankInfo.count or 0,32)-1 do
                    p.bankInfo.trackHashes[#p.bankInfo.trackHashes+1]=call(info,"getTracksNameHash(via.motion.SequenceCategory, System.UInt32)",p.category,t)
                end
            end
        end
        local layer=call(motion,"getLayer(System.UInt32)",0)
        for i=0,math.min(call(layer,"getMotionNodeCount") or 0,6)-1 do
            local node=call(layer,"getMotionNode(System.UInt32)",i)
            local mid=call(node,"get_MotionID");local bank=call(node,"get_MotionBankID")
            local frame=call(node,"get_MotionFrame") or 0
            if mid and mid~=0 and not p.movementStarted then p.movementStarted=os.time()end
            local key=tostring(bank).."/"..tostring(mid).."/"..math.floor(frame/10)
            if not p.seen[key] and #p.samples<120 then
                p.seen[key]=true
                local sample={bank=bank,motion=mid,name=call(node,"get_MotionName"),frame=frame,
                    sequenceActive=call(node,"get_SequenceActive"),tracks={}}
                local count=call(node,"getSequenceTracksCount(via.motion.SequenceCategory)",p.category) or 0
                sample.count=count
                for t=0,math.min(count,32)-1 do
                    local info=call(node,"getSequenceTracksTypeinfo(via.motion.SequenceCategory, System.UInt32)",p.category,t)
                    local name=call(info,"get_FullName")
                    local row={index=t,type=name}
                    if name=="via.effect.script.VFXRangeTrack" then
                        p.track=p.track or own(sdk.create_instance(name))
                        row.read=call(node,"getSequenceTracks(via.motion.SequenceCategory, System.UInt32, via.motion.Tracks)",p.category,t,p.track)
                        if row.read then
                            row.effectId=field(p.track,"EffectId");row.container=field(p.track,"DataContainerId")
                            row.joint=field(p.track,"JointName");row.endType=field(p.track,"EndType")
                            local scale=p.track:get_field("Scale");row.scale=scale and {x=scale.x,y=scale.y,z=scale.z}
                        end
                    end
                    sample.tracks[#sample.tracks+1]=row
                end
                p.samples[#p.samples+1]=sample
            end
        end
    end)
    if not ok and #p.errors<10 then p.errors[#p.errors+1]=tostring(err)end
    if p.ticks%120==0 then report()end
end)
report()
