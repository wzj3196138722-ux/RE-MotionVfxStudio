using ReeLib;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Basic;
using ReeLib.Efx.Structs.Main;
using ReeLib.Pak;
using System.Numerics;

namespace VfxStudio.Re4Render;

public sealed record PreviewTexture(string Path, string Source, byte[] Bytes);
public sealed record UvFrame(Vector4 Rect, int Texture);
public sealed record PreviewDocument(string Path, int Offset, EFXEntry Entry, string UvsPath, uint UvFlags,
    IReadOnlyList<UvFrame[]> Sequences, IReadOnlyList<PreviewTexture> Textures)
{
    public EFXAttributeTypeBillboard3D Billboard => Entry.Attributes.OfType<EFXAttributeTypeBillboard3D>().Single();
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
        if(entry.TypeAttribute is not EFXAttributeTypeBillboard3D billboard) throw new NotSupportedException("Preview supports CPU Billboard3D entries");
        if(!BillboardRenderer.SupportsBlend((int)(billboard.Flags & 15)))
            throw new NotSupportedException($"预览暂不支持混合方式 {billboard.Flags & 15}（目前只实现 AlphaBlend 0 与 Physical 1）。");
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
    public Re4ResourceSource(string gameDirectory, params string[] extraRoots)
    {
        this.gameDirectory = gameDirectory;
        looseRoots = extraRoots.Concat(new[] { System.IO.Path.Combine(gameDirectory,"RE4_EXTRACT"),System.IO.Path.Combine(gameDirectory,"RE4_EXTRACT","re_chunk_000") }).ToArray();
    }
    public PreviewTexture Read(string rawPath, string version)
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
