-- Tests only the targeted MDF-XL prefix. Never loads a full third-party mod,
-- starts the game, or writes to the user's installed scripts or settings.
local project=assert(arg[1],"project path required")
local source_path=arg[2]
local function read(path)
    local file=assert(io.open(path,"rb"));local value=file:read("*a");file:close();return value
end
local guard=read(project.."/tools/MdfxlCompatibility/MDF-XL-frame-guard.lua")
local checks=0
local function check(value,label)
    assert(value,label);checks=checks+1;print("PASS "..label)
end
local function context()
    local c={callbacks={},draws={},entries={},messages={},infos={},time=100}
    local env=setmetatable({}, {__index=_G});env._G=env
    env.re={
        on_frame=function(callback)c.callbacks[#c.callbacks+1]=callback;return "registered",#c.callbacks end,
        on_draw_ui=function(callback)c.draws[#c.draws+1]=callback end,
        on_application_entry=function(name,callback)c.entries[#c.entries+1]={name,callback} end,
    }
    env.os={time=function()return c.time end,clock=function()return c.time end}
    env.log={warn=function(message)
        if c.throw_log then error("fixture log failure") end
        c.messages[#c.messages+1]=message
    end}
    env.log.error=env.log.warn
    env.log.info=function(message)
        if c.throw_log then error("fixture log failure") end
        c.infos[#c.infos+1]=message
    end
    c.env=env;c.original_re=env.re;c.original_frame=env.re.on_frame
    c.load=function(source,prefix)
        return assert(load((prefix==false and "" or guard).."\n"..source,"@MDF-XL-test.lua","t",env))()
    end
    c.dispatch=function()
        -- REFramework's catch surrounds the complete callback iteration.
        return pcall(function()for _,callback in ipairs(c.callbacks) do callback() end end)
    end
    return c
end

do
    local c=context();local env=c.env
    local draw,entry=env.re.on_draw_ui,env.re.on_application_entry
    env.fixture_callback=function(...)return nil,false,"retained",select("#",...),... end
    local proxy=c.load([[re.on_frame(fixture_callback)
re.on_draw_ui(function() end)
re.on_application_entry("BeginRendering",function() end)
return re]])
    check(env.re==c.original_re and env.re.on_frame==c.original_frame,"prefix leaves shared re registration unchanged")
    check(proxy~=env.re and proxy.on_frame~=env.re.on_frame,"only this MDF chunk gets the local frame proxy")
    check(proxy.on_draw_ui==draw and proxy.on_application_entry==entry and #c.draws==1 and #c.entries==1,
        "draw and application callbacks pass through unmodified")
    local values=table.pack(c.callbacks[1]("x",nil,7))
    check(values.n==7 and values[1]==nil and values[2]==false and values[3]=="retained" and
        values[4]==3 and values[5]=="x" and values[6]==nil and values[7]==7,
        "successful callback preserves arguments and multiple returns including nil")
    local s=assert(env.MotionVfxMdfxlCompatibility)
    check(s.version=="1.0.0" and s.registeredCallbacks==1 and s.callbackFrames==1 and s.errorCount==0,
        "successful callback reports scoped compatibility status")
    local later=function()error("fixture other mod error")end
    env.re.on_frame(later)
    check(c.callbacks[2]==later,"later unrelated registrations are not wrapped")
    local ok,err=c.dispatch()
    check(not ok and tostring(err):find("fixture other mod error",1,true),"unrelated script errors are not swallowed")
end

do
    local c=context();local env=c.env
    env.fixture_fail=true;env.fixture_calls=0
    c.load([[re.on_frame(function()
fixture_calls=fixture_calls+1
if fixture_fail then error("fixture stale get_GameObject") end
return "recovered"
end)]])
    local later=0;env.re.on_frame(function()later=later+1 end)
    check(c.dispatch() and later==1,"MDF error no longer aborts later callback execution")
    local s=assert(env.MotionVfxMdfxlCompatibility)
    check(s.errorCount==1 and s.consecutiveErrors==1 and s.lastErrorTime==100 and
        s.lastError:find("fixture stale get_GameObject",1,true) and s.lastError:find("stack traceback",1,true),
        "error status retains original failure, time and stack traceback")
    check(#c.messages==1,"first callback error is logged")
    local values=table.pack(c.callbacks[1]())
    check(values.n<=1 and values[1]==nil,"failed callback has no fake success value")
    c.time=109;c.dispatch()
    check(#c.messages==1 and s.errorCount==3,"repeated errors are counted without repeated logs inside ten seconds")
    c.time=110;c.dispatch()
    check(#c.messages==2 and s.errorCount==4,"same callback can log again after ten seconds")
    env.fixture_fail=false
    check(c.callbacks[1]()=="recovered" and env.fixture_calls==5 and s.consecutiveErrors==0,
        "callback is retried and recovers on the next successful frame")
    local count=s.errorCount;c.dispatch()
    check(later==4 and s.errorCount==count,"recovery does not duplicate or disable later callbacks")
    env.fixture_fail=true;c.throw_log=true;c.time=120
    check(c.dispatch() and later==5 and s.errorCount==count+1,"a failing log sink cannot escape the MDF guard")
end

do
    local c=context();local env=c.env
    c.load([[local fn=function()error("fixture repeated registrations")end
re.on_frame(fn)
re.on_frame(fn)]])
    local s=assert(env.MotionVfxMdfxlCompatibility)
    check(s.registeredCallbacks==2 and #c.callbacks==2,"separate registrations retain their intended callback count")
    check(c.dispatch() and s.errorCount==2 and #c.messages==2,"throttling is per registration, not shared across callbacks")
    c.dispatch()
    check(s.errorCount==4 and #c.messages==2,"both failing registrations remain active without per-frame log spam")
end

do
    local c=context();local env=c.env
    local values=table.pack(c.load("return re.on_frame(function() end)"))
    check(values.n==2 and values[1]=="registered" and values[2]==1,
        "registration returns are preserved as well as callback returns")
    local forwarded
    env.re.on_frame=function(value)forwarded=value;error("original invalid callback validation")end
    local proxy=c.load("return re")
    local ok,err=pcall(proxy.on_frame,"not a function")
    check(not ok and forwarded=="not a function" and tostring(err):find("original invalid callback validation",1,true),
        "invalid registrations keep the original REFramework validation")
end

do
    local c=context();local env=c.env
    env.os=false;env.debug=false
    env.fixture_error=setmetatable({}, {__tostring=function()error("unprintable error object")end})
    c.load("re.on_frame(function()error(fixture_error)end)")
    for _=1,25 do assert(c.dispatch(),"missing clock or bad error object escaped protection") end
    local s=assert(env.MotionVfxMdfxlCompatibility)
    check(s.errorCount==25 and #c.messages==1,"absent clock does not generate an error log on every frame")
    check(s.lastError:find("unreadable error",1,true)~=nil,"unprintable error objects are handled even without traceback support")
end

-- Read only the exact final on_frame registration from an optional local mod.
-- No third-party source is copied into the repository or output package.
local actual_source
if source_path then
    local source=read(source_path)
    local original,original_error=load(source,"@MDF-XL-original.lua","t",{})
    check(type(original)=="function","complete original MDF source compiles without executing it: "..tostring(original_error))
    local patched,patched_error=load(guard.."\n"..source,"@MDF-XL-patched.lua","t",{})
    check(type(patched)=="function","complete patched MDF source compiles within Lua local-variable limits: "..tostring(patched_error))
    local begin_at=assert(source:find("--MARK:On Frame",1,true),"MDF On Frame marker absent")
    local end_at=assert(source:find("--MARK:On Draw UI",begin_at,true),"MDF On Draw UI marker absent")
    actual_source=source:sub(begin_at,end_at-1)
    check(actual_source:find("manage_MasterMaterialData_MHWS",1,true)~=nil,
        "local MDF frame registration contains the production material update")
else
    actual_source=[[re.on_frame(function()
if reframework.get_game_name()=="mhwilds" then
update_MDFXLViaHotkeys_MHWS()
manage_MasterMaterialData_MHWS(MDFXL,MDFXLSub,MDFXLSaveDataChunks)
isUpdaterBypass=false;changed=false;wc=false
for _,entry in pairs(MDFXL) do entry.isUpdated=false end
end
end)]]
    print("NOTE local MDF path omitted; using a portable material-update failure fixture")
end
for _,protected in ipairs({false,true}) do
    local c=context();local env=c.env
    env.reframework={get_game_name=function()return "mhwilds" end}
    env.MDFXL={{isUpdated=true}};env.MDFXLSub={};env.MDFXLSaveDataChunks={}
    env.fixture_stale=true
    local hotkeys,materials,editor=0,0,0
    local component={get_GameObject=function()
        if env.fixture_stale then error("via.Component.get_GameObject: System.InvalidOperationException") end
        return {}
    end}
    env.update_MDFXLViaHotkeys_MHWS=function()hotkeys=hotkeys+1 end
    env.manage_MasterMaterialData_MHWS=function()materials=materials+1;component:get_GameObject()end
    c.load(actual_source,protected)
    env.re.on_frame(function()editor=editor+1 end)
    local ok,err=c.dispatch()
    check(env.MDFXL[1].isUpdated==true,"failed MDF update does not execute success-only state resets")
    if protected then
        check(ok and hotkeys==1 and materials==1 and editor==1,
            "exact MDF callback: stale component failure does not skip MotionVfx")
        check(env.MotionVfxMdfxlCompatibility.lastError:find("get_GameObject",1,true)~=nil,
            "exact MDF callback: component failure remains diagnosable")
    else
        check(not ok and tostring(err):find("get_GameObject",1,true) and editor==0,
            "baseline exact MDF callback reproduces skipped MotionVfx update")
    end
    env.fixture_stale=false
    check(c.dispatch() and hotkeys==2 and materials==2 and editor==(protected and 2 or 1) and
        env.MDFXL[1].isUpdated==false,"MDF material-update success still completes and later callbacks run once")
end
print("MDF-XL scoped compatibility checks: "..checks.." passed; no game or installation writes.")
