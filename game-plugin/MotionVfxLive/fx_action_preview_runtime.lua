-- Wilds APIs verified through its running TDB. Invoked only at pre LockScene.
-- Resource construction is isolated; actor references change only after every
-- new resource is ready. No original installed file is replaced.
local M={version="1.2.0"}
local function id(o)return o and tostring(o:get_address())end
local function path(o,kind)return o:call(kind=="pfb" and "get_Path" or "get_ResourcePath")end
local function samepath(a,b)return type(a)=="string" and type(b)=="string" and a:lower()==b:lower()end
local function pin(o)assert(o,"资源引用为空");o:add_ref();return o end
-- REFramework's managed add_ref() is idempotent for each Lua userdata.
-- Multiple logical owners MUST NOT each call release(): that frees backups
-- while another owner still needs them. Keep strong Lua references instead;
-- userdata GC releases the SDK-owned managed reference after the last owner.
local function release(_)end
local function copy(t)if type(t)~="table" then return t end;local v={};for k,x in pairs(t)do v[k]=copy(x)end;return v end
local function native_prefab_bytes(value)
    -- RSZ Wilds via.Prefab f53c28f9/a0e05e19: Standby bool, align(4),
    -- Resource Path UTF-16. deserialize_native calls the engine deserializer.
    assert(value:match("^[%w_/%.-]+$") and #value<512,"预览 PFB 路径无效")
    local bytes=string.pack("<I4I4",1,#value+1)
    for i=1,#value do bytes=bytes..string.pack("<I2",value:byte(i))end
    bytes=bytes..string.pack("<I2",0)
    return {bytes:byte(1,#bytes)}
end
M.prefab_bytes=native_prefab_bytes
function M.new(transaction,host)
    -- Scalar aliases survive a controlled UI reload, so captured private paths
    -- still resolve to their verified source after all live refs are restored.
    local aliases=_G.MotionVfxActionPreviewAliases or {};_G.MotionVfxActionPreviewAliases=aliases
    local engine={now=os.time,retain=pin,release=release}
    local self={}
    function self:canonical(value)
        return type(value)=="string" and (aliases[value:lower()] or value) or value
    end
    function self:aliases()return copy(aliases)end
    local function context(proof)
        local snapshot=host.capture()
        assert(snapshot.actor and snapshot.actor.sceneEpoch==proof.sceneEpoch,"场景已变化，请还原后重新记录。")
        local pm=assert(sdk.get_managed_singleton("app.PlayerManager"),"角色尚未就绪")
        local ch=pm:call("getMasterPlayer"):call("get_Character")
        assert(id(ch)==proof.actorIdentity,"角色已变化，未访问旧角色引用。")
        assert(ch:call("get_WeaponType")==proof.weaponType and ch:call("get_WeaponID")==proof.weaponId,"装备已变化，请切回原装备后还原。")
        local manager=ch:call("get_GameObject"):call("getComponent(System.Type)",sdk.typeof("via.effect.script.ObjectEffectManager2"))
        assert(id(manager)==proof.managerIdentity and manager:call("get_Valid"),"特效管理器已重建，未访问旧组件。")
        local motion=manager:get_field("Motion")
        assert(id(motion)==proof.motionIdentity and motion:call("get_Valid"),"动作组件已重建。")
        local bank=assert(motion:call("findMotionBank(System.UInt32)",proof.bankId),"当前动作库已卸载")
        return {proof=proof,manager=manager,motion=motion,bank=bank}
    end
    local function owner(c,p)
        assert(c.manager:call("getExternalDataContainerIndexFromId(System.Int32)",p.dataContainerId)==p.externalIndex,"PFB 容器编号已变化")
        local o=p.externalIndex<0 and c.manager or c.manager:call("getExtenalDataConatiner(System.Int32)",p.externalIndex)
        assert(id(o)==p.ownerIdentity,"PFB 容器已重建")
        return o
    end
    local function current_target(t)
        local c=context(t.proof)
        if t.kind=="motlist" then
            assert(id(c.bank)==t.bankIdentity,"动作库对象已更换")
            return c.bank:call("get_MotionList"),c.bank,c
        end
        local o=owner(c,t.provider)
        return o:get_field("DataContainer"),o,c
    end
    local function equal(a,b,kind)
        if not a or not b then return a==b end
        local typename=kind=="pfb" and "via.Prefab" or "via.ResourceHolder"
        return samepath(path(a,kind),path(b,kind)) and sdk.call_native_func(nil,sdk.find_type_definition(typename),
            "equals("..typename..", "..typename..")",a,b)==true
    end
    function engine.resolve(plan)
        assert(not host.blocked(),"请先还原 EFX 资源试改，再热重载动作槽位；编辑内容会保留。")
        local c=context(plan.source);c.plan=plan;c.initial={}
        for i,r in ipairs(plan.resources)do
            local old,o
            if r.kind=="motlist" then old=c.bank:call("get_MotionList")
            else
                assert(r.provider and r.provider.proof=="live_prefab_path","PFB 来源未经核实")
                o=owner(c,r.provider);old=o:get_field("DataContainer")
                assert(id(old)==r.provider.prefabIdentity,"PFB 引用在发布期间发生变化")
                assert(samepath(o:get_field("DataContainerPath"),path(old,"pfb")),"PFB 正在切换，请稍后重试")
            end
            assert(samepath(self:canonical(path(old,r.kind)),r.originalPath),"动作/PFB 资源来源已变化")
            c.initial[i]={resource=r,value=old,owner=o}
        end
        return c
    end
    function engine.validate(c)
        assert(not host.blocked(),"其他资源操作正在进行，动作预览已取消")
        local current=context(c.proof);assert(id(current.bank)==id(c.bank),"动作库对象已变化")
        for _,item in ipairs(c.initial)do
            local r=item.resource
            local actual=r.kind=="motlist" and current.bank:call("get_MotionList") or owner(current,r.provider):get_field("DataContainer")
            assert(equal(actual,item.value,r.kind),"加载期间资源被其他操作更改")
        end
    end
    function engine.release_prepared(p)
        -- Raw REResource uses native counting, unlike managed holders/prefabs.
        for i=#p.rawOwned,1,-1 do p.rawOwned[i]:release();p.rawOwned[i]=nil end
        p.owned={};p.items={}
    end
    function engine.prepare(plan,c)
        local p={owned={},rawOwned={},items={}}
        local function own(o)o=pin(o);p.owned[#p.owned+1]=o;return o end
        local ok,err=pcall(function()
            for _,item in ipairs(c.initial)do own(item.value)end
            for _,r in ipairs(plan.resources)do
                local item={resource=r}
                if r.kind=="pfb" then
                    item.value=own(sdk.create_instance("via.Prefab"))
                    item.value:deserialize_native(native_prefab_bytes(r.path),{})
                else
                    local raw=assert(sdk.create_resource("via.motion.MotionListResource",r.path),"动作资源加载失败")
                    raw:add_ref();p.rawOwned[#p.rawOwned+1]=raw
                    item.value=own(raw:create_holder("via.motion.MotionListResourceHolder"))
                    item.probe=own(sdk.create_instance("via.motion.MotionBank"))
                    item.probe:call("set_MotionList(via.motion.MotionListBaseResourceHolder)",item.value)
                end
                assert(samepath(path(item.value,r.kind),r.path),"新资源路径回读不一致")
                p.items[#p.items+1]=item
            end
        end)
        if not ok then engine.release_prepared(p);error(err,0)end
        return p
    end
    function engine.ready(p)
        for _,item in ipairs(p.items)do
            if item.resource.kind=="pfb" then
                if not item.value:call("get_Ready") or not item.value:call("get_Valid") then return false end
            elseif not item.probe:call("get_MotionListValid") or item.probe:call("get_MotionListEmpty") then return false end
        end
        return true
    end
    function engine.targets(c,p)
        local targets={}
        for _,item in ipairs(p.items)do
            local r=item.resource
            targets[#targets+1]={key=r.kind=="pfb" and ("pfb|"..r.provider.ownerIdentity) or ("motion|"..c.proof.motionIdentity.."|"..c.proof.bankId),
                kind=r.kind,value=item.value,provider=r.provider,proof=c.proof,bankIdentity=id(c.bank),originalPath=r.originalPath}
        end
        return targets
    end
    function engine.read(t)return current_target(t)end
    local function write(t,v,expected)
        local actual,o=current_target(t)
        assert(equal(actual,expected,t.kind),"资源引用被其他操作更改；未覆盖其他修改")
        if t.kind=="motlist" then o:call("set_MotionList(via.motion.MotionListBaseResourceHolder)",v)
        else
            -- Both APIs consume RequestDataContainer in updateDataContainer.
            -- Root has an ambiguous multi-int convenience method; use its
            -- named request field rather than guessing integer argument order.
            local pending=o:get_field("RequestDataContainer")
            assert(not pending or pending:call("get_IsEmpty"),"游戏已有 PFB 加载请求，请稍后重试")
            assert(o:get_field("RequestDeleteDataContainer")~=true,"游戏正在删除该 PFB 容器")
            if t.provider.externalIndex<0 then o:set_field("RequestDataContainer",v)
            else o:call("requestSetDataContainer(via.Prefab)",v)end
            o:call("updateDataContainer()")
        end
    end
    engine.write=write
    function engine.verify(t,v)
        local actual,o,c=current_target(t)
        assert(equal(actual,v,t.kind),"新资源未被实际引用")
        if t.kind=="motlist" then
            assert(o:call("get_MotionListValid") and not o:call("get_MotionListEmpty"),"实际动作库未就绪")
            assert(equal(o:call("get_TargetMotionList"),v,t.kind),"动作库的目标资源尚未切换")
        else
            assert(samepath(o:get_field("DataContainerPath"),path(v,"pfb")),"PFB 容器尚未重建")
            local map=assert(o:get_field("StandardDataMap"),"PFB 特效索引未创建")
            for _,e in ipairs(t.provider.expectedElements or {})do
                local provider=assert(map:call("get_Item(System.UInt32)",e.container),"新增特效容器未加载")
                assert(provider:call("getElementFromID(System.UInt32)",e.element),"新增特效编号未加载")
                -- This getter accepts the resolved external INDEX, unlike a
                -- VFX track's DataContainerId. Verify actor-side dispatch too:
                -- a populated private PFB map alone is not proof it is used.
                local routed=c.manager:call("getStandardData(System.UInt32, System.Int32, System.Boolean)",e.container,t.provider.externalIndex,false)
                assert(routed and id(routed)==id(provider),"角色特效管理器未路由到新增 PFB 槽位")
            end
        end
        aliases[path(v,t.kind):lower()]=t.originalPath
    end
    function engine.restore(t,v,expected)
        local actual,o=current_target(t)
        if t.kind=="pfb" then
            local pending=o:get_field("RequestDataContainer")
            if pending and not pending:call("get_IsEmpty") then
                assert(expected and equal(pending,expected,"pfb"),"PFB 存在其他加载请求；保留备份")
                o:set_field("RequestDataContainer",nil)
            end
        end
        if equal(actual,v,t.kind) then return end
        assert(expected and equal(actual,expected,t.kind),"还原目标已被替换，保留备份等待重试")
        write(t,v,expected)
        local restored,o=current_target(t)
        assert(equal(restored,v,t.kind),"原始引用尚未恢复")
        if t.kind=="pfb" then assert(samepath(o:get_field("DataContainerPath"),path(v,"pfb")),"原始 PFB 索引尚未重建")end
    end
    local runtime=transaction.new(engine)
    function self:apply(plan)return runtime:apply(plan)end
    function self:restore()return runtime:restore()end
    function self:tick()runtime:tick()end
    function self:snapshot()local s=runtime:snapshot();s.aliases=self:aliases();return s end
    return self
end
return M
