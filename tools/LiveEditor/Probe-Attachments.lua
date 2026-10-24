-- One-shot proof. No live-object writes or resource loads. Only a detached
-- temporary element is configured for the engine's parent lookup getter.
local pending=true
re.on_pre_application_entry("LockScene",function()
    if not pending then return end;pending=false
    local result={observedAt=os.time()}
    local ok,value=pcall(function()
        local module=require("MotionVfxLive.fx_attachment_targets")
        local character=sdk.get_managed_singleton("app.PlayerManager"):call("getMasterPlayer"):call("get_Character")
        local manager=character:call("get_GameObject"):call("getComponent(System.Type)",sdk.typeof("via.effect.script.ObjectEffectManager2"))
        local info=module.collect(character,manager)
        info.weaponType=character:call("get_WeaponType");info.weaponId=character:call("get_WeaponID")
        info.followProbe={}
        local provider=manager:get_field("StandardProvider")
        local scratch=sdk.create_instance("via.effect.script.EPVStandardData.Element");scratch:add_ref()
        scratch:set_field("ParentDegree",0)
        for _,getter in ipairs({"get_Weapon","get_SubWeapon"})do
            local weapon=character:call(getter)
            if weapon then
                local go=weapon:call("get_GameObject")
                local child=go:call("getComponent(System.Type)",sdk.typeof("via.relib.effect.behavior.ReLibEPVStandardChild"))
                if child then
                    local relation=child:get_field("RelationChildId");scratch:set_field("RelationChildId",relation)
                    for _,name in ipairs({"FollowParent","InitializationParent"})do
                        local mode=info.relationTypes[name]
                        if mode~=nil then
                            scratch:set_field("RelationType",mode)
                            local tf=provider:call("getFollowParent(via.effect.script.EPVStandardData.Element, via.GameObject)",scratch,character:call("get_GameObject"))
                            local actual=tf and tf:call("get_GameObject")
                            info.followProbe[#info.followProbe+1]={getter=getter,relationChildId=relation,parentDegree=0,mode=name,
                                expected=tostring(go:get_address()),actual=actual and tostring(actual:get_address()),
                                same=actual and actual:get_address()==go:get_address()or false}
                        end
                    end
                end
            end
        end
        return info
    end)
    result.ok=ok;if ok then result.result=value else result.error=tostring(value)end
    json.dump_file("MotionVfxLive/attachment-probe.json",result)
end)
