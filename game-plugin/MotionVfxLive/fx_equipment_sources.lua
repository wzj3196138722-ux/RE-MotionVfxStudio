-- Exact current equipment-prefab provenance through the player's own loader.
-- Read-only: no allocation of engine objects, setters, or filename guessing.
local M={version="1.0.0"}
local function id(o)return o and tostring(o:get_address())end
local function norm(p)return type(p)=="string" and p:gsub("\\","/"):lower()end
local weapon_roles={"WEAPON_MAIN","WEAPON_SUB","DUMMY_WEAPON_MAIN","DUMMY_WEAPON_SUB"}
local armor_roles={"ARMOR_HELM","ARMOR_BODY","ARMOR_ARM","ARMOR_WAIST","ARMOR_LEG","ARMOR_SLINGER"}
local role_getter={WEAPON_MAIN="get_Weapon",WEAPON_SUB="get_SubWeapon",DUMMY_WEAPON_MAIN="get_DummyWeapon",DUMMY_WEAPON_SUB="get_DummySubWeapon"}
local function read_enum(td,name)
    local f=assert(td:get_field(name),"游戏缺少装备资源槽枚举："..name)
    return assert(tonumber(f:get_data(nil)),"装备资源槽枚举无效："..name)
end
function M.resolve(host,original)
    local target=norm(original)
    assert(target and target:match("%.pfb$") and not target:find("..",1,true) and (target:match("^art/") or target:match("^gamedesign/")),"请填写完整的游戏装备 PFB 路径")
    local function canonical(p)
        local aliases=host.aliases and host.aliases() or {};local key=norm(p)
        return key and norm(aliases[key] or p)
    end
    local actor=assert(host.capture().actor,"角色尚未就绪")
    local pm=assert(sdk.get_managed_singleton("app.PlayerManager"),"角色管理器尚未就绪")
    local player=assert(pm:call("getMasterPlayer"),"当前没有本地角色，请先进入游戏场景")
    local ch=assert(player:call("get_Character"),"本地角色尚未加载完成")
    assert(ch and id(ch)==actor.characterIdentity,"本地角色来源已变化")
    local list=assert(pm:get_field("_PlayerList"),"玩家加载器列表尚未就绪")
    local n=list:get_size();assert(n>=1 and n<=256,"玩家加载器列表数量异常")
    local control
    for i=0,n-1 do local item=list:get_element(i)
        if item then local info=item:get_field("_PlayerInfo");local current=info and info:call("get_Character")
            if current and id(current)==id(ch) then assert(not control,"当前角色匹配到多个加载器");control=item end
        end
    end
    assert(control,"找不到当前角色所属的装备加载器")
    local loader=assert(control:get_field("_HunterLoader"),"当前角色装备加载器未就绪")
    assert(loader:call("get_NowLoading")==false,"游戏正在加载装备，请等待完成")
    local enum=assert(sdk.find_type_definition("app.PlayerDef.LOAD_CONTENTS"))
    local array=assert(loader:get_field("_LoadPrefabs"));local count=array:get_size()
    assert(count>=1 and count<=256,"装备 PFB 缓存数量异常")
    local context={character=ch,control=control,loader=loader,array=array,weaponPrefabs={},roles={}}
    local proof={kind="equipment",proof="player_loader_exact_prefab_path",sceneEpoch=actor.sceneEpoch,actorIdentity=id(ch),
        weaponType=ch:call("get_WeaponType"),weaponId=ch:call("get_WeaponID"),weaponModelId=ch:call("get_WeaponModelID"),
        controlIdentity=id(control),loaderIdentity=id(loader),originalPath=original,relatedPrefabs={}}
    local function add(name,kind)
        local index=read_enum(enum,name);assert(index>=0 and index<count,"装备资源槽超出当前加载器范围")
        local p=array:get_element(index)
        local getter=loader:call("getLoadedPrefab(app.PlayerDef.LOAD_CONTENTS)",index)
        assert((not p and not getter) or (p and getter and id(p)==id(getter)),"装备加载器的数组与读取接口不一致")
        local observed=p and p:call("get_Path")
        local slot={role=name,index=index,value=p}
        context.roles[name]=slot
        if kind=="weapon" then context.weaponPrefabs[#context.weaponPrefabs+1]=slot end
        proof.relatedPrefabs[#proof.relatedPrefabs+1]={role=name,index=index,prefabIdentity=id(p),observedPath=observed,path=canonical(observed)}
        if canonical(observed)==target then
            assert(not proof.role,"多个装备槽同时使用此 PFB，需先明确要重建的槽位")
            assert(p:call("get_Ready") and p:call("get_Valid"),"当前装备 PFB 尚未加载完成")
            proof.role=name;proof.category=kind;proof.loadIndex=index;proof.prefabIdentity=id(p);proof.observedPath=observed
            context.target=slot
        end
    end
    for _,name in ipairs(weapon_roles)do add(name,"weapon")end
    for _,name in ipairs(armor_roles)do add(name,"armor")end
    assert(proof.role,"当前角色装备加载器没有持有此完整路径的 PFB；不会根据文件名猜测替换")
    if proof.category=="weapon" then
        assert(ch:call("isWeaponBuildEnd"),"当前武器仍在构建")
        local weapon=assert(ch:call(role_getter[proof.role]),"目标武器槽没有实际实例")
        local go=weapon:call("get_GameObject")
        assert(go and go:call("get_Valid"),"当前武器实例无效")
        proof.weaponIdentity=id(weapon);proof.gameObjectIdentity=id(go);proof.gameObjectName=go:call("get_Name")
        proof.getter=role_getter[proof.role];context.weapon=weapon;context.gameObject=go
    else
        -- Verified in cPlayerManageControl.updateLoadWaitHasInstance: loader
        -- ARMOR_HELM..SLINGER are passed to changeParts indices 0..5.
        local parts={ARMOR_HELM=0,ARMOR_BODY=1,ARMOR_ARM=2,ARMOR_WAIST=3,ARMOR_LEG=4,ARMOR_SLINGER=5}
        assert(ch:call("isPartsBuildEnd"),"当前防具仍在构建")
        proof.partsIndex=read_enum(assert(sdk.find_type_definition("app.ArmorDef.ARMOR_PARTS")),proof.role:gsub("^ARMOR_",""))
        assert(proof.partsIndex==parts[proof.role],"当前游戏防具部位枚举与已核实的装备重建接口不一致")
        local go=assert(ch:call("getParts(app.ArmorDef.ARMOR_PARTS)",proof.partsIndex),"目标防具槽没有实际实例")
        assert(go:call("get_Valid"),"当前防具实例无效")
        context.gameObject=go;context.createOption=assert(control:get_field("_CreateOption"),"角色装备构建选项未就绪")
        proof.gameObjectIdentity=id(go);proof.gameObjectName=go:call("get_Name");proof.createOptionIdentity=id(context.createOption)
    end
    return proof,context
end
return M
