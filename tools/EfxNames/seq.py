import sys,re; sys.path.insert(0,sys.argv[1]); from wk import *
g=sys.argv[2]; f=int(sys.argv[3],16); P=get(g)
xs=P.disf(f)
for x in xs:
    s=None
    if x.mnemonic in('call','jmp') and not x.op_str.startswith('qword ptr [rip'):
        s=f'{x.mnemonic} {x.op_str}'
        if x.op_str.startswith('0x'):
            t=int(x.op_str,16); 
            if P.secname(t)=='.trace' or True:
                h=P.dis(t,1)
                if h and h[0].mnemonic=='jmp': s+=f'  -> {h[0].op_str}'
    elif re.search(r'\[r\w+ \+ 0x(1[0-9a-f]{2}|2[0-9a-f]{2}|3[0-9a-f]{2}|5[0-9a-f]{2})\]',x.op_str) and x.mnemonic in('mov','cmp','test','lea') and 'rsp' not in x.op_str and 'rbp' not in x.op_str:
        s='  {'+x.mnemonic+' '+x.op_str+'}'
    elif x.mnemonic=='ret': s='ret'
    if s: print(f'{x.address:#x} {s}')
