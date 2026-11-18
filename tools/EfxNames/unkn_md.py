"""unkn.json -> Markdown list, grouped by game and type."""
import sys, collections, json, os

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from paths import OUT as H, RE4NATIVE, REPO
rows = json.load(open(os.path.join(H, 'unkn.json'), encoding='utf-8'))
out_path = sys.argv[1]

LABEL = {
    'named': '引擎有名字',
    'candidate': '解析器有名字，偏移没对上',
    're4_parser': 'RE4 解析器有名字，没提取',
    'unmapped': '解析器非空，脚本没走通',
    'not_named': '已对上，引擎不给名字',
    'not_read': '已对上，引擎不给名字',
    'converted': '已对上，引擎不给名字',
    'no_parser': '解析器是空函数',
    'unanalysed': '未分析',
}
ORDER = ['引擎有名字', '解析器有名字，偏移没对上', '解析器非空，脚本没走通',
         '已对上，引擎不给名字', '解析器是空函数', '未分析']
GAME = {'RE4': 'RE4', 'MHWilds': 'Wilds'}


def off(r):
    return '?' if r['off'] is None else f"+{r['off']}"


def fieldtxt(r):
    t = f"`{r['field']}` {off(r)}"
    if not r['unk_name']:
        t += '（名字已知，类型未定）'
    return t


L = ['# EFX 未知字段清单（RE4 / Wilds）', '',
     '由 csdump（当前 C# 定义）与 Wilds / RE4 exe 的解析器、构建器分析结果生成，只读。RE4 的类型按 re4.exe 原生运行全部 240 个工厂取得 vtable，解析器是否为空、有哪些名字都来自 re4.exe 本身；RE4 有语料的 68 个带名字类型已在 re4.exe 上原生做过标记实验（解析器 slot 18 → 块偏移；加载器 slot 5 + 构建器 slot 15 → 文件偏移；GPU 类型和 TypeMesh 用加载后的属性对象加静态构建器映射），这些字段的说明以"re4.exe 原生"开头；其余 RE4 字段与 Wilds 同名同类型时借用 Wilds 的偏移结论（标"偏移由 Wilds 对上"）。偏移是载荷内的文件偏移（不含 UniqueID），'
     '"?" 表示前面有变长字段、偏移没算出来。"语料"是该类型在官方文件里出现的次数。',
     '', '收录条件：字段名含 unk，或类型是 UndeterminedFieldType。', '']

# summary
L += ['## 总览', '', '| 分类 | RE4 | Wilds | 说明 |', '|---|---|---|---|']
desc = {
    '引擎有名字': '解析器给出了名字并且落在这个偏移上，可以直接改名；几个相邻字合成一个名字时说明 C# 拆分有误',
    '解析器有名字，偏移没对上': '类型的解析器里有名字，但没走通到这个偏移；名字就在候选里，需要原生标记实验定位',
    'RE4 解析器有名字，没提取': 'RE4 独有类型，re4.exe 的解析器非空，还没跑映射',
    '解析器非空，脚本没走通': '解析器在 .trace、是内层形式或有条件分支，静态脚本没解析；需要手工或原生实验',
    '已对上，引擎不给名字': '解析器和构建器都走通了，这个字不进解析器（构建器不读、换算或当标志位）',
    '解析器是空函数': '该类型的文本解析器是 `return 0`，引擎里没有名字；只能按行为命名或找其他游戏的 exe',
    '未分析': 'exe 分析结果里没有这个类型',
}
cnt = collections.Counter((r['game'], LABEL[r['cat']]) for r in rows)
for k in ORDER:
    L.append(f"| {k} | {cnt[('RE4', k)]} | {cnt[('MHWilds', k)]} | {desc[k]} |")
L.append(f"| 合计 | {sum(1 for r in rows if r['game'] == 'RE4')} | {sum(1 for r in rows if r['game'] == 'MHWilds')} | |")
L.append('')

# actionable list first
L += ['## 引擎有名字的字段', '', '| 游戏 | 类型 | C# 字段 | 偏移 | 引擎名 | 语料 |', '|---|---|---|---|---|---|']
for r in rows:
    if r['cat'] != 'named':
        continue
    L.append(f"| {GAME[r['game']]} | {r['kind']} | `{r['field']}` | {off(r)} | {' / '.join(r['names'])} | {r['usage']} |")
L.append('')

for game in ('RE4', 'MHWilds'):
    L += [f"## {GAME[game]} 全部", '']
    by = collections.defaultdict(list)
    for r in rows:
        if r['game'] == game:
            by[r['kind']].append(r)
    for kind, rs in sorted(by.items(), key=lambda kv: (-kv[1][0]['usage'], kv[0])):
        L.append(f"### {kind}（id {rs[0]['id']}，语料 {rs[0]['usage']}，{len(rs)} 个）")
        groups = collections.OrderedDict()
        for r in rs:
            key = LABEL[r['cat']] if r['cat'] != 'named' else '引擎有名字'
            groups.setdefault((key, r['note'] if r['cat'] in ('candidate', 're4_parser') else ''), []).append(r)
        for (key, note), gs in sorted(groups.items(), key=lambda kv: ORDER.index(kv[0][0])):
            if key == '引擎有名字':
                txt = '；'.join(f"{fieldtxt(r)} → {' / '.join(r['names'])}" for r in gs)
            else:
                txt = '、'.join(fieldtxt(r) for r in gs)
            extra = ''
            if note:
                extra = '（' + note.split('：', 1)[-1] + '）' if '：' in note else ''
            same = '（同 Wilds）' if game == 'RE4' and gs[0]['note'].startswith('（同 Wilds）') else ''
            L.append(f"- **{key}**{same}{extra}：{txt}")
        L.append('')

open(out_path, 'w', encoding='utf-8', newline='\n').write('\n'.join(L))
print('lines', len(L))
