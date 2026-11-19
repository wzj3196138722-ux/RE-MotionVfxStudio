"""Emulate every attribute's vtable slot 6 (step registration) with a fake ctx -> X and read X+0x50 list."""
import sys, json, struct, re; sys.path.insert(0, sys.argv[1])
import emu
from unicorn import *; from unicorn.x86_const import *
import wk
W = wk.get('wilds')
vts = {}
for line in open('table.md', encoding='utf-8'):
    m = re.match(r'\| (\d+) \| (\w+) \|[^|]*\| [^|]*\| (e[0-9a-f]+) \|', line)
    if m: vts[int(m.group(1))] = (m.group(2), int(m.group(3), 16))
try:
    for k, v in json.load(open('emu_fac.json')).items():
        if v.get('vts') and int(k) not in vts: vts[int(k)] = ('?', v['vts'][-1])
except FileNotFoundError: pass
E = emu.Emu(); u = E.u
for i in range(64): u.mem_write(E.teb + 0x2000 + 0x100 * i + 0x1c, struct.pack('<I', 0x7fffffff))
E.patch(0x1670, lambda E, u: E.malloc(u.reg_read(UC_X86_REG_RCX) + 16))
E.patch(0x1330, lambda E, u: None)
def _memcpy(E, u):
    d, sr, n = u.reg_read(UC_X86_REG_RCX), u.reg_read(UC_X86_REG_RDX), u.reg_read(UC_X86_REG_R8)
    if n: u.mem_write(d, bytes(u.mem_read(sr, n)))
    return d
E.patch(0xd16a8f0, _memcpy)
reg = json.load(open('wreg.json'))
OBJ = {}
for tid in sorted(vts):
    r = E.call(reg[str(tid)]['fac'], count=50_000_000) if str(tid) in reg else None
    if isinstance(r, int) and r and E.q(r) - emu.B == vts[tid][1]: OBJ[tid] = r
    else:
        o = E.zalloc(0x400); u.mem_write(o, struct.pack('<Q', emu.B + vts[tid][1])); OBJ[tid] = o
        print('fake obj', tid, r if not isinstance(r, tuple) else r[:3], flush=True)
out = {}
for mode in (1, 0, 2):
    for tid, (name, vt) in sorted(vts.items()) + [(-1, ('EMITTER', None))]:
        f = W.q(vt + 6 * 8) - W.base if vt else 0x386e3e0
        ctx = E.malloc(0x100); X = E.malloc(0x200); lst = E.malloc(0x1000)
        u.mem_write(ctx, struct.pack('<Q', X)); u.mem_write(X + 0x34, struct.pack('<I', mode))
        u.mem_write(X + 0x50, struct.pack('<QII', lst, 0, 1000)); lst2 = E.malloc(0x1000)
        u.mem_write(X + 0x60, struct.pack('<QII', lst2, 0, 1000))
        obj = OBJ.get(tid)
        r = E.call(f, (obj or 0, ctx) if vt else (ctx,), count=5_000_000)
        extra = {}
        for off in range(0, 0x200, 8):
            if off in (0x30, 0x50, 0x58, 0x60, 0x68): continue
            v = E.q(X + off)
            if v: extra[hex(off)] = hex(v)
        n2 = E.d(X + 0x68); L2 = list(struct.unpack('<%dI' % n2, u.mem_read(E.q(X + 0x60), 4 * n2))) if n2 < 1000 else 'bad'
        n = E.d(X + 0x58); L = list(struct.unpack('<%dI' % n, u.mem_read(E.q(X + 0x50), 4 * n))) if n < 1000 else 'bad'
        out.setdefault(tid, {})[mode] = dict(f=hex(f), steps=L, steps2=L2, extra=extra, err=None if not isinstance(r, tuple) else r[:3])
        if L or isinstance(r, tuple): print(mode, tid, name, hex(f), L, L2, extra, '' if not isinstance(r, tuple) else r[:3], flush=True)
json.dump(out, open('emu_steps.json', 'w'))
