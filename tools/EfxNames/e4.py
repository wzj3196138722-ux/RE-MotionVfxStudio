import sys, json, struct, gc; sys.path.insert(0, sys.argv[1])
import emu
from unicorn import *; from unicorn.x86_const import *
reg = json.load(open('wreg.json')); out = {}
for i in range(1, 282):
    E = emu.Emu(); u = E.u
    peb = E.malloc(0x1000); u.mem_write(E.teb + 0x60, struct.pack('<Q', peb))
    u.mem_write(peb + 0x10, struct.pack('<Q', emu.B))
    log = []
    u.hook_add(UC_HOOK_CODE, lambda u, a, sz, ud: log.append(('base', u.reg_read(UC_X86_REG_RDX) & 0xffffffff, u.reg_read(UC_X86_REG_R8) & 0xffffffff)), begin=emu.B + 0x38970, end=emu.B + 0x38970)
    def wr(u, acc, a, sz, v, ud):
        if a == 0x800001000 and sz == 8 and emu.B <= v < emu.B + E.size: log.append(('vt', v - emu.B))
        if a == 0x800001008 and sz == 4: log.append(('id', v))
    u.hook_add(UC_HOOK_MEM_WRITE, wr, begin=emu.HEAP, end=emu.HEAP + 0x2000)
    r = E.call(reg[str(i)]['fac'], count=50_000_000)
    ok = not isinstance(r, tuple)
    out[i] = dict(ok=ok, base=[x[1:] for x in log if x[0] == 'base'], vts=[x[1] for x in log if x[0] == 'vt'], ids=[x[1] for x in log if x[0] == 'id'],
                  final_vt=(E.q(r) - emu.B) if ok and r else None, final_id=E.d(r + 8) if ok and r else None)
    print(i, out[i], flush=True)
    del E, u; gc.collect()
json.dump(out, open('emu_fac.json', 'w'))
