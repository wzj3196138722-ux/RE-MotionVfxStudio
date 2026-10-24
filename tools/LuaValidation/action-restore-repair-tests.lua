local project=arg[1]
local load=loadfile
local env=setmetatable({arg={project.."/game-plugin/MotionVfxLive","fixture"}},{__index=_G})
local fixture=assert(load(project.."/tools/LuaValidation/action-preview-runtime-tests.lua","t",env))()
local function up(fn,name)
    for i=1,128 do local n,v=debug.getupvalue(fn,i);if not n then break end;if n==name then return v end end
    error("Missing upvalue "..name)
end
local function scenario(foreign,notReady)
    local c=fixture();c.runtime:apply(c:plan(1,true));c:drain()
    local previous=c.runtime
    local old=up(previous.restore,"runtime")
    -- Reproduce the observed state: a live bank holds the private path, while
    -- legacy backups/expected holders are invalid. Do not ever call them.
    local expected=old.current["motion|"..c.motion.address.."|0"].value
    c.bank.holder=c.make("holder",expected.path)
    previous:restore();c:drain();assert(old.state.phase=="restore_failed")
    c.invalidReads=0
    local function invalid()c.invalidReads=c.invalidReads+1;error("Invalid stale userdata")end
    expected.call=invalid;c.original_motion.call=invalid;c.original_root.call=invalid;c.original_external.call=invalid
    if foreign then c.bank.holder.path="art/another-mod.motlist" end
    local action_preview=previous
    local core={state={}}
    function core.action_preview_status()return action_preview:snapshot()end
    function core.preview_action(plan)return action_preview:apply(plan)end
    local callbacks={}
    c.env._G.MotionVfxLive=core
    c.env.json={dump_file=function(_,value)c.report=value end}
    c.env.re={on_pre_application_entry=function(name,fn)assert(name=="LockScene");callbacks[#callbacks+1]=fn end}
    c.env.loadfile=function(path)
        local name=assert(path:match("/([^/]+)$"))
        return load(project.."/game-plugin/MotionVfxLive/"..name,"t",c.env)
    end
    c.writes={};if notReady then c.new_not_ready=true end
    assert(load(project.."/tools/LiveEditor/Repair-ActionRestore.lua","t",c.env))()
    for _=1,10 do
        action_preview:tick()
        for _,fn in ipairs(callbacks)do fn()end
    end
    assert(c.invalidReads==0,"Recovery touched invalid userdata")
    if foreign then
        assert(c.report.phase=="failed" and action_preview==previous and #c.writes==0)
        assert(#old.order==3 and old.state.requiresRestore)
        print("PASS foreign resource change blocks recovery without losing old records")
    elseif notReady then
        assert(c.report.phase=="loading" and action_preview==previous and #c.writes==0)
        c.sceneEpoch=2
        for _,fn in ipairs(callbacks)do fn()end
        assert(c.report.phase=="failed" and #c.writes==0 and action_preview==previous)
        print("PASS recovery waits for original resources and rejects scene changes before writes")
    else
        assert(c.report.phase=="restored" and action_preview~=previous and not core.action_preview_status().requiresRestore)
        assert(c.bank.holder.path=="Motion/original.motlist" and c.manager.fields.DataContainer.path=="art/character.pfb" and c.external.fields.DataContainer.path=="art/weapon.pfb")
        assert(c.writes[1]=="motion" and #c.writes==3)
        assert(c.env._G.MotionVfxActionRecoveryArchive[1].runtime==old and #old.order==3)
        print("PASS restores freshly loaded original motion before both PFBs; never accesses/releases stale holders")
    end
    c:clean()
end
scenario(false,false)
scenario(true,false)
scenario(false,true)
