local root=arg[1]
local passed=0
local function check(v,m)assert(v,m);passed=passed+1;print("PASS "..m)end
local function fixture()
    local c={objects={},writes={},clock=0,ready=true,sceneEpoch=1,weapon=2}
    local serial=100
    local function obj(kind,path)
        serial=serial+1;local o={kind=kind,path=path,address=serial,refs=0,fields={}}
        c.objects[#c.objects+1]=o
        function o:get_address()return self.address end
        function o:add_ref()
            if self.kind=="raw" then self.refs=self.refs+1 else self.refs=1 end
        end
        function o:release()
            assert(self.kind=="raw","Managed release can invalidate another logical owner under REFramework")
            self.refs=self.refs-1;assert(self.refs>=0,"raw reference underflow")
        end
        function o:get_field(k)return self.fields[k]end
        function o:set_field(k,v)self.fields[k]=v end
        function o:deserialize_native(bytes)
            local s=string.char(table.unpack(bytes));local standby,length,pos=string.unpack("<I4I4",s)
            assert(standby==1 and #s==8+length*2)
            local chars={};for _=1,length-1 do local cp;cp,pos=string.unpack("<I2",s,pos);chars[#chars+1]=string.char(cp)end
            self.path=table.concat(chars)
        end
        function o:create_holder()return obj("holder",self.path)end
        function o:call(method,...)
            local a={...}
            if method=="get_ResourcePath" or method=="get_Path" then return self.path
            elseif method=="get_Valid" then return true
            elseif method=="get_Ready" then return c.ready and not c.new_not_ready
            elseif method=="get_IsEmpty" then return not self.path
            elseif method=="getMasterPlayer" then return c.player
            elseif method=="get_Character" then return c.character
            elseif method=="get_WeaponType" then return c.weapon
            elseif method=="get_WeaponID" then return 89
            elseif method=="get_GameObject" then return c.go
            elseif method=="getComponent(System.Type)" then return c.manager
            elseif method=="findMotionBank(System.UInt32)" then return c.bank
            elseif method=="get_MotionList" or method=="get_TargetMotionList" then return self.holder
            elseif method=="get_MotionListValid" then return c.ready and (self==c.bank or not c.new_not_ready)
            elseif method=="get_MotionListEmpty" then return not self.holder
            elseif method=="getExternalDataContainerIndexFromId(System.Int32)" then return a[1]==2 and 0 or -1
            elseif method=="getExtenalDataConatiner(System.Int32)" then return c.external
            elseif method=="set_MotionList(via.motion.MotionListBaseResourceHolder)" then
                self.holder=a[1]
                if self==c.bank then c.writes[#c.writes+1]="motion";if c.fail_motion then c.fail_motion=false;error("post mutation failure")end end
            elseif method=="requestSetDataContainer(via.Prefab)" then self.fields.RequestDataContainer=a[1]
            elseif method=="updateDataContainer()" then
                if c.fail_before_update then c.fail_before_update=false;error("request queued but not consumed")end
                self.fields.DataContainer=self.fields.RequestDataContainer;self.fields.RequestDataContainer=nil
                self.fields.DataContainerPath=self.fields.DataContainer.path;c.writes[#c.writes+1]=self==c.manager and "root" or "external"
            elseif method=="get_Item(System.UInt32)" then return c.provider
            elseif method=="getStandardData(System.UInt32, System.Int32, System.Boolean)" then
                assert(a[2]==-1 or a[2]==0,"Routing requires the external index, not DataContainerId")
                if not c.missing_route then return c.provider end
            elseif method=="getElementFromID(System.UInt32)" then if not c.missing_element then return c.element end
            else error("Unexpected engine call "..method)end
        end
        return o
    end
    c.pm=obj("pm");c.player=obj("player");c.character=obj("character");c.go=obj("go");c.manager=obj("manager")
    c.motion=obj("motion");c.bank=obj("bank");c.external=obj("external");c.provider=obj("provider");c.element=obj("element")
    c.original_motion=obj("holder","Motion/original.motlist");c.bank.holder=c.original_motion
    c.original_root=obj("pfb","art/character.pfb");c.original_external=obj("pfb","art/weapon.pfb")
    c.manager.fields={Motion=c.motion,DataContainer=c.original_root,DataContainerPath=c.original_root.path,StandardDataMap=obj("map")}
    c.external.fields={DataContainer=c.original_external,DataContainerPath=c.original_external.path,StandardDataMap=obj("map")}
    local env=setmetatable({_G={},os={time=function()return c.clock end},sdk={
        get_managed_singleton=function()return c.pm end,typeof=function(v)return v end,find_type_definition=function(v)return v end,
        call_native_func=function(_,_,_,a,b)return a==b end,
        create_instance=function(kind)return obj(kind)end,
        create_resource=function(_,path)if c.fail_load then return nil end;return obj("raw",path)end,
    }},{__index=_G})
    local adapter=assert(loadfile(root.."/fx_action_preview_runtime.lua","t",env))()
    local transaction=assert(loadfile(root.."/fx_action_preview_transaction.lua","t",env))()
    c.runtime=adapter.new(transaction,{capture=function()return {actor={sceneEpoch=c.sceneEpoch}}end,blocked=function()return c.blocked end})
    c.env=env;c.make=obj
    function c:plan(n,both)
        local token=string.rep(tostring(n or 1),32);local base="art/MotionVfxLive/ActionPreview/"..token.."/"
        local source={actorIdentity=tostring(self.character.address),managerIdentity=tostring(self.manager.address),motionIdentity=tostring(self.motion.address),
            bankId=0,weaponType=2,weaponId=89,sceneEpoch=1}
        local function pfb(o,idx,dataid,original,suffix)
            return {kind="pfb",path=base..suffix..".pfb",originalPath=original,provider={proof="live_prefab_path",externalIndex=idx,dataContainerId=dataid,
                ownerIdentity=tostring(o.address),prefabIdentity=tostring(o.fields.DataContainer.address),expectedElements={{container=1,element=80}}}}
        end
        local resources={{kind="motlist",path=base.."new.motlist",originalPath=self.original_motion.path},pfb(self.external,0,2,self.original_external.path,"weapon")}
        if both then resources[#resources+1]=pfb(self.manager,-1,0,self.original_root.path,"character")end
        return {schema=1,id=token,revision=n or 1,source=source,resources=resources}
    end
    function c:drain()for _=1,15 do if not self.runtime:snapshot().busy then break end;self.runtime:tick()end end
    function c:restore()self.runtime:restore();self:drain();check(self.runtime:snapshot().phase=="idle","Restoration reaches idle")end
    function c:clean()for _,o in ipairs(self.objects)do if o.kind=="raw" then assert(o.refs==0,"raw references "..o.refs)end end end
    return c
end
if arg[2]=="fixture" then return fixture end
do
    local c=fixture();check(c.runtime:apply(c:plan()),"Accepts paired MOTLIST and verified weapon PFB")
    c:drain();check(c.runtime:snapshot().phase=="active","Actual holder and new EPV element readback are required")
    check(c.original_motion.refs==1 and c.original_external.refs==1 and c.bank.holder.refs==1,
        "Idempotent managed pins survive prepared-resource cleanup and retain the baseline")
    check(c.writes[1]=="external" and c.writes[2]=="motion","PFB index is rebuilt before the motion bank changes")
    check(c.runtime:canonical(c.bank.holder.path)==c.original_motion.path,"Private motion aliases preserve verified source")
    c.runtime:apply(c:plan(2,true));c:drain()
    check(c.runtime:snapshot().phase=="active" and c.runtime:snapshot().appliedCount==3,"Later preview can add the character PFB to a weapon preview")
    c.writes={};c:restore()
    check(c.writes[1]=="motion" and c.bank.holder==c.original_motion and c.manager.fields.DataContainer==c.original_root and c.external.fields.DataContainer==c.original_external,
        "All motions restore before PFBs, including a PFB added on the second application");c:clean()
end
do
    local c=fixture();c.fail_motion=true;c.runtime:apply(c:plan(1,true));c:drain()
    check(c.runtime:snapshot().phase=="error" and c.bank.holder==c.original_motion and c.external.fields.DataContainer==c.original_external,"Post-setter failure rolls back actual native targets")
    c:restore();c:clean()
end
do
    local c=fixture();c.missing_element=true;c.runtime:apply(c:plan());c:drain()
    check(c.runtime:snapshot().phase=="error" and c.bank.holder==c.original_motion,"A PFB without the added EPV ID never activates the new motion")
    c:restore();c:clean()
end
do
    local c=fixture();c.missing_route=true;c.runtime:apply(c:plan());c:drain()
    check(c.runtime:snapshot().phase=="error" and c.bank.holder==c.original_motion,"Loaded PFB without actor dispatch never activates the new motion")
    c:restore();c:clean()
end
do
    local c=fixture();c.fail_before_update=true;c.runtime:apply(c:plan());c:drain()
    check(c.runtime:snapshot().phase=="error" and c.external.fields.RequestDataContainer==nil,"Rollback cancels an owned PFB request even before its reference changes")
    c:restore();c:clean()
end
do
    local c=fixture();c.runtime:apply(c:plan());c.runtime:tick();c.weapon=7;c:drain()
    check(c.runtime:snapshot().phase=="error" and #c.writes==0,"Equipment change cancels loading without touching old components");c:clean()
end
do
    local c=fixture();c.fail_load=true;c.runtime:apply(c:plan());c:drain()
    check(c.runtime:snapshot().phase=="error" and #c.writes==0,"Partial resource preparation releases all owned references");c:clean()
end
do
    local c=fixture();c.blocked=true;c.runtime:apply(c:plan());c:drain()
    check(c.runtime:snapshot().phase=="error" and #c.writes==0,"Active EFX override blocks PFB replacement");c:clean()
end
do
    local c=fixture();c.ready=false;c.runtime:apply(c:plan());c.runtime:tick();c.clock=16;c:drain()
    check(c.runtime:snapshot().phase=="error" and #c.writes==0,"Readiness timeout never modifies the actor");c:clean()
end
print("RESULT "..passed.." native-adapter checks; mocked SDK, no game writes")
