"""Wilds: construct each attribute of one root entry with its factory, then run its loader (vtable slot 5)
on the file bytes through a stream object; report bytes consumed vs the declared size."""
import sys, json, ctypes, struct
import os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import wn
img = wn.load(); B = wn.IMAGE_BASE
import os
for g in filter(None, os.environ.get('W_GLOBALS', '').split(',')):
    wn.install_global(img, int(g, 16), 0x10000)
reg = json.load(open('wreg.json'))
STUB_FN = ctypes.CFUNCTYPE(ctypes.c_uint64, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_uint64)
CALLS = []
def stub(rva, name, answer=None):
    def cb(a, b, c, d):
        try: r = answer(a, b, c, d) if answer else 0
        except Exception as e: r = 0; name2 = f'{name} error {e}'
        CALLS.append((name, a, b, c, d)); return r or 0
    f = img.keep(STUB_FN(cb)); wn.patch_jump(rva, ctypes.cast(f, ctypes.c_void_p).value)
def wstr(a, n=260):
    return ctypes.wstring_at(a, n).split(chr(0))[0] if a else None
RES = {}
def resource(mgr, kind, path, x):
    s = wstr(path + (2 if wn.u32(path) & 0xffff == 0x40 else 0))
    if s not in RES:
        RES[s] = img.alloc(0x1000); wn.set_u32(RES[s] + 0x28, 0x10000)   # refcount: release b006090 never frees
    return RES[s]
stub(0x614a0, 'resource lookup', resource)
READ_FN = ctypes.CFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t)
class Stream:
    def __init__(s, data, pos):
        s.data, s.pos, s.log = data, pos, []
        s.cb = img.keep(READ_FN(s._read)); s.vt = img.alloc(0x100)
        for i in range(32): wn.set_u64(s.vt + 8 * i, ctypes.cast(s.cb, ctypes.c_void_p).value)
        s.obj = img.alloc(0x80); wn.set_u64(s.obj, s.vt); s.handle = img.alloc(16); wn.set_u64(s.handle, s.obj)
    def _read(s, this, buf, size):
        c = s.data[s.pos:s.pos + size]; ctypes.memmove(buf, c, len(c)); s.log.append((s.pos, size)); s.pos += len(c); return len(c)
def run(path, off, skip_size):
    data = open(path, 'rb').read()
    idx, nh, asg, cnt = struct.unpack_from('<4I', data, off)
    pos = off + 16; ctx = img.alloc(0x1000); out = []
    for i in range(cnt):
        tid, size = struct.unpack_from('<2I', data, pos)
        start = pos + 8 if skip_size else pos + 4
        nxt = pos + 8 + size
        st = Stream(data, start)
        attr = img.call(img.function(reg[str(tid)]['fac'], ctypes.c_uint64))
        vt = wn.u64(attr) - B; loader = wn.u64(B + vt + 8 * 5) - B
        try:
            ok = img.call(img.function(loader, ctypes.c_uint8, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_uint64), attr, st.handle, ctx)
            r = dict(tid=tid, size=size, ok=ok, consumed=st.pos - start, reads=len(st.log), end_ok=st.pos == nxt)
        except Exception as e:
            r = dict(tid=tid, size=size, err=str(e)[:200])
        wn.set_u64(ctx + 8 + 8 * tid, attr)
        out.append(r); pos = nxt
    return out
if __name__ == '__main__':
    for r in run(sys.argv[1], int(sys.argv[2]), sys.argv[3] == '1'): print(json.dumps(r))
    print('resources', list(RES))
