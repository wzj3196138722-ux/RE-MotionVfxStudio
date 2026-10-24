using System.Numerics;
using ReeLib;
using ReeLib.Efx;
using ReeLib.Efx.Enums;
using ReeLib.Efx.Structs.Basic;
using ReeLib.Efx.Structs.Field;
using ReeLib.Efx.Structs.Main;
using ReeLib.Efx.Structs.Pt;
using ReeLib.Efx.Structs.Transforms;
using Silk.NET.OpenGL;
using VfxStudio.Renderer.Meshes;
using VfxStudio.Renderer.Shaders;
using VfxStudio.Renderer.Textures;
using Shader = VfxStudio.Renderer.Shaders.Shader;

namespace VfxStudio.Renderer.Vfx;

/// <summary>
/// Deterministic native RNG. The reference plugin uses Python's Random with
/// per-particle seeds, so equal seed values do not imply equal samples yet.
/// </summary>
public sealed class EfxDetRng
{
    private ulong _state;

    public EfxDetRng(ulong seed)
    {
        _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
    }

    public uint NextUInt32()
    {
        ulong z = (_state += 0x9E3779B97F4A7C15UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return (uint)((z ^ (z >> 31)) & 0xFFFFFFFFUL);
    }

    public float NextFloat() => (NextUInt32() & 0xFFFFFF) / (float)0x1000000;

    public float Uniform(float min, float max)
    {
        if (MathF.Abs(max - min) < 1e-6f) return min;
        float lo = MathF.Min(min, max);
        float hi = MathF.Max(min, max);
        return lo + (hi - lo) * NextFloat();
    }

    public int RandInt(int min, int max)
    {
        int lo = Math.Min(min, max);
        int hi = Math.Max(min, max);
        if (lo == hi) return lo;
        return lo + (int)(NextUInt32() % (uint)(hi - lo + 1));
    }

    /// <summary>
    /// Samples staticBase +/- abs(randomSpread).
    /// </summary>
    public float SamplePlusMinus(float staticBase, float randomSpread)
    {
        float spread = MathF.Abs(randomSpread);
        if (spread < 1e-6f) return staticBase;
        return staticBase + (NextFloat() * 2.0f - 1.0f) * spread;
    }

    public int SampleIntPlusMinus(int staticBase, int randomSpread)
    {
        int spread = Math.Abs(randomSpread);
        if (spread == 0) return staticBase;
        return staticBase + RandInt(-spread, spread);
    }
}

public enum EfxRenderBodyKind
{
    NoDraw,
    Point,
    Billboard3D,
    Polygon,
    RibbonFollow,
    RibbonLength,
    PolygonTrail,
    Mesh
}

public sealed class SimParticle
{
    public int Id;
    public int AgeFrames;
    public int AppearFrames;
    public int KeepFrames;
    public int VanishFrames;
    public float LifeWeight = 1.0f;
    public bool Alive = true;

    public Vector3 LocalPosition;
    public Vector3 Velocity;
    public float SpeedCoef = 1.0f;
    public float GravityAccel;
    public Vector3 ShapeDirection = Vector3.UnitY;

    public Vector3 Scale = Vector3.One;
    public Vector3 BodySize = Vector3.One;
    public float ScaleScalarAdd;
    public float ScaleScalarCoef = 1.0f;
    public Vector3 ScaleAxisAdd;
    public Vector3 ScaleAxisCoef = Vector3.One;

    public Vector3 Rotation; // Radians XYZ
    public Vector3 AngularVelocity;

    public Vector4 Color = Vector4.One;
    public float Alpha = 1.0f;

    // RibbonLength / PolygonTrail per-particle rolled direction & length
    public Vector3 RibbonDir = Vector3.UnitY;
    public float RibbonLength = 0.6f;
    public float RibbonHalfWidth = 0.08f;
    public int RibbonDivisions = 6;

    public int UvSequenceIndex;
    public int UvPatternIndex;
    public int UvPatternStart;
    public int UvPatternCount;
    public float UvPlaySpeed = 1.0f;
    public float UvFrameCursor;

    // Matrix of the emitter in world space at the moment this particle spawned
    public Matrix4x4 SpawnWorldMatrix = Matrix4x4.Identity;

    // World-space or local-space trail points for RibbonFollow
    public List<Vector3> TrailPositions { get; } = new(32);

    public int TotalLifeFrames => Math.Max(1, AppearFrames + KeepFrames + VanishFrames);
}

public sealed class EfxEntrySimState
{
    public int EntryIndex;
    public EFXEntryBase Entry = null!;
    public EfxDetRng Rng = null!;

    // Extracted Transform & Parent relation
    public Vector3 LocalPosition = Vector3.Zero;
    public Vector3 LocalRotation = Vector3.Zero;
    public Vector3 LocalScale = Vector3.One;
    public int RelationType = 2; // 0=Independent after spawn, 1=FollowPos, 2=FollowPosRot, 3=FollowAll
    public string? ParentBoneName;

    // Spawn config
    public int MaxAliveParticles = 16;
    public int DelayFrames;
    public int IntervalFrames;
    public int BurstCount = 1;
    public int MaxLoops = 1; // 0 = infinite
    public int LoopsDone;
    public int NextSpawnFrame;
    public bool HasBurstTriggered;

    // Life config
    public int AppearMin, AppearMax;
    public int KeepMin = 20, KeepMax = 30;
    public int VanishMin = 5, VanishMax = 10;

    // EmitterShape3D
    public Shape3DType EmitterShape = default;
    public float RangeXMin, RangeXMax;
    public float RangeYMin, RangeYMax;
    public float RangeZMin, RangeZMax;
    public bool ShapeUseExtension;
    public float ShapeHorizontalStart, ShapeHorizontalSpan;
    public float ShapeVerticalStart, ShapeVerticalSpan;
    public int ShapeDivideHorizontal, ShapeDivideVertical;

    // Velocity3D
    public bool HasVelocity3D;
    public uint VelocityFlags;
    public Vector3 VelocityDir = Vector3.UnitY;
    public float SpeedMin, SpeedMax;
    public float SpeedCoefMin = 1f, SpeedCoefMax = 1f;
    public float GravityMin, GravityMax;
    public float VelocityDamping;

    // ScaleAnim (ports scaleanim.py: SizeScalarAdd * SizeScalarCoef + SizeX/Y/ZAdd * SizeX/Y/ZAddCoef)
    public bool HasScaleAnim;
    public float ScaleAddOverallMin, ScaleAddOverallMax;
    public float ScaleCoefOverallMin = 1f, ScaleCoefOverallMax = 1f;
    public Vector3 ScaleAddMin, ScaleAddMax;
    public Vector3 ScaleCoefMin = Vector3.One, ScaleCoefMax = Vector3.One;

    // RotateAnim
    public bool HasRotateAnim;
    public Vector3 RotateAddMin, RotateAddMax;
    public Vector3 RotateCoef = Vector3.One;

    // Attractor
    public bool HasAttractor;
    public Vector3 AttractorPos;
    public float AttractorForce;

    // UVSequence
    public string? UvsPath;
    public int UvsSeqNo;
    public int UvsPatternMin, UvsPatternMax;
    public float UvsPlaySpeedMin = 1f, UvsPlaySpeedMax = 1f;
    public uint UvsFlags;

    // Render body (defaults to NoDraw unless a render body attribute is present, matching simulator.py)
    public EfxRenderBodyKind BodyKind = EfxRenderBodyKind.NoDraw;
    public Vector4 BaseColor0 = new Vector4(1.0f, 0.85f, 0.5f, 1.0f);
    public Vector4 BaseColor1 = new Vector4(1.0f, 0.6f, 0.2f, 1.0f);
    public float BaseAlpha = 1.0f;
    public Vector3 BodyScale = new Vector3(0.25f, 0.25f, 0.25f);
    public bool RollBillboardSize;
    public float BillboardSizeX, BillboardSizeXRandom;
    public float BillboardSizeY, BillboardSizeYRandom;
    public float BillboardScalar = 1f, BillboardScalarRandom;
    public float BillboardRotation, BillboardRotationRandom;
    public Vector3 BodyInitialRot = Vector3.Zero;
    public int MaxTrailPoints = 14;
    public float RibbonWidth0 = 0.16f;
    public float RibbonWidth1 = 0.03f;
    public float RibbonLengthVal = 0.65f;
    public Vector3 RibbonDirVal = Vector3.UnitY;
    public VfxResolvedMeshMaterial? MeshMaterial;

    public List<SimParticle> ActiveParticles { get; } = new();
    private int _nextParticleId = 1;

    public int AllocateParticleId() => _nextParticleId++;
}

public sealed record EfxEntryDiagnostics(
    int EntryIndex,
    string EntryName,
    string BodyKind,
    int Particles,
    float[]? MinPosition,
    float[]? MaxPosition,
    float[]? FirstScale,
    float[]? FirstColor,
    int? FirstUvPattern,
    string? UvsPath);

/// <summary>
/// Deterministic EFX simulator and OpenGL 4.6 renderer porting mhws_efx_editor-0.3.0/efx_sim.
/// Evaluates EFX entries (Spawn, Life, EmitterShape3D, Velocity3D, ScaleAnim, RotateAnim,
/// UVSequence, Billboard2D/3D, Polygon, GpuPolygon, RibbonLength, RibbonFollow, RibbonChain,
/// PolygonTrail, and TypeMeshV2/TypeMesh/TypeGpuMesh) and renders them using real .uvs/.tex/.mesh/.mdf2 assets.
/// </summary>
public sealed class EfxSimulator
{
    public string EfxFilePath { get; }
    public EfxFile Efx { get; }
    public int CurrentSimFrame { get; private set; } = -1;

    private readonly List<EfxEntrySimState> _entryStates = new();
    private readonly VfxAssetCache _assetCache;
    private readonly ulong _baseSeed;

    public bool HasActiveParticles => _entryStates.Any(s => s.BodyKind != EfxRenderBodyKind.NoDraw && s.ActiveParticles.Count > 0);

    public IReadOnlyList<EfxEntryDiagnostics> GetDiagnostics()
    {
        var results = new List<EfxEntryDiagnostics>(_entryStates.Count);
        foreach (var state in _entryStates)
        {
            var particles = state.ActiveParticles.Where(p => p.Alive).ToArray();
            float[]? min = null, max = null, scale = null, color = null;
            int? uvPattern = null;
            if (particles.Length > 0)
            {
                var first = particles[0];
                min = new[] { particles.Min(p => p.LocalPosition.X), particles.Min(p => p.LocalPosition.Y), particles.Min(p => p.LocalPosition.Z) };
                max = new[] { particles.Max(p => p.LocalPosition.X), particles.Max(p => p.LocalPosition.Y), particles.Max(p => p.LocalPosition.Z) };
                // Report the quad size sent to rendering, rather than the
                // ScaleAnim multiplier alone, for comparison with RenderItem.size.
                scale = new[] { first.Scale.X * first.BodySize.X,
                    first.Scale.Y * first.BodySize.Y, 1f };
                color = new[] { first.Color.X, first.Color.Y, first.Color.Z, first.Alpha * first.LifeWeight };
                uvPattern = first.UvPatternIndex;
            }
            results.Add(new EfxEntryDiagnostics(state.EntryIndex, state.Entry.name ?? "", state.BodyKind.ToString(),
                particles.Length, min, max, scale, color, uvPattern, state.UvsPath));
        }
        return results;
    }

    public EfxSimulator(string efxFilePath, EfxFile efx, VfxAssetCache assetCache, ulong seed = 0x5646585354554449UL)
    {
        EfxFilePath = efxFilePath;
        Efx = efx;
        _assetCache = assetCache;
        _baseSeed = seed;
        BuildEntryStates(Matrix4x4.Identity);
    }

    public void Reset(Matrix4x4 initialWorldMatrix)
    {
        CurrentSimFrame = -1;
        BuildEntryStates(initialWorldMatrix);
    }

    public void RebuildAndSeek(int targetLocalFrame, Matrix4x4 currentEmitterWorldMatrix)
    {
        Reset(currentEmitterWorldMatrix);
        StepToFrame(targetLocalFrame, currentEmitterWorldMatrix);
    }

    private void BuildEntryStates(Matrix4x4 initialWorldMatrix)
    {
        _entryStates.Clear();
        int idx = 0;

        if (Efx.Entries != null)
        {
            for (int i = 0; i < Efx.Entries.Count; i++)
            {
                var st = ParseEntryState(idx++, Efx.Entries[i]);
                if (st.BodyKind != EfxRenderBodyKind.NoDraw)
                    _entryStates.Add(st);
            }
        }

        // Also include embedded PlayEmitter entries inside Efx.Actions (ports sim_preview.py _action_nested_entries)
        if (Efx.Actions != null)
        {
            foreach (var action in Efx.Actions)
            {
                foreach (var attr in action.Attributes)
                {
                    if (attr is EFXAttributePlayEmitter pe && pe.efxrData?.Entries != null)
                    {
                        foreach (var subEntry in pe.efxrData.Entries)
                        {
                            var st = ParseEntryState(idx++, subEntry);
                            if (st.BodyKind != EfxRenderBodyKind.NoDraw)
                                _entryStates.Add(st);
                        }
                    }
                }
            }
        }
    }

    private EfxEntrySimState ParseEntryState(int index, EFXEntryBase entry)
    {
        var rng = new EfxDetRng(_baseSeed ^ ((ulong)(index + 1) * 0x9E3779B97F4A7C15UL));
        var st = new EfxEntrySimState
        {
            EntryIndex = index,
            Entry = entry,
            Rng = rng
        };

        bool hasExplicitNoDraw = false;

        foreach (var attr in entry.Attributes)
        {
            switch (attr)
            {
                case EFXAttributeTransform3D tr:
                    st.LocalPosition = tr.LocalPosition;
                    st.LocalRotation = tr.LocalRotation;
                    st.LocalScale = tr.LocalScale == Vector3.Zero ? Vector3.One : tr.LocalScale;
                    break;

                case EFXAttributeParentOptions po:
                    st.RelationType = po.RelationPos.x;
                    st.ParentBoneName = !string.IsNullOrWhiteSpace(po.ParentBone) ? po.ParentBone : po.BoneName;
                    break;

                case EFXAttributeSpawn sp:
                {
                    st.MaxAliveParticles = sp.MaxParticles > 0 ? Math.Clamp((int)sp.MaxParticles, 1, 32) : 24;
                    st.BurstCount = Math.Clamp(rng.RandInt(sp.SpawnNum.x, sp.SpawnNum.y), 0, st.MaxAliveParticles);
                    st.IntervalFrames = Math.Max(0, rng.RandInt(sp.IntervalFrame.x, sp.IntervalFrame.y));
                    st.DelayFrames = Math.Max(0, rng.RandInt(sp.EmitterDelayFrame.x, sp.EmitterDelayFrame.y));
                    st.MaxLoops = Math.Max(0, rng.SampleIntPlusMinus(sp.LoopNum.r, sp.LoopNum.s));
                    st.NextSpawnFrame = st.DelayFrames;
                    break;
                }

                case EFXAttributeLife lf:
                {
                    st.AppearMin = lf.AppearFrame.r;
                    st.AppearMax = lf.AppearFrame.s;
                    int keepHold = Math.Max(0, lf.KeepHoldFrame.r);
                    st.KeepMin = Math.Max(1, lf.KeepFrame.r + keepHold);
                    st.KeepMax = Math.Max(1, lf.KeepFrame.s + Math.Max(keepHold, lf.KeepHoldFrame.s));
                    st.VanishMin = lf.VanishFrame.r;
                    st.VanishMax = lf.VanishFrame.s;
                    break;
                }

                case EFXAttributeEmitterShape3D es:
                {
                    st.EmitterShape = es.ShapeType;
                    // EmitterShape3D ranges are literal (min, max) pairs in
                    // the reference model, unlike the usual static/random fields.
                    st.RangeXMin = es.RangeX.s; st.RangeXMax = es.RangeX.r;
                    st.RangeYMin = es.RangeY.s; st.RangeYMax = es.RangeY.r;
                    st.RangeZMin = es.RangeZ.s; st.RangeZMax = es.RangeZ.r;
                    st.ShapeUseExtension = es.UseExtension;
                    st.ShapeHorizontalStart = es.ScaleHorizontal.s;
                    st.ShapeHorizontalSpan = es.ScaleHorizontal.r;
                    st.ShapeVerticalStart = es.ScaleVertical.s;
                    st.ShapeVerticalSpan = es.ScaleVertical.r;
                    st.ShapeDivideHorizontal = (int)Math.Min(es.RangeDivideHorizontalNum, 4096u);
                    st.ShapeDivideVertical = (int)Math.Min(es.RangeDivideVerticalNum, 4096u);
                    break;
                }

                case EFXAttributeVelocity3D vel:
                {
                    st.HasVelocity3D = true;
                    st.VelocityFlags = vel.Flags;
                    Vector3 rawDir = new Vector3(vel.DirectionVectorX.s, vel.DirectionVectorY.s, vel.DirectionVectorZ.s);
                    st.VelocityDir = rawDir.LengthSquared() > 1e-6f
                        ? Vector3.Normalize(rawDir)
                        : Vector3.UnitY;
                    st.SpeedMin = vel.Speed.s; st.SpeedMax = vel.Speed.r;
                    st.SpeedCoefMin = vel.SpeedCoef.s == 0f && vel.SpeedCoef.r == 0f ? 1f : vel.SpeedCoef.s;
                    st.SpeedCoefMax = vel.SpeedCoef.r;
                    st.GravityMin = vel.GravityRate.s; st.GravityMax = vel.GravityRate.r;
                    break;
                }

                case EFXAttributeScaleAnim sa:
                {
                    st.HasScaleAnim = true;
                    st.ScaleAddOverallMin = sa.SizeScalarAdd.s;
                    st.ScaleAddOverallMax = sa.SizeScalarAdd.r;
                    st.ScaleCoefOverallMin = sa.SizeScalarCoef.s == 0f && sa.SizeScalarCoef.r == 0f ? 1f : sa.SizeScalarCoef.s;
                    st.ScaleCoefOverallMax = sa.SizeScalarCoef.r;
                    st.ScaleAddMin = new Vector3(sa.SizeXAdd.s, sa.SizeYAdd.s, sa.SizeZAdd.s);
                    st.ScaleAddMax = new Vector3(sa.SizeXAdd.r, sa.SizeYAdd.r, sa.SizeZAdd.r);
                    st.ScaleCoefMin = new Vector3(
                        sa.SizeXAddCoef.s == 0f && sa.SizeXAddCoef.r == 0f ? 1f : sa.SizeXAddCoef.s,
                        sa.SizeYAddCoef.s == 0f && sa.SizeYAddCoef.r == 0f ? 1f : sa.SizeYAddCoef.s,
                        sa.SizeZAddCoef.s == 0f && sa.SizeZAddCoef.r == 0f ? 1f : sa.SizeZAddCoef.s);
                    st.ScaleCoefMax = new Vector3(sa.SizeXAddCoef.r, sa.SizeYAddCoef.r, sa.SizeZAddCoef.r);
                    break;
                }

                case EFXAttributeRotateAnim ra:
                {
                    st.HasRotateAnim = true;
                    st.RotateAddMin = new Vector3(ra.RotationAddX.s, ra.RotationAddY.s, ra.RotationAddZ.s);
                    st.RotateAddMax = new Vector3(ra.RotationAddX.r, ra.RotationAddY.r, ra.RotationAddZ.r);
                    break;
                }

                case EFXAttributeUVSequence uvs:
                {
                    if (!string.IsNullOrWhiteSpace(uvs.UVSPath))
                    {
                        st.UvsPath = uvs.UVSPath;
                        st.UvsSeqNo = uvs.SequenceNo.r;
                        st.UvsPatternMin = uvs.PatternNo.r;
                        st.UvsPatternMax = uvs.PatternNo.s;
                        st.UvsPlaySpeedMin = uvs.PlaySpeed.s;
                        st.UvsPlaySpeedMax = uvs.PlaySpeed.r;
                        st.UvsFlags = uvs.Flags;
                    }
                    break;
                }

                case EFXAttributeTextureUnit tu:
                {
                    if (string.IsNullOrWhiteSpace(st.UvsPath))
                    {
                        if (!string.IsNullOrWhiteSpace(tu.uvs0Path)) st.UvsPath = tu.uvs0Path;
                        else if (!string.IsNullOrWhiteSpace(tu.uvs1Path)) st.UvsPath = tu.uvs1Path;
                    }
                    break;
                }

                case EFXAttributeTypeBillboard2D bb2:
                {
                    st.BodyKind = EfxRenderBodyKind.Billboard3D;
                    st.BaseColor0 = ColorToVec4(bb2.Color, bb2.ColorRange);
                    st.BaseAlpha = bb2.AlphaRate > 0f ? bb2.AlphaRate : 1.0f;
                    float sizeScalar = SampleRangeOrDefault(rng, bb2.SizeScalar.s, bb2.SizeScalar.r, 0.35f);
                    float sx = SampleRangeOrDefault(rng, bb2.SizeX.s, bb2.SizeX.r, 0.35f) * sizeScalar;
                    float sy = SampleRangeOrDefault(rng, bb2.SizeY.s, bb2.SizeY.r, 0.35f) * sizeScalar;
                    st.BodyScale = new Vector3(Math.Clamp(sx, 0.03f, 2.5f), Math.Clamp(sy, 0.03f, 2.5f), 1.0f);
                    float rotZ = rng.SamplePlusMinus(bb2.Rotation.s, bb2.Rotation.r);
                    st.BodyInitialRot = new Vector3(0f, 0f, rotZ);
                    break;
                }

                case EFXAttributeTypeBillboard3D bb:
                {
                    st.BodyKind = EfxRenderBodyKind.Billboard3D;
                    st.BaseColor0 = ColorToVec4(bb.Color, bb.ColorRange);
                    st.BaseAlpha = bb.AlphaRate > 0f ? bb.AlphaRate : 1.0f;
                    st.RollBillboardSize = true;
                    st.BillboardSizeX = bb.SizeX.s;
                    st.BillboardSizeXRandom = bb.SizeX.r;
                    st.BillboardSizeY = bb.SizeY.s;
                    st.BillboardSizeYRandom = bb.SizeY.r;
                    st.BillboardScalar = bb.SizeScalar.s;
                    st.BillboardScalarRandom = bb.SizeScalar.r;
                    st.BillboardRotation = bb.Rotation.s;
                    st.BillboardRotationRandom = bb.Rotation.r;
                    st.BodyScale = Vector3.One;
                    break;
                }

                case EFXAttributeTypeBillboard3DMaterial bbm:
                {
                    st.BodyKind = EfxRenderBodyKind.Billboard3D;
                    st.BaseColor0 = ColorToVec4(bbm.Color, bbm.ColorRange);
                    float sizeScalar = SampleRangeOrDefault(rng, bbm.SizeScalar.s, bbm.SizeScalar.r, 0.35f);
                    float sx = SampleRangeOrDefault(rng, bbm.SizeX.s, bbm.SizeX.r, 0.35f) * sizeScalar;
                    float sy = SampleRangeOrDefault(rng, bbm.SizeY.s, bbm.SizeY.r, 0.35f) * sizeScalar;
                    st.BodyScale = new Vector3(Math.Clamp(sx, 0.03f, 2.5f), Math.Clamp(sy, 0.03f, 2.5f), 1.0f);
                    break;
                }

                case EFXAttributeTypeGpuBillboard gbb:
                {
                    st.BodyKind = EfxRenderBodyKind.Billboard3D;
                    st.BaseColor0 = ColorToVec4(gbb.Color, gbb.ColorRange);
                    st.BaseAlpha = gbb.AlphaRate > 0f ? gbb.AlphaRate : 1.0f;
                    float sz = Math.Clamp(gbb.SizeScalar.s > 0.001f ? gbb.SizeScalar.s : 0.18f, 0.03f, 1.5f);
                    st.BodyScale = new Vector3(sz, sz, 1.0f);
                    break;
                }

                case EFXAttributeTypePolygon poly:
                {
                    st.BodyKind = EfxRenderBodyKind.Polygon;
                    st.BaseColor0 = ColorToVec4(poly.Color, poly.ColorRange);
                    st.BaseAlpha = poly.AlphaRate > 0f ? poly.AlphaRate : 1.0f;
                    float sc = SampleRangeOrDefault(rng, poly.SizeScalar.s, poly.SizeScalar.r, 0.35f);
                    float sx = SampleRangeOrDefault(rng, poly.Width.s, poly.Width.r, 0.35f) * sc;
                    float sy = SampleRangeOrDefault(rng, poly.Height.s, poly.Height.r, 0.35f) * sc;
                    st.BodyScale = new Vector3(Math.Clamp(sx, 0.03f, 2.5f), Math.Clamp(sy, 0.03f, 2.5f), 1.0f);
                    st.BodyInitialRot = new Vector3(
                        rng.SamplePlusMinus(poly.RotationX.s, poly.RotationX.r),
                        rng.SamplePlusMinus(poly.RotationY.s, poly.RotationY.r),
                        rng.SamplePlusMinus(poly.RotationZ.s, poly.RotationZ.r));
                    break;
                }

                case EFXAttributeTypePolygonMaterial polym:
                {
                    st.BodyKind = EfxRenderBodyKind.Polygon;
                    st.BaseColor0 = ColorToVec4(polym.Color, polym.ColorRange);
                    float sc = SampleRangeOrDefault(rng, polym.SizeScalar.s, polym.SizeScalar.r, 0.35f);
                    float sx = SampleRangeOrDefault(rng, polym.Width.s, polym.Width.r, 0.35f) * sc;
                    float sy = SampleRangeOrDefault(rng, polym.Height.s, polym.Height.r, 0.35f) * sc;
                    st.BodyScale = new Vector3(Math.Clamp(sx, 0.03f, 2.5f), Math.Clamp(sy, 0.03f, 2.5f), 1.0f);
                    break;
                }

                case EFXAttributeTypeGpuPolygon gpoly:
                {
                    st.BodyKind = EfxRenderBodyKind.Polygon;
                    st.BaseColor0 = ColorToVec4(gpoly.Color, gpoly.ColorRange);
                    st.BaseAlpha = gpoly.AlphaRate > 0f ? gpoly.AlphaRate : 1.0f;
                    float sc = SampleRangeOrDefault(rng, gpoly.SizeScalar.s, gpoly.SizeScalar.r, 0.25f);
                    float sx = SampleRangeOrDefault(rng, gpoly.Width.s, gpoly.Width.r, 0.25f) * sc;
                    float sy = SampleRangeOrDefault(rng, gpoly.Height.s, gpoly.Height.r, 0.25f) * sc;
                    st.BodyScale = new Vector3(Math.Clamp(sx, 0.03f, 1.5f), Math.Clamp(sy, 0.03f, 1.5f), 1.0f);
                    st.BodyInitialRot = new Vector3(
                        rng.SamplePlusMinus(gpoly.RotationX.s, gpoly.RotationX.r),
                        rng.SamplePlusMinus(gpoly.RotationY.s, gpoly.RotationY.r),
                        rng.SamplePlusMinus(gpoly.RotationZ.s, gpoly.RotationZ.r));
                    break;
                }

                case EFXAttributeTypeRibbonLength rl:
                {
                    // Ports ribbonlength.py: fixed-length directional streak
                    st.BodyKind = EfxRenderBodyKind.RibbonLength;
                    st.BaseColor0 = ColorToVec4(rl.Color, rl.ColorRange);
                    st.BaseColor1 = ColorToVec4(rl.HeadColor, rl.ColorPlace1);
                    st.BaseAlpha = rl.AlphaRate > 0f ? rl.AlphaRate : 1.0f;
                    float sc = SampleRangeOrDefault(rng, rl.SizeScalar.s, rl.SizeScalar.r, 1.0f);
                    st.RibbonLengthVal = Math.Clamp(SampleRangeOrDefault(rng, rl.Length.s, rl.Length.r, 0.7f) * sc, 0.08f, 3.0f);
                    st.RibbonWidth0 = Math.Clamp(SampleRangeOrDefault(rng, rl.Width.s, rl.Width.r, 0.16f) * sc, 0.02f, 1.2f);
                    st.RibbonWidth1 = st.RibbonWidth0 * 0.35f;
                    Vector3 dir = new Vector3(
                        rng.SamplePlusMinus(rl.DirectionX.s, rl.DirectionX.r),
                        rng.SamplePlusMinus(rl.DirectionY.s, rl.DirectionY.r),
                        rng.SamplePlusMinus(rl.DirectionZ.s, rl.DirectionZ.r));
                    st.RibbonDirVal = dir.LengthSquared() > 1e-6f ? Vector3.Normalize(dir) : Vector3.UnitY;
                    st.MaxTrailPoints = Math.Clamp((int)rl.ShapeDivision, 2, 16);
                    break;
                }

                case EFXAttributeTypeGpuRibbonLength grl:
                {
                    // Ports gpuribbonlength.py: fixed-length directional streak
                    st.BodyKind = EfxRenderBodyKind.RibbonLength;
                    st.BaseColor0 = ColorToVec4(grl.Color, grl.ColorRange);
                    st.BaseColor1 = st.BaseColor0;
                    st.BaseAlpha = grl.AlphaRate > 0f ? grl.AlphaRate : 1.0f;
                    float sc = SampleRangeOrDefault(rng, grl.SizeScalar.s, grl.SizeScalar.r, 1.0f);
                    st.RibbonLengthVal = Math.Clamp(SampleRangeOrDefault(rng, grl.Length.s, grl.Length.r, 0.7f) * sc, 0.08f, 3.0f);
                    st.RibbonWidth0 = Math.Clamp(SampleRangeOrDefault(rng, grl.Width.s, grl.Width.r, 0.14f) * sc, 0.02f, 1.2f);
                    st.RibbonWidth1 = st.RibbonWidth0 * 0.35f;
                    Vector3 dir = new Vector3(
                        rng.SamplePlusMinus(grl.DirectionX.s, grl.DirectionX.r),
                        rng.SamplePlusMinus(grl.DirectionY.s, grl.DirectionY.r),
                        rng.SamplePlusMinus(grl.DirectionZ.s, grl.DirectionZ.r));
                    st.RibbonDirVal = dir.LengthSquared() > 1e-6f ? Vector3.Normalize(dir) : Vector3.UnitY;
                    st.MaxTrailPoints = Math.Clamp((int)grl.ShapeDivision, 2, 16);
                    break;
                }

                case EFXAttributeTypeRibbonFollow rf:
                {
                    st.BodyKind = EfxRenderBodyKind.RibbonFollow;
                    st.BaseColor0 = ColorToVec4(rf.Color, rf.ColorRange);
                    st.BaseColor1 = ColorToVec4(rf.HeadColor, rf.ColorPlace1);
                    st.BaseAlpha = rf.AlphaRate > 0f ? rf.AlphaRate : 1.0f;
                    float sc = SampleRangeOrDefault(rng, rf.SizeScalar.s, rf.SizeScalar.r, 1.0f);
                    st.MaxTrailPoints = 16;
                    st.RibbonWidth0 = Math.Clamp(SampleRangeOrDefault(rng, rf.Width.s, rf.Width.r, 0.2f) * sc, 0.03f, 1.2f);
                    st.RibbonWidth1 = st.RibbonWidth0 * 0.2f;
                    break;
                }

                case EFXAttributeTypeRibbonFollowMaterial rfm:
                {
                    st.BodyKind = EfxRenderBodyKind.RibbonFollow;
                    st.BaseColor0 = ColorToVec4(rfm.Color, rfm.ColorRange);
                    st.BaseColor1 = ColorToVec4(rfm.HeadColor, rfm.ColorPlace1);
                    st.BaseAlpha = rfm.AlphaRate > 0f ? rfm.AlphaRate : 1.0f;
                    float sc = SampleRangeOrDefault(rng, rfm.SizeScalar.s, rfm.SizeScalar.r, 1.0f);
                    st.MaxTrailPoints = 16;
                    st.RibbonWidth0 = Math.Clamp(SampleRangeOrDefault(rng, rfm.Width.s, rfm.Width.r, 0.2f) * sc, 0.03f, 1.2f);
                    st.RibbonWidth1 = st.RibbonWidth0 * 0.2f;
                    break;
                }

                case EFXAttributeTypeGpuRibbonFollow grf:
                {
                    st.BodyKind = EfxRenderBodyKind.RibbonFollow;
                    st.BaseColor0 = ColorToVec4(grf.Color, grf.ColorRange);
                    st.BaseColor1 = st.BaseColor0;
                    st.BaseAlpha = grf.AlphaRate > 0f ? grf.AlphaRate : 1.0f;
                    float sc = SampleRangeOrDefault(rng, grf.SizeScalar.s, grf.SizeScalar.r, 0.18f);
                    st.MaxTrailPoints = 16;
                    st.RibbonWidth0 = Math.Clamp(sc, 0.03f, 1.0f);
                    st.RibbonWidth1 = st.RibbonWidth0 * 0.2f;
                    break;
                }

                case EFXAttributeTypeRibbonChain rc:
                {
                    st.BodyKind = EfxRenderBodyKind.RibbonFollow;
                    st.BaseColor0 = ColorToVec4(rc.color1, rc.color2);
                    st.BaseColor1 = st.BaseColor0;
                    st.BaseAlpha = 1.0f;
                    st.MaxTrailPoints = 16;
                    st.RibbonWidth0 = 0.20f;
                    st.RibbonWidth1 = 0.05f;
                    break;
                }

                case EFXAttributeTypeRibbonTrail rt:
                {
                    st.BodyKind = EfxRenderBodyKind.RibbonFollow;
                    st.BaseColor0 = ColorToVec4(rt.Color, rt.ColorRange);
                    st.BaseColor1 = st.BaseColor0;
                    st.BaseAlpha = rt.AlphaRate > 0f ? rt.AlphaRate : 1.0f;
                    st.MaxTrailPoints = Math.Clamp((int)(rt.Division > 2 ? rt.Division : 16), 4, 24);
                    st.RibbonWidth0 = Math.Clamp(SampleRangeOrDefault(rng, rt.Width.s, rt.Width.r, 0.22f), 0.03f, 1.2f);
                    st.RibbonWidth1 = st.RibbonWidth0 * 0.2f;
                    break;
                }

                case EFXAttributeTypePolygonTrail pt:
                {
                    // Ports polygontrail.py: blade trail along Axis * Length
                    st.BodyKind = EfxRenderBodyKind.PolygonTrail;
                    st.BaseColor0 = ColorToVec4(pt.Color, pt.ColorRange);
                    st.BaseColor1 = ColorToVec4(pt.HeadColor, pt.Place1);
                    st.BaseAlpha = pt.AlphaRate > 0f ? pt.AlphaRate : 1.0f;
                    st.RibbonLengthVal = Math.Clamp(SampleRangeOrDefault(rng, pt.Length.s, pt.Length.r, 0.85f), 0.10f, 3.0f);
                    st.RibbonWidth0 = Math.Clamp(st.RibbonLengthVal * 0.15f, 0.04f, 0.6f);
                    st.RibbonWidth1 = st.RibbonWidth0 * 0.4f;
                    st.RibbonDirVal = AxisTypeToVec3((int)pt.Axis);
                    st.MaxTrailPoints = Math.Clamp((int)(pt.NumTrailDivision > 2 ? pt.NumTrailDivision : 8), 2, 16);
                    break;
                }

                case EFXAttributeTypeMeshV2 tm2:
                {
                    st.BodyKind = EfxRenderBodyKind.Mesh;
                    st.MeshMaterial = _assetCache.ResolveEntryMeshMaterial(entry);
                    if (st.MeshMaterial != null)
                    {
                        st.BaseColor0 = st.MeshMaterial.TintColor;
                    }
                    float mul = SampleRangeOrDefault(rng, tm2.ScaleMultiplier.s, tm2.ScaleMultiplier.r, 1.0f);
                    float sx = SampleRangeOrDefault(rng, tm2.ScaleX.s, tm2.ScaleX.r, 1.0f) * mul;
                    float sy = SampleRangeOrDefault(rng, tm2.ScaleY.s, tm2.ScaleY.r, 1.0f) * mul;
                    float sz = SampleRangeOrDefault(rng, tm2.ScaleZ.s, tm2.ScaleZ.r, 1.0f) * mul;
                    st.BodyScale = new Vector3(
                        Math.Clamp(sx, 0.02f, 5.0f),
                        Math.Clamp(sy, 0.02f, 5.0f),
                        Math.Clamp(sz, 0.02f, 5.0f));
                    st.BodyInitialRot = new Vector3(
                        rng.SamplePlusMinus(tm2.RotationX.s, tm2.RotationX.r),
                        rng.SamplePlusMinus(tm2.RotationY.s, tm2.RotationY.r),
                        rng.SamplePlusMinus(tm2.RotationZ.s, tm2.RotationZ.r));
                    break;
                }

                case EFXAttributeTypeMesh or EFXAttributeTypeGpuMesh or EFXAttributeTypeGpuMeshTrailV2:
                {
                    st.BodyKind = EfxRenderBodyKind.Mesh;
                    st.MeshMaterial = _assetCache.ResolveEntryMeshMaterial(entry);
                    if (st.MeshMaterial != null)
                    {
                        st.BaseColor0 = st.MeshMaterial.TintColor;
                    }
                    st.BodyScale = Vector3.One;
                    break;
                }

                case EFXAttributeTypeNoDraw:
                {
                    hasExplicitNoDraw = true;
                    st.BodyKind = EfxRenderBodyKind.NoDraw;
                    break;
                }
            }
        }

        if (hasExplicitNoDraw)
        {
            st.BodyKind = EfxRenderBodyKind.NoDraw;
        }
        // Unknown render bodies stay invisible until their geometry is implemented.

        return st;
    }

    /// <summary>
    /// Advances or seeks the simulation to targetLocalFrame (0-based relative to slot start)
    /// using the current world transform of the attachment bone.
    /// </summary>
    public void StepToFrame(int targetLocalFrame, Matrix4x4 currentEmitterWorldMatrix)
    {
        targetLocalFrame = Math.Clamp(targetLocalFrame, 0, 180);

        if (targetLocalFrame < CurrentSimFrame)
        {
            Reset(currentEmitterWorldMatrix);
        }

        while (CurrentSimFrame < targetLocalFrame)
        {
            CurrentSimFrame++;
            StepSingleFrame(CurrentSimFrame, currentEmitterWorldMatrix);
        }
    }

    private void StepSingleFrame(int frame, Matrix4x4 emitterWorldMatrix)
    {
        foreach (var st in _entryStates)
        {
            if (st.BodyKind == EfxRenderBodyKind.NoDraw) continue;

            // Compute entry local matrix combined with emitterWorldMatrix
            Matrix4x4 entryLocalMat =
                Matrix4x4.CreateScale(st.LocalScale) *
                Matrix4x4.CreateRotationX(st.LocalRotation.X) *
                Matrix4x4.CreateRotationY(st.LocalRotation.Y) *
                Matrix4x4.CreateRotationZ(st.LocalRotation.Z) *
                Matrix4x4.CreateTranslation(st.LocalPosition);
            Matrix4x4 entryWorldMat = entryLocalMat * emitterWorldMatrix;

            // 1. Spawn phase (ports efx_sim/behaviors/spawn.py)
            int toSpawn = EvaluateSpawnCount(st, frame);
            for (int s = 0; s < toSpawn; s++)
            {
                if (st.ActiveParticles.Count >= st.MaxAliveParticles) break;
                var p = CreateParticle(st, entryWorldMat);
                st.ActiveParticles.Add(p);
            }

            // 2. Update active particles (ports life.py, velocity3d.py, scaleanim.py, rotateanim.py, attractor.py, uvsequence.py)
            for (int idx = st.ActiveParticles.Count - 1; idx >= 0; idx--)
            {
                var p = st.ActiveParticles[idx];
                p.AgeFrames++;

                // Life evaluation
                int total = p.TotalLifeFrames;
                if (p.AgeFrames > total)
                {
                    p.Alive = false;
                    st.ActiveParticles.RemoveAt(idx);
                    continue;
                }

                float age = p.AgeFrames;
                if (p.AppearFrames > 0 && age < p.AppearFrames)
                {
                    p.LifeWeight = Math.Clamp(age / p.AppearFrames, 0f, 1f);
                }
                else if (age <= p.AppearFrames + p.KeepFrames)
                {
                    p.LifeWeight = 1.0f;
                }
                else if (p.VanishFrames > 0)
                {
                    float vanishElapsed = age - (p.AppearFrames + p.KeepFrames);
                    p.LifeWeight = Math.Clamp(1.0f - (vanishElapsed / p.VanishFrames), 0f, 1f);
                }
                else
                {
                    p.LifeWeight = 0.0f;
                }

                // Velocity3D (ports velocity3d.py: Speed & GravityRate in m/s with dt=1/60, SpeedCoef per-frame multiplier)
                if (st.HasVelocity3D)
                {
                    p.Velocity = new Vector3(p.Velocity.X, p.Velocity.Y - p.GravityAccel, p.Velocity.Z);
                    p.Velocity *= p.SpeedCoef;
                }
                if (st.HasAttractor && MathF.Abs(st.AttractorForce) > 1e-5f)
                {
                    Vector3 toAttr = st.AttractorPos - p.LocalPosition;
                    float dist = toAttr.Length();
                    if (dist > 1e-4f)
                    {
                        p.Velocity += (toAttr / dist) * (st.AttractorForce * (1.0f / 60.0f));
                    }
                }
                p.LocalPosition += p.Velocity;

                // ScaleAnim (ports scaleanim.py: additive velocity with multiplicative decay coefficient)
                if (st.HasScaleAnim)
                {
                    Vector3 delta = new Vector3(p.ScaleScalarAdd) + p.ScaleAxisAdd;
                    p.Scale = Vector3.Clamp(p.Scale + delta, new Vector3(0.01f), new Vector3(1.5f));
                    p.ScaleScalarAdd *= p.ScaleScalarCoef;
                    p.ScaleAxisAdd *= p.ScaleAxisCoef;
                }

                // RotateAnim
                if (st.HasRotateAnim)
                {
                    p.Rotation += p.AngularVelocity;
                }

                // UVSequence
                if (!string.IsNullOrEmpty(st.UvsPath) && p.UvPatternCount > 0)
                {
                    p.UvFrameCursor += p.UvPlaySpeed;
                    int offset = (int)MathF.Floor(p.UvFrameCursor);
                    bool loop = (st.UvsFlags & 2) != 0;
                    if (loop)
                    {
                        p.UvPatternIndex = p.UvPatternStart + ((offset % p.UvPatternCount + p.UvPatternCount) % p.UvPatternCount);
                    }
                    else
                    {
                        p.UvPatternIndex = p.UvPatternStart + Math.Clamp(offset, 0, p.UvPatternCount - 1);
                    }
                }

                // Update world-space trail points for RibbonFollow / RibbonLength / PolygonTrail
                if (st.BodyKind == EfxRenderBodyKind.RibbonFollow)
                {
                    Vector3 worldPt = ComputeParticleWorldPosition(st, p, entryWorldMat);
                    if (p.TrailPositions.Count > 0 && Vector3.DistanceSquared(p.TrailPositions[^1], worldPt) < 1e-4f)
                    {
                        float angle = p.AgeFrames * 0.22f;
                        worldPt += Vector3.TransformNormal(new Vector3(MathF.Cos(angle) * 0.14f, 0f, MathF.Sin(angle) * 0.14f), entryWorldMat);
                    }
                    p.TrailPositions.Add(worldPt);
                    while (p.TrailPositions.Count > st.MaxTrailPoints)
                    {
                        p.TrailPositions.RemoveAt(0);
                    }
                }
                else if (st.BodyKind is EfxRenderBodyKind.RibbonLength or EfxRenderBodyKind.PolygonTrail)
                {
                    PopulateDirectionalRibbonPoints(st, p, entryWorldMat);
                }
            }
        }
    }

    private static void PopulateDirectionalRibbonPoints(EfxEntrySimState st, SimParticle p, Matrix4x4 entryWorldMat)
    {
        p.TrailPositions.Clear();
        Matrix4x4 refMat = st.RelationType == 0 ? p.SpawnWorldMatrix : entryWorldMat;
        int n = Math.Max(2, p.RibbonDivisions);
        float len = p.RibbonLength * p.Scale.Y;
        Vector3 step = p.RibbonDir * (len / (n - 1));
        for (int i = 0; i < n; i++)
        {
            Vector3 localPt = p.LocalPosition + step * i;
            p.TrailPositions.Add(Vector3.Transform(localPt, refMat));
        }
    }

    private static int EvaluateSpawnCount(EfxEntrySimState st, int frame)
    {
        if (st.BurstCount <= 0 || frame < st.DelayFrames) return 0;

        if (st.IntervalFrames <= 0 && st.MaxLoops <= 1)
        {
            if (st.HasBurstTriggered) return 0;
            st.HasBurstTriggered = true;
            st.LoopsDone = 1;
            return st.BurstCount;
        }

        if (st.MaxLoops > 0 && st.LoopsDone >= st.MaxLoops) return 0;

        if (frame >= st.NextSpawnFrame)
        {
            st.LoopsDone++;
            st.NextSpawnFrame = frame + Math.Max(1, st.IntervalFrames);
            return st.BurstCount;
        }

        return 0;
    }

    private SimParticle CreateParticle(EfxEntrySimState st, Matrix4x4 entryWorldMat)
    {
        var rng = st.Rng;
        var p = new SimParticle
        {
            Id = st.AllocateParticleId(),
            AppearFrames = Math.Max(0, rng.RandInt(st.AppearMin, st.AppearMax)),
            KeepFrames = Math.Max(1, rng.RandInt(st.KeepMin, st.KeepMax)),
            VanishFrames = Math.Max(0, rng.RandInt(st.VanishMin, st.VanishMax)),
            Scale = Vector3.One,
            Rotation = st.BodyInitialRot,
            Color = st.BaseColor0,
            Alpha = st.BaseAlpha,
            RibbonDir = st.RibbonDirVal,
            RibbonLength = st.RibbonLengthVal,
            RibbonHalfWidth = st.RibbonWidth0 * 0.5f,
            RibbonDivisions = st.MaxTrailPoints,
            SpawnWorldMatrix = entryWorldMat
        };

        p.LocalPosition = SampleEmitterShape(st, rng);
        p.BodySize = st.BodyScale;
        if (st.RollBillboardSize)
        {
            // The reference rolls each Billboard3D dimension at particle birth.
            float scalar = rng.SamplePlusMinus(st.BillboardScalar, st.BillboardScalarRandom);
            p.BodySize = new Vector3(
                rng.SamplePlusMinus(st.BillboardSizeX, st.BillboardSizeXRandom) * scalar,
                rng.SamplePlusMinus(st.BillboardSizeY, st.BillboardSizeYRandom) * scalar,
                1f);
            p.Rotation.Z = rng.SamplePlusMinus(st.BillboardRotation, st.BillboardRotationRandom);
        }
        p.ShapeDirection = p.LocalPosition.LengthSquared() > 1e-6f
            ? Vector3.Normalize(p.LocalPosition)
            : Vector3.UnitY;

        // Velocity3D (ports velocity3d.py: Speed & GravityRate in m/s -> divide by 60 fps)
        if (st.HasVelocity3D)
        {
            const float dt = 1.0f / 60.0f;
            Vector3 dir = (st.VelocityFlags & 1) != 0 ? p.ShapeDirection : st.VelocityDir;
            float speedPerSec = rng.SamplePlusMinus(st.SpeedMin, st.SpeedMax);
            float speedCoef = rng.SamplePlusMinus(st.SpeedCoefMin, st.SpeedCoefMax);
            float gravPerSec = rng.SamplePlusMinus(st.GravityMin, st.GravityMax);

            float speedPerFrame = Math.Clamp(speedPerSec * dt, -0.25f, 0.25f);
            p.SpeedCoef = Math.Clamp(speedCoef, 0.5f, 1.2f);
            p.GravityAccel = Math.Clamp(gravPerSec * dt, -0.04f, 0.04f);
            p.Velocity = dir * speedPerFrame;
        }

        // ScaleAnim (ports scaleanim.py: SizeScalarAdd * SizeScalarCoef + SizeX/Y/ZAdd * SizeX/Y/ZAddCoef)
        if (st.HasScaleAnim)
        {
            p.ScaleScalarAdd = Math.Clamp(rng.SamplePlusMinus(st.ScaleAddOverallMin, st.ScaleAddOverallMax), -0.05f, 0.08f);
            p.ScaleScalarCoef = Math.Clamp(rng.SamplePlusMinus(st.ScaleCoefOverallMin, st.ScaleCoefOverallMax), 0.5f, 1.15f);
            p.ScaleAxisAdd = new Vector3(
                Math.Clamp(rng.SamplePlusMinus(st.ScaleAddMin.X, st.ScaleAddMax.X), -0.05f, 0.08f),
                Math.Clamp(rng.SamplePlusMinus(st.ScaleAddMin.Y, st.ScaleAddMax.Y), -0.05f, 0.08f),
                Math.Clamp(rng.SamplePlusMinus(st.ScaleAddMin.Z, st.ScaleAddMax.Z), -0.05f, 0.08f));
            p.ScaleAxisCoef = new Vector3(
                Math.Clamp(rng.SamplePlusMinus(st.ScaleCoefMin.X, st.ScaleCoefMax.X), 0.5f, 1.15f),
                Math.Clamp(rng.SamplePlusMinus(st.ScaleCoefMin.Y, st.ScaleCoefMax.Y), 0.5f, 1.15f),
                Math.Clamp(rng.SamplePlusMinus(st.ScaleCoefMin.Z, st.ScaleCoefMax.Z), 0.5f, 1.15f));
        }

        // RotateAnim
        if (st.HasRotateAnim)
        {
            p.AngularVelocity = new Vector3(
                Math.Clamp(rng.SamplePlusMinus(st.RotateAddMin.X, st.RotateAddMax.X), -0.4f, 0.4f),
                Math.Clamp(rng.SamplePlusMinus(st.RotateAddMin.Y, st.RotateAddMax.Y), -0.4f, 0.4f),
                Math.Clamp(rng.SamplePlusMinus(st.RotateAddMin.Z, st.RotateAddMax.Z), -0.4f, 0.4f));
        }

        // UVSequence init
        if (!string.IsNullOrEmpty(st.UvsPath))
        {
            p.UvSequenceIndex = Math.Max(0, st.UvsSeqNo);
            int totalPats = _assetCache.GetUvsPatternCount(st.UvsPath, p.UvSequenceIndex);
            if (totalPats > 0)
            {
                int pMin = Math.Clamp(st.UvsPatternMin, 0, totalPats - 1);
                int pMax = st.UvsPatternMax > pMin ? Math.Min(st.UvsPatternMax, totalPats) : totalPats;
                p.UvPatternStart = pMin;
                p.UvPatternCount = Math.Max(1, pMax - pMin);
                p.UvPlaySpeed = Math.Clamp(rng.Uniform(st.UvsPlaySpeedMin, st.UvsPlaySpeedMax), 0.1f, 4.0f);
                if ((st.UvsFlags & 8) != 0) // RandomFirstFrame
                {
                    p.UvFrameCursor = rng.RandInt(0, p.UvPatternCount - 1);
                }
                p.UvPatternIndex = p.UvPatternStart + ((int)p.UvFrameCursor % p.UvPatternCount);
            }
        }

        // Initialize trail points for RibbonFollow / RibbonLength / PolygonTrail
        if (st.BodyKind == EfxRenderBodyKind.RibbonFollow)
        {
            Vector3 worldPt = ComputeParticleWorldPosition(st, p, entryWorldMat);
            p.TrailPositions.Add(worldPt);
        }
        else if (st.BodyKind is EfxRenderBodyKind.RibbonLength or EfxRenderBodyKind.PolygonTrail)
        {
            PopulateDirectionalRibbonPoints(st, p, entryWorldMat);
        }

        return p;
    }

    private static Vector3 ComputeParticleWorldPosition(EfxEntrySimState st, SimParticle p, Matrix4x4 currentEntryWorldMat)
    {
        Matrix4x4 refMat = st.RelationType == 0 ? p.SpawnWorldMatrix : currentEntryWorldMat;
        return Vector3.Transform(p.LocalPosition, refMat);
    }

    /// <summary>
    /// Renders all active particles, ribbons, polygon trails, and 3D VFX meshes in this simulator.
    /// </summary>
    public void Render(
        GL gl,
        Shader vfxShader,
        Shader meshShader,
        GpuTexture defaultWhiteTex,
        uint dynamicQuadVao,
        uint dynamicQuadVbo,
        uint dynamicRibbonVao,
        uint dynamicRibbonVbo,
        int maxRibbonVerts,
        Matrix4x4 currentEmitterWorldMatrix,
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 cameraPos)
    {
        // The reference preview's implemented bodies use straight alpha blending.
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.DepthMask(false);
        gl.Disable(EnableCap.CullFace);

        foreach (var st in _entryStates)
        {
            if (st.BodyKind == EfxRenderBodyKind.NoDraw || st.ActiveParticles.Count == 0)
                continue;

            Matrix4x4 entryLocalMat =
                Matrix4x4.CreateScale(st.LocalScale) *
                Matrix4x4.CreateRotationX(st.LocalRotation.X) *
                Matrix4x4.CreateRotationY(st.LocalRotation.Y) *
                Matrix4x4.CreateRotationZ(st.LocalRotation.Z) *
                Matrix4x4.CreateTranslation(st.LocalPosition);
            Matrix4x4 entryWorldMat = entryLocalMat * currentEmitterWorldMatrix;

            if (st.BodyKind == EfxRenderBodyKind.Mesh && st.MeshMaterial?.Mesh != null)
            {
                RenderMeshParticles(gl, st, meshShader, defaultWhiteTex, entryWorldMat, view, projection);
            }
            else if (st.BodyKind is EfxRenderBodyKind.RibbonFollow or EfxRenderBodyKind.RibbonLength or EfxRenderBodyKind.PolygonTrail)
            {
                RenderRibbonParticles(gl, st, vfxShader, defaultWhiteTex, dynamicRibbonVao, dynamicRibbonVbo, maxRibbonVerts, view, projection);
            }
            else
            {
                RenderQuadParticles(gl, st, vfxShader, defaultWhiteTex, dynamicQuadVao, dynamicQuadVbo, entryWorldMat, view, projection, cameraPos);
            }
        }

        gl.DepthMask(true);
        gl.Disable(EnableCap.Blend);
    }

    private void RenderMeshParticles(
        GL gl,
        EfxEntrySimState st,
        Shader meshShader,
        GpuTexture defaultWhiteTex,
        Matrix4x4 currentEntryWorldMat,
        Matrix4x4 view,
        Matrix4x4 projection)
    {
        var matInfo = st.MeshMaterial!;
        var mesh = matInfo.Mesh!;

        meshShader.Use();
        meshShader.SetMatrix4("u_View", view);
        meshShader.SetMatrix4("u_Projection", projection);
        meshShader.SetInt("u_HasSkinning", 0);

        if (matInfo.Texture != null)
        {
            matInfo.Texture.Bind(TextureUnit.Texture0);
            meshShader.SetInt("u_HasTexture", 1);
            meshShader.SetInt("u_AlbedoMap", 0);
        }
        else
        {
            defaultWhiteTex.Bind(TextureUnit.Texture0);
            meshShader.SetInt("u_HasTexture", 0);
        }

        foreach (var p in st.ActiveParticles)
        {
            if (!p.Alive || p.LifeWeight <= 0.01f) continue;

            Matrix4x4 parentMat = st.RelationType == 0 ? p.SpawnWorldMatrix : currentEntryWorldMat;
            Matrix4x4 pScale = Matrix4x4.CreateScale(p.BodySize * p.Scale);
            Matrix4x4 pRot =
                Matrix4x4.CreateRotationX(p.Rotation.X) *
                Matrix4x4.CreateRotationY(p.Rotation.Y) *
                Matrix4x4.CreateRotationZ(p.Rotation.Z);
            Matrix4x4 pTrans = Matrix4x4.CreateTranslation(p.LocalPosition);

            Matrix4x4 worldMat = pScale * pRot * pTrans * parentMat;
            meshShader.SetMatrix4("u_Model", worldMat);

            Vector4 baseCol = new Vector4(p.Color.X, p.Color.Y, p.Color.Z, p.Alpha * p.LifeWeight);
            meshShader.SetUniform("u_BaseColor", baseCol);
            meshShader.SetUniform("u_EmissionColor", new Vector3(p.Color.X, p.Color.Y, p.Color.Z));
            meshShader.SetFloat("u_EmissionIntensity", matInfo.EmissionIntensity * p.LifeWeight);

            // Filter submeshes by PartsStartNo [Min, Max) if specified
            if (matInfo.PartsStartMin >= 0 && mesh.Submeshes.Count > 1)
            {
                int lo = Math.Clamp(matInfo.PartsStartMin, 0, mesh.Submeshes.Count - 1);
                int hi = matInfo.PartsStartMax > lo
                    ? Math.Min(matInfo.PartsStartMax, mesh.Submeshes.Count)
                    : lo + 1;
                for (int s = lo; s < hi; s++)
                {
                    mesh.RenderSubmesh(s);
                }
            }
            else
            {
                mesh.Render();
            }
        }
    }

    private unsafe void RenderQuadParticles(
        GL gl,
        EfxEntrySimState st,
        Shader vfxShader,
        GpuTexture defaultWhiteTex,
        uint dynamicQuadVao,
        uint dynamicQuadVbo,
        Matrix4x4 currentEntryWorldMat,
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 cameraPos)
    {
        vfxShader.Use();
        vfxShader.SetMatrix4("u_View", view);
        vfxShader.SetMatrix4("u_Projection", projection);
        vfxShader.SetInt("u_IsRibbon", 0);

        gl.BindVertexArray(dynamicQuadVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, dynamicQuadVbo);

        Span<float> quadVerts = stackalloc float[6 * 9];

        foreach (var p in st.ActiveParticles)
        {
            if (!p.Alive || p.LifeWeight <= 0.01f) continue;

            GpuTexture? uvsTex = !string.IsNullOrEmpty(st.UvsPath)
                ? _assetCache.GetUvsTexture(st.UvsPath, p.UvSequenceIndex, p.UvPatternIndex)
                : null;
            (uvsTex ?? defaultWhiteTex).Bind(TextureUnit.Texture0);
            vfxShader.SetInt("u_HasTexture", uvsTex != null ? 1 : 0);
            vfxShader.SetInt("u_ParticleTex", 0);

            var (uvMin, uvMax) = !string.IsNullOrEmpty(st.UvsPath)
                ? _assetCache.SampleUvsRect(st.UvsPath, p.UvSequenceIndex, p.UvPatternIndex)
                : (Vector2.Zero, Vector2.One);

            float alpha = p.Alpha * p.LifeWeight;
            FillQuadVertices(quadVerts, uvMin, uvMax, alpha);

            fixed (float* ptr = quadVerts)
            {
                gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(quadVerts.Length * sizeof(float)), ptr);
            }

            Vector3 worldPos = ComputeParticleWorldPosition(st, p, currentEntryWorldMat);
            Matrix4x4 modelMat;
            float sizeX = p.BodySize.X * p.Scale.X;
            float sizeY = p.BodySize.Y * p.Scale.Y;

            if (st.BodyKind is EfxRenderBodyKind.Billboard3D or EfxRenderBodyKind.Point)
            {
                Vector3 forward = cameraPos - worldPos;
                if (forward.LengthSquared() < 1e-4f) forward = Vector3.UnitZ;
                else forward = Vector3.Normalize(forward);

                Vector3 right = Vector3.Cross(Vector3.UnitY, forward);
                if (right.LengthSquared() < 1e-4f) right = Vector3.UnitX;
                else right = Vector3.Normalize(right);

                Vector3 up = Vector3.Cross(forward, right);

                Matrix4x4 billboardRot = new Matrix4x4(
                    right.X, right.Y, right.Z, 0,
                    up.X, up.Y, up.Z, 0,
                    forward.X, forward.Y, forward.Z, 0,
                    0, 0, 0, 1
                );

                modelMat =
                    Matrix4x4.CreateScale(sizeX, sizeY, 1.0f) *
                    Matrix4x4.CreateRotationZ(p.Rotation.Z) *
                    billboardRot *
                    Matrix4x4.CreateTranslation(worldPos);
            }
            else
            {
                // 3D oriented Polygon / GpuPolygon
                Matrix4x4 parentMat = st.RelationType == 0 ? p.SpawnWorldMatrix : currentEntryWorldMat;
                modelMat =
                    Matrix4x4.CreateScale(sizeX, sizeY, 1.0f) *
                    Matrix4x4.CreateRotationX(p.Rotation.X) *
                    Matrix4x4.CreateRotationY(p.Rotation.Y) *
                    Matrix4x4.CreateRotationZ(p.Rotation.Z) *
                    Matrix4x4.CreateTranslation(p.LocalPosition) *
                    parentMat;
            }

            vfxShader.SetMatrix4("u_Model", modelMat);
            vfxShader.SetUniform("u_TintColor", new Vector4(p.Color.X, p.Color.Y, p.Color.Z, alpha));
            vfxShader.SetFloat("u_Intensity", 1.0f);

            gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        }

        gl.BindVertexArray(0);
    }

    private unsafe void RenderRibbonParticles(
        GL gl,
        EfxEntrySimState st,
        Shader vfxShader,
        GpuTexture defaultWhiteTex,
        uint dynamicRibbonVao,
        uint dynamicRibbonVbo,
        int maxRibbonVerts,
        Matrix4x4 view,
        Matrix4x4 projection)
    {
        vfxShader.Use();
        vfxShader.SetMatrix4("u_Model", Matrix4x4.Identity);
        vfxShader.SetMatrix4("u_View", view);
        vfxShader.SetMatrix4("u_Projection", projection);
        vfxShader.SetInt("u_IsRibbon", 1);

        gl.BindVertexArray(dynamicRibbonVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, dynamicRibbonVbo);

        List<float> stripFloats = new(128 * 9);

        foreach (var p in st.ActiveParticles)
        {
            if (!p.Alive || p.LifeWeight <= 0.01f || p.TrailPositions.Count < 2) continue;

            GpuTexture? uvsTex = !string.IsNullOrEmpty(st.UvsPath)
                ? _assetCache.GetUvsTexture(st.UvsPath, p.UvSequenceIndex, p.UvPatternIndex)
                : null;
            (uvsTex ?? defaultWhiteTex).Bind(TextureUnit.Texture0);
            vfxShader.SetInt("u_HasTexture", uvsTex != null ? 1 : 0);
            vfxShader.SetInt("u_ParticleTex", 0);

            stripFloats.Clear();
            int count = Math.Min(p.TrailPositions.Count, maxRibbonVerts / 2);

            for (int i = 0; i < count; i++)
            {
                float t = count > 1 ? (float)i / (count - 1) : 0f;
                Vector3 pt = p.TrailPositions[i];

                Vector3 tangent;
                if (i < count - 1) tangent = p.TrailPositions[i + 1] - pt;
                else tangent = pt - p.TrailPositions[i - 1];

                if (tangent.LengthSquared() < 1e-6f) tangent = Vector3.UnitX;
                else tangent = Vector3.Normalize(tangent);

                Vector3 side = Vector3.Cross(tangent, Vector3.UnitY);
                if (side.LengthSquared() < 1e-4f) side = Vector3.Cross(tangent, Vector3.UnitZ);
                side = Vector3.Normalize(side);

                float halfWidth = 0.5f * (st.RibbonWidth0 * (1f - t) + st.RibbonWidth1 * t);
                Vector3 v0 = pt - side * halfWidth;
                Vector3 v1 = pt + side * halfWidth;

                Vector4 col = Vector4.Lerp(st.BaseColor1, st.BaseColor0, t);
                float alpha = MathF.Sin(t * MathF.PI) * p.Alpha * p.LifeWeight;

                stripFloats.AddRange([v0.X, v0.Y, v0.Z, t, 0f, col.X, col.Y, col.Z, alpha]);
                stripFloats.AddRange([v1.X, v1.Y, v1.Z, t, 1f, col.X, col.Y, col.Z, alpha]);
            }

            if (stripFloats.Count == 0) continue;
            var arr = stripFloats.ToArray();
            fixed (float* ptr = arr)
            {
                gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(arr.Length * sizeof(float)), ptr);
            }

            vfxShader.SetUniform("u_TintColor", Vector4.One);
            vfxShader.SetFloat("u_Intensity", 1.0f);
            gl.DrawArrays(PrimitiveType.TriangleStrip, 0, (uint)(arr.Length / 9));
        }

        gl.BindVertexArray(0);
    }

    private static void FillQuadVertices(Span<float> dst, Vector2 uvMin, Vector2 uvMax, float alpha)
    {
        float u0 = uvMin.X, v0 = uvMin.Y;
        float u1 = uvMax.X, v1 = uvMax.Y;

        // Triangle 1
        SetVert(dst, 0, -0.5f, -0.5f, 0f, u0, v1, 1f, 1f, 1f, alpha);
        SetVert(dst, 1,  0.5f, -0.5f, 0f, u1, v1, 1f, 1f, 1f, alpha);
        SetVert(dst, 2,  0.5f,  0.5f, 0f, u1, v0, 1f, 1f, 1f, alpha);
        // Triangle 2
        SetVert(dst, 3, -0.5f, -0.5f, 0f, u0, v1, 1f, 1f, 1f, alpha);
        SetVert(dst, 4,  0.5f,  0.5f, 0f, u1, v0, 1f, 1f, 1f, alpha);
        SetVert(dst, 5, -0.5f,  0.5f, 0f, u0, v0, 1f, 1f, 1f, alpha);
    }

    private static void SetVert(Span<float> dst, int vertIdx, float x, float y, float z, float u, float v, float r, float g, float b, float a)
    {
        int o = vertIdx * 9;
        dst[o + 0] = x; dst[o + 1] = y; dst[o + 2] = z;
        dst[o + 3] = u; dst[o + 4] = v;
        dst[o + 5] = r; dst[o + 6] = g; dst[o + 7] = b; dst[o + 8] = a;
    }

    private static Vector3 SampleEmitterShape(EfxEntrySimState st, EfxDetRng rng)
    {
        float Pick(float lo, float hi) => rng.Uniform(lo, hi);
        float DivideOrPick(int count, float start, float span, bool periodic)
        {
            if (count <= 0) return start + span * rng.NextFloat();
            if (count == 1) return start;
            int index = rng.RandInt(0, count - 1);
            return start + span * index / (periodic ? count : count - 1);
        }

        float x, y, z;
        switch ((int)st.EmitterShape)
        {
            case 2: // Cylinder: the Y range is signed height; X/Z are radial spans.
            {
                float start = st.ShapeUseExtension ? st.ShapeHorizontalStart : 0f;
                float span = st.ShapeUseExtension ? st.ShapeHorizontalSpan : 2f * MathF.PI;
                float az = DivideOrPick(st.ShapeDivideHorizontal, start, span, true);
                y = st.ShapeDivideVertical > 0
                    ? DivideOrPick(st.ShapeDivideVertical, st.RangeYMin,
                        st.RangeYMax - st.RangeYMin, false)
                    : Pick(st.RangeYMin, st.RangeYMax);
                float heightSpan = st.RangeYMax - st.RangeYMin;
                float t = heightSpan == 0f ? 0f : (y - st.RangeYMin) / heightSpan;
                float taper = st.ShapeUseExtension
                    ? st.ShapeVerticalStart + st.ShapeVerticalSpan * t : 1f;
                x = MathF.Cos(az) * Pick(st.RangeXMin, st.RangeXMax) * taper;
                z = MathF.Sin(az) * Pick(st.RangeZMin, st.RangeZMax) * taper;
                break;
            }
            case 1: // Sphere: independent axial radii and angular sweeps.
            {
                float hStart = st.ShapeUseExtension ? st.ShapeHorizontalStart : 0f;
                float hSpan = st.ShapeUseExtension ? st.ShapeHorizontalSpan : 2f * MathF.PI;
                float vStart = st.ShapeUseExtension ? st.ShapeVerticalStart : -MathF.PI / 2f;
                float vSpan = st.ShapeUseExtension ? st.ShapeVerticalSpan : MathF.PI;
                float az = DivideOrPick(st.ShapeDivideHorizontal, hStart, hSpan, true);
                float polar = DivideOrPick(st.ShapeDivideVertical, vStart, vSpan, false);
                x = MathF.Cos(az) * MathF.Cos(polar) * Pick(st.RangeXMin, st.RangeXMax);
                y = MathF.Sin(polar) * Pick(st.RangeYMin, st.RangeYMax);
                z = MathF.Sin(az) * MathF.Cos(polar) * Pick(st.RangeZMin, st.RangeZMax);
                break;
            }
            default: // Box: signed axes; the reference uses a joint hollow volume.
            {
                float xLo = MathF.Max(0f, st.RangeXMin), xHi = MathF.Max(0f, st.RangeXMax);
                float yLo = MathF.Max(0f, st.RangeYMin), yHi = MathF.Max(0f, st.RangeYMax);
                float zLo = MathF.Max(0f, st.RangeZMin), zHi = MathF.Max(0f, st.RangeZMax);
                x = y = z = 0f;
                for (int attempt = 0; attempt < 64; attempt++)
                {
                    x = xLo == xHi ? xHi * (rng.NextFloat() < 0.5f ? 1f : -1f) : Pick(-xHi, xHi);
                    y = yLo == yHi ? yHi * (rng.NextFloat() < 0.5f ? 1f : -1f) : Pick(-yHi, yHi);
                    z = zLo == zHi ? zHi * (rng.NextFloat() < 0.5f ? 1f : -1f) : Pick(-zHi, zHi);
                    if (MathF.Abs(x) >= xLo || MathF.Abs(y) >= yLo || MathF.Abs(z) >= zLo) break;
                }
                float t = yHi == 0f ? 0.5f : (y + yHi) / (2f * yHi);
                if (st.ShapeUseExtension)
                {
                    x *= st.ShapeHorizontalStart + st.ShapeHorizontalSpan * t;
                    z *= st.ShapeVerticalStart + st.ShapeVerticalSpan * t;
                }
                break;
            }
        }
        return new Vector3(x, y, z);
    }

    private static Vector4 ColorToVec4(ReeLib.via.Color c0, ReeLib.via.Color c1)
    {
        // The reference behavior uses Color as the base color. ColorRange has
        // unverified semantics and must not silently replace black or zero.
        var c = c0;
        return new Vector4(
            c.R / 255f,
            c.G / 255f,
            c.B / 255f,
            c.A / 255f
        );
    }

    private static float SampleRangeOrDefault(EfxDetRng rng, float s, float r, float defaultVal)
    {
        if (MathF.Abs(s) < 1e-5f && MathF.Abs(r) < 1e-5f) return defaultVal;
        float val = rng.SamplePlusMinus(s, r);
        return MathF.Abs(val) < 1e-4f ? defaultVal : MathF.Abs(val);
    }

    private static Vector3 AxisTypeToVec3(int axis) => axis switch
    {
        0 => Vector3.UnitX,
        1 => Vector3.UnitY,
        2 => Vector3.UnitZ,
        3 => -Vector3.UnitX,
        4 => -Vector3.UnitY,
        5 => -Vector3.UnitZ,
        _ => Vector3.UnitZ
    };
}
