-- Explorer interaction regressions. These exercise successful row-release clicks,
-- rather than relying on ImGui's mouse-down-only double-click event.
local root=arg[1]or "."
local module=assert(loadfile(root.."/game-plugin/MotionVfxLive/fx_file_dialog.lua"))()
local passed=0
local function check(value,why)
    assert(value,why);passed=passed+1;print("PASS "..why)
end
local function equal_paths(actual,expected)
    if #actual~=#expected then return false end
    for i,path in ipairs(expected)do if actual[i]~=path then return false end end
    return true
end
local function listing(path,names)
    local entries={}
    for _,value in ipairs(names or {"子目录/","一.efx.12","中间目录/","二.efx.12","三.efx.12"})do
        local dir=value:sub(-1)=="/"
        local name=dir and value:sub(1,-2)or value
        entries[#entries+1]={name=name,path=path.."\\"..name,isDirectory=dir,size=dir and 0 or 256}
    end
    return {path=path,parentPath=path:match("^(.*)\\[^\\]+$"),roots={{name="D:",path="D:\\"}},shortcuts={},entries=entries,total=#entries,offset=0,limit=100}
end
local function setup(options)
    local calls,results={},{}
    local core={state={}}
    local u={scale=1,real={},colors={}}
    local held,pressed={},{}
    u.real.ImGuiKey={}
    for i,name in ipairs({"Enter","KeypadEnter","Escape","Backspace","LeftArrow","RightArrow","UpArrow","DownArrow","Home","End","F5","A","Space","LeftCtrl","RightCtrl","LeftShift","RightShift","LeftAlt","RightAlt"})do u.real.ImGuiKey["Key_"..name]=i end
    function u.real.is_key_down(key)return held[key]==true end
    function u.real.is_key_pressed(key)return pressed[key]==true end
    local browser=module.new(core,u,function(action,data,callback)
        core.state.pending={action=action}
        calls[#calls+1]={action=action,data=data,callback=callback}
        return true
    end)
    local c={b=browser,core=core,calls=calls,results=results}
    function c:reply(value,index)
        local call=assert(self.calls[index or #self.calls],"expected a backend request")
        self.core.state.pending=nil;call.callback(value)
    end
    function c:respond_directory(path,names)
        self:reply({ok=true,fileBrowser=listing(path,names)})
    end
    function c:release(index,now,mods,position)
        self.b:click_entry(assert(self.b.entries[index]),index,mods or {},now,position or {x=120,y=180})
    end
    function c:press(name,mods)
        local key=u.real.ImGuiKey["Key_"..name];held[key]=true;pressed[key]=true
        for modifier,down in pairs(mods or {})do if down then held[assert(u.real.ImGuiKey["Key_Left"..modifier])]=true end end
        self.b:handle_keys();held={};pressed={};self.b:handle_keys()
    end
    function c:navigate(path,names)
        self.b:browse(path);self.b:tick()
        check(self.calls[#self.calls].data.path==path,"navigation request uses selected target "..path)
        self:respond_directory(path,names)
    end
    options=options or {};options.path=options.path or "D:\\素材"
    options.on_select=function(value)results[#results+1]=value end
    browser:open(options);browser:tick();c:respond_directory(options.path)
    return c
end

-- A click is selection; the second release must activate even without a native
-- is_mouse_double_clicked implementation (the real widget reports on release).
local c=setup()
c:release(1,10)
check(c.b.active and c.b.focus.isDirectory and not c.b.queued,"single folder click selects without navigating")
c:release(1,10.2)
check(c.b.queued and c.b.queued.data.path=="D:\\素材\\子目录","second release enters folder without an extra button")
c.b:tick();c:respond_directory("D:\\素材\\子目录")
check(c.b.path=="D:\\素材\\子目录","double-click navigation accepts returned child directory")
c=setup();c:release(2,20);c:release(2,20.3)
check(not c.b.active and #c.results==1 and c.results[1][1]=="D:\\素材\\一.efx.12","double-click file opens the selected file exactly once")
c=setup({multiple=true});c:release(4,30);c:release(4,30.3)
check(not c.b.active and #c.results==1 and c.results[1][1]=="D:\\素材\\二.efx.12","double-click file also confirms in a multi-file picker")
c=setup({save=true,name="新文件.efx.12"});c:release(2,35);c:release(2,35.2);c.b:tick()
check(c.b.active and #c.results==0 and c.calls[#c.calls].action=="file_stat"and c.calls[#c.calls].data.path=="D:\\素材\\一.efx.12","save-mode double-click selects the filename and still checks before overwriting")
c:reply({ok=true,fileStat={path="D:\\素材\\一.efx.12",exists=true,isDirectory=false,sha256="ABC"}})
check(c.b.active and #c.results==0 and c.b.confirm_overwrite.expectedHash=="ABC","save-mode double-click never bypasses overwrite confirmation")
c=setup();c:release(2,40);c:release(2,40.51)
check(c.b.active and #c.results==0,"clicks separated by more than half a second do not open a file")
c:release(2,40.75)
check(#c.results==1,"a slow second click becomes the start of a new double-click pair")
c=setup();c:release(2,50,nil,{x=100,y=100});c:release(2,50.5,nil,{x=106,y=100})
check(#c.results==1,"double-click accepts the documented time and distance boundary")
c=setup();c:release(2,60,nil,{x=100,y=100});c:release(2,60.2,nil,{x=106.1,y=100})
check(c.b.active and #c.results==0,"pointer movement beyond six pixels prevents activation")
c=setup();c:release(2,70);c:release(4,70.2)
check(c.b.active and #c.results==0 and c.b.selected_order[1]=="D:\\素材\\二.efx.12","rapid clicks on different rows only change the selection")
c=setup();c:release(2,80);c:release(2,79.9)
check(c.b.active and #c.results==0,"backward clock movement does not create a false double-click")
c=setup({multiple=true});c:release(2,90,{ctrl=true});c:release(2,90.2,{ctrl=true})
check(c.b.active and #c.results==0 and #c.b.selected_order==0,"Ctrl clicks toggle selection without opening")
c=setup({multiple=true});c:release(2,100,{shift=true});c:release(2,100.2,{shift=true})
check(c.b.active and #c.results==0,"Shift clicks never activate a file")
c=setup();c:release(2,110);c.b:cancel();c.b:open({path="D:\\素材",on_select=function(value)c.results[#c.results+1]=value end});c.b:tick();c:respond_directory("D:\\素材");c:release(2,110.2)
check(c.b.active and #c.results==0,"reopening a dialog resets its previous double-click candidate")

-- Explorer selection uses normal replace, Ctrl toggle, and Shift range.
c=setup({multiple=true})
c.b:select(c.b.entries[2],{},2);c.b:select(c.b.entries[4],{},4)
check(equal_paths(c.b.selected_order,{"D:\\素材\\二.efx.12"}),"ordinary file selection replaces the previous selection")
c.b:select(c.b.entries[2],{ctrl=true},2)
check(equal_paths(c.b.selected_order,{"D:\\素材\\二.efx.12","D:\\素材\\一.efx.12"}),"Ctrl adds a file while keeping other selected files")
c.b:select(c.b.entries[4],{ctrl=true},4)
check(equal_paths(c.b.selected_order,{"D:\\素材\\一.efx.12"}),"Ctrl deselects only the clicked file")
c.b:select(c.b.entries[2],{},2);c.b:select(c.b.entries[5],{shift=true},5)
check(equal_paths(c.b.selected_order,{"D:\\素材\\一.efx.12","D:\\素材\\二.efx.12","D:\\素材\\三.efx.12"}),"Shift selects the inclusive file range and skips directories")
c.b:select(c.b.entries[5],{},5);c.b:select(c.b.entries[2],{shift=true},2)
check(equal_paths(c.b.selected_order,{"D:\\素材\\一.efx.12","D:\\素材\\二.efx.12","D:\\素材\\三.efx.12"}),"Shift selects the same inclusive range in reverse direction")
c.b:select(c.b.entries[1],{},1)
check(#c.b.selected_order==0 and c.b.focus.isDirectory,"ordinary folder selection clears earlier file selections")
c=setup();c.b:select(c.b.entries[2],{},2);c.b:select(c.b.entries[4],{ctrl=true},4)
check(equal_paths(c.b.selected_order,{"D:\\素材\\二.efx.12"}),"single-file picker never retains multiple files under Ctrl")
c=setup({multiple=true});c.b:select(c.b.entries[2],{},2);c.b:browse("D:\\不可访问");c.b:tick();c:reply({ok=false,error="无权访问"})
check(c.b.path=="D:\\素材"and c.b.selected_order[1]=="D:\\素材\\一.efx.12","failed navigation retains the old folder and its selected file")
c:navigate("D:\\另一目录")
check(#c.b.selected_order==0 and next(c.b.selected)==nil and c.b.focus==nil,"successful directory change clears stale selection and focus")
c=setup({multiple=true});c.b:select(c.b.entries[2],{},2);c.b:browse(c.b.path,0);c.b:tick();c:respond_directory("D:\\素材")
check(c.b.selected_order[1]=="D:\\素材\\一.efx.12","same-directory refresh preserves a still-valid selected file")
c.b:browse(c.b.path,100);c.b:tick();local page=listing("D:\\素材",{"下一页.efx.12"});page.offset=100;page.total=101;c:reply({ok=true,fileBrowser=page})
check(c.b.selected_order[1]=="D:\\素材\\一.efx.12","pagination preserves selected files on previous pages")

-- Navigation history is committed only after the backend accepts a directory.
c=setup();c:navigate("D:\\B");c:navigate("D:\\C")
c.b:navigate_back();c.b:tick()
check(c.calls[#c.calls].data.path=="D:\\B","Back requests the previous successful directory")
c:respond_directory("D:\\B");c.b:navigate_back();c.b:tick()
check(c.calls[#c.calls].data.path=="D:\\素材","a second Back reaches the initial directory")
c:respond_directory("D:\\素材");local previous=#c.calls;c.b:navigate_back();c.b:tick()
check(#c.calls==previous,"Back at the beginning does not send another request")
c.b:navigate_forward();c.b:tick()
check(c.calls[#c.calls].data.path=="D:\\B","Forward revisits the next successful directory")
c:respond_directory("D:\\B");c:navigate("D:\\新分支");previous=#c.calls;c.b:navigate_forward();c.b:tick()
check(#c.calls==previous,"navigating elsewhere after Back discards the forward branch")
c.b:browse("D:\\不存在");c.b:tick();c:reply({ok=false,error="目录不存在"});c.b:navigate_back();c.b:tick()
check(c.calls[#c.calls].data.path=="D:\\B","failed navigation never adds a history entry")
c:respond_directory("D:\\B");c.b:navigate_back();c.b:tick();c:reply({ok=false,error="临时无法访问"});c.b:navigate_back();c.b:tick()
check(c.calls[#c.calls].data.path=="D:\\素材","failed Back does not advance the history cursor")
c:respond_directory("D:\\素材")
c=setup();c:navigate("D:\\父目录\\子目录");c.b:navigate_parent();c.b:tick()
check(c.calls[#c.calls].data.path=="D:\\父目录","parent navigation uses the backend's canonical parent path")
c:respond_directory("D:\\父目录");c.b:navigate_back();c.b:tick()
check(c.calls[#c.calls].data.path=="D:\\父目录\\子目录","parent navigation is included in Back history")
c:respond_directory("D:\\父目录\\子目录");c.b:browse(c.b.path);c.b:tick();c:respond_directory("D:\\父目录\\子目录");c.b:navigate_back();c.b:tick()
check(c.calls[#c.calls].data.path=="D:\\素材","refresh does not add duplicate history entries")
c=setup();c:release(1,200);c.b:browse("D:\\别处");c.b:tick();c:respond_directory("D:\\别处");c:release(1,200.2)
check(not c.b.queued,"navigating changes resets the row double-click candidate")

-- File-navigation keys must never steal text editing keys from an input box.
c=setup();c.b:select(c.b.entries[1],{},1);c:press("Enter")
check(c.b.queued and c.b.queued.data.path=="D:\\素材\\子目录","Enter opens the focused folder")
c=setup();c.b:select(c.b.entries[4],{},4);c:press("KeypadEnter")
check(not c.b.active and c.results[1][1]=="D:\\素材\\二.efx.12","keypad Enter opens the focused file")
c=setup();c:press("Escape")
check(not c.b.active,"Escape closes a picker when no text field owns the key")
c=setup();c.b.text_focus="path";c.b.path_input="D:\\输入的路径";c:press("Enter")
check(c.b.queued and c.b.queued.data.path=="D:\\输入的路径","Enter in the path field navigates to its text")
c=setup();c:navigate("D:\\其他目录");c.b.text_focus="path";local before=#c.calls;c:press("Backspace");c:press("LeftArrow",{Alt=true});c:press("Escape")
check(c.b.active and not c.b.queued and #c.calls==before,"text editing keeps Backspace, arrows and Escape from navigating or closing")
c=setup({multiple=true});c.b.text_focus="filter";c:press("A",{Ctrl=true})
check(#c.b.selected_order==0,"Ctrl+A in a text field does not select files")
c.b.filter="粒子";c:press("Enter")
check(c.b.queued and c.b.queued.data.filter=="粒子","Enter in filter field requests matching files")
c=setup({multiple=true});c:press("A",{Ctrl=true})
check(equal_paths(c.b.selected_order,{"D:\\素材\\一.efx.12","D:\\素材\\二.efx.12","D:\\素材\\三.efx.12"}),"Ctrl+A selects the visible files and skips folders")
c=setup();c:press("DownArrow");c:press("DownArrow")
check(c.b.focus.path=="D:\\素材\\一.efx.12","Down moves file-list focus and selection")
c:press("End");check(c.b.focus.path=="D:\\素材\\三.efx.12","End moves to the last displayed entry")
c:press("Home");check(c.b.focus.path=="D:\\素材\\子目录","Home moves to the first displayed entry")
c=setup({multiple=true});c.b:select(c.b.entries[2],{},2);c:press("DownArrow",{Shift=true});c:press("DownArrow",{Shift=true})
check(equal_paths(c.b.selected_order,{"D:\\素材\\一.efx.12","D:\\素材\\二.efx.12"}),"Shift+arrows extend the selection range through folders")
c=setup();c:press("F5")
check(c.b.queued and c.b.queued.data.path=="D:\\素材","F5 refreshes the current directory")
c=setup();c:navigate("D:\\B");c:press("LeftArrow",{Alt=true})
check(c.b.queued and c.b.queued.data.path=="D:\\素材","Alt+Left navigates backward")
c.b:tick();c:respond_directory("D:\\素材");c:press("RightArrow",{Alt=true})
check(c.b.queued and c.b.queued.data.path=="D:\\B","Alt+Right navigates forward")
c.b:tick();c:respond_directory("D:\\B");c:press("Backspace")
check(c.b.queued and c.b.queued.data.path=="D:\\素材","Backspace navigates backward outside text fields")
c=setup();c:navigate("D:\\父目录\\子目录");c:press("UpArrow",{Alt=true})
check(c.b.queued and c.b.queued.data.path=="D:\\父目录","Alt+Up navigates to the parent folder")
c=setup();c.b.keyboard_owned=false;c:press("Escape");c:press("DownArrow")
check(c.b.active and c.b.focus==nil,"dialog keys do not fire after keyboard focus leaves its bounds")

-- Ctrl+A includes files on every backend page, commits atomically, and leaves
-- the current visible page and navigation history alone.
local function collection_page(path,offset,count,total)
    local value=listing(path,{})
    value.offset=offset;value.limit=200;value.total=total
    for number=offset+1,offset+count do
        local directory=(number-1)%200==0
        local name=string.format(directory and "目录%04d"or "文件%04d.efx.12",number)
        value.entries[#value.entries+1]={name=name,path=path.."\\"..name,isDirectory=directory,size=directory and 0 or 512}
    end
    return value
end
local function paged_dialog()
    local value=setup({multiple=true})
    value.b:browse(value.b.path,100);value.b:tick()
    value:reply({ok=true,fileBrowser=collection_page("D:\\素材",100,100,405)})
    value.b:select(value.b.entries[2],{},2)
    return value
end
c=setup({multiple=true});before=#c.calls;c.b:select_all();c.b:tick()
check(#c.calls==before and #c.b.selected_order==3,"complete listing selects all files without another backend request")
c=paged_dialog();local current_entries=c.b.entries;local history_index=c.b.history_index;local history_size=#c.b.history;local original_path=c.b.selected_order[1]
c:press("A",{Ctrl=true});c.b:tick()
check(c.calls[#c.calls].action=="file_browse"and c.calls[#c.calls].data.offset==0 and c.calls[#c.calls].data.limit==200,"Ctrl+A on a paginated listing starts collection at the first page")
c:reply({ok=true,fileBrowser=collection_page("D:\\素材",0,200,405)})
check(equal_paths(c.b.selected_order,{original_path})and c.b.queued.data.offset==200,"first collection page keeps the prior selection until all pages succeed")
c.b:tick();c:reply({ok=true,fileBrowser=collection_page("D:\\素材",200,200,405)})
check(equal_paths(c.b.selected_order,{original_path})and c.b.queued.data.offset==400,"second collection page advances without publishing partial selection")
c.b:tick();c:reply({ok=true,fileBrowser=collection_page("D:\\素材",400,5,405)})
check(#c.b.selected_order==402 and c.b.selected["D:\\素材\\文件0002.efx.12"]and c.b.selected["D:\\素材\\文件0405.efx.12"]and not c.b.selected["D:\\素材\\目录0201"],"multi-page selection includes all 402 files across three pages and excludes directories")
check(c.b.entries==current_entries and c.b.offset==100 and c.b.total==405 and c.b.path=="D:\\素材"and c.b.history_index==history_index and #c.b.history==history_size,"select-all does not replace the visible page or navigation history")
c=paged_dialog();original_path=c.b.selected_order[1];c.b:select_all();c.b:tick();c:reply({ok=true,fileBrowser=collection_page("D:\\素材",0,200,405)});c.b:tick();c:reply({ok=false,error="后续目录页读取失败"})
check(c.b.active and c.b.error=="后续目录页读取失败"and equal_paths(c.b.selected_order,{original_path})and not c.b.queued,"later-page failure preserves the original selection without a partial result")
c=paged_dialog();original_path=c.b.selected_order[1];c.b:select_all();c.b:tick();c:reply({ok=true,fileBrowser=collection_page("D:\\别的目录",0,200,405)})
check(c.b.error and equal_paths(c.b.selected_order,{original_path})and not c.b.queued,"a mismatched collection directory cannot pollute selected paths")
c=paged_dialog();original_path=c.b.selected_order[1];c.b:select_all();c.b:tick();c:reply({ok=true,fileBrowser=collection_page("D:\\素材",0,0,405)})
check(c.b.error and equal_paths(c.b.selected_order,{original_path})and not c.b.queued,"an empty nonfinal collection page fails instead of looping forever")
c=paged_dialog();original_path=c.b.selected_order[1];c.b:select_all();c.b:tick();c:reply({ok=true,fileBrowser=collection_page("D:\\素材",0,200,405)});c.b:tick();c.b:cancel();c:reply({ok=true,fileBrowser=collection_page("D:\\素材",200,200,405)})
check(not c.b.active and equal_paths(c.b.selected_order,{original_path})and not c.b.queued,"late collection response after cancellation cannot commit or queue more pages")
c=paged_dialog();c.b:select_all();c.b:tick();local stale_request=#c.calls;c.b:cancel();c.b:open({multiple=true,path="D:\\新窗口"});c:reply({ok=true,fileBrowser=collection_page("D:\\素材",0,200,405)},stale_request)
check(c.b.active and c.b.path==nil and #c.b.selected_order==0 and c.b.queued.data.path=="D:\\新窗口","late collection response cannot populate a newly opened dialog")
c.b:tick();c:respond_directory("D:\\新窗口")
check(c.b.path=="D:\\新窗口"and #c.b.selected_order==0,"new dialog can browse normally after a stale collection reply")
c=setup();c.b:select_all();check(not c.b.queued and #c.b.selected_order==0,"single-file picker ignores select-all")
c=setup({multiple=true,save=true});c.b:select_all();check(not c.b.queued and #c.b.selected_order==0,"save picker ignores select-all")

print(string.format("Explorer file dialog: %d passed",passed))
