import json,re
res={int(k):v for k,v in json.load(open('allmap.json')).items()}
def norm(s): return re.sub(r'[^a-z0-9]','',s.lower())
out=[]
for i,r in sorted(res.items()):
    fm=r['fm']
    if not fm or 'fields' not in fm or not fm['fields']: continue
    cs=r['cs']; lines=[]
    psz=fm['payload']
    csz=sum(f[1] for f in cs) if cs is not None else None
    hdr=f"{i} {r['kind']} exe payload {psz} C# {csz} corpus {r['corpus']}"
    for name,offs,st in fm['fields']:
        if not offs: lines.append(f"   ? {name}: block {st} (no builder copy)"); continue
        o=offs[0]; span=offs[-1]-o+4
        if cs is None: lines.append(f"   {name} @{o:#x}+{span}"); continue
        hit=[f for f in cs if f[0]<=o<f[0]+f[1]]
        if not hit: lines.append(f"   !! {name} @{o:#x}+{span}: no C# field"); continue
        f=hit[0]
        tag='  ' if norm(f[2])==norm(name) else 'NM'
        extra='' if f[0]==o and f[1]==span else f' (C# @{f[0]:#x}+{f[1]})'
        lines.append(f"   {tag} {name} @{o:#x}+{span} -> C# {f[2]}:{f[3]}{extra}")
    out.append(hdr); out+=lines
open('diff.txt','w',encoding='utf-8').write('\n'.join(out))
print(len(out))
