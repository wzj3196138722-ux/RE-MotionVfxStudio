using ReeLib;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Basic;
using ReeLib.Efx.Structs.Main;
using ReeLib.Pak;
using System.Numerics;

namespace VfxStudio.Re4Render;

public sealed record PreviewTexture(string Path, string Source, byte[] Bytes);
public sealed record UvFrame(Vector4 Rect, int Texture);
/// <summary>Drawable Type attributes. The value is the vertex shader's typeKind.</summary>
public enum PreviewTypeKind { Billboard3D = 0, Polygon = 1, Billboard2D = 2, RibbonFollow = 3, RibbonLength = 4 }
public sealed record PreviewDocument(string Path, int Offset, EFXEntry Entry, string UvsPath, uint UvFlags,
    IReadOnlyList<UvFrame[]> Sequences, IReadOnlyList<PreviewTexture> Textures)
{
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
        _ => null
    };
    /// <summary>Ribbon types drawn from the engine's point pool.</summary>
    public bool IsRibbon => Kind is PreviewTypeKind.RibbonFollow or PreviewTypeKind.RibbonLength;
    /// <summary>UVSequence Flags bit 10: cross-fade the previous frame into the current one.</summary>
    public bool FrameBlend => (UvFlags & 0x400) != 0;
    /// <summary>Engine points per particle of a ribbon: (ShapeDivision-1)*SplineDivision + ShapeDivision,
    /// ShapeDivision clamped to [2, 1000] as the loader does (RUNTIME 4.3.5); 0 for other types.</summary>
    /// RibbonLength has no spline: ShapeDivision points (RUNTIME 4.3.4).
    public int RibbonPointsPerParticle => Entry.TypeAttribute switch
    {
        EFXAttributeTypeRibbonFollow r => checked((int)((Math.Clamp(r.ShapeDivision, 2u, 1000u) - 1) * r.SplineDivision + Math.Clamp(r.ShapeDivision, 2u, 1000u))),
        EFXAttributeTypeRibbonLength l => (int)Math.Clamp(l.ShapeDivision, 2u, 1000u),
        _ => 0
    };
    private static uint FlagsOf(EFXAttribute? type) => type switch
    {
        EFXAttributeTypeBillboard3D b => b.Flags, EFXAttributeTypePolygon p => p.Flags, EFXAttributeTypeBillboard2D b2 => b2.Flags,
        EFXAttributeTypeRibbonFollow r => r.BlendFlags, EFXAttributeTypeRibbonLength l => l.BlendFlags, _ => 0
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
        var data = source.Read(uv.UVSPath, ".7");
        using var memory = new MemoryStream(data.Bytes, writable:false);
        var uvs = new UvsFile(new FileHandler(memory, uv.UVSPath + ".7"));
        if (!uvs.Read() || uvs.Sequences.Count == 0 || uvs.Sequences.Count > 256) throw new InvalidDataException("Invalid UVS sequences");
        var textures = uvs.Textures.Select(t => source.Read(t.path,".143221013")).ToArray();
        var sequences = uvs.Sequences.Select(s => s.patterns.Select(p => new UvFrame(new Vector4(p.left,p.top,p.right,p.bottom),p.textureIndex)).ToArray()).ToArray();
        if (sequences.Any(s => s.Length is < 1 or > 256 || s.Any(p => p.Texture < 0 || p.Texture >= textures.Length ||
            !float.IsFinite(p.Rect.X) || !float.IsFinite(p.Rect.Y) || !float.IsFinite(p.Rect.Z) || !float.IsFinite(p.Rect.W))))
            throw new InvalidDataException("Invalid UV rectangles or texture indices");
        return new(System.IO.Path.GetFullPath(path),offset,entry,uv.UVSPath,uv.Flags,sequences,textures);
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
        looseRoots = extraRoots.Concat(new[] { System.IO.Path.Combine(gameDirectory,"RE4_EXTRACT"),System.IO.Path.Combine(gameDirectory,"RE4_EXTRACT","re_chunk_000") }).ToArray();
    }
    public PreviewTexture Read(string rawPath, string version)
    {
        lock (sync)
        {
            if (cache.TryGetValue(rawPath + "|" + version, out var hit)) return hit;
            var read = ReadUncached(rawPath, version);
            cache[rawPath + "|" + version] = read;
            return read;
        }
    }

    private PreviewTexture ReadUncached(string rawPath, string version)
    {
        string path = rawPath.TrimStart('@','/').Replace('\\','/');
        if (!path.StartsWith("natives/",StringComparison.OrdinalIgnoreCase)) path = "natives/stm/" + path;
        if (!path.EndsWith(version,StringComparison.OrdinalIgnoreCase)) path += version;
        foreach (string root in looseRoots)
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
