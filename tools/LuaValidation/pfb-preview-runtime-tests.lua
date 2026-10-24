local root=assert(arg[1])
local previous=arg;arg={root,"fixture"}
local fixture=assert(loadfile(root.."/../../tools/LuaValidation/action-preview-runtime-tests.lua"))()
arg=previous
local passed=0
local function check(v,m)assert(v,m);passed=passed+1;print("PASS "..m)end
local function setup()
    local c=fixture()
    local call=c.manager.call
    function c.manager:call(m,...)
        if m=="getExternalDataContainerNum" then return 1 end
        if m=="getStandardData(System.UInt32, System.Int32, System.Boolean)" then
            local args={...};assert(args[2]==0 or args[2]==-1,"Native EPV route takes external index, not dataContainerId")
            return not c.wrong_route and c.provider or nil
        end
        return call(self,m,...)
    end
    function c.external:call(m,...)
        if m=="get_dataContainerId" then return 2 end
        return call(self,m,...)
    end
    local module=assert(loadfile(root.."/fx_pfb_preview_runtime.lua","t",c.env))()
    c.runtime=module.new({capture=function()return {actor={sceneEpoch=c.sceneEpoch,characterIdentity=tostring(c.character.address)}}end,
        blocked=function()return c.blocked end})
    function c:plan(n,source)
        local token=string.rep(tostring(n or 1),32)
        source=source or self.original_external.path
        return {schema=1,id=token,revision=n or 1,source={pfbType="effect",originalPath=source,expectedElements={{container=1,id=80}}},
            resources={{kind="pfb",originalPath=source,path="art/MotionVfxLive/PrefabPreview/"..token.."/resource.pfb"}}}
    end
    return c
end
do
    local c=setup();c.runtime:restore();check(c.runtime:snapshot().phase=="idle" and not c.runtime:snapshot().busy,"Restore without resource ownership completes synchronously")
    local proof=assert(c.runtime:resolve(c.original_external.path))
    check(#proof.providers==1 and proof.providers[1].externalIndex==0,"Full PFB path resolves to actual external owner")
    local plan=c:plan();plan.proof=proof
    check(c.runtime:apply(plan),"Independent EPV plan queues")
    check(#c.writes==0,"apply does not mutate outside the scene boundary")
    c:drain()
    check(c.runtime:snapshot().phase=="active" and c.writes[1]=="external","Scene tick rebuilds actual external EPV holder")
    check(c.bank.holder==c.original_motion,"Independent PFB preview leaves motion bank untouched")
    check(c.runtime:snapshot().appliedCount==1 and c.runtime:snapshot().requiresRestore,"Applied holder is retained with baseline")
    local first=c.external.fields.DataContainer
    c.runtime:apply(c:plan(2));c:drain()
    check(c.runtime:snapshot().phase=="active" and c.external.fields.DataContainer~=first,"Repeated application resolves private alias to original path")
    c:restore()
    check(c.external.fields.DataContainer==c.original_external,"Restoration returns first original reference after two revisions")
    c:clean()
end
do
    local c=setup();c.original_external=c.original_root;c.external.fields.DataContainer=c.original_root;c.external.fields.DataContainerPath=c.original_root.path
    c.runtime:apply(c:plan(1,c.original_root.path));c:drain()
    check(c.runtime:snapshot().appliedCount==2 and #c.writes==2,"All current holders of the same original PFB are rebuilt")
    c:restore();check(c.external.fields.DataContainer==c.original_root and c.manager.fields.DataContainer==c.original_root,"All matching owners restore together")
end
do
    local c=setup();c.runtime:apply(c:plan(1,"art/another/weapon.pfb"));c:drain()
    check(c.runtime:snapshot().phase=="error" and #c.writes==0,"Same basename at another full path is rejected")
    local p=c:plan();p.source.pfbType="equipment"
    check(not c.runtime:apply(p) and #c.writes==0,"Equipment prefab cannot masquerade as an EPV hot reload")
    p=c:plan();p.resources[1].path="art/MotionVfxLive/PrefabPreview/"..p.id.."/../other.pfb"
    check(not c.runtime:apply(p),"Private preview path traversal is rejected")
end
do
    local c=setup();c.runtime:apply(c:plan());c.runtime:tick();c.sceneEpoch=2;c:drain()
    check(c.runtime:snapshot().phase=="error" and #c.writes==0,"Scene change during resource load prevents activation")
end
do
    local c=setup();c.new_not_ready=true;c.runtime:apply(c:plan());c.runtime:tick();c.clock=20;c:drain()
    check(c.runtime:snapshot().phase=="error" and #c.writes==0,"Prefab readiness timeout leaves live resources untouched")
end
do
    local c=setup();c.fail_before_update=true;c.runtime:apply(c:plan());c:drain()
    check(c.runtime:snapshot().phase=="error" and c.external.fields.RequestDataContainer==nil and c.external.fields.DataContainer==c.original_external,"Failure after request creation cancels owned request and preserves original")
    c:restore()
end
do
    local c=setup();c.missing_element=true;c.runtime:apply(c:plan());c:drain()
    check(c.runtime:snapshot().phase=="error" and c.external.fields.DataContainer==c.original_external,"Missing expected EPV element causes native holder rollback")
    c:restore()
end
do
    local c=setup();c.wrong_route=true;c.runtime:apply(c:plan());c:drain()
    check(c.runtime:snapshot().phase=="error" and c.external.fields.DataContainer==c.original_external,"Unrouted provider cannot be reported as a successful native activation")
    c:restore()
end
do
    local c=setup();c.runtime:apply(c:plan());c:drain();local edited=c.external.fields.DataContainer
    c.weapon=3;c.runtime:restore();c:drain()
    check(c.runtime:snapshot().phase=="restore_failed" and c.runtime:snapshot().requiresRestore and c.external.fields.DataContainer==edited,"Equipment change retains backups and never writes stale owner")
    c.weapon=2;c:restore();check(c.external.fields.DataContainer==c.original_external,"Restore succeeds after returning to proven actor/equipment")
end
do
    local c=setup();c.runtime:apply(c:plan());c:drain();local edited=c.external.fields.DataContainer
    c.external.fields.DataContainer=c.make("pfb","art/foreign.pfb");c.external.fields.DataContainerPath="art/foreign.pfb"
    c.runtime:restore();c:drain()
    check(c.runtime:snapshot().phase=="restore_failed" and c.runtime:snapshot().requiresRestore,"Foreign modification is not overwritten and original backup remains")
    c.external.fields.DataContainer=edited;c.external.fields.DataContainerPath=edited.path;c:restore()
end
do
    local c=setup();c.runtime:apply(c:plan());c.runtime:tick();c.runtime:restore();c:drain()
    check(c.runtime:snapshot().phase=="idle" and #c.writes==0 and not c.runtime:snapshot().requiresRestore,"Cancel loading before activation leaves no live resource change")
end
do
    local c=setup();c.original_external.path="GameDesign/EPV/Weapon/epvr_it03.pfb";c.external.fields.DataContainerPath=c.original_external.path
    c.runtime:apply(c:plan());c:drain()
    check(c.runtime:snapshot().phase=="active","Verified GameDesign original EPV path is supported independently of art preview output")
    c:restore()
end
print("Independent PFB runtime results: "..passed.." passed")
