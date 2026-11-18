"""Wilds: native builder (slot 16) marker diff on corpus payloads: file word i -> block offsets.

    python -I -X utf8 n8.py <this folder> <raw.json from RawDump> <TypeName> <vtable hex> [<payload base hex>]  -> bm_<TypeName>.json in the current folder
"""
import sys, json, struct, collections, ctypes, os; sys.path.insert(0, sys.argv[1])
from paths import RE4NATIVE; sys.path.insert(0, RE4NATIVE)
import wn, re4native as rn, wk, fmap
W = wk.get('wilds'); img = wn.load()
raw = json.load(open(sys.argv[2]))
name, vt = sys.argv[3], int(sys.argv[4], 16)
sl = lambda k: W.q(vt + 8 * k) - W.base
A4, B = fmap.wloader(W, sl(5)) or (None, None)
base = A4 if A4 is not None else int(sys.argv[5], 16)
OBJ = 0x400; BL = 0x400
import os
for g in filter(None, os.environ.get('W_GLOBALS', '').split(',')):
    rn.set_u64(rn.IMAGE_BASE + int(g, 16), img.alloc(0x10000))
STUB = {'ret0': bytes([0x31, 0xC0, 0xC3]), 'ret1f': bytes([0xB8, 0, 0, 0x80, 0x3F, 0x66, 0x0F, 0x6E, 0xC0, 0xC3])}
for p in filter(None, os.environ.get('W_PATCH', '').split(',')):
    a, k = p.split('=')
    ctypes.memmove(rn.IMAGE_BASE + int(a, 16), STUB[k], len(STUB[k]))
obj = img.alloc(OBJ); blk = img.alloc(BL); ctx = img.alloc(0x1000)
builder = img.function(sl(16), ctypes.c_uint64, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_uint64)
def build(payload):
    ctypes.memset(obj, 0, OBJ); rn.set_u64(obj, rn.IMAGE_BASE + vt)
    ctypes.memmove(obj + base, payload, len(payload))
    ctypes.memset(blk, 0, BL); ctypes.memset(ctx, 0, 0x1000)
    img.call(builder, obj, blk, ctx)
    return ctypes.string_at(blk, BL)
samples = [bytes.fromhex(h) for h in raw[sys.argv[6] if len(sys.argv) > 6 else name]]
res = collections.defaultdict(collections.Counter); flagish = collections.Counter(); runs = 0
for s in samples:
    b0 = build(s); runs += 1
    for i in range(len(s) // 4):
        M = 0x5A5A0000 | i
        b1 = build(s[:4 * i] + struct.pack('<I', M) + s[4 * i + 4:])
        diff = [o for o in range(0, BL, 4) if b0[o:o + 4] != b1[o:o + 4]]
        hit = [o for o in diff if struct.unpack_from('<I', b1, o)[0] == M]
        if len(diff) > len(hit): flagish[i] += 1
        for o in hit: res[i][o] += 1
pm = fmap.parser_map(W, sl(20)) or []
names = collections.defaultdict(list)
for nm, st in pm:
    for bo, w in st:
        for k in range(0, max(w, 4), 4): names[bo + k].append(nm)
out = dict(name=name, vt=hex(vt), base=base, payload=len(samples[0]), runs=runs,
           words={4 * i: dict(blocks={hex(o): c for o, c in res.get(i, {}).items()}, flag=flagish[i]) for i in range(len(samples[0]) // 4)},
           parser={hex(k): v for k, v in names.items()})
json.dump(out, open('bm_%s.json' % name, 'w'))
print(name, 'vt', hex(vt), 'base', hex(base), 'payload', len(samples[0]), 'runs', runs)
for i in range(len(samples[0]) // 4):
    tg = res.get(i, {})
    print('  file %3d' % (4 * i), '->', ', '.join('blk %#x x%d %s' % (o, c, '/'.join(dict.fromkeys(names.get(o, [])))) for o, c in sorted(tg.items())), (' changes-other %d/%d' % (flagish[i], runs)) if flagish[i] else '')
