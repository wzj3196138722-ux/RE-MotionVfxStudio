"""Step 4: the engine's samplers against the reference RNG (refsim.py, spec §1.6).

  seed   the two seeders 3d09990 (E through owner+8) and 3d099e0 (E direct) on random
         (salt, E.state, E.seed, key): xs(xs(salt + state + key) + seed)
  scale  ScaleAnim spawn draws (salt 0x27, key = particle id), 8 guarded CenterSpread draws, with a
         mix of r = 0 fields so the "no draw when |r| <= 1e-6" rule changes the stream position
  blink  Blink spawn draws (salt 0x087f7c20), 4 unguarded CenterSpread draws + the phase u
Every case uses random E.seed / E.state / particle id / slot index.
"""
import ctypes
import json
import os
import random
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import re4native as rn  # noqa: E402
import refsim  # noqa: E402
from exp_scaleanim import BLOCK, SLOT, SLOT_STRIDE, same  # noqa: E402

F = np.float32
rnd = random.Random(20261115)


def seeders(img, n=20000):
    s990 = img.function(0x3D09990, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_uint32)
    s9e0 = img.function(0x3D099E0, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_uint32)
    em = rn.Emitter(img)
    out = img.alloc(16)
    bad = 0
    for _ in range(n):
        salt, st, sd, key = (rnd.getrandbits(32) for _ in range(4))
        em.seed, em.state = sd, st
        ref = refsim.seed(salt, st, sd, key)
        img.call(s990, out, salt, em.owner, key)
        a = rn.u32(out)
        img.call(s9e0, out, salt, em.E, key)
        bad += (a != ref) + (rn.u32(out) != ref)
    return dict(cases=2 * n, match=2 * n - bad, status="PASS" if bad == 0 else "FAIL")


def scaleanim(img, n=3000):
    rn.install_settings(img, eb2=0)
    step = img.function(0x3D1D830, None, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int32, ctypes.c_int32)
    order = ["ScalarAdd", "ScalarCoef", "XAdd", "XCoef", "YAdd", "YCoef", "ZAdd", "ZCoef"]
    keys = ["AddU", "CoefU", "AddX", "CoefX", "AddY", "CoefY", "AddZ", "CoefZ"]
    em = rn.Emitter(img)
    blk = img.alloc(0x50 + SLOT_STRIDE * 64)
    p = em.particle(0)
    bad, skipped_draws = 0, 0
    for i in range(n):
        ranges = []
        for k in order:
            s = rnd.uniform(-1, 1)
            r = rnd.choice([0.0, 0.0, 1e-7, -1e-7, 2e-6, rnd.uniform(0, 0.5), -rnd.uniform(0, 0.5)])
            ranges.append((float(F(s)), float(F(r))))
            rn.set_f32(blk + BLOCK[k], s)
            rn.set_f32(blk + BLOCK[k] + 4, r)
        skipped_draws += sum(abs(r) <= 1e-6 for _, r in ranges)
        em.seed, em.state = rnd.getrandbits(32), rnd.getrandbits(32)
        pid, slot = rnd.getrandbits(16), rnd.randrange(64)
        rn.set_u32(p + 0x18, pid)
        rn.set_u32(p, 3)
        img.call(step, blk, p, em.owner, 1, slot)
        sl = blk + slot * SLOT_STRIDE
        ref = refsim.scaleanim_draws(em.state, em.seed, pid, ranges)
        bad += not all(same(rn.f32(sl + SLOT[k]), v) for k, v in zip(keys, ref))
    return dict(particles=n, fields_without_draw=skipped_draws, match=n - bad, status="PASS" if bad == 0 else "FAIL")


def blink(img, n=3000):
    fn = img.function(0x3DC7A20, None, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int32)
    em = rn.Emitter(img, dt=0.0)          # dt 0: the spawn-tick evaluation leaves the phase as drawn
    blk = img.alloc(0x38 + 0x14 * 64)
    p = em.particle(0)
    bad = 0
    for i in range(n):
        pr = [(float(F(rnd.uniform(0, 5))), float(F(rnd.choice([0.0, rnd.uniform(0, 3)])))) for _ in range(4)]
        rn.set_f32(blk + 0x10, 0.0)
        rn.set_f32(blk + 0x14, 1.0)
        for j, (s, r) in enumerate(pr):
            rn.set_f32(blk + 0x18 + 8 * j, s)
            rn.set_f32(blk + 0x1C + 8 * j, r)
        em.seed, em.state = rnd.getrandbits(32), rnd.getrandbits(32)
        pid, slot = rnd.getrandbits(16), rnd.randrange(64)
        rn.set_u32(p + 0x18, pid)
        rn.set_u32(p, 3)
        img.call(fn, blk, p, em.owner, slot)
        sl = blk + 0x38 + 0x14 * slot
        fL, AL, fH, AH, ph = refsim.blink_draws(em.state, em.seed, pid, *pr)
        # with dt = 0 the evaluation stores fract(ph + 0) = ph (u < 1), or 0 when u rounded to 1.0
        ph_stored = F(ph - F(np.floor(ph)))
        got = [rn.f32(sl + 4 * i) for i in range(5)]
        bad += not all(same(a, b) for a, b in zip(got, [ph_stored, fL, AL, fH, AH]))
    return dict(particles=n, match=n - bad, status="PASS" if bad == 0 else "FAIL")


def main():
    img = rn.load()
    out = dict(seed=seeders(img), scaleanim=scaleanim(img), blink=blink(img))
    print(json.dumps(out, indent=1))
    st = "PASS" if all(v["status"] == "PASS" for v in out.values()) else "FAIL"
    print("RESULT " + json.dumps({"exp": "rng", "status": st, **{k: f'{v["match"]}/{v.get("cases", v.get("particles"))}' for k, v in out.items()}}))


if __name__ == "__main__":
    main()
