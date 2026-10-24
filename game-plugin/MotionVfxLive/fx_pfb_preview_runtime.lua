-- Independent EPV prefab activation. tick() MUST run only before LockScene.
-- Loading a prefab is not activation: every matched live manager/container is
-- rebuilt, then its actual prefab and EPV lookup table are read back.
local M={version="1.1.0"}
local function copy(t)if type(t)~="table"then return t end;local r={};for k,v in pairs(t)do r[k]=copy(v)end;return r end
local function id(o)return o and tostring(o:get_address())end
local function pin(o)assert(o,"PFB 引用为空");o:add_ref();return o end
local function path(o)return o and o:call("get_Path")end
local function norm(s)return type(s)=="string" and s:gsub("\\","/"):lower()end
local function eq(a,b)
    if not a or not b then return a==b end
    return norm(path(a))==norm(path(b)) and sdk.call_native_func(nil,sdk.find_type_definition("via.Prefab"),"equals(via.Prefab, via.Prefab)",a,b)==true
end
local function bytes(s)
    assert(s:match("^[%w_/%.-]+$") and #s<512,"预览 PFB 路径无效")
    local b=string.pack("<I4I4",1,#s+1)
    for i=1,#s do b=b..string.pack("<I2",s:byte(i))end
    b=b..string.pack("<I2",0);return {b:byte(1,#b)}
end
local function new_effect(host)
    assert(type(host)=="table" and type(host.capture)=="function","PFB 预览缺少当前角色来源")
    local aliases=_G.MotionVfxPrefabPreviewAliases or {};_G.MotionVfxPrefabPreviewAliases=aliases
    local self={state={phase="idle",busy=false,appliedCount=0,requiresRestore=false},originals={},current={},order={},changes={}}
    local function canonical(p)
        local key=norm(p);if not key then return nil end
        local external=host.aliases and host.aliases() or {}
        return norm(aliases[key] or external[key] or p)
    end
    local function refresh()
        local n=0;for _ in pairs(self.current)do n=n+1 end
        self.state.appliedCount=n;self.state.requiresRestore=#self.order>0 or #self.changes>0
    end
    local function status(phase,message,busy)
        self.state.phase=phase;self.state.message=message;self.state.busy=busy==true;refresh()
    end
    local function world(proof)
        local actor=assert(host.capture().actor,"角色尚未就绪")
        local pm=assert(sdk.get_managed_singleton("app.PlayerManager"),"角色管理器尚未就绪")
        local player=assert(pm:call("getMasterPlayer"),"当前没有本地角色，请先进入游戏场景")
        local ch=assert(player:call("get_Character"),"本地角色尚未加载完成")
        assert(ch and id(ch)==actor.characterIdentity,"角色来源已变化")
        local manager=ch:call("get_GameObject"):call("getComponent(System.Type)",sdk.typeof("via.effect.script.ObjectEffectManager2"))
        assert(manager and manager:call("get_Valid"),"角色特效管理器尚未就绪")
        local now={actorIdentity=id(ch),sceneEpoch=actor.sceneEpoch,weaponType=ch:call("get_WeaponType"),
            weaponId=ch:call("get_WeaponID"),managerIdentity=id(manager)}
        if proof then
            for _,k in ipairs({"actorIdentity","sceneEpoch","weaponType","weaponId","managerIdentity"})do
                assert(now[k]==proof[k],"角色、场景或装备已变化；原始 PFB 备份仍保留，请回到原装备后重试")
            end
        end
        return manager,now
    end
    local function owner(proof,provider)
        local manager=world(proof)
        assert(manager:call("getExternalDataContainerIndexFromId(System.Int32)",provider.dataContainerId)==provider.externalIndex,"PFB 容器编号已变化")
        local o=provider.externalIndex<0 and manager or manager:call("getExtenalDataConatiner(System.Int32)",provider.externalIndex)
        assert(id(o)==provider.ownerIdentity,"PFB 容器已重建；保留备份")
        return o
    end
    local function discover(original)
        local target=norm(original)
        assert(target and (target:match("^art/") or target:match("^gamedesign/")) and target:match("%.pfb$") and not target:find("..",1,true),"请填写完整的游戏 PFB 路径（art/ 或 GameDesign/）")
        local manager,proof=world();proof.proof="live_prefab_path";proof.originalPath=original;proof.providers={}
        local function add(o,index,dataid)
            if not o or manager:call("getExternalDataContainerIndexFromId(System.Int32)",dataid)~=index then return end
            local value=o:get_field("DataContainer")
            if value and value:call("get_Valid") and canonical(path(value))==target then
                assert(norm(o:get_field("DataContainerPath"))==norm(path(value)),"目标 PFB 正在切换，请稍后重试")
                proof.providers[#proof.providers+1]={externalIndex=index,dataContainerId=dataid,ownerIdentity=id(o),prefabIdentity=id(value),observedPath=path(value)}
            end
        end
        add(manager,-1,0)
        local n=tonumber(manager:call("getExternalDataContainerNum"))
        assert(n and n>=0 and n<=64,"外部 PFB 容器数量异常")
        for i=0,n-1 do local o=manager:call("getExtenalDataConatiner(System.Int32)",i);if o then add(o,i,o:call("get_dataContainerId"))end end
        assert(#proof.providers>0,"当前角色没有持有此完整路径的特效 PFB；不会按文件名猜测替换目标")
        return proof
    end
    function self:resolve(original)
        local ok,result=pcall(discover,original);if ok then return result end;return nil,tostring(result)
    end
    local function blocked()assert(not(host.blocked and host.blocked()),"请先结束其他资源操作并还原动作槽位预览")end
    local function drop()self.pending=nil;self.request=nil;self.context=nil end
    local function read(t)local o=owner(t.proof,t.provider);return o:get_field("DataContainer"),o end
    local function write(t,value,expected)
        local current,o=read(t);assert(eq(current,expected),"PFB 被其他操作替换；未覆盖其他修改")
        local pending=o:get_field("RequestDataContainer")
        assert(not pending or pending:call("get_IsEmpty"),"游戏已有 PFB 加载请求")
        assert(o:get_field("RequestDeleteDataContainer")~=true,"游戏正在删除此 PFB 容器")
        if t.provider.externalIndex<0 then o:set_field("RequestDataContainer",value)
        else o:call("requestSetDataContainer(via.Prefab)",value)end
        o:call("updateDataContainer()")
    end
    local function verify(t,value,check_elements)
        local current,o=read(t)
        assert(eq(current,value) and norm(o:get_field("DataContainerPath"))==norm(path(value)),"PFB 引用或索引尚未切换")
        local map=assert(o:get_field("StandardDataMap"),"特效 PFB 索引未重建")
        if check_elements then for _,e in ipairs(t.expectedElements or {})do
            local element=e.id or e.element
            if (e.providerClass==nil or e.providerClass=="via.effect.script.EPVStandardData") and type(e.container)=="number" and e.container>=0 and type(element)=="number" then
                local provider=assert(map:call("get_Item(System.UInt32)",e.container),"新 PFB 特效容器未加载")
                assert(provider:call("getElementFromID(System.UInt32)",element),"新 PFB 特效槽位未加载")
                local manager=world(t.proof)
                local routed=manager:call("getStandardData(System.UInt32, System.Int32, System.Boolean)",e.container,t.provider.externalIndex,false)
                assert(routed and id(routed)==id(provider),"角色特效路由未指向新 PFB 容器")
            end
        end end
    end
    local function restore_one(t,value,expected)
        local current,o=read(t)
        local pending=o:get_field("RequestDataContainer")
        if pending and not pending:call("get_IsEmpty") then
            assert(expected and eq(pending,expected),"存在其他 PFB 请求；保留原始备份")
            o:set_field("RequestDataContainer",nil)
        end
        if not eq(current,value) then
            assert(expected and eq(current,expected),"PFB 已由其他操作替换；保留原始备份")
            write(t,value,expected)
        end
        verify(t,value,false)
    end
    function self:apply(plan)
        if self.state.busy then return false,"PFB 操作正在进行" end
        if self.state.phase=="restore_failed" then return false,"请先完成上次 PFB 还原" end
        if type(plan)~="table" or plan.schema~=1 or type(plan.id)~="string" or #plan.id~=32 or not plan.id:match("^[a-f0-9]+$") or
            type(plan.resources)~="table" or #plan.resources~=1 or type(plan.source)~="table" then return false,"PFB 预览计划无效"end
        local r=plan.resources[1]
        if r.kind~="pfb" or type(r.path)~="string" or not r.path:match("^art/MotionVfxLive/PrefabPreview/"..plan.id.."/[%w_%-]+%.pfb$") then return false,"PFB 预览资源路径无效"end
        if plan.source.pfbType~="effect" then return false,"此预览通道只重建特效 PFB；装备 PFB 必须使用已核实的装备重建通道"end
        if norm(r.originalPath)~=norm(plan.source.originalPath) then return false,"PFB 发布来源与目标不一致"end
        self.request=copy(plan);self.cancel=false;self.changes={}
        status("prepare","正在核实此 PFB 的全部实际持有者",true);return true
    end
    function self:restore()
        self.cancel=true
        if not self.state.busy and not self.request and not self.pending and #self.changes==0 and #self.order==0 then
            status("idle","没有需要还原的独立 PFB 预览",false);return true
        end
        if self.state.phase~="rollback" then status("restore","正在还原独立 PFB 预览",true)end
        return true
    end
    function self:tick()
        if not self.state.busy then return end
        local phase=self.state.phase
        local ok,err=pcall(function()
            if phase=="prepare" then
                blocked()
                local r=self.request.resources[1];local proof=discover(r.originalPath)
                if self.request.proof then
                    world(self.request.proof)
                    assert(norm(self.request.proof.originalPath)==norm(r.originalPath),"PFB 绑定证明不属于目标路径")
                end
                self.context={proof=proof,targets={}}
                for _,provider in ipairs(proof.providers)do
                    local t={key=proof.managerIdentity.."|"..provider.ownerIdentity,proof=proof,provider=provider,originalPath=r.originalPath,
                        expectedElements=self.request.source.expectedElements}
                    t.before=pin(read(t));self.context.targets[#self.context.targets+1]=t
                end
                local value=pin(sdk.create_instance("via.Prefab"));self.pending=value
                value:deserialize_native(bytes(r.path),{})
                assert(norm(path(value))==norm(r.path),"加载后的 PFB 路径不一致")
                self.started=os.time();status("loading","等待独立 PFB 资源加载",true)
            elseif phase=="loading" or phase=="activate" then
                blocked();world(self.context.proof)
                for _,t in ipairs(self.context.targets)do assert(eq(read(t),t.before),"加载期间 PFB 引用发生变化")end
                if phase=="loading" then
                    assert(os.time()-self.started<=15,"PFB 加载超时，尚未更换角色资源")
                    if self.pending:call("get_Ready") and self.pending:call("get_Valid") then status("activate","正在重建实际 PFB 容器",true)end
                else
                    for _,t in ipairs(self.context.targets)do
                        if not self.originals[t.key] then self.originals[t.key]={target=t,value=pin(t.before)};self.order[#self.order+1]=t.key end
                        local change={target=t,before=pin(t.before),after=self.pending};self.changes[#self.changes+1]=change
                        write(t,self.pending,t.before);verify(t,self.pending,true)
                    end
                    for _,change in ipairs(self.changes)do self.current[change.target.key]={target=change.target,value=pin(change.after)}end
                    local r=self.request.resources[1];aliases[norm(r.path)]=r.originalPath
                    self.state.appliedId=self.request.id;self.state.appliedRevision=self.request.revision;self.state.originalPath=r.originalPath
                    self.changes={};drop();status("active","实际特效 PFB 已重建，修改将在再次触发特效时生效",false)
                end
            elseif phase=="restore" or phase=="rollback" then
                for i=#self.changes,1,-1 do local c=self.changes[i];restore_one(c.target,c.before,c.after);table.remove(self.changes,i)end
                drop()
                if phase=="rollback" and not self.cancel then status("error","PFB 应用失败，已回退本次修改："..self.failure,false)
                else
                    for i=#self.order,1,-1 do
                        local key=self.order[i];local saved=self.originals[key];local current=self.current[key]
                        restore_one(saved.target,saved.value,current and current.value)
                        self.originals[key]=nil;self.current[key]=nil;table.remove(self.order,i)
                    end
                    self.state.appliedId=nil;self.state.appliedRevision=nil;self.state.originalPath=nil
                    status("idle","已恢复独立 PFB 预览前的全部原始引用",false)
                end
            end
        end)
        if not ok then
            if phase=="restore" or phase=="rollback" then status("restore_failed","PFB 还原未完成，原始备份仍保留："..tostring(err),false)
            elseif #self.changes>0 then self.failure=tostring(err);status("rollback","PFB 应用失败，正在回退已改动的容器",true)
            else drop();status("error","PFB 尚未应用："..tostring(err),false)end
        end
        refresh()
    end
    function self:aliases()return copy(aliases)end
    function self:snapshot()local s=copy(self.state);s.aliases=self:aliases();return s end
    return self
end
-- One facade keeps all independent PFB operations mutually exclusive. Loading
-- the equipment adapter is deferred so effect-only installations fail clearly.
function M.new(host)
    local effect=new_effect(host);local equipment;local selected=effect
    local function equip()
        if not equipment then
            local name="MotionVfxLive.fx_equipment_pfb_preview_runtime"
            local ok,module=pcall(require,name)
            if not ok or type(module)~="table"then
                local loader,err=loadfile("reframework/autorun/MotionVfxLive/fx_equipment_pfb_preview_runtime.lua")
                assert(loader,"装备预览模块未安装："..tostring(err));module=loader()
            end
            assert(type(module)=="table" and type(module.new)=="function","装备预览模块无效")
            package.loaded[name]=module;equipment=module.new(host)
        end
        return equipment
    end
    local function choose(kind)
        local target=kind=="equipment" and equip() or effect
        if target~=selected then
            local s=selected:snapshot();assert(not s.busy and not s.requiresRestore,"请先还原当前独立 PFB 预览，再切换其他类型")
        end
        selected=target;return target
    end
    local api={}
    function api:resolve(path,kind)local ok,a,b=pcall(function()return choose(kind):resolve(path)end);if ok then return a,b end;return nil,tostring(a)end
    function api:apply(plan)
        local ok,a,b=pcall(function()return choose(type(plan)=="table" and type(plan.source)=="table" and plan.source.pfbType):apply(plan)end)
        if ok then return a,b end;return false,tostring(a)
    end
    function api:tick()selected:tick()end
    function api:restore()return selected:restore()end
    function api:aliases()
        local a=effect:aliases();if equipment then for k,v in pairs(equipment:aliases())do a[k]=v end end;return a
    end
    function api:snapshot()local s=selected:snapshot();s.aliases=self:aliases();return s end
    return api
end
return M
