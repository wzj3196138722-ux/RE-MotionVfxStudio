-- Rebind the resources used by real EPV requests. Never starts an effect.
-- All engine mutations, including probe destruction and restoration, run at
-- pre LockScene. Call restore_all and wait before a full REFramework reset.
if _G.MotionVfxActionBindings then return _G.MotionVfxActionBindings end
local M = { version = "1.3.0" }
local capture, command, transaction
local records, aliases, retained, original_flags = {}, {}, {}, {}
local expired_records = {}
local session_overrides = {}
local observed_identity
local active, serial, idle_frames = true, 0, 0
local state = { phase="idle", message="尚未替换动作资源", busy=false, appliedCount=0,
    requiresRestart=false, requiresRestoreBeforeReset=false, sharedScope=true, contextInvalid=false,
    scope="game_session", followsNewBindings=true,
    scopeMessage="修改保留到游戏退出；持续更新新加载的匹配引用，主动还原后停止接管" }
local function fail(s) error(s, 0) end
local function warn(s) if log and log.warn then log.warn("[MotionVfxActionBindings] " .. tostring(s)) end end
local function copy(v)
    if type(v) ~= "table" then return v end
    local r={}; for k,x in pairs(v) do r[k]=copy(x) end; return r
end
local function logical(path)
    if type(path) ~= "string" or path == "" or path:find("%z") then fail("EFX路径为空或无效") end
    path=path:gsub("\\", "/"):gsub("%.5571972$", "")
    path=path:gsub("^@/?", "")
    local lower=path:lower()
    if lower:sub(1,12)=="natives/stm/" then path=path:sub(13)
    elseif lower:sub(1,13)=="/natives/stm/" then path=path:sub(14) end
    if path:match("^[A-Za-z]:") or path:match("^/") or path:find("..",1,true) or path:find(":",1,true) then fail("必须使用游戏逻辑资源路径") end
    if not path:lower():match("%.efx$") then fail("资源路径必须以.efx结尾") end
    return path
end
local function canonical(path)
    local p=logical(path):lower()
    for _=1,16 do if not aliases[p] then return p end; p=aliases[p] end
    fail("资源别名循环")
end
local function address(o) return o and tostring(o:get_address()) or nil end
local function pin(o) if o then o:add_ref() end; return o end
local function valid(o)
    if not o then return false end
    if sdk.is_managed_object then
        local managed=sdk.is_managed_object(o)
        if type(managed)~="boolean" then fail("无法确认托管对象有效性") end
        if not managed then return false end
    end
    local value=o:call("get_Valid")
    if type(value)~="boolean" then fail("对象有效性读取未返回布尔值") end
    return value
end
local function current_scene()
    local manager=sdk.get_native_singleton("via.SceneManager")
    return manager and sdk.call_native_func(manager,sdk.find_type_definition("via.SceneManager"),"get_CurrentScene")
end
local function identity()
    local scene=current_scene()
    local pm=sdk.get_managed_singleton("app.PlayerManager")
    local master=pm and pm:call("getMasterPlayer")
    local character=master and master:call("get_Character")
    local weapon=character and character:call("get_Weapon")
    if not scene or not character or not weapon then fail("角色、场景或装备尚未就绪") end
    -- These getters were verified against the running Wilds TDB. The IDs also
    -- detect changing equipment while reusing the same Weapon component.
    local weapon_id=character:call("get_WeaponID")
    local model_id=character:call("get_WeaponModelID")
    local weapon_type=character:call("get_WeaponType")
    if weapon_id == nil or model_id == nil or weapon_type == nil then fail("无法确认装备身份") end
    local result={scene=scene,sceneAddress=address(scene),characterAddress=address(character),weaponAddress=address(weapon),
        weaponId=tostring(weapon_id),modelId=tostring(model_id),weaponType=tostring(weapon_type)}
    result.token=table.concat({result.sceneAddress,result.characterAddress,result.weaponAddress,result.weaponId,result.modelId,result.weaponType},"|")
    return result
end
local function clear_context_diagnostic()
    state.contextInvalid=false; state.contextDiagnostic=nil
end
local function checked_identity()
    local ok,now=pcall(identity)
    if ok then return now end
    state.contextInvalid=true
    state.contextDiagnostic={reasonCode="context_unavailable",changedFields={},recoveryDisposition="retry_when_ready",restartRecommended=false}
    fail(tostring(now).."；等待角色和场景就绪后再试，尚未确认还原完成。")
end
local context_fields={
    {"sceneAddress","场景实例",true},{"characterAddress","角色实例",true},{"weaponAddress","武器实例",true},
    {"weaponId","装备编号"},{"modelId","武器模型"},{"weaponType","武器类型"},
}
local function same_context(expected)
    local now=checked_identity()
    if now.token ~= expected.token then
        local changes,labels,rebuilt={},{},false
        for _,field in ipairs(context_fields) do
            local before,after=expected[field[1]],now[field[1]]
            if before~=after then
                -- Only saved scalar identities are compared. Never inspect an
                -- old actor/provider merely to explain a rejected operation.
                changes[#changes+1]={field=field[1],before=tostring(before),after=tostring(after)}
                labels[#labels+1]=field[2]; rebuilt=rebuilt or field[3]==true
            end
        end
        local outstanding=next(records)~=nil or (transaction and #(transaction.changed or {})>0) or false
        local disposition=not outstanding and "retry_when_ready" or rebuilt and "restart_recommended" or "retry_original_context"
        state.contextInvalid=true
        state.contextDiagnostic={reasonCode="context_changed",changedFields=changes,recoveryDisposition=disposition,
            restartRecommended=disposition=="restart_recommended"}
        local guidance
        if not outstanding then guidance="本次尚未替换真实资源；状态稳定后重新操作。"
        elseif rebuilt then guidance="尚有资源试改未还原。请先保存编辑副本，再重启游戏解除本轮运行时试改；勿用 Reset scripts 代替还原。"
        else guidance="尚有资源试改未还原。可切回原装备及角色上下文后重试；若对象已重建或仍无法还原，请先保存编辑副本，再重启游戏解除本轮运行时试改。" end
        fail(table.concat(labels,"、").."已改变；未向身份不匹配的旧绑定写入。"..guidance)
    end
    return now
end
local function holder_path(holder)
    if not holder then fail("EFX资源持有器为空") end
    return logical(holder:call("get_ResourcePath"))
end
local function equal_holder(a,b)
    if not a or not b then return a == b end
    -- getResources can return another wrapper for the same resource. Use the
    -- engine's typed resource equality, never a guessed pointer at +0x10.
    local td=sdk.find_type_definition("via.ResourceHolder")
    if not td then fail("via.ResourceHolder类型不可用") end
    return sdk.call_native_func(nil,td,"equals(via.ResourceHolder, via.ResourceHolder)",a,b) == true
end
local function current_holder(binding)
    if not valid(binding.provider) then fail("EPV资源绑定已失效") end
    if binding.component and not valid(binding.component) then fail("EPV组件已失效") end
    local count=tonumber(binding.provider:call("getResourcesCount()"))
    if not count or binding.index < 0 or binding.index >= count then fail("EPV资源槽已改变") end
    return binding.provider:call("getResources(System.UInt64)",binding.index)
end
local function check(binding, expected)
    local actual=current_holder(binding)
    if not equal_holder(actual,expected) or holder_path(actual):lower() ~= holder_path(expected):lower() then fail("EPV资源已被其他操作更改，事务取消") end
end
local function cache_fields(provider)
    local td=provider:get_type_definition()
    local values={}
    for _,name in ipairs({"IgnoreEffectCache","EffectCache"}) do
        if td and td:get_field(name) then
            local value=provider:get_field(name)
            if type(value) ~= "boolean" then fail("缓存标记不是布尔字段: " .. name) end
            values[name]=value
        end
    end
    return values
end
local function write_flags(provider, values)
    for _,name in ipairs({"IgnoreEffectCache","EffectCache"}) do
        if values[name] ~= nil then
            provider:set_field(name,values[name])
            if provider:get_field(name) ~= values[name] then fail("缓存标记回读失败: " .. name) end
        end
    end
end
local function counts()
    local n=0; for _ in pairs(records) do n=n+1 end
    state.appliedCount=n; state.requiresRestoreBeforeReset=n>0 or #retained>0 or #expired_records>0
    state.expiredBackupCount=#expired_records
end
local function member_function(object,name)
    local ok,value=pcall(function() return object[name] end)
    return ok and type(value)=="function" and value or nil
end
local function read_component_array(collection)
    if collection==nil then fail("无法扫描当前场景EPV组件：返回nil，不是空数组") end
    local limit=16384
    local size,getter
    local get_size=member_function(collection,"get_size")
    local get_element=member_function(collection,"get_element")
    local call=member_function(collection,"call")
    local get_type=member_function(collection,"get_type_definition")
    if get_size and get_element then
        size=get_size(collection)
        getter=function(i) return get_element(collection,i) end
    elseif type(collection)=="table" and not call and not get_type then
        -- Lua's length operator/ipairs cannot distinguish a dense zero-based
        -- array from a one-based array and silently truncate holes. Validate
        -- every raw key before reading either representation.
        local count,minimum,maximum=0,nil,nil
        for key in next,collection do
            if type(key)~="number" or math.tointeger(key)==nil or key<0 then fail("EPV组件表包含非数组索引") end
            count=count+1
            if count>limit then fail("EPV场景扫描数量超过安全上限，未截断应用") end
            minimum=minimum and math.min(minimum,key) or key
            maximum=maximum and math.max(maximum,key) or key
        end
        if count==0 then return {} end
        if (minimum~=0 and minimum~=1) or maximum-minimum+1~=count then fail("EPV组件数组存在孔洞或不支持的起始索引") end
        size=count
        getter=function(i) return rawget(collection,i+minimum) end
    elseif call and get_type then
        -- Some SDK results retain only REManagedObject methods instead of the
        -- SystemArray convenience methods. Resolve typed methods explicitly.
        local td=get_type(collection)
        local find=td and member_function(td,"get_method")
        if not find or not find(td,"get_Length") then fail("EPV组件结果没有可用的数组长度方法") end
        local element_method
        for _,name in ipairs({"GetValue(System.Int32)","get_Item(System.Int32)"}) do
            if find(td,name) then element_method=name; break end
        end
        if not element_method then fail("EPV组件结果没有可用的数组元素方法") end
        size=call(collection,"get_Length")
        getter=function(i) return call(collection,element_method,i) end
    else fail("EPV组件结果不是可识别的Lua数组或托管数组") end
    size=math.tointeger(tonumber(size))
    if not size or size<0 or size>limit then fail("EPV场景扫描数量异常，未截断应用") end
    local result={}
    for index=0,size-1 do
        local value=getter(index)
        if value==nil then fail("EPV组件数组读取返回nil，拒绝部分应用："..index) end
        result[#result+1]=value
    end
    return result
end
local function component_array(collection)
    local ok,result=pcall(read_component_array,collection)
    if ok then return result end
    local description="Lua "..type(collection)
    local get_type=member_function(collection,"get_type_definition")
    if get_type then
        local type_ok,td=pcall(get_type,collection)
        local get_name=type_ok and td and member_function(td,"get_full_name")
        if get_name then
            local name_ok,name=pcall(get_name,td)
            if name_ok and type(name)=="string" then description=description.." / "..name end
        end
    end
    fail(tostring(result).." [collection="..description.."]")
end
local function scan_scene(context,visit)
    local components=component_array(context.scene:call("findComponents(System.Type)",sdk.typeof("via.effect.script.EPVDataBase")))
    for _,component in ipairs(components) do
        if component and valid(component) then
            local td=component:get_type_definition()
            if td and td:get_field("Elements") then
                local elements=component:get_field("Elements")
                if elements then
                    local n=math.tointeger(tonumber(elements:call("get_Count")))
                    if not n or n<0 or n>65536 then fail("EPV元素数量异常") end
                    for e=0,n-1 do
                        local provider=elements:call("get_Item",e)
                        if provider then
                            if valid(provider) then
                                local count=math.tointeger(tonumber(provider:call("getResourcesCount()")))
                                if not count or count<0 or count>4096 then fail("EPV资源数量异常") end
                                -- Even a zero-slot provider remains associated with a component.
                                visit(provider,nil,component,true)
                                for slot=0,count-1 do visit(provider,slot,component,true) end
                            else visit(provider,nil,component,false) end
                        end
                    end
                end
            end
        end
    end
end
local function collect(path, context)
    local found, by_key={},{}
    local function add(provider,index,component)
        index=math.tointeger(index)
        if not provider or not index or index<0 then fail("捕获模块提供了无效资源槽") end
        if not valid(provider) or (component and not valid(component)) then return end
        local key=address(provider)..":"..index
        if by_key[key] then
            -- Fresh scene evidence supersedes a captured short-lived component.
            if component then by_key[key].component=component end
            return
        end
        local b={provider=provider,index=index,component=component,key=key}
        local holder=current_holder(b)
        if not holder or canonical(holder_path(holder)) ~= path then return end
        b.expected=holder; by_key[key]=b; found[#found+1]=b
    end
    local list=capture.list_bindings(path)
    if type(list) ~= "table" then fail("捕获模块未返回资源绑定列表") end
    for _,b in ipairs(list) do add(b.provider,b.index,b.component) end
    scan_scene(context,function(provider,index,component,is_valid)
        if is_valid and index~=nil then add(provider,index,component) end
    end)
    table.sort(found,function(a,b) return a.key<b.key end)
    return found
end
local function prepare_binding(b,context,path)
    local previous=records[b.key]
    if previous and previous.originalPath~=path then fail("已有绑定来自其他资源") end
    if previous then check(b,previous.current) end
    pin(b.provider); pin(b.expected); pin(b.component)
    b.beforeFlags=cache_fields(b.provider)
    b.record=previous or {key=b.key,provider=b.provider,index=b.index,component=b.component,context=context,originalPath=path,original=pin(b.expected),originalFlags=copy(original_flags[address(b.provider)] or b.beforeFlags)}
    return b
end
local function prepare_restore(tx)
    local summary={restoredCount=0,pinnedProviderCount=0,restoredPinnedProviderCount=0,
        expiredCount=0,reassociatedCount=0,unresolvedCount=0,untrackedCount=0}
    local diagnostic={unresolved={}}
    state.restoreSummary,state.restoreDiagnostic=summary,diagnostic
    local function unresolved(record,reason)
        summary.unresolvedCount=summary.unresolvedCount+1
        diagnostic.unresolved[#diagnostic.unresolved+1]={key=record.key,path=record.originalPath,reason=tostring(reason)}
    end
    -- Restore only the exact ProviderData objects retained by prepare_binding.
    -- ProviderData owns its resource slots and validity API independently of
    -- the component that exposed it. A short-lived shell component may die
    -- while its retained provider is still valid. Do not read that component
    -- or rediscover a replacement by address/path; apply still requires its
    -- normal live discovery checks. Provider validity, slot bounds, typed
    -- holder equality, path, context and readback remain mandatory here.
    for _,record in pairs(records) do
        local b={key=record.key,provider=record.provider,index=record.index,
            record=record,expected=record.current,next=record.original}
        local ok,why=pcall(function() check(b,b.expected); b.beforeFlags=cache_fields(b.provider) end)
        if ok then
            tx.bindings[#tx.bindings+1]=b
            summary.pinnedProviderCount=summary.pinnedProviderCount+1
        else unresolved(record,why) end
    end
    if summary.unresolvedCount>0 then
        fail("还有"..summary.unresolvedCount.."处引用无法安全确认，备份已保留；"..diagnostic.unresolved[1].reason)
    end
end
local function release_raw(resource)
    if resource and resource.raw_owned then resource.raw:release(); resource.raw_owned=false end
end
local function destroy_probe(resource)
    if not resource or not resource.object then return end
    local scene_now
    -- A loading probe belongs to its scene, not to the actor/equipment. Its
    -- cleanup must remain possible when the player temporarily disappears.
    local ok,scene=pcall(current_scene); if ok then scene_now=address(scene) end
    if scene_now~=resource.sceneAddress then
        -- The scene owns this object. Never write a stale GameObject address.
        if scene_now then resource.object,resource.player=nil,nil; return end
        fail("场景暂不可用，等待安全清理加载检查对象")
    end
    if not sdk.is_managed_object or sdk.is_managed_object(resource.object) then
        resource.object:call("destroy",resource.object)
    end
    resource.object,resource.player=nil,nil
end
local function dispose_resource(resource)
    destroy_probe(resource); release_raw(resource)
end
local function load(tx)
    local r={path=tx.generated,sceneAddress=tx.context.sceneAddress,raw_owned=false}
    tx.resource=r
    r.raw=sdk.create_resource("via.effect.EffectResource",r.path)
    if not r.raw then fail("新版本EFX加载失败") end
    r.raw:add_ref(); r.raw_owned=true
    r.holder=r.raw:create_holder("via.effect.EffectResourceHolder")
    if not r.holder then fail("新版本资源持有器创建失败") end
    pin(r.holder)
    r.object=sdk.call_native_func(nil,sdk.find_type_definition("via.GameObject"),"create(System.String)","MotionVfxLive_resource_probe_"..serial)
    if not r.object then fail("无法创建不播放的资源检查对象") end
    pin(r.object)
    r.player=r.object:call("createComponent(System.Type)",sdk.typeof("via.effect.EffectPlayer"))
    if not r.player then fail("无法创建资源就绪检查组件") end
    pin(r.player)
    r.player:call("set_AutoStart(System.Boolean)",false)
    if r.player:call("get_AutoStart()")~=false then fail("不能关闭资源检查组件的自动播放") end
    r.player:call("set_Resource(via.effect.EffectResourceHolder)",r.holder)
    tx.started=os.time(); tx.frames=0; tx.readyFrames=0
end
local function notify(tx,ok,message)
    counts(); state.busy=false; state.message=tostring(message)
    if not ok then state.phase=state.requiresRestart and "recovery_required" or "error" end
    local result=M.snapshot(); result.ok=ok; result.operation=tx.operation
    command,transaction=nil,nil
    if tx.callback then local good,err=pcall(tx.callback,result); if not good then warn(err) end end
end
local function rollback(tx)
    local errors={}
    local same=pcall(same_context,tx.context)
    for i=#tx.changed,1,-1 do
        local b=tx.changed[i]
        local ok,err=pcall(function()
            if not same then fail("上下文改变，跳过旧绑定回滚") end
            local actual=current_holder(b)
            if not equal_holder(actual,b.expected) and not equal_holder(actual,b.next) then fail("绑定已被其他操作替换，未覆盖") end
            b.provider:call("setResources(System.UInt64, via.effect.EffectResourceHolder)",b.index,b.expected)
            check(b,b.expected); write_flags(b.provider,b.beforeFlags)
        end)
        if not ok then
            errors[#errors+1]=tostring(err)
            b.record.current=b.next; records[b.key]=b.record
            original_flags[address(b.provider)]=copy(b.record.originalFlags)
        end
    end
    return #errors==0,table.concat(errors,"; ")
end
local function failed(tx,err)
    local restored,rollback_error=rollback(tx)
    local clean,cleanup_error=pcall(dispose_resource,tx.resource)
    if not clean then retained[#retained+1]=tx.resource end
    if not restored then state.requiresRestart=true; err=tostring(err).."；回滚未完整完成："..rollback_error end
    if not clean then err=tostring(err).."；加载检查对象清理待重试："..tostring(cleanup_error) end
    notify(tx,false,err)
end
local function commit(tx)
    same_context(tx.context)
    -- Discover references added while the new resource was loading, but also
    -- verify every originally selected slot even if its path has since changed.
    local seen={}; for _,b in ipairs(tx.bindings) do check(b,b.expected); seen[b.key]=true end
    for _,b in ipairs(collect(tx.original,tx.context)) do
        if not seen[b.key] then tx.bindings[#tx.bindings+1]=prepare_binding(b,tx.context,tx.original); seen[b.key]=true end
    end
    for _,b in ipairs(tx.bindings) do
        check(b,b.expected)
        b.next=tx.resource.holder
        tx.changed[#tx.changed+1]=b
        local flags={}
        if b.beforeFlags.IgnoreEffectCache~=nil then flags.IgnoreEffectCache=true end
        if b.beforeFlags.EffectCache~=nil then flags.EffectCache=false end
        write_flags(b.provider,flags)
        b.provider:call("setResources(System.UInt64, via.effect.EffectResourceHolder)",b.index,b.next)
        check(b,b.next)
    end
    local ok,err=capture.set_alias(tx.generated,tx.original)
    if ok~=true then fail("捕获资源别名登记失败："..tostring(err)) end
    aliases[tx.generated:lower()]=tx.original
    for _,b in ipairs(tx.bindings) do
        b.record.current=b.next; b.record.generatedPath=tx.generated
        b.record.context=tx.context
        b.record.component=b.component; records[b.key]=b.record
        original_flags[address(b.provider)]=copy(b.record.originalFlags)
    end
    state.originalPath,state.generatedPath=tx.original,tx.generated
    session_overrides[tx.original]={holder=tx.resource.holder,generated=tx.generated}
    state.phase="awaiting_action"
    notify(tx,true,"已应用；修改在本次游戏中持续保留，并自动跟进新加载的引用，等待角色真实动作确认")
end
local function start(tx)
    if tx.operation~="apply" and #retained>0 then fail("加载检查对象尚未清理，恢复仍待完成") end
    if #expired_records>0 then fail("旧版留有"..#expired_records.."条未确认还原的失效引用备份；备份已保留，请保存编辑副本后重启游戏，勿用Reset scripts代替还原。") end
    if tx.operation~="apply" and next(records)==nil then
        session_overrides={}
        clear_context_diagnostic()
        state.phase=tx.operation=="shutdown" and "closed" or "restored"
        if tx.operation=="shutdown" then active=false end
        notify(tx,true,"没有需要恢复的资源引用"); return
    end
    tx.context=checked_identity(); tx.changed={}; tx.bindings={}
    if tx.acceptedContext then same_context(tx.acceptedContext) end
    if state.requiresRestart and tx.operation=="apply" then fail("存在未完成的回滚，必须先恢复或重启游戏") end
    clear_context_diagnostic()
    if tx.operation=="apply" then
        state.restoreSummary,state.restoreDiagnostic=nil,nil
        for _,b in ipairs(collect(tx.original,tx.context)) do tx.bindings[#tx.bindings+1]=prepare_binding(b,tx.context,tx.original) end
        if #tx.bindings==0 then fail("当前场景没有引用所选EFX的真实资源绑定；请先由角色触发相应动作") end
        state.phase="loading"; state.message="正在加载新版本；真实动作资源尚未改变"
        load(tx)
    else
        prepare_restore(tx)
        table.sort(tx.bindings,function(a,b) return a.key<b.key end)
        same_context(tx.context)
        for _,b in ipairs(tx.bindings) do
            check(b,b.expected)
            tx.changed[#tx.changed+1]=b
            b.provider:call("setResources(System.UInt64, via.effect.EffectResourceHolder)",b.index,b.next)
            check(b,b.next); write_flags(b.provider,b.record.originalFlags)
        end
        state.restoreSummary.restoredCount=#tx.bindings
        state.restoreSummary.restoredPinnedProviderCount=#tx.bindings
        records={}; original_flags={}; session_overrides={}; state.requiresRestart=false; state.phase="restored"
        clear_context_diagnostic()
        state.originalPath,state.generatedPath=nil,nil
        if tx.operation=="shutdown" then active=false; state.phase="closed" end
        local message="已恢复首次应用前的全部资源引用及缓存设置"
        notify(tx,true,message)
    end
end
local function advance(tx)
    same_context(tx.context)
    tx.frames=tx.frames+1
    if tx.resource.player:call("get_Running()") == true then fail("资源检查组件意外进入播放状态") end
    if tx.resource.player:call("get_ReadyResource()") == true then tx.readyFrames=tx.readyFrames+1 else tx.readyFrames=0 end
    if tx.readyFrames>=2 then
        -- Wilds may return nil/throw from EffectPlayer.get_ResourcePath even
        -- when ready. The holder is the authoritative typed fallback.
        local holder=tx.resource.player:call("get_Resource()")
        if not equal_holder(holder,tx.resource.holder) then fail("加载检查组件的资源持有器不一致") end
        local path_ok,path=pcall(function() return tx.resource.player:call("get_ResourcePath()") end)
        if not path_ok or type(path)~="string" or path=="" then path=holder_path(holder) end
        if logical(path):lower()~=tx.generated:lower() then fail("加载检查组件资源路径不一致") end
        -- Cleanup must succeed before any real binding is changed.
        destroy_probe(tx.resource)
        release_raw(tx.resource)
        commit(tx)
    elseif os.time()-tx.started>=15 or tx.frames>=3600 then fail("新版EFX在15秒内未就绪，未替换真实动作资源") end
end
function M.configure(options)
    if state.busy then return false,"正在处理资源事务" end
    if type(options)~="table" or type(options.capture)~="table" or type(options.capture.list_bindings)~="function" or type(options.capture.set_alias)~="function" then return false,"需要真实动作捕获模块" end
    capture=options.capture; active=true; return true
end
local function enqueue(operation,original,generated,callback)
    if not active then return false,"资源绑定模块已关闭" end
    if not capture then return false,"资源绑定模块尚未配置" end
    if state.busy then return false,"已有资源事务正在进行，请等待完成" end
    if state.requiresRestart and operation=="apply" then return false,"上次事务回滚未完成" end
    serial=serial+1
    command={operation=operation,original=original,generated=generated,callback=callback,changed={},acceptedContext=observed_identity}
    state.busy=true; state.phase="queued"; state.message=operation=="apply" and "已排队应用真实动作资源" or "已排队恢复原始资源引用"
    return true
end
function M.apply(original,generated,callback)
    local ok,a,b=pcall(function()
        local original_path=canonical(original); local generated_path=logical(generated)
        if aliases[generated_path:lower()] then fail("新版资源路径已被使用，不能重新分配给其他原始资源") end
        if canonical(generated_path)==original_path or generated_path:lower()==original_path then fail("新版资源必须使用未使用过的独立路径") end
        return original_path,generated_path
    end)
    if not ok then return false,tostring(a) end
    return enqueue("apply",a,b,callback)
end
function M.restore_all(callback) return enqueue("restore",nil,nil,callback) end
function M.shutdown(callback) return enqueue("shutdown",nil,nil,callback) end
function M.current_status()
    -- The per-frame safety gate only needs scalars. Recount the live backup
    -- tables, including state migrated by RepairBindings, without building
    -- resource maps or copying detailed transaction/restore diagnostics.
    counts()
    return {busy=state.busy,appliedCount=state.appliedCount,
        requiresRestoreBeforeReset=state.requiresRestoreBeforeReset,
        requiresRestart=state.requiresRestart,contextInvalid=state.contextInvalid,
        phase=state.phase,expiredBackupCount=state.expiredBackupCount}
end
function M.snapshot()
    counts(); local n=0;for _ in pairs(session_overrides) do n=n+1 end
    state.sessionResourceCount=n
    state.appliedResources={};for path,value in pairs(session_overrides) do state.appliedResources[path]=value.generated end
    return copy(state)
end
local function follow_session(context)
    if state.requiresRestart then return end
    for path,override in pairs(session_overrides) do
        local tx={operation="follow",context=context,original=path,generated=override.generated,
            resource={holder=override.holder},bindings={},changed={}}
        local ok,err=pcall(function()
            for _,b in ipairs(collect(path,context)) do
                if not equal_holder(b.expected,override.holder) then tx.bindings[#tx.bindings+1]=prepare_binding(b,context,path) end
            end
            if #tx.bindings==0 then return end
            -- commit also rechecks every discovered reference at LockScene.
            commit(tx)
        end)
        if not ok then
            local restored,why=rollback(tx)
            state.followError=tostring(err)
            if not restored then state.requiresRestart=true;state.followError=state.followError.."; "..why end
            return
        end
    end
    state.followError=nil
end
local function drain()
    if not active then return end
    idle_frames=idle_frames+1
    if not transaction and not command and (not observed_identity or idle_frames%30==0) then
        local identity_ok,context=pcall(identity)
        if identity_ok then
            observed_identity=context
            if next(session_overrides) and idle_frames%60==0 then follow_session(context) end
        end
    end
    for i=#retained,1,-1 do
        local ok=pcall(dispose_resource,retained[i]); if ok then table.remove(retained,i) end
    end
    local tx=transaction or command
    if not tx then return end
    local ok,err
    if transaction then ok,err=pcall(advance,tx)
    else transaction=tx; command=nil; ok,err=pcall(start,tx) end
    if not ok then failed(tx,err) end
end
re.on_pre_application_entry("LockScene",drain)
if re.on_script_reset then re.on_script_reset(function()
    if state.appliedCount>0 or state.busy then warn("完整Reset scripts前必须等待restore_all成功。Reset回调未对旧资源指针进行写入；请使用受控Reload.lua。") end
    active=false
end) end
_G.MotionVfxActionBindings=M
return M
