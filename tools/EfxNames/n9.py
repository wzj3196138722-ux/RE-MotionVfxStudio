import sys, json, struct, ctypes, os; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from paths import OUT
import wn, re4native as rn, wk, fmap
W = wk.get('wilds'); img = wn.load(); vt = 0xe0fdc90
sl = lambda k: W.q(vt + 8 * k) - W.base
base = fmap.wloader(W, sl(5))[0]
raw = json.load(open(os.path.join(OUT, 'raw.json'))); s = bytes.fromhex(next(iter(raw['ShaderSettings'])))
obj = img.alloc(0x400); blk = img.alloc(0x400); ctx = img.alloc(0x1000)
b = img.function(sl(16), ctypes.c_uint64, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_uint64)
def run(p):
    ctypes.memset(obj, 0, 0x400); rn.set_u64(obj, rn.IMAGE_BASE + vt); ctypes.memmove(obj + base, p, len(p))
    ctypes.memset(blk, 0, 0x400); img.call(b, obj, blk, ctx); return ctypes.string_at(blk, 0x400)
b0 = run(s)
for off, val in ((136, struct.pack('<f', 0.5)), (136, struct.pack('<f', 0.25)), (140, struct.pack('<I', 5)), (112, struct.pack('<I', 7)), (148, struct.pack('<f', 3.0)), (152, struct.pack('<f', 3.0))):
    for fl in (struct.unpack_from('<I', s, 20)[0], 0xffffffff):
        p = bytearray(s); p[off:off + 4] = val; p[20:24] = struct.pack('<I', fl); bb = run(bytes(p))
        pb = bytearray(s); pb[20:24] = struct.pack('<I', fl); base0 = run(bytes(pb))
        d = [(hex(o), base0[o:o + 4].hex(), bb[o:o + 4].hex()) for o in range(0, 0x400, 4) if base0[o:o + 4] != bb[o:o + 4]]
        print(off, val.hex(), hex(fl), d)
# 2-byte field at 156
for fl in (struct.unpack_from('<I', s, 20)[0], 0xffffffff):
    p = bytearray(s); p[156:158] = struct.pack('<H', 0x1234); p[20:24] = struct.pack('<I', fl)
    pb = bytearray(s); pb[20:24] = struct.pack('<I', fl)
    bb, b0 = run(bytes(p)), run(bytes(pb))
    print(156, hex(fl), [(hex(o), b0[o:o + 4].hex(), bb[o:o + 4].hex()) for o in range(0, 0x400, 4) if b0[o:o + 4] != bb[o:o + 4]])
