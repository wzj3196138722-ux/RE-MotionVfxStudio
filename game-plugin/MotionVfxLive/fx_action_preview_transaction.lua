-- Paired action/PFB activation. The engine adapter runs this only at its
-- verified scene-update boundary; it owns resource references and readback.
local M={}
local function copy(t)
    if type(t)~="table" then return t end
    local out={};for k,v in pairs(t) do out[k]=copy(v) end;return out
end
function M.new(adapter)
    local self={state={phase="idle",busy=false,appliedCount=0,requiresRestore=false},
        originals={},order={},current={},request=nil,pending=nil,changed={}}
    local function status(phase,message,busy)
        self.state.phase=phase;self.state.message=message;self.state.busy=busy==true
        local n=0;for _ in pairs(self.current)do n=n+1 end
        self.state.appliedCount=n;self.state.requiresRestore=#self.order>0
    end
    local function free_prepared()
        if self.pending then adapter.release_prepared(self.pending);self.pending=nil end
        self.context=nil
    end
    function self:apply(plan)
        if self.state.busy then return false,"动作预览仍在处理中。" end
        if self.state.phase=="restore_failed" then return false,"请先完成上一轮预览还原。" end
        if type(plan)~="table" or plan.schema~=1 or type(plan.id)~="string" or #plan.id~=32
            or not plan.id:match("^[a-f0-9]+$") or type(plan.resources)~="table" or #plan.resources>65 then
            return false,"动作预览数据无效。"
        end
        local motion,pfb=0,0
        for _,r in ipairs(plan.resources) do
            if type(r.path)~="string" or not r.path:match("^art/MotionVfxLive/ActionPreview/"..plan.id.."/")
                or r.path:find("..",1,true) then return false,"动作预览路径无效。" end
            if r.kind=="motlist" then motion=motion+1 elseif r.kind=="pfb" then pfb=pfb+1 else return false,"不支持的动作预览资源。" end
        end
        if motion~=1 or pfb==0 then return false,"动作预览缺少动作表或 PFB。" end
        self.request=copy(plan);self.changed={};self.cancel=false
        status("prepare","正在准备动作表与对应 PFB。",true);return true
    end
    function self:restore()
        -- Cancel a load before its first mutation; do not lose the baseline
        -- retained from any earlier successful preview.
        self.cancel=true;self.request=nil
        if not self.pending and #self.order==0 and #self.changed==0 then
            status("idle","动作槽位没有待还原的引用。",false);return true
        end
        if self.state.phase~="rollback" then status("restore","正在还原动作槽位预览。",true) end
        return true
    end
    local function rollback(reason)
        self.failure=tostring(reason);status("rollback","预览未完成，正在回退已修改的引用。",true)
    end
    local function restore_changed()
        for i=#self.changed,1,-1 do
            local change=self.changed[i]
            adapter.restore(change.target,change.before,change.after)
            adapter.release(change.before)
            table.remove(self.changed,i)
        end
    end
    function self:tick()
        if not self.state.busy then return end
        local phase=self.state.phase
        local ok,err=pcall(function()
            if phase=="prepare" then
                -- resolve rejects scene/equipment changes before loading files.
                self.context=adapter.resolve(self.request,self.current)
                self.pending=adapter.prepare(self.request,self.context)
                self.started=adapter.now();status("loading","等待整组动作/PFB 资源加载。",true)
            elseif phase=="loading" then
                adapter.validate(self.context,self.current)
                if adapter.now()-self.started>15 then error("动作/PFB 加载超时；未更换角色引用。") end
                if adapter.ready(self.pending) then status("activate","正在热重载动作与 PFB。",true) end
            elseif phase=="activate" then
                adapter.validate(self.context,self.current)
                local targets=adapter.targets(self.context,self.pending)
                assert(#targets==#self.request.resources,"预览目标与资源数量不一致")
                local seen={}
                for _,target in ipairs(targets)do
                    assert(type(target.key)=="string" and not seen[target.key],"动作预览目标重复或无效")
                    seen[target.key]=true
                end
                -- PFBs first: the new clip must never address an unavailable ID.
                table.sort(targets,function(a,b)return a.kind=="pfb" and b.kind~="pfb" end)
                for _,target in ipairs(targets) do
                    local before=adapter.read(target)
                    if not self.originals[target.key] then
                        self.originals[target.key]={target=target,value=adapter.retain(before)}
                        self.order[#self.order+1]=target.key
                    end
                    -- Record intent BEFORE a setter: native setters may mutate
                    -- successfully and then throw or fail their readback.
                    local change={target=target,before=adapter.retain(before),after=target.value}
                    self.changed[#self.changed+1]=change
                    adapter.write(target,target.value,before)
                    adapter.verify(target,target.value)
                end
                for _,change in ipairs(self.changed) do
                    local previous=self.current[change.target.key]
                    self.current[change.target.key]={target=change.target,value=adapter.retain(change.after)}
                    if previous then adapter.release(previous.value) end
                    adapter.release(change.before)
                end
                self.state.appliedRevision=self.request.revision;self.state.appliedId=self.request.id
                self.changed={};free_prepared();self.request=nil
                status("active","动作槽位预览已热重载，请重新执行对应动作。",false)
            elseif phase=="rollback" then
                restore_changed();free_prepared();self.request=nil
                if self.cancel then status("restore","正在还原之前的动作预览。",true)
                else status("error","动作预览失败，已回退："..self.failure,false) end
            elseif phase=="restore" then
                restore_changed();free_prepared()
                -- A later application may introduce another PFB after a motion
                -- was already backed up. Restore ALL motions before any PFB.
                table.sort(self.order,function(a,b)
                    return self.originals[a].target.kind=="pfb" and self.originals[b].target.kind~="pfb"
                end)
                for i=#self.order,1,-1 do
                    local key=self.order[i];local original=self.originals[key]
                    local installed=self.current[key]
                    adapter.restore(original.target,original.value,installed and installed.value)
                    if installed then adapter.release(installed.value) end
                    adapter.release(original.value)
                    self.originals[key]=nil;self.current[key]=nil;table.remove(self.order,i)
                end
                self.state.appliedRevision=nil;self.state.appliedId=nil
                status("idle","已还原动作表及对应 PFB 的原始引用。",false)
            end
        end)
        if not ok then
            if phase=="restore" or phase=="rollback" then
                status("restore_failed","动作预览还原未完成；备份仍保留："..tostring(err),false)
            elseif #self.changed>0 then rollback(err)
            else
                free_prepared();self.request=nil
                status("error","动作预览未应用："..tostring(err),false)
            end
        end
        local n=0;for _ in pairs(self.current)do n=n+1 end
        self.state.appliedCount=n;self.state.requiresRestore=#self.order>0 or #self.changed>0
    end
    function self:snapshot()return copy(self.state)end
    return self
end
return M
