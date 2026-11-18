import sys, json, struct; sys.path.insert(0, sys.argv[1])
import emu
from unicorn import *; from unicorn.x86_const import *
reg = json.load(open('wreg.json'))
for i in [int(x) for x in sys.argv[2].split(',')]:
    E = emu.Emu(); u = E.u; log = []; mapped = []
    def bad(u, acc, addr, size, val, ud):
        if acc in (UC_MEM_READ_UNMAPPED, UC_MEM_WRITE_UNMAPPED) and len(mapped) < 50:
            pg = addr & ~0xfff
            try: u.mem_map(pg, 0x2000); mapped.append(hex(addr)); return True
            except UcError: return False
        return False
    u.hook_add(UC_HOOK_MEM_INVALID, bad)
    u.hook_add(UC_HOOK_CODE, lambda u, a, sz, ud: log.append(('base', u.reg_read(UC_X86_REG_RDX) & 0xffffffff)), begin=emu.B + 0x38970, end=emu.B + 0x38970)
    obj0 = E.hp + 0x10 & ~15
    def wr(u, acc, a, sz, v, ud):
        if sz == 8 and emu.B <= v < emu.B + E.size and a - obj0 < 0x10: log.append(('vt@%x' % (a - obj0), hex(v - emu.B)))
    u.hook_add(UC_HOOK_MEM_WRITE, wr, begin=emu.HEAP, end=emu.HEAP + 0x10000)
    E.trace_on(8)
    r = E.call(reg[str(i)]['fac'], count=100_000_000)
    print(i, 'obj0', hex(obj0), r if isinstance(r, tuple) else ('vt', hex(E.q(r) - emu.B), 'id', E.d(r + 8)), log, 'mapped', mapped[:8], [hex(b) for b in E.blocks], flush=True)
