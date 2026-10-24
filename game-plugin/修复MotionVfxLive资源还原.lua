-- Manual Run Script helper; install as data/MotionVfxLive/RepairBindings.lua.
-- Migrates known 1.1.1/1.2.0 Lua closure state to 1.2.1 without touching engine
-- objects, dropping original holders, resetting scripts, or starting restore.
local previous = _G.MotionVfxActionBindings
local destination = "reframework/autorun/MotionVfxLive/fx_action_bindings.lua"
local result = { success=false, phase="checking", fromVersion=previous and previous.version,
    toVersion="1.2.1", backupPreserved=true, automaticallyRestored=false }
local function report(phase, message)
    result.phase, result.message = phase, message
    if os and os.time then result.observedAt=os.time() end
    _G.MotionVfxBindingsRepair=result
    if log and log.info then pcall(log.info,"[MotionVfx bindings repair] "..message) end
    if json and json.dump_file then pcall(json.dump_file,"MotionVfxLive/repair-status.json",result) end
    return result
end
local function fail(message) error(message,0) end
local gated = false
local old_active, old_fields, old_state, added_defaults
local switched = false
local D = debug
local function upvalue(fn, name)
    if type(fn)~="function" then fail("模块闭包不是函数："..name) end
    local found
    for index=1,128 do
        local key,value=D.getupvalue(fn,index)
        if key==nil then break end
        if key==name then
            if found then fail("模块闭包布局有重名："..name) end
            found={fn=fn,index=index,value=value,id=D.upvalueid(fn,index),name=name}
        end
    end
    if not found then fail("模块闭包布局不兼容：缺少 "..name) end
    return found
end
local function value(cell) local name,v=D.getupvalue(cell.fn,cell.index); if name~=cell.name then fail("闭包布局在修复期间改变") end; return v end
local function put(cell,v)
    if D.setupvalue(cell.fn,cell.index,v)~=cell.name then fail("无法迁移闭包："..cell.name) end
end
local function shared(a,b,name)
    local left,right=upvalue(a,name),upvalue(b,name)
    if left.id~=right.id then fail("模块闭包共享关系不兼容："..name) end
    return left
end
local function plain(t,name)
    if type(t)~="table" or getmetatable(t)~=nil then fail("不是预期的普通表："..name) end
    return t
end
local function count(t) local n=0; for _ in next,t do n=n+1 end; return n end
local function flags_equal(a,b)
    for k,v in next,a do if b[k]~=v then return false end end
    for k,v in next,b do if a[k]~=v then return false end end
    return true
end
local function module_source(fn)
    local info=D.getinfo(fn,"S")
    if not info or info.what~="Lua" or type(info.source)~="string" then fail("模块函数不是可识别的 Lua 闭包") end
    return info.source
end
local function graph(roots,source)
    local seen,cells={},{}
    local function visit(fn,depth)
        if seen[fn] then return end
        if depth>32 or count(seen)>=256 then fail("新版闭包图超出已验证范围") end
        local info=D.getinfo(fn,"S")
        if not info or info.what~="Lua" or info.source~=source then return end
        seen[fn]=true
        for index=1,128 do
            local name,v=D.getupvalue(fn,index)
            if not name then break end
            if name~="_ENV" then
                local id=D.upvalueid(fn,index)
                cells[name]=cells[name] or {}
                cells[name][id]={fn=fn,index=index,value=v,id=id,name=name}
                if type(v)=="function" then visit(v,depth+1) end
            end
        end
    end
    for _,fn in ipairs(roots) do visit(fn,0) end
    return function(name)
        local matches=cells[name]
        if not matches or count(matches)~=1 then fail("新版闭包布局不唯一或缺少："..name) end
        return select(2,next(matches))
    end
end
local function run()
    if type(previous)~="table" or (previous.version~="1.1.1" and previous.version~="1.2.0") then fail("只支持正在运行的资源模块 1.1.1 或 1.2.0；当前模块未改动") end
    local original_version=previous.version
    if type(D)~="table" then fail("当前 Lua 未提供 debug；原始资源备份保持不变") end
    for _,name in ipairs({"getupvalue","setupvalue","upvalueid","getinfo"}) do
        if type(D[name])~="function" then fail("当前 Lua 缺少 debug."..name.."；原始资源备份保持不变") end
    end
    plain(previous,"旧模块")
    local allowed={version=true,configure=true,apply=true,restore_all=true,shutdown=true,snapshot=true}
    for key in next,previous do if not allowed[key] then fail("旧模块公开布局不兼容："..tostring(key)) end end
    local source=module_source(previous.configure)
    for key in next,allowed do
        if key~="version" and (type(previous[key])~="function" or module_source(previous[key])~=source) then fail("旧模块方法来源不一致："..key) end
    end
    local enqueue=upvalue(previous.restore_all,"enqueue").value
    if upvalue(previous.shutdown,"enqueue").value~=enqueue or upvalue(previous.apply,"enqueue").value~=enqueue then fail("旧模块排队函数不一致") end
    local counts=upvalue(previous.snapshot,"counts").value
    local state_cell=shared(previous.configure,enqueue,"state")
    if upvalue(counts,"state").id~=state_cell.id or upvalue(previous.snapshot,"state").id~=state_cell.id then fail("旧模块状态闭包不一致") end
    local state=plain(state_cell.value,"旧状态")
    local capture_cell=shared(previous.configure,enqueue,"capture")
    local active_cell=shared(previous.configure,enqueue,"active")
    local command_cell=upvalue(enqueue,"command")
    local serial_cell=upvalue(enqueue,"serial")
    local observed_cell=upvalue(enqueue,"observed_identity")
    local records=plain(upvalue(counts,"records").value,"原始资源备份")
    local retained=plain(upvalue(counts,"retained").value,"待清理资源")
    local expired_records,expired_count
    if original_version=="1.2.0" then
        expired_records=plain(upvalue(counts,"expired_records").value,"已归档的失效引用备份")
        expired_count=count(expired_records)
        if expired_count>4096 or state.expiredBackupCount~=expired_count then fail("失效引用备份数量与状态不一致，未迁移") end
        for key,record in next,expired_records do
            if type(key)~="number" or math.tointeger(key)~=key or key<1 or key>expired_count then fail("失效引用备份不是完整数组，未迁移") end
            plain(record,"失效引用记录")
            if type(record.key)~="string" or not record.provider or not record.original or not record.current then fail("失效引用缺少原始备份，未迁移") end
        end
    end
    result.preservedExpiredRecords=expired_count or 0
    local canonical=upvalue(previous.apply,"canonical").value
    local aliases_cell=shared(previous.apply,canonical,"aliases")
    local aliases=plain(aliases_cell.value,"资源别名")
    if state.busy~=false or command_cell.value~=nil then fail("资源事务尚未结束；请等待后重试，备份未改动") end
    if active_cell.value~=true then fail("旧资源模块已经关闭，未迁移") end
    if type(serial_cell.value)~="number" or math.tointeger(serial_cell.value)==nil or serial_cell.value<0 then fail("旧事务序号无效") end
    if type(capture_cell.value)~="table" or type(capture_cell.value.list_bindings)~="function" or type(capture_cell.value.set_alias)~="function" then fail("旧捕获模块未配置") end
    if observed_cell.value~=nil then plain(observed_cell.value,"旧上下文") end
    local total=count(records)
    if state.appliedCount~=total then fail("原始资源备份数量与状态不一致，未迁移") end
    result.preservedRecords=total
    local original_flags={}
    for key,record in next,records do
        plain(record,"资源记录")
        if type(key)~="string" or record.key~=key or not record.provider or not record.original or not record.current then fail("资源记录缺少原始持有器，未迁移") end
        local provider,slot=key:match("^(.*):(%d+)$")
        if not provider or provider=="" or math.tointeger(record.index)~=tonumber(slot) then fail("资源槽标识不兼容") end
        if type(record.originalPath)~="string" or record.originalPath=="" then fail("资源记录缺少原始路径") end
        plain(record.context,"资源上下文")
        if type(record.context.token)~="string" or record.context.token=="" then fail("资源记录缺少上下文身份") end
        local flags=plain(record.originalFlags,"原始缓存设置")
        for name,v in next,flags do
            if (name~="IgnoreEffectCache" and name~="EffectCache") or type(v)~="boolean" then fail("原始缓存设置格式不兼容") end
        end
        if original_flags[provider] and not flags_equal(original_flags[provider],flags) then fail("同一资源提供者的原始缓存设置冲突；未迁移或丢弃备份") end
        if not original_flags[provider] then local f={}; for k,v in next,flags do f[k]=v end; original_flags[provider]=f end
    end
    if type(re)~="table" or type(re.on_pre_application_entry)~="function" or type(re.on_script_reset)~="function" then fail("当前环境缺少受控回调注册接口") end
    local callbacks={}
    local staged_global=setmetatable({},{__index=function(_,key) if key~="MotionVfxActionBindings" then return _G[key] end end})
    local staged_re=setmetatable({
        on_pre_application_entry=function(name,fn)
            if name~="LockScene" or type(fn)~="function" or callbacks.drain then fail("新版场景回调布局不兼容") end
            callbacks.drain=fn
        end,
        on_script_reset=function(fn)
            if type(fn)~="function" or callbacks.reset then fail("新版重置回调布局不兼容") end
            callbacks.reset=fn
        end,
    },{__index=re})
    local env=setmetatable({_G=staged_global,re=staged_re},{__index=_G})
    local loader,compile_error=loadfile(destination,"t",env)
    if type(loader)~="function" then fail("新版资源模块无法编译："..tostring(compile_error)) end
    local replacement=loader()
    if type(replacement)~="table" or replacement.version~="1.2.1" or rawget(staged_global,"MotionVfxActionBindings")~=replacement then fail("部署的新模块不是预期的 1.2.1") end
    if not callbacks.drain or not callbacks.reset then fail("新版未提供完整回调，旧模块保持运行") end
    plain(replacement,"新版模块")
    local cell=graph({callbacks.drain,callbacks.reset,replacement.configure,replacement.apply,replacement.restore_all,replacement.shutdown,replacement.snapshot},module_source(replacement.configure))
    local new_state=plain(value(cell("state")),"新版初始状态")
    local new_active=cell("active")
    put(new_active,false)
    local transfers={records=records,aliases=aliases,retained=retained,original_flags=original_flags,
        state=state,capture=capture_cell.value,serial=serial_cell.value}
    for name,v in next,transfers do put(cell(name),v) end
    if expired_records then put(cell("expired_records"),expired_records) end
    put(cell("observed_identity"),observed_cell.value)
    put(cell("command"),nil); put(cell("transaction"),nil); put(cell("idle_frames"),0)
    put(cell("M"),previous)
    env._G=_G; env.re=re
    -- Registration is deliberately staged behind a closed gate. A failed
    -- second registration can leave only an inert callback, never two drains.
    re.on_pre_application_entry("LockScene",function(...) if gated then return callbacks.drain(...) end end)
    re.on_script_reset(function(...) if gated then return callbacks.reset(...) end end)
    if _G.MotionVfxActionBindings~=previous or previous.version~=original_version or state.busy~=false or value(command_cell)~=nil or value(active_cell)~=true then fail("迁移前模块状态已改变，原始备份保持不变") end
    if value(upvalue(counts,"records"))~=records or count(records)~=total then fail("迁移前资源备份已改变，未提交") end
    if expired_records and (value(upvalue(counts,"expired_records"))~=expired_records or count(expired_records)~=expired_count) then fail("迁移前失效引用备份已改变，未提交") end
    old_fields={}; for k,v in next,previous do old_fields[k]=v end
    old_active=active_cell; old_state=state; added_defaults={}
    switched=true
    put(old_active,false)
    for k,v in next,new_state do if rawget(state,k)==nil then added_defaults[#added_defaults+1]=k; rawset(state,k,v) end end
    for k,v in next,replacement do rawset(previous,k,v) end
    put(new_active,true)
    if value(old_active)~=false or value(new_active)~=true or previous.version~="1.2.1" then fail("资源模块切换校验失败") end
    gated=true
    result.success=true; result.needsRestore=total>0 or next(retained)~=nil or (expired_count or 0)>0
    result.preservedRecords=total
end
local ok,err=pcall(run)
if not ok then
    gated=false
    if switched then
        -- All rollback operations affect ordinary Lua state only. The new
        -- callback remains gated off even if any unexpected rollback fails.
        local rollback_ok,rollback_error=pcall(function()
            for k in next,previous do if old_fields[k]==nil then rawset(previous,k,nil) end end
            for k,v in next,old_fields do rawset(previous,k,v) end
            for _,k in ipairs(added_defaults) do rawset(old_state,k,nil) end
            put(old_active,true)
        end)
        if not rollback_ok then result.rollbackError=tostring(rollback_error) end
    end
    return report("failed","资源模块热修复未完成："..tostring(err).."。原始资源备份未清除；请不要 Reset scripts。")
end
return report("ready","资源模块已更新到 1.2.1，保留了 "..tostring(result.preservedRecords).." 条原始资源备份及 "..tostring(result.preservedExpiredRecords).." 条已归档备份。尚未执行还原；请点击顶部「停止记录及预览，并还原资源与场景」，等待明确的完成结果。")
