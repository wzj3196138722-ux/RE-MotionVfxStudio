"""Generic: walk callers of the attribute base ctor; for every function on a path record (edx type id or None, vt written after the call)."""
import sys,json; sys.path.insert(0,sys.argv[1]); from wk import *
g=sys.argv[2]; P=get(g)
BASE={'re4':0x3D0E910,'wilds':0x38970}[g]
def window(s):
    f=P.fstart(s)
    if f is not None and s-f<0x400:
        return f,[x for x in P.dis(f,200,stop=False) if x.address<s]
    for k in range(0x60,0,-1):
        o=P.rva2off(s-k)
        xs=list(md.disasm(P.d[o:P.rva2off(s)],s-k))
        if xs and xs[-1].address+xs[-1].size==s:
            j=max([i for i,x in enumerate(xs) if x.mnemonic in('ret','int3','jmp')]+[-1])
            return (xs[j+1].address if j+1<len(xs) else s), xs[j+1:]
    return s,[]
def eval_edx(xs):
    v=None
    for x in xs:
        o=x.op_str
        if x.mnemonic=='xor' and o=='edx, edx': v=0; continue
        if not o.startswith('edx, '): continue
        s=o[5:]
        try:
            if x.mnemonic=='mov':
                t=P.riptarget(x)
                if t is not None: v=P.u32(t)
                elif s.startswith('0x'): v=int(s,16)
                elif s.isdigit(): v=int(s)
                else: v=None
            elif x.mnemonic in('add','sub','xor') and v is not None:
                i=int(s,16) if s.startswith('0x') else int(s)
                v={'add':v+i,'sub':v-i,'xor':v^i}[x.mnemonic]&0xffffffff
            else: v=None
        except Exception: v=None
    return v
CODE=set(n for n,*_ in P.execsecs())
def vt_after(s,op):
    if op==0xE9: return None
    vt=None
    xs=P.dis(s+5,40)
    for i,x in enumerate(xs):
        t=P.riptarget(x)
        if x.mnemonic=='lea' and t and P.secname(t) not in CODE and i+1<len(xs):
            n=xs[i+1]
            r=x.op_str.split(',')[0]
            if n.mnemonic=='mov' and n.op_str.endswith(', '+r) and n.op_str.startswith('qword ptr [r') and '+' not in n.op_str: vt=t
    return vt
nodes={}   # func -> dict(id, vt, parent)
def walk(target,depth):
    o,si,tg=P.branches
    for op,s in zip(o[tg==target],si[tg==target]):
        s=int(s); op=int(op)
        f,xs=window(s)
        tid=eval_edx(xs)
        if f in nodes: continue
        nodes[f]=dict(id=tid,vt=vt_after(s,op),parent=target,site=s,op=op)
        if tid is None and depth<4: walk(f,depth+1)
walk(BASE,0)
json.dump({str(k):v for k,v in nodes.items()},open(f'nodes_{g}.json','w'))
print(g,'nodes',len(nodes),'with id',sum(1 for v in nodes.values() if v['id'] is not None))
