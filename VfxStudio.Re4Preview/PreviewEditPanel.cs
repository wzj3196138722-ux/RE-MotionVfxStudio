using System.Numerics;
using Hexa.NET.ImGui;
using VfxStudio.Re4Render;

namespace VfxStudio.Re4Preview;

internal sealed record PreviewEditCommand(string Action, long Revision=0, BillboardEdit? Billboard=null, LifeEdit? Life=null, string? SavePath=null);

internal sealed class PreviewEditPanel
{
    private long draftRevision=-1;
    private bool dirty;
    private Vector4 color, colorRange;
    private float colorRate;
    private Vector2 scalar, sizeX, sizeY;
    private int[] appear=[0,0], keep=[0,0], vanish=[0,0];
    private string savePath="";
    public bool HasUnappliedChanges => dirty;

    public void Reset(PreviewEditorState state)
    {
        var p=state.Parameters;
        if(p==null) return;
        draftRevision=state.Revision; dirty=false;
        color=new(p.Color[0]/255f,p.Color[1]/255f,p.Color[2]/255f,p.Color[3]/255f);
        colorRange=new(p.ColorRange[0]/255f,p.ColorRange[1]/255f,p.ColorRange[2]/255f,p.ColorRange[3]/255f);
        colorRate=p.ColorRate;
        scalar=new(p.SizeScalar[0],p.SizeScalar[1]);sizeX=new(p.SizeX[0],p.SizeX[1]);sizeY=new(p.SizeY[0],p.SizeY[1]);
        appear=(int[]?)p.Appear?.Clone()??[0,0];keep=(int[]?)p.Keep?.Clone()??[0,0];vanish=(int[]?)p.Vanish?.Clone()??[0,0];
    }

    public PreviewEditCommand? Draw(PreviewEditorState? state, OfflinePreviewManifest? displayed, bool busy, bool stressMode, string sessionDirectory, string? error)
    {
        PreviewEditCommand? command=null;
        ImGui.SetNextWindowPos(new Vector2(850,12),ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(410,740),ImGuiCond.FirstUseEver);
        ImGui.Begin("EFX 参数编辑");
        ImGui.TextWrapped("修改由 LiveEditor 保管。应用后从头播放，当前重载约需 5 秒。");
        if(state!=null)
        {
            ImGui.Text($"文档 r{state.Revision} · {(state.Dirty?"有未保存修改":"已保存 / 原始状态")}");
            ImGui.Text(displayed==null?"画面：尚未加载":"画面：r"+displayed.Revision);
            bool waiting=displayed?.Key!=state.Manifest?.Key || state.Manifest?.Revision!=state.Revision;
            ImGui.Text("预览状态："+(waiting?"等待更新 / 可重试":"已同步"));
        }
        else
        {
            ImGui.Text("文档：准备中");
            ImGui.Text("画面：尚未加载");
            ImGui.Text("预览状态：加载编辑后台");
        }
        if(!string.IsNullOrEmpty(error)) ImGui.TextWrapped(error);
        if(!string.IsNullOrEmpty(state?.PublicationError)) ImGui.TextWrapped("预览发布失败："+state.PublicationError);
        if(stressMode) ImGui.TextWrapped("压力模式使用临时出生数和寿命设置，参数编辑暂不开放。");
        if(state?.Parameters!=null && draftRevision<0) Reset(state);
        ImGui.BeginDisabled(busy || state?.Parameters==null || stressMode);
        if(ImGui.Button("刷新表单")) command=new("status");
        ImGui.SameLine();
        ImGui.BeginDisabled(state?.CanUndo!=true);
        if(ImGui.Button("撤销")) command=new("undo");
        ImGui.EndDisabled();ImGui.SameLine();
        ImGui.BeginDisabled(state?.CanRedo!=true);
        if(ImGui.Button("重做")) command=new("redo");
        ImGui.EndDisabled();
        ImGui.Separator();
        dirty |= ImGui.ColorEdit4("出生颜色",ref color);
        dirty |= ImGui.ColorEdit4("范围颜色",ref colorRange);
        ImGui.TextWrapped("范围颜色仅在属性启用随机颜色时生效。");
        dirty |= ImGui.InputFloat("颜色倍率",ref colorRate);
        ImGui.SetNextItemWidth(210);
        dirty |= ImGui.InputFloat2("整体 中心/幅度",ref scalar);
        ImGui.SetNextItemWidth(210);
        dirty |= ImGui.InputFloat2("宽度 中心/幅度",ref sizeX);
        ImGui.SetNextItemWidth(210);
        dirty |= ImGui.InputFloat2("高度 中心/幅度",ref sizeY);
        ImGui.BeginDisabled(draftRevision!=state?.Revision);
        if(ImGui.Button("应用颜色和尺寸")) command=new("billboard",draftRevision,new(color,colorRange,colorRate,scalar,sizeX,sizeY));
        ImGui.EndDisabled();
        ImGui.Separator();
        ImGui.Text("Life：最小值 / 最大值，单位为帧");
        ImGui.BeginDisabled(state?.Parameters?.LifeAttribute<0);
        dirty |= ImGui.InputInt2("淡入",ref appear[0]);
        dirty |= ImGui.InputInt2("保持",ref keep[0]);
        dirty |= ImGui.InputInt2("淡出",ref vanish[0]);
        ImGui.BeginDisabled(draftRevision!=state?.Revision);
        if(ImGui.Button("应用 Life")) command=new("life",draftRevision,Life:new(appear[0],appear[1],keep[0],keep[1],vanish[0],vanish[1]));
        ImGui.EndDisabled();ImGui.EndDisabled();
        if(dirty) ImGui.TextWrapped("表单有未应用的修改；保存副本仅包含已应用参数。");
        if(state!=null && draftRevision!=state.Revision) ImGui.TextWrapped("表单来自旧修订，请先刷新表单。");
        ImGui.Separator();
        ImGui.InputText("另存路径（可留空）",ref savePath,1024);
        if(ImGui.Button("保存副本")) command=new("save",SavePath:string.IsNullOrWhiteSpace(savePath)?null:savePath);
        ImGui.SameLine();
        ImGui.BeginDisabled(state?.SavedPath==null);
        if(ImGui.Button("打开已存副本")) command=new("open_saved");
        ImGui.EndDisabled();
        if(state?.SavedPath!=null) ImGui.TextWrapped("已保存："+state.SavedPath);
        if(ImGui.Button("重试预览")) command=new("publish_offline_preview");
        ImGui.EndDisabled();
        ImGui.BeginDisabled(busy);
        if(ImGui.Button("重新打开源文件")) command=new("open_source");
        ImGui.EndDisabled();
        ImGui.TextWrapped("会话目录："+sessionDirectory);
        ImGui.End();
        return command;
    }
}
