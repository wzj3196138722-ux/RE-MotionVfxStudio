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
/// <param name="Decal">An EffectDecal PtBehavior on the entry: the preview lays an approximate decal under its particles.</param>
public sealed record PreviewSceneEntry(int Position, int Index, string Name, string Type, PreviewDocument? Document, string? Excluded,
    bool Logic = false, int Offset = 0, IReadOnlyList<int>? Actions = null, int? Action = null, int? Child = null, PreviewDecal? Decal = null)
{
    public bool Shown => (Document != null || Logic) && Excluded == null;
    /// <summary>Resources that were not found and are drawn with the checker placeholder (billboard and decal).</summary>
    public IReadOnlyList<string> Missing => (Document?.Missing ?? []).Concat(Decal?.Albedo is { Placeholder: true } a ? [a.Path] : [])
        .Concat(Decal?.UvsMissing is { } u ? [u] : []).ToArray();
    public bool Embedded => Action != null;
    /// <summary>[position] for top-level entries, [A action.child] for embedded ones.</summary>
    public string Label => Embedded ? $"[A{Action}.{Child}]" : $"[{Position}]";
}

/// <summary>The parameters of an EffectDecal PtBehavior the approximate decal uses, and the prefab's colour texture.
/// The game script places, sizes and fades the decal; the preview only approximates it (PreviewDecalRenderer).</summary>
/// <param name="Albedo">The prefab's *_ALBA texture (RGB colour, A coverage), or null when it could not be read.</param>
/// <param name="TextureError">Why <paramref name="Albedo"/> is null.</param>
/// <param name="Color">BaseColorMod as 0..1 RGBA.</param>
/// <param name="ObbAdd">OBBScalarAdd.X: added to the scale each step (step n adds ObbAdd x ObbCoef^n), like ScaleAnim; 0 = no growth.</param>
/// <param name="ObbCoef">OBBScalarCoef.X.</param>
/// <param name="UvRect">EnableUVRect is on.</param>
/// <param name="UvsPath">With <paramref name="UvRect"/>: the carrier's UVSequence .uvs. The decal then shows the carrier
/// particle's current cell of the prefab's texture, so a flipbook sheet plays on the ground (checked in game 2026-11-16:
/// Typho's kuo sheet on a TypeNoDraw carrier with kuo's UVSequence). Wilds sets EnableUVRect on decal carriers with a
/// UVSequence too. UVOffset / UVScale are not applied (RE4 uses them only in two footprint files, without a UVSequence).</param>
/// <param name="Sequences">Frame rectangles of <paramref name="UvsPath"/> (placeholder full frames when it is missing).</param>
/// <param name="UvsMissing">The .uvs path when it was not found.</param>
public sealed record PreviewDecal(string Prefab, PreviewTexture? Albedo, string? TextureError, Vector3 Extent, Vector2 ScaleXY,
    float RollRange, float SearchDistance, Vector4 Color, float ObbAdd = 0, float ObbCoef = 1, bool UvRect = false,
    string? UvsPath = null, IReadOnlyList<UvFrame[]>? Sequences = null, string? UvsMissing = null)
{
    /// <summary>Texture rectangle (left, top, right, bottom) of a carrier particle's decal: its current UVSequence cell
    /// (sequence in Flags bits 10-17, frame in 18-25, as for billboards), or the whole texture.</summary>
    public Vector4 UvOf(uint particleFlags)
    {
        if (Sequences is not { Count: > 0 } sequences) return new(0, 0, 1, 1);
        var sequence = sequences[Math.Min((int)((particleFlags >> 10) & 255), sequences.Count - 1)];
        return sequence[Math.Min((int)((particleFlags >> 18) & 255), sequence.Length - 1)].Rect;
    }

    /// <summary>With EnableUVRect, the carrier entry's UVSequence picks the decal's cell: reads its .uvs.</summary>
    public PreviewDecal WithCarrierUvs(EFXEntry carrier, Re4ResourceSource resources)
    {
        if (!UvRect || carrier.Attributes.OfType<EFXAttributeUVSequence>().SingleOrDefault() is not { UVSPath: { Length: > 0 } path } uv) return this;
        try { return this with { UvsPath = path, Sequences = PreviewDocument.ReadUvRects(path, resources) }; }
        catch (FileNotFoundException)
        {
            int count = Math.Clamp(Math.Max(uv.SequenceNo.maxExclusive, 1), 1, 256);
            var full = Enumerable.Range(0, count).Select(_ => Enumerable.Repeat(new UvFrame(new Vector4(0, 0, 1, 1), 0), PreviewDocument.PlaceholderFrames).ToArray()).ToArray();
            return this with { UvsPath = path, Sequences = full, UvsMissing = path + ".7" };
        }
    }

    /// <summary>Scale gained after <paramref name="steps"/> OBBScalar steps: sum of ObbAdd x ObbCoef^k, k = 1..steps
    /// (the ScaleAnim model; the first step is already decayed once). Measured in game (2026-11-16): ScaleXY 0.5, add 0.05,
    /// coef 0.9 stops at 0.95; next to a Polygon with the same numbers as ScaleAnim the decal grows about twice as fast
    /// (<see cref="StepsPerTick"/>) and reaches the same size; the decal ends with its carrier particle.</summary>
    /// <summary>OBBScalar steps per 60 Hz tick of the carrier's age, as measured in game (about 1.9 from one frame; 2 fits
    /// every result so far).</summary>
    public const float StepsPerTick = 2;

    public float ObbGrowth(float steps)
    {
        if (ObbAdd == 0 || steps <= 0) return 0;
        return MathF.Abs(ObbCoef - 1) < 1e-6f ? ObbAdd * steps : ObbAdd * ObbCoef * (1 - MathF.Pow(ObbCoef, steps)) / (1 - ObbCoef);
    }

    /// <summary>Reads the EffectDecal parameters of <paramref name="behavior"/>, or null when it is another script.</summary>
    public static PreviewDecal? From(ReeLib.Efx.Structs.Pt.EFXAttributePtBehavior behavior, Re4ResourceSource resources)
    {
        if (behavior.behaviorString != PreviewBehaviorTemplates.Decal) return null;
        ReeLib.Efx.Structs.Pt.PtBehaviorVariableDataBase? Value(string name) => behavior.properties.FirstOrDefault(p => p.behaviorProperty == name)?.variable;
        string prefab = (Value("DecalPrefab") as ReeLib.Efx.Structs.Pt.PtBehaviorVariableDataWString)?.str ?? "";
        var extent = (Value("Extent") as ReeLib.Efx.Structs.Pt.PtBehaviorVariableFloat3)?.vec ?? Vector3.One;
        var scale = (Value("ScaleXY") as ReeLib.Efx.Structs.Pt.PtBehaviorVariableFloat2)?.Vec ?? Vector2.One;
        float roll = (Value("RandomRollRange") as ReeLib.Efx.Structs.Pt.PtBehaviorVariableFloat)?.value ?? 0;
        float search = (Value("SearchTerrainDistance") as ReeLib.Efx.Structs.Pt.PtBehaviorVariableFloat)?.value ?? 1;
        var c = (Value("BaseColorMod") as ReeLib.Efx.Structs.Pt.PtBehaviorVariableDataColor)?.color;
        var color = c is { } k ? new Vector4(k.R, k.G, k.B, k.A) / 255f : Vector4.One;
        PreviewTexture? albedo = null; string? error = null;
        try { albedo = AlbedoOf(prefab, resources); }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or IOException)
        {
            // Drawn with the checker placeholder so the decal's size stays visible.
            error = ex.Message + "（用占位贴图代替）";
            albedo = PreviewTexture.Missing(prefab);
        }
        var obbAdd = (Value("OBBScalarAdd") as ReeLib.Efx.Structs.Pt.PtBehaviorVariableFloat2)?.Vec.X ?? 0;
        var obbCoef = (Value("OBBScalarCoef") as ReeLib.Efx.Structs.Pt.PtBehaviorVariableFloat2)?.Vec.X ?? 1;
        bool uvRect = behavior.properties.FirstOrDefault(p => p.behaviorProperty == "EnableUVRect")?.ValueText() is "1" or "True" or "true";
        return new(prefab, albedo, error, extent, scale, roll, search, color, obbAdd, obbCoef, uvRect);
    }

    /// <summary>A decal prefab names its textures directly (no material file): the *_ALBA one is the colour.</summary>
    private static PreviewTexture AlbedoOf(string prefab, Re4ResourceSource resources)
    {
        if (prefab.Length == 0) throw new InvalidDataException("DecalPrefab 为空。");
        var bytes = resources.Read(prefab, ".17").Bytes;
        var paths = new List<string>();
        var text = new System.Text.StringBuilder();
        for (int i = 0; i + 1 < bytes.Length; i += 2)
        {
            char ch = (char)(bytes[i] | bytes[i + 1] << 8);
            if (ch >= 0x20 && ch < 0x7f) text.Append(ch);
            else { if (text.ToString().EndsWith(".tex", StringComparison.OrdinalIgnoreCase)) paths.Add(text.ToString()); text.Clear(); }
        }
        string path = paths.FirstOrDefault(p => p.EndsWith("_ALBA.tex", StringComparison.OrdinalIgnoreCase)) ?? paths.FirstOrDefault()
            ?? throw new InvalidDataException("预制体里没有找到贴图路径：" + prefab);
        return resources.Read(path, ".143221013");
    }
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
        void Add(string? path, IReadOnlyList<UvFrame[]>? sequences)
        {
            if (string.IsNullOrEmpty(path) || sequences == null) return;
            var counts = sequences.Select(s => s.Length).ToArray();
            if (uvs.TryGetValue(path, out var existing) && !existing.SequenceEqual(counts))
                throw new InvalidDataException("同一 UVS 资源在不同 entry 中读出的帧数不同：" + path);
            uvs[path] = counts;
        }
        foreach (var entry in Shown)
        {
            Add(entry.Document?.UvsPath, entry.Document?.Sequences);
            // A decal carrier's UVSequence (EnableUVRect): the worker plays it like a billboard's, also on NoDraw carriers.
            Add(entry.Decal?.UvsPath, entry.Decal?.Sequences);
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
            var decal = entry.Attributes.OfType<ReeLib.Efx.Structs.Pt.EFXAttributePtBehavior>().Select(b => PreviewDecal.From(b, source)).FirstOrDefault(d => d != null)
                ?.WithCarrierUvs(entry, source);
            if (excluded != null && excluded.TryGetValue(position, out var known)) reason = known;
            else if (entry.TypeAttribute == null) reason = "没有 Type 属性，不产生粒子。";
            else if (entry.TypeAttribute is EFXAttributeTypeNoDraw && (actions.Length > 0 || decal != null))
            {
                // Not drawn, but its particles fire child Actions or carry a decal: simulated without a billboard renderer.
                if (shown >= MaxEntries) reason = $"一次最多预览 {MaxEntries} 个 entry。";
                else { logic = true; shown++; }
            }
            else if (entry.TypeAttribute is EFXAttributeTypeNoDraw) reason = "NoDraw 不绘制，也没有预览能运行的子 Action 或贴花。";
            else if (PreviewDocument.KindOf(entry) == null) reason = $"类型 {type} 尚未支持。";
            else if (shown >= MaxEntries) reason = $"一次最多预览 {MaxEntries} 个 entry。";
            else
            {
                try { document = PreviewDocument.Read(path, (int)entry.Start, source); shown++; }
                catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or FileNotFoundException or ArgumentException)
                {
                    // A decal carrier whose own particles cannot be drawn (RE4's often have no UVSequence) still places its
                    // decal: simulate it without a billboard renderer.
                    if (decal != null) { logic = true; shown++; }
                    else reason = ex.Message;
                }
            }
            entries.Add(new(position, entry.index, entry.name ?? "", type, document, reason, logic, (int)entry.Start, actions.Length > 0 ? actions : null, Decal: decal));
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
    /// <summary>Approximate decals of EffectDecal carriers. The caller draws them into the full-resolution scene before
    /// the particles (not into the reduced-resolution particle layer, which composites with the particles' blend).</summary>
    public PreviewDecalRenderer Decals { get; }
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
        Decals = new PreviewDecalRenderer(gl, scene, cache);
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

    public void Dispose() { foreach (var r in renderers) r?.Dispose(); Decals.Dispose(); }
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
