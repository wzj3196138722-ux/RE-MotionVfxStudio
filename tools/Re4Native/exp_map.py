"""Step 1: map re4.exe, bind imports, and check that faults come back as Python exceptions.

Checks
  map      the image lands at 0x140000000 and its bytes equal the file
  imports  how many IAT entries are bound to system DLLs, how many point at traps (by DLL)
  fault    an engine function given a bad pointer raises NativeFault with the faulting RVA
  trap     calling an unbound import raises UnresolvedImportCalled with its name
"""
import collections
import ctypes
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import re4native as rn  # noqa: E402


def main():
    out = {}
    img = rn.load()
    lay = img.layout
    text = next(s for s in lay["sections"] if s[0] == ".text")
    with open(img.path, "rb") as f:
        f.seek(text[4] + 0x3D1D830 - text[2])
        disk = f.read(64)
    out["map"] = dict(base=hex(rn.IMAGE_BASE), size=hex(img.size),
                      code_matches_file=img.read(rn.IMAGE_BASE + 0x3D1D830, 64) == disk)
    by_dll = collections.Counter(t.split("!")[0] for t in img.traps)
    out["imports"] = dict(bound=img.bound, trapped=len(img.traps), trapped_by_dll=dict(by_dll.most_common()))

    # TLS directory: report only (callbacks are never run)
    tls = rn.IMAGE_BASE + lay["tls_rva"]
    start, end, index_addr, callbacks = (rn.u64(tls), rn.u64(tls + 8), rn.u64(tls + 16), rn.u64(tls + 24))
    ncb = 0
    while rn.u64(callbacks + 8 * ncb):
        ncb += 1
    out["tls"] = dict(template_bytes=end - start, index_rva=hex(index_addr - rn.IMAGE_BASE),
                      callbacks=ncb, run=False)

    # fault: the seeder 3d09990 reads E = [owner+8] then [E+0x120]; give it E = 0x10
    seeder = img.function(0x3D09990, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_uint32)
    st = img.alloc(16)
    owner = img.alloc(32)
    rn.set_u64(owner + 8, 0x10)
    try:
        img.call(seeder, st, 0x27, owner, 0)
        out["fault"] = "no fault (unexpected)"
    except rn.NativeFault as e:
        out["fault"] = dict(rip=hex(e.rip - rn.IMAGE_BASE), access=e.access, address=hex(e.address),
                            caught=True, message=str(e))

    # trap: call the first unbound import's slot directly
    name = img.traps[0]
    fn = ctypes.CFUNCTYPE(ctypes.c_void_p)(img.trap_address(name))
    try:
        img.call(fn)
        out["trap"] = "returned (unexpected)"
    except rn.UnresolvedImportCalled as e:
        out["trap"] = dict(raised="UnresolvedImportCalled", name=str(e))

    ok = out["map"]["code_matches_file"] and isinstance(out["fault"], dict) and isinstance(out["trap"], dict)
    out["status"] = "PASS" if ok else "FAIL"
    print(json.dumps(out, indent=1, ensure_ascii=False))
    print("RESULT " + json.dumps({"exp": "map", "status": out["status"]}))


if __name__ == "__main__":
    sys.exit(main())
