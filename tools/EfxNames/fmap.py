"""Wilds: engine field name -> file offset, via parser (slot 20) -> block, builder (slot 16) block <- attr, loader (slot 5) attr base."""
import sys,re,json,os
sys.path.insert(0,os.path.dirname(os.path.abspath(__file__)))
from wk import *; from pstr import follow
WIDTH={'byte':1,'word':2,'dword':4,'qword':8,'xmmword':16,'ymmword':32}
REGW={'xmm':16,'ymm':32}
MEM=re.compile(r'(byte|word|dword|qword|xmmword|ymmword) ptr \[(r\w+)(?: \+ (0x[0-9a-f]+|\d+))?\]')
def memop(s):
    m=MEM.search(s)
    if not m: return None
    off=m.group(3); off=int(off,16) if off and off.startswith('0x') else int(off or 0)
    return m.group(2),off,WIDTH[m.group(1)]
def first_regs(xs,src):
    for x in xs[:25]:
        m=re.match(r'(r\w+), '+src+'$',x.op_str)
        if x.mnemonic=='mov' and m: return m.group(1)
def builder_map(P,f):
    """block_off -> attr_off (per 4-byte granule)."""
    f=follow(P,f); xs=P.dis(f,600,stop=False)
    dst=first_regs(xs,'rdx') or 'rdx'; src=first_regs(xs,'rcx') or 'rcx'
    regs={}; out={}
    for x in xs:
        if x.mnemonic in('ret','int3'): break
        ops=x.op_str.split(', ')
        if len(ops)==2:
            a,b=ops
            mb=memop(b); ma=memop(a)
            if mb and mb[0]==src and not ma and x.mnemonic.startswith(('mov','vmov','vbroadcast')) :
                regs[a]=(mb[1],mb[2])
            elif ma and ma[0]==dst and not mb and b in regs:
                so,w=regs[b]
                for k in range(0,min(w,ma[2]),4): out[ma[1]+k]=so+k
    return out
def parser_map(P,f,maxn=4000):
    f=follow(P,f); xs=P.dis(f,maxn,stop=False)
    idx={x.address:i for i,x in enumerate(xs)}
    obj=first_regs(xs,'rdx')
    if obj is None: return []
    pairs=[]; pending=None; started=False
    for i,x in enumerate(xs):
        if x.mnemonic=='int3': break
        t=P.riptarget(x)
        if x.mnemonic=='lea' and x.op_str.startswith('rcx,') and t is not None and P.cstr(t):
            c=P.cstr(t); pending=c[2:] if c.startswith('w:') else c; started=True; continue
        if not started: continue
        if x.mnemonic in('je','jne') and pending and x.op_str.startswith('0x'):
            tgt=int(x.op_str,16) if x.mnemonic=='je' else (xs[i+1].address if i+1<len(xs) else None)
            pairs.append((pending,tgt)); pending=None; continue
        if x.mnemonic in('call','test') or (x.mnemonic=='mov' and x.op_str.startswith(('rdx,','rcx,','r8','r8d'))) or x.mnemonic=='lea':
            continue
        break
    res=[]
    for name,tgt in pairs:
        stores=[]; pc=tgt; steps=0; seen=set()
        while pc is not None and steps<400:
            if pc not in idx:
                ys=P.dis(pc,60,stop=False); 
                for y in ys: idx.setdefault(y.address,len(xs)); xs.append(y)
                if pc not in idx: break
            y=xs[idx[pc]]; steps+=1
            if y.address in seen: break
            seen.add(y.address)
            ops=y.op_str.split(', ')
            ma=memop(ops[0]) if ops else None
            if ma and ma[0]==obj and y.mnemonic not in('cmp','test','bt') and len(ops)==2:
                stores.append((ma[1],ma[2] if not ops[1].startswith(('xmm','ymm')) or y.mnemonic in('vmovups','vmovaps','vmovdqu','movups') else ma[2]))
            if y.mnemonic=='jmp':
                if stores: break
                if y.op_str.startswith('0x'): pc=int(y.op_str,16); continue
                break
            if y.mnemonic=='ret': break
            nx=idx.get(pc)+1
            pc=xs[nx].address if nx<len(xs) and xs[nx].address==y.address+y.size else y.address+y.size
        res.append((name,stores))
    return res
def wloader(P,f):
    f=follow(P,f); xs=P.dis(f,40)
    A=B=None
    for x in xs:
        m=re.match(r'(r8|r9), \[rcx \+ (0x[0-9a-f]+)\]',x.op_str)
        if x.mnemonic=='lea' and m:
            if m.group(1)=='r8': A=int(m.group(2),16)
            else: B=int(m.group(2),16)
        if x.mnemonic in('call','jmp') and x.op_str=='0x5fb70' and A is not None and B is not None:
            return A+4,B
    return None
def fieldmap(P,vt):
    sl=lambda k: P.q(vt+8*k)-P.base
    ld=wloader(P,sl(5))
    if not ld: return None
    base,end=ld
    bm=builder_map(P,sl(16))
    pm=parser_map(P,sl(20))
    out=[]
    for name,st in pm:
        offs=[]
        for bo,w in st:
            for k in range(0,max(w,4),4):
                if bo+k in bm: offs.append(bm[bo+k]-base)
        out.append((name,sorted(set(offs)),st))
    return dict(payload=end-base,fields=out,builder=len(bm))
if __name__=='__main__':
    P=get('wilds'); r=fieldmap(P,int(sys.argv[1],16))
    print(r['payload'],r['builder']); [print(' ',f) for f in r['fields']]
