local root=assert(arg[1]);local args=arg;arg={root,"fixture"}
local setup=assert(loadfile(root.."/../../tools/LuaValidation/equipment-source-tests.lua"))();arg=args
local passed=0
local function check(v,s)assert(v,s);passed=passed+1;print("PASS "..s)end
local function clone(t)if type(t)~="table"then return t end;local r={};for k,v in pairs(t)do r[k]=clone(v)end;return r end
local function fixture()
    local c=setup();local old=c.character.call;c.installed={};c.rebuilds={}
    c.host.capture=function()
        c.captureCalls=(c.captureCalls or 0)+1
        return {actor={characterIdentity=tostring(c.character.address),sceneEpoch=c.captureCalls==c.failWorldOn and 2 or c.sceneEpoch}}
    end
    c.host.blocked=function()return c.blocked end
    c.originalComponents={
        {gameObjectNames={"weapon_root"},className="via.motion.Chain2",classIndex=0,enabled=true,resources={{fieldName="ChainAsset",path="art/old.chain2",index=-1}}},
        {gameObjectNames={"weapon_root"},className="via.character.CollisionShapePreset",classIndex=0,enabled=true,resources={{fieldName="CollisionShapePresetInfos",path="art/old.clsp",index=0}}},
        {gameObjectNames={"weapon_root"},className="via.motion.JointConstraints",classIndex=0,enabled=true,resources={{fieldName="Layer",path="art/old.jcns",index=0}}},
        {gameObjectNames={"weapon_root"},className="via.effect.script.ObjectEffectManager2",classIndex=0,enabled=true,resources={{fieldName="DataContainer",path="",index=-1},{fieldName="ExternalDataContainers",path="art/old.pfb",index=0,dataContainerId=7}}},
    }
    function c:makeGo(rows)
        local go=self.make("equipment_go");local base=go.call;local components={};local tr=self.make("transform")
        function tr:call(m)assert(m=="get_Child");return nil end
        for _,row in ipairs(rows)do
            local comp=self.make("component");components[#components+1]=comp
            function comp:get_type_definition()return {get_full_name=function()return row.className end}end
            function comp:call(m,...)
                local a={...}
                if m=="get_Enabled"then if c.componentReadyAt and c.clock<c.componentReadyAt then return not row.enabled end;return row.enabled
                elseif m=="get_ChainAsset"then return c.make("holder",row.resources[1].path)
                elseif m=="getCollisionShapePresetInfosCount" or m=="getLayerCount"then return #row.resources
                elseif m=="getCollisionShapePresetInfos(System.UInt32)" or m=="getLayer(System.Int32)"then
                    local r=row.resources[a[1]+1];local info=c.make("resourceinfo")
                    function info:call(name)assert(name=="get_Resource" or name=="get_JointConstraintsAsset");return c.make("holder",r.path)end;return info
                elseif m=="getExternalDataContainerNum"then return #row.resources-1
                elseif m=="getExtenalDataConatiner(System.Int32)"then
                    local r=row.resources[a[1]+2];local ext=c.make("external");ext.fields.DataContainer=c.make("pfb",r.path)
                    function ext:call(name)assert(name=="get_dataContainerId");return r.dataContainerId end;return ext
                end
                return base(self,m,...)
            end
            if row.className=="via.effect.script.ObjectEffectManager2"then comp.fields.DataContainer=c.make("pfb",row.resources[1].path)end
        end
        function go:call(m,...)
            if m=="get_Name"then return "weapon_root"elseif m=="get_Transform"then return tr
            elseif m=="get_Components"then return {get_size=function()return #components end,get_element=function(_,i)return components[i+1]end}end
            return base(self,m,...)
        end
        return go
    end
    c.weapon_go=c:makeGo(c.originalComponents);local originalGo=c.weapon_go
    setmetatable(c.array,{__newindex=function(_,i,v)assert(type(i)=="number" and v,"Native SystemArray assignment requires a managed reference");c.prefabs[i+1]=v;c.writes[#c.writes+1]={index=i,value=v}
        if c.fail_array then c.fail_array=false;error("setter failed after write")end
    end})
    function c.character:call(m,...)
        if m:match("^changeWeapon%(") or m:match("^changeParts%(")then
            local a={...};local p
            if m:match("^changeWeapon")then
                assert(a[1]==c.weapon and a[2]==25,"changeWeapon must use model ID, not equipment ID")
                assert(a[4]==c.prefabs[2] and a[5]==nil and a[6]==nil,"Unedited secondary/dummy slots are preserved")
                p=a[3]
            else assert(a[1]==0 and a[3]==c.control.fields._CreateOption,"Armor slot and original construction option are preserved");p=a[2]end
            c.rebuilds[#c.rebuilds+1]={method=m,args=a,path=p.path};c.building=true;c.buildFrames=2
            if c.delay_restore and not c.installed[p.path]then c.componentReadyAt=c.clock+4 end
            if not c.no_rebuild then
                local rows=clone(c.installed[p.path] or c.originalComponents)
                if c.wrong_component and c.installed[p.path]then rows[1].resources[1].path="art/wrong.chain2"end
                c.weapon_go=c:makeGo(rows)
            end
            if c.fail_rebuild then c.fail_rebuild=false;error("native request failed after mutation")end
            return
        end
        return old(self,m,...)
    end
    c.env.package={loaded={}}
    c.env.require=function(name)assert(name=="MotionVfxLive.fx_equipment_sources");return c.module end
    c.adapter=assert(loadfile(root.."/fx_equipment_pfb_preview_runtime.lua","t",c.env))()
    c.runtime=c.adapter.new(c.host)
    function c:plan(n,armor)
        local token=string.rep(tostring(n or 1),32);local p="art/MotionVfxLive/PrefabPreview/"..token.."/resource.pfb"
        local rows=clone(c.originalComponents);rows[1].resources[1].path="art/new"..tostring(n or 1)..".chain2"
        rows[2].resources[2]={fieldName="CollisionShapePresetInfos",path="art/new.clsp",index=1}
        rows[3].resources[2]={fieldName="Layer",path="art/new.jcns",index=1}
        rows[4].resources[3]={fieldName="ExternalDataContainers",path="art/new.pfb",index=1,dataContainerId=11}
        self.installed[p]=rows
        local original=self.paths[armor and 5 or 1]
        return {schema=1,id=token,revision=n or 1,source={pfbType="equipment",originalPath=original,equipmentComponents=rows},resources={{kind="pfb",originalPath=original,path=p}}}
    end
    function c:tick()
        self.clock=self.clock+1
        if self.building and not self.stuck then self.buildFrames=self.buildFrames-1;if self.buildFrames==0 then self.building=false end end
        self.runtime:tick()
    end
    function c:drain()for _=1,90 do if not self.runtime:snapshot().busy then break end;self:tick()end end
    function c:restore()self.runtime:restore();self:drain();assert(self.runtime:snapshot().phase=="idle",self.runtime:snapshot().message)end
    return c
end
do
    local c=fixture();local original=c.prefabs[1];local oldroot=c.weapon_go
    check(c.runtime:apply(c:plan()),"Equipment plan queues with explicit metadata")
    check(#c.writes==0 and #c.rebuilds==0,"apply never rebuilds outside the pre-LockScene tick")
    c:drain();local s=c.runtime:snapshot();assert(s.phase=="active",s.message)
    check(c.weapon_go~=oldroot and s.verifiedComponents==4,"Success requires a newly instantiated equipped object and actual component readback")
    check(c.rebuilds[1].args[2]==25 and c.rebuilds[1].args[2]~=89,"Native weapon rebuild receives model ID rather than item ID")
    check(c.prefabs[1].path==c:plan().resources[1].path and original.refs==1,"Loader cache and first original prefab are both retained correctly")
    c.runtime:apply(c:plan(2));c:drain();check(c.runtime:snapshot().phase=="active","A second edit resolves its owned private path and rebuilds again")
    c:restore();check(c.prefabs[1]==original and #c.rebuilds==3,"Restore rebuilds first original equipment after multiple revisions")
end
do
    local c=fixture();local original=c.prefabs[5];c.runtime:apply(c:plan(1,true));c:drain()
    local s=c.runtime:snapshot();assert(s.phase=="active",s.message)
    check(c.rebuilds[1].method:match("^changeParts")~=nil,"Armor uses the verified changeParts route")
    c:restore();check(c.prefabs[5]==original and #c.rebuilds==2,"Armor cache, original construction options and components restore")
end
do
    local c=fixture();local original=c.prefabs[1];c.wrong_component=true;c.runtime:apply(c:plan());c:drain()
    check(c.runtime:snapshot().phase=="error" and c.prefabs[1]==original and #c.rebuilds==2,"Mismatched component resource triggers actual equipment rollback")
end
do
    local c=fixture();local original=c.prefabs[1];c.fail_rebuild=true;c.runtime:apply(c:plan());c:drain()
    check(c.runtime:snapshot().phase=="error" and c.prefabs[1]==original and #c.rebuilds==2,"Native throw after mutation retains backup and rebuilds baseline")
end
do
    local c=fixture();local original=c.prefabs[1];c.fail_array=true;c.runtime:apply(c:plan());c:drain()
    check(c.runtime:snapshot().phase=="error" and c.prefabs[1]==original,"Partially changed loader arrays are rolled back without losing original")
end
do
    local c=fixture();c.runtime:apply(c:plan());c:tick();c.sceneEpoch=2;c:drain()
    check(c.runtime:snapshot().phase=="error" and #c.rebuilds==0,"Scene change during loading prevents any equipment rebuild")
    c=fixture();c.blocked=true;c.runtime:apply(c:plan());c:drain()
    check(c.runtime:snapshot().phase=="error" and #c.writes==0,"Other resource operations block equipment rebuilding")
end
do
    local c=fixture();c.runtime:apply(c:plan());c:drain();local owned=c.prefabs[1];c.prefabs[1]=c.make("pfb","art/foreign.pfb")
    c.runtime:restore();c:drain()
    check(c.runtime:snapshot().phase=="restore_failed" and c.runtime:snapshot().requiresRestore and #c.rebuilds==1,"Foreign equipment cache changes are not overwritten; backups remain")
    c.prefabs[1]=owned;c:restore();check(c.runtime:snapshot().requiresRestore==false,"Restoration can resume after owned reference is restored")
end
do
    local c=fixture();c.runtime:apply(c:plan());c:tick();c:tick();assert(c.runtime:snapshot().phase=="building")
    c.runtime:restore();c:drain()
    check(c.runtime:snapshot().phase=="idle" and #c.rebuilds==2,"Cancel during native build waits and rebuilds original equipment")
end
do
    local c=fixture();c.runtime:apply(c:plan());c:drain();local ok=c.runtime:apply(c:plan(2,true))
    check(not ok and c.runtime:snapshot().phase=="active","Another equipment target requires restoring the current one first")
    c:restore()
end
do
    local c=fixture();c.runtime:apply(c:plan());c:tick();c:restore();check(#c.rebuilds==0 and not c.runtime:snapshot().requiresRestore,"Cancel before activation has no live-resource effects")
    c=fixture();c.no_rebuild=true;c.runtime:apply(c:plan());c:drain()
    check(c.runtime:snapshot().phase=="restore_failed" and c.runtime:snapshot().requiresRestore,"Resource load alone cannot masquerade as successful rebuild or restoration")
end
do
    local c=fixture();local original=c.prefabs[1];c.failWorldOn=3;c.runtime:apply(c:plan());c:drain()
    check(c.runtime:snapshot().phase=="error" and not c.runtime:snapshot().requiresRestore and c.prefabs[1]==original and #c.writes==0,
        "First rebuild precondition failure releases tentative ownership before any mutation")
    c.failWorldOn=nil;c:restore();check(#c.rebuilds==0,"No-op restoration after preparation failure stays synchronous and performs no rebuild")
    c=fixture();original=c.prefabs[1];c.runtime:apply(c:plan());c:drain();local previous=c.prefabs[1]
    c.captureCalls=0;c.failWorldOn=4;c.runtime:apply(c:plan(2));c:drain()
    check(c.runtime:snapshot().phase=="error" and c.runtime:snapshot().requiresRestore and c.prefabs[1]==previous and #c.rebuilds==1,
        "Repeated-apply precondition failure preserves previous live state and first baseline")
    c.failWorldOn=nil;c:restore();check(c.prefabs[1]==original,"Previous baseline can still restore after a rejected second rebuild")
end
do
    local c=fixture();c.runtime:apply(c:plan());c:drain();c.delay_restore=true;c:restore()
    check(not c.runtime:snapshot().requiresRestore,"Restoration waits through delayed component initialization before confirming success")
end
do
    local c=fixture();local original=c.prefabs[1];c.runtime:apply(c:plan());c:drain();c.fail_array=true
    c.runtime:restore();c:drain()
    check(c.runtime:snapshot().phase=="restore_failed" and c.runtime:snapshot().requiresRestore and c.prefabs[1]==original,
        "Restore setter failure after cache mutation preserves original backup")
    c:restore();check(c.prefabs[1]==original and #c.rebuilds==2,"A partially completed restore can retry and rebuild the original equipment")
end
do
    local c=fixture();local original=c.prefabs[1];c.runtime:apply(c:plan());c:drain();c.wrong_component=true;c.runtime:apply(c:plan(2))
    for _=1,15 do c:tick();if c.runtime:snapshot().phase=="rollback"then break end end
    assert(c.runtime:snapshot().phase=="rollback");c.wrong_component=false;c.runtime:restore();c:drain()
    check(c.runtime:snapshot().phase=="idle" and c.prefabs[1]==original,"Cancel during second-edit rollback continues restoring the first original rather than the preceding edit")
end
do
    local c=fixture();c.env.require=function()error("Manual Run Script has no autorun package.path")end
    c.env.loadfile=function(p)
        local name=p:match("^reframework/autorun/MotionVfxLive/(fx_equipment[%w_]+%.lua)$");assert(name,"Unexpected fallback path")
        return loadfile(root.."/"..name,"t",c.env)
    end
    local facade=assert(loadfile(root.."/fx_pfb_preview_runtime.lua","t",c.env))();c.runtime=facade.new(c.host)
    local proof=assert(c.runtime:resolve(c.paths[1],"equipment"))
    check(proof.role=="WEAPON_MAIN" and c.env.package.loaded["MotionVfxLive.fx_equipment_sources"]~=nil and
        c.env.package.loaded["MotionVfxLive.fx_equipment_pfb_preview_runtime"]~=nil,"Manual script fallback loads both modules into the canonical Reload cache keys")
    c.runtime:apply(c:plan());c:drain();local another,err=c.runtime:resolve("art/character.pfb","effect")
    check(not another and err:find("还原",1,true)~=nil,"Facade prevents switching to EPV while equipment references remain owned")
    c:restore()
end
do
    local c=fixture();local plan=c:plan();c.installed[plan.resources[1].path]=clone(plan.source.equipmentComponents)
    plan.source.equipmentComponents[1].resources[1].path="@Art/new1.chain2"
    c.runtime:apply(plan);c:drain()
    check(c.runtime:snapshot().phase=="active","Serialized @ resource marker and case do not cause a false mismatch with native resource paths")
    c:restore()
end
print("Equipment preview results: "..passed.." passed; mocked native API, no live equipment writes")
