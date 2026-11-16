using System.Numerics;
using ReeLib;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Basic;
using ReeLib.Efx.Structs.Main;
using Silk.NET.OpenGL;

namespace VfxStudio.Re4Render;

/// <param name="Position">Position in the file's entry list (the editor's entryPosition). Entries embedded in
/// a child Action have a negative key instead (<see cref="PreviewScene.ChildKey"/>), so exclusion maps keyed by
/// position cover them too.</param>
/// <param name="Index">The entry's stored index field (frame entry tables carry this value).</param>
/// <param name="Excluded">Why the entry is not simulated, or null when it is.</param>
/// <param name="Logic">Simulated but not drawn: a NoDraw entry that fires child Actions.</param>
/// <param name="Offset">Byte offset of the entry in the file (embedded entries: inside the PlayEmitter data).</param>
/// <param name="Actions">PtLife triggers of a top-level entry whose Action the preview runs (Action indices).</param>
/// <param name="Action">For an embedded entry: the Action that plays it, and its position in the embedded file.</param>
public sealed record PreviewSceneEntry(int Position, int Index, string Name, string Type, PreviewDocument? Document, string? Excluded,
    bool Logic = false, int Offset = 0, IReadOnlyList<int>? Actions = null, int? Action = null, int? Child = null)
{
    public bool Shown => (Document != null || Logic) && Excluded == null;
    public bool Embedded => Action != null;
    /// <summary>[position] for top-level entries, [A action.child] for embedded ones.</summary>
    public string Label => Embedded ? $"[A{Action}.{Child}]" : $"[{Position}]";
}

/// <summary>Every entry of one EFX file, with the subset the native worker simulates. The worker's
/// frame entry table follows <see cref="Shown"/> order, so the table position of an entry is its
/// index in <see cref="Shown"/>: top-level entries in file order (drawn ones and NoDraw trigger carriers),
/// then the entries embedded in the child Actions those entries fire (one table per Action and embedded
/// entry, holding every running instance).</summary>
public sealed class PreviewScene
{
    /// <summary>Entries one native load accepts (re4host.py and FrameDecoder).</summary>
    public const int MaxEntries = 64;

    public string Path { get; }
    public IReadOnlyList<PreviewSceneEntry> Entries { get; }
    /// <summary>Entries embedded in child Actions that the scene runs (or left out, with the reason).</summary>
    public IReadOnlyList<PreviewSceneEntry> Children { get; }
    public IReadOnlyList<PreviewSceneEntry> Shown { get; }
    /// <summary>The file's structure (entries, Actions, trigger links), read with the same file.</summary>
    public PreviewEffectGraph Graph { get; }

    private PreviewScene(string path, IReadOnlyList<PreviewSceneEntry> entries, IReadOnlyList<PreviewSceneEntry> children, PreviewEffectGraph graph)
    {
        Path = path; Entries = entries; Children = children; Graph = graph;
        Shown = entries.Where(e => e.Shown).Concat(children.Where(e => e.Shown)).ToArray();
    }

    /// <summary>Exclusion key of an embedded entry (negative, never a file position).</summary>
    public static int ChildKey(int action, int child) => -1 - (action * 1024 + child);

    public int TableOf(int position)
    {
        for (int i = 0; i < Shown.Count; i++) if (Shown[i].Position == position) return i;
        return -1;
    }

    public PreviewSceneEntry? AtTable(int table) => table >= 0 && table < Shown.Count ? Shown[table] : null;

    /// <summary>Native load command for the shown entries. <paramref name="stressParticles"/> applies only
    /// to the entry at <paramref name="stressPosition"/>.</summary>
    public object LoadCommand(int stressParticles = 0, int stressPosition = -1)
    {
        if (Shown.Count == 0) throw new NotSupportedException("此文件没有可预览的 entry。" + string.Join("；", Entries.Where(e => e.Excluded != null).Take(3).Select(e => $"[{e.Position}] {e.Excluded}")));
        var uvs = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var entry in Shown)
        {
            if (entry.Document is not { } document || string.IsNullOrEmpty(document.UvsPath)) continue;
            var counts = document.Sequences.Select(s => s.Length).ToArray();
            if (uvs.TryGetValue(document.UvsPath, out var existing) && !existing.SequenceEqual(counts))
                throw new InvalidDataException("同一 UVS 资源在不同 entry 中读出的帧数不同：" + document.UvsPath);
            uvs[document.UvsPath] = counts;
        }
        return new
        {
            command = "load",
            entries = Shown.Where(e => !e.Embedded).Select(e => new { path = Path, offset = e.Offset, stress_particles = e.Position == stressPosition ? stressParticles : 0,
                ribbon_points = e.Document?.RibbonPointsPerParticle ?? 0, logic = e.Logic,
                triggers = (e.Actions ?? []).Select(a => new { type = PtLifeTypeId, action = a }).ToArray() }).ToArray(),
            // Every Action a shown entry fires, with its embedded entries in table order.
            actions = Shown.Where(e => e.Embedded).GroupBy(e => e.Action!.Value)
                .Select(g => new { index = g.Key, entries = g.Select(c => new { path = Path, offset = c.Offset }).ToArray() }).ToArray(),
            uvs_resources = uvs
        };
    }

    /// <summary>PtLife's attribute type id in RE4 (re4host.py PTLIFE_TYPE).</summary>
    private const int PtLifeTypeId = 131;
    /// <summary>Drawn kinds the preview runs inside child Actions (ribbons not yet).</summary>
    private static bool ChildKind(PreviewTypeKind? kind) => kind is PreviewTypeKind.Billboard3D or PreviewTypeKind.Polygon or PreviewTypeKind.Billboard2D;

    /// <param name="excluded">Entries to leave out, by position, with the reason (for example a native
    /// fault seen earlier in this document).</param>
    public static PreviewScene Read(string path, Re4ResourceSource source, IReadOnlyDictionary<int, string>? excluded = null)
    {
        path = System.IO.Path.GetFullPath(path);
        EfxFile file;
        using (var stream = File.OpenRead(path))
        {
            file = new EfxFile(new FileHandler(stream, path) { FileVersion = (int)EfxVersion.RE4 });
            if (!file.Read()) throw new InvalidDataException("无法读取 EFX 文件：" + path);
        }
        // Actions the preview can run: a PlayEmitter with at least one embedded entry of a supported kind.
        var playable = new Dictionary<int, EFXAttributePlayEmitter>();
        for (int a = 0; a < file.Actions.Count; a++)
            if (file.Actions[a].Attributes.OfType<EFXAttributePlayEmitter>().FirstOrDefault() is { efxrData: { } inner } play &&
                inner.Entries.Any(e => ChildKind(PreviewDocument.KindOf(e))))
                playable[a] = play;
        var entries = new List<PreviewSceneEntry>();
        int shown = 0;
        for (int position = 0; position < file.Entries.Count; position++)
        {
            var entry = file.Entries[position];
            string type = entry.TypeAttribute?.type.ToString() ?? "无";
            string? reason = null; PreviewDocument? document = null;
            var actions = entry.Attributes.OfType<ReeLib.Efx.Structs.Pt.EFXAttributePtLife>().Select(l => l.ActionIndex).Where(playable.ContainsKey).Distinct().ToArray();
            bool logic = false;
            if (excluded != null && excluded.TryGetValue(position, out var known)) reason = known;
            else if (entry.TypeAttribute == null) reason = "没有 Type 属性，不产生粒子。";
            else if (entry.TypeAttribute is EFXAttributeTypeNoDraw && actions.Length > 0)
            {
                // Not drawn, but its particles fire child Actions: simulated without a renderer.
                if (shown >= MaxEntries) reason = $"一次最多预览 {MaxEntries} 个 entry。";
                else { logic = true; shown++; }
            }
            else if (entry.TypeAttribute is EFXAttributeTypeNoDraw) reason = "NoDraw 不绘制，也没有预览能运行的子 Action。";
            else if (PreviewDocument.KindOf(entry) == null) reason = $"类型 {type} 尚未支持。";
            else if (shown >= MaxEntries) reason = $"一次最多预览 {MaxEntries} 个 entry。";
            else
            {
                try { document = PreviewDocument.Read(path, (int)entry.Start, source); shown++; }
                catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or FileNotFoundException or ArgumentException)
                { reason = ex.Message; }
            }
            entries.Add(new(position, entry.index, entry.name ?? "", type, document, reason, logic, (int)entry.Start, actions.Length > 0 ? actions : null));
        }
        // Embedded entries of the Actions that shown entries fire, in Action order.
        var children = new List<PreviewSceneEntry>();
        foreach (int a in entries.Where(e => e.Shown).SelectMany(e => e.Actions ?? []).Distinct().Order())
        {
            var inner = playable[a].efxrData!;
            for (int j = 0; j < inner.Entries.Count; j++)
            {
                var entry = inner.Entries[j];
                int offset = (int)(inner.FileHandler.Offset + entry.Start);
                int key = ChildKey(a, j);
                string type = entry.TypeAttribute?.type.ToString() ?? "无";
                string? reason = null; PreviewDocument? document = null;
                if (excluded != null && excluded.TryGetValue(key, out var known)) reason = known;
                else if (!ChildKind(PreviewDocument.KindOf(entry))) reason = $"子 Action 里的 {type} 尚未支持。";
                else if (shown >= MaxEntries) reason = $"一次最多预览 {MaxEntries} 个 entry。";
                else
                {
                    try { document = PreviewDocument.Read(path, offset, source); shown++; }
                    catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or FileNotFoundException or ArgumentException)
                    { reason = ex.Message; }
                }
                children.Add(new(key, entry.index, entry.name ?? "", type, document, reason, Offset: offset, Action: a, Child: j));
            }
        }
        return new(path, entries, children, PreviewEffectGraph.Build(file, System.IO.Path.GetFileName(path)));
    }
}

/// <summary>One <see cref="BillboardRenderer"/> per shown entry, drawn in file order.</summary>
public sealed class PreviewSceneRenderer : IDisposable
{
    private readonly BillboardRenderer?[] renderers;
    private readonly string?[] failures;
    public PreviewScene Scene { get; }
    public int DrawCalls { get; private set; }
    /// <summary>Entries the last render actually drew (renderer present and blend supported).</summary>
    public int Drawn { get; private set; }
    public double UploadMs { get; private set; }
    /// <summary>Entries the last render skipped, with the reason; null when every entry was drawn.</summary>
    public string? Unsupported { get; private set; }
    public int TextureCount => renderers.Sum(r => r?.TextureCount ?? 0);
    public string TextureDescription => string.Join(", ", renderers.Where(r => r != null).Select(r => r!.TextureDescription).Distinct());
    /// <summary>Why an entry has no renderer (its textures could not be created), by table position.</summary>
    public string? FailureAt(int table) => table >= 0 && table < failures.Length ? failures[table] : null;

    public PreviewSceneRenderer(GL gl, PreviewScene scene, BillboardGpuCache? cache = null)
    {
        Scene = scene;
        renderers = new BillboardRenderer?[scene.Shown.Count];
        failures = new string?[scene.Shown.Count];
        // One entry whose textures cannot be created is skipped; the others are still drawn.
        for (int i = 0; i < renderers.Length; i++)
        {
            if (scene.Shown[i].Document is null) continue;      // logic-only (NoDraw): simulated, not drawn
            try { renderers[i] = new BillboardRenderer(gl, scene.Shown[i].Document!, i, cache); }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException)
            { failures[i] = "贴图无法创建：" + ex.Message; }
        }
    }

    public BillboardRenderer? ForTable(int table) => table >= 0 && table < renderers.Length ? renderers[table] : null;
    public int DrawableCount => renderers.Count(r => r != null);

    /// <summary>The blend every drawable entry uses, or null when they differ (the reduced-resolution
    /// particle layer composites with one blend mode).</summary>
    public int? CommonBlend(PreviewFrame frame)
    {
        int? blend = null;
        foreach (var r in renderers)
        {
            if (r == null) continue;
            if (r.IsDistortion) return null;     // Distortion samples the full-resolution scene
            int b = r.Blend(frame);
            if (b is not (0 or 1)) return null;   // the reduced-resolution layer composites 0/1 only
            if (blend != null && blend != b) return null;
            blend = b;
        }
        return blend ?? 0;
    }

    /// <param name="soloTable">Draw only this entry; null draws all.</param>
    public void Render(PreviewFrame frame, Matrix4x4 view, Matrix4x4 projection, int? soloTable = null) =>
        Render(frame, view, projection, PreviewRenderContext.Default, soloTable);

    /// <summary>Draw order of the last render, as entry-table positions.</summary>
    public IReadOnlyList<int> LastOrder => lastOrder;
    private int[] lastOrder = [];

    /// <summary>Entry order (RENDER-HOST §6.3, compromise): PriorityBias ascending, then far to near
    /// by the distance from the camera to the entry's particle centroid plus DistanceBias. With EmitterPriority
    /// on the Root entry, file order (table order) replaces the distance.</summary>
    public int[] DrawOrder(PreviewFrame frame, Matrix4x4 view, bool sort)
    {
        if (!sort || frame.EntryStates == null) return Enumerable.Range(0, renderers.Length).ToArray();
        // EmitterPriority on the Root entry: entry order (later on top) replaces the distance key.
        bool fileOrder = Scene.Graph.EmitterPriority;
        Matrix4x4.Invert(view, out var inverse);
        var camera = new Vector3(inverse.M41, inverse.M42, inverse.M43);
        var keys = new (int Priority, float Distance)[renderers.Length];
        for (int i = 0; i < renderers.Length; i++)
        {
            var state = i < frame.EntryStates.Count ? frame.EntryStates[i] : null;
            var sum = Vector3.Zero; int n = 0;
            if (state != null)
                for (int k = state.First; k < state.First + state.Count; k++)
                {
                    var p = frame.Particles[k].Position;
                    if (float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)) { sum += p; n++; }
                }
            var appearance = renderers[i]?.Appearance ?? EntryAppearance.None;
            keys[i] = (appearance.PriorityBias, n == 0 ? 0 : Vector3.Distance(sum / n, camera) + appearance.DistanceBias);
        }
        return Order(keys, fileOrder);
    }

    /// <summary>Draw order from per-table keys: PriorityBias ascending, then far to near, or table order when
    /// <paramref name="fileOrder"/> (EmitterPriority). Stable: equal keys keep table order.</summary>
    public static int[] Order(IReadOnlyList<(int Priority, float Distance)> keys, bool fileOrder) =>
        Enumerable.Range(0, keys.Count).OrderBy(i => keys[i].Priority).ThenByDescending(i => fileOrder ? 0 : keys[i].Distance).ToArray();

    public void Render(PreviewFrame frame, Matrix4x4 view, Matrix4x4 projection, PreviewRenderContext context, int? soloTable = null)
    {
        int draws = 0, drawn = 0; double upload = 0; List<string>? skipped = null;
        lastOrder = DrawOrder(frame, view, context.Config.SortEntries);
        // Distortion entries come last and sample a copy of everything drawn before them (captured once).
        var ordered = lastOrder.Where(i => renderers[i]?.IsDistortion != true).Concat(lastOrder.Where(i => renderers[i]?.IsDistortion == true)).ToArray();
        lastOrder = ordered;
        var distortionContext = context;
        bool captured = false;
        foreach (int i in lastOrder)
        {
            if (soloTable != null && i != soloTable) continue;
            var r = renderers[i];
            if (r == null) { if (failures[i] != null) (skipped ??= new()).Add($"{Scene.Shown[i].Label} {failures[i]}"); continue; }
            if (r.IsDistortion)
            {
                if (!context.Config.Distortion) continue;
                if (!captured) { distortionContext = context with { SceneColor = context.CaptureScene?.Invoke() ?? 0 }; captured = true; }
                r.Render(frame, view, projection, distortionContext);
            }
            else r.Render(frame, view, projection, context);
            draws += r.DrawCalls; upload += r.UploadMs;
            if (r.Unsupported != null) (skipped ??= new()).Add($"{Scene.Shown[r.Table].Label} {r.Unsupported}");
            else drawn++;
        }
        DrawCalls = draws; UploadMs = upload; Drawn = drawn;
        Unsupported = skipped == null ? null : string.Join("\n", skipped);
    }

    public void Dispose() { foreach (var r in renderers) r?.Dispose(); }
}

/// <summary>Loads a scene into the native worker. An entry that fails natively while loading is
/// excluded with the worker's error and the remaining entries are loaded again, so one entry the
/// emulator cannot run does not hide the rest of the file.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class PreviewSceneLoader
{
    public const int MaxExclusionRetries = 8;

    /// <param name="excluded">Known exclusions for this document by entry position; entries excluded
    /// during this load are added to it.</param>
    public static async Task<PreviewScene> LoadAsync(NativePreviewClient client, string path, Re4ResourceSource resources,
        Dictionary<int, string> excluded, int stressParticles = 0, int stressPosition = -1)
    {
        for (int attempt = 0; ; attempt++)
        {
            var scene = PreviewScene.Read(path, resources, excluded);
            try
            {
                await client.LoadAsync(scene.LoadCommand(stressParticles, stressPosition));
                if (client.LastLoadSkipped.Count == 0) return scene;
                // The worker already left these out; the scene without them matches its entry table.
                foreach (var (table, message) in client.LastLoadSkipped)
                    if (table >= 0 && scene.AtTable(table) is { } skipped) excluded[skipped.Position] = "原生模拟器加载失败：" + message;
                foreach (var (action, child, message) in client.LastLoadSkippedChildren)
                    excluded[PreviewScene.ChildKey(action, child)] = "原生模拟器加载失败：" + message;
                // Entries beyond the 64-entry cap stay out, or they would shift into the freed places.
                var view = new Dictionary<int, string>(excluded);
                foreach (var e in scene.Entries.Concat(scene.Children))
                    if (!e.Shown && e.Excluded != null && !view.ContainsKey(e.Position)) view[e.Position] = e.Excluded;
                return PreviewScene.Read(path, resources, view);
            }
            catch (HostFaultException ex) when (ex.Entry is int table && scene.AtTable(table) is { } failed && attempt < MaxExclusionRetries)
            {
                excluded[failed.Position] = "原生模拟器加载失败：" + Summarize(ex.Message);
            }
        }
    }

    /// <summary>When a frame failed inside one entry, records that entry as excluded and returns it;
    /// the caller then loads the scene again without it.</summary>
    public static PreviewSceneEntry? ExcludeFailedEntry(PreviewScene scene, Exception error, Dictionary<int, string> excluded, int tick)
    {
        int? table = error switch { HostFaultException h => h.Entry, NativeCommandException c => c.Entry, _ => null };
        if (table is not int t || scene.AtTable(t) is not { } failed) return null;
        excluded[failed.Position] = $"原生模拟器运行失败（tick {tick}）：" + Summarize(error.Message);
        return failed;
    }

    /// <summary>Short form of a worker error: its message field when the text is the worker's JSON error.</summary>
    public static string Summarize(string error)
    {
        int start = error.IndexOf('{');
        if (start >= 0)
        {
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(error[start..]);
                if (json.RootElement.TryGetProperty("message", out var message)) return message.GetString() ?? error;
            }
            catch (System.Text.Json.JsonException) { }
        }
        return error;
    }
}
