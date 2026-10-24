-- Read-only provenance for the selected actor action. No filename guessing,
-- pointer resurrection, resource creation, or live-object setters are permitted here.
local M={}
local function call(o,m,...)
    if not o then return nil end
    local ok,v=pcall(o.call,o,m,...);if ok then return v end
end
local function field(o,k) if not o then return nil end local ok,v=pcall(o.get_field,o,k);if ok then return v end end
local function id(o) if not o then return nil end local ok,v=pcall(o.get_address,o);if ok then return tostring(v) end end
function M.find_record(proof,snapshot,aliases)
    if type(proof)~="table" then return nil end
    local function canonical(p)return type(p)=="string" and (((aliases or {})[p:lower()] or p):lower())end
    local records=snapshot.actions or {}
    for i=#records,1,-1 do
        local r=records[i];local m=r.motion or {}
        if r.componentKind=="character" and r.sceneEpoch==proof.sceneEpoch and r.actorIdentity==proof.actorIdentity and
            r.weaponType==proof.weaponType and r.weaponId==proof.weaponId and m.componentIdentity==proof.motionIdentity and
            m.bankId==proof.bankId and m.motionId==proof.motionId and canonical(m.bankResourcePath)==canonical(proof.bankPath) then return r end
    end
end
function M.resolve(record,snapshot,aliases,attachments)
    local function canonical(p)return type(p)=="string" and ((aliases or {})[p:lower()] or p) or p end
    assert(record and record.componentKind=="character","请选择角色动作记录。")
    local actor=snapshot.actor or {}
    assert(record.sceneEpoch==actor.sceneEpoch and record.actorIdentity==actor.characterIdentity,"记录所属角色或场景已经变化，请重新记录。")
    local character=call(call(sdk.get_managed_singleton("app.PlayerManager"),"getMasterPlayer"),"get_Character")
    assert(id(character)==actor.characterIdentity,"当前角色与记录不一致。")
    assert(call(character,"get_WeaponType")==record.weaponType and call(character,"get_WeaponID")==record.weaponId,"装备已变化，请重新记录。")
    local go=call(character,"get_GameObject")
    local manager=call(go,"getComponent(System.Type)",sdk.typeof("via.effect.script.ObjectEffectManager2"))
    assert(call(manager,"get_Valid")==true,"角色特效管理器不可用。")
    local motion=field(manager,"Motion")
    assert(id(motion)==record.motion.componentIdentity,"特效管理器未关联此动作组件。")
    local bank=call(motion,"findMotionBank(System.UInt32)",record.motion.bankId)
    local bankpath=call(call(bank,"get_MotionList"),"get_ResourcePath")
    assert(bankpath and canonical(bankpath):lower()==canonical(record.motion.bankResourcePath or ""):lower(),"动作表来源已变化。")
    local result={actionId=record.id,motionId=record.motion.motionId,motionName=record.motion.name,
        bankPath=canonical(bankpath),observedBankPath=bankpath,bankId=record.motion.bankId,weaponType=record.weaponType,weaponId=record.weaponId,
        sceneEpoch=actor.sceneEpoch,actorIdentity=actor.characterIdentity,
        managerIdentity=id(manager),motionIdentity=id(motion),proof="manager_motion_and_prefab",prefabs={}}
    local enum=sdk.find_type_definition("via.effect.script.EffectManager.EffectEndType")
    local kill=enum and enum:get_field("Kill")
    result.killEndType=kill and tonumber(kill:get_data(nil))
    assert(result.killEndType and result.killEndType>=0,"未能读取引擎的特效停止类型。")
    -- Resolve named native values, including the signed Invalid sentinel.
    -- Never infer enum ordinals from declaration order or an offline fixture.
    result.effectEndTypes={Kill=result.killEndType}
    for _,name in ipairs({"Invalid","Finish","UnparentFinish"})do
        local f=enum:get_field(name)
        if f then local ok,value=pcall(f.get_data,f,nil);if ok then result.effectEndTypes[name]=tonumber(value)end end
    end
    local function add(owner,index,dataid)
        -- DataContainerId 0 selects the manager's own table only if it is not
        -- assigned to an external container. Verify this against the engine.
        if tonumber(call(manager,"getExternalDataContainerIndexFromId(System.Int32)",dataid))~=index then return end
        local prefab=field(owner,"DataContainer")
        local path=call(prefab,"get_Path")
        local observed=field(owner,"DataContainerPath")
        if type(path)~="string" or path=="" or call(prefab,"get_Valid")~=true or
            type(observed)~="string" or path:lower()~=observed:lower() then return end
        result.prefabs[#result.prefabs+1]={path=canonical(path),observedPath=path,externalIndex=index,dataContainerId=dataid,
            ownerIdentity=id(owner),prefabIdentity=id(prefab),proof="live_prefab_path"}
    end
    add(manager,-1,0)
    local n=tonumber(call(manager,"getExternalDataContainerNum")) or 0
    assert(n>=0 and n<=64,"外部特效容器数量异常。")
    for i=0,n-1 do
        local external=call(manager,"getExtenalDataConatiner(System.Int32)",i)
        local dataid=tonumber(call(external,"get_dataContainerId"))
        if dataid and dataid>=0 then add(external,i,dataid) end
    end
    assert(#result.prefabs>0,"未读到可核实的 PFB 引用；不会使用同名文件替代。")
    if attachments then
        local ok,info=pcall(attachments.collect,character,manager)
        result.attachmentTargets=ok and info.targets or {}
        result.attachmentRelationTypes=ok and info.relationTypes or {}
        result.attachmentWarnings=ok and info.warnings or {tostring(info)}
    end
    return result
end
return M
