"""TypePolygon orientation: dfae10(quat out, euler (x, y, z, 0), RotationOrder) against the composition
rule the preview uses (Polygon instance writer 3d6f5e0 calls it with particle +0x9c..+0xa4 and [p+4] & 7).

Rule: output is (x, y, z, w); q = product of the axis rotations in the reverse of the order's name,
e.g. RotationOrder_XYZ (0) -> q = qZ * qY * qX (X applied first).
"""
import ctypes, json, math, random, sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
import re4native as rn

ORDERS = ['XYZ', 'YZX', 'ZXY', 'ZYX', 'YXZ', 'XZY']   # RotationOrder enum 0..5


def axis(ax, a):
    s, c = math.sin(a / 2), math.cos(a / 2)
    v = [0.0, 0.0, 0.0]; v[ax] = s
    return (v[0], v[1], v[2], c)


def mul(p, q):
    px, py, pz, pw = p; qx, qy, qz, qw = q
    return (pw*qx + px*qw + py*qz - pz*qy, pw*qy - px*qz + py*qw + pz*qx,
            pw*qz + px*qy - py*qx + pz*qw, pw*qw - px*qx - py*qy - pz*qz)


def rule(x, y, z, order):
    a = (x, y, z); q = (0.0, 0.0, 0.0, 1.0)
    for ch in reversed(ORDERS[order]):
        q = mul(q, axis('XYZ'.index(ch), a['XYZ'.index(ch)]))
    return q


def main():
    img = rn.load(rn.DEFAULT_EXE)
    fn = img.function(0xDFAE10, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32)
    out, eul = img.alloc(16), img.alloc(16)
    random.seed(7)
    worst = 0.0
    for order in range(6):
        for _ in range(200):
            x, y, z = (random.uniform(-6.3, 6.3) for _ in range(3))
            for i, v in enumerate((x, y, z, 0.0)): rn.set_f32(eul + 4 * i, v)
            img.call(fn, out, eul, order)
            n = [rn.f32(out + 4 * i) for i in range(4)]
            r = rule(x, y, z, order)
            worst = max(worst, min(max(abs(n[i] - r[i]) for i in range(4)), max(abs(n[i] + r[i]) for i in range(4))))
    print('RESULT ' + json.dumps(dict(exp='polygon_quat', status='PASS' if worst < 1e-5 else 'FAIL', max_error=worst)))


if __name__ == '__main__':
    main()
