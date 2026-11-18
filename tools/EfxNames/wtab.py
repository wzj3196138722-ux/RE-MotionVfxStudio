import sys,re,json; sys.path.insert(0,sys.argv[1]); from wk import *
W=get('wilds')
BASE=0x38970
def window(s):
    f=W.fstart(s)
    if f is not None and s-f<0x400:
        return f,[x for x in W.dis(f,200,stop=False) if x.address<s]
    for k in range(0x60,0,-1):
        xs=list(md.disasm(W.d[W.rva2off(s-k):W.rva2off(s)],s-k))
        if xs and xs[-1].address+xs[-1].size==s:
            # cut after last terminator
            j=max([i for i,x in enumerate(xs) if x.mnemonic in('ret','int3','jmp')]+[-1])
            return xs[j+1].address if j+1<len(xs) else s, xs[j+1:]
    return s,[]
def eval_edx(xs):
    v=None; seen=False
    for x in xs:
        o=x.op_str
        if x.mnemonic=='xor' and o=='edx, edx': v=0; seen=True; continue
        if not o.startswith('edx, '): continue
        seen=True; s=o[5:]
        try:
            if x.mnemonic=='mov':
                t=W.riptarget(x)
                if t is not None: v=W.u32(t)
                elif s.startswith('0x') or s.isdigit(): v=int(s,16) if s.startswith('0x') else int(s)
                else: v=None
            elif x.mnemonic in('add','sub','xor') and v is not None:
                i=int(s,16) if s.startswith('0x') else int(s)
                v={'add':v+i,'sub':v-i,'xor':v^i}[x.mnemonic]&0xffffffff
            else: v=None
        except Exception: v=None
    return v,seen
def vt_after(s):
    vt=None
    for x in W.dis(s+5,30):
        t=W.riptarget(x)
        if x.mnemonic=='lea' and t and W.secname(t)!=W.secname(s) and 'qword ptr' not in x.op_str:
            # must be followed by store to [rsi]/[rbx]/[rdi]
            vt=t
    return vt
out={}; shared={}
def walk(target,depth,chain):
    for s in W.callers(target):
        f,xs=window(s)
        tid,seen=eval_edx(xs)
        if tid is None:
            if depth<3 and f not in chain:
                shared[f]=target; walk(f,depth+1,chain+[f])
            continue
        out.setdefault(tid,[]).append(dict(ctor=f,site=s,vt=vt_after(s),chain=chain))
walk(BASE,0,[])
print('ids',len(out)); print('shared',[hex(k) for k in shared])
mult={k:v for k,v in out.items() if len(v)>1}
print('multi',{k:[hex(e['ctor']) for e in v] for k,v in mult.items()})
json.dump(out,open('wtab.json','w'))
print(sorted(out))
