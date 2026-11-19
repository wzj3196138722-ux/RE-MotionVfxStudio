"""RE4: every attribute type id -> vtable (factories run natively), then the text parser (slot 18) and its strings.

    python -I -X utf8 re4vt.py run    -> re4_native_vt.json (child: native factories)
    python -I -X utf8 re4vt.py        -> re4_parsers.json (static: parser address, empty or not, strs = parser's own strings,
       strs_all = also the strings of the functions it calls)
"""
import sys, json, os, subprocess

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from paths import OUT as H, RE4NATIVE, REPO
RN = RE4NATIVE

if len(sys.argv) > 1 and sys.argv[1] == "run":
    sys.path.insert(0, RN)
    import re4native as rn
    import emitter as em
    import factory
    img = rn.load()
    em.install_runtime(img)
    t = factory.native_table(img)
    st = factory.table()
    out = {str(k): dict(vt=v['vtable'], factory=v['factory'], ctor=v['ctor']) for k, v in t.items()}
    for k, v in st.items():   # static ctor-immediate table as a cross-check
        out.setdefault(str(k), dict(vt=v['vtable'], factory=v['factory'], ctor=v['ctor'], static_only=True))
        out[str(k)]['static_vt'] = v['vtable']
    json.dump(out, open(os.path.join(H, 're4_native_vt.json'), 'w'))
    print('native', len(t), 'static', len(st), 'union', len(out))
    sys.exit(0)

p = subprocess.run([sys.executable, '-I', '-X', 'utf8', os.path.abspath(__file__), 'run'], capture_output=True, text=True)
print(p.stdout.strip()[-500:], p.stderr.strip()[-1500:])
sys.path.insert(0, H)
import wk
from pstr import parser_strings, follow
R = wk.get('re4')
vts = json.load(open(os.path.join(H, 're4_native_vt.json')))
cs = {x['id']: x['kind'] for x in json.load(open(os.path.join(H, 'cs_now.json')))['RE4']}
EMPTY = 0xAEA70
res = {}
for k, v in sorted(vts.items(), key=lambda kv: int(kv[0])):
    vt = v['vt']
    if not vt:
        continue
    parser = R.q(vt + 8 * 18) - R.base
    f = follow(R, parser)
    first = R.dis(f, 2)
    empty = f == EMPTY or (first and first[0].mnemonic == 'xor' and len(first) > 1 and first[1].mnemonic == 'ret')
    strs = [] if empty else parser_strings(R, parser, 3000)
    res[k] = dict(kind=cs.get(int(k)), vt=hex(vt), parser=hex(f), empty=bool(empty), strs=strs,
                  mismatch_static=(v.get('static_vt') not in (None, vt)))


def strings_full(f, limit=6000):
    """Every string literal a parser function references up to its int3 padding (inner parsers return early
    after each name, so stopping at the first ret would keep only the first one)."""
    f = follow(R, f)
    out = []
    for x in R.dis(f, limit, stop=False):
        if x.mnemonic == 'int3':
            break
        t = R.riptarget(x)
        if t is not None:
            c = R.cstr(t)
            if c and c not in out:
                out.append(c)
    return out


# names also come from functions the parser calls: the inner-parser form (outer packs its arguments and calls
# an inner function) and the shared material colour parser
for k, v in res.items():
    if v['empty']:
        continue
    f = int(v['parser'], 16)
    calls = [int(x.op_str, 16) for x in R.dis(f, 400, stop=False) if x.mnemonic in ('call', 'jmp') and x.op_str.startswith('0x')]
    names = []
    for c in [f] + list(dict.fromkeys(calls)):
        for s in strings_full(c):
            if s not in names:
                names.append(s)
    v['strs_all'] = names
json.dump(res, open(os.path.join(H, 're4_parsers.json'), 'w'), indent=0)
n = len(res)
ne = [k for k, v in res.items() if not v['empty']]
named = [k for k in ne if res[k]['strs']]
print('types', n, 'in C#', sum(1 for v in res.values() if v['kind']), 'nonempty parser', len(ne), 'with strings', len(named))
print('C# kinds without a vtable:', sorted(set(cs.values()) - {v['kind'] for v in res.values()}))
print('static/native vtable disagree:', [k for k, v in res.items() if v['mismatch_static']])
