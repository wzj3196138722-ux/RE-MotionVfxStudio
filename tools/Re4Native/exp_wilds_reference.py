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
  shape    spawn position from EmitterShape3D (salt 0x30): Sphere / Cylinder with UseExtension (§3.1)
  vel      Velocity3D spawn draws (salt 0x23): Normal direction, Speed, GravityRate, SpeedCoef (§3.2, §1.6.3)
  bb       TypeBillboard3D spawn draws (salt 0x0f): SizeScalar, SizeX, SizeY, Rotation, each skipped at spread 0
  scale    ScaleAnim (salt 0x27): every tick's U slot = f(age - age0, base, SizeScalarAdd, SizeScalarCoef) (§3.3)
  rotate   RotateAnim (salt 0x25): X speed, coef and sign draws; every tick angle += n*v, wrapped to [0, 2pi),
           then v *= powf(c, n) (§3.4)
  uv       UVSequence (salt 0x2b): start pattern = PatternNo.min + x2 % (max - min); frame advance not compared
"""
import hashlib
import json
import math
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np  # noqa: E402

F = np.float32
ROOT = os.environ.get("MHWILDS_EXTRACT",
                      r"E:\Program\Steam\steamapps\common\MonsterHunterWilds\MHWILDS_EXTRACT\natives\STM\Art\VFX")
PI = 3.1415927
# Field values as ReeLib reads them. shape: (type, inner/outer X, Y, Z, H start/span, V start/span, LocalRotation.X)
ENTRIES = {
    # Spawn Max 6, SpawnNum 2, IntervalFrame 10, LoopNum 3; Billboard3D SizeScalar 1±0.3, Rotation 0±π; Velocity3D
    # Normal, Speed 1.2±0.5, Offset (0, 0.245, −1), Size (2, 1.5, 2), GravityRate 1.2; ScaleAnim SizeScalarAdd
    # 0.065±0.01; Life 0 / 4 / 45; UVSequence SequenceNo 2, PatternNo 5..11; EmitterShape3D Cylinder, UseExtension
    "em2": dict(path=r"EffectEditor\Enemy\em0002\11_em0002_00_800.efx.5571972", off=173,
                delay=0, interval=10, loops=3, spawn_num=2, cap=6, life=((0, 0), (4, 4), (45, 45)),
                vel=dict(offset=(0, 0.245, -1), size=(2, 1.5, 2), speed=(1.2, 0.5), gravity=(1.2, 0), coef=(1, 0)),
                bb=dict(U=(1, 0.3), X=(1, 0), Y=(1, 0), rot=(0, PI)), scale_add=(0.065, 0.01), pattern=(5, 11),
                shape=("cylinder", (0, 0.3), (-0.5, 0.3), (0, 0.3), (0, PI), (1.25, -0.5), math.pi / 2)),
    # Spawn Max 3, SpawnNum 1, IntervalFrame 2, LoopNum 8; Billboard3D SizeScalar 2±1, SizeY 1±0.2; Velocity3D Normal,
    # Speed 1, Size 1.1; Life 0 / 1 / 5; EmitterShape3D Sphere 1..2, UseExtension, V −10°..90°
    "jimen": dict(path=r"EffectEditor\Common\slinger\11_pl_slinger_055_jimen.efx.5571972", off=15735,
                  delay=0, interval=2, loops=8, spawn_num=1, cap=3, life=((0, 0), (1, 1), (5, 5)),
                  vel=dict(offset=(0, 0, 0), size=(1.1, 1.1, 1.1), speed=(1, 0), gravity=(0, 0), coef=(1, 0)),
                  bb=dict(U=(2, 1), X=(1, 0), Y=(1, 0.2), rot=(0, PI)), pattern=(0, 64),
                  shape=("sphere", (1, 2), (1, 2), (1, 2), (0, 6.2831855), (-0.17453294, 1.7453294), math.pi / 2)),
    # Spawn Max 3, SpawnNum 3, LoopNum 1; Velocity3D Normal, Speed 0.1, SpeedCoef 0.95, Offset (0, −0.06, 0);
    # Life 0 / 5 / 10..20; EmitterShape3D Cylinder with RangeDivideNum (divide mode: shape not checked)
    "s053": dict(path=r"EffectEditor\Common\slinger\11_pl_slinger_053.efx.5571972", off=6635,
                 delay=0, interval=0, loops=1, spawn_num=3, cap=3, life=((0, 0), (5, 5), (10, 20)),
                 vel=dict(offset=(0, -0.06, 0), size=(1.1, 1.05, 1.1), speed=(0.1, 0), gravity=(0, 0), coef=(0.95, 0)),
                 bb=dict(U=(0.75, 0.25), X=(0.8, 0), Y=(1, 0), rot=(0, PI)), pattern=(20, 41), shape_rot=math.pi / 2),
    # FixRandomGenerator seed 1; Spawn Max 6, EmitterDelayFrame 115, IntervalFrame 30..60, LoopNum 0 (endless);
    # Billboard3D SizeScalar 0.1±0.025; Velocity3D Flags 4, Normal, Offset (0, 1.8, −0.05), Size (3, 1, 3),
    # GravityRate −0.004; RotateAnim Flags 1, AddX 1°±0.5°, CoefX 0.99; ScaleAnim Add 0.002, Coef 0.99;
    # Life 15 / 165 / 0; EmitterShape3D Cylinder with RangeDivideHorizontalNum (shape not checked)
    "em21": dict(path=r"EffectEditor\Enemy\em0021\11_em0021_00_206.efx.5571972", off=5845,
                 delay=115, interval=(30, 60), loops=None, spawn_num=1, cap=6, life=((15, 15), (165, 165), (0, 0)),
                 vel=dict(offset=(0, 1.8, -0.049999997), size=(3, 1, 3), speed=(0.20000014, 0), gravity=(-0.004, 0),
                          coef=(1, 0)), flags4=True, shape_rot=0.0,
                 bb=dict(U=(0.1, 0.025), X=(1, 0), Y=(1, 0), rot=(0, PI)), scale_add=(0.002, 0), scale_coef=(0.99, 0),
                 rotate=dict(add=(0.017453294, 0.008726647), coef=(0.99, 0), sign=True), pattern=(0, 64)),
    # FixRandomGenerator seed 406056259 (state chain with a large seed); Spawn Max 6, IntervalFrame 2, endless;
    # Life 0 / 2 / 8; no Velocity3D; EmitterShape3D Sphere with RangeDivideNum (shape not checked)
    "s000": dict(path=r"EffectEditor\Common\slinger\11_pl_slinger_000.efx.5571972", off=6319,
                 delay=0, interval=2, loops=None, spawn_num=1, cap=6, life=((0, 0), (2, 2), (8, 8)),
                 bb=dict(U=(0.1, 0), X=(1, 0), Y=(1, 0), rot=(0, 0)), pattern=(0, 1)),
    # Spawn Max 1, LoopNum 1; no Velocity3D; Life 0 / 1 / 3
    "s012": dict(path=r"EffectEditor\Common\slinger\11_pl_slinger_012.efx.5571972", off=906,
                 delay=0, interval=0, loops=1, spawn_num=1, cap=1, life=((0, 0), (1, 1), (3, 3)),
                 bb=dict(U=(2, 0), X=(1, 0), Y=(1, 0), rot=(0, 0)), pattern=(0, 1)),
}
DTS = {"60": [1 / 60] * 90, "var": ([1 / 30, 1 / 90, 1 / 45, 1 / 60, 1 / 120, 1 / 20] * 15)[:90],
       "60_long": [1 / 60] * 420}   # em21: first batch at tick 115
SCENARIOS = {"em2_60": ("em2", "60"), "em2_60_b": ("em2", "60"), "em2_var": ("em2", "var"),
             "jimen_60": ("jimen", "60"), "em21_60": ("em21", "60_long"),
             "s000_60": ("s000", "60"), "s053_60": ("s053", "60"), "s012_60": ("s012", "60")}


def run(name):
    import mhwildsnative as wn
    import mhwilds_emitter as me
    key, clock = SCENARIOS[name]
    ent = ENTRIES[key]
    me.UVS_SHAPE[:] = [4, 255]   # long sequences: with 16 patterns UVSequence's end-of-sequence kill shortens lives
    img = wn.load()
    me.install_runtime(img)
    out = dict(scenario=name, entry=f"{os.path.basename(ent['path'])}@{ent['off']}", frames=[])
    try:
        e = me.Entry(img, os.path.join(ROOT, ent["path"]), ent["off"])
        e.set_3d(10 in [a["type"] for a in e.attrs])
        e.register_steps(img)
        m = me.Emitter(img, e)
        out["capacity"] = m.capacity
        m.play()
        for k, dt in enumerate(DTS[clock], 1):
            m.tick(dt)
            out["frames"].append(dict(tick=k, dt=dt, emitter=m.emitter_state(), particles=m.state()))
        out["status"] = "RAN"
    except Exception as ex:
        out["status"] = "FAULT"
        out["error"] = str(ex)[:300]
    return out


# ---------------------------------------------------------------------------------------- checks
def _rx(a):
    """Shape rotation R about X, row-vector convention (world = local · R)."""
    c, s = math.cos(a), math.sin(a)
    return np.array([[1, 0, 0], [0, c, s], [0, -s, c]])


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
    if "shape" not in ent:
        return None
    kind, X, Y, Z, H, V, rot = ent["shape"]
    R = _rx(rot)
    lerp = lambda a, b, t: a + (b - a) * t   # noqa: E731
    worst, n = 0.0, 0
    for pid, (fr, p) in _first(frames).items():
        g = _rng(refsim, 0x30, fr, pid)
        if kind == "cylinder":            # UseExtension: angle, t, h
            a, t, h = (float(g.u()) for _ in range(3))
            th, sc = H[0] + a * H[1], V[0] + h * V[1]
            local = (lerp(*X, t) * math.cos(th) * sc, lerp(*Y, h), lerp(*Z, t) * math.sin(th) * sc)
        else:                             # sphere, UseExtension: a, b, t
            a, b, t = (float(g.u()) for _ in range(3))
            th, ph = H[0] + a * H[1], V[0] + b * V[1]
            d = (math.cos(ph) * math.cos(th), math.sin(ph), math.cos(ph) * math.sin(th))
            local = (lerp(*X, t) * d[0], lerp(*Y, t) * d[1], lerp(*Z, t) * d[2])
        world = np.array(local) @ R
        worst = max(worst, float(np.abs(world - np.array(p["pos"])).max()))
        n += 1
    return dict(particles=n, worst_abs=worst, status="PASS" if worst < 1e-6 else "FAIL")


def check_vel(frames, ent, refsim):
    v = ent.get("vel")
    if not v:
        return None
    R = _rx(ent["shape"][6] if "shape" in ent else ent["shape_rot"])
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


def check_uv(frames, ent, refsim):
    lo, hi = ent["pattern"]
    bad, n = [], 0
    for pid, (fr, p) in _first(frames).items():
        g = _rng(refsim, 0x2B, fr, pid)
        g.next()                                     # x1: SequenceNo
        start = lo + g.next() % max(hi - lo, 1)
        if p["uv_frame"] != start:
            bad.append((pid, start, p["uv_frame"]))
        n += 1
    return dict(particles=n, mismatches=bad[:3], status="PASS" if not bad else "FAIL")


def check(dump, ent):
    import exp_emitter
    import refsim
    frames = dump["frames"]
    if not frames:
        return {}
    moved = dump
    if ent.get("flags4"):                     # motion is integrated into the displacement slot
        moved = dict(frames=[dict(fr, particles=[dict(q, pos=q["disp"]) for q in fr["particles"]]) for fr in frames])
    base = exp_emitter.check(moved, dict(ent, vel=None, interval=0, loops=0))   # life and motion only
    res = {"spawn": check_spawn(frames, ent, refsim)}
    if "life" in base:
        bad = [x for x in base["life"]["particles"] if not (x["draws_match_refsim"] and x["match"] == x["ticks"])]
        res["life"] = dict(particles=len(base["life"]["particles"]), mismatches=bad[:3], status=base["life"]["status"])
    if "motion" in base:
        res["motion"] = base["motion"]
    for k, fn in (("shape", check_shape), ("vel", check_vel), ("bb", check_bb), ("scale", check_scale),
                    ("rotate", check_rotate), ("uv", check_uv)):
        r = fn(frames, ent, refsim)
        if r:
            res[k] = r
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
            r["checks"] = check(dump, ENTRIES[SCENARIOS[name][0]])
        results[name] = r
    results["determinism"] = dict(same=results["em2_60"].get("digest") == results["em2_60_b"].get("digest"))
    print(json.dumps(results, indent=1, ensure_ascii=False, default=str))
    summ = {k: (v.get("status"), {c: x.get("status") for c, x in v.get("checks", {}).items()})
            for k, v in results.items() if k != "determinism"}
    print("RESULT " + json.dumps({"exp": "wilds_reference", "determinism": results["determinism"], **summ}))


if __name__ == "__main__":
    main()
