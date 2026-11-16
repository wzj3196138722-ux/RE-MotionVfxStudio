"""EFX attribute factories of re4.exe, keyed by RE4 attribute type id (static scan, nothing is run).

Every attribute constructor calls the base constructor 3d0e910(attr, edx = type id, r8b) with the type
id as an immediate. Each type also has a factory `alloc(size) -> ctor` (e.g. 3cf7ab0: `mov ecx, 0x70;
call 3a85cc0; ...; jmp 3d1d040`); the factories sit in slot 2 of per-type info objects that the game
registers at startup (not run here), so they are found by their call to the ctor.

    table() -> {type_id: dict(ctor, factory, size, vtable)}      static: ctor with the type id as immediate
    native_table(img) -> {type_id: ...}                           runs every factory candidate (allocator
                                                                  patched) and reads attr+8; covers the types whose
                                                                  factory passes the id to a shared ctor (240 types)
"""
import functools
import os
import struct
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import re4native as rn  # noqa: E402

BASE_CTOR = 0x3D0E910
ALLOC = 0x3A85CC0


@functools.lru_cache(None)
def _image():
    from pe_image import Image
    return Image(os.environ.get("RE4_EXE", rn.DEFAULT_EXE))


@functools.lru_cache(None)
def _pdata():
    img = _image()
    pd = next(s for s in img.secs if s[0] == ".pdata")
    arr = np.frombuffer(img.data, dtype="<u4", count=pd[4] // 4, offset=pd[2]).reshape(-1, 3)
    return arr[:, 0].astype(np.int64), arr[:, 1].astype(np.int64)


def func_of(rva):
    b, e = _pdata()
    i = int(np.searchsorted(b, rva, side="right")) - 1
    return (int(b[i]), int(e[i])) if i >= 0 and b[i] <= rva < e[i] else None


@functools.lru_cache(None)
def _branch_index():
    """(op, site, target) for every E8 / E9 rel32 in .text."""
    img = _image()
    n, va, po, ps, vs, ch = img.code_section()
    a = np.frombuffer(img.data, dtype=np.uint8, count=ps, offset=po)
    L = len(a) - 5
    op = a[:L]
    d = (a[1:L + 1].astype(np.int64) | (a[2:L + 2].astype(np.int64) << 8) |
         (a[3:L + 3].astype(np.int64) << 16) | (a[4:L + 4].astype(np.int64) << 24))
    d = np.where(d >= 2 ** 31, d - 2 ** 32, d)
    site = va + np.arange(L)
    m = (op == 0xE8) | (op == 0xE9)
    return op[m], site[m], (site + 5 + d)[m]


def callers(target):
    op, site, tgt = _branch_index()
    return [int(s) for s in site[tgt == target]]


@functools.lru_cache(None)
def table():
    img = _image()
    out = {}
    for site in callers(BASE_CTOR):
        f = func_of(site)
        if not f:
            continue
        type_id = None
        for x in img.md.disasm(img.data[img.rva2off(f[0]):img.rva2off(site)], f[0]):
            ops = x.op_str
            if ops.startswith("edx, ") and x.mnemonic in ("mov", "lea"):
                s = ops[5:]
                try:
                    type_id = int(s, 16) if x.mnemonic == "mov" else int(s.split("+")[1].strip(" ]"), 16)
                except (ValueError, IndexError):
                    type_id = None
        if type_id is None or type_id in out:
            continue
        ctor = f[0]
        vt = None
        for x in img.md.disasm(img.data[img.rva2off(site):img.rva2off(f[1])], site):
            if x.mnemonic == "lea" and "rip" in x.op_str:
                disp = x.op_str.split("rip")[1].strip(" ]")
                vt = x.address + x.size + int(disp.replace(" ", ""), 16)
                break
        fac = size = None
        for s in callers(ctor):
            g = func_of(s)
            if not g or g[1] - g[0] > 0x60:
                continue
            body = list(img.md.disasm(img.data[img.rva2off(g[0]):img.rva2off(g[1])], g[0]))
            if any(x.mnemonic == "call" and x.op_str == hex(ALLOC) for x in body):
                for x in body:
                    if x.mnemonic == "mov" and x.op_str.startswith("ecx, 0x"):
                        size = int(x.op_str[5:], 16)
                fac = g[0]
                break
        out[type_id] = dict(ctor=ctor, factory=fac, size=size, vtable=vt)
    return out


def slot(vtable, i):
    off = _image().rva2off(vtable + 8 * i)
    return struct.unpack("<Q", _image().data[off:off + 8])[0] - rn.IMAGE_BASE   # slice 8 bytes, not the whole file


if __name__ == "__main__":
    t = table()
    print(len(t), "types")
    for k in sorted(t):
        v = t[k]
        print(k, {a: (hex(b) if isinstance(b, int) else b) for a, b in v.items()})


@functools.lru_cache(None)
def factory_candidates():
    """Small functions that call the allocator and then call / jump into a function that calls the
    base attribute constructor: `alloc(size)` + ctor, with the type id either inside the ctor or
    passed by the factory in edx."""
    img = _image()
    base_callers = {func_of(s)[0] for s in callers(BASE_CTOR) if func_of(s)}
    # family-B type ctors go through 3e14ca0 (itself a 3d0e910 caller): two levels
    base_callers |= {func_of(s)[0] for b in list(base_callers) for s in callers(b) if func_of(s)}
    out = []
    for s in callers(ALLOC):
        g = func_of(s)
        if not g or g[1] - g[0] > 0x80:
            continue
        body = list(img.md.disasm(img.data[img.rva2off(g[0]):img.rva2off(g[1])], g[0]))
        for x in body:
            if x.mnemonic in ("call", "jmp") and x.op_str.startswith("0x"):
                t = int(x.op_str, 16)
                if t in base_callers:
                    out.append((g[0], t))
                    break
    return sorted(set(out))


def native_table(img):
    """Run every factory candidate (allocator patched) and read the attribute it built:
    {type_id: dict(factory, ctor, vtable, size)}; when two factories give one type id the first wins."""
    import ctypes
    out = {}
    for fac, ctor in factory_candidates():
        try:
            obj = img.call(img.function(fac, ctypes.c_void_p))
        except Exception:
            continue
        if not obj:
            continue
        tid = rn.u32(obj + 8)
        vt = rn.u64(obj) - rn.IMAGE_BASE
        if 0 < tid < 512 and 0 < vt < 0x10000000 and (tid not in out or PREFERRED_FACTORY.get(tid) == fac):
            out[tid] = dict(factory=fac, ctor=ctor, vtable=vt, size=None)
    return out


# Type ids built by more than one factory, with the factory whose loader matches the RE4 file layout.
# 18 (EmitterColor): 3cfa430's loader reads 0x10 bytes (UniqueID + 12, the file's size); 3cf9610 also writes
# id 18 but its loader (3de87a0) reads 0x58 bytes and misaligns every later attribute of the entry.
# (24 has two factories with the same loader; 196 and 219 are not RE4 file ids.)
PREFERRED_FACTORY = {18: 0x3CFA430}
