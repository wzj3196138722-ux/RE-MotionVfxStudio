"""Reference RNG of RE4 EFX samplers, as written from the static reading (spec §1.6, exec-order-rng §7.1).

The native experiments compare the engine's own draws with this file. float32 arithmetic in the
engine's operation order: u = f32(x) * 2^-32, rnd(c, r) = ((u*2) - 1) * r + c.
"""
import numpy as np

M = 0xFFFFFFFF
F = np.float32


def xs(x):
    x ^= (x << 13) & M
    x ^= x >> 17
    x ^= (x << 5) & M
    return x


def seed(salt, e_state, e_seed, key):
    """3d09990 / 3d099e0: xs(xs(salt + E.state + key) + E.seed)."""
    return xs((xs((salt + e_state + key) & M) + e_seed) & M)


class Rng:
    def __init__(self, salt, e_state, e_seed, key):
        self.x = seed(salt, e_state, e_seed, key)

    def next(self):                       # the first draw is already xs(seeded state)
        self.x = xs(self.x)
        return self.x

    def u(self):
        return F(F(self.next()) * F(2.0 ** -32))

    def rnd(self, c, r):                  # CenterSpread, unguarded
        t = F(F(self.u() * F(2.0)) - F(1.0))
        return F(F(t * F(r)) + F(c))

    def rnd_guard(self, c, r):            # ScaleAnim / Billboard3D size: no draw when |r| <= 1e-6
        return self.rnd(c, r) if (F(r) > F(1e-6) or float(F(r)) < -1e-6) else F(c)


SCALEANIM_SALT = 0x27
BLINK_SALT = 0x087F7C20


def scaleanim_draws(e_state, e_seed, pid, ranges):
    """ranges in draw order: ScalarAdd, ScalarCoef, XAdd, XCoef, YAdd, YCoef, ZAdd, ZCoef -> 8 values."""
    g = Rng(SCALEANIM_SALT, e_state, e_seed, pid)
    return [g.rnd_guard(s, r) for s, r in ranges]


def blink_draws(e_state, e_seed, pid, low_f, low_w, high_f, high_w):
    """-> (fL, AL = 2*W_L, fH, AH = 2*W_H, phase u)."""
    g = Rng(BLINK_SALT, e_state, e_seed, pid)
    fL = g.rnd(*low_f)
    AL = F(g.rnd(*low_w) * F(2.0))
    fH = g.rnd(*high_f)
    AH = F(g.rnd(*high_w) * F(2.0))
    ph = g.u()
    return fL, AL, fH, AH, ph
