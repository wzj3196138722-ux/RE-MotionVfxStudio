using System.Numerics;
using ReeLib;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Basic;
using ReeLib.Efx.Structs.Common;
using ReeLib.Efx.Structs.Main;
using Silk.NET.OpenGL;
using VfxStudio.Renderer.Meshes;
using VfxStudio.Renderer.Textures;

namespace VfxStudio.Renderer.Vfx;

public class VfxResolvedMeshMaterial
{
    public GpuMesh? Mesh { get; set; }
    public GpuTexture? Texture { get; set; }
    public Vector4 TintColor { get; set; } = new Vector4(1.8f, 1.4f, 1.0f, 1.0f);
    public float EmissionIntensity { get; set; } = 2.0f;
    public int PartsStartMin { get; set; } = -1;
    public int PartsStartMax { get; set; } = -1;
}

/// <summary>
/// Resolves and caches companion VFX assets (.uvs.8, .tex.241106027, .mesh.241111606, .mdf2.45)
/// referenced by .efx entries across Assets/WeaponBases/{wpXX}, Assets/VfxShared, and external directories.
/// Ports the asset resolution and material/UVS logic from mhws_efx_editor-0.3.0 (asset_link.py & uvs_table.py).
/// </summary>
public class VfxAssetCache : IDisposable
{
    private readonly GL _gl;
    private readonly string _assetsDir;

    public string CurrentWeaponCode { get; set; } = string.Empty;
    public List<string> ExtraSearchRoots { get; } = new();

    private readonly Dictionary<string, UvsFile?> _uvsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GpuTexture?> _texCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GpuMesh?> _meshCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MdfFile?> _mdfCache = new(StringComparer.OrdinalIgnoreCase);

    // Fast filename lookup across Assets/VfxShared
    private readonly Dictionary<string, List<string>> _sharedFileIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _sharedStreamingIndex = new(StringComparer.OrdinalIgnoreCase);

    public int LoadedUvsCount => _uvsCache.Values.Count(v => v != null);
    public int LoadedTexCount => _texCache.Values.Count(v => v != null);
    public int LoadedMeshCount => _meshCache.Values.Count(v => v != null);
    public int LoadedMdfCount => _mdfCache.Values.Count(v => v != null);

    private string CacheKey(string rawPath) => CurrentWeaponCode + "|" + string.Join('|', ExtraSearchRoots) + "|" + rawPath;

    public VfxAssetCache(GL gl, string assetsDir)
    {
        _gl = gl;
        _assetsDir = assetsDir;
        BuildSharedIndex();
    }

    private void BuildSharedIndex()
    {
        string sharedDir = Path.Combine(_assetsDir, "VfxShared");
        if (!Directory.Exists(sharedDir)) return;

        try
        {
            foreach (var file in Directory.GetFiles(sharedDir, "*.*", SearchOption.AllDirectories))
            {
                string fn = Path.GetFileName(file);
                bool isStreaming = file.Contains(@"\streaming\", StringComparison.OrdinalIgnoreCase) ||
                                   file.Contains(@"/streaming/", StringComparison.OrdinalIgnoreCase);
                var index = isStreaming ? _sharedStreamingIndex : _sharedFileIndex;
                if (!index.TryGetValue(fn, out var paths)) index[fn] = paths = new List<string>();
                paths.Add(file);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VfxAssetCache] Warning indexing VfxShared: {ex.Message}");
        }
    }

    public string? ResolveAssetPath(string? rawInternalPath, string versionSuffix, bool preferStreaming = false)
    {
        if (string.IsNullOrWhiteSpace(rawInternalPath)) return null;

        string clean = rawInternalPath.Trim().TrimStart('@', '/').Replace('\\', '/');
        if (clean.StartsWith("natives/stm/", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean.Substring("natives/stm/".Length);
        }

        // Strip existing numeric version suffix if present
        int lastDot = clean.LastIndexOf('.');
        if (lastDot > 0 && int.TryParse(clean.AsSpan(lastDot + 1), out _))
        {
            clean = clean.Substring(0, lastDot);
        }

        string relWithVer = clean + versionSuffix;
        string relSys = relWithVer.Replace('/', Path.DirectorySeparatorChar);
        string fileNameWithVer = Path.GetFileName(relSys);

        // 1. Extra search roots (e.g. near imported external .efx)
        foreach (var root in ExtraSearchRoots)
        {
            if (preferStreaming)
            {
                string sCandidate = Path.Combine(root, "natives", "stm", "streaming", relSys);
                if (File.Exists(sCandidate)) return sCandidate;
            }
            string candidate = Path.Combine(root, "natives", "stm", relSys);
            if (File.Exists(candidate)) return candidate;
            string direct = Path.Combine(root, fileNameWithVer);
            if (File.Exists(direct)) return direct;
        }

        // 2. Current weapon directory
        if (!string.IsNullOrEmpty(CurrentWeaponCode))
        {
            string wpDir = Path.Combine(_assetsDir, "WeaponBases", CurrentWeaponCode);
            if (preferStreaming)
            {
                string wpStream = Path.Combine(wpDir, "natives", "stm", "streaming", relSys);
                if (File.Exists(wpStream)) return wpStream;
            }
            string wpPreserved = Path.Combine(wpDir, "natives", "stm", relSys);
            if (File.Exists(wpPreserved)) return wpPreserved;
            string wpDirect = Path.Combine(wpDir, fileNameWithVer);
            if (File.Exists(wpDirect)) return wpDirect;
        }

        // 3. Shared VFX directory (exact relative path)
        string sharedRoot = Path.Combine(_assetsDir, "VfxShared");
        if (preferStreaming)
        {
            string sharedStream = Path.Combine(sharedRoot, "natives", "stm", "streaming", relSys);
            if (File.Exists(sharedStream)) return sharedStream;
        }
        string sharedExact = Path.Combine(sharedRoot, "natives", "stm", relSys);
        if (File.Exists(sharedExact)) return sharedExact;

        // 4. Shared VFX filename index fallback
        if (preferStreaming && _sharedStreamingIndex.TryGetValue(fileNameWithVer, out var idxStream))
        {
            var matches = idxStream.Where(File.Exists).ToArray();
            if (matches.Length == 1) return matches[0];
            if (matches.Length > 1) Console.WriteLine($"[VfxAssetCache] Ambiguous streaming asset '{fileNameWithVer}': {string.Join(" | ", matches)}");
        }
        if (_sharedFileIndex.TryGetValue(fileNameWithVer, out var idxFile))
        {
            var matches = idxFile.Where(File.Exists).ToArray();
            if (matches.Length == 1) return matches[0];
            if (matches.Length > 1) Console.WriteLine($"[VfxAssetCache] Ambiguous asset '{fileNameWithVer}': {string.Join(" | ", matches)}");
        }

        return null;
    }

    public UvsFile? GetUvs(string? uvsInternalPath)
    {
        if (string.IsNullOrWhiteSpace(uvsInternalPath)) return null;
        string key = uvsInternalPath.Trim();
        string cacheKey = CacheKey(key);
        if (_uvsCache.TryGetValue(cacheKey, out var cached)) return cached;

        string? localPath = ResolveAssetPath(key, ".8", preferStreaming: false);
        if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
        {
            return null;
        }

        try
        {
            using var fs = File.OpenRead(localPath);
            var uvs = new UvsFile(new FileHandler(fs, localPath));
            if (uvs.Read())
            {
                _uvsCache[cacheKey] = uvs;
                return uvs;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VfxAssetCache] Failed reading UVS {localPath}: {ex.Message}");
        }

        return null;
    }

    public GpuTexture? GetTexture(string? texInternalPath)
    {
        if (string.IsNullOrWhiteSpace(texInternalPath)) return null;
        string key = texInternalPath.Trim();
        string cacheKey = CacheKey(key);
        if (_texCache.TryGetValue(cacheKey, out var cached)) return cached;

        // Try streaming texture (full hi-res mip0) first, then fallback to non-streaming base texture
        string? streamingPath = ResolveAssetPath(key, ".241106027", preferStreaming: true);
        string? basePath = ResolveAssetPath(key, ".241106027", preferStreaming: false);

        foreach (var candidatePath in new[] { streamingPath, basePath })
        {
            if (string.IsNullOrEmpty(candidatePath) || !File.Exists(candidatePath)) continue;

            try
            {
                byte[] bytes = File.ReadAllBytes(candidatePath);
                var gpuTex = GpuTexture.CreateFromTexBytes(_gl, bytes, candidatePath);
                if (gpuTex != null)
                {
                    _texCache[cacheKey] = gpuTex;
                    return gpuTex;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VfxAssetCache] Failed reading TEX {candidatePath}: {ex.Message}");
            }
        }

        return null;
    }

    public GpuTexture? GetUvsTexture(string? uvsInternalPath, int sequenceIndex = 0, int patternIndex = 0)
    {
        var uvs = GetUvs(uvsInternalPath);
        if (uvs == null || uvs.Textures.Count == 0) return null;

        int texIdx = 0;
        if (uvs.Sequences.Count > 0)
        {
            int seqIdx = Math.Clamp(sequenceIndex, 0, uvs.Sequences.Count - 1);
            var seq = uvs.Sequences[seqIdx];
            if (seq.patterns.Count > 0)
            {
                int patIdx = ((patternIndex % seq.patterns.Count) + seq.patterns.Count) % seq.patterns.Count;
                int candidateIdx = seq.patterns[patIdx].textureIndex;
                if (candidateIdx >= 0 && candidateIdx < uvs.Textures.Count)
                {
                    texIdx = candidateIdx;
                }
            }
        }

        string texPath = uvs.Textures[texIdx].path;
        return GetTexture(texPath);
    }

    /// <summary>
    /// Ports UVSTable.uv_rect from mhws_efx_editor-0.3.0/efx_sim/uvs_table.py.
    /// Returns (UvMin, UvMax) for the given sequence and pattern index.
    /// </summary>
    public (Vector2 UvMin, Vector2 UvMax) SampleUvsRect(string? uvsInternalPath, int sequenceIndex, int patternIndex)
    {
        var uvs = GetUvs(uvsInternalPath);
        if (uvs == null || uvs.Sequences.Count == 0)
            return (Vector2.Zero, Vector2.One);

        int seqIdx = Math.Clamp(sequenceIndex, 0, uvs.Sequences.Count - 1);
        var seq = uvs.Sequences[seqIdx];
        if (seq.patterns.Count == 0)
            return (Vector2.Zero, Vector2.One);

        int patIdx = ((patternIndex % seq.patterns.Count) + seq.patterns.Count) % seq.patterns.Count;
        var pat = seq.patterns[patIdx];

        return (new Vector2(pat.left, pat.top), new Vector2(pat.right, pat.bottom));
    }

    public int GetUvsPatternCount(string? uvsInternalPath, int sequenceIndex = 0)
    {
        var uvs = GetUvs(uvsInternalPath);
        if (uvs == null || uvs.Sequences.Count == 0) return 0;
        int seqIdx = Math.Clamp(sequenceIndex, 0, uvs.Sequences.Count - 1);
        return uvs.Sequences[seqIdx].patterns.Count;
    }

    public GpuMesh? GetMesh(string? meshInternalPath)
    {
        if (string.IsNullOrWhiteSpace(meshInternalPath)) return null;
        string key = meshInternalPath.Trim();
        string cacheKey = CacheKey(key);
        if (_meshCache.TryGetValue(cacheKey, out var cached)) return cached;

        string? localPath = ResolveAssetPath(key, ".241111606", preferStreaming: false);
        if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
        {
            return null;
        }

        try
        {
            using var fs = File.OpenRead(localPath);
            var meshFile = new MeshFile(new FileHandler(fs, localPath));
            if (meshFile.Read())
            {
                if (meshFile.RequiresStreamingData)
                {
                    string? streamPath = ResolveAssetPath(key, ".241111606", preferStreaming: true);
                    if (!string.IsNullOrEmpty(streamPath) && File.Exists(streamPath))
                    {
                        using var sfs = File.OpenRead(streamPath);
                        meshFile.LoadStreamingData(new FileHandler(sfs, streamPath));
                        meshFile.MergeStreamingMeshData();
                    }
                }
                var gpuMesh = GpuMesh.CreateFromMeshFile(_gl, meshFile);
                _meshCache[cacheKey] = gpuMesh;
                return gpuMesh;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VfxAssetCache] Failed reading MESH {localPath}: {ex.Message}");
        }

        return null;
    }

    public MdfFile? GetMdf(string? mdfInternalPath)
    {
        if (string.IsNullOrWhiteSpace(mdfInternalPath)) return null;
        string key = mdfInternalPath.Trim();
        string cacheKey = CacheKey(key);
        if (_mdfCache.TryGetValue(cacheKey, out var cached)) return cached;

        string? localPath = ResolveAssetPath(key, ".45", preferStreaming: false);
        if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
        {
            return null;
        }

        try
        {
            using var fs = File.OpenRead(localPath);
            var mdf = new MdfFile(new FileHandler(fs, localPath));
            if (mdf.Read())
            {
                _mdfCache[cacheKey] = mdf;
                return mdf;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VfxAssetCache] Failed reading MDF2 {localPath}: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Resolves the 3D mesh, submesh range (PartsStartNo), MDF2 material tint, and primary texture
    /// for an EFXEntry containing TypeMeshV2 / TypeMesh / TypeGpuMesh.
    /// Ports mhws_efx_editor-0.3.0/blender_efx_re/asset_link.py (_extract_mesh_mdf_config & _apply_mdf_material).
    /// </summary>
    public VfxResolvedMeshMaterial? ResolveEntryMeshMaterial(EFXEntryBase entry)
    {
        string? meshPath = null;
        string? mdfPath = null;
        int partsMin = -1;
        int partsMax = -1;
        Vector4 baseTint = Vector4.One;
        string? overrideTexPath = null;
        Vector4? overrideEmissive = null;

        var tm2 = entry.Attributes.OfType<EFXAttributeTypeMeshV2>().FirstOrDefault();
        if (tm2 != null)
        {
            meshPath = tm2.MeshPath;
            mdfPath = tm2.MaterialPath;
            partsMin = tm2.PartsStartNo.r;
            partsMax = tm2.PartsStartNo.s;
            if (tm2.Color.A > 0)
            {
                baseTint = new Vector4(tm2.Color.R / 255f, tm2.Color.G / 255f, tm2.Color.B / 255f, tm2.Color.A / 255f);
            }

            foreach (var p in tm2.properties)
            {
                if (!string.IsNullOrWhiteSpace(p.texturePath) && overrideTexPath == null)
                {
                    overrideTexPath = p.texturePath;
                }
                else if (p.parameterType != MaterialParameterType.Texture && p.VectorValue.LengthSquared() > 0.001f && overrideEmissive == null)
                {
                    overrideEmissive = p.VectorValue;
                }
            }

            if (overrideTexPath == null && tm2.texPaths != null)
            {
                overrideTexPath = tm2.texPaths.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            }
        }
        else
        {
            var tm1 = entry.Attributes.OfType<EFXAttributeTypeMesh>().FirstOrDefault();
            if (tm1 != null)
            {
                meshPath = tm1.meshPath;
                mdfPath = tm1.mdfPath;
                partsMin = (int)tm1.startingFrameMin;
                partsMax = (int)tm1.startingFrameMax;
                if (tm1.color1.A > 0)
                {
                    baseTint = new Vector4(tm1.color1.R / 255f, tm1.color1.G / 255f, tm1.color1.B / 255f, tm1.color1.A / 255f);
                }
                if (tm1.texPaths != null)
                {
                    overrideTexPath = tm1.texPaths.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
                }
            }
            else
            {
                var gm = entry.Attributes.OfType<EFXAttributeTypeGpuMesh>().FirstOrDefault();
                if (gm != null)
                {
                    meshPath = gm.MeshPath;
                    mdfPath = gm.MaterialPath;
                    partsMin = gm.PartsStartNo.r;
                    partsMax = gm.PartsStartNo.s;
                    if (gm.Color.A > 0)
                    {
                        baseTint = new Vector4(gm.Color.R / 255f, gm.Color.G / 255f, gm.Color.B / 255f, gm.Color.A / 255f);
                    }
                    if (gm.texturePaths != null)
                    {
                        overrideTexPath = gm.texturePaths.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(meshPath)) return null;
        var gpuMesh = GetMesh(meshPath);
        if (gpuMesh == null || gpuMesh.TotalIndices == 0) return null;

        // Inspect MDF2 if present for textures and EmissiveParam / ColorParam
        var mdf = GetMdf(mdfPath);
        if (mdf != null && mdf.Materials.Count > 0)
        {
            var mat = mdf.Materials[0];
            if (overrideTexPath == null && mat.Textures.Count > 0)
            {
                // Prefer AlphaMap / BaseMap / AlbedoMap over NormalMap
                var preferred = mat.Textures.FirstOrDefault(t =>
                    t.texType.Contains("Alpha", StringComparison.OrdinalIgnoreCase) ||
                    t.texType.Contains("Base", StringComparison.OrdinalIgnoreCase) ||
                    t.texType.Contains("Albedo", StringComparison.OrdinalIgnoreCase) ||
                    t.texType.Contains("Mask", StringComparison.OrdinalIgnoreCase)) ?? mat.Textures[0];
                overrideTexPath = preferred.texPath;
            }

            if (overrideEmissive == null)
            {
                var emParam = mat.Parameters.FirstOrDefault(p =>
                    p.paramName.Contains("Emissive", StringComparison.OrdinalIgnoreCase) ||
                    p.paramName.Contains("Color", StringComparison.OrdinalIgnoreCase));
                if (emParam != null && emParam.parameter.LengthSquared() > 0.001f)
                {
                    overrideEmissive = emParam.parameter;
                }
            }
        }

        Vector4 finalTint = baseTint;
        if (overrideEmissive.HasValue)
        {
            var em = overrideEmissive.Value;
            finalTint = new Vector4(
                Math.Clamp(baseTint.X * (em.X > 0.01f ? em.X : 1f), 0.1f, 4.0f),
                Math.Clamp(baseTint.Y * (em.Y > 0.01f ? em.Y : 1f), 0.1f, 4.0f),
                Math.Clamp(baseTint.Z * (em.Z > 0.01f ? em.Z : 1f), 0.1f, 4.0f),
                baseTint.W
            );
        }

        return new VfxResolvedMeshMaterial
        {
            Mesh = gpuMesh,
            Texture = GetTexture(overrideTexPath),
            TintColor = finalTint,
            EmissionIntensity = 2.0f,
            PartsStartMin = partsMin,
            PartsStartMax = partsMax
        };
    }

    public int UvsCount => LoadedUvsCount;
    public int TexCount => LoadedTexCount;
    public int MeshCount => LoadedMeshCount;
    public int MdfCount => LoadedMdfCount;

    /// <summary>
    /// Preloads companion assets (.uvs, .tex, .mesh, .mdf2) referenced by a collection of EFX files.
    /// </summary>
    public void WarmupCompanionAssets(IEnumerable<EfxFile> efxFiles)
    {
        foreach (var efx in efxFiles)
        {
            try
            {
                foreach (var entry in efx.Entries)
                {
                    ResolveEntryMeshMaterial(entry);
                }
                foreach (var action in efx.Actions)
                {
                    if (action.Attributes.OfType<EFXAttributePlayEmitter>().FirstOrDefault()?.efxrData is EfxFile subEfx)
                    {
                        foreach (var subEntry in subEfx.Entries)
                        {
                            ResolveEntryMeshMaterial(subEntry);
                        }
                    }
                }
                foreach (var attr in efx.GetAttributesAndActions(includeEmbeddedEfx: true))
                {
                    if (attr is EFXAttributeUVSequence uvsAttr && !string.IsNullOrWhiteSpace(uvsAttr.UVSPath))
                    {
                        GetUvsTexture(uvsAttr.UVSPath, uvsAttr.SequenceNo.r);
                    }
                    else if (attr is EFXAttributeTextureUnit tuAttr)
                    {
                        if (!string.IsNullOrWhiteSpace(tuAttr.uvs0Path)) GetUvsTexture(tuAttr.uvs0Path, 0);
                        if (!string.IsNullOrWhiteSpace(tuAttr.uvs1Path)) GetUvsTexture(tuAttr.uvs1Path, 0);
                    }
                    else if (attr is EFXAttributeTypeMeshV2 tm2)
                    {
                        GetMesh(tm2.MeshPath);
                        GetMdf(tm2.MaterialPath);
                        foreach (var p in tm2.properties)
                        {
                            if (!string.IsNullOrWhiteSpace(p.texturePath)) GetTexture(p.texturePath);
                        }
                    }
                    else if (attr is EFXAttributeTypeMesh tm1)
                    {
                        GetMesh(tm1.meshPath);
                        GetMdf(tm1.mdfPath);
                    }
                    else if (attr is EFXAttributeTypeGpuMesh gm)
                    {
                        GetMesh(gm.MeshPath);
                        GetMdf(gm.MaterialPath);
                    }
                }
            }
            catch { }
        }
    }

    public void Dispose()
    {
        foreach (var tex in _texCache.Values)
        {
            tex?.Dispose();
        }
        _texCache.Clear();

        foreach (var mesh in _meshCache.Values)
        {
            mesh?.Dispose();
        }
        _meshCache.Clear();
        _uvsCache.Clear();
        _mdfCache.Clear();
    }
}
