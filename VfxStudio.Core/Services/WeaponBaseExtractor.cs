using System.Diagnostics;
using System.Text.RegularExpressions;
using ReeLib;
using ReeLib.Common;
using ReeLib.Efx;
using VfxStudio.Core.Models;

namespace VfxStudio.Core.Services;

public class ExtractionReport
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int TotalFilesExtracted { get; set; }
    public Dictionary<string, List<string>> WeaponFiles { get; set; } = new();
    public List<string> HunterNativeFiles { get; set; } = new();
    public TimeSpan Elapsed { get; set; }
}

public class WeaponBaseExtractor
{
    public string GameDirectory { get; set; }
    public string ListFilePath { get; set; }
    public string OutputBaseDirectory { get; set; }

    public WeaponBaseExtractor(string gameDirectory, string listFilePath, string outputBaseDirectory)
    {
        GameDirectory = gameDirectory;
        ListFilePath = listFilePath;
        OutputBaseDirectory = outputBaseDirectory;
    }

    public static List<string> ResolvePakPriority(string gameDir)
    {
        var paks = new List<string>();

        // 1. Base pak
        var basePak = Path.Combine(gameDir, "re_chunk_000.pak");
        if (File.Exists(basePak)) paks.Add(basePak);

        // 2. Patches 001 - 099
        for (int i = 1; i <= 99; i++)
        {
            var patch = Path.Combine(gameDir, $"re_chunk_000.pak.patch_{i:D3}.pak");
            if (File.Exists(patch)) paks.Add(patch);
        }

        // 3. Sub chunk 000 (streaming / hi-res textures)
        var subPak = Path.Combine(gameDir, "re_chunk_000.pak.sub_000.pak");
        if (File.Exists(subPak)) paks.Add(subPak);

        // 4. Sub patches 001 - 099
        for (int i = 1; i <= 99; i++)
        {
            var subPatch = Path.Combine(gameDir, $"re_chunk_000.pak.sub_000.pak.patch_{i:D3}.pak");
            if (File.Exists(subPatch)) paks.Add(subPatch);
        }

        return paks;
    }

    public ExtractionReport Run()
    {
        var sw = Stopwatch.StartNew();
        var report = new ExtractionReport();

        if (!Directory.Exists(GameDirectory))
        {
            report.Success = false;
            report.Message = $"Game directory not found: {GameDirectory}";
            return report;
        }

        if (!File.Exists(ListFilePath))
        {
            report.Success = false;
            report.Message = $"List file not found: {ListFilePath}";
            return report;
        }

        var pakList = ResolvePakPriority(GameDirectory);
        if (pakList.Count == 0)
        {
            report.Success = false;
            report.Message = $"No valid .pak files found in: {GameDirectory}";
            return report;
        }

        Console.WriteLine($"[Extractor] Found {pakList.Count} PAK files in {GameDirectory}");
        Console.WriteLine($"[Extractor] Loading list index from {ListFilePath}...");

        // Map weapon folders and hunter paths
        var weaponTargetPrefixes = WeaponDefinition.AllWeapons.ToDictionary(
            w => w.FirstOrdinalFolder,
            w => w
        );

        var targetFilesByWeapon = new Dictionary<WeaponDefinition, List<string>>();
        foreach (var w in WeaponDefinition.AllWeapons)
        {
            targetFilesByWeapon[w] = new List<string>();
        }

        var hunterTargetFiles = new List<string>();
        var allFilesToExtract = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Scan list file fast using buffered StreamReader
        using (var reader = new StreamReader(ListFilePath))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // Check 14 weapons
                foreach (var w in WeaponDefinition.AllWeapons)
                {
                    string artPrefix = w.FirstOrdinalFolder;
                    string streamingPrefix = artPrefix.Replace("natives/stm/art/", "natives/stm/streaming/art/");
                    string motlistPattern = $"natives/stm/motion/player/weapon/{w.WpCode}/{w.WpCode}_00/{w.WpCode}_00.motlist.";
                    string efxPrefix = $"natives/stm/art/vfx/effecteditor/weapon/{w.ItCode}/";

                    if (line.StartsWith(artPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        if (line.Contains(".mesh.", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains(".mdf2.", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains(".tex.", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains(".chain2.", StringComparison.OrdinalIgnoreCase))
                        {
                            targetFilesByWeapon[w].Add(line);
                            allFilesToExtract.Add(line);
                        }
                    }
                    else if (line.StartsWith(streamingPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        if (line.Contains(".mesh.", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains(".tex.", StringComparison.OrdinalIgnoreCase))
                        {
                            targetFilesByWeapon[w].Add(line);
                            allFilesToExtract.Add(line);
                        }
                    }
                    else if (line.StartsWith(motlistPattern, StringComparison.OrdinalIgnoreCase))
                    {
                        targetFilesByWeapon[w].Add(line);
                        allFilesToExtract.Add(line);
                    }
                    else if (line.StartsWith(efxPrefix, StringComparison.OrdinalIgnoreCase) && line.Contains(".efx.", StringComparison.OrdinalIgnoreCase))
                    {
                        targetFilesByWeapon[w].Add(line);
                        allFilesToExtract.Add(line);
                    }
                }

                // Check Hunter native body
                if (line.StartsWith("natives/stm/art/model/character/ch00/000/0000/ch00_000_0000.", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("natives/stm/streaming/art/model/character/ch00/000/0000/ch00_000_0000.", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("natives/stm/motion/player/decorate/hanging/pl_headtohipskeleton.skeleton.7", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("natives/stm/art/model/character/ch00/002/0000/textures/", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("natives/stm/art/model/character/ch00/500/0000/textures/", StringComparison.OrdinalIgnoreCase))
                {
                    hunterTargetFiles.Add(line);
                    allFilesToExtract.Add(line);
                }
            }
        }

        Console.WriteLine($"[Extractor] Matched {allFilesToExtract.Count} total files to extract across 14 weapons and hunter native body.");
        foreach (var w in WeaponDefinition.AllWeapons)
        {
            Console.WriteLine($"  - {w.WpCode} ({w.NameCn} - {w.NameEn}): {targetFilesByWeapon[w].Count} files");
        }
        Console.WriteLine($"  - Hunter Native Body: {hunterTargetFiles.Count} files");

        // Staging extraction directory
        var stagingDir = Path.Combine(OutputBaseDirectory, "_staging");
        if (Directory.Exists(stagingDir))
        {
            Directory.Delete(stagingDir, true);
        }
        Directory.CreateDirectory(stagingDir);

        // Run PakReader
        var pakReader = new PakReader
        {
            PakFilePriority = pakList,
            EnableConsoleLogging = false
        };
        pakReader.AddFiles(allFilesToExtract);

        Console.WriteLine("[Extractor] Extracting files from PAKs (multithreaded)...");
        var missingFiles = new List<string>();
        int extractedCount = pakReader.UnpackFilesTo(stagingDir, missingFiles);
        Console.WriteLine($"[Extractor] Extraction complete! Extracted {extractedCount} files from PAKs.");

        // Distribute to Assets/WeaponBases/{wpXX}/
        var weaponBasesDir = Path.Combine(OutputBaseDirectory, "WeaponBases");
        Directory.CreateDirectory(weaponBasesDir);

        foreach (var w in WeaponDefinition.AllWeapons)
        {
            var wpOutDir = Path.Combine(weaponBasesDir, w.WpCode);
            Directory.CreateDirectory(wpOutDir);

            var filesForThisWp = new List<string>();
            var prefix = kvp_KeyForWeapon(w);

            foreach (var relPath in targetFilesByWeapon[w])
            {
                var stagedFile = Path.Combine(stagingDir, relPath);
                if (File.Exists(stagedFile))
                {
                    // 1. Preserve native directory tree under wpXX
                    var targetPreserved = Path.Combine(wpOutDir, relPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPreserved)!);
                    File.Copy(stagedFile, targetPreserved, true);

                    // 2. Also copy direct convenience files into wpXX root
                    string fileName = Path.GetFileName(relPath);
                    if (relPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var subRelative = relPath.Substring(prefix.Length);
                        var targetDirect = Path.Combine(wpOutDir, subRelative);
                        Directory.CreateDirectory(Path.GetDirectoryName(targetDirect)!);
                        File.Copy(stagedFile, targetDirect, true);
                        filesForThisWp.Add(subRelative);
                    }
                    else if (relPath.Contains("/streaming/"))
                    {
                        var streamingDirect = Path.Combine(wpOutDir, "streaming", fileName);
                        Directory.CreateDirectory(Path.GetDirectoryName(streamingDirect)!);
                        File.Copy(stagedFile, streamingDirect, true);
                        filesForThisWp.Add("streaming/" + fileName);
                    }
                    else if (relPath.Contains(".motlist.") || relPath.Contains(".efx."))
                    {
                        var targetDirect = Path.Combine(wpOutDir, fileName);
                        File.Copy(stagedFile, targetDirect, true);
                        filesForThisWp.Add(fileName);
                    }
                }
            }

            // 3. Post-process: Automatically merge streaming mesh data into weapon meshes
            foreach (var meshPath in Directory.GetFiles(wpOutDir, "*.mesh.*", SearchOption.AllDirectories)
                .Where(f => !f.Contains("/streaming/") && !f.Contains("\\streaming\\")).ToList())
            {
                try
                {
                    string meshFileName = Path.GetFileName(meshPath);
                    string? streamingFile = Directory.GetFiles(wpOutDir, meshFileName, SearchOption.AllDirectories)
                        .FirstOrDefault(f => f.Contains("/streaming/") || f.Contains("\\streaming\\"));

                    if (streamingFile != null && File.Exists(streamingFile))
                    {
                        var meshBytes = File.ReadAllBytes(meshPath);
                        using var ms = new MemoryStream(meshBytes);
                        var handler = new FileHandler(ms, meshPath);
                        var meshFile = new MeshFile(handler);
                        if (meshFile.Read() && meshFile.RequiresStreamingData)
                        {
                            using var sfs = File.OpenRead(streamingFile);
                            meshFile.LoadStreamingData(new FileHandler(sfs, streamingFile));
                            meshFile.MergeStreamingMeshData();
                            meshFile.WriteTo(meshPath);
                            Console.WriteLine($"[Extractor] Merged streaming mesh into {meshFileName} for {w.WpCode} (LOD 0 Verts: {meshFile.MeshData?.LODs[0].VertexCount})");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Extractor] Note on {meshPath}: {ex.Message}");
                }
            }

            report.WeaponFiles[w.WpCode] = filesForThisWp;
        }

        // Distribute to Assets/HunterBase/Native/
        var hunterNativeDir = Path.Combine(OutputBaseDirectory, "HunterBase", "Native");
        Directory.CreateDirectory(hunterNativeDir);

        foreach (var relPath in hunterTargetFiles)
        {
            var stagedFile = Path.Combine(stagingDir, relPath);
            if (File.Exists(stagedFile))
            {
                // Preserve native tree
                var targetPreserved = Path.Combine(hunterNativeDir, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPreserved)!);
                File.Copy(stagedFile, targetPreserved, true);

                // Copy direct to Native root if it's the primary ch00_000_0000 mesh/mdf or skeleton (skip streaming buffer)
                var fileName = Path.GetFileName(relPath);
                if (!relPath.Contains("/streaming/") && (fileName.StartsWith("ch00_000_0000.") || fileName.Contains("skeleton")))
                {
                    var targetDirect = Path.Combine(hunterNativeDir, fileName);
                    File.Copy(stagedFile, targetDirect, true);
                }

                report.HunterNativeFiles.Add(relPath);
            }
        }

        // Cleanup staging directory
        try
        {
            Directory.Delete(stagingDir, true);
        }
        catch { }

        sw.Stop();
        report.Success = true;
        report.TotalFilesExtracted = extractedCount;
        report.Elapsed = sw.Elapsed;
        report.Message = $"Successfully extracted {extractedCount} files into Assets in {sw.Elapsed.TotalSeconds:F1}s.";

        return report;
    }

    private static string kvp_KeyForWeapon(WeaponDefinition w)
    {
        return w.FirstOrdinalFolder;
    }

    /// <summary>
    /// Scans all 14 weapons' PFB and EFX files, resolves all referenced companion VFX assets
    /// (.pfb, .efx, .uvs, .mesh, .mdf2, .tex + streaming counterparts), extracts them from PAKs into Assets,
    /// merges streaming meshes, and builds the compact rsz_pfb_classes.json for fast PFB parsing.
    /// </summary>
    public void ExtractVfxCompanionAssets(string? fullRszJsonPath = null)
    {
        var sw = Stopwatch.StartNew();
        var pakList = ResolvePakPriority(GameDirectory);
        if (pakList.Count == 0 || !File.Exists(ListFilePath))
        {
            Console.WriteLine("[VfxCompanionExtractor] Game PAKs or ListFile not found.");
            return;
        }

        // Build O(1) lookup from internal path (without version suffix) -> full list path(s)
        var listByNoVer = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var pfbPathsByWeapon = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var commonPfbPaths = new List<string>();

        foreach (var w in WeaponDefinition.AllWeapons)
        {
            pfbPathsByWeapon[w.WpCode] = new List<string>();
        }

        using (var reader = new StreamReader(ListFilePath))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // Strip trailing numeric version extension (.18, .5571972, .8, .241106027, etc.)
                int lastDot = line.LastIndexOf('.');
                if (lastDot > 0 && int.TryParse(line.AsSpan(lastDot + 1), out _))
                {
                    string noVer = line.Substring(0, lastDot);
                    if (!listByNoVer.TryGetValue(noVer, out var list))
                    {
                        listByNoVer[noVer] = list = new List<string>(1);
                    }
                    list.Add(line);
                }

                if (line.StartsWith("natives/stm/art/vfx/effectprovider/weapon/", StringComparison.OrdinalIgnoreCase) &&
                    line.EndsWith(".pfb.18", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var w in WeaponDefinition.AllWeapons)
                    {
                        if (line.Contains($"/{w.ItCode}/", StringComparison.OrdinalIgnoreCase))
                        {
                            pfbPathsByWeapon[w.WpCode].Add(line);
                        }
                    }
                }
                else if (line.StartsWith("natives/stm/art/vfx/effectprovider/common/", StringComparison.OrdinalIgnoreCase) &&
                         line.EndsWith(".pfb.18", StringComparison.OrdinalIgnoreCase))
                {
                    commonPfbPaths.Add(line);
                }
            }
        }

        void ResolveInternalPath(string? rawInternal, HashSet<string> targetSet, bool includeStreaming = true)
        {
            if (string.IsNullOrWhiteSpace(rawInternal)) return;
            string norm = rawInternal.Trim().Replace('\\', '/').TrimStart('/');
            if (norm.StartsWith("natives/stm/", StringComparison.OrdinalIgnoreCase))
            {
                norm = norm.Substring("natives/stm/".Length);
            }
            // Strip numeric suffix if already present
            int ld = norm.LastIndexOf('.');
            if (ld > 0 && int.TryParse(norm.AsSpan(ld + 1), out _))
            {
                norm = norm.Substring(0, ld);
            }

            string baseKey = "natives/stm/" + norm;
            if (listByNoVer.TryGetValue(baseKey, out var matches))
            {
                foreach (var m in matches) targetSet.Add(m);
            }
            if (includeStreaming)
            {
                string streamKey = "natives/stm/streaming/" + norm;
                if (listByNoVer.TryGetValue(streamKey, out var sMatches))
                {
                    foreach (var m in sMatches) targetSet.Add(m);
                }
            }
        }

        var stagingDir = Path.Combine(OutputBaseDirectory, "_vfx_staging");
        if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
        Directory.CreateDirectory(stagingDir);

        // Step 1: Extract all weapon & common .pfb.18 files
        var allPfbs = new HashSet<string>(commonPfbPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in pfbPathsByWeapon)
        {
            foreach (var p in kv.Value) allPfbs.Add(p);
        }

        Console.WriteLine($"[VfxCompanionExtractor] Extracting {allPfbs.Count} .pfb.18 files...");
        var pakReader = new PakReader { PakFilePriority = pakList, EnableConsoleLogging = false };
        pakReader.AddFiles(allPfbs);
        pakReader.UnpackFilesTo(stagingDir, new List<string>());

        // Step 2: Copy .pfb.18 files to Assets/WeaponBases/{wpXX} and inspect RSZ classes & referenced .efx paths
        var weaponBasesDir = Path.Combine(OutputBaseDirectory, "WeaponBases");
        var extraEfxByWeapon = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var extraAllEfx = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedRszTypeIds = new HashSet<uint>();

        RszFileOption? rszOpt = null;
        if (!string.IsNullOrEmpty(fullRszJsonPath) && File.Exists(fullRszJsonPath))
        {
            Console.WriteLine($"[VfxCompanionExtractor] Loading full RSZ table from {fullRszJsonPath} to inspect PFB classes...");
            rszOpt = new RszFileOption(GameName.mhwilds, RszParser.GetInstance(fullRszJsonPath));
        }

        foreach (var w in WeaponDefinition.AllWeapons)
        {
            string wpDir = Path.Combine(weaponBasesDir, w.WpCode);
            Directory.CreateDirectory(wpDir);
            extraEfxByWeapon[w.WpCode] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var relPfb in pfbPathsByWeapon[w.WpCode].Concat(commonPfbPaths))
            {
                string stagedPfb = Path.Combine(stagingDir, relPfb);
                if (!File.Exists(stagedPfb)) continue;

                // Copy to preserved path & convenience root under wpDir
                string destPreserved = Path.Combine(wpDir, relPfb);
                Directory.CreateDirectory(Path.GetDirectoryName(destPreserved)!);
                File.Copy(stagedPfb, destPreserved, true);

                string destDirect = Path.Combine(wpDir, Path.GetFileName(relPfb));
                File.Copy(stagedPfb, destDirect, true);

                if (rszOpt != null)
                {
                    try
                    {
                        using var fs = File.OpenRead(stagedPfb);
                        var handler = new FileHandler(fs, stagedPfb);
                        var pfb = new PfbFile(rszOpt, handler);
                        if (pfb.Read())
                        {
                            foreach (var res in pfb.ResourceInfoList)
                            {
                                if (res.Path.EndsWith(".efx", StringComparison.OrdinalIgnoreCase))
                                {
                                    ResolveInternalPath(res.Path, extraEfxByWeapon[w.WpCode], false);
                                    ResolveInternalPath(res.Path, extraAllEfx, false);
                                }
                            }
                            foreach (var inst in pfb.RSZ.InstanceList)
                            {
                                if (inst?.RszClass != null)
                                {
                                    usedRszTypeIds.Add(inst.RszClass.typeId);
                                    foreach (var f in inst.RszClass.fields)
                                    {
                                        if (f.StructClass != null) usedRszTypeIds.Add(f.StructClass.typeId);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[VfxCompanionExtractor] PFB read warning on {relPfb}: {ex.Message}");
                    }
                }
            }
        }

        // Save compact rsz_pfb_classes.json if we loaded fullRszJsonPath
        if (rszOpt != null && usedRszTypeIds.Count > 0)
        {
            var parser = RszParser.GetInstance(fullRszJsonPath!);
            var subDict = new Dictionary<string, RszClass>();
            foreach (var kv in parser.ClassDict)
            {
                if (usedRszTypeIds.Contains(kv.Key) ||
                    kv.Value.name.StartsWith("via.effect.", StringComparison.OrdinalIgnoreCase) ||
                    kv.Value.name == "via.GameObject" ||
                    kv.Value.name == "via.Transform" ||
                    kv.Value.name == "via.Folder")
                {
                    subDict[kv.Key.ToString("x")] = kv.Value;
                }
            }
            string compactJsonPath = Path.Combine(OutputBaseDirectory, "Lists", "rsz_pfb_classes.json");
            var jsonBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(subDict, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllBytes(compactJsonPath, jsonBytes);
            Console.WriteLine($"[VfxCompanionExtractor] Wrote compact rsz_pfb_classes.json ({subDict.Count} classes, {jsonBytes.Length / 1024.0:F1} KB).");
        }

        // Extract any extra .efx referenced by PFBs that weren't in weapon/{itXX}
        if (extraAllEfx.Count > 0)
        {
            var efxReader = new PakReader { PakFilePriority = pakList, EnableConsoleLogging = false };
            efxReader.AddFiles(extraAllEfx);
            efxReader.UnpackFilesTo(stagingDir, new List<string>());

            foreach (var w in WeaponDefinition.AllWeapons)
            {
                string wpDir = Path.Combine(weaponBasesDir, w.WpCode);
                foreach (var relEfx in extraEfxByWeapon[w.WpCode])
                {
                    string stagedEfx = Path.Combine(stagingDir, relEfx);
                    if (File.Exists(stagedEfx))
                    {
                        string destPreserved = Path.Combine(wpDir, relEfx);
                        Directory.CreateDirectory(Path.GetDirectoryName(destPreserved)!);
                        File.Copy(stagedEfx, destPreserved, true);

                        string destDirect = Path.Combine(wpDir, Path.GetFileName(relEfx));
                        if (!File.Exists(destDirect)) File.Copy(stagedEfx, destDirect, true);
                    }
                }
            }
        }

        // Step 3: Scan all .efx files across all 14 weapons to collect referenced .uvs, .mesh, .mdf2, .tex
        var uvsToExtract = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var meshToExtract = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mdfToExtract = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var texToExtract = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var efxService = new EfxEditorService();

        foreach (var w in WeaponDefinition.AllWeapons)
        {
            string wpDir = Path.Combine(weaponBasesDir, w.WpCode);
            if (!Directory.Exists(wpDir)) continue;

            var efxFiles = Directory.GetFiles(wpDir, "*.efx.*", SearchOption.TopDirectoryOnly);
            foreach (var efxPath in efxFiles)
            {
                try
                {
                    var efx = efxService.Load(efxPath);
                    foreach (var attr in efx.GetAttributesAndActions(includeEmbeddedEfx: true))
                    {
                        if (attr is ReeLib.Efx.Structs.Basic.EFXAttributeUVSequence uvsAttr)
                        {
                            ResolveInternalPath(uvsAttr.UVSPath, uvsToExtract, false);
                        }
                        else if (attr is ReeLib.Efx.Structs.Basic.EFXAttributeTextureUnit tuAttr)
                        {
                            ResolveInternalPath(tuAttr.uvs0Path, uvsToExtract, false);
                            ResolveInternalPath(tuAttr.uvs1Path, uvsToExtract, false);
                            ResolveInternalPath(tuAttr.uvs2Path, uvsToExtract, false);
                        }
                        else if (attr is ReeLib.Efx.Structs.Main.EFXAttributeTypeMeshV2 tm2)
                        {
                            ResolveInternalPath(tm2.MeshPath, meshToExtract, true);
                            ResolveInternalPath(tm2.MirrorMeshPath, meshToExtract, true);
                            ResolveInternalPath(tm2.MaterialPath, mdfToExtract, false);
                            if (tm2.texPaths != null)
                            {
                                foreach (var tp in tm2.texPaths) ResolveInternalPath(tp, texToExtract, true);
                            }
                            foreach (var p in tm2.properties)
                            {
                                ResolveInternalPath(p.texturePath, texToExtract, true);
                            }
                        }
                        else if (attr is ReeLib.Efx.Structs.Main.EFXAttributeTypeMesh tm1)
                        {
                            ResolveInternalPath(tm1.meshPath, meshToExtract, true);
                            ResolveInternalPath(tm1.mdfPath, mdfToExtract, false);
                            if (tm1.texPaths != null)
                            {
                                foreach (var tp in tm1.texPaths) ResolveInternalPath(tp, texToExtract, true);
                            }
                        }
                        else if (attr is ReeLib.Efx.Structs.Main.EFXAttributeTypeGpuMesh gm)
                        {
                            ResolveInternalPath(gm.MeshPath, meshToExtract, true);
                            ResolveInternalPath(gm.MirrorMeshPath, meshToExtract, true);
                            ResolveInternalPath(gm.MaterialPath, mdfToExtract, false);
                            if (gm.texturePaths != null)
                            {
                                foreach (var tp in gm.texturePaths) ResolveInternalPath(tp, texToExtract, true);
                            }
                        }
                        else if (attr is ReeLib.Efx.Structs.Main.EFXAttributeTypeGpuMeshTrailV2 gmt2)
                        {
                            ResolveInternalPath(gmt2.meshPath, meshToExtract, true);
                            ResolveInternalPath(gmt2.mdfPath, mdfToExtract, false);
                            if (gmt2.texPaths != null)
                            {
                                foreach (var tp in gmt2.texPaths) ResolveInternalPath(tp, texToExtract, true);
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore any malformed EFX
                }
            }
        }

        Console.WriteLine($"[VfxCompanionExtractor] Discovered from EFX: UVS={uvsToExtract.Count}, Mesh={meshToExtract.Count}, MDF2={mdfToExtract.Count}, DirectTex={texToExtract.Count}");

        // Step 4: Extract UVS, Mesh, and MDF2 first so we can parse UVS & MDF2 for their referenced .tex files!
        var firstPassFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in uvsToExtract) firstPassFiles.Add(f);
        foreach (var f in meshToExtract) firstPassFiles.Add(f);
        foreach (var f in mdfToExtract) firstPassFiles.Add(f);

        if (firstPassFiles.Count > 0)
        {
            var fpReader = new PakReader { PakFilePriority = pakList, EnableConsoleLogging = false };
            fpReader.AddFiles(firstPassFiles);
            int fpCount = fpReader.UnpackFilesTo(stagingDir, new List<string>());
            Console.WriteLine($"[VfxCompanionExtractor] Extracted {fpCount} UVS/Mesh/MDF2 files.");
        }

        // Step 5: Parse extracted .uvs.8 and .mdf2.45 to collect all indirect .tex paths!
        foreach (var relUvs in uvsToExtract)
        {
            string stagedUvs = Path.Combine(stagingDir, relUvs);
            if (!File.Exists(stagedUvs)) continue;
            try
            {
                using var fs = File.OpenRead(stagedUvs);
                var uvs = new UvsFile(new FileHandler(fs, stagedUvs));
                if (uvs.Read())
                {
                    foreach (var tb in uvs.Textures)
                    {
                        ResolveInternalPath(tb.path, texToExtract, true);
                    }
                }
            }
            catch { }
        }

        foreach (var relMdf in mdfToExtract)
        {
            string stagedMdf = Path.Combine(stagingDir, relMdf);
            if (!File.Exists(stagedMdf)) continue;
            try
            {
                using var fs = File.OpenRead(stagedMdf);
                var mdf = new MdfFile(new FileHandler(fs, stagedMdf));
                if (mdf.Read())
                {
                    foreach (var mat in mdf.Materials)
                    {
                        foreach (var th in mat.Textures)
                        {
                            ResolveInternalPath(th.texPath, texToExtract, true);
                        }
                    }
                }
            }
            catch { }
        }

        Console.WriteLine($"[VfxCompanionExtractor] Total VFX .tex files (including UVS & MDF2 textures + streaming): {texToExtract.Count}");

        if (texToExtract.Count > 0)
        {
            var texReader = new PakReader { PakFilePriority = pakList, EnableConsoleLogging = false };
            texReader.AddFiles(texToExtract);
            int texCount = texReader.UnpackFilesTo(stagingDir, new List<string>());
            Console.WriteLine($"[VfxCompanionExtractor] Extracted {texCount} VFX texture files.");
        }

        // Step 6: Merge streaming meshes in stagingDir and copy all extracted VFX assets into Assets/VfxShared/natives/stm/...
        var vfxSharedRoot = Path.Combine(OutputBaseDirectory, "VfxShared");
        Directory.CreateDirectory(vfxSharedRoot);

        var allExtractedRel = new HashSet<string>(firstPassFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var f in texToExtract) allExtractedRel.Add(f);
        foreach (var f in allPfbs) allExtractedRel.Add(f);
        foreach (var f in extraAllEfx) allExtractedRel.Add(f);

        foreach (var rel in allExtractedRel)
        {
            string staged = Path.Combine(stagingDir, rel);
            if (!File.Exists(staged)) continue;
            string dest = Path.Combine(vfxSharedRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(staged, dest, true);
        }

        // Merge streaming mesh data into non-streaming VFX meshes in VfxShared
        int mergedMeshes = 0;
        foreach (var relMesh in meshToExtract.Where(m => !m.Contains("/streaming/", StringComparison.OrdinalIgnoreCase)))
        {
            string meshDest = Path.Combine(vfxSharedRoot, relMesh);
            string streamRel = relMesh.Replace("natives/stm/", "natives/stm/streaming/", StringComparison.OrdinalIgnoreCase);
            string streamDest = Path.Combine(vfxSharedRoot, streamRel);
            if (File.Exists(meshDest) && File.Exists(streamDest))
            {
                try
                {
                    var meshBytes = File.ReadAllBytes(meshDest);
                    using var ms = new MemoryStream(meshBytes);
                    var meshFile = new MeshFile(new FileHandler(ms, meshDest));
                    if (meshFile.Read() && meshFile.RequiresStreamingData)
                    {
                        using var sfs = File.OpenRead(streamDest);
                        meshFile.LoadStreamingData(new FileHandler(sfs, streamDest));
                        meshFile.MergeStreamingMeshData();
                        meshFile.WriteTo(meshDest);
                        mergedMeshes++;
                    }
                }
                catch { }
            }
        }

        try { Directory.Delete(stagingDir, true); } catch { }
        sw.Stop();
        Console.WriteLine($"[VfxCompanionExtractor] Completed in {sw.Elapsed.TotalSeconds:F1}s! Merged {mergedMeshes} streaming VFX meshes into Assets/VfxShared.");
    }
}
