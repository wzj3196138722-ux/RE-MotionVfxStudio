"""Step 5 probe: call the whole per-particle pipeline 3d05350 once per tick with a mostly empty ctx.

Every attribute slot of ctx is null except ScaleAnim (ctx+0x1f0) and Blink (ctx+0x230); the call
struct members ([+8] LegacyMode2016 byte, [+0x10] type ctx, [+0x18], [+0x20]->+8) point at zeroed
buffers. The question is how far the engine's own pipeline runs before it needs real engine state;
a fault is reported with the faulting RVA and the exe backtrace.

3d05350(rcx = call struct {ctx*, flag byte*, type ctx*, ?, ?}, rdx = particle, r8d = slot index)
"""
import ctypes
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import re4native as rn  # noqa: E402
import refsim  # noqa: E402
from exp_scaleanim import BLOCK, PSLOT, SLOT, SLOT_STRIDE, same, spec_slot  # noqa: E402
from exp_blink import spec_eval  # noqa: E402

F = np.float32
PIPE = 0x3D05350


def main():
    img = rn.load()
    rn.install_settings(img, eb0=0, eb2=0)
    em = rn.Emitter(img, seed=0x13572468, state=refsim.xs(0x13572468), dt=1 / 60)
    ctx = em.ctx
    rn.set_u64(ctx + 0x120, em.E)                          # = owner+8
    rn.set_u64(ctx + 0x100, img.alloc(0x400))              # = update+0x20 (3d084fe); 3dfa810 reads byte +1
    rn.set_u64(em.E + 0x198, img.alloc(0x4000))            # per-slot side data base (E+0x148 stride stays 0)
    # ScaleAnim block
    sa = img.alloc(0x50 + SLOT_STRIDE * 4)
    ranges = dict(ScalarAdd=(0.05, 0.01), ScalarCoef=(0.9, 0.0), XAdd=(0.01, 0.0), XCoef=(1.0, 0.0),
                  YAdd=(0.02, 0.005), YCoef=(0.98, 0.0))
    for k, off in BLOCK.items():
        s, r = ranges.get(k, (0.0, 0.0))
        rn.set_f32(sa + off, s)
        rn.set_f32(sa + off + 4, r)
    rn.set_u64(ctx + 0x1F0, sa)
    # Blink block
    bl = img.alloc(0x38 + 0x14 * 4)
    bprm = [0.2, 1.0, 1.0, 0.0, 1.0, 0.0, 3.0, 0.0, 0.5, 0.0]
    for i, v in enumerate(bprm):
        rn.set_f32(bl + 0x10 + 4 * i, v)
    rn.set_u64(ctx + 0x230, bl)
    flag = img.alloc(16)
    typectx = img.alloc(0x1000)                         # [typectx] = ctx; ctx[0] = Type id (0: no Type)
    rn.set_u64(typectx, ctx)
    x18 = img.alloc(0x1000)
    x20 = img.alloc(0x100)
    rn.set_u64(x20 + 8, img.alloc(0x1000))
    cs = img.alloc(0x40)
    for off, v in ((0, ctx), (8, flag), (0x10, typectx), (0x18, x18), (0x20, x20)):
        rn.set_u64(cs + off, v)
    pipe = img.function(PIPE, None, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32)
    p = em.particle(4)
    slot = 1
    out = dict(ran=0)
    try:
        rn.set_u32(p, 3)
        rn.set_f32(p + 0x58, 1.0)
        img.call(pipe, cs, p, slot)
        out["ran"] += 1
        # post functor's base capture with the Type's sizes = 1.0 (no type ran)
        for o in PSLOT:
            rn.set_f32(p + o, 1.0)
        img.call(img.function(0x3D1DD00, None, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int32),
                 sa, p, em.owner, slot)
        rn.set_u32(p, rn.u32(p) & ~2)
        for i, v in enumerate((0.01, 0.02, -0.005)):        # a velocity, as Velocity3D would have set it
            rn.set_f32(p + 0x5C + 4 * i, v)
        out["velocity_set"] = [0.01, 0.02, -0.005]
        sl = sa + slot * SLOT_STRIDE
        sv = {k: rn.f32(sl + o) for k, o in SLOT.items()}
        order = ["ScalarAdd", "ScalarCoef", "XAdd", "XCoef", "YAdd", "YCoef", "ZAdd", "ZCoef"]
        ref = refsim.scaleanim_draws(em.state, em.seed, 4, [ranges.get(k, (0.0, 0.0)) for k in order])
        keys = ["AddU", "CoefU", "AddX", "CoefX", "AddY", "CoefY", "AddZ", "CoefZ"]
        out["scaleanim_draws_match"] = all(same(sv[k], v) for k, v in zip(keys, ref))
        fL, AL, fH, AH, ph = refsim.blink_draws(em.state, em.seed, 4, (1.0, 0.0), (1.0, 0.0), (3.0, 0.0), (0.5, 0.0))
        ph, rate = spec_eval(ph, fL, AL, fH, AH, em.dt, 0.2, 1.0)
        out["blink_spawn_match"] = same(rn.f32(p + 0x50), rate)
        ok = 0
        for k in range(60):
            rn.set_f32(p + 0x58, F(F(rn.f32(p + 0x58)) + F(F(em.dt) * F(60.0))))
            img.call(pipe, cs, p, slot)
            out["ran"] += 1
            t = F(F(rn.f32(p + 0x58)) - F(sv["age0"]))
            exp = [spec_slot(t, sv["base" + a], sv["Add" + a], sv["Coef" + a]) for a in "UXYZ"]
            ph, rate = spec_eval(ph, fL, AL, fH, AH, em.dt, 0.2, 1.0)
            ok += all(same(rn.f32(p + o), e) for o, e in zip(PSLOT, exp)) and same(rn.f32(p + 0x50), rate)
        out["frames_match"] = f"{ok}/60"
        out["position_after_60"] = [rn.f32(p + 0x28 + 4 * i) for i in range(3)]
        out["velocity_after_60"] = [rn.f32(p + 0x5C + 4 * i) for i in range(3)]
        out["status"] = "PASS" if ok == 60 and out["scaleanim_draws_match"] and out["blink_spawn_match"] else "FAIL"
    except (rn.NativeFault, rn.UnresolvedImportCalled) as e:
        out["stopped"] = str(e)
        out["status"] = "STUCK"
    print(json.dumps(out, indent=1, default=float))
    print("RESULT " + json.dumps({"exp": "pipeline", **{k: out[k] for k in ("status", "ran")},
                                  **({"stopped": out["stopped"]} if "stopped" in out else {"frames": out.get("frames_match")})}))


if __name__ == "__main__":
    main()
