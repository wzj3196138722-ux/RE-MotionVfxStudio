"""Step 2b: Blink 3dc7a20, the engine's own code against the triangle-wave formula (spec §4.5).

Hand-built block (builder layout: +0x10 MinRate, +0x14 MaxRate, +0x18 LowFrequency {s,r},
+0x20 LowFrequencyWidth, +0x28 HighFrequency, +0x30 HighFrequencyWidth; slots at +0x38, stride 0x14:
phase, fL, AL, fH, AH). The particle is spawned (flags alive|new) and then evaluated every tick;
p+0x50 is compared with the spec, float32 in the engine's operation order, CRT floorf:

  ph = fract(ph + fL*dt);  low = ph <= 0.5 ? ph*AL : AL - AL*ph
  high = fH != 0 ? tri(fract(fH*ph), AH) : 0;  rate = clamp(low + high, MinRate, MaxRate)
"""
import ctypes
import json
import math
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import re4native as rn  # noqa: E402

F = np.float32
FLOORF = rn.ucrt_floorf()
BLINK = 0x3DC7A20


def fract(x):
    return F(F(x) - F(FLOORF(float(x))))


def spec_eval(ph, fL, AL, fH, AH, dt, mn, mx):
    ph = fract(F(F(fL * F(dt)) + F(ph)))
    low = F(ph * AL) if F(0.5) >= ph else F(AL - F(AL * ph))
    v = low
    if F(fH) != F(0.0):
        q = fract(F(fH * ph))
        v = F(v + (F(q * AH) if F(0.5) >= q else F(AH - F(AH * q))))
    rate = F(mn) if F(mn) > v else (F(mx) if F(mx) <= v else v)
    return ph, rate


def same(a, b):
    a, b = F(a), F(b)
    return (math.isnan(a) and math.isnan(b)) or a.tobytes() == b.tobytes()


def run_case(img, name, prm, ticks=180, dt=1 / 60, slot=0, pid=0, seed=0xC0FFEE, state=1):
    em = rn.Emitter(img, seed=seed, state=state, dt=dt)
    blk = img.alloc(0x38 + 0x14 * 8)
    rn.set_f32(blk + 0x10, prm["MinRate"])
    rn.set_f32(blk + 0x14, prm["MaxRate"])
    for off, key in ((0x18, "LowFrequency"), (0x20, "LowFrequencyWidth"), (0x28, "HighFrequency"), (0x30, "HighFrequencyWidth")):
        s, r = prm[key]
        rn.set_f32(blk + off, s)
        rn.set_f32(blk + off + 4, r)
    p = em.particle(pid)
    fn = img.function(BLINK, None, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int32)
    sl = blk + 0x38 + 0x14 * slot
    rn.set_u32(p, 3)
    img.call(fn, blk, p, em.owner, slot)          # spawn tick: draws, then one evaluation
    ph0, fL, AL, fH, AH = (rn.f32(sl + 4 * i) for i in range(5))
    rn.set_u32(p, 1)
    # reconstruct the phase before the spawn-tick evaluation is not possible from the slot, so the
    # reference starts from the slot after the spawn tick
    ph = F(ph0)
    rows, bad = [], 0
    for k in range(1, ticks + 1):
        img.call(fn, blk, p, em.owner, slot)
        got_rate, got_ph = rn.f32(p + 0x50), rn.f32(sl)
        ph, ref = spec_eval(ph, F(fL), F(AL), F(fH), F(AH), dt, prm["MinRate"], prm["MaxRate"])
        ok = same(got_rate, ref) and same(got_ph, ph)
        bad += not ok
        ph = F(got_ph)
        if k in (1, 2, 15, 30, 45, 60, ticks):
            rows.append((k, float(got_ph), float(got_rate), float(ref), ok))
    return dict(case=name, slot=dict(phase_after_spawn_tick=ph0, fL=fL, AL=AL, fH=fH, AH=AH),
                ticks=ticks, bit_exact=ticks - bad, sample=rows, status="PASS" if bad == 0 else "FAIL")


BASE = dict(MinRate=0.0, MaxRate=1.0, LowFrequency=(1.0, 0.0), LowFrequencyWidth=(1.0, 0.0),
            HighFrequency=(0.0, 0.0), HighFrequencyWidth=(0.5, 0.0))
CASES = [
    ("low_only_1Hz", BASE, {}),
    ("official_0.2_1_hf3", dict(BASE, MinRate=0.2, HighFrequency=(3.0, 0.0)), {}),
    ("spreads_hf10x10", dict(BASE, MinRate=0.2, LowFrequency=(3.0, 1.0), HighFrequency=(10.0, 10.0)), dict(pid=5, state=77)),
    ("fL0_frozen", dict(BASE, LowFrequency=(0.0, 0.0)), dict(ticks=30)),
    ("fH_noninteger_2.5", dict(BASE, HighFrequency=(2.5, 0.0)), {}),
    ("fps30_slot3", dict(BASE, MinRate=0.3, HighFrequency=(3.0, 0.0)), dict(dt=1 / 30, ticks=90, slot=3)),
]


def main():
    img = rn.load()
    out = [run_case(img, n, prm, **kw) for n, prm, kw in CASES]
    # width doubling: AL / AH are stored as 2*W
    widths = dict(AL_over_W=out[0]["slot"]["AL"] / 1.0, AH_over_W=out[1]["slot"]["AH"] / 0.5)
    # not alive: p+0x50 untouched; no block + new: p+0x50 = 1.0
    em = rn.Emitter(img)
    p = em.particle(0)
    rn.set_f32(p + 0x50, 0.25)
    fn = img.function(BLINK, None, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int32)
    img.call(fn, img.alloc(0x100), p, em.owner, 0)
    dead_untouched = rn.f32(p + 0x50) == 0.25
    rn.set_u32(p, 3)
    img.call(fn, None, p, em.owner, 0)
    noblock_new = rn.f32(p + 0x50)
    extra = dict(widths_stored_doubled=widths, dead_particle_untouched=dead_untouched, no_block_new_particle_rate=noblock_new)
    print(json.dumps(dict(cases=out, extra=extra), indent=1, default=float))
    st = "PASS" if all(c["status"] == "PASS" for c in out) else "FAIL"
    print("RESULT " + json.dumps({"exp": "blink", "status": st,
                                  "cases": f'{sum(c["status"] == "PASS" for c in out)}/{len(out)}',
                                  "ticks_bit_exact": sum(c["bit_exact"] for c in out),
                                  "ticks": sum(c["ticks"] for c in out)}, default=float))


if __name__ == "__main__":
    main()
