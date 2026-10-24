-- Fixed read-only probe. Never loads resources, invokes changeWeapon, or writes
-- live fields. Addresses refer only to objects discovered in this callback.
local pending=true
re.on_pre_application_entry("LockScene",function()
    if not pending then return end;pending=false
    local result={observedAt=os.time()}
    local ok,value=pcall(function()
        local out={actorReady=false,methods={},prefabs={},loadContents={},loadedByRole={}}
        local signatures={
            {"app.HunterCharacter","changeWeapon(app.WeaponDef.TYPE, System.Int32, via.Prefab, via.Prefab, via.Prefab, via.Prefab)"},
            {"app.mcHunterWeaponBuilder","changeWeapon(app.WeaponDef.TYPE, System.Int32, via.Prefab, via.Prefab, via.Prefab, via.Prefab)"},
            {"app.cPlayerManageControl","updateLoadWaitHasInstance()"},
            {"app.cPlayerManageControl","onChangeWeaponFromReserve()"},
            {"app.HunterCharacter","get_Weapon()"},
            {"app.HunterCharacter","get_WeaponID()"},
            {"app.HunterCharacter","get_WeaponModelID()"},
            {"app.HunterCharacter","changeParts(System.Int32, via.Prefab, ace.Bitset`1<app.HunterDef.CREATE_HUNTER_OPTION>)"},
            {"app.HunterCharacter","getParts(app.ArmorDef.ARMOR_PARTS)"},
            {"app.cHunterLoader","getLoadedPrefab(app.PlayerDef.LOAD_CONTENTS)"},
        }
        for _,pair in ipairs(signatures)do
            local m=sdk.find_type_definition(pair[1]):get_method(pair[2])
            if m then out.methods[#out.methods+1]={type=pair[1],signature=pair[2],address=tostring(sdk.to_int64(m:get_function())),parameterNames=m:get_param_names()}end
        end
        local enum=sdk.find_type_definition("app.PlayerDef.LOAD_CONTENTS")
        for _,name in ipairs({"WEAPON_MAIN","WEAPON_SUB","DUMMY_WEAPON_MAIN","DUMMY_WEAPON_SUB","WEAPON_EXTERNAL","RESERVE_WEAPON_MAIN","RESERVE_WEAPON_SUB","RESERVE_DUMMY_WEAPON_MAIN","RESERVE_DUMMY_WEAPON_SUB"})do
            local f=assert(enum:get_field(name));out.loadContents[name]=f:get_data(nil)
        end
        local pm=sdk.get_managed_singleton("app.PlayerManager")
        local master=pm and pm:call("getMasterPlayer")
        local character=master and master:call("get_Character")
        if not character then return out end
        out.actorReady=true;out.actorIdentity=tostring(character:get_address());out.weaponType=character:call("get_WeaponType");out.weaponId=character:call("get_WeaponID")
        out.weaponModelId=character:call("get_WeaponModelID")
        local weapon=character:call("get_Weapon")
        if weapon then local go=weapon:call("get_GameObject");out.weaponIdentity=tostring(weapon:get_address());out.weaponRootName=go:call("get_Name")end
        local list=pm:get_field("_PlayerList")
        local count=list:get_size();assert(count>=1 and count<=256,"Unexpected player-list size")
        local control
        for i=0,count-1 do
            local item=list:get_element(i)
            if item then local info=item:get_field("_PlayerInfo");local ch=info and info:call("get_Character")
                if ch and ch:get_address()==character:get_address() then control=item;out.controlIndex=i;break end
            end
        end
        assert(control,"Local player's loader owner not found")
        local loader=assert(control:get_field("_HunterLoader"))
        out.controlIdentity=tostring(control:get_address());out.loaderIdentity=tostring(loader:get_address())
        out.loading=loader:call("get_NowLoading")
        local prefabs=loader:get_field("_LoadPrefabs")
        count=prefabs:get_size();assert(count>0 and count<=256,"Unexpected prefab-array size")
        for i=0,count-1 do local p=prefabs:get_element(i)
            if p then out.prefabs[#out.prefabs+1]={index=i,identity=tostring(p:get_address()),path=p:call("get_Path"),valid=p:call("get_Valid"),ready=p:call("get_Ready")}end
        end
        for name,index in pairs(out.loadContents)do
            local p=loader:call("getLoadedPrefab(app.PlayerDef.LOAD_CONTENTS)",index)
            out.loadedByRole[name]={index=index,identity=p and tostring(p:get_address()),path=p and p:call("get_Path"),empty=not p or p:call("get_IsEmpty")}
        end
        return out
    end)
    result.ok=ok;if ok then result.result=value else result.error=tostring(value)end
    json.dump_file("MotionVfxLive/equipment-prefabs-probe.json",result)
end)
