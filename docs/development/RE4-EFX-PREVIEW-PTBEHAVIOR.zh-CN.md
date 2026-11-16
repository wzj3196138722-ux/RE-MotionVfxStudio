# RE4 离线预览：PtBehavior

PtBehavior 让特效调用游戏的 C# 脚本（RE:2023 讲座：特效里挂一个 PtBehavior，指定脚本类，脚本按参数做径向模糊、点光源等）。文件里只存脚本类名和一组带名字、带类型的参数，效果完全由游戏脚本决定。

## 语料

RE4 全部 3946 个文件里有 2193 个 PtBehavior（933 个文件），每个 entry 最多一个：顶层 NoDraw 1777、Billboard3D 275、Polygon 2，内嵌 entry 139。脚本类 14 个：

| 脚本类 | 次数 | 参数（主要） |
|---|---|---|
| via.effect.script.EffectDecal | 1008 | DecalPrefab、DecalDirection、Extent、ScaleXY、BaseColorMod、EmissiveMod、AlphaMaskRange |
| via.effect.script.EffectRecord | 404 | MarkSystemkAssetID、MarkOrderDataID、SearchDistance、Extent |
| chainsaw.EffectLight1500lm | 390 | Intensity、Radius、ReferenceEffectiveRange、阴影、IntensityAmplitude* |
| via.effect.script.EffectLight1500lm | 147 | 同上 |
| via.effect.script.EffectRadialBlur | 92 | BlurPower、LookAtType、OccludeScale、FadeStart/EndDistance |
| via.effect.script.EffectLensFlare | 41 | LensflarePrefab、Intensity、遮挡与淡出 |
| chainsaw.EffectShakeCameraCustom | 27 | 距离/角度衰减、TranslationX/YRange |
| EffectWaterObstacle / EffectFluid2DObstacle / EffectFluid2DPressure | 27 / 21 / 5 | Radius、Length、MovingPower / PowerScale / Pressure |
| SoundEffectEvent | 20 | TriggerId（音效事件哈希）、MaxParticleSounds |
| EffectMaterialController | 10 | MaterialName、VariableName、StartRate、EndRate |
| EffectSpotLight | 1 | Intensity、Radius、Cone、Spread、Falloff |

（这张表前一版是 2065 个，漏数了一部分内嵌 entry。）

## 参数编码

每个参数：`varSize`、`dataType`、数据头（`unkn`、`size`、两个 short，第二个是参数序号）、`size` 字节的值、参数名。`varSize = 名字长度 + 17 + size`。按语料确认的类型：

| dataType | 长度 | 内容 |
|---|---|---|
| 4 Uint | 4 | 无符号整数（TriggerId 是哈希） |
| 9 Float / 19 Float2 / 11 Float3 | 4 / 8 / 12 | 浮点 |
| 10 Range | 8 | 两个浮点 min、max（EffectDecal Alpha0 = 0 ~ 0.1） |
| 14 Int / 18 Enum | 4 | 整数 |
| 15 Color | 4 | RGBA8 |
| 16 WstringName / 17 Prefabpath / 21 Wstring2 | 可变 | UTF-16 字符串 |
| 3、22 | 4 | 整数（RadialBlur Priority = 1；LensFlare AxisType、RotationOrder，像枚举） |
| 20 | 8 | 只有 ShakeCamera Period 一处（1, 0），保留原始字节 |

库里新增了 Uint、Range 的解析，Wstring2 用字符串类，3 和 22 按 Uint 读。全部 933 个文件在内存里写回与原文件逐字节相同。

## 编辑

- 属性面板按参数名列出参数值（"脚本参数"组，按脚本里的参数顺序），X/Y/Z、min/max 并成一行，颜色用取色器。
- 名称、类型、长度、序号、类名放在默认收起的"参数结构（只读）"组里，不能改：参数名和类型由脚本类定义，`varSize`、`size` 是存储值，不会自动重算。
- 字符串参数（预制体路径、材质名）暂不能改：改长度要同时更新 `size` 和 `varSize`。
- 节点图里的行显示脚本类短名（`PtBehavior [EffectRadialBlur]`），悬停显示完整类名和参数列表。
- 顺带修正：`EFXAttribute.ExpectedByteSize`（读取时记录的字节数，不写回文件）以前在所有属性上都显示为可编辑，现在只读。

## 原生模拟器里的 PtBehavior

逐粒子管线在五处调用 3d3a760（击杀检查、逐粒子步骤之后等），发射器事件处调用 3d3a6e0（事件 1、2、4）。两者都先检查上下文 `ctx+0x278`（3d08480 帧里的 `[rbp+0x238]`）非空才调用；函数本身遍历这条链表（`node+0x18` 是下一个），对每个节点调用 3d38470 / 3d37bf0。3d38470 在节点的脚本对象 `node+0x10` 为空时直接返回，否则经托管类型表（`d691e88` 等）读写脚本对象的字段。

模拟器里没有 C# 运行时，脚本实例链表始终为空：在四个带 PtBehavior 的 Billboard3D entry（Decal、RadialBlur、SoundEffectEvent、WaterObstacle）上把两个函数换成记录器，120 帧里一次都没有被调用。这些 entry 的粒子照常模拟，roar_0000、hold_dead_0000、gm91_519_10_0_0000、grenade_explosion_0002 整个文件经预览加载器运行都没有排除或重启。所以 PtBehavior 在预览里没有效果；脚本能做的事（光照、模糊、贴花，或者讲座提到的读取粒子寿命、控制粒子）都不出现。

## 验证

`tools/Re4MultiEntryTests` 第 12 节：2193 个 PtBehavior 除类型 20 外全部解析，933 个文件写回逐字节相同（只写到内存）；Decal 的 PtBehavior 只有 20 个参数值可编辑；改 Extent.Y 和 RandomRollRange 后写出再读回值正确；roar_0000 的节点图行为 `PtBehavior [EffectRadialBlur]`。通用编辑、结构编辑 12/12、MCP 端到端 65/65 通过。属性面板的新分组没有在窗口里截图核对。
