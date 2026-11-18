import sys,json; sys.path.insert(0,sys.argv[1]); from wk import *; from resolve import *
W=get('wilds'); R=get('re4')
# Wilds: registration index -> ctor
reg={int(k):v for k,v in json.load(open('wreg.json')).items()}
wn=load('wilds'); rn=load('re4')
wt={}
miss=[]
for i,e in reg.items():
    c=e['ctor']
    if not c: continue
    c2=follow_jmp(W,c)
    tid,vt=resolve(wn,c2) if c2 in wn else resolve(wn,c)
    if c2 not in wn and c not in wn: miss.append((i,hex(c),hex(c2),W.secname(c2)))
    wt[i]=dict(ctor=c,ctor2=c2,objsize=e['size'],vt=vt,tid=tid)
print('wilds resolved vt',sum(1 for v in wt.values() if v['vt']),'of',len(wt),'tid mismatch',[(i,v['tid']) for i,v in wt.items() if v['tid'] is not None and v['tid']!=i])
print('miss',len(miss),miss[:20])
json.dump(wt,open('wt.json','w'))
# RE4: id -> vt
rt={}
for f,n in rn.items():
    tid,vt=resolve(rn,f)
    if n['id'] is None: continue
    # only outermost nodes: those nobody else points to as parent
    rt.setdefault(tid,[]).append(dict(f=f,vt=vt))
print('re4 ids',len(rt))
json.dump(rt,open('rt.json','w'))
# fallback: parse ctor body directly
for i,v in wt.items():
    if v['vt']: continue
    xs=W.dis(v['ctor2'],30)
    tid=None
    for j,x in enumerate(xs):
        if x.op_str.startswith('edx, 0x') and x.mnemonic=='mov' and tid is None: tid=int(x.op_str[5:],16)
        t=W.riptarget(x)
        if x.mnemonic=='lea' and t and W.secname(t) not in ('.xtls','.trace') and j+1<len(xs) and xs[j+1].op_str.startswith('qword ptr [rsi], ') and W.cstr(t) is None:
            v['vt']=t; v['tid']=tid; v['fallback']=1
print('after fallback missing',[(i,hex(v['ctor2'])) for i,v in wt.items() if not v['vt']])
json.dump(wt,open('wt.json','w'))
import collections
cnt=[collections.Counter() for _ in range(40)]
for v in wt.values():
    if v['vt']:
        for k in range(40): cnt[k][W.q(v['vt']+8*k)]+=1
for k in range(40): print(k,[(hex(a-W.base),n) for a,n in cnt[k].most_common(3)])
