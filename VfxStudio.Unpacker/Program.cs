using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using VfxStudio.Core.Models;
using VfxStudio.Core.Services;

namespace VfxStudio.Unpacker;

internal class Program
{
    static int Main(string[] args)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("            RE-MotionVfxStudio - Verification & Extraction Tool");
        Console.WriteLine("================================================================================");

        string gameDir = Environment.GetEnvironmentVariable("MOTIONVFX_GAME_ROOT") ?? string.Empty;
        bool forceExtract = args.Contains("--extract-phase1");
        bool verifyPhase2Only = args.Contains("--verify-phase2");
        bool verifyPhase3Only = args.Contains("--verify-phase3");
        bool inspectFbx = args.Contains("--inspect-fbx");
        bool inspectMesh = args.Contains("--inspect-mesh");
        bool extractVfxCompanions = args.Contains("--extract-vfx-companions");

        foreach (var arg in args)
        {
            if (!arg.StartsWith("--") && Directory.Exists(arg))
            {
                gameDir = arg;
            }
        }

        // Determine project Assets directory
        string currentDir = AppDomain.CurrentDomain.BaseDirectory;
        string assetsDir = Path.GetFullPath(Path.Combine(currentDir, @"..\..\..\..\Assets"));
        if (!Directory.Exists(assetsDir))
        {
            assetsDir = Path.GetFullPath(Path.Combine(currentDir, @"..\..\..\Assets"));
        }
        if (!Directory.Exists(assetsDir))
        {
            assetsDir = Path.GetFullPath(Path.Combine(currentDir, @"Assets"));
        }
        if (!Directory.Exists(assetsDir))
        {
            assetsDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Assets"));
        }
        string? configuredAssets = Environment.GetEnvironmentVariable("MOTIONVFX_ASSETS");
        if (!string.IsNullOrWhiteSpace(configuredAssets)) assetsDir = Path.GetFullPath(configuredAssets);

        string listFilePath = Path.Combine(assetsDir, "Lists", "MHWS_STM.list");

        Console.WriteLine($"[Config] Game Directory:    {gameDir}");
        Console.WriteLine($"[Config] Project Assets:    {assetsDir}");
        Console.WriteLine($"[Config] List File Path:    {listFilePath}");
        Console.WriteLine();

        bool inspectPfb = args.Contains("--inspect-pfb");

        if (inspectPfb)
        {
            string compactJson = Path.Combine(assetsDir, "Lists", "rsz_pfb_classes.json");
            var opt = new ReeLib.RszFileOption(ReeLib.GameName.mhwilds, ReeLib.RszParser.GetInstance(compactJson));
            string wp02Dir = Path.Combine(assetsDir, "WeaponBases", "wp02");
            foreach (var pfbPath in Directory.GetFiles(wp02Dir, "*.pfb.18", SearchOption.TopDirectoryOnly))
            {
                using var fs = File.OpenRead(pfbPath);
                var pfb = new ReeLib.PfbFile(opt, new ReeLib.FileHandler(fs, pfbPath));
                pfb.Read();
                pfb.SetupGameObjects();
                Console.WriteLine($"=== PFB: {Path.GetFileName(pfbPath)} ===");
                void PrintGoBrief(ReeLib.Pfb.PfbGameObject go, int depth)
                {
                    string indent = new string(' ', depth * 2);
                    var epvComp = go.Components.FirstOrDefault(c => c.RszClass.name.Contains("EPV"));
                    int elemCount = 0;
                    if (epvComp != null)
                    {
                        for (int f = 0; f < epvComp.Fields.Length; f++)
                            if (epvComp.Fields[f].name == "Elements" && epvComp.Values[f] is System.Collections.IList l)
                                elemCount = l.Count;
                    }
                    Console.WriteLine($"{indent}GO '{go.Name}' Comp={epvComp?.RszClass.name ?? "none"} Elements={elemCount}");
                    if (epvComp != null && epvComp.RszClass.name == "via.effect.script.EPVStandardData")
                    {
                        for (int f = 0; f < epvComp.Fields.Length; f++)
                        {
                            if (epvComp.Fields[f].name == "Elements" && epvComp.Values[f] is System.Collections.IList l)
                            {
                                foreach (var item in l.OfType<ReeLib.RszInstance>())
                                {
                                    uint id = 0;
                                    int relChild = -1;
                                    string jn = "";
                                    var resList = new List<string>();
                                    for (int ef = 0; ef < item.Fields.Length; ef++)
                                    {
                                        string fn = item.Fields[ef].name;
                                        if (fn == "ID" && item.Values[ef] is uint u) id = u;
                                        else if (fn == "RelationChildId" && item.Values[ef] is int rc) relChild = rc;
                                        else if (fn == "JointName" && item.Values[ef] is string s) jn = s;
                                        else if (fn == "Resources" && item.Values[ef] is System.Collections.IList rl)
                                        {
                                            foreach (var r in rl) resList.Add(r?.ToString() ?? "");
                                        }
                                    }
                                    Console.WriteLine($"{indent}  Elem ID={id} RelChild={relChild} Joint='{jn}' Res=[{string.Join(", ", resList)}]");
                                }
                            }
                        }
                    }
                    foreach (var ch in go.Children) PrintGoBrief(ch, depth + 1);
                }
                foreach (var root in pfb.GameObjects) PrintGoBrief(root, 0);
            }

            string motlistPath = Path.Combine(assetsDir, "Samples", "wp02_00.motlist.992");
            if (File.Exists(motlistPath))
            {
                var mgr = new MotionVfxSlotManager();
                var ml = mgr.LoadMotlist(motlistPath);
                var mots = mgr.GetMotions(ml, "wp02");
                Console.WriteLine($"=== wp02_00.motlist.992 Motions with VFX Slots ===");
                foreach (var m in mots.Where(x => x.VfxSlots.Count > 0).Take(15))
                {
                    Console.WriteLine($"Motion [{m.MotId}] {m.Name} ({m.Remark}): {string.Join(" | ", m.VfxSlots.Select(s => $"EffectId={s.EffectId} Cid={s.ContainerId} Eid={s.ElementId} Joint={s.JointName} [{s.StartFrame}-{s.EndFrame}]"))}");
                }
            }
            return 0;
        }

        if (extractVfxCompanions)
        {
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
                throw new DirectoryNotFoundException("Pass the game installation folder as a positional argument or set MOTIONVFX_GAME_ROOT.");
            string rszJson = Environment.GetEnvironmentVariable("MOTIONVFX_RSZ_SCHEMA") ?? Path.Combine(assetsDir, "Lists", "rsz_pfb_classes.json");
            var extractor = new WeaponBaseExtractor(gameDir, listFilePath, assetsDir);
            extractor.ExtractVfxCompanionAssets(rszJson);
            return 0;
        }

        if (inspectMesh)
        {
            string[] testWeapons = ["wp00", "wp01", "wp02", "wp03", "wp04", "wp05", "wp06", "wp07", "wp08", "wp09", "wp10", "wp11", "wp12", "wp13"];
            foreach (var wp in testWeapons)
            {
                string wpDir = Path.Combine(assetsDir, "WeaponBases", wp);
                var meshFiles = Directory.GetFiles(wpDir, "*.mesh.*", SearchOption.AllDirectories)
                    .Where(f => !f.Contains("/streaming/") && !f.Contains("\\streaming\\"))
                    .OrderBy(f => f);

                foreach (var mf in meshFiles)
                {
                    using var fs = File.OpenRead(mf);
                    var handler = new ReeLib.FileHandler(fs, mf);
                    var mesh = new ReeLib.MeshFile(handler);
                    mesh.Read();
                    Console.WriteLine($"\n>>> Weapon {wp} Mesh: {Path.GetFileName(mf)}");
                    Console.WriteLine($"  Bones count: {mesh.BoneData?.Bones.Count ?? 0}");
                    if (mesh.BoneData != null)
                    {
                        for (int b = 0; b < mesh.BoneData.Bones.Count; b++)
                        {
                            var bone = mesh.BoneData.Bones[b];
                            Console.WriteLine($"    Bone [{b}]: name='{bone.name}', remapIndex={bone.remapIndex}, local={bone.localTransform.m30:F3}, {bone.localTransform.m31:F3}, {bone.localTransform.m32:F3}");
                        }
                    }
                }
            }
            return 0;
        }

        if (inspectFbx)
        {
            string fbxPath = Path.Combine(assetsDir, "HunterBase", "MHWilds_Female.fbx");
            Console.WriteLine($">>> Inspecting FBX: {fbxPath}");
            using var importer = new Assimp.AssimpContext();
            var scene = importer.ImportFile(fbxPath, Assimp.PostProcessSteps.Triangulate);
            Console.WriteLine($"[FBX] HasMeshes: {scene.HasMeshes}, Count: {scene.MeshCount}");
            int totalBones = 0;
            var boneNames = new HashSet<string>();
            for (int i = 0; i < scene.MeshCount; i++)
            {
                var m = scene.Meshes[i];
                var mat = scene.Materials[m.MaterialIndex];
                var min = new System.Numerics.Vector3(m.Vertices.Min(v => v.X), m.Vertices.Min(v => v.Y), m.Vertices.Min(v => v.Z));
                var max = new System.Numerics.Vector3(m.Vertices.Max(v => v.X), m.Vertices.Max(v => v.Y), m.Vertices.Max(v => v.Z));
                Console.WriteLine($"  Mesh [{i}]: Name='{m.Name}', Mat='{mat.Name}', Verts={m.VertexCount}, Faces={m.FaceCount}, BBox=[{min.X:F2},{min.Y:F2},{min.Z:F2}] to [{max.X:F2},{max.Y:F2},{max.Z:F2}]");
            }
            Console.WriteLine($"[FBX] Total unique bone names across meshes: {boneNames.Count}");
            
            // Check all nodes in node hierarchy
            var rootNode = scene.RootNode.FindNode("root");
            if (rootNode != null)
            {
                Console.WriteLine($"Found 'root' node! Children count: {rootNode.ChildCount}");
                var rHand = scene.RootNode.FindNode("R_Hand");
                var lHand = scene.RootNode.FindNode("L_Hand");
                var rHandNode = scene.RootNode.FindNode("R_Hand");
                if (rHandNode != null)
                {
                    // Traverse chain from root to R_Hand
                    var chain = new List<Assimp.Node>();
                    var p = rHandNode;
                    while (p != null && p.Name != "MHWilds_Female Armature" && p.Name != "RootNode")
                    {
                        chain.Add(p);
                        p = p.Parent;
                    }
                    chain.Reverse();

                    var m1 = System.Numerics.Matrix4x4.Identity;
                    foreach (var n in chain)
                    {
                        m1 = System.Numerics.Matrix4x4.Transpose(n.Transform) * m1;
                    }

                    var m2 = System.Numerics.Matrix4x4.Identity;
                    foreach (var n in chain)
                    {
                        m2 = m2 * System.Numerics.Matrix4x4.Transpose(n.Transform);
                    }

                    Console.WriteLine($"Accumulation order 1 (local * parent): {m1.Translation}");
                    Console.WriteLine($"Accumulation order 2 (parent * local): {m2.Translation}");

                    Console.WriteLine($"m1 Row0 (X): {m1.M11:F3}, {m1.M12:F3}, {m1.M13:F3}");
                    Console.WriteLine($"m1 Row1 (Y): {m1.M21:F3}, {m1.M22:F3}, {m1.M23:F3}");
                    Console.WriteLine($"m1 Row2 (Z): {m1.M31:F3}, {m1.M32:F3}, {m1.M33:F3}");
                }
            }
            return 0;
        }

        if (verifyPhase3Only)
        {
            Console.WriteLine(">>> Running Phase 3 GPU Hardware Accelerated Rendering Verification...");
            var p3Report = VfxStudio.Renderer.Verification.Phase3GpuVerifier.Run(assetsDir);
            Console.WriteLine($"[Phase3] Verification Result: {(p3Report.Success ? "SUCCESS" : "FAILED")}");
            if (!p3Report.Success)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Error] Phase 3 failed: {p3Report.ErrorMessage}");
                Console.ResetColor();
                return 5;
            }
            return 0;
        }

        if (!Directory.Exists(gameDir))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Error] Game directory does not exist: {gameDir}");
            Console.ResetColor();
            return 1;
        }

        if (!File.Exists(listFilePath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Error] Embedded list file does not exist: {listFilePath}");
            Console.ResetColor();
            return 2;
        }

        bool needExtraction = forceExtract || !Directory.Exists(Path.Combine(assetsDir, "WeaponBases", "wp13"));

        if (needExtraction && !verifyPhase2Only)
        {
            Console.WriteLine(">>> Running Phase 1 Base Body & Weapon Extraction...");
            var extractor = new WeaponBaseExtractor(gameDir, listFilePath, assetsDir);
            var report = extractor.Run();
            if (!report.Success)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Error] Extraction failed: {report.Message}");
                Console.ResetColor();
                return 3;
            }
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("[Info] Phase 1 assets already present in Assets/WeaponBases & Assets/HunterBase. Skipping extraction.");
            Console.ResetColor();
        }

        // Run Phase 2 Verification
        var phase2Verifier = new Phase2VerificationService(gameDir, assetsDir, listFilePath);
        bool p2Success = phase2Verifier.RunVerification();
        if (!p2Success)
        {
            return 4;
        }

        if (!verifyPhase2Only)
        {
            Console.WriteLine("\n>>> Running Phase 3 GPU Hardware Accelerated Rendering Verification...");
            var p3Report = VfxStudio.Renderer.Verification.Phase3GpuVerifier.Run(assetsDir);
            Console.WriteLine($"[Phase3] Verification Result: {(p3Report.Success ? "SUCCESS" : "FAILED")}");
            if (!p3Report.Success)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Error] Phase 3 failed: {p3Report.ErrorMessage}");
                Console.ResetColor();
                return 5;
            }
        }

        return 0;
    }
}
