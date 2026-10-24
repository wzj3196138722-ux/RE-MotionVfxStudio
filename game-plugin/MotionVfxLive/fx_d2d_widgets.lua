-- One layout/input host; D2D paints the chrome, labels and control backgrounds.
-- ImGui supplies scrolling, Unicode/IME editing and focus, not visible windows.
-- Editable text is rendered by its native editor so mouse hit testing, selection,
-- horizontal scrolling and the IME all use exactly the same font and coordinates.
local M = {}
local C = {bg=0xFF0D111C,sidebar=0xFF111725,panel=0xFF171F30,raised=0xFF202B40,
    line=0xFF2A3851,text=0xFFE6EDF9,muted=0xFF8F9DB6,accent=0xFF8275FF,
    cyan=0xFF63D9E9,green=0xFF6EDDAA,warning=0xFFFFC17A,input=0xFF101827}
M.colors=C
local function caption(s) return tostring(s or ""):match("^(.-)##") or tostring(s or "") end
local function argb(n)
    n = n & 0xffffffff
    return (n & 0xff00ff00) | ((n & 255)<<16) | ((n>>16)&255)
end
function M.new(real, api)
    local u = {real=real,api=api,commands={},trees={},clips={},colors={},tree_depth=0,serial=0,scale=1}
    local a = {}
    u.adapter=a
    function u:emit(kind,data)
        data.kind=kind
        local clip
        for _,bounds in ipairs(self.clips) do
            if not clip then clip={x=bounds.x,y=bounds.y,w=bounds.w,h=bounds.h}
            else
                local right,bottom=math.min(clip.x+clip.w,bounds.x+bounds.w),math.min(clip.y+clip.h,bounds.y+bounds.h)
                clip.x,clip.y=math.max(clip.x,bounds.x),math.max(clip.y,bounds.y)
                clip.w,clip.h=right-clip.x,bottom-clip.y
            end
        end
        if clip then
            -- D2D v1.4.1 has no clipping API. Omit partially clipped text/rows;
            -- panel backgrounds stay outside the scroll host.
            if clip.w<=0 or clip.h<=0 then return end
            if data.y and (data.y < clip.y or data.y+(data.h or 20*self.scale) > clip.y+clip.h) then return end
            if data.kind=="line" and (data.y2<clip.y or data.y2>clip.y+clip.h or
                data.x<clip.x or data.x2<clip.x or data.x>clip.x+clip.w or data.x2>clip.x+clip.w) then return end
            data.maxWidth=math.min(data.maxWidth or clip.w,clip.x+clip.w-(data.x or clip.x))
            if data.w then data.w=math.max(0,math.min(data.w,clip.x+clip.w-(data.x or clip.x))) end
        end
        self.commands[#self.commands+1]=data
    end
    function u:rect(x,y,w,h,color,r) self:emit("rect",{x=x,y=y,w=w,h=h,color=color or C.panel,r=r or 0}) end
    function u:label(text,x,y,size,color,w)
        self:emit("text",{text=tostring(text or ""),x=x,y=y,size=size or 15,color=color or C.text,maxWidth=w,h=(size or 15)*1.4})
    end
    function u:at(x,y) real.set_cursor_screen_pos({x,y}) end
    function u:pos() local p=real.get_cursor_screen_pos();return p.x,p.y end
    function u:space(height)
        self.serial=self.serial+1;real.invisible_button("##mvl_d2d_space_"..self.serial,{1,height or 8*self.scale})
    end
    function u:measure(text,size)
        size=size or 15*self.scale
        local font=self.fonts and self.fonts[math.floor(size+.5)]
        if font then
            local ok,w,h=pcall(function() return font:measure(tostring(text or "")) end)
            if ok and type(w)=="number" then return w,type(h)=="number" and h or size*1.4 end
        end
        local width,line,rows=0,0,1
        for _,c in utf8.codes(tostring(text or "")) do
            if c==10 then width=math.max(width,line);line=0;rows=rows+1
            else line=line+(c>255 and size or size*.55) end
        end
        return math.max(width,line),size*1.4*rows
    end
    function u:button(id,label,x,y,w,h,kind,enabled)
        enabled=enabled~=false; h=h or 34*self.scale
        if x then self:at(x,y) else x,y=self:pos() end
        local clicked=real.invisible_button(id,{w,h})
        local hover=real.is_item_hovered and real.is_item_hovered()
        local active=real.is_item_active and real.is_item_active()
        local color=kind=="primary" and C.accent or kind=="selected" and 0xFF423A73 or kind=="warning" and 0xFF694134 or C.raised
        if hover and enabled then color=kind=="primary" and 0xFF988CFF or kind=="selected" and 0xFF53498F or kind=="warning" and 0xFF855342 or 0xFF2E3C55 end
        if active and enabled then color=0xFF514894 end
        self:rect(x,y,w,h,color,6*self.scale)
        if kind=="selected" then self:rect(x+2*self.scale,y+7*self.scale,3*self.scale,h-14*self.scale,C.cyan,1.5*self.scale) end
        local tw,th=self:measure(label,14*self.scale)
        local padding=math.min(10*self.scale,w*.18)
        self:label(label,x+math.max(padding,(w-tw)/2),y+(h-th)/2,14*self.scale,
            enabled and C.text or C.muted,w-padding*2)
        return enabled and clicked
    end
    function u:textbox(id,value,width,prefix)
        width=width or self.next_width or 260*self.scale;self.next_width=nil
        local x,y=self:pos()
        local h=30*self.scale
        local lead=prefix and prefix.."  " or ""
        local lead_width=math.min(self:measure(lead,14*self.scale),math.max(0,width-26*self.scale))
        self:at(x+lead_width,y)
        real.set_next_item_width(math.max(1,width-lead_width))
        local style=type(real.ImGuiStyleVar)=="table" and real.ImGuiStyleVar or {}
        real.push_style_var(style.Alpha or 0,1.0)
        real.push_style_var(style.FramePadding or 11,Vector2f.new(9*self.scale,8*self.scale))
        real.push_style_var(style.FrameBorderSize or 13,0.0)
        -- The workbench hides native chrome. Restore only text here; leave its
        -- clipping/cursor/selection to InputText, with D2D painting the frame.
        local ink=argb(C.text)
        real.push_style_color(0,ink>=0x80000000 and ink-0x100000000 or ink)
        for _,color in ipairs({5,6,7,8,9}) do real.push_style_color(color,0) end
        real.push_font_size(14*self.scale)
        -- Do not reconstruct a caret from REFramework's two selection offsets:
        -- they do not contain CursorPos, and CallbackResize can overwrite them
        -- with zero on the same keystroke. The native editor has the full state.
        -- A hidden label also prevents a visible parameter name outside its box.
        local native_id=tostring(id):sub(1,2)=="##" and id or ("##"..tostring(id))
        local ok,changed,next_value=pcall(real.input_text,native_id,tostring(value or ""))
        real.pop_font_size()
        real.pop_style_color(6)
        real.pop_style_var(3)
        if not ok then error(changed,0) end
        local active=real.is_item_active()
        self:rect(x,y,width,h,C.input,5*self.scale)
        self:rect(x,y+h-1,width,1,active and C.accent or C.line)
        if prefix then self:label(prefix,x+9*self.scale,y+6*self.scale,14*self.scale,C.muted,lead_width) end
        if active then
            self.text_active=true
        end
        -- Normalize vertical spacing independently of the underlying ImGui font.
        real.set_cursor_screen_pos({real.get_cursor_screen_pos().x,y+h+7*self.scale})
        return changed,next_value
    end
    function u:panel(id,x,y,w,h,fn)
        self:rect(x,y,w,h,C.panel,10*self.scale)
        self:at(x+12*self.scale,y+10*self.scale)
        local visible=real.begin_child_window("##mvl_d2d_"..id,{w-24*self.scale,h-20*self.scale},false)
        self.clips[#self.clips+1]={x=x+12*self.scale,y=y+10*self.scale,w=w-24*self.scale,h=h-20*self.scale}
        local ok,err=true,nil
        if visible~=false then ok,err=pcall(fn) end
        self.clips[#self.clips]=nil;real.end_child_window()
        if not ok then error(err,0) end
    end
    function u:heading(title,subtitle)
        local x,y=self:pos()
        self:label(title,x,y,22*self.scale,C.text,real.get_window_size().x-24*self.scale)
        self:space(36*self.scale)
        if subtitle then
            local x,y=self:pos();self:label(subtitle,x,y,12*self.scale,C.muted,real.get_window_size().x-24)
            self:space(22*self.scale)
        end
    end
    function u:row(label,value)
        local x,y=self:pos()
        self:label(label,x,y,13*self.scale,C.muted,170*self.scale)
        self:label(value,x+180*self.scale,y,13*self.scale,C.text,400*self.scale)
        self:space(24*self.scale)
    end
    function u:render()
        if not self.fonts then return end
        for _,c in ipairs(self.commands) do
            if c.kind=="rect" then
                api.fill_rounded_rect(c.x+0.0,c.y+0.0,c.w+0.0,c.h+0.0,c.r+0.0,c.r+0.0,c.color)
            elseif c.kind=="text" then
                local key=math.floor(c.size+.5);local font=self.fonts[key] or self.fonts[15]
                local s=c.text;local max=c.maxWidth
                if max and max>0 then
                    local w=font:measure(s)
                    if w>max then
                        local offsets={};for p in utf8.codes(s) do offsets[#offsets+1]=p end
                        while #offsets>0 and font:measure(s.."…")>max do s=s:sub(1,table.remove(offsets)-1) end
                        s=s.."…"
                    end
                end
                if not max or max>4 then api.text(font,s,c.x+0.0,c.y+0.0,c.color) end
            elseif c.kind=="image" then
                local image=self:image(c.path)
                if image then api.image(image,c.x+0.0,c.y+0.0,c.w+0.0,c.h+0.0) end
            elseif c.kind=="line" then
                api.line(c.x+0.0,c.y+0.0,c.x2+0.0,c.y2+0.0,1.5,c.color)
            end
        end
    end
    function u:init()
        self.fonts={};self.images={};self.image_order={}
        for size=6,44 do self.fonts[size]=api.Font.new("Microsoft YaHei UI",size,false,false) end
    end
    function u:image(path)
        if self.images[path] then
            for i,p in ipairs(self.image_order) do if p==path then table.remove(self.image_order,i);break end end
            self.image_order[#self.image_order+1]=path
            return self.images[path]
        end
        local ok,img=pcall(api.Image.new,path)
        if not ok or not img then return nil end
        self.images[path]=img;self.image_order[#self.image_order+1]=path
        -- Keep replay GPU memory bounded; old bitmap references are released by Lua GC.
        while #self.image_order>12 do self.images[table.remove(self.image_order,1)]=nil end
        return img
    end
    function u:begin(scale)
        self.commands={};self.serial=0;self.text_active=false;self.scale=scale or 1;self.tree_depth=0
        self.clips={};self.colors={};self.next_width=nil;self.next_button_kind=nil
    end
    a.get_window_size=real.get_window_size
    a.get_window_pos=real.get_window_pos
    a.get_cursor_pos=real.get_cursor_pos
    a.get_cursor_screen_pos=real.get_cursor_screen_pos
    a.get_display_size=real.get_display_size
    a.get_default_font_size=function() return 15*u.scale end
    a.calc_text_size=function(s) local w,h=u:measure(s,15*u.scale);return {x=w,y=h} end
    a.is_item_active=real.is_item_active
    a.get_clipboard=real.get_clipboard
    a.same_line=real.same_line
    a.set_next_item_width=function(w) u.next_width=w end
    a.push_style_color=function(id,color) u.colors[#u.colors+1]={id=id,color=argb(color)} end
    a.pop_style_color=function(n) for _=1,n or 1 do table.remove(u.colors) end end
    a.push_font_size=function() end;a.pop_font_size=function() end
    a.text=function(s)
        local x,y=u:pos();local size=15*u.scale;local color=C.text
        for i=#u.colors,1,-1 do if u.colors[i].id==0 then color=u.colors[i].color;break end end
        local count=0
        for line in (tostring(s).."\n"):gmatch("(.-)\n") do
            u:label(line,x,y+count*size*1.45,size,color,real.get_window_size().x-28*u.scale);count=count+1
        end
        u:space(math.max(1,count)*size*1.45+3*u.scale)
    end
    a.set_button_kind=function(kind) u.next_button_kind=kind end
    a.button=function(label)
        local kind=u.next_button_kind;u.next_button_kind=nil
        local text=caption(label);local w,h=u:measure(text,14*u.scale)
        return u:button(label,text,nil,nil,math.min(real.get_window_size().x-26*u.scale,math.max(68*u.scale,w+24*u.scale)),math.max(34*u.scale,h+14*u.scale),kind)
    end
    a.input_text=function(label,value)
        local shown=caption(label)
        if shown=="R" or shown=="G" or shown=="B" or shown=="A" or shown=="X" or shown=="Y" or shown=="Z" or
            shown=="普通值" or shown=="随机值" or shown=="Base" or shown=="Random" then
            return u:textbox(label,value,u.next_width or 64*u.scale,shown)
        end
        if shown~="" then a.text(shown) end
        return u:textbox(label,value,u.next_width or math.min(380*u.scale,real.get_window_size().x-28*u.scale))
    end
    a.checkbox=function(label,value)
        local x,y=u:pos();local w=u:measure(caption(label),14*u.scale)
        local click=real.invisible_button(label,{math.min(w+35*u.scale,real.get_window_size().x-26*u.scale),28*u.scale})
        u:rect(x,y+3*u.scale,18*u.scale,18*u.scale,value and C.accent or C.input,4*u.scale)
        if value then u:label("✓",x+3*u.scale,y+2*u.scale,14*u.scale,C.text) end
        u:label(caption(label),x+28*u.scale,y+2*u.scale,14*u.scale,C.text)
        if click then return true,not value end
        return false,value
    end
    a.set_next_item_open=function(value) u.next_tree=value end
    a.tree_node=function(label)
        local key=label:match("###(.+)$") or label:match("##(.+)$") or label
        if u.next_tree~=nil then u.trees[key]=u.next_tree;u.next_tree=nil end
        local open=u.trees[key]==true;local x,y=u:pos()
        local click=real.invisible_button(label,{math.max(60,real.get_window_size().x-36*u.scale-u.tree_depth*18*u.scale),32*u.scale})
        if click then open=not open;u.trees[key]=open end
        u:label(open and "⌄" or "›",x,y+3*u.scale,16*u.scale,C.accent)
        u:label(caption(label),x+20*u.scale,y+5*u.scale,14*u.scale,C.text)
        if open then real.indent(18*u.scale);u.tree_depth=u.tree_depth+1 end
        return open
    end
    a.tree_pop=function() real.unindent(18*u.scale);u.tree_depth=u.tree_depth-1 end
    a.separator=function()
        local x,y=u:pos();u:rect(x,y+4*u.scale,real.get_window_size().x-28*u.scale,1,C.line);u:space(13*u.scale)
    end
    a.begin_child_window=function(label,size,border)
        local x,y=u:pos();local w=size[1]>0 and size[1] or real.get_window_size().x-28*u.scale
        local h=size[2]>0 and size[2] or 320*u.scale
        local visible=real.begin_child_window(label,{w,h},false)
        u.clips[#u.clips+1]={x=x,y=y,w=w,h=h};return visible
    end
    a.end_child_window=function() table.remove(u.clips);real.end_child_window() end
    a.combo=function(label,value,items)
        local changed=false
        if a.tree_node(caption(label)..": "..tostring(items[value] or "").."##"..label) then
            for i,item in ipairs(items) do if a.button(item.."##"..label..i) then changed=true;value=i end end
            a.tree_pop()
        end
        return changed,value
    end
    a.color_edit4=function(label,value)
        local rgba={value.x,value.y,value.z,value.w};local x,y=u:pos()
        local click=real.invisible_button(label,{190*u.scale,34*u.scale})
        if rgba[4]<1 then
            for row=0,1 do for column=0,10 do
                u:rect(x+column*17.3*u.scale,y+row*17*u.scale,17.3*u.scale,17*u.scale,
                    (row+column)%2==0 and 0xFF8E96A6 or 0xFF4C5669)
            end end
        end
        u:rect(x,y,190*u.scale,34*u.scale,(math.floor(rgba[4]*255)<<24) | (math.floor(rgba[1]*255)<<16) | (math.floor(rgba[2]*255)<<8) | math.floor(rgba[3]*255),5*u.scale)
        u:label("调色",x+8*u.scale,y+7*u.scale,13*u.scale,(rgba[1]+rgba[2]+rgba[3])>1.6 and C.bg or C.text)
        if click then u.color_open=u.color_open==label and nil or label end
        local changed=false
        if u.color_open==label then
            for row=0,1 do
                local sx,sy=u:pos()
                for col=0,11 do
                    local hue=col/12
                    local function channel(n) local k=(n+hue*6)%6;return (1-math.max(0,math.min(k,4-k,1)))*(row==0 and 1 or .55)+ (row==0 and 0 or .25) end
                    local r,g,b=channel(5),channel(3),channel(1)
                    local px=sx+col*23*u.scale
                    u:at(px,sy)
                    local picked=real.invisible_button("##palette"..label..row.."_"..col,{21*u.scale,23*u.scale})
                    u:rect(px,sy,21*u.scale,21*u.scale,0xff000000 | (math.floor(r*255)<<16) | (math.floor(g*255)<<8) | math.floor(b*255),3*u.scale)
                    if picked then rgba[1],rgba[2],rgba[3]=r,g,b;changed=true end
                end
                u:at(sx,sy+27*u.scale)
            end
            for i,name in ipairs({"R","G","B","A"}) do
                local sx,sy=u:pos();local width=260*u.scale
                real.invisible_button("##channel"..label..i,{width,22*u.scale})
                u:rect(sx,sy+6*u.scale,width,7*u.scale,C.raised,3*u.scale)
                u:rect(sx,sy+6*u.scale,width*rgba[i],7*u.scale,({0xFFFF748A,0xFF70DCB4,0xFF78A6FF,0xFFE6EDF9})[i],3*u.scale)
                if real.is_item_active() and real.get_mouse then
                    rgba[i]=math.max(0,math.min(1,(real.get_mouse().x-sx)/width));changed=true
                end
                local edit,s=a.input_text(name.."##picker"..label..i,tostring(math.floor(rgba[i]*255+.5)))
                if edit and tonumber(s) then rgba[i]=math.max(0,math.min(255,tonumber(s)))/255;changed=true end
            end
        end
        return changed,Vector4f.new(rgba[1],rgba[2],rgba[3],rgba[4])
    end
    a.get_window_draw_list=function()
        return {add_line=function(_,p1,p2,color)
            -- Curve drawing API compatibility: D2D uses a compact line command.
            u:emit("line",{x=p1.x or p1[1],y=p1.y or p1[2],x2=p2.x or p2[1],y2=p2.y or p2[2],h=1,color=argb(color)})
        end}
    end
    return u
end
return M
