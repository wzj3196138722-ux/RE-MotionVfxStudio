-- One-shot read-only inspection of retained action preview backups.
-- Does not replace closure cells, SDK fields, resources, or the editor state.
local function up(fn,wanted)
    for i=1,128 do local n,v=debug.getupvalue(fn,i);if not n then break end;if n==wanted then return v end end
    error("Unsupported closure layout: "..wanted)
end
local result={version=2,started=os.time(),rows={}}
local finished=false
re.on_pre_application_entry("LockScene",function()
    if finished then return end;finished=true
    local ok,err=pcall(function()
        local core=assert(_G.MotionVfxLive)
        local live=up(core.action_preview_status,"action_preview")
        local runtime=up(live.restore,"runtime")
        local engine=up(runtime.tick,"adapter")
        result.status=runtime:snapshot()
        local function read(d,key,fn)
            local good,value=pcall(fn)
            if good then d[key]=value else d[key.."Error"]=tostring(value)end
            return good,value
        end
        local function describe(o,kind)
            if not o then return {empty=true}end
            local d={luaType=type(o)}
            read(d,"address",function()return tostring(o:get_address())end)
            read(d,"path",function()return o:call(kind=="pfb" and "get_Path" or "get_ResourcePath")end)
            read(d,"type",function()local td=o:get_type_definition();return td and td:get_full_name()end)
            return d
        end
        for _,key in ipairs(runtime.order)do
            local original=assert(runtime.originals[key]);local installed=runtime.current[key]
            local target=original.target
            local row={key=key,kind=target.kind,originalPath=target.originalPath,proof=target.proof,provider=target.provider}
            result.rows[#result.rows+1]=row
            row.original=describe(original.value,target.kind)
            row.expected=describe(installed and installed.value,target.kind)
            local good,actual,owner=pcall(engine.read,target)
            if good then
                row.actual=describe(actual,target.kind)
                if target.kind=="pfb" then
                    read(row,"ownerPath",function()return owner:get_field("DataContainerPath")end)
                    read(row,"requestDelete",function()return owner:get_field("RequestDeleteDataContainer")end)
                    read(row,"pending",function()return describe(owner:get_field("RequestDataContainer"),"pfb")end)
                else
                    read(row,"target",function()return describe(owner:call("get_TargetMotionList"),target.kind)end)
                    read(row,"motionValid",function()return owner:call("get_MotionListValid")end)
                end
                for label,other in pairs({original=original.value,expected=installed and installed.value})do
                    local typename=target.kind=="pfb" and "via.Prefab" or "via.ResourceHolder"
                    read(row,label.."Equals",function()return sdk.call_native_func(nil,sdk.find_type_definition(typename),"equals("..typename..", "..typename..")",actual,other)end)
                end
            else row.readError=tostring(actual)end
        end
    end)
    if not ok then result.error=tostring(err)end
    result.completedAt=os.time()
    json.dump_file("MotionVfxLive/action-restore-inspection.json",result)
end)
