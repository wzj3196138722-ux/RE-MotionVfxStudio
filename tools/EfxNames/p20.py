import sys, re, os; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import wk, fmap
p = wk.get('wilds')
def inner(f):
    for x in p.dis(f, 30):
        if x.mnemonic == 'call' and x.op_str.startswith('0x') and int(x.op_str, 16) != 0xb12a410: return int(x.op_str, 16)
def parse(f):
    xs = p.dis(f, 3000, stop=False); idx = {x.address: i for i, x in enumerate(xs)}
    names = []; pend = None
    for i, x in enumerate(xs):
        t = p.riptarget(x)
        if x.mnemonic == 'lea' and x.op_str.startswith('rcx,') and t and p.cstr(t): pend = p.cstr(t)
        elif x.mnemonic == 'je' and pend:
            names.append((pend, int(x.op_str, 16))); pend = None
        elif x.mnemonic in ('ret',) and names: break
    out = []
    for n, tg in names:
        st = []
        for y in p.dis(tg, 40, stop=False):
            m = re.match(r'(byte|word|dword|qword|xmmword) ptr \[rbx \+ (0x[0-9a-f]+)\], (\w+)', y.op_str)
            if m and y.mnemonic.startswith(('mov', 'vmov')): st.append((int(m.group(2), 16), m.group(1), y.mnemonic))
            if m is None and re.match(r'(byte|word|dword|qword|xmmword) ptr \[rbx\], ', y.op_str): st.append((0, y.op_str.split()[0], y.mnemonic))
            if y.mnemonic in ('jmp', 'ret'): break
        out.append((n, st))
    return out
for t, vt in [(int(a), int(b, 16)) for a, b in (s.split(':') for s in sys.argv[1:])]:
    pr = p.q(vt + 160) - p.base; f = inner(pr)
    ld = fmap.wloader(p, p.q(vt + 40) - p.base)
    bm = fmap.builder_map(p, p.q(vt + 128) - p.base)
    print('==', t, hex(vt), 'parser', hex(pr), '->', hex(f or 0), 'loader', ld, 'builder', {hex(k): hex(v) for k, v in sorted(bm.items())})
    for n, st in parse(f): print('   ', n, [(hex(a), w, m) for a, w, m in st])
