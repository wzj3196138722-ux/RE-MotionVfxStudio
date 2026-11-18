import sys,json; sys.path.insert(0,sys.argv[1]); from wk import *; from pstr import follow
import numpy as np
W=get('wilds'); T=0x14bf66e8; N=200
si,tg=W.riprefs
m=(tg>=T)&(tg<T+8*N)
steps={}
for a,b in zip(si[m],tg[m]):
    a=int(a); k=(int(b)-T)//8
    xs=W.dis(a-7,2,stop=False)
    if len(xs)==2 and xs[0].mnemonic=='lea' and xs[1].address==a and xs[1].mnemonic=='mov':
        F=W.riptarget(xs[0]); steps.setdefault(k,set()).add((F,W.fstart(a)))
# owner: slot 6 function -> type ids
ext={int(k):v for k,v in json.load(open('ext.json'))['wilds'].items()}
cs={x['id']:x['kind'] for x in json.load(open('cs.json'))['MHWilds']}
own={}
for i,v in ext.items():
    f=follow(W,W.q(v['vt']+6*8)-W.base)
    own.setdefault(f,[]).append(i)
out=[]
for k in sorted(steps):
    for F,reg in steps[k]:
        o=own.get(reg,[])
        out.append((k,F,reg,[cs.get(i,i) for i in o]))
        print(f'{k:3d} {F:#x} reg={reg:#x} {[cs.get(i,i) for i in o][:4]}')
json.dump(out,open('steps.json','w'))
print('steps',len(steps),'max',max(steps))
