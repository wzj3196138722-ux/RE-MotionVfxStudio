"""List every unknown-named EFX field for RE4 and MHWilds and say what the exes can tell about it.

Inputs (this folder): ../cs_now.json (C# fields from csdump), ext.json (vtable, loader, parser, parser strings),
allmap.json (Wilds parser name -> file offset), bm_*.json (Wilds builder marker runs), corpus_wilds.json,
../catalog/re4-attribute-usage.json. Output: unkn.json and unkn.md.
"""
import sys, collections, glob, json, os, re

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from paths import OUT as H, RE4NATIVE, REPO
cs = json.load(open(os.path.join(H, 'cs_now.json')))
ext = json.load(open(os.path.join(H, 'ext.json')))
amap = json.load(open(os.path.join(H, 'allmap.json')))
EMPTY = {'wilds': 189198864, 're4': 715376}  # xor eax,eax / xor al,al; ret
r4p = json.load(open(os.path.join(H, 're4_parsers.json')))
# re4.exe native marker results (r4mark.py + r4join.py)
r4j = json.load(open(os.path.join(H, 'r4join.json'), encoding='utf-8'))
R4STATUS = {
    'named': ('named', 're4.exe 原生标记实验定位到名字'),
    'copied_unnamed': ('not_named', 're4.exe 原生：构建器拷贝进块，但解析器没有这个位置的名字'),
    'converted': ('not_named', 're4.exe 原生：构建器换算或当标志位读，解析器没有对应名字'),
    'not_read': ('not_named', 're4.exe 原生：语料实例里构建器不读这个字'),
    'length': ('not_named', 're4.exe 原生：改动会改变读取长度（计数或长度字段）'),
}

SZ = dict(Single=4, ExpressionAssignType=4, UInt32=4, CenterSpread=8, Color=4, Range=8, UndeterminedFieldType=4,
          Vector3=12, Int32=4, Boolean=1, MinExtraI=8, Byte=1, Vector2=8, RangeI=8, RotationOrder=4, MinMaxI=8,
          MinMax=8, InnerOuter=8, AxisXYZ=4, LifeType=4, MinMaxExclusiveI=8, StartSpan=8, Int3=12, MinExtra=8,
          CenterSpreadI=8, EmitterColorOperator=4, PlayType=4, PlayOrder=4, AnchorPair=8, AxisType=4,
          PtColorOperator=4, Int16=2, Int4=16, SortType=4, Repeat=4, VelocityType=4, ClampRange=8, Shape2DType=4,
          Shape3DType=4, RotationCorrectType=4, DistortionType=4, PtLifeStatus=4, LuminanceBleedType=4,
          LuminanceBleedSamplingType=4, Finish=4, Vector4=16, VelocityShapeType=4, EffectBoundsType=4,
          NodeBillboardType=4, VelocityEmitType=4, VelocityAttenuationType=4, SolidBodyShapeType=4,
          TerrainSnapType=4, ByteSet=4)
UNK = re.compile(r'unk', re.I)

# corpus usage
use = {'RE4': collections.Counter(), 'MHWilds': collections.Counter()}
u4 = json.load(open(os.path.join(REPO, 'VfxStudio.Re4Render', 'Data', 're4-attribute-usage.json')))
for o in u4['owners'].values():
    for k, v in o.get('attributes', {}).items():
        use['RE4'][k] += v
for k, v in json.load(open(os.path.join(H, 'corpus_wilds.json'))).items():
    use['MHWilds'][k] = sum(v.values())

bm = {}
for p in glob.glob(os.path.join(H, 'bm_*.json')):
    d = json.load(open(p))
    bm[d['name']] = d


def offsets(fields):
    off, out = 0, []
    for n, t in fields:
        out.append(off)
        if off is not None:
            off = off + SZ[t] if t in SZ else None
    return out


def wilds_info(tid, kind, off, width):
    """What the Wilds exe says about file bytes [off, off+width) of type tid."""
    e = ext['wilds'].get(str(tid))
    if e is None:
        return 'unanalysed', 'Wilds 未分析该类型', None
    if e.get('parser') == EMPTY['wilds'] and kind not in bm:
        return 'no_parser', 'Wilds 解析器是空函数，没有名字', None
    if kind in bm and off is not None:
        b = bm[kind]
        hits, flag = set(), False
        for k in range(off - off % 4, off + width, 4):
            w = b['words'].get(str(k))
            if not w:
                continue
            flag |= bool(w['flag'])
            for blk in w['blocks']:
                hits.update(b['parser'].get(blk, []))
        if hits:
            return 'named', '引擎名：' + ' / '.join(sorted(hits)), sorted(hits)
        if flag:
            return 'converted', '构建器换算或当标志位读，解析器没有对应名字', None
        if e.get('parser') == EMPTY['wilds']:
            return 'no_parser', 'Wilds 解析器是空函数，没有名字', None
        return 'not_read', '构建器不读这个字，解析器里没有名字', None
    fm = (amap.get(str(tid)) or {}).get('fm') or {}
    if fm.get('fields') and off is not None:
        names = [n for n, offs, st in fm['fields'] if any(off <= o < off + width for o in offs)]
        if names:
            return 'named', '引擎名：' + ' / '.join(names), names
        unmapped = [n for n, offs, st in fm['fields'] if not offs]
        if unmapped:
            return 'candidate', '解析器有未定位的名字：' + '、'.join(unmapped), unmapped
        return 'not_named', '解析器已对上，这里没有名字', None
    if e.get('strs'):
        return 'candidate', '解析器有名字但没定位：' + '、'.join(e['strs']), e['strs']
    return 'unmapped', 'Wilds 解析器非空，但没有自动走通（.trace、内层解析器或条件分支）', None


rows = []
wk = {x['kind']: x for x in cs['MHWilds']}
for game in ('RE4', 'MHWilds'):
    for x in cs[game]:
        fields = x['fields'] or []
        if x['err'] or not fields:
            continue
        offs = offsets(fields)
        for (n, t), off in zip(fields, offs):
            if not (UNK.search(n) or t == 'UndeterminedFieldType'):
                continue
            width = SZ.get(t, 4)
            r = dict(game=game, id=x['id'], kind=x['kind'], cls=x['cls'], field=n, type=t, off=off,
                     unk_name=bool(UNK.search(n)), usage=use[game].get(x['kind'], 0))
            if game == 'MHWilds':
                r['cat'], r['note'], r['names'] = wilds_info(x['id'], x['kind'], off, width)
            else:
                # re4.exe's own parser (re4_parsers.json, ids = file type ids, factories run natively)
                p4 = r4p.get(str(x['id']))
                w = wk.get(x['kind'])
                same = w and [n, t] in [list(f) for f in w['fields']]
                wres = None
                if same:
                    woffs = offsets(w['fields'])
                    woff = woffs[[list(f) for f in w['fields']].index([n, t])]
                    wres = wilds_info(w['id'], x['kind'], woff, width)
                j4 = next((x4 for x4 in r4j.get(x['kind'], {}).get('rows', []) if x4['field'] == n), None)
                if j4 and j4.get('status') in R4STATUS:
                    cat, note = R4STATUS[j4['status']]
                    r['cat'], r['note'], r['names'] = cat, note + (('：' + ' / '.join(j4['names'])) if j4.get('names') else ''), j4.get('names')
                elif p4 is None:
                    r['cat'], r['note'], r['names'] = 'unanalysed', 're4.exe 里没有这个类型的工厂', None
                elif p4['empty']:
                    r['cat'], r['note'], r['names'] = 'no_parser', 'RE4 解析器是空函数，没有名字', None
                elif wres and wres[0] in ('named', 'not_named', 'not_read', 'converted'):
                    r['cat'], r['note'], r['names'] = wres
                    r['note'] = '（偏移由 Wilds 对上）' + r['note']
                elif p4['strs']:
                    r['cat'], r['note'], r['names'] = ('candidate', 'RE4 解析器有名字，偏移没对上：' +
                                                       '、'.join(p4['strs']), p4['strs'])
                else:
                    r['cat'], r['note'], r['names'] = 'unmapped', 'RE4 解析器非空，但没提出名字（内层解析器等形式）', None
            rows.append(r)

json.dump(rows, open(os.path.join(H, 'unkn.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
c = collections.Counter((r['game'], r['cat']) for r in rows)
for k, v in sorted(c.items()):
    print(k, v)
print('total', len(rows), 'types', len({(r['game'], r['kind']) for r in rows}))
