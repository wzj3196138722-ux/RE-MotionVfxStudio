import sys,os,json,re
sys.path.insert(0,os.path.dirname(os.path.abspath(__file__)))
from fmap import *
exec(open('cmp.py').read().split('cw={')[0])
W=get('wilds')
ext=json.load(open('ext.json'))['wilds']; corp=json.load(open('corpus_wilds.json'))
cw={x['id']:x for x in c['MHWilds']}
def norm(s): return re.sub(r'[^a-z0-9]','',s.lower())
res={}
for i,x in sorted(cw.items()):
    w=ext.get(str(i))
    if not w: continue
    try: fm=fieldmap(W,w['vt'])
    except Exception as ex: fm=dict(err=str(ex))
    # C# offsets
    off=0; cso=[]
    for n,t in x['fields']:
        if t not in SZ: cso=None; break
        cso.append((off,SZ[t],n,t)); off+=SZ[t]
    res[i]=dict(kind=x['kind'],fm=fm,cs=cso,corpus=corp.get(x['kind'],{}))
json.dump(res,open('allmap.json','w'))
print('done',len(res),'mapped',sum(1 for r in res.values() if r['fm'] and 'fields' in r['fm'] and r['fm']['fields']))
