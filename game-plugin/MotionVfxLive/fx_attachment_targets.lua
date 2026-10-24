-- Read-only live attachment discovery (only a detached scratch element is
-- written for a getter probe). RelationChildId belongs to a live
-- ReLibEPVStandardChild component; it is NOT a weapon-type/container index.
local M={version="1.0.0"}
local function call(o,m,...)if o then local ok,v=pcall(o.call,o,m,...);if ok then return v end end end
local function field(o,k)if o then local ok,v=pcall(o.get_field,o,k);if ok then return v end end end
local function identity(o)if o then local ok,v=pcall(o.get_address,o);if ok then return tostring(v)end end end
local function component(go,name)
    local ok,t=pcall(sdk.typeof,name);if ok and t then return call(go,"getComponent(System.Type)",t)end
end
local function sequence(value,limit)
    if not value then return {} end
    local out={}
    if type(value)=="table" and not value.call and not value.get_elements then
        local keys={};for k in pairs(value)do if type(k)=="number" then keys[#keys+1]=k end end;table.sort(keys)
        assert(#keys<=limit,"挂点集合数量异常")
        for _,k in ipairs(keys)do out[#out+1]=value[k]end;return out
    end
    local ok,items=pcall(function()return value:get_elements()end)
    if ok and type(items)=="table" then return sequence(items,limit)end
    local n=tonumber(call(value,"get_Count"));assert(n and n>=0 and n<=limit,"无法完整读取挂点集合")
    for i=0,n-1 do out[#out+1]=assert(call(value,"get_Item",i),"挂点集合在读取期间发生变化")end
    return out
end
local function joints(go)
    local transform=assert(call(go,"get_Transform"),"挂点对象没有 Transform")
    local names,seen={},{}
    for _,joint in ipairs(sequence(assert(call(transform,"get_Joints"),"骨骼尚未就绪"),4096))do
        local name=call(joint,"get_Name")
        if type(name)=="string" and name~="" and not seen[name] then names[#names+1]=name;seen[name]=true end
    end
    table.sort(names);return names
end
local function enum_values()
    local t=sdk.find_type_definition("via.effect.script.EPVDataBase.EffectRelationType")
    local out={}
    if t then for _,name in ipairs({"World","WorldAng","FollowParent","InitializationParent","FollowCamera","FollowCameraPos","CameraNodeBillboard"})do
        local f=t:get_field(name);if f then local ok,v=pcall(f.get_data,f,nil);if ok then out[name]=tonumber(v)end end
    end end
    return out
end
function M.collect(character,manager)
    local result={targets={},relationTypes=enum_values(),warnings={}}
    local go=assert(call(character,"get_GameObject"),"角色对象尚未就绪")
    local current=call(manager,"getCurrentGameObject")
    assert(identity(current)==identity(go),"特效管理器当前目标不是角色，未猜测挂点")
    local transform=assert(call(go,"get_Transform"),"角色骨骼尚未就绪")
    local function add(target,kind,relation,source)
        local ok,names=pcall(joints,target)
        if not ok then result.warnings[#result.warnings+1]=tostring(names);return end
        result.targets[#result.targets+1]={key=kind.."|"..relation.."|"..identity(target),kind=kind,
            label=(kind=="character" and "人物" or "武器").." · "..tostring(call(target,"get_Name") or relation),
            relationChildId=relation,parentDegree=0,gameObjectIdentity=identity(target),joints=names,
            relationType=result.relationTypes.FollowParent,initializationRelationType=result.relationTypes.InitializationParent,
            proof="live_relation_child",relationComponentIdentity=source and identity(source) or nil}
    end
    add(go,"character",-1)
    local standard=field(manager,"StandardProvider")
    if not standard then result.warnings[#result.warnings+1]="角色特效挂点解析器尚未就绪";return result end
    local scratch=assert(sdk.create_instance("via.effect.script.EPVStandardData.Element"),"无法创建挂点校验对象")
    scratch:add_ref() -- strong local reference; managed userdata GC releases it
    scratch:set_field("ParentDegree",0)
    if result.relationTypes.FollowParent~=nil then scratch:set_field("RelationType",result.relationTypes.FollowParent)end
    local seen,count={},0
    local function visit(target)
        local key=identity(target);if not key or seen[key]then return end;seen[key]=true;count=count+1
        assert(count<=256,"当前武器子对象数量异常")
        local child=component(target,"via.relib.effect.behavior.ReLibEPVStandardChild")
        local relation=tonumber(field(child,"RelationChildId"))
        if relation and relation>=0 and relation%1==0 then
            -- Verify through the same resolver EPVStandard uses. A duplicated
            -- ID which resolves multiple objects cannot identify one UI target.
            local ok,matched=pcall(function()return sequence(assert(call(standard,"getChildrenTransform(via.Transform, System.Int32)",transform,relation),"挂点解析失败"),256)end)
            scratch:set_field("RelationChildId",relation)
            local follow=call(standard,"getFollowParent(via.effect.script.EPVStandardData.Element, via.GameObject)",scratch,go)
            if ok and #matched==1 and identity(call(matched[1],"get_GameObject"))==key and identity(call(follow,"get_GameObject"))==key then add(target,"weapon",relation,child)
            else result.warnings[#result.warnings+1]="未唯一核实武器挂点 "..tostring(relation).."："..tostring(call(target,"get_Name"))end
        end
        local node=call(call(target,"get_Transform"),"get_Child");local siblings={}
        while node do
            local nid=identity(node);if not nid or siblings[nid] then break end;siblings[nid]=true
            visit(call(node,"get_GameObject"));node=call(node,"get_Next")
        end
    end
    -- The active primary/secondary weapons are authoritative. Reserve and
    -- preview weapons must never be inferred from similar GameObject names.
    for _,getter in ipairs({"get_Weapon","get_SubWeapon"})do
        local weapon=call(character,getter);visit(call(weapon,"get_GameObject"))
    end
    return result
end
return M
