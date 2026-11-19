import sys,json; sys.path.insert(0,sys.argv[1]); from wk import *
W=get('wilds')
ARR=0x14bf2000
start=0xaab6b5b
xs=W.dis(start,4000,stop=False)
reg={}; last=None
for x in xs:
    t=W.riptarget(x)
    if x.mnemonic=='lea' and x.op_str.startswith('rax') and t and W.secname(t)=='.xtls' is False: pass
    if x.mnemonic=='lea' and x.op_str.startswith('rax') and t is not None: last=t
    elif x.mnemonic=='mov' and x.op_str.startswith('qword ptr [rip') and x.op_str.endswith('rax') and t is not None and last is not None:
        if ARR<=t<ARR+0x40*400 and (t-ARR)%0x40==0 and W.secname(last) not in ('.bss',) and not (ARR<=last<ARR+0x40*400):
            reg[(t-ARR)//0x40]=last
    if x.mnemonic in('ret',): break
print('entries',len(reg),min(reg),max(reg), 'end',hex(x.address))
tab=json.load(open('wtab.json')); byctor={}
for k,v in tab.items():
    for e in v: byctor.setdefault(e['ctor'],[]).append(int(k))
out={}
for i,vt in sorted(reg.items()):
    fac=W.q(vt+16)-W.base
    fx=W.dis(fac,8); size=None; ctor=None
    for y in fx:
        if y.mnemonic=='mov' and y.op_str.startswith('ecx, '):
            try: size=int(y.op_str[5:],16)
            except: pass
        if y.mnemonic in('jmp','call') and y.op_str.startswith('0x') and int(y.op_str,16)!=0xb020830: ctor=int(y.op_str,16)
    out[i]=dict(lam=vt,fac=fac,size=size,ctor=ctor)
json.dump(out,open('wreg.json','w'))
# compare type ids
ok=bad=0
for i,e in out.items():
    ids=byctor.get(e['ctor'])
    if ids is None: continue
    if i in ids: ok+=1
    else: bad+=1; print('mismatch idx',i,'ctor',hex(e['ctor']),'exe ids',ids)
print('ctor agree',ok,'disagree',bad)
print([ (i,hex(e['fac']),e['size'],hex(e['ctor'] or 0)) for i,e in list(out.items())[:5]])
