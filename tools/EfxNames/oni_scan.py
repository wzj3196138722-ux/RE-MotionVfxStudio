"""Onimusha: Way of the Sword (OnimushaWotS.exe, EFX version OniWS): attribute vtables and their text-parser names,
matched to Wilds / RE4 types by name sets. Static, reads only.

    python -I -X utf8 oni_scan.py        -> out/oni_parsers.json, summary on stdout

Attribute vtables (28 slots; text parser in slot 20 as in Wilds) are found through slot 13, which every
attribute type shares (0x4c077b0 in the current build), checked against slot 1. Parsers that are the shared
`xor eax, eax; ret` have no names. Names are read from the parser and the functions it calls, up to int3
padding (inner parsers return after each name).
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from paths import OUT  # noqa: E402
import wk  # noqa: E402
from pstr import follow  # noqa: E402

SHARED_SLOT13 = 0x4C077B0
SHARED_SLOT1 = 0x47E44F0


def strings_full(P, f, limit=6000):
    if P.rva2off(f) is None:      # call target outside the file image
        return []
    f = follow(P, f)
    if P.rva2off(f) is None:
        return []
    out = []
    for x in P.dis(f, limit, stop=False):
        if x.mnemonic == 'int3':
            break
        t = P.riptarget(x)
        if t is not None:
            c = P.cstr(t)
            if c and c not in out:
                out.append(c)
    return out


def parser_names(P, f):
    if P.rva2off(f) is None:
        return []
    calls = [int(x.op_str, 16) for x in P.dis(follow(P, f), 400, stop=False)
             if x.mnemonic in ('call', 'jmp') and x.op_str.startswith('0x')]
    names = []
    for c in [f] + list(dict.fromkeys(calls)):
        for s in strings_full(P, c):
            if s not in names:
                names.append(s)
    return names


def is_empty(P, f):
    if P.rva2off(f) is None:
        return False
    xs = P.dis(follow(P, f), 2)
    return len(xs) == 2 and xs[0].mnemonic == 'xor' and xs[1].mnemonic == 'ret'


def main():
    O = wk.get('oni')
    vts = [q - 8 * 13 for q in O.qrefs(SHARED_SLOT13)]
    vts = [v for v in vts if O.q(v + 8) - O.base == SHARED_SLOT1]
    oni = {}
    for v in vts:
        p = O.q(v + 8 * 20) - O.base
        oni[hex(v)] = dict(parser=hex(p), empty=is_empty(O, p), strs=[] if is_empty(O, p) else parser_names(O, p))
    # reference name sets: Wilds (out/ext.json parser addresses) and RE4 (out/re4_parsers.json)
    cs = json.load(open(os.path.join(OUT, 'cs_now.json')))
    ref = {}
    W = wk.get('wilds')
    wk_kind = {x['id']: x['kind'] for x in cs['MHWilds']}
    for k, v in json.load(open(os.path.join(OUT, 'ext.json')))['wilds'].items():
        if v.get('parser') and not is_empty(W, v['parser']):
            ref.setdefault(wk_kind.get(int(k), f'wilds#{k}'), set()).update(parser_names(W, v['parser']))
    for k, v in json.load(open(os.path.join(OUT, 're4_parsers.json'))).items():
        if v.get('kind') and v.get('strs_all'):
            ref.setdefault(v['kind'], set()).update(v['strs_all'])
    for v in oni.values():
        s = set(v['strs'])
        if not s:
            continue
        best = max(ref.items(), key=lambda kv: (len(s & kv[1]) / len(s | kv[1]), len(s & kv[1])))
        v['kind'] = best[0]
        v['jaccard'] = round(len(s & best[1]) / len(s | best[1]), 3)
        v['new'] = sorted(s - best[1])
        v['missing'] = sorted(best[1] - s)
    json.dump(oni, open(os.path.join(OUT, 'oni_parsers.json'), 'w'), indent=0)
    named = [v for v in oni.values() if v['strs']]
    print('vtables', len(oni), 'empty parser', sum(v['empty'] for v in oni.values()), 'named', len(named))
    for v in sorted(named, key=lambda v: (-len(v['new']), v.get('kind', ''))):
        if v['new'] or v['jaccard'] < 0.6:
            print(f"{v.get('kind')} J={v['jaccard']} new={v['new'][:14]} missing={v['missing'][:8]}")


if __name__ == '__main__':
    main()
