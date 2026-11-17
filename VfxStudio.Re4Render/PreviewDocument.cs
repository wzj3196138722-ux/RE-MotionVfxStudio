using ReeLib;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Basic;
using ReeLib.Efx.Structs.Main;
using ReeLib.Pak;
using System.Numerics;

namespace VfxStudio.Re4Render;

/// <param name="Placeholder">Not read from the game: <paramref name="Bytes"/> are raw RGBA8 pixels of a square
/// checker that stands in for a missing resource (see <see cref="Missing"/>).</param>
public sealed record PreviewTexture(string Path, string Source, byte[] Bytes, bool Placeholder = false)
{
    public const int CheckerSize = 64;
    private static readonly byte[] checker = MakeChecker();
    /// <summary>A dim yellow / white checker (8 x 8 cells) standing in for <paramref name="path"/>, which was not found.</summary>
    public static PreviewTexture Missing(string path) => new(path, "占位贴图（找不到 " + path + "）", checker, true);
    private static byte[] MakeChecker()
    {
        var pixels = new byte[CheckerSize * CheckerSize * 4];
        for (int y = 0; y < CheckerSize; y++)
            for (int x = 0; x < CheckerSize; x++)
            {
                bool light = (x / 8 + y / 8) % 2 == 0;
                int o = (y * CheckerSize + x) * 4;
                (pixels[o], pixels[o + 1], pixels[o + 2], pixels[o + 3]) = light ? ((byte)180, (byte)180, (byte)170, (byte)255) : ((byte)140, (byte)120, (byte)40, (byte)255);
            }
        return pixels;
    }
}
public sealed record UvFrame(Vector4 Rect, int Texture);
/// <summary>Drawable Type attributes. The value is the vertex shader's typeKind.</summary>
public enum PreviewTypeKind { Billboard3D = 0, Polygon = 1, Billboard2D = 2, RibbonFollow = 3, RibbonLength = 4, RibbonFixEnd = 5, PolygonTrail = 6 }
/// <param name="Missing">Resources (.uvs, textures) that were not found and are drawn with the checker placeholder.</param>
public sealed record PreviewDocument(string Path, int Offset, EFXEntry Entry, string UvsPath, uint UvFlags,
    IReadOnlyList<UvFrame[]> Sequences, IReadOnlyList<PreviewTexture> Textures, IReadOnlyList<string>? Missing = null)
{
    /// <summary>Frames per sequence assumed when the .uvs is missing (its real count is unknown).</summary>
    public const int PlaceholderFrames = 64;
    public EFXAttributeTypeBillboard3D Billboard => Entry.Attributes.OfType<EFXAttributeTypeBillboard3D>().Single();
    public PreviewTypeKind Kind => KindOf(Entry) ?? throw new NotSupportedException("Unsupported Type attribute");
    /// <summary>The Type attribute's Flags (low 4 bits = BlendType).</summary>
    public uint TypeFlags => FlagsOf(Entry.TypeAttribute);
    public static PreviewTypeKind? KindOf(EFXEntry entry) => entry.TypeAttribute switch
    {
        EFXAttributeTypeBillboard3D => PreviewTypeKind.Billboard3D,
        EFXAttributeTypePolygon => PreviewTypeKind.Polygon,
        EFXAttributeTypeBillboard2D => PreviewTypeKind.Billboard2D,
        EFXAttributeTypeRibbonFollow => PreviewTypeKind.RibbonFollow,
        EFXAttributeTypeRibbonLength => PreviewTypeKind.RibbonLength,
        EFXAttributeTypeRibbonFixEnd f when f.ShapeDivision >= 2 => PreviewTypeKind.RibbonFixEnd,
        EFXAttributeTypePolygonTrail => PreviewTypeKind.PolygonTrail,
        _ => null
    };
    /// <summary>Ribbon types drawn from the engine's point pool.</summary>
    public bool IsRibbon => Kind is PreviewTypeKind.RibbonFollow or PreviewTypeKind.RibbonLength or PreviewTypeKind.RibbonFixEnd or PreviewTypeKind.PolygonTrail;
    /// <summary>PolygonTrail: each exported point is one column of the engine's swept sheet (base and tip),
    /// drawn as quads between neighbouring columns instead of being widened.</summary>
    public bool IsSheet => Kind == PreviewTypeKind.PolygonTrail;
    /// <summary>UVSequence Flags bit 10: cross-fade the previous frame into the current one.</summary>
    public bool FrameBlend => (UvFlags & 0x400) != 0;
    /// <summary>Engine points per particle of a ribbon: (ShapeDivision-1)*SplineDivision + ShapeDivision,
    /// ShapeDivision clamped to [2, 1000] as the loader does (RUNTIME 4.3.5); 0 for other types.</summary>
    /// RibbonLength has no spline: ShapeDivision points (RUNTIME 4.3.4).
    public int RibbonPointsPerParticle => Entry.TypeAttribute switch
    {
        EFXAttributeTypeRibbonFollow r => checked((int)((Math.Clamp(r.ShapeDivision, 2u, 1000u) - 1) * r.SplineDivision + Math.Clamp(r.ShapeDivision, 2u, 1000u))),
        EFXAttributeTypeRibbonLength l => (int)Math.Clamp(l.ShapeDivision, 2u, 1000u),
        // RibbonFixEnd: N = ShapeDivision points, buffer N * 0x54 (RUNTIME 4.3.7; not clamped by the loader).
        EFXAttributeTypeRibbonFixEnd f => (int)f.ShapeDivision,
        // PolygonTrail: columns of the vertex grid, (N+1)*S + N+2 with N = NumTrailDivision, S = NumSplineDivision (4.3.9).
        EFXAttributeTypePolygonTrail t => checked((int)((t.NumTrailDivision + 1) * t.NumSplineDivision + t.NumTrailDivision + 2)),
        _ => 0
    };
    private static uint FlagsOf(EFXAttribute? type) => type switch
    {
        EFXAttributeTypeBillboard3D b => b.Flags, EFXAttributeTypePolygon p => p.Flags, EFXAttributeTypeBillboard2D b2 => b2.Flags,
        EFXAttributeTypeRibbonFollow r => r.BlendFlags, EFXAttributeTypeRibbonLength l => l.BlendFlags,
        EFXAttributeTypeRibbonFixEnd f => f.BlendFlags, EFXAttributeTypePolygonTrail t => t.Flags, _ => 0
    };
    public object LoadCommand(int stressParticles = 0) => new
    {
        command = "load",
        entries = new[] { new { path = Path, offset = Offset, stress_particles = stressParticles } },
        uvs_resources = string.IsNullOrEmpty(UvsPath) ? new Dictionary<string,int[]>() : new Dictionary<string,int[]> { [UvsPath] = Sequences.Select(s => s.Length).ToArray() }
    };

    public static (string Path, int Offset) Fixture(string gameDirectory, string fixture) => fixture switch
    {
        "bb" => (System.IO.Path.Combine(gameDirectory,@"RE4_EXTRACT\natives\stm\_chainsaw\vfx\effecteditor\efd_event\cs\efd_csa013_s00\efd_0015_csa013_s00_saliva_0000.efx.3539837"),0x89),
        "bb2" => (System.IO.Path.Combine(gameDirectory,@"RE4_EXTRACT\natives\stm\_anotherorder\vfx\effecteditor\efd_setmodel\efd_sm82_320_00\efd_0015_sm82_320_00_0000.efx.3539837"),0x2e29),
        _ => throw new ArgumentException("Expected fixture bb or bb2")
    };

    public static int ReadEntryId(string path,int offset) => ReadEntry(path,offset).index;

    private static EFXEntry ReadEntry(string path,int offset)
    {
        using var stream=File.OpenRead(path);
        var handler=new FileHandler(stream,path){FileVersion=(int)EfxVersion.RE4};
        handler.Seek(offset);
        var entry=new EFXEntry{Version=EfxVersion.RE4};
        if(!entry.Read(handler)) throw new InvalidDataException("Could not read EFX entry");
        return entry;
    }

    public static PreviewDocument Read(string path, int offset, Re4ResourceSource source)
    {
        var entry=ReadEntry(path,offset);
        if(KindOf(entry)==null) throw new NotSupportedException("Preview supports CPU Billboard3D, Polygon and Billboard2D entries");
        uint flags=FlagsOf(entry.TypeAttribute);
        if(!BillboardRenderer.SupportsBlend((int)(flags & 15)))
            throw new NotSupportedException($"预览暂不支持混合方式 {flags & 15}（支持 0–4）。");
        var uv = entry.Attributes.OfType<EFXAttributeUVSequence>().SingleOrDefault();
        if (string.IsNullOrWhiteSpace(uv?.UVSPath))
            throw new NotSupportedException("This entry has no UVSPath; P1 requires an explicit texture source");
        // A missing .uvs or texture is drawn with a checker placeholder, so the entry's size and motion stay visible
        // and the gap is obvious (listed in Missing).
        var missing = new List<string>();
        PreviewTexture[] textures; UvFrame[][] sequences;
        PreviewTexture? data = null;
        try { data = source.Read(uv.UVSPath, ".7"); }
        catch (FileNotFoundException) { missing.Add(uv.UVSPath + ".7"); }
        if (data == null)
        {
            // Unknown frame count: as many sequences as SequenceNo can pick, PlaceholderFrames full frames each.
            int count = Math.Clamp(Math.Max(uv.SequenceNo.maxExclusive, 1), 1, 256);
            textures = [PreviewTexture.Missing(uv.UVSPath)];
            sequences = Enumerable.Range(0, count).Select(_ => Enumerable.Repeat(new UvFrame(new Vector4(0, 0, 1, 1), 0), PlaceholderFrames).ToArray()).ToArray();
        }
        else
        {
            using var memory = new MemoryStream(data.Bytes, writable:false);
            var uvs = new UvsFile(new FileHandler(memory, uv.UVSPath + ".7"));
            if (!uvs.Read() || uvs.Sequences.Count == 0 || uvs.Sequences.Count > 256) throw new InvalidDataException("Invalid UVS sequences");
            textures = uvs.Textures.Select(t =>
            {
                try { return source.Read(t.path, ".143221013"); }
                catch (FileNotFoundException) { missing.Add(t.path + ".143221013"); return PreviewTexture.Missing(t.path); }
            }).ToArray();
            sequences = uvs.Sequences.Select(s => s.patterns.Select(p => new UvFrame(new Vector4(p.left,p.top,p.right,p.bottom),p.textureIndex)).ToArray()).ToArray();
        }
        if (sequences.Any(s => s.Length is < 1 or > 256 || s.Any(p => p.Texture < 0 || p.Texture >= textures.Length ||
            !float.IsFinite(p.Rect.X) || !float.IsFinite(p.Rect.Y) || !float.IsFinite(p.Rect.Z) || !float.IsFinite(p.Rect.W))))
            throw new InvalidDataException("Invalid UV rectangles or texture indices");
        return new(System.IO.Path.GetFullPath(path),offset,entry,uv.UVSPath,uv.Flags,sequences,textures,missing.Count > 0 ? missing : null);
    }

    /// <summary>The frame rectangles of a .uvs (no textures): a decal carrier's UVSequence picks the decal's cell.
    /// Throws FileNotFoundException when the .uvs is missing.</summary>
    public static UvFrame[][] ReadUvRects(string uvsPath, Re4ResourceSource source)
    {
        using var memory = new MemoryStream(source.Read(uvsPath, ".7").Bytes, writable:false);
        var uvs = new UvsFile(new FileHandler(memory, uvsPath + ".7"));
        if (!uvs.Read() || uvs.Sequences.Count == 0 || uvs.Sequences.Count > 256) throw new InvalidDataException("Invalid UVS sequences: " + uvsPath);
        var sequences = uvs.Sequences.Select(s => s.patterns.Select(p => new UvFrame(new Vector4(p.left,p.top,p.right,p.bottom),p.textureIndex)).ToArray()).ToArray();
        if (sequences.Any(s => s.Length is < 1 or > 256)) throw new InvalidDataException("Invalid UVS sequence sizes: " + uvsPath);
        return sequences;
    }
}

/// <summary>Read-only loose-resource lookup plus a cached index of official PAK metadata.</summary>
public sealed class Re4ResourceSource : IDisposable
{
    private readonly string gameDirectory;
    private readonly string[] looseRoots;
    private readonly Dictionary<ulong,(PakFile Pak,PakEntry Entry)> index = new();
    private readonly List<PakFile> paks = new();
    private bool indexed;
    // Entries of one file share textures and UVS files; resources are read-only for the session.
    private readonly Dictionary<string,PreviewTexture> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync = new();
    public Re4ResourceSource(string gameDirectory, params string[] extraRoots)
    {
        this.gameDirectory = gameDirectory;
        // Lookup order: the open document's own natives root (UseDocument), explicit roots, the game's loose files
        // (<game>/natives/STM/..., where mods are installed; the game loads them over pak contents), the extracted paks,
        // then the paks themselves. Paths start with natives/, so the game folder is the root.
        looseRoots = extraRoots.Concat(new[] { gameDirectory, System.IO.Path.Combine(gameDirectory,"RE4_EXTRACT"),System.IO.Path.Combine(gameDirectory,"RE4_EXTRACT","re_chunk_000") })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>The folder holding the document's natives folder (found by walking up from it), searched first: a file
    /// opened from a release folder or a mod finds its own textures and .uvs there. Null when none.</summary>
    public string? DocumentRoot { get; private set; }

    /// <summary>Searches the natives root above <paramref name="documentPath"/> first from now on (the original file,
    /// not a published revision copy).</summary>
    public void UseDocument(string? documentPath)
    {
        lock (sync) DocumentRoot = documentPath == null ? null : NativesRootOf(documentPath);
    }

    /// <summary>Folder levels <see cref="NativesRootOf"/> walks up at most (RE4 resource paths are about 6-8 levels
    /// below natives/STM).</summary>
    public const int MaxNativesSearchDepth = 16;

    /// <summary>The parent of the nearest folder named natives above <paramref name="path"/>, within
    /// <see cref="MaxNativesSearchDepth"/> levels, or null.</summary>
    public static string? NativesRootOf(string path)
    {
        var dir = new DirectoryInfo(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        for (int depth = 0; dir != null && depth < MaxNativesSearchDepth; dir = dir.Parent, depth++)
            if (string.Equals(dir.Name, "natives", StringComparison.OrdinalIgnoreCase)) return dir.Parent?.FullName;
        return null;
    }
    public PreviewTexture Read(string rawPath, string version)
    {
        lock (sync)
        {
            string key = rawPath + "|" + version + "|" + DocumentRoot;
            if (cache.TryGetValue(key, out var hit)) return hit;
            var read = ReadUncached(rawPath, version);
            cache[key] = read;
            return read;
        }
    }

    private PreviewTexture ReadUncached(string rawPath, string version)
    {
        string path = rawPath.TrimStart('@','/').Replace('\\','/');
        if (!path.StartsWith("natives/",StringComparison.OrdinalIgnoreCase)) path = "natives/stm/" + path;
        if (!path.EndsWith(version,StringComparison.OrdinalIgnoreCase)) path += version;
        foreach (string root in (DocumentRoot == null ? looseRoots : looseRoots.Prepend(DocumentRoot)))
        {
            string relative=path;
            string normalizedRoot=root.Replace('\\','/').TrimEnd('/');
            if(normalizedRoot.EndsWith("/natives/stm",StringComparison.OrdinalIgnoreCase)) relative=path["natives/stm/".Length..];
            else if(normalizedRoot.EndsWith("/natives",StringComparison.OrdinalIgnoreCase)) relative=path["natives/".Length..];
            string disk = System.IO.Path.Combine(root,relative.Replace('/',System.IO.Path.DirectorySeparatorChar));
            if (File.Exists(disk)) return new(rawPath,disk,File.ReadAllBytes(disk));
        }
        if (!indexed)
        {
            // Standard RE4 archive names, in patch order. Metadata and payloads are read-only.
            foreach (string file in Directory.EnumerateFiles(gameDirectory,"re_chunk_000*.pak").Order(StringComparer.OrdinalIgnoreCase))
            {
                if (new FileInfo(file).Length <= 16) continue;
                var pak = new PakFile { filepath = file };
                pak.ReadContents(file);
                paks.Add(pak);
                foreach (var entry in pak.Entries) index[entry.CombinedHash] = (pak,entry);
            }
            indexed = true;
        }
        if (!index.TryGetValue(PakUtils.GetFilepathHash(path),out var found)) throw new FileNotFoundException("Missing RE4 resource: " + path);
        using var output = new MemoryStream();
        found.Pak.Read(found.Entry,output);
        return new(rawPath,found.Pak.filepath + " :: " + path,output.ToArray());
    }
    public void Dispose() { foreach (var pak in paks) pak.Dispose(); }
}
