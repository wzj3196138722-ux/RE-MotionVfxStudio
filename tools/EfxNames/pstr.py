import sys,re
from wk import *
def follow(P,f):
    for _ in range(3):
        xs=P.dis(f,1)
        if xs and xs[0].mnemonic=='jmp' and xs[0].op_str.startswith('0x'): f=int(xs[0].op_str,16)
        else: break
    return f
def parser_strings(P,f,limit=3000):
    """String literals referenced in order by a parser function (rip-relative), following one tail jmp."""
    f=follow(P,f)
    out=[]
    for x in P.dis(f,limit):
        t=P.riptarget(x)
        if t is not None:
            c=P.cstr(t)
            if c and (not out or out[-1]!=c): out.append(c)
    return out
