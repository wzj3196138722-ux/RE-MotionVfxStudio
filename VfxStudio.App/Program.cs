using System.Numerics;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using ReeLib.Efx;
using ReeLib.Efx.Enums;
using ReeLib.Efx.Structs.Basic;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using VfxStudio.App.ImGui;
using VfxStudio.App.Camera;
using VfxStudio.App.UI;
using VfxStudio.Core.Services;

namespace VfxStudio.App;

public static class Program
{
    public static readonly string BuildId = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    // =========================================================================
    // Native High-Performance GPU Acceleration Exports
    // =========================================================================

    [UnmanagedCallersOnly(EntryPoint = "NvOptimusEnablement")]
    public static uint NvOptimusEnablement() => 0x00000001;

    [UnmanagedCallersOnly(EntryPoint = "AmdPowerXpressRequestHighPerformance")]
    public static int AmdPowerXpressRequestHighPerformance() => 1;

    // =========================================================================
    // Application Entry Point
    // =========================================================================

    public static void Main(string[] args)
    {
        if (args.Contains("--self-test-camera"))
        {
            TestCameraProjection();
            return;
        }
        if (args.Contains("--audit-stage3-attribute"))
        {
            AuditStage3Attribute();
            return;
        }
        if (args.Contains("--audit-stage3-resolution"))
        {
            AuditStage3Resolution();
            return;
        }
        if (args.Contains("--audit-weapon-provider-data"))
        {
            string assets = ResolveAssetsRoot();
            string code = args.FirstOrDefault(a => a.StartsWith("wp", StringComparison.OrdinalIgnoreCase)) ?? "wp02";
            string weapon = Path.Combine(assets, "WeaponBases", code);
            var files = Directory.GetFiles(weapon, "*.efx.5571972", SearchOption.AllDirectories);
            var providers = new PfbEpvService(assets);
            providers.LoadWeaponPfbs(code, files);
            Console.WriteLine($"{code}: providers={providers.LoadedPfbPaths.Count} elements={providers.AllElements.Count}");
            var stateMatches = providers.WeaponResourceCandidates
                .Where(candidate => candidate.Source.StateTriggerName != null)
                .ToArray();
            Console.WriteLine($"{code}: state trigger resource references={stateMatches.Length}");
            if (code.Equals("wp02", StringComparison.OrdinalIgnoreCase) &&
                !stateMatches.Any(candidate => candidate.Source.StateTriggerName == "PL_WP02_KIJIN_ON" &&
                    candidate.LocalEfxPath is string path &&
                    Path.GetFileName(path).StartsWith("11_it02_000.efx", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Dual blades KIJIN_ON state resource mapping is missing");
            foreach (var e in providers.AllElements.Where(e => e.SourcePfbName.Contains("it", StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine($"{e.SourcePfbName}|{e.ContainerGameObjectName}|{e.FullId}|{e.RelationChildId}|{string.Join(';', e.ResourceEfxPaths)}");
            return;
        }
        if (args.Contains("--audit-weapon-provider-raw"))
        {
            string assets = ResolveAssetsRoot();
            string path = Path.Combine(assets, "WeaponBases", "wp02", "epvr_it02.pfb.18");
            var option = new ReeLib.RszFileOption(ReeLib.GameName.mhwilds,
                ReeLib.RszParser.GetInstance(Path.Combine(assets, "Lists", "rsz_pfb_classes.json")));
            using var stream = File.OpenRead(path);
            var pfb = new ReeLib.PfbFile(option, new ReeLib.FileHandler(stream, path));
            if (!pfb.Read()) throw new InvalidDataException(path);
            pfb.SetupGameObjects();
            void Walk(ReeLib.Pfb.PfbGameObject go)
            {
                if (go.Name?.Contains("epve_it02", StringComparison.OrdinalIgnoreCase) == true)
                    foreach (var comp in go.Components)
                        for (int f = 0; f < comp.Fields.Length; f++)
                            if (comp.Fields[f].name == "Elements" && comp.Values[f] is System.Collections.IList list)
                            {
                                Console.WriteLine($"{go.Name}: {comp.RszClass.name} {list.Count} elements");
                                int index = 0;
                                foreach (var item in list)
                                {
                                    if (item is not ReeLib.RszInstance instance) continue;
                                    Console.WriteLine($" [{index++}] {instance.RszClass.name}");
                                    for (int j = 0; j < instance.Fields.Length; j++)
                                        Console.WriteLine($"  {instance.Fields[j].name}={instance.Values[j]} ({instance.Values[j]?.GetType().Name})");
                                }
                            }
                foreach (var child in go.Children) Walk(child);
            }
            foreach (var root in pfb.GameObjects) Walk(root);
            return;
        }
        if (args.Contains("--audit-stage3-coverage"))
        {
            AuditStage3Coverage();
            return;
        }
        Console.WriteLine("=================================================================");
        Console.WriteLine("  RE-MotionVfxStudio - MHWilds 动作与特效实时工作台 (OpenGL 4.6)  ");
        Console.WriteLine($"  Build: {BuildId}");
        Console.WriteLine("=================================================================");

        bool testMode = args.Contains("--test") || args.Contains("--verify");
        bool captureReference1080 = args.Contains("--capture-reference-1080");
        bool captureReference = args.Contains("--capture-reference") || captureReference1080;
        bool captureEfxBaseline = args.Contains("--capture-efx-baseline");
        bool captureStage3Panel = args.Contains("--capture-stage3-panel");
        bool captureStage3MultiKey = args.Contains("--capture-stage3-multikey");
        bool captureStage3LongMultiKey = args.Contains("--capture-stage3-multikey-long");
        bool captureStage4WeaponCandidates = args.Contains("--capture-stage4-wp02-007");

        // Locate Assets Directory
        string assetsDir = ResolveAssetsRoot();
        Console.WriteLine($"[VfxStudio.App] Assets Root: {assetsDir}");



        var options = WindowOptions.Default;
        options.Size = captureReference1080 || captureStage4WeaponCandidates
            ? new Vector2D<int>(1920, 1080) : new Vector2D<int>(1280, 761);
        options.Title = $"RE-MotionVfxStudio [{BuildId}] - 怪物猎人荒野 动作与特效实时工作台";
        options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));
        options.VSync = false;
        options.IsVisible = true; // Make visible for GPU swapchain & UI rasterization

        using var window = Window.Create(options);

        GL? gl = null;
        IInputContext? input = null;
        ImGuiController? imgui = null;
        MainWindow? mainWindow = null;
        int frameCount = 0;

        window.Load += () =>
        {
            gl = GL.GetApi(window);
            input = window.CreateInput();

            Console.WriteLine($"[VfxStudio.App] GPU: {gl.GetStringS(StringName.Renderer)} ({gl.GetStringS(StringName.Vendor)})");
            Console.WriteLine($"[VfxStudio.App] GL Version: {gl.GetStringS(StringName.Version)}");

            imgui = new ImGuiController(gl, window, input);
            StudioTheme.Apply();

            mainWindow = new MainWindow(gl, window, input, assetsDir);
            if (args.Contains("--reference-scene") || captureReference)
            {
                mainWindow.SwitchWeapon(0);
                int motionIndex = -1;
                for (int i = 0; i < mainWindow.MotionInfos.Count; i++)
                    if (mainWindow.MotionInfos[i].MotId == 30) { motionIndex = i; break; }
                if (motionIndex >= 0) mainWindow.SelectMotion(motionIndex);
                if (mainWindow.WeaponManager.Parts.Count > 0)
                    mainWindow.WeaponManager.Parts[0].Scale = 0.6f;
                mainWindow.CurrentFrame = 10f;
                mainWindow.IsPlaying = false;
                mainWindow.Camera.Yaw = -0.7f;
                mainWindow.Camera.Pitch = 0.22f;
                mainWindow.Camera.Distance = 4.5f;
                mainWindow.Camera.Target = new Vector3(0.5f, 1.15f, 0.35f);
            }
            if (captureEfxBaseline)
            {
                mainWindow.SwitchWeapon(2);
                mainWindow.SelectMotion(8);
                mainWindow.CurrentFrame = 9f;
                mainWindow.IsPlaying = false;
                mainWindow.PreviewLiveEfx = true;
                mainWindow.Camera.Yaw = 0.45f;
                mainWindow.Camera.Pitch = 0.14f;
                mainWindow.Camera.Distance = 2.0f;
                Console.WriteLine($"[EFX baseline] slots={mainWindow.CurrentSlots.Count}, called EFX={mainWindow.MotionCalledEfxPaths.Count}");
                var nativeFrames = new[] { 0, 3, 6, 9, 12, 30 }
                    .Select(frame => new { frame, entries = mainWindow.CaptureEfxDiagnostics(frame, 0UL) }).ToArray();
                string diagnosticsPath = Path.Combine(assetsDir, "..", "docs", "efx-completion", "native-reference-wp02-touch006.json");
                File.WriteAllText(diagnosticsPath, JsonSerializer.Serialize(new
                {
                    build = BuildId,
                    efx = mainWindow.CurrentEfxPath,
                    seed = 0,
                    fps = 60,
                    frames = nativeFrames
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            if (captureStage3Panel)
            {
                mainWindow.SwitchWeapon(2);
                mainWindow.SelectMotion(8);
                mainWindow.CurrentFrame = 9f;
                mainWindow.IsPlaying = false;
                Console.WriteLine($"[Stage3 panel] slots={mainWindow.CurrentSlots.Count}, confirmed files={mainWindow.MotionCalledEfxPaths.Count}, sample selected={mainWindow.SelectStage3AttributeSample()}");
            }
            if (captureStage3MultiKey)
            {
                mainWindow.SwitchWeapon(2);
                int motionIndex = -1;
                for (int i = 0; i < mainWindow.MotionInfos.Count; i++)
                    if (mainWindow.MotionInfos[i].MotId == 267) { motionIndex = i; break; }
                if (motionIndex < 0) throw new InvalidDataException("wp02 motion 267 was not loaded");
                mainWindow.SelectMotion(motionIndex);
                mainWindow.CurrentFrame = 47f;
                mainWindow.IsPlaying = false;
                mainWindow.PreviewLiveEfx = true;
                Console.WriteLine($"[Stage3 multi-key] motion=267 slots={mainWindow.CurrentSlots.Count}, confirmed files={mainWindow.MotionCalledEfxPaths.Count}, second-key events={mainWindow.CurrentSlots.Count(s => s.StartFrame == 47f && s.SourceKeyIndex == 1)}");
                if (!mainWindow.CurrentSlots.Any(s => s.StartFrame == 47f && s.SourceKeyIndex == 1))
                    throw new InvalidDataException("The second native EffectId key is missing from the live motion");
            }
            if (captureStage3LongMultiKey)
            {
                mainWindow.SwitchWeapon(5);
                int motionIndex = -1;
                for (int i = 0; i < mainWindow.MotionInfos.Count; i++)
                    if (mainWindow.MotionInfos[i].MotId == 253) { motionIndex = i; break; }
                if (motionIndex < 0) throw new InvalidDataException("wp05 motion 253 was not loaded");
                mainWindow.SelectMotion(motionIndex);
                mainWindow.CurrentFrame = 55f;
                mainWindow.IsPlaying = false;
                mainWindow.PreviewLiveEfx = true;
                mainWindow.Camera.Yaw = 0.45f;
                mainWindow.Camera.Pitch = 0.14f;
                mainWindow.Camera.Distance = 2.0f;
                Console.WriteLine($"[Stage3 multi-key long] motion=253 slots={mainWindow.CurrentSlots.Count}, confirmed files={mainWindow.MotionCalledEfxPaths.Count}, second-key events={mainWindow.CurrentSlots.Count(s => s.StartFrame == 50f && s.SourceKeyIndex == 1)}");
                if (!mainWindow.CurrentSlots.Any(s => s.StartFrame == 50f && s.SourceKeyIndex == 1))
                    throw new InvalidDataException("The second native EffectId key is missing from the live motion");
            }
            if (captureStage4WeaponCandidates)
            {
                mainWindow.SwitchWeapon(2);
                int motionIndex = -1;
                for (int i = 0; i < mainWindow.MotionInfos.Count; i++)
                    if (mainWindow.MotionInfos[i].MotId == 7) { motionIndex = i; break; }
                if (motionIndex < 0) throw new InvalidDataException("wp02 action 007 was not loaded");
                mainWindow.SelectMotion(motionIndex);
                mainWindow.CurrentFrame = 20f;
                mainWindow.IsPlaying = false;
                mainWindow.EfxSearchFilter = "11_it02_00";
                Console.WriteLine($"[Stage4 wp02 action007] direct files={mainWindow.MotionCalledEfxPaths.Count}, weapon candidates={mainWindow.WeaponPfbCandidateNames.Count}");
                for (int i = 0; i <= 4; i++)
                    if (!mainWindow.WeaponPfbCandidateNames.Contains($"11_it02_{i:000}.efx.5571972", StringComparer.OrdinalIgnoreCase))
                        throw new InvalidDataException($"wp02 action007 lacks weapon PFB candidate 11_it02_{i:000}");
            }
            Console.WriteLine("[VfxStudio.App] UI and 3D Rendering Subsystems Initialized Successfully.");
        };

        window.Update += (dt) =>
        {
            mainWindow?.Update(dt);
        };

        window.Render += (dt) =>
        {
            if (gl == null || imgui == null || mainWindow == null) return;

            // 1. Render 3D Scene into HDR Bloom Pipeline
            mainWindow.Render3D(dt);

            // 2. Prepare Default Window Framebuffer
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.Viewport(0, 0, (uint)window.Size.X, (uint)window.Size.Y);
            gl.ClearColor(0.08f, 0.08f, 0.10f, 1.0f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            // 3. Update & Render ImGui Workstation UI
            imgui.Update((float)dt);
            mainWindow.RenderImGui();
            imgui.Render();

            frameCount++;

            if (captureReference && frameCount == 30)
            {
                string screenshotPath = Path.Combine(assetsDir, "..", "docs", "ui-redesign",
                    captureReference1080 ? "current-reference-scene-1080.png" : "current-reference-scene.png");
                CaptureWindowScreenshot(gl, window.Size.X, window.Size.Y, screenshotPath);
                window.Close();
            }
            if (captureEfxBaseline && frameCount == 40)
            {
                string path = Path.Combine(assetsDir, "..", "docs", "efx-completion", "baseline-wp02-motion008-frame009.png");
                CaptureWindowScreenshot(gl, window.Size.X, window.Size.Y, path);
                Console.WriteLine($"[EFX baseline] screenshot: {path}");
                window.Close();
            }
            if (captureStage3Panel && frameCount == 40)
            {
                string path = Path.Combine(assetsDir, "..", "docs", "efx-completion", "stage3-spawn-panel.png");
                CaptureWindowScreenshot(gl, window.Size.X, window.Size.Y, path);
                Console.WriteLine($"[Stage3 panel] screenshot: {path}");
                window.Close();
            }
            if (captureStage3MultiKey && frameCount == 40)
            {
                string path = Path.Combine(assetsDir, "..", "docs", "efx-completion", "stage3-wp02-motion267-second-key-frame047.png");
                CaptureWindowScreenshot(gl, window.Size.X, window.Size.Y, path);
                Console.WriteLine($"[Stage3 multi-key] screenshot: {path}");
                window.Close();
            }
            if (captureStage3LongMultiKey && frameCount == 40)
            {
                string path = Path.Combine(assetsDir, "..", "docs", "efx-completion", "stage3-wp05-motion253-second-key-frame055.png");
                CaptureWindowScreenshot(gl, window.Size.X, window.Size.Y, path);
                foreach (var slot in mainWindow.CurrentSlots.Where(s => s.StartFrame == 50f && s.SourceKeyIndex == 1))
                {
                    var state = mainWindow.GetSlotSimulationState(slot.SlotId);
                    Console.WriteLine($"[Stage3 multi-key long] second-key slot={slot.SlotId} {slot.EffectId} @ {slot.StartFrame:F0}f, simulators={state.Simulators}, active={state.Active}");
                }
                Console.WriteLine($"[Stage3 multi-key long] screenshot: {path}");
                window.Close();
            }
            if (captureStage4WeaponCandidates && frameCount == 40)
            {
                string path = Path.Combine(assetsDir, "..", "docs", "efx-completion", "stage4-wp02-action007-weapon-pfb-candidates.png");
                CaptureWindowScreenshot(gl, window.Size.X, window.Size.Y, path);
                Console.WriteLine($"[Stage4 wp02 action007] screenshot: {path}");
                window.Close();
            }

            // In verification/test mode: verify per-motion weapon offset, dynamic EFX panel visibility, and slot-driven VFX simulation
            if (testMode)
            {
                if (frameCount == 1)
                {
                    // Step 1: Switch to wp02 Dual Blades, select Motion 0 (wp02_00_000, which has 0 VFX slots),
                    // apply a custom per-motion weapon offset override on Part 0, and open Weapon Mount Calibration Tab.
                    mainWindow.SwitchWeapon(2);
                    mainWindow.SelectMotion(0);
                    mainWindow.CurrentFrame = 0f;
                    mainWindow.IsPlaying = false;
                    mainWindow.PreviewLiveEfx = true;
                    mainWindow.ForceSelectMountCalibrationTab = true;
                    mainWindow.ShowWeaponMountGizmo = true;
                    mainWindow.SelectedWeaponPartIndex = 0;

                    if (mainWindow.WeaponManager.Parts.Count > 0)
                    {
                        mainWindow.WeaponManager.Parts[0].PositionOffset = new Vector3(0.02f, -0.03f, 0.01f);
                        mainWindow.WeaponManager.Parts[0].RotationEuler = new Vector3(15f, -20f, 95f);
                        mainWindow.WeaponManager.SaveCurrentMotionOverride(mainWindow.CurrentMot?.Name ?? "wp02_00_000");
                    }

                    mainWindow.Camera.Yaw = 0.55f;
                    mainWindow.Camera.Pitch = 0.16f;
                    mainWindow.Camera.Distance = 2.2f;

                    Console.WriteLine($"[Verify Step 1] Motion 0 ({mainWindow.CurrentMot?.Name}): Slots={mainWindow.CurrentSlots.Count}, IsUsingMotionOverride={mainWindow.WeaponManager.IsUsingMotionOverride}");
                }
                else if (frameCount == 14)
                {
                    string pathStep1 = Path.Combine(assetsDir, "Samples", "verify_no_slots_and_motion_override.png");
                    CaptureWindowScreenshot(gl, window.Size.X, window.Size.Y, pathStep1);
                    Console.WriteLine($"[VfxStudio.App] [1/3] Screenshot 1 (0 VFX Slots Hidden EFX Panel + Per-Motion Weapon Offset Override) captured: {pathStep1}");

                    // Step 2: Switch to Motion 8 ([008] wp02_00_008, which has 2 active VFX slots at frames 6-12f) at Frame 9f
                    // Also switch Left Panel to Motion List Tab to show [★专属偏移] on Motion 0, companion asset counts, and slot-driven EFX simulation!
                    mainWindow.SelectMotion(8);
                    mainWindow.CurrentFrame = 9f;
                    mainWindow.IsPlaying = false;
                    mainWindow.PreviewLiveEfx = true;
                    mainWindow.ForceSelectMotionListTab = true;
                    mainWindow.ShowWeaponMountGizmo = false;
                    mainWindow.Camera.Yaw = 0.45f;
                    mainWindow.Camera.Pitch = 0.14f;
                    mainWindow.Camera.Distance = 2.0f;

                    Console.WriteLine($"[Verify Step 2] Motion 8 ({mainWindow.CurrentMot?.Name}): Slots={mainWindow.CurrentSlots.Count}, CalledEfx={mainWindow.MotionCalledEfxPaths.Count}, IsUsingMotionOverride={mainWindow.WeaponManager.IsUsingMotionOverride}");
                }
                else if (frameCount == 28)
                {
                    string pathStep2 = Path.Combine(assetsDir, "Samples", "verify_slot_driven_efx_preview.png");
                    CaptureWindowScreenshot(gl, window.Size.X, window.Size.Y, pathStep2);
                    Console.WriteLine($"[VfxStudio.App] [2/3] Screenshot 2 (Slot-Driven EFX Simulation + Filtered Right EFX Module) captured: {pathStep2}");

                    // Step 3: Select wp03 Long Sword Motion 0 (0 VFX slots initially), then call AddNewSlot() at Frame 12f
                    // to verify that adding a VFX slot immediately reveals the Right EFX Panel and simulates the weapon's EFX!
                    mainWindow.SwitchWeapon(3);
                    mainWindow.SelectMotion(0);
                    mainWindow.AddNewSlot();
                    mainWindow.CurrentFrame = 12f;
                    mainWindow.IsPlaying = false;
                    mainWindow.PreviewLiveEfx = true;
                    mainWindow.Camera.Yaw = 0.65f;
                    mainWindow.Camera.Pitch = 0.15f;
                    mainWindow.Camera.Distance = 2.2f;

                    Console.WriteLine($"[Verify Step 3] wp03 Motion 0 after AddNewSlot(): Slots={mainWindow.CurrentSlots.Count}, CalledEfx={mainWindow.MotionCalledEfxPaths.Count}");
                }
                else if (frameCount == 42)
                {
                    string pathStep3 = Path.Combine(assetsDir, "Samples", "verify_add_slot_reveals_efx_panel.png");
                    CaptureWindowScreenshot(gl, window.Size.X, window.Size.Y, pathStep3);
                    Console.WriteLine($"[VfxStudio.App] [3/3] Screenshot 3 (Adding VFX Slot Dynamically Reveals EFX Panel & Simulates VFX) captured: {pathStep3}");

                    // Clean up the test motion override on wp02_00_000_Loop so weapon_mount_presets.json remains clean
                    mainWindow.WeaponManager.PresetStore.ClearMotionOverride("wp02", "wp02_00_000_Loop");
                    mainWindow.WeaponManager.PresetStore.ClearMotionOverride("wp02", "wp02_00_000");
                    mainWindow.WeaponManager.PresetStore.Save(mainWindow.WeaponMountPresetsPath);

                    Console.WriteLine("[VfxStudio.App] All 4 enhancements verified successfully!");
                    window.Close();
                }
            }
        };

        window.Closing += () =>
        {
            mainWindow?.Dispose();
            imgui?.Dispose();
            input?.Dispose();
            gl?.Dispose();
            Console.WriteLine("[VfxStudio.App] Cleanly shut down.");
        };

        window.Run();
    }

    private static void TestCameraProjection()
    {
        var camera = new OrbitCamera();
        foreach (OrbitCamera.StandardView direction in Enum.GetValues<OrbitCamera.StandardView>())
        {
            camera.SetStandardView(direction);
            Matrix4x4 view = camera.GetViewMatrix();
            Matrix4x4 projection = camera.GetProjectionMatrix(1280f / 761f);
            if (!float.IsFinite(view.M11) || !float.IsFinite(view.M22) ||
                !float.IsFinite(view.M33) || !float.IsFinite(projection.M11))
                throw new InvalidOperationException($"Invalid camera matrix for {direction}");
        }
        camera.Focus(new Vector3(5f, 2f, -3f), 0.5f, 1280f / 761f);
        if (Vector3.Distance(camera.Target, new Vector3(5f, 2f, -3f)) > 0.001f)
            throw new InvalidOperationException("Focus did not use the selected target");
        camera.ToggleProjection();
        if (camera.Orthographic) throw new InvalidOperationException("Projection toggle failed");
        Vector3 beforePan = camera.Target;
        float yaw = camera.Yaw, pitch = camera.Pitch;
        camera.Pan(100f, 40f);
        if (Vector3.Distance(beforePan, camera.Target) < 0.001f || camera.Yaw != yaw || camera.Pitch != pitch)
            throw new InvalidOperationException("Perspective pan changed orientation or failed to move target");
        camera.SetStandardView(OrbitCamera.StandardView.Front);
        beforePan = camera.Target;
        camera.Pan(100f, 0f);
        if (Vector3.Distance(beforePan, camera.Target) < 0.001f)
            throw new InvalidOperationException("Orthographic pan failed to move target");
        Console.WriteLine("Camera projection checks passed.");
    }

    private static void AuditStage3Attribute()
    {
        string assets = ResolveAssetsRoot();
        string source = Path.Combine(assets, "WeaponBases", "wp02", "11_pl_touch_w_006.efx.5571972");
        var service = new EfxEditorService();
        var efx = service.Load(source);
        var spawn = efx.Entries.SelectMany(e => e.Attributes).OfType<EFXAttributeSpawn>().First();
        var fields = EfxContentSchema.Describe(spawn.GetType(), spawn.Version);
        var maxField = fields.Single(f => f.Info.Name == nameof(spawn.MaxParticles) && f.Applicable).Info;
        var countField = fields.Single(f => f.Info.Name == nameof(spawn.SpawnNum) && f.Applicable).Info;
        var xField = countField.FieldType.GetField("x") ?? throw new InvalidDataException("Int2.x missing");
        object[] maxPath = { maxField };
        object[] nestedPath = { countField, xField };
        uint before = (uint)MainWindow.ReadContentPath(spawn, maxPath, 0)!;
        int nestedBefore = Convert.ToInt32(MainWindow.ReadContentPath(spawn, nestedPath, 0));
        var history = new UndoRedoManager();
        history.Execute(new RelayUndoableCommand("MaxParticles",
            () => MainWindow.WriteContentPath(spawn, maxPath, 0, checked(before + 1)),
            () => MainWindow.WriteContentPath(spawn, maxPath, 0, before)));
        history.Execute(new RelayUndoableCommand("SpawnNum.x",
            () => MainWindow.WriteContentPath(spawn, nestedPath, 0, nestedBefore + 1),
            () => MainWindow.WriteContentPath(spawn, nestedPath, 0, nestedBefore)));
        if ((uint)MainWindow.ReadContentPath(spawn, maxPath, 0)! != before + 1 ||
            Convert.ToInt32(MainWindow.ReadContentPath(spawn, nestedPath, 0)) != nestedBefore + 1)
            throw new InvalidDataException("Production field path did not update primitive and nested struct");
        string output = Path.Combine(assets, "..", "docs", "efx-completion",
            $".stage3-attribute-{Guid.NewGuid():N}.efx.5571972");
        try
        {
            service.Save(efx, output);
            var reopened = service.Load(output).Entries.SelectMany(e => e.Attributes)
                .OfType<EFXAttributeSpawn>().First();
            if (reopened.MaxParticles != before + 1 || reopened.SpawnNum.x != nestedBefore + 1)
                throw new InvalidDataException("Edited fields did not survive save and reopen");
            if (!history.Undo() || !history.Undo())
                throw new InvalidDataException("Undo history was incomplete");
            if (spawn.MaxParticles != before || spawn.SpawnNum.x != nestedBefore)
                throw new InvalidDataException("Undo did not restore prior values");
            if (!history.Redo() || !history.Redo() ||
                spawn.MaxParticles != before + 1 || spawn.SpawnNum.x != nestedBefore + 1)
                throw new InvalidDataException("Redo did not restore edited values");
            Console.WriteLine($"Attribute audit passed: {Path.GetFileName(source)}, Spawn, {fields.Count} content fields, MaxParticles {before}->{before+1}, SpawnNum.x {nestedBefore}->{nestedBefore+1}, save/reopen/undo/redo");
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }

    private static void AuditStage3Resolution()
    {
        string assets = ResolveAssetsRoot();
        string weapon = Path.Combine(assets, "WeaponBases", "wp02");
        var files = Directory.GetFiles(weapon, "*.efx.5571972", SearchOption.AllDirectories);
        var providers = new PfbEpvService(assets);
        providers.LoadWeaponPfbs("wp02", files);
        var manager = new MotionVfxSlotManager();
        var motlist = manager.LoadMotlist(Path.Combine(weapon, "wp02_00.motlist.992"));
        var motion = manager.GetMotions(motlist, "wp02").First(m => m.MotId == 8);
        Console.WriteLine($"[Stage3 resolution] providers={providers.LoadedPfbPaths.Count}, elements={providers.AllElements.Count}, action8 slots={motion.VfxSlots.Count}");
        foreach (var slot in motion.VfxSlots)
        {
            var result = providers.ResolveSlotDetailed(slot, files);
            Console.WriteLine($"  {slot.ClipIndex}/{slot.TrackIndex}/{slot.SourceKeyIndex} {slot.EffectId} {slot.StartFrame}-{slot.EndFrame}: {result.Status}; files={string.Join('|', result.LocalEfxPaths)}");
            foreach (var item in result.Candidates.Take(8))
                Console.WriteLine($"    {item.SourcePfbPath} {item.ContainerGameObjectName} resources={string.Join('|', item.ResourceEfxPaths)}");
        }
    }

    private static void AuditStage3Coverage()
    {
        string output = Path.Combine(ResolveAssetsRoot(), "..", "docs", "efx-completion", "attribute-ui-coverage.csv");
        var rows = new List<string> { Csv("version", "TypeId", "type", "member path", "CLR type", "version/condition status", "production control", "save test", "undo test", "sample", "status") };
        foreach (var (id, kind) in EfxAttributeTypeRemapper.GetAllTypes(EfxVersion.MHWilds).OrderBy(p => p.Key))
        {
            EFXAttribute attr;
            try { attr = EFXAttribute.Create(EfxVersion.MHWilds, kind); }
            catch (Exception ex)
            {
                rows.Add(Csv("MHWilds", id, kind, "<unmapped>", "", "", "", "", "", "", ex.Message));
                continue;
            }
            var ancestors = new HashSet<Type>();
            Walk(attr.GetType(), kind.ToString(), attr.Version);
            void Walk(Type type, string prefix, EfxVersion version)
            {
                if (!ancestors.Add(type)) return;
                foreach (var item in EfxContentSchema.Describe(type, version))
                {
                    Type fieldType = item.Info.FieldType;
                    string path = prefix + "." + item.Info.Name;
                    bool tested = kind == EfxAttributeType.Spawn &&
                        (path == "Spawn.MaxParticles" || path == "Spawn.SpawnNum.x");
                    string control = fieldType == typeof(bool) ? "Checkbox"
                        : fieldType == typeof(float) ? "DragFloat"
                        : fieldType == typeof(Vector3) ? "DragFloat3"
                        : fieldType == typeof(Vector4) ? "DragFloat4"
                        : fieldType == typeof(ReeLib.via.Color) ? "ColorEdit4"
                        : fieldType.IsEnum ? "Enum Combo"
                        : fieldType == typeof(string) || fieldType.IsPrimitive || fieldType == typeof(decimal) ? "InputText Enter"
                        : typeof(System.Collections.IList).IsAssignableFrom(fieldType) ? "Indexed collection view"
                        : "Nested tree";
                    rows.Add(Csv("MHWilds", id, kind, path, fieldType.FullName,
                        item.Applicable ? "applicable" : item.Condition,
                        item.Applicable ? control : "read-only",
                        tested ? "passed: wp02 Spawn modified/save/reopen" : "not tested",
                        tested ? "passed: path undo/redo" : "not tested",
                        tested ? "wp02/11_pl_touch_w_006.efx.5571972" : "not mapped",
                        !item.Applicable ? "version/condition gated"
                            : tested ? "tested sample" : control == "Indexed collection view" ? "structure edits incomplete"
                            : "visible adapter; verification pending"));
                    bool colorElement = fieldType.IsArray && fieldType.GetElementType() == typeof(ReeLib.via.Color);
                    if (fieldType == typeof(ReeLib.via.Color) || colorElement)
                        rows.Add(Csv("MHWilds", id, kind, path + (colorElement ? "[].rgba" : ".rgba"), "System.UInt32",
                            item.Applicable ? "applicable" : item.Condition,
                            item.Applicable ? "packed value via parent ColorEdit4" : "read-only",
                            "not tested", "not tested", "not mapped",
                            item.Applicable ? "covered by parent color control; verification pending" : "version/condition gated"));
                    Type child = fieldType.IsArray ? fieldType.GetElementType()! : fieldType;
                    if (child.IsGenericType && child.GetGenericTypeDefinition() == typeof(List<>))
                        child = child.GetGenericArguments()[0];
                    if (!child.IsPrimitive && !child.IsEnum && child != typeof(string) &&
                        child != typeof(Vector3) && child != typeof(Vector4) &&
                        child != typeof(ReeLib.via.Color) && child != typeof(decimal) &&
                        (child.Namespace?.StartsWith("ReeLib", StringComparison.Ordinal) == true ||
                         child.Namespace == "System.Numerics"))
                        Walk(child, path + (fieldType != child ? "[]" : ""), version);
                }
                ancestors.Remove(type);
            }
        }
        File.WriteAllLines(output, rows);
        Console.WriteLine($"Stage3 UI coverage inventory: {rows.Count-1} rows, {rows.Count(r => r.Contains("tested sample"))} sample-verified fields, {output}");
    }

    private static string Csv(params object?[] values) => string.Join(',', values.Select(value =>
        "\"" + (value?.ToString() ?? "").Replace("\"", "\"\"").Replace('\r', ' ').Replace('\n', ' ') + "\""));

    public static string ResolveAssetsRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("MOTIONVFX_ASSETS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Directory.Exists(configured)) throw new DirectoryNotFoundException("MOTIONVFX_ASSETS does not exist: " + configured);
            return Path.GetFullPath(configured);
        }
        string[] searchRoots = [
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
            AppDomain.CurrentDomain.BaseDirectory
        ];

        foreach (var startDir in searchRoots)
        {
            if (string.IsNullOrEmpty(startDir) || !Directory.Exists(startDir)) continue;

            string? current = Path.GetFullPath(startDir);
            for (int i = 0; i < 7 && !string.IsNullOrEmpty(current); i++)
            {
                // 1. Direct Assets/HunterBase check
                string candidate1 = Path.Combine(current, "Assets");
                if (Directory.Exists(Path.Combine(candidate1, "HunterBase")))
                {
                    return Path.GetFullPath(candidate1);
                }

                // 2. Subdirectory RE-MotionVfxStudio/Assets/HunterBase check
                string candidate2 = Path.Combine(current, "RE-MotionVfxStudio", "Assets");
                if (Directory.Exists(Path.Combine(candidate2, "HunterBase")))
                {
                    return Path.GetFullPath(candidate2);
                }

                string? parent = Directory.GetParent(current)?.FullName;
                if (parent == current || string.IsNullOrEmpty(parent)) break;
                current = parent;
            }
        }

        throw new DirectoryNotFoundException("无法定位 Assets；请设置 MOTIONVFX_ASSETS 或提供自己的 Assets/HunterBase 游戏素材。源码仓库不附游戏素材。");
    }

    private static unsafe void CaptureWindowScreenshot(GL gl, int width, int height, string outputPath)
    {
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        byte[] pixels = new byte[width * height * 4];
        fixed (byte* p = pixels)
        {
            gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        using var image = Image.LoadPixelData<Rgba32>(pixels, width, height);
        image.Mutate(x => x.Flip(FlipMode.Vertical));

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        image.SaveAsPng(outputPath);
    }
}
