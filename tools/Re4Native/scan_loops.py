"""Step 5 (static, nothing is run): what the emitter loop 3d08480 and the per-particle pipeline
3d05350 reach beyond the emitter, i.e. what a native run of them would have to stub.

For every function reachable through direct calls / tail jumps up to DEPTH levels:
  - imports it calls (bound by the harness or trapped)
  - globals it touches: rip-relative operands into .data, split into initialised and .bss (zero in a
    freshly mapped image, so a pointer there is null)
  - indirect calls (vtables, function pointers: their targets come from runtime objects)
  - TLS / TEB access through gs:
Function bounds: start from the call target, disassemble until a ret / jmp is followed by int3
padding (MSVC layout).

    python -I -X utf8 -u scan_loops.py [depth]
"""
import collections
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import re4native as rn  # noqa: E402
from pe_image import Image  # noqa: E402

ROOTS = {0x3D08480: "emitter tick loop (3D path)", 0x3D05350: "per-particle pipeline"}
DEPTH = int(sys.argv[1]) if len(sys.argv) > 1 else 3

img = Image(os.environ.get("RE4_EXE", rn.DEFAULT_EXE))
TEXT = next(s for s in img.secs if s[0] == ".text")
DATA = next(s for s in img.secs if s[0] == ".data")
DATA_RAW_END = DATA[1] + DATA[3]          # past this, .data is zero-fill (.bss)
IMPORTS = img.imports()
THUNKS = {}                               # rva of `jmp [rip+IAT]` -> import name


def thunk_name(rva):
    if rva in THUNKS:
        return THUNKS[rva]
    name = None
    o = img.rva2off(rva)
    if o is not None and img.data[o:o + 2] == b"\xff\x25":
        disp = int.from_bytes(img.data[o + 2:o + 6], "little", signed=True)
        name = IMPORTS.get(rva + 6 + disp)
    THUNKS[rva] = name
    return name


RIP = re.compile(r"\[rip ([+-]) (0x[0-9a-f]+)\]")


def scan_fn(start, limit=30000):
    o = img.rva2off(start)
    info = dict(calls=set(), tails=set(), imports=set(), g_data=set(), g_bss=set(), indirect=0, gs=set(), size=0)
    if o is None:
        return info
    end_seen = False
    n = 0
    for x in img.md.disasm(img.data[o:o + limit * 8], start):
        n += 1
        if n > limit:
            break
        if end_seen and x.mnemonic == "int3":
            break
        end_seen = x.mnemonic in ("ret", "jmp")
        ops = x.op_str
        m = RIP.search(ops)
        if m:
            tgt = x.address + x.size + (int(m.group(2), 16) * (1 if m.group(1) == "+" else -1))
            if tgt in IMPORTS:
                info["imports"].add(IMPORTS[tgt])
            elif DATA[1] <= tgt < DATA[1] + DATA[4]:
                info["g_bss" if tgt >= DATA_RAW_END else "g_data"].add(tgt)
        if "gs:" in ops:
            info["gs"].add(ops)
        if x.mnemonic == "call" or (x.mnemonic == "jmp" and end_seen):
            if ops.startswith("0x"):
                t = int(ops, 16)
                tn = thunk_name(t)
                if tn:
                    info["imports"].add(tn)
                elif TEXT[1] <= t < TEXT[1] + TEXT[4] and not (start <= t < x.address):
                    (info["calls"] if x.mnemonic == "call" else info["tails"]).add(t)
            elif not m:
                info["indirect"] += 1
        info["size"] = x.address + x.size - start
    return info


def walk(root):
    seen = {root: 0}
    q = collections.deque([root])
    infos = {}
    while q:
        f = q.popleft()
        infos[f] = scan_fn(f)
        if seen[f] >= DEPTH:
            continue
        for c in infos[f]["calls"] | infos[f]["tails"]:
            if c not in seen:
                seen[c] = seen[f] + 1
                q.append(c)
    return seen, infos


def summary(root):
    seen, infos = walk(root)
    imports = collections.Counter()
    bss = collections.Counter()
    data = collections.Counter()
    gs_users, indirect = [], 0
    for f, i in infos.items():
        imports.update(i["imports"])
        bss.update(i["g_bss"])
        data.update(i["g_data"])
        indirect += i["indirect"]
        if i["gs"]:
            gs_users.append((hex(f), sorted(i["gs"])[:3]))
    direct = scan_fn(root)
    bound = set()
    try:
        import pefile  # noqa: F401
        for e in img.pe.DIRECTORY_ENTRY_IMPORT:
            dll = e.dll.decode().lower()
            if dll in rn._RESOLVED_DLLS or dll.startswith("api-ms-win-crt-"):
                bound.update(imp.name.decode() for imp in e.imports if imp.name)
    except Exception:
        pass
    return dict(
        root=hex(root), what=ROOTS[root], depth=DEPTH,
        functions=len(infos), by_depth=dict(collections.Counter(seen.values())),
        direct_callees=sorted(hex(c) for c in direct["calls"] | direct["tails"]),
        direct_indirect_calls=direct["indirect"], direct_bss_globals=sorted(hex(g) for g in direct["g_bss"]),
        direct_data_globals=sorted(hex(g) for g in direct["g_data"]),
        imports={k: ("bound" if k in bound else "TRAP") for k in sorted(imports)},
        bss_globals=len(bss), bss_most_used=[(hex(g), n) for g, n in bss.most_common(15)],
        data_globals=len(data), indirect_calls=indirect, gs_users=gs_users[:15], gs_user_count=len(gs_users))


def main():
    out = [summary(r) for r in ROOTS]
    print(json.dumps(out, indent=1))
    print("RESULT " + json.dumps({"exp": "scan_loops", "status": "INFO",
                                  **{o["root"]: dict(functions=o["functions"], bss_globals=o["bss_globals"],
                                                     trapped_imports=sum(v == "TRAP" for v in o["imports"].values()),
                                                     tls_users=o["gs_user_count"], indirect_calls=o["indirect_calls"])
                                     for o in out}}))


if __name__ == "__main__":
    main()
