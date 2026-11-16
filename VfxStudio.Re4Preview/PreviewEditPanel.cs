using System.Globalization;
using System.Numerics;
using System.Text;
using Hexa.NET.ImGui;
using VfxStudio.Re4Render;

namespace VfxStudio.Re4Preview;

internal sealed record PreviewEditCommand(string Action,long Revision=0,BillboardEdit? Billboard=null,
    LifeEdit? Life=null,string? SavePath=null,PreviewFieldBatch? Batch=null,int AttributeIndex=-1,string? DocumentId=null,int EntryPosition=-1,
    System.Text.Json.Nodes.JsonObject? Body=null);

internal sealed class PreviewEditPanel
{
    private string? documentId,attemptedInspection;
    private int entryPosition=-1,selected=-1,page;
    private PreviewPropertyDraft? draft;
    private string filter="",savePath="",cachedFilter="";
    private bool showReadOnly=true,cachedShowReadOnly;
    private PropertyRow[] rows=[],filtered=[];
    private sealed record PropertyRow(string Path,string Group,bool Editable,PreviewFieldInfo? Field=null,PreviewColorGroup? Color=null);
    public bool HasUnappliedChanges=>draft?.Dirty==true;
    public int SelectedAttribute=>selected;
    private PreviewGraphPick? pendingFocus;

    /// <summary>Opens an attribute (from the node graph). For another entry the attribute is kept until
    /// that entry becomes current. Refused while a draft has unapplied changes.</summary>
    public bool Focus(PreviewGraphPick pick)
    {
        if(HasUnappliedChanges) return false;
        if(pick.EntryPosition!=entryPosition) {pendingFocus=pick;return true;}
        if(selected!=pick.AttributeIndex) {selected=pick.AttributeIndex;draft=null;attemptedInspection=null;page=0;filter="";}
        return true;
    }

    public void Reset(PreviewEditorState state,bool discardDraft=false)
    {
        if(documentId!=state.Manifest?.DocumentId || entryPosition!=state.Manifest?.EntryPosition)
        {
            documentId=state.Manifest?.DocumentId;entryPosition=state.Manifest?.EntryPosition??-1;
            selected=pendingFocus is {} focus && focus.EntryPosition==entryPosition && state.Attributes?.Any(a=>a.Index==focus.AttributeIndex)==true
                ? focus.AttributeIndex
                : state.Attributes?.FirstOrDefault(a=>a.Type.StartsWith("Type",StringComparison.Ordinal))?.Index??state.Attributes?.FirstOrDefault()?.Index??-1;
            pendingFocus=null;
            draft=null;attemptedInspection=null;page=0;filter="";
        }
        if(discardDraft) {draft=null;attemptedInspection=null;}
        if(draft!=null && !draft.Matches(state) && !draft.Dirty) draft=null;
        if(draft?.Dirty!=true && state.Inspection is {} inspection && inspection.DocumentId==documentId &&
            inspection.EntryPosition==entryPosition && inspection.AttributeIndex==selected && inspection.Revision==state.Revision &&
            !ReferenceEquals(draft?.Inspection,inspection))
        {draft=new(inspection);BuildRows(inspection);}
    }
    private void BuildRows(PreviewInspection inspection)
    {
        var colors=inspection.ColorGroups.Where(c=>c.Storage=="rgba8" && c.Scale==255 && !c.Hdr && c.Channels.Length==4 &&
            c.Channels.All(ch=>inspection.Fields.Any(f=>f.Path==ch.Path))).ToArray();
        var hidden=colors.SelectMany(c=>c.Channels.Select(ch=>ch.Path).Append(c.Path.Length==0?"rgba":c.Path+".rgba")).ToHashSet(StringComparer.Ordinal);
        string Group(string path) {int i=path.IndexOfAny(['.','[']);return i>0?path[..i]:"基本字段";}
        rows=colors.Select(c=>new PropertyRow(c.Path,"颜色",c.Editable,Color:c))
            .Concat(inspection.Fields.Where(f=>!hidden.Contains(f.Path)).Select(f=>new PropertyRow(f.Path,Group(f.Path),f.Editable,Field:f))).ToArray();
        cachedFilter="\0";page=0;
    }
    private PreviewEditCommand Inspect(PreviewEditorState state)
    {
        attemptedInspection=$"{documentId}:{state.Revision}:{selected}";
        return new("inspect",state.Revision,AttributeIndex:selected,DocumentId:documentId);
    }

    public PreviewEditCommand? Draw(PreviewEditorState? state,OfflinePreviewManifest? displayed,bool busy,
        bool stressMode,string sessionDirectory,string? error)
    {
        if(state!=null) Reset(state);
        PreviewEditCommand? command=null;
        bool dirty=HasUnappliedChanges,current=draft?.Matches(state)==true;
        ImGui.SetNextWindowPos(new Vector2(850,12),ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(410,760),ImGuiCond.FirstUseEver);
        ImGui.Begin("EFX 属性编辑");
        ImGui.TextWrapped("选择已有属性编辑参数；应用后保留 tick 和播放／暂停状态。");
        ImGui.Text(state==null?"文档：准备中":$"文档 r{state.Revision} · {(state.Dirty?"有未保存修改":"已保存 / 原始状态")}");
        ImGui.Text(displayed==null?"画面：尚未加载":"画面：r"+displayed.Revision);
        ImGui.Text("预览状态："+(state?.Manifest?.Revision==state?.Revision && displayed?.Key==state?.Manifest?.Key?"已同步":"等待更新 / 可重试"));
        ImGui.BeginChild("提示",new Vector2(0,50));
        if(error!=null) ImGui.TextWrapped(error);
        if(state?.PublicationError!=null) ImGui.TextWrapped("预览发布失败："+state.PublicationError);
        if(stressMode) ImGui.TextWrapped("压力模式下参数编辑暂不开放。");
        if(dirty && !current) ImGui.TextWrapped("草稿来自旧修订，已保留原值。请丢弃草稿并刷新后再编辑。");
        ImGui.EndChild();

        bool unavailable=busy || stressMode || state?.Manifest==null || state.Attributes?.Length is not >0;
        ImGui.BeginDisabled(unavailable);
        ImGui.BeginDisabled(dirty);
        var attribute=state?.Attributes?.FirstOrDefault(a=>a.Index==selected);
        ImGui.SetNextItemWidth(-1);
        if(ImGui.BeginCombo("##属性",attribute==null?"选择属性":$"{attribute.Index}: {attribute.Type}"))
        {
            foreach(var a in state?.Attributes??[])
                if(ImGui.Selectable($"{a.Index}: {a.Type}",a.Index==selected))
                {selected=a.Index;draft=null;attemptedInspection=null;page=0;filter="";}
            ImGui.EndCombo();
        }
        if(ImGui.Button("刷新属性") && state!=null) {draft=null;command=Inspect(state);}
        ImGui.SameLine();ImGui.BeginDisabled(state?.CanUndo!=true);
        if(ImGui.Button("撤销")) command=new("undo");
        ImGui.EndDisabled();ImGui.SameLine();ImGui.BeginDisabled(state?.CanRedo!=true);
        if(ImGui.Button("重做")) command=new("redo");
        ImGui.EndDisabled();ImGui.EndDisabled();
        if(dirty)
        {
            if(ImGui.Button(current?"放弃草稿":"丢弃草稿并刷新") && state!=null) {draft=null;command=Inspect(state);}
            ImGui.SameLine();
        }
        var changes=draft?.Changes()??[];
        ImGui.BeginDisabled(!current || changes.Length is <1 or >64);
        if(ImGui.Button("应用本属性修改") && draft!=null) command=new("fields",Batch:new(draft.Inspection,changes));
        ImGui.EndDisabled();
        ImGui.Text(changes.Length>64?"超过单次 64 字段限制，请减少本次修改。":$"草稿修改 {changes.Length} 项；切换属性前请应用或放弃。");
        ImGui.SetNextItemWidth(160);ImGui.InputText("搜索字段",ref filter,256);
        ImGui.SameLine();if(ImGui.SmallButton("清空")) filter="";
        ImGui.SameLine();ImGui.Checkbox("显示只读",ref showReadOnly);
        ImGui.EndDisabled();

        // Inspect once per target/revision; failed reads wait for an explicit refresh.
        if(command==null && !unavailable && !dirty && (draft==null || !current) &&
            attemptedInspection!=$"{documentId}:{state!.Revision}:{selected}") command=Inspect(state!);
        if(draft!=null)
        {
            if(cachedFilter!=filter || cachedShowReadOnly!=showReadOnly)
            {
                filtered=rows.Where(r=>(showReadOnly || r.Editable) && (filter.Length==0 ||
                    r.Path.Contains(filter,StringComparison.OrdinalIgnoreCase) || r.Group.Contains(filter,StringComparison.OrdinalIgnoreCase))).ToArray();
                cachedFilter=filter;cachedShowReadOnly=showReadOnly;page=0;
            }
            int pages=Math.Max(1,(filtered.Length+63)/64);page=Math.Clamp(page,0,pages-1);
            if(ImGui.SmallButton("上一页")) page=Math.Max(0,page-1);
            ImGui.SameLine();if(ImGui.SmallButton("下一页")) page=Math.Min(pages-1,page+1);
            ImGui.SameLine();ImGui.Text($"{page+1}/{pages} 页 · {filtered.Length} 项");
        }
        ImGui.BeginChild("属性字段",new Vector2(0,Math.Max(160,ImGui.GetContentRegionAvail().Y-190)));
        ImGui.BeginDisabled(unavailable || !current);
        if(draft==null) ImGui.TextWrapped(busy?"正在读取属性…":"选择或刷新属性以读取字段。");
        else
        {
            if(filtered.Length==0) ImGui.TextWrapped("没有匹配字段，请清空搜索或显示只读字段。");
            foreach(var group in filtered.Skip(page*64).Take(64).GroupBy(r=>r.Group))
                if(ImGui.CollapsingHeader(group.Key,ImGuiTreeNodeFlags.DefaultOpen))
                    foreach(var row in group)
                    {
                        ImGui.PushID(row.Path);
                        if(row.Color is {} color) DrawColor(draft,color);
                        else if(row.Field is {} field) DrawField(draft,field,attribute?.Type);
                        ImGui.PopID();
                    }
            if(draft.Inspection.CurveCount>0) ImGui.TextWrapped("现有曲线可编辑字段值；图形曲线编辑后续加入。");
        }
        ImGui.EndDisabled();ImGui.EndChild();

        ImGui.Separator();
        ImGui.BeginDisabled(busy || state?.Manifest==null || stressMode);
        ImGui.SetNextItemWidth(240);ImGui.InputText("另存路径",ref savePath,1024);
        if(ImGui.Button("保存副本")) command=new("save",SavePath:string.IsNullOrWhiteSpace(savePath)?null:savePath);
        ImGui.SameLine();ImGui.BeginDisabled(state?.SavedPath==null || HasUnappliedChanges);
        if(ImGui.Button("打开已存副本")) command=new("open_saved");
        ImGui.EndDisabled();
        if(state?.SavedPath is {} saved) {ImGui.SetNextItemWidth(280);ImGui.InputText("已保存",ref saved,(uint)Encoding.UTF8.GetByteCount(saved)+1,ImGuiInputTextFlags.ReadOnly);}
        if(ImGui.Button("重试预览")) command=new("publish_offline_preview");
        ImGui.EndDisabled();ImGui.SameLine();ImGui.BeginDisabled(busy || HasUnappliedChanges);
        if(ImGui.Button("重新打开源文件")) command=new("open_source");
        ImGui.EndDisabled();
        ImGui.TextWrapped("保存只包含已应用参数；能保存的字段可能尚未参与当前预览渲染。");
        ImGui.TextDisabled("会话目录（悬停查看）");if(ImGui.IsItemHovered()) ImGui.SetTooltip(sessionDirectory);
        ImGui.End();return command;
    }

    private static void DrawColor(PreviewPropertyDraft draft,PreviewColorGroup color)
    {
        ImGui.TextUnformatted(color.Label);
        ImGui.BeginDisabled(!color.Editable);
        float[] c=color.Channels.Select(ch=>int.Parse(draft.Value(ch.Path),CultureInfo.InvariantCulture)/255f).ToArray();
        var value=new Vector4(c[0],c[1],c[2],c[3]);
        if(ImGui.ColorEdit4("RGBA",ref value)) draft.SetColor(color,[value.X,value.Y,value.Z,value.W]);
        ImGui.EndDisabled();
        if(!color.Editable) ImGui.TextWrapped("只读："+(color.Reason??"后台不允许编辑。"));
    }

    private static void DrawField(PreviewPropertyDraft draft,PreviewFieldInfo field,string? attributeType)
    {
        ImGui.TextWrapped(field.Path);
        if(ImGui.IsItemHovered()) ImGui.SetTooltip(field.Type+(field.OwnerType==null?"":"\n"+field.OwnerType));
        string value=draft.Value(field.Path);
        if(!field.Editable)
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##值",ref value,(uint)Math.Max(256,Encoding.UTF8.GetByteCount(value)+1),ImGuiInputTextFlags.ReadOnly);
            ImGui.TextWrapped("只读："+(field.Reason??"后台不允许编辑。"));return;
        }
        if(field.Type=="System.Boolean")
        {
            bool enabled=bool.TryParse(value,out bool b) && b;
            if(ImGui.Checkbox("启用",ref enabled)) draft.Set(field.Path,enabled?"true":"false");return;
        }
        if(field.Choices is {Length:>0})
        {
            ImGui.SetNextItemWidth(-1);
            if(ImGui.BeginCombo("##选项",value))
            {
                foreach(var choice in field.Choices) if(ImGui.Selectable(choice,choice==value)) {value=choice;draft.Set(field.Path,choice);}
                ImGui.EndCombo();
            }
            // A value outside the named choices (an unknown number) still needs a way to be seen and kept.
            if(Array.IndexOf(field.Choices,value)>=0) return;
        }
        else if(field.Type=="System.UInt32" && PreviewFieldBits.For(attributeType,field.Path) is {} bits &&
            PreviewBitField.TryParse(value,out uint flags))
        {
            if(DrawBits(bits,ref flags)) {value=flags.ToString(CultureInfo.InvariantCulture);draft.Set(field.Path,value);}
            if(ImGui.TreeNode("精确值")) {DrawExact(draft,field,ref value,false);ImGui.TreePop();}
            return;
        }
        // Exact text preserves large integers, flag combinations and unknown numeric values.
        DrawExact(draft,field,ref value,field.Choices is {Length:>0});
    }

    private static void DrawExact(PreviewPropertyDraft draft,PreviewFieldInfo field,ref string value,bool labelled)
    {
        ImGui.SetNextItemWidth(labelled?-70:-1);
        if(ImGui.InputText(labelled?"精确值":"##值",ref value,(uint)Math.Max(field.Type=="System.String"?32769:512,Encoding.UTF8.GetByteCount(value)+1))) draft.Set(field.Path,value);
    }

    /// <summary>Exclusive groups as combos, independent bits as checkboxes; bits outside the table are kept.</summary>
    private static bool DrawBits(PreviewBitField bits,ref uint flags)
    {
        bool changed=false;
        ImGui.TextDisabled($"0x{flags:X} · {bits.Summary}");
        foreach(var group in bits.Groups)
        {
            ImGui.PushID(group.Shift);
            uint part=group.Read(flags);
            if(group.IsSwitch)
            {
                bool on=part!=0;
                if(ImGui.Checkbox(group.Label,ref on)) {flags=group.Write(flags,on?1u:0u);changed=true;}
            }
            else
            {
                uint count=group.Width<=3?1u<<group.Width:(uint)(group.Options?.Length??0);
                ImGui.SetNextItemWidth(-130);
                if(ImGui.BeginCombo(group.Label,group.OptionLabel(part)))
                {
                    for(uint option=0;option<count;option++)
                        if(ImGui.Selectable(group.OptionLabel(option),option==part)) {flags=group.Write(flags,option);changed=true;}
                    ImGui.EndCombo();
                }
            }
            ImGui.PopID();
        }
        uint other=flags&~bits.KnownMask;
        if(other!=0) ImGui.TextDisabled($"其他位 0x{other:X}（含义未列出，保持原值）");
        return changed;
    }
}
