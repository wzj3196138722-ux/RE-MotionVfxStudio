"""Step 3: the engine's own loader and block builder on real .efx bytes.

For one ScaleAnim (type 87) and one Blink (type 108) attribute taken from corpus files:
  ctor (3d1d040 / 3dc7480) -> loader, vtable slot 5 (3d1d520 / 3dc77e0), reading through a fake
  stream object whose vtable slot 2 (+0x10) is a Python read(this, buf, size) callback
  -> builder, slot 15 (3d1d640 / 3dc7890), which tail-calls slot 6 (block size)
  -> the runtime block is compared with the file bytes in the order the spec gives
  -> the built block drives the per-particle function for a few particles; spawn draws are compared
     with refsim.py and the per-frame values with the spec formulas (exp_scaleanim / exp_blink).

The corpus files are only read. Offsets point at the attribute's type id (u32), then UniqueID, then
the payload; the bytes are checked before use.
"""
import ctypes
import json
import os
import struct
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import re4native as rn  # noqa: E402
import refsim  # noqa: E402
from exp_blink import spec_eval  # noqa: E402
from exp_scaleanim import BLOCK, PSLOT, SLOT, SLOT_STRIDE, Sim, same, spec_slot  # noqa: E402

F = np.float32
STM = r"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\RE4_EXTRACT\natives\stm"
SCALEANIM_SRC = (STM + r"\_anotherorder\vfx\effecteditor\efd_character\efd_ao_chd7\efd_0015_ao_chd7_damage_0000.efx.3539837",
                 0x335, 87, 0x44)   # emitter 0 "煙　糸系0"
BLINK_SRC = (STM + r"\_chainsaw\ui\ui_effect\cs_ui_treasure.efx.3539837", 0x768, 108, 0x2C)   # emitter 1

READ_FN = ctypes.CFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t)


class FakeStream:
    """An object the loaders can call `[[obj]+0x10](obj, buf, size)` on. Logs every read."""

    def __init__(self, img, data: bytes):
        self.data, self.pos, self.log = data, 0, []
        self.cb = img.keep(READ_FN(self._read))
        self.vtable = img.alloc(8 * 8)
        for i in range(8):        # every slot traps into the same callback; only +0x10 is expected
            rn.set_u64(self.vtable + 8 * i, ctypes.cast(self.cb, ctypes.c_void_p).value)
        self.obj = img.alloc(0x40)
        rn.set_u64(self.obj, self.vtable)
        self.handle = img.alloc(16)          # the loader gets rdx = &stream, reads rcx = [rdx]
        rn.set_u64(self.handle, self.obj)

    def _read(self, this, buf, size):
        chunk = self.data[self.pos:self.pos + size]
        ctypes.memmove(buf, chunk, len(chunk))
        self.pos += len(chunk)
        self.log.append(size)
        return len(chunk)


def read_attr(src):
    path, off, type_id, n = src
    with open(path, "rb") as f:
        f.seek(off)
        raw = f.read(4 + n)
    tid = struct.unpack_from("<I", raw)[0]
    if tid != type_id:
        raise RuntimeError(f"{path}@{off:#x}: type id {tid}, expected {type_id}")
    return raw[4:]          # UniqueID + payload


def load_and_build(img, ctor, loader, builder, attr_size, payload, max_particles):
    attr = img.alloc(attr_size)
    img.call(img.function(ctor, ctypes.c_void_p, ctypes.c_void_p), attr)
    vt = rn.u64(attr) - rn.IMAGE_BASE
    stream = FakeStream(img, payload)
    owner = img.alloc(0x100)                       # loader reads [owner+0x88]->+0x28 = max particles
    cnt = img.alloc(0x40)
    rn.set_u32(cnt + 0x28, max_particles)
    rn.set_u64(owner + 0x88, cnt)
    ok = img.call(img.function(loader, ctypes.c_uint8, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p),
                  attr, stream.handle, owner)
    block = img.alloc(0x4000)
    alloc_ctx = img.alloc(0x200)                   # builder: [r8+0x148] running offset (slot 7 returned 0)
    size = img.call(img.function(builder, ctypes.c_uint64, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p),
                    attr, block, alloc_ctx)
    return dict(attr=attr, vtable=hex(vt), loader_ok=ok, reads=stream.log, consumed=stream.pos,
                block=block, block_size=size, block_head=(hex(rn.u64(block)), rn.u32(block + 8)))


def scaleanim(img):
    payload = read_attr(SCALEANIM_SRC)
    fl = struct.unpack_from("<I16f", payload)
    uid, vals = fl[0], fl[1:]
    file_ranges = dict(ScalarAdd=vals[0:2], ScalarCoef=vals[2:4], XAdd=vals[4:6], XCoef=vals[6:8],
                       YAdd=vals[8:10], YCoef=vals[10:12], ZAdd=vals[12:14], ZCoef=vals[14:16])
    r = load_and_build(img, 0x3D1D040, 0x3D1D520, 0x3D1D640, 0x70, payload, max_particles=40)
    blk, attr = r["block"], r["attr"]
    # spec: block +0x10.. = ScalarAdd, ScalarCoef, XAdd, YAdd, ZAdd, XCoef, YCoef, ZCoef (each {s, r})
    block_ok = all(same(rn.f32(blk + off), file_ranges[k][0]) and same(rn.f32(blk + off + 4), file_ranges[k][1])
                   for k, off in BLOCK.items())
    out = dict(source=os.path.basename(SCALEANIM_SRC[0]), uid=uid, attr_uid=rn.u32(attr + 0x20),
               attr_maxparticles=rn.u32(attr + 0x68), block_matches_spec_order=block_ok,
               expected_size=(40 - 1) * SLOT_STRIDE + 0x88,
               **{k: v for k, v in r.items() if k not in ("attr", "block")})
    # drive 3d1d830 with the engine-built block: 5 particles, draws vs refsim, 30 frames vs spec
    rn.install_settings(img, eb2=0)
    order = ["ScalarAdd", "ScalarCoef", "XAdd", "XCoef", "YAdd", "YCoef", "ZAdd", "ZCoef"]
    keys = ["AddU", "CoefU", "AddX", "CoefX", "AddY", "CoefY", "AddZ", "CoefZ"]
    draws_ok, frames_ok, frames = 0, 0, 0
    for pid in range(5):
        sim = Sim(img, {}, base=(0.3, 1.0, 1.0, 1.0), slot=pid, seed=0x5EED0000 + pid, state=refsim.xs(0x5EED0000 + pid), pid=pid)
        sim.block = blk
        sim.sl = blk + pid * SLOT_STRIDE
        sim.spawn()
        sv = sim.slotvals()
        ref = refsim.scaleanim_draws(sim.em.state, sim.em.seed, pid, [file_ranges[k] for k in order])
        draws_ok += all(same(sv[k], v) for k, v in zip(keys, ref))
        for _ in range(30):
            got = sim.tick()
            t = F(F(rn.f32(sim.p + 0x58)) - F(sv["age0"]))
            exp = [spec_slot(t, sv["base" + a], sv["Add" + a], sv["Coef" + a]) for a in "UXYZ"]
            frames_ok += all(same(g, e) for g, e in zip(got, exp))
            frames += 1
    out.update(particles=5, spawn_draws_match_refsim=f"{draws_ok}/5", frames_bit_exact=f"{frames_ok}/{frames}")
    out["status"] = "PASS" if (r["loader_ok"] and block_ok and draws_ok == 5 and frames_ok == frames
                               and r["block_size"] == out["expected_size"]) else "FAIL"
    return out


def blink(img):
    payload = read_attr(BLINK_SRC)
    fl = struct.unpack_from("<I10f", payload)
    uid, v = fl[0], fl[1:]
    r = load_and_build(img, 0x3DC7480, 0x3DC77E0, 0x3DC7890, 0x60, payload, max_particles=12)
    blk = r["block"]
    # spec: block +0x10.. = MinRate, MaxRate, LowFrequency, LowFrequencyWidth, HighFrequency, HighFrequencyWidth (file order)
    block_ok = all(same(rn.f32(blk + 0x10 + 4 * i), v[i]) for i in range(10))
    out = dict(source=os.path.basename(BLINK_SRC[0]), uid=uid, attr_uid=rn.u32(r["attr"] + 0x20),
               attr_maxparticles=rn.u32(r["attr"] + 0x50), block_matches_spec_order=block_ok,
               expected_size=(12 + 3) * 20, **{k: x for k, x in r.items() if k not in ("attr", "block")})
    fn = img.function(0x3DC7A20, None, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int32)
    draws_ok = frames_ok = frames = 0
    for pid in range(5):
        em = rn.Emitter(img, seed=0xB11E0000 + pid, state=1 + pid, dt=1 / 60)
        p = em.particle(100 + pid)
        sl = blk + 0x38 + 0x14 * pid
        rn.set_u32(p, 3)
        img.call(fn, blk, p, em.owner, pid)
        fL, AL, fH, AH, ph0 = refsim.blink_draws(em.state, em.seed, 100 + pid, v[2:4], v[4:6], v[6:8], v[8:10])
        # the spawn tick already evaluated once, so the stored phase is fract(ph0 + fL*dt)
        ph, rate = spec_eval(ph0, fL, AL, fH, AH, em.dt, v[0], v[1])
        got = [rn.f32(sl + 4 * i) for i in range(5)]
        draws_ok += all(same(a, b) for a, b in zip(got, [ph, fL, AL, fH, AH])) and same(rn.f32(p + 0x50), rate)
        rn.set_u32(p, 1)
        for _ in range(60):
            img.call(fn, blk, p, em.owner, pid)
            ph, rate = spec_eval(ph, fL, AL, fH, AH, em.dt, v[0], v[1])
            frames_ok += same(rn.f32(p + 0x50), rate) and same(rn.f32(sl), ph)
            frames += 1
    out.update(particles=5, spawn_draws_match_refsim=f"{draws_ok}/5", frames_bit_exact=f"{frames_ok}/{frames}")
    out["status"] = "PASS" if (r["loader_ok"] and block_ok and draws_ok == 5 and frames_ok == frames
                               and r["block_size"] == out["expected_size"]) else "FAIL"
    return out


def main():
    img = rn.load()
    out = dict(scaleanim=scaleanim(img), blink=blink(img))
    print(json.dumps(out, indent=1, default=float, ensure_ascii=False))
    st = "PASS" if all(x["status"] == "PASS" for x in out.values()) else "FAIL"
    print("RESULT " + json.dumps({"exp": "loader", "status": st,
                                  "scaleanim": out["scaleanim"]["status"], "blink": out["blink"]["status"]}))


if __name__ == "__main__":
    main()
