"""Equidistant cylinder in re4.exe against equidistant_ref.py, on the one RE4 corpus entry that uses it.

    python -I -X utf8 -u exp_equidistant.py

csa071 smoke, entry at 0x3d: EmitterShape3D Cylinder, radius 0.2, RangeDivideHorizontalNum 3, DivideEquidistant,
UseExtension off, LocalRotation.Y 0.925; Transform3D LocalPosition (-0.2, 0.0734, 0); Spawn 3 at once after
EmitterDelayFrame 9. Compared: the engine's table (shape block +0x168, N = attribute +0x84) with build() from the
block's matrix, and every particle's spawn position with sample()·R (salt 0x30: b, then t; a from the counter).
"""
import json
import math
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np  # noqa: E402

STM = r"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\RE4_EXTRACT\natives\stm"
PATH = STM + r"\_chainsaw\vfx\effecteditor\efd_event\cs\efd_csa071_s00\efd_0015_csa071_s00_smoke_0004.efx.3539837"
OFF = 61
SHAPE = 102                       # RE4 EmitterShape3D type id
X = Z = (0.2, 0.2)
Y = (0.0, 0.0)
H, V = (0.0, 2 * math.pi), (1.0, 0.0)   # UseExtension off: the defaults
HN = 3
ROT_Y = 0.9250245


def main():
    import emitter as em
    import equidistant_ref as eq
    import re4native as rn
    import refsim
    img = rn.load()
    em.install_runtime(img)
    e = em.Entry(img, PATH, OFF)
    attr = next(a["attr"] for a in e.attrs if a["type"] == SHAPE)
    m = em.Emitter(img, e)
    m.play()
    blk = m.block_of(SHAPE)
    first = {}
    for _ in range(60):
        m.tick(1 / 60)
        es = m.emitter_state()
        for q in m.state():
            first.setdefault(q["id"], (es, q))
    n = rn.u32(attr + 0x84)
    table = struct.unpack(f"<{n}f", img.read(blk + 0x168, 4 * n))
    M = struct.unpack("<16f", img.read(blk + 0x80, 64))
    ref = eq.build(M, eq.F(eq.F(eq.F(X[0]) + eq.F(X[1])) * eq.F(0.5)), eq.F(eq.F(eq.F(Z[0]) + eq.F(Z[1])) * eq.F(0.5)),
                   n, H[0], eq.F(H[1]), True)
    table_bad = sum(float(a) != b for a, b in zip(ref, table))
    c, s = math.cos(ROT_Y), math.sin(ROT_Y)
    R = np.array([[c, 0, -s], [0, 1, 0], [s, 0, c]])
    worst = 0.0
    for k, (pid, (es, q)) in enumerate(sorted(first.items())):
        g = refsim.Rng(0x30, es["rng_state"], es["seed"], pid)
        a = (k % HN) / HN                       # H.span is 2pi: index / Hn
        b, t = float(g.u()), float(g.u())
        local = np.array([float(v) for v in eq.sample(ref, n, a, b, t, X, Y, Z, H, V, False)])
        worst = max(worst, float(np.abs(local @ R - np.array(q["pos"])).max()))
    out = dict(exp="equidistant", n=n, table_mismatches=table_bad, particles=len(first), worst_position=worst,
               status="PASS" if first and table_bad == 0 and worst < 1e-6 else "FAIL")
    print("RESULT " + json.dumps(out))


if __name__ == "__main__":
    main()
