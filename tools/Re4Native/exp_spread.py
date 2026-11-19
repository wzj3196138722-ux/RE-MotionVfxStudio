"""Velocity3D VelocityType Spread (3d1a470) on re4.exe: every RE4 corpus entry that uses it on a CPU type.

    python -I -X utf8 -u exp_spread.py

Model, read from 3d1a470 (salt 0x23, key = particle id; Spread mode takes no direction draws of its own):
  axis = normalize(DirX.center, DirY.center, DirZ.center); Spread == (0, 0) -> the axis itself
  u1 -> polar angle th = 0.5·(Spread.min + u1·Spread.extra); u2 -> azimuth ph = 2π·u2
  local = (cos th, sin th·cos ph, sin th·sin ph) about +X, turned onto the axis by the shortest-arc rotation
  (|axis.x| >= 0.999999: only the x component's sign follows the axis); not multiplied by the shape rotation
  then Speed (gated), velocity = direction · speed / 60
Compared: each particle's spawn velocity (tolerance 1e-6).
"""
import json
import math
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
STM = r"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\RE4_EXTRACT\natives\stm"
# (path under natives\stm, entry start, Velocity3D Flags, Dir centers, Spread (min, extra), Speed (center, spread))
ENTRIES = [
    (r"_chainsaw\vfx\effecteditor\efd_character\efd_cha0\efd_0015_cha0_chb7_jack_pl_0001_back_hold_dead_0001.efx.3539837", 4648, 0, [0, 0, 1], [0.5585054, 0], [2, 0]),
    (r"_chainsaw\vfx\effecteditor\efd_setmodel\efd_sm24_510_10\efd_0015_sm24_510_10_0000.efx.3539837", 2423, 0, [0, 1, 0], [0, 3.1415927], [4, 2]),
    (r"_chainsaw\vfx\effecteditor\efd_setmodel\efd_sm31_502_01\efd_0015_sm31_502_01_0000.efx.3539837", 2057, 0, [0, 1, 0], [0, 3.1415927], [1, 1.2]),
    (r"_chainsaw\vfx\effecteditor\efd_setmodel\efd_sm31_502_02\efd_0015_sm31_502_02_0000.efx.3539837", 2057, 0, [0, 1, 0], [0, 3.1415927], [1, 1.2]),
    (r"_chainsaw\vfx\effecteditor\efd_setmodel\efd_sm31_502_03\efd_0015_sm31_502_03_0000.efx.3539837", 2057, 0, [0, 1, 0], [0, 3.1415927], [1, 1.2]),
    (r"_chainsaw\vfx\effecteditor\efd_setmodel\efd_sm84_514_00\efd_0015_sm84_514_00_0000.efx.3539837", 2067, 0, [0, 0, 1], [0, 3.1415927], [1, 1.5]),
    (r"_chainsaw\vfx\effecteditor\efd_setmodel\efd_sm84_514_00\efd_0015_sm84_514_00_0000.efx.3539837", 4059, 0, [0, 0, 1], [0, 3.1415927], [1, 1.5]),
    (r"_chainsaw\vfx\effecteditor\efd_setmodel\efd_sm84_514_00\efd_0015_sm84_514_00_0001.efx.3539837", 3308, 0, [0, 0, 1], [0, 3.1415927], [1, 1.5]),
    (r"_chainsaw\vfx\effecteditor\efd_setmodel\efd_sm84_514_00\efd_0015_sm84_514_00_0001.efx.3539837", 5300, 0, [0, 0, 1], [0, 3.1415927], [1, 1.5]),
    (r"_chainsaw\vfx\effecteditor\efd_setmodel\efd_sm84_764_03\efd_0015_sm84_764_03_0001.efx.3539837", 2256, 0, [0, 0, 1], [0, 3.1415927], [3, 1.5]),
    (r"_chainsaw\vfx\effecteditor\efd_setmodel\efd_sm84_804_00\efd_0015_sm84_804_00_0000.efx.3539837", 604, 0, [0, 0, 1], [0, 3.1415927], [4.5, 1.5]),
    (r"_chainsaw\vfx\effecteditor\efd_setmodel\efd_sm84_804_00\efd_0015_sm84_804_00_0000.efx.3539837", 1623, 0, [0, 0, 1], [0, 3.1415927], [4.5, 1.5]),
]


def cone(axis, u1, u2, smin, sext):
    n = [v / math.sqrt(sum(c * c for c in axis)) for v in axis]
    th, ph = 0.5 * (smin + u1 * sext), 2 * math.pi * u2
    loc = [math.cos(th), math.sin(th) * math.cos(ph), math.sin(th) * math.sin(ph)]
    if abs(n[0]) >= 0.999999:
        return [loc[0] * (1 if n[0] >= 0 else -1), loc[1], loc[2]]
    k = [0.0, -n[2], n[1]]                       # (1,0,0) x n
    s = math.sqrt(k[1] ** 2 + k[2] ** 2)
    k = [c / s for c in k]
    c, sn = n[0], math.sqrt(max(0.0, 1 - n[0] ** 2))
    dot = sum(k[i] * loc[i] for i in range(3))
    cr = [k[1] * loc[2] - k[2] * loc[1], k[2] * loc[0] - k[0] * loc[2], k[0] * loc[1] - k[1] * loc[0]]
    return [loc[i] * c + cr[i] * sn + k[i] * dot * (1 - c) for i in range(3)]


def run(path, off):
    import emitter as em
    import re4native as rn
    img = rn.load()
    em.install_runtime(img)
    m = em.Emitter(img, em.Entry(img, path, off))
    m.play()
    first = {}
    for _ in range(400):
        m.tick(1 / 60)
        es = m.emitter_state()
        for q in m.state():
            first.setdefault(q["id"], dict(es=es, vel=q["vel"]))
    return first


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "run":
        print("RESULT " + json.dumps(run(sys.argv[2], int(sys.argv[3]))))
        return
    import refsim
    total, worst_all, rows = 0, 0.0, []
    for rel, off, flags, axis, spread, speed in ENTRIES:
        p = subprocess.run([sys.executable, "-I", "-X", "utf8", os.path.abspath(__file__), "run", os.path.join(STM, rel), str(off)],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        line = next((l for l in p.stdout.splitlines() if l.startswith("RESULT ")), None)
        if not line:
            rows.append(dict(entry=f"{os.path.basename(rel)}@{off}", status="CRASH"))
            continue
        worst = 0.0
        first = json.loads(line[7:])
        for pid, q in first.items():
            g = refsim.Rng(0x23, q["es"]["rng_state"], q["es"]["seed"], int(pid))
            for bit in (0x20, 0x40):              # delay draws come first when enabled
                if flags & bit:
                    g.next()
            u1, u2 = float(g.u()), float(g.u())
            spd = float(g.rnd_guard(*speed))
            v = [c * spd / 60 for c in cone(axis, u1, u2, *spread)]
            worst = max(worst, max(abs(a - b) for a, b in zip(v, q["vel"])))
        total += len(first)
        worst_all = max(worst_all, worst)
        rows.append(dict(entry=f"{os.path.basename(rel)}@{off}", particles=len(first), worst=worst))
    for r in rows:
        print(json.dumps(r))
    ok = all(r.get("particles") for r in rows) and worst_all < 1e-6
    print("RESULT " + json.dumps(dict(exp="spread", entries=len(rows), particles=total, worst=worst_all, status="PASS" if ok else "FAIL")))


if __name__ == "__main__":
    main()
