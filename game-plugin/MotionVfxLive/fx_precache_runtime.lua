local M={}
local types={
    mesh={{"via.render.MeshResource","via.render.MeshResourceHolder"}},
    mdf2={{"via.render.MeshMaterialResource","via.render.MeshMaterialResourceHolder"}},
    tex={{"via.render.TextureResource","via.render.TextureResourceHolder"}},
    uvs={{"via.effect.UVSequenceResource","via.effect.UVSequenceResourceHolder"}},
    efx={{"via.effect.EffectResource","via.effect.EffectResourceHolder"}},
    pfb={{"via.Prefab","via.Prefab"}},
    motlist={{"via.motion.MotionListResource","via.motion.MotionListResourceHolder"}},
    mot={{"via.motion.MotionResource","via.motion.MotionResourceHolder"}},
}
function M.new()
    local self={records={},queue={},active=true}
    function self:prepare(entries,paths)
        if not self.active then return false end
        for _,e in ipairs(entries or {}) do
            local path=paths[e.alias]
            if type(path)~="string" or not path:lower():match("^art/motionvfxlive/precache/") or path:find("..",1,true) then
                return false,"预缓存引擎路径无效"
            end
        end
        self.queue={}
        for _,e in ipairs(entries or {}) do
            local old=self.records[e.id]
            if not old or old.path~=paths[e.alias] then self.queue[#self.queue+1]={entry=e,path=paths[e.alias]} end
        end
        return true
    end
    local function release(record)
        if record.holder then pcall(function() record.holder:release() end);record.holder=nil end
        if record.raw then pcall(function() record.raw:release() end);record.raw=nil end
    end
    function self:tick()
        if not self.active then return end
        local item=table.remove(self.queue,1)
        if not item then return end
        local entry=item.entry
        local record={path=item.path,phase="requested",message="等待游戏加载",kind=entry.kind}
        local ok,err=pcall(function()
            local chosen
            for _,candidate in ipairs(types[entry.kind] or {}) do
                if sdk.find_type_definition(candidate[1]) then chosen=candidate;break end
            end
            if not chosen then error("当前引擎未暴露此类型的预加载接口；应用时由引擎按引用加载") end
            record.raw=sdk.create_resource(chosen[1],item.path)
            if not record.raw then error("引擎拒绝预加载请求") end
            record.raw:add_ref()
            record.type=chosen[1]
            if sdk.find_type_definition(chosen[2]) then
                record.holder=record.raw:create_holder(chosen[2])
                if record.holder then record.holder:add_ref() end
            end
            -- A non-null create_resource result is NOT proof of GPU readiness.
            record.message="预加载请求已接受"
        end)
        if not ok then release(record);record.phase="error";record.message=tostring(err) end
        local old=self.records[entry.id]
        if not ok and old and old.raw then
            old.failedPath=item.path;old.phase="error";old.message=tostring(err).."；保留上一版预加载引用"
            return
        end
        if old then release(old) end
        self.records[entry.id]=record
    end
    function self:snapshot()
        local result={}
        for id,r in pairs(self.records) do result[id]={path=r.path,phase=r.phase,message=r.message,type=r.type} end
        return result
    end
    function self:close()
        self.active=false;self.queue={}
        for _,record in pairs(self.records) do release(record) end
        self.records={}
    end
    return self
end
return M
