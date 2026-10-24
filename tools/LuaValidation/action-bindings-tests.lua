-- Offline engine mock. No game process, resource files or network are used.
local source=assert(arg[1],"pass fx_action_bindings.lua path")
local passed,failed,assertions=0,0,0
local function eq(a,b,label) assertions=assertions+1; assert(a==b,(label or "value")..": expected "..tostring(b)..", got "..tostring(a)) end
local function test(name,fn)
    local ok,err=pcall(fn)
    if ok then passed=passed+1; print("PASS "..name) else failed=failed+1; print("FAIL "..name..": "..tostring(err)) end
end
local ORIGINAL="Art/VFX/EffectEditor/Weapon/it03/test.efx"
local OTHER="Art/VFX/EffectEditor/Weapon/it03/other.efx"
local NEW="Art/VFX/EffectEditor/MotionVfxLive/session/rev1.efx"
local NEW2="Art/VFX/EffectEditor/MotionVfxLive/session/rev2.efx"
local function status_matches(module)
    local current=module.current_status();local snapshot=module.snapshot()
    for _,key in ipairs({"busy","appliedCount","requiresRestoreBeforeReset","requiresRestart","contextInvalid","phase","expiredBackupCount"}) do
        eq(current[key],snapshot[key],"current status: "..key)
    end
    for _,value in pairs(current) do eq(type(value)~="table",true,"status is scalar-only") end
end
local function find_upvalue(fn,wanted,seen)
    seen=seen or {};if seen[fn] then return nil end;seen[fn]=true
    for i=1,100 do
        local name,value=debug.getupvalue(fn,i);if not name then break end
        if name==wanted then return value end
        if type(value)=="function" and name~="_ENV" then
            local found=find_upvalue(value,wanted,seen);if found~=nil then return found end
        end
    end
end
local function env_new()
    local c={nextAddress=1000,sceneId=10,characterId=20,weaponId=30,equip=2,model=4,weaponType=3,
        callbacks={},providers={},components={},captured={},objects={},resources={},raws={},writes={},flagsWrites={},aliases={},clock=100,providerReads=0,sceneScanCount=0}
    local env=setmetatable({},{__index=_G}); env._G=env
    env.os={time=function() return c.clock end}
    env.log={warn=function() end}
    env.re={on_pre_application_entry=function(name,fn) c.callbacks[name]=fn end,on_script_reset=function(fn) c.callbacks.reset=fn end}
    local function managed(o)
        c.nextAddress=c.nextAddress+1; o.address=c.nextAddress; o.refs=0
        function o:get_address() return self.address end
        function o:add_ref() self.refs=self.refs+1 end
        function o:release() error("borrowed managed wrapper must never be released") end
        return o
    end
    function c:holder(path)
        local key=path:lower()
        self.resources[key]=self.resources[key] or {path=path}
        local o=managed({resource=self.resources[key]})
        function o:call(name)
            if name=="get_ResourcePath" then return self.resource.path end
            error("unexpected holder call "..name)
        end
        return o
    end
    function c:provider(paths,fields)
        local p=managed({slots={},valid=true,resourceReads=0,validityReads=0,fields=fields or {IgnoreEffectCache=false,EffectCache=true}})
        for i,path in ipairs(paths) do p.slots[i]=self:holder(path) end
        function p:get_type_definition() return {get_field=function(_,name) return self.fields[name]~=nil and {} or nil end} end
        function p:get_field(name) return self.fields[name] end
        function p:set_field(name,value)
            assert(c.in_callback,"field write outside safe callback")
            self.fields[name]=value; c.flagsWrites[#c.flagsWrites+1]={provider=self,name=name,value=value}
            if c.failFlag==name then c.failFlag=nil; error("injected cache flag failure") end
        end
        function p:call(name,index,value)
            c.providerReads=c.providerReads+1
            if name=="get_Valid" then
                self.validityReads=self.validityReads+1
                if self.validError then error("injected provider validity failure") end
                if self.validNil then return nil end
                return self.valid
            end
            if name=="getResourcesCount()" then self.resourceReads=self.resourceReads+1; return #self.slots end
            if name=="getResources(System.UInt64)" then
                self.resourceReads=self.resourceReads+1
                eq(math.type(index),"integer","UInt64 index ABI")
                local h=self.slots[index+1]
                if not h then return nil end
                if c.new_wrappers then return c:holder(h.resource.path) end
                return h
            end
            if name=="setResources(System.UInt64, via.effect.EffectResourceHolder)" then
                assert(c.in_callback,"resource write outside safe callback")
                assert(self.valid,"stale provider write")
                eq(math.type(index),"integer","UInt64 setter ABI")
                self.slots[index+1]=value; c.writes[#c.writes+1]={provider=self,index=index,holder=value}
                if c.failProvider==self and (not c.failedOnce or c.failAlways) then c.failedOnce=true; error("injected setter failure after write") end
                return
            end
            error("unexpected provider method "..name)
        end
        self.providers[#self.providers+1]=p; return p
    end
    function c:component(providers)
        local comp=managed({valid=true,providers=providers,engineReads=0})
        function comp:get_type_definition() self.engineReads=self.engineReads+1;return {get_field=function(_,name) return name=="Elements" and {} or nil end} end
        function comp:get_field(name)
            self.engineReads=self.engineReads+1
            eq(name,"Elements")
            if self.elementsError then error("injected component elements failure") end
            return {call=function(_,method,index) if method=="get_Count" then return #self.providers elseif method=="get_Item" then return self.providers[index+1] end; error(method) end}
        end
        function comp:call(name)
            self.engineReads=self.engineReads+1
            if name=="get_Valid" then
                if self.validError then error("injected component validity failure") end
                if self.validNil then return nil end
                return self.valid
            end
            error(name)
        end
        self.components[#self.components+1]=comp; return comp
    end
    local scene={get_address=function() return c.sceneId end}
    function scene:call(name)
        eq(name,"findComponents(System.Type)")
        c.sceneScanCount=c.sceneScanCount+1
        if c.scanFail then error("scan unavailable") end
        if c.scanNil then return nil end
        if c.scanTable then
            local result={}
            if c.scanTable~="empty" then
                local offset=c.scanTable=="zero" and 1 or 0
                for i,component in ipairs(c.components) do result[i-offset]=component end
                if c.scanTable=="hole" then result[2]=nil;result[4]=c.components[1] end
                if c.scanTable=="invalid" then result.unexpected=true end
            end
            return result
        end
        if c.scanTyped then
            return {
                get_type_definition=function() return {
                    get_full_name=function() return "via.effect.script.EPVDataBase[]" end,
                    get_method=function(_,method)
                        return (method=="get_Length" or method=="GetValue(System.Int32)") and {} or nil
                    end,
                } end,
                call=function(_,method,index)
                    if method=="get_Length" then return c.scanTypedLength or #c.components end
                    eq(method,"GetValue(System.Int32)");eq(math.type(index),"integer","typed array index ABI")
                    if c.scanMissingElement then return nil end
                    return c.components[index+1]
                end,
            }
        end
        return {get_size=function() return #c.components end,get_element=function(_,i) return c.components[i+1] end}
    end
    local weapon={get_address=function() return c.weaponId end}
    local character={get_address=function() return c.characterId end}
    function character:call(name)
        if name=="get_Weapon" then return weapon end
        if name=="get_WeaponID" then return c.equip end
        if name=="get_WeaponModelID" then return c.model end
        if name=="get_WeaponType" then return c.weaponType end
        error(name)
    end
    env.sdk={
        find_type_definition=function(name) return {name=name} end,
        typeof=function(name) return name end,
        is_managed_object=function(o)
            o.managedProbeReads=(o.managedProbeReads or 0)+1
            if o.managedProbeError then error("injected managed-object probe failure") end
            return not o.dead
        end,
        get_native_singleton=function() return {} end,
        get_managed_singleton=function() return {call=function() if c.noCharacter then return nil end; return {call=function() return character end} end} end,
        create_resource=function(_,path)
            assert(c.in_callback,"allocation outside safe callback")
            if c.noResource then return nil end
            local r={path=path,adds=0,releases=0}
            function r:add_ref() self.adds=self.adds+1 end
            function r:release() self.releases=self.releases+1; eq(self.releases<=self.adds,true,"balanced native refs") end
            function r:create_holder() if c.noHolder then return nil end; return c:holder(path) end
            c.raws[#c.raws+1]=r; return r
        end,
        call_native_func=function(_,td,name,a,b)
            if name=="get_CurrentScene" then if c.noScene then return nil end; return scene end
            if name=="equals(via.ResourceHolder, via.ResourceHolder)" then return a.resource==b.resource end
            if name=="create(System.String)" then
                assert(c.in_callback,"probe creation outside safe callback")
                if c.noObject then return nil end
                local object=managed({destroyed=false})
                local player=managed({auto=true})
                function player:call(method,value)
                    if method=="set_AutoStart(System.Boolean)" then self.auto=value; return end
                    if method=="get_AutoStart()" then return self.auto end
                    if method=="set_Resource(via.effect.EffectResourceHolder)" then eq(self.auto,false,"AutoStart disabled before resource bind"); self.holder=value; return end
                    if method=="get_ReadyResource()" then return c.ready~=false end
                    if method=="get_Running()" then return c.probeRunning==true end
                    if method=="get_Resource()" then return c.wrongProbeHolder or self.holder end
                    if method=="get_ResourcePath()" then
                        if c.nilProbePath then return nil end
                        if c.throwProbePath then error("Unexpected nil") end
                        return self.holder.resource.path
                    end
                    error("probe must never Start/Kill or invoke unexpected method: "..method)
                end
                function object:call(method)
                    if method=="createComponent(System.Type)" then if c.noComponent then return nil end; return player end
                    if method=="destroy" then
                        assert(c.in_callback,"probe destruction outside safe callback")
                        if c.failDestroy then error("injected destruction failure") end
                        self.destroyed=true; return
                    end
                    error(method)
                end
                object.player=player; c.objects[#c.objects+1]=object; return object
            end
            error("unexpected native call "..name)
        end,
    }
    c.capture={list_bindings=function(path)
        local result={}
        for _,b in ipairs(c.captured) do
            local h=b.provider.slots[b.index+1]
            local key=h.resource.path:lower(); local original=c.aliases[key] or key
            if original==path then result[#result+1]={provider=b.provider,index=b.index,holder=h,component=b.component} end
        end
        return result
    end,set_alias=function(new,original) if c.aliasFail then return false,"injected alias failure" end; c.aliases[new:lower()]=original; return true end}
    c.module=assert(loadfile(source,"t",env))(); c.env=env
    assert(c.module.configure({capture=c.capture}))
    function c:tick()
        status_matches(self.module)
        self.in_callback=true; self.callbacks.LockScene(); self.in_callback=false
        status_matches(self.module)
    end
    function c:finish()
        for _=1,10 do self:tick(); if not self.module.snapshot().busy then return end end
        error("mock transaction did not finish")
    end
    function c:apply(original,new)
        local result; local ok,err=self.module.apply(original or ORIGINAL,new or NEW,function(r) result=r end)
        assert(ok,err); self:finish(); return result
    end
    function c:restore()
        local result; assert(self.module.restore_all(function(r) result=r end)); self:finish(); return result
    end
    function c:standard()
        local p=self:provider({ORIGINAL}); self:component({p}); self.captured={{provider=p,index=0}}; return p
    end
    function c:component_bound(paths)
        local p=self:provider(paths or {ORIGINAL});local comp=self:component({p})
        self.captured[#self.captured+1]={provider=p,index=0,component=comp}
        return p,comp
    end
    return c
end
test("queued apply does not allocate or mutate before safe callback",function()
    local c=env_new(); local p=c:standard(); local r
    assert(c.module.apply(ORIGINAL,NEW,function(x)r=x end)); eq(#c.raws,0);eq(#c.writes,0);eq(r,nil)
    c:finish(); eq(r.ok,true);eq(p.slots[1].resource.path,NEW);eq(r.phase,"awaiting_action")
    eq(c.objects[1].destroyed,true);eq(c.raws[1].adds,c.raws[1].releases)
end)
test("exact shared path includes all scene references and excludes sibling files",function()
    local c=env_new(); local a=c:standard(); local b=c:provider({ORIGINAL,OTHER}); c:component({b})
    local r=c:apply();eq(r.ok,true);eq(r.appliedCount,2);eq(a.slots[1].resource.path,NEW);eq(b.slots[1].resource.path,NEW);eq(b.slots[2].resource.path,OTHER)
    eq(#c.writes,2,"capture and scene deduplicated")
end)

test("scene Lua one-based and zero-based arrays include every shared reference",function()
    for _,mode in ipairs({"one","zero"}) do
        local c=env_new();local a=c:standard();local b=c:provider({ORIGINAL,OTHER});c:component({b});c.scanTable=mode
        local r=c:apply();eq(r.ok,true);eq(r.appliedCount,2);eq(#c.writes,2)
        eq(a.slots[1].resource.path,NEW);eq(b.slots[1].resource.path,NEW);eq(b.slots[2].resource.path,OTHER)
        eq(c:restore().ok,true)
    end
end)

test("managed array typed methods work when convenience methods are absent",function()
    local c=env_new();c:standard();c.scanTyped=true;eq(c:apply().ok,true);eq(c:restore().ok,true)
end)

test("generic managed array failures report wrapper and TDB type without truncating",function()
    for _,length in ipairs({16385,-1,1.5}) do
        local c=env_new();c:standard();c.scanTyped=true;c.scanTypedLength=length
        local r=c:apply();eq(r.ok,false);eq(#c.writes,0);eq(#c.raws,0)
        eq(r.message:find("collection=Lua table / via.effect.script.EPVDataBase[]",1,true)~=nil,true)
    end
end)

test("empty Lua scene array succeeds with captured bindings but nil fails closed",function()
    local c=env_new();c:standard();c.scanTable="empty";eq(c:apply().ok,true);eq(c:restore().ok,true)
    local d=env_new();d:standard();d.scanNil=true;local r=d:apply()
    eq(r.ok,false);eq(#d.writes,0);eq(#d.raws,0);eq(r.message:find("nil",1,true)~=nil,true)
end)

test("sparse or mixed Lua scene arrays and missing typed elements fail without partial edits",function()
    for _,mode in ipairs({"hole","invalid","missing"}) do
        local c=env_new();c:standard();local b=c:provider({ORIGINAL});c:component({b})
        if mode=="missing" then c.scanTyped=true;c.scanMissingElement=true else c.scanTable=mode end
        eq(c:apply().ok,false);eq(#c.writes,0);eq(#c.raws,0)
    end
end)

test("three applied references survive failed second scan and restore the first baseline",function()
    local c=env_new();local providers={c:standard()}
    for _=1,2 do local p=c:provider({ORIGINAL});c:component({p});providers[#providers+1]=p end
    eq(c:apply().ok,true);eq(c.module.snapshot().appliedCount,3)
    c.scanTable="hole";local r=c:apply(ORIGINAL,NEW2)
    eq(r.ok,false);eq(r.appliedCount,3);eq(r.requiresRestoreBeforeReset,true)
    for _,p in ipairs(providers) do eq(p.slots[1].resource.path,NEW);eq(p.fields.IgnoreEffectCache,true) end
    -- Restore consumes saved bindings, never the failing scene-array API.
    eq(c:restore().ok,true);eq(c.module.snapshot().appliedCount,0)
    for _,p in ipairs(providers) do eq(p.slots[1].resource.path,ORIGINAL);eq(p.fields.IgnoreEffectCache,false);eq(p.fields.EffectCache,true) end
end)

test("second apply can switch from SDK array to Lua table without losing baseline",function()
    local c=env_new();local providers={c:standard()}
    for _=1,2 do local p=c:provider({ORIGINAL});c:component({p});providers[#providers+1]=p end
    eq(c:apply().ok,true);c.scanTable="zero";local r=c:apply(ORIGINAL,NEW2)
    eq(r.ok,true);eq(r.appliedCount,3)
    for _,p in ipairs(providers) do eq(p.slots[1].resource.path,NEW2) end
    eq(c:restore().ok,true)
    for _,p in ipairs(providers) do eq(p.slots[1].resource.path,ORIGINAL) end
    eq(c.module.version,"1.3.0")
end)
test("fresh wrapper for same resource passes typed equality without offsets",function()
    local c=env_new(); c:standard();c.new_wrappers=true;eq(c:apply().ok,true);eq(c:restore().ok,true)
end)
test("probe path nil and throwing getter fall back to typed resource holder",function()
    for _,mode in ipairs({"nilProbePath","throwProbePath"}) do
        local c=env_new();c:standard();c[mode]=true;eq(c:apply().ok,true)
    end
end)
test("probe mismatched holder is rejected even if its direct path appears correct",function()
    local c=env_new();c:standard();c.wrongProbeHolder=c:holder(OTHER);eq(c:apply().ok,false);eq(#c.writes,0)
end)
test("engine root and native prefixes resolve to one exact canonical resource",function()
    local c=env_new();local p=c:provider({"@/natives/stm/"..ORIGINAL..".5571972"});c:component({p})
    eq(c:apply("NATIVES/STM/"..ORIGINAL,NEW).ok,true);eq(p.slots[1].resource.path,NEW);eq(c:restore().ok,true)
end)
test("multiple versions restore first original and original cache flags",function()
    local c=env_new();local p=c:standard();eq(c:apply().ok,true);eq(p.fields.IgnoreEffectCache,true);eq(p.fields.EffectCache,false)
    eq(c:apply(ORIGINAL,NEW2).ok,true);eq(p.slots[1].resource.path,NEW2)
    eq(c:restore().ok,true);eq(p.slots[1].resource.path,ORIGINAL);eq(p.fields.IgnoreEffectCache,false);eq(p.fields.EffectCache,true);eq(c.module.snapshot().appliedCount,0)
end)
test("two edited paths in the same provider restore shared first cache flags",function()
    local c=env_new();local p=c:provider({ORIGINAL,OTHER});c:component({p})
    eq(c:apply().ok,true);eq(c:apply(OTHER,NEW2).ok,true);eq(c:restore().ok,true)
    eq(p.slots[1].resource.path,ORIGINAL);eq(p.slots[2].resource.path,OTHER);eq(p.fields.IgnoreEffectCache,false);eq(p.fields.EffectCache,true)
end)
test("failure after setter changed a slot reverses the full transaction",function()
    local c=env_new();local a=c:standard();local b=c:provider({ORIGINAL});c:component({b});c.failProvider=b
    local r=c:apply();eq(r.ok,false);eq(a.slots[1].resource.path,ORIGINAL);eq(b.slots[1].resource.path,ORIGINAL);eq(a.fields.IgnoreEffectCache,false);eq(b.fields.EffectCache,true);eq(r.appliedCount,0)
end)
test("second version failure preserves previous successful version and first baseline",function()
    local c=env_new();local p=c:standard();eq(c:apply().ok,true);c.failProvider=p
    eq(c:apply(ORIGINAL,NEW2).ok,false);eq(p.slots[1].resource.path,NEW);eq(c.module.snapshot().appliedCount,1)
    eq(c:restore().ok,true);eq(p.slots[1].resource.path,ORIGINAL)
end)
test("cache setter failure restores cache flags before reporting failure",function()
    local c=env_new();local p=c:standard();c.failFlag="IgnoreEffectCache";eq(c:apply().ok,false);eq(p.fields.IgnoreEffectCache,false);eq(p.fields.EffectCache,true);eq(p.slots[1].resource.path,ORIGINAL)
end)
test("alias registration failure rolls back real bindings",function()
    local c=env_new();local p=c:standard();c.aliasFail=true;eq(c:apply().ok,false);eq(p.slots[1].resource.path,ORIGINAL);eq(c.module.snapshot().appliedCount,0)
end)
test("original holder changed while loading cancels without overwriting other editor",function()
    local c=env_new();local p=c:standard();local r;assert(c.module.apply(ORIGINAL,NEW,function(x)r=x end));c:tick()
    p.slots[1]=c:holder(OTHER);c:finish();eq(r.ok,false);eq(p.slots[1].resource.path,OTHER);eq(#c.writes,0)
end)
test("references appearing while loading are included at commit",function()
    local c=env_new();c:standard();local r;assert(c.module.apply(ORIGINAL,NEW,function(x)r=x end));c:tick()
    local b=c:provider({ORIGINAL});c:component({b});c:finish();eq(r.ok,true);eq(r.appliedCount,2);eq(b.slots[1].resource.path,NEW)
end)
test("equipment change during load cannot write any old resource address",function()
    local c=env_new();local p=c:standard();local r;assert(c.module.apply(ORIGINAL,NEW,function(x)r=x end));c:tick();c.equip=99;c:finish()
    eq(r.ok,false);eq(#c.writes,0);eq(p.slots[1].resource.path,ORIGINAL);eq(c.objects[1].destroyed,true)
end)
test("scene change during load does not destroy old-scene probe or touch old binding",function()
    local c=env_new();c:standard();local r;assert(c.module.apply(ORIGINAL,NEW,function(x)r=x end));c:tick();c.sceneId=99;c:finish()
    eq(r.ok,false);eq(#c.writes,0);eq(c.objects[1].destroyed,false);eq(c.raws[1].releases,1)
end)

test("actor disappearing in the same scene still cleans its unstarted probe",function()
    local c=env_new();local p=c:standard();local r
    assert(c.module.apply(ORIGINAL,NEW,function(x)r=x end));c:tick();c.noCharacter=true;c:finish()
    eq(r.ok,false);eq(#c.writes,0);eq(p.slots[1].resource.path,ORIGINAL)
    eq(c.objects[1].destroyed,true);eq(c.raws[1].adds,c.raws[1].releases)
    eq(c.module.snapshot().requiresRestoreBeforeReset,false)
    assert(c.module.shutdown(function(x)r=x end));c:finish();eq(r.ok,true);eq(r.phase,"closed")
end)
test("restoration after equip change restores the exact pinned provider",function()
    local c=env_new();local p=c:standard();eq(c:apply().ok,true);local n=#c.writes;c.model=98
    eq(c:restore().ok,true);eq(#c.writes,n+1);eq(c.module.snapshot().appliedCount,0);eq(p.slots[1].resource.path,ORIGINAL)
end)

for _,case in ipairs({
    {mock="sceneId",field="sceneAddress",value=110,rebuilt=true},
    {mock="characterId",field="characterAddress",value=120,rebuilt=true},
    {mock="weaponId",field="weaponAddress",value=130,rebuilt=true},
    {mock="equip",field="weaponId",value=102,rebuilt=false},
    {mock="model",field="modelId",value=104,rebuilt=false},
    {mock="weaponType",field="weaponType",value=113,rebuilt=false},
}) do
    test("restore across "..case.field.." change requires the exact retained provider",function()
        local c=env_new();local p=c:standard();eq(c:apply().ok,true)
        local original,writes,reads=c[case.mock],#c.writes,c.providerReads
        c[case.mock]=case.value
        local r=c:restore();eq(r.ok,true);eq(r.phase,"restored");eq(r.appliedCount,0)
        eq(#c.writes,writes+1);eq(c.writes[#c.writes].provider,p)
        eq(p.slots[1].resource.path,ORIGINAL);eq(r.requiresRestart,false)
        eq(r.contextInvalid,false)
        c[case.mock]=original
        r=c:restore();eq(r.ok,true);eq(r.appliedCount,0);eq(r.contextInvalid,false);eq(r.contextDiagnostic,nil)
        eq(p.slots[1].resource.path,ORIGINAL)
    end)
end

test("rebuilt weapon cannot cause old backup to overwrite a replacement provider",function()
    local c=env_new();local p=c:standard();eq(c:apply().ok,true)
    c.weaponId=300;c.equip=99
    local replacement=c:provider({OTHER});c:component({replacement})
    local r=c:restore();eq(r.ok,true);eq(replacement.slots[1].resource.path,OTHER)
    c.equip=2
    local writes=#c.writes;r=c:restore()
    eq(r.ok,true);eq(#c.writes,writes);eq(r.appliedCount,0)
    eq(p.slots[1].resource.path,ORIGINAL);eq(replacement.slots[1].resource.path,OTHER)
end)

test("sampled queue context change with no applied resources does not recommend restart",function()
    local c=env_new();c:standard();c:tick();c.equip=99
    local r=c:apply();eq(r.ok,false);eq(r.appliedCount,0);eq(#c.writes,0);eq(#c.raws,0)
    eq(r.contextDiagnostic.recoveryDisposition,"retry_when_ready");eq(r.contextDiagnostic.restartRecommended,false)
    eq(r.requiresRestart,false);eq(r.message:find("本次尚未替换真实资源",1,true)~=nil,true)
    eq(c:restore().ok,true);eq(c.module.snapshot().contextDiagnostic,nil);eq(c.module.snapshot().contextInvalid,false)
end)

test("temporarily unavailable identity preserves references and permits later restoration",function()
    for _,flag in ipairs({"noCharacter","noScene"}) do
        local c=env_new();local p=c:standard();eq(c:apply().ok,true)
        local writes,reads=#c.writes,c.providerReads;c[flag]=true
        local r=c:restore();eq(r.ok,false);eq(r.appliedCount,1);eq(#c.writes,writes);eq(c.providerReads,reads)
        eq(r.contextDiagnostic.reasonCode,"context_unavailable");eq(r.contextDiagnostic.recoveryDisposition,"retry_when_ready")
        eq(r.contextDiagnostic.restartRecommended,false);eq(r.requiresRestart,false);eq(p.slots[1].resource.path,NEW)
        c[flag]=false;eq(c:restore().ok,true);eq(c.module.snapshot().contextDiagnostic,nil)
    end
end)

test("context diagnostics contain only detached JSON-compatible scalar differences",function()
    local c=env_new();c:standard();c:tick();c.model=98;eq(c:apply().ok,false)
    local function walk(value)
        for key,item in pairs(value) do
            eq(type(key)=="string" or type(key)=="number",true)
            local kind=type(item);eq(kind=="string" or kind=="number" or kind=="boolean" or kind=="table",true)
            if kind=="table" then walk(item) end
        end
    end
    local snapshot=c.module.snapshot();walk(snapshot)
    snapshot.contextDiagnostic.changedFields[1].before="tampered"
    eq(c.module.snapshot().contextDiagnostic.changedFields[1].before,"4")
    eq(c.module.version,"1.3.0")
end)

test("successful controlled shutdown clears old context diagnostics",function()
    local c=env_new();c:standard();eq(c:apply().ok,true);c.model=98
    local r;assert(c.module.shutdown(function(x)r=x end));c:finish()
    eq(r.ok,true);eq(r.phase,"closed");eq(r.contextInvalid,false);eq(r.contextDiagnostic,nil);eq(r.appliedCount,0)
end)
test("readiness timeout releases own resource and never swaps",function()
    local c=env_new();c:standard();c.ready=false;local r;assert(c.module.apply(ORIGINAL,NEW,function(x)r=x end));c:tick();c.clock=c.clock+16;c:finish()
    eq(r.ok,false);eq(#c.writes,0);eq(c.objects[1].destroyed,true);eq(c.raws[1].releases,1)
end)
test("probe is never played and unexpected running is rejected",function()
    local c=env_new();c:standard();c.probeRunning=true;eq(c:apply().ok,false);eq(#c.writes,0);eq(c.objects[1].destroyed,true)
end)
test("probe cleanup failure leaves no real edits and is retried safely",function()
    local c=env_new();c:standard();c.failDestroy=true;eq(c:apply().ok,false);eq(#c.writes,0);eq(c.raws[1].releases,0)
    c.failDestroy=false;c:tick();eq(c.objects[1].destroyed,true);eq(c.raws[1].releases,1)
end)
test("missing component cleans partial allocation",function()
    local c=env_new();c:standard();c.noComponent=true;eq(c:apply().ok,false);eq(c.objects[1].destroyed,true);eq(c.raws[1].releases,1)
end)
test("scene scan failure refuses partial global application",function()
    local c=env_new();c:standard();c.scanFail=true;eq(c:apply().ok,false);eq(#c.writes,0);eq(#c.raws,0)
end)
test("missing cache fields are supported without inventing fields",function()
    local c=env_new();local p=c:provider({ORIGINAL},{});c:component({p});eq(c:apply().ok,true);eq(#c.flagsWrites,0);eq(c:restore().ok,true)
end)
test("restore failure rolls all changed slots back to edited version",function()
    local c=env_new();local a=c:standard();local b=c:provider({ORIGINAL});c:component({b});eq(c:apply().ok,true);c.failProvider=b
    eq(c:restore().ok,false);eq(a.slots[1].resource.path,NEW);eq(b.slots[1].resource.path,NEW);eq(c.module.snapshot().appliedCount,2)
    eq(c:restore().ok,true);eq(a.slots[1].resource.path,ORIGINAL)
end)
test("persistent rollback failure is explicit and blocks new apply",function()
    local c=env_new();local p=c:standard();c.failProvider=p;c.failAlways=true
    local r=c:apply();eq(r.ok,false);eq(r.requiresRestart,true);eq(c.module.apply(ORIGINAL,NEW2),false)
end)
test("busy operations cannot overtake restore",function()
    local c=env_new();c:standard();eq(c:apply().ok,true);assert(c.module.restore_all());eq(c.module.apply(ORIGINAL,NEW2),false);eq(c.module.shutdown(),false);c:finish();eq(c.module.snapshot().appliedCount,0)
end)
test("shutdown restores only in safe callback and stops accepting work",function()
    local c=env_new();local p=c:standard();eq(c:apply().ok,true);local r;assert(c.module.shutdown(function(x)r=x end));eq(p.slots[1].resource.path,NEW)
    c:finish();eq(r.ok,true);eq(r.phase,"closed");eq(p.slots[1].resource.path,ORIGINAL);eq(c.module.apply(ORIGINAL,NEW2),false)
end)

test("shutdown remains active until pending probe cleanup can finish",function()
    local c=env_new();local p=c:standard();eq(c:apply().ok,true)
    c.failDestroy=true;eq(c:apply(ORIGINAL,NEW2).ok,false)
    local r;assert(c.module.shutdown(function(x)r=x end));c:finish()
    eq(r.ok,false);eq(p.slots[1].resource.path,NEW);eq(r.requiresRestoreBeforeReset,true)
    c.failDestroy=false;c:tick()
    assert(c.module.shutdown(function(x)r=x end));c:finish()
    eq(r.ok,true);eq(r.phase,"closed");eq(p.slots[1].resource.path,ORIGINAL)
end)
test("idle restore works outside a character scene",function()
    local c=env_new();c.noCharacter=true;eq(c:restore().ok,true)
end)
test("full script reset never writes native resources outside safe callback",function()
    local c=env_new();c:standard();eq(c:apply().ok,true);local n=#c.writes;c.callbacks.reset();eq(#c.writes,n);eq(c.module.snapshot().requiresRestoreBeforeReset,true)
end)
test("invalid paths and reused version aliases are rejected without allocations",function()
    local c=env_new();c:standard()
    for _,p in ipairs({"C:/x.efx","../x.efx","/x.efx","x.txt"}) do eq(c.module.apply(ORIGINAL,p),false) end
    eq(c.module.apply(ORIGINAL,ORIGINAL),false);eq(#c.raws,0);eq(c:apply().ok,true);eq(c.module.apply(ORIGINAL,NEW),false)
end)

test("generated paths cannot be reassigned across different original resources",function()
    local c=env_new();local p=c:provider({ORIGINAL,OTHER});c:component({p});eq(c:apply().ok,true)
    local writes,raws=#c.writes,#c.raws
    eq(c.module.apply(OTHER,NEW),false);eq(#c.writes,writes);eq(#c.raws,raws)
    eq(p.slots[1].resource.path,NEW);eq(p.slots[2].resource.path,OTHER)
    eq(c.aliases[NEW:lower()],ORIGINAL:lower());eq(c:restore().ok,true)
end)

test("destroyed projectile component does not prevent restoring its strongly retained provider",function()
    local c=env_new();local p,old=c:component_bound();eq(c:apply().ok,true)
    eq(p.refs>0,true,"the restored provider was retained before component destruction")
    old.valid=false;local reads,probes,scans=old.engineReads,old.managedProbeReads,c.sceneScanCount
    local r=c:restore();eq(r.ok,true);eq(p.slots[1].resource.path,ORIGINAL)
    eq(p.fields.IgnoreEffectCache,false);eq(p.fields.EffectCache,true);eq(r.appliedCount,0)
    eq(old.engineReads,reads);eq(old.managedProbeReads,probes);eq(c.sceneScanCount,scans)
    eq(r.restoreSummary.restoredCount,1);eq(r.restoreSummary.reassociatedCount,0)
    eq(r.restoreSummary.pinnedProviderCount,1);eq(r.restoreSummary.restoredPinnedProviderCount,1)
    eq(r.restoreSummary.expiredCount,0);eq(r.restoreSummary.unresolvedCount,0)
end)

test("restoring the retained provider does not substitute or modify another provider with the same path",function()
    local c=env_new();local p,old=c:component_bound();eq(c:apply().ok,true)
    old.valid=false;local unrelated=c:provider({ORIGINAL});c:component({unrelated})
    local writes,unrelatedReads=#c.writes,unrelated.resourceReads
    local r=c:restore();eq(r.ok,true);eq(r.appliedCount,0);eq(r.requiresRestoreBeforeReset,false)
    eq(#c.writes,writes+1);eq(c.writes[#c.writes].provider,p);eq(unrelated.resourceReads,unrelatedReads)
    eq(p.slots[1].resource.path,ORIGINAL);eq(unrelated.slots[1].resource.path,ORIGINAL)
    eq(unrelated.fields.IgnoreEffectCache,false);eq(unrelated.fields.EffectCache,true)
end)

test("retained provider slot changed by another editor is not overwritten or forgotten",function()
    local c=env_new();local p,old=c:component_bound();eq(c:apply().ok,true)
    old.valid=false;p.slots[1]=c:holder(OTHER)
    local writes,flags=#c.writes,#c.flagsWrites
    local r=c:restore();eq(r.ok,false);eq(r.appliedCount,1)
    eq(#c.writes,writes);eq(#c.flagsWrites,flags);eq(p.slots[1].resource.path,OTHER)
    p.slots[1]=c:holder(NEW);eq(c:restore().ok,true);eq(p.slots[1].resource.path,ORIGINAL)
end)

test("matching resource path alone cannot authorize restoration over a different resource instance",function()
    local c=env_new();local p,old=c:component_bound();eq(c:apply().ok,true)
    old.valid=false;local expected=p.slots[1];local foreign=c:holder(NEW)
    foreign.resource={path=NEW};p.slots[1]=foreign
    local writes,flags=#c.writes,#c.flagsWrites
    local r=c:restore();eq(r.ok,false);eq(r.appliedCount,1);eq(r.restoreSummary.unresolvedCount,1)
    eq(#c.writes,writes);eq(#c.flagsWrites,flags);eq(p.slots[1],foreign)
    p.slots[1]=expected;r=c:restore();eq(r.ok,true);eq(p.slots[1].resource.path,ORIGINAL)
end)

test("missing resource slot in a retained provider blocks restoration",function()
    local c=env_new();local p,old=c:component_bound();eq(c:apply().ok,true)
    old.valid=false;local edited=p.slots[1];p.slots={}
    local writes,flags=#c.writes,#c.flagsWrites
    local r=c:restore();eq(r.ok,false);eq(r.appliedCount,1)
    eq(#c.writes,writes);eq(#c.flagsWrites,flags)
    p.slots={edited};eq(c:restore().ok,true);eq(p.slots[1].resource.path,ORIGINAL)
end)

test("provider validity false remains unresolved and never retires the saved baseline",function()
    local c=env_new();local p,old=c:component_bound();eq(c:apply().ok,true)
    old.valid=false;p.valid=false
    local writes,flags,reads=#c.writes,#c.flagsWrites,p.resourceReads
    local r=c:restore();eq(r.ok,false);eq(r.appliedCount,1);eq(r.requiresRestoreBeforeReset,true)
    eq(#c.writes,writes);eq(#c.flagsWrites,flags);eq(p.resourceReads,reads)
    eq(p.slots[1].resource.path,NEW,"unavailable provider is not overwritten")
    eq(r.restoreSummary.restoredCount,0);eq(r.restoreSummary.expiredCount,0);eq(r.expiredBackupCount,0)
    eq(r.restoreSummary.reassociatedCount,0);eq(r.restoreSummary.unresolvedCount,1)
    p.valid=true;r=c:restore();eq(r.ok,true);eq(r.appliedCount,0);eq(p.slots[1].resource.path,ORIGINAL)
end)

test("one unavailable provider blocks all writes while every original baseline remains recoverable",function()
    local c=env_new();local live,oldLive=c:component_bound();local dead,oldDead=c:component_bound()
    eq(c:apply().ok,true);oldLive.valid=false;oldDead.valid=false;dead.valid=false
    local deadReads=dead.resourceReads;local writes,flags=#c.writes,#c.flagsWrites
    local r=c:restore();eq(r.ok,false);eq(r.appliedCount,2);eq(#c.writes,writes);eq(#c.flagsWrites,flags)
    eq(dead.resourceReads,deadReads);eq(dead.slots[1].resource.path,NEW)
    eq(live.slots[1].resource.path,NEW);eq(live.fields.IgnoreEffectCache,true);eq(live.fields.EffectCache,false)
    eq(r.restoreSummary.restoredCount,0);eq(r.restoreSummary.expiredCount,0);eq(r.expiredBackupCount,0)
    eq(r.restoreSummary.unresolvedCount,1);eq(r.restoreSummary.restoredPinnedProviderCount,0)
    dead.valid=true;r=c:restore();eq(r.ok,true);eq(r.appliedCount,0)
    eq(live.slots[1].resource.path,ORIGINAL);eq(dead.slots[1].resource.path,ORIGINAL)
    eq(r.restoreSummary.restoredCount,2);eq(r.restoreSummary.restoredPinnedProviderCount,2)
end)

test("an invalid provider still linked by a live component is unresolved rather than retired",function()
    local c=env_new();local p,old=c:component_bound();eq(c:apply().ok,true)
    p.valid=false
    local writes,flags,reads=#c.writes,#c.flagsWrites,p.resourceReads
    local r=c:restore();eq(r.ok,false);eq(r.appliedCount,1);eq(r.expiredBackupCount,0)
    eq(r.restoreSummary.unresolvedCount,1);eq(r.restoreSummary.expiredCount,0)
    eq(#c.writes,writes);eq(#c.flagsWrites,flags);eq(p.resourceReads,reads)
    eq(old.valid,true);p.valid=true
    eq(c:restore().ok,true);eq(p.slots[1].resource.path,ORIGINAL)
end)

test("multiple unavailable providers remain pending until all can be verified",function()
    local c=env_new();local dead,oldDead=c:component_bound();local live,oldLive=c:component_bound()
    eq(c:apply().ok,true);oldDead.valid=false;dead.valid=false;oldLive.valid=false;live.validNil=true
    local writes,flags=#c.writes,#c.flagsWrites
    local r=c:restore();eq(r.ok,false);eq(r.appliedCount,2);eq(r.expiredBackupCount,0)
    eq(r.restoreSummary.unresolvedCount,2);eq(r.restoreSummary.expiredCount,0)
    eq(#c.writes,writes);eq(#c.flagsWrites,flags)
    live.validNil=nil;r=c:restore();eq(r.ok,false);eq(r.appliedCount,2);eq(r.restoreSummary.unresolvedCount,1)
    eq(#c.writes,writes);eq(#c.flagsWrites,flags)
    dead.valid=true;r=c:restore();eq(r.ok,true);eq(r.appliedCount,0);eq(r.expiredBackupCount,0)
    eq(r.restoreSummary.restoredCount,2);eq(r.restoreSummary.expiredCount,0)
    eq(live.slots[1].resource.path,ORIGINAL);eq(dead.slots[1].resource.path,ORIGINAL)
end)

test("successful repeated apply refreshes the recorded component while retaining the first baseline",function()
    local c=env_new();local p,old=c:component_bound();eq(c:apply().ok,true)
    old.valid=false;c:component({p});eq(c:apply(ORIGINAL,NEW2).ok,true)
    eq(p.slots[1].resource.path,NEW2)
    c.scanFail=true
    local r=c:restore();eq(r.ok,true);eq(r.appliedCount,0);eq(p.slots[1].resource.path,ORIGINAL)
    eq(r.restoreSummary.reassociatedCount,0,"latest recorded component is already live")
    eq(p.fields.IgnoreEffectCache,false);eq(p.fields.EffectCache,true)
end)

test("unknown provider validity blocks the whole restore batch and preserves every baseline",function()
    for _,mode in ipairs({"throws","nil","nonboolean","managed_probe_throws","not_managed"}) do
        local c=env_new();local live,oldLive=c:component_bound();local unknown,oldUnknown=c:component_bound()
        eq(c:apply().ok,true);oldLive.valid=false;oldUnknown.valid=false
        if mode=="throws" then unknown.validError=true
        elseif mode=="nil" then unknown.validNil=true
        elseif mode=="nonboolean" then unknown.valid="false"
        elseif mode=="managed_probe_throws" then unknown.managedProbeError=true
        else unknown.dead=true end
        local writes,flags=#c.writes,#c.flagsWrites
        local r=c:restore();eq(r.ok,false);eq(r.appliedCount,2);eq(r.requiresRestoreBeforeReset,true)
        eq(#c.writes,writes);eq(#c.flagsWrites,flags)
        eq(live.slots[1].resource.path,NEW);eq(unknown.slots[1].resource.path,NEW)
        eq(r.restoreSummary.unresolvedCount,1);eq(r.expiredBackupCount,0)
        unknown.validError=nil;unknown.validNil=nil;unknown.valid=true;unknown.managedProbeError=nil;unknown.dead=nil
        eq(c:restore().ok,true);eq(live.slots[1].resource.path,ORIGINAL);eq(unknown.slots[1].resource.path,ORIGINAL)
    end
end)

test("restoration never probes or calls the old component even when its wrapper is unusable",function()
    for _,mode in ipairs({"throws","nil","nonboolean","not_managed","managed_probe_throws"}) do
        local c=env_new();local p,old=c:component_bound();eq(c:apply().ok,true)
        if mode=="throws" then old.validError=true elseif mode=="nil" then old.validNil=true
        elseif mode=="nonboolean" then old.valid=0 elseif mode=="not_managed" then old.dead=true
        else old.managedProbeError=true end
        local reads,probes=old.engineReads,old.managedProbeReads
        local r=c:restore();eq(r.ok,true);eq(r.appliedCount,0)
        eq(old.engineReads,reads);eq(old.managedProbeReads,probes);eq(p.slots[1].resource.path,ORIGINAL)
        eq(r.restoreSummary.pinnedProviderCount,1);eq(r.restoreSummary.restoredPinnedProviderCount,1)
    end
end)

test("scene enumeration is unnecessary for restoring exact retained providers",function()
    for _,mode in ipairs({"throws","nil","hole","elements"}) do
        local c=env_new();local p,old=c:component_bound();eq(c:apply().ok,true)
        old.valid=false;local fresh=c:component({p})
        if mode=="throws" then c.scanFail=true elseif mode=="nil" then c.scanNil=true
        elseif mode=="hole" then c.scanTable="hole" else fresh.elementsError=true end
        local scans,reads,probes=c.sceneScanCount,old.engineReads,old.managedProbeReads
        local r=c:restore();eq(r.ok,true);eq(r.appliedCount,0);eq(c.sceneScanCount,scans)
        eq(old.engineReads,reads);eq(old.managedProbeReads,probes);eq(p.slots[1].resource.path,ORIGINAL)
    end
end)

test("restore is scoped to recorded providers and never adopts later copies lacking original baselines",function()
    local c=env_new();local p,old=c:component_bound();eq(c:apply().ok,true)
    old.valid=false;c:component({p});local copied=c:provider({NEW});local copiedComponent=c:component({copied})
    local writes,reads,componentReads=#c.writes,copied.resourceReads,copiedComponent.engineReads
    local r=c:restore();eq(r.ok,true);eq(r.appliedCount,0)
    eq(#c.writes,writes+1);eq(c.writes[#c.writes].provider,p)
    eq(copied.resourceReads,reads);eq(copiedComponent.engineReads,componentReads)
    eq(p.slots[1].resource.path,ORIGINAL);eq(copied.slots[1].resource.path,NEW)
    eq(r.scope,"game_session");eq(r.followsNewBindings,true)
end)

test("retained multi-slot provider restores both original resources and shared first cache settings",function()
    local c=env_new();local p,old=c:component_bound({ORIGINAL,OTHER})
    eq(c:apply().ok,true);eq(c:apply(OTHER,NEW2).ok,true)
    old.valid=false
    local r=c:restore();eq(r.ok,true);eq(r.appliedCount,0)
    eq(p.slots[1].resource.path,ORIGINAL);eq(p.slots[2].resource.path,OTHER)
    eq(p.fields.IgnoreEffectCache,false);eq(p.fields.EffectCache,true)
    eq(r.restoreSummary.restoredCount,2);eq(r.restoreSummary.reassociatedCount,0)
    eq(r.restoreSummary.pinnedProviderCount,2);eq(r.restoreSummary.restoredPinnedProviderCount,2)
    eq(r.restoreSummary.expiredCount,0);eq(r.restoreSummary.unresolvedCount,0)
end)

test("provider-only restore setter failure rolls back all slots without touching invalid components",function()
    local c=env_new();local a,oldA=c:component_bound();local b,oldB=c:component_bound()
    eq(c:apply().ok,true);oldA.validError=true;oldB.validError=true;c.failProvider=b
    local readsA,readsB,probesA,probesB=oldA.engineReads,oldB.engineReads,oldA.managedProbeReads,oldB.managedProbeReads
    local r=c:restore();eq(r.ok,false);eq(r.appliedCount,2)
    eq(a.slots[1].resource.path,NEW);eq(b.slots[1].resource.path,NEW)
    eq(a.fields.IgnoreEffectCache,true);eq(b.fields.IgnoreEffectCache,true)
    eq(c:restore().ok,true);eq(a.slots[1].resource.path,ORIGINAL);eq(b.slots[1].resource.path,ORIGINAL)
    eq(a.fields.IgnoreEffectCache,false);eq(b.fields.IgnoreEffectCache,false)
    eq(oldA.engineReads,readsA);eq(oldB.engineReads,readsB);eq(oldA.managedProbeReads,probesA);eq(oldB.managedProbeReads,probesB)
end)

test("apply still rejects an invalid captured component before allocating or changing any resource",function()
    for _,mode in ipairs({"false","throws","nil"}) do
        local c=env_new();local p,comp=c:component_bound()
        if mode=="false" then comp.valid=false elseif mode=="throws" then comp.validError=true else comp.validNil=true end
        local r=c:apply();eq(r.ok,false);eq(r.appliedCount,0)
        eq(#c.raws,0);eq(#c.writes,0);eq(#c.flagsWrites,0);eq(p.slots[1].resource.path,ORIGINAL)
    end
end)

test("apply component invalidation while loading remains blocked despite a valid retained provider",function()
    local c=env_new();local p,comp=c:component_bound();local result
    assert(c.module.apply(ORIGINAL,NEW,function(r) result=r end));c:tick()
    comp.valid=false;c:finish()
    eq(result.ok,false);eq(result.appliedCount,0);eq(#c.writes,0);eq(#c.flagsWrites,0)
    eq(p.slots[1].resource.path,ORIGINAL);eq(c.objects[1].destroyed,true)
end)

test("restore cache failure rolls the retained provider back without accessing its old component",function()
    local c=env_new();local p,old=c:component_bound();eq(c:apply().ok,true)
    old.managedProbeError=true;old.validError=true;c.failFlag="IgnoreEffectCache"
    local reads,probes=old.engineReads,old.managedProbeReads
    local r=c:restore();eq(r.ok,false);eq(r.appliedCount,1)
    eq(p.slots[1].resource.path,NEW);eq(p.fields.IgnoreEffectCache,true);eq(p.fields.EffectCache,false)
    eq(r.restoreSummary.restoredCount,0);eq(r.restoreSummary.restoredPinnedProviderCount,0)
    r=c:restore();eq(r.ok,true);eq(r.appliedCount,0);eq(p.slots[1].resource.path,ORIGINAL)
    eq(p.fields.IgnoreEffectCache,false);eq(p.fields.EffectCache,true)
    eq(old.engineReads,reads);eq(old.managedProbeReads,probes)
end)

test("legacy retired backups retained by a module upgrade keep reset blocked and cannot be silently cleared",function()
    local c=env_new()
    -- Model the state carried by the controlled upgrade bridge. A new session
    -- no longer retires providers, so there is intentionally no public API
    -- that manufactures this old-version state.
    local function find_upvalue(fn,wanted,seen)
        seen=seen or {};if seen[fn] then return nil end;seen[fn]=true
        for i=1,100 do
            local name,value=debug.getupvalue(fn,i);if not name then break end
            if name==wanted then return value end
            if type(value)=="function" and name~="_ENV" then
                local found=find_upvalue(value,wanted,seen);if found~=nil then return found end
            end
        end
    end
    local legacy=assert(find_upvalue(c.module.snapshot,"expired_records"),"upgrade-compatible legacy backup table")
    local baseline={key="legacy:0",originalPath=ORIGINAL};legacy[1]=baseline
    local status=c.module.snapshot();eq(status.expiredBackupCount,1);eq(status.requiresRestoreBeforeReset,true)
    local r=c:restore();eq(r.ok,false);eq(r.expiredBackupCount,1);eq(r.requiresRestoreBeforeReset,true)
    eq(legacy[1],baseline);eq(#c.writes,0);eq(#c.flagsWrites,0)
    assert(c.module.shutdown(function(result) r=result end));c:finish()
    eq(r.ok,false);eq(r.expiredBackupCount,1);eq(r.requiresRestoreBeforeReset,true);eq(legacy[1],baseline)
    c:standard();r=c:apply();eq(r.ok,false);eq(#c.raws,0);eq(#c.writes,0);eq(#c.flagsWrites,0)
end)

test("snapshot contains only JSON-compatible values and no engine objects",function()
    local c=env_new();c:standard();eq(c:apply().ok,true)
    local function walk(t) for k,v in pairs(t) do assert(type(k)=="string" or type(k)=="number"); local kind=type(v);assert(kind=="string" or kind=="boolean" or kind=="number" or kind=="table");if kind=="table" then walk(v) end end end
    walk(c.module.snapshot());eq(c.module.snapshot().sharedScope,true)
    eq(c.module.snapshot().scope,"game_session")
    eq(c.module.snapshot().followsNewBindings,true)
end)
test("session override follows newly loaded references after equipment changes and stops after restore",function()
    local c=env_new();local first=c:standard();eq(c:apply().ok,true)
    c.weaponType=13;c.equip=100;c.model=120
    local nextProvider=c:provider({ORIGINAL});c:component({nextProvider})
    for _=1,120 do c:tick() end
    eq(nextProvider.slots[1].resource.path,NEW);eq(first.slots[1].resource.path,NEW)
    eq(c.module.snapshot().sessionResourceCount,1);eq(c.module.snapshot().followError,nil)
    eq(c:restore().ok,true);eq(nextProvider.slots[1].resource.path,ORIGINAL);eq(first.slots[1].resource.path,ORIGINAL)
    local later=c:provider({ORIGINAL});c:component({later});for _=1,120 do c:tick() end
    eq(later.slots[1].resource.path,ORIGINAL);eq(c.module.snapshot().sessionResourceCount,0)
end)
test("current status observes migrated backup tables immediately and returns independent scalars",function()
    local c=env_new();status_matches(c.module)
    local records=assert(find_upvalue(c.module.snapshot,"records"))
    local retained=assert(find_upvalue(c.module.snapshot,"retained"))
    local expired=assert(find_upvalue(c.module.snapshot,"expired_records"))
    records["migrated:0"]={};local s=c.module.current_status()
    eq(s.appliedCount,1);eq(s.requiresRestoreBeforeReset,true);status_matches(c.module)
    records["migrated:0"]=nil;retained[1]={};s=c.module.current_status()
    eq(s.appliedCount,0);eq(s.requiresRestoreBeforeReset,true);status_matches(c.module)
    retained[1]=nil;expired[1]={};s=c.module.current_status()
    eq(s.expiredBackupCount,1);eq(s.requiresRestoreBeforeReset,true);status_matches(c.module)
    expired[1]=nil;s=c.module.current_status();eq(s.requiresRestoreBeforeReset,false)
    s.appliedCount=100;s.busy=true;eq(c.module.current_status().appliedCount,0);eq(c.module.current_status().busy,false)
end)
test("scalar status avoids full session-resource snapshots in the frame safety gate",function()
    local c=env_new();local resources=assert(find_upvalue(c.module.snapshot,"session_overrides"))
    local records=assert(find_upvalue(c.module.snapshot,"records"))
    for i=1,512 do resources["art/vfx/test_"..i..".efx"]={generated=NEW..i};records[i]={} end
    local function measure(name,read)
        collectgarbage("collect");collectgarbage("stop")
        local memory,started=collectgarbage("count"),os.clock()
        for _=1,600 do local s=read();eq(s.appliedCount,512);eq(s.requiresRestoreBeforeReset,true);eq(s.busy,false) end
        local seconds,allocated=os.clock()-started,collectgarbage("count")-memory
        collectgarbage("restart")
        print(("PERF %s iterations=600 resources=512 lua_seconds=%.6f allocated_kib=%.3f"):format(name,seconds,allocated))
        return allocated
    end
    local full=measure("bindings_snapshot",c.module.snapshot)
    local current=measure("bindings_current_status",c.module.current_status)
    eq(current<full/5,true,"scalar read removes most allocation");status_matches(c.module)
end)
print(string.format("RESULT action bindings: %d passed, %d failed, %d assertions",passed,failed,assertions))
if failed>0 then error("action binding regression failed") end
