local script=assert(arg[1]);local checks=0
local function check(v,m)assert(v,m);checks=checks+1;print("PASS "..m)end
local function execute(bad_path,timeout)
    local callback,output,now,created=nil,nil,0,0
    local manifest={privateRoot="art/MotionVfxLive/Validation/Pfb080/test",caseCount=7,instantiationAllowed=false,cases={}}
    for _,name in ipairs({"effect_original","effect_copy","equipment_original","equipment_add_effectpfb","equipment_add_chain","equipment_add_clsp","equipment_add_jcns"})do
        manifest.cases[#manifest.cases+1]={name=name,path=manifest.privateRoot.."/"..name..".pfb",sha256="test"}
    end
    if bad_path then manifest.cases[1].path="art/unrelated.pfb"end
    local env=setmetatable({_G={},os={time=function()return now end},
        json={load_file=function(path)assert(path=="MotionVfxLive/pfb-load-manifest.json");return manifest end,dump_file=function(_,v)output=v end},
        re={on_pre_application_entry=function(name,fn)assert(name=="LockScene");callback=fn end},
        sdk={create_instance=function(typ)
            assert(typ=="via.Prefab");created=created+1;local number=created
            return {add_ref=function()end,deserialize_native=function(self,bytes)
                local data=string.char(table.unpack(bytes));local flag,size=string.unpack("<I4I4",data)
                assert(flag==1 and #data==8+size*2 and data:sub(-2)=="\0\0")
                local chars={};for i=9,#data-2,2 do local code=string.unpack("<I2",data,i);chars[#chars+1]=string.char(code)end;self.path=table.concat(chars)
            end,call=function(self,name)
                if name=="get_Path"then return self.path:lower()end
                assert(name=="get_Ready"or name=="get_Valid","Mutating/instantiating engine API was invoked")
                return not(timeout and number==3)
            end}
        end}}, {__index=_G})
    assert(loadfile(script,"t",env))()
    for i=1,500 do now=now+1;callback();if not output.running then break end end
    return output,created
end
local report,created=execute(false,false)
if not report.passed then print(report.reason,created,table.concat(report.errors," | "))end
check(report.passed and #report.cases==7 and created==7,"All seven detached Prefab resources are loaded and validated")
check(report.instantiated==false and report.actorWrites==false and report.editorStateUnchanged,"No instance, live owner mutation or editor takeover occurs")
for _,row in ipairs(report.cases)do assert(row.ready and row.valid and row.passed)end
check(report.reason=="complete","Every prefabricated case records Ready/Valid success")
local bad,count=execute(true,false);check(not bad.passed and count==0 and bad.reason=="exception","Non-private manifest target is rejected before resource loading")
local timed=execute(false,true);check(not timed.passed and #timed.cases==7 and timed.cases[3].passed==false and timed.cases[4].passed,"A timed-out case fails while preserving later control results")
print("RESULT PFB detached load probe: "..checks.." checks")
