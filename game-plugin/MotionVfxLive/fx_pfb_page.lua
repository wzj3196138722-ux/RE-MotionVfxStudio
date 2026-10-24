local M={}
function M.new(core,u,send,button)
    local self={values={},filter="",resource="",target_path=""}
    local function accept(r)
        if r.ok and r.pfbDocument then
            self.document=r.pfbDocument;self.values={}
            local found=false
            for _,slot in ipairs(self.document.slots or {})do if slot.key==self.selected then found=true end end
            if not found then self.selected=nil end
        end
    end
    self.accept=accept
    function self:open(path,kind,callback)
        send("pfb_open",{path=path,sourceKind=kind or "local_file"},function(r)
            if r.ok then self.selected=nil;self.target=nil;self.target_path=kind=="game_files" and path or "" end
            accept(r);if callback then callback(r)end
        end)
    end
    local function mutate(action,data)
        data=data or {};data.pfbRevision=self.document.revision
        send(action,data,accept)
    end
    local function input(id,label,value,width)
        u.adapter.text(label)
        local changed,text=u:textbox("##pfb_"..id,value or "",math.min(width or 440*u.scale,u.real.get_window_size().x-32*u.scale))
        return changed and text or value
    end
    function self:draw()
        local s=core.state;local doc=self.document
        u:heading("PFB 编辑","从实际组件识别装备与特效类型；编辑与 EFX、动作文档分别保留。")
        button("pfb_open","打开本地 PFB",170,function()send("desktop_start",{mode="pick_pfb"})end,"primary",not s.pending)
        self.game_path=input("game_path","或填写完整游戏 PFB 路径（art/ 或 gamedesign/）",self.game_path)
        button("pfb_game_open","读取游戏内 PFB",190,function()self:open(self.game_path,"game_files")end,nil,not s.pending and (self.game_path or "")~="")
        if not doc then return end
        u.adapter.separator()
        u:heading(doc.name,({equipment="装备 PFB",effect="特效 PFB",unknown="未识别用途的 PFB"})[doc.type] or tostring(doc.type))
        u.adapter.text(doc.path);u.adapter.text(doc.dirty and "有未导出的修改 · 原文件未改动" or "与打开时一致")
        button("pfb_undo","撤销",90,function()mutate("pfb_undo")end,nil,doc.canUndo and not s.pending)
        button("pfb_redo","重做",90,function()mutate("pfb_redo")end,nil,doc.canRedo and not s.pending)
        button("pfb_export","导出 PFB 文件",180,function()send("begin_export",{scope="pfb"})end,"primary",not s.pending)
        self.target_path=input("target_path","热重载目标游戏路径（本地文件需指定其实际被使用的 PFB 路径）",self.target_path)
        button("pfb_preview","核实目标并热重载",220,function()core.preview_pfb(doc,self.target_path)end,"primary",not s.pending and core.preview_pfb~=nil and self.target_path~="")
        button("pfb_restore","还原游戏中的 PFB",200,function()core.restore_pfb_preview()end,nil,core.restore_pfb_preview~=nil)
        local preview=core.pfb_preview_status and core.pfb_preview_status() or {}
        if preview.message then u.adapter.text(preview.message)end
        if doc.type=="equipment" then
            u.adapter.separator();u:heading("装备资源槽位","特效槽位填写特效 PFB 路径；物理与约束填写对应资源路径。")
            local targets,selected={},1
            for i,target in ipairs(doc.targets or {})do targets[i]=target.label;if target.key==self.target then selected=i end end
            if #targets>0 then
                local changed,index=u.adapter.combo("目标对象##pfb_target",selected,targets)
                if changed or not self.target then self.target=doc.targets[index].key end
            end
            self.resource=input("add_resource","资源游戏路径",self.resource)
            for _,kind in ipairs(doc.addKinds or {})do
                button("pfb_add_"..kind.kind,kind.label,240,function()
                    mutate("pfb_add",{kind=kind.kind,resourcePath=self.resource,targetKey=self.target})
                end,nil,not s.pending and self.resource~="")
            end
        elseif doc.type=="effect" then
            u.adapter.text("选择已有槽位后复制；可打开另一个特效 PFB，再选目标槽位所在容器并粘贴。")
            button("pfb_paste","粘贴特效槽位",190,function()mutate("pfb_paste",{slotKey=self.selected})end,"primary",doc.canPaste and not s.pending)
        else u.adapter.text("未识别的类型保留原始内容；不套用装备或特效的新增操作。")end
        u.adapter.separator();self.filter=input("filter","筛选槽位或资源路径",self.filter)
        for _,slot in ipairs(doc.slots or {})do
            if (slot.label.." "..(slot.resourcePath or "")):lower():find((self.filter or ""):lower(),1,true) then
                button("pfb_slot_"..slot.key,slot.label,600,function()self.selected=slot.key;self.values={}end,self.selected==slot.key and "selected")
                if slot.key==self.selected then
                    if slot.resourcePath and slot.resourcePath~="" then u.adapter.text(slot.resourcePath)end
                    if doc.type=="effect" then button("pfb_copy","复制此槽位",150,function()mutate("pfb_copy",{slotKey=slot.key})end,nil,not s.pending)end
                    button("pfb_remove","删除此槽位",150,function()mutate("pfb_remove",{slotKey=slot.key})end,"warning",not s.pending)
                    for _,field in ipairs(slot.fields or {})do
                        if field.editable then
                            local key=slot.key..":"..field.path;local value=self.values[key]
                            if value==nil then value=field.value end
                            u.adapter.text(field.label or field.path)
                            if field.type=="bool" then
                                local changed,checked=u.adapter.checkbox("启用##pfb_"..key,value==true or value=="true")
                                if changed then self.values[key]=checked end
                            elseif type(value)=="table" then
                                local copy={};for i,n in ipairs(value)do copy[i]=n end
                                for i,n in ipairs(copy)do
                                    local changed,text=u:textbox("##pfb_"..key.."_"..i,tostring(n),145*u.scale)
                                    if changed then copy[i]=text;self.values[key]=copy end
                                end
                            else
                                local changed,text=u:textbox("##pfb_"..key,tostring(value or ""),math.min(440*u.scale,u.real.get_window_size().x-32*u.scale))
                                if changed then self.values[key]=text end
                            end
                            button("pfb_field_"..key,"提交此参数",135,function()
                                local submitted=self.values[key];if submitted==nil then submitted=field.value end
                                mutate("pfb_set",{slotKey=slot.key,fieldPath=field.path,value=submitted})
                            end,nil,not s.pending and self.values[key]~=nil)
                        end
                    end
                end
            end
        end
    end
    return self
end
return M
