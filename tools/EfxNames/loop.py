import sys,re,collections; sys.path.insert(0,sys.argv[1]); from wk import *
W=get('wilds')
hits=collections.defaultdict(set)
for n,va,vs,ro,rs,ch in W.execsecs():
    seg=W.d[ro:ro+rs]
    for s in (0xc8,0xd0,0xd8):
        for m in re.finditer(rb'(?:\x41)?\xff[\x90-\x97\x50-\x57]'+(bytes([s,0,0,0]) if True else b''),seg):
            r=va+m.start(); f=W.fstart(r)
            if f is not None: hits[f].add(s)
full=[f for f,ss in hits.items() if ss>={0xc8,0xd0,0xd8}]
print('candidates',len(full),[hex(f) for f in sorted(full)][:40])
