"""Noise 3dc7050 in re4.exe against noise_ref.py, on the one RE4 Billboard3D corpus entry with Noise.

    python -I -X utf8 -u exp_noise.py

csa114 dust: Spawn 25 at once after EmitterDelayFrame 180; Noise LowFrequency 0.01±0.003, LowFrequencyWidth
0.02±0.005, HighFrequency 0.02±0.003, HighFrequencyWidth 0.05±0.005. Every tick of every particle: the offset at
p+0x34 and the 9-float per-particle record are compared with noise_ref (tolerance 1e-6).
"""
import json
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np  # noqa: E402

STM = r"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\RE4_EXTRACT\natives\stm"
PATH = STM + r"\_chainsaw\vfx\effecteditor\efd_event\cs\efd_csa114_s00\efd_0015_csa114_s00_dust_0000.efx.3539837"
OFF = 2555
BLOCK = (0.01, 0.003, 0.02, 0.005, 0.02, 0.003, 0.05, 0.005)   # the file's values (ReeLib)
NOISE = 109
TICKS = 600


def main():
    import emitter as em
    import noise_ref
    import re4native as rn
    F = np.float32
    img = rn.load()
    em.install_runtime(img)
    e = em.Entry(img, PATH, OFF)
    m = em.Emitter(img, e)
    m.play()
    blk = m.block_of(NOISE)
    state, n, wo, wr = {}, 0, 0.0, 0.0
    for _ in range(TICKS):
        m.tick(1 / 60)
        es = m.emitter_state()
        for q in m.state():
            p = rn.u64(m.E + 0x190) + q["slot"] * 0x140
            off = [rn.f32(p + 0x34 + 4 * k) for k in range(3)]
            rec = struct.unpack("<9f", img.read(m.side(q["slot"]) + rn.u32(blk + 8), 36))
            if q["id"] not in state:
                state[q["id"]] = noise_ref.birth(es["rng_state"], es["seed"], q["slot"])
            ref = noise_ref.step(state[q["id"]], BLOCK, F(F(1 / 60) * noise_ref.TWO_PI))
            wo = max(wo, max(abs(float(ref[k]) - off[k]) for k in range(3)))
            wr = max(wr, max(abs(float(state[q["id"]][k]) - rec[k]) for k in range(9)))
            n += 1
    out = dict(exp="noise", particles=len(state), ticks=n, worst_offset=wo, worst_record=wr,
               status="PASS" if n and max(wo, wr) < 1e-6 else "FAIL")
    print("RESULT " + json.dumps(out))


if __name__ == "__main__":
    main()
