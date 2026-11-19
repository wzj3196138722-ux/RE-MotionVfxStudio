import sys,json,re; sys.path.insert(0,sys.argv[1]); from wk import *; from pstr import *
W=get('wilds'); R=get('re4')
def slot(P,vt,k): return P.q(vt+8*k)-P.base
def install(P,f):
    f=follow(P,f); xs=P.dis(f,3)
    if xs and xs[0].mnemonic=='mov' and xs[0].op_str.startswith('qword ptr [rdx + ') and xs[0].op_str.endswith('r8'):
        return int(xs[0].op_str.split('+ ')[1].split(']')[0],16)
    if xs and xs[0].mnemonic=='ret': return None
    return 'code:'+hex(f)
def wloader(f):
    f=follow(W,f); xs=W.dis(f,40)
    A=B=None
    for x in xs:
        m=re.match(r'(r8|r9), \[rcx \+ (0x[0-9a-f]+)\]',x.op_str)
        if x.mnemonic=='lea' and m:
            if m.group(1)=='r8': A=int(m.group(2),16)
            else: B=int(m.group(2),16)
        if x.mnemonic=='call' and x.op_str=='0x5fb70' and A is not None and B is not None:
            return dict(kind='range',A=A,B=B,payload=B-A-4)
    reads=[int(x.op_str[5:],16) for x in xs if x.mnemonic=='mov' and x.op_str.startswith('r8d, 0x')]
    return dict(kind='custom',f=f,reads=reads,n=len(xs),calls=[x.op_str for x in xs if x.mnemonic=='call'][:6])
def rloader(f):
    xs=R.dis(f,200)
    reads=[int(x.op_str[5:],16) for x in xs if x.mnemonic=='mov' and x.op_str.startswith('r8d, 0x')]
    return dict(f=f,reads=reads)
wt={int(k):v for k,v in json.load(open('wt.json')).items()}
rt={int(k):v for k,v in json.load(open('rt.json')).items()}
out={'wilds':{},'re4':{}}
for i,v in wt.items():
    vt=v['vt']
    if not vt: continue
    out['wilds'][i]=dict(vt=vt,objsize=v['objsize'],load=wloader(slot(W,vt,5)),install=install(W,slot(W,vt,26)),
        parser=slot(W,vt,20),strs=parser_strings(W,slot(W,vt,20),3000))
PREF={18:0x3CFA430}
for i,L in rt.items():
    c=[e for e in L if e['vt']]
    if not c: continue
    e=c[0]
    for x in c:
        if PREF.get(i)==x['f']: e=x
    vt=e['vt']
    out['re4'][i]=dict(vt=vt,load=rloader(slot(R,vt,5)),install=install(R,slot(R,vt,23)),parser=slot(R,vt,18),strs=parser_strings(R,slot(R,vt,18),3000),alts=len(c))
json.dump(out,open('ext.json','w'))
print(len(out['wilds']),len(out['re4']))
