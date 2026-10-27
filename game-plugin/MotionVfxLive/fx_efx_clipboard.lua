-- Explicit editor copy/paste; never reads or replaces the OS text clipboard.
local M = {}
function M.new(h)
    local self={}
    local s=h.state
    local function find(nodes,index)
        for _,node in ipairs(nodes or {})do if node.index==index then return node end end
    end
    local function container(target)
        local doc=s.document
        for _,hop in ipairs(target and target.embeddedPath or {})do
            local owner=find(doc and (hop.kind=="entry" and doc.entries or doc.actions),hop.index)
            local attr=find(owner and owner.attributes,hop.attributeIndex)
            doc=attr and attr.embedded
        end
        local owner=target and find(doc and (target.kind=="entry" and doc.entries or doc.actions),target.index)
        return doc,owner
    end
    local function ready()
        if s.pending then return false,"正在处理，请稍候。" end
        if h.has_drafts() then return false,"有未提交的参数，请先提交或还原输入，再复制或粘贴。" end
        return s.document~=nil,"先打开一个 EFX 文件。"
    end
    local function copy(target)
        local ok,reason=ready();if not ok then s.copy_notice=reason;return end
        h.send("copy_efx",{target=h.copy_target(target)},function(response)
            self.confirm=nil
            s.copy_notice=response.ok and "已复制。选择目标 Group、特效块或属性后，点击下方粘贴。" or "复制失败："..tostring(response.error)
        end)
    end
    local function paste(target,replaceIndex)
        local ok,reason=ready();if not ok then s.copy_notice=reason;return end
        local buffer=s.copy_buffer;if not buffer then return end
        h.send("paste_efx",{target=h.copy_target(target),clipboardId=buffer.id,
            replace=replaceIndex~=nil,replaceAttributeIndex=replaceIndex},function(response)
            self.confirm=nil
            s.copy_notice=response.ok and (response.message or "已粘贴，可用撤销恢复。") or "粘贴失败："..tostring(response.error)
            if response.ok and response.selection then
                s.structure_filter="";s.field_filter="";s.field_modified_only=false
                s.copy_reveal=true
                h.inspect(response.selection,{})
            end
        end)
    end
    function self:draw()
        if not s.document then return end
        local selected=s.selected or {kind="document",index=0}
        local buffer=s.copy_buffer
        if self.document~=s.document or not h.same_target(self.selected,selected) or self.buffer~=buffer or self.language~=s.parameter_language then
            self.confirm=nil;self.document=s.document;self.selected=h.copy_target(selected);self.buffer=buffer;self.language=s.parameter_language
            local doc,owner=container(selected)
            self.owner=owner;self.target=h.copy_target(selected)
            self.destination=nil;self.replace_index=nil;self.blocked=nil
            if buffer and buffer.kind=="entry" then
                if selected.kind=="group" then self.destination=h.title(selected)
                elseif selected.kind=="entry" and owner then
                    local names={}
                    for _,g in ipairs(doc.groups or {})do
                        for _,index in ipairs(g.entries or {})do if index==selected.index then names[#names+1]="Group ["..g.index.."] "..tostring(g.displayName or g.name);break end end
                    end
                    self.destination=(selected.embeddedPath and #selected.embeddedPath>0 and "当前内嵌特效 / " or "")..
                        (#names>0 and table.concat(names,"、") or "未分组").." · 新建副本"
                elseif selected.kind=="document" then self.destination="当前文件 / 未分组 · 新建副本"
                else self.blocked="请选择目标 Group 或特效块。" end
            elseif buffer then
                if owner and (selected.kind=="entry" or selected.kind=="action") then
                    local ownerTarget=h.copy_target(selected);ownerTarget.attributeIndex=nil
                    self.destination=h.title(ownerTarget)
                    local matches={}
                    for _,attr in ipairs(owner.attributes or {})do if attr.type==buffer.attributeType then matches[#matches+1]=attr.index end end
                    if #matches==1 then self.replace_index=matches[1]
                    elseif #matches>1 then
                        for _,index in ipairs(matches)do if index==selected.attributeIndex then self.replace_index=index end end
                        if not self.replace_index then self.blocked="存在多个同类型属性，请先选中要替换的那一项。" end
                    end
                else self.blocked="请选择接收属性的特效块或 Action。" end
            end
            if buffer and doc and doc.version and buffer.version and doc.version~=buffer.version then self.blocked="来源与目标 EFX 版本不同。" end
        end
        local available,reason=ready()
        h.text("复制 / 粘贴",h.palette.accent)
        local owner=h.copy_target(selected);owner.attributeIndex=nil
        h.row({
            {"复制特效块##mvl_copy_entry",function()copy(owner)end,available and selected.kind=="entry" and self.owner and "normal" or "disabled"},
            {"复制此属性##mvl_copy_attribute",function()copy(selected)end,available and self.owner and selected.attributeIndex~=nil and "normal" or "disabled"}
        })
        if not buffer then
            h.text("先选择来源并复制，再选择目标粘贴。",h.palette.muted)
        else
            h.text("已复制「"..(buffer.kind=="entry" and "特效块" or "属性").."」 "..tostring(buffer.name),h.palette.accent)
            h.text("来源："..tostring(buffer.sourceName or "当前文件"),h.palette.muted)
            h.text(self.destination and ("粘贴到："..self.destination) or self.blocked,h.palette.muted)
            local label=buffer.kind=="entry" and "粘贴为新特效块" or self.replace_index and "替换同类型属性…" or "粘贴属性到此块"
            h.row({{label.."##mvl_paste_efx",function()
                if self.replace_index then self.confirm={id=buffer.id,target=h.copy_target(selected),revision=s.document.revision,index=self.replace_index}
                else paste(selected)end
            end,available and not self.blocked and "primary" or "disabled"},
            {"清空已复制内容##mvl_clear_efx_copy",function()
                h.send("clear_efx_clipboard",{},function(r)if r.ok then self.confirm=nil;s.copy_notice=nil end end)
            end,s.pending and "disabled" or nil}})
            if self.confirm then
                h.text("将替换目标中的 "..tostring(buffer.attributeType).."，原参数会被覆盖。可用撤销恢复。",h.palette.warning)
                h.row({{"确认替换属性##mvl_confirm_paste_attribute",function()
                    local c=self.confirm
                    if c and c.id==buffer.id and c.revision==s.document.revision and h.same_target(c.target,selected) then paste(c.target,c.index)end
                end,available and "warning" or "disabled"},
                {"取消替换##mvl_cancel_paste_attribute",function()self.confirm=nil end}})
            end
        end
        if not available and (s.pending or h.has_drafts()) then h.text(reason,h.palette.warning)end
        if s.copy_notice then h.text(s.copy_notice,h.palette.muted)end
        h.ui().separator()
    end
    return self
end
return M
