"""Reference for EmitterShape3D's equidistant cylinder (DivideEquidistant), read from re4.exe; float32 in the engine's order.

  build   3d23470: N points on the ellipse (rx, rz) at equal angle steps over H (half of it with UseExtension off),
          each as (x, 0, z, 1)·M with the perspective divide, the radii first divided by max(min(rx, rz), 1);
          cumulative chord length L; then for each of N equal arc-length targets a 10-step bisection on the
          linear interpolation of L, starting from the previous result; table[j] = result / (N - 1) · (0.5 or 1)
  lookup  3d20e20: linear interpolation in the table at position s (the last entry when floor(s) = N - 1)
  sample  3d22b00 / 3d22030: radius lerp(inner, outer, t) per axis, height lerp(Y, b); the angle fraction is
          looked up at a·(N - 1) (UseExtension) or per half turn at 2a·(N - 1) (+ 0.5 for a > 0.5)
N is the attribute's +0x84 (64 on re4.exe's csa071 entry; Wilds matches with 64). With DivideEquidistantCalcOuterCurveData off the one
table is built from the mean radii (inner + outer) / 2; M is the shape block's matrix +0x80 (shape rotation, then
the emitter's translation) at the time the table is built."""
import ctypes
import math
import numpy as np

F = np.float32
_crt = ctypes.CDLL("ucrtbase")
for _n in ("sinf", "cosf", "floorf", "sqrtf"):
    getattr(_crt, _n).restype = ctypes.c_float
    getattr(_crt, _n).argtypes = [ctypes.c_float]
cos = lambda x: F(_crt.cosf(float(x)))      # noqa: E731
sin = lambda x: F(_crt.sinf(float(x)))      # noqa: E731
floor = lambda x: F(_crt.floorf(float(x)))  # noqa: E731


def lookup(table, n, s):
    """3d20e20: linear interpolation in a table of n floats at position s."""
    i = int(floor(s))
    fr = F(F(s) - F(i))
    if i == n - 1:
        return F(table[i])
    return F(F(F(F(1) - fr) * F(table[i])) + F(fr * F(table[i + 1])))


def build(M, rx, rz, n, h_start, h_span, half):
    """3d23470: per-sample angle fraction at equal arc length. M: 16 floats, row-vector (x, y, z, 1)·M."""
    m = [F(v) for v in M]
    m00, m01, m02, m03, m10, m11, m12, m13, m20, m21, m22, m23, m30, m31, m32, m33 = m
    mn = F(max(F(min(F(rx), F(rz))), F(1)))
    rxn, rzn = F(F(rx) / mn), F(F(rz) / mn)
    zero = F(0)

    def point(theta):
        c, s = cos(theta), sin(theta)
        xc, zc = F(c * rxn), F(s * rzn)
        w = F(F(F(F(m03 * xc) + F(m13 * zero)) + F(m23 * zc)) + m33)
        iw = F(0) if w == 0 else F(F(1) / w)
        X = F(F(F(F(F(m00 * xc) + F(m10 * zero)) + F(m20 * zc)) + m30) * iw)
        Y = F(F(F(F(F(m01 * xc) + F(m11 * zero)) + F(m21 * zc)) + m31) * iw)
        Z = F(F(F(F(F(m02 * xc) + F(m12 * zero)) + F(m22 * zc)) + m32) * iw)
        return X, Y, Z

    frac = F(0.5) if half else F(1)
    L = [F(0)] * n
    prev = point(F(F(F(h_span) * zero) + F(h_start)))
    nm1 = F(n - 1)
    for i in range(1, n):
        th = F(F(F(F(F(i) / nm1) * frac) * F(h_span)) + F(h_start))
        p = point(th)
        d2 = F(F(F(F(p[0] - prev[0]) * F(p[0] - prev[0])) + F(F(p[1] - prev[1]) * F(p[1] - prev[1]))) + F(F(p[2] - prev[2]) * F(p[2] - prev[2])))
        L[i] = F(F(math.sqrt(float(d2))) + L[i - 1]) if d2 >= 0 else F(float("nan"))
        prev = p
    step = F(L[n - 1] / nm1)
    out, lo_start = [], F(0)
    for j in range(n):
        target = F(F(j) * step)
        lo, hi = lo_start, nm1
        for _ in range(10):
            mid = F(F(hi + lo) * F(0.5))
            f_lo = F(target - lookup(L, n, lo))
            f_mid = F(target - lookup(L, n, mid))
            if F(f_mid * f_lo) > 0:
                lo = mid
            else:
                hi = mid
        res = F(F(hi + lo) * F(0.5))
        out.append(F(F(res / nm1) * frac))
        lo_start = res
    return out


def sample(table, n, a, b, t, X, Y, Z, H, V, ext):
    """3d22b00 single-table path + 3d22030: shape-local position. a, b already resolved (counter or drawn).
    Draw order in 3d22b00: a (when not divided), b (when not divided), then t."""
    rX = F(F(F(F(X[1]) - F(X[0])) * F(t)) + F(X[0]))
    rZ = F(F(F(F(Z[1]) - F(Z[0])) * F(t)) + F(Z[0]))
    y = F(F(F(F(Y[1]) - F(Y[0])) * F(b)) + F(Y[0]))
    a = F(a)
    if ext:
        v = lookup(table, n, F(F(F(n - 1)) * a))
    elif a > F(0.5):
        s = F(F(F(float(a) - 0.5) * F(2)) * F(n - 1))
        v = F(float(lookup(table, n, s)) + 0.5)
    else:
        v = lookup(table, n, F(F(a * F(2)) * F(n - 1)))
    th = F(F(v * F(H[1])) + F(H[0]))
    x, z = F(cos(th) * rX), F(sin(th) * rZ)
    if ext:
        k = F(F(F(V[1]) * F(b)) + F(V[0]))
        x, z = F(x * k), F(z * k)
    return x, y, z
