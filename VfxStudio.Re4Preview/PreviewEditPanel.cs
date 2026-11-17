using System.Globalization;
using System.Numerics;
using System.Text;
using Hexa.NET.ImGui;
using VfxStudio.Re4Render;

namespace VfxStudio.Re4Preview;

internal sealed record PreviewEditCommand(string Action,long Revision=0,BillboardEdit? Billboard=null,
    LifeEdit? Life=null,string? SavePath=null,PreviewFieldBatch? Batch=null,int AttributeIndex=-1,string? DocumentId=null,int EntryPosition=-1,
    System.Text.Json.Nodes.JsonObject? Body=null,object? Tag=null);

internal sealed class PreviewEditPanel
{
    private string? documentId,attemptedInspection;
    private int entryPosition=-1,selected=-1,page;
    private PreviewPropertyDraft? draft;
    private string filter="",savePath="",cachedFilter="";
    private bool showReadOnly=true,cachedShowReadOnly;
    private PropertyRow[] rows=[],filtered=[];
    /// <summary>One table row: a field, an RGBA8 colour, or sibling components (X/Y/Z, min/max, center/spread)
    /// edited side by side.</summary>
    /// <param name="Label">Name shown instead of the path (PtBehavior parameters show their parameter name).</param>
    /// <param name="Order">Sort key: PtBehavior parameters keep the script's parameter order.</param>
    private sealed record PropertyRow(string Path,string Group,bool Editable,PreviewFieldInfo? Field=null,PreviewColorGroup? Color=null,
        PreviewFieldInfo[]? Parts=null,string? Label=null,int Order=0);
    private const string BehaviorParameters="脚本参数",BehaviorStructure="参数结构（只读）";
    private static readonly string[][] ComponentSets=[["X","Y","Z","W"],["x","y","z","w"],["min","max"],["center","spread"],["R","G","B","A"],["r","g","b","a"]];
    public bool HasUnappliedChanges=>draft?.Dirty==true;
    public int SelectedAttribute=>selected;
    private PreviewGraphPick? pendingFocus;

    /// <summary>Opens an attribute (from the node graph). For another entry the attribute is kept until
    /// that entry becomes current. AttributeIndex -1 opens the entry itself: its Type attribute, or for the
    /// current entry the attribute already open. Refused while a draft has unapplied changes.</summary>
    public bool Focus(PreviewGraphPick pick)
    {
        if(HasUnappliedChanges) return false;
        if(pick.EntryPosition!=entryPosition) {pendingFocus=pick;return true;}
        if(pick.AttributeIndex<0) return true;
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
        // PtBehavior: parameters are properties[i] with the name in behaviorProperty and the value under variable.
        // Values are listed by parameter name, in parameter order; names, types and lengths go to a closed group.
        var names=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var f in inspection.Fields)
            if(System.Text.RegularExpressions.Regex.Match(f.Path,@"^(properties\[\d+\])\.behaviorProperty$") is {Success:true} m) names[m.Groups[1].Value]=f.Value;
        (string Group,string? Label,int Order) Place(string path)
        {
            var m=System.Text.RegularExpressions.Regex.Match(path,@"^(properties\[(\d+)\])\.(.*)$");
            if(names.Count>0 && m.Success && names.TryGetValue(m.Groups[1].Value,out var name))
            {
                int index=int.Parse(m.Groups[2].Value);
                string rest=m.Groups[3].Value;
                bool value=rest.StartsWith("variable.",StringComparison.Ordinal) &&
                    rest is not ("variable.unkn" or "variable.size" or "variable.re4_unkn0" or "variable.re4_unkn1") && !rest.StartsWith("variable.restData",StringComparison.Ordinal);
                return value ? (BehaviorParameters,name,index) : (BehaviorStructure,$"{name} · {rest}",100000+index);
            }
            int i=path.IndexOfAny(['.','[']);
            return (i>0?path[..i]:"基本字段",null,-1);
        }
        string Group(string path)=>Place(path).Group;
        PropertyRow Row(string path,bool editable,PreviewFieldInfo? field=null,PreviewColorGroup? color=null,PreviewFieldInfo[]? parts=null)
        {var (group,label,order)=Place(path);return new(path,group,editable,field,color,parts,label,order);}
        var list=colors.Select(c=>Row(c.Path,c.Editable,color:c)).ToList();
        var fields=inspection.Fields.Where(f=>!hidden.Contains(f.Path)).ToArray();
        for(int i=0;i<fields.Length;)
        {
            // Consecutive siblings named like components of one value (LocalScale.X/.Y/.Z, KeepFrame.min/.max)
            // share a row, as in the MHW:IB EFX editor.
            int n=Components(fields,i);
            if(n>=2)
            {
                var parts=fields[i..(i+n)];string prefix=parts[0].Path[..parts[0].Path.LastIndexOf('.')];
                list.Add(Row(prefix,parts.Any(f=>f.Editable),parts:parts));i+=n;
            }
            else {list.Add(Row(fields[i].Path,fields[i].Editable,field:fields[i]));i++;}
        }
        rows=list.OrderBy(r=>r.Order).ToArray();   // stable: other rows keep the inspector's order
        cachedFilter="\0";page=0;
    }
    /// <summary>How many fields from <paramref name="start"/> form one component row (0 or 1 when none).</summary>
    private static int Components(PreviewFieldInfo[] fields,int start)
    {
        static (string Prefix,string Leaf)? Split(PreviewFieldInfo f)
        {
            int dot=f.Path.LastIndexOf('.');
            if(dot<=0 || f.Choices is {Length:>0} || f.Type=="System.Boolean") return null;
            return (f.Path[..dot],f.Path[(dot+1)..]);
        }
        if(Split(fields[start]) is not {} first) return 0;
        // Pair structs (CenterSpread, MinMaxI, via.Range, ...) are recognised by their type, whatever the member names.
        if(PreviewPairKind.For(fields[start].OwnerType)!=null)
            return start+1<fields.Length && Split(fields[start+1]) is {} second && second.Prefix==first.Prefix &&
                fields[start+1].OwnerType==fields[start].OwnerType ? 2 : 1;
        var set=ComponentSets.FirstOrDefault(c=>c[0]==first.Leaf);
        if(set==null) return 0;
        int n=1;
        while(n<set.Length && start+n<fields.Length && Split(fields[start+n]) is {} next && next.Prefix==first.Prefix && next.Leaf==set[n]) n++;
        return n;
    }

    private PreviewEditCommand Inspect(PreviewEditorState state)
    {
        attemptedInspection=$"{documentId}:{state.Revision}:{selected}";
        return new("inspect",state.Revision,AttributeIndex:selected,DocumentId:documentId);
    }

    /// <param name="clipboardKind">The backend copy buffer's kind ("attribute" or "entry"), or null.</param>
    public PreviewEditCommand? Draw(PreviewEditorState? state,OfflinePreviewManifest? displayed,bool busy,
        bool stressMode,string sessionDirectory,string? error,string? clipboardKind=null,string? clipboardType=null)
    {
        if(state!=null) Reset(state);
        PreviewEditCommand? command=null;
        bool dirty=HasUnappliedChanges,current=draft?.Matches(state)==true;
        ImGui.SetNextWindowPos(new Vector2(850,12),ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(410,760),ImGuiCond.FirstUseEver);
        ImGui.Begin("EFX 属性编辑");
        // One status line; what each part means is in its tooltip.
        bool synced=state?.Manifest?.Revision==state?.Revision && displayed?.Key==state?.Manifest?.Key;
        ImGui.TextDisabled(state==null?"文档准备中":
            $"r{state.Revision}{(state.Dirty?" · 有未保存修改":"")} · 画面 {(displayed==null?"未加载":"r"+displayed.Revision)}{(synced?"":" · 等待更新")}");
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("r 是文档修订号，每应用一次修改加 1；“有未保存修改”指已应用的修改还没保存成 EFX 副本。\n"+
            "画面是 3D 视图正在显示的修订。应用修改后保留 tick 和播放状态。\n从属性目录把属性拖到这个面板上可以添加。");
        var notes=new List<string>();
        if(error!=null) notes.Add(error);
        if(state?.PublicationError!=null) notes.Add("预览发布失败："+state.PublicationError);
        if(stressMode) notes.Add("压力模式下参数编辑暂不开放。");
        if(dirty && !current) notes.Add("草稿来自旧修订，已保留原值。请丢弃草稿并刷新后再编辑。");
        foreach(var note in notes)
        {
            ImGui.PushStyleColor(ImGuiCol.Text,new Vector4(1,.75f,.35f,1));ImGui.TextWrapped(note.Replace("%","%%"));ImGui.PopStyleColor();
        }

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
        // Toolbar: undo/redo, then the selected attribute's structure edits (each one revision, undoable).
        ImGui.BeginDisabled(state?.CanUndo!=true);
        if(ImGui.Button("撤销")) command=new("undo");
        ImGui.EndDisabled();ImGui.SameLine();ImGui.BeginDisabled(state?.CanRedo!=true);
        if(ImGui.Button("重做")) command=new("redo");
        ImGui.EndDisabled();
        if(attribute!=null && state?.Manifest is {} m)
        {
            var existing=state.Attributes!.Select(a=>a.Type).ToArray();
            ImGui.SameLine();ImGui.TextDisabled("|");ImGui.SameLine();
            if(ImGui.Button("复制")) command=Structure(new("copy_attribute",m.EntryPosition,attribute.Index));
            if(ImGui.IsItemHovered()) ImGui.SetTooltip("复制这个属性（可以粘贴到其他 entry 或其他文件）");
            ImGui.SameLine();ImGui.BeginDisabled(clipboardKind!="attribute");
            if(ImGui.Button("粘贴")) command=Structure(new("paste_attribute",m.EntryPosition));
            ImGui.EndDisabled();
            if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(clipboardKind=="attribute"?"粘贴 "+clipboardType:"先复制一个属性（可以来自其他文件）");
            ImGui.SameLine();
            string? refusal=PreviewStructureMenu.RemoveRefusal(attribute.Type,existing);
            ImGui.BeginDisabled(refusal!=null);
            if(ImGui.Button("删除")) command=Structure(new("remove_attribute",m.EntryPosition,attribute.Index));
            ImGui.EndDisabled();
            if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(refusal??"删除这个属性");
        }
        ImGui.SameLine();
        if(ImGui.Button("刷新") && state!=null) {draft=null;command=Inspect(state);}
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("重新读取这个属性的字段");
        ImGui.EndDisabled();
        var changes=draft?.Changes()??[];
        if(dirty)
        {
            ImGui.BeginDisabled(!current || changes.Length is <1 or >64);
            if(ImGui.Button($"应用修改（{changes.Length}）") && draft!=null) command=new("fields",Batch:new(draft.Inspection,changes));
            ImGui.EndDisabled();ImGui.SameLine();
            if(ImGui.Button(current?"放弃草稿":"丢弃草稿并刷新") && state!=null) {draft=null;command=Inspect(state);}
            ImGui.SameLine();ImGui.TextDisabled(changes.Length>64?"一次最多 64 项":"切换属性前请应用或放弃");
        }
        ImGui.SetNextItemWidth(Math.Max(80,ImGui.GetContentRegionAvail().X-110));ImGui.InputTextWithHint("##搜索字段","搜索字段",ref filter,256);
        ImGui.SameLine();ImGui.Checkbox("只读",ref showReadOnly);
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("也显示只读字段");
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
            if(pages>1)
            {
                if(ImGui.SmallButton("上一页")) page=Math.Max(0,page-1);
                ImGui.SameLine();if(ImGui.SmallButton("下一页")) page=Math.Min(pages-1,page+1);
                ImGui.SameLine();ImGui.Text($"{page+1}/{pages} 页 · {filtered.Length} 项");
            }
        }
        float footer=ImGui.GetFrameHeightWithSpacing()+ImGui.GetStyle().ItemSpacing.Y*2+(state?.SavedPath!=null?ImGui.GetTextLineHeightWithSpacing():0);
        ImGui.BeginChild("属性字段",new Vector2(0,Math.Max(120,ImGui.GetContentRegionAvail().Y-footer)));
        ImGui.BeginDisabled(unavailable || !current);
        if(draft==null) ImGui.TextWrapped(busy?"正在读取属性…":"选择或刷新属性以读取字段。");
        else
        {
            if(filtered.Length==0) ImGui.TextWrapped("没有匹配字段，请清空搜索或显示只读字段。");
            // Label | value table. A first path segment with several rows (a struct, an array) gets a header;
            // single rows stay flat with their full name.
            foreach(var group in filtered.Skip(page*64).Take(64).GroupBy(r=>r.Group))
            {
                var members=group.ToArray();
                bool header=(members.Length>1 && group.Key!="基本字段") || group.Key is BehaviorParameters or BehaviorStructure;
                if(header && !ImGui.TreeNodeEx(group.Key,(group.Key==BehaviorStructure?0:ImGuiTreeNodeFlags.DefaultOpen)|ImGuiTreeNodeFlags.SpanAvailWidth)) continue;
                if(ImGui.BeginTable("##字段表",2,ImGuiTableFlags.Resizable|ImGuiTableFlags.BordersInnerV|ImGuiTableFlags.RowBg))
                {
                    ImGui.TableSetupColumn("名称",ImGuiTableColumnFlags.WidthStretch,.4f);
                    ImGui.TableSetupColumn("值",ImGuiTableColumnFlags.WidthStretch,.6f);
                    foreach(var row in members)
                    {
                        ImGui.PushID(row.Path);
                        ImGui.TableNextRow();ImGui.TableNextColumn();
                        string label=row.Label ?? (header && row.Path.StartsWith(group.Key,StringComparison.Ordinal) && row.Path.Length>group.Key.Length
                            ? row.Path[group.Key.Length..].TrimStart('.') : row.Path);
                        if(row.Label==null && row.Color is {} c && c.Label.Length>0) label=c.Label;
                        var pair=row.Parts is {Length:2} pp ? PreviewPairKind.For(pp[0].OwnerType) : null;
                        var semantic=(row.Field??row.Parts?[0])?.Semantic;
                        DrawLabel(label,row.Editable,pair,semantic);
                        if(ImGui.IsItemHovered()) ImGui.SetTooltip(RowTooltip(row,pair,semantic).Replace("%","%%"));
                        ImGui.TableNextColumn();
                        if(row.Color is {} color) DrawColor(draft,color);
                        else if(row.Parts is {} parts) DrawParts(draft,parts,PreviewPairKind.For(parts[0].OwnerType));
                        else if(row.Field is {} prefab && row.Label=="DecalPrefab" && prefab.Editable) DrawDecalPrefab(draft,prefab);
                        else if(row.Field is {} field) DrawField(draft,field);
                        ImGui.PopID();
                    }
                    ImGui.EndTable();
                }
                if(header) ImGui.TreePop();
            }
            if(draft.Inspection.CurveCount>0) ImGui.TextWrapped("现有曲线可编辑字段值；图形曲线编辑后续加入。");
        }
        ImGui.EndDisabled();ImGui.EndChild();

        // Saving writes the whole EFX file (every entry), never the source: see the tooltip.
        ImGui.Separator();
        ImGui.BeginDisabled(busy || state?.Manifest==null || stressMode);
        if(ImGui.Button("保存整个 EFX 为副本")) command=new("save",SavePath:string.IsNullOrWhiteSpace(savePath)?null:savePath);
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(
            "把整个 EFX 文件（所有 entry 已应用的修改，不含没应用的草稿）写成一个新文件。\n"+
            "不会覆盖源文件，也不能写进游戏目录或解包目录。每次保存都生成一个新文件。\n"+
            "路径在“更多…”里设置；留空时存到会话目录，文件名带修订号。");
        ImGui.SameLine();ImGui.BeginDisabled(busy || state?.SavedPath==null || HasUnappliedChanges);
        if(ImGui.Button("打开副本")) command=new("open_saved");
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("把上次保存的副本作为当前文档打开，确认它能正确读回");
        ImGui.SameLine();
        if(ImGui.Button("更多…")) ImGui.OpenPopup("文件操作");
        if(ImGui.BeginPopup("文件操作"))
        {
            ImGui.SetNextItemWidth(360);ImGui.InputTextWithHint("另存路径","留空 = 会话目录",ref savePath,1024);
            if(state?.SavedPath is {} savedFull) {ImGui.SetNextItemWidth(360);ImGui.InputText("上次保存",ref savedFull,(uint)Encoding.UTF8.GetByteCount(savedFull)+1,ImGuiInputTextFlags.ReadOnly);}
            ImGui.BeginDisabled(busy || state?.Manifest==null || stressMode);
            if(ImGui.Button("重试预览")) {command=new("publish_offline_preview");ImGui.CloseCurrentPopup();}
            ImGui.EndDisabled();
            if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("预览发布失败时，用当前修订重新生成 3D 视图");
            ImGui.SameLine();ImGui.BeginDisabled(busy || HasUnappliedChanges);
            if(ImGui.Button("重新打开源文件")) {command=new("open_source");ImGui.CloseCurrentPopup();}
            ImGui.EndDisabled();
            if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("放弃当前文档，从源文件重新打开");
            ImGui.TextDisabled("会话目录（悬停查看）");if(ImGui.IsItemHovered()) ImGui.SetTooltip(sessionDirectory);
            ImGui.EndPopup();
        }
        if(state?.SavedPath is {} saved)
        {
            ImGui.PushStyleColor(ImGuiCol.Text,ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);ImGui.TextUnformatted("已保存："+Path.GetFileName(saved));ImGui.PopStyleColor();
            if(ImGui.IsItemHovered()) ImGui.SetTooltip(saved);
        }
        // The whole panel accepts attributes dragged from the catalogue, for the current entry.
        if(state?.Manifest!=null && state.Attributes is {} attributes)
        {
            var existing=attributes.Select(a=>a.Type).ToArray();
            string? entryType=existing.FirstOrDefault(PreviewAttributeCatalog.IsTypeAttribute);
            var min=ImGui.GetWindowPos();
            if(PreviewCatalogPanel.DropTarget(min,min+ImGui.GetWindowSize(),ImGui.GetID("属性目录拖放"),
                t=>busy?"编辑器忙，稍候再试":stressMode?"压力模式下不能编辑":dirty?"先应用或放弃属性草稿":PreviewCatalogPanel.Refusal(t,entryType,existing)) is {} dropped)
                command=Structure(new("add_attribute",entryPosition,Type:dropped));
        }
        ImGui.End();return command;
    }

    private static PreviewEditCommand Structure(PreviewStructureRequest request) => new("structure",Tag:request);

    private static void DrawColor(PreviewPropertyDraft draft,PreviewColorGroup color)
    {
        ImGui.BeginDisabled(!color.Editable);
        float[] c=color.Channels.Select(ch=>int.Parse(draft.Value(ch.Path),CultureInfo.InvariantCulture)/255f).ToArray();
        var value=new Vector4(c[0],c[1],c[2],c[3]);
        ImGui.SetNextItemWidth(-1);
        if(ImGui.ColorEdit4("##RGBA",ref value)) draft.SetColor(color,[value.X,value.Y,value.Z,value.W]);
        ImGui.EndDisabled();
        if(!color.Editable && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("只读："+(color.Reason??"后台不允许编辑。"));
    }

    /// <summary>EffectDecal's prefab: the prefabs RE4 decals use (most used first), or any path typed below.</summary>
    private static void DrawDecalPrefab(PreviewPropertyDraft draft,PreviewFieldInfo field)
    {
        string value=draft.Value(field.Path);
        ImGui.SetNextItemWidth(-1);
        if(ImGui.BeginCombo("##贴花",Path.GetFileNameWithoutExtension(value)))
        {
            foreach(var (path,uses) in PreviewAttributeCatalog.Re4.Behaviors.DecalPrefabs)
            {
                if(ImGui.Selectable($"{Path.GetFileNameWithoutExtension(path)}  ·  RE4 {uses} 处##{path}",string.Equals(path,value,StringComparison.OrdinalIgnoreCase)))
                    draft.Set(field.Path,path);
                if(ImGui.IsItemHovered()) ImGui.SetTooltip(path);
            }
            ImGui.EndCombo();
        }
        if(ImGui.IsItemHovered()) ImGui.SetTooltip(value+"\n列表是 RE4 贴花用过的 46 个预制体；也可以在下面直接填路径。");
        DrawExact(draft,field,ref value,false);
    }

    /// <summary>Field name, then the unit and the pair reading (dimmed; orange when the pair's use is unconfirmed).</summary>
    private static void DrawLabel(string label,bool editable,PreviewPairKind? pair,PreviewFieldSemantic? semantic)
    {
        ImGui.AlignTextToFramePadding();
        var dim=ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];
        ImGui.BeginGroup();
        if(editable) ImGui.TextUnformatted(label);
        else {ImGui.PushStyleColor(ImGuiCol.Text,dim);ImGui.TextUnformatted(label);ImGui.PopStyleColor();}
        string note=string.Join(" · ",new[]{semantic?.Unit,pair?.Label}.Where(n=>n!=null));
        if(note.Length>0)
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text,pair?.Unconfirmed==true?new Vector4(1,.75f,.35f,1):dim);
            ImGui.TextUnformatted(note);ImGui.PopStyleColor();
        }
        else if(semantic?.Help!=null) {ImGui.SameLine();ImGui.TextDisabled("(?)");}
        ImGui.EndGroup();
    }

    /// <summary>Meaning, unit and pair formula first; the technical path and types after a blank line.</summary>
    private static string RowTooltip(PropertyRow row,PreviewPairKind? pair,PreviewFieldSemantic? semantic)
    {
        var lines=new List<string>();
        if(semantic?.Text is {Length:>0} text) lines.Add(text);
        if(pair!=null) lines.Add($"{pair.Label}：{pair.Formula}");
        if(semantic?.Bits is {} bits) lines.Add("位："+bits.Summary);
        if(lines.Count>0) lines.Add("");
        lines.Add(row.Field is {} tf ? tf.Path+"\n"+tf.Type+(tf.OwnerType==null?"":"\n"+tf.OwnerType)
            : row.Parts is {} tp ? string.Join("\n",tp.Select(p=>p.Path+"  "+p.Type))+(tp[0].OwnerType==null?"":"\n"+tp[0].OwnerType) : row.Path);
        return string.Join("\n",lines);
    }

    /// <summary>Components of one value side by side, each as exact text. Pairs show their reading between the
    /// boxes (center ± spread, [min, max), ...).</summary>
    private static void DrawParts(PreviewPropertyDraft draft,PreviewFieldInfo[] parts,PreviewPairKind? pair)
    {
        float spacing=ImGui.GetStyle().ItemInnerSpacing.X;
        string[] glue=pair==null?[]:[pair.Prefix,pair.Separator,pair.Suffix];
        float text=glue.Where(g=>g.Length>0).Sum(g=>ImGui.CalcTextSize(g).X+spacing);
        float width=(ImGui.GetContentRegionAvail().X-text-spacing*(parts.Length-1))/parts.Length;
        if(pair!=null && pair.Prefix.Length>0) {ImGui.AlignTextToFramePadding();ImGui.TextUnformatted(pair.Prefix);ImGui.SameLine(0,spacing);}
        for(int i=0;i<parts.Length;i++)
        {
            var part=parts[i];
            if(i>0)
            {
                ImGui.SameLine(0,spacing);
                if(pair!=null) {ImGui.AlignTextToFramePadding();ImGui.TextUnformatted(pair.Separator);ImGui.SameLine(0,spacing);}
            }
            ImGui.PushID(i);
            string value=draft.Value(part.Path);
            ImGui.SetNextItemWidth(Math.Max(20,width));
            if(ImGui.InputText("##值",ref value,512,part.Editable?ImGuiInputTextFlags.None:ImGuiInputTextFlags.ReadOnly) && part.Editable) draft.Set(part.Path,value);
            string name=part.Path[(part.Path.LastIndexOf('.')+1)..];
            if(pair!=null && i<pair.Parts.Length) name=$"{pair.Parts[i]}（{name}）";
            if(ImGui.IsItemHovered()) ImGui.SetTooltip((name+(part.Editable?"":"\n只读："+(part.Reason??"后台不允许编辑。"))).Replace("%","%%"));
            ImGui.PopID();
        }
        if(pair!=null && pair.Suffix.Length>0) {ImGui.SameLine(0,spacing);ImGui.AlignTextToFramePadding();ImGui.TextUnformatted(pair.Suffix);}
    }

    private static void DrawField(PreviewPropertyDraft draft,PreviewFieldInfo field)
    {
        string value=draft.Value(field.Path);
        if(!field.Editable)
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##值",ref value,(uint)Math.Max(256,Encoding.UTF8.GetByteCount(value)+1),ImGuiInputTextFlags.ReadOnly);
            if(ImGui.IsItemHovered()) ImGui.SetTooltip("只读："+(field.Reason??"后台不允许编辑。"));return;
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
        else if(field.Type=="System.UInt32" && field.Semantic?.Bits is {} bits &&
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

    /// <summary>Exclusive groups as combos, independent bits as checkboxes. Bits with no documented meaning are
    /// listed as "位 N" (set ones directly, the rest folded), so every bit stays reachable.</summary>
    private static bool DrawBits(PreviewBitField bits,ref uint flags)
    {
        bool changed=false;
        ImGui.TextDisabled($"0x{flags:X} · {bits.Summary}");
        foreach(var group in bits.Groups)
        {
            ImGui.PushID(group.Shift);
            ImGui.BeginDisabled(group.Locked);
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
            ImGui.EndDisabled();
            if(group.Locked && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("这一位决定文件里有没有对应字段，只能在“精确值”里改。");
            ImGui.PopID();
        }
        uint unknown=~bits.KnownMask;
        for(int bit=0;bit<32;bit++) if(((unknown&flags)>>bit&1)!=0 && UnknownBit(bit,ref flags)) changed=true;
        if((unknown&~flags)!=0 && ImGui.TreeNode("其他位（含义未列出）"))
        {
            for(int bit=0;bit<32;bit++) if(((unknown&~flags)>>bit&1)!=0 && UnknownBit(bit,ref flags)) changed=true;
            ImGui.TreePop();
        }
        return changed;
    }

    private static bool UnknownBit(int bit,ref uint flags)
    {
        bool on=(flags>>bit&1)!=0;
        if(!ImGui.Checkbox($"位 {bit}（未列出）##未知位{bit}",ref on)) return false;
        flags=on?flags|1u<<bit:flags&~(1u<<bit);return true;
    }
}
