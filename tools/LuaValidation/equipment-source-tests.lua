local root=assert(arg[1]);local saved=arg;arg={root,"fixture"}
local fixture=assert(loadfile(root.."/../../tools/LuaValidation/action-preview-runtime-tests.lua"))();arg=saved
local passed=0
local function check(v,s)assert(v,s);passed=passed+1;print("PASS "..s)end
local function setup()
    local c=fixture();local old=c.character.call
    c.loader=c.make("loader");c.control=c.make("control");c.weapon_object=c.make("weapon");c.weapon_go=c.make("weapon_go")
    c.paths={"GameDesign/Equip/Weapon/main.pfb","GameDesign/Equip/Weapon/sub.pfb",nil,nil,"GameDesign/Equip/Armor/helm.pfb"}
    c.enums={WEAPON_MAIN=0,WEAPON_SUB=1,DUMMY_WEAPON_MAIN=2,DUMMY_WEAPON_SUB=3,ARMOR_HELM=4,ARMOR_BODY=5,ARMOR_ARM=6,ARMOR_WAIST=7,ARMOR_LEG=8,ARMOR_SLINGER=9}
    c.prefabs={};for i,p in pairs(c.paths)do c.prefabs[i]=c.make("pfb",p)end
    c.array={get_size=function()return 10 end,get_element=function(_,i)return c.prefabs[i+1]end}
    c.loader.fields._LoadPrefabs=c.array;c.control.fields._HunterLoader=c.loader;c.control.fields._PlayerInfo=c.player;c.control.fields._CreateOption=c.make("options")
    c.pm.fields._PlayerList={get_size=function()return 1 end,get_element=function()return c.control end}
    function c.character:call(m,...)
        if m=="get_WeaponModelID"then return 25 elseif m=="isWeaponBuildEnd" or m=="isPartsBuildEnd"then return not c.building
        elseif m=="getParts(app.ArmorDef.ARMOR_PARTS)"then return c.weapon_go
        elseif m=="get_Weapon" or m=="get_SubWeapon"then return c.weapon_object end
        return old(self,m,...)
    end
    function c.weapon_object:call(m,...)
        if m=="get_GameObject"then return c.weapon_go end;return old(self,m,...)
    end
    function c.weapon_go:call(m,...)
        if m=="get_Name"then return "weapon_root" end;return old(self,m,...)
    end
    function c.loader:call(m,...)
        if m=="get_NowLoading"then return c.loading==true
        elseif m=="getLoadedPrefab(app.PlayerDef.LOAD_CONTENTS)"then local a={...};return c.prefabs[a[1]+1]end
        return old(self,m,...)
    end
    c.env.sdk.find_type_definition=function(t)
        assert(t=="app.PlayerDef.LOAD_CONTENTS" or t=="app.ArmorDef.ARMOR_PARTS")
        local values=t=="app.PlayerDef.LOAD_CONTENTS" and c.enums or {HELM=0,BODY=1,ARM=2,WAIST=3,LEG=4,SLINGER=5}
        return {get_field=function(_,name)return {get_data=function()return values[name]end}end}
    end
    c.module=assert(loadfile(root.."/fx_equipment_sources.lua","t",c.env))()
    c.host={capture=function()return {actor={characterIdentity=tostring(c.character.address),sceneEpoch=1}}end,aliases=function()return c.aliases or {}end}
    function c:resolve(p)return self.module.resolve(self.host,p)end
    return c
end
if arg[2]=="fixture"then return setup end
do
    local c=setup();local proof,context=c:resolve(c.paths[1])
    check(proof.role=="WEAPON_MAIN" and proof.category=="weapon" and proof.loadIndex==0,"Current main weapon is proven through its own player's exact loader entry")
    check(proof.weaponId==89 and proof.weaponModelId==25,"Equipment and model IDs remain distinct")
    check(#context.weaponPrefabs==4 and context.weaponPrefabs[2].value==c.prefabs[2] and context.weaponPrefabs[3].value==nil,"Related main/sub/dummy references preserve empty slots without guessing")
    check(proof.gameObjectIdentity==tostring(c.weapon_go.address) and proof.weaponIdentity==tostring(c.weapon_object.address),"Weapon component and GameObject identities are not confused")
    proof=c:resolve(c.paths[5]);check(proof.category=="armor" and proof.role=="ARMOR_HELM","Armor PFB is distinguished by native loader role, not its basename")
    check(#c.writes==0,"Provenance discovery performs no live-resource writes")
end
do
    local c=setup();local ok=pcall(c.resolve,c,"GameDesign/Other/main.pfb")
    check(not ok,"Same basename from another path cannot bind equipment")
    c.loading=true;ok=pcall(c.resolve,c,c.paths[1]);check(not ok,"Loading equipment cannot be bound as a stable target")
    c.loading=false;c.building=true;ok=pcall(c.resolve,c,c.paths[1]);check(not ok,"Building weapon cannot be bound prematurely")
end
do
    local c=setup();c.aliases={['art/motionvfxlive/prefabpreview/test/resource.pfb']=c.paths[1]}
    c.prefabs[1].path="art/MotionVfxLive/PrefabPreview/test/resource.pfb"
    local proof=c:resolve(c.paths[1]);check(proof.role=="WEAPON_MAIN" and proof.observedPath==c.prefabs[1].path,"Owned preview aliases preserve original full-path provenance")
end
print("Equipment source results: "..passed.." passed")
