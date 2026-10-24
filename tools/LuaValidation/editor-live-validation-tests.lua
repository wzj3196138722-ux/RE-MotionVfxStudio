local script=assert(arg[1]);local count=0
local function check(v,m)assert(v,m);count=count+1;print("PASS "..m)end
local function execute(overlay,ui_failure,pfb_failure,no_previous)
    local frames,time,callbacks,output=0,0,{},nil
    local original=function(_,_,value)return false,value end
    local state={window_open=false,window_hidden=true,capture_snapshot={recording=true},bindings_snapshot={appliedCount=0}}
    local w={page="effects",action_source="live",window_size={w=1200,h=780},window_position={x=.3,y=.2},local_path="original",scale=1,
        widgets={textbox=original,commands={},pos=function()return 30,40 end}}
    function w:hide()state.window_hidden=true;self.replay_speed=0 end
    function w:show()state.window_hidden=false;state.window_open=true end
    local previous={path="old.pfb.18",sourceKind="local_file",type="effect",revision=3,dirty=true,slots={{key="old",fields={}}}}
    w.pfb={document=not no_previous and previous or nil,selected="old",filter="preserved",values={draft="unsaved"}}
    function w.pfb:open(path,kind,callback)
        if pfb_failure and path:match("GameDesign")then callback({ok=false,message="deliberate PFB failure"});return end
        self.document=path==previous.path and previous or {path=path,sourceKind=kind,type=path:match("GameDesign")and "equipment"or "effect",
            revision=0,dirty=false,slots={{key="one",fields={{editable=true},{editable=true}}}}}
        self.values={};self.selected=nil;callback({ok=true,pfbDocument=self.document})
    end
    local core={active=true,version="0.8.0",state=state,workbench=w,action_preview_status=function()return{phase="idle",appliedCount=0}end,
        pfb_preview_status=function()return{phase="idle",appliedCount=0}end}
    local function no_functions(v)if type(v)=="function"then error("report contains callback")end;if type(v)=="table"then for _,x in pairs(v)do no_functions(x)end end end
    local env=setmetatable({_G={},os={time=function()return math.floor(time)end},
        re={on_frame=function(fn)callbacks[#callbacks+1]=fn end},reframework={is_drawing_ui=function()return overlay end},
        imgui={is_item_active=function()return false end},json={dump_file=function(_,v)no_functions(v);output=v end,load_file=function()return{ok=true,result={targets={}}}end}}, {__index=_G})
    env._G.MotionVfxLive=core;assert(loadfile(script,"t",env))()
    while frames<250 and env._G.MotionVfxEditorValidation080.running do
        frames=frames+1;time=time+.1
        if overlay then
            state.window_drawn=not state.window_hidden;w.orb_status=state.window_hidden and {side="right"}or nil
            w.widgets.commands={{kind="text"}}
            w.widgets:textbox("##test",w.local_path,400)
        else state.window_drawn=false;w.orb_status=nil end
        if ui_failure and frames==14 then state.ui_error="deliberate render error"end
        for _,fn in ipairs(callbacks)do fn()end
    end
    check(w.page==(no_previous and "pfb"or "effects")and w.top==nil and w.local_path=="original"and w.cache_alias==nil,"Original presentation/draft values restore")
    check(w.widgets.textbox==original and state.window_hidden==not no_previous and state.window_open==not not no_previous,"Instrumentation and window visibility restore")
    if not no_previous then check(w.pfb.document==previous and w.pfb.selected=="old"and w.pfb.filter=="preserved"and w.pfb.values.draft=="unsaved","Previous backend document and uncommitted PFB UI drafts restore")end
    return output
end
local ok=execute(true,false);check(ok.passed==true and #ok.steps==18 and ok.renderedSteps==18,"All 18 visible UI stages report completion")
check(ok.resourceStateUnchanged==true and ok.attachmentProbe.ok==true,"Preview/recording state remains unchanged; attachment diagnostics included")
check(ok.steps[17].document.type=="equipment"and ok.steps[18].document.type=="effect"and ok.previousPfbRestored==true,"Real document callbacks select slots before rendering, then reopen previous document")
local hidden=execute(false,false);check(hidden.passed==false and hidden.renderedSteps==0,"Hidden overlay cannot be reported as a rendering pass")
local failed=execute(true,true);check(failed.passed==false and failed.reason=="ui_error","A render error terminates and restores presentation")
local pfb_failed=execute(true,false,true);check(pfb_failed.passed==false and pfb_failed.reason=="pfb_content_failed"and pfb_failed.previousPfbRestored==true,"PFB open failure restores the prior backend document")
local no_old=execute(true,false,false,true);check(no_old.passed==true and no_old.leavesEffectPfbOpen==true and no_old.finalPfbDocument.type=="effect","Without a previous document the effect page remains ready for inspection")
print("RESULT live UI exercise harness: "..count.." checks")
