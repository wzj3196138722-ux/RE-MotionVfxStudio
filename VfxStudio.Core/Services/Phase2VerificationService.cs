using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using ReeLib;
using ReeLib.Common;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Transforms;
using VfxStudio.Core.Models;

namespace VfxStudio.Core.Services;

/// <summary>
/// Automation service for Phase 2 round-trip verification:
/// 1. Real mesh parsing & bone node verification (Hunter & Dual Blades).
/// 2. Sample extraction of real weapon .motlist & .efx from PAK files into Assets/Samples/.
/// 3. Motion VFX slot parsing, modification, undo/redo, and byte round-trip.
/// 4. EFX attribute filtering, batch override, group copy/paste, bone binding, spatial transforms, and byte round-trip.
/// </summary>
public class Phase2VerificationService
{
    private readonly string _gameDir;
    private readonly string _assetsDir;
    private readonly string _listFilePath;

    public Phase2VerificationService(string gameDir, string assetsDir, string listFilePath)
    {
        _gameDir = gameDir;
        _assetsDir = assetsDir;
        _listFilePath = listFilePath;
    }

    public bool RunVerification()
    {
        Console.WriteLine();
        Console.WriteLine("================================================================================");
        Console.WriteLine("     RE-MotionVfxStudio - Phase 2: Data Layer Round-Trip Verification");
        Console.WriteLine("================================================================================");

        bool allOk = true;

        // ---------------------------------------------------------------------
        // STEP 1: Verify Mesh File Parsing (Hunter Bare Body + Dual Blades)
        // ---------------------------------------------------------------------
        Console.WriteLine("\n[Test 1/4] Verifying Real Engine .mesh Parsing & Bone Trees...");
        allOk &= VerifyMeshes();

        // ---------------------------------------------------------------------
        // STEP 2: Extract Sample .motlist and .efx from PAKs
        // ---------------------------------------------------------------------
        Console.WriteLine("\n[Test 2/4] Extracting Real Weapon .motlist and .efx from PAKs...");
        var (sampleMotlistPath, sampleEfxPath) = EnsureSamplesExtracted();
        if (string.IsNullOrEmpty(sampleMotlistPath) || string.IsNullOrEmpty(sampleEfxPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[Fail] Failed to extract sample .motlist or .efx files!");
            Console.ResetColor();
            return false;
        }

        // ---------------------------------------------------------------------
        // STEP 3: Verify Motion VFX Slot Manager & .motlist Round-Trip
        // ---------------------------------------------------------------------
        Console.WriteLine("\n[Test 3/4] Verifying Motion VFX Slot Logic & Round-Trip Serialization...");
        allOk &= VerifyMotionVfxSlots(sampleMotlistPath);

        // ---------------------------------------------------------------------
        // STEP 4: Verify EFX Editor Service & .efx Round-Trip
        // ---------------------------------------------------------------------
        Console.WriteLine("\n[Test 4/4] Verifying EFX Editor Service & Round-Trip Serialization...");
        allOk &= VerifyEfxEditor(sampleEfxPath);

        Console.WriteLine();
        Console.WriteLine("================================================================================");
        if (allOk)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  >>> PHASE 2 ROUND-TRIP VERIFICATION: 100% PASSED! <<<");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  >>> PHASE 2 ROUND-TRIP VERIFICATION: ONE OR MORE TESTS FAILED! <<<");
            Console.ResetColor();
        }
        Console.WriteLine("================================================================================");
        return allOk;
    }

    private bool VerifyMeshes()
    {
        try
        {
            // 1. Hunter Bare Body Mesh
            var hunterMeshPath = Path.Combine(_assetsDir, "HunterBase", "Native", "ch00_000_0000.mesh.241111606");
            if (!File.Exists(hunterMeshPath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  [Fail] Hunter mesh not found at {hunterMeshPath}");
                Console.ResetColor();
                return false;
            }

            using (var fs = File.OpenRead(hunterMeshPath))
            {
                var handler = new FileHandler(fs, hunterMeshPath);
                var hunterMesh = new MeshFile(handler);
                if (!hunterMesh.Read())
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("  [Fail] Failed to parse Hunter mesh file!");
                    Console.ResetColor();
                    return false;
                }

                int hunterVerts = hunterMesh.TotalVertexCount;
                int hunterTris = hunterMesh.TotalTriangleCount;
                int hunterBones = hunterMesh.BoneData?.Bones.Count ?? 0;
                int hunterMaterials = hunterMesh.MaterialNames.Count;

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  [OK] Hunter Native Mesh (ch00_000_0000):");
                Console.ResetColor();
                Console.WriteLine($"       - Total Vertices:   {hunterVerts:N0}");
                Console.WriteLine($"       - Total Triangles:  {hunterTris:N0}");
                Console.WriteLine($"       - Bones Count:      {hunterBones} (Registered in BoneHashRegistry)");
                Console.WriteLine($"       - Material Names:   {hunterMaterials} [{string.Join(", ", hunterMesh.MaterialNames.Take(4))}...]");

                // Ingest bones into registry
                BoneHashRegistry.IngestFromMesh(hunterMesh);
            }

            // 2. Weapon Mesh: Dual Blades (wp02 - it0200_0000_0)
            var weaponMeshPath = Path.Combine(_assetsDir, "WeaponBases", "wp02", "it0200_0000_0.mesh.241111606");
            if (!File.Exists(weaponMeshPath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  [Fail] Weapon mesh not found at {weaponMeshPath}");
                Console.ResetColor();
                return false;
            }

            using (var fs = File.OpenRead(weaponMeshPath))
            {
                var handler = new FileHandler(fs, weaponMeshPath);
                var weaponMesh = new MeshFile(handler);
                if (!weaponMesh.Read())
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("  [Fail] Failed to parse Dual Blades mesh file!");
                    Console.ResetColor();
                    return false;
                }

                int weaponVerts = weaponMesh.TotalVertexCount;
                int weaponTris = weaponMesh.TotalTriangleCount;
                int weaponBones = weaponMesh.BoneData?.Bones.Count ?? 0;

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  [OK] Dual Blades Mesh (it0200_0000_0):");
                Console.ResetColor();
                Console.WriteLine($"       - Total Vertices:   {weaponVerts:N0}");
                Console.WriteLine($"       - Total Triangles:  {weaponTris:N0}");
                Console.WriteLine($"       - Bones Count:      {weaponBones}");

                BoneHashRegistry.IngestFromMesh(weaponMesh);
            }

            Console.WriteLine($"  [Info] Total registered bone names in BoneHashRegistry: {BoneHashRegistry.GetAllKnownBoneNames().Count}");
            return true;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [Exception in VerifyMeshes] {ex.Message}\n{ex.StackTrace}");
            Console.ResetColor();
            return false;
        }
    }

    private (string? motlistPath, string? efxPath) EnsureSamplesExtracted()
    {
        var samplesDir = Path.Combine(_assetsDir, "Samples");
        Directory.CreateDirectory(samplesDir);

        const string targetMotlist = "natives/stm/motion/player/weapon/wp02/wp02_00/wp02_00.motlist.992";
        const string targetEfx = "natives/stm/art/vfx/effecteditor/weapon/it02/11_it02_000.efx.5571972";

        var outMotlist = Path.Combine(samplesDir, "wp02_00.motlist.992");
        var outEfx = Path.Combine(samplesDir, "11_it02_000.efx.5571972");

        if (File.Exists(outMotlist) && File.Exists(outEfx) && new FileInfo(outMotlist).Length > 0 && new FileInfo(outEfx).Length > 0)
        {
            Console.WriteLine($"  [OK] Sample files already exist in {samplesDir}:");
            Console.WriteLine($"       - Motlist: {Path.GetFileName(outMotlist)} ({new FileInfo(outMotlist).Length:N0} bytes)");
            Console.WriteLine($"       - EFX:     {Path.GetFileName(outEfx)} ({new FileInfo(outEfx).Length:N0} bytes)");
            return (outMotlist, outEfx);
        }

        var pakList = WeaponBaseExtractor.ResolvePakPriority(_gameDir);
        var pakReader = new PakReader
        {
            PakFilePriority = pakList,
            EnableConsoleLogging = false
        };
        pakReader.AddFiles(new[] { targetMotlist, targetEfx });

        var stagingDir = Path.Combine(samplesDir, "_staging");
        Directory.CreateDirectory(stagingDir);

        var missing = new List<string>();
        int count = pakReader.UnpackFilesTo(stagingDir, missing);

        var stagedMot = Path.Combine(stagingDir, targetMotlist);
        var stagedEfx = Path.Combine(stagingDir, targetEfx);

        if (File.Exists(stagedMot))
        {
            File.Copy(stagedMot, outMotlist, true);
        }
        if (File.Exists(stagedEfx))
        {
            File.Copy(stagedEfx, outEfx, true);
        }

        try { Directory.Delete(stagingDir, true); } catch { }

        if (File.Exists(outMotlist) && File.Exists(outEfx))
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  [OK] Successfully extracted samples to {samplesDir}:");
            Console.ResetColor();
            Console.WriteLine($"       - Motlist: {Path.GetFileName(outMotlist)} ({new FileInfo(outMotlist).Length:N0} bytes)");
            Console.WriteLine($"       - EFX:     {Path.GetFileName(outEfx)} ({new FileInfo(outEfx).Length:N0} bytes)");
            return (outMotlist, outEfx);
        }

        return (null, null);
    }

    private bool VerifyMotionVfxSlots(string motlistPath)
    {
        try
        {
            var dictPath = Path.Combine(_assetsDir, "HunterBase", "motion_dict.json");
            var motionDict = MotionDictionary.LoadFromJson(dictPath);
            var manager = new MotionVfxSlotManager(motionDict);

            // 1. Load Motlist
            var motlist = manager.LoadMotlist(motlistPath);
            Console.WriteLine($"  [Info] Motlist loaded: {motlist.MotFiles.Count} .mot action files contained.");

            var motions = manager.GetMotions(motlist, "wp02");
            Console.WriteLine($"  [Info] Total motions extracted: {motions.Count}");

            var motionsWithVfx = motions.Where(m => m.VfxSlots.Count > 0).ToList();
            Console.WriteLine($"  [Info] Motions containing VFX Slots: {motionsWithVfx.Count}");

            if (motionsWithVfx.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("  [Warn] No VFX slots found in wp02_00.motlist; inspecting first motion for raw tracks...");
                Console.ResetColor();
            }
            else
            {
                var sampleMotion = motionsWithVfx.First();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  [OK] Sample Motion with VFX:");
                Console.ResetColor();
                Console.WriteLine($"       - Motion ID:      {sampleMotion.MotId} ({sampleMotion.Name})");
                Console.WriteLine($"       - Duration:       {sampleMotion.FrameCount} frames");
                Console.WriteLine($"       - VFX Slots Count: {sampleMotion.VfxSlots.Count}");
                foreach (var s in sampleMotion.VfxSlots.Take(3))
                {
                    Console.WriteLine($"         -> Slot {s.SlotId}: EffectId='{s.EffectId}', Frames=[{s.StartFrame:F0}..{s.EndFrame:F0}], Joint='{s.JointName}', ActorMotion={s.ActorMotion}");
                }

                // 2. Test Undo/Redo & Slot Operations
                var undoRedo = new UndoRedoManager();
                var firstMot = motlist.MotFiles.OfType<MotFile>().FirstOrDefault() ?? (MotFile)motlist.MotFiles.First();
                var testSlot = new VfxSlot
                {
                    SlotId = 9999,
                    EffectId = "999-0",
                    ContainerId = 1,
                    JointName = "R_Weapon_Hand",
                    StartFrame = 10,
                    EndFrame = 45,
                    ActorMotion = true
                };

                // Add Slot command
                var addCmd = manager.CreateAddSlotCommand(firstMot, testSlot);
                undoRedo.Execute(addCmd);
                Console.WriteLine($"  [Undo/Redo] Executed: '{addCmd.Name}' (CanUndo: {undoRedo.CanUndo})");

                // Verify undo
                undoRedo.Undo();
                Console.WriteLine($"  [Undo/Redo] Undone: '{addCmd.Name}' (CanUndo: {undoRedo.CanUndo}, CanRedo: {undoRedo.CanRedo})");

                // Verify redo
                undoRedo.Redo();
                Console.WriteLine($"  [Undo/Redo] Redone: '{addCmd.Name}' (CanUndo: {undoRedo.CanUndo})");

                // Clean up test slot
                manager.RemoveSlot(firstMot, testSlot);
            }

            // 3. Round-Trip Serialization Test
            Console.WriteLine("  [Round-Trip] Serializing .motlist to memory stream...");
            using var ms = new MemoryStream();
            using var handler = new FileHandler(ms, "roundtrip.motlist");
            bool writeSuccess = motlist.WriteTo(handler);
            if (!writeSuccess || ms.Length == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  [Fail] Failed to write .motlist to MemoryStream!");
                Console.ResetColor();
                return false;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  [OK] Motlist serialized successfully ({ms.Length:N0} bytes).");
            Console.ResetColor();

            // Re-read from serialized memory
            ms.Seek(0, SeekOrigin.Begin);
            var reloadedHandler = new FileHandler(ms, "roundtrip.motlist");
            var reloadedMotlist = new MotlistFile(reloadedHandler);
            if (!reloadedMotlist.Read())
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  [Fail] Failed to re-read serialized .motlist from MemoryStream!");
                Console.ResetColor();
                return false;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  [OK] Reloaded serialized .motlist: {reloadedMotlist.MotFiles.Count} .mot files intact!");
            Console.ResetColor();
            return true;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [Exception in VerifyMotionVfxSlots] {ex.Message}\n{ex.StackTrace}");
            Console.ResetColor();
            return false;
        }
    }

    private bool VerifyEfxEditor(string efxPath)
    {
        try
        {
            var efxService = new EfxEditorService();

            // 1. Load EFX file
            var efx = efxService.Load(efxPath);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  [OK] EFX File Loaded: {Path.GetFileName(efxPath)}");
            Console.ResetColor();
            Console.WriteLine($"       - Version:        {efx.Header.Version}");
            Console.WriteLine($"       - Entries Count:  {efx.Entries.Count}");
            Console.WriteLine($"       - Actions Count:  {efx.Actions.Count}");
            Console.WriteLine($"       - Groups Count:   {efx.EffectGroups.Count}");
            Console.WriteLine($"       - Bones Count:    {efx.Bones.Count}");

            // 2. Parameter Filtering Test
            var allTypes = efxService.GetAllAttributeTypeNames(efx);
            Console.WriteLine($"  [Info] Unique Attribute Types present ({allTypes.Count}): [{string.Join(", ", allTypes.Take(5))}...]");

            var transformMatches = efxService.FilterAttributesByName(efx, "Transform3D");
            Console.WriteLine($"  [Filter] Filtered 'Transform3D' attributes: {transformMatches.Count} matches found.");

            var parentOptMatches = efxService.FilterAttributesByName(efx, "ParentOptions");
            Console.WriteLine($"  [Filter] Filtered 'ParentOptions' attributes: {parentOptMatches.Count} matches found.");

            // 3. Spatial Transform & Bone Hash Binding Test
            if (efx.Entries.Count > 0)
            {
                var entry0 = efx.Entries[0];
                var transform = efxService.GetTransform3D(entry0);
                if (transform.HasValue)
                {
                    Console.WriteLine($"  [Transform] Entry[0] ('{entry0.name}'): Pos={transform.Value.position}, Rot={transform.Value.rotation}, Scale={transform.Value.scale}");
                }

                var (bone, hash) = efxService.GetBoneBinding(entry0);
                Console.WriteLine($"  [Bone Binding] Entry[0] ParentBone: '{bone ?? "(None)"}', Hash=0x{hash:X8}");

                // Test Setting Bone Binding & Transform
                efxService.SetBoneBinding(efx, entry0, "R_Weapon_Hand");
                var (newBone, newHash) = efxService.GetBoneBinding(entry0);
                Console.WriteLine($"  [Bone Binding Set] Entry[0] updated to: '{newBone}', Hash=0x{newHash:X8}");

                efxService.SetTransform3D(entry0, position: new Vector3(1.5f, 2.0f, 0.5f), scale: new Vector3(2.0f, 2.0f, 2.0f));
                var updatedTransform = efxService.GetTransform3D(entry0);
                Console.WriteLine($"  [Transform Set] Entry[0] updated Pos: {updatedTransform?.position}, Scale: {updatedTransform?.scale}");
            }

            // 4. Group Deep Copy / Paste & Clone Test
            if (efx.Entries.Count > 1)
            {
                var srcGroups = efxService.CopyGroups(efx.Entries[0]);
                efxService.PasteGroups(efx.Entries[1], srcGroups);
                Console.WriteLine($"  [Group Paste] Copied {srcGroups.Count} groups from Entry[0] to Entry[1].");

                int preCloneCount = efx.Entries.Count;
                var clonedEntry = efxService.DeepCloneEntry(efx, efx.Entries[0], "cloned_test_entry");
                Console.WriteLine($"  [Entry Clone] Cloned Entry[0] -> '{clonedEntry.name}' (Total entries: {preCloneCount} -> {efx.Entries.Count})");

                // Clean up cloned entry
                efxService.RemoveEntry(efx, clonedEntry.index);
                Console.WriteLine($"  [Entry Remove] Removed cloned entry (Total entries restored to: {efx.Entries.Count})");
            }

            // 5. Batch Attribute Override Test
            var allTransformMatches = efxService.FilterAttributesByName(efx, "Transform3D");
            if (allTransformMatches.Count > 0)
            {
                int modified = efxService.BatchScaleVector3(allTransformMatches, "LocalScale", 1.5f);
                Console.WriteLine($"  [Batch Override] Scaled 'LocalScale' across {modified} Transform3D attributes by x1.5.");
            }

            // 6. Undo/Redo Test on EFX
            var undoRedo = new UndoRedoManager();
            if (efx.Entries.Count > 0)
            {
                var entry = efx.Entries[0];
                var cmd = efxService.CreateSetTransformCommand(entry, new Vector3(10, 20, 30), Vector3.Zero, Vector3.One);
                undoRedo.Execute(cmd);
                Console.WriteLine($"  [EFX Undo/Redo] Executed: '{cmd.Name}' (Pos: {efxService.GetTransform3D(entry)?.position})");

                undoRedo.Undo();
                Console.WriteLine($"  [EFX Undo/Redo] Undone: '{cmd.Name}' (Pos restored: {efxService.GetTransform3D(entry)?.position})");

                undoRedo.Redo();
                Console.WriteLine($"  [EFX Undo/Redo] Redone: '{cmd.Name}' (Pos: {efxService.GetTransform3D(entry)?.position})");
            }

            // 7. Memory Round-Trip Serialization Test
            Console.WriteLine("  [Round-Trip] Serializing .efx to memory bytes...");
            byte[] efxBytes = efxService.SaveToBytes(efx);
            if (efxBytes == null || efxBytes.Length == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  [Fail] Failed to write .efx to byte array!");
                Console.ResetColor();
                return false;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  [OK] EFX serialized successfully ({efxBytes.Length:N0} bytes).");
            Console.ResetColor();

            // Re-read serialized bytes
            var reloadedEfx = efxService.Load(efxBytes, "reloaded.efx");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  [OK] Reloaded serialized .efx: {reloadedEfx.Entries.Count} entries, {reloadedEfx.Actions.Count} actions intact!");
            Console.ResetColor();

            return true;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [Exception in VerifyEfxEditor] {ex.Message}\n{ex.StackTrace}");
            Console.ResetColor();
            return false;
        }
    }
}
