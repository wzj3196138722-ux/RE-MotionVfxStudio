-- The browser shares the workbench's input host and theme. File system access
-- stays in the backend; no OS dialog can disappear behind the fullscreen game.
local M={}
local titles={efx="打开特效文件",motion="打开动作文件",pfb="打开 PFB 文件",assets="选择外部素材",all="打开本地文件"}
local hints={efx="EFX 特效",motion="MOT / MOTLIST 动作",pfb="PFB 预制体",assets="Mesh / MDF / UVS / Tex / PFB / 动作 / EFX",all="所有文件"}
local function clean(value)
    value=tostring(value or ""):match("^%s*(.-)%s*$")
    if value:sub(1,1)=='"' and value:sub(-1)=='"' then value=value:sub(2,-2)end
    return value
end
local function filename(path)return tostring(path or ""):match("([^/\\]+)$")or ""end
local function size_text(bytes)
    bytes=tonumber(bytes)or 0
    if bytes>=1073741824 then return string.format("%.1f GB",bytes/1073741824)end
    if bytes>=1048576 then return string.format("%.1f MB",bytes/1048576)end
    if bytes>=1024 then return string.format("%.1f KB",bytes/1024)end
    return tostring(bytes).." B"
end
function M.new(core,u,send)
    local self={active=false,generation=0,sequence=0,last_path=nil}
    -- u.colors is the adapter's temporary style stack, not the theme palette.
    local C={panel=0xFF171F30,bg=0xFF0D111C,text=0xFFE6EDF9,muted=0xFF8F9DB6,accent=0xFF8275FF,warning=0xFFFFC17A,line=0xFF2A3851}
    local function busy()
        local s=core.state or {}
        return s.pending or s.ai_busy or s.reloading
    end
    local function message(text)self.message=tostring(text or "");self.error=nil end
    function self:browse(path,offset,history_index)
        if not self.active then return end
        self.sequence=self.sequence+1
        self.last_click=nil
        self.queued={action="file_browse",data={path=clean(path),kind=self.options.kind or "all",filter=self.filter or "",offset=offset or 0,limit=100},sequence=self.sequence,history_index=history_index}
        self.last_browse={path=clean(path),offset=offset or 0}
        self.confirm_overwrite=nil;self.error=nil
        message("正在读取目录…")
    end
    function self:navigate_back()
        if self.history_index>1 then self.filter="";self:browse(self.history[self.history_index-1],0,self.history_index-1)end
    end
    function self:navigate_forward()
        if self.history_index<#self.history then self.filter="";self:browse(self.history[self.history_index+1],0,self.history_index+1)end
    end
    function self:navigate_parent()
        if self.parent and self.parent~=""then self.filter="";self:browse(self.parent)end
    end
    function self:open(options)
        self.generation=self.generation+1;self.sequence=self.sequence+1
        self.active=true;self.options=options or {};self.filter="";self.entries={};self.roots={};self.shortcuts={}
        self.selected={};self.selected_order={};self.focus=nil;self.path=nil;self.parent=nil;self.total=0;self.offset=0;self.limit=100
        self.loading=nil;self.queued=nil;self.confirm_overwrite=nil;self.error=nil
        self.history={};self.history_index=0;self.last_click=nil;self.anchor_index=nil;self.keys_down={};self.text_focus=nil;self.keyboard_owned=true
        self.name=self.options.name or "";self.path_input=self.options.path or self.last_path or ""
        self:browse(self.path_input,0)
    end
    function self:cancel()
        if not self.active then return end
        local callback=self.options.on_cancel
        self.active=false;self.generation=self.generation+1;self.queued=nil;self.loading=nil;self.confirm_overwrite=nil
        if callback then callback()end
    end
    local function finish(result)
        local callback=self.options.on_select
        self.active=false;self.generation=self.generation+1;self.queued=nil;self.loading=nil;self.confirm_overwrite=nil
        if callback then callback(result)end
    end
    local function accept_browser(browser,history_index)
        local changed=self.path~=browser.path
        if history_index and self.history[history_index]==browser.path then self.history_index=history_index
        elseif self.history[self.history_index]~=browser.path then
            for i=#self.history,self.history_index+1,-1 do table.remove(self.history,i)end
            self.history[#self.history+1]=browser.path;self.history_index=#self.history
        end
        self.path=browser.path;self.path_input=browser.path or "";self.last_path=browser.path or self.last_path
        self.parent=browser.parentPath;self.entries=browser.entries or {};self.roots=browser.roots or {};self.shortcuts=browser.shortcuts or {}
        self.total=browser.total or #self.entries;self.offset=browser.offset or 0;self.limit=browser.limit or 100
        self.last_click=nil;self.anchor_index=nil
        if changed then self.focus=nil;self.selected={};self.selected_order={}
        else
            -- A full refresh can prove a file has gone. Another page cannot.
            if self.offset==0 and #self.entries>=self.total then
                local found={};for _,entry in ipairs(self.entries)do found[entry.path]=true end
                for i=#self.selected_order,1,-1 do
                    local path=self.selected_order[i]
                    if not found[path]then self.selected[path]=nil;table.remove(self.selected_order,i)end
                end
            end
            local focus=self.focus;self.focus=nil
            for _,entry in ipairs(self.entries)do if focus and entry.path==focus.path then self.focus=entry;break end end
        end
        if browser.selectedPath then
            for _,entry in ipairs(self.entries)do
                if entry.path==browser.selectedPath then self:select(entry);break end
            end
        end
        message(self.total==0 and "此目录中没有符合条件的文件；可修改筛选或进入其他目录。" or ("已读取 "..tostring(self.total).." 项"))
    end
    function self:tick()
        if not self.active then return end
        if self.loading and os.time()-self.loading.started>25 then
            self.loading=nil;self.error="读取超时。请检查编辑后台连接，然后重试；原有文档保持不变。"
            self.sequence=self.sequence+1
        end
        if self.loading or not self.queued or busy() then return end
        local request=self.queued;self.queued=nil
        local generation,sequence=self.generation,request.sequence
        self.loading={started=os.time(),sequence=sequence,action=request.action}
        local accepted=send(request.action,request.data,function(r)
            if not self.active or self.generation~=generation or self.sequence~=sequence then
                if self.active and self.generation==generation and self.loading and self.loading.sequence==sequence then self.loading=nil end
                return
            end
            self.loading=nil
            if not r or not r.ok then self.error=tostring(r and (r.error or r.message)or "文件浏览请求失败，请重试。");return end
            if request.action=="file_browse" then
                if not r.fileBrowser then self.error="后台没有返回目录内容，请更新编辑后台后重试。";return end
                if request.collect then
                    local browser=r.fileBrowser;local selection=request.collect
                    if browser.path~=selection.path then self.error="目录已变化，请重新选择文件。";return end
                    for _,entry in ipairs(browser.entries or {})do
                        if not entry.isDirectory and not selection.selected[entry.path]then
                            selection.selected[entry.path]=true;selection.paths[#selection.paths+1]=entry.path
                        end
                    end
                    local next_offset=(browser.offset or 0)+#(browser.entries or {})
                    if next_offset<(browser.total or 0)then
                        if next_offset<=(request.data.offset or 0)then self.error="目录内容发生变化，请重新全选。";return end
                        self.queued={action="file_browse",data={path=selection.path,kind=self.options.kind or "all",filter=selection.filter,offset=next_offset,limit=200},sequence=self.sequence,collect=selection}
                        message("正在全选文件… "..#selection.paths)
                    else
                        self.selected=selection.selected;self.selected_order=selection.paths
                        message("已选择 "..#selection.paths.." 个文件")
                    end
                    return
                end
                accept_browser(r.fileBrowser,request.history_index)
            else
                local stat=r.fileStat
                if not stat or type(stat.path)~="string"or stat.path==""then self.error="后台没有返回目标文件状态，请重试。";return end
                if stat.isDirectory then self.error="这个名称是文件夹，请输入文件名。";return end
                if stat.exists then
                    if not stat.sha256 then self.error="无法核实已有文件，未覆盖；请选择另一个文件名。";return end
                    self.confirm_overwrite={path=stat.path,expectedHash=stat.sha256}
                    message("目标文件已存在。确认覆盖将只替换所选文件。")
                else finish({path=stat.path,expectedHash=nil})end
            end
        end)
        if accepted==false or accepted==nil and self.loading and not (core.state or {}).pending then
            self.loading=nil;self.error=tostring((core.state or {}).message or "编辑后台未连接，无法读取文件。请恢复连接后重试。")
        end
    end
    local function add_selection(entry)
        if not entry.isDirectory and not self.selected[entry.path]then self.selected[entry.path]=true;self.selected_order[#self.selected_order+1]=entry.path end
    end
    function self:select_all()
        if not self.active or not self.options.multiple or self.options.save or busy()or self.loading or self.queued then return end
        self.last_click=nil
        if self.offset==0 and #self.entries>=self.total then
            self.selected={};self.selected_order={};for _,entry in ipairs(self.entries)do add_selection(entry)end
        else
            self.sequence=self.sequence+1
            self.queued={action="file_browse",data={path=self.path,kind=self.options.kind or "all",filter=self.filter or "",offset=0,limit=200},sequence=self.sequence,
                collect={path=self.path,filter=self.filter or "",selected={},paths={}}}
            message("正在全选文件…")
        end
    end
    function self:select(entry,mods,index)
        mods=mods or {}
        self.focus=entry;self.confirm_overwrite=nil
        if not index then for i,row in ipairs(self.entries)do if row.path==entry.path then index=i;break end end end
        if self.options.save then
            self.selected={};self.selected_order={};self.anchor_index=index
            if not entry.isDirectory then self.name=entry.name;add_selection(entry)end
            return
        end
        if self.options.multiple and mods.shift and self.anchor_index and index then
            if not mods.ctrl then self.selected={};self.selected_order={}end
            for i=math.min(self.anchor_index,index),math.max(self.anchor_index,index)do add_selection(self.entries[i])end
            return
        end
        if not self.options.multiple or not mods.ctrl then self.selected={};self.selected_order={}end
        self.anchor_index=index
        if entry.isDirectory then return end
        if self.options.multiple and mods.ctrl and self.selected[entry.path] then
            self.selected[entry.path]=nil
            for i,path in ipairs(self.selected_order)do if path==entry.path then table.remove(self.selected_order,i);break end end
        else add_selection(entry)end
    end
    function self:click_entry(entry,index,mods,now,position)
        if not self.active or busy()or self.loading or self.queued then return end
        mods=mods or {};now=now or os.clock()
        position=position or (u.real.get_mouse and u.real.get_mouse())or {x=0,y=0}
        local last=self.last_click
        local double=last and last.path==entry.path and not mods.ctrl and not mods.shift and not last.modified and
            now>=last.time and now-last.time<=.5 and (position.x-last.x)^2+(position.y-last.y)^2<=36
        self:select(entry,mods,index)
        -- InvisibleButton reports successful clicks on release. Remember those
        -- releases together; ImGui's double-click flag belongs to the earlier
        -- second press and therefore cannot be tested inside this branch.
        self.last_click={path=entry.path,time=now,x=position.x,y=position.y,modified=mods.ctrl or mods.shift}
        if double then self.last_click=nil;self:submit()end
    end
    function self:submit()
        if not self.active or busy()or self.loading or self.queued then return end
        if self.confirm_overwrite then finish(self.confirm_overwrite);return end
        if self.focus and self.focus.isDirectory then self.filter="";self:browse(self.focus.path);return end
        if self.options.save then
            local name=clean(self.name)
            if name==""or name=="."or name==".."or name:find('[<>:"/\\|?*%c]') then self.error="请输入有效的文件名；文件夹请在上方选择。";return end
            if not self.path or self.path==""then self.error="请先选择保存文件夹。";return end
            self.sequence=self.sequence+1
            self.queued={action="file_stat",data={path=self.path:gsub("[/\\]+$","").."\\"..name,hash=true},sequence=self.sequence}
            message("正在检查导出位置…")
        elseif #self.selected_order>0 then
            local result={};for i,path in ipairs(self.selected_order)do result[i]=path end
            finish(result)
        else self.error="请先选择要打开的文件。"end
    end
    local function key_call(method,name)
        local api=u.real;local keys=type(api.ImGuiKey)=="table"and api.ImGuiKey or {};local key=keys["Key_"..name]
        if type(api[method])~="function"or type(key)~="number"then return false end
        return api[method](key)==true
    end
    function self:modifiers()
        return {ctrl=key_call("is_key_down","LeftCtrl")or key_call("is_key_down","RightCtrl"),
            shift=key_call("is_key_down","LeftShift")or key_call("is_key_down","RightShift"),
            alt=key_call("is_key_down","LeftAlt")or key_call("is_key_down","RightAlt")}
    end
    function self:handle_keys()
        if not self.active then return end
        local pressed={}
        for _,name in ipairs({"Enter","KeypadEnter","Escape","Backspace","LeftArrow","RightArrow","UpArrow","DownArrow","Home","End","F5","A","Space"})do
            local down=key_call("is_key_down",name)
            pressed[name]=key_call("is_key_pressed",name)and (not self.keys_down[name]or name=="UpArrow"or name=="DownArrow")
            self.keys_down[name]=down
        end
        if not self.keyboard_owned then return end
        local field=self.text_focus or self.prior_text_focus
        if pressed.Escape then
            if not field then self:cancel()end
            self.last_click=nil;return
        end
        if busy()or self.loading or self.queued then return end
        local enter=pressed.Enter or pressed.KeypadEnter
        if field then
            if enter then
                if field=="path"then self.filter="";self:browse(self.path_input)
                elseif field=="filter"then self:browse(self.path or self.path_input)
                elseif field=="name"then self:submit()end
            end
            return -- Leave text movement, selection and deletion to InputText.
        end
        local mods=self:modifiers()
        if mods.alt and pressed.LeftArrow or pressed.Backspace then self:navigate_back();return end
        if mods.alt and pressed.RightArrow then self:navigate_forward();return end
        if mods.alt and pressed.UpArrow then self:navigate_parent();return end
        if pressed.F5 then self:browse(self.path,self.offset);return end
        if enter then self.last_click=nil;self:submit();return end
        if mods.ctrl and pressed.A and self.options.multiple and not self.options.save then
            self:select_all();return
        end
        local index
        for i,entry in ipairs(self.entries)do if self.focus and entry.path==self.focus.path then index=i;break end end
        local next_index=pressed.Home and 1 or pressed.End and #self.entries or
            pressed.DownArrow and math.min(#self.entries,(index or 0)+1)or
            pressed.UpArrow and math.max(1,(index or (#self.entries+1))-1)
        if next_index and self.entries[next_index]and not mods.alt then
            local entry=self.entries[next_index]
            if mods.ctrl and not mods.shift then self.focus=entry
            else self:select(entry,{shift=mods.shift,ctrl=mods.ctrl},next_index)end
            self.scroll_to=entry.path;self.last_click=nil
        elseif pressed.Space and index then self:select(self.entries[index],{ctrl=mods.ctrl,shift=mods.shift},index);self.last_click=nil end
    end
    function self:draw(x,y,w,h,k)
        if not self.active then return end
        self.prior_text_focus=self.text_focus;self.text_focus=nil
        if u.real.is_mouse_clicked and u.real.is_mouse_clicked(0)then
            local mouse=u.real.get_mouse and u.real.get_mouse()
            if mouse then self.keyboard_owned=mouse.x>=x and mouse.x<x+w and mouse.y>=y and mouse.y<y+h end
            if not self.keyboard_owned then self.last_click=nil;self.prior_text_focus=nil end
        end
        k=k or u.scale or 1
        local pad=18*k;local row=32*k;local usable=w-pad*2
        local function label(text,lx,ly,size,color,width)u:label(text,lx,ly,(size or 14)*k,color or C.text,width)end
        local function button(id,text,bx,by,bw,kind,enabled)
            local hit=u:button("##mvl_files_"..id,text,bx,by,bw,row,kind,enabled)
            if hit then self.last_click=nil end
            return hit
        end
        local function text_focus(name)
            if u.real.is_item_active and u.real.is_item_active()then
                self.text_focus=name;self.last_click=nil
                if name=="name"then self.focus=nil end
            end
        end
        local enabled=not busy()and not self.loading and not self.queued
        u:rect(x,y,w,h,C.bg,12*k)
        label(self.options.title or (self.options.save and "导出文件"or titles[self.options.kind]or titles.all),x+pad,y+14*k,22,C.text,usable-86*k)
        if button("close","关闭",x+w-pad-76*k,y+12*k,76*k,nil,true)then self:cancel();return end
        label(hints[self.options.kind]or hints.all,x+pad,y+48*k,12,C.muted,usable)
        if button("back","后退",x+pad,y+73*k,48*k,nil,enabled and self.history_index>1)then self:navigate_back()end
        if button("forward","前进",x+pad+54*k,y+73*k,48*k,nil,enabled and self.history_index<#self.history)then self:navigate_forward()end
        u:at(x+pad+110*k,y+73*k)
        local changed,value=u:textbox("##mvl_files_path",self.path_input,usable-208*k)
        text_focus("path")
        if changed then self.path_input=value;self.confirm_overwrite=nil end
        if button("go","前往路径",x+w-pad-90*k,y+73*k,90*k,nil,enabled)then self:browse(self.path_input)end
        u:at(x+pad,y+113*k)
        changed,value=u:textbox("##mvl_files_filter_input",self.filter,math.max(90*k,usable-296*k),"筛选")
        text_focus("filter")
        if changed then self.filter=value end
        if button("filter","筛选",x+w-pad-288*k,y+113*k,76*k,nil,enabled)then self:browse(self.path or self.path_input)end
        if button("parent","上级目录",x+w-pad-204*k,y+113*k,100*k,nil,enabled and self.parent~=nil and self.parent~="")then self:navigate_parent()end
        if button("refresh","刷新",x+w-pad-96*k,y+113*k,96*k,nil,enabled)then self:browse(self.path or self.path_input,self.offset)end
        local footer=self.options.save and 146*k or 110*k
        local top=y+157*k;local height=math.max(65*k,h-157*k-footer)
        local side=math.min(178*k,usable*.26)
        u:panel("files_places",x+pad,top,side,height,function()
            local function section(title,items,prefix)
                local px,py=u:pos();label(title,px,py,12,C.muted,side-24*k);u:space(23*k)
                for i,item in ipairs(items)do
                    if u:button("##mvl_files_"..prefix..i,item.name,nil,nil,side-24*k,row,self.path==item.path and "selected"or nil,enabled)then self.filter="";self:browse(item.path)end
                end
                u:space(12*k)
            end
            section("常用位置",self.shortcuts,"shortcut_");section("磁盘",self.roots,"root_")
        end)
        local listx=x+pad+side+10*k;local listw=usable-side-10*k
        u:panel("files_entries",listx,top,listw,height,function()
            local px,py=u:pos();label("名称",px,py,12,C.muted,listw-120*k);label("大小",px+listw-106*k,py,12,C.muted,76*k);u:space(25*k)
            for i,entry in ipairs(self.entries)do
                local rx,ry=u:pos();local focused=self.focus and self.focus.path==entry.path
                local selected=self.selected[entry.path]or entry.isDirectory and focused
                local hit=u:button("##mvl_files_entry_"..i,"",nil,nil,listw-24*k,row,selected and "selected"or nil,enabled)
                if focused and not selected then u:rect(rx+3*k,ry+row-2*k,listw-30*k,1,C.muted,0)end
                if self.scroll_to==entry.path then
                    if u.real.set_scroll_here_y then u.real.set_scroll_here_y(.5)end
                    self.scroll_to=nil
                end
                label(entry.isDirectory and "目录"or "文件",rx+10*k,ry+7*k,11,entry.isDirectory and C.accent or C.muted,35*k)
                label(entry.name,rx+49*k,ry+6*k,13,C.text,listw-159*k)
                label(entry.isDirectory and "—"or size_text(entry.size),rx+listw-106*k,ry+7*k,11,C.muted,76*k)
                if hit then
                    self:click_entry(entry,i,self:modifiers())
                end
            end
            if #self.entries==0 then
                local px,py=u:pos();label(self.loading and "正在读取…"or "没有可显示的文件",px,py,13,C.muted,listw-24*k)
            end
        end)
        local footy=top+height+8*k
        local first=self.total>0 and self.offset+1 or 0;local last=math.min(self.total,self.offset+#self.entries)
        label(string.format("%d–%d / %d 项",first,last,self.total),x+pad,footy+7*k,12,C.muted,usable-220*k)
        if button("previous","上一页",x+w-pad-190*k,footy,90*k,nil,enabled and self.offset>0)then self:browse(self.path,math.max(0,self.offset-self.limit))end
        if button("next","下一页",x+w-pad-90*k,footy,90*k,nil,enabled and self.offset+#self.entries<self.total)then self:browse(self.path,self.offset+self.limit)end
        if self.options.save then
            u:at(x+pad,footy+38*k)
            changed,value=u:textbox("##mvl_files_name",self.name,usable,"文件名")
            text_focus("name")
            if changed then
                self.name=value;self.focus=nil;self.confirm_overwrite=nil
                if self.loading and self.loading.action=="file_stat"or self.queued and self.queued.action=="file_stat"then
                    self.sequence=self.sequence+1;self.queued=nil;message("文件名已改变，请重新点击导出。")
                end
            end
        end
        local statusy=y+h-68*k
        local status=self.error or self.message or ""
        if self.queued and busy()then status="等待编辑后台完成当前请求…"end
        if self.options.multiple and #self.selected_order>0 and not self.error then status="已选择 "..#self.selected_order.." 个文件 · "..status end
        label(status,x+pad,statusy,12,self.error and C.warning or C.muted,usable)
        local btny=y+h-40*k
        local text=self.confirm_overwrite and "确认覆盖文件"or self.focus and self.focus.isDirectory and "进入文件夹"or self.options.save and "导出到此位置"or (self.options.multiple and "选择这些文件"or "打开选中文件")
        if self.error and button("retry","重试",x+pad,btny,80*k,nil,enabled)then
            self:browse(self.last_browse and self.last_browse.path or self.path_input,self.last_browse and self.last_browse.offset or 0)
        end
        if self.options.multiple and #self.selected_order>0 and button("clear","清除选择",x+pad+88*k,btny,110*k,nil,enabled)then self.selected={};self.selected_order={};self.focus=nil end
        if button("cancel",self.options.cancel_label or "取消",x+w-pad-270*k,btny,118*k,nil,true)then self:cancel();return end
        if button("submit",text,x+w-pad-144*k,btny,144*k,self.confirm_overwrite and "warning"or "primary",enabled and (self.options.save or self.focus~=nil or #self.selected_order>0))then self:submit()end
        self:handle_keys()
    end
    return self
end
return M
