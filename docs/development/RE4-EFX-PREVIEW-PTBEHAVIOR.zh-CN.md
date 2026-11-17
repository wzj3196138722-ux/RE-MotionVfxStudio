# RE4 离线预览：PtBehavior

PtBehavior 让特效调用游戏的 C# 脚本（RE:2023 讲座：特效里挂一个 PtBehavior，指定脚本类，脚本按参数做径向模糊、点光源等）。文件里只存脚本类名和一组带名字、带类型的参数，效果完全由游戏脚本决定。

## 语料

RE4 全部 3946 个文件里有 2193 个 PtBehavior（933 个文件），每个 entry 最多一个：顶层 NoDraw 1777、Billboard3D 275、Polygon 2，内嵌 entry 139。脚本类 13 个：

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
- 名称、类型、长度、序号、类名放在默认收起的"参数结构（只读）"组里，不能改：参数名和类型由脚本类定义。
- 字符串参数（预制体路径、材质名）可以改：写出时按内容重算长度。字符串占 (长度 + 1) × 2 字节（含结尾的 0），`varSize = 参数名长度 + 17 + size`（数据类型 4 + 数据头 12 + 值 + 参数名 + 0），语料里所有参数都符合。
- EffectDecal 的 DecalPrefab 是下拉框，列出 RE4 贴花用过的 46 个预制体（按使用次数），也可以直接填路径。

## 新建

一个空的 PtBehavior（没有类名、没有参数）游戏用不了，所以新建时总是按 RE4 的写法填好：

- `tools/Re4AttributeCatalog` 生成 `re4-ptbehavior-templates.json`：每个脚本类最常见的参数列表，取值来自与各参数众数最一致的那个实例；最常见的载体 entry 属性列表；EffectDecal 用过的预制体路径。
- 从属性目录添加 PtBehavior（拖到面板、节点图或双击）时先选脚本类，参数按模板填入。
- 文件菜单"新建脚本载体 entry"按 RE4 的常见载体新建：EffectDecal 是 Transform3D、ParentOptions、Spawn、TypeNoDraw、Life、PtBehavior（RE4 里 324 个顶层贴花 entry 是这样），其他属性字段取各类型的默认值。新 entry 成为编辑对象。
- 后台 `add_attribute` / `add_entry` 的属性项可带 `behavior: {class, parameters: [{name, type, value}]}`；MCP 的 `add_attribute` 有 `script`，`add_entry` 可以用 `script` 代替 `type` 新建载体。
- 参数由 `PtBehaviorVariable.Create` 按文本生成（数据头：Float2 和类型 20 的 unkn 为 2，其余为 1，然后是 -1 和参数序号）。把语料里全部 2193 个 PtBehavior 的参数按各自的文本重建，933 个文件写出与原文件逐字节相同。

贴花选的是哪个预制体：预制体里直接写着三张贴图的路径（ALBA 颜色、NRMR 法线、MSK2 遮罩），没有经过材质文件；46 个预制体和贴图都在游戏的 pak 里，预览能读到。预览用近似的方式画贴花，见下一节。

## 贴花近似预览

游戏脚本把贴花投到地形上，预览只有 y = 0 的平地，所以只做最简单的近似：

- 带 EffectDecal 的 NoDraw entry 现在会被模拟（不画粒子），和触发子 Action 的 NoDraw 一样。
- 每个存活的载体粒子，在地面以上 0 ~ SearchTerrainDistance 之间时（允许低于地面 5 cm），在正下方地面上铺一张面片。贴图是预制体的 ALBA，颜色乘 BaseColorMod，透明度再乘粒子的 Life alpha；粒子消失贴花就消失。旋转在 ±RandomRollRange 之间按粒子编号随机。
- 尺寸：宽 = 2 × Extent.X × (ScaleXY.X + u × ScaleXY.Y) × 系数，深用 Extent.Z；Extent 是半边长，系数默认 1。依据是游戏里的对照（2026-11-16）：替换手雷爆炸，贴花 Extent 1,1,1、ScaleXY (1, 0)、白色方块，中心立两张十字交叉的 1 × 1 竖直 Polygon，贴花约为 Polygon 宽度的 2 倍；ScaleXY (1, 0.5) 的贴花比它更大，符合"缩放 + 随机范围"的读法（按宽深缩放读会有一个方向是 0.5）。手雷自带的焦痕 ScaleXY (3, 0) 也只能按这种读法解释。仍可切换成宽深缩放，系数在"合成配置"里调。主面板显示所选贴花 entry 的尺寸。
- 没做：MSK2 遮罩（血迹的形状和扩散多半靠它，所以现在边缘是方的）、法线、自发光、AlphaMaskRange 和 Alpha0/1 的时间变化、ProjectionShape 和投射方向、预制体里组件自己的参数。贴花画在完整分辨率的场景里、粒子之前，不写深度。
- Billboard3D 载体本身读不出贴图来源时（RE4 的贴花载体常常没有 UVSequence，例如手雷爆炸的 [50] デカール、hold_dead_0000 的三个 entry），按逻辑 entry 模拟：粒子不画，贴花照常放。
- MCP `composition` 有 `decals`、`decalScale`、`decalScaleXYRandom`。
- 贴花随载体年龄放大：OBBScalarAdd / OBBScalarCoef 和 ScaleAnim 的规则一样（第 n 步加 add × coefⁿ，加到收敛为止），每个 60 Hz tick 约 2 步（游戏里和同参数 ScaleAnim 的 Polygon 并排对照，2026-11-16）。
- 贴花翻页：EnableUVRect 打开、载体 entry 带 UVSequence 时，贴花只显示载体粒子当前所在的格子，所以序列帧贴图会在地上播放。游戏里验证过（2026-11-16）：Typho 的 kuo 贴花载体改成 TypeNoDraw，挂上 kuo 自己的 UVSequence（tyl_kuo.uvs，6 × 6），预制体用整张序列帧贴图，地上的贴花跟着 kuo 一起翻页。荒野有 447 个贴花载体也是 EnableUVRect + UVSequence。预览读载体的 .uvs，把帧数交给原生模拟（NoDraw 载体同样会播放 UVSequence），按粒子的序列号和帧号取格子。UVOffset / UVScale 没有用上：RE4 里只有两个足迹文件用到它们，载体也没有 UVSequence。

验证：
- `tools/Re4MultiEntryTests` 第 13 节：crushe_0000 的两个 NoDraw 贴花载体被模拟、贴图读到，第 60 帧内地面上最多同时有 6 个贴花，位置和尺寸符合上面的规则。窗口截图（俯视，第 40 帧）看到血迹贴花铺在地面上。
- 第 16 节：Typho 发布版的 kuo 贴花第 1–45 帧都和 kuo 当帧的格子一致，共经过 34 个不同格子。窗口单独显示这个贴花时，地上是 kuo 的单格冲击环，逐帧变大，不是 6 × 6 的整张图。
- 节点图里的行显示脚本类短名（`PtBehavior [EffectRadialBlur]`），悬停显示完整类名和参数列表。
- 顺带修正：`EFXAttribute.ExpectedByteSize`（读取时记录的字节数，不写回文件）以前在所有属性上都显示为可编辑，现在只读。

## 原生模拟器里的 PtBehavior

逐粒子管线在五处调用 3d3a760（击杀检查、逐粒子步骤之后等），发射器事件处调用 3d3a6e0（事件 1、2、4）。两者都先检查上下文 `ctx+0x278`（3d08480 帧里的 `[rbp+0x238]`）非空才调用；函数本身遍历这条链表（`node+0x18` 是下一个），对每个节点调用 3d38470 / 3d37bf0。3d38470 在节点的脚本对象 `node+0x10` 为空时直接返回，否则经托管类型表（`d691e88` 等）读写脚本对象的字段。

模拟器里没有 C# 运行时，脚本实例链表始终为空：在四个带 PtBehavior 的 Billboard3D entry（Decal、RadialBlur、SoundEffectEvent、WaterObstacle）上把两个函数换成记录器，120 帧里一次都没有被调用。这些 entry 的粒子照常模拟，roar_0000、hold_dead_0000、gm91_519_10_0_0000、grenade_explosion_0002 整个文件经预览加载器运行都没有排除或重启。所以 PtBehavior 在预览里没有效果；脚本能做的事（光照、模糊、贴花，或者讲座提到的读取粒子寿命、控制粒子）都不出现。

## 验证

- `tools/Re4MultiEntryTests` 第 12 节：2193 个 PtBehavior 除类型 20 外全部解析，933 个文件写回逐字节相同（只写到内存）；按文本重建全部参数后仍逐字节相同；Decal 的 PtBehavior 只有参数值（含字符串）可编辑；改 Extent.Y、RandomRollRange 和一个更长的 DecalPrefab 路径后写出再读回正确；roar_0000 的节点图行为 `PtBehavior [EffectRadialBlur]`。
- `tools/Re4StructureTests` 第 7 节：按窗口的请求新建贴花载体 entry，把预制体改成 Burnt，保存后读回，属性列表和 10 个参数都与模板一致。
- MCP 端到端：`add_entry` 带 `script` 新建贴花载体并选中。
- 属性面板的新分组、脚本类选择框和预制体下拉框没有在窗口里截图核对。
