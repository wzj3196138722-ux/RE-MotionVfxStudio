"""EmitterShape3D RotationCorrect on re4.exe with a fake camera.

    python -I -X utf8 -u exp_rotation_correct.py

3d21210 builds the shape rotation (shape block +0x40, three rows of three floats, stride 0x10). For modes 1 and 2 it
asks the scene for the camera (3a3f920) and its matrix (3a17660); modes 3 and 4 use 3a17ad0, the camera position
(row 3) minus the emitter position, and are evaluated only on the per-frame pass (slot 23, 3d24710: r8b = 1), which
this harness does not run, so they are reported, not checked. Here the camera lookup returns a fake object and both
matrix getters write a chosen matrix C (row-vector rows: right, up, forward, position).

Checked on a temporary copy of csa013 saliva (LocalRotation 0, RotationCorrect patched), for two cameras:
  mode 1 ParallelCamera       R = transpose of C's 3x3
  mode 2 ParallelCameraAxisY  z = normalize(C[0][2], 0, C[2][2]) (R mode 1's third row with y dropped);
                              R = ((z.z, 0, -z.x), (0, 1, 0), z)
"""
import ctypes
import json
import math
import os
import struct
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
STM = r"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\RE4_EXTRACT\natives\stm"
SRC = STM + r"\_chainsaw\vfx\effecteditor\efd_event\cs\efd_csa013_s00\efd_0015_csa013_s00_saliva_0000.efx.3539837"
OFF = 137
SHAPE = 102


def patched(mode):
    """The source with RotationCorrect = mode in the entry's EmitterShape3D (payload found by its known values)."""
    data = bytearray(open(SRC, "rb").read())
    head = struct.pack("<6f", 0, 0.02, 0, 0, 0, 0) + struct.pack("<5I", 0, 0, 0, 0, 0) + struct.pack("<3f", 0, 0, 0)
    i = data.find(head, OFF)
    if not OFF < i < OFF + 0x800 or struct.unpack_from("<2I", data, i + 56) != (2, 0):
        raise RuntimeError("EmitterShape3D payload not where expected")
    struct.pack_into("<I", data, i + 60, mode)
    out = os.path.join(tempfile.gettempdir(), "re4native-rc", f"saliva_rc{mode}.efx.3539837")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    open(out, "wb").write(data)
    return out


def camera(yaw, pitch, roll, pos):
    """Row-vector rotation roll (Z), then pitch (X), then yaw (Y); rows right, up, forward, then the position."""
    def rx(a): return [[1, 0, 0], [0, math.cos(a), math.sin(a)], [0, -math.sin(a), math.cos(a)]]
    def ry(a): return [[math.cos(a), 0, -math.sin(a)], [0, 1, 0], [math.sin(a), 0, math.cos(a)]]
    def rz(a): return [[math.cos(a), math.sin(a), 0], [-math.sin(a), math.cos(a), 0], [0, 0, 1]]
    def mul(a, b): return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]
    R = mul(mul(rz(roll), rx(pitch)), ry(yaw))
    return [R[0] + [0], R[1] + [0], R[2] + [0], list(pos) + [1]]


def run(mode, C):
    import emitter as em
    import re4native as rn
    img = rn.load()
    em.install_runtime(img)
    fake = img.alloc(0x400)
    buf = struct.pack("<16f", *[v for row in C for v in row])

    def write(rcx, rdx, r8, r9):
        ctypes.memmove(rdx, buf, 64)
        return rdx
    em.stub(img, 0x3A3F920, "camera lookup", lambda *a: fake)
    em.stub(img, 0x3A17660, "camera matrix", write)
    em.stub(img, 0x3A17AD0, "camera matrix (position)", write)
    m = em.Emitter(img, em.Entry(img, patched(mode), OFF))
    m.play()
    m.tick(1 / 60)
    blk = m.block_of(SHAPE)
    R = [list(struct.unpack("<3f", img.read(blk + 0x40 + 0x10 * i, 12))) for i in range(3)]
    return dict(R=R, calls=sorted({c[0] for c in em.CALLS if c[0].startswith("camera")}))


def expected(mode, C):
    if mode == 0:
        return [[1, 0, 0], [0, 1, 0], [0, 0, 1]]
    T = [[C[j][i] for j in range(3)] for i in range(3)]
    if mode == 1:
        return T
    z = [T[2][0], 0.0, T[2][2]]
    n = math.hypot(z[0], z[2])
    z = [z[0] / n, 0.0, z[2] / n]
    return [[z[2], 0.0, -z[0]], [0.0, 1.0, 0.0], z]


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "run":
        print("RESULT " + json.dumps(run(int(sys.argv[2]), json.loads(sys.argv[3]))))
        return
    cams = [camera(math.radians(30), math.radians(20), 0, (3, 2, 5)), camera(math.radians(-50), math.radians(-35), math.radians(10), (-1, 4, 2))]
    ok = True
    for ci, C in enumerate(cams):
        for mode in range(5):
            p = subprocess.run([sys.executable, "-I", "-X", "utf8", os.path.abspath(__file__), "run", str(mode), json.dumps(C)],
                               capture_output=True, text=True, encoding="utf-8", errors="replace")
            line = next((l for l in p.stdout.splitlines() if l.startswith("RESULT ")), None)
            r = json.loads(line[7:])
            if mode in (0, 1, 2):
                err = max(abs(a - b) for ra, rb in zip(r["R"], expected(mode, C)) for a, b in zip(ra, rb))
                ok &= err < 1e-5
                print(json.dumps(dict(camera=ci, mode=mode, worst=err, calls=r["calls"])))
            else:
                print(json.dumps(dict(camera=ci, mode=mode, not_checked="per-frame pass not run", calls=r["calls"],
                                      R=[[round(v, 4) for v in row] for row in r["R"]])))
    print("RESULT " + json.dumps(dict(exp="rotation_correct", checked_modes=[0, 1, 2], status="PASS" if ok else "FAIL")))


if __name__ == "__main__":
    main()
