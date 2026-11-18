"""Join r4m_*.json (re4.exe parser + loader/builder markers) with the C# RE4 fields: per C# field, the engine names
that land on its file bytes. Output r4join.json and a summary on stdout."""
import sys, glob, json, os, re

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from paths import OUT as H, RE4NATIVE, REPO
cs = {x['kind']: x for x in json.load(open(os.path.join(H, 'cs_now.json')))['RE4']}
_src = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'unkn_list.py'), encoding='utf-8').read()
SZ = eval(_src.split('SZ = ')[1].split('\nUNK')[0])   # the same size table as unkn_list.py


def nm(s):
    return re.sub(r'[^a-z0-9]', '', s.lower())


res = {}
for p in sorted(glob.glob(os.path.join(H, 'r4m_*.json'))):
    d = json.load(open(p))
    kind = d['kind']
    blk2names = {}
    for n, offs in d['parser'].items():
        for o in offs:
            blk2names.setdefault(o, []).append(n)
    word2names = {}
    for k, w in d['words'].items():
        names = set()
        for b in w['blocks']:
            names.update(blk2names.get(int(b, 16), []))
        word2names[int(k)] = dict(names=sorted(names), blocks=sorted(w['blocks']), changed=w['changed'], fail=w['fail'])
    fields = (cs.get(kind) or {}).get('fields') or []
    off, rows = 0, []
    for n, t in fields:
        if off is None or t not in SZ:
            rows.append(dict(field=n, type=t, off=None))
            off = None
            continue
        ws = [word2names.get(k) for k in range(off - off % 4, off + SZ[t], 4) if word2names.get(k)]
        names = sorted({x for w in ws for x in w['names']})
        status = ('match' if names and any(nm(x) == nm(n) or nm(n).startswith(nm(x)) or nm(x).startswith(nm(n)) for x in names)
                  else 'named' if names else
                  'converted' if any(w['changed'] for w in ws) and not any(w['blocks'] for w in ws) else
                  'copied_unnamed' if any(w['blocks'] for w in ws) else
                  'length' if any(w['fail'] for w in ws) else
                  'not_read' if ws else 'no_data')
        rows.append(dict(field=n, type=t, off=off, names=names, status=status,
                         blocks=sorted({b for w in ws for b in w['blocks']})))
        off += SZ[t]
    unused = sorted(set(d['parser']) - {x for r in rows for x in r.get('names', [])})
    res[kind] = dict(runs=d['runs'], payload=d.get('payload'), parser=d['parser'], rows=rows, unplaced=unused)
json.dump(res, open(os.path.join(H, 'r4join.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
for kind, r in res.items():
    ren = [(x['field'], x['off'], x['names']) for x in r['rows'] if x.get('status') == 'named']
    if ren or r['unplaced']:
        print(kind, 'runs', r['runs'])
        for x in ren:
            print('   ', x)
        if r['unplaced']:
            print('    unplaced', r['unplaced'])
