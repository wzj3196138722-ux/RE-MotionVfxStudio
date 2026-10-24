-- Actual equipment rebuilding, never a resource-preload success surrogate.
-- tick() must run at pre-LockScene. See tools/LiveEditor/equipment-native-proof.md.
local source_module="MotionVfxLive.fx_equipment_sources"
local source_ok,sources=pcall(require,source_module)
if not source_ok or type(sources)~="table"then
    local loader,err=loadfile("reframework/autorun/MotionVfxLive/fx_equipment_sources.lua")
    assert(loader,"装备来源模块未安装："..tostring(err));sources=loader()
end
assert(type(sources)=="table" and type(sources.resolve)=="function","装备来源模块无效")
package.loaded[source_module]=sources
local M={version="1.0.0"}
local function norm(s)return type(s)=="string" and s:gsub("\\","/"):lower() or ""end
local function resource_norm(s)return (norm(s):gsub("^@",""))end
local function id(o)return o and tostring(o:get_address())end
local function pin(o)if o then o:add_ref()end;return o end
local function copy(t)if type(t)~="table"then return t end;local r={};for k,v in pairs(t)do r[k]=copy(v)end;return r end
local function same(a,b)return id(a)==id(b)end
local function path(p)return p and p:call("get_Path") or ""end
local function bytes(s)
    assert(s:match("^[%w_/%.-]+$") and #s<512,"预览 PFB 路径无效")
    local b=string.pack("<I4I4",1,#s+1);for i=1,#s do b=b..string.pack("<I2",s:byte(i))end
    b=b..string.pack("<I2",0);return {b:byte(1,#b)}
end
local supported={['via.motion.Chain2']=true,['via.motion.Chain']=true,['via.character.CollisionShapePreset']=true,
    ['via.motion.JointConstraints']=true,['via.effect.script.ObjectEffectManager2']=true}
local function holder_path(o)return o and o:call("get_ResourcePath") or ""end
local function component_resources(o,class)
    local rows={}
    local function add(field,p,index,dataid)rows[#rows+1]={fieldName=field,path=p or "",index=index,dataContainerId=dataid}end
    if class=="via.motion.Chain2" or class=="via.motion.Chain"then add("ChainAsset",holder_path(o:call("get_ChainAsset")),-1)
    elseif class=="via.character.CollisionShapePreset"then
        local n=o:call("getCollisionShapePresetInfosCount");assert(n>=0 and n<=512,"碰撞资源数量异常")
        for i=0,n-1 do local info=assert(o:call("getCollisionShapePresetInfos(System.UInt32)",i));add("CollisionShapePresetInfos",holder_path(info:call("get_Resource")),i)end
    elseif class=="via.motion.JointConstraints"then
        local n=o:call("getLayerCount");assert(n>=0 and n<=512,"关节约束资源数量异常")
        for i=0,n-1 do local layer=assert(o:call("getLayer(System.Int32)",i));add("Layer",holder_path(layer:call("get_JointConstraintsAsset")),i)end
    elseif class=="via.effect.script.ObjectEffectManager2"then
        add("DataContainer",path(o:get_field("DataContainer")),-1)
        local n=o:call("getExternalDataContainerNum");assert(n>=0 and n<=64,"装备特效容器数量异常")
        for i=0,n-1 do local ext=assert(o:call("getExtenalDataConatiner(System.Int32)",i));add("ExternalDataContainers",path(ext:get_field("DataContainer")),i,ext:call("get_dataContainerId"))end
    end
    return rows
end
local function capture_components(root)
    local result,visited,total={},{},0
    local function walk(go,names)
        assert(not visited[id(go)],"装备对象层级循环");visited[id(go)]=true
        total=total+1;assert(total<=1024 and #names<=64 and #result<1024,"装备对象或组件层级数量异常")
        local components=go:call("get_Components");local n=components:get_size();assert(n>=0 and n<=256,"装备组件数量异常")
        local classes={}
        for i=0,n-1 do local o=components:get_element(i)
            if o then local class=o:get_type_definition():get_full_name()
                if supported[class]then local ci=classes[class] or 0;classes[class]=ci+1
                    result[#result+1]={gameObjectNames=copy(names),className=class,classIndex=ci,enabled=o:call("get_Enabled"),resources=component_resources(o,class)}
                end
            end
        end
        local transform=go:call("get_Transform");local child=transform and transform:call("get_Child");local count=0
        while child do
            count=count+1;assert(count<=256,"装备子对象数量异常")
            local next_go=child:call("get_GameObject");local next_names=copy(names);next_names[#next_names+1]=next_go:call("get_Name")
            walk(next_go,next_names);child=child:call("get_Next")
        end
    end
    walk(root,{root:call("get_Name")});return result
end
local function verify_components(actual,expected)
    -- The engine may rename the instantiated root. The exact loader slot binds
    -- that root; descendant names and same-class component indices remain exact.
    local function key(row)
        local names=assert(row.gameObjectNames,"PFB 组件缺少对象路径");assert(#names>=1,"PFB 组件对象路径为空")
        return table.concat(names,"/",2).."|"..assert(row.className).."|"..tostring(row.classIndex or 0)
    end
    local map={};for _,r in ipairs(actual)do local k=key(r);assert(not map[k],"装备子对象路径存在重名，不能可靠校验");map[k]=r end
    local n=0
    for _,r in ipairs(expected)do
        assert(supported[r.className],"不支持的装备组件验证类型")
        local k=key(r);local got=assert(map[k],"装备实例未创建组件："..k);map[k]=nil;n=n+1
        assert(got.enabled==r.enabled,"装备组件启用状态与导出文件不符："..k)
        assert(#got.resources==#(r.resources or {}),"装备资源槽位数量与导出文件不符："..k)
        for i,want in ipairs(r.resources or {})do local value=got.resources[i]
            assert(value.fieldName==want.fieldName and value.index==want.index and value.dataContainerId==want.dataContainerId and resource_norm(value.path)==resource_norm(want.path),
                "装备实例资源未切换："..k.." / "..tostring(want.fieldName).."["..tostring(want.index).."]")
        end
    end
    assert(n==#actual,"装备实例仍有导出文件中不存在的旧组件")
    return n
end
function M.new(host)
    local self={state={phase="idle",busy=false,requiresRestore=false,appliedCount=0,kind="equipment"}}
    local aliases=_G.MotionVfxEquipmentPreviewAliases or {};_G.MotionVfxEquipmentPreviewAliases=aliases
    local wrapped={capture=host.capture,aliases=function()
        local all=copy(host.aliases and host.aliases() or {});for k,v in pairs(aliases)do all[k]=v end;return all
    end}
    local function state(phase,message,busy)
        self.state.phase=phase;self.state.message=message;self.state.busy=busy==true
        self.state.requiresRestore=self.baseline~=nil;self.state.appliedCount=self.baseline and 1 or 0
    end
    function self:resolve(original)local ok,a=pcall(sources.resolve,wrapped,original);if ok then return a end;return nil,tostring(a)end
    local function blocked()assert(not(host.blocked and host.blocked()),"请先结束其他资源操作并还原动作槽位预览")end
    local function world(t,stable)
        local p,c=t.proof,t.context;local actor=assert(host.capture().actor,"角色尚未就绪，装备原始备份仍保留")
        assert(actor.characterIdentity==p.actorIdentity and actor.sceneEpoch==p.sceneEpoch,"角色或场景已变化，装备原始备份仍保留")
        local pm=assert(sdk.get_managed_singleton("app.PlayerManager"));local master=assert(pm:call("getMasterPlayer"));local ch=assert(master:call("get_Character"))
        assert(id(ch)==p.actorIdentity and id(c.control:get_field("_HunterLoader"))==p.loaderIdentity and
            id(c.control:get_field("_PlayerInfo"):call("get_Character"))==p.actorIdentity,"当前装备加载器归属已变化")
        assert(c.loader:call("get_NowLoading")==false,"游戏正在切换装备，暂不覆盖")
        if stable then
            assert(ch:call("get_WeaponType")==p.weaponType and ch:call("get_WeaponID")==p.weaponId and ch:call("get_WeaponModelID")==p.weaponModelId,"武器已由游戏更换，原始备份仍保留")
            assert(ch:call(p.category=="weapon" and "isWeaponBuildEnd" or "isPartsBuildEnd"),"装备仍在构建")
        end
        return ch
    end
    local function root(t)
        local ch=t.context.character
        if t.proof.category=="weapon"then local weapon=assert(ch:call(t.proof.getter),"重建后的武器尚未出现");return assert(weapon:call("get_GameObject"))end
        return assert(ch:call("getParts(app.ArmorDef.ARMOR_PARTS)",t.proof.partsIndex),"重建后的防具尚未出现")
    end
    local function check_slots(t,values)
        for _,v in ipairs(values)do assert(same(t.context.array:get_element(v.index),v.value),"装备缓存已被其他操作替换，保留原始备份")end
    end
    local function values(t,newvalue)
        local out={}
        if t.proof.category=="weapon"then
            for _,v in ipairs(t.context.weaponPrefabs)do out[#out+1]={index=v.index,role=v.role,value=v.index==t.proof.loadIndex and newvalue or t.context.array:get_element(v.index)}end
        else out[1]={index=t.proof.loadIndex,role=t.proof.role,value=newvalue}end
        return out
    end
    local function preserve(list)local out={};for _,v in ipairs(list)do out[#out+1]={index=v.index,role=v.role,value=pin(v.value)}end;return out end
    local function rebuild(t,wanted,expected)
        world(t,true);check_slots(t,expected)
        -- Record the mutation before either setter or native rebuild can throw.
        t.mutated=true
        -- REFramework exposes SystemArray assignment through __newindex, not a
        -- Lua set_element method. Unchanged empty companion slots are untouched.
        for _,v in ipairs(wanted)do
            if not same(t.context.array:get_element(v.index),v.value)then
                assert(v.value,"不能清除未拥有的装备缓存槽");t.context.array[v.index]=v.value
            end
        end
        if t.proof.category=="weapon"then
            t.context.character:call("changeWeapon(app.WeaponDef.TYPE, System.Int32, via.Prefab, via.Prefab, via.Prefab, via.Prefab)",
                t.proof.weaponType,t.proof.weaponModelId,wanted[1].value,wanted[2].value,wanted[3].value,wanted[4].value)
        else
            assert(id(t.context.control:get_field("_CreateOption"))==t.proof.createOptionIdentity,"装备构建选项已变化")
            t.context.character:call("changeParts(System.Int32, via.Prefab, ace.Bitset`1<app.HunterDef.CREATE_HUNTER_OPTION>)",
                t.proof.partsIndex,wanted[1].value,t.context.createOption)
        end
    end
    local function ready(t)
        local ch=world(t,false);return ch:call(t.proof.category=="weapon" and "isWeaponBuildEnd" or "isPartsBuildEnd")
    end
    local function confirm(t,wanted,components,beforeRoot)
        world(t,true);check_slots(t,wanted)
        local go=root(t);assert(go:call("get_Valid"),"重建后的装备对象无效")
        assert(id(go)~=beforeRoot,"装备对象没有被重建，不能只以资源已加载判断成功")
        local count=verify_components(capture_components(go),components)
        return id(go),count
    end
    local function drop()self.pending=nil;self.request=nil;self.transaction=nil end
    function self:apply(plan)
        if self.state.busy then return false,"装备 PFB 操作正在进行"end
        if self.state.phase=="restore_failed"then return false,"请先完成上次装备还原"end
        if type(plan)~="table" or plan.schema~=1 or type(plan.id)~="string" or #plan.id~=32 or not plan.id:match("^[a-f0-9]+$") or
            type(plan.source)~="table" or plan.source.pfbType~="equipment" or type(plan.source.equipmentComponents)~="table" or type(plan.resources)~="table" or #plan.resources~=1 then return false,"装备 PFB 预览计划无效或缺少组件回读信息"end
        local r=plan.resources[1]
        if r.kind~="pfb" or type(r.path)~="string" or not r.path:match("^art/MotionVfxLive/PrefabPreview/"..plan.id.."/[%w_%-]+%.pfb$") or norm(r.originalPath)~=norm(plan.source.originalPath)then return false,"装备 PFB 发布路径无效"end
        if self.baseline and norm(r.originalPath)~=norm(self.baseline.proof.originalPath)then return false,"请先还原当前装备 PFB，再预览另一个装备文件"end
        self.request=copy(plan);self.cancel=false;state("prepare","正在核实装备 PFB 的实际加载来源",true);return true
    end
    function self:restore()
        self.cancel=true
        if self.state.phase=="building" or self.state.phase=="rollback" or self.state.phase=="restore_wait"then return true end
        if not self.baseline then drop();state("idle","没有需要还原的装备 PFB",false);return true end
        state("restore","正在还原原始装备 PFB 并重建装备",true);return true
    end
    local function start_restore(t,to,expected,components,beforeRoot,rollback)
        check_slots(t,expected);rebuild(t,to,expected)
        self.restoring={target=t,values=to,components=components,beforeRoot=beforeRoot,rollback=rollback}
        self.started=os.time();state("restore_wait","等待原始装备重新构建",true)
    end
    function self:tick()
        if not self.state.busy then return end
        local phase=self.state.phase
        local ok,err=pcall(function()
            if phase=="prepare"then
                blocked();local request=self.request;local p,c=sources.resolve(wrapped,request.source.originalPath)
                if request.proof then for _,k in ipairs({"actorIdentity","sceneEpoch","loaderIdentity","controlIdentity","role","loadIndex","weaponType","weaponId","weaponModelId"})do
                    assert(request.proof[k]==p[k],"装备绑定证明已过期，请重新核实目标")end end
                if self.baseline then world(self.baseline,true);check_slots(self.baseline,self.currentValues)end
                local t={proof=p,context=c,beforeRoot=id(c.gameObject),beforeComponents=capture_components(c.gameObject),previousCurrent=self.currentValues}
                t.before=preserve(values(t,c.target.value));self.transaction=t
                local pending=pin(sdk.create_instance("via.Prefab"));self.pending=pending;pending:deserialize_native(bytes(request.resources[1].path),{})
                assert(norm(path(pending))==norm(request.resources[1].path),"加载的装备 PFB 路径不一致")
                self.started=os.time();state("loading","等待装备 PFB 与依赖资源加载",true)
            elseif phase=="loading"then
                blocked();local t=self.transaction;world(t,true);check_slots(t,t.before)
                assert(os.time()-self.started<=20,"装备 PFB 加载超时，尚未替换装备")
                if self.pending:call("get_Ready") and self.pending:call("get_Valid")then
                    if not self.baseline then
                        self.baseline=t;t.firstBaseline=true;pin(t.context.character);pin(t.context.control);pin(t.context.loader);pin(t.context.createOption)
                    end
                    t.after=preserve(values(t,self.pending));self.currentValues=t.after
                    rebuild(t,t.after,t.before);self.started=os.time();state("building","游戏正在重建当前装备和新增组件",true)
                end
            elseif phase=="building"then
                local t=self.transaction;assert(os.time()-self.started<=30,"装备重建超时，原始备份仍保留")
                if ready(t)then
                    if self.cancel then state("restore","正在恢复编辑前的装备",true);return end
                    local valid,actual,count=pcall(confirm,t,t.after,self.request.source.equipmentComponents,t.beforeRoot)
                    if not valid then
                        t.verifyStarted=t.verifyStarted or os.time()
                        assert(os.time()-t.verifyStarted<3,tostring(actual));return
                    end
                    self.state.gameObjectIdentity=actual;self.state.verifiedComponents=count
                    self.currentComponents=copy(self.request.source.equipmentComponents)
                    local r=self.request.resources[1];aliases[norm(r.path)]=r.originalPath
                    self.state.appliedId=self.request.id;self.state.appliedRevision=self.request.revision;self.state.originalPath=r.originalPath
                    self.baseline.currentRoot=actual;drop();state("active","当前装备已重新构建，已回读核对特效、物理和碰撞等资源槽位",false)
                end
            elseif phase=="restore"then
                local t=self.baseline;world(t,true)
                -- A previous restore can throw after updating only the cache.
                -- Accept only our original/preview references, then perform a
                -- full rebuild again. Never adopt an unrelated live reference.
                local actual={}
                for i,v in ipairs(self.currentValues)do local value=t.context.array:get_element(v.index)
                    assert(same(value,v.value) or same(value,t.before[i].value),"装备缓存存在其他修改，未覆盖")
                    actual[i]={index=v.index,role=v.role,value=value}
                end
                local beforeRoot=id(root(t));start_restore(t,t.before,actual,t.beforeComponents,beforeRoot,false)
            elseif phase=="rollback"then
                local t=self.transaction;assert(os.time()-self.started<=30,"等待装备回退超时，原始备份仍保留")
                if ready(t)then
                    -- A throwing array setter can leave a mix of old/new values.
                    local actual={};for i,v in ipairs(t.after or t.before)do local p=t.context.array:get_element(v.index)
                        assert(same(p,v.value) or same(p,t.before[i].value),"装备缓存存在其他修改，未覆盖")
                        actual[i]={index=v.index,role=v.role,value=p}
                    end
                    start_restore(t,t.before,actual,t.beforeComponents,id(root(t)),true)
                end
            elseif phase=="restore_wait"then
                local r=self.restoring;assert(os.time()-self.started<=30,"原始装备重建超时，备份仍保留")
                if ready(r.target)then
                    local valid,message=pcall(confirm,r.target,r.values,r.components,r.beforeRoot)
                    if not valid then
                        r.verifyStarted=r.verifyStarted or os.time()
                        assert(os.time()-r.verifyStarted<3,tostring(message));return
                    end
                    if r.rollback and self.cancel and self.baseline~=r.target then
                        self.currentValues=r.values;self.restoring=nil;drop();state("restore","正在继续还原首次编辑前的装备",true)
                    elseif r.rollback and not self.cancel then
                        self.currentValues=r.values
                        if self.baseline==r.target then self.baseline=nil;self.currentValues=nil end
                        self.restoring=nil;drop();state("error","装备应用失败，已重新构建应用前的装备："..self.failure,false)
                    else
                        self.baseline=nil;self.currentValues=nil;self.currentComponents=nil;self.restoring=nil;drop()
                        self.state.appliedId=nil;self.state.appliedRevision=nil;self.state.originalPath=nil;self.state.gameObjectIdentity=nil
                        state("idle","已恢复原始装备 PFB、资源槽位与装备实例",false)
                    end
                end
            end
        end)
        if not ok then
            if phase=="restore" or phase=="restore_wait" or phase=="rollback"then state("restore_failed","装备还原未完成，原始备份仍保留："..tostring(err),false)
            elseif self.transaction and self.transaction.mutated then self.failure=tostring(err);self.started=os.time();state("rollback","装备应用失败，正在重建应用前的装备",true)
            else
                local t=self.transaction
                if t then
                    if t.after and self.currentValues==t.after then self.currentValues=t.previousCurrent end
                    if t.firstBaseline and self.baseline==t then self.baseline=nil end
                end
                drop();state("error","装备尚未应用："..tostring(err),false)
            end
        end
    end
    function self:aliases()return copy(aliases)end
    function self:snapshot()local s=copy(self.state);s.aliases=self:aliases();return s end
    return self
end
return M
