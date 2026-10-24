-- Temporary local validation helper. Installed only for this test session and
-- removed afterward; never shipped with the editor or distribution package.
local active=true
local last
local previous=json.load_file("MotionVfxLive/validation-response.json")
if type(previous)=="table" then last=previous.id end
local allowed={attachments="Probe-Attachments.lua",equipment_probe="Probe-EquipmentPrefabs.lua",reload="Reload.lua",exercise="Validate-Editor-080.lua"}
local poll=0
re.on_frame(function()
    if not active or os.time()==poll then return end;poll=os.time()
    local request=json.load_file("MotionVfxLive/validation-request.json")
    if type(request)~="table" or type(request.id)~="string" or request.id==last then return end
    last=request.id
    local ok,result=pcall(function()
        assert(type(request.expires)=="number" and request.expires>=os.time(),"Validation request expired")
        if request.action=="disable" then active=false;return "disabled" end
        if request.action=="restore" then
            assert(_G.MotionVfxLive and _G.MotionVfxLive.active,"Editor unavailable")
            _G.MotionVfxLive.stop_and_restore();return "restore requested"
        end
        local script=assert(allowed[request.action],"Unknown validation command")
        local loader=assert(loadfile("reframework/data/MotionVfxLive/"..script))
        loader();return "script started"
    end)
    json.dump_file("MotionVfxLive/validation-response.json",{id=last,ok=ok,message=tostring(result),time=os.time()})
end)
