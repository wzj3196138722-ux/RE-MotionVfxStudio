using System.Numerics;
using System.Reflection;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;
using ReeLib;
using ReeLib.Common;
using ReeLib.Efx;
using ReeLib.Efx.Enums;
using ReeLib.Efx.Structs.Basic;
using ReeLib.Efx.Structs.Transforms;
using ReeLib.Mot;
using ReeLib.Motlist;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using VfxStudio.App.Camera;
using VfxStudio.Core.Models;
using VfxStudio.Core.Services;
using VfxStudio.Renderer.Animation;
using VfxStudio.Renderer.Framebuffer;
using VfxStudio.Renderer.Meshes;
using VfxStudio.Renderer.Shaders;
using VfxStudio.Renderer.Vfx;
using VfxStudio.Renderer.Weapons;
using Shader = VfxStudio.Renderer.Shaders.Shader;

namespace VfxStudio.App.UI;

public partial class MainWindow : IDisposable
{
    private readonly GL _gl;
    private readonly IWindow _window;
    private readonly IInputContext _input;
    private readonly string _assetsDir;

    // Rendering Subsystems
    private HdrBloomPipeline _pipeline;
    private Shader _meshShader;
    private readonly ViewportGridRenderer _gridRenderer;
    private readonly UiIconTextures _uiIcons;
    private VfxProxyRenderer _vfxProxy;
    private WeaponAttachmentManager _weaponManager;
    private BoneAnimationEvaluator _animator;
    private FbxHunterModel? _hunterModel;
    public OrbitCamera Camera { get; } = new();

    // Data Services & Undo/Redo
    public UndoRedoManager UndoRedo { get; } = new();
    private MotionVfxSlotManager _slotManager;
    private EfxEditorService _efxService = new();
    private PfbEpvService _pfbService;
    private MotionDictionary _motionDict = new();

    // Weapon Selection (14 Weapons)
    public readonly string[] WeaponCodes = [
        "wp00", "wp01", "wp02", "wp03", "wp04", "wp05", "wp06",
        "wp07", "wp08", "wp09", "wp10", "wp11", "wp12", "wp13"
    ];

    public readonly string[] WeaponNames = [
        "wp00: 大剑 (Great Sword)",
        "wp01: 片手剑 (Sword & Shield)",
        "wp02: 双刀 (Dual Blades)",
        "wp03: 太刀 (Long Sword)",
        "wp04: 大锤 (Hammer)",
        "wp05: 狩猎笛 (Hunting Horn)",
        "wp06: 长枪 (Lance)",
        "wp07: 铳枪 (Gunlance)",
        "wp08: 斩斧 (Switch Axe)",
        "wp09: 盾斧 (Charge Blade)",
        "wp10: 操虫棍 (Insect Glaive)",
        "wp11: 弓 (Bow)",
        "wp12: 重弩 (Heavy Bowgun)",
        "wp13: 轻弩 (Light Bowgun)"
    ];

    public int CurrentWeaponIndex { get; private set; } = -1; // Default to -1 (No weapon selected initially)
    public bool IsModelLoaded { get; private set; } = false;
    public string CurrentWeaponCode => (CurrentWeaponIndex >= 0 && CurrentWeaponIndex < WeaponCodes.Length) ? WeaponCodes[CurrentWeaponIndex] : "";

    // Motion State
    private MotlistFile? _currentMotlist;
    private string _currentMotlistPath = string.Empty;
    private List<MotionInfo> _motionInfos = new();
    public IReadOnlyList<MotionInfo> MotionInfos => _motionInfos;
    private int _selectedMotionIndex = -1;
    private bool _scrollToSelectedMotion;
    public int SelectedMotionIndex => _selectedMotionIndex;
    private MotFile? _currentMot;
    public MotFile? CurrentMot => _currentMot;
    private string _motionSearchFilter = string.Empty;
    private int _motionCategoryFilter = 0; // 0=全部, 1=攻击, 2=移动, 3=含特效, 4=含专属偏移
    private readonly PreviewDebugStore _previewDebug;
    private readonly string _previewDebugPath;
    private readonly HashSet<int> _previewTargetMotions = new();
    private string _previewEffectPath = string.Empty;
    private string _previewMotionFilter = string.Empty;
    private string _selectedPreviewBindingId = string.Empty;
    private string _previewTimelineDragId = string.Empty;
    private int _previewTimelineDragMode; // 1 = move, 2 = start edge, 3 = end edge
    private float _previewTimelineDragMouseX;
    private float _previewTimelineDragStartFrame;
    private float _previewTimelineDragEndFrame;
    private string _motionNoteDraft = string.Empty;
    private int _motionNoteDraftIndex = -1;

    // Playback State
    private bool _isPlaying = true;
    public bool IsPlaying { get => _isPlaying; set => _isPlaying = value; }
    private bool _isLoop = true;
    private float _currentFrame = 0f;
    public float CurrentFrame { get => _currentFrame; set => _currentFrame = value; }
    private float _maxFrame = 60f;
    private float _playbackSpeed = 1.0f;
    private readonly float[] _speedOptions = [0.1f, 0.25f, 0.5f, 1.0f, 1.5f, 2.0f];

    // VFX Slots State
    private List<VfxSlot> _currentSlots = new();
    public IReadOnlyList<VfxSlot> CurrentSlots => _currentSlots;
    public (int Simulators, int Active) GetSlotSimulationState(int slotId) =>
        _vfxProxy.GetSlotSimulationState(slotId);
    private int _selectedSlotIndex = -1;
    public VfxSlot? SelectedSlot => (_selectedSlotIndex >= 0 && _selectedSlotIndex < _currentSlots.Count) ? _currentSlots[_selectedSlotIndex] : null;

    // Batch Slot Operations
    private int _batchOffsetFrames = 5;
    private float _batchScaleFactor = 1.2f;

    // Murmur3 Bone Hash Tool
    private string _boneLookupInput = "R_Weapon";
    private string _boneLookupHex = "0x51EACD38";
    private int _quickBoneIndex = 0;
    private readonly string[] _commonBones = [
        "R_Weapon", "L_Weapon", "R_Hand", "L_Hand", "Head", "Spine_2", "Waist", "COG", "root",
        "VFX_Attack", "VFX_Weapon_Tip", "Base", "Grip", "Wp_Base", "Wp_Tip", "Wp_Blade",
        "R_Shoulder", "L_Shoulder", "R_Foot", "L_Foot"
    ];

    // EFX & Companion Assets State (Slot-Driven)
    private EfxFile? _currentEfx;
    public EfxFile? CurrentEfx => _currentEfx;
    private string _currentEfxPath = string.Empty;
    public string CurrentEfxPath => _currentEfxPath;
    private EFXEntryBase? _selectedEfxEntry;
    public EFXEntryBase? SelectedEfxEntry { get => _selectedEfxEntry; set => _selectedEfxEntry = value; }
    private EFXAttribute? _selectedEfxAttribute;
    public EFXAttribute? SelectedEfxAttribute { get => _selectedEfxAttribute; set => _selectedEfxAttribute = value; }
    private bool _previewLiveEfx = true; // Slot-driven VFX preview enabled by default when motion has VFX slots
    public bool PreviewLiveEfx { get => _previewLiveEfx; set => _previewLiveEfx = value; }
    public bool ForceOpenWeaponCombo { get; set; } = false;
    public bool ForceSelectMountCalibrationTab { get; set; } = false;
    public bool ForceSelectMotionListTab { get; set; } = false;
    private string _efxSearchFilter = string.Empty;
    public string EfxSearchFilter { get => _efxSearchFilter; set => _efxSearchFilter = value; }
    private string _boneSearchFilter = string.Empty;
    private bool _showBoneHierarchy;
    private int _selectedBoneIndex = -1;
    private bool _showAdvancedInspector;

    private readonly List<string> _weaponAllEfxFiles = new();
    private readonly Dictionary<string, EfxFile> _loadedEfxByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _motionCalledEfxPaths = new();
    private readonly List<string> _motionCalledEfxLabels = new();
    private readonly Dictionary<int, string> _slotResolutionMessages = new();
    private readonly Dictionary<int, PfbSlotResolution> _slotResolutionDetails = new();
    private readonly Dictionary<string, List<(string EfxFile, int Count, string FirstSeen, string Source)>>
        _observedMotionEfx = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int ResourceRequests, int StateTriggers, int MotionCallbacks)>
        _weaponFileEvidence = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> MotionCalledEfxPaths => _motionCalledEfxPaths;
    public IReadOnlyList<string> WeaponPfbCandidateNames => _pfbService.WeaponResourceCandidates
        .Select(c => Path.GetFileName(c.LocalEfxPath ?? c.RawResourcePath))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private readonly Dictionary<int, (PfbElementEntry? Element, List<(string Path, EfxFile Efx)> EfxList)> _slotResolvedEfxMap = new();

    // Weapon Mount Calibration State
    private int _selectedWeaponPartIndex = 0;
    public int SelectedWeaponPartIndex { get => _selectedWeaponPartIndex; set => _selectedWeaponPartIndex = value; }
    private string _mountBoneSearchFilter = string.Empty;
    public string MountBoneSearchFilter { get => _mountBoneSearchFilter; set => _mountBoneSearchFilter = value; }
    private bool _showWeaponMountGizmo = true;
    public bool ShowWeaponMountGizmo { get => _showWeaponMountGizmo; set => _showWeaponMountGizmo = value; }
    private WeaponPartMountPreset? _sliderDragStartPreset;
    private WeaponPartMountPreset? _gizmoDragStartPreset;
    private int _hoveredGizmoAxis = -1;
    private int _activeGizmoAxis = -1;
    public WeaponAttachmentManager WeaponManager => _weaponManager;

    // 3D Viewport State
    private Vector2 _viewportSize = new(1280, 720);
    private Vector2 _viewportPos = Vector2.Zero;
    private bool _isViewportHovered;
    private bool _isViewportFocused;
    private ViewportNavigation _viewportNavigation;
    private enum ViewportNavigation { None, Rotate, Pan, Zoom }
    private bool _viewportMaximized;
    private char _transformKind;
    private int _transformAxis = -1;
    private bool _transformPlane;
    private string _transformNumber = string.Empty;
    private Vector2 _transformMouseStart;
    private WeaponPart? _transformPart;
    private WeaponPartMountPreset? _transformBefore;
    private ImGuizmoOperation _gizmoOperation = ImGuizmoOperation.Translate;
    private bool _showGizmo = true;
    private bool _showGrid = true;
    private float _bloomIntensity = 1.2f;
    private float _exposure = 1.0f;

    // Status & Notification
    private string _statusMessage = "就绪 (Ready)";
    private float _statusTimer = 5.0f;
    private string _gpuRenderer = "Unknown";
    private float _fps = 60.0f;
    private float _frameTimeMs = 16.6f;

    // Modal Alert
    private bool _showAlertModal = false;
    private string _alertTitle = string.Empty;
    private string _alertMessage = string.Empty;

    // Local EFX staging directory. This is not a verified game installation.
    private string _gamePath = @"natives\STM\Art\VFX\EffectEditor\CodexWeaponEfxReload";

    public string WeaponMountPresetsPath => Path.Combine(_assetsDir, "HunterBase", "weapon_mount_presets.json");

    public MainWindow(GL gl, IWindow window, IInputContext input, string assetsDir, string? previewSettingsPath = null)
    {
        _gl = gl;
        _window = window;
        _input = input;
        _assetsDir = assetsDir;
        _previewDebugPath = previewSettingsPath ?? Path.GetFullPath(Path.Combine(_assetsDir, "..", "preview_debug_settings.json"));
        _previewDebug = PreviewDebugStore.Load(_previewDebugPath);

        _pipeline = new HdrBloomPipeline(_gl, 1280, 720);
        _meshShader = new Shader(_gl, ShaderSources.SkinningVertexShader, ShaderSources.MeshFragmentShader);
        _gridRenderer = new ViewportGridRenderer(_gl);
        _uiIcons = new UiIconTextures(_gl, _assetsDir);
        _vfxProxy = new VfxProxyRenderer(_gl, _assetsDir);
        UndoRedo.HistoryChanged += (_, _) => _vfxProxy.ClearSimulators();
        _weaponManager = new WeaponAttachmentManager(_gl, WeaponMountPresetsPath);
        _animator = new BoneAnimationEvaluator(_gl);
        _pfbService = new PfbEpvService(_assetsDir);
        LoadObservedMotionEfx();

        string dictPath = Path.Combine(_assetsDir, "HunterBase", "motion_dict.json");
        _motionDict = MotionDictionary.LoadFromJson(dictPath);
        _slotManager = new MotionVfxSlotManager(_motionDict);

        _gpuRenderer = _gl.GetStringS(StringName.Renderer) ?? "NVIDIA GPU";

        InitializeDefaultData();
    }

    public void SaveScreenshot(string path)
    {
        _pipeline.SaveScreenshot(path);
    }

    public void SaveAll()
    {
        if (_currentMotlist != null && !string.IsNullOrEmpty(_currentMotlistPath))
        {
            _slotManager.SaveMotlist(_currentMotlist, _currentMotlistPath);
        }
        if (_currentEfx != null && !string.IsNullOrEmpty(_currentEfxPath))
        {
            _efxService.Save(_currentEfx, _currentEfxPath);
        }
        if (IsModelLoaded && _weaponManager.CurrentWeapon != null)
        {
            _weaponManager.SaveCurrentWeaponPresets();
        }
        SetStatus("[成功] 动作、特效与武器默认挂点配置已全部保存！");
    }

    public void SaveAs()
    {
        string initialDir = !string.IsNullOrEmpty(_currentMotlistPath)
            ? (Path.GetDirectoryName(_currentMotlistPath) ?? _assetsDir)
            : Path.Combine(_assetsDir, "Samples");
        if (!Directory.Exists(initialDir)) initialDir = _assetsDir;

        string? selected = NativeFileDialog.SaveFile(
            "另存动作文件 (Save Motlist As)",
            "MHWilds 动作列表 (*.motlist*)|*.motlist*|所有文件 (*.*)|*.*",
            initialDir,
            Path.GetFileName(_currentMotlistPath)
        );
        if (!string.IsNullOrEmpty(selected) && _currentMotlist != null)
        {
            string oldPath = _currentMotlistPath;
            _currentMotlistPath = selected;
            _slotManager.SaveMotlist(_currentMotlist, _currentMotlistPath);
            if (!string.Equals(Path.GetFullPath(oldPath), Path.GetFullPath(selected),
                    StringComparison.OrdinalIgnoreCase))
            {
                foreach (var effect in _previewDebug.Effects.Where(e =>
                    string.Equals(e.MotlistPath, oldPath, StringComparison.OrdinalIgnoreCase)).ToArray())
                    _previewDebug.Effects.Add(new PreviewEffectBinding
                    {
                        MotlistPath = Path.GetFullPath(selected), WeaponCode = effect.WeaponCode,
                        MotionIndex = effect.MotionIndex, MotionId = effect.MotionId, EfxPath = effect.EfxPath,
                        StartFrame = effect.StartFrame, EndFrame = effect.EndFrame
                    });
                foreach (var note in _previewDebug.Notes.Where(n =>
                    string.Equals(n.MotlistPath, oldPath, StringComparison.OrdinalIgnoreCase)).ToArray())
                    _previewDebug.Notes.Add(new MotionNoteOverride
                    {
                        MotlistPath = Path.GetFullPath(selected), WeaponCode = note.WeaponCode,
                        MotionIndex = note.MotionIndex, MotionId = note.MotionId, Text = note.Text
                    });
                SavePreviewDebug();
            }
            SetStatus($"[成功] 动作文件已另存为: {Path.GetFileName(selected)}");
        }
    }

    public void EnsureHunterModelLoaded()
    {
        if (_hunterModel != null) return;

        try
        {
            // 1. Load Full-Body Hunter Model (MHWilds_Female.fbx)
            string fbxPath = Path.Combine(_assetsDir, "HunterBase", "MHWilds_Female.fbx");
            if (File.Exists(fbxPath))
            {
                _hunterModel = FbxHunterModel.Load(_gl, fbxPath);
                _animator.SetHunterModel(_hunterModel);
                SetStatus($"成功加载猎人全身素体: {_hunterModel.Mesh.TotalVertices} 顶点, {_hunterModel.Bones.Count} 骨骼");
            }

            // 2. Load Weapon Grip
            string gripJson = Path.Combine(_assetsDir, "HunterBase", "weapon_grip.json");
            if (File.Exists(gripJson))
            {
                _animator.LoadWeaponGrip(gripJson);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"猎人素体加载错误: {ex.Message}");
        }
    }

    private void InitializeDefaultData()
    {
        try
        {
            // Do not preload model or switch weapon automatically on startup.
            // Keep 3D viewport clean and blank until user selects weapon or imports motion.
            CurrentWeaponIndex = -1;
            IsModelLoaded = false;
            SetStatus("就绪。请从顶部下拉框选择武器或点击 [导入外部动作] 开始。");
        }
        catch (Exception ex)
        {
            SetStatus($"初始化错误: {ex.Message}");
        }
    }

    public void SwitchWeapon(int index)
    {
        if (index < 0 || index >= WeaponCodes.Length) return;
        CancelViewportTransform();

        // Ensure full-body hunter model and grips are loaded
        EnsureHunterModelLoaded();

        CurrentWeaponIndex = index;
        IsModelLoaded = true;
        string wpCode = CurrentWeaponCode;
        string weaponBasesDir = Path.Combine(_assetsDir, "WeaponBases");
        string wpDir = Path.Combine(weaponBasesDir, wpCode);

        // 1. Switch 3D weapon meshes & apply weapon_mount_presets.json
        _weaponManager.LoadWeapon(wpCode, weaponBasesDir, WeaponMountPresetsPath);
        _selectedWeaponPartIndex = 0;

        // 2. Discover all weapon EFX files & load PFB (Effect Provider) + companion assets (.uvs, .tex, .mesh, .mdf2)
        _weaponAllEfxFiles.Clear();
        _loadedEfxByPath.Clear();
        _vfxProxy.ClearSimulators();
        if (_vfxProxy.AssetCache != null)
        {
            _vfxProxy.AssetCache.CurrentWeaponCode = wpCode;
        }

        if (Directory.Exists(wpDir))
        {
            // Collect top-level .efx files first, then any preserved subfolder .efx files without duplicating filenames
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(wpDir, "*.efx.*", SearchOption.TopDirectoryOnly).OrderBy(x => x))
            {
                if (seenNames.Add(Path.GetFileName(f)))
                    _weaponAllEfxFiles.Add(f);
            }
            foreach (var f in Directory.GetFiles(wpDir, "*.efx.*", SearchOption.AllDirectories).OrderBy(x => x))
            {
                if (seenNames.Add(Path.GetFileName(f)))
                    _weaponAllEfxFiles.Add(f);
            }
        }

        // Load .pfb.18 Effect Provider mappings (ContainerId-ElementId -> .efx, bone, RelationChildId)
        _pfbService.LoadWeaponPfbs(wpCode, _weaponAllEfxFiles);

        // Preload all weapon .efx files and warm up companion .uvs, .tex, .mesh, .mdf2 in GPU cache
        var preloadEfxList = new List<EfxFile>();
        foreach (var efxFile in _weaponAllEfxFiles)
        {
            var loaded = GetOrLoadEfx(efxFile);
            if (loaded != null) preloadEfxList.Add(loaded);
        }
        _vfxProxy.AssetCache?.WarmupCompanionAssets(preloadEfxList);

        // 3. Locate and load corresponding weapon motlist
        string? motlistPath = null;
        if (Directory.Exists(wpDir))
        {
            motlistPath = Directory.GetFiles(wpDir, "*.motlist.*", SearchOption.AllDirectories)
                .OrderBy(f => f.Length)
                .FirstOrDefault();
        }

        if (motlistPath == null || !File.Exists(motlistPath))
        {
            string sampleMot = Path.Combine(_assetsDir, "Samples", $"{wpCode}_00.motlist.992");
            if (File.Exists(sampleMot)) motlistPath = sampleMot;
        }

        if (motlistPath != null && File.Exists(motlistPath))
        {
            LoadMotlistFile(motlistPath);
        }
        else
        {
            SetStatus($"未找到武器 {wpCode} 专属动作列表");
        }

        _isPlaying = true;
        int uvsCnt = _vfxProxy.AssetCache?.LoadedUvsCount ?? 0;
        int texCnt = _vfxProxy.AssetCache?.LoadedTexCount ?? 0;
        int meshCnt = _vfxProxy.AssetCache?.LoadedMeshCount ?? 0;
        int mdfCnt = _vfxProxy.AssetCache?.LoadedMdfCount ?? 0;
        SetStatus($"已加载 {WeaponNames[index]} | 动作:{_motionInfos.Count} | PFB:{_pfbService.LoadedPfbPaths.Count} EFX:{_weaponAllEfxFiles.Count} UVS:{uvsCnt} TEX:{texCnt} MESH:{meshCnt} MDF2:{mdfCnt}");
    }

    private EfxFile? GetOrLoadEfx(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        if (_loadedEfxByPath.TryGetValue(path, out var cached)) return cached;

        try
        {
            var efx = _efxService.Load(path);
            _loadedEfxByPath[path] = efx;
            _vfxProxy.AssetCache?.WarmupCompanionAssets([efx]);
            return efx;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MainWindow] Warning loading EFX {path}: {ex.Message}");
            return null;
        }
    }

    public void RefreshCurrentMotionCalledEfx()
    {
        _motionCalledEfxPaths.Clear();
        _motionCalledEfxLabels.Clear();
        _slotResolvedEfxMap.Clear();
        _slotResolutionMessages.Clear();
        _slotResolutionDetails.Clear();

        if (_currentSlots.Count == 0)
        {
            if (!_pfbService.WeaponResourceCandidates.Any(c => string.Equals(c.LocalEfxPath,
                    _currentEfxPath, StringComparison.OrdinalIgnoreCase)))
            {
                _currentEfx = null;
                _currentEfxPath = string.Empty;
                _selectedEfxEntry = null;
                _selectedEfxAttribute = null;
            }
            return;
        }

        var pathToSlotsDesc = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < _currentSlots.Count; i++)
        {
            var slot = _currentSlots[i];
            var resolution = _pfbService.ResolveSlotDetailed(slot, _weaponAllEfxFiles);
            var pfbElem = resolution.Element;
            var localPaths = resolution.LocalEfxPaths;
            _slotResolutionMessages[slot.SlotId] = resolution.Status;
            _slotResolutionDetails[slot.SlotId] = resolution;
            var resolvedList = new List<(string Path, EfxFile Efx)>();

            foreach (var p in localPaths)
            {
                var efxObj = GetOrLoadEfx(p);
                if (efxObj != null)
                {
                    resolvedList.Add((p, efxObj));
                    if (!pathToSlotsDesc.TryGetValue(p, out var tagList))
                    {
                        pathToSlotsDesc[p] = tagList = new List<string>();
                        _motionCalledEfxPaths.Add(p);
                    }
                    string tag = $"Slot#{i}({slot.EffectId})";
                    if (!tagList.Contains(tag)) tagList.Add(tag);
                }
            }

            _slotResolvedEfxMap[slot.SlotId] = (pfbElem, resolvedList);
            if (resolvedList.Count < localPaths.Count)
                _slotResolutionMessages[slot.SlotId] = "EFX 文件读取失败；路径已保留，可重试";
        }

        foreach (var p in _motionCalledEfxPaths)
        {
            string fn = Path.GetFileName(p);
            string slotsStr = pathToSlotsDesc.TryGetValue(p, out var tags) ? string.Join(", ", tags) : "";
            _motionCalledEfxLabels.Add($"{fn}  [{slotsStr}]");
        }

        // Ensure _currentEfx is one of the current motion's called EFX files
        if (_motionCalledEfxPaths.Count > 0)
        {
            // If SelectedSlot is valid and has a resolved EFX, prefer that slot's EFX
            string? preferredPath = null;
            if (SelectedSlot != null &&
                _slotResolvedEfxMap.TryGetValue(SelectedSlot.SlotId, out var selInfo) &&
                selInfo.EfxList.Count > 0)
            {
                preferredPath = selInfo.EfxList[0].Path;
            }

            if (string.IsNullOrEmpty(_currentEfxPath) ||
                (!_motionCalledEfxPaths.Contains(_currentEfxPath, StringComparer.OrdinalIgnoreCase) &&
                 !_pfbService.WeaponResourceCandidates.Any(c => string.Equals(c.LocalEfxPath,
                     _currentEfxPath, StringComparison.OrdinalIgnoreCase))))
            {
                SelectCalledEfx(preferredPath ?? _motionCalledEfxPaths[0]);
            }
        }
        else
        {
            if (!_pfbService.WeaponResourceCandidates.Any(c => string.Equals(c.LocalEfxPath,
                    _currentEfxPath, StringComparison.OrdinalIgnoreCase)))
            {
                _currentEfx = null;
                _currentEfxPath = string.Empty;
                _selectedEfxEntry = null;
                _selectedEfxAttribute = null;
            }
        }
    }

    public void SelectCalledEfx(string path)
    {
        var efx = GetOrLoadEfx(path);
        if (efx == null) return;
        _currentEfxPath = path;
        _currentEfx = efx;
        _selectedEfxEntry = _currentEfx.Entries.FirstOrDefault();
        _selectedEfxAttribute = null;
    }

    private void SavePreviewDebug()
    {
        try { _previewDebug.Save(_previewDebugPath); }
        catch (Exception ex) { ShowAlert("调试数据保存失败", ex.Message); }
    }

    private List<PreviewEffectBinding> GetPreviewBindings(int motionListIndex)
    {
        if (motionListIndex < 0 || motionListIndex >= _motionInfos.Count ||
            string.IsNullOrWhiteSpace(_currentMotlistPath)) return new();
        return _previewDebug.GetBindings(_currentMotlistPath, CurrentWeaponCode,
            _previewMotionCatalog[motionListIndex], _previewMotionCatalog);
    }

    private void ApplyMotionNoteOverrides()
    {
        for (int i = 0; i < _motionInfos.Count; i++)
        {
            var note = _previewDebug.GetNote(_currentMotlistPath, CurrentWeaponCode,
                _previewMotionCatalog[i], _previewMotionCatalog);
            if (note != null) _motionInfos[i].Remark = note.Text;
        }
    }

    private void BindPreviewEffectToSelectedMotions()
    {
        string path = _previewEffectPath.Trim();
        if (_currentMotlist == null || string.IsNullOrWhiteSpace(_currentMotlistPath))
        {
            ShowAlert("尚未加载动作", "请先选择武器并加载动作列表。");
            return;
        }
        if (_previewTargetMotions.Count == 0)
        {
            ShowAlert("请选择动作", "请先在列表中勾选至少一个动作。");
            return;
        }
        if (!File.Exists(path))
        {
            ShowAlert("特效文件不存在", path);
            return;
        }
        path = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(path);
        if (dir != null && _vfxProxy.AssetCache != null &&
            !_vfxProxy.AssetCache.ExtraSearchRoots.Contains(dir, StringComparer.OrdinalIgnoreCase))
            _vfxProxy.AssetCache.ExtraSearchRoots.Add(dir);
        if (GetOrLoadEfx(path) == null)
        {
            ShowAlert("特效文件无法解析", path);
            return;
        }
        var added = new List<PreviewEffectBinding>();
        var targets = _previewTargetMotions.Where(i => i >= 0 && i < _motionInfos.Count).Order().ToArray();
        if (targets.Any(i => !PreviewDebugStore.IsUnambiguous(_previewMotionCatalog[i], _previewMotionCatalog)))
        {
            ShowAlert("动作身份存在歧义", "选中的动作有重复编号及名称，无法唯一确认。请先核对源动作。");
            return;
        }
        foreach (int index in targets)
        {
            added.Add(_previewDebug.CreateBinding(_currentMotlistPath, CurrentWeaponCode,
                _previewMotionCatalog[index], _previewMotionCatalog, path));
        }
        if (added.Count == 0) return;
        UndoRedo.Record(new RelayUndoableCommand("批量绑定预览特效",
            () => { _previewDebug.Effects.AddRange(added); PersistPreviewEdit(); },
            () => { foreach (var binding in added) _previewDebug.Effects.Remove(binding); PersistPreviewEdit(); }));
        _lastPreviewTargetIndices.Clear();
        _lastPreviewTargetIndices.AddRange(targets);
        bool switched = !targets.Contains(_selectedMotionIndex);
        if (switched) SelectMotion(targets[0]);
        var currentBinding = added.First(b => PreviewDebugStore.MatchesMotion(_currentMotlistPath,
            CurrentWeaponCode, _previewMotionCatalog[_selectedMotionIndex], b));
        _previewDebug.ShowPreviewEffects = true;
        RevealPreviewBinding(currentBinding);
        PersistPreviewEdit();
        SetStatus($"已为 {added.Count} 个动作绑定 {Path.GetFileName(path)}；{(switched ? "已跳转到目标动作，" : "")}新轨道已定位");
    }

    private void LoadObservedMotionEfx()
    {
        _observedMotionEfx.Clear();
        foreach (string fileName in new[] { "motion-efx-observed.tsv", "motion-state-observed.tsv" })
        {
            string path = Path.Combine(_assetsDir, "RuntimeEvidence", fileName);
            if (!File.Exists(path)) continue;
            foreach (string line in File.ReadLines(path).Skip(1))
            {
                var parts = line.Split('\t');
                if (parts.Length < 5 || !int.TryParse(parts[1], out int motion) ||
                    !int.TryParse(parts[3], out int count)) continue;
                string key = $"{parts[0]}|{motion}";
                if (!_observedMotionEfx.TryGetValue(key, out var calls))
                    _observedMotionEfx[key] = calls = new();
                string source = fileName.StartsWith("motion-state", StringComparison.Ordinal)
                    ? (parts.Length > 5 ? parts[5] : "状态") : "资源路径";
                calls.Add((parts[2], count, parts[4], source));
            }
        }
        LoadWeaponFileEvidence();
    }

    private void LoadWeaponFileEvidence()
    {
        _weaponFileEvidence.Clear();
        string path = Path.Combine(_assetsDir, "RuntimeEvidence", "weapon-file-coverage.tsv");
        if (!File.Exists(path)) return;
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            var parts = line.Split('\t');
            if (parts.Length < 5 || !int.TryParse(parts[2], out int resources) ||
                !int.TryParse(parts[3], out int states) ||
                !int.TryParse(parts[4], out int callbacks)) continue;
            _weaponFileEvidence[$"{parts[0]}|{parts[1]}"] = (resources, states, callbacks);
        }
    }

    public bool SelectStage3AttributeSample()
    {
        if (_currentEfx == null) return false;
        foreach (var entry in _currentEfx.Entries)
        {
            var spawn = entry.Attributes.OfType<EFXAttributeSpawn>().FirstOrDefault();
            if (spawn == null) continue;
            _selectedEfxEntry = entry;
            _selectedEfxAttribute = spawn;
            _efxSearchFilter = string.Empty;
            Console.WriteLine($"[Stage3 panel] {Path.GetFileName(_currentEfxPath)} Entry={entry.name} Attribute={spawn.type} ID={spawn.UniqueID} CLR={spawn.GetType().FullName}");
            return true;
        }
        return false;
    }

    private (PfbElementEntry? Element, List<(string Path, EfxFile Efx)> EfxList) ResolveSlotForSimulator(VfxSlot slot)
    {
        if (_slotResolvedEfxMap.TryGetValue(slot.SlotId, out var found))
            return found;

        var (elem, paths) = _pfbService.ResolveSlot(slot, _weaponAllEfxFiles);
        var list = new List<(string Path, EfxFile Efx)>();
        foreach (var p in paths)
        {
            var efx = GetOrLoadEfx(p);
            if (efx != null) list.Add((p, efx));
        }
        var res = (elem, list);
        _slotResolvedEfxMap[slot.SlotId] = res;
        return res;
    }

    public void LoadMotlistFile(string path)
    {
        try
        {
            _currentMotlistPath = path;
            _currentMotlist = _slotManager.LoadMotlist(path);
            _motionInfos = _slotManager.GetMotions(_currentMotlist, CurrentWeaponCode);
            _previewMotionCatalog = _motionInfos.Select(m => new PreviewMotionDescriptor(m.Index, m.MotId, m.Name, m.FrameCount)).ToArray();
            var migration = _previewDebug.ReconcileMotionCatalog(_currentMotlistPath, CurrentWeaponCode, _previewMotionCatalog);
            if (migration.Changed) SavePreviewDebug();
            _previewTargetMotions.Clear();
            _selectedPreviewBindingId = string.Empty;
            _motionNoteDraftIndex = -1;
            ApplyMotionNoteOverrides();

            if (_motionInfos.Count > 0)
            {
                SelectMotion(0);
            }
            SetStatus($"已加载动作列表: {Path.GetFileName(path)} (共 {_motionInfos.Count} 个动作)");
        }
        catch (Exception ex)
        {
            ShowAlert("动作列表加载失败", ex.Message);
        }
    }

    public void LoadEfxFile(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && _vfxProxy.AssetCache != null && !_vfxProxy.AssetCache.ExtraSearchRoots.Contains(dir, StringComparer.OrdinalIgnoreCase))
            {
                _vfxProxy.AssetCache.ExtraSearchRoots.Add(dir);
            }

            if (!_weaponAllEfxFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _weaponAllEfxFiles.Add(path);
            }

            var efx = GetOrLoadEfx(path);
            if (efx != null)
            {
                _currentEfxPath = path;
                _currentEfx = efx;
                _selectedEfxEntry = _currentEfx.Entries.FirstOrDefault();
                _selectedEfxAttribute = null;
                if (!_motionCalledEfxPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    _motionCalledEfxPaths.Add(path);
                    _motionCalledEfxLabels.Add($"{Path.GetFileName(path)}  [外部导入]");
                }
                SetStatus($"已加载特效文件及伴随资源: {Path.GetFileName(path)} (包含 {_currentEfx.Entries.Count} 个Entry, {_currentEfx.Actions.Count} 个Action)");
            }
        }
        catch (Exception ex)
        {
            ShowAlert("EFX 加载失败", ex.Message);
        }
    }

    public IReadOnlyList<EfxEntryDiagnostics> CaptureEfxDiagnostics(int localFrame, ulong seed)
    {
        if (_currentEfx == null || _vfxProxy.AssetCache == null)
            return Array.Empty<EfxEntryDiagnostics>();
        var simulator = new EfxSimulator(_currentEfxPath, _currentEfx, _vfxProxy.AssetCache, seed);
        simulator.StepToFrame(localFrame, Matrix4x4.Identity);
        return simulator.GetDiagnostics();
    }

    public void SelectMotion(int index)
    {
        if (index < 0 || index >= _motionInfos.Count || _currentMotlist == null) return;
        CancelViewportTransform();
        _selectedMotionIndex = index;
        _previewTimelineDragId = string.Empty;
        if (_previewTargetMotions.Count == 0) _previewTargetMotions.Add(index);
        _scrollToSelectedMotion = true;
        var info = _motionInfos[index];
        _motionNoteDraftIndex = index;
        _motionNoteDraft = info.Remark;
        var previewBindings = GetPreviewBindings(index);
        if (!previewBindings.Any(b => b.Id == _selectedPreviewBindingId))
            _selectedPreviewBindingId = previewBindings.FirstOrDefault()?.Id ?? string.Empty;
        _pendingPreviewScrollId = _selectedPreviewBindingId;

        if (info.Index >= 0 && info.Index < _currentMotlist.Motions.Count)
        {
            _currentMot = _currentMotlist.Motions[info.Index].MotFile as MotFile;
            if (_currentMot != null)
            {
                _maxFrame = Math.Max(1f, _currentMot.Header?.frameCount ?? 60f);
                _currentFrame = 0f;

                _currentSlots = info.VfxSlots;
                _selectedSlotIndex = _currentSlots.Count > 0 ? 0 : -1;

                // 1. Apply per-motion weapon mount offset override if configured, else revert to weapon default
                _weaponManager.ApplyMotionPreset(info.Name);

                // 2. Clear previous simulators and resolve only the EFX files called by this motion's VFX slots
                _vfxProxy.ClearSimulators();
                RefreshCurrentMotionCalledEfx();
            }
        }
    }

    public void ImportExternalMotion(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        string fileName = Path.GetFileName(path).ToLowerInvariant();

        // 1. Detect weapon code in motion filename
        string? matchedWp = null;
        int matchedIdx = -1;
        for (int i = 0; i < WeaponCodes.Length; i++)
        {
            if (fileName.Contains(WeaponCodes[i]))
            {
                matchedWp = WeaponCodes[i];
                matchedIdx = i;
                break;
            }
        }

        // 2. Rule: If not belonging to any weapon -> reject
        if (matchedWp == null || matchedIdx < 0)
        {
            ShowAlert("动作导入拦截", $"文件 '{Path.GetFileName(path)}' 文件名未包含任何已知猎人武器代码 (wp00~wp13)！\n\n根据系统规范，非武器动作已被拦截，不予导入。");
            return;
        }

        // 3. Auto-switch to matching weapon base and reload presets from weapon_mount_presets.json
        SwitchWeapon(matchedIdx);

        // 4. Load the user's specific imported motion file (overriding the default motlist from SwitchWeapon)
        LoadMotlistFile(path);
        _isPlaying = true;
        SetStatus($"成功导入并匹配武器动作: {Path.GetFileName(path)} ({WeaponNames[matchedIdx]})");
    }

    public void SetStatus(string message)
    {
        _statusMessage = message;
        _statusTimer = 5.0f;
    }

    public void ShowAlert(string title, string message)
    {
        _alertTitle = title;
        _alertMessage = message;
        _showAlertModal = true;
    }

    public void Update(double dt)
    {
        float fdt = (float)dt;

        // FPS and Frametime calculation
        if (fdt > 0f)
        {
            _fps = _fps * 0.9f + (1.0f / fdt) * 0.1f;
            _frameTimeMs = fdt * 1000f;
        }

        if (_statusTimer > 0f)
        {
            _statusTimer -= fdt;
        }

        if (IsModelLoaded)
        {
            // Animation playback progression
            if (_isPlaying && _maxFrame > 0f)
            {
                _currentFrame += fdt * 60f * _playbackSpeed;
                if (_currentFrame > _maxFrame)
                {
                    if (_isLoop)
                    {
                        _currentFrame %= _maxFrame;
                    }
                    else
                    {
                        _currentFrame = _maxFrame;
                        _isPlaying = false;
                    }
                }
            }

            // Evaluate animation bones
            _animator.EvaluatePose(_currentMot, _currentFrame);
        }
    }

    public void Render3D(double dt)
    {
        // 1. Begin Scene in HDR Bloom Pipeline
        _pipeline.BeginScene(0.015f, 0.025f, 0.05f, 1.0f);

        // 2. Bind GPU Bone SSBO for skinning
        _animator.BindBoneBuffer(0);

        // 3. Render 3D Floor Grid if enabled
        if (_showGrid)
        {
            RenderGrid();
        }

        // 4. Render 3D Models only when a weapon素体 has been loaded
        if (IsModelLoaded)
        {
            // Full-body Hunter FBX with GPU Skinning
            if (_hunterModel != null)
            {
                _meshShader.Use();
                _meshShader.SetUniform("u_View", Camera.GetViewMatrix());
                _meshShader.SetUniform("u_Projection", Camera.GetProjectionMatrix(_viewportSize.X / Math.Max(1f, _viewportSize.Y)));
                _meshShader.SetUniform("u_Model", Matrix4x4.Identity);
                _meshShader.SetUniform("u_ViewPos", Camera.Position);
                _meshShader.SetUniform("u_HasSkinning", 1);
                _meshShader.SetUniform("u_HasTexture", 0);
                _meshShader.SetUniform("u_BaseColor", new Vector4(0.85f, 0.85f, 0.9f, 1.0f));
                _meshShader.SetUniform("u_EmissionColor", Vector3.Zero);
                _meshShader.SetUniform("u_EmissionIntensity", 0.0f);

                _hunterModel.Mesh.Render();
            }

            // Attached Weapons
            _meshShader.Use();
            _meshShader.SetUniform("u_View", Camera.GetViewMatrix());
            _meshShader.SetUniform("u_Projection", Camera.GetProjectionMatrix(_viewportSize.X / Math.Max(1f, _viewportSize.Y)));
            _meshShader.SetUniform("u_ViewPos", Camera.Position);
            _weaponManager.Render(_meshShader, _animator);

            // 5. Slot-Driven Deterministic EFX Simulation & Companion Asset Rendering (.efx + .pfb + .uvs + .tex + .mesh + .mdf2)
            // Strictly rendered only when the current motion has VFX slots (_currentSlots.Count > 0)
            if (_previewLiveEfx && _currentSlots.Count > 0)
            {
                Matrix4x4 view = Camera.GetViewMatrix();
                Matrix4x4 proj = Camera.GetProjectionMatrix(_viewportSize.X / Math.Max(1f, _viewportSize.Y));
                int selSlotId = SelectedSlot?.SlotId ?? -1;
                _vfxProxy.RenderSlotDrivenSimulators(
                    _currentSlots,
                    _currentFrame,
                    _animator,
                    _weaponManager,
                    _meshShader,
                    view,
                    proj,
                    Camera.Position,
                    selSlotId,
                    ResolveSlotForSimulator);
            }

            if (!_previewDebug.PreviewEffectsMuted)
            {
                var previewItems = new List<PreviewEffectRenderItem>();
                foreach (var binding in GetPreviewBindings(_selectedMotionIndex))
                {
                    if (!binding.Enabled) continue;
                    if (!string.IsNullOrWhiteSpace(binding.MountBoneName) && _hunterModel != null &&
                        !_hunterModel.Bones.Any(b => b.Name.Equals(binding.MountBoneName, StringComparison.OrdinalIgnoreCase))) continue;
                    string? dir = Path.GetDirectoryName(binding.EfxPath);
                    if (dir != null && _vfxProxy.AssetCache != null &&
                        !_vfxProxy.AssetCache.ExtraSearchRoots.Contains(dir, StringComparer.OrdinalIgnoreCase))
                        _vfxProxy.AssetCache.ExtraSearchRoots.Add(dir);
                    var efx = GetOrLoadEfx(binding.EfxPath);
                    if (efx != null)
                        previewItems.Add(new PreviewEffectRenderItem(binding.Id, binding.EfxPath,
                            efx, binding.StartFrame, binding.EndFrame, binding.MountBoneName));
                }
                _vfxProxy.RenderPreviewEffects(previewItems, _currentFrame, _animator,
                    _weaponManager, _meshShader, Camera.GetViewMatrix(),
                    Camera.GetProjectionMatrix(_viewportSize.X / Math.Max(1f, _viewportSize.Y)),
                    Camera.Position);
            }
        }

        // 7. Post-Processing: Gaussian Bloom & HDR Tonemap
        _pipeline.EndSceneAndProcessBloom(6, _bloomIntensity, _exposure);
    }

    private void RenderGrid()
    {
        _gridRenderer.Render(Camera.GetViewMatrix(),
            Camera.GetProjectionMatrix(_viewportSize.X / Math.Max(1f, _viewportSize.Y)));
    }

    private void HandleGlobalShortcuts()
    {
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        if (io.WantTextInput) return;

        bool ctrl = io.KeyCtrl;
        bool shift = io.KeyShift;

        if (ctrl && !shift && Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.Z))
        {
            if (UndoRedo.CanUndo)
            {
                UndoRedo.Undo();
                SetStatus($"已撤销: {UndoRedo.NextRedoName}");
            }
        }
        else if (ctrl && ((!shift && Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.Y)) || (shift && Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.Z))))
        {
            if (UndoRedo.CanRedo)
            {
                UndoRedo.Redo();
                SetStatus($"已重做: {UndoRedo.NextUndoName}");
            }
        }
        else if (ctrl && shift && Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.S))
        {
            SaveAs();
        }
        else if (ctrl && !shift && Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.S))
        {
            SaveAll();
        }
        else if (ctrl && Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.Space))
        {
            _viewportMaximized = !_viewportMaximized;
        }
        else if (!ctrl && Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.Space))
        {
            _isPlaying = !_isPlaying;
        }
        else if (!ctrl && Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.LeftArrow))
        {
            _isPlaying = false;
            _currentFrame = shift ? 0f : Math.Max(0f, _currentFrame - 1f);
        }
        else if (!ctrl && Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.RightArrow))
        {
            _isPlaying = false;
            _currentFrame = shift ? _maxFrame : Math.Min(_maxFrame, _currentFrame + 1f);
        }
    }

    public void RenderImGui()
    {
        HandleGlobalShortcuts();

        var io = Hexa.NET.ImGui.ImGui.GetIO();
        Vector2 screen = io.DisplaySize;
        float gap = StudioTheme.CardGap;

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        //  Root fullscreen window — no borders, no title, #090C15 bg
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Hexa.NET.ImGui.ImGui.SetNextWindowPos(Vector2.Zero);
        Hexa.NET.ImGui.ImGui.SetNextWindowSize(screen);
        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.WindowBg, StudioTheme.MainBg);
        Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

        ImGuiWindowFlags rootFlags = ImGuiWindowFlags.NoTitleBar |
                                    ImGuiWindowFlags.NoResize |
                                    ImGuiWindowFlags.NoMove |
                                    ImGuiWindowFlags.NoCollapse |
                                    ImGuiWindowFlags.NoBringToFrontOnFocus |
                                    ImGuiWindowFlags.NoScrollbar |
                                    ImGuiWindowFlags.NoScrollWithMouse;

        Hexa.NET.ImGui.ImGui.Begin("MainWorkstation", rootFlags);

        // ── Layout dimensions ───────────────────────────────────────────
        float headerH  = StudioTheme.HeaderHeight;
        float leftW    = StudioTheme.LeftPanelWidth;
        float rightW   = StudioTheme.RightPanelWidth;
        float bodyTop  = headerH + gap;
        float bodyH    = screen.Y - bodyTop - gap;
        int previewTrackCount = GetPreviewBindings(_selectedMotionIndex).Count;
        float preferredTimeH = StudioTheme.TimelineHeight + Math.Min(3, previewTrackCount) * 30f
            + (previewTrackCount > 0 ? 24f : 0f);
        float timeH = Math.Clamp(_previewDebug.TimelineHeight > 0f ? _previewDebug.TimelineHeight : preferredTimeH,
            180f, Math.Max(180f, bodyH - 210f));
        float centerW  = Math.Max(200f, screen.X - leftW - rightW - gap * 4f);
        float viewportH = Math.Max(200f, bodyH - timeH - gap);

        // Zone 1: Top Navigation Bar (48px)
        Hexa.NET.ImGui.ImGui.SetCursorPos(Vector2.Zero);
        RenderTopNavBar(screen.X, headerH);

        if (!_viewportMaximized)
        {
            Hexa.NET.ImGui.ImGui.SetCursorPos(new Vector2(gap, bodyTop));
            if (_activeNavTab == 4) RenderDebugSettings_Card(leftW - gap, bodyH);
            else RenderLeftPanel_Card(leftW - gap, bodyH);
        }

        // Zone 3: Center-Top Card — 3D Viewport
        float centerX = _viewportMaximized ? gap : leftW + gap;
        Hexa.NET.ImGui.ImGui.SetCursorPos(new Vector2(centerX, bodyTop));
        RenderCenterViewport_Card(_viewportMaximized ? screen.X - gap * 2f : centerW,
            _viewportMaximized ? bodyH : viewportH);

        // Zone 4: Center-Bottom Card — Timeline
        if (!_viewportMaximized)
        {
            Hexa.NET.ImGui.ImGui.SetCursorPos(new Vector2(centerX, bodyTop + viewportH + gap));
            RenderBottomTimeline_Card(centerW, timeH);
        }

        // Zone 5: Right Column — 3 stacked cards
        if (!_viewportMaximized)
        {
            float rightX = centerX + centerW + gap;
            Hexa.NET.ImGui.ImGui.SetCursorPos(new Vector2(rightX, bodyTop));
            float rightPanelWidth = screen.X - rightX - gap;
            const float hierarchyHeight = 280f;
            RenderMotionInfo_Card(rightPanelWidth, 208f);
            Hexa.NET.ImGui.ImGui.SetCursorPos(new Vector2(rightX, bodyTop + 208f + gap));
            RenderHierarchy_Card(rightPanelWidth, hierarchyHeight);
            Hexa.NET.ImGui.ImGui.SetCursorPos(new Vector2(rightX, bodyTop + 208f + hierarchyHeight + gap * 2f));
            RenderNodeInspector_Card(rightPanelWidth, Math.Max(180f, bodyH - 208f - hierarchyHeight - gap * 2f));
        }

        Hexa.NET.ImGui.ImGui.End();
        Hexa.NET.ImGui.ImGui.PopStyleVar(2);
        Hexa.NET.ImGui.ImGui.PopStyleColor();

        if (_showAdvancedInspector)
        {
            Hexa.NET.ImGui.ImGui.SetNextWindowSize(new Vector2(600f, 700f), ImGuiCond.FirstUseEver);
            if (Hexa.NET.ImGui.ImGui.Begin("高级槽位与 EFX 编辑", ref _showAdvancedInspector))
                RenderRightInspector_Card(570f, 640f);
            Hexa.NET.ImGui.ImGui.End();
        }

        // Render Popups / Modals
        RenderModals();
    }

    private int _activeNavTab = 1; // Default: 动作编辑

    private void RenderTopNavBar(float width, float height)
    {
        // Header card background
        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.ChildBg, StudioTheme.HexToV4(0x0E1220));
        Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 0f);
        Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 0f));
        Hexa.NET.ImGui.ImGui.BeginChild("TopNavBar", new Vector2(width, height), ImGuiChildFlags.AlwaysUseWindowPadding);

        var drawList = Hexa.NET.ImGui.ImGui.GetWindowDrawList();
        Vector2 origin = Hexa.NET.ImGui.ImGui.GetCursorScreenPos();
        float cx = 14f;
        float cy = (height - 32f) * 0.5f;

        // ── Brand Icon ("M" gradient) ───────────────────────────────────
        Vector2 iconPos = new(origin.X + cx, origin.Y + cy);
        float iconSize = 32f;
        uint colPurple = StudioTheme.V4ToAbgr(StudioTheme.HexToV4(0x6C5CE7));
        uint colBlue   = StudioTheme.V4ToAbgr(StudioTheme.PrimaryBlueBright);
        Vector2[] brand = [new(2f, 25f), new(8f, 9f), new(14f, 23f),
            new(20f, 11f), new(25f, 23f), new(30f, 14f)];
        for (int segment = 0; segment < brand.Length - 1; segment++)
            drawList.AddLine(iconPos + brand[segment], iconPos + brand[segment + 1],
                segment < 2 ? colPurple : colBlue, 4.5f);
        cx += iconSize + 10f;

        // ── Brand Title ─────────────────────────────────────────────────
        float titleY = origin.Y + 8f;
        drawList.AddText(new Vector2(origin.X + cx, titleY), 0xFFFFFFFFu, "Motion");
        var motionSize = Hexa.NET.ImGui.ImGui.CalcTextSize("Motion");
        drawList.AddText(new Vector2(origin.X + cx + motionSize.X + 2f, titleY), StudioTheme.V4ToAbgr(StudioTheme.PrimaryBlueBright), "Editor");
        float subY = titleY + motionSize.Y + 1f;
        drawList.AddText(new Vector2(origin.X + cx, subY), StudioTheme.V4ToAbgr(StudioTheme.TextSecondary), "ReFramework Animation Tool");
        cx += 175f;

        // ── Navigation Tabs ─────────────────────────────────────────────
        string[] navTabs = ["主页", "动作编辑", "轨道编辑", "资源管理", "设置"];
        string[] navIcons = ["house", "film", "layers", "package", "settings"];
        float tabStartX = cx;
        float tabH = 30f;
        float tabY = (height - tabH) * 0.5f;
        for (int i = 0; i < navTabs.Length; i++)
        {
            var tabText = navTabs[i];
            var tabSize = Hexa.NET.ImGui.ImGui.CalcTextSize(tabText);
            float tabW = tabSize.X + 42f;
            bool available = i == 1 || i == 4 || (i == 3 && _previewDebug.ShowBoneMounts);
            Vector2 tabPos = new(origin.X + cx, origin.Y + tabY);
            Vector2 tabEnd = new(tabPos.X + tabW, tabPos.Y + tabH);

            bool isActive = (i == _activeNavTab);
            if (isActive)
            {
                drawList.AddRectFilled(tabPos, tabEnd, StudioTheme.V4ToAbgr(StudioTheme.ActiveNavBg), 6f);
                // Bottom glow bar
                drawList.AddRectFilled(
                    new Vector2(tabPos.X + 4f, tabEnd.Y - 3f),
                    new Vector2(tabEnd.X - 4f, tabEnd.Y),
                    StudioTheme.V4ToAbgr(StudioTheme.PrimaryBlueBright), 1.5f);
            }

            // Invisible button for click
            Hexa.NET.ImGui.ImGui.SetCursorPos(new Vector2(cx, tabY));
            if (Hexa.NET.ImGui.ImGui.InvisibleButton($"##navTab_{i}", new Vector2(tabW, tabH)))
            {
                if (available) _activeNavTab = i;
                if (i == 3 && available)
                {
                    ForceSelectMountCalibrationTab = true;
                }
            }
            if (!available && Hexa.NET.ImGui.ImGui.IsItemHovered())
                Hexa.NET.ImGui.ImGui.SetTooltip(i == 3 ? "请先在设置的调试模式中启用骨骼挂点调试" : "此页面尚未实现");

            uint textCol = isActive ? 0xFFFFFFFFu : StudioTheme.V4ToAbgr(
                available ? StudioTheme.TextSecondary : StudioTheme.HexToV4(0x53617A));
            _uiIcons.Draw(drawList, navIcons[i], tabPos + new Vector2(8f, 8f), 14f);
            drawList.AddText(new Vector2(tabPos.X + 27f, tabPos.Y + (tabH - tabSize.Y) * 0.5f), textCol, tabText);
            cx += tabW + 6f;
        }

        // ── Spacer → right-aligned elements ─────────────────────────────
        float rightSection = width - 16f;

        // ── Hardware Telemetry ───────────────────────────────────────────
        string fpsText = $"FPS: {_fps:F0} ({_frameTimeMs:F1}ms)";
        string gpuText = $"GPU: {_gpuRenderer.Split('/')[0]}";
        var gpuTextSize = Hexa.NET.ImGui.ImGui.CalcTextSize(gpuText);
        var fpsTextSize = Hexa.NET.ImGui.ImGui.CalcTextSize(fpsText);

        float hwX = rightSection - gpuTextSize.X - fpsTextSize.X - 130f;
        drawList.AddText(new Vector2(origin.X + hwX, origin.Y + 15f), StudioTheme.V4ToAbgr(StudioTheme.TextSecondary), gpuText);
        drawList.AddText(new Vector2(origin.X + hwX + gpuTextSize.X + 10f, origin.Y + 15f), StudioTheme.V4ToAbgr(StudioTheme.PrimaryBlueBright), fpsText);

        // ── Game Connection Badge ───────────────────────────────────────
        float badgeX = rightSection - 110f;
        float badgeY = origin.Y + (height - 24f) * 0.5f;
        drawList.AddRectFilled(
            new Vector2(origin.X + badgeX, badgeY),
            new Vector2(origin.X + badgeX + 100f, badgeY + 24f),
            StudioTheme.V4ToAbgr(StudioTheme.HexToV4(0x1E273D)), 12f);
        drawList.AddCircleFilled(new Vector2(origin.X + badgeX + 14f, badgeY + 12f), 4f, StudioTheme.V4ToAbgr(StudioTheme.TextSecondary));
        drawList.AddText(new Vector2(origin.X + badgeX + 24f, badgeY + 4f), StudioTheme.V4ToAbgr(StudioTheme.TextSecondary), "游戏未连接");

        // ── Bottom border line ──────────────────────────────────────────
        drawList.AddLine(
            new Vector2(origin.X, origin.Y + height - 1f),
            new Vector2(origin.X + width, origin.Y + height - 1f),
            StudioTheme.V4ToAbgr(StudioTheme.CardBorder));

        // ── Invisible toolbar action buttons (behind the scenes) ────────
        // Place functional buttons in a thin row for file ops, using styled approach
        float actionY = (height - 28f) * 0.5f;
        Hexa.NET.ImGui.ImGui.SetCursorPos(new Vector2(cx + 12f, actionY));
        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.Button, StudioTheme.HexToV4(0x1A2240));
        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.ButtonHovered, StudioTheme.PrimaryBlue);
        Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        if (Hexa.NET.ImGui.ImGui.Button("操作", new Vector2(50f, 28f)))
            Hexa.NET.ImGui.ImGui.OpenPopup("WorkstationActions");
        if (Hexa.NET.ImGui.ImGui.BeginPopup("WorkstationActions"))
        {

        if (Hexa.NET.ImGui.ImGui.Button("[+] 打开动作", new Vector2(0, 28)))
        {
            string initialDir = (IsModelLoaded && CurrentWeaponIndex >= 0)
                ? Path.Combine(_assetsDir, "WeaponBases", CurrentWeaponCode)
                : Path.Combine(_assetsDir, "Samples");
            if (!Directory.Exists(initialDir)) initialDir = _assetsDir;

            string? selected = NativeFileDialog.OpenFile(
                "打开动作列表文件 (Open Motlist)",
                "MHWilds 动作文件 (*.motlist*;*.mot)|*.motlist*;*.mot|所有文件 (*.*)|*.*",
                initialDir
            );
            if (!string.IsNullOrEmpty(selected))
            {
                ImportExternalMotion(selected);
            }
        }
        if (Hexa.NET.ImGui.ImGui.IsItemHovered())
            Hexa.NET.ImGui.ImGui.SetTooltip("加载动作列表文件 (*.motlist / *.mot)");

        if (Hexa.NET.ImGui.ImGui.Button("[+] 打开特效", new Vector2(0, 28)))
        {
            string initialDir = (IsModelLoaded && CurrentWeaponIndex >= 0)
                ? Path.Combine(_assetsDir, "WeaponBases", CurrentWeaponCode)
                : Path.Combine(_assetsDir, "Samples");
            if (!Directory.Exists(initialDir)) initialDir = _assetsDir;

            string? selected = NativeFileDialog.OpenFile(
                "打开特效文件 (Open EFX)",
                "MHWilds 特效文件 (*.efx*)|*.efx*|所有文件 (*.*)|*.*",
                initialDir
            );
            if (!string.IsNullOrEmpty(selected))
            {
                LoadEfxFile(selected);
            }
        }
        if (Hexa.NET.ImGui.ImGui.IsItemHovered())
            Hexa.NET.ImGui.ImGui.SetTooltip("加载特效文件 (*.efx)");

        if (Hexa.NET.ImGui.ImGui.Button("[S] 保存", new Vector2(0, 28)))
        {
            SaveAll();
        }
        if (Hexa.NET.ImGui.ImGui.IsItemHovered())
            Hexa.NET.ImGui.ImGui.SetTooltip("保存动作与特效文件 (Ctrl+S / Ctrl+Shift+S 另存为)");

        bool canUndo = UndoRedo.CanUndo;
        if (!canUndo) Hexa.NET.ImGui.ImGui.BeginDisabled();
        if (Hexa.NET.ImGui.ImGui.Button("<- 撤销", new Vector2(0, 28)))
        {
            UndoRedo.Undo();
            SetStatus($"已撤销: {UndoRedo.NextRedoName}");
        }
        if (!canUndo) Hexa.NET.ImGui.ImGui.EndDisabled();

        bool canRedo = UndoRedo.CanRedo;
        if (!canRedo) Hexa.NET.ImGui.ImGui.BeginDisabled();
        if (Hexa.NET.ImGui.ImGui.Button("-> 重做", new Vector2(0, 28)))
        {
            UndoRedo.Redo();
            SetStatus($"已重做: {UndoRedo.NextUndoName}");
        }
        if (!canRedo) Hexa.NET.ImGui.ImGui.EndDisabled();

        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.Button, StudioTheme.HexToV4(0x7C3A12));
        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.ButtonHovered, StudioTheme.HexToV4(0xB45309));
        if (Hexa.NET.ImGui.ImGui.Button("写入本地 EFX", new Vector2(0, 28)))
        {
            TriggerGameHotReload();
        }
        Hexa.NET.ImGui.ImGui.PopStyleColor(2);
        if (Hexa.NET.ImGui.ImGui.IsItemHovered())
        {
            Hexa.NET.ImGui.ImGui.SetTooltip("写入程序旁的 EFX 暂存目录；不会宣称游戏已重载");
        }
        Hexa.NET.ImGui.ImGui.EndPopup();
        }

        Hexa.NET.ImGui.ImGui.PopStyleVar();
        Hexa.NET.ImGui.ImGui.PopStyleColor(2);

        Hexa.NET.ImGui.ImGui.EndChild();
        Hexa.NET.ImGui.ImGui.PopStyleVar(2);
        Hexa.NET.ImGui.ImGui.PopStyleColor();
    }

    private void TriggerGameHotReload()
    {
        try
        {
            if (_currentEfx == null)
            {
                SetStatus("未加载 EFX 文件，无法进行热重载");
                return;
            }

            byte[] bytes = _efxService.SaveToBytes(_currentEfx);

            // Stage beside the application until a configured game path and
            // valid plugin request/receipt protocol are available.
            string targetDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _gamePath);
            Directory.CreateDirectory(targetDir);
            string targetFile = Path.Combine(targetDir, Path.GetFileName(_currentEfxPath.Length > 0 ? _currentEfxPath : "11_it02_000.efx.5571972"));
            File.WriteAllBytes(targetFile, bytes);

            SetStatus($"已写入本地暂存目录：{bytes.Length} 字节 EFX；游戏未重载");
        }
        catch (Exception ex)
        {
            ShowAlert("热加载失败", ex.Message);
        }
    }

    private void RenderDebugSettings_Card(float width, float height)
    {
        StudioTheme.BeginCard("DebugSettingsCard", new Vector2(width, height));
        StudioTheme.SectionHeader("", "设置");
        Hexa.NET.ImGui.ImGui.SeparatorText("调试模式");
        bool mounts = _previewDebug.ShowBoneMounts;
        if (Hexa.NET.ImGui.ImGui.Checkbox("骨骼挂点调试", ref mounts))
        {
            _previewDebug.ShowBoneMounts = mounts;
            if (!mounts)
            {
                CancelViewportTransform();
                _activeGizmoAxis = -1;
                ForceSelectMountCalibrationTab = false;
                if (_activeNavTab == 3) _activeNavTab = 1;
            }
            SavePreviewDebug();
        }
        Hexa.NET.ImGui.ImGui.TextWrapped("显示武器挂点校准栏和视窗中的挂点调试坐标轴。");
        Hexa.NET.ImGui.ImGui.Spacing();
        bool effects = _previewDebug.ShowPreviewEffects;
        if (Hexa.NET.ImGui.ImGui.Checkbox("显示预览特效绑定工具", ref effects))
        {
            _previewDebug.ShowPreviewEffects = effects;
            SavePreviewDebug();
        }
        Hexa.NET.ImGui.ImGui.TextWrapped("显示左侧绑定工具。已绑定轨道始终保留，可在时间轴单独静音，不会因此隐藏。");
        Hexa.NET.ImGui.ImGui.Spacing();
        bool notes = _previewDebug.ShowMotionNotes;
        if (Hexa.NET.ImGui.ImGui.Checkbox("手动修改动作备注名", ref notes))
        {
            _previewDebug.ShowMotionNotes = notes;
            SavePreviewDebug();
        }
        Hexa.NET.ImGui.ImGui.TextWrapped("显示动作备注栏；备注只用于编辑器显示和搜索。");
        Hexa.NET.ImGui.ImGui.Separator();
        Hexa.NET.ImGui.ImGui.TextDisabled("以上三个选项可以分别开启；预览绑定不会写入动作槽位或导出的动作文件。");
        StudioTheme.EndCard();
    }

    private void RenderLeftPanel_Card(float width, float height)
    {
        StudioTheme.BeginCard("LeftMotionCard", new Vector2(width, height));
            // ── Section Header ──────────────────────────────────────────
            StudioTheme.SectionHeader("|", "动作资源库");

            // ── Weapon Selector Combo ───────────────────────────────────
            Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.FrameBg, StudioTheme.InputBg);
            Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, StudioTheme.InputRounding);
            Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
            Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.Border, StudioTheme.InputBorder);
            Hexa.NET.ImGui.ImGui.SetNextItemWidth(-1);
            if (ForceOpenWeaponCombo)
            {
                Hexa.NET.ImGui.ImGui.OpenPopup("##WeaponSelect");
            }
            string comboPreview = (IsModelLoaded && CurrentWeaponIndex >= 0 && CurrentWeaponIndex < WeaponNames.Length)
                ? WeaponNames[CurrentWeaponIndex]
                : "请选择武器 (Select Weapon)...";

            if (Hexa.NET.ImGui.ImGui.BeginCombo("##WeaponSelect", comboPreview))
            {
                for (int w = 0; w < WeaponNames.Length; w++)
                {
                    bool isSelected = (IsModelLoaded && w == CurrentWeaponIndex);
                    if (Hexa.NET.ImGui.ImGui.Selectable(WeaponNames[w], isSelected))
                    {
                        SwitchWeapon(w);
                    }
                }
                Hexa.NET.ImGui.ImGui.EndCombo();
            }
            Hexa.NET.ImGui.ImGui.PopStyleColor(2);
            Hexa.NET.ImGui.ImGui.PopStyleVar(2);
            Hexa.NET.ImGui.ImGui.Spacing();

            // ── Tab Bar ─────────────────────────────────────────────────
            if (Hexa.NET.ImGui.ImGui.BeginTabBar("LeftTabs"))
            {
                // TAB 1: 动作列表
                ImGuiTabItemFlags motionTabFlags = ForceSelectMotionListTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
                if (Hexa.NET.ImGui.ImGui.BeginTabItem("动作列表", motionTabFlags))
                {
                    if (ForceSelectMotionListTab) ForceSelectMotionListTab = false;

                    // Import button
                    Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.Button, StudioTheme.PrimaryBlue);
                    Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.ButtonHovered, StudioTheme.PrimaryBlueBright);
                    if (Hexa.NET.ImGui.ImGui.Button("[导入外部动作]", new Vector2(-1, 28)))
                    {
                        string initialDir = (IsModelLoaded && CurrentWeaponIndex >= 0)
                            ? Path.Combine(_assetsDir, "WeaponBases", CurrentWeaponCode)
                            : Path.Combine(_assetsDir, "Samples");
                        if (!Directory.Exists(initialDir)) initialDir = _assetsDir;

                        string? selected = NativeFileDialog.OpenFile(
                            "导入外部动作文件 (Import Motion)",
                            "MHWilds 动作文件 (*.motlist*;*.mot)|*.motlist*;*.mot|所有文件 (*.*)|*.*",
                            initialDir
                        );
                        if (!string.IsNullOrEmpty(selected))
                        {
                            ImportExternalMotion(selected);
                        }
                    }
                    Hexa.NET.ImGui.ImGui.PopStyleColor(2);
                    if (Hexa.NET.ImGui.ImGui.IsItemHovered())
                        Hexa.NET.ImGui.ImGui.SetTooltip("导入外部动作文件，自动识别所属武器并智能切换对应武器素体预览！");

                    Hexa.NET.ImGui.ImGui.Spacing();

                    // Search
                    StudioTheme.SearchInput("##SearchMot", ref _motionSearchFilter, "搜索动作ID或名称...");

                    Hexa.NET.ImGui.ImGui.Spacing();
                    if (IsModelLoaded)
                    {
                        int vfxMotCount = _motionInfos.Count(m => m.VfxSlots.Count > 0);
                        Hexa.NET.ImGui.ImGui.TextColored(StudioTheme.TextSecondary,
                            $"共 {_motionInfos.Count} 个动作 (含特效: {vfxMotCount}) | {CurrentWeaponCode}");
                    }
                    else
                    {
                        Hexa.NET.ImGui.ImGui.TextColored(StudioTheme.TextSecondary, "（未选择武器素体）");
                    }
                Hexa.NET.ImGui.ImGui.Separator();

                float libraryListHeight = Math.Max(100f, Hexa.NET.ImGui.ImGui.GetContentRegionAvail().Y - 28f);
                Hexa.NET.ImGui.ImGui.BeginChild("MotionCategoryRail", new Vector2(58f, libraryListHeight));
                string[] railLabels = ["全部", "攻击", "移动", "特效", "偏移"];
                for (int filter = 0; filter < railLabels.Length; filter++)
                {
                    bool activeFilter = _motionCategoryFilter == filter;
                    if (activeFilter)
                        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.Button, StudioTheme.SelectedRowBg);
                    if (Hexa.NET.ImGui.ImGui.Button($"{railLabels[filter]}##rail_{filter}", new Vector2(52f, 36f)))
                        _motionCategoryFilter = filter;
                    if (activeFilter)
                        Hexa.NET.ImGui.ImGui.PopStyleColor();
                }
                Hexa.NET.ImGui.ImGui.EndChild();
                Hexa.NET.ImGui.ImGui.SameLine(0f, 4f);

                // Motion List Child
                Hexa.NET.ImGui.ImGui.BeginChild("MotionListScroll",
                    new Vector2(-1f, libraryListHeight),
                    ImGuiChildFlags.None);
                if (!IsModelLoaded || _motionInfos.Count == 0)
                {
                    Hexa.NET.ImGui.ImGui.Spacing();
                    Hexa.NET.ImGui.ImGui.TextDisabled("（请点击上方 [导入外部动作] 或选择武器开始预览）");
                }
                else
                {
                    var motDrawList = Hexa.NET.ImGui.ImGui.GetWindowDrawList();
                    for (int i = 0; i < _motionInfos.Count; i++)
                    {
                        var info = _motionInfos[i];
                        bool hasVfx = info.VfxSlots.Count > 0;
                        bool hasCustomMount = _weaponManager.PresetStore.HasMotionOverride(CurrentWeaponCode, info.Name);
                        string vfxTag = hasVfx ? $" [特效:{info.VfxSlots.Count}]" : "";
                        string mountTag = hasCustomMount ? " [*专属偏移]" : "";
                        string label = $"[{info.MotId:D3}] {info.Remark} ({info.Name}){vfxTag}{mountTag}";

                        // Category filter
                        if (_motionCategoryFilter == 1 && !info.Remark.Contains("攻击", StringComparison.OrdinalIgnoreCase)
                            && !info.Name.Contains("atk", StringComparison.OrdinalIgnoreCase)) continue;
                        if (_motionCategoryFilter == 2 && !info.Remark.Contains("移动", StringComparison.OrdinalIgnoreCase)
                            && !info.Name.Contains("move", StringComparison.OrdinalIgnoreCase)
                            && !info.Name.Contains("walk", StringComparison.OrdinalIgnoreCase)
                            && !info.Name.Contains("run", StringComparison.OrdinalIgnoreCase)) continue;
                        if (_motionCategoryFilter == 3 && !hasVfx) continue;
                        if (_motionCategoryFilter == 4 && !hasCustomMount) continue;

                        if (!string.IsNullOrEmpty(_motionSearchFilter))
                        {
                            if (!label.Contains(_motionSearchFilter, StringComparison.OrdinalIgnoreCase) &&
                                !info.MotId.ToString().Contains(_motionSearchFilter))
                            {
                                continue;
                            }
                        }

                        bool isSelected = (_selectedMotionIndex == i);

                        // Custom selected-row background + 3px accent bar
                        if (isSelected)
                        {
                            Vector2 rowMin = Hexa.NET.ImGui.ImGui.GetCursorScreenPos();
                            float rowHeight = Hexa.NET.ImGui.ImGui.GetTextLineHeightWithSpacing();
                            float rowWidth = Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X;
                            Vector2 rowMax = new(rowMin.X + rowWidth, rowMin.Y + rowHeight);
                            motDrawList.AddRectFilled(rowMin, rowMax, StudioTheme.V4ToAbgr(StudioTheme.SelectedRowBg));
                            motDrawList.AddRectFilled(rowMin, new Vector2(rowMin.X + 3f, rowMax.Y), StudioTheme.V4ToAbgr(StudioTheme.PrimaryBlueBright));
                        }

                        if (hasCustomMount)
                        {
                            Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.85f, 0.35f, 1.0f));
                        }
                        else if (hasVfx)
                        {
                            Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.95f, 1.0f, 1.0f));
                        }

                        // Use transparent header color for selectable so our custom bg shows through
                        if (isSelected)
                            Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0, 0, 0, 0));

                        if (Hexa.NET.ImGui.ImGui.Selectable(label + $"##mot_{i}", isSelected))
                        {
                            SelectMotion(i);
                        }
                        if (isSelected && _scrollToSelectedMotion)
                        {
                            Hexa.NET.ImGui.ImGui.SetScrollHereY(0.5f);
                            _scrollToSelectedMotion = false;
                        }

                        if (isSelected)
                            Hexa.NET.ImGui.ImGui.PopStyleColor();

                        if (hasCustomMount || hasVfx)
                        {
                            Hexa.NET.ImGui.ImGui.PopStyleColor();
                        }
                    }
                }
                Hexa.NET.ImGui.ImGui.EndChild();

                Hexa.NET.ImGui.ImGui.EndTabItem();
            }

            // TAB 2: 特效槽位批量操作
            if (Hexa.NET.ImGui.ImGui.BeginTabItem("特效槽位操作 (Slots)"))
            {
                Hexa.NET.ImGui.ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "[槽位] 动作特效槽位管理");
                Hexa.NET.ImGui.ImGui.Spacing();

                // Slot Add / Duplicate / Delete
                if (Hexa.NET.ImGui.ImGui.Button("[+ 新增槽位 (Add Slot)]", new Vector2(-1, 28)))
                {
                    AddNewSlot();
                }

                bool hasSlot = SelectedSlot != null;
                if (!hasSlot) Hexa.NET.ImGui.ImGui.BeginDisabled();
                if (Hexa.NET.ImGui.ImGui.Button("[复制选中槽位 (Duplicate)]", new Vector2(-1, 26)))
                {
                    DuplicateSelectedSlot();
                }
                if (Hexa.NET.ImGui.ImGui.Button("[- 删除选中槽位 (Delete)]", new Vector2(-1, 26)))
                {
                    DeleteSelectedSlot();
                }
                if (!hasSlot) Hexa.NET.ImGui.ImGui.EndDisabled();

                Hexa.NET.ImGui.ImGui.Separator();
                Hexa.NET.ImGui.ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), "[平移] 批量起止帧平移 (Batch Shift)");

                Hexa.NET.ImGui.ImGui.SetNextItemWidth(120);
                Hexa.NET.ImGui.ImGui.InputInt("平移帧数", ref _batchOffsetFrames);

                if (Hexa.NET.ImGui.ImGui.Button("平移选中槽位", new Vector2(140, 26)))
                {
                    if (SelectedSlot != null)
                    {
                        var s = SelectedSlot;
                        float oldStart = s.StartFrame, oldEnd = s.EndFrame;
                        UndoRedo.Execute(new RelayUndoableCommand("平移选中槽位帧",
                            () => { s.StartFrame += _batchOffsetFrames; s.EndFrame += _batchOffsetFrames; },
                            () => { s.StartFrame = oldStart; s.EndFrame = oldEnd; }));
                    }
                }
                Hexa.NET.ImGui.ImGui.SameLine();
                if (Hexa.NET.ImGui.ImGui.Button("平移所有槽位", new Vector2(140, 26)))
                {
                    _slotManager.BatchShiftFrames(_currentSlots, _batchOffsetFrames);
                    SetStatus($"已批量平移所有槽位 {_batchOffsetFrames} 帧");
                }

                Hexa.NET.ImGui.ImGui.Separator();
                Hexa.NET.ImGui.ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), "[缩放] 批量帧缩放 (Batch Scale)");
                Hexa.NET.ImGui.ImGui.SetNextItemWidth(120);
                Hexa.NET.ImGui.ImGui.DragFloat("缩放比例", ref _batchScaleFactor, 0.05f, 0.1f, 5.0f, "%.2fx");
                if (Hexa.NET.ImGui.ImGui.Button("缩放所有槽位时长", new Vector2(-1, 26)))
                {
                    _slotManager.BatchScaleDuration(_currentSlots, _batchScaleFactor);
                    SetStatus($"已批量缩放所有槽位时长为 {_batchScaleFactor:F2}x");
                }

                Hexa.NET.ImGui.ImGui.Separator();
                Hexa.NET.ImGui.ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), "[骨骼] 骨骼名称与 Murmur3 Hash 双向转换");

                Hexa.NET.ImGui.ImGui.SetNextItemWidth(-1);
                if (Hexa.NET.ImGui.ImGui.Combo("##QuickBones", ref _quickBoneIndex, _commonBones, _commonBones.Length))
                {
                    _boneLookupInput = _commonBones[_quickBoneIndex];
                    uint h = MurMur3HashUtils.GetHash(_boneLookupInput);
                    _boneLookupHex = $"0x{h:X8}";
                }

                Hexa.NET.ImGui.ImGui.Text("骨骼名称:");
                Hexa.NET.ImGui.ImGui.SetNextItemWidth(-1);
                if (Hexa.NET.ImGui.ImGui.InputText("##BoneNameInput", ref _boneLookupInput, 64))
                {
                    uint h = MurMur3HashUtils.GetHash(_boneLookupInput);
                    _boneLookupHex = $"0x{h:X8}";
                }

                Hexa.NET.ImGui.ImGui.Text("Murmur3 Hash (Hex):");
                Hexa.NET.ImGui.ImGui.SetNextItemWidth(-1);
                if (Hexa.NET.ImGui.ImGui.InputText("##BoneHexInput", ref _boneLookupHex, 32))
                {
                    try
                    {
                        string clean = _boneLookupHex.Replace("0x", "").Trim();
                        if (uint.TryParse(clean, System.Globalization.NumberStyles.HexNumber, null, out uint val))
                        {
                            string resolved = BoneHashRegistry.GetName(val);
                            if (!string.IsNullOrEmpty(resolved))
                            {
                                _boneLookupInput = resolved;
                            }
                        }
                    }
                    catch { }
                }

                if (SelectedSlot != null && Hexa.NET.ImGui.ImGui.Button("应用到当前选中槽位", new Vector2(-1, 26)))
                {
                    var s = SelectedSlot;
                    string oldBone = s.JointName;
                    string newBone = _boneLookupInput;
                    UndoRedo.Execute(new RelayUndoableCommand("修改挂载骨骼",
                        () => { s.JointName = newBone; },
                        () => { s.JointName = oldBone; }));
                    SetStatus($"已将槽位挂载骨骼修改为: {newBone}");
                }

                Hexa.NET.ImGui.ImGui.EndTabItem();
            }

            if (_previewDebug.ShowPreviewEffects && Hexa.NET.ImGui.ImGui.BeginTabItem("预览特效绑定",
                    _forcePreviewBindingTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                _forcePreviewBindingTab = false;
                RenderPreviewEffectBindingTab();
                Hexa.NET.ImGui.ImGui.EndTabItem();
            }

            if (_previewDebug.ShowMotionNotes && Hexa.NET.ImGui.ImGui.BeginTabItem("动作备注"))
            {
                RenderMotionNoteTab();
                Hexa.NET.ImGui.ImGui.EndTabItem();
            }

            // Weapon mount calibration is opt-in under Debug Mode.
            ImGuiTabItemFlags mountTabFlags = ForceSelectMountCalibrationTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (_previewDebug.ShowBoneMounts && Hexa.NET.ImGui.ImGui.BeginTabItem("武器挂点校准", mountTabFlags))
            {
                if (ForceSelectMountCalibrationTab)
                {
                    ForceSelectMountCalibrationTab = false;
                }
                RenderWeaponMountCalibrationTab();
                Hexa.NET.ImGui.ImGui.EndTabItem();
            }

            Hexa.NET.ImGui.ImGui.EndTabBar();
        }

        // ── Bottom footer status ────────────────────────────────────────
        StudioTheme.FooterStatusBar($"导入/导出  |  {_statusMessage}");

        StudioTheme.EndCard();
    }

    private void RenderPreviewEffectBindingTab()
    {
        Hexa.NET.ImGui.ImGui.TextWrapped("仅供预览：绑定不会创建动作特效槽位，也不会写入导出的动作文件。");
        if (_currentMotlist == null || _motionInfos.Count == 0)
        {
            Hexa.NET.ImGui.ImGui.TextDisabled("请先选择武器并加载动作列表。");
            return;
        }
        Hexa.NET.ImGui.ImGui.SeparatorText("选择要绑定的动作（可多选）");
        if (Hexa.NET.ImGui.ImGui.Button("勾选当前动作") && _selectedMotionIndex >= 0)
            _previewTargetMotions.Add(_selectedMotionIndex);
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("全选"))
            for (int i = 0; i < _motionInfos.Count; i++) _previewTargetMotions.Add(i);
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("清空")) _previewTargetMotions.Clear();
        Hexa.NET.ImGui.ImGui.TextDisabled($"已勾选 {_previewTargetMotions.Count} 个动作");
        StudioTheme.SearchInput("##PreviewMotionFilter", ref _previewMotionFilter, "筛选动作编号或名称...");
        Hexa.NET.ImGui.ImGui.BeginChild("PreviewMotionTargets", new Vector2(-1f, 190f), ImGuiChildFlags.Borders);
        for (int i = 0; i < _motionInfos.Count; i++)
        {
            var info = _motionInfos[i];
            string label = $"[{info.MotId:D3}] {info.Remark} ({info.Name})";
            if (!string.IsNullOrWhiteSpace(_previewMotionFilter) &&
                !label.Contains(_previewMotionFilter, StringComparison.OrdinalIgnoreCase)) continue;
            bool selected = _previewTargetMotions.Contains(i);
            if (Hexa.NET.ImGui.ImGui.Checkbox($"{label}##PreviewTarget_{i}", ref selected))
            {
                if (selected) _previewTargetMotions.Add(i);
                else _previewTargetMotions.Remove(i);
            }
        }
        Hexa.NET.ImGui.ImGui.EndChild();

        Hexa.NET.ImGui.ImGui.SeparatorText("导入特效文件");
        Hexa.NET.ImGui.ImGui.SetNextItemWidth(-1f);
        Hexa.NET.ImGui.ImGui.InputText("##PreviewEffectPath", ref _previewEffectPath, 2048);
        if (Hexa.NET.ImGui.ImGui.Button("选择 EFX 文件...", new Vector2(-1f, 27f)))
        {
            string? selected = NativeFileDialog.OpenFile("选择预览特效", "EFX 文件 (*.efx*)|*.efx*|所有文件 (*.*)|*.*",
                Directory.Exists(Path.GetDirectoryName(_previewEffectPath))
                    ? Path.GetDirectoryName(_previewEffectPath)! : _assetsDir);
            if (!string.IsNullOrWhiteSpace(selected)) _previewEffectPath = selected;
        }
        if (Hexa.NET.ImGui.ImGui.Button("绑定到已勾选动作", new Vector2(-1f, 30f)))
            BindPreviewEffectToSelectedMotions();

        Hexa.NET.ImGui.ImGui.SeparatorText("当前动作的预览特效");
        var bindings = GetPreviewBindings(_selectedMotionIndex);
        if (bindings.Count == 0)
            Hexa.NET.ImGui.ImGui.TextDisabled("当前动作尚未绑定预览特效。");
        foreach (var binding in bindings)
        {
            string name = Path.GetFileName(binding.EfxPath);
            if (!File.Exists(binding.EfxPath)) name += " [文件丢失]";
            if (Hexa.NET.ImGui.ImGui.Selectable($"{name}  {binding.StartFrame:F0}–{binding.EndFrame:F0} 帧##{binding.Id}",
                    _selectedPreviewBindingId == binding.Id))
                _selectedPreviewBindingId = binding.Id;
        }
        var current = bindings.FirstOrDefault(b => b.Id == _selectedPreviewBindingId);
        if (current != null)
        {
            Hexa.NET.ImGui.ImGui.TextWrapped("时间范围请在 3D 预览视窗中调整。");
            if (Hexa.NET.ImGui.ImGui.Button("删除选中的预览绑定", new Vector2(-1f, 27f)))
            {
                _previewDebug.Effects.Remove(current);
                _selectedPreviewBindingId = bindings.FirstOrDefault(b => b.Id != current.Id)?.Id ?? string.Empty;
                SavePreviewDebug();
                _vfxProxy.ClearPreviewSimulators();
            }
        }
    }

    private void RenderMotionNoteTab()
    {
        if (_selectedMotionIndex < 0 || _selectedMotionIndex >= _motionInfos.Count)
        {
            Hexa.NET.ImGui.ImGui.TextDisabled("请先选择动作。");
            return;
        }
        var info = _motionInfos[_selectedMotionIndex];
        if (_motionNoteDraftIndex != _selectedMotionIndex)
        {
            _motionNoteDraftIndex = _selectedMotionIndex;
            _motionNoteDraft = info.Remark;
        }
        Hexa.NET.ImGui.ImGui.Text($"动作 [{info.MotId:D3}] {info.Name}");
        Hexa.NET.ImGui.ImGui.TextWrapped("备注会显示在动作列表和预览标题中，仅保存在编辑器调试数据中。");
        Hexa.NET.ImGui.ImGui.SetNextItemWidth(-1f);
        Hexa.NET.ImGui.ImGui.InputText("##MotionNoteDraft", ref _motionNoteDraft, 256);
        if (Hexa.NET.ImGui.ImGui.Button("保存备注", new Vector2(-1f, 28f)))
        {
            string noteText = _motionNoteDraft.Trim();
            var note = _previewDebug.Notes.LastOrDefault(n => PreviewDebugStore.MatchesMotion(
                _currentMotlistPath, CurrentWeaponCode, info.Index, n.MotlistPath, n.WeaponCode, n.MotionIndex));
            if (note == null)
            {
                note = new MotionNoteOverride { MotlistPath = Path.GetFullPath(_currentMotlistPath),
                    WeaponCode = CurrentWeaponCode, MotionIndex = info.Index, MotionId = info.MotId };
                _previewDebug.Notes.Add(note);
            }
            note.Text = noteText;
            info.Remark = noteText;
            SavePreviewDebug();
            SetStatus($"已保存动作 [{info.MotId:D3}] 的备注");
        }
        if (Hexa.NET.ImGui.ImGui.Button("恢复原备注", new Vector2(-1f, 28f)))
        {
            _previewDebug.Notes.RemoveAll(n => PreviewDebugStore.MatchesMotion(
                _currentMotlistPath, CurrentWeaponCode, info.Index, n.MotlistPath, n.WeaponCode, n.MotionIndex));
            info.Remark = _motionDict.GetMotionName(CurrentWeaponCode, info.MotId);
            _motionNoteDraft = info.Remark;
            SavePreviewDebug();
        }
    }

    private void CommitPresetChange(WeaponPart part, WeaponPartMountPreset before, WeaponPartMountPreset after, string description)
    {
        UndoRedo.Execute(new RelayUndoableCommand(description,
            () => part.ApplyPreset(after),
            () => part.ApplyPreset(before)));
    }

    private void TrackSliderUndoRedo(WeaponPart part, string description)
    {
        if (Hexa.NET.ImGui.ImGui.IsItemActivated())
        {
            _sliderDragStartPreset = part.ToPreset();
        }
        if (Hexa.NET.ImGui.ImGui.IsItemDeactivatedAfterEdit() && _sliderDragStartPreset != null)
        {
            var before = _sliderDragStartPreset.Clone();
            var after = part.ToPreset();
            _sliderDragStartPreset = null;
            CommitPresetChange(part, before, after, description);
        }
    }

    private void ApplyTargetBoneToPart(WeaponPart part, string newBoneName)
    {
        if (string.IsNullOrWhiteSpace(newBoneName) || part.TargetBoneName == newBoneName) return;
        var before = part.ToPreset();
        part.TargetBoneName = newBoneName;
        var after = part.ToPreset();
        CommitPresetChange(part, before, after, $"切换部件 {part.PartIndex} 挂载骨骼 -> {newBoneName}");
        SetStatus($"[实时预览] 部件 Part {part.PartIndex} 已挂载至骨骼: {newBoneName}");
    }

    private void RotatePartAxis(WeaponPart part, int axis, float deltaDegrees)
    {
        var before = part.ToPreset();
        Vector3 euler = part.RotationEuler;
        if (axis == 0) euler.X = WeaponAttachmentManager.NormalizeAngle180(euler.X + deltaDegrees);
        else if (axis == 1) euler.Y = WeaponAttachmentManager.NormalizeAngle180(euler.Y + deltaDegrees);
        else if (axis == 2) euler.Z = WeaponAttachmentManager.NormalizeAngle180(euler.Z + deltaDegrees);
        part.RotationEuler = euler;
        var after = part.ToPreset();
        string axisName = axis == 0 ? "X" : (axis == 1 ? "Y" : "Z");
        CommitPresetChange(part, before, after, $"旋转武器部件 {axisName} 轴 ({deltaDegrees:+0;-0}度)");
        SetStatus($"[实时校准] Part {part.PartIndex} {axisName} 轴旋转已调整为 {euler.X:F1}°, {euler.Y:F1}°, {euler.Z:F1}°");
    }

    private void RenderWeaponMountCalibrationTab()
    {
        if (!IsModelLoaded || _weaponManager.CurrentWeapon == null || _weaponManager.Parts.Count == 0)
        {
            Hexa.NET.ImGui.ImGui.Spacing();
            Hexa.NET.ImGui.ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), "尚未加载武器模型！");
            Hexa.NET.ImGui.ImGui.TextWrapped("请先在上方「当前武器与猎人素体」中选择一把武器（wp00 ~ wp13），即可实时校准武器挂点骨骼、XYZ 位置偏移与欧拉旋转角。");
            Hexa.NET.ImGui.ImGui.Spacing();
            if (Hexa.NET.ImGui.ImGui.Button("立即加载 wp03 太刀进行挂点校准", new Vector2(-1, 30)))
            {
                SwitchWeapon(3);
            }
            return;
        }

        var wpDef = _weaponManager.CurrentWeapon;
        Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6f, 3f));

        string wpDisplayName = (CurrentWeaponIndex >= 0 && CurrentWeaponIndex < WeaponNames.Length)
            ? WeaponNames[CurrentWeaponIndex]
            : wpDef.WpCode;
        Hexa.NET.ImGui.ImGui.TextColored(new Vector4(0.35f, 0.88f, 1.0f, 1.0f), $"当前校准武器: {wpDisplayName}");

        string curMotName = _weaponManager.CurrentMotionKey ?? string.Empty;
        if (!string.IsNullOrEmpty(curMotName))
        {
            if (_weaponManager.IsUsingMotionOverride)
            {
                Hexa.NET.ImGui.ImGui.TextColored(new Vector4(1.0f, 0.82f, 0.25f, 1.0f),
                    $"当前模式: [★ 动作专属武器偏移] ({curMotName})");
            }
            else
            {
                Hexa.NET.ImGui.ImGui.TextColored(new Vector4(0.45f, 0.95f, 0.65f, 1.0f),
                    $"当前模式: [武器通用默认偏移] (动作: {curMotName})");
            }
        }

        // 1. 控件 1：当前调节部件切换 (Sub-part Selector)
        Hexa.NET.ImGui.ImGui.SeparatorText("1. 当前调节部件切换 (Sub-part Selector)");
        if (_selectedWeaponPartIndex >= _weaponManager.Parts.Count)
        {
            _selectedWeaponPartIndex = 0;
        }

        float partBtnW = _weaponManager.Parts.Count > 1
            ? (Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X - 8f) * 0.5f
            : -1f;

        for (int i = 0; i < _weaponManager.Parts.Count; i++)
        {
            string label = i == 0
                ? "Part 0: 主武器 (_0.mesh)"
                : $"Part {i}: 副手/刀鞘 (_{i}.mesh)";
            bool isSelected = (_selectedWeaponPartIndex == i);
            if (isSelected)
            {
                Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.52f, 0.82f, 1.0f));
            }
            if (Hexa.NET.ImGui.ImGui.Button($"{label}##partSel_{i}", new Vector2(i == 0 ? partBtnW : -1f, 25)))
            {
                _selectedWeaponPartIndex = i;
            }
            if (isSelected)
            {
                Hexa.NET.ImGui.ImGui.PopStyleColor();
            }
            if (i == 0 && _weaponManager.Parts.Count > 1)
            {
                Hexa.NET.ImGui.ImGui.SameLine();
            }
        }

        var activePart = _weaponManager.Parts[_selectedWeaponPartIndex];
        Matrix4x4 targetBoneMat = _weaponManager.GetTargetBoneWorldMatrix(activePart, _animator);
        Vector3 bonePos = targetBoneMat.Translation;

        // 2. 控件 2：挂载目标骨骼选择器 (Target Bone Selector - 支持实时搜索与快捷按钮)
        Hexa.NET.ImGui.ImGui.SeparatorText("2. 挂载目标骨骼选择器 (Target Bone)");
        Hexa.NET.ImGui.ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.55f, 1.0f),
            $"当前挂载骨骼: {activePart.TargetBoneName}  Pos:<{bonePos.X:F2}, {bonePos.Y:F2}, {bonePos.Z:F2}>");

        // 快捷手部与后背挂点按钮
        float halfW = (Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X - 6f) * 0.5f;
        if (Hexa.NET.ImGui.ImGui.Button("右手 R_Hand_HJ_01", new Vector2(halfW, 22)))
            ApplyTargetBoneToPart(activePart, "R_Hand_HJ_01");
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("左手 L_Hand_HJ_01", new Vector2(-1, 22)))
            ApplyTargetBoneToPart(activePart, "L_Hand_HJ_01");

        if (Hexa.NET.ImGui.ImGui.Button("右手掌 R_Hand", new Vector2(halfW, 22)))
            ApplyTargetBoneToPart(activePart, "R_Hand");
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("左手掌 L_Hand", new Vector2(-1, 22)))
            ApplyTargetBoneToPart(activePart, "L_Hand");

        // 自动扫描并展示素体中的后背/腰部武器约束骨骼 (*WpConst* 等)
        if (Hexa.NET.ImGui.ImGui.Button("上背 Spine_2_WpConst", new Vector2(halfW, 22)))
            ApplyTargetBoneToPart(activePart, "Spine_2_WpConst");
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("下背 Spine_1_WpConst", new Vector2(-1, 22)))
            ApplyTargetBoneToPart(activePart, "Spine_1_WpConst");

        if (Hexa.NET.ImGui.ImGui.Button("腰部 Hip_WpConst", new Vector2(halfW, 22)))
            ApplyTargetBoneToPart(activePart, "Hip_WpConst");
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("胸椎 Spine_2 / COG", new Vector2(-1, 22)))
            ApplyTargetBoneToPart(activePart, activePart.TargetBoneName == "Spine_2" ? "COG" : "Spine_2");

        // 全骨骼搜索框 + 实时过滤骨骼列表
        Hexa.NET.ImGui.ImGui.SetNextItemWidth(-1);
        Hexa.NET.ImGui.ImGui.InputTextWithHint("##mountBoneSearch", "搜索全身骨骼 (如 Hand, Spine, Const, Wp, Hip)...", ref _mountBoneSearchFilter, 128);

        if (Hexa.NET.ImGui.ImGui.BeginChild("MountBoneListChild", new Vector2(-1, 64), ImGuiChildFlags.Borders))
        {
            foreach (var bone in _animator.Bones)
            {
                if (string.IsNullOrEmpty(bone.Name)) continue;
                if (!string.IsNullOrEmpty(_mountBoneSearchFilter) &&
                    !bone.Name.Contains(_mountBoneSearchFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool selected = (bone.Name == activePart.TargetBoneName);
                Vector3 bWorld = _animator.GetBoneWorldMatrix(bone.Name).Translation;
                string itemLabel = $"{bone.Name}  (<{bWorld.X:F2}, {bWorld.Y:F2}, {bWorld.Z:F2}>)##mb_{bone.Index}";
                if (Hexa.NET.ImGui.ImGui.Selectable(itemLabel, selected))
                {
                    ApplyTargetBoneToPart(activePart, bone.Name);
                }
            }
        }
        Hexa.NET.ImGui.ImGui.EndChild();

        // 3. 控件 3：局部位置偏移实时滑块 (XYZ Position Offset)
        Hexa.NET.ImGui.ImGui.SeparatorText("3. 局部位置偏移 (XYZ Position Offset)");
        Vector3 pos = activePart.PositionOffset;

        Hexa.NET.ImGui.ImGui.SetNextItemWidth(Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X - 70f);
        if (Hexa.NET.ImGui.ImGui.DragFloat("X 偏移##posX", ref pos.X, 0.005f, -5.0f, 5.0f, "%.3f m"))
        {
            activePart.PositionOffset = pos;
        }
        TrackSliderUndoRedo(activePart, "调整武器 X 轴位置偏移");

        Hexa.NET.ImGui.ImGui.SetNextItemWidth(Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X - 70f);
        if (Hexa.NET.ImGui.ImGui.DragFloat("Y 偏移##posY", ref pos.Y, 0.005f, -5.0f, 5.0f, "%.3f m"))
        {
            activePart.PositionOffset = pos;
        }
        TrackSliderUndoRedo(activePart, "调整武器 Y 轴位置偏移");

        Hexa.NET.ImGui.ImGui.SetNextItemWidth(Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X - 70f);
        if (Hexa.NET.ImGui.ImGui.DragFloat("Z 偏移##posZ", ref pos.Z, 0.005f, -5.0f, 5.0f, "%.3f m"))
        {
            activePart.PositionOffset = pos;
        }
        TrackSliderUndoRedo(activePart, "调整武器 Z 轴位置偏移");

        if (Hexa.NET.ImGui.ImGui.Button("重置位置归零 (Reset Pos)", new Vector2(-1, 22)))
        {
            var before = activePart.ToPreset();
            activePart.PositionOffset = Vector3.Zero;
            CommitPresetChange(activePart, before, activePart.ToPreset(), "重置武器位置偏移归零");
            SetStatus($"已重置 Part {activePart.PartIndex} 位置偏移为 <0, 0, 0>");
        }

        // 4. 控件 4：局部旋转角度实时滑块与 90° 快捷翻转按钮 (XYZ Rotation Euler - Degrees)
        Hexa.NET.ImGui.ImGui.SeparatorText("4. 局部旋转角度 (XYZ Euler Degrees)");
        Vector3 rot = activePart.RotationEuler;

        // X 轴 (Pitch)
        Hexa.NET.ImGui.ImGui.SetNextItemWidth(Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X - 155f);
        if (Hexa.NET.ImGui.ImGui.SliderFloat("X轴##rotX", ref rot.X, -180.0f, 180.0f, "%.1f°"))
        {
            activePart.RotationEuler = rot;
        }
        TrackSliderUndoRedo(activePart, "调整武器 X 轴旋转角");
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("-90°##rx_m90", new Vector2(36, 0))) RotatePartAxis(activePart, 0, -90f);
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("+90°##rx_p90", new Vector2(36, 0))) RotatePartAxis(activePart, 0, +90f);
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("180°##rx_180", new Vector2(38, 0))) RotatePartAxis(activePart, 0, 180f);

        // Y 轴 (Yaw)
        rot = activePart.RotationEuler;
        Hexa.NET.ImGui.ImGui.SetNextItemWidth(Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X - 155f);
        if (Hexa.NET.ImGui.ImGui.SliderFloat("Y轴##rotY", ref rot.Y, -180.0f, 180.0f, "%.1f°"))
        {
            activePart.RotationEuler = rot;
        }
        TrackSliderUndoRedo(activePart, "调整武器 Y 轴旋转角");
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("-90°##ry_m90", new Vector2(36, 0))) RotatePartAxis(activePart, 1, -90f);
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("+90°##ry_p90", new Vector2(36, 0))) RotatePartAxis(activePart, 1, +90f);
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("180°##ry_180", new Vector2(38, 0))) RotatePartAxis(activePart, 1, 180f);

        // Z 轴 (Roll)
        rot = activePart.RotationEuler;
        Hexa.NET.ImGui.ImGui.SetNextItemWidth(Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X - 155f);
        if (Hexa.NET.ImGui.ImGui.SliderFloat("Z轴##rotZ", ref rot.Z, -180.0f, 180.0f, "%.1f°"))
        {
            activePart.RotationEuler = rot;
        }
        TrackSliderUndoRedo(activePart, "调整武器 Z 轴旋转角");
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("-90°##rz_m90", new Vector2(36, 0))) RotatePartAxis(activePart, 2, -90f);
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("+90°##rz_p90", new Vector2(36, 0))) RotatePartAxis(activePart, 2, +90f);
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("180°##rz_180", new Vector2(38, 0))) RotatePartAxis(activePart, 2, 180f);

        if (Hexa.NET.ImGui.ImGui.Button("重置旋转归零 (Reset Rot)", new Vector2(-1, 22)))
        {
            var before = activePart.ToPreset();
            activePart.RotationEuler = Vector3.Zero;
            CommitPresetChange(activePart, before, activePart.ToPreset(), "重置武器旋转角度归零");
            SetStatus($"已重置 Part {activePart.PartIndex} 旋转角度为 <0°, 0°, 0°>");
        }

        // 整体缩放与 Base 骨骼逆矩阵选项
        float scale = activePart.Scale;
        Hexa.NET.ImGui.ImGui.SetNextItemWidth(Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X - 75f);
        if (Hexa.NET.ImGui.ImGui.SliderFloat("缩放##wpScale", ref scale, 0.1f, 3.0f, "%.2f x"))
        {
            activePart.Scale = scale;
        }
        TrackSliderUndoRedo(activePart, "调整武器缩放倍率");
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("重置1.0", new Vector2(-1, 0)))
        {
            var before = activePart.ToPreset();
            activePart.Scale = 1.0f;
            CommitPresetChange(activePart, before, activePart.ToPreset(), "重置武器缩放为 1.0");
        }

        bool useBaseInv = activePart.UseWeaponBaseBoneInverse;
        if (Hexa.NET.ImGui.ImGui.Checkbox("叠乘武器自身 Base 骨骼逆矩阵对齐", ref useBaseInv))
        {
            var before = activePart.ToPreset();
            activePart.UseWeaponBaseBoneInverse = useBaseInv;
            CommitPresetChange(activePart, before, activePart.ToPreset(), "切换武器 Base 骨骼逆矩阵对齐");
        }

        // 5. 控件 5：3D 视口坐标轴直接拖拽 (ImGuizmo Direct Manipulation)
        Hexa.NET.ImGui.ImGui.SeparatorText("5. 3D 视口坐标轴直接拖拽 (ImGuizmo)");
        Hexa.NET.ImGui.ImGui.Checkbox("在 3D 视口中显示并拖拽当前武器坐标轴", ref _showWeaponMountGizmo);

        float modeBtnW = (Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X - 12f) / 3.0f;
        bool isTrans = _gizmoOperation == ImGuizmoOperation.Translate;
        bool isRot = _gizmoOperation == ImGuizmoOperation.Rotate;
        bool isScale = _gizmoOperation == ImGuizmoOperation.Scale;

        if (isTrans) Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.55f, 0.85f, 1.0f));
        if (Hexa.NET.ImGui.ImGui.Button("W: 平移", new Vector2(modeBtnW, 22))) _gizmoOperation = ImGuizmoOperation.Translate;
        if (isTrans) Hexa.NET.ImGui.ImGui.PopStyleColor();
        Hexa.NET.ImGui.ImGui.SameLine();

        if (isRot) Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.55f, 0.85f, 1.0f));
        if (Hexa.NET.ImGui.ImGui.Button("E: 旋转", new Vector2(modeBtnW, 22))) _gizmoOperation = ImGuizmoOperation.Rotate;
        if (isRot) Hexa.NET.ImGui.ImGui.PopStyleColor();
        Hexa.NET.ImGui.ImGui.SameLine();

        if (isScale) Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.55f, 0.85f, 1.0f));
        if (Hexa.NET.ImGui.ImGui.Button("R: 缩放", new Vector2(-1, 22))) _gizmoOperation = ImGuizmoOperation.Scale;
        if (isScale) Hexa.NET.ImGui.ImGui.PopStyleColor();

        // 6. 控件 6：单动作专属武器偏移 & 武器通用默认预设持久化
        Hexa.NET.ImGui.ImGui.SeparatorText("6. 单动作专属偏移 & 武器通用预设");
        if (!string.IsNullOrEmpty(curMotName))
        {
            Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.75f, 0.45f, 0.12f, 1.0f));
            Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.88f, 0.55f, 0.18f, 1.0f));
            if (Hexa.NET.ImGui.ImGui.Button($"★ 保存为当前动作专属偏移 ({curMotName})", new Vector2(-1, 26)))
            {
                _weaponManager.SaveCurrentMotionOverride(curMotName);
                SetStatus($"[成功] 已为动作 {curMotName} 单独保存专属武器挂点与偏移！切换其它动作不受影响。");
            }
            Hexa.NET.ImGui.ImGui.PopStyleColor(2);

            bool hasMotOverride = _weaponManager.IsUsingMotionOverride;
            if (!hasMotOverride) Hexa.NET.ImGui.ImGui.BeginDisabled();
            if (Hexa.NET.ImGui.ImGui.Button("清除当前动作专属偏移 (恢复武器通用配置)", new Vector2(-1, 23)))
            {
                _weaponManager.ClearMotionOverride(curMotName);
                SetStatus($"已清除动作 {curMotName} 的专属偏移，已恢复为 {wpDef.WpCode} 通用挂点配置");
            }
            if (!hasMotOverride) Hexa.NET.ImGui.ImGui.EndDisabled();
        }

        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.16f, 0.58f, 0.28f, 1.0f));
        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.22f, 0.68f, 0.35f, 1.0f));
        if (Hexa.NET.ImGui.ImGui.Button("保存并设为该武器通用默认配置 (Save Default)", new Vector2(-1, 26)))
        {
            _weaponManager.SaveCurrentWeaponPresets();
            SetStatus($"[成功] 已将 {wpDef.WpCode} 的挂点与旋转配置设为通用默认，下次启动与切换将自动生效！");
        }
        Hexa.NET.ImGui.ImGui.PopStyleColor(2);
        Hexa.NET.ImGui.ImGui.PopStyleVar();
    }

    public void AddNewSlot()
    {
        if (_currentMot == null) return;

        int defaultContainerId = 10;
        int defaultElementId = 2;
        string defaultJoint = "R_Weapon";

        // Pick a weapon-specific PFB element if available so the newly added slot immediately previews a real weapon EFX
        var preferredElem = _pfbService.AllElements.FirstOrDefault(e => (e.ContainerId == 10 || e.ContainerId == 90) && e.ResourceEfxPaths.Count > 0)
                         ?? _pfbService.AllElements.FirstOrDefault(e => e.ResourceEfxPaths.Count > 0);
        if (preferredElem != null)
        {
            defaultContainerId = preferredElem.ContainerId >= 0 ? preferredElem.ContainerId : 10;
            defaultElementId = preferredElem.ElementId;
            if (!string.IsNullOrEmpty(preferredElem.JointName))
                defaultJoint = preferredElem.JointName;
        }

        var newSlot = new VfxSlot
        {
            SlotId = _currentSlots.Count + 1,
            EffectId = defaultElementId.ToString(),
            ElementId = defaultElementId,
            ContainerId = defaultContainerId,
            StartFrame = 0f,
            EndFrame = Math.Min(35f, _maxFrame),
            JointName = defaultJoint
        };

        UndoRedo.Execute(new RelayUndoableCommand("新增特效槽位",
            () => {
                _currentSlots.Add(newSlot);
                _selectedSlotIndex = _currentSlots.Count - 1;
                _vfxProxy.ClearSimulators();
                RefreshCurrentMotionCalledEfx();
            },
            () => {
                _currentSlots.Remove(newSlot);
                _selectedSlotIndex = _currentSlots.Count - 1;
                _vfxProxy.ClearSimulators();
                RefreshCurrentMotionCalledEfx();
            }));

        SetStatus($"已新增特效槽位 (Container {newSlot.ContainerId} - Element {newSlot.EffectId})，右侧特效面板已激活");
    }

    private void DuplicateSelectedSlot()
    {
        if (SelectedSlot == null) return;
        var s = SelectedSlot;
        if (s.NativeMultiKeyTrack) { SetStatus("原生多关键帧轨道暂不支持安全复制"); return; }
        var clone = new VfxSlot
        {
            SlotId = _currentSlots.Count + 1,
            EffectId = s.EffectId,
            ContainerId = s.ContainerId,
            ElementId = s.ElementId,
            StartFrame = s.StartFrame,
            EndFrame = s.EndFrame,
            JointName = s.JointName,
            IntervalFrame = s.IntervalFrame,
            ActorMotion = s.ActorMotion
        };

        UndoRedo.Execute(new RelayUndoableCommand("复制特效槽位",
            () => {
                _currentSlots.Add(clone);
                _selectedSlotIndex = _currentSlots.Count - 1;
                _vfxProxy.ClearSimulators();
                RefreshCurrentMotionCalledEfx();
            },
            () => {
                _currentSlots.Remove(clone);
                _selectedSlotIndex = _currentSlots.Count - 1;
                _vfxProxy.ClearSimulators();
                RefreshCurrentMotionCalledEfx();
            }));

        SetStatus("已复制特效槽位");
    }

    private void DeleteSelectedSlot()
    {
        if (SelectedSlot == null) return;
        var s = SelectedSlot;
        if (s.NativeMultiKeyTrack) { SetStatus("原生多关键帧轨道暂不支持单事件删除"); return; }
        int idx = _selectedSlotIndex;

        UndoRedo.Execute(new RelayUndoableCommand("删除特效槽位",
            () => {
                _currentSlots.RemoveAt(idx);
                _selectedSlotIndex = Math.Clamp(idx, 0, _currentSlots.Count - 1);
                _vfxProxy.ClearSimulators();
                RefreshCurrentMotionCalledEfx();
            },
            () => {
                _currentSlots.Insert(idx, s);
                _selectedSlotIndex = idx;
                _vfxProxy.ClearSimulators();
                RefreshCurrentMotionCalledEfx();
            }));

        SetStatus("已删除特效槽位");
    }

    private void RenderCenterViewport_Card(float width, float height)
    {
        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.ChildBg, StudioTheme.HexToV4(0x0D111C));
        Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, StudioTheme.CardRounding);
        Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0f, 0f));
        Hexa.NET.ImGui.ImGui.BeginChild("CenterViewportCard", new Vector2(width, height), ImGuiChildFlags.Borders);

        Vector2 contentSize = Hexa.NET.ImGui.ImGui.GetContentRegionAvail();
        int targetW = Math.Max(100, (int)contentSize.X);
        int targetH = Math.Max(100, (int)contentSize.Y);

        if (targetW != _pipeline.Width || targetH != _pipeline.Height)
        {
            _pipeline.Resize(targetW, targetH);
            _viewportSize = new Vector2(targetW, targetH);
        }

        Vector2 curPos = Hexa.NET.ImGui.ImGui.GetCursorScreenPos();
        _viewportPos = curPos;

        // Display Rendered OpenGL Scene Image
        ImTextureRef texRef = default;
        texRef.TexID = new ImTextureID((nint)_pipeline.FinalColorTexture);
        Hexa.NET.ImGui.ImGui.Image(texRef, new Vector2(targetW, targetH), new Vector2(0, 1), new Vector2(1, 0));

        _isViewportHovered = Hexa.NET.ImGui.ImGui.IsItemHovered();
        _isViewportFocused = Hexa.NET.ImGui.ImGui.IsWindowFocused();

        HandleViewportTransform(curPos, contentSize);
        if (_transformKind == '\0' && Hexa.NET.ImGui.ImGui.BeginPopupContextItem("ViewportViews"))
        {
            if (Hexa.NET.ImGui.ImGui.MenuItem("前视图  Num 1")) Camera.SetStandardView(OrbitCamera.StandardView.Front);
            if (Hexa.NET.ImGui.ImGui.MenuItem("后视图  Ctrl+Num 1")) Camera.SetStandardView(OrbitCamera.StandardView.Back);
            if (Hexa.NET.ImGui.ImGui.MenuItem("右视图  Num 3")) Camera.SetStandardView(OrbitCamera.StandardView.Right);
            if (Hexa.NET.ImGui.ImGui.MenuItem("左视图  Ctrl+Num 3")) Camera.SetStandardView(OrbitCamera.StandardView.Left);
            if (Hexa.NET.ImGui.ImGui.MenuItem("顶视图  Num 7")) Camera.SetStandardView(OrbitCamera.StandardView.Top);
            if (Hexa.NET.ImGui.ImGui.MenuItem("底视图  Ctrl+Num 7")) Camera.SetStandardView(OrbitCamera.StandardView.Bottom);
            if (Hexa.NET.ImGui.ImGui.MenuItem("透视/正交  Num 5")) Camera.ToggleProjection();
            if (Hexa.NET.ImGui.ImGui.MenuItem("聚焦选中  Num .")) FocusViewportSelection();
            if (Hexa.NET.ImGui.ImGui.MenuItem("显示全部  Home")) Camera.Focus(new Vector3(0f, 1.15f, 0f), 1.8f, _viewportSize.X / Math.Max(1f, _viewportSize.Y));
            Hexa.NET.ImGui.ImGui.EndPopup();
        }

        // Viewport Camera Navigation
        HandleCameraInput();

        // 3D Transform Gizmo (ImGuizmo)
        RenderImGuizmoGizmo(curPos, contentSize);

        // ── Viewport title overlay ──────────────────────────────────────
        var drawList = Hexa.NET.ImGui.ImGui.GetWindowDrawList();
        string viewLabel = "3D 预览";
        if (IsModelLoaded && _currentMot != null)
        {
            string motName = _motionInfos.Count > _selectedMotionIndex && _selectedMotionIndex >= 0
                ? _motionInfos[_selectedMotionIndex].Remark
                : _currentMot.Name ?? "";
            viewLabel = $"3D 预览  |  {motName}  |  Frame {_currentFrame:F1}/{_maxFrame:F0}";
        }
        drawList.AddText(new Vector2(curPos.X + 10f, curPos.Y + 6f), StudioTheme.V4ToAbgr(StudioTheme.TextSecondary), viewLabel);

        // ── Floating Viewport Toolbar (5 buttons, left side) ────────────
        {
            float tbX = curPos.X + 10f;
            float tbY = curPos.Y + 28f;
            float btnSize = 34f;
            float btnGap = 6f;
            uint tbBg = StudioTheme.V4ToAbgr(new Vector4(StudioTheme.CardBg.X, StudioTheme.CardBg.Y, StudioTheme.CardBg.Z, 0.85f));
            uint tbBorder = StudioTheme.V4ToAbgr(StudioTheme.CardBorder);

            string[] tbTooltips = ["移动 (G)", "旋转 (R)", "统一缩放 (S)", "网格开关", "坐标轴开关"];
            for (int tb = 0; tb < tbTooltips.Length; tb++)
            {
                Vector2 bMin = new(tbX, tbY + tb * (btnSize + btnGap));
                Vector2 bMax = new(bMin.X + btnSize, bMin.Y + btnSize);

                bool isActive = (tb == 0 && _gizmoOperation == ImGuizmoOperation.Translate)
                             || (tb == 1 && _gizmoOperation == ImGuizmoOperation.Rotate)
                             || (tb == 2 && _gizmoOperation == ImGuizmoOperation.Scale)
                             || (tb == 3 && _showGrid)
                             || (tb == 4 && _showGizmo);

                uint bgCol = isActive ? StudioTheme.V4ToAbgr(StudioTheme.PrimaryBlue) : tbBg;
                drawList.AddRectFilled(bMin, bMax, bgCol, 6f);
                drawList.AddRect(bMin, bMax, tbBorder, 6f);

                Vector2 c = bMin + new Vector2(btnSize * 0.5f);
                string[] iconNames = ["move", "rotate-3d", "scan", "layers", "crosshair"];
                if (_uiIcons.Draw(drawList, iconNames[tb], bMin + new Vector2(7f), 20f))
                {
                    // PNG textures are loaded once during window creation.
                }
                else if (tb == 0)
                {
                    drawList.AddLine(c + new Vector2(-9f, 0f), c + new Vector2(9f, 0f), 0xFFFFFFFFu, 2f);
                    drawList.AddLine(c + new Vector2(0f, -9f), c + new Vector2(0f, 9f), 0xFFFFFFFFu, 2f);
                }
                else if (tb == 1) drawList.AddCircle(c, 8f, 0xFFFFFFFFu, 20, 2f);
                else if (tb == 2)
                {
                    drawList.AddRect(c - new Vector2(7f), c + new Vector2(7f), 0xFFFFFFFFu, 1f, ImDrawFlags.None, 2f);
                    drawList.AddLine(c + new Vector2(2f, 2f), c + new Vector2(10f, 10f), 0xFFFFFFFFu, 2f);
                }
                else if (tb == 3)
                    for (int g = -1; g <= 1; g++) drawList.AddLine(c + new Vector2(-8f, g * 6f), c + new Vector2(8f, g * 6f), 0xFFFFFFFFu, 1.5f);
                else
                {
                    drawList.AddLine(c, c + new Vector2(9f, 0f), 0xFF5555FFu, 2f);
                    drawList.AddLine(c, c + new Vector2(0f, -9f), 0xFF55FF55u, 2f);
                    drawList.AddLine(c, c + new Vector2(-6f, 6f), 0xFFFF9955u, 2f);
                }

                // Click handling via invisible button
                Hexa.NET.ImGui.ImGui.SetCursorScreenPos(bMin);
                if (Hexa.NET.ImGui.ImGui.InvisibleButton($"##vpTool_{tb}", new Vector2(btnSize, btnSize)))
                {
                    switch (tb)
                    {
                        case 0: _gizmoOperation = ImGuizmoOperation.Translate; break;
                        case 1: _gizmoOperation = ImGuizmoOperation.Rotate; break;
                        case 2: _gizmoOperation = ImGuizmoOperation.Scale; break;
                        case 3: _showGrid = !_showGrid; break;
                        case 4: _showGizmo = !_showGizmo; break;
                    }
                }
                if (Hexa.NET.ImGui.ImGui.IsItemHovered())
                    Hexa.NET.ImGui.ImGui.SetTooltip(tbTooltips[tb]);
            }
        }

        // ── XYZ Axis Tripod (top-right corner) ─────────────────────────
        {
            float tripodX = curPos.X + contentSize.X - 60f;
            float tripodY = curPos.Y + 28f;
            Vector2 tripodOrigin = new(tripodX, tripodY + 28f);
            float axisLen = 22f;

            // Simple screen-space axis representation (fixed orientation hint)
            Matrix4x4 view = Camera.GetViewMatrix();
            // Extract camera-relative axis directions
            Vector3 camRight = new(view.M11, view.M21, view.M31);
            Vector3 camUp = new(view.M12, view.M22, view.M32);

            Vector2 xEnd = tripodOrigin + new Vector2(camRight.X, -camRight.Y) * axisLen;
            Vector2 yEnd = tripodOrigin + new Vector2(camUp.X, -camUp.Y) * axisLen;
            Vector2 zEnd = tripodOrigin + new Vector2(view.M13, -view.M23) * axisLen;

            drawList.AddLine(tripodOrigin, xEnd, 0xFF3838FFu, 2.5f); // X red
            drawList.AddText(xEnd + new Vector2(2, -4), 0xFF3838FFu, "X");
            drawList.AddLine(tripodOrigin, yEnd, 0xFF38F038u, 2.5f); // Y green
            drawList.AddText(yEnd + new Vector2(2, -4), 0xFF38F038u, "Y");
            drawList.AddLine(tripodOrigin, zEnd, 0xFFFF8822u, 2.5f); // Z blue
            drawList.AddText(zEnd + new Vector2(2, -4), 0xFFFF8822u, "Z");
            drawList.AddCircleFilled(tripodOrigin, 3f, 0xFFAAAAAAu);
        }

        if (_previewDebug.ShowPreviewEffects && _selectedMotionIndex >= 0)
        {
            var bindings = GetPreviewBindings(_selectedMotionIndex);
            if (bindings.Count > 0)
            {
                var selected = bindings.FirstOrDefault(b => b.Id == _selectedPreviewBindingId) ?? bindings[0];
                _selectedPreviewBindingId = selected.Id;
                float panelWidth = Math.Min(330f, Math.Max(210f, contentSize.X - 24f));
                float panelHeight = 124f;
                Hexa.NET.ImGui.ImGui.SetCursorScreenPos(new Vector2(
                    curPos.X + Math.Max(8f, contentSize.X - panelWidth - 10f),
                    curPos.Y + Math.Max(96f, contentSize.Y - panelHeight - 10f)));
                Hexa.NET.ImGui.ImGui.BeginChild("ViewportPreviewEffectTiming", new Vector2(panelWidth, panelHeight), ImGuiChildFlags.Borders);
                string fileName = Path.GetFileName(selected.EfxPath);
                if (Hexa.NET.ImGui.ImGui.BeginCombo("##ViewportPreviewEffect", fileName))
                {
                    foreach (var binding in bindings)
                    {
                        if (Hexa.NET.ImGui.ImGui.Selectable($"{Path.GetFileName(binding.EfxPath)}##timing_{binding.Id}",
                                binding.Id == selected.Id))
                            _selectedPreviewBindingId = binding.Id;
                    }
                    Hexa.NET.ImGui.ImGui.EndCombo();
                }
                float start = selected.StartFrame;
                float end = selected.EndFrame;
                Hexa.NET.ImGui.ImGui.SetNextItemWidth(-90f);
                if (Hexa.NET.ImGui.ImGui.DragFloat("出现帧", ref start, 0.25f, 0f, _maxFrame, "%.1f"))
                {
                    selected.StartFrame = Math.Clamp(start, 0f, _maxFrame);
                    if (selected.StartFrame > selected.EndFrame) selected.EndFrame = selected.StartFrame;
                    SavePreviewDebug();
                    _vfxProxy.ClearPreviewSimulators();
                }
                Hexa.NET.ImGui.ImGui.SetNextItemWidth(-90f);
                if (Hexa.NET.ImGui.ImGui.DragFloat("消失帧", ref end, 0.25f, 0f, _maxFrame, "%.1f"))
                {
                    selected.EndFrame = Math.Clamp(end, selected.StartFrame, _maxFrame);
                    SavePreviewDebug();
                    _vfxProxy.ClearPreviewSimulators();
                }
                Hexa.NET.ImGui.ImGui.TextDisabled("仅预览，不写入动作文件");
                Hexa.NET.ImGui.ImGui.EndChild();
            }
        }

        Hexa.NET.ImGui.ImGui.EndChild();
        Hexa.NET.ImGui.ImGui.PopStyleVar(2);
        Hexa.NET.ImGui.ImGui.PopStyleColor();
    }

    private void HandleCameraInput()
    {
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        // Capture the gesture only when the middle button starts inside the viewport.
        // Keep the chosen operation until release, even if the pointer leaves the image.
        if (!Hexa.NET.ImGui.ImGui.IsMouseDown(ImGuiMouseButton.Middle))
            _viewportNavigation = ViewportNavigation.None;
        else if (_viewportNavigation == ViewportNavigation.None &&
                 Hexa.NET.ImGui.ImGui.IsMouseClicked(ImGuiMouseButton.Middle) &&
                 _isViewportHovered && !io.WantTextInput && _transformKind == '\0')
            _viewportNavigation = io.KeyShift ? ViewportNavigation.Pan
                : io.KeyCtrl ? ViewportNavigation.Zoom : ViewportNavigation.Rotate;

        if (io.WantTextInput || _transformKind != '\0')
        {
            _viewportNavigation = ViewportNavigation.None;
            return;
        }
        if (_isViewportHovered)
        {
            if (io.MouseWheel != 0f) Camera.Zoom(io.MouseWheel);
        }
        Vector2 delta = io.MouseDelta;
        if (Hexa.NET.ImGui.ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
            switch (_viewportNavigation)
            {
                case ViewportNavigation.Pan: Camera.Pan(delta.X, delta.Y); break;
                case ViewportNavigation.Zoom: Camera.Zoom(-delta.Y * 0.05f); break;
                case ViewportNavigation.Rotate: Camera.Rotate(delta.X * 0.005f, delta.Y * 0.005f); break;
            }
        if (!_isViewportFocused || io.KeyCtrl && Hexa.NET.ImGui.ImGui.IsKeyDown(ImGuiKey.Space)) return;

        if (Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.Keypad1))
            Camera.SetStandardView(io.KeyCtrl ? OrbitCamera.StandardView.Back : OrbitCamera.StandardView.Front);
        if (Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.Keypad3))
            Camera.SetStandardView(io.KeyCtrl ? OrbitCamera.StandardView.Left : OrbitCamera.StandardView.Right);
        if (Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.Keypad7))
            Camera.SetStandardView(io.KeyCtrl ? OrbitCamera.StandardView.Bottom : OrbitCamera.StandardView.Top);
        if (Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.Keypad5)) Camera.ToggleProjection();
        if (Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.Keypad4)) Camera.Rotate(-MathF.PI / 12f, 0f);
        if (Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.Keypad6)) Camera.Rotate(MathF.PI / 12f, 0f);
        if (Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.Keypad8)) Camera.Rotate(0f, MathF.PI / 12f);
        if (Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.Keypad2)) Camera.Rotate(0f, -MathF.PI / 12f);
        if (Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.Keypad9)) Camera.Rotate(MathF.PI, 0f);
        if (Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.KeypadAdd)) Camera.Zoom(1f);
        if (Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.KeypadSubtract)) Camera.Zoom(-1f);
        if (Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.KeypadDecimal)) FocusViewportSelection();
        if (Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.Home))
            Camera.Focus(new Vector3(0f, 1.15f, 0f), 1.8f, _viewportSize.X / Math.Max(1f, _viewportSize.Y));
    }

    private void HandleViewportTransform(Vector2 viewportMin, Vector2 viewportSize)
    {
        var imgui = Hexa.NET.ImGui.ImGui.GetIO();
        if (_transformKind == '\0')
        {
            if (!_previewDebug.ShowBoneMounts || !_isViewportFocused || imgui.WantTextInput || imgui.KeyCtrl ||
                !IsModelLoaded || _weaponManager.Parts.Count == 0 || _activeGizmoAxis != -1) return;
            char kind = Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.G) ? 'G' :
                Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.R) ? 'R' :
                Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.S) ? 'S' : '\0';
            if (kind == '\0') return;
            _transformKind = kind;
            _transformPart = _weaponManager.Parts[Math.Clamp(_selectedWeaponPartIndex, 0, _weaponManager.Parts.Count - 1)];
            _transformBefore = _transformPart.ToPreset();
            _transformMouseStart = imgui.MousePos;
            _transformAxis = -1;
            _transformPlane = false;
            _transformNumber = string.Empty;
            return;
        }

        if (_transformPart == null || _transformBefore == null || !_weaponManager.Parts.Contains(_transformPart))
        {
            CancelViewportTransform();
            return;
        }
        if (imgui.WantTextInput)
        {
            CancelViewportTransform();
            return;
        }
        if (Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.Escape) ||
            Hexa.NET.ImGui.ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            CancelViewportTransform();
            return;
        }
        if (Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.Enter) ||
            Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.KeypadEnter) ||
            Hexa.NET.ImGui.ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            CommitPresetChange(_transformPart, _transformBefore, _transformPart.ToPreset(),
                $"视口 {_transformKind} 变换武器部件");
            _transformKind = '\0';
            _transformPart = null;
            _transformBefore = null;
            return;
        }

        for (int axis = 0; axis < 3; axis++)
        {
            ImGuiKey key = axis == 0 ? ImGuiKey.X : axis == 1 ? ImGuiKey.Y : ImGuiKey.Z;
            if (!Hexa.NET.ImGui.ImGui.IsKeyPressed(key)) continue;
            if (_transformKind == 'S' || _transformKind == 'R' && imgui.KeyShift)
                continue; // The stored mount scale is uniform; planar rotation is unsupported.
            _transformAxis = axis;
            _transformPlane = imgui.KeyShift;
        }
        for (int digit = 0; digit <= 9; digit++)
        {
            if (Hexa.NET.ImGui.ImGui.IsKeyPressed((ImGuiKey)((int)ImGuiKey.Key0 + digit)) ||
                Hexa.NET.ImGui.ImGui.IsKeyPressed((ImGuiKey)((int)ImGuiKey.Keypad0 + digit)))
                _transformNumber += digit.ToString();
        }
        if (Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.Minus) || Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.KeypadSubtract))
            _transformNumber = _transformNumber.StartsWith('-') ? _transformNumber[1..] : "-" + _transformNumber;
        if (Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.Period) || Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.KeypadDecimal))
            if (!_transformNumber.Contains('.')) _transformNumber += ".";
        if (Hexa.NET.ImGui.ImGui.IsKeyPressed(ImGuiKey.Backspace) && _transformNumber.Length > 0)
            _transformNumber = _transformNumber[..^1];

        _transformPart.ApplyPreset(_transformBefore);
        Vector2 pixels = imgui.MousePos - _transformMouseStart;
        float mouseValue = _transformKind == 'R' ? pixels.X * 0.5f :
            _transformKind == 'S' ? MathF.Exp(pixels.X * 0.005f) : pixels.X * 0.003f;
        if (imgui.KeyShift) mouseValue = _transformKind == 'S' ? 1f + (mouseValue - 1f) * 0.1f : mouseValue * 0.1f;
        float value = float.TryParse(_transformNumber, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float parsed) ? parsed : mouseValue;
        if (imgui.KeyCtrl)
        {
            float step = _transformKind == 'R' ? (imgui.KeyShift ? 1f : 5f) :
                _transformKind == 'S' ? (imgui.KeyShift ? 0.01f : 0.1f) : (imgui.KeyShift ? 0.001f : 0.01f);
            value = MathF.Round(value / step) * step;
        }
        if (_transformKind == 'G')
        {
            Vector3 delta = new(pixels.X * 0.003f, -pixels.Y * 0.003f, 0f);
            if (_transformNumber.Length > 0 && float.TryParse(_transformNumber, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _)) delta = new Vector3(value, 0f, 0f);
            if (_transformAxis >= 0)
            {
                if (_transformPlane) { if (_transformAxis == 0) delta.X = 0f; else if (_transformAxis == 1) delta.Y = 0f; else delta.Z = 0f; }
                else delta = _transformAxis == 0 ? new Vector3(value, 0f, 0f) :
                    _transformAxis == 1 ? new Vector3(0f, value, 0f) : new Vector3(0f, 0f, value);
            }
            _transformPart.PositionOffset += delta;
        }
        else if (_transformKind == 'R')
        {
            Vector3 rotation = _transformPart.RotationEuler;
            if (_transformAxis == 0) rotation.X += value;
            else if (_transformAxis == 2) rotation.Z += value;
            else rotation.Y += value;
            _transformPart.RotationEuler = rotation;
        }
        else _transformPart.Scale = Math.Clamp(_transformPart.Scale * value, 0.1f, 5f);

        string axisName = _transformAxis < 0 ? "" : $" {(char)('X' + _transformAxis)}";
        string hint = $"{_transformKind}{axisName}  {_transformNumber}  |  左键/Enter 确认  右键/Esc 取消";
        Hexa.NET.ImGui.ImGui.GetWindowDrawList().AddText(
            new Vector2(viewportMin.X + 12f, viewportMin.Y + viewportSize.Y - 24f),
            0xFFFFFFFFu, hint);
    }

    private void CancelViewportTransform()
    {
        if (_transformPart != null && _transformBefore != null) _transformPart.ApplyPreset(_transformBefore);
        _transformKind = '\0';
        _transformPart = null;
        _transformBefore = null;
    }

    private void FocusViewportSelection()
    {
        if (IsModelLoaded && _weaponManager.Parts.Count > 0)
        {
            var part = _weaponManager.Parts[Math.Clamp(_selectedWeaponPartIndex, 0, _weaponManager.Parts.Count - 1)];
            Matrix4x4 world = _weaponManager.GetPartWorldMatrix(part, _animator);
            Vector3 center = world.Translation;
            float radius = 0.45f;
            if (part.Mesh != null && part.Mesh.TotalVertices > 0)
            {
                Vector3 min = new(float.PositiveInfinity);
                Vector3 max = new(float.NegativeInfinity);
                Vector3 a = part.Mesh.BoundsMin;
                Vector3 b = part.Mesh.BoundsMax;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 local = new((corner & 1) == 0 ? a.X : b.X,
                        (corner & 2) == 0 ? a.Y : b.Y, (corner & 4) == 0 ? a.Z : b.Z);
                    Vector3 point = Vector3.Transform(local, world);
                    min = Vector3.Min(min, point);
                    max = Vector3.Max(max, point);
                }
                center = (min + max) * 0.5f;
                radius = Math.Max(0.1f, Vector3.Distance(max, min) * 0.5f);
            }
            Camera.Focus(center, radius, _viewportSize.X / Math.Max(1f, _viewportSize.Y));
        }
        else Camera.Focus(new Vector3(0f, 1.15f, 0f), 1.2f, _viewportSize.X / Math.Max(1f, _viewportSize.Y));
    }

    private static bool ProjectWorldToScreen(Vector3 worldPos, Matrix4x4 viewProj, Vector2 viewportMin, Vector2 viewportSize, out Vector2 screenPos)
    {
        Vector4 clip = Vector4.Transform(new Vector4(worldPos, 1.0f), viewProj);
        if (clip.W <= 0.05f)
        {
            screenPos = Vector2.Zero;
            return false;
        }
        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        screenPos = new Vector2(
            viewportMin.X + (ndcX * 0.5f + 0.5f) * viewportSize.X,
            viewportMin.Y + (1.0f - (ndcY * 0.5f + 0.5f)) * viewportSize.Y
        );
        return true;
    }

    private static float DistancePointToSegment2D(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.LengthSquared();
        if (lenSq < 1e-4f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0f, 1f);
        Vector2 proj = a + t * ab;
        return Vector2.Distance(p, proj);
    }

    private void RenderImGuizmoGizmo(Vector2 viewportMin, Vector2 viewportSize)
    {
        if (!_showGizmo) return;

        Matrix4x4 view = Camera.GetViewMatrix();
        Matrix4x4 proj = Camera.GetProjectionMatrix(viewportSize.X / Math.Max(1f, viewportSize.Y));
        Matrix4x4 viewProj = view * proj;
        var drawList = Hexa.NET.ImGui.ImGui.GetWindowDrawList();
        var io = Hexa.NET.ImGui.ImGui.GetIO();

        // Priority 1: Active Weapon Part Mount Calibration 3D Gizmo
        if (_previewDebug.ShowBoneMounts && _showWeaponMountGizmo && IsModelLoaded && _weaponManager.Parts.Count > 0)
        {
            int partIdx = Math.Clamp(_selectedWeaponPartIndex, 0, _weaponManager.Parts.Count - 1);
            var activePart = _weaponManager.Parts[partIdx];

            Matrix4x4 targetBoneWorld = _weaponManager.GetTargetBoneWorldMatrix(activePart, _animator);
            Matrix4x4 userTransform = Matrix4x4.CreateScale(activePart.Scale)
                                    * WeaponAttachmentManager.CreateRotationFromEulerDegrees(activePart.RotationEuler)
                                    * Matrix4x4.CreateTranslation(activePart.PositionOffset);

            Matrix4x4 gizmoMatrix = userTransform * targetBoneWorld;
            Vector3 origin3D = gizmoMatrix.Translation;

            Vector3 axisX = new(gizmoMatrix.M11, gizmoMatrix.M12, gizmoMatrix.M13);
            Vector3 axisY = new(gizmoMatrix.M21, gizmoMatrix.M22, gizmoMatrix.M23);
            Vector3 axisZ = new(gizmoMatrix.M31, gizmoMatrix.M32, gizmoMatrix.M33);
            axisX = axisX.LengthSquared() > 1e-6f ? Vector3.Normalize(axisX) : Vector3.UnitX;
            axisY = axisY.LengthSquared() > 1e-6f ? Vector3.Normalize(axisY) : Vector3.UnitY;
            axisZ = axisZ.LengthSquared() > 1e-6f ? Vector3.Normalize(axisZ) : Vector3.UnitZ;

            float axisLen3D = 0.36f;
            if (!ProjectWorldToScreen(origin3D, viewProj, viewportMin, viewportSize, out Vector2 origin2D))
                return;

            ProjectWorldToScreen(origin3D + axisX * axisLen3D, viewProj, viewportMin, viewportSize, out Vector2 endX2D);
            ProjectWorldToScreen(origin3D + axisY * axisLen3D, viewProj, viewportMin, viewportSize, out Vector2 endY2D);
            ProjectWorldToScreen(origin3D + axisZ * axisLen3D, viewProj, viewportMin, viewportSize, out Vector2 endZ2D);

            Vector2[] ends2D = [endX2D, endY2D, endZ2D];
            uint[] baseColors = [0xFF3838FFu, 0xFF38F038u, 0xFFFF8822u]; // X=Red, Y=Green, Z=Blue (ABGR)
            string[] axisLabels = ["X", "Y", "Z"];

            // Hit-test mouse against 3D gizmo axes
            if (_activeGizmoAxis == -1)
            {
                _hoveredGizmoAxis = -1;
                if (_isViewportHovered)
                {
                    float bestDist = 14.0f;
                    for (int a = 0; a < 3; a++)
                    {
                        float d = DistancePointToSegment2D(io.MousePos, origin2D, ends2D[a]);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            _hoveredGizmoAxis = a;
                        }
                    }

                    if (_hoveredGizmoAxis != -1 && Hexa.NET.ImGui.ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        _activeGizmoAxis = _hoveredGizmoAxis;
                        _gizmoDragStartPreset = activePart.ToPreset();
                    }
                }
            }
            else
            {
                if (Hexa.NET.ImGui.ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    Vector2 axisVec = ends2D[_activeGizmoAxis] - origin2D;
                    Vector2 axisDir = axisVec.LengthSquared() > 1.0f ? Vector2.Normalize(axisVec) : new Vector2(1, 0);
                    float dragPixels = Vector2.Dot(io.MouseDelta, axisDir);

                    if (MathF.Abs(dragPixels) > 0.01f)
                    {
                        if (_gizmoOperation == ImGuizmoOperation.Translate)
                        {
                            Vector3 pos = activePart.PositionOffset;
                            float deltaM = dragPixels * 0.003f;
                            if (_activeGizmoAxis == 0) pos.X = MathF.Round(pos.X + deltaM, 3);
                            else if (_activeGizmoAxis == 1) pos.Y = MathF.Round(pos.Y + deltaM, 3);
                            else if (_activeGizmoAxis == 2) pos.Z = MathF.Round(pos.Z + deltaM, 3);
                            activePart.PositionOffset = pos;
                        }
                        else if (_gizmoOperation == ImGuizmoOperation.Rotate)
                        {
                            Vector3 rot = activePart.RotationEuler;
                            float deltaDeg = dragPixels * 0.6f;
                            if (_activeGizmoAxis == 0) rot.X = MathF.Round(WeaponAttachmentManager.NormalizeAngle180(rot.X + deltaDeg), 1);
                            else if (_activeGizmoAxis == 1) rot.Y = MathF.Round(WeaponAttachmentManager.NormalizeAngle180(rot.Y + deltaDeg), 1);
                            else if (_activeGizmoAxis == 2) rot.Z = MathF.Round(WeaponAttachmentManager.NormalizeAngle180(rot.Z + deltaDeg), 1);
                            activePart.RotationEuler = rot;
                        }
                        else if (_gizmoOperation == ImGuizmoOperation.Scale)
                        {
                            activePart.Scale = MathF.Round(Math.Clamp(activePart.Scale + dragPixels * 0.005f, 0.1f, 5.0f), 2);
                        }
                    }
                }
                else
                {
                    if (_gizmoDragStartPreset != null)
                    {
                        var before = _gizmoDragStartPreset.Clone();
                        var after = activePart.ToPreset();
                        _gizmoDragStartPreset = null;
                        CommitPresetChange(activePart, before, after, "3D 视口拖拽调整武器姿态");
                    }
                    _activeGizmoAxis = -1;
                }
            }

            // Draw rotation guide circle if in Rotate mode
            if (_gizmoOperation == ImGuizmoOperation.Rotate)
            {
                float r = Math.Max(32f, Vector2.Distance(origin2D, endX2D) * 0.85f);
                drawList.AddCircle(origin2D, r, 0x66FFFFFFu, 36, 1.5f);
            }

            // Draw the 3 coordinate axes + tips + labels
            for (int a = 0; a < 3; a++)
            {
                bool isHighlighted = (_hoveredGizmoAxis == a || _activeGizmoAxis == a);
                uint col = isHighlighted ? 0xFF00FFFFu : baseColors[a];
                float thickness = isHighlighted ? 4.5f : 3.0f;

                drawList.AddLine(origin2D, ends2D[a], col, thickness);
                drawList.AddCircleFilled(ends2D[a], isHighlighted ? 7.0f : 5.5f, col);
                drawList.AddText(ends2D[a] + new Vector2(8f, -8f), col, axisLabels[a]);
            }

            drawList.AddCircleFilled(origin2D, 6.0f, 0xFFFFFFFFu);
            drawList.AddCircle(origin2D, 6.5f, 0xFF000000u, 16, 1.5f);
            string gizmoTag = $"Part {activePart.PartIndex} [{activePart.TargetBoneName}]";
            drawList.AddText(origin2D + new Vector2(10f, 8f), 0xFF88FFAAu, gizmoTag);
            return;
        }

        // Priority 2: Selected VFX Slot Joint Indicator
        if (SelectedSlot == null) return;

        string boneName = string.IsNullOrEmpty(SelectedSlot.JointName) ? "R_Weapon" : SelectedSlot.JointName;
        Matrix4x4 boneWorld = _animator.GetBoneWorldMatrix(boneName);
        if (boneWorld == Matrix4x4.Identity && _weaponManager.Parts.Count > 0)
        {
            boneWorld = _weaponManager.GetPartWorldMatrix(_weaponManager.Parts[0], _animator);
        }

        if (ProjectWorldToScreen(boneWorld.Translation, viewProj, viewportMin, viewportSize, out Vector2 slotPos2D))
        {
            drawList.AddCircleFilled(slotPos2D, 6.0f, 0xFF00CCFFu);
            drawList.AddText(slotPos2D + new Vector2(8f, -8f), 0xFF00CCFFu, boneName);
        }
    }

    private void RenderFloatingPlaybackBar(Vector2 viewportMin, Vector2 viewportSize)
    {
        float barWidth = 620f;
        float barHeight = 44f;
        Vector2 barPos = new Vector2(viewportMin.X + (viewportSize.X - barWidth) * 0.5f, viewportMin.Y + viewportSize.Y - barHeight - 12f);

        Hexa.NET.ImGui.ImGui.SetNextWindowPos(barPos);
        Hexa.NET.ImGui.ImGui.SetNextWindowSize(new Vector2(barWidth, barHeight));
        ImGuiWindowFlags overlayFlags = ImGuiWindowFlags.NoTitleBar |
                                       ImGuiWindowFlags.NoResize |
                                       ImGuiWindowFlags.NoMove |
                                       ImGuiWindowFlags.NoScrollbar |
                                       ImGuiWindowFlags.NoSavedSettings;

        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(StudioTheme.CardBg.X, StudioTheme.CardBg.Y, StudioTheme.CardBg.Z, 0.92f));
        Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, StudioTheme.CardRounding);

        if (Hexa.NET.ImGui.ImGui.Begin("FloatingPlaybackBar", overlayFlags))
        {
            // First Frame
            if (Hexa.NET.ImGui.ImGui.Button("|<")) _currentFrame = 0f;
            Hexa.NET.ImGui.ImGui.SameLine();

            // Prev Frame
            if (Hexa.NET.ImGui.ImGui.Button("<")) _currentFrame = Math.Max(0f, _currentFrame - 1f);
            Hexa.NET.ImGui.ImGui.SameLine();

            // Play / Pause
            string playBtn = _isPlaying ? "||" : ">";
            if (Hexa.NET.ImGui.ImGui.Button(playBtn, new Vector2(32, 0)))
            {
                _isPlaying = !_isPlaying;
            }
            Hexa.NET.ImGui.ImGui.SameLine();

            // Next Frame
            if (Hexa.NET.ImGui.ImGui.Button(">")) _currentFrame = Math.Min(_maxFrame, _currentFrame + 1f);
            Hexa.NET.ImGui.ImGui.SameLine();

            // End Frame
            if (Hexa.NET.ImGui.ImGui.Button(">|")) _currentFrame = _maxFrame;
            Hexa.NET.ImGui.ImGui.SameLine();

            // Loop Toggle
            Hexa.NET.ImGui.ImGui.Checkbox("循环", ref _isLoop);
            Hexa.NET.ImGui.ImGui.SameLine();

            // Frame Scrub Slider
            Hexa.NET.ImGui.ImGui.SetNextItemWidth(170);
            Hexa.NET.ImGui.ImGui.SliderFloat("##FrameScrub", ref _currentFrame, 0f, _maxFrame, "%.1f f");
            Hexa.NET.ImGui.ImGui.SameLine();

            // Speed Selector
            Hexa.NET.ImGui.ImGui.SetNextItemWidth(65);
            string speedLabel = $"{_playbackSpeed:0.##}x";
            if (Hexa.NET.ImGui.ImGui.BeginCombo("##Speed", speedLabel, ImGuiComboFlags.NoArrowButton))
            {
                foreach (var s in _speedOptions)
                {
                    if (Hexa.NET.ImGui.ImGui.Selectable($"{s}x", Math.Abs(s - _playbackSpeed) < 0.01f))
                    {
                        _playbackSpeed = s;
                    }
                }
                Hexa.NET.ImGui.ImGui.EndCombo();
            }

            Hexa.NET.ImGui.ImGui.SameLine();
            Hexa.NET.ImGui.ImGui.Checkbox("实时特效 (Live EFX)", ref _previewLiveEfx);
            Hexa.NET.ImGui.ImGui.SameLine();
            Hexa.NET.ImGui.ImGui.Checkbox("网格", ref _showGrid);
            Hexa.NET.ImGui.ImGui.SameLine();
            Hexa.NET.ImGui.ImGui.Checkbox("轴", ref _showGizmo);
        }
        Hexa.NET.ImGui.ImGui.End();

        Hexa.NET.ImGui.ImGui.PopStyleVar();
        Hexa.NET.ImGui.ImGui.PopStyleColor();
    }

    private void RenderMotionInfo_Card(float width, float height)
    {
        StudioTheme.BeginCard("MotionInfoCard", new Vector2(width, height));
        StudioTheme.SectionHeader("", "动作信息");
        MotionInfo? motion = _selectedMotionIndex >= 0 && _selectedMotionIndex < _motionInfos.Count
            ? _motionInfos[_selectedMotionIndex] : null;
        if (motion == null)
        {
            Hexa.NET.ImGui.ImGui.TextDisabled("未加载动作");
        }
        else
        {
            Hexa.NET.ImGui.ImGui.TextColored(StudioTheme.PrimaryBlueBright, motion.Name);
            Hexa.NET.ImGui.ImGui.TextDisabled($"动作编号     {motion.MotId:D3}");
            Hexa.NET.ImGui.ImGui.TextDisabled($"动作名称     {motion.Remark}");
            Hexa.NET.ImGui.ImGui.TextDisabled($"文件路径     {Path.GetFileName(_currentMotlistPath)}");
            Hexa.NET.ImGui.ImGui.TextDisabled($"时长         {motion.FrameCount:F0} 帧");
            int observedFiles = _observedMotionEfx.TryGetValue($"{CurrentWeaponCode}|{motion.MotId}", out var calls)
                ? calls.Select(c => c.EfxFile).Distinct(StringComparer.OrdinalIgnoreCase).Count() : 0;
            Hexa.NET.ImGui.ImGui.TextDisabled($"直接事件 {motion.VfxSlots.Count}  |  实测同动作文件 {observedFiles}  |  武器 PFB 候选 {WeaponPfbCandidateNames.Count}");
        }
        Hexa.NET.ImGui.ImGui.Spacing();
        if (Hexa.NET.ImGui.ImGui.Button(_isPlaying ? "暂停" : "播放")) _isPlaying = !_isPlaying;
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("导出")) SaveAs();
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("高级编辑")) _showAdvancedInspector = true;
        StudioTheme.EndCard();
    }

    private void RenderHierarchy_Card(float width, float height)
    {
        StudioTheme.BeginCard("HierarchyCard", new Vector2(width, height));
        StudioTheme.SectionHeader("", "层级结构");
        if (Hexa.NET.ImGui.ImGui.Button("骨骼")) _showBoneHierarchy = true;
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("EFX")) _showBoneHierarchy = false;
        if (_showBoneHierarchy)
            StudioTheme.SearchInput("##BoneSearch", ref _boneSearchFilter, "搜索骨骼...");
        else
            StudioTheme.SearchInput("##EfxSearch", ref _efxSearchFilter, "搜索 EFX 节点...");
        Hexa.NET.ImGui.ImGui.BeginChild("HierarchyScroll", new Vector2(-1f, -1f));
        if (_showBoneHierarchy)
        {
            if (_hunterModel == null) Hexa.NET.ImGui.ImGui.TextDisabled("未加载骨骼模型");
            else
            {
                for (int i = 0; i < _hunterModel.Bones.Count; i++)
                {
                    var bone = _hunterModel.Bones[i];
                    if (string.IsNullOrEmpty(_boneSearchFilter) && bone.ParentIndex >= 0 &&
                        bone.ParentIndex < _hunterModel.Bones.Count && bone.ParentIndex != i) continue;
                    if (!string.IsNullOrEmpty(_boneSearchFilter) &&
                        !bone.Name.Contains(_boneSearchFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrEmpty(_boneSearchFilter)) RenderBoneNode(i, 0);
                    else if (Hexa.NET.ImGui.ImGui.Selectable($"{bone.Name}##bone_{i}", _selectedBoneIndex == i))
                        _selectedBoneIndex = i;
                }
            }
        }
        else
        {
            RenderWeaponProviderCandidates();
            if (_currentEfx != null) RenderEfxHierarchyTree();
            else Hexa.NET.ImGui.ImGui.TextDisabled("当前动作没有已加载的 EFX");
        }
        Hexa.NET.ImGui.ImGui.EndChild();
        StudioTheme.EndCard();
    }

    private void RenderWeaponProviderCandidates()
    {
        RenderObservedMotionEfx();
        var groups = _pfbService.WeaponResourceCandidates
            .GroupBy(c => c.LocalEfxPath ?? c.RawResourcePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => Path.GetFileName(g.Key), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (groups.Length == 0) return;
        if (!Hexa.NET.ImGui.ImGui.TreeNodeEx($"武器 PFB 资源 ({groups.Length}) · 状态触发见悬停说明##WeaponCandidatesTree",
                ImGuiTreeNodeFlags.DefaultOpen)) return;
        foreach (var group in groups)
        {
            var candidate = group.First();
            string fileName = Path.GetFileName(candidate.LocalEfxPath ?? candidate.RawResourcePath);
            string name = fileName;
            if (!string.IsNullOrEmpty(_efxSearchFilter) &&
                !name.Contains(_efxSearchFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsEquipmentSpecificWeaponEffect(name)) name += " [装备专属]";
            _weaponFileEvidence.TryGetValue($"{CurrentWeaponCode}|{fileName}", out var fileEvidence);
            bool direct = candidate.LocalEfxPath != null &&
                _motionCalledEfxPaths.Contains(candidate.LocalEfxPath, StringComparer.OrdinalIgnoreCase);
            var triggers = group.Select(c => c.Source.StateTriggerName)
                .Where(name => !string.IsNullOrEmpty(name)).Distinct().ToArray();
            if (candidate.LocalEfxPath != null)
            {
                string label = direct ? $"{name} [直接调用]" :
                    triggers.Length > 0 ? $"{name} [状态触发 {triggers.Length}]" : name;
                if (fileEvidence.ResourceRequests > 0) label += " [会话已请求]";
                else if (fileEvidence.StateTriggers > 0) label += " [状态已实测]";
                else if (fileEvidence.MotionCallbacks > 0) label += " [动作回调已实测]";
                if (Hexa.NET.ImGui.ImGui.Selectable($"{label}##wpPfb_{group.Key}",
                        string.Equals(_currentEfxPath, candidate.LocalEfxPath, StringComparison.OrdinalIgnoreCase)))
                    SelectCalledEfx(candidate.LocalEfxPath);
            }
            else Hexa.NET.ImGui.ImGui.TextDisabled($"{name}（文件缺失/歧义）");
            if (Hexa.NET.ImGui.ImGui.IsItemHovered())
            {
                Hexa.NET.ImGui.ImGui.BeginTooltip();
                Hexa.NET.ImGui.ImGui.TextWrapped(direct ? "已有动作轨道直接引用" :
                    triggers.Length > 0 ? "PFB Fixed ID 已与游戏状态特效 ID 精确匹配；当前动作是否进入该状态仍需验证" :
                    "当前武器 PFB 资源；动作触发条件待核定，不自动播放");
                if (fileEvidence.ResourceRequests > 0 || fileEvidence.StateTriggers > 0 ||
                    fileEvidence.MotionCallbacks > 0)
                    Hexa.NET.ImGui.ImGui.TextWrapped(
                        $"该武器会话：精确路径请求 {fileEvidence.ResourceRequests} 次，状态 {fileEvidence.StateTriggers} 项，动作创建回调 {fileEvidence.MotionCallbacks} 次。具体所选动作请看「同动作实时调用」。");
                foreach (var source in group.Take(8))
                    Hexa.NET.ImGui.ImGui.TextWrapped($"{source.Source.SourcePfbName}/{source.Source.ContainerGameObjectName} FixedID={source.Source.ElementIdFixed} 状态={source.Source.StateTriggerName ?? "未识别"} ID={source.Source.StateTriggerId?.ToString() ?? "-"}: {source.RawResourcePath}");
                Hexa.NET.ImGui.ImGui.EndTooltip();
            }
        }
        Hexa.NET.ImGui.ImGui.TreePop();
    }

    private void RenderObservedMotionEfx()
    {
        if (_selectedMotionIndex < 0 || _selectedMotionIndex >= _motionInfos.Count) return;
        string key = $"{CurrentWeaponCode}|{_motionInfos[_selectedMotionIndex].MotId}";
        if (!_observedMotionEfx.TryGetValue(key, out var calls) || calls.Count == 0) return;
        if (!Hexa.NET.ImGui.ImGui.TreeNodeEx(
                $"同动作实时调用 ({calls.Count})##ObservedMotionEfx", ImGuiTreeNodeFlags.DefaultOpen)) return;
        Hexa.NET.ImGui.ImGui.TextWrapped(
            "游戏调用状态或请求 EFX 路径时，当前 MOT 编号在事件前后保持一致。该记录用于定位文件，不能单独证明屏幕粒子已播放。");
        foreach (var call in calls.OrderBy(c => c.EfxFile, StringComparer.OrdinalIgnoreCase))
        {
            var candidate = _pfbService.WeaponResourceCandidates.FirstOrDefault(c =>
                c.LocalEfxPath != null &&
                (Path.GetFileName(c.LocalEfxPath).Equals(call.EfxFile, StringComparison.OrdinalIgnoreCase) ||
                 Path.GetFileName(c.LocalEfxPath).StartsWith(call.EfxFile + ".", StringComparison.OrdinalIgnoreCase)));
            bool equipmentSpecific = IsEquipmentSpecificWeaponEffect(call.EfxFile);
            string label = $"{call.EfxFile}  [{call.Source} {call.Count} 次]" +
                (equipmentSpecific ? " [当前装备专属]" : "");
            if (candidate?.LocalEfxPath is { } localPath)
            {
                if (Hexa.NET.ImGui.ImGui.Selectable($"{label}##observed_{call.Source}_{call.EfxFile}",
                        string.Equals(_currentEfxPath, localPath, StringComparison.OrdinalIgnoreCase)))
                    SelectCalledEfx(localPath);
            }
            else Hexa.NET.ImGui.ImGui.TextDisabled($"{label}（本地文件未定位）");
            if (Hexa.NET.ImGui.ImGui.IsItemHovered())
                Hexa.NET.ImGui.ImGui.SetTooltip(equipmentSpecific
                    ? $"首次记录：{call.FirstSeen}\n900 及以上序号为武器套系专属；本记录只适用于实测装备。"
                    : $"首次记录：{call.FirstSeen}");
        }
        if (Hexa.NET.ImGui.ImGui.Button("刷新动作调用记录")) LoadObservedMotionEfx();
        Hexa.NET.ImGui.ImGui.TreePop();
    }

    private static bool IsEquipmentSpecificWeaponEffect(string fileName) =>
        System.Text.RegularExpressions.Regex.IsMatch(fileName, @"_9\d{2}(?:_|\.efx)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private void RenderBoneNode(int index, int depth)
    {
        if (_hunterModel == null || depth > 64) return;
        var bone = _hunterModel.Bones[index];
        bool hasChildren = _hunterModel.Bones.Any(b => b.ParentIndex == index && b.Index != index);
        ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow |
            (_selectedBoneIndex == index ? ImGuiTreeNodeFlags.Selected : 0) |
            (hasChildren ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.Leaf);
        bool open = Hexa.NET.ImGui.ImGui.TreeNodeEx($"{bone.Name}##bone_{index}", flags);
        if (Hexa.NET.ImGui.ImGui.IsItemClicked() && !Hexa.NET.ImGui.ImGui.IsItemToggledOpen())
            _selectedBoneIndex = index;
        if (!open) return;
        if (hasChildren)
            for (int i = 0; i < _hunterModel.Bones.Count; i++)
                if (i != index && _hunterModel.Bones[i].ParentIndex == index) RenderBoneNode(i, depth + 1);
        Hexa.NET.ImGui.ImGui.TreePop();
    }

    private void RenderNodeInspector_Card(float width, float height)
    {
        StudioTheme.BeginCard("NodeInspectorCard", new Vector2(width, height));
        string title = _showBoneHierarchy && _hunterModel != null && _selectedBoneIndex >= 0 && _selectedBoneIndex < _hunterModel.Bones.Count
            ? _hunterModel.Bones[_selectedBoneIndex].Name
            : _selectedEfxEntry?.name ?? _selectedEfxAttribute?.type.ToString() ?? "未选中节点";
        StudioTheme.SectionHeader("", $"选中节点: {title}");
        Hexa.NET.ImGui.ImGui.BeginChild("NodeInspectorScroll", new Vector2(-1f, -1f));
        if (_showBoneHierarchy)
        {
            if (_hunterModel != null && _selectedBoneIndex >= 0 && _selectedBoneIndex < _hunterModel.Bones.Count)
            {
                var bone = _hunterModel.Bones[_selectedBoneIndex];
                Hexa.NET.ImGui.ImGui.Text($"名称: {bone.Name}");
                Hexa.NET.ImGui.ImGui.Text($"索引: {bone.Index}");
                Hexa.NET.ImGui.ImGui.Text($"父骨骼: {bone.ParentIndex}");
                Hexa.NET.ImGui.ImGui.TextDisabled("动画骨骼为只读数据");
            }
            else Hexa.NET.ImGui.ImGui.TextDisabled("请选择骨骼");
        }
        else if (_currentEfx != null) RenderUniversalInspector();
        else if (SelectedSlot != null)
        {
            Hexa.NET.ImGui.ImGui.Text($"特效槽位 #{_selectedSlotIndex}");
            Hexa.NET.ImGui.ImGui.Text($"挂载骨骼: {SelectedSlot.JointName}");
            Hexa.NET.ImGui.ImGui.Text($"帧范围: {SelectedSlot.StartFrame:F0}–{SelectedSlot.EndFrame:F0}");
            if (Hexa.NET.ImGui.ImGui.Button("编辑槽位")) _showAdvancedInspector = true;
        }
        else Hexa.NET.ImGui.ImGui.TextDisabled("请选择 EFX 节点或特效槽位");
        Hexa.NET.ImGui.ImGui.EndChild();
        StudioTheme.EndCard();
    }

    private void RenderRightInspector_Card(float width, float height)
    {
        StudioTheme.BeginCard("RightInspectorCard", new Vector2(width, height));

        // ── Right Panel Section Header ──────────────────────────────────
        {
            string motLabel = (_currentMot != null && _selectedMotionIndex >= 0 && _selectedMotionIndex < _motionInfos.Count)
                ? $"{_motionInfos[_selectedMotionIndex].Remark} ({_motionInfos[_selectedMotionIndex].Name})"
                : "未加载动作";
            StudioTheme.SectionHeader("|", $"槽位与 EFX  |  {motLabel}");
        }

        // Requirement: When playing a motion with NO VFX slots (_currentSlots.Count == 0),
        // hide the right-hand EFX module and only show a prompt to add a VFX slot.
        if (_currentSlots.Count == 0)
        {
            Hexa.NET.ImGui.ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "[动作特效槽位] 当前解析规则未识别到槽位 (0 Slots)");
            Hexa.NET.ImGui.ImGui.Separator();
            Hexa.NET.ImGui.ImGui.Spacing();

            if (!IsModelLoaded || _currentMot == null)
            {
                Hexa.NET.ImGui.ImGui.TextDisabled("（请先在左上角选择武器素体并选择动作）");
            }
            else
            {
                Hexa.NET.ImGui.ImGui.TextWrapped($"当前动作 ({_currentMot.Name}) 未在已识别的轨道中发现特效事件。其它轨道与条件调用仍需解析，不能据此判定动作没有特效。");
                Hexa.NET.ImGui.ImGui.Spacing();
                Hexa.NET.ImGui.ImGui.TextDisabled("点击下方按钮为该动作添加特效槽位后，将自动显示并激活右侧 EFX 特效模块：");
                Hexa.NET.ImGui.ImGui.Spacing();

                Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.16f, 0.55f, 0.30f, 1.0f));
                Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.22f, 0.68f, 0.38f, 1.0f));
                if (Hexa.NET.ImGui.ImGui.Button("+ 为当前动作添加特效槽位 (Add VFX Slot)", new Vector2(-1, 32)))
                {
                    AddNewSlot();
                }
                Hexa.NET.ImGui.ImGui.PopStyleColor(2);
            }

            StudioTheme.EndCard();
            return;
        }

        // Upper Section: Motion VFX Slot Properties
        Hexa.NET.ImGui.ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), $"[动作特效槽位] 属性与控制 (共 {_currentSlots.Count} 个槽位)");
        Hexa.NET.ImGui.ImGui.Separator();

        if (SelectedSlot != null)
        {
            var s = SelectedSlot;
            Hexa.NET.ImGui.ImGui.Text($"当前选中: Slot #{_selectedSlotIndex}");
            if (s.NativeMultiKeyTrack)
            {
                Hexa.NET.ImGui.ImGui.TextWrapped("原生轨道有多个关键帧；事件可预览，编辑需保留原始键结构，当前只读。");
                Hexa.NET.ImGui.ImGui.BeginDisabled();
            }

            // PFB EPV Element Preset Dropdown
            if (_pfbService.AllElements.Count > 0)
            {
                string pfbPreview = $"Container {s.ContainerId} - Element {s.EffectId}";
                if (Hexa.NET.ImGui.ImGui.BeginCombo("PFB 动作特效映射", pfbPreview))
                {
                    for (int pIdx = 0; pIdx < _pfbService.AllElements.Count; pIdx++)
                    {
                        var pe = _pfbService.AllElements[pIdx];
                        if (pe.ResourceEfxPaths.Count == 0) continue;
                        string efxShort = Path.GetFileName(pe.ResourceEfxPaths[0]);
                        string partTag = pe.RelationChildId == 1021 ? "副手Part1" : (pe.RelationChildId == 1020 ? "主武Part0" : $"Rel:{pe.RelationChildId}");
                        string itemText = $"[C:{pe.ContainerId} E:{pe.ElementId}] -> {efxShort} ({partTag}, {pe.JointName})##pfb_{pIdx}";
                        bool isCur = (pe.ContainerId == s.ContainerId && pe.ElementId.ToString() == s.EffectId);
                        if (Hexa.NET.ImGui.ImGui.Selectable(itemText, isCur))
                        {
                            s.ContainerId = pe.ContainerId >= 0 ? pe.ContainerId : s.ContainerId;
                            s.ElementId = pe.ElementId;
                            s.EffectId = pe.ElementId.ToString();
                            if (!string.IsNullOrEmpty(pe.JointName))
                                s.JointName = pe.JointName;
                            _vfxProxy.ClearSimulators();
                            RefreshCurrentMotionCalledEfx();
                        }
                    }
                    Hexa.NET.ImGui.ImGui.EndCombo();
                }
            }

            string effId = s.EffectId;
            if (Hexa.NET.ImGui.ImGui.InputText("特效编号 (EffectId)", ref effId, 32))
            {
                s.EffectId = effId;
                if (int.TryParse(effId, out int parsedElem)) s.ElementId = parsedElem;
                _vfxProxy.ClearSimulators();
                RefreshCurrentMotionCalledEfx();
            }

            int contId = s.ContainerId;
            if (Hexa.NET.ImGui.ImGui.InputInt("数据容器 (ContainerId)", ref contId))
            {
                s.ContainerId = contId;
                _vfxProxy.ClearSimulators();
                RefreshCurrentMotionCalledEfx();
            }

            if (_slotResolvedEfxMap.TryGetValue(s.SlotId, out var slotRes) && slotRes.EfxList.Count > 0)
            {
                string calledNames = string.Join(", ", slotRes.EfxList.Select(x => Path.GetFileName(x.Path)));
                Hexa.NET.ImGui.ImGui.TextColored(new Vector4(0.45f, 0.95f, 0.65f, 1.0f), $"槽位调用特效: {calledNames}");
            }
            if (_slotResolutionMessages.TryGetValue(s.SlotId, out string? resolutionStatus))
                Hexa.NET.ImGui.ImGui.TextWrapped($"解析状态: {resolutionStatus}");
            if (_slotResolutionDetails.TryGetValue(s.SlotId, out var selectedResolution) &&
                Hexa.NET.ImGui.ImGui.TreeNodeEx($"来源与资源 ({selectedResolution.Candidates.Count})##Source_{s.SlotId}"))
            {
                Hexa.NET.ImGui.ImGui.TextDisabled($"Clip {s.ClipIndex} / Track {s.TrackIndex} / Key {s.SourceKeyIndex} @ {s.StartFrame:F0}f");
                foreach (var candidate in selectedResolution.Candidates)
                {
                    Hexa.NET.ImGui.ImGui.TextWrapped($"Provider: {candidate.SourcePfbPath}");
                    Hexa.NET.ImGui.ImGui.TextDisabled($"对象: {candidate.ContainerGameObjectName} / C:{candidate.ContainerId} E:{candidate.ElementId}");
                    foreach (string resource in candidate.ResourceEfxPaths)
                        Hexa.NET.ImGui.ImGui.TextWrapped($"资源: {resource}");
                }
                foreach (string resolvedPath in selectedResolution.LocalEfxPaths)
                    Hexa.NET.ImGui.ImGui.TextWrapped($"本地文件: {resolvedPath}");
                Hexa.NET.ImGui.ImGui.TreePop();
            }

            float sf = s.StartFrame;
            if (Hexa.NET.ImGui.ImGui.SliderFloat("起始帧 (StartFrame)", ref sf, 0f, _maxFrame, "%.1f f"))
            {
                s.StartFrame = Math.Min(sf, s.EndFrame);
            }

            float ef = s.EndFrame;
            if (Hexa.NET.ImGui.ImGui.SliderFloat("结束帧 (EndFrame)", ref ef, 0f, _maxFrame, "%.1f f"))
            {
                s.EndFrame = Math.Max(ef, s.StartFrame);
            }

            // Bone dropdown combo + custom input
            string currentJoint = s.JointName ?? "R_Weapon";
            if (Hexa.NET.ImGui.ImGui.BeginCombo("挂载骨骼预设", currentJoint))
            {
                for (int b = 0; b < _commonBones.Length; b++)
                {
                    bool isSelected = (currentJoint == _commonBones[b]);
                    if (Hexa.NET.ImGui.ImGui.Selectable(_commonBones[b], isSelected))
                    {
                        string oldJoint = s.JointName ?? "R_Weapon";
                        string newJoint = _commonBones[b];
                        UndoRedo.Execute(new RelayUndoableCommand($"修改骨骼: {newJoint}",
                            () => s.JointName = newJoint,
                            () => s.JointName = oldJoint));
                    }
                }
                Hexa.NET.ImGui.ImGui.EndCombo();
            }

            string joint = s.JointName ?? "";
            if (Hexa.NET.ImGui.ImGui.InputText("自定义骨骼名 (JointName)", ref joint, 64))
            {
                s.JointName = joint;
            }

            uint hash = MurMur3HashUtils.GetHash(s.JointName ?? "");
            Hexa.NET.ImGui.ImGui.TextDisabled($"Murmur3 BoneHash: 0x{hash:X8}");

            if (s.NativeMultiKeyTrack) Hexa.NET.ImGui.ImGui.EndDisabled();

            Hexa.NET.ImGui.ImGui.Spacing();
            if (Hexa.NET.ImGui.ImGui.Button("复制此槽位##Slot")) DuplicateSelectedSlot();
            Hexa.NET.ImGui.ImGui.SameLine();
            if (Hexa.NET.ImGui.ImGui.Button("删除此槽位##Slot")) DeleteSelectedSlot();
            Hexa.NET.ImGui.ImGui.SameLine();
            if (Hexa.NET.ImGui.ImGui.Button("+ 新增槽位##Slot")) AddNewSlot();
        }
        else
        {
            Hexa.NET.ImGui.ImGui.TextDisabled("（请在时间轴中点击选中特效槽位）");
            if (Hexa.NET.ImGui.ImGui.Button("+ 新增动作特效槽位")) AddNewSlot();
        }

        Hexa.NET.ImGui.ImGui.Spacing();
        Hexa.NET.ImGui.ImGui.Separator();
        Hexa.NET.ImGui.ImGui.Spacing();

        // Lower Section: EFX Hierarchy & Parameters (Filtered strictly to current motion's called EFX files)
        Hexa.NET.ImGui.ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f),
            $"[EFX] 本地定位文件 {_motionCalledEfxPaths.Count} | 调用事件 {_currentSlots.Count} | 未解决槽位 {_slotResolutionMessages.Count(p => p.Value != "已解析")}");
        Hexa.NET.ImGui.ImGui.Separator();

        if (_slotResolutionMessages.Any(p => p.Value != "已解析") &&
            Hexa.NET.ImGui.ImGui.TreeNodeEx($"未解决调用 ({_slotResolutionMessages.Count(p => p.Value != "已解析")})##UnresolvedCalls"))
        {
            foreach (var slot in _currentSlots.Where(s => _slotResolutionMessages.TryGetValue(s.SlotId, out var status) && status != "已解析"))
            {
                Hexa.NET.ImGui.ImGui.TextWrapped($"{slot.EffectId} @ {slot.StartFrame:F0}f: {_slotResolutionMessages[slot.SlotId]}");
                if (_slotResolutionDetails.TryGetValue(slot.SlotId, out var detail))
                    foreach (var candidate in detail.Candidates)
                        Hexa.NET.ImGui.ImGui.TextDisabled($"{candidate.SourcePfbPath}: {string.Join(" | ", candidate.ResourceEfxPaths)}");
            }
            Hexa.NET.ImGui.ImGui.TreePop();
        }

        var weaponCandidates = _pfbService.WeaponResourceCandidates
            .GroupBy(c => c.LocalEfxPath ?? c.RawResourcePath, StringComparer.OrdinalIgnoreCase)
            .Where(group => !_motionCalledEfxPaths.Contains(group.Key, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (weaponCandidates.Length > 0 && Hexa.NET.ImGui.ImGui.TreeNodeEx(
                $"武器 PFB 资源 ({weaponCandidates.Length}) · 状态触发可查看##WeaponPfbCandidates",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            Hexa.NET.ImGui.ImGui.TextWrapped("已识别 PFB Fixed ID 对应的游戏状态触发名；当前动作是否触发该状态仍需验证。点击本地文件可查看与编辑。");
            foreach (var group in weaponCandidates)
            {
                var sample = group.First();
                string label = Path.GetFileName(sample.LocalEfxPath ?? sample.RawResourcePath);
                int triggerCount = group.Select(c => c.Source.StateTriggerName)
                    .Where(name => !string.IsNullOrEmpty(name)).Distinct().Count();
                if (triggerCount > 0) label += $" [状态触发 {triggerCount}]";
                if (sample.LocalEfxPath != null)
                {
                    if (Hexa.NET.ImGui.ImGui.Selectable($"{label}##candidate_{group.Key}"))
                        SelectCalledEfx(sample.LocalEfxPath);
                }
                else Hexa.NET.ImGui.ImGui.TextDisabled($"{label}（本地文件缺失或歧义）");
                if (Hexa.NET.ImGui.ImGui.IsItemHovered())
                {
                    Hexa.NET.ImGui.ImGui.BeginTooltip();
                    foreach (var source in group.Take(8))
                        Hexa.NET.ImGui.ImGui.TextWrapped($"{source.Source.SourcePfbName} / {source.Source.ContainerGameObjectName} / FixedID {source.Source.ElementIdFixed} / 状态 {source.Source.StateTriggerName ?? "未识别"} ({source.Source.StateTriggerId?.ToString() ?? "-"}): {source.RawResourcePath}");
                    Hexa.NET.ImGui.ImGui.EndTooltip();
                }
            }
            Hexa.NET.ImGui.ImGui.TreePop();
        }

        if (_motionCalledEfxPaths.Count > 0)
        {
            int curEfxIdx = _motionCalledEfxPaths.FindIndex(p => string.Equals(p, _currentEfxPath, StringComparison.OrdinalIgnoreCase));
            string curLabel = (curEfxIdx >= 0 && curEfxIdx < _motionCalledEfxLabels.Count)
                ? _motionCalledEfxLabels[curEfxIdx]
                : Path.GetFileName(_currentEfxPath);

            Hexa.NET.ImGui.ImGui.SetNextItemWidth(-1);
            if (Hexa.NET.ImGui.ImGui.BeginCombo("##MotionCalledEfxCombo", curLabel))
            {
                for (int idx = 0; idx < _motionCalledEfxPaths.Count; idx++)
                {
                    bool isSel = (idx == curEfxIdx);
                    if (Hexa.NET.ImGui.ImGui.Selectable(_motionCalledEfxLabels[idx], isSel))
                    {
                        SelectCalledEfx(_motionCalledEfxPaths[idx]);
                    }
                }
                Hexa.NET.ImGui.ImGui.EndCombo();
            }

            if (_vfxProxy.AssetCache != null)
            {
                var ac = _vfxProxy.AssetCache;
                Hexa.NET.ImGui.ImGui.TextDisabled(
                    $"已联动特效资源: PFB({ _pfbService.AllElements.Count }) | UVS({ ac.UvsCount }) | TEX({ ac.TexCount }) | MESH({ ac.MeshCount }) | MDF2({ ac.MdfCount })");
            }
        }

        if (_currentEfx != null)
        {
            Hexa.NET.ImGui.ImGui.SetNextItemWidth(-1);
            Hexa.NET.ImGui.ImGui.InputTextWithHint("##EfxParamFilter", "属性搜索过滤 (Filter)...", ref _efxSearchFilter, 32);
            Hexa.NET.ImGui.ImGui.Spacing();

            // 1. EFX Hierarchy Tree
            Hexa.NET.ImGui.ImGui.BeginChild("EfxHierarchyTree", new Vector2(-1, 200), ImGuiChildFlags.Borders);
            RenderEfxHierarchyTree();
            Hexa.NET.ImGui.ImGui.EndChild();

            // Action Toolbar under Tree
            if (Hexa.NET.ImGui.ImGui.Button("+ 新增 Entry")) AddNewEfxEntry();
            Hexa.NET.ImGui.ImGui.SameLine();
            if (Hexa.NET.ImGui.ImGui.Button("复制选中 Entry") && _selectedEfxEntry is EFXEntry selEff)
            {
                var clone = _efxService.DeepCloneEntry(_currentEfx, selEff);
                _selectedEfxEntry = clone;
                _selectedEfxAttribute = null;
                SetStatus($"已克隆 Entry: {clone.name}");
            }
            Hexa.NET.ImGui.ImGui.SameLine();
            if (Hexa.NET.ImGui.ImGui.Button("删除选中 Entry") && _selectedEfxEntry != null)
            {
                int idx = _currentEfx.Entries.IndexOf((EFXEntry)_selectedEfxEntry);
                if (idx >= 0)
                {
                    _efxService.RemoveEntry(_currentEfx, idx);
                    _selectedEfxEntry = _currentEfx.Entries.FirstOrDefault();
                    _selectedEfxAttribute = null;
                    SetStatus("已删除选中 Entry");
                }
            }
            Hexa.NET.ImGui.ImGui.SameLine();
            if (Hexa.NET.ImGui.ImGui.Button("同步 Groups"))
            {
                _efxService.SynchronizeEffectGroups(_currentEfx);
                SetStatus("已同步 EffectGroups 映射！");
            }

            Hexa.NET.ImGui.ImGui.Spacing();
            Hexa.NET.ImGui.ImGui.Separator();
            Hexa.NET.ImGui.ImGui.Spacing();

            // 2. Universal Inspector Child
            Hexa.NET.ImGui.ImGui.BeginChild("EfxInspectorChild", new Vector2(-1, -1), ImGuiChildFlags.Borders);
            RenderUniversalInspector();
            Hexa.NET.ImGui.ImGui.EndChild();
        }
        else
        {
            Hexa.NET.ImGui.ImGui.TextDisabled("（未加载 EFX 文件）");
        }
        // ── Bottom status: Selected node info (green accent) ────────────
        Hexa.NET.ImGui.ImGui.Spacing();
        string selectedNodeName = _selectedEfxEntry != null
            ? (_selectedEfxEntry is EFXEntry se ? se.name ?? "Entry" : "Entry")
            : (_selectedEfxAttribute != null ? _selectedEfxAttribute.type.ToString() : "Root");
        Hexa.NET.ImGui.ImGui.TextColored(StudioTheme.AccentGreen, $"选中节点: {selectedNodeName}");

        StudioTheme.EndCard();
    }

    private void RenderEfxHierarchyTree()
    {
        if (_currentEfx == null) return;

        bool rootOpen = Hexa.NET.ImGui.ImGui.TreeNodeEx($"EFX: {Path.GetFileName(_currentEfxPath)} (v{_currentEfx.Header.Version})", ImGuiTreeNodeFlags.DefaultOpen);
        if (rootOpen)
        {
            // EffectGroups
            if (Hexa.NET.ImGui.ImGui.TreeNodeEx($"EffectGroups ({_currentEfx.EffectGroups.Count})"))
            {
                for (int g = 0; g < _currentEfx.EffectGroups.Count; g++)
                {
                    var group = _currentEfx.EffectGroups[g];
                    string members = (group.efxEntryIndexes != null && group.efxEntryIndexes.Length > 0)
                        ? string.Join(", ", group.efxEntryIndexes)
                        : "空";
                    Hexa.NET.ImGui.ImGui.BulletText($"[{group.groupName}] -> Entries: [{members}]");
                }
                Hexa.NET.ImGui.ImGui.TreePop();
            }

            // Actions
            if (Hexa.NET.ImGui.ImGui.TreeNodeEx($"Actions ({_currentEfx.Actions.Count})"))
            {
                for (int a = 0; a < _currentEfx.Actions.Count; a++)
                {
                    var act = _currentEfx.Actions[a];
                    string aName = act.name ?? $"Action_{a}";
                    if (Hexa.NET.ImGui.ImGui.TreeNodeEx($"Action #{a}: {aName} ({act.Attributes.Count} Attrs)##act_{a}"))
                    {
                        for (int at = 0; at < act.Attributes.Count; at++)
                        {
                            var attr = act.Attributes[at];
                            bool isAttrSel = (_selectedEfxAttribute == attr);
                            if (Hexa.NET.ImGui.ImGui.Selectable($"  [{attr.UniqueID}] {attr.type}##act_attr_{a}_{at}", isAttrSel))
                            {
                                _selectedEfxAttribute = attr;
                                _selectedEfxEntry = null;
                            }
                        }
                        Hexa.NET.ImGui.ImGui.TreePop();
                    }
                }
                Hexa.NET.ImGui.ImGui.TreePop();
            }

            // Entries
            if (Hexa.NET.ImGui.ImGui.TreeNodeEx($"Entries ({_currentEfx.Entries.Count})", ImGuiTreeNodeFlags.DefaultOpen))
            {
                for (int i = 0; i < _currentEfx.Entries.Count; i++)
                {
                    var entry = _currentEfx.Entries[i];
                    string name = entry.name ?? $"Entry_{i}";

                    // Filter check
                    if (!string.IsNullOrEmpty(_efxSearchFilter))
                    {
                        bool matchName = name.Contains(_efxSearchFilter, StringComparison.OrdinalIgnoreCase);
                        bool matchAttr = entry.Attributes.Any(at => at.type.ToString().Contains(_efxSearchFilter, StringComparison.OrdinalIgnoreCase));
                        if (!matchName && !matchAttr) continue;
                    }

                    bool isEntrySelected = (_selectedEfxEntry == entry && _selectedEfxAttribute == null);
                    ImGuiTreeNodeFlags nodeFlags = ImGuiTreeNodeFlags.OpenOnArrow | (isEntrySelected ? ImGuiTreeNodeFlags.Selected : 0);
                    bool entryOpen = Hexa.NET.ImGui.ImGui.TreeNodeEx($"Entry #{i}: {name} ({entry.Attributes.Count} Attrs)##tree_entry_{i}", nodeFlags);

                    if (Hexa.NET.ImGui.ImGui.IsItemClicked() && !Hexa.NET.ImGui.ImGui.IsItemToggledOpen())
                    {
                        _selectedEfxEntry = entry;
                        _selectedEfxAttribute = null;
                    }

                    if (entryOpen)
                    {
                        for (int j = 0; j < entry.Attributes.Count; j++)
                        {
                            var attr = entry.Attributes[j];
                            bool isAttrSel = (_selectedEfxAttribute == attr);
                            if (Hexa.NET.ImGui.ImGui.Selectable($"  [{attr.UniqueID}] {attr.type}##entry_attr_{i}_{j}", isAttrSel))
                            {
                                _selectedEfxEntry = entry;
                                _selectedEfxAttribute = attr;
                            }
                        }
                        Hexa.NET.ImGui.ImGui.TreePop();
                    }
                }
                Hexa.NET.ImGui.ImGui.TreePop();
            }

            Hexa.NET.ImGui.ImGui.TreePop();
        }
    }

    private void RenderUniversalInspector()
    {
        if (_selectedEfxAttribute != null)
        {
            RenderAttributeInspector(_selectedEfxAttribute);
        }
        else if (_selectedEfxEntry != null)
        {
            RenderEntryInspector(_selectedEfxEntry);
        }
        else
        {
            Hexa.NET.ImGui.ImGui.TextDisabled("（请在上方树中选择 Entry 或 Attribute 进行检查与编辑）");
        }
    }

    private void RenderEntryInspector(EFXEntryBase entry)
    {
        Hexa.NET.ImGui.ImGui.TextColored(new Vector4(0.5f, 1.0f, 0.5f, 1.0f), $"[条目检查器] {entry.name}");
        Hexa.NET.ImGui.ImGui.Separator();

        string name = entry.name ?? "";
        if (Hexa.NET.ImGui.ImGui.InputText("条目名 (Name)", ref name, 64))
        {
            string old = entry.name ?? "";
            string cur = name;
            UndoRedo.Execute(new RelayUndoableCommand($"重命名条目: {cur}",
                () => { entry.name = cur; entry.nameHash = MurMur3HashUtils.GetUTF8Hash(cur); },
                () => { entry.name = old; entry.nameHash = MurMur3HashUtils.GetUTF8Hash(old); }));
        }
        Hexa.NET.ImGui.ImGui.TextDisabled($"NameHash: 0x{entry.nameHash:X8} | Version: {entry.Version}");

        Hexa.NET.ImGui.ImGui.Spacing();
        // Quick Color Presets
        Hexa.NET.ImGui.ImGui.Text("快速预设色相 (Quick Color):");
        if (Hexa.NET.ImGui.ImGui.Button("烈焰 (Fire)")) ApplyQuickColor(new Vector4(2.5f, 0.8f, 0.2f, 1f));
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("鬼人化 (Demon)")) ApplyQuickColor(new Vector4(0.2f, 1.2f, 2.5f, 1f));
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("龙属性 (Dragon)")) ApplyQuickColor(new Vector4(2.2f, 0.2f, 0.4f, 1f));
        Hexa.NET.ImGui.ImGui.SameLine();
        if (Hexa.NET.ImGui.ImGui.Button("冰霜 (Ice)")) ApplyQuickColor(new Vector4(0.5f, 1.8f, 2.5f, 1f));

        Hexa.NET.ImGui.ImGui.Spacing();
        Hexa.NET.ImGui.ImGui.SliderFloat("Bloom 辉光强度", ref _bloomIntensity, 0.0f, 3.0f, "%.2f");
        Hexa.NET.ImGui.ImGui.SliderFloat("视口曝光度", ref _exposure, 0.1f, 3.0f, "%.2f");

        Hexa.NET.ImGui.ImGui.Spacing();
        Hexa.NET.ImGui.ImGui.Separator();
        Hexa.NET.ImGui.ImGui.TextColored(new Vector4(0.4f, 0.9f, 1f, 1f), "核心属性快速配置:");

        // ParentOptions quick check
        var parentOpt = entry.Attributes.OfType<EFXAttributeParentOptions>().FirstOrDefault();
        if (parentOpt != null)
        {
            string currentParent = parentOpt.ParentBone ?? parentOpt.BoneName ?? "None";
            if (Hexa.NET.ImGui.ImGui.BeginCombo("挂载骨骼 (ParentBone)##entry_quick", currentParent))
            {
                for (int b = 0; b < _commonBones.Length; b++)
                {
                    bool isSel = (currentParent == _commonBones[b]);
                    if (Hexa.NET.ImGui.ImGui.Selectable(_commonBones[b], isSel))
                    {
                        string oldB = currentParent;
                        string newB = _commonBones[b];
                        UndoRedo.Execute(new RelayUndoableCommand($"修改挂载骨骼: {newB}",
                            () => {
                                parentOpt.ParentBone = newB;
                                parentOpt.BoneName = newB;
                                if (_currentEfx != null) _efxService.SetBoneBinding(_currentEfx, entry, newB);
                            },
                            () => {
                                parentOpt.ParentBone = oldB;
                                parentOpt.BoneName = oldB;
                                if (_currentEfx != null) _efxService.SetBoneBinding(_currentEfx, entry, oldB);
                            }));
                    }
                }
                Hexa.NET.ImGui.ImGui.EndCombo();
            }
        }

        // Transform3D quick check
        var trans = entry.Attributes.OfType<EFXAttributeTransform3D>().FirstOrDefault();
        if (trans != null)
        {
            Vector3 pos = trans.LocalPosition;
            if (Hexa.NET.ImGui.ImGui.DragFloat3("位置 (Position)##entry_quick", ref pos, 0.05f))
            {
                trans.LocalPosition = pos;
            }
            Vector3 rot = trans.LocalRotation;
            if (Hexa.NET.ImGui.ImGui.DragFloat3("旋转 (Rotation)##entry_quick", ref rot, 0.05f))
            {
                trans.LocalRotation = rot;
            }
            Vector3 scl = trans.LocalScale;
            if (Hexa.NET.ImGui.ImGui.DragFloat3("缩放 (Scale)##entry_quick", ref scl, 0.05f))
            {
                trans.LocalScale = scl;
            }
        }

        Hexa.NET.ImGui.ImGui.Spacing();
        Hexa.NET.ImGui.ImGui.Separator();
        Hexa.NET.ImGui.ImGui.Text($"条目包含属性列表 ({entry.Attributes.Count}):");
        for (int i = 0; i < entry.Attributes.Count; i++)
        {
            var attr = entry.Attributes[i];
            if (Hexa.NET.ImGui.ImGui.Button($"编辑 [{attr.UniqueID}] {attr.type}##btn_inspect_{i}"))
            {
                _selectedEfxAttribute = attr;
            }
        }
    }

    private void RenderAttributeInspector(EFXAttribute attr)
    {
        Hexa.NET.ImGui.ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), $"[属性检查器] {attr.type} (ID: {attr.UniqueID})");
        if (Hexa.NET.ImGui.ImGui.Button("返回所属 Entry##BackToEntry") && _selectedEfxEntry != null)
        {
            _selectedEfxAttribute = null;
            return;
        }
        Hexa.NET.ImGui.ImGui.Separator();

        if (attr is EFXAttributeParentOptions parentOpt)
        {
            string currentParent = parentOpt.ParentBone ?? parentOpt.BoneName ?? "None";
            if (Hexa.NET.ImGui.ImGui.BeginCombo("挂载骨骼 (ParentBone)", currentParent))
            {
                for (int b = 0; b < _commonBones.Length; b++)
                {
                    bool isSel = (currentParent == _commonBones[b]);
                    if (Hexa.NET.ImGui.ImGui.Selectable(_commonBones[b], isSel))
                    {
                        string oldB = currentParent;
                        string newB = _commonBones[b];
                        UndoRedo.Execute(new RelayUndoableCommand($"修改挂载骨骼: {newB}",
                            () => {
                                parentOpt.ParentBone = newB;
                                parentOpt.BoneName = newB;
                                if (_currentEfx != null && _selectedEfxEntry != null)
                                    _efxService.SetBoneBinding(_currentEfx, _selectedEfxEntry, newB);
                            },
                            () => {
                                parentOpt.ParentBone = oldB;
                                parentOpt.BoneName = oldB;
                                if (_currentEfx != null && _selectedEfxEntry != null)
                                    _efxService.SetBoneBinding(_currentEfx, _selectedEfxEntry, oldB);
                            }));
                    }
                }
                Hexa.NET.ImGui.ImGui.EndCombo();
            }

            string bText = parentOpt.ParentBone ?? "";
            if (Hexa.NET.ImGui.ImGui.InputText("自定义骨骼名", ref bText, 64))
            {
                parentOpt.ParentBone = bText;
                parentOpt.BoneName = bText;
                if (_currentEfx != null && _selectedEfxEntry != null)
                    _efxService.SetBoneBinding(_currentEfx, _selectedEfxEntry, bText);
            }
            uint bHash = MurMur3HashUtils.GetHash(parentOpt.ParentBone ?? "");
            Hexa.NET.ImGui.ImGui.TextDisabled($"ParentBone Hash: 0x{bHash:X8}");
            Hexa.NET.ImGui.ImGui.Separator();
        }
        else if (attr is EFXAttributeTransform3D trans)
        {
            Vector3 pos = trans.LocalPosition;
            if (Hexa.NET.ImGui.ImGui.DragFloat3("位置 (LocalPosition)", ref pos, 0.05f))
            {
                trans.LocalPosition = pos;
            }
            Vector3 rot = trans.LocalRotation;
            if (Hexa.NET.ImGui.ImGui.DragFloat3("旋转 (LocalRotation)", ref rot, 0.05f))
            {
                trans.LocalRotation = rot;
            }
            Vector3 scl = trans.LocalScale;
            if (Hexa.NET.ImGui.ImGui.DragFloat3("缩放 (LocalScale)", ref scl, 0.05f))
            {
                trans.LocalScale = scl;
            }
            Hexa.NET.ImGui.ImGui.Separator();
        }

        // Generic reflection inspector for all other properties & fields
        RenderObjectFields(attr, 0);
    }

    private readonly Dictionary<string, string> _efxTextDrafts = new();
    private readonly Dictionary<string, (object? Old, object New)> _efxDragEdits = new();

    private void RenderObjectFields(object target, int depth)
    {
        // Both the hierarchy and advanced editor call RenderAttributeInspector.
        // The hierarchy search must not silently empty the attribute panel.
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        RenderContentChildren(target, target, Array.Empty<object>(), seen);
    }

    private void RenderContentChildren(object root, object current, object[] path, HashSet<object> seen, bool readOnly = false)
    {
        if (!current.GetType().IsValueType && !seen.Add(current))
        {
            Hexa.NET.ImGui.ImGui.TextDisabled("循环引用（只显示一次）");
            return;
        }
        var version = root is EFXAttribute attribute ? attribute.Version : EfxVersion.MHWilds;
        var fields = EfxContentSchema.Describe(current.GetType(), version);
        if (fields.Count == 0 && path.Length == 0)
            Hexa.NET.ImGui.ImGui.TextDisabled("该类型无已识别的独立内容字段");
        foreach (var descriptor in fields)
        {
            var field = descriptor.Info;
            var childPath = path.Append((object)field).ToArray();
            object? value;
            try { value = field.GetValue(current); }
            catch (Exception ex)
            {
                Hexa.NET.ImGui.ImGui.TextDisabled($"{field.Name}: 读取失败 ({ex.Message})");
                continue;
            }
            if (!descriptor.Applicable)
            {
                if (descriptor.Condition.Contains("RszIgnore", StringComparison.Ordinal) && value != null)
                {
                    Hexa.NET.ImGui.ImGui.TextDisabled($"{field.Name}: 手动序列化或内部状态（待核定，只读）");
                    RenderContentValue(root, value, field.FieldType, childPath, field.Name, seen, true);
                }
                else
                {
                    string reason = descriptor.Condition.Contains("RszConditional", StringComparison.Ordinal)
                        ? "序列化条件尚未核定（只读）" : "当前版本不适用";
                    Hexa.NET.ImGui.ImGui.TextDisabled($"{field.Name}: {reason}");
                }
                continue;
            }
            RenderContentValue(root, value, field.FieldType, childPath, field.Name, seen, readOnly);
        }
        if (!current.GetType().IsValueType) seen.Remove(current);
    }

    private void RenderContentValue(object root, object? value, Type type, object[] path, string name, HashSet<object> seen, bool readOnly = false)
    {
        string id = string.Join("/", path.Select(p => p is FieldInfo f ? f.Name : $"[{p}]"));
        string label = $"{name}##efx_{id}";
        if (value == null)
        {
            Hexa.NET.ImGui.ImGui.TextDisabled($"{name}: <null> ({type.Name})");
            return;
        }
        if (readOnly) Hexa.NET.ImGui.ImGui.BeginDisabled();
        void Change(object replacement)
        {
            if (readOnly) return;
            object? oldValue = ReadContentPath(root, path, 0);
            if (Equals(oldValue, replacement)) return;
            UndoRedo.Execute(new RelayUndoableCommand($"修改 {id}",
                () => WriteContentPath(root, path, 0, replacement),
                () => WriteContentPath(root, path, 0, oldValue)));
        }
        void DragChange(bool changed, object replacement)
        {
            if (readOnly) return;
            string key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(root) + ":" + id;
            if (Hexa.NET.ImGui.ImGui.IsItemActivated())
                _efxDragEdits[key] = (ReadContentPath(root, path, 0), replacement);
            if (changed)
            {
                if (!_efxDragEdits.TryGetValue(key, out var pending))
                    pending = (ReadContentPath(root, path, 0), replacement);
                _efxDragEdits[key] = (pending.Old, replacement);
                WriteContentPath(root, path, 0, replacement);
                _vfxProxy.ClearSimulators();
            }
            if (Hexa.NET.ImGui.ImGui.IsItemDeactivatedAfterEdit() &&
                _efxDragEdits.Remove(key, out var completed) && !Equals(completed.Old, completed.New))
            {
                UndoRedo.Record(new RelayUndoableCommand($"修改 {id}",
                    () => WriteContentPath(root, path, 0, completed.New),
                    () => WriteContentPath(root, path, 0, completed.Old)));
            }
        }
        if (type == typeof(bool))
        {
            bool edit = (bool)value;
            if (Hexa.NET.ImGui.ImGui.Checkbox(label, ref edit)) Change(edit);
        }
        else if (type == typeof(float))
        {
            float edit = (float)value;
            bool changed = Hexa.NET.ImGui.ImGui.DragFloat(label, ref edit, 0.01f);
            DragChange(changed, edit);
        }
        else if (type == typeof(Vector3))
        {
            Vector3 edit = (Vector3)value;
            bool changed = Hexa.NET.ImGui.ImGui.DragFloat3(label, ref edit, 0.01f);
            DragChange(changed, edit);
        }
        else if (type == typeof(Vector4))
        {
            Vector4 edit = (Vector4)value;
            bool changed = Hexa.NET.ImGui.ImGui.DragFloat4(label, ref edit, 0.01f);
            DragChange(changed, edit);
        }
        else if (type == typeof(ReeLib.via.Color))
        {
            var c = (ReeLib.via.Color)value;
            var edit = new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
            bool changed = Hexa.NET.ImGui.ImGui.ColorEdit4(label, ref edit);
            DragChange(changed, new ReeLib.via.Color((byte)(Math.Clamp(edit.X, 0, 1) * 255),
                (byte)(Math.Clamp(edit.Y, 0, 1) * 255),
                (byte)(Math.Clamp(edit.Z, 0, 1) * 255),
                (byte)(Math.Clamp(edit.W, 0, 1) * 255)));
        }
        else if (type.IsEnum)
        {
            string[] names = Enum.GetNames(type);
            int selected = Array.IndexOf(names, value.ToString());
            if (Hexa.NET.ImGui.ImGui.Combo(label, ref selected, names, names.Length) && selected >= 0)
                Change(Enum.Parse(type, names[selected]));
        }
        else if (type == typeof(string) || type.IsPrimitive || type == typeof(decimal))
        {
            string draftKey = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(root) + ":" + id;
            if (!_efxTextDrafts.TryGetValue(draftKey, out string? draft)) draft = value.ToString() ?? "";
            bool commit = Hexa.NET.ImGui.ImGui.InputText(label, ref draft, 4096, ImGuiInputTextFlags.EnterReturnsTrue);
            _efxTextDrafts[draftKey] = draft;
            if (commit)
            {
                try
                {
                    Change(type == typeof(string) ? draft : Convert.ChangeType(draft,
                        type, System.Globalization.CultureInfo.InvariantCulture));
                }
                catch (Exception ex) { SetStatus($"{id}: 输入无效 ({ex.Message})"); }
            }
            if (!Hexa.NET.ImGui.ImGui.IsItemActive()) _efxTextDrafts[draftKey] = value.ToString() ?? "";
        }
        else if (value is System.Collections.IList items)
        {
            if (Hexa.NET.ImGui.ImGui.TreeNodeEx($"{name} [{items.Count}]##efx_{id}"))
            {
                for (int i = 0; i < items.Count; i++)
                {
                    object? item = items[i];
                    Type itemType = item?.GetType() ?? (type.IsArray ? type.GetElementType()! : type.GetGenericArguments().FirstOrDefault() ?? typeof(object));
                    RenderContentValue(root, item, itemType, path.Append((object)i).ToArray(), $"[{i}]", seen, readOnly);
                }
                Hexa.NET.ImGui.ImGui.TreePop();
            }
        }
        else if (Hexa.NET.ImGui.ImGui.TreeNodeEx($"{name} ({type.Name})##efx_{id}"))
        {
            RenderContentChildren(root, value, path, seen, readOnly);
            Hexa.NET.ImGui.ImGui.TreePop();
        }
        if (readOnly) Hexa.NET.ImGui.ImGui.EndDisabled();
    }

    internal static object? ReadContentPath(object current, object[] path, int depth)
    {
        if (depth == path.Length) return current;
        object? next = path[depth] is FieldInfo field
            ? field.GetValue(current)
            : ((System.Collections.IList)current)[(int)path[depth]];
        return next == null ? null : ReadContentPath(next, path, depth + 1);
    }

    internal static object WriteContentPath(object current, object[] path, int depth, object? value)
    {
        if (depth == path.Length) return value!;
        if (path[depth] is FieldInfo field)
        {
            object child = field.GetValue(current) ?? throw new InvalidOperationException(field.Name + " is null");
            field.SetValue(current, WriteContentPath(child, path, depth + 1, value));
        }
        else
        {
            var list = (System.Collections.IList)current;
            int index = (int)path[depth];
            object child = list[index] ?? throw new InvalidOperationException("List item is null");
            list[index] = WriteContentPath(child, path, depth + 1, value);
        }
        return current;
    }

    private void AddNewEfxEntry()
    {
        if (_currentEfx == null) return;
        var newEntry = new EFXEntry
        {
            Version = _currentEfx.Header.Version,
            name = $"Entry_{_currentEfx.Entries.Count}",
            nameHash = MurMur3HashUtils.GetUTF8Hash($"Entry_{_currentEfx.Entries.Count}")
        };
        newEntry.AddAttribute(EfxAttributeType.ParentOptions);
        newEntry.AddAttribute(EfxAttributeType.Transform3D);

        _efxService.AddEntry(_currentEfx, newEntry);
        _selectedEfxEntry = newEntry;
        _selectedEfxAttribute = null;
        SetStatus($"已新增特效 Entry: {newEntry.name}");
    }

    private void ApplyQuickColor(Vector4 color)
    {
        if (_currentEfx == null) return;
        var criteria = new EfxAttributeFilterCriteria { PropertyOrFieldName = "Color" };
        _efxService.BatchOverrideProperty(_currentEfx, criteria, "Color", color);
        SetStatus($"已批量应用色相预设！");
    }

    private void RenderModals()
    {
        if (_showAlertModal)
        {
            Hexa.NET.ImGui.ImGui.OpenPopup("SystemAlertModal");
            _showAlertModal = false;
        }

        bool open = true;
        if (Hexa.NET.ImGui.ImGui.BeginPopupModal("SystemAlertModal", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            Hexa.NET.ImGui.ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), $"[警告] {_alertTitle}");
            Hexa.NET.ImGui.ImGui.Separator();
            Hexa.NET.ImGui.ImGui.Spacing();
            Hexa.NET.ImGui.ImGui.TextWrapped(_alertMessage);
            Hexa.NET.ImGui.ImGui.Spacing();
            Hexa.NET.ImGui.ImGui.Separator();

            if (Hexa.NET.ImGui.ImGui.Button("确认 (OK)", new Vector2(120, 0)))
            {
                Hexa.NET.ImGui.ImGui.CloseCurrentPopup();
            }
            Hexa.NET.ImGui.ImGui.EndPopup();
        }
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        _meshShader.Dispose();
        _gridRenderer.Dispose();
        _uiIcons.Dispose();
        _vfxProxy.Dispose();
        _weaponManager.Dispose();
        _animator.Dispose();
        _hunterModel?.Dispose();
    }
}
