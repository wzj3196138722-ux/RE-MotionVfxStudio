-- Actual ImGui press/release and key events + production dialog/widgets.
-- The filesystem alone is simulated; no desktop or game input is generated.
local project=assert(arg[1])
local N=native_imgui
Vector2f={new=function(x,y)return {x=x,y=y}end}
local widget_factory=assert(loadfile(project.."/game-plugin/MotionVfxLive/fx_d2d_widgets.lua"))()
local passed=0
local function check(value,label)assert(value,label);passed=passed+1;print("PASS "..label)end
local function setup(scale,multiple)
    N.reset({1920,1080},24,{18,12},{32,32},2,1)
    local c={clock=0,rects={},requests={},opened=nil,hits=0,double_frames=0,old_intersections=0}
    local env=setmetatable({},{__index=_G})
    env.os=setmetatable({clock=function()return c.clock end},{__index=os})
    local factory=assert(loadfile(project.."/game-plugin/MotionVfxLive/fx_file_dialog.lua","t",env))()
    c.u=widget_factory.new(N,{})
    local original=c.u.button
    c.u.button=function(u,id,label,x,y,w,h,...)
        local bx,by=x,y;if not bx then bx,by=u:pos()end
        c.rects[id]={x=bx,y=by,w=w,h=h or 34*u.scale}
        local hit=original(u,id,label,x,y,w,h,...)
        if id:find("##mvl_files_entry_",1,true)then
            local double=N.is_mouse_double_clicked(0)
            if hit then c.hits=c.hits+1 end
            if double and N.is_item_hovered()then c.double_frames=c.double_frames+1 end
            if hit and double then c.old_intersections=c.old_intersections+1 end
        end
        return hit
    end
    local original_textbox=c.u.textbox
    c.u.textbox=function(u,id,value,width,prefix)
        local x,y=u:pos();c.rects[id]={x=x,y=y,w=width,h=30*u.scale}
        return original_textbox(u,id,value,width,prefix)
    end
    c.core={state={}}
    local directories={
        ["C:\\Assets"]={parent="C:\\",entries={
            {name="textures",path="C:\\Assets\\textures",isDirectory=true},
            {name="alpha.efx.5571972",path="C:\\Assets\\alpha.efx.5571972",size=123},
            {name="beta.efx.5571972",path="C:\\Assets\\beta.efx.5571972",size=456},
            {name="gamma.efx.5571972",path="C:\\Assets\\gamma.efx.5571972",size=789}}},
        ["C:\\Assets\\textures"]={parent="C:\\Assets",entries={{name="flame.tex.240701001",path="C:\\Assets\\textures\\flame.tex.240701001",size=1000}}},
        ["C:\\"]={entries={{name="Assets",path="C:\\Assets",isDirectory=true}}}
    }
    c.dialog=factory.new(c.core,c.u,function(action,data,callback)
        c.requests[#c.requests+1]={action=action,data=data}
        if action=="file_browse"then
            local path=data.path~=""and data.path or "C:\\Assets";local d=directories[path]
            if not d then callback({ok=false,error="Test directory does not exist: "..path});return true end
            local entries={}
            for _,entry in ipairs(d.entries)do if (data.filter or "")==""or entry.name:lower():find(data.filter:lower(),1,true)then entries[#entries+1]=entry end end
            callback({ok=true,fileBrowser={path=path,parentPath=d.parent,roots={{name="C:",path="C:\\"}},shortcuts={{name="Assets",path="C:\\Assets"}},entries=entries,total=#entries,offset=0,limit=100}})
        elseif action=="file_stat"then callback({ok=true,fileStat={path=data.path,exists=false,isDirectory=false}})
        else error("Unexpected backend action "..tostring(action))end
        return true
    end)
    function c:frame(x,y,down,dt)
        dt=dt or 1/60;self.clock=self.clock+dt;N.set_delta_time(dt)
        self.dialog:tick()
        N.begin_frame(x or -100,y or -100,down or false)
        N.set_next_window_pos({100,80});N.set_next_window_size({1650,900})
        N.begin_window("Dialog native regression",true,1+2+4+8+32+256)
        self.u:begin(scale)
        self.dialog:draw(120,100,1100*scale,650*scale,scale)
        N.end_window();N.end_frame()
    end
    function c:idle(n)for _=1,n or 1 do self:frame()end end
    function c:click(id)
        local r=assert(self.rects[id],"Missing input "..id);local x,y=r.x+r.w*.5,r.y+r.h*.5
        self:frame(x,y,false);self:frame(x,y,true);self:frame(x,y,false)
    end
    function c:row(index)self:click("##mvl_files_entry_"..index)end
    function c:double(index)self:row(index);self:row(index);self:idle(2)end
    function c:key(name,modifier)
        if modifier then N.dialog_key_event(modifier,true)end
        N.dialog_key_event(name,true);self:frame();N.dialog_key_event(name,false)
        if modifier then N.dialog_key_event(modifier,false)end
        self:frame()
    end
    function c:select_modified(index,modifier)
        N.dialog_key_event(modifier,true);self:row(index);N.dialog_key_event(modifier,false);self:frame()
    end
    c.dialog:open({kind="efx",path="C:\\Assets",multiple=multiple,on_select=function(paths)c.opened=paths end,on_cancel=function()c.cancelled=true end})
    c:idle(3)
    return c
end

for _,scale in ipairs({.75,1,1.3})do
    local prefix="scale "..scale.." / "
    local c=setup(scale)
    c:row(1)
    check(c.dialog.path=="C:\\Assets"and c.dialog.active,prefix.."single click selects folder without navigating")
    c:row(1);c:idle(2)
    check(c.dialog.path=="C:\\Assets\\textures",prefix.."second release opens folder")
    check(c.hits>=2 and c.double_frames>0 and c.old_intersections==0,prefix.."real ImGui proves old hit-and-double condition misses events")
    c:key("Backspace");c:idle(2)
    check(c.dialog.path=="C:\\Assets",prefix.."Backspace returns to parent")
    c:double(2)
    check(not c.dialog.active and c.opened and c.opened[1]=="C:\\Assets\\alpha.efx.5571972",prefix.."double click opens selected file")
end
do
    local c=setup(1)
    c:row(2);c:idle(45);c:row(2)
    check(c.dialog.active and not c.opened,"slow repeated clicks do not open a file")
    c:row(3)
    check(c.dialog.active and not c.opened,"quick clicks on different rows do not open a file")
    c:key("Enter")
    check(c.opened and c.opened[1]=="C:\\Assets\\beta.efx.5571972","Enter opens the selected file")
end
do
    local c=setup(1,true)
    c:row(2);c:select_modified(4,"LeftCtrl")
    check(#c.dialog.selected_order==2 and c.dialog.selected["C:\\Assets\\alpha.efx.5571972"]and c.dialog.selected["C:\\Assets\\gamma.efx.5571972"],"Ctrl-click adds a separate file")
    c:select_modified(2,"LeftCtrl")
    check(#c.dialog.selected_order==1 and c.dialog.selected["C:\\Assets\\gamma.efx.5571972"],"Ctrl-click toggles an existing selection off")
    c:idle(40);c:row(2);c:select_modified(4,"LeftShift")
    check(#c.dialog.selected_order==3,"Shift-click selects contiguous file range")
    c:key("Enter")
    check(c.opened and #c.opened==3,"Enter accepts all selected files")
end
do
    local c=setup(1,true)
    c:row(2);c:select_modified(4,"LeftCtrl")
    N.dialog_key_event("LeftCtrl",true);N.dialog_key_event("LeftShift",true)
    N.dialog_key_event("UpArrow",true);c:frame();N.dialog_key_event("UpArrow",false)
    N.dialog_key_event("LeftShift",false);N.dialog_key_event("LeftCtrl",false);c:frame()
    check(#c.dialog.selected_order==3,"Ctrl+Shift+Up extends a range while retaining other selected files")
end
do
    local c=setup(1)
    c:key("End");c:key("Enter")
    check(c.opened and c.opened[1]=="C:\\Assets\\gamma.efx.5571972","End selects the last entry")
end
do
    local c=setup(1)
    c:key("End");c:key("Home");c:key("DownArrow");c:key("Enter")
    check(c.opened and c.opened[1]=="C:\\Assets\\alpha.efx.5571972","Home and arrow navigation select entries without opening folders")
end
do
    local c=setup(1,true)
    c:click("##mvl_files_path")
    c:key("A","LeftCtrl");N.text_event("C:\\Assets\\textures");c:frame()
    check(c.dialog.path_input=="C:\\Assets\\textures"and #c.dialog.selected_order==0,"path text Ctrl+A stays in the text editor")
    c:key("Enter");c:idle(2)
    check(c.dialog.active and c.dialog.path=="C:\\Assets\\textures"and not c.opened,"Enter in the path field navigates without submitting a file")
end
do
    local c=setup(1)
    c.dialog:open({kind="all",save=true,path="C:\\Assets",name="export.efx.5571972",on_select=function(destination)c.saved=destination end})
    c:idle(3);c:row(1)
    check(c.dialog.focus and c.dialog.focus.isDirectory,"save dialog may select a folder before editing its filename")
    c:click("##mvl_files_name");c:key("Enter");c:idle(2)
    check(not c.dialog.active and c.saved and c.saved.path=="C:\\Assets\\export.efx.5571972","unchanged filename Enter saves in the current folder instead of entering a selected folder")
end
do
    local c=setup(1)
    c:double(1);c:key("LeftArrow","LeftAlt");c:idle(2)
    check(c.dialog.path=="C:\\Assets","Alt+Left navigates back")
    c:key("RightArrow","LeftAlt");c:idle(2)
    check(c.dialog.path=="C:\\Assets\\textures","Alt+Right navigates forward")
    c:key("UpArrow","LeftAlt");c:idle(2)
    check(c.dialog.path=="C:\\Assets","Alt+Up navigates to parent")
    local before=#c.requests;c:key("F5");c:idle(2)
    check(#c.requests==before+1 and c.requests[#c.requests].action=="file_browse","F5 refreshes current directory once")
    c:key("Escape")
    check(not c.dialog.active and c.cancelled,"Escape closes the dialog")
end
print(string.format("Native file dialog results: %d passed",passed))
