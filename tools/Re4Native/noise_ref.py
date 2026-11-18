"""Reference Noise, read from re4.exe 3dc7050 (per particle) and 3dc6d50 (offset); float32 in the engine's order.

Block (+0x10..+0x2c): LowFrequency, LowFrequencyWidth, HighFrequency, HighFrequencyWidth, each (s, r). Per-particle
record (9 floats): k0, k1, k2, k3, high phase b, low phase a, unused, dir x, dir y.

  birth (salt 0xd17f868f, key = slot): b = 2pi u1, a = 2pi u2, k2 = 2u3 - 1, k0 = 2u4 - 1, k3 = 2u5 - 1, k1 = 2u6 - 1,
        dir = normalize(2u7 - 1, 2u8 - 1), or (1, 0) when its length is below 1e-6
  every tick, the birth tick included:
        LW = LowFrequencyWidth.s + .r * k3;  HW = HighFrequencyWidth.s + .r * k1
        C = cos(a) * LW + cos(b) * HW;  S = sin(a) * LW + sin(b) * HW
        offset = (C * dir.x, C * dir.y, S) for 3D emitters, (C, S, 0) for 2D; it replaces the previous tick's offset
        a += (LowFrequency.s + .r * k2) * dt * 2pi;  b += (HighFrequency.s + .r * k0) * dt * 2pi;  each fmodf 2pi
"""
import ctypes
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np  # noqa: E402
import refsim  # noqa: E402

F = np.float32
_crt = ctypes.CDLL("ucrtbase")
for _n in ("sinf", "cosf", "fmodf"):
    getattr(_crt, _n).restype = ctypes.c_float
_crt.sinf.argtypes = _crt.cosf.argtypes = [ctypes.c_float]
_crt.fmodf.argtypes = [ctypes.c_float, ctypes.c_float]
TWO_PI = F(6.2831855)
SALT = 0xD17F868F


def birth(state, seed, slot):
    g = refsim.Rng(SALT, state, seed, slot)
    u = [F(F(g.next()) * F(2.0 ** -32)) for _ in range(8)]
    signed = lambda x: F(F(x * F(2)) - F(1))   # noqa: E731
    rec = [F(0)] * 9
    rec[4], rec[5] = F(u[0] * TWO_PI), F(u[1] * TWO_PI)
    rec[2], rec[0], rec[3], rec[1] = signed(u[2]), signed(u[3]), signed(u[4]), signed(u[5])
    dx, dy = signed(u[6]), signed(u[7])
    ln = F(math.sqrt(float(F(F(dx * dx) + F(dy * dy)))))
    if F(1e-6) > ln:
        dx, dy = F(1), F(0)
    else:
        k = F(F(1) / ln)
        dx, dy = F(dx * k), F(dy * k)
    rec[7], rec[8] = dx, dy
    return rec


def step(rec, blk, K, three_d=True):
    """This tick's offset; advances the phases in rec. blk: the 8 floats LF.s, LF.r, LW.s, LW.r, HF.s, HF.r, HW.s,
    HW.r. K = dt * 2pi."""
    LFs, LFr, LWs, LWr, HFs, HFr, HWs, HWr = (F(v) for v in blk[:8])
    a, b = rec[5], rec[4]
    hw = F(F(HWr * rec[1]) + HWs)
    lw = F(F(LWr * rec[3]) + LWs)
    c = F(F(F(_crt.cosf(float(a))) * lw) + F(F(_crt.cosf(float(b))) * hw))
    s = F(F(F(_crt.sinf(float(a))) * lw) + F(F(_crt.sinf(float(b))) * hw))
    off = (F(c * rec[7]), F(c * rec[8]), s) if three_d else (c, s, F(0))
    for i, w in ((4, F(F(F(HFr * rec[0]) + HFs) * K)), (5, F(F(F(LFr * rec[2]) + LFs) * K))):
        r = F(_crt.fmodf(float(F(rec[i] + w)), float(TWO_PI)))
        rec[i] = r if r >= 0 else F(TWO_PI - r)
    return off
