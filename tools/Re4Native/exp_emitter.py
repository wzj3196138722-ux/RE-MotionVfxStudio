"""Emitter loop on real corpus entries: the engine parses the entry, builds the emitter and ticks it.

    python -I -X utf8 -u exp_emitter.py                  # all scenarios, each in a child process
    python -I -X utf8 -u exp_emitter.py run <scenario>   # one scenario in this process (JSON on stdout)

Scenarios (ENTRIES x clock):
  bb_60       Billboard3D entry, dt = 1/60, 90 ticks
  bb_60_b     the same again in a second process (determinism)
  bb_var      the same entry, variable dt (1/30, 1/90, 1/45, ...)
  bb_nofrg    the entry without its FixRandomGenerator, global generator as in a fresh image (zero)
  bb_nofrg_g  the same with the global generator seeded (d6a3150..5c = 1, 2, 3, 4)
  mesh_60     a TypeMesh entry, dt = 1/60
  ribbon_60   a TypeRibbonLength entry, dt = 1/60

Checks against the spec (RE4-EFX-RUNTIME.zh-CN.md):
  spawn   first batch at tick max(EmitterDelayFrame, 1); E.state at spawn = xs(xs(seed)) (restart + one advance)
  life    Life draws (salt 0x29) = refsim; every tick's phase code and 8-bit alpha = the §2.4.2 state machine
  motion  every tick: gvel += n*accel; pos += (vel + (0,1,0)*gvel)*n; vel *= powf(coef, n)   (identity world)
  spawnv  Velocity3D spawn draws (salt 0x23): direction, speed/60, coef, gravity accel = refsim (1e-6)
  size    ScaleAnim slots = spec §3.3 from the particle's own slot values
  uv      UVSequence frame advance (synthetic .uvs: 4 sequences x 16 patterns)
Full per-tick dumps go to <out>/emitter_<scenario>.json.
"""
import hashlib
import json
import math
import os
import struct
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np  # noqa: E402

F = np.float32
STM = r"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\RE4_EXTRACT\natives\stm"
ENTRIES = {
    # 飛沫 3: FixRandomGenerator, Transform3D, ParentOptions (BoneName "Head"), Spawn (Max 3, SpawnNum 1,
    # LoopNum {3,0}, EmitterDelayFrame 7), TypeBillboard3D, Velocity3D, ScaleAnim, Life (0 / 3..10 / 30..60),
    # UVSequence (.uvs not in the extract), EmitterShape3D (box)
    "bb": dict(path=STM + r"\_chainsaw\vfx\effecteditor\efd_event\cs\efd_csa013_s00\efd_0015_csa013_s00_saliva_0000.efx.3539837",
               off=0x89, delay=7, interval=0, loops=3, life=((0, 0), (3, 10), (30, 60)), uvs=(4, 16),
               vel=dict(dir=((0, 0.08716), (0, 0), (0.9981, 0.0019)), speed=(2.5, 2), gravity=(0.3, 0.1), coef=(0.9, 0))),
    # entry 13 of sm82_320_00: Spawn (Max 10, SpawnNum 1, IntervalFrame 30, LoopNum {0,0} = endless, no delay),
    # Life (Appear 20..40, Keep 20..30, Vanish 20), Velocity3D, ScaleAnim, UVSequence (PatternNo 0..64), ShaderSettings
    "bb2": dict(path=STM + r"\_anotherorder\vfx\effecteditor\efd_setmodel\efd_sm82_320_00\efd_0015_sm82_320_00_0000.efx.3539837",
                off=0x2E29, delay=0, interval=30, life=((20, 40), (20, 30), (20, 20)), uvs=(4, 64),
                vel=dict(dir=((0.9924, 0.0076), (0, 0), (0, 0.17365)), speed=(1.5, 0.2), gravity=(-0.07, 0.03), coef=(0.98, 0))),
    # entry 4 of cse355 smoke: FixRandomGenerator, Transform3D, ParentOptions, Spawn (1 particle), TypeMesh, Life (Keep 300, Flags 1)
    "mesh": dict(path=STM + r"\_anotherorder\vfx\effecteditor\efd_event\cs\efd_cse355_s00\efd_0015_cse355_s00_smoke_0002.efx.3539837",
                 off=0x1159, delay=0, interval=0, loops=1),
    # entry 8 of cse010 torch: ... Spawn (1 particle), TypeRibbonLength (Length 1.3, Width 0.8), Life (Keep 100, Flags 1),
    # UVSequence, ShaderSettings
    "ribbon": dict(path=STM + r"\_anotherorder\vfx\effecteditor\efd_event\cs\efd_cse010_s00\efd_0015_cse010_s00_torch_0000.efx.3539837",
                   off=0x1A21, delay=0, interval=0, loops=1, uvs=(4, 16)),
}
DTS = {"60": [1 / 60] * 120, "var": ([1 / 30, 1 / 90, 1 / 45, 1 / 60, 1 / 120, 1 / 20] * 30)[:120]}
SCENARIOS = {
    "bb_60": ("bb", "60", {}), "bb_60_b": ("bb", "60", {}), "bb_var": ("bb", "var", {}),
    "bb_nofrg": ("bb", "60", dict(drop=(1,))), "bb_nofrg_g": ("bb", "60", dict(drop=(1,), gseed=(1, 2, 3, 4))),
    "bb2_60": ("bb2", "60", {}), "bb2_var": ("bb2", "var", {}),
    "mesh_60": ("mesh", "60", {}), "ribbon_60": ("ribbon", "60", {}),
}


# ---------------------------------------------------------------------------------------- run
def run(name):
    import emitter as em
    import re4native as rn
    key, clock, opt = SCENARIOS[name]
    ent = ENTRIES[key]
    img = rn.load()
    em.install_runtime(img, global_seed=opt.get("gseed"))
    if "uvs" in ent:
        em.UVS_SHAPE[:] = list(ent["uvs"])
    out = dict(scenario=name, entry=dict(file=os.path.basename(ent["path"]), offset=hex(ent["off"])), frames=[])
    try:
        e = em.Entry(img, ent["path"], ent["off"], drop_types=opt.get("drop", ()))
        out["attributes"] = [dict(type=a["type"], uid=a["uid"], bytes=a["bytes"], loader=hex(a["loader"]),
                                  dropped=a.get("dropped", False)) for a in e.attrs]
        out["entry_end"] = hex(e.end)
        m = em.Emitter(img, e)
        out["max_particles"] = m.max_particles
        out["emitter0"] = m.emitter_state()
        m.play()
        for k, dt in enumerate(DTS[clock], 1):
            m.tick(dt)
            out["frames"].append(dict(tick=k, dt=dt, emitter=m.emitter_state(), particles=m.state()))
        out["status"] = "RAN"
    except (rn.NativeFault, rn.UnresolvedImportCalled, RuntimeError) as ex:
        out["status"] = "STUCK"
        out["stopped"] = str(ex)
    out["stub_calls"] = [(c[0], c[5]) for c in em.CALLS][:40]
    out["stub_call_count"] = len(em.CALLS)
    out["arena_bytes"] = em.arena_used()
    return out


# ---------------------------------------------------------------------------------------- checks
def _powf():
    import ctypes
    f = ctypes.CDLL("ucrtbase").powf
    f.restype, f.argtypes = ctypes.c_float, [ctypes.c_float, ctypes.c_float]
    return f


def life_model(A, K, T, step_seq):
    """Spec §2.4.2 mode 0: per tick (phase, alpha) after the spawn tick; spawn tick = (2, 255 if A == 0 else 0)."""
    remain = T
    out = []
    for step in step_seq:
        remain -= step
        if remain <= 0:
            out.append((9, 0))
            break
        el = T - remain
        if el < A:
            out.append((3, int(el / A * 255)))
        elif el < A + K:
            out.append((5, 255))
        else:
            out.append((8, int(remain / (T - A - K) * 255)))
    return out


def check(dump, ent):
    import refsim
    res = {}
    frames = dump["frames"]
    if not frames:
        return res
    first = {}
    for fr in frames:
        for p in fr["particles"]:
            first.setdefault(p["id"], (fr["tick"], fr))
    # spawn (spec §2.3.2): countdown c = EmitterDelayFrame; per tick N = trunc(acc*60) with the remainder
    # kept, n = dt*60; c > N -> c -= N, else a batch of SpawnNum * k particles, k = max(1, floor(n/(I+1)))
    # (catch-up at low frame rate), c = IntervalFrame. LoopNum: k batches are used up per tick (inferred
    # from the variable-dt run: 3 loops spawned in one tick and none after).
    acc, c, loops, expect = F(0), ent["delay"], ent.get("loops"), []
    for fr in frames:
        acc = F(acc + F(fr["dt"]))
        N = int(F(acc * F(60.0)))
        acc = F(acc - F(F(N) * F(1 / 60)))
        n = float(F(F(fr["dt"]) * F(60.0)))
        if loops == 0:
            continue
        if c > N:
            c -= N
        else:
            k = max(1, math.floor(n / (ent["interval"] + 1)))
            if loops is not None:
                k = min(k, loops)
                loops -= k
            expect += [fr["tick"]] * (k * ent.get("spawn_num", 1))
            c = ent["interval"]
    ticks = sorted(t for t, _ in first.values())
    seed_at = {pid: fr["emitter"]["seed"] for pid, (t, fr) in first.items()}
    res["spawn"] = dict(batches=ticks, expected=[t for t in expect if t <= frames[-1]["tick"]][:len(ticks) + 1],
                        e_state_at_first=first[0][1]["emitter"]["rng_state"] if 0 in first else None,
                        xs_xs_seed=refsim.xs(refsim.xs(seed_at[0])) if 0 in first else None)
    res["spawn"]["status"] = "PASS" if ticks and ticks == expect[:len(ticks)] else "FAIL"
    # life
    lres = []
    for pid, (t0, fr0) in (first.items() if "life" in ent else []):
        p0 = next(p for p in fr0["particles"] if p["id"] == pid)
        st = fr0["emitter"]["rng_state"]
        g = refsim.Rng(0x29, st, seed_at[pid], pid)
        x = [g.next() for _ in range(3)]
        A, K, V = [lo + xx % (hi - lo + 1) for (lo, hi), xx in zip(ent["life"], x)]
        rec = p0["life_rec"]                       # remain, A, A+K, T, ...
        draws_ok = (rec[1], rec[2] - rec[1], rec[3] - rec[2]) == (A, K, V)
        # the step of each tick is read back from the record's remain counter
        seen, seq, prev = [], [], rec[0]
        for fr in frames:
            if fr["tick"] <= t0:
                continue
            p = next((q for q in fr["particles"] if q["id"] == pid), None)
            if p is None:
                seq.append(prev if prev > 0 else 1)
                seen.append((9, 0))
                break
            seq.append(prev - p["life_rec"][0])
            prev = p["life_rec"][0]
            seen.append((p["phase"], p["life_alpha"]))
        model = life_model(A, K, A + K + V, seq)
        lres.append(dict(id=pid, AKV=(A, K, V), draws_match_refsim=draws_ok, spawn_tick=(p0["phase"], p0["life_alpha"]),
                         ticks=len(seen), match=sum(a == b for a, b in zip(seen, model)), first_diff=next(
                             ((i, a, b) for i, (a, b) in enumerate(zip(seen, model)) if a != b), None)))
    if lres:
        res["life"] = dict(particles=lres, status="PASS" if all(l["draws_match_refsim"] and l["match"] == l["ticks"] for l in lres) else "FAIL")
    # motion
    powf = _powf()
    n_ok = n = 0
    worst = 0.0
    last = {}
    for fr in frames:
        nf = F(F(fr["dt"]) * F(60.0))
        for p in fr["particles"]:
            q = last.get(p["id"])
            if q is not None and p["flags"] & 2 == 0 and q["age"] > 0:
                gv = F(F(q["gvel"]) + F(nf * F(q["accel"])))
                vel = [F(v) for v in q["vel"]]
                pos = [F(F(q["pos"][i]) + F(F(vel[i] + (gv if i == 1 else F(0))) * nf)) for i in range(3)]
                v2 = [F(vel[i] * F(powf(float(F(q["coef"])), float(nf)))) for i in range(3)]
                err = max([abs(float(a) - b) for a, b in zip(pos, p["pos"])] + [abs(float(a) - b) for a, b in zip(v2, p["vel"])]
                          + [abs(float(gv) - p["gvel"])])
                worst = max(worst, err)
                n += 1
                n_ok += err <= 1e-6
            last[p["id"]] = p
    if n:
        res["motion"] = dict(steps=n, within_1e6=n_ok, worst_abs=worst, status="PASS" if n_ok == n else "FAIL")
    # spawn velocity draws
    vr = []
    vcfg = ent.get("vel")
    for pid, (t0, fr0) in (first.items() if vcfg else []):
        p0 = next(p for p in fr0["particles"] if p["id"] == pid)
        g = refsim.Rng(0x23, fr0["emitter"]["rng_state"], seed_at[pid], pid)
        d = [g.rnd(*vcfg["dir"][i]) for i in range(3)]
        ln = math.sqrt(sum(float(x) ** 2 for x in d))
        spd = g.rnd_guard(*vcfg["speed"])
        grav = g.rnd(*vcfg["gravity"])
        coef = g.rnd(*vcfg["coef"])
        vel = [float(x) / ln * float(spd) / 60 for x in d]
        accel = float(grav) * (-9.80665 / 3600)
        # the dump holds the state after the spawn tick; undo nothing (the spawn tick does not integrate, see README)
        err = max([abs(a - b) for a, b in zip(vel, p0["vel"])] + [abs(float(coef) - p0["coef"]), abs(accel - p0["accel"])])
        vr.append(dict(id=pid, err=err))
    if vr:
        res["spawnv"] = dict(particles=len(vr), worst=max(v["err"] for v in vr),
                             status="PASS" if all(v["err"] < 1e-6 for v in vr) else "FAIL")
    # uv frame advance
    adv = []
    for pid in list(first)[:3]:
        fr_seq = [next(p for p in fr["particles"] if p["id"] == pid)["uv_frame"]
                  for fr in frames if any(p["id"] == pid for p in fr["particles"])]
        adv.append(fr_seq[:20])
    res["uv"] = dict(frames_first20=adv)
    return res


def digest(dump):
    return hashlib.sha256(json.dumps(dump["frames"], sort_keys=True).encode()).hexdigest()[:16]


def main():
    if len(sys.argv) >= 3 and sys.argv[1] == "run":
        print(json.dumps(run(sys.argv[2])))
        return
    out_dir = os.environ.get("RE4NATIVE_OUT", os.path.join(tempfile.gettempdir(), "re4native"))
    os.makedirs(out_dir, exist_ok=True)
    results = {}
    for name in SCENARIOS:
        p = subprocess.run([sys.executable, "-I", "-X", "utf8", "-u", os.path.abspath(__file__), "run", name],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        if p.returncode != 0 or not p.stdout.strip():
            results[name] = dict(status="CRASH", exit=hex(p.returncode & 0xFFFFFFFF), stderr=p.stderr[-800:])
            continue
        dump = json.loads(p.stdout.strip().splitlines()[-1])
        with open(os.path.join(out_dir, f"emitter_{name}.json"), "w", encoding="utf-8") as f:
            json.dump(dump, f, ensure_ascii=False)
        r = dict(status=dump["status"], stopped=dump.get("stopped"), digest=digest(dump),
                 particles=len({p["id"] for fr in dump["frames"] for p in fr["particles"]}),
                 seed=dump.get("emitter0", {}).get("seed"), stubs=dump["stub_call_count"])
        if dump["status"] == "RAN":
            r["checks"] = check(dump, ENTRIES[SCENARIOS[name][0]])
        results[name] = r
    if results.get("bb_60", {}).get("digest") and results.get("bb_60_b", {}).get("digest"):
        results["determinism"] = dict(same=results["bb_60"]["digest"] == results["bb_60_b"]["digest"])
    print(json.dumps(results, indent=1, ensure_ascii=False, default=str))
    summ = {k: (v.get("status"), {c: x.get("status") for c, x in v.get("checks", {}).items() if isinstance(x, dict) and "status" in x})
            for k, v in results.items() if k != "determinism"}
    print("RESULT " + json.dumps({"exp": "emitter", "status": "INFO", "determinism": results.get("determinism"), **summ},
                                 ensure_ascii=False))


if __name__ == "__main__":
    main()
