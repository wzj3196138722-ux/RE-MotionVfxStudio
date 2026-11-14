# EfxCorpusGate

EFX 格式代码的语料门禁。改动 `VfxStudio.Core/Formats/EFX`、`EfxFile.cs` 或 `REE-Lib.Generators` 之后都要跑。

**判据是写出结果和官方原文件逐字节相同。**“读→写→读→写两次结果相同”和“和旧写入器输出相同”都不算数：
长度前缀写错一半、骨骼关系错位这类问题每次都错得一模一样，那两种判据会照样全绿。

## 命令

```powershell
dotnet build tools/EfxCorpusGate/EfxCorpusGate.csproj -c Release
$gate = "tools/EfxCorpusGate/bin/Release/net8.0/EfxCorpusGate.dll"

# 逐字节往返：每个文件一行 same / diff / read-error / write-error
dotnet $gate roundtrip <语料目录> <输出.tsv> [--limit N]
# 和基线比：有文件从 same 退化、新出现异常或文件缺失，就返回 1
dotnet $gate compare tools/EfxCorpusGate/baselines/re4.tsv <输出.tsv>
# 骨骼关系对齐：消费者数量 == 声明的 BoneRelations 数，且按下标查出的骨骼名 == attribute 内联的骨骼名
dotnet $gate bonealign <语料目录> <输出.json> [--extra 类型名,类型名]
```

`diff` 行的明细：
- `off`：首个差异偏移；
- `at`：该偏移落在哪个 attribute 里。`header-of-X` 指 X 的头部（类型、声明长度、UniqueID），
  通常意味着 X 自己的写出长度变了；`A>B` 表示 PlayEmitter 内嵌 EFX 里的 B；
- `hint=prefix-bytes-written-as-units`：差异处的 u32 恰好是原值的一半，基本可以断定是内联宽字符串
  该按字节数写、却写成了字符数（缺 `ByteSize = true`）。

## 语料（本机路径）

| 游戏 | 目录 | 文件数 |
|---|---|---|
| RE4 | `E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\RE4_EXTRACT\natives\stm` | 3946 |
| Wilds | `E:\Program\Steam\steamapps\common\MonsterHunterWilds\MHWILDS_EXTRACT\natives\STM\Art\VFX` | 9225 |

两份都是 2026-11-14 从 PAK 重新解包的。

## 安全

工具只读语料。写出时用的是 `efx.WriteTo(new FileHandler(stream), saveFile: false)`。ReeLib 的
`BaseFile.WriteTo` 默认 `saveFile = true`，会把结果存回 handler 的 `FilePath`；这个工具的第一版就是这样
把整份 RE4 语料和大半 Wilds 语料覆盖了。`WriteTo` 还会吞掉异常、只返回 false，所以返回值也必须检查。
跑之前想确认的话，可以先 `touch` 一个时间戳文件，跑完用 `find <语料> -newer <时间戳>` 确认没有文件被改。

## 基线（`baselines/`，代码基于 main fadfc0f）

| | 和原文件相同 | 写出不同 | 读取异常 |
|---|---|---|---|
| RE4 | 3512 / 3946 | 434 | 0 |
| Wilds | 6520 / 9225 | 2652 | 53 |

- RE4 写出不同：主要是内联宽字符串长度前缀写成一半（PtCollision 187、PtColliderAction 84、PlayEfx 36、
  VanishArea3D 24、RenderTarget 15、AssignCSV 3，另有 PlayEmitter 内嵌的同类问题），
  另外 attribute 之前的区域 50 个、PlayEmitter 内嵌 EFX 的头部 20 个。
- Wilds 写出不同：Layout 1124（缺 0002 生成器修复），其余以 FlowMap、VanishArea3D、AssignCSV 的长度前缀为主。
- bonealign：RE4 有 52 个作用域数量对不上，骨骼名不一致 1153 处；Wilds 是 218 个和 1158 处。
  RE4 上加 `--extra EFXAttributeStretchBlur,EFXAttributeTypeStrainRibbonV3,EFXAttributeFluidParticle2DSimulator`
  之后两项都降到 0。

**什么时候更新基线**：只有在 `compare` 通过（没有退化）、并且确实有文件新变成 same 时才更新，
提交时写明哪些文件变好了、为什么。不允许为了让门禁通过而更新基线。

## 自测记录

2026-11-14：把 `EFXAttributeTypeMeshV2.MeshPath` 的 `ByteSize = true` 去掉后，`compare` 返回 1——
RE4 有 1417 个文件、Wilds 有 1717 个文件从 same 退化，`at=entry:TypeMesh`，提示为长度前缀减半；
撤销改动后两份都恢复“门禁：通过”。
