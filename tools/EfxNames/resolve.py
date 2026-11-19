import sys,json; sys.path.insert(0,sys.argv[1]); from wk import *
def load(g):
    return {int(k):v for k,v in json.load(open(f'nodes_{g}.json')).items()}
def follow_jmp(P,f):
    for _ in range(4):
        xs=P.dis(f,1)
        if xs and xs[0].mnemonic=='jmp' and xs[0].op_str.startswith('0x'): f=int(xs[0].op_str,16)
        else: break
    return f
def resolve(nodes,f):
    tid=vt=None; seen=0
    while f in nodes and seen<8:
        n=nodes[f]
        if vt is None and n['vt']: vt=n['vt']
        if tid is None and n['id'] is not None: tid=n['id']
        f=n['parent']; seen+=1
    return tid,vt
