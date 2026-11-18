# EfxNames

从 re4.exe 和 MonsterHunterWilds.exe 里取 EFX 属性的引擎字段名，并落到文件偏移上，与 C# 定义逐项对照。结论记在 `docs/development/WILDS-EFX-VS-RE4.zh-CN.md`（§2、§5、§7、§8）和 `docs/development/EFX-UNKNOWN-FIELDS.zh-CN.md`。

## 安全

- 只读语料和 exe。三个 C# 工具都用只读内存流打开语料，写出只写到内存里的 FileHandler。
- 原生运行在 Python 进程里映射 exe，不碰游戏进程，不写游戏目录。每个类型一个子进程，原生崩溃不会连带。
- 改语料条目做实验时，先复制到临时目录再改，不改原文件。

## 准备

- Python 3，装 `capstone`、`pefile`、`numpy`；`emu.py` 另需 `unicorn`。
- 环境变量：
  - `RE4_EXE`：默认 Steam 安装目录下的 re4.exe。
  - `WILDS_EXE`：默认 `E:\Data\Github\RE-MotionVfxStudio\sample\Wilds\MonsterHunterWilds.exe`。
  - `EFXNAMES_OUT`：中间数据目录，默认 `tools/EfxNames/out`（已加进 `.gitignore`）。`wk.py` 的反汇编索引缓存也放在这里。
- 原生运行复用 `tools/Re4Native`（`re4native.py`、`emitter.py`、`factory.py`；Wilds 经 `wn.py` 转到 `mhwildsnative.py`）。

## RE4：解析器名字 → 文件偏移

在本目录运行，产物写到 `out/`：

```powershell
dotnet run -c Debug --project CsDump -- out\cs_now.json            # 当前 C# 字段表（RE4 与 MHWilds）
python -I -X utf8 re4vt.py                                         # 原生跑全部工厂 → 240 个类型的 vtable，读槽 18 解析器 → re4_parsers.json
dotnet run -c Debug --project RawDump -- <RE4 natives\stm> out\raw_re4.json <类型,类型,...> 12   # 每类型最多 12 份不同的语料载荷
python -I -X utf8 r4mark.py <类型> [<类型> ...]                     # 每个类型一个子进程 → r4m_<类型>.json
$env:R4MODE = "attr"; python -I -X utf8 r4mark.py TypeGpuBillboard TypeMesh   # GPU 类型与 TypeMesh
python -I -X utf8 r4join.py                                        # 与 C# 字段逐项对照 → r4join.json，并打印名字不符的字段
```

`r4mark.py` 的两步：

1. **名字 → 块偏移**：对每个名字用两组不同的值原生调用解析器（槽 18，参数 this、块、名字串、值串），两次结果不同的字是值槽；写法相同的字（"已设置"标志等）记为附带写入，不算值槽。
2. **文件偏移 → 块偏移**：载荷的每个 4 字节字依次换成标记值，原生运行加载器（槽 5）和构建器（槽 15），看标记出现在块的哪个位置。块里别处变了、却找不到标记的，记为换算或标志位读取。

GPU 类型的构建器会去注册渲染资源，在这里跑不完。`R4MODE=attr` 时只运行加载器，在加载后的属性对象里找标记，再用静态读出的构建器拷贝关系（`fmap.builder_map`）换算到块偏移。在 DepthOperator 上，两种做法的结果逐字相同。

加载器按载荷里已加载的 Spawn 决定粒子表大小，所以 `r4mark.py` 会先加载一份真实的 Spawn 放进加载上下文；每次运行后把分配器游标退回原位。

## Wilds：解析器名字 → 文件偏移

这组是早先的脚本，按当前目录读写中间文件，第一个参数是本目录。在 `out\` 里运行：

```powershell
cd out
python -I -X utf8 ..\wreg.py ..                 # §2 工厂注册表 → wreg.json
python -I -X utf8 ..\wtab.py ..                 # → wtab.json
python -I -X utf8 ..\ctors.py .. wilds          # 构造函数链 → nodes_wilds.json（re4 同理）
python -I -X utf8 ..\table.py ..                # → wt.json、rt.json
python -I -X utf8 ..\extract.py ..              # 加载器区间、安装偏移、解析器地址与字符串 → ext.json
python -I -X utf8 ..\allmap.py                  # §5 字段名 → 文件偏移 → allmap.json（需要 cs.json、corpus_wilds.json）
python -I -X utf8 ..\diff.py                    # 与 C# 逐项对照 → diff.txt
python -I -X utf8 ..\n8.py .. raw.json <类型> <vtable>   # 构建器原生标记实验 → bm_<类型>.json
```

`allmap.py` 读的 `cs.json` 是 CsDump 的输出（与 `cs_now.json` 同一格式）。`corpus_wilds.json` 是每个类型在 Wilds 语料里各种载荷长度的出现次数，只用于报告；产生它的工具没有保留下来。

FluidSimulator2D 在 RE4 上用原生标记实验核对时，Wilds 静态映射整体偏后一个字。Wilds 侧只有静态映射、没跑原生标记实验的结论，改名前要先核对。

## 未知字段清单

```powershell
python -I -X utf8 unkn_list.py                                     # → out\unkn.json
python -I -X utf8 unkn_md.py ..\..\docs\development\EFX-UNKNOWN-FIELDS.zh-CN.md
```

## 其余脚本

| 脚本 | 作用 |
|---|---|
| `wk.py` | PE 读取、E8/E9 与 rip 相对引用索引（缓存为 npz） |
| `pstr.py` | 跟随尾跳转、按顺序取函数引用的字符串 |
| `fmap.py` | 解析器、构建器、Wilds 加载器的静态读取 |
| `cmp.py` | C# 字段的字节数表；Wilds 载荷长度与 C# 的对照 |
| `loop.py`、`seq.py` | WILDS-EFX-VS-RE4 §6.1 的三遍循环与调用序列 |
| `steps.py` | §6.3 步骤表与所属属性（静态部分） |
| `emu.py` | unicorn 模拟：映射 exe，补 KUSER_SHARED_DATA / PEB / TLS，AVX 单步补丁，int3 函数替换 |
| `e4.py`、`e5.py`、`e7.py` | §2 工厂、§6.3 槽 6 与 0x386e3e0、缺页补零重跑 |
| `n9.py` | ShaderSettings 构建器的定点标记实验 |
| `p20.py` | 内层解析器形式的字段名 → 块偏移 |
| `nl.py` | Wilds：用工厂构造条目的每个属性，原生运行加载器，报告读取字节数与声明长度 |
| `EntryDump/` | 列出语料每个根条目的文件偏移与属性类型 |
