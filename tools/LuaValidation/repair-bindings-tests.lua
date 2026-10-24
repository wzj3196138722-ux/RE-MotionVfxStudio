-- Offline migration tests against the actual installed 1.1.1 source fixture
-- plus actual 1.2.0 source and current 1.2.1 source. No game access.
local old_source=assert(arg[1],"pass saved 1.1.1 fixture")
local new_source=assert(arg[2],"pass current 1.2.1 source")
local bridge_source=assert(arg[3],"pass manual repair helper")
local old12_source=assert(arg[4],"pass saved 1.2.0 fixture")
local old_bridge_source=assert(arg[5],"pass prior 1.1.1 to 1.2.0 repair helper")
local passed,failed,assertions=0,0,0
local function eq(a,b,label) assertions=assertions+1; assert(a==b,(label or "value")..": expected "..tostring(b)..", got "..tostring(a)) end
local function test(name,fn)
    local ok,err=pcall(fn)
    if ok then passed=passed+1; print("PASS "..name) else failed=failed+1; print("FAIL "..name..": "..tostring(err)) end
end
local function uv(fn,name)
    for i=1,128 do local k,v=debug.getupvalue(fn,i); if not k then break end; if k==name then return v,i end end
    error("missing test upvalue "..name)
end
local function setuv(fn,name,value) local _,i=uv(fn,name); assert(debug.setupvalue(fn,i,value)==name) end
local function opaque()
    return setmetatable({},{__index=function() error("repair accessed an engine object") end,
        __newindex=function() error("repair changed an engine object") end})
end
local function make(n,version)
    local c={callbacks={},resets={},nativeReads=0,statuses={},registrations=0,oldVersion=version or "1.1.1"}
    local env=setmetatable({},{__index=_G}); env._G=env; env.package={loaded={}}
    env.log={info=function() end,warn=function() end}
    env.json={dump_file=function(path,value) eq(path,"MotionVfxLive/repair-status.json"); c.statuses[#c.statuses+1]=value end}
    env.sdk={
        get_native_singleton=function() c.nativeReads=c.nativeReads+1; return nil end,
        get_managed_singleton=function() return nil end,
    }
    env.re={
        on_pre_application_entry=function(name,fn)
            eq(name,"LockScene"); c.registrations=c.registrations+1
            if c.failPre then error("injected pre registration failure") end
            c.callbacks[#c.callbacks+1]=fn
            if c.invokeDuringRegistration then fn() end
        end,
        on_script_reset=function(fn)
            c.registrations=c.registrations+1
            if c.failReset then error("injected reset registration failure") end
            c.resets[#c.resets+1]=fn
        end,
    }
    c.env=env; c.module=assert(loadfile(version=="1.2.0" and old12_source or old_source,"t",env))()
    eq(c.module.version,c.oldVersion,"real old fixture version")
    c.oldDrain=c.callbacks[1]; c.oldConfigure=c.module.configure
    c.capture={list_bindings=function() return {} end,set_alias=function() return true end}
    assert(c.module.configure({capture=c.capture}))
    c.enqueue=uv(c.module.restore_all,"enqueue")
    c.counts=uv(c.module.snapshot,"counts")
    c.state=uv(c.module.configure,"state")
    c.records=uv(c.counts,"records"); c.retained=uv(c.counts,"retained")
    c.aliases=uv(c.module.apply,"aliases")
    c.context={token="scene|actor|weapon|id|model|type"}
    for i=1,n or 20 do
        local key=tostring(1000+i)..":0"
        c.records[key]={key=key,provider=opaque(),component=opaque(),index=0,context=c.context,
            originalPath="art/original.efx",generatedPath="art/generated.efx",original=opaque(),current=opaque(),
            originalFlags={IgnoreEffectCache=false,EffectCache=true}}
    end
    c.aliases["art/generated.efx"]="art/original.efx"
    c.state.appliedCount=n or 20; c.state.phase="error"; c.state.message="EPV组件已失效"
    c.state.requiresRestoreBeforeReset=(n or 20)>0
    if version=="1.2.0" then c.state.expiredBackupCount=0 end
    setuv(c.enqueue,"serial",27)
    env.package.loaded["MotionVfxLive.fx_action_bindings"]=c.module
    env.loadfile=function(path,mode,staged)
        eq(path,"reframework/autorun/MotionVfxLive/fx_action_bindings.lua"); eq(mode,"t")
        if c.compileFail then return nil,"injected compile error" end
        if c.loaderFail then return function() error("injected loader error") end end
        local loader,err=loadfile(c.target or new_source,mode,staged)
        if not loader then return nil,err end
        return function()
            local m=loader()
            if c.newVersion then m.version=c.newVersion end
            if c.newDefaults then local st=uv(m.configure,"state"); st.repairTestDefault=83 end
            if c.onNew then c.onNew(m) end
            return m
        end
    end
    function c:repair(source)
        local r=assert(loadfile(source or bridge_source,"t",self.env))()
        eq(r,self.env.MotionVfxBindingsRepair,"status singleton")
        eq(r,self.statuses[#self.statuses],"status file payload")
        return r
    end
    function c:tick() for _,fn in ipairs(self.callbacks) do fn() end end
    function c:unchanged()
        eq(self.module.version,self.oldVersion); eq(self.module.configure,self.oldConfigure)
        eq(uv(self.oldConfigure,"active"),true,"old drain still active")
        eq(uv(self.counts,"records"),self.records,"original record table identity")
        eq(self.env.MotionVfxActionBindings,self.module,"global singleton identity")
        eq(self.env.package.loaded["MotionVfxLive.fx_action_bindings"],self.module,"package cache identity")
    end
    return c
end
test("20 original holders survive migration without native access or automatic restore",function()
    local c=make(); local r=c:repair()
    eq(r.success,true); eq(r.preservedRecords,20); eq(r.automaticallyRestored,false); eq(r.backupPreserved,true)
    eq(c.nativeReads,0); eq(c.module.version,"1.2.1"); eq(c.module.snapshot().busy,false)
    eq(uv(c.module.snapshot,"counts")~=c.counts,true,"new methods installed")
    eq(uv(uv(c.module.snapshot,"counts"),"records"),c.records)
    eq(c.env.MotionVfxActionBindings,c.module); eq(c.env.package.loaded["MotionVfxLive.fx_action_bindings"],c.module)
    eq(c.state.message,"EPV组件已失效","old error stays until real restore")
end)
test("aliases retained resources serial capture observed context and state remain intact",function()
    local c=make(); local retained={raw=opaque(),raw_owned=true}; c.retained[1]=retained
    setuv(c.enqueue,"observed_identity",c.context)
    local r=c:repair(); eq(r.success,true)
    local e=uv(c.module.restore_all,"enqueue"); local co=uv(c.module.snapshot,"counts")
    eq(uv(e,"serial"),27); eq(uv(e,"observed_identity"),c.context); eq(uv(e,"capture"),c.capture)
    eq(uv(co,"retained"),c.retained); eq(c.retained[1],retained); eq(uv(c.module.apply,"aliases"),c.aliases)
    eq(uv(c.module.configure,"state"),c.state); eq(r.needsRestore,true)
end)
test("new defaults supplement old state without replacing existing values",function()
    local c=make(); c.newDefaults=true; eq(c:repair().success,true)
    eq(c.state.repairTestDefault,83); eq(c.state.appliedCount,20); eq(c.state.phase,"error")
end)
test("only one drain is active after commit",function()
    local c=make(); eq(c:repair().success,true); eq(#c.callbacks,2); eq(#c.resets,2)
    eq(uv(c.oldConfigure,"active"),false); eq(uv(c.module.configure,"active"),true)
    c:tick(); eq(c.nativeReads,1,"one identity read from the new drain")
    eq(c.module.snapshot().appliedCount,20)
end)
test("new registration callbacks stay inert before commit",function()
    local c=make(); c.invokeDuringRegistration=true
    eq(c:repair().success,true); eq(c.nativeReads,0)
end)
test("first callback registration failure leaves original drain and records",function()
    local c=make(); c.failPre=true; local r=c:repair(); eq(r.success,false); c:unchanged()
    eq(#c.callbacks,1); c:tick(); eq(c.nativeReads,1)
end)
test("second callback registration failure leaves new callback permanently inert",function()
    local c=make(); c.failReset=true; eq(c:repair().success,false); c:unchanged()
    eq(#c.callbacks,2); c:tick(); eq(c.nativeReads,1)
end)
test("busy state is refused before replacement load or registration",function()
    local c=make(); c.state.busy=true; eq(c:repair().success,false); c:unchanged(); eq(c.registrations,2)
end)
test("pending command is refused even when busy flag is false",function()
    local c=make(); setuv(c.enqueue,"command",{operation="restore"})
    eq(c:repair().success,false); c:unchanged(); eq(c.registrations,2)
end)
test("conflicting original cache flags are refused without losing either record",function()
    local c=make(1); c.records["1001:1"]={key="1001:1",provider=c.records["1001:0"].provider,index=1,
        context=c.context,originalPath="art/original.efx",original=opaque(),current=opaque(),originalFlags={EffectCache=false,IgnoreEffectCache=false}}
    c.state.appliedCount=2; eq(c:repair().success,false); c:unchanged(); eq(c.records["1001:1"].originalFlags.EffectCache,false)
end)
test("duplicate provider cache flags with matching backups migrate",function()
    local c=make(1); c.records["1001:1"]={key="1001:1",provider=c.records["1001:0"].provider,index=1,
        context=c.context,originalPath="art/original.efx",original=opaque(),current=opaque(),originalFlags={EffectCache=true,IgnoreEffectCache=false}}
    c.state.appliedCount=2; eq(c:repair().success,true); eq(c.module.snapshot().appliedCount,2)
    local callbacks=uv(c.callbacks[2],"callbacks")
    local start=uv(callbacks.drain,"start")
    local flags=uv(start,"original_flags")
    eq(flags["1001"].IgnoreEffectCache,false); eq(flags["1001"].EffectCache,true)
    eq(uv(uv(start,"notify"),"M"),c.module,"internal notify uses original public table")
end)
test("missing debug API is a clean refusal",function()
    local c=make(); c.env.debug=false; eq(c:repair().success,false); c:unchanged()
end)
test("missing individual debug API is a clean refusal",function()
    local c=make(); c.env.debug={getupvalue=debug.getupvalue,setupvalue=debug.setupvalue,getinfo=debug.getinfo}
    eq(c:repair().success,false); c:unchanged()
end)
test("unsupported old version does not migrate",function()
    local c=make(); c.module.version="1.1.0"; eq(c:repair().success,false); eq(c.module.version,"1.1.0"); eq(c.module.configure,c.oldConfigure)
end)
test("unsupported new version is rejected before actual callback registration",function()
    local c=make(); c.newVersion="1.1.1"; eq(c:repair().success,false); c:unchanged(); eq(c.registrations,2)
end)
test("missing original holder is refused",function()
    local c=make(); c.records["1001:0"].original=nil; eq(c:repair().success,false); c:unchanged()
end)
test("record count mismatch is refused",function()
    local c=make(); c.state.appliedCount=19; eq(c:repair().success,false); c:unchanged()
end)
test("compiler failure preserves old module",function()
    local c=make(); c.compileFail=true; eq(c:repair().success,false); c:unchanged(); eq(c.registrations,2)
end)
test("loader failure preserves old module",function()
    local c=make(); c.loaderFail=true; eq(c:repair().success,false); c:unchanged(); eq(c.registrations,2)
end)
test("midcommit failure restores old methods active cell and default keys",function()
    local c=make(); c.newDefaults=true; local injected=false
    c.env.debug=setmetatable({setupvalue=function(fn,i,v)
        local name=debug.getupvalue(fn,i)
        local r=debug.setupvalue(fn,i,v)
        if name=="active" and v==true and not injected then injected=true; error("injected failure after new module activation") end
        return r
    end},{__index=debug})
    eq(c:repair().success,false); eq(injected,true); c:unchanged(); eq(c.state.repairTestDefault,nil)
    c:tick(); eq(c.nativeReads,1,"new drain stays gated after rollback")
end)
test("late busy change during registration prevents commit",function()
    local c=make(); local previous=c.env.re.on_script_reset
    c.env.re.on_script_reset=function(fn) previous(fn); c.state.busy=true end
    eq(c:repair().success,false); c:unchanged(); eq(c.state.busy,true)
end)
test("repeat repair refuses without adding callbacks or touching saved records",function()
    local c=make(); eq(c:repair().success,true); local current=c.module.configure
    eq(c:repair().success,false); eq(c.module.version,"1.2.1"); eq(c.module.configure,current); eq(#c.callbacks,2)
    eq(c.module.snapshot().appliedCount,20)
end)
test("1.2.0 archived backups and their holder references migrate intact",function()
    local c=make(20,"1.2.0"); local expired=uv(c.counts,"expired_records")
    local record={key="expired:0",provider=opaque(),original=opaque(),current=opaque()}
    expired[1]=record; c.state.expiredBackupCount=1
    local r=c:repair(); eq(r.success,true); eq(r.preservedExpiredRecords,1)
    eq(uv(uv(c.module.snapshot,"counts"),"expired_records"),expired); eq(expired[1],record)
    eq(c.module.snapshot().expiredBackupCount,1); eq(c.module.snapshot().appliedCount,20); eq(c.nativeReads,0)
end)
test("1.2.0 archived backup count conflict is refused",function()
    local c=make(20,"1.2.0"); c.state.expiredBackupCount=1
    eq(c:repair().success,false); c:unchanged(); eq(c.registrations,2)
end)
test("archived backups alone keep restore and reset protection pending",function()
    local c=make(0,"1.2.0"); local expired=uv(c.counts,"expired_records")
    expired[1]={key="expired:0",provider=opaque(),original=opaque(),current=opaque()}; c.state.expiredBackupCount=1
    local r=c:repair(); eq(r.success,true); eq(r.preservedRecords,0); eq(r.preservedExpiredRecords,1); eq(r.needsRestore,true)
    eq(c.module.snapshot().appliedCount,0); eq(c.module.snapshot().requiresRestoreBeforeReset,true)
end)
test("1.2.0 archived backup array holes are refused",function()
    local c=make(20,"1.2.0"); local expired=uv(c.counts,"expired_records")
    expired[2]={key="expired:0",provider=opaque(),original=opaque(),current=opaque()}; c.state.expiredBackupCount=1
    eq(c:repair().success,false); c:unchanged(); eq(expired[2].key,"expired:0")
end)
test("1.2.0 registration failure keeps archived and active backups",function()
    local c=make(20,"1.2.0"); local expired=uv(c.counts,"expired_records")
    expired[1]={key="expired:0",provider=opaque(),original=opaque(),current=opaque()}; c.state.expiredBackupCount=1
    c.failReset=true; eq(c:repair().success,false); c:unchanged()
    eq(uv(c.counts,"expired_records"),expired); eq(c.state.expiredBackupCount,1); c:tick(); eq(c.nativeReads,1)
end)
test("1.2.0 midcommit rollback preserves archived backups and single active drain",function()
    local c=make(20,"1.2.0"); local expired=uv(c.counts,"expired_records")
    expired[1]={key="expired:0",provider=opaque(),original=opaque(),current=opaque()}; c.state.expiredBackupCount=1
    local injected=false
    c.env.debug=setmetatable({setupvalue=function(fn,i,v)
        local name=debug.getupvalue(fn,i); local r=debug.setupvalue(fn,i,v)
        if name=="active" and v==true and not injected then injected=true; error("injected 1.2.0 transition failure") end
        return r
    end},{__index=debug})
    eq(c:repair().success,false); c:unchanged(); eq(uv(c.counts,"expired_records"),expired)
    c:tick(); eq(c.nativeReads,1)
end)
test("sequential real 1.1.1 to 1.2.0 to 1.2.1 migration has one active drain",function()
    local c=make(); c.target=old12_source
    eq(c:repair(old_bridge_source).success,true); eq(c.module.version,"1.2.0")
    local middle_configure=c.module.configure; local middle_counts=uv(c.module.snapshot,"counts")
    local expired=uv(middle_counts,"expired_records")
    expired[1]={key="expired:0",provider=opaque(),original=opaque(),current=opaque()}
    c.module.snapshot()
    c.target=nil; local r=c:repair(); eq(r.success,true); eq(r.preservedExpiredRecords,1)
    eq(c.module.version,"1.2.1"); eq(c.env.MotionVfxActionBindings,c.module)
    eq(c.env.package.loaded["MotionVfxLive.fx_action_bindings"],c.module)
    eq(uv(c.oldConfigure,"active"),false); eq(uv(middle_configure,"active"),false); eq(uv(c.module.configure,"active"),true)
    eq(uv(uv(c.module.snapshot,"counts"),"records"),c.records)
    eq(uv(uv(c.module.snapshot,"counts"),"expired_records"),expired)
    eq(#c.callbacks,3); c:tick(); eq(c.nativeReads,1)
end)
print(string.format("Repair bindings tests: %d passed, %d failed, %d assertions",passed,failed,assertions))
if failed>0 then os.exit(1) end
