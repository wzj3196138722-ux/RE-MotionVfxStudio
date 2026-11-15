"""Transform3DExpression on a real entry: the engine's expression VM evaluates, and op(file value, expr)
lands in the slot the C# BitNames give.

    python -I -X utf8 -u exp_expr.py                  # both runs, each in a child process
    python -I -X utf8 -u exp_expr.py run <bind>       # one run here; bind = none | 10 | -25

Entry: chd4 grapple_hold_needle_knife_escape root 10 @0x294c (Mesh, Transform3DExpression with bits 0x18 =
slots 3, 4 = LocalRotationX / LocalRotationY, assign type Add).
The combine function 3e06330(out, expr, cur, assign) is hooked: each call records cur, the assign type,
the result, and the expression value e from the evaluator 3b27a00(expr, &out) run on the same expression.
The expression nodes of type 4 (vtable 79f4110, evaluator 3b27cd0) read an external value through
node+0x40; nothing binds it here, so the run "10" / "-25" points it at a value object {flags 0, +0x10 value}.
Check: the Transform3D block's animated TRS copy (block + 0x58: pos, rot (rad), scale) has, for each set slot,
op(cur, e) (rotation: (cur_deg + e) * (1/180) * pi), and the other slots keep the file value.
"""
import ctypes
import json
import math
import os
import struct
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

STM = r"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\RE4_EXTRACT\natives\stm"
PATH = STM + r"\_chainsaw\vfx\effecteditor\efd_character\efd_chd5\efd_0015_chd4_grapple_hold_needle_knife_escape_0000.efx.3539837"
OFF = 0x294C
NAMES = ["LocalPositionX", "LocalPositionY", "LocalPositionZ", "LocalRotationX", "LocalRotationY",
         "LocalRotationZ", "LocalScaleX", "LocalScaleY", "LocalScaleZ"]      # C# BitNames, slot order


def run(bind):
    import emitter as em
    import re4native as rn
    from emitter import _jmp, _k32
    B = rn.IMAGE_BASE
    img = rn.load()
    em.install_runtime(img)
    log = []
    entry, n = 0x3E06330, 15                    # mov [rsp+18],rbx; push rdi; sub rsp,40; movaps [rsp+30],xmm6
    orig = bytes(ctypes.string_at(B + entry, n))
    tramp = _k32.VirtualAlloc(None, 0x1000, 0x3000, 0x40)
    ctypes.memmove(tramp, orig + _jmp(B + entry + n), n + 12)
    T = ctypes.CFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_float, ctypes.c_uint32)
    game = T(tramp)
    ev = img.function(0x3B27A00, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p)
    value = None
    if bind != "none":
        value = img.alloc(0x20)
        rn.set_f32(value + 0x10, float(bind))

    def hook(out, expr, cur, assign):
        nodes = [rn.u64(rn.u64(expr) + 8 * i) for i in range(rn.u32(expr + 0x14))]
        ext = [nd for nd in nodes if rn.u64(nd) - B == 0x79F4110]
        if value:
            for nd in ext:
                rn.set_u64(nd + 0x40, value)
        o = img.alloc(16)
        img.call(ev, expr, o)
        e_ok, e = rn.u8(o), rn.f32(o + 4)
        r = game(out, expr, cur, assign)
        log.append(dict(cur_deg=cur, assign=assign, ok=rn.u8(out), result_deg=rn.f32(out + 4), e_ok=e_ok, e=e,
                        nodes=[(hex(rn.u64(nd) - B), rn.u32(nd + 8)) for nd in nodes],
                        external_nodes=len(ext), external_bound=[bool(rn.u64(nd + 0x40)) for nd in ext]))
        return r

    h = img.keep(T(hook))
    ctypes.memmove(B + entry, _jmp(ctypes.cast(h, ctypes.c_void_p).value), 12)
    e = em.Entry(img, PATH, OFF)
    x = next(a["attr"] for a in e.attrs if a["type"] == 13)
    t3 = next(a["attr"] for a in e.attrs if a["type"] == 9)
    bits = rn.u32(x + 0x28)
    assign = list(struct.unpack("<9I", img.read(x + 0x2C, 36)))
    file_trs = list(struct.unpack("<3f4x3f4x3f", img.read(t3 + 0x30, 44)))   # attr +0x30 pos, +0x40 rot (rad), +0x50 scale
    m = em.Emitter(img, e)
    m.play()
    frames = []
    for k in range(1, 121):
        n0 = len(log)
        m.tick(1 / 60)
        blk = m.block_of(9)
        anim = list(struct.unpack("<9f", img.read(blk + 0x58, 36)))
        frames.append(dict(tick=k, calls=log[n0:], animated=anim))
    return dict(bind=bind, bits=bits, assign=assign, file_trs=file_trs, frames=frames)


def check(d):
    set_slots = [i for i in range(9) if d["bits"] >> i & 1]
    ok = n = 0
    rows = []
    for fr in d["frames"]:
        calls = fr["calls"]
        for j, s in enumerate(set_slots):
            if j >= len(calls):
                continue
            c = calls[j]
            n += 1
            op = {0: lambda a, b: b + a, 1: lambda a, b: a - b, 2: lambda a, b: a * b, 3: lambda a, b: a / b,
                  4: lambda a, b: b}[d["assign"][s]]
            want_deg = op(c["cur_deg"], c["e"])
            want = want_deg * 0.005555556 * math.pi if 3 <= s <= 5 else want_deg
            got = fr["animated"][s]
            good = abs(got - want) < 1e-5
            ok += good
            if fr["tick"] in (1, 60, 120):
                rows.append(dict(tick=fr["tick"], slot=NAMES[s], assign=d["assign"][s], cur_deg=c["cur_deg"], e=c["e"],
                                 animated=got, expected=want))
        for s in range(9):
            if s not in set_slots:
                n += 1
                ok += abs(fr["animated"][s] - d["file_trs"][s]) < 1e-6
    calls0 = d["frames"][0]["calls"]
    return dict(bind=d["bind"], bits=hex(d["bits"]), slots=[NAMES[s] for s in set_slots],
                assign=[d["assign"][s] for s in set_slots], checks=f"{ok}/{n}", sample=rows,
                nodes=calls0[0]["nodes"] if calls0 else None, external=[(c["external_nodes"], c["external_bound"]) for c in calls0],
                status="PASS" if n and ok == n else "FAIL")


def main():
    if len(sys.argv) >= 3 and sys.argv[1] == "run":
        print(json.dumps(run(sys.argv[2])))
        return
    res = {}
    for bind in ("none", "10", "-25"):
        p = subprocess.run([sys.executable, "-I", "-X", "utf8", "-u", os.path.abspath(__file__), "run", bind],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        if p.returncode != 0 or not p.stdout.strip():
            res[bind] = dict(status="CRASH", exit=hex(p.returncode & 0xFFFFFFFF), stderr=p.stderr[-600:])
            continue
        res[bind] = check(json.loads(p.stdout.strip().splitlines()[-1]))
    print(json.dumps(res, indent=1))
    print("RESULT " + json.dumps({"exp": "expr", "status": "INFO", **{k: v["status"] for k, v in res.items()}}))


if __name__ == "__main__":
    main()
