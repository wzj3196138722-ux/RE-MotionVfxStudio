local source=assert(arg[1]);local count=0
local function eq(a,b) count=count+1;assert(a==b,tostring(a).." ~= "..tostring(b)) end
local function fixture(weapon)
    local nextid=100
    local function object(methods,fields)
        nextid=nextid+1;local o={methods=methods or {},fields=fields or {},address=nextid}
        function o:get_address() return self.address end
        function o:get_field(k) return self.fields[k] end
        function o:call(k,...)
            local v=self.methods[k];if type(v)=="function" then return v(...) end
            if v~=nil then return v end
            error("Unexpected engine call: "..k)
        end
        return o
    end
    local bankpath=string.format("Motion/Player/Weapon/Wp%02d/wp%02d_00/wp%02d_00.motlist",weapon,weapon,weapon)
    local pfbpath=string.format("Art/VFX/EffectProvider/Weapon/it%02d/epvr_it%02d.pfb",weapon,weapon)
    local common="Art/VFX/EffectProvider/Player/pl_cm/epvr_hm_cm.pfb"
    local holder=object({get_ResourcePath=bankpath});local bank=object({get_MotionList=holder})
    local motion=object({["findMotionBank(System.UInt32)"]=bank})
    local prefab=object({get_Path=pfbpath,get_Valid=true})
    local external=object({get_dataContainerId=weapon+100},{DataContainer=prefab,DataContainerPath=pfbpath})
    local manager=object({get_Valid=true,getExternalDataContainerNum=1,["getExtenalDataConatiner(System.Int32)"]=external,
        ["getExternalDataContainerIndexFromId(System.Int32)"]=function(n) return n==weapon+100 and 0 or -1 end},
        {Motion=motion,DataContainer=object({get_Path=common,get_Valid=true}),DataContainerPath=common})
    local go=object({["getComponent(System.Type)"]=manager})
    local character=object({get_GameObject=go,get_WeaponType=weapon,get_WeaponID=77})
    local player=object({get_Character=character});local pm=object({getMasterPlayer=player})
    local env=setmetatable({sdk={get_managed_singleton=function() return pm end,typeof=function(v)return v end,
        find_type_definition=function()return {get_field=function(_,name)
            local values={Kill=7,Invalid=-12345,Finish=9,UnparentFinish=10};local value=values[name]
            return value~=nil and {get_data=function()return value end} or nil
        end}end}},{__index=_G})
    local module=assert(loadfile(source,"t",env))()
    local record={id="action",componentKind="character",actorIdentity=tostring(character.address),sceneEpoch=4,weaponType=weapon,weaponId=77,
        motion={motionId=312,bankId=20,componentIdentity=tostring(motion.address),bankResourcePath=bankpath,name="attack"}}
    local snapshot={actor={sceneEpoch=4,characterIdentity=tostring(character.address)}}
    return module,record,snapshot,manager,external,prefab
end
for weapon=0,13 do
    local m,r,s,manager,external,prefab=fixture(weapon)
    local v=m.resolve(r,s);eq(#v.prefabs,2);eq(v.prefabs[1].externalIndex,-1);eq(v.prefabs[1].dataContainerId,0)
    eq(v.killEndType,7);eq(v.effectEndTypes.Invalid,-12345)
    eq(v.prefabs[2].dataContainerId,weapon+100);eq(v.prefabs[2].path,external.fields.DataContainerPath)
    eq(v.bankPath,r.motion.bankResourcePath);eq(v.motionId,312)
    r.sceneEpoch=3;eq(pcall(m.resolve,r,s),false);r.sceneEpoch=4
    r.weaponType=99;eq(pcall(m.resolve,r,s),false);r.weaponType=weapon
    r.motion.componentIdentity="stale";eq(pcall(m.resolve,r,s),false)
end
do
    local m,r,s,manager,external,prefab=fixture(2)
    external.fields.DataContainerPath="Art/guessed.pfb";eq(#m.resolve(r,s).prefabs,1)
    manager.fields.DataContainerPath="Art/also_guessed.pfb";eq(pcall(m.resolve,r,s),false)
end
do
    local m,r,s,manager,external,prefab=fixture(2)
    local original=prefab.methods.get_Path
    prefab.methods.get_Path="art/MotionVfxLive/ActionPreview/test/weapon.pfb"
    external.fields.DataContainerPath=prefab.methods.get_Path
    local aliases={[prefab.methods.get_Path:lower()]=original}
    local v=m.resolve(r,s,aliases)
    eq(v.prefabs[2].path,original);eq(v.prefabs[2].observedPath,prefab.methods.get_Path)
    local holder=manager.fields.Motion.methods["findMotionBank(System.UInt32)"].methods.get_MotionList
    local originalBank=holder.methods.get_ResourcePath
    holder.methods.get_ResourcePath="art/MotionVfxLive/ActionPreview/test/new.motlist"
    aliases[holder.methods.get_ResourcePath:lower()]=originalBank
    eq(m.resolve(r,s,aliases).bankPath,originalBank)
    r.motion.bankResourcePath=holder.methods.get_ResourcePath
    eq(m.resolve(r,s,aliases).bankPath,originalBank)
    r.motion.bankResourcePath=originalBank
    eq(pcall(m.resolve,r,s),false)
end
do
    local m,r,s=fixture(2);local proof=m.resolve(r,s)
    r.id="new-record-after-clear";s.actions={r}
    eq(m.find_record(proof,s),r)
    r.motion.motionId=999;eq(m.find_record(proof,s),nil);r.motion.motionId=proof.motionId
    r.weaponId=78;eq(m.find_record(proof,s),nil);r.weaponId=proof.weaponId
    r.sceneEpoch=5;eq(m.find_record(proof,s),nil)
end
print("RESULT action sources: "..count.." assertions, all 14 weapon types and character PFB; mocked engine only")
