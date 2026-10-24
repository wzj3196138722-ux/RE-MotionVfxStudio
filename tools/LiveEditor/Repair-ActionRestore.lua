-- Recover only an idle failed action preview from the pre-1.1.0 adapter.
-- Invalid userdata is never read/released. Proven original paths are loaded
-- anew; current actor/bank/PFB identities and owned private paths must match.
-- The old Lua state stays quarantined, while the normal transaction restores
-- the newly loaded baselines at pre LockScene. No original file is overwritten.
local D=assert(debug)
local function cell(fn,name)
    for i=1,128 do
        local n,v=D.getupvalue(fn,i);if not n then break end
        if n==name then return {fn=fn,index=i,value=v,id=D.upvalueid(fn,i),name=name}end
    end
    error("Unsupported closure: "..name)
end
local function get(c)local n,v=D.getupvalue(c.fn,c.index);assert(n==c.name);return v end
local function same(a,b)return type(a)=="string" and type(b)=="string" and a:lower()==b:lower()end
local function clone(t)
    if type(t)~="table" then return t end
    local v={};for k,x in pairs(t)do v[k]=clone(x)end;return v
end
local core=assert(_G.MotionVfxLive,"MotionVfx Live is not loaded")
assert(not (_G.MotionVfxActionRecovery and _G.MotionVfxActionRecovery.running),"Recovery is already running")
local liveCell=cell(core.action_preview_status,"action_preview")
assert(cell(core.preview_action,"action_preview").id==liveCell.id,"Preview closure differs")
local oldLive=assert(liveCell.value)
local old=cell(oldLive.restore,"runtime").value
local oldEngine=cell(old.tick,"adapter").value
local host=cell(oldEngine.resolve,"host").value
assert(old.state.phase=="restore_failed" and not old.state.busy and #old.order>0,"Expected an idle failed restoration")
assert(not old.pending and not old.request and #old.changed==0,"Partial activation needs a different recovery")
assert(not core.state.pending and not host.blocked(),"Finish other editor operations first")
local token=assert(old.state.appliedId)
assert(#token==32 and token:match("^[a-f0-9]+$"),"Missing preview identity")
local aliases=oldLive:aliases()
local adapter=assert(loadfile("reframework/autorun/MotionVfxLive/fx_action_preview_runtime.lua"))()
local transaction=assert(loadfile("reframework/autorun/MotionVfxLive/fx_action_preview_transaction.lua"))()
assert(adapter.version=="1.1.0","Install the fixed reference adapter first")
local live=adapter.new(transaction,host)
local replacement=cell(live.restore,"runtime").value
local engine=cell(replacement.tick,"adapter").value
local task={running=true,phase="validate",started=os.time(),rows={}}
_G.MotionVfxActionRecovery=task
local prepared,committed
local targets,resources={},{}
local function report()
    task.observedAt=os.time()
    json.dump_file("MotionVfxLive/action-restore-repair.json",task)
end
local function fail(err)
    task.running=false;task.phase="failed";task.error=tostring(err)
    if prepared and not committed then pcall(engine.release_prepared,prepared);prepared=nil end
    core.state.message="动作预览修复已停止，恢复记录仍保留："..tostring(err)
    report()
end
local function active_path(t)
    local actual,owner=engine.read(t) -- Reacquires and validates all live identities.
    assert(actual,"Current resource is absent")
    local path=actual:call(t.kind=="pfb" and "get_Path" or "get_ResourcePath")
    assert(same(path,t.expectedPath),"Current resource is not this preview; no other edit will be overwritten")
    if t.kind=="pfb" then
        assert(same(owner:get_field("DataContainerPath"),path),"PFB index path differs")
        local pending=owner:get_field("RequestDataContainer")
        assert(not pending or pending:call("get_IsEmpty"),"Another PFB request is pending")
        assert(owner:get_field("RequestDeleteDataContainer")~=true,"PFB deletion is pending")
    else
        assert(owner:call("get_MotionListValid") and not owner:call("get_MotionListEmpty"),"Current motion bank is unavailable")
        assert(same(owner:call("get_TargetMotionList"):call("get_ResourcePath"),path),"Motion target differs")
    end
    return actual
end
report()
re.on_pre_application_entry("LockScene",function()
    if not task.running then return end
    local ok,err=pcall(function()
        if committed then
            local s=live:snapshot();task.status=s
            if s.phase=="idle" and not s.requiresRestore then
                task.running=false;task.phase="restored"
                core.state.message="已重新加载原始资源并还原动作表和 PFB；编辑内容仍保留。"
            elseif s.phase=="restore_failed" then error(s.message)end
            report();return
        end
        assert(get(liveCell)==oldLive and old.state.phase=="restore_failed" and not old.state.busy,"Preview state changed during recovery")
        assert(not core.state.pending and not host.blocked(),"Another editor operation started")
        if task.phase=="validate" then
            assert(#old.order<=65,"Too many recovery targets")
            for _,key in ipairs(old.order)do
                local record=assert(old.originals[key]);local t=record.target
                assert(old.current[key] and (t.kind=="pfb" or t.kind=="motlist"),"Missing applied target")
                local n={key=key,kind=t.kind,proof=clone(t.proof),provider=clone(t.provider),
                    bankIdentity=t.bankIdentity,originalPath=t.originalPath}
                assert(type(n.originalPath)=="string" and n.originalPath~="","Original path is absent")
                local prefix=("art/MotionVfxLive/ActionPreview/"..token.."/"):lower()
                for path,canonical in pairs(aliases)do
                    if same(canonical,n.originalPath) and path:lower():sub(1,#prefix)==prefix and path:lower():sub(-#n.kind-1)=="."..n.kind then
                        assert(not n.expectedPath,"Ambiguous preview path");n.expectedPath=path
                    end
                end
                assert(n.expectedPath,"No verified alias for applied resource")
                active_path(n)
                targets[#targets+1]=n
                resources[#resources+1]={kind=n.kind,path=n.originalPath}
                task.rows[#task.rows+1]={key=key,originalPath=n.originalPath,previewPath=n.expectedPath}
            end
            prepared=engine.prepare({resources=resources},{initial={}})
            task.phase="loading";task.loadStarted=os.time();report()
        elseif task.phase=="loading" then
            for _,t in ipairs(targets)do active_path(t)end
            assert(os.time()-task.loadStarted<=15,"Original resources did not become ready")
            if not engine.ready(prepared)then return end
            -- Complete all validation before switching the controller. The old
            -- native userdata remains strongly reachable and is never released.
            for i,t in ipairs(targets)do
                local actual=active_path(t)
                local baseline=prepared.items[i].value
                assert(same(baseline:call(t.kind=="pfb" and "get_Path" or "get_ResourcePath"),t.originalPath))
                t.value=actual
                replacement.originals[t.key]={target=t,value=engine.retain(baseline)}
                replacement.current[t.key]={target=t,value=engine.retain(actual)}
                replacement.order[#replacement.order+1]=t.key
            end
            replacement.state=clone(old.state)
            local archive=_G.MotionVfxActionRecoveryArchive or {};_G.MotionVfxActionRecoveryArchive=archive
            archive[#archive+1]={live=oldLive,runtime=old,reason="Legacy adapter prematurely released managed references"}
            assert(D.setupvalue(liveCell.fn,liveCell.index,live)==liveCell.name)
            committed=true
            _G.MotionVfxActionPreviewRuntime=adapter;_G.MotionVfxActionPreviewTransaction=transaction
            engine.release_prepared(prepared);prepared=nil
            task.phase="restoring";replacement:restore();report()
        end
    end)
    if not ok then fail(err)end
end)
