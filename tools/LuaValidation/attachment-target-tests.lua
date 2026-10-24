local root=assert(arg[1]);local count=0
local function check(v,m)assert(v,m);count=count+1;print("PASS "..m)end
local function fixture()
    local serial=0;local c={}
    local function obj(methods,fields)
        serial=serial+1;local o={address=serial,methods=methods or {},fields=fields or {}}
        function o:get_address()return self.address end
        function o:get_field(k)return self.fields[k]end
        function o:set_field(k,v)self.fields[k]=v end
        function o:add_ref()end
        function o:call(k,...)
            local v=self.methods[k];if type(v)=="function"then return v(...)end
            if v~=nil then return v end
            error("Unsupported call "..k)
        end
        return o
    end
    local function go(name,names,relation)
        local child=relation and obj({},{RelationChildId=relation})or nil
        local joints={};for i,n in ipairs(names)do joints[i]=obj({get_Name=n})end
        local t=obj({get_Joints=joints});local g=obj({get_Name=name,get_Transform=t,
            ["getComponent(System.Type)"]=function(typ)if typ=="via.relib.effect.behavior.ReLibEPVStandardChild" then return child end end})
        t.methods.get_GameObject=g;return g,t,child
    end
    c.character,c.ct=go("player",{"root","L_Foot","R_Foot","root"})
    c.weapon,c.wt,c.child=go("unpredictable_weapon_name",{"base","blade"},8734)
    c.sub,c.st,c.subchild=go("secondary",{"base","shell"},290)
    c.reserve,c.rt=go("same_name_but_reserve",{"reserve"},111)
    c.ch=obj({get_GameObject=c.character,get_Weapon=obj({get_GameObject=c.weapon}),get_SubWeapon=obj({get_GameObject=c.sub}),get_ReserveWeapon=obj({get_GameObject=c.reserve})})
    c.map={[8734]={c.wt},[290]={c.st}}
    c.standard=obj({["getChildrenTransform(via.Transform, System.Int32)"]=function(tr,id)assert(tr==c.ct);return c.map[id]end,
        ["getFollowParent(via.effect.script.EPVStandardData.Element, via.GameObject)"]=function(e,owner)
            assert(owner==c.character and e.fields.ParentDegree==0);local matches=c.map[e.fields.RelationChildId];return not c.bad_parent and matches and matches[1]
        end})
    c.manager=obj({getCurrentGameObject=c.character},{StandardProvider=c.standard})
    local env=setmetatable({sdk={typeof=function(t)return t end,create_instance=function()return obj()end,find_type_definition=function()return {get_field=function(_,k)
        local values={FollowParent=97,InitializationParent=13,World=40};return values[k]and{get_data=function()return values[k]end}or nil
    end}end}},{__index=_G})
    c.module=assert(loadfile(root.."/fx_attachment_targets.lua","t",env))();return c
end
do
    local c=fixture();local r=c.module.collect(c.ch,c.manager)
    check(#r.targets==3,"Only current actor and active primary/secondary weapons are listed")
    check(r.targets[1].kind=="character" and r.targets[1].relationChildId==-1,"Actor sentinel -1 is kept distinct from weapon child IDs")
    check(r.targets[2].relationChildId==8734 and r.targets[3].relationChildId==290,"Weapon IDs come from live components without arithmetic or name guessing")
    check(#r.targets[1].joints==3 and r.targets[1].joints[1]=="L_Foot","Joints are read per actual target, sorted and deduplicated")
    check(r.relationTypes.FollowParent==97 and r.relationTypes.InitializationParent==13,"Relation types use engine field values, not guessed ordinals")
end
do local c=fixture();c.map[8734]={c.rt};local r=c.module.collect(c.ch,c.manager);check(#r.targets==2 and #r.warnings==1,"Mismatched child resolution excludes an unrelated target")end
do local c=fixture();c.map[8734]={c.wt,c.rt};local r=c.module.collect(c.ch,c.manager);check(#r.targets==2,"Duplicate child IDs are not presented as a unique weapon target")end
do local c=fixture();c.bad_parent=true;local r=c.module.collect(c.ch,c.manager);check(#r.targets==1,"The full native parent/child resolver must agree with the selected target")end
do local c=fixture();c.child.fields.RelationChildId=-1;local r=c.module.collect(c.ch,c.manager);check(#r.targets==2,"A weapon with uninitialized child ID is not given actor ownership")end
do local c=fixture();c.manager.methods.getCurrentGameObject=c.weapon;check(not pcall(c.module.collect,c.ch,c.manager),"Unexpected manager follow owner fails closed")end
do local c=fixture();c.wt.methods.get_Joints=false;local r=c.module.collect(c.ch,c.manager);check(#r.targets==2 and #r.warnings==1,"Unavailable skeleton does not produce invented joint choices")end
do local c=fixture();c.wt.methods.get_Child=c.st;c.st.methods.get_Next=c.st;local r=c.module.collect(c.ch,c.manager);check(#r.targets==3,"Repeated child hierarchy references terminate and deduplicate")end
print("RESULT attachment target tests: "..count.." passed; mocked engine")
