local M={}
function M.new(core,widgets,pfb_factory,file_dialog_factory)
    local s=core.state
    local real=imgui
    local u=widgets.new(real,d2d)
    local C=widgets.colors
    local self={active=true,page="actions",top=nil,cache={},imports={},filter="",resource_filter="",
        cache_alias="",scale=1,replay_index=1,replay_speed=0,replay_clock=0,replay_rate=1,replay=nil,
        desktop_seen=nil,desktop_jobs={},cache_status={},last_poll=0,cache_epoch=0,record_seconds=15,
        window_position={x=.5,y=.5},window_size={w=1380,h=830},orb_side="right",orb_t=.45,
        document_generation=s.document_open_generation or 0,local_path="",motion_filter="",action_source="live",
        slot_lifetime_mode="independent",slot_lifetime="120",action_groups={}}
    do
        local ok,groups=pcall(json.load_file,"MotionVfxLive/action-groups.json")
        if ok and type(groups)=="table" and groups.schema==1 and type(groups.groups)=="table" then self.action_groups=groups.groups end
    end
    local function clamp(v,a,b) return math.max(a,math.min(b,v)) end
    local style=type(real.ImGuiStyleVar)=="table" and real.ImGuiStyleVar or {}
    local alpha_style=style.Alpha or 0
    function self:save_settings()
        local ok,result=pcall(json.dump_file,"MotionVfxLive/workbench-settings.json",{
            scale=self.scale,language=s.parameter_language,position=self.window_position,size=self.window_size,orbSide=self.orb_side,orbT=self.orb_t})
        if not ok or result==false then s.message="界面设置保存失败，本次会话仍保留当前设置。" end
    end
    function self:hide() s.window_hidden=true;self.drag=nil;self.resize=nil;self.replay_speed=0 end
    function self:show() s.window_open=true;s.window_hidden=false;self.drag=nil;self.orb_free=nil end
    -- Effect-list scope is independent of the action retained for slot editing.
    function self:reset_effect_filter(notice)
        self.effect_action_id=nil;self.resource_filter="";self.effect_notice=notice
    end
    local function terminal(phase)return phase=="complete" or phase=="error" or phase=="failed" or phase=="cancelled"end
    function self:recording_active()
        return self.desktop and self.desktop.mode=="record" and not terminal(self.desktop.phase) or false
    end
    function self:load_replay(replay)
        if type(replay)~="table" or type(replay.frames)~="table" or #replay.frames==0 then
            self.replay_notice="没有可回看的录像。动作／特效记录保存调用信息；需要单独录制画面才能重播。";return false
        end
        for _,frame in ipairs(replay.frames)do
            if type(frame)~="table" or type(frame.path)~="string" or type(frame.time)~="number" then
                self.replay_notice="录像索引不完整，未替换当前回看片段。";return false
            end
        end
        self.replay=replay;self.replay_index=1;self.replay_speed=0;self.replay_elapsed=0;self.replay_clock=os.clock()
        self.replay_notice="录像已就绪，点击「正放重播」或「倒放」查看。";return true
    end
    function self:read_last_replay()
        local ok,replay=pcall(json.load_file,"MotionVfxLive/last-replay.json")
        return self:load_replay(ok and replay or nil)
    end
    -- An invisible title hit target owns dragging; controls never move the host.
    local function drag_handle(id,x,y,w,h,kind,sw,sh,window_w,window_h)
        u:at(x,y);local clicked=real.invisible_button(id,{w,h})
        local active=real.is_item_active and real.is_item_active()
        local mouse=real.get_mouse and real.get_mouse()
        if active and mouse then
            if not self.drag then self.drag={id=id,mx=mouse.x,my=mouse.y,x=x,y=y,moved=false} end
            if self.drag.id==id then
                local drag=self.drag;local dx,dy=mouse.x-drag.mx,mouse.y-drag.my
                drag.moved=drag.moved or dx*dx+dy*dy>16
                if drag.moved then
                    if kind=="window" then
                        self.window_position={x=clamp((drag.x+dx)/math.max(1,sw-window_w),0,1),y=clamp((drag.y+dy)/math.max(1,sh-window_h),0,1)}
                    else self.orb_free={x=clamp(drag.x+dx,0,sw-w),y=clamp(drag.y+dy,0,sh-h)} end
                end
            end
        elseif self.drag and self.drag.id==id then
            local moved=self.drag.moved
            if kind=="orb" and self.orb_free then
                local p=self.orb_free
                local distances={{"left",p.x},{"right",sw-w-p.x},{"top",p.y},{"bottom",sh-h-p.y}}
                table.sort(distances,function(a,b)return a[2]<b[2] end)
                self.orb_side=distances[1][1]
                self.orb_t=(self.orb_side=="left" or self.orb_side=="right") and p.y/math.max(1,sh-h) or p.x/math.max(1,sw-w)
            end
            self.drag=nil;self.orb_free=nil
            if moved then self:save_settings();return false end
        end
        return clicked and not (self.drag and self.drag.moved)
    end
    local function orb(sw,sh,interactive,k)
        local size=56*k
        local x,y=0,(sh-size)*self.orb_t
        if self.orb_side=="right" then x=sw-size
        elseif self.orb_side=="top" or self.orb_side=="bottom" then x=(sw-size)*self.orb_t;y=self.orb_side=="top" and 0 or sh-size end
        if self.orb_free then x,y=self.orb_free.x,self.orb_free.y end
        local hovered=false
        if interactive then
            -- D2D remains visible even when another script/theme leaves native
            -- Alpha at zero. ImGui then skips hit testing on the entire host.
            -- Own our host style, without changing REFramework's global style.
            real.push_style_var(alpha_style,1.0)
            real.push_style_var(style.WindowPadding or 2,Vector2f.new(0,0))
            real.push_style_var(style.WindowBorderSize or 4,0.0)
            real.push_style_var(style.WindowMinSize or 5,Vector2f.new(1,1))
            real.set_next_window_pos({x,y});real.set_next_window_size({size,size})
            for _,id in ipairs({0,2,3,5}) do real.push_style_color(id,0) end
            real.begin_window("##MotionVfxD2DOrbHost",true,1+2+4+8+16+32+128+256)
            local ok,err=pcall(function()
                -- Use the native host's actual coordinates, including its
                -- integer rounding; the picture and button must coincide.
                local p=real.get_window_pos();x,y=p.x,p.y
                if real.set_scroll_x then real.set_scroll_x(0) end
                if real.set_scroll_y then real.set_scroll_y(0) end
                if drag_handle("##mvl_logo_orb",x,y,size,size,"orb",sw,sh) then self:show() end
                hovered=real.is_item_hovered and real.is_item_hovered() or false
            end)
            real.end_window();real.pop_style_color(4);real.pop_style_var(4)
            if not ok then error(err,0) end
        end
        self.orb_status={x=x,y=y,size=size,interactive=interactive,hovered=hovered}
        u:rect(x,y,size,size,hovered and C.accent or C.line,size/2)
        u:rect(x+2*k,y+2*k,size-4*k,size-4*k,C.sidebar,(size-4*k)/2)
        u:emit("image",{path="MotionVfxLive/logo.png",x=x+8*k,y=y+8*k,w=size-16*k,h=size-16*k})
        if s.capture_snapshot.recording or self:recording_active() then u:rect(x+size-12*k,y+9*k,6*k,6*k,C.warning,3*k) end
    end
    local function adapter(fn) return core.with_ui(u.adapter,fn) end
    local function call(fn) if not s.ai_busy and not s.reloading then return fn() end end
    local function send(action,data,callback)
        if action=="desktop_start" and data and ({pick=true,pick_efx=true,pick_motion=true,pick_pfb=true})[data.mode] then
            return call(function()return self:choose_file(data.mode)end)
        end
        if action=="begin_export" then return call(function()return self:begin_export(data and data.scope or "efx")end)end
        return call(function() return core.send(action,data,function(response)
            local job=response.ok and response.desktop
            if job and job.id and job.statusFile then
                self.desktop_jobs[job.id]=job
                if job.mode=="record" then self.desktop={id=job.id,mode="record",phase="starting",message="录屏正在启动；请回到游戏，等待倒计时。"}end
            end
            if callback then callback(response) end
        end) end)
    end
    if file_dialog_factory then self.file_dialog=file_dialog_factory.new(core,u,send)end
    function self:choose_file(mode)
        if not self.file_dialog then s.message="游戏内文件选择模块未安装，请更新编辑器。";return false end
        if self.file_dialog.active or self.export_plan then s.message="请先完成或取消当前文件选择。";return false end
        if s.open_confirmation or self.open_pending then s.message="请先完成当前文档切换。";return false end
        local kind=({pick="assets",pick_efx="efx",pick_motion="motion",pick_pfb="pfb"})[mode]
        self.file_dialog:open({kind=kind,multiple=mode=="pick",title=({pick="选择外部资源",pick_efx="打开特效文件",pick_motion="打开动作文件",pick_pfb="打开 PFB 文件"})[mode],
            on_select=function(paths)
                if mode=="pick" then self.selected_files=paths;self.top="cache"
                elseif paths[1] then self:queue_open(paths[1])end
            end})
        return true
    end
    function self:begin_export(scope)
        if not self.file_dialog then s.message="游戏内文件选择模块未安装，请更新编辑器。";return false end
        if self.file_dialog.active or self.export_plan then s.message="请先完成或跳过当前导出。";return false end
        if scope=="efx" and core.has_drafts()then s.message="请先提交或还原输入，再导出。";return false end
        return send("prepare_export",{scope=scope},function(r)
            if r.ok then self.export_plan={token=r.exportRequest.exportToken,items=r.exportRequest.items,index=1,destinations={}}end
        end)
    end
    local function next_export_dialog()
        local plan=self.export_plan
        if not plan or self.file_dialog.active or s.pending then return end
        local item=plan.items[plan.index]
        if not item then
            self.export_pending={exportToken=plan.token,destinations=plan.destinations};self.export_plan=nil;return
        end
        self.file_dialog:open({kind="all",save=true,name=item.name,title="导出 "..plan.index.." / "..#plan.items.." · "..item.label,
            cancel_label="跳过此文件",on_cancel=function()plan.index=plan.index+1 end,
            on_select=function(destination)
                plan.destinations[#plan.destinations+1]={key=item.key,path=destination.path,expectedHash=destination.expectedHash}
                plan.index=plan.index+1
            end})
    end
    -- Keep explicit open intents until the mailbox is available. A file-picker
    -- result must not disappear merely because another request is still running.
    function self:queue_open(path,resource)
        if s.open_confirmation then s.message="请先确认或取消当前文档切换。";return false end
        path=type(path)=="string" and path:match("^%s*(.-)%s*$") or ""
        if path:sub(1,1)=='"' and path:sub(-1)=='"' then path=path:sub(2,-2) end
        local lower=path:lower()
        local kind=resource and "captured" or (lower:match("%.efx%.?%d*$") and "efx") or
            ((lower:match("%.mot%.?%d*$") or lower:match("%.motlist%.?%d*$")) and "motion") or (lower:match("%.pfb%.?%d*$") and "pfb")
        if not kind then s.message="请选择 .efx、.mot、.motlist 或 .pfb 文件（支持版本号后缀）。";return false end
        self.open_pending={path=path,resource=resource,kind=kind}
        s.message="等待打开："..path
        return true
    end
    local function open_pending()
        local choice=self.open_pending
        if not choice or s.pending or s.apply_requested or s.open_confirmation then return end
        self.open_pending=nil
        if choice.kind=="pfb" then
            if not self.pfb then s.message="PFB 编辑模块未加载。";return end
            self.pfb:open(choice.path,"local_file",function(r)if r.ok then self.top=nil;self.page="pfb" end end)
        elseif choice.kind=="motion" then
            send("open_motion",{path=choice.path},function(r)
                if not r.ok then return end
                self.motion_document=r.motionDocument;self.motion_selected=1;self.motion_filter=""
                self.top=nil;self.page="actions";self.action_source="local"
                s.message="已打开本地动作文件："..choice.path
            end)
        else
            local accepted
            if choice.resource then accepted=core.resolve_captured_resource(choice.resource)
            else accepted=core.request_open_document(choice.path) end
            if not accepted and not s.pending then s.message=s.action_message end
        end
    end
    local function button(id,label,width,fn,kind,enabled)
        local available=math.max(60,real.get_window_size().x-32*u.scale)
        if u:button("##d2d_"..id,label,nil,nil,math.min(width*u.scale,available),34*u.scale,kind,enabled~=false and not s.ai_busy) then call(fn) end
    end
    if pfb_factory then self.pfb=pfb_factory.new(core,u,send,button)end
    local function cache_response(r)
        if not r.ok then return end
        s.message="素材库已更新。"
        if r.cache then self.cache=r.cache end
        if r.cachePaths then
            core.precache_load(r.cache or self.cache,r.cachePaths)
            if self.reapply_after_warm then
                self.reapply_after_warm=false
                self.apply_pending=true
            end
        end
    end
    local function refresh_cache() send("cache_list",{},cache_response) end
    local function materialize() send("cache_materialize",{},cache_response) end
    local function capture_controls()
        button("capture",s.capture_snapshot.recording and "结束记录调用" or "开始记录调用",155,function()
            local operation=s.capture_snapshot.recording and "stop" or "start"
            if core.capture_command(operation) and operation=="start" then self:reset_effect_filter()end
        end,"primary")
        real.same_line()
        button("clear","清空记录",100,function() if core.capture_command("clear")then self:reset_effect_filter()end end)
    end
    local function small_input(id,label,key,width)
        u:label(label,u:pos());u:space(24*u.scale)
        local changed,value=u:textbox("##d2d_input_"..id,self[key] or "",width)
        if changed then self[key]=value end
    end
    local function local_motions()
        local doc=self.motion_document
        u:heading("本地动作文件","读取文件中的动作与特效调用；不改变角色当前动作。")
        if not doc then
            button("local_motion_open","选择动作文件",150,function() send("desktop_start",{mode="pick_motion"}) end,"primary")
            return
        end
        u.adapter.text(doc.name.."  ·  "..tostring(#doc.motions).." 个动作")
        u.adapter.text(doc.path)
        if doc.basePath and doc.basePath~="" then u.adapter.text("继承动作表："..doc.basePath) end
        local changed,value=u:textbox("##local_motion_filter",self.motion_filter,math.min(520*u.scale,real.get_window_size().x-32*u.scale))
        if changed then self.motion_filter=value end
        local selected=doc.motions[self.motion_selected or 1]
        if selected then
            u:heading(selected.name,"Motion ID "..tostring(selected.motionId or "未存储于独立 MOT").."  ·  "..
                tostring(selected.frameCount or "未知").." 帧  ·  "..tostring(selected.frameRate or "未知").." FPS")
            if selected.externalPath then u.adapter.text("外链文件尚未载入："..selected.externalPath) end
            if selected.status then u.adapter.text(selected.status) end
            if u.adapter.tree_node("特效调用（"..tostring(#(selected.slots or {})).."）##local_motion_slots") then
                for _,slot in ipairs(selected.slots or {}) do
                    u.adapter.text(string.format("%.2f → %.2f f   ·   %s   ·   %s",slot.startFrame,slot.endFrame,tostring(slot.effectId),slot.jointName or ""))
                    u.adapter.text("容器 "..tostring(slot.containerId).." / 元素 "..tostring(slot.elementId).." · "..slot.origin)
                end
                if #(selected.slots or {})==0 then u.adapter.text("此动作内没有可解析的特效调用。游戏状态机触发不包含在此列表中。") end
                u.adapter.tree_pop()
            end
            u.adapter.separator()
        end
        for i,motion in ipairs(doc.motions) do
            local label="["..tostring(motion.motionId or "MOT").."]  "..motion.name
            if label:lower():find(self.motion_filter:lower(),1,true) then
                button("local_motion_"..i,label,math.min(700,(real.get_window_size().x-32*u.scale)/u.scale),function() self.motion_selected=i end,
                    self.motion_selected==i and "selected")
            end
        end
    end
    local function action_slots()
        local preview=core.action_preview_status and core.action_preview_status() or {}
        local edit_busy=core.action_edit_pending and core.action_edit_pending()
        u:heading("动作特效槽位","先核实动作与 PFB 的关联，再写入出现与结束帧。")
        local backend_ready=core.backend_ready()
        if not backend_ready then
            u.adapter.text("文件编辑服务未连接：当前可以查看动作记录和核实 PFB，但无法打开文件。")
            u.adapter.text("请运行 MotionVfx Live 启动入口；连接恢复后直接重试，无需重新记录或 Reset。")
        end
        local record
        for _,r in ipairs((s.capture_snapshot or {}).actions or {}) do if r.id==s.selected_action_id then record=r;break end end
        if self.action_document and self.action_document.source and core.find_action_record then record=core.find_action_record(self.action_document)end
        u.adapter.text(record and ((record.motion or {}).name or record.id) or "请先在动作记录中选择目标动作。")
        button("verify_action_source","核实当前动作的 PFB",210,function()
            local source=core.resolve_action_source(record)
            if source then self.action_proof=source;self.action_document=nil;self.slot_template=nil;self.slot_template_label=nil;self.action_open_error=nil;self.action_open_status=nil end
        end,"primary",record~=nil and not s.pending and not preview.busy and not edit_busy)
        if self.action_proof then
            u.adapter.text("动作表："..self.action_proof.bankPath)
            for i,pfb in ipairs(self.action_proof.prefabs or {}) do
                u.adapter.text((pfb.externalIndex<0 and "角色通用" or "外部容器 "..tostring(pfb.dataContainerId)).." · "..pfb.path)
                button("open_action_pfb_"..i,"编辑此 PFB 的动作槽位",240,function()
                    self.action_open_error=nil;self.action_open_status=nil
                    if not core.backend_ready() then
                        self.action_open_error="文件编辑服务未连接，请先启动 MotionVfx Live 后再点击此按钮。"
                        return
                    end
                    local selected=core.find_action_record and core.find_action_record({source=self.action_proof}) or record
                    local fresh=core.resolve_action_source(selected)
                    if not fresh then self.action_open_error=s.message;return end
                    local index
                    for j,p in ipairs(fresh.prefabs or {})do if p.path:lower()==pfb.path:lower() and p.dataContainerId==pfb.dataContainerId then index=j-1;break end end
                    if not index then s.message="当前角色已不再使用这个 PFB，请重新核实。";self.action_open_error=s.message;return end
                    self.action_proof=fresh
                    self.action_open_status="正在打开动作槽位："..pfb.path
                    local accepted=send("open_action",{source=fresh,prefabIndex=index},function(r)
                        if r.ok then
                            self.action_document=r.actionDocument;self.slot_template=nil;self.slot_template_filter="";self.slot_attach_key=nil;self.slot_joint=""
                            self.action_open_status="已打开动作槽位："..r.actionDocument.pfbPath
                        else
                            self.action_open_status=nil;self.action_open_error="打开失败："..tostring(r.error or "服务未返回错误详情")
                        end
                    end)
                    if not accepted then self.action_open_status=nil;self.action_open_error=s.message end
                end,self.action_document and self.action_document.pfbPath==pfb.path and "selected" or nil,not s.pending and not preview.busy and not edit_busy)
            end
        end
        if self.action_open_error then u.adapter.text(self.action_open_error)
        elseif self.action_open_status then u.adapter.text(self.action_open_status)end
        local doc=self.action_document
        if not doc then return end
        u.adapter.separator();u:heading(doc.name,"添加后可热重载到角色动作；导出时分别选择动作表与 PFB 的保存位置。")
        u.adapter.text("对应 PFB："..doc.pfbPath)
        u.adapter.text("帧范围：0 → "..tostring(doc.frameCount))
        u.adapter.text("出现/结束帧是此动作内部的帧数；动作衔接可能从中途进入。")
        u.adapter.text("首次验证可覆盖完整帧范围，确认触发后再缩短。")
        small_input("slot_resource","要调用的 EFX 游戏路径","slot_resource",math.min(650*u.scale,real.get_window_size().x-32*u.scale))
        button("use_current_effect","使用当前编辑的 EFX 路径",250,function()
            self.slot_resource=s.target_resource_path or (s.document and s.document.gameSource and s.document.gameSource.sharedTargetPath) or ""
            if self.slot_resource=="" then s.message="本地 EFX 没有已核实的游戏路径，请填写安装后的 Art 路径。" end
        end)
        button("load_slot_groups","读取此 EFX 的全部 Group",260,function()
            local path=self.slot_resource or ""
            send("action_effect_groups",{resourcePath=path},function(r)
                if r.ok and path==self.slot_resource then self.slot_groups=r.effectGroups.groups;self.slot_groups_path=path;self.slot_group="" end
            end)
        end,nil,not s.pending and (self.slot_resource or "")~="")
        if self.slot_groups_path==self.slot_resource and self.slot_groups then
            local names={"全部 / 不指定分组"};local selected=1;local found=(self.slot_group or "")==""
            for i,name in ipairs(self.slot_groups)do names[#names+1]=name;if name==self.slot_group then selected=i+1;found=true end end
            if not found then self.slot_group="" end
            local changed,index=u.adapter.combo("Group 分组##action_group_choice",selected,names)
            if changed then self.slot_group=index==1 and "" or self.slot_groups[index-1] end
            u.adapter.text("显示调用的 EFX 文件中的全部原始分组名；分组备注不写入调用。")
        else self.slot_group="";u.adapter.text("填写或更换 EFX 路径后，请读取该文件的 Group。")end
        local targets=(doc.source or {}).attachmentTargets or {}
        local kind=self.slot_attach_kind or "character"
        local changed,index=u.adapter.combo("挂载对象##action_attach_kind",kind=="weapon" and 2 or 1,{"人物","当前武器"})
        if changed then kind=index==1 and "character" or "weapon";self.slot_attach_kind=kind;self.slot_attach_key=nil;self.slot_joint="" end
        local choices,labels={},{};local selected=1;local found=false
        for _,target in ipairs(targets)do if target.kind==kind then
            choices[#choices+1]=target;labels[#labels+1]=target.label.."  ·  RelationChildId "..tostring(target.relationChildId)
            if target.key==self.slot_attach_key then selected=#choices;found=true end
        end end
        if #choices>0 then
            changed,index=u.adapter.combo("实际挂载对象##action_attach_target",selected,labels)
            if changed or not found then self.slot_attach_key=choices[index].key;self.slot_joint="" end
            local target=choices[index or selected]
            for _,t in ipairs(choices)do if t.key==self.slot_attach_key then target=t;break end end
            local joints={"对象原点（不指定骨骼）"};local joint_index=1
            for i,joint in ipairs(target.joints or {})do joints[#joints+1]=joint;if joint==self.slot_joint then joint_index=i+1 end end
            if joint_index==1 then self.slot_joint="" end
            changed,index=u.adapter.combo("骨骼挂点##action_attach_joint",joint_index,joints)
            if changed then self.slot_joint=index==1 and "" or target.joints[index-1] end
            u.adapter.text("挂点来自当前对象的真实骨骼；切换装备后需重新核实来源。")
        else self.slot_attach_key=nil;u.adapter.text("尚未核实此对象的 RelationChildId 和骨骼，请重新核实并打开动作 PFB。")end
        local mode=self.slot_attachment_mode or "initial"
        changed,index=u.adapter.combo("挂载方式##action_attachment_mode",mode=="follow" and 1 or 2,{"持续跟随挂点（刀光、身上效果）","仅在触发时定位（脚印、地面效果）"})
        if changed then self.slot_attachment_mode=index==1 and "follow" or "initial" end
        small_input("slot_start","出现帧","slot_start",150*u.scale)
        small_input("slot_end","触发区间结束帧","slot_end",150*u.scale)
        button("slot_life_independent","独立寿命（切换动作继续播放）",300,function()self.slot_lifetime_mode="independent"end,
            self.slot_lifetime_mode=="independent" and "selected")
        button("slot_life_action","随触发区间或动作结束",260,function()self.slot_lifetime_mode="action"end,
            self.slot_lifetime_mode=="action" and "selected")
        if self.slot_lifetime_mode=="independent" then
            small_input("slot_lifetime","每次触发后的最长寿命（特效帧）","slot_lifetime",170*u.scale)
            u.adapter.text("每次触发独立计时；不受动作长度限制。到期销毁，EFX 自身可以提前结束。")
            u.adapter.text("区间内沿用引擎的单次触发；再次进入会新建一份。位置与跟随方式使用上方挂载设置。")
            if not doc.independentLifetimeAvailable then u.adapter.text("请先重新核实并打开此动作的 PFB，以读取游戏结束类型。")end
        end
        if u.adapter.tree_node("选择挂接模板（来自上述 PFB）##slot_templates") then
            u.adapter.text("必选：点击下面一条已有特效作为挂接模板，无需手填名称。")
            u.adapter.text("模板是一条已有、结构完整的 PFB 特效槽位，新槽位会复制它的引擎参数。")
            u.adapter.text("这样可保留偏移、旋转、缩放、颜色、镜像、延迟及自定义参数等设置。")
            u.adapter.text("原槽位不会被修改；新槽位会获得独立编号。它不会复制原动作的触发帧。")
            u.adapter.text("上方 EFX、Group、挂载对象、骨骼、帧区间和寿命会覆盖模板对应设置。")
            u.adapter.text("选择方法：优先选同类用途的现有槽位，例如脚下效果、武器拖尾或身上持续效果。")
            u.adapter.text("若位置、大小或延迟不合适，可换模板，或在 PFB 编辑区检查对应参数。")
            small_input("slot_template_filter","按容器、编号或资源名筛选","slot_template_filter",400*u.scale)
            for _,t in ipairs(doc.templates or {}) do
                local label=tostring(t.container).."-"..tostring(t.element).." · "..tostring((t.resources or {})[1] or t.name)
                if label:lower():find((self.slot_template_filter or ""):lower(),1,true) then
                    button("slot_template_"..t.key,label,math.min(700,(real.get_window_size().x-32*u.scale)/u.scale),function()
                        self.slot_template=t.key;self.slot_template_label=label
                    end,self.slot_template==t.key and "selected")
                end
            end
            u.adapter.tree_pop()
        end
        u.adapter.text(self.slot_template_label and ("已选模板："..self.slot_template_label) or "尚未选择模板：请展开上方列表并点击一条。")
        button("add_action_slot","添加特效槽位",180,function()
            send("add_action_slot",{actionRevision=doc.revision,templateKey=self.slot_template,resourcePath=self.slot_resource or "",
                groupName=self.slot_group or "",jointName=self.slot_joint or "",attachmentTargetKey=self.slot_attach_key,attachmentMode=self.slot_attachment_mode or "initial",startFrame=tonumber(self.slot_start),endFrame=tonumber(self.slot_end),
                lifetimeMode=self.slot_lifetime_mode,lifetimeFrames=tonumber(self.slot_lifetime)},function(r)
                if r.ok then self.action_document=r.actionDocument;s.message="已添加槽位。点击热重载到游戏后，重新执行对应动作即可预览。" end
            end)
        end,"primary",self.slot_template~=nil and self.slot_attach_key~=nil and self.slot_groups_path==self.slot_resource and not s.pending and not preview.busy and not edit_busy and
            (self.slot_lifetime_mode~="independent" or doc.independentLifetimeAvailable==true))
        for _,slot in ipairs(doc.added or {}) do
            u.adapter.text(string.format("%.2f → %.2f 帧 · %s · %s",slot.startFrame,slot.endFrame,slot.effectId,slot.resourcePath))
            u.adapter.text(slot.lifetimeMode=="independent" and ("独立寿命："..tostring(slot.lifetimeFrames).." 特效帧") or "随区间/动作结束")
            button("delete_action_slot_"..tostring(slot.id or slot.effectId),"删除此槽位",145,function()core.remove_action_slot(doc,slot.id)end,"warning",
                slot.id~=nil and not s.pending and not preview.busy and not edit_busy)
        end
        button("preview_action_pair","热重载到游戏",200,function()core.preview_action(doc,record)end,"primary",
            (doc.previewSlotCount or #(doc.added or {}))>0 and record~=nil and not s.pending and not preview.busy and not edit_busy)
        real.same_line();button("restore_action_pair","还原并清除新增槽位",230,function()core.restore_action_preview()end,nil,
            (preview.requiresRestore==true or (doc.totalAddedSlots or #(doc.added or {}))>0) and not preview.busy and not edit_busy)
        u.adapter.text("还原会清除本次编辑会话添加的全部槽位，并恢复动作表与对应 PFB；EFX 参数编辑保留。")
        if preview.message then u.adapter.text(preview.message)end
        if preview.appliedRevision and preview.appliedRevision~=doc.revision then u.adapter.text("有尚未热重载的槽位修改。")end
        button("export_action_pair","导出动作文件与对应 PFB",270,function() send("begin_export",{scope="action"}) end,"primary",(doc.previewSlotCount or #(doc.added or {}))>0 and not s.pending and not edit_busy)
    end
    local function show_action_groups()
        u:heading("已保存动作组","按完整动作表路径和动作编号保存；选择时匹配当前角色记录。")
        local aliases=(core.action_preview_status and core.action_preview_status() or {}).aliases or {}
        local function canonical(path)return type(path)=="string" and ((aliases[path:lower()] or path):lower()) or ""end
        local records=(s.capture_snapshot or {}).actions or {};local actor=(s.capture_snapshot or {}).actor or {}
        for _,group in ipairs(self.action_groups)do
            if u.adapter.tree_node(tostring(group.name).."##action_group_"..tostring(group.id))then
                u.adapter.text(tostring(#(group.motions or {})).." 个动作 · "..tostring(group.description or ""))
                for index,m in ipairs(group.motions or {})do
                    local current
                    for i=#records,1,-1 do
                        local r=records[i];local rm=r.motion or {}
                        if r.componentKind=="character" and r.sceneEpoch==actor.sceneEpoch and r.actorIdentity==actor.characterIdentity and
                            r.weaponType==actor.weaponType and r.weaponId==actor.weaponId and rm.layer==(m.layer or 0) and
                            rm.motionId==m.motionId and rm.bankId==m.bankId and canonical(rm.bankResourcePath)==canonical(m.path) then current=r;break end
                    end
                    u.adapter.text(tostring(m.name or "").." · Bank "..tostring(m.bankId).." / "..tostring(m.motionId))
                    u.adapter.text(tostring(m.path))
                    button("group_action_"..tostring(group.id).."_"..index,current and "选择此动作的特效槽位" or "本次记录中未找到此动作",250,function()
                        s.selected_action_id=current.id;self.action_document=nil;self.action_proof=nil;self.slot_template=nil;self.slot_template_label=nil
                        self.action_source="slots"
                    end,nil,current~=nil and not s.pending and not (core.action_edit_pending and core.action_edit_pending()))
                end
                u.adapter.tree_pop()
            end
        end
        if #self.action_groups==0 then u.adapter.text("尚无已保存动作组。")end
    end
    local function show_actions()
        button("actions_live","当前角色 / 记录",170,function() self.action_source="live" end,self.action_source=="live" and "selected")
        real.same_line();button("actions_local","本地动作文件",150,function() self.action_source="local" end,self.action_source=="local" and "selected")
        real.same_line();button("actions_slots","动作特效槽位",150,function() self.action_source="slots" end,self.action_source=="slots" and "selected")
        real.same_line();button("actions_groups","动作组",100,function()self.action_source="groups"end,self.action_source=="groups" and "selected")
        u:space(12*u.scale)
        if self.action_source=="local" then local_motions();return end
        if self.action_source=="slots" then action_slots();return end
        if self.action_source=="groups" then show_action_groups();return end
        local live=core.current_actions()
        u:heading("当前角色动作",live.recording and "实时更新 · 正在记录" or "实时更新 · 未开始记录")
        local main
        for _,record in ipairs(live.currentActions or {}) do
            if record.componentKind=="character" and (record.motion or {}).layer==0 then main=record;break end
        end
        if main then
            local motion=main.motion or {}
            local x,y=u:pos();local width=real.get_window_size().x-28*u.scale
            u:rect(x,y,width,82*u.scale,C.raised,8*u.scale)
            u:label(tostring(motion.name or ("Motion "..tostring(motion.motionId or "?"))),x+14*u.scale,y+12*u.scale,19*u.scale,C.cyan,width-28*u.scale)
            u:label("Bank "..tostring(motion.bankId or "?").."   /   Motion "..tostring(motion.motionId or "?")..
                "   /   帧 "..string.format("%.1f",tonumber(motion.frame) or 0),x+14*u.scale,y+48*u.scale,14*u.scale,C.text)
            u:space(95*u.scale)
        else u.adapter.text(live.actionStatus or "等待游戏角色就绪…");u:space(12*u.scale) end
        capture_controls();u:space(9*u.scale)
        if s.capture_error then u.adapter.text("动作读取失败："..s.capture_error) end
        u:heading("动作序列","选择动作，查看关联的特效记录。")
        local changed,value=u:textbox("##d2d_action_filter",s.action_filter or "",440*u.scale)
        if changed then s.action_filter=value end
        local snapshot=s.capture_snapshot or {}
        for i,record in ipairs(snapshot.actions or {}) do
            local motion=record.motion or {}
            local label=tostring(motion.name or ("Motion "..tostring(motion.motionId or "?")))
            if record.componentKind=="character" and (motion.layer or 0)==0 and
                (s.action_filter=="" or (label.." "..tostring(motion.motionId)):lower():find(s.action_filter:lower(),1,true)) then
                local x,y=u:pos()
                if u:button("##action_"..record.id,string.format("%02d   %s",i,label),nil,nil,real.get_window_size().x-28*u.scale,52*u.scale,
                    s.selected_action_id==record.id and "selected") then
                    if s.selected_action_id~=record.id then self.action_document=nil;self.action_proof=nil;self.slot_template=nil;self.slot_template_label=nil end
                    s.selected_action_id=record.id;self:reset_effect_filter();self.effect_action_id=record.id;self.page="effects"
                end
                u:label("Bank "..tostring(motion.bankId or "?").."  /  Motion "..tostring(motion.motionId or "?")..
                    "     "..tostring(record.firstMotionFrame or "?").." → "..tostring(record.lastMotionFrame or "?").." f",
                    x+15*u.scale,y+53*u.scale,12*u.scale,C.muted)
                u:space(24*u.scale)
            end
        end
        if #(snapshot.actions or {})==0 then
            u:space(32*u.scale);u:heading("等待第一个动作","开始记录后，回到游戏完成攻击、蓄力或鬼人化。")
        end
    end
    local function show_effects()
        capture_controls();real.same_line()
        button("all_effects","全部特效",100,function() self:reset_effect_filter() end,not self.effect_action_id and "selected")
        u:space(10*u.scale)
        u.adapter.text("此处记录特效资源与触发动作，不生成画面录像。需要重播请使用「录制画面 / 重播」。")
        button("effects_replay","录制画面 / 重播",205,function()self.page="time";self.top=nil;self:read_last_replay()end)
        local snapshot=s.capture_snapshot or {}
        local action,links,paths
        if self.effect_action_id then
            action,links,paths=core.action_details(snapshot,self.effect_action_id)
            if not action then
                self:reset_effect_filter("此前选择的动作记录已过期，已自动显示全部角色特效。")
                paths=nil
            end
        end
        local confirmed=0
        for _,resource in ipairs(snapshot.resources or {})do if resource.localConfirmed then confirmed=confirmed+1 end end
        if action then
            u:heading("动作关联特效",(action.motion or {}).name or action.id)
            button("effect_action_slots","为此动作添加特效",210,function() s.selected_action_id=action.id;self.page="actions";self.action_source="slots" end,"primary")
            for _,link in ipairs(links) do
                u.adapter.text((link.association=="direct_motion_request" and "直接调用" or "同期出现")..
                    " · 帧 "..tostring(link.motionFrame or "?").." · 调用 "..tostring(link.effectId or "?"))
            end
        else u:heading("角色特效资源","本次记录中已确认归属的全部角色特效") end
        u.adapter.text("已捕获 "..confirmed.." 个角色资源 · "..(snapshot.recording and "正在记录，列表实时更新" or "记录已结束"))
        if self.effect_notice then u.adapter.text(self.effect_notice)end
        local changed,value=u:textbox("##d2d_effect_filter",self.resource_filter,440*u.scale)
        if changed then self.resource_filter=value end
        if self.resource_filter~="" then
            button("clear_effect_filter","清除搜索",110,function()self.resource_filter=""end)
        end
        local count=0
        for i,resource in ipairs(snapshot.resources or {}) do
            local path=tostring(resource.path or resource.canonicalPath or "")
            local canonical=path:lower():gsub("\\","/"):gsub("%.efx%.%d+$",".efx")
            if resource.localConfirmed and (not paths or paths[canonical]) and path:lower():find(self.resource_filter:lower(),1,true) then
                count=count+1
                u:space(7*u.scale)
                local x,y=u:pos()
                u:label(path:match("[^/\\]+$") or path,x,y,18*u.scale,C.text)
                u:label("已确认归属   ·   "..tostring(resource.count or 0).." 次调用",x,y+28*u.scale,12*u.scale,C.green)
                u:space(53*u.scale)
                button("effect_"..i,"打开并编辑",120,function() self:queue_open(path,resource) end,"primary")
                u.adapter.text(path);u.adapter.separator()
            end
        end
        self.effect_view_status={scope=action and "action" or "all",actionId=self.effect_action_id,filter=self.resource_filter,visibleCount=count,totalConfirmed=confirmed}
        if count==0 then
            if self.resource_filter~="" then u:heading("没有匹配搜索的特效","清除搜索可查看当前范围内已捕获的特效。")
            elseif action then u:heading("该动作暂未记录到关联特效","可点「全部特效」查看本次记录；录制中新捕获的关联资源会实时显示。")
            elseif snapshot.recording then u:heading("正在等待角色特效","回到游戏执行带特效的动作；捕获后会实时显示，无需先结束记录。")
            else u:heading("尚无角色特效记录","开始记录后执行带特效的动作；列表只显示已确认属于当前角色的资源。")end
        end
    end
    local function cache_page()
        u:heading("资源预缓存","路径是资源的独立名称，可以填写到特效属性中。")
        button("choose","选择外部文件",150,function() send("desktop_start",{mode="pick"}) end,"primary")
        real.same_line();button("cache_refresh","刷新列表",95,refresh_cache)
        real.same_line();button("cache_warm","预加载资源",120,materialize)
        u:space(8*u.scale)
        u.adapter.text("Mesh · MDF · UVS · TEX · PFB · 动作文件 · EFX")
        small_input("alias","自定义虚拟路径（留空自动生成；一次自定义一个文件）","cache_alias",math.min(620,real.get_window_size().x-30))
        if self.selected_files and #self.selected_files>0 then
            for _,path in ipairs(self.selected_files) do u.adapter.text(path) end
            button("cache_import","加入预缓存",140,function()
                if #self.selected_files>1 and self.cache_alias~="" then s.message="多个文件请使用自动路径，或每次只选择一个文件。";return end
                for _,path in ipairs(self.selected_files) do self.imports[#self.imports+1]={path=path,alias=self.cache_alias~="" and self.cache_alias or nil} end
                self.selected_files=nil
            end,"primary")
        end
        u.adapter.separator()
        for i,entry in ipairs(self.cache) do
            local x,y=u:pos()
            u:label(entry.kind:upper(),x,y,12*u.scale,C.cyan)
            u:label(entry.alias,x+72*u.scale,y,14*u.scale,C.text,real.get_window_size().x-106*u.scale)
            u:space(29*u.scale)
            local runtime=(core.precache_status() or {})[entry.id]
            u.adapter.text("版本 "..entry.revision.."   ·   "..(runtime and runtime.message or "已缓存，待预加载"))
            button("cache_copy"..i,"复制路径",100,function() core.copy_text(entry.alias) end)
            real.same_line();button("cache_reload"..i,"重新读取并热重载",170,function()
                if core.has_drafts() then s.message="请先提交或还原参数输入，再热重载素材。";return end
                local document=s.document and s.document.source
                local epoch=self.cache_epoch
                send("cache_reload",{entryId=entry.id},function(r)
                    cache_response(r)
                    if r.ok then
                        self.warm_pending=true
                        if self.cache_epoch==epoch and s.target_resource_path and s.document and s.document.source==document and
                            (s.bindings_snapshot.appliedCount or 0)>0 then
                            self.reapply_after_warm=true;self.reapply_source=document
                        end
                    end
                end)
            end)
            if s.selected and self.path_target then
                real.same_line();button("cache_use"..i,"填入所选参数",130,function()
                    s.values[self.path_target]=entry.alias;self.top=nil;self.page="edit"
                end,"primary")
            end
            if entry.dependencies and #entry.dependencies>0 and u.adapter.tree_node("依赖资源##cache_deps"..entry.id) then
                for _,dep in ipairs(entry.dependencies) do u.adapter.text(dep) end
                u.adapter.tree_pop()
            end
            u.adapter.separator()
        end
        if #self.cache==0 then u:heading("素材库为空","选择已转换好的游戏格式文件，建立第一个 art/ 路径。") end
    end
    local function file_page()
        u:heading("本地文件")
        button("open_efx","打开特效文件",150,function() send("desktop_start",{mode="pick_efx"}) end,"primary",not s.pending)
        real.same_line();button("open_motion","打开动作文件",150,function() send("desktop_start",{mode="pick_motion"}) end,nil,not s.pending)
        real.same_line();button("open_pfb","打开 PFB 文件",150,function() send("desktop_start",{mode="pick_pfb"}) end,nil,not s.pending)
        u:space(12*u.scale)
        small_input("local_path","或粘贴完整文件路径（可带引号）","local_path",math.min(720*u.scale,real.get_window_size().x-30*u.scale))
        button("open_path","打开此路径",150,function() self:queue_open(self.local_path) end)
        u.adapter.separator()
        local action_export=self.page=="actions" and self.action_source=="slots" and self.action_document
        local export_available
        if self.page=="pfb" then export_available=self.pfb and self.pfb.document~=nil
        elseif self.page=="actions" and self.action_source=="slots" then export_available=action_export~=nil
        else export_available=s.document~=nil end
        button("save_copy","导出文件",150,function()
            if self.page=="pfb" then send("begin_export",{scope="pfb"})
            elseif action_export then send("begin_export",{scope="action"}) else core.save_copy() end
        end,"primary",export_available and not s.pending)
        u:space(12*u.scale)
        if s.document then
            u.adapter.text("来源："..tostring(s.document.source))
            u.adapter.text("角色资源："..tostring(s.target_resource_path or "独立文件"))
        end
        if self.desktop and (self.desktop.phase=="error" or self.desktop.phase=="choosing" or self.desktop.phase=="cancelled") then
            u.adapter.text(self.desktop.message or self.desktop.phase)
        end
    end
    local function settings_page()
        u:heading("设置","界面、快捷键与工作状态")
        u.adapter.text("界面缩放")
        for i,scale in ipairs({0.85,1,1.15,1.3}) do
            if i>1 then real.same_line() end
            button("scale"..i,tostring(math.floor(scale*100+.5)).."%",80,function()
                self.scale=scale;self:save_settings()
            end,self.scale==scale and "selected")
        end
        u:space(15*u.scale)
        u.adapter.text("属性与参数名称")
        button("language_zh","中文（保留原名）",170,function() core.set_parameter_language("zh") end,s.parameter_language=="zh" and "selected")
        real.same_line();button("language_en","English",110,function() core.set_parameter_language("en") end,s.parameter_language=="en" and "selected")
        u.adapter.text("中文和英文均可用于搜索；切换只影响显示。未释义的字段保留原名。")
        button("center_window","窗口居中",120,function() self.window_position={x=.5,y=.5};self:save_settings() end)
        u.adapter.text("拖动左上方 Logo 标题区移动窗口；隐藏后拖动小球可吸附到任一屏幕边缘。")
        u.adapter.text("点击小球恢复编辑器。游戏中按 Insert 开启鼠标后操作小球。")
        u:space(15*u.scale)
        adapter(core.ui_debug)
        u.adapter.separator()
        button("sync","同步当前文档",145,core.synchronize)
        u:row("界面版本",core.version);u:row("渲染器","REFramework D2D")
        u:row("资源后台",s.status and s.status.version or "未连接")
        u:row("资源试改",tostring((s.bindings_snapshot or {}).appliedCount or 0).." 处")
        u:space(22*u.scale);u.adapter.separator()
        u:heading("关于作者","")
        local x,y=u:pos();local width=math.min(390*u.scale,real.get_window_size().x-38*u.scale)
        local height=width*832/1920
        u:emit("image",{path="MotionVfxLive/author-logo.png",x=x,y=y,w=width,h=height})
        u:space(height+14*u.scale)
        u.adapter.text("尝试给推搓推的推")
        for i,link in ipairs({{"bilibili","b站"},{"caimogu","踩蘑菇"}}) do
            if i>1 then real.same_line() end
            local lx,ly=u:pos();local lw=(i==1 and 58 or 80)*u.scale
            local clicked=real.invisible_button("##author_"..link[1],{lw,28*u.scale})
            u:label(link[2],lx,ly,15*u.scale,C.cyan)
            u:emit("line",{x=lx,y=ly+23*u.scale,x2=lx+lw-15*u.scale,y2=ly+23*u.scale,color=C.cyan,h=1})
            if clicked then send("desktop_start",{mode="author_link",site=link[1]}) end
        end
    end
    local function time_page()
        u:heading("场景时间","控制真实游戏中的动作与特效")
        button("pause",s.scene_pause and "继续场景" or "暂停场景",150,function()
            core.queue_scene("pause",s.scene_pause==nil)
        end,"primary")
        for _,speed in ipairs({0.1,0.25,0.5,1}) do
            real.same_line();button("speed"..speed,speed.."×",65,function() core.queue_scene("speed",speed) end)
        end
        u:space(10*u.scale);u.adapter.text(s.scene_message)
        u.adapter.separator();u:heading("画面录像与重播","录制当前游戏画面，包含动作和特效；之后可正放、倒放、逐帧查看。")
        u.adapter.text("动作／特效页的「记录调用」用于查找资源；这里的「开始录制画面」才会保存录像。")
        for i,seconds in ipairs({5,15,30}) do
            if i>1 then real.same_line() end
            button("record_length"..i,seconds.." 秒",75,function() self.record_seconds=seconds end,self.record_seconds==seconds and "selected")
        end
        button("record","开始录制画面",175,function()
            send("desktop_start",{mode="record",seconds=self.record_seconds},function(r) if r.ok then self:hide() end end)
        end,"primary",not s.pending and not self:recording_active())
        real.same_line();button("record_stop","结束并保存录像",175,function() send("desktop_stop",{}) end,nil,not s.pending and self:recording_active())
        real.same_line();button("replay_refresh","读取最近录制",145,function()
            self:read_last_replay()
        end)
        button("replay_folder","打开录像保存文件夹",230,function()send("desktop_start",{mode="open_replay_folder"})end,nil,not s.pending)
        u.adapter.text("保存位置：游戏目录/reframework/images/MotionVfxLive/replays（逐帧图片与 replay.json）。")
        if self.desktop then u.adapter.text(self.desktop.message or "") end
        if self.replay_notice then u.adapter.text(self.replay_notice)end
        local r=self.replay
        if not r or not r.frames or #r.frames==0 then
            u:space(20*u.scale);u:heading("尚无录制片段","点击录制后工作台会隐藏，切回游戏完成动作。只记录游戏在前台的画面。");return
        end
        u:space(12*u.scale)
        local frame=r.frames[math.max(1,math.min(#r.frames,math.floor(self.replay_index)))]
        u.adapter.text("当前录像："..tostring(r.id or "最近录制").." · "..#r.frames.." 帧")
        if r.outputDirectory then
            u.adapter.text("文件夹："..r.outputDirectory)
            button("replay_copy_path","复制录像路径",170,function()core.copy_text(r.outputDirectory)end)
        end
        for _,control in ipairs({{"prev","前一帧"},{"reverse","倒放"},{"hold","暂停"},{"forward","正放重播"},{"next","后一帧"}}) do
            if control[1]~="prev" then real.same_line() end
            button("replay_"..control[1],control[2],90,function()
                if control[1]=="prev" then self.replay_speed=0;self.replay_index=math.max(1,self.replay_index-1)
                elseif control[1]=="next" then self.replay_speed=0;self.replay_index=math.min(#r.frames,self.replay_index+1)
                else
                    self.replay_speed=control[1]=="reverse" and -1 or control[1]=="forward" and 1 or 0
                    if self.replay_speed==1 and self.replay_index>=#r.frames then self.replay_index=1
                    elseif self.replay_speed==-1 and self.replay_index<=1 then self.replay_index=#r.frames end
                end
                self.replay_elapsed=0;self.replay_clock=os.clock()
            end,((control[1]=="forward" and self.replay_speed==1) or (control[1]=="reverse" and self.replay_speed==-1)) and "selected")
        end
        u:space(6*u.scale)
        for i,rate in ipairs({.25,.5,1,2}) do
            if i>1 then real.same_line() end
            button("replay_rate"..i,"回看 "..rate.."×",105,function() self.replay_rate=rate end,self.replay_rate==rate and "selected")
        end
        u:space(8*u.scale)
        local width=math.min(800*u.scale,real.get_window_size().x-30*u.scale)
        local tx,ty=u:pos()
        real.invisible_button("##replay_seek",{width,24*u.scale})
        if real.is_item_active() and real.get_mouse then
            local mouse=real.get_mouse();self.replay_index=1+math.floor(math.max(0,math.min(1,(mouse.x-tx)/width))*(#r.frames-1));self.replay_speed=0
        end
        u:rect(tx,ty+8*u.scale,width,5*u.scale,C.raised,2)
        u:rect(tx,ty+8*u.scale,width*(self.replay_index-1)/math.max(1,#r.frames-1),5*u.scale,C.accent,2)
        u.adapter.text(string.format("帧 %d / %d    %.2f 秒",self.replay_index,#r.frames,frame.time or 0))
        local x,y=u:pos();local height=width*(r.height or 720)/(r.width or 1280)
        u:emit("image",{path=frame.path,x=x,y=y,w=width,h=height});u:space(height+10*u.scale)
    end
    function self:tick()
        if self.file_dialog then self.file_dialog:tick()end
        local now=os.clock()
        if self.replay and self.replay_speed~=0 and self.replay.frames and #self.replay.frames>0 then
            local frames=self.replay.frames
            local remaining=math.min(.25,math.max(0,now-self.replay_clock))*self.replay_rate
            self.replay_elapsed=(self.replay_elapsed or 0)+remaining
            for _=1,#frames do
                local next_index=self.replay_index+self.replay_speed
                if next_index<1 or next_index>#frames then self.replay_speed=0;self.replay_elapsed=0;break end
                local delta=math.abs((frames[next_index].time or 0)-(frames[self.replay_index].time or 0))
                if delta<=0 then delta=1/(self.replay.fps or 20) end
                if self.replay_elapsed<delta then break end
                self.replay_elapsed=self.replay_elapsed-delta;self.replay_index=next_index
            end
        else self.replay_elapsed=0
        end
        self.replay_clock=now
        if os.time()~=self.last_poll then
            self.last_poll=os.time()
            local ok,recording=pcall(json.load_file,"MotionVfxLive/desktop-recording-status.json")
            if ok and recording then
                self.desktop=recording
                if terminal(recording.phase) and recording.id~=self.recording_seen then
                    self.recording_seen=recording.id
                    if recording.phase=="complete" and self:load_replay(recording.replay) then
                        s.message="录像已保存，打开「时间与回看」点击「正放重播」。"
                        self.page="time";self.top=nil
                    elseif recording.phase~="complete" then self.replay_notice=recording.message or "录制失败，没有生成新录像。" end
                end
            end
            local dialog_ok,dialog=pcall(json.load_file,"MotionVfxLive/desktop-dialog-status.json")
            if dialog_ok and dialog then self.dialog=dialog end
            for id,job in pairs(self.desktop_jobs)do
                local loaded,status=pcall(json.load_file,"MotionVfxLive/"..job.statusFile)
                if loaded and status and status.id==id and (status.phase=="complete" or status.phase=="failed" or status.phase=="cancelled" or status.phase=="error") then
                    self.desktop_jobs[id]=nil
                    if status.phase~="complete" then s.message=status.message or "操作未完成"
                    else
                    if status.mode=="export" then self.export_pending=status
                    elseif status.replay then self:load_replay(status.replay)
                    elseif (status.mode=="pick_efx" or status.mode=="pick_motion" or status.mode=="pick_pfb") and status.files and status.files[1] then self:queue_open(status.files[1])
                    elseif status.mode=="pick" and status.files then self.selected_files=status.files;self.top="cache" end
                    end
                end
            end
        end
        if self.document_generation~=(s.document_open_generation or 0) then
            self.document_generation=s.document_open_generation or 0
            self.page="edit";self.top=nil;self.path_target=nil;self.narrow_pane=nil;self.narrow_selection=nil
            u.trees={};self.edit_generation=(self.edit_generation or 0)+1
        end
        if self.restoring then
            local error_message=s.action_edit_error or s.bindings_restore_failed or s.scene_control_error or s.restore_preview_error
            local b=s.bindings_snapshot or {}
            local action=core.action_preview_status and core.action_preview_status() or {}
            local pfb=core.pfb_preview_status and core.pfb_preview_status() or {}
            if pfb.phase=="restore_failed" then error_message=pfb.message end
            if action.phase=="restore_failed" then error_message=action.message end
            if error_message then
                s.message="还原尚未完成："..tostring(error_message)
                self.restoring=false
            elseif s.bindings_restore_pending or b.busy or b.requiresRestoreBeforeReset or (b.appliedCount or 0)>0
                or action.busy or action.requiresRestore or pfb.busy or pfb.requiresRestore or (core.action_edit_pending and core.action_edit_pending())
                or s.scene_original or s.global_time_original or s.effect_update_original
                or s.restore_preview_pending or #(s.scene_queue or {})>0 or s.apply_requested then
                s.message="正在停止并还原资源与场景，请稍候。"
            else
                s.message="已还原资源与场景，游戏控制已交回。"
                self.restoring=false
            end
        end
        if s.ai_busy or s.reloading then return end
        next_export_dialog()
        if self.file_dialog and self.file_dialog.active then return end
        if self.export_pending and not s.pending then
            local choice=self.export_pending;self.export_pending=nil
            send("finish_export",{exportToken=choice.exportToken,destinations=choice.destinations or {}},function(r)
                if r.ok then s.message=#(r.exportedFiles or {})>0 and ("已导出："..table.concat(r.exportedFiles," / ")) or "已取消导出" end
            end)
        elseif self.open_pending then open_pending()
        elseif not s.pending and self.stop_record_pending then self.stop_record_pending=false;send("desktop_stop",{})
        elseif not s.pending and #self.imports>0 then
            local item=table.remove(self.imports,1)
            send("cache_import",item,function(r) cache_response(r);if r.ok then self.warm_pending=true else self.imports={} end end)
        elseif not s.pending and self.warm_pending then self.warm_pending=false;materialize()
        elseif not s.pending and self.apply_pending then
            self.apply_pending=false
            if s.document and s.document.source==self.reapply_source and not core.publish_document(true) then s.message=s.action_message end
        end
    end
    local function resize_edges(x,y,w,h,k,sw,sh)
        local grip=math.max(6,7*k);local corner=16*k
        local handles={
            {"nw",x,y,corner,corner},{"ne",x+w-corner,y,corner,corner},
            {"sw",x,y+h-corner,corner,corner},{"se",x+w-corner,y+h-corner,corner,corner},
            {"n",x+corner,y,w-2*corner,grip},{"s",x+corner,y+h-grip,w-2*corner,grip},
            {"w",x,y+corner,grip,h-2*corner},{"e",x+w-grip,y+corner,grip,h-2*corner}}
        for _,r in ipairs(handles) do
            local id="##mvl_resize_"..r[1]
            u:at(r[2],r[3]);real.invisible_button(id,{r[4],r[5]})
            local active=real.is_item_active and real.is_item_active()
            local hover=real.is_item_hovered and real.is_item_hovered()
            if active or hover then
                u:rect(r[2],r[3],r[4],r[5],C.accent,2*k)
                if real.set_mouse_cursor then
                    local cursor=({n=3,s=3,w=4,e=4,nw=6,se=6,ne=5,sw=5})[r[1]]
                    real.set_mouse_cursor(cursor)
                end
            end
            local mouse=real.get_mouse and real.get_mouse()
            if active and mouse and not self.drag then
                if not self.resize then self.resize={id=id,edge=r[1],mx=mouse.x,my=mouse.y,x=x,y=y,w=w,h=h} end
                if self.resize.id==id then
                    local d=self.resize;local dx,dy=mouse.x-d.mx,mouse.y-d.my
                    local l,t,right,bottom=d.x,d.y,d.x+d.w,d.y+d.h
                    local minw,minh=math.min(900*k,sw-28),math.min(560*k,sh-28)
                    if d.edge:find("w",1,true) then l=clamp(d.x+dx,math.max(0,right-(sw-28)),right-minw) end
                    if d.edge:find("e",1,true) then right=clamp(d.x+d.w+dx,l+minw,math.min(sw,l+sw-28)) end
                    if d.edge:find("n",1,true) then t=clamp(d.y+dy,math.max(0,bottom-(sh-28)),bottom-minh) end
                    if d.edge:find("s",1,true) then bottom=clamp(d.y+d.h+dy,t+minh,math.min(sh,t+sh-28)) end
                    self.window_size={w=(right-l)/k,h=(bottom-t)/k}
                    self.window_position={x=l/math.max(1,sw-(right-l)),y=t/math.max(1,sh-(bottom-t))}
                end
            elseif self.resize and self.resize.id==id then self.resize=nil;self:save_settings() end
        end
        u:emit("line",{x=x+w-20*k,y=y+h-8*k,x2=x+w-8*k,y2=y+h-20*k,color=C.muted,width=1.5*k})
        u:emit("line",{x=x+w-13*k,y=y+h-8*k,x2=x+w-8*k,y2=y+h-13*k,color=C.muted,width=1.5*k})
    end
    function self:frame()
        u.commands={};self:tick()
        s.window_drawn=false
        if not self.active or not s.window_open then return end
        local size=real.get_display_size();local sw,sh=size.x,size.y
        if sw<400 or sh<300 then return end
        local k=math.min(self.scale,sw/1420,sh/860);u:begin(k)
        local interactive=reframework:is_drawing_ui()
        if not interactive then self.drag=nil;self.resize=nil;self.orb_free=nil end
        if s.window_hidden or not interactive then orb(sw,sh,interactive,math.max(.85,k));return end
        self.orb_status=nil
        local w,h=clamp(self.window_size.w*k,math.min(900*k,sw-28),sw-28),clamp(self.window_size.h*k,math.min(560*k,sh-28),sh-28)
        if s.window_center_requested then self.window_position={x=.5,y=.5};s.window_center_requested=false;self:save_settings() end
        local x,y=clamp(self.window_position.x*(sw-w),0,sw-w),clamp(self.window_position.y*(sh-h),0,sh-h)
        local side=210*k;local compact=w<1100*k;local top=(compact and 132 or 90)*k
        self.bounds={x=x,y=y,w=w,h=h,scale=k}
        real.set_next_window_pos({x,y});real.set_next_window_size({w,h})
        real.push_style_var(alpha_style,1.0)
        for _,id in ipairs({0,2,3,5,7,8,9,21,22,23,24}) do real.push_style_color(id,0) end
        local visible=real.begin_window("##MotionVfxD2DInputHost",true,1+2+4+8+32+256)
        local ok,err=pcall(function()
            if visible==false then return end
            s.window_drawn=true
            if self.file_dialog and self.file_dialog.active then
                u:rect(x,y,w,h,C.bg,14*k)
                self.file_dialog:draw(x+14*k,y+14*k,w-28*k,h-28*k,k)
                return
            end
            u:rect(x,y,w,h,C.bg,14*k);u:rect(x,y,side,h,C.sidebar,14*k)
            resize_edges(x,y,w,h,k,sw,sh)
            u:rect(x+side,y,1,h,C.line)
            u:emit("image",{path="MotionVfxLive/logo.png",x=x+18*k,y=y+20*k,w=48*k,h=48*k})
            u:label("MotionVfx",x+74*k,y+26*k,22*k,C.text)
            u:label("LIVE STUDIO",x+76*k,y+56*k,10*k,C.muted)
            if not self.resize then drag_handle("##mvl_title_drag",x,y,side,96*k,"window",sw,sh,w,h) end
            u:label("工作空间",x+24*k,y+112*k,11*k,C.muted)
            local tabs={{"actions","动作记录","01"},{"effects","特效记录","02"},{"edit","特效编辑","03"},{"pfb","PFB 编辑","04"},{"time","时间与回看","05"}}
            for i,tab in ipairs(tabs) do
                if u:button("##nav_"..tab[1],tab[3].."    "..tab[2],x+16*k,y+(140+(i-1)*55)*k,side-32*k,44*k,
                    self.page==tab[1] and not self.top and "selected") then
                    self.page=tab[1];self.top=nil
                    if tab[1]=="effects" then self:reset_effect_filter()end
                end
                if self.page==tab[1] and not self.top then u:rect(x+16*k,y+(151+(i-1)*55)*k,3*k,22*k,C.accent,2*k) end
            end
            u:rect(x+24*k,y+h-115*k,side-48*k,1,C.line)
            u:label(self:recording_active() and "●  画面录制中" or s.capture_snapshot.recording and "●  正在记录调用" or "●  编辑器就绪",x+24*k,y+h-92*k,13*k,
                (self:recording_active() or s.capture_snapshot.recording) and C.warning or C.green)
            u:label("v"..core.version,x+24*k,y+h-60*k,12*k,C.muted)
            local bx=x+side+24*k
            for i,tab in ipairs({{"cache","资源预缓存"},{"file","文件"},{"edit_tools","编辑"},{"settings","设置"}}) do
                if u:button("##top_"..tab[1],tab[2],bx,y+20*k,(i==1 and 140 or 84)*k,36*k,self.top==tab[1] and "selected") then
                    self.top=self.top==tab[1] and nil or tab[1]
                    if tab[1]=="cache" then refresh_cache() end
                end
                bx=bx+(i==1 and 150 or 94)*k
            end
            local action_y=y+(compact and 63 or 20)*k
            local slots=self.page=="actions" and self.action_source=="slots"
            local pfb=self.page=="pfb" and self.pfb
            local applicable=pfb and pfb.document and core.preview_pfb and pfb.target_path~="" or
                not pfb and (slots and self.action_document and (self.action_document.previewSlotCount or #(self.action_document.added or {}))>0 or not slots and s.document~=nil)
            if u:button("##top_apply",pfb and "热重载 PFB" or slots and "热重载槽位" or "应用特效参数",x+w-367*k,action_y,134*k,36*k,"primary",applicable and not s.ai_busy and not s.open_confirmation and not self.open_pending and not (core.action_edit_pending and core.action_edit_pending())) then
                if pfb then core.preview_pfb(pfb.document,pfb.target_path)
                elseif slots then core.preview_action(self.action_document)
                elseif not core.publish_document(true) then s.message=s.action_message end
            end
            if u:button("##top_restore","停止并还原",x+w-223*k,action_y,134*k,36*k,nil,not s.reloading) then
                self.replay_speed=0
                self.cache_epoch=self.cache_epoch+1;self.reapply_after_warm=false;self.apply_pending=false;self.imports={};self.warm_pending=false
                if self.desktop and self.desktop.mode=="record" and self.desktop.phase=="recording" then self.stop_record_pending=true end
                core.stop_and_restore();self.restoring=true;s.message="正在停止并还原资源与场景，请稍候。"
            end
            if u:button("##top_hide","−",x+w-78*k,y+23*k,30*k,29*k) then self:hide() end
            if u:button("##top_close","×",x+w-40*k,y+23*k,30*k,29*k) then s.window_open=false;s.window_hidden=false end
            local document_label=pfb and pfb.document and pfb.document.name or s.document and (s.document.name or "EFX") or "未打开特效文档"
            if s.verification and s.verification.message then document_label=document_label.."   ·   "..s.verification.message end
            u:label(document_label,x+side+25*k,y+top-23*k,12*k,C.muted,w-side-60*k)
            local px,py,pw,ph=x+side+20*k,y+top+8*k,w-side-40*k,h-top-65*k
            if s.open_confirmation or s.ai_busy then
                u:panel("waiting",px,py,pw,ph,function()
                    u:heading(s.ai_busy and "AI 正在操作" or "等待切换文档",
                        s.ai_busy and "本次操作结束后可继续编辑。停止并还原始终可用。" or "先处理当前文档的未保存修改。")
                end)
            elseif self.top then
                u:panel("top_workspace",px,py,pw,ph,function()
                    if self.top=="cache" then cache_page()
                    elseif self.top=="file" then file_page()
                    elseif self.top=="settings" then settings_page()
                    else
                        u:heading("编辑操作","修改仅在提交后写入当前文档。")
                        button("undo","撤销",100,function() core.ui_mutate("undo") end)
                        real.same_line();button("redo","重做",100,function() core.ui_mutate("redo") end)
                        button("structure","管理特效块与属性",210,function() self.page="edit";self.top=nil end)
                    end
                end)
            elseif self.page=="edit" then
                local narrow=pw<780*k
                local left=math.min(330*k,pw*.36)
                local suffix=self.edit_generation and ("_"..self.edit_generation) or ""
                local function properties()
                    -- Keep every existing typed field, expression curve and RGBA transaction.
                    adapter(core.ui_fields)
                    if s.selected then
                        if self.path_fields_source~=s.fields then
                            self.path_fields_source=s.fields;self.path_fields={}
                            for _,field in ipairs(s.fields or {}) do
                            if field.editable and (field.type=="System.String") and
                                (tostring(field.value):match("%.[a-z]+$") or field.path:lower():find("path",1,true)) then
                                self.path_fields[#self.path_fields+1]=field
                            end
                            end
                        end
                        for _,field in ipairs(self.path_fields) do
                                button("usecache"..field.path,"从预缓存选择："..core.parameter_label(field.path),300,function()
                                    self.path_target=field.path;self.top="cache";refresh_cache()
                                end)
                        end
                    end
                end
                if narrow then
                    local selected=s.selected
                    local key=selected and core.target_key(selected)
                    if key~=self.narrow_selection then self.narrow_selection=key;self.narrow_pane=key and "properties" or "structure" end
                    local pane=self.narrow_pane or "structure"
                    if u:button("##narrow_structure","特效块与属性",px,py,160*k,34*k,pane=="structure" and "selected") then self.narrow_pane="structure";pane="structure" end
                    if u:button("##narrow_properties","参数编辑",px+172*k,py,140*k,34*k,pane=="properties" and "selected") then self.narrow_pane="properties";pane="properties" end
                    u:panel(pane..suffix,px,py+44*k,pw,ph-44*k,pane=="structure" and function() adapter(core.ui_tree) end or properties)
                else
                    u:panel("structure"..suffix,px,py,left,ph,function() adapter(core.ui_tree) end)
                    u:panel("properties"..suffix,px+left+14*k,py,pw-left-14*k,ph,properties)
                end
            else
                u:panel("content_"..self.page,px,py,pw,ph,function()
                    if self.page=="actions" then show_actions() elseif self.page=="effects" then show_effects()
                    elseif self.page=="pfb" then if self.pfb then self.pfb:draw() else u.adapter.text("PFB 编辑模块未加载")end
                    else time_page() end
                end)
            end
            u:rect(x+side+20*k,y+h-43*k,w-side-40*k,1,C.line)
            local backend_error=not core.backend_ready() and "文件编辑服务未连接 · 请运行 MotionVfx Live 启动入口，连接恢复后可重试。"
            local status=s.bindings_restore_failed or s.scene_control_error or backend_error or s.message
            u:label(status,x+side+22*k,y+h-31*k,12*k,
                (s.bindings_restore_failed or s.scene_control_error or backend_error) and C.warning or C.muted,w-side-50*k)
            if s.open_confirmation then
                u:rect(x+w*.25,y+h*.35,w*.56,150*k,C.raised,10*k)
                u:label("当前有未提交的输入",x+w*.25+20*k,y+h*.35+18*k,20*k,C.warning)
                u:label("切换将放弃输入草稿；已提交修改仍保留。",x+w*.25+20*k,y+h*.35+53*k,14*k,C.text)
                if u:button("##confirm_doc","确认切换",x+w*.25+20*k,y+h*.35+93*k,120*k,34*k,"primary") then core.confirm_open() end
                if u:button("##cancel_doc","取消",x+w*.25+155*k,y+h*.35+93*k,100*k,34*k) then s.open_confirmation=nil end
            end
        end)
        real.end_window();real.pop_style_color(11);real.pop_style_var(1)
        s.debug_text_input_active=u.text_active
        if not ok then s.ui_error=tostring(err) end
    end
    local ok,prefs=pcall(json.load_file,"MotionVfxLive/workbench-settings.json")
    if ok and prefs and type(prefs.scale)=="number" then self.scale=math.max(.75,math.min(1.3,prefs.scale)) end
    if ok and type(prefs)=="table" then
        s.parameter_language=prefs.language=="en" and "en" or "zh"
        if type(prefs.position)=="table" and type(prefs.position.x)=="number" and type(prefs.position.y)=="number" then
            self.window_position={x=clamp(prefs.position.x,0,1),y=clamp(prefs.position.y,0,1)}
        end
        if type(prefs.size)=="table" and type(prefs.size.w)=="number" and type(prefs.size.h)=="number" then
            self.window_size={w=clamp(prefs.size.w,900,10000),h=clamp(prefs.size.h,560,10000)}
        end
        if ({left=true,right=true,top=true,bottom=true})[prefs.orbSide] then self.orb_side=prefs.orbSide end
        if type(prefs.orbT)=="number" then self.orb_t=clamp(prefs.orbT,0,1) end
    end
    local loaded,previous=pcall(json.load_file,"MotionVfxLive/desktop-recording-status.json")
    if loaded and previous then self.desktop=previous;self.recording_seen=previous.id end
    local replay_ok,latest=pcall(json.load_file,"MotionVfxLive/last-replay.json")
    if replay_ok and latest then self:load_replay(latest) end
    d2d.register(function() if self.active then u:init() end end,function()
        if self.active and core.active then
            local ok,err=pcall(function() u:render() end)
            if not ok then s.ui_error="D2D: "..tostring(err) end
        end
    end)
    self.widgets=u
    return self
end
return M
