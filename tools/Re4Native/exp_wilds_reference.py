"""Wilds particle values against the reference: the RE4 runtime spec (RE4-EFX-RUNTIME.zh-CN.md) and refsim.

    python -I -X utf8 -u exp_wilds_reference.py                  # all scenarios, each in a child process
    python -I -X utf8 -u exp_wilds_reference.py run <scenario>   # one scenario in this process (JSON on stdout)

MonsterHunterWilds.exe builds and ticks the emitter (mhwilds_emitter.py); the expected values are computed
from the entry's field values with the RE4 formulas. Field values are ReeLib's reading of the file (the
MHWilds schema in VfxStudio.Core), copied into ENTRIES.

Checks (spec section in brackets):
  spawn    batch ticks and sizes from EmitterDelayFrame / IntervalFrame / SpawnNum / LoopNum (§2.3.2), per-batch
           draws (salt 1, key 0) with the state before the batch's advance, and E.state after each batch tick.
           Two observations outside the spec: a batch only fills slots free at the end of the previous tick and
           still uses up a loop; the first batch draws with xs(seed) and does not advance the state (RE4 advances
           it, so its first batch sees xs(xs(seed))); later batches draw, then advance
  life     Life draws (salt 0x29) and every tick's phase code and 8-bit alpha (§2.4.2 mode 0); exp_emitter.check
  motion   every tick: gvel += n*accel; pos += (vel + (0,1,0)*gvel)*n; vel *= powf(coef, n); exp_emitter.check.
           With Velocity3D Flags bit 2 the displacement slot (+0x4c) moves instead of the position
  shape    spawn position from EmitterShape3D (salt 0x30, §3.1): Box, Sphere, Cylinder, with and without
           UseExtension, and the divide modes read from re4.exe: Sphere / Cylinder take a, b from the shape counter
           c (3d231b0: b index = c % Vn, a index = (c / Vn + c % Vn) % Hn, a = index / Hn when H.span is exactly
           the float 2pi, else index / (Hn - 1); one cell -> 0.5); Box takes the RangeDivideAxis coordinate as
           (c % Dn) / (Dn - 1) * 2 - 1 (3d223c0, own counter wrapping at Dn), and its shell test cascades from the
           first other axis to the second. RangeDivideNum is not read by Sphere / Cylinder, H / V nums not by Box.
           DivideEquidistant cylinders use equidistant_ref.py (the engine's arc-length table); no corpus entry with
           it runs natively, so the eq_* scenarios set the flag in a copy of a working entry (see EQUIDISTANT)
  vel      Velocity3D spawn draws (salt 0x23): Normal direction, Speed, GravityRate, SpeedCoef (§3.2, §1.6.3)
  bb       TypeBillboard3D spawn draws (salt 0x0f): SizeScalar, SizeX, SizeY, Rotation, each skipped at spread 0
  color    with Billboard3D Flags bit 4: one more draw u first, colour byte = trunc(lerp(Color, ColorRange, u)),
           one u for all four channels (§4.2); every tick the colour stays the birth colour
  scale    ScaleAnim (salt 0x27): every tick's U slot = f(age - age0, base, SizeScalarAdd, SizeScalarCoef) (§3.3)
  rotate   RotateAnim (salt 0x25): X speed, coef and sign draws; every tick angle += n*v, wrapped to [0, 2pi),
           then v *= powf(c, n) (§3.4)
  uv       UVSequence (salt 0x2b, §4.1): SequenceNo, PatternNo (start frame) and PlaySpeed draws; every tick
           acc += dt, steps = int(acc/T) with T = (1/60)/speed, acc = frac(acc/T)/60; PlayMode 0 static, 1 loop
           to 0, 2 clamp to the last pattern and kill (the particle is gone the next tick). UVSequenceModifier
           replaces T from the next tick: speed = m*Init + (1-m)*Final, m starting at 1 and *= powf(c, n). The
           .uvs is synthetic (UVS patterns per sequence, see run)
  rgb      RgbCommon / RgbWater life envelopes (§4.9) in the per-particle record: per channel remain, duration
           and phase code (5 Keep, 8 Vanish, 9 Terminate), and the channel factor (Keep 1, Vanish remain/V,
           Terminate 0, times GtoB for RgbWater's GtoB). Draws: the first life channel's Appear, Keep, Vanish are
           draws 1..3 (salts 0x7625a743 / 0xe107eec2, key = slot)
  noise    Noise, read from re4.exe 3dc7050 / 3dc6d50 and checked against RE4 natively (5,424 ticks bit-exact):
           8 draws at birth (salt 0xd17f868f, key = slot): high and low phase (2pi u), four coefficients (2u - 1),
           a 2D direction normalized; every tick including the birth tick C = cos(a) * LW + cos(b) * HW and
           S = sin(a) * LW + sin(b) * HW (LW = LowFrequencyWidth.center + .spread * k3, HW = HighFrequencyWidth.center + .spread * k1),
           the particle's noise offset (+0x40, RE4 +0x34) = (C * dir.x, C * dir.y, S) for 3D, then
           a += (LowFrequency.center + .spread * k2) * dt * 2pi, b += (HighFrequency.center + .spread * k0) * dt * 2pi, fmodf to 2pi
"""
import hashlib
import json
import math
import os
import struct
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np  # noqa: E402

F = np.float32
ROOT = os.environ.get("MHWILDS_EXTRACT",
                      r"E:\Program\Steam\steamapps\common\MonsterHunterWilds\MHWILDS_EXTRACT\natives\STM\Art\VFX")
PI = 3.1415927
# Field values as ReeLib reads them. shape: ranges as (inner, outer), H / V as (start, span), LocalRotation in radians
ENTRIES = {
    # Spawn Max 6, SpawnNum 2, IntervalFrame 10, LoopNum 3; Billboard3D SizeScalar 1±0.3, Rotation 0±π; Velocity3D
    # Normal, Speed 1.2±0.5, Offset (0, 0.245, −1), Size (2, 1.5, 2), GravityRate 1.2; ScaleAnim SizeScalarAdd
    # 0.065±0.01; Life 0 / 4 / 45; UVSequence SequenceNo 2, PatternNo 5..11; EmitterShape3D Cylinder, UseExtension
    "em2": dict(path=r"EffectEditor\Enemy\em0002\11_em0002_00_800.efx.5571972", off=173,
                delay=0, interval=10, loops=3, spawn_num=2, cap=6, life=((0, 0), (4, 4), (45, 45)),
                vel=dict(offset=(0, 0.245, -1), size=(2, 1.5, 2), speed=(1.2, 0.5), gravity=(1.2, 0), coef=(1, 0)),
                bb=dict(U=(1, 0.3), X=(1, 0), Y=(1, 0), rot=(0, PI)), scale_add=(0.065, 0.01),
                uv=dict(seq=(2, 3), pat=(5, 11), speed=(1, 1.25), mode=2),
                shape=dict(kind="cylinder", X=(0, 0.3), Y=(-0.5, 0.3), Z=(0, 0.3), H=(0, PI), V=(1.25, -0.5), ext=True, rot=(math.pi / 2, 0, 0))),
    # Spawn Max 3, SpawnNum 1, IntervalFrame 2, LoopNum 8; Billboard3D SizeScalar 2±1, SizeY 1±0.2; Velocity3D Normal,
    # Speed 1, Size 1.1; Life 0 / 1 / 5; EmitterShape3D Sphere 1..2, UseExtension, V −10°..90°
    "jimen": dict(path=r"EffectEditor\Common\slinger\11_pl_slinger_055_jimen.efx.5571972", off=15735,
                  delay=0, interval=2, loops=8, spawn_num=1, cap=3, life=((0, 0), (1, 1), (5, 5)),
                  vel=dict(offset=(0, 0, 0), size=(1.1, 1.1, 1.1), speed=(1, 0), gravity=(0, 0), coef=(1, 0)),
                  bb=dict(U=(2, 1), X=(1, 0), Y=(1, 0.2), rot=(0, PI)), uv=dict(seq=(0, 1), pat=(0, 64), speed=(0.5, 0.5), mode=1),
                  rgb=dict(type=242, salt=0x7625A743, channels=[dict(base=0, life=((0, 0), (1, 1), (1, 10)), factor=6, scale=1.0, draws=True),
                                                               dict(base=3, life=((0, 0), (2, 2), (0, 0)))]),
                  shape=dict(kind="sphere", X=(1, 2), Y=(1, 2), Z=(1, 2), H=(0, 6.2831855), V=(-0.17453294, 1.7453294), ext=True, rot=(math.pi / 2, 0, 0)),
                  noise=dict(type=112, lf=(5, 0.5), lw=(0.1, 0.1), hf=(11, 0.05), hw=(0.05, 0.01))),
    # Spawn Max 3, SpawnNum 3, LoopNum 1; Velocity3D Normal, Speed 0.1, SpeedCoef 0.95, Offset (0, −0.06, 0);
    # Life 0 / 5 / 10..20; EmitterShape3D Cylinder, RangeDivideVerticalNum 3 (and RangeDivideNum 2, unused by Cylinder)
    "s053": dict(path=r"EffectEditor\Common\slinger\11_pl_slinger_053.efx.5571972", off=6635,
                 delay=0, interval=0, loops=1, spawn_num=3, cap=3, life=((0, 0), (5, 5), (10, 20)),
                 vel=dict(offset=(0, -0.06, 0), size=(1.1, 1.05, 1.1), speed=(0.1, 0), gravity=(0, 0), coef=(0.95, 0)),
                 bb=dict(U=(0.75, 0.25), X=(0.8, 0), Y=(1, 0), rot=(0, PI)),
                 shape=dict(kind="cylinder", X=(0.01, 0.049999997), Y=(-0.1, 0.1), Z=(0.01, 0.049999997), H=(0, 6.2831855), V=(1, 0), ext=False, rot=(math.pi / 2, 0, 0), div=2, axis=2, vn=3),
                 uv=dict(seq=(0, 1), pat=(20, 41), speed=(1, 1), mode=1)),
    # FixRandomGenerator seed 1; Spawn Max 6, EmitterDelayFrame 115, IntervalFrame 30..60, LoopNum 0 (endless);
    # Billboard3D SizeScalar 0.1±0.025; Velocity3D Flags 4, Normal, Offset (0, 1.8, −0.05), Size (3, 1, 3),
    # GravityRate −0.004; RotateAnim Flags 1, AddX 1°±0.5°, CoefX 0.99; ScaleAnim Add 0.002, Coef 0.99;
    # Life 15 / 165 / 0; EmitterShape3D Cylinder, RangeDivideHorizontalNum 2; Noise
    "em21": dict(path=r"EffectEditor\Enemy\em0021\11_em0021_00_206.efx.5571972", off=5845,
                 delay=115, interval=(30, 60), loops=None, spawn_num=1, cap=6, life=((15, 15), (165, 165), (0, 0)),
                 vel=dict(offset=(0, 1.8, -0.049999997), size=(3, 1, 3), speed=(0.20000014, 0), gravity=(-0.004, 0),
                          coef=(1, 0)), flags4=True,
                 shape=dict(kind="cylinder", X=(0.020000003, 0.1), Y=(0.54999995, 0.54999995), Z=(0.020000003, 0.1), H=(0, 6.2831855), V=(1, 0), ext=False, rot=(0, 0, 0), hn=2),
                 noise=dict(type=112, lf=(0.05, 0.04), lw=(0.060000006, 0), hf=(0.05, 0.04), hw=(0.1, 0)),
                 bb=dict(U=(0.1, 0.025), X=(1, 0), Y=(1, 0), rot=(0, PI)), scale_add=(0.002, 0), scale_coef=(0.99, 0),
                 rotate=dict(add=(0.017453294, 0.008726647), coef=(0.99, 0), sign=True),
                 uv=dict(seq=(2, 3), pat=(0, 64), speed=(0.5, 0.5), mode=1, modifier=dict(init=1, final=0.3, coef=0.98))),
    # FixRandomGenerator seed 406056259 (state chain with a large seed); Spawn Max 6, IntervalFrame 2, endless;
    # Life 0 / 2 / 8; no Velocity3D; EmitterShape3D Sphere with RangeDivideNum 3 (unused by Sphere)
    "s000": dict(path=r"EffectEditor\Common\slinger\11_pl_slinger_000.efx.5571972", off=6319,
                 delay=0, interval=2, loops=None, spawn_num=1, cap=6, life=((0, 0), (2, 2), (8, 8)),
                 bb=dict(U=(0.1, 0), X=(1, 0), Y=(1, 0), rot=(0, 0)), uv=dict(seq=(1, 2), pat=(0, 1), speed=(1, 1), mode=0),
                 shape=dict(kind="sphere", X=(0, 0.015), Y=(0, 0.075), Z=(0, 0.015), H=(0, 6.2831855), V=(-1.5707964, 3.1415927), ext=False, rot=(math.pi / 2, 0, 0), div=3, axis=1)),
    # Billboard3D Flags 16 (random colour between 0xffffffff and 0xffb3b3b3), SizeScalar 2±0.4; Spawn 8 at once;
    # Velocity3D Normal, Speed 3.5±2, GravityRate 0.7±0.1; ScaleAnim Add 0.04±0.01; Life 0 / 15 / 20..25;
    # UVSequence PlayMode 2, PlaySpeed 1..1.5; RgbWater GtoB life 0 / 20 / 20..30 (GtoB 0.7); EmitterShape3D Sphere,
    # UseExtension, RangeDivideHorizontalNum 8
    "em9": dict(path=r"EffectEditor\Enemy\em0009\11_em0009_00_204.efx.5571972", off=473,
                delay=0, interval=0, loops=1, spawn_num=8, cap=8, life=((0, 0), (15, 15), (20, 25)),
                vel=dict(offset=(0, 0.5, 0), size=(2, 1.8, 2), speed=(3.5, 2), gravity=(0.7, 0.1), coef=(1, 0)),
                shape=dict(kind="sphere", X=(1, 1.2), Y=(0.1, 0.1), Z=(1, 1.2), H=(0, 6.2831855), V=(0, 1.5707964), ext=True, rot=(0, 0, 0), hn=8),
                bb=dict(U=(2, 0.4), X=(1, 0), Y=(1, 0), rot=(0, 0), color=(0xFFFFFFFF, 0xFFB3B3B3)),
                scale_add=(0.04, 0.01), uv=dict(seq=(4, 5), pat=(0, 6), speed=(1, 1.5), mode=2),
                rgb=dict(type=244, salt=0xE107EEC2, channels=[dict(base=0, life=None, factor=9, scale=1.0),
                                                               dict(base=3, life=None, factor=10, scale=1.0),
                                                               dict(base=6, life=((0, 0), (20, 20), (20, 30)), factor=11,
                                                                    scale=0.7, draws=True)])),
    # Shape-only entries (only the spawn position is compared): Box RangeDivideNum 3 on Z; Cylinder with
    # RangeDivideHorizontalNum 4 over 140° (H.span not 2pi); Cylinder H 7 / V 7 over 250°, LocalRotation.Y 25°
    "box_div": dict(path=r"EffectEditor\Enemy\em0005\11_em0005_00_302.efx.5571972", off=6010,
                    shape=dict(kind="box", X=(0, 0), Y=(0, 0), Z=(1, 1), H=(1, 0), V=(1, 0), ext=False, rot=(0, 0, 0), div=3, axis=2)),
    "cyl_part": dict(path=r"EffectEditor\Enemy\em0005\11_em0005_00_204.efx.5571972", off=5496,
                     shape=dict(kind="cylinder", X=(0.7, 1), Y=(0, 0.2), Z=(1, 1.3), H=(0.34906587, 2.4434612), V=(1, 0), ext=True, rot=(0, 0, 0), hn=4)),
    "cyl_hv": dict(path=r"EffectEditor\Enemy\em0009\11_em0009_00_401.efx.5571972", off=16886,
                   shape=dict(kind="cylinder", X=(0.5, 0.9), Y=(0, 0.19999999), Z=(0.5, 0.9), H=(0, 4.363323), V=(1, 0), ext=True, rot=(0, 0.43633235, 0), hn=7, vn=7)),
    # Equidistant cylinders: the same three cylinders with DivideEquidistant set in a temporary copy (no Wilds
    # corpus entry with it runs natively: 143 of 158 stop in other steps, 15 have no particles). tr = Transform3D
    # LocalPosition, part of the matrix the table is built with. cyl_part: ellipse 0.85 / 1.15, H over 140°,
    # UseExtension; em21: circle, H 2 over a full turn, UseExtension off; em2: circle, angle drawn, LocalRotation.X 90°
    "eq_cyl_part": dict(path=r"EffectEditor\Enemy\em0005\11_em0005_00_204.efx.5571972", off=5496, equidistant=(0.7, 1),
                        shape=dict(kind="cylinder", X=(0.7, 1), Y=(0, 0.2), Z=(1, 1.3), H=(0.34906587, 2.4434612), V=(1, 0),
                                   ext=True, rot=(0, 0, 0), hn=4, equi=True, tr=(0, -0.03, 0))),
    "eq_em21": dict(path=r"EffectEditor\Enemy\em0021\11_em0021_00_206.efx.5571972", off=5845, equidistant=(0.020000003, 0.1),
                    shape=dict(kind="cylinder", X=(0.020000003, 0.1), Y=(0.54999995, 0.54999995), Z=(0.020000003, 0.1),
                               H=(0, 6.2831855), V=(1, 0), ext=False, rot=(0, 0, 0), hn=2, equi=True, tr=(0, -0.79999995, -1.2))),
    "eq_em2": dict(path=r"EffectEditor\Enemy\em0002\11_em0002_00_800.efx.5571972", off=173, equidistant=(0, 0.3),
                   shape=dict(kind="cylinder", X=(0, 0.3), Y=(-0.5, 0.3), Z=(0, 0.3), H=(0, PI), V=(1.25, -0.5), ext=True,
                              rot=(math.pi / 2, 0, 0), equi=True, tr=(0, 0, 0))),
    # Spawn Max 1, LoopNum 1; no Velocity3D; Life 0 / 1 / 3
    "s012": dict(path=r"EffectEditor\Common\slinger\11_pl_slinger_012.efx.5571972", off=906,
                 delay=0, interval=0, loops=1, spawn_num=1, cap=1, life=((0, 0), (1, 1), (3, 3)),
                 bb=dict(U=(2, 0), X=(1, 0), Y=(1, 0), rot=(0, 0)), uv=dict(seq=(1, 2), pat=(0, 1), speed=(1, 1), mode=0)),
}
DTS = {"60": [1 / 60] * 90, "var": ([1 / 30, 1 / 90, 1 / 45, 1 / 60, 1 / 120, 1 / 20] * 15)[:90],
       "60_long": [1 / 60] * 420}   # em21: first batch at tick 115
SCENARIOS = {"em2_60": ("em2", "60"), "em2_60_b": ("em2", "60"), "em2_var": ("em2", "var"),
             "jimen_60": ("jimen", "60"), "em21_60": ("em21", "60_long"),
             "s000_60": ("s000", "60"), "em9_60": ("em9", "60"),
             "s053_60": ("s053", "60"), "s012_60": ("s012", "60"),
             "em2_uvs16": ("em2", "60", dict(uvs=16, only=("uv",))),   # PlayMode 2 reaching the last pattern
             "box_div": ("box_div", "60", dict(only=("shape",))), "cyl_part": ("cyl_part", "60", dict(only=("shape",))),
             "cyl_hv": ("cyl_hv", "60", dict(only=("shape",))),
             "eq_cyl_part": ("eq_cyl_part", "60", dict(only=("shape",))), "eq_em21": ("eq_em21", "60_long", dict(only=("shape",))),
             "eq_em2": ("eq_em2", "60", dict(only=("shape",)))}
SHAPE_TYPE = 105   # Wilds EmitterShape3D type id


def equidistant_copy(src, start, range_x):
    """A copy of src in the temp folder with DivideEquidistant set on the entry's EmitterShape3D (payload +64, checked
    against RangeX first). The game folder is only read."""
    import tempfile
    data = bytearray(open(src, "rb").read())
    pos = start + 16
    for _ in range(struct.unpack_from("<I", data, start + 12)[0]):
        tid, size = struct.unpack_from("<2I", data, pos)
        if tid == SHAPE_TYPE:
            p = pos + 12                       # type id, declared size, UniqueID
            got = struct.unpack_from("<2f", data, p)
            if any(abs(a - b) > 1e-6 for a, b in zip(got, range_x)) or bytes(data[p + 64:p + 67]) != bytes(3):
                raise RuntimeError(f"unexpected EmitterShape3D payload at {p:#x}: RangeX {got}")
            data[p + 64] = 1
            folder = os.path.join(tempfile.gettempdir(), "re4native-equidistant")
            os.makedirs(folder, exist_ok=True)
            out = os.path.join(folder, f"{start}_{os.path.basename(src)}")
            open(out, "wb").write(data)
            return out
        pos += 8 + size
    raise RuntimeError("no EmitterShape3D in the entry")


def run(name):
    import mhwildsnative as wn
    import mhwilds_emitter as me
    key, clock, opt = (SCENARIOS[name] + ({},))[:3]
    ent = ENTRIES[key]
    me.UVS_SHAPE[:] = [4, opt.get("uvs", 255)]   # synthetic .uvs: 4 sequences of this many patterns
    img = wn.load()
    me.install_runtime(img)
    out = dict(scenario=name, entry=f"{os.path.basename(ent['path'])}@{ent['off']}", uvs=me.UVS_SHAPE[1], frames=[])
    try:
        src = os.path.join(ROOT, ent["path"])
        if "equidistant" in ent:
            src = equidistant_copy(src, ent["off"], ent["equidistant"])
        e = me.Entry(img, src, ent["off"])
        e.set_3d(10 in [a["type"] for a in e.attrs])
        e.register_steps(img)
        m = me.Emitter(img, e)
        out["capacity"] = m.capacity
        m.play()
        for k, dt in enumerate(DTS[clock], 1):
            m.tick(dt)
            ps = m.state()
            if "rgb" in ent:          # Rgb* per-particle record: 3 x (remain, duration, phase), then factors (raw)
                blk = m.block_of(ent["rgb"]["type"])
                for q in ps:
                    r = wn.u64(m.E + 0x128) + q["slot"] * wn.u32(m.E + 0xD0) + wn.u32(blk + 8)
                    q["rgb"] = list(struct.unpack("<12i", img.read(r, 0x30)))
            if "noise" in ent:        # Noise per-particle record: k0..k3, high / low phase, -, dir x, dir y
                blk = m.block_of(ent["noise"]["type"])
                for q in ps:
                    r = wn.u64(m.E + 0x128) + q["slot"] * wn.u32(m.E + 0xD0) + wn.u32(blk + 8)
                    q["noise_rec"] = list(struct.unpack("<9f", img.read(r, 36)))
            out["frames"].append(dict(tick=k, dt=dt, emitter=m.emitter_state(), particles=ps))
        out["status"] = "RAN"
    except Exception as ex:
        out["status"] = "FAULT"
        out["error"] = str(ex)[:300]
    return out


# ---------------------------------------------------------------------------------------- checks
def _rot(r):
    """Shape rotation R, row-vector convention (world = local · R), for a LocalRotation about one axis."""
    nz = [i for i in range(3) if abs(r[i]) > 1e-7]
    if not nz:
        return np.eye(3)
    if len(nz) > 1:
        raise ValueError("rotations about several axes are not modelled")
    c, s = math.cos(r[nz[0]]), math.sin(r[nz[0]])
    return [np.array([[1, 0, 0], [0, c, s], [0, -s, c]]), np.array([[c, 0, -s], [0, 1, 0], [s, 0, c]]),
            np.array([[c, s, 0], [-s, c, 0], [0, 0, 1]])][nz[0]]


TWO_PI_F = float(F(2 * math.pi))


_TABLES = {}


def _equidistant_table(sh, H):
    """The engine's table for one shape: mean radii (CalcOuterCurveData off), matrix = shape rotation + translation."""
    key = id(sh)
    if key not in _TABLES:
        import equidistant_ref as eq
        M = np.eye(4)
        M[:3, :3] = _rot(sh["rot"])
        M[3, :3] = sh.get("tr", (0, 0, 0))
        mean = lambda r: eq.F(eq.F(eq.F(r[0]) + eq.F(r[1])) * eq.F(0.5))   # noqa: E731
        _TABLES[key] = eq.build(M.flatten().tolist(), mean(sh["X"]), mean(sh["Z"]), 64, H[0], eq.F(H[1]), not sh["ext"])
    return _TABLES[key]


def _divide(c, hn, vn, wrap):
    """3d231b0: a and b from the shape counter; -1 = not divided (drawn)."""
    rem, q = c % max(vn, 1), c // max(vn, 1)
    h = (q + rem) % max(hn, 1)
    a = -1 if hn == 0 else (h / hn if wrap else (h / (hn - 1) if hn > 1 else 0.5))
    b = -1 if vn == 0 else (rem / (vn - 1) if vn > 1 else 0.5)
    return a, b


def _shape_local(sh, g, c):
    """Spawn position in shape space (before R). g: the salt 0x30 stream; c: the shape counter."""
    lerp = lambda a, b, t: a + (b - a) * t   # noqa: E731
    X, Y, Z, H, V, ext = sh["X"], sh["Y"], sh["Z"], sh["H"], sh["V"], sh["ext"]
    hn, vn, dn = sh.get("hn", 0), sh.get("vn", 0), sh.get("div", 0)
    if sh["kind"] == "box":                     # 3d223c0
        k = sh["axis"] if dn else g.next() % 3
        u = [(0.0 if dn == 1 else (c % dn) / (dn - 1) * 2 - 1) if dn and j == k else float(g.u()) * 2 - 1
             for j in range(3)]
        inner, outer = (X[0], Y[0], Z[0]), (X[1], Y[1], Z[1])
        v = [0.0] * 3
        v[k] = outer[k] * u[k]
        inside = inner[k] >= abs(v[k])
        for step in (1, 2):                     # the shell test cascades: the second axis only if the first was inside
            j = (k + step) % 3
            if inside:
                sign = 0.0 if u[j] == 0 else math.copysign(1.0, u[j])
                v[j] = (outer[j] - inner[j]) * u[j] + inner[j] * sign
                inside = inner[j] >= abs(v[j])
            else:
                v[j] = outer[j] * u[j]
        if ext:
            t = (Y[1] + v[1]) * 0.5 / Y[1] if Y[1] > 0 else 0.0
            v[0] *= H[0] + t * H[1]
            v[2] *= V[0] + t * V[1]
        return np.array(v)
    if not ext:                                 # UseExtension 0: H and V take the defaults
        H, V = ((0, 2 * math.pi), (-math.pi / 2, math.pi)) if sh["kind"] == "sphere" else ((0, 2 * math.pi), (1, 0))
    a, b = _divide(c, hn, vn, float(F(H[1])) == TWO_PI_F)
    if sh["kind"] == "sphere":
        if ext or hn or vn:
            a = a if a >= 0 else float(g.u())
            b = b if b >= 0 else float(g.u())
            t = float(g.u())
            th, ph = H[0] + a * H[1], V[0] + b * V[1]
            d = (math.cos(ph) * math.cos(th), math.sin(ph), math.cos(ph) * math.sin(th))
        else:
            d = np.array([float(g.u()) * 2 - 1 for _ in range(3)])
            d = d / np.linalg.norm(d)
            t = float(g.u())
        return np.array([lerp(*X, t) * d[0], lerp(*Y, t) * d[1], lerp(*Z, t) * d[2]])
    if sh.get("equi"):                          # DivideEquidistant (3d22b00): a, b, then t; the angle from the table
        import equidistant_ref as eq
        a = a if a >= 0 else float(g.u())
        b = b if b >= 0 else float(g.u())
        t = float(g.u())
        return np.array([float(v) for v in eq.sample(_equidistant_table(sh, H), 64, a, b, t, X, Y, Z, H, V, ext)])
    if a >= 0 or ext:                           # cylinder
        a = a if a >= 0 else float(g.u())
        th = H[0] + a * H[1]
        cxz = (math.cos(th), math.sin(th))
    else:
        w = np.array([float(g.u()) * 2 - 1 for _ in range(2)])
        cxz = w / np.linalg.norm(w)
    t = float(g.u())
    h = b if b >= 0 else float(g.u())
    sc = V[0] + h * V[1] if ext else 1.0
    return np.array([lerp(*X, t) * cxz[0] * sc, lerp(*Y, h), lerp(*Z, t) * cxz[1] * sc])


def _first(frames):
    out = {}
    for fr in frames:
        for p in fr["particles"]:
            out.setdefault(p["id"], (fr, p))
    return out


def _rng(refsim, salt, fr, pid):
    return refsim.Rng(salt, fr["emitter"]["rng_state"], fr["emitter"]["seed"], pid)


def _irange(x, r):
    lo, hi = r if isinstance(r, tuple) else (r, r)
    return lo + x % (hi - lo + 1)


def check_spawn(frames, ent, refsim):
    """§2.3.2 countdown with LoopNum; per batch SpawnNum then IntervalFrame (salt 1, key 0) drawn with the state
    before the batch's xs advance. Capacity: a batch fills only slots that were free at the end of the previous
    tick (a particle dying in this tick still holds its slot) and uses up its loop even when empty."""
    seed = frames[0]["emitter"]["seed"]
    state, batch = refsim.xs(seed), 0             # restart: xs(seed); the first batch does not advance (RE4 does)
    acc, c, loops, expect, alive_prev, states = F(0), ent["delay"], ent["loops"], [], 0, []
    cur_i = ent["interval"][0] if isinstance(ent["interval"], tuple) else ent["interval"]
    for fr in frames:
        acc = F(acc + F(fr["dt"]))
        N = int(F(acc * F(60.0)))
        acc = F(acc - F(F(N) * F(1 / 60)))
        n = float(F(F(fr["dt"]) * F(60.0)))
        born = 0
        if loops != 0:
            if c > N:
                c -= N
            else:
                k = max(1, math.floor(n / (cur_i + 1)))   # low-frame-rate catch-up with the current interval
                if loops is not None:
                    k = min(k, loops)
                    loops -= k
                g = refsim.Rng(1, state, seed, 0)
                num = _irange(g.next(), ent["spawn_num"])
                c = cur_i = _irange(g.next(), ent["interval"])
                if batch:
                    state = refsim.xs(state)
                batch += 1
                states.append((fr["tick"], state, fr["emitter"]["rng_state"]))
                born = min(k * num, ent["cap"] - alive_prev)
        expect += [fr["tick"]] * born
        alive_prev = len(fr["particles"])
    seen = sorted(t for t, _ in ((fr["tick"], p) for fr, p in _first(frames).values()))
    state_ok = all(a == b for _, a, b in states)
    return dict(batches=seen, expected=expect, batch_states=len(states), state_ok=state_ok,
                status="PASS" if seen == expect and state_ok else "FAIL")


def check_shape(frames, ent, refsim):
    sh = ent.get("shape")
    if not sh:
        return None
    R = _rot(sh["rot"])
    worst, n = 0.0, 0
    for c, (pid, (fr, p)) in enumerate(sorted(_first(frames).items())):   # c: the shape counter, one per sample
        world = _shape_local(sh, _rng(refsim, 0x30, fr, pid), c) @ R
        worst = max(worst, float(np.abs(world - np.array(p["pos"])).max()))
        n += 1
    return dict(particles=n, worst_abs=worst, status="PASS" if worst < 1e-6 else "FAIL")


def check_vel(frames, ent, refsim):
    v = ent.get("vel")
    if not v:
        return None
    R = _rot(ent["shape"]["rot"])
    worst, n = 0.0, 0
    for pid, (fr, p) in _first(frames).items():
        g = _rng(refsim, 0x23, fr, pid)   # Normal: no direction draws
        spd = float(g.rnd_guard(*v["speed"]))
        accel = 0.0 if v["gravity"] == (0, 0) else float(g.rnd(*v["gravity"])) * (-9.80665 / 3600)
        coef = float(g.rnd(*v["coef"]))
        local = np.array(p["pos"]) @ R.T                       # p = P·R⁻¹
        d = (np.array(v["size"]) - 1) * local + np.array(v["offset"])
        d = d / np.linalg.norm(d)
        if not ent.get("flags4"):                              # Flags bit 2: not rotated by the shape
            d = d @ R
        err = max(float(np.abs(d * spd / 60 - np.array(p["vel"])).max()), abs(accel - p["accel"]), abs(coef - p["coef"]))
        worst = max(worst, err)
        n += 1
    return dict(particles=n, worst_abs=worst, status="PASS" if worst < 1e-6 else "FAIL")


def check_bb(frames, ent, refsim):
    b = ent["bb"]
    bad, n = [], 0
    for pid, (fr, p) in _first(frames).items():
        g = _rng(refsim, 0x0F, fr, pid)
        if "color" in b:
            g.u()                                    # the colour's u comes first
        U, X, Y, rot = (float(g.rnd_guard(*b[k])) for k in ("U", "X", "Y", "rot"))
        got = (p["size"][0], p["size"][1], p["size"][2], p["rot"])
        if max(abs(a - c) for a, c in zip((U, X, Y, rot), got)) > 1e-6:
            bad.append(dict(id=pid, expected=(U, X, Y, rot), got=got))
        n += 1
    return dict(particles=n, mismatches=bad[:3], status="PASS" if not bad else "FAIL")


def check_scale(frames, ent, refsim):
    if "scale_add" not in ent:
        return None
    import exp_emitter
    powf = exp_emitter._powf()
    worst, n = 0.0, 0
    for pid, (fr, p) in _first(frames).items():
        g = _rng(refsim, 0x27, fr, pid)
        add, c = g.rnd(*ent["scale_add"]), g.rnd(*ent.get("scale_coef", (1, 0)))
        for f2 in frames:
            for q in f2["particles"]:
                if q["id"] == pid:
                    t = F(F(q["age"]) - F(p["age"]))
                    if c == F(1.0):
                        u = F(F(p["size"][0]) + F(add * t))
                    else:
                        ct = F(powf(float(c), float(t)))
                        u = F(F(p["size"][0]) + F(F(add * c) * F(F(F(1) - ct) / F(F(1) - c))))
                    worst = max(worst, abs(float(u) - q["size"][0]))
                    n += 1
    return dict(ticks=n, worst_abs=worst, status="PASS" if worst < 1e-6 else "FAIL")


def check_rotate(frames, ent, refsim):
    r = ent.get("rotate")
    if not r:
        return None
    import exp_emitter
    powf = exp_emitter._powf()
    two_pi = F(2 * math.pi)
    worst, n = 0.0, 0
    for pid, (fr, p) in _first(frames).items():
        g = _rng(refsim, 0x25, fr, pid)
        v, c = g.rnd(*r["add"]), g.rnd(*r["coef"])
        if r["sign"] and g.next() % 2 == 0:
            v = F(-v)
        angle = F(p["rot"])
        for f2 in frames:
            if f2["tick"] <= fr["tick"]:
                continue
            q = next((x for x in f2["particles"] if x["id"] == pid), None)
            if q is None:
                break
            nf = F(F(f2["dt"]) * F(60.0))
            angle = F(angle + F(nf * v))
            while angle >= two_pi:
                angle = F(angle - two_pi)
            while angle < 0:
                angle = F(angle + two_pi)
            if c != 0 and c != 1:
                v = F(v * F(powf(float(c), float(nf))))
            worst = max(worst, abs(float(angle) - q["rot"]))
            n += 1
    return dict(ticks=n, worst_abs=worst, status="PASS" if worst < 1e-5 else "FAIL")


def check_uv(frames, ent, refsim, count):
    import exp_emitter
    powf = exp_emitter._powf()
    u = ent["uv"]
    mod = u.get("modifier")
    bad, n, kills = [], 0, 0
    for pid, (fr, p) in _first(frames).items():
        g = _rng(refsim, 0x2B, fr, pid)
        if u["seq"][1] != u["seq"][0]:
            g.next()                                 # SequenceNo
        x = g.next() if u["pat"][1] != u["pat"][0] else 0
        frame = u["pat"][0] + x % max(u["pat"][1] - u["pat"][0], 1)
        sp = F(F(u["speed"][0]) + F(F(F(u["speed"][1]) - F(u["speed"][0])) * g.u()))
        m = F(1)

        def mod_speed(m):
            return F(F(m * F(mod["init"])) + F(F(F(1) - m) * F(mod["final"])))
        T = F(F(1 / 60) / (mod_speed(m) if mod else sp))
        acc, dead, expect, last = F(0), False, {fr["tick"]: frame}, p
        for f2 in frames:
            if f2["tick"] <= fr["tick"]:
                continue
            q = next((x for x in f2["particles"] if x["id"] == pid), None)
            if dead:                                 # killed in the previous tick: gone now
                if q is not None:
                    bad.append((pid, f2["tick"], "alive after kill"))
                kills += 1
                break
            if q is None:                            # gone without a UV kill: only when its Life ran out
                step = math.floor(f2["dt"] * 60) + 1      # at most this many Life frames pass in this tick
                if last.get("life_rec") and last["life_rec"][0] > step:
                    bad.append((pid, f2["tick"], "gone with life remaining", last["life_rec"][0]))
                break
            last = q
            acc = F(acc + F(f2["dt"]))
            if u["mode"] and acc >= T:
                r = F(acc / T)
                steps = int(r)
                acc = F(F(r - F(steps)) * F(1 / 60))
                frame += steps
                if frame >= count:
                    frame = 0 if u["mode"] == 1 else count - 1
                    dead = u["mode"] == 2
            if mod:                                  # the modifier's T applies from the next tick
                m = F(F(powf(mod["coef"], float(F(F(f2["dt"]) * F(60))))) * m)
                T = F(F(1 / 60) / mod_speed(m))
            expect[f2["tick"]] = frame
        for f2 in frames:
            q = next((x for x in f2["particles"] if x["id"] == pid), None)
            if q is not None and f2["tick"] in expect:
                n += 1
                if q["uv_frame"] != expect[f2["tick"]]:
                    bad.append((pid, f2["tick"], expect[f2["tick"]], q["uv_frame"]))
    return dict(ticks=n, kills=kills, mismatches=bad[:3], status="PASS" if not bad else "FAIL")


def check_color(frames, ent, refsim):
    b = ent["bb"]
    if "color" not in b:
        return None
    c0, c1 = b["color"]
    bad, n, ticks = [], 0, 0
    for pid, (fr, p) in _first(frames).items():
        u = _rng(refsim, 0x0F, fr, pid).u()
        exp = 0
        for k in range(4):
            lo, hi = (c0 >> 8 * k) & 0xFF, (c1 >> 8 * k) & 0xFF
            exp |= int(F(F(lo) + F(F(F(hi) - F(lo)) * u))) << 8 * k
        n += 1
        for f2 in frames:
            for q in f2["particles"]:
                if q["id"] == pid:
                    ticks += 1
                    if q["rgba"] != exp:
                        bad.append((pid, f2["tick"], hex(exp), hex(q["rgba"])))
    return dict(particles=n, ticks=ticks, mismatches=bad[:3], status="PASS" if not bad else "FAIL")


def check_rgb(frames, ent, refsim):
    rg = ent.get("rgb")
    if not rg:
        return None
    bad, n = [], 0
    for pid, (fr, p) in _first(frames).items():
        g = refsim.Rng(rg["salt"], fr["emitter"]["rng_state"], fr["emitter"]["seed"], p["slot"])
        xs = [g.next() for _ in range(3)]
        for ch in rg["channels"]:
            if ch.get("life"):
                A, K, V = ((_irange(x, r) if ch.get("draws") else r[0]) for x, r in zip(xs, ch["life"]))
                assert A == 0, "Appear segments are not modelled"
            for f2 in frames:
                if f2["tick"] < fr["tick"]:
                    continue
                q = next((x for x in f2["particles"] if x["id"] == pid), None)
                if q is None:
                    break
                rec = q["rgb"]
                if not ch.get("life"):
                    exp, f = None, F(ch["scale"])
                else:
                    e = f2["tick"] - fr["tick"] + 1           # the birth tick already counts one frame
                    if e <= K:
                        exp, f = (K - e, K, 5), F(1)
                    else:
                        rem = V - (e - K)
                        exp = (rem, V, 8 if rem >= 0 else 9)
                        f = F(F(rem) / F(V)) if rem > 0 else F(0)
                    f = F(f * F(ch.get("scale", 1.0)))
                got = tuple(rec[ch["base"]:ch["base"] + 3])
                if exp is not None and got != exp:
                    bad.append((pid, f2["tick"], ch["base"], exp, got))
                got_f = struct.unpack("<f", struct.pack("<i", rec[ch["factor"]]))[0] if "factor" in ch else None
                if got_f is not None and abs(float(f) - got_f) > 1e-6:
                    bad.append((pid, f2["tick"], "factor", ch["factor"], float(f), got_f))
                n += 1
    return dict(checks=n, mismatches=bad[:4], status="PASS" if not bad else "FAIL")


def check_noise(frames, ent, refsim):
    nz = ent.get("noise")
    if not nz:
        return None
    import noise_ref
    blk = list(nz["lf"]) + list(nz["lw"]) + list(nz["hf"]) + list(nz["hw"])
    state, n, wo, wr = {}, 0, 0.0, 0.0
    for fr in frames:
        for q in fr["particles"]:
            if q["id"] not in state:
                state[q["id"]] = noise_ref.birth(fr["emitter"]["rng_state"], fr["emitter"]["seed"], q["slot"])
            rec = state[q["id"]]
            off = noise_ref.step(rec, blk, F(F(fr["dt"]) * noise_ref.TWO_PI))
            wo = max(wo, max(abs(float(off[k]) - q["noise"][k]) for k in range(3)))
            wr = max(wr, max(abs(float(rec[k]) - q["noise_rec"][k]) for k in range(9)))
            n += 1
    return dict(ticks=n, worst_offset=wo, worst_record=wr, status="PASS" if max(wo, wr) < 1e-6 else "FAIL")


def check(dump, ent, only=None):
    import exp_emitter
    import refsim
    frames = dump["frames"]
    if not frames:
        return {}
    want = (lambda k: k in only) if only else (lambda k: True)
    res = {}
    if want("spawn"):
        res["spawn"] = check_spawn(frames, ent, refsim)
    if want("life") or want("motion"):
        moved = dump
        if ent.get("flags4"):                 # motion is integrated into the displacement slot
            moved = dict(frames=[dict(fr, particles=[dict(q, pos=q["disp"]) for q in fr["particles"]]) for fr in frames])
        base = exp_emitter.check(moved, dict(ent, vel=None, interval=0, loops=0))   # life and motion only
        if "life" in base:
            bad = [x for x in base["life"]["particles"] if not (x["draws_match_refsim"] and x["match"] == x["ticks"])]
            res["life"] = dict(particles=len(base["life"]["particles"]), mismatches=bad[:3], status=base["life"]["status"])
        if "motion" in base:
            res["motion"] = base["motion"]
    for k, fn in (("shape", check_shape), ("vel", check_vel), ("bb", check_bb), ("color", check_color),
                  ("scale", check_scale), ("rotate", check_rotate), ("rgb", check_rgb), ("noise", check_noise)):
        if want(k):
            r = fn(frames, ent, refsim)
            if r:
                res[k] = r
    if want("uv"):
        res["uv"] = check_uv(frames, ent, refsim, dump.get("uvs", 255))
    return res


def digest(dump):
    return hashlib.sha256(json.dumps(dump["frames"], sort_keys=True).encode()).hexdigest()[:16]


def main():
    if len(sys.argv) >= 3 and sys.argv[1] == "run":
        print(json.dumps(run(sys.argv[2])))
        return
    results = {}
    for name in SCENARIOS:
        p = subprocess.run([sys.executable, "-I", "-X", "utf8", "-u", os.path.abspath(__file__), "run", name],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        if p.returncode != 0 or not p.stdout.strip():
            results[name] = dict(status="CRASH", exit=hex(p.returncode & 0xFFFFFFFF), stderr=p.stderr[-800:])
            continue
        dump = json.loads(p.stdout.strip().splitlines()[-1])
        r = dict(status=dump["status"], error=dump.get("error"), digest=digest(dump),
                 particles=len({q["id"] for fr in dump["frames"] for q in fr["particles"]}))
        if dump["status"] == "RAN":
            sc = SCENARIOS[name]
            r["checks"] = check(dump, ENTRIES[sc[0]], (sc[2] if len(sc) > 2 else {}).get("only"))
        results[name] = r
    results["determinism"] = dict(same=results["em2_60"].get("digest") == results["em2_60_b"].get("digest"))
    print(json.dumps(results, indent=1, ensure_ascii=False, default=str))
    summ = {k: (v.get("status"), {c: x.get("status") for c, x in v.get("checks", {}).items()})
            for k, v in results.items() if k != "determinism"}
    print("RESULT " + json.dumps({"exp": "wilds_reference", "determinism": results["determinism"], **summ}))


if __name__ == "__main__":
    main()
