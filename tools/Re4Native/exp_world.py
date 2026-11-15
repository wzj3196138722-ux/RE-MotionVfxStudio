"""PtCollision / PtColliderAction against a flat ground, and PtLife with the Action recorder (world.py).

    python -I -X utf8 -u exp_world.py                 # all scenarios, one child process each
    python -I -X utf8 -u exp_world.py run <scenario>  # one scenario here (JSON on stdout)

The ground plane is put 0.3 m below the lowest start point of the first queries (y = GROUND, normal +Y).
Checks (spec §3.6, §3.7, §5.1; ptcollision.md, ptlife.md):
  bounce   on a processed hit that leaves hits > 0: v = vel*coef^n + (0, gvel + n*accel + accel, 0) after the
           tick's integration (3d630c0 adds one more gravity step), v' = (vx*r*H, -vy*r*V, vz*r*H), gvel = 0
  count    hits processed until the finish == the particle's sampled BounceNum
  finish   at the last hit [p+4] gets 0x200 (Kill) or 0x400 (Freeze), plus 0x800 with flag bit 3
  delay    no hit is processed / no state initialised before age >= DelayFrameCollision
  latency  every processed hit at tick t answers a query submitted at tick t-1
  ptlife   per particle each non -1 status is passed once; an Action fires once when the status equals
           the attribute's Status; zero-length phases are never passed; with follow bits the instance
           translation moves with the particle
"""
import json
import os
import struct
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np  # noqa: E402

F = np.float32
STM = r"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\RE4_EXTRACT\natives\stm"
SCENARIOS = {
    # cse350 dust_cloud root 13: Mesh, Velocity3D (gravity), PtCollision ray, BounceNum 3, rate 0.25, V 1, H 2, Freeze
    "col_freeze": (STM + r"\_anotherorder\vfx\effecteditor\efd_event\cs\efd_cse350_s00\efd_0015_cse350_s00_dust_cloud_0000.efx.3539837", 0x369E),
    # csa067 break root 12: Mesh, sphere cast (flag bit 2), BounceNum 2..5, Kill
    "col_kill": (STM + r"\_chainsaw\vfx\effecteditor\efd_event\cs\efd_csa067_s00\efd_0015_csa067_s00_break_0000.efx.3539837", 0x2CD2),
    # cse940 fire root 5: NoDraw, no FixRandomGenerator, PtLife + PtCollision with DelayFrameCollision 15, BounceNum 50
    "col_delay": (STM + r"\_anotherorder\vfx\effecteditor\efd_event\cs\efd_cse940_s00\efd_0015_cse940_s00_fire_0000.efx.3539837", 0x25FE),
    # sm82_614 root 3: Billboard3D with PtColliderAction and PtCollision (BounceNum 1, Kill)
    "colact": (STM + r"\_chainsaw\vfx\effecteditor\efd_setmodel\efd_sm82_614_00\efd_0015_sm82_614_00_0000.efx.3539837", 0xFA3),
    # csa023 footprints root 0: NoDraw, Life, PtLife Status 0 (Flags 12, ActionIndex 2)
    "life0": (STM + r"\_chainsaw\vfx\effecteditor\efd_event\cs\efd_csa023_s00\efd_0015_csa023_s00_footprints_0002.efx.3539837", 0x6FC5),
    # csa075 debris root 0: NoDraw, Velocity3D, PtLife Status 3 (Flags 28 = follow position, rotation, scale)
    "life3": (STM + r"\_chainsaw\vfx\effecteditor\efd_event\cs\efd_csa075_s00\efd_0015_csa075_s00_debris_0200.efx.3539837", 0x50F9),
}
TICKS = 150
T_COL, T_ACT, T_LIFE = 162, 161, 131


def run(name):
    import ctypes
    import emitter as em
    import re4native as rn
    import world
    path, off = SCENARIOS[name]
    img = rn.load()
    em.install_runtime(img, collision=True, ptlife=True)
    world.GROUND[0] = -1e9
    out = dict(scenario=name, file=os.path.basename(path), offset=hex(off), frames=[])
    try:
        e = em.Entry(img, path, off)
        out["attributes"] = [(a["type"], a["bytes"]) for a in e.attrs]
        m = em.Emitter(img, e)
        col = next((a["attr"] for a in e.attrs if a["type"] == T_COL), None)
        if col:
            out["ptcollision"] = dict(flags=rn.u32(col + 0x80), bounce_num=struct.unpack("<2i", img.read(col + 0x48, 8)),
                                      rate=struct.unpack("<2f", img.read(col + 0x50, 8)), V=rn.f32(col + 0x58),
                                      H=rn.f32(col + 0x5C), finish=rn.u32(col + 0x60),
                                      delay=struct.unpack("<2i", img.read(col + 0x78, 8)))
        pl = next((a["attr"] for a in e.attrs if a["type"] == T_LIFE), None)
        if pl:
            act = rn.u64(pl + 0x28)
            files = rn.u64(rn.u64(e.loadctx) + 0x58)
            out["ptlife"] = dict(status=struct.unpack("<i", img.read(pl + 0x30, 4))[0],
                                 action_index=(act - files) // 0x38 if act else None,
                                 follow=list(img.read(pl + 0x3C, 3)), pool=rn.u32(pl + 0x38))
        m.play()
        cblk = m.block_of(T_COL)
        lblk = m.block_of(T_LIFE)
        ablk = m.block_of(T_ACT)
        for k in range(1, TICKS + 1):
            q0, l0 = len(world.QUERIES), len(world.PTLIFE)
            m.tick(1 / 60)
            if world.GROUND[0] < -1e8 and len(world.QUERIES) > q0:
                world.GROUND[0] = min(q["frm"][1] for q in world.QUERIES) - 0.3
                out["ground"] = world.GROUND[0]
            parts = m.state()
            for p in parts:
                if lblk and rn.u64(lblk + 0x78):          # particle -> Action instance map (int16 per slot)
                    p["ptlife_inst"] = struct.unpack("<h", img.read(rn.u64(lblk + 0x78) + 2 * p["slot"], 2))[0]
                if cblk:
                    s = rn.u64(cblk + 0x70) + p["slot"] * 0x1C
                    c = struct.unpack("<if3fif", img.read(s, 0x1C))
                    side = m.side(p["slot"]) + rn.u32(cblk + 8)
                    p["col"] = dict(handle=c[0], radius=c[1], prev=list(c[2:5]), hits=c[5], rate=c[6],
                                    init=rn.u32(side) & 1, delay=struct.unpack("<i", img.read(side + 4, 4))[0])
            fr = dict(tick=k, particles=parts, queries=world.QUERIES[q0:], ptlife=world.PTLIFE[l0:])
            if lblk:
                fr["instances"] = [i for i in world.instance_matrices_blk(lblk) if i["flags"] & 1]
            if ablk:                                  # PtColliderAction pool: blk+0xa8+8k -> instance, count attr+0x34
                attr = rn.u64(ablk)
                fr["act_instances"] = []
                for k2 in range(rn.u32(attr + 0x34)):
                    i = rn.u64(ablk + 0xA8 + 8 * k2)
                    if i and rn.u32(i + 8) & 1:
                        fr["act_instances"].append(dict(instance=k2, flags=rn.u32(i + 8), stamp=rn.u32(i + 0xC),
                                                        matrix=list(struct.unpack("<16f", img.read(i + 0x10, 64)))))
            out["frames"].append(fr)
        out["status"] = "RAN"
    except (rn.NativeFault, rn.UnresolvedImportCalled, RuntimeError) as ex:
        out["status"] = "STUCK"
        out["stopped"] = str(ex)
    out["action_ticks"] = {n: sum(1 for x in world.ACTION_TICKS if x[0] == n) for n in {x[0] for x in world.ACTION_TICKS}}
    out["stubs"] = sorted({c[0] for c in em.CALLS})
    out["names"] = sorted({c[5] for c in em.CALLS if c[0].startswith("collision") and c[5]})
    return out


# ------------------------------------------------------------------------------------------- checks
def _powf(a, b):
    import ctypes
    f = ctypes.CDLL("ucrtbase").powf
    f.restype, f.argtypes = ctypes.c_float, [ctypes.c_float, ctypes.c_float]
    return f(a, b)


def check_collision(d):
    pc = d.get("ptcollision")
    if not pc:
        return None
    frames = d["frames"]
    hist = {}
    for fr in frames:
        for p in fr["particles"]:
            hist.setdefault(p["id"], []).append((fr["tick"], p))
    bounce = dict(n=0, ok=0, worst=0.0)
    count = dict(n=0, ok=0, fails=[])
    finish = dict(n=0, ok=0, fails=[])
    delay = dict(n=0, ok=0)
    latency = dict(n=0, ok=0)
    hitq = {fr["tick"]: sum(1 for q in fr["queries"] if q["hit"]) for fr in frames}
    proc = {}
    lo, hi = pc["delay"]
    for pid, h in hist.items():
        start = None
        processed = 0
        for (t0, a), (t1, b) in zip(h, h[1:]):
            ca, cb = a.get("col"), b.get("col")
            if cb and cb["init"] and not ca["init"]:
                delay["n"] += 1
                delay["ok"] += b["age"] >= cb["delay"]
            if cb and ca and ca["hits"] > 0 and start is None:
                start = ca["hits"]
            if ca and cb and cb["hits"] == ca["hits"] - 1 and ca["init"]:
                processed += 1
                proc[t1] = proc.get(t1, 0) + 1
                r = ca["rate"]
                if cb["hits"] > 0:
                    vel = [F(F(x) * F(_powf(F(a["coef"]), 1.0))) for x in a["vel"]]
                    g1 = F(F(a["gvel"]) + F(a["accel"]))
                    vy = F(F(vel[1]) + F(g1 + F(a["accel"])))
                    exp = [vel[0] * F(r * pc["H"]), -vy * F(r * pc["V"]), vel[2] * F(r * pc["H"])]
                    err = max(abs(float(x) - y) for x, y in zip(exp, b["vel"])) + abs(b["gvel"])
                    bounce["n"] += 1
                    bounce["ok"] += err < 2e-4
                    bounce["worst"] = max(bounce["worst"], err)
                else:
                    finish["n"] += 1
                    want = {1: 0x200, 2: 0x400}.get(pc["finish"], 0) | (0x800 if pc["flags"] & 8 else 0)
                    ok = (b["flags2"] & want) == want if want else True
                    if pc["finish"] == 1:                     # Kill: 0x200 is serviced at the start of the next
                        later = [t for t, _ in h if t > t1]   # tick, so the particle must be gone after t1
                        ok = ok or not later
                    finish["ok"] += ok
                    if not ok:
                        finish["fails"].append((pid, t1, hex(b["flags2"])))
                    count["n"] += 1
                    count["ok"] += processed == start
                    if processed != start:
                        count["fails"].append((pid, start, processed))
    for t, n in proc.items():
        latency["n"] += n
        latency["ok"] += n if n <= hitq.get(t - 1, 0) else 0
    res = dict(bounce=bounce, count=count, finish=finish, delay=delay, latency=latency,
               queries=sum(len(fr["queries"]) for fr in frames), hit_queries=sum(hitq.values()))
    good = all(x["ok"] == x["n"] for x in (bounce, count, finish, delay, latency)) and bounce["n"] + finish["n"] > 0
    res["status"] = "PASS" if good else "FAIL"
    return res


def check_ptlife(d):
    pl = d.get("ptlife")
    if not pl:
        return None
    per = {}
    for fr in d["frames"]:
        for c in fr["ptlife"]:
            per.setdefault(c["id"], []).append((fr["tick"], c["status"], bool(c.get("fired"))))
    once, fired_ok, zero = [], [], []
    life = {}
    for fr in d["frames"]:
        for p in fr["particles"]:
            life.setdefault(p["id"], p.get("life_rec"))
    for pid, ev in per.items():
        sts = [s for _, s, _ in ev if s != -1]
        once.append(len(sts) == len(set(sts)))
        nf = sum(f for _, _, f in ev)
        fired_ok.append(nf == (1 if pl["status"] in sts and pl["action_index"] is not None else 0))
        rec = life.get(pid)
        if rec:
            A, K, V = rec[1], rec[2] - rec[1], rec[3] - rec[2]
            zero.append(not ((A == 0 and 1 in sts) or (V == 0 and 3 in sts)))
    # follow (Flags bit 2): while the particle lives, its instance's translation (matrix row 3) moves by the
    # same amount as the particle position; without the bit it stays at the trigger snapshot
    follow = dict(n=0, ok=0, worst=0.0)
    prev = None
    for fr in d["frames"]:
        inst = {i["instance"]: i["matrix"][12:15] for i in fr.get("instances", [])}
        cur = {p["id"]: (p.get("ptlife_inst", -1), p["pos"]) for p in fr["particles"]}
        if prev:
            for pid, (k, pos) in cur.items():
                pk, ppos = prev[1].get(pid, (-1, None))
                if k >= 0 and k == pk and k in inst and k in prev[0]:
                    dinst = [a - b for a, b in zip(inst[k], prev[0][k])]
                    dpos = [a - b for a, b in zip(pos, ppos)] if pl["follow"][0] else [0.0, 0.0, 0.0]
                    err = max(abs(a - b) for a, b in zip(dinst, dpos))
                    follow["n"] += 1
                    follow["ok"] += err < 1e-5
                    follow["worst"] = max(follow["worst"], err)
        prev = (inst, cur)
    res = dict(status_field=pl["status"], action_index=pl["action_index"], follow_bits=pl["follow"],
               particles=len(per), once=f"{sum(once)}/{len(once)}", fired=f"{sum(fired_ok)}/{len(fired_ok)}",
               zero_length=f"{sum(zero)}/{len(zero)}", fires=sum(1 for v in per.values() for e in v if e[2]),
               status_counts={s: sum(1 for v in per.values() for e in v if e[1] == s) for s in (0, 1, 2, 3, 4)},
               follow=follow)
    res["status"] = "PASS" if (all(once) and all(fired_ok) and all(zero) and res["fires"] > 0
                               and follow["ok"] == follow["n"]) else "FAIL"
    return res


def check_colact(d):
    """PtColliderAction: an Action instance becomes active the tick after a hit query, its translation is
    the hit point and its +Y axis (matrix row 1, normalised) the hit normal (0, 1, 0)."""
    if not any("act_instances" in fr for fr in d["frames"]):
        return None
    seen, out = set(), []
    hits = {fr["tick"]: [q["hit"] for q in fr["queries"] if q["hit"]] for fr in d["frames"]}
    for fr in d["frames"]:
        for i in fr.get("act_instances", []):
            if i["instance"] in seen:
                continue
            seen.add(i["instance"])
            m = i["matrix"]
            y = m[4:7]
            ny = sum(v * v for v in y) ** 0.5 or 1.0
            tr = m[12:15]
            prev = hits.get(fr["tick"] - 1, [])
            near = min((max(abs(a - b) for a, b in zip(tr, h)) for h in prev), default=None)
            out.append(dict(tick=fr["tick"], translation=tr, y_axis=[v / ny for v in y], hit_dist=near))
    ok = bool(out) and all(o["hit_dist"] is not None and o["hit_dist"] < 1e-4 and abs(o["y_axis"][1] - 1) < 1e-5 for o in out)
    return dict(activations=out, status="PASS" if ok else "FAIL")


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
            results[name] = dict(status="CRASH", exit=hex(p.returncode & 0xFFFFFFFF), stderr=p.stderr[-600:])
            continue
        d = json.loads(p.stdout.strip().splitlines()[-1])
        with open(os.path.join(out_dir, f"world_{name}.json"), "w", encoding="utf-8") as f:
            json.dump(d, f, ensure_ascii=False)
        r = dict(status=d["status"], stopped=d.get("stopped"), ptcollision=d.get("ptcollision"), ptlife_attr=d.get("ptlife"),
                 particles=len({p["id"] for fr in d["frames"] for p in fr["particles"]}), ground=d.get("ground"),
                 stubs=d["stubs"], filter_names=d["names"], action_ticks=d["action_ticks"])
        if d["status"] == "RAN":
            r["collision"] = check_collision(d)
            r["ptlife"] = check_ptlife(d)
            r["collider_action"] = check_colact(d)
        results[name] = r
    print(json.dumps(results, indent=1, ensure_ascii=False, default=str))
    print("RESULT " + json.dumps({"exp": "world", "status": "INFO", **{
        k: (v["status"], (v.get("collision") or {}).get("status"), (v.get("ptlife") or {}).get("status"),
            (v.get("collider_action") or {}).get("status"))
        for k, v in results.items()}}))


if __name__ == "__main__":
    main()
