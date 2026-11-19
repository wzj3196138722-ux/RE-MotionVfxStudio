"""Step 2a: ScaleAnim, the engine's own code against the spec formula (RE4-EFX-RUNTIME §3.3).

  A  closed form 3cfd420 f(t, base, add, coef) over a grid of t / add / coef, bit for bit
  B  per-particle step 3d1d830 + base capture 3d1dd00 on hand-built blocks, frame by frame:
     Coef 1, Coef 0, Coef 0.5 (first step), corpus-like spreads, shrink to the 0 floor,
     Coef < 0 (NaN), 30 fps, slot index 2, under LegacyMode2018 ([g+0xeb2]) = 0 and = 1
  C  gates: particle flag2 0x800, tick flag 0x10, tick flag 0x20, and the r9d = 0 branch

Reference (spec, float32 in the engine's operation order, CRT powf):
  c == 1 : base + add*t          else : base + (1 - c^t) * (add*c) / (1 - c)      each slot max(0, .)
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
POWF = rn.ucrt_powf()

SIZE_FN = 0x3CFD420
STEP = 0x3D1D830
CAPTURE = 0x3D1DD00
SLOT_STRIDE = 0x34


def spec_f(t, base, add, c):
    t, base, add, c = F(t), F(base), F(add), F(c)
    if c == F(1.0):
        return F(F(t * add) + base)
    pw = F(POWF(float(c), float(t)))
    return F(F(F(F(F(1.0) - pw) * F(add * c)) / F(F(1.0) - c)) + base)


def spec_slot(t, base, add, c):
    v = spec_f(t, base, add, c)
    return F(0.0) if F(0.0) > v else v      # comiss 0, v ; jbe -> NaN is kept


def same(a, b):
    a, b = F(a), F(b)
    return (math.isnan(a) and math.isnan(b)) or a.tobytes() == b.tobytes()


def part_a(img):
    fn = img.function(SIZE_FN, ctypes.c_float, ctypes.c_float, ctypes.c_float, ctypes.c_float, ctypes.c_float)
    n = bad = 0
    worst = []
    for c in (1.0, 0.0, 0.5, 0.9, 0.94, 0.95, 0.99, 0.995, 1.01, -0.5):
        for add in (0.1, -0.003, 0.02, 0.4):
            for base in (0.0, 1.0, 0.25):
                for t in [k * 0.5 for k in range(0, 241)] + [0.37, 1.0001, 59.99]:
                    got = img.call(fn, t, base, add, c)
                    ref = spec_f(t, base, add, c)
                    n += 1
                    if not same(got, ref):
                        bad += 1
                        if len(worst) < 5:
                            worst.append(dict(t=t, base=base, add=add, c=c, got=float(got), ref=float(ref)))
    return dict(cases=n, bit_exact=n - bad, mismatches=worst,
                coef0=float(img.call(fn, 30.0, 1.0, 0.1, 0.0)), coef1_t10=float(img.call(fn, 10.0, 1.0, 0.1, 1.0)),
                coef_neg_t1_5=float(img.call(fn, 1.5, 1.0, 0.1, -0.5)),
                status="PASS" if bad == 0 else "FAIL")


# block layout written by the builder 3d1d640 (engine order, each {s, r})
BLOCK = dict(ScalarAdd=0x10, ScalarCoef=0x18, XAdd=0x20, YAdd=0x28, ZAdd=0x30, XCoef=0x38, YCoef=0x40, ZCoef=0x48)
# per-particle slot (block + slot*0x34): Add U/X/Y/Z, Coef U/X/Y/Z, base X/Y/Z, base U, age0
SLOT = dict(AddU=0x50, AddX=0x54, AddY=0x58, AddZ=0x5C, CoefU=0x60, CoefX=0x64, CoefY=0x68, CoefZ=0x6C,
            baseX=0x70, baseY=0x74, baseZ=0x78, baseU=0x7C, age0=0x80)
PSLOT = (0x80, 0x84, 0x88, 0x8C)   # particle size slots U, X, Y, Z


class Sim:
    def __init__(self, img, ranges, base=(1.0, 1.0, 1.0, 1.0), slot=0, seed=0x1234567, state=1, pid=0, dt=1 / 60):
        self.img = img
        self.em = rn.Emitter(img, seed=seed, state=state, dt=dt)
        self.block = img.alloc(0x50 + SLOT_STRIDE * 8)
        for name, off in BLOCK.items():
            s, r = ranges.get(name, (0.0, 0.0))
            rn.set_f32(self.block + off, s)
            rn.set_f32(self.block + off + 4, r)
        self.slot = slot
        self.sl = self.block + slot * SLOT_STRIDE
        self.p = self.em.particle(pid)
        self.base = base
        self.step_fn = img.function(STEP, None, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int32, ctypes.c_int32)
        self.cap_fn = img.function(CAPTURE, None, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int32)

    def step(self, r9=1):
        self.img.call(self.step_fn, self.block, self.p, self.em.owner, r9, self.slot)

    def spawn(self):
        """Spawn tick as in 3d08480: flags alive|new, age += dt*60, 3d05350 (ScaleAnim init),
        the Type writes the size slots, post functor captures the base, 'new' is cleared."""
        rn.set_u32(self.p, 3)
        self.age_step()
        self.step()
        for off, v in zip(PSLOT, self.base):
            rn.set_f32(self.p + off, v)
        self.img.call(self.cap_fn, self.block, self.p, self.em.owner, self.slot)
        rn.set_u32(self.p, rn.u32(self.p) & ~2)

    def age_step(self):
        rn.set_f32(self.p + 0x58, F(F(rn.f32(self.p + 0x58)) + F(F(self.em.dt) * F(60.0))))

    def tick(self):
        self.age_step()
        self.step()
        return [rn.f32(self.p + o) for o in PSLOT]

    def slotvals(self):
        return {k: rn.f32(self.sl + o) for k, o in SLOT.items()}


def run_case(img, name, ranges, ticks=60, **kw):
    sim = Sim(img, ranges, **kw)
    sim.spawn()
    sv = sim.slotvals()
    adds = (sv["AddU"], sv["AddX"], sv["AddY"], sv["AddZ"])
    coefs = (sv["CoefU"], sv["CoefX"], sv["CoefY"], sv["CoefZ"])
    bases = (sv["baseU"], sv["baseX"], sv["baseY"], sv["baseZ"])
    rows, bad = [], 0
    for k in range(1, ticks + 1):
        got = sim.tick()
        t = F(F(rn.f32(sim.p + 0x58)) - F(sv["age0"]))
        ref = [spec_slot(t, b, a, c) for b, a, c in zip(bases, adds, coefs)]
        ok = all(same(g, r) for g, r in zip(got, ref))
        bad += not ok
        rows.append((k, float(t), got, [float(x) for x in ref], ok))
    first_inc = rows[0][2][0] - bases[0]
    return dict(case=name, slot=sv, first_increment_U=first_inc, ticks=ticks, bit_exact=ticks - bad,
                sample=[rows[i] for i in (0, 1, 2, 9, ticks - 1) if i < ticks],
                status="PASS" if bad == 0 else "FAIL")


CASES = [
    ("coef1_linear", dict(ScalarAdd=(0.1, 0), ScalarCoef=(1, 0), XCoef=(1, 0), YCoef=(1, 0)), {}),
    ("coef0_freeze", dict(ScalarAdd=(0.1, 0), ScalarCoef=(0, 0), XAdd=(0.05, 0), XCoef=(0, 0)), {}),
    ("coef0.5_first_step", dict(ScalarAdd=(0.1, 0), ScalarCoef=(0.5, 0)), {}),
    ("corpus_like_spreads", dict(ScalarAdd=(0.02, 0.0025), ScalarCoef=(0.95, 0), XAdd=(-0.004, 0.001), XCoef=(1, 0),
                                 YAdd=(0.008, 0.002), YCoef=(1, 0)), dict(base=(0.5, 1.0, 1.0, 1.0), pid=7, state=0xBEEF)),
    ("shrink_floor", dict(ScalarAdd=(-0.1, 0), ScalarCoef=(1, 0)), dict(base=(0.5, 1, 1, 1))),
    ("coef_negative", dict(ScalarAdd=(0.1, 0), ScalarCoef=(-0.5, 0)), dict(ticks=5)),
    ("coef_negative_fps50", dict(ScalarAdd=(0.1, 0), ScalarCoef=(-0.5, 0)), dict(ticks=5, dt=1 / 50)),
    ("fps30", dict(ScalarAdd=(0.05, 0), ScalarCoef=(0.9, 0), YAdd=(0.01, 0), YCoef=(0.98, 0)), dict(dt=1 / 30, ticks=30)),
    ("slot2_mesh_xyz", dict(ScalarAdd=(0.03, 0.01), ScalarCoef=(0.97, 0.01), XAdd=(0.01, 0), XCoef=(1, 0),
                            YAdd=(0.02, 0), YCoef=(0.99, 0), ZAdd=(0.03, 0.005), ZCoef=(1, 0)), dict(slot=2, pid=3)),
]


def part_b(img, eb2):
    rn.install_settings(img, eb0=0, eb2=eb2)
    out = []
    for name, ranges, kw in CASES:
        kw = dict(kw)
        ticks = kw.pop("ticks", 60)
        out.append(run_case(img, name, ranges, ticks=ticks, **kw))
    return out


def part_c(img):
    rn.install_settings(img, eb2=0)
    res = {}
    r = dict(ScalarAdd=(0.1, 0), ScalarCoef=(1, 0), XAdd=(0.1, 0), XCoef=(1, 0), YAdd=(0.1, 0), YCoef=(1, 0), ZAdd=(0.1, 0), ZCoef=(1, 0))
    # flag2 0x800: nothing written
    s = Sim(img, r); s.spawn(); rn.set_u32(s.p + 4, 0x800); before = [rn.f32(s.p + o) for o in PSLOT]
    res["flag2_0x800_skips"] = s.tick() == before
    # tick flag 0x10: nothing written
    s = Sim(img, r); s.spawn(); s.em.flags = 0x10; before = [rn.f32(s.p + o) for o in PSLOT]
    res["tickflag_0x10_skips"] = s.tick() == before
    # tick flag 0x20: re-runs init every tick (age0 follows the age, size not recomputed)
    s = Sim(img, r); s.spawn(); s.em.flags = 0x20; s.tick()
    res["tickflag_0x20_reinit"] = dict(age=rn.f32(s.p + 0x58), age0=rn.f32(s.sl + SLOT["age0"]),
                                       sizeU=rn.f32(s.p + 0x80))
    # r9d = 0 branch: X and Y with Add * const, Z untouched
    s = Sim(img, r); s.spawn(); rn.set_f32(s.p + 0x8C, 7.0)
    s.age_step(); s.step(r9=0)
    k = rn.f32(rn.IMAGE_BASE + 0x3D1D91B + 0x3CA55D1)
    res["r9d0_branch"] = dict(const_at_79c2eec=k, U=rn.f32(s.p + 0x80), X=rn.f32(s.p + 0x84),
                              Y=rn.f32(s.p + 0x88), Z_untouched=rn.f32(s.p + 0x8C) == 7.0)
    return res


def main():
    img = rn.load()
    out = dict(A=part_a(img))
    # the settings object must exist: 3e0e9b0 dereferences qword [d62f8f0] (null in a fresh image)
    out["B_eb2_0"] = part_b(img, 0)
    out["B_eb2_1"] = part_b(img, 1)
    out["C"] = part_c(img)
    print(json.dumps(out, indent=1, default=float))
    allb = [c["status"] for c in out["B_eb2_0"] + out["B_eb2_1"]]
    st = "PASS" if out["A"]["status"] == "PASS" and all(x == "PASS" for x in allb) else "FAIL"
    fi = {c["case"]: (c["first_increment_U"]) for c in out["B_eb2_0"]}
    fi1 = {c["case"]: (c["first_increment_U"]) for c in out["B_eb2_1"]}
    print("RESULT " + json.dumps({"exp": "scaleanim", "status": st,
                                  "closed_form": f'{out["A"]["bit_exact"]}/{out["A"]["cases"]}',
                                  "step_cases": f'{allb.count("PASS")}/{len(allb)}',
                                  "first_inc_coef0.5_eb2_0": fi["coef0.5_first_step"],
                                  "first_inc_coef0.5_eb2_1": fi1["coef0.5_first_step"]}, default=float))


if __name__ == "__main__":
    main()
