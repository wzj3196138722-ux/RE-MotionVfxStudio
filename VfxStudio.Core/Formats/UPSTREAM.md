# 内嵌 ReeLib 的来源与本地改动

`VfxStudio.Core/Formats`、`VfxStudio.Core/Common` 和 `REE-Lib.Generators` 是
[kagenocookie/RE-Engine-Lib](https://github.com/kagenocookie/RE-Engine-Lib)（MIT）的部分拷贝，命名空间仍为 `ReeLib`。
这份代码由本仓库自己维护，不再作为外部依赖跟随上游。EFX 部分改动后必须跑 `tools/EfxCorpusGate`（见该目录 README）。

## EFX 子树：基线 = 上游 `1c2f92d` + 本地补丁（2026-11-14 定）

范围：`Formats/EFX/*.cs`、`Formats/EfxFile.cs`、`REE-Lib.Generators/ReeLibGenerator.cs`。
`1c2f92d` 之后到 `9fe1901`（2026-11-05），上游没有任何 EFX、源码生成器或 FileHandler 改动。

### 来自 MHWs-EFX-Editor 的补丁（`tools/vendor-patches/0002..0006`，证据见该项目的 README 与 KNOWN_UPSTREAM_ISSUES.md）

| 补丁 | 内容 |
|---|---|
| 0002 | 源码生成器：`[RszFixedSizeArray]` 不带 size、字段又不是 readonly 时，写出端补写元素个数，和读取端对称（影响 `Layout.layoutDataFloats`、`PtCollisionInfluence.Data`） |
| 0003 | `EfxUnknownAttributes.cs`：PtColorMixerClip / FixRandomGeneratorExpression / FluidParticle2DSimulatorExpression 整块按不透明字节原样读写；新增 `EFXAttribute.ExpectedByteSize` |
| 0004 | `TypeStrainRibbonV3`、`FluidParticle2DSimulator` 实现 `IBoneRelationAttribute`（BoneRelations 下标流的消费者） |
| 0005 | 18 个内联宽字符串字段加 `ByteSize = true`（含 `PlayEfx.efxPath`） |
| 0006 | `Attractor` 的字段边界和类型订正 |

### 本仓库自己的改动（B1 之前就有，已叠回）

- `EFXAttributePlayEfx`：MHWilds 下自写读写。写出按字节数；读取时同时接受旧版编辑器写成字符数的长度，
  这样旧的已编辑存档还能读回。其他版本走生成代码（受 0005 的 `ByteSize` 影响，同样按字节数）。
- `EfxFile.UpdateEffectGroups`：保留每个组原有的成员顺序；同名的 EffectGroup 是合法数据，不再合并。
- `EfxFile`：`Version <= 0` 时当作 MHWilds（游戏配置层完成后改为由调用方传入）；构造 attribute 时补上 `this.type = type`。
- `ClipSubstructs`：`IntValue` 的 setter 原来没有赋值。

### 换基线时舍弃的旧改动

- 表达式三件（EfxExpressionParser / ExpressionTree / ExpressionData）原先是 `a96e1d9` 之前的旧版
  （`Unary0..` 命名、运算符编号错误），现在换成上游 `1c2f92d` 的版本。LiveEditor 的 `ExpressionInspector`
  按数字操作码翻译，不依赖枚举名。
- `PtCollisionInfluence` 原先用显式的 `DataCount` 字段绕开生成器问题，现在由 0002 解决，该字段删除（写出字节相同）。

### 换基线的验证（EfxCorpusGate，和原文件逐字节比）

| 语料 | 换基线前 | 换基线后 | 退化 |
|---|---|---|---|
| RE4（3946） | 3512 | 3600 | 0 |
| Wilds（9225） | 6520 | 8942 | 0 |

bonealign：RE4 有 27 个作用域数量对不上、110 处骨骼名不一致（还缺 StretchBlur）；Wilds 是 0 个作用域、2 处不一致。

## 非 EFX 文件

这部分维持现状，不跟随上游。和 `1c2f92d` 比（`diff -w` 的差异行数）：`PfbFile.cs` 303、`RszParser.cs` 33、`MeshFile.cs` 9、
`RszValueType.cs` 9、`ClipFile.cs` 4、`MotlistFile.cs` 3，其余文件和 `1c2f92d` 相同。
这些文件当初基于上游哪个 commit 没有记录。
