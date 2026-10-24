-- Browser controller integration: backend replies, user selection and save races.
local root=arg[1]or "."
local module=assert(loadfile(root.."/game-plugin/MotionVfxLive/fx_file_dialog.lua"))()
local count=0
local function check(value,why)assert(value,why);count=count+1;print("PASS "..why)end
local function setup()
    local calls,clicks,inputs,items={},{},{},{}
    local x,y=0,0
    local u={scale=1,real={},colors={}}
    function u:at(px,py)x,y=px,py end
    function u:pos()return x,y end
    function u:space(h)y=y+h end
    function u:rect()end
    function u:label()end
    function u:button(id,text,px,py,w,h,kind,enabled)
        items[id]={text=text,enabled=enabled,kind=kind,x=px or x,y=py or y,w=w,h=h}
        y=(py or y)+h+4
        local hit=clicks[id];clicks[id]=nil
        return hit and enabled~=false
    end
    function u:textbox(id,value,w)
        items[id]={x=x,y=y,w=w,h=30}
        y=y+37;local next=inputs[id];inputs[id]=nil
        return next~=nil,next or value
    end
    function u:panel(_,px,py,w,h,fn)
        local sx,sy=x,y;x,y=px+12,py+10;fn();x,y=sx,sy
    end
    local core={state={}}
    local browser=module.new(core,u,function(action,data,callback)
        if core.reject then core.state.message="编辑后台未连接";return false end
        core.state.pending={action=action};calls[#calls+1]={action=action,data=data,callback=callback};return true
    end)
    local function reply(data,index)
        local call=calls[index or #calls];assert(call,"expected backend call")
        core.state.pending=nil;call.callback(data)
    end
    local function draw(id,values)
        if id then clicks["##mvl_files_"..id]=true end
        for key,value in pairs(values or {})do inputs["##mvl_files_"..key]=value end
        browser:draw(0,0,900,560,1)
    end
    local listing={path="D:\\素材",parentPath="D:\\",roots={{name="D:",path="D:\\"}},shortcuts={{name="桌面",path="C:\\桌面"}},total=3,offset=0,limit=100,entries={
        {name="子目录",path="D:\\素材\\子目录",isDirectory=true},
        {name="特效.efx.12",path="D:\\素材\\特效.efx.12",isDirectory=false,size=2500},
        {name="贴图.tex.34",path="D:\\素材\\贴图.tex.34",isDirectory=false,size=2000000}}}
    return {b=browser,core=core,calls=calls,reply=reply,draw=draw,items=items,listing=listing}
end
local c=setup();local selected
c.b:open({kind="efx",on_select=function(value)selected=value end})
check(c.b.active and #c.calls==0,"opening queues asynchronous in-game browsing")
c.core.state.pending={};c.b:tick();check(#c.calls==0,"navigation waits for other requests without losing intent")
c.core.state.pending=nil;c.b:tick();check(c.calls[1].action=="file_browse"and c.calls[1].data.kind=="efx","typed EFX browse request")
c.reply({ok=true,fileBrowser=c.listing});c.draw("entry_2")
check(c.b.focus.name=="特效.efx.12"and c.b.path=="D:\\素材","file click selects Chinese filename without changing folder")
c.draw();check(c.items["##mvl_files_entry_2"].kind=="selected","selected file is visibly highlighted")
c.draw("submit");check(not c.b.active and selected[1]=="D:\\素材\\特效.efx.12","open returns selected path array and closes modal")
c.b:open({kind="efx"});c.b:tick();check(c.calls[#c.calls].data.path=="D:\\素材","last successful directory retained in memory")
c.reply({ok=true,fileBrowser=c.listing});c.draw("entry_1");c.draw("submit");c.b:tick()
check(c.calls[#c.calls].data.path=="D:\\素材\\子目录","directory opens using explicit action")
c.reply({ok=false,error="无权访问"});check(c.b.error=="无权访问"and c.b.path=="D:\\素材","failed navigation keeps known folder and displays backend error")
c.draw("retry");c.b:tick();check(c.calls[#c.calls].data.path=="D:\\素材\\子目录","retry retries the failed target rather than silently returning to old folder")
c.reply({ok=false,error="无权访问"})
c.draw("go",{path='"D:\\新的中文目录"'});c.b:tick();check(c.calls[#c.calls].data.path=="D:\\新的中文目录","pasted quoted Unicode path is normalized")
c.b:cancel();c.reply({ok=true,fileBrowser=c.listing});check(not c.b.active,"late browse reply cannot reopen a cancelled dialog")
c=setup();c.b:open({kind="assets",multiple=true});c.b:tick();c.reply({ok=true,fileBrowser=c.listing})
c.b:select(c.listing.entries[2]);c.b:select(c.listing.entries[3],{ctrl=true});check(#c.b.selected_order==2,"Ctrl adds another asset file to selection")
c.b:select(c.listing.entries[2],{ctrl=true});check(#c.b.selected_order==1 and c.b.selected_order[1]:find("贴图",1,true),"Ctrl toggles one selected file without discarding others")
c.draw("clear");check(#c.b.selected_order==0,"clear selection removes prior files")
c.draw("filter",{filter_input="火花"});c.b:tick();check(c.calls[#c.calls].data.filter=="火花","explicit filter sends Unicode text to backend")
c=setup();local old_count=0
c.b:open({on_select=function()old_count=old_count+1 end});c.b:tick()
c.b:cancel();c.b:open({kind="pfb"});c.reply({ok=true,fileBrowser=c.listing},1);c.b:tick()
check(old_count==0 and c.calls[#c.calls].data.kind=="pfb"and c.b.path==nil,"previous dialog generation cannot populate a new dialog")
c.core.reject=true;c.core.state.pending=nil;c.b.loading=nil;c.b:browse("D:\\");c.b:tick()
check(c.b.error=="编辑后台未连接"and c.b.loading==nil,"offline failure stays visible and releases pending state")
c=setup();local saved
c.b:open({save=true,name="导出.efx.12",on_select=function(value)saved=value end});c.b:tick();c.reply({ok=true,fileBrowser=c.listing})
c.draw("submit");c.b:tick();check(c.calls[#c.calls].action=="file_stat"and c.calls[#c.calls].data.path=="D:\\素材\\导出.efx.12","save checks exact target before export")
c.reply({ok=true,fileStat={path="D:\\素材\\导出.efx.12",exists=true,isDirectory=false,sha256="abc"}})
check(not saved and c.b.confirm_overwrite.expectedHash=="abc","existing file requires explicit overwrite confirmation with hash")
c.draw("submit");check(saved.expectedHash=="abc"and not c.b.active,"confirmed overwrite carries expected hash")
c=setup();saved=nil
c.b:open({save=true,name="旧名.efx.12",on_select=function(value)saved=value end});c.b:tick();c.reply({ok=true,fileBrowser=c.listing})
c.draw("submit");c.b:tick();c.draw(nil,{name="新名.efx.12"})
c.reply({ok=true,fileStat={path="D:\\素材\\旧名.efx.12",exists=false}})
check(not saved and c.b.active and c.b.loading==nil,"editing filename while stat is pending cannot export to stale target")
c.draw("submit");c.b:tick();c.reply({ok=true,fileStat={path="D:\\素材\\新名.efx.12",exists=false}})
check(saved.path=="D:\\素材\\新名.efx.12"and not saved.expectedHash,"new export target returns canonical path without overwrite hash")
c=setup();local skipped=0
c.b:open({save=true,cancel_label="跳过此文件",on_cancel=function()skipped=skipped+1;c.b:open({title="第二个文件"})end})
c.draw("cancel");check(skipped==1 and c.b.active and c.b.options.title=="第二个文件","cancelling one export can open next modal without closing it")
c=setup();c.b:open({save=true,name="../非法.efx"});c.b:tick();c.reply({ok=true,fileBrowser=c.listing});c.draw("submit")
check(c.b.error and #c.calls==1,"save rejects directory traversal in filename")
c.b.name="合法.efx";c.draw("submit");c.b:tick();c.reply({ok=true,fileStat={path="D:\\素材\\合法.efx",exists=true}})
check(c.b.error and not c.b.confirm_overwrite,"existing file without hash cannot be silently overwritten")
c=setup();c.b:open({});c.b:tick();local listing=c.listing;listing.path="D:\\";listing.parentPath=nil;c.reply({ok=true,fileBrowser=listing});c.draw("parent")
check(not c.items["##mvl_files_parent"].enabled and #c.calls==1,"drive root cannot navigate to invalid parent")
c=setup();c.b:open({});c.b:tick();listing=c.listing;listing.total=205;listing.limit=3;c.reply({ok=true,fileBrowser=listing})
c.draw("next");c.b:tick();check(c.calls[#c.calls].data.offset==3,"next page advances by backend page size")
listing.offset=3;c.reply({ok=true,fileBrowser=listing});c.draw("previous");c.b:tick()
check(c.calls[#c.calls].data.offset==0,"previous page returns to beginning without negative offset")
c.reply({ok=true,fileBrowser=listing});c.draw("shortcut_1");c.b:tick()
check(c.calls[#c.calls].data.path=="C:\\桌面","shortcut navigates directly to real folder path")
print(string.format("File dialog: %d passed",count))
