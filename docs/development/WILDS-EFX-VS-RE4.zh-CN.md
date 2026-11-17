# MH Wilds 与 RE4R 的 EFX CPU 代码差异

适用：MonsterHunterWilds.exe（546,013,600 字节，PE 时间戳 0x6ACECA63）对照 re4.exe（EFX 3539837，与 `tools/Re4Native` 同一补丁）。RVA 只对这两个版本有效。
读者：修改 `VfxStudio.Core/Formats/EFX/` 的 Wilds 版本字段，或要把 RE4 运行时规格（`RE4-EFX-RUNTIME.zh-CN.md`，下称 RT）套到 Wilds 上的人。
本文以 RE4 为基准，只记差异；没有写到的部分按 RT 理解。GPU 粒子类型和离线预览不在范围内。

状态标记与 RT §0.1 相同（已确认 / 部分待确认 / 折中实现 / 待清理）。全部结论来自静态反汇编（pefile + capstone）和 Wilds 语料（`MHWILDS_EXTRACT/natives/STM/Art/VFX`，9225 个文件）只读统计，没有运行 Wilds 代码。

## 1. 读 Wilds exe 的前提

| 项 | 内容 | 状态 |
|---|---|---|
| 节 | 节名被混淆；代码在 `.xtls`（可执行）和 `.trace`（RWX）。`.pdata` 只覆盖 `.xtls` 41 万个函数和 `.trace` 1.2 万个，许多小函数要靠 int3 填充找起点 | 已确认 |
| 常量隐藏 | `.trace` 里的立即数写成"全局变量 ± 立即数"，如类型 id `mov edx,[rip+G]; sub edx,0xa02f`。G 的值在文件里，静态即可求出 | 已确认 |
| 混淆构造函数 | 11 个类型（24、31、62、125、157、207、239、251、261、278、280）的构造函数在 `.trace` 里做了控制流混淆（flags 分支、跳转寄存器），静态没有解出它们的 vtable | 部分待确认 |

## 2. 类型表的来源

RE4 靠"基类构造函数 3d0e910 的调用者 + edx 立即数"建表（`factory.py`）。Wilds 有一张更直接的表：

- **工厂注册表**：静态初始化函数 0xaab6b5b..0xaab8b72 把 282 个 `std::function` 依次写进全局数组 0x14bf2000（每项 0x40 字节，`+0x38` 是实现指针），**数组下标 = 文件里的类型 id**（0..281，与 C# `EfxAttributeTypes.cs` 的 MHWilds 编号一致）。每个 lambda 的调用槽是 `mov ecx, 对象大小; call 0xb020830（分配）; jmp 构造函数`。已确认。
- **基类构造函数** 0x38970，`edx` = 类型 id，写到对象 +8。共享构造函数（多个类型共用一个类）由外层按 `edx` 传入 id，`*MaterialExpression` 还多传 `r8d` = 所属 Type 的 id（如 29 → 27）。由下标得到的 id 与构造函数里的立即数 id 有 131 项可以互相核对，只有一处不一致：下标 161（PtTransform3DExpression）的构造函数 0xb6dafc0 写入的是 204。RE4 也有同类现象（类型 18 有两个工厂，RT 依据见 `factory.py` 的 `PREFERRED_FACTORY`）。部分待确认。
- vtable：268 个类型解出（257 个经构造函数链，11 个直接读构造函数体）。完整表见附录。

## 3. vtable 布局

Wilds vtable 比首轮笔记假定的起点早 0x10（多出两个槽），共 32 个槽。以 ScaleAnim（RE4 7a0b508 ↔ Wilds e0fb120）逐槽对照：

| RE4 槽 | Wilds 槽 | 内容 | 状态 |
|---|---|---|---|
| 0–4 | 0–4 | 析构（Wilds 跳进 `.trace`）、若干空函数 | 已确认 |
| 5 加载器 | 5 加载器 | 读法改变，见 §4 | 已确认 |
| 6 块大小 | 6 | Wilds 改成**注册逐粒子步骤**（§6.3）；RE4 的块大小计算在 Wilds 中的位置未定 | 部分待确认 |
| 7–12 常量 getter | 8–13 | 值大多相同；ScaleAnim 块大小 0x40 → 0x50（多了 SizeDelayFrame）、RotateAnim / UVScroll 第三个值不同 | 已确认 |
| 15 块构建 | 16 块构建 | 逐字段 `[attr+X] → [block+Y]` 拷贝；ScaleAnim 块内顺序与 RE4 相同 | 已确认 |
| 18 文本解析器 | 20 文本解析器 | strcmp 链，字段名字符串与 RE4 基本相同，部分改存 UTF-16；写的是运行时块，不是 attribute 对象 | 已确认 |
| 19–20 | 21–24 | Wilds 新增：21 = `[this+0x30] != 0`，22–24 = `this+0x28` 上的三个转发 | 部分待确认 |
| 22 / 23 / 24 三遍 | 25 / 26 / 27 | 三遍属性遍历；26 仍是 `mov [rdx+X], r8; ret` 形式的安装函数 | 已确认 |

安装偏移（ctx 上的块指针位置）到 0x210 为止与 RE4 相同（Life 0x1e0、RotateAnim 0x1e8、ScaleAnim 0x1f0、UVScroll 0x200、AlphaCorrection 0x210），之后的块整体后移 0x10（PtColor 0x380 → 0x390，PtUvSequence 0x3b0 → 0x3c0）。已确认。

## 4. 加载器与文件长度

RE4 每个类型的加载器自带 `mov r8d, 长度`，读进栈再逐字段搬进对象。Wilds 的定长加载器统一成：

```
lea r8, [this + A]
lea r9, [this + B]
call/jmp 0x5fb70
```

0x5fb70 先读 4 字节 UniqueID 到 `this+0x20`，再读 `B − (A+4)` 字节到 `this+A+4`；`[this+A]` 保留构造函数写的 0xdeadbeaf。所以**文件载荷长度（不含 UniqueID）= B − A − 4**，载荷按文件顺序原样放在对象里，`文件偏移 = 对象偏移 − (A+4)`。已确认。

- 有的加载器在区间读取后再用 0x1b66f0 / 0x1a4f50 读一个尾随值：PtLife 的 ActionIndex、VectorFieldParameter 的 FieldIndex、PtLightningBranchAction 的下标是 4 字节下标，PtProjection 是字符串。附录"载荷"列标为 `+ R`。
- 带字符串、列表或材质参数（0x387cbf0）的类型是变长的，区间只给出定长部分。
- 校验：区间读取、没有尾随读取、在语料中出现过的 66 个类型里，64 个的 `B − A − 4` 与语料里每个实例的声明长度减 4 完全相等；另外两个（TypeBillboard3DMaterial、TypePolygonMaterial）后面跟材质读取器。Wilds 文件每个 attribute 自带声明长度、ReeLib 读取时逐个校验，所以语料覆盖到的类型长度本来就已被证实；exe 区间主要用于语料里没有的类型。

## 5. 字段名的取法

`字段名 → 块偏移`（槽 20 解析器）+ `块偏移 ← 对象偏移`（槽 16 构建器）+ `对象偏移 → 文件偏移`（§4）三步组合，得到引擎字段名在文件中的位置。能完整走通的有 63 个类型；解析器或构建器在 `.trace` 里、或构建器有条件分支（ShaderSettings、Spawn、EmitterShape3D、AngularVelocity2D/3D 部分字段）的类型没有自动走通，不能直接用于改名。

解析器有两个现象，含义未定：

- 同一个槽有多个名字，按标志位或场类型切换：ShaderSettings 的 SoftParticleDistance / …WithStochastic / …WithVolumetricParticle，ProceduralDistortion 的 Wave* / Vertical* / Horizontal*，VectorFieldParameter 的 Parameter 三分量（Frequency / Range、Amplitude / Detail、SmoothBlend），AlphaCorrection 的 HiPass / Max / LifePow0。
- ScaleAnim 解析一对值后用 `vshufps 0xe1` 交换两个 float 再写入块。

## 6. 发射器循环与逐粒子管线

### 6.1 三遍属性遍历

| 项 | RE4 | Wilds | 状态 |
|---|---|---|---|
| 3D 路径循环 | 3d08480 | 0xaaba8e0 | 已确认 |
| 另一路循环 | 3d079c0（2D，只有两遍） | 0xaabba90，同样三遍 | 部分待确认（按结构推定为 2D） |
| 属性数组 | `E+0x288` | `E+0x2c8` | 已确认 |
| 三遍调用 | vtable +0xb0 / +0xb8 / +0xc0 | +0xc8 / +0xd0 / +0xd8 | 已确认 |

首轮笔记按 RE4 字节模式没搜到，原因是数组偏移变了，且下标寄存器换成 r15（`mov r8,[rax+r15*8+0x2c8]`），指令编码不同。

### 6.2 粒子槽

- 槽跨度 0x140 → **0x150**；粒子编号仍写在 `p+0x18`，来自发射器计数器自增。已确认。
- 认领时同样 `[p] |= 3`（alive | new），出生阶段结束后 `[p] = ([p] & 0xeffffffc) | 0x10000000`。已确认。

### 6.3 步骤列表取代固定调用

这是与 RE4 唯一的结构性差别。RE4 的出生前置步骤（RT §1.3 第 5 步）和逐粒子管线 3d05350（RT §1.4）是写死的直接调用；Wilds 改成数据驱动：

- 每个属性的 vtable 槽 6 在首次调用时（函数级静态初始化）把自己的步骤函数写进全局步骤表 0x14bf66e8（`.didata`）的第 k 项，再把 k 追加到发射器的步骤列表（u32 数组 `[X+0x50]`，个数 `[X+0x58]`）。已确认。
- 粒子循环按号段分阶段遍历该列表，遇到超出本段上限的号就结束本段，所以列表应是升序；排序代码没有找到。部分待确认。
- 3D 路径用 0..116，2D 路径用 117..199（同一函数，编号差 111）。已确认。

3D 号段与 RT 的对应：

| 号段 | 循环位置 | 对应 RE4 | 步骤（号 = 所属属性；? = 由共享函数 0x386e3e0 等注册，按读的 ctx 偏移推定） |
|---|---|---|---|
| 0–4 | 每槽先执行（`[p+4]` 位 10 置位时；RE4 击杀检查看位 9） | 击杀检查 | 2、3 PtLife，4 PtBehavior |
| 5–9 | 认领之后 | 其他出生源 | 5 AssignCSV，7 ?（读 ctx+0x1c0 / 0x208，与 40 同一函数），8 Attractor，9 PtProjection |
| 0xa–0x15 | 新粒子 | 出生前置 | 11 PointCloudEmitter，13 / 15 PtFreezer，**14 ?（读 Life 块 ctx+0x1e0，即 Life 出生）**，16 ParentOptions，17 TexelChannelOperator，18 AssignCSV，20 PointCloudEmitter，21 ShaderSettings；6 / 12 / 19 未认出 |
| 0x17–0x6e | 存活粒子 | 3d05350 | 24 Trigger，26 ?（Life 逐帧），27 Attractor，28 VanishArea3D，29–31 PtTransform3D / Clip / Expression，32 AngularVelocity3D，33 PtAngularVelocity3D，34 PtPathTranslate，35 Layout，36 Noise，**38 RotateAnim，39 ScaleAnim，40 ?（Velocity）**，41–42 PtVelocity3D / Clip，43 PtVortexelWind，45 WindInfluence3D，47–48 ?（读 ctx+0x1c8 与变换，推定积分），49 PtColliderAction，50 PtProjection，51 PtCollision，52 PtCollisionInfluence，**53–86 各 Type***，87–88 RibbonJoint*，89 PtLightningBranchAction，91 Blink，92 UVSequence，93 UVSequenceModifier，94–95 PtUvSequence / Clip，96 UVScroll，97 TextureUnit，98 AlphaCorrection，99 FluidSimulator2D，101 FlowMap，102 RgbCommon，103 RgbWater，104 ParticleTimer，105–107 PtColor / Clip / Mixer，108 DestinationCSV，109 RayTracingTarget |
| 0x6f–0x74 | 类型不是 0x22 / 0x23 时 | 类型后处理 | 110 PtBehavior，112 Billboard3D，113 Polygon，114 Mesh，**115 ScaleAnim（基值捕获）**，116 Trigger |

与 RE4 顺序的差别：

- **Type 出生抽样在碰撞之后**。RE4 默认（`[g+0xeb0] == 0`）把类型分派放在 Noise 之后、RotateAnim 之前，`!= 0` 时才移到碰撞之后（RT §1.4 第 6、16 步）。Wilds 的 Type 步骤（53–86）在 PtCollision（51）之后、Blink（91）之前，相当于 RE4 的 `!= 0` 分支，且没有看到另一种排列。因此 Wilds 中 RotateAnim / ScaleAnim / Velocity 的出生抽样先于 Type 的颜色、尺寸抽样，随机数抽取顺序与 RE4 默认路径不同。ScaleAnim 基值捕获仍在后处理（115），所以基值仍是 Type 抽样后的值。部分待确认（步骤 40 = Velocity 是按读的 ctx 偏移推定）。
- 新增的步骤属于 RE4 没有的属性：PtFreezer、PointCloudEmitter、Trigger、PtVortexelWind、RibbonJoint*、PtLightningBranchAction、FlowMap、ParticleTimer、DestinationCSV、RayTracingTarget、TypeLightning3D 等。
- 后处理段跳过 0x22 / 0x23 类型，与 RE4 相同。

## 7. 对 C# 读取器的修改

已提交（字节不变；RE4 3946/3946、Wilds 9179/9225 与基线相同；Re4MultiEntryTests 通过）：

| 类型 | 改动 |
|---|---|
| Velocity2D | Angle、Speed、SpeedCoef、GravityRate 改为 `via.Range`；OffsetX / OffsetY、SizeX / SizeY、VelocityType 命名 |
| PtVelocity2D / PtVelocity3D | `Direction`（Vector2 / Vector3）+ `Speed` |
| PtTransform2D | LocalPosition / LocalRotation / LocalScale，布局同 Transform2D |
| PtAngularVelocity3D / 2D | AddRadius、AddRadiusCoef、AddRotationAxisX/Y/Z、AddRotationRate、AddRotationCoef；RotationScaleRateX/Y、RotationSpeed、RotationSpeedCoef |
| TypeNoDraw | MHWilds 新增的一对命名为 SizeScalar（`via.Range`） |
| UnitCulling | Rotation → LocalRotation，DrawDistance → Distance |
| FadeByOcclusion | Radius → CullingRadius，Offset → SphereOffset |
| PtTransform3D | position / rotation / scale → LocalPosition / LocalRotation / LocalScale |
| VectorFieldParameter | Blend → VelocityBlend，lfoRange / lfoTime → OscillationRange / OscillationTime |
| TexelChannelOperator | EmissivePower → EmissionPower |

`VfxStudio.Re4Render/Data/re4-attribute-defaults.json` 随字段路径重新生成。没读采样代码的成对值一律用 `via.Range`。

exe 给出了、但没有改的：

| 项 | 原因 |
|---|---|
| Velocity3D `DirectionVectorX/Y/Z`（引擎名 DirX/Y/Z，两个 exe 相同） | 离线模拟器与预览按现名读取，改名要连带改它们 |
| VolumetricLighting（VolumeDepth、IntensityToAlphaBlendRatio、ShadowToLightRatio、BackfaceLightRatio，16 字节）、ShapeOperator（Operator、Position、Size、Rotation、Radius，44 字节）、VolumeBufferInfluence（0x08 起 Threshold、BounceRate、DragRatio，20 字节） | C# 是不透明类，语料里没有实例；可按左列结构化，需要单独提交 |
| ShaderSettings | 构建器按标志位条件拷贝，自动映射错位，不能用于改名 |
| FakeDoF Near / NearDistance、FadeByEmitterAngle FadeinRange、UVSequenceModifier PlaySpeedChangeTime / Coef、PlaneCollider 0x00（解析器名 RotationOrder，C# 为 Flags） | 名字或宽度与 C# 字段不是一一对应，需要读对应的处理块 |
| `*MaterialClip` 的 clipBits 位数 | 所有材质 Clip 共用加载器 0xaf0e3c0，位数随材质参数变化；C# 的 `BitSet(4)` 只是占位，读到第 5 位时 ReeLib 记一条错误日志（TypeRibbonLengthMaterialClip，语料 21 处），写出字节不受影响 |
| 第 1 节列出的 11 个混淆类型 | 没有解出 vtable |

## 8. 复核

脚本在共享笔记目录 `side-session-notes/local_73f294ed…/wilds-exe-scripts/`（只读 exe，不运行游戏代码）：

| 脚本 | 产出 |
|---|---|
| `wk.py` | PE 读取、E8/E9 与 rip 相对引用索引（缓存为 npz） |
| `wreg.py` | §2 的工厂注册表：下标 → 工厂、对象大小、构造函数 |
| `ctors.py` / `table.py` | 构造函数链 → 类型 id 与 vtable |
| `fmap.py` / `allmap.py` / `diff.py` | §5 的字段名 → 文件偏移，以及与 C# 字段的逐项对照（`diff.txt`） |
| `loop.py` / `seq.py` | §6.1 的三遍循环与调用序列 |
| `steps.py` | §6.3 的步骤表与所属属性 |

## 附录：Wilds 属性类型表

列：Wilds id、C# 类型名、RE4 id（C# 映射）、对象大小、vtable（Wilds RVA）、载荷（`B − A − 4`，字节；`+ R` = 之后还有尾随读取；"自定义" = 加载器不是 §4 的形式，多为 `.trace` 转发）、语料中出现过的载荷长度（声明长度 − 4）、安装偏移（槽 26；"代码" = 槽 26 不是单条 mov）。

| id | 类型 | RE4 id | 对象 | vtable | 载荷 | 语料 | 安装 |
|---|---|---|---|---|---|---|---|
| 0 | ? |  |  | — |  |  |  |
| 1 | FixRandomGenerator | 1 | 0x68 | e0f4770 | 40 | 40 |  |
| 2 | FixRandomGeneratorExpression |  | 0x88 | e0f4870 | 自定义 | 40 |  |
| 3 | EffectShader | 2 | 0xb8 | e0f4a00 | 8 + R | 110 |  |
| 4 | VortexelHeatReceiver |  | 0x60 | e0f4b00 | 32 |  |  |
| 5 | Transform2D | 4 | 0x58 | e0f4c10 | 20 | 20 | 0x130 |
| 6 | Transform2DModifierDelayFrame | 5 | 0x48 | e0f4d30 | 8 |  | 代码 |
| 7 | Transform2DModifier | 6 | 0xc0 | e0f4e30 | 124 |  | 代码 |
| 8 | Transform2DClip | 7 | 0xa8 | e0f5030 | 自定义 |  | 代码 |
| 9 | Transform2DExpression | 8 | 0x88 | e0f5170 | 自定义 |  | 代码 |
| 10 | Transform3D | 9 | 0x68 | e0f5300 | 40 | 40 | 代码 |
| 11 | Transform3DModifierDelayFrame | 10 | 0x48 | e0f4d30 | 8 | 8 | 代码 |
| 12 | Transform3DModifier | 11 | 0x120 | e0f4f30 | 220 | 220 | 代码 |
| 13 | Transform3DClip | 12 | 0xc8 | e0f5460 | 自定义 | 40,72,84… | 代码 |
| 14 | Transform3DExpression | 13 | 0x88 | e0f5590 | 自定义 | 44,64,72… | 代码 |
| 15 | ParentOptions | 14 | 0x108 | e0f5740 | 65 + R | 71,73,77… | 代码 |
| 16 | ParentOptionsExpression |  | 0x88 | e0f5840 | 自定义 | 96,152 | 代码 |
| 17 | Spawn | 16 | 0x98 | e0f5960 | 84 | 84 | 代码 |
| 18 | SpawnExpression | 17 | 0x88 | e0f5a60 | 自定义 | 36,56,64… |  |
| 19 | EmitterColor | 18 | 0x50 | e0f5bf0 | 12 | 12 | 0x180 |
| 20 | EmitterColorClip | 19 | 0x80 | e0f5cf0 | 自定义 | 84,216 | 代码 |
| 21 | Layout |  | 0x100 | e0f5ef0 | 12 + R | 12,80,108… | 0x590 |
| 22 | PtSort | 20 | 0x48 | e0f6000 | 4 | 4 | 0x538 |
| 23 | TypeBillboard2D | 21 | 0x80 | e0f6130 | 64 | 64 | 代码 |
| 24 | TypeBillboard2DExpression | 22 | 0x88 | — |  |  |  |
| 25 | TypeBillboard3D | 23 | 0x90 | e0f6560 | 76 | 76 | 代码 |
| 26 | TypeBillboard3DExpression | 24 | 0x88 | e0f6230 | 自定义 | 80,92,108… | 代码 |
| 27 | TypeBillboard3DMaterial | 25 | 0x100 | e0f6670 | 52 | 362,364,370… | 代码 |
| 28 | TypeBillboard3DMaterialClip | 26 | 0x98 | e0f6780 | 自定义 | 112,296,308… | 代码 |
| 29 | TypeBillboard3DMaterialExpression | 27 | 0xe0 | e0f68a0 | 自定义 | 128,144,152… | 代码 |
| 30 | TypeMesh | 28 | 0x198 | e0f6b90 | 自定义 | 322,330,342… | 代码 |
| 31 | TypeMeshClip | 29 | 0x98 | — |  | 100,112,124… |  |
| 32 | TypeMeshExpression | 30 | 0xd8 | e0f6df0 | 自定义 | 120,152,160… | 代码 |
| 33 | TypeRibbonFollow | 31 | 0xe0 | e0f6f90 | 自定义 | 140 | 代码 |
| 34 | TypeRibbonLength | 32 | 0xf8 | e0f70d0 | 自定义 | 164 | 代码 |
| 35 | TypeRibbonChain | 33 | 0x158 | e0f7230 | 自定义 | 264 | 代码 |
| 36 | TypeRibbonFixEnd | 34 | 0xe0 | e0f7370 | 自定义 | 136 | 代码 |
| 37 | TypeRibbonLightweight |  | 0x88 | e0f74c0 | 64 |  | 代码 |
| 38 | TypeRibbonParticle |  | 0xe8 | e0f75e0 | 自定义 | 136 | 代码 |
| 39 | TypeRibbonFollowMaterial |  | 0x1a8 | e0f7720 | 自定义 | 456,596,724… | 代码 |
| 40 | TypeRibbonFollowMaterialClip |  | 0x98 | e0f7870 | 自定义 |  | 代码 |
| 41 | TypeRibbonFollowMaterialExpression |  | 0xe0 | e0f7990 | 自定义 |  | 代码 |
| 42 | TypeRibbonLengthMaterial |  | 0x1c0 | e0f7ab0 | 自定义 | 480,484,512… | 代码 |
| 43 | TypeRibbonLengthMaterialClip |  | 0x98 | e0f7c00 | 自定义 | 112,292,296… | 代码 |
| 44 | TypeRibbonLengthMaterialExpression |  | 0xe0 | e0f7d20 | 自定义 | 152,168,176 | 代码 |
| 45 | TypeRibbonChainMaterial |  | 0x220 | e0f7e40 | 自定义 |  | 代码 |
| 46 | TypeRibbonChainMaterialClip |  | 0x98 | e0f7f90 | 自定义 |  | 代码 |
| 47 | TypeRibbonChainMaterialExpression |  | 0xe0 | e0f80b0 | 自定义 |  | 代码 |
| 48 | TypeRibbonFixEndMaterial |  | 0x1a8 | e0f81d0 | 自定义 |  | 代码 |
| 49 | TypeRibbonFixEndMaterialClip |  | 0x98 | e0f8320 | 自定义 |  | 代码 |
| 50 | TypeRibbonFixEndMaterialExpression |  | 0xe0 | e0f8440 | 自定义 |  | 代码 |
| 51 | TypeRibbonLightweightMaterial |  | 0x150 | e0f8560 | 自定义 |  | 代码 |
| 52 | TypeRibbonLightweightMaterialClip |  | 0x98 | e0f86b0 | 自定义 |  | 代码 |
| 53 | TypeRibbonLightweightMaterialExpression |  | 0xe0 | e0f87d0 | 自定义 |  | 代码 |
| 54 | TypeStrainRibbonMaterial |  | 0x288 | e0f88f0 | 自定义 |  | 代码 |
| 55 | TypeStrainRibbonMaterialClip |  | 0x98 | e0f8a00 | 自定义 |  | 代码 |
| 56 | TypeStrainRibbonMaterialExpression |  | 0xe0 | e0f8b20 | 自定义 |  | 代码 |
| 57 | TypeRibbonParticleMaterial |  | 0x1b0 | e0f8cb0 | 自定义 | 674,696,856… | 代码 |
| 58 | TypeRibbonParticleMaterialClip |  | 0x98 | e0f8e00 | 自定义 |  | 代码 |
| 59 | TypeRibbonParticleMaterialExpression |  | 0xe0 | e0f8f20 | 自定义 | 184 | 代码 |
| 60 | TypeRibbonFollowExpression |  | 0x88 | e0f9040 | 自定义 | 108,132,140… | 代码 |
| 61 | TypeRibbonLengthExpression | 58 | 0x88 | e0f9160 | 自定义 | 104,116,132… | 代码 |
| 62 | TypeRibbonChainExpression |  | 0x88 | — |  | 108,132,340 |  |
| 63 | TypeRibbonFixEndExpression | 60 | 0x88 | e0f9410 | 自定义 | 156 | 代码 |
| 64 | TypeRibbonLightweightExpression |  | 0x88 | e0f9530 | 自定义 |  | 代码 |
| 65 | TypeRibbonParticleExpression |  | 0x88 | e0f9650 | 自定义 | 156,172,236 | 代码 |
| 66 | TypePolygon | 63 | 0xa8 | e0f9780 | 100 | 100 | 代码 |
| 67 | TypePolygonClip | 64 | 0x1d0 | e0f98b0 | 自定义 | 72,88,116… | 代码 |
| 68 | TypePolygonExpression | 65 | 0x88 | e0f99d0 | 自定义 | 116,164,196… | 代码 |
| 69 | TypePolygonMaterial |  | 0x118 | e0f9b70 | 72 | 588 | 代码 |
| 70 | TypePolygonMaterialClip |  | 0x98 | e0f9c80 | 自定义 |  | 代码 |
| 71 | TypePolygonMaterialExpression |  | 0xe0 | e0f9da0 | 自定义 |  | 代码 |
| 72 | TypeRibbonTrail | 69 | 0x78 | e0f9f40 | 52 |  | 代码 |
| 73 | TypePolygonTrail |  | 0xa8 | e0fa050 | 88 | 88 | 代码 |
| 74 | TypePolygonTrailExpression | 71 | 0x88 | e0fa180 | 自定义 |  | 代码 |
| 75 | TypePolygonTrailMaterial | 73 | 0x118 | e0fa310 | 64 |  | 代码 |
| 76 | TypePolygonTrailMaterialClip |  | 0x98 | e0fa430 | 自定义 |  | 代码 |
| 77 | TypePolygonTrailMaterialExpression | 74 | 0xe0 | e0fa550 | 自定义 |  | 代码 |
| 78 | TypeNoDraw | 75 | 0x88 | e0fa670 | 72 | 72 | 代码 |
| 79 | TypeNoDrawExpression | 76 | 0x88 | e0fa770 | 自定义 | 80,112,120… | 代码 |
| 80 | Velocity2DDelayFrame | 77 | 0x48 | e0f4d30 | 8 |  | 代码 |
| 81 | Velocity2D | 78 | 0x90 | e0fa910 | 72 |  | 0x1c0 |
| 82 | Velocity2DExpression | 79 | 0x88 | e0fac70 | 自定义 |  | 代码 |
| 83 | Velocity3DDelayFrame | 80 | 0x48 | e0f4d30 | 8 | 8 | 代码 |
| 84 | Velocity3D | 81 | 0xc0 | e0fadb0 | 120 | 120 | 代码 |
| 85 | Velocity3DExpression | 82 | 0xa8 | e0fab50 | 自定义 | 116,136,144… | 代码 |
| 86 | RotateAnimDelayFrame | 83 | 0x48 | e0f4d30 | 8 | 8 | 代码 |
| 87 | RotateAnim | 84 | 0x80 | e0faf00 | 60 | 60 | 0x1e8 |
| 88 | RotateAnimExpression | 85 | 0x88 | e0fb000 | 自定义 | 56,128,136… | 代码 |
| 89 | ScaleAnimDelayFrame | 86 | 0x48 | e0f4d30 | 8 | 8 | 代码 |
| 90 | ScaleAnim | 87 | 0x90 | e0fb120 | 72 | 72 | 0x1f0 |
| 91 | ScaleAnimExpression | 88 | 0x88 | e0fb220 | 自定义 | 40,60,72… | 代码 |
| 92 | VanishArea3D | 89 | 0x108 | e0fb350 | 60 + R | 66,74,80… | 代码 |
| 93 | VanishArea3DExpression | 90 | 0x88 | e0fb490 | 自定义 | 96,112,144… | 代码 |
| 94 | Life | 91 | 0x68 | e0fb660 | 36 | 36 | 0x1e0 |
| 95 | LifeExpression | 92 | 0x88 | e0fb760 | 自定义 | 32,96,128… | 代码 |
| 96 | UVSequence | 93 | 0x88 | e0fb880 | 28 + R | 92,94,96… | 代码 |
| 97 | UVSequenceModifier | 94 | 0x60 | e0fb980 | 28 | 28 | 0x3c0 |
| 98 | UVSequenceExpression | 95 | 0x88 | e0fba80 | 自定义 | 72,88,104… | 代码 |
| 99 | UVScroll | 96 | 0x78 | e0fbba0 | 52 | 52 | 0x200 |
| 100 | TextureUnit | 97 | 0x268 | e0fbca0 | 4 |  | 代码 |
| 101 | TextureUnitExpression | 98 | 0x98 | e0fbda0 | 自定义 |  | 代码 |
| 102 | TextureFilter | 99 | 0x50 | e0fbf30 | 12 | 12 | 0x448 |
| 103 | EmitterShape2D | 100 | 0x60 | e0fc030 | 32 | 32 | 0x208 |
| 104 | EmitterShape2DExpression | 101 | 0x88 | e0fc130 | 自定义 |  | 代码 |
| 105 | EmitterShape3D | 102 | 0x98 | e0fc3e0 | 84 | 84 | 代码 |
| 106 | EmitterShape3DExpression | 103 | 0x88 | e0fc250 | 自定义 | 44,76,92… | 代码 |
| 107 | AlphaCorrection | 104 | 0x50 | e0fc4f0 | 16 | 16 | 0x210 |
| 108 | ContrastHighlighter | 105 | 0x58 | e0fc600 | 24 |  | 0x218 |
| 109 | ColorGrading |  | 0x58 | e0fc700 | 24 | 24 | 0x220 |
| 110 | ColorGradingExpression |  | 0x88 | e0fc800 | 自定义 |  | 代码 |
| 111 | Blink | 108 | 0x70 | e0fc920 | 44 | 44 | 0x230 |
| 112 | Noise | 109 | 0x60 | e0fca40 | 32 | 32 | 0x238 |
| 113 | NoiseExpression | 110 | 0x88 | e0fcb40 | 自定义 | 72,80,96… | 代码 |
| 114 | TexelChannelOperator | 111 | 0x88 | e0fcca0 | 68 | 68 | 0x458 |
| 115 | TexelChannelOperatorClip | 112 | 0x170 | e0fcda0 | 自定义 |  | 代码 |
| 116 | TexelChannelOperatorExpression | 113 | 0x88 | e0fced0 | 自定义 |  | 代码 |
| 117 | TypeStrainRibbon | 114 | 0x1b8 | e0fd0e0 | 自定义 | 290,296,298… | 代码 |
| 118 | TypeStrainRibbonExpression |  | 0x88 | e0fd1e0 | 自定义 | 152,160,200… | 代码 |
| 119 | TypeLightning3D |  | 0x268 | e0fd300 | 260 + R | 318,326,328… | 代码 |
| 120 | TypeLightning3DExpression |  | 0x88 | e0fd430 | 自定义 | 236,244,252… | 代码 |
| 121 | TypeLightning3DMaterial |  | 0x2d8 | e0fd5c0 | 236 + R |  | 代码 |
| 122 | TypeLightning3DMaterialClip |  | 0x98 | e0fd710 | 自定义 |  | 代码 |
| 123 | TypeLightning3DMaterialExpression |  | 0xe0 | e0fd830 | 自定义 |  | 代码 |
| 124 | TypeLightningExpensive |  | 0x1f8 | e0fd950 | 自定义 | 184 | 代码 |
| 125 | PtLightningColliderAction |  | 0x58 | — |  | 12 |  |
| 126 | PtLightningBranchAction |  | 0x68 | e0fdb60 | 24 + R | 28 | 0x2a8 |
| 127 | ShaderSettings | 123 | 0xe0 | e0fdc90 | 158 | 158 | 代码 |
| 128 | ShaderSettingsExpression | 124 | 0x88 | e0fe060 | 自定义 | 112,136,200 | 代码 |
| 129 | Distortion | 125 | 0x60 | e0fe180 | 25 | 25 | 0x260 |
| 130 | DistortionExpression | 126 | 0x88 | e0fe280 | 自定义 | 76,84,100… | 代码 |
| 131 | VolumetricLighting | 127 | 0x50 | e0fe3b0 | 16 |  | 代码 |
| 132 | RenderTarget | 128 | 0x50 | e0fe4b0 | 4 + R | 112 | 代码 |
| 133 | DrawSubscene |  | 0x38 | e0fe5b0 | 自定义 | 0 | 代码 |
| 134 | UnitCulling | 130 | 0x70 | e0fe6e0 | 44 | 44 | 代码 |
| 135 | UnitCullingExpression |  | 0x88 | e0fe840 | 自定义 |  | 代码 |
| 136 | PtLife | 131 | 0x58 | e0fe9f0 | 8 + R | 12 | 0x280 |
| 137 | PtBehavior | 132 | 0x178 | e0feb30 | 4 + R | 34,37,39… | 代码 |
| 138 | PtBehaviorClip | 133 |  | — |  |  |  |
| 139 | PlayEfx | 134 | 0x40 | e0fec40 | 自定义 | 100,102,104… |  |
| 140 | FadeByAngle | 135 | 0x60 | e0fedc0 | 28 | 28 | 0x2b8 |
| 141 | FadeByAngleExpression | 136 | 0x88 | e0feed0 | 自定义 |  | 代码 |
| 142 | FadeByEmitterAngle | 137 | 0x68 | e0feff0 | 28 | 28 | 0x2c0 |
| 143 | FadeByDepth | 138 | 0x50 | e0ff100 | 16 | 16 | 代码 |
| 144 | FadeByDepthExpression | 139 | 0x88 | e0ff200 | 自定义 |  | 代码 |
| 145 | FadeByOcclusion | 140 | 0x50 | e0ff390 | 16 | 16 | 0x2d0 |
| 146 | FadeByOcclusionExpression | 141 | 0x88 | e0ff4a0 | 自定义 |  | 代码 |
| 147 | FadeByRootCulling |  | 0x80 | e0ff5d0 | 64 |  | 代码 |
| 148 | FakeDoF | 142 | 0x58 | e0ff6e0 | 20 | 20 | 0x2d8 |
| 149 | LuminanceBleed | 143 | 0x60 | e0ff7f0 | 28 | 28 | 0x2e0 |
| 150 | ScaleByDepth | 144 | 0x60 | e0ff910 | 28 | 28 | 代码 |
| 151 | TypeNodeBillboard |  | 0x138 | e0ffa40 | 244 |  | 代码 |
| 152 | TypeNodeBillboardExpression |  | 0x98 | e0ffc50 | 自定义 |  | 代码 |
| 153 | FluidEmitter2D |  | 0x90 | e0ffd80 | 52 |  | 0x308 |
| 154 | FluidEmitter2DClip |  | 0x80 | e0ffe80 | 自定义 |  | 代码 |
| 155 | FluidEmitter2DExpression |  | 0x88 | e0fffa0 | 自定义 |  | 代码 |
| 156 | FluidSimulator2D | 150 | 0x1b0 | e1000d0 | 248 |  | 代码 |
| 157 | FluidParticle2DSimulator |  | 0x150 | — |  | 214,230,246… |  |
| 158 | PlayEmitter | 151 | 0x50 | e100340 | 自定义 | 494,540,545… |  |
| 159 | PtTransform3D | 152 | 0x68 | e1004c0 | 36 | 36 | 0x140 |
| 160 | PtTransform3DClip | 153 | 0x130 | e1005f0 | 自定义 | 40,60,72… | 0x148 |
| 161 | PtTransform3DExpression |  | 0x88 | e100710 | 自定义 | 212 | 代码 |
| 162 | PtTransform2D | 155 | 0x58 | e100830 | 20 | 20 | 0x140 |
| 163 | PtTransform2DClip |  | 0xd0 | e100950 | 自定义 | 128 | 0x148 |
| 164 | PtVelocity3D | 157 | 0x50 | e100a70 | 16 | 16 | 0x158 |
| 165 | PtVelocity3DClip | 158 | 0xb8 | e100b80 | 自定义 | 72 | 0x160 |
| 166 | PtVelocity2D | 159 | 0x50 | e100ca0 | 12 |  | 0x158 |
| 167 | PtVelocity2DClip | 160 | 0xa0 | e100db0 | 自定义 |  | 0x160 |
| 168 | PtColliderAction | 161 | 0xa8 | e100ef0 | 24 + R | 28,58 | 代码 |
| 169 | PtProjection |  | 0xa8 | e100ff0 | 28 + R | 28,58 | 代码 |
| 170 | PtCollision | 162 | 0xc0 | e101130 | 52 + R | 52,58,82… | 代码 |
| 171 | PtCollisionInfluence |  | 0x78 | e101230 | 40 + R | 360 | 代码 |
| 172 | PtColor | 164 | 0x50 | e101340 | 12 | 12 | 0x390 |
| 173 | PtColorClip | 165 | 0x88 | e101440 | 自定义 | 72,84,96… | 0x398 |
| 174 | PtColorMixer |  | 0x70 | e101570 | 16 + R | 24,28,32… | 0x3a0 |
| 175 | PtColorMixerClip |  | 0x68 | e101670 | 自定义 | 72,84,228 | 代码 |
| 176 | PtUvSequence | 168 | 0x50 | e1017a0 | 12 |  | 0x3b0 |
| 177 | PtUvSequenceClip | 169 | 0x88 | e1018a0 | 自定义 |  | 0x3b8 |
| 178 | MeshEmitter | 170 | 0x198 | e101a00 | 72 + R | 140,144,232… | 代码 |
| 179 | MeshEmitterClip | 171 | 0x140 | e101b40 | 自定义 |  | 代码 |
| 180 | MeshEmitterExpression | 172 | 0x88 | e101c60 | 自定义 |  | 代码 |
| 181 | ScreenSpaceEmitter | 173 | 0x48 | e101d80 | 8 |  | 0x478 |
| 182 | Listener |  | 0x58 | e101ed0 | 8 | 8 | 0x250 |
| 183 | VectorFieldParameter | 175 | 0xc0 | e102040 | 76 + R | 80 | 0x3d8 |
| 184 | VectorFieldParameterClip | 176 | 0x1c0 | e102160 | 自定义 |  | 代码 |
| 185 | VectorFieldParameterExpression | 177 | 0x88 | e102280 | 自定义 | 136,144,168… | 代码 |
| 186 | GlobalVectorField |  | 0x90 | e102440 | 自定义 |  | 0x480 |
| 187 | GlobalVectorFieldClip |  | 0x1b8 | e102540 | 自定义 |  | 代码 |
| 188 | GlobalVectorFieldExpression |  | 0x88 | e102670 | 自定义 |  | 代码 |
| 189 | DirectionalFieldParameter |  | 0x88 | e1027d0 | 52 + R |  | 0x3e0 |
| 190 | DirectionalFieldParameterClip |  | 0x170 | e1028f0 | 自定义 |  | 代码 |
| 191 | DirectionalFieldParameterExpression |  | 0x88 | e102a10 | 自定义 |  | 代码 |
| 192 | DepthOperator | 184 | 0x88 | e102b80 | 自定义 | 60 | 代码 |
| 193 | PlaneCollider | 185 | 0x80 | e102cc0 | 64 | 64 | 代码 |
| 194 | PlaneColliderExpression |  | 0x88 | e102e10 | 自定义 |  | 代码 |
| 195 | DepthOcclusion | 187 | 0x48 | e102f30 | 4 | 4 | 0x450 |
| 196 | ShapeOperator |  | 0x78 | e103080 | 44 |  | 0x408 |
| 197 | ShapeOperatorExpression |  | 0x88 | e1031b0 | 自定义 |  | 代码 |
| 198 | WindInfluence3DDelayFrame |  | 0x48 | e0f4d30 | 8 |  | 代码 |
| 199 | WindInfluence3D | 191 | 0x60 | e1032d0 | 24 | 24 | 代码 |
| 200 | Attractor | 192 | 0x138 | e1033d0 | 116 + R | 122,130,132… | 代码 |
| 201 | AttractorClip | 193 | 0xd8 | e1034e0 | 自定义 | 72 | 代码 |
| 202 | AttractorExpression | 194 | 0x88 | e103600 | 自定义 | 100,108,116… | 代码 |
| 203 | PtVortexelWind | 195 | 0x68 | e103720 | 34 | 34 | 0x4e0 |
| 204 | PtVortexelWindExpression |  | 0x88 | e103820 | 自定义 | 72 | 代码 |
| 205 | VortexelWindEmitter |  | 0x98 | e103980 | 82 | 82 | 代码 |
| 206 | VortexelWindEmitterExpression |  | 0x88 | e103a80 | 自定义 | 80,96,120… | 代码 |
| 207 | PtVortexelHeatSource |  | 0x50 | — |  | 14 |  |
| 208 | VortexelCollider |  | 0x60 | e103cb0 | 32 | 32 | 代码 |
| 209 | VortexelColliderExpression |  | 0x88 | e103db0 | 自定义 |  | 代码 |
| 210 | VortexelIndoorMask |  | 0x58 | e103ed0 | 24 | 24 | 代码 |
| 211 | VortexelIndoorMaskExpression |  | 0x88 | e103fd0 | 自定义 |  | 代码 |
| 212 | PtVortexelPhysics |  | 0xb0 | e1041f0 | 自定义 | 104 | 0x4e8 |
| 213 | PtVortexelPhysicsSimple |  | 0x60 | e1043b0 | 自定义 | 24 | 0x4f0 |
| 214 | PtVortexelSnap |  | 0x60 | e104540 | 自定义 | 24 | 0x5a0 |
| 215 | EmitterPriority | 200 | 0x38 | e104640 | 自定义 | 0 | 代码 |
| 216 | DrawOverlay | 201 | 0x50 | e104740 | 16 |  | 代码 |
| 217 | VectorField |  | 0x78 | e104840 | 24 + R |  |  |
| 218 | VolumeField |  | 0x58 | e104940 | 24 |  |  |
| 219 | DirectionalField |  | 0x40 | e104a40 | 自定义 |  |  |
| 220 | AngularVelocity3DDelayFrame | 205 | 0x48 | e0f4d30 | 8 | 8 | 代码 |
| 221 | AngularVelocity3D | 206 | 0x98 | e104b40 | 80 | 80 | 0x1c8 |
| 222 | PtAngularVelocity3D |  | 0x68 | e104c40 | 32 | 32 | 0x1d0 |
| 223 | PtAngularVelocity3DExpression |  | 0x88 | e104d40 | 自定义 | 84,116,204 | 代码 |
| 224 | AngularVelocity2DDelayFrame |  | 0x48 | e0f4d30 | 8 |  | 代码 |
| 225 | AngularVelocity2D | 210 | 0x80 | e104e60 | 60 |  | 0x1c8 |
| 226 | PtAngularVelocity2D |  | 0x60 | e104f60 | 24 |  | 0x1d0 |
| 227 | PtAngularVelocity2DExpression |  | 0x88 | e105060 | 自定义 |  | 代码 |
| 228 | PtPathTranslate | 213 | 0x2790 | e105180 | 自定义 | 148,166,182… | 代码 |
| 229 | PtPathTranslateExpression |  | 0x88 | e1052a0 | 自定义 | 80 | 代码 |
| 230 | IgnorePlayerColor |  | 0x38 | e1053c0 | 自定义 | 0 |  |
| 231 | IgnoreSettings |  | 0x48 | e1054c0 | 4 | 4 |  |
| 232 | ProceduralDistortionDelayFrame | 216 | 0x48 | e0f4d30 | 8 |  | 代码 |
| 233 | ProceduralDistortion | 217 | 0x90 | e1055e0 | 76 | 76 | 0x410 |
| 234 | ProceduralDistortionClip | 218 | 0x200 | e1056e0 | 自定义 | 108 | 代码 |
| 235 | ProceduralDistortionExpression | 219 | 0x88 | e105800 | 自定义 |  | 代码 |
| 236 | TestBehaviorUpdater | 220 | 0x118 | e105920 | 4 + R |  |  |
| 237 | StretchBlur | 221 | 0x78 | e105a20 | 48 |  | 代码 |
| 238 | StretchBlurExpression | 222 | 0x88 | e105bb0 | 自定义 |  | 代码 |
| 239 | EmitterHSV |  | 0x98 | — |  | 84 |  |
| 240 | EmitterHSVExpression |  | 0x90 | e105de0 | 自定义 | 156 | 代码 |
| 241 | FlowMap |  | 0x68 | e105f10 | 32 + R | 144,146 | 0x4c0 |
| 242 | RgbCommon | 226 | 0xa8 | e106020 | 104 | 104 | 0x4b0 |
| 243 | RgbCommonExpression |  | 0x88 | e106120 | 自定义 | 96,116,128… | 代码 |
| 244 | RgbWater | 228 | 0xb8 | e1062b0 | 115 | 115 | 0x4b8 |
| 245 | RgbWaterExpression |  | 0x88 | e1063b0 | 自定义 | 148,164,172… | 代码 |
| 246 | PtFreezer |  | 0x68 | e106550 | 40 | 40 | 0x4c8 |
| 247 | AssignCSV | 231 | 0x70 | e106650 | 10 + R | 126,130,132… | 0x4d0 |
| 248 | DestinationCSV |  | 0xc8 | e106750 | 28 + R | 170,190 | 0x4d8 |
| 249 | GpuPhysics |  | 0x70 | e1068d0 | 25 + R |  | 0x550 |
| 250 | TerrainSnap |  | 0x80 | e106a40 | 自定义 | 52 | 0x560 |
| 251 | RepeatArea |  | 0x60 | — |  | 20 |  |
| 252 | EmitMask |  | 0x48 | e106ca0 | 4 | 4 |  |
| 253 | ParticleTimer |  | 0x38 | e106da0 | 自定义 |  | 0x530 |
| 254 | Trigger |  | 0x58 | e106f20 | 自定义 | 12 | 0x5a8 |
| 255 | CustomComputeShader |  |  | — |  |  |  |
| 256 | VolumeBufferInfluence |  | 0x58 | e107020 | 20 |  | 0x5b8 |
| 257 | VolumeBufferOperator |  | 0x48 | e107130 | 8 |  | 0x5c8 |
| 258 | TypeGpuBillboard | 243 | 0x98 | e107240 | 自定义 | 76 | 代码 |
| 259 | TypeGpuBillboardExpression | 244 | 0x88 | e107340 | 自定义 | 96,120,124… | 代码 |
| 260 | TypeGpuPolygon | 245 | 0xa0 | e107460 | 自定义 | 88 | 代码 |
| 261 | TypeGpuPolygonExpression |  | 0x88 | — |  | 132,164,196… |  |
| 262 | TypeGpuRibbonFollow | 247 | 0x88 | e107680 | 60 | 60 | 代码 |
| 263 | TypeGpuRibbonFollowExpression | 248 | 0x88 | e107780 | 自定义 | 124,176,236 | 代码 |
| 264 | TypeGpuRibbonLength | 249 | 0xb0 | e1078a0 | 100 | 100 | 代码 |
| 265 | TypeGpuRibbonLengthExpression |  | 0x88 | e1079a0 | 自定义 | 60,92,108… | 代码 |
| 266 | TypeGpuMesh | 251 | 0x178 | e107ad0 | 自定义 | 322,350,352… | 代码 |
| 267 | TypeGpuMeshClip |  | 0x90 | e107be0 | 自定义 | 100,112,176… | 代码 |
| 268 | TypeGpuMeshExpression | 252 | 0xd8 | e107d00 | 自定义 | 128,144,152… | 代码 |
| 269 | TypeGpuMeshTrail |  | 0x1f8 | e107e90 | 自定义 | 530,672,1148 | 代码 |
| 270 | TypeGpuMeshTrailClip |  | 0x1f0 | e107fa0 | 自定义 | 144 | 代码 |
| 271 | TypeGpuMeshTrailExpression |  | 0xd8 | e1080c0 | 自定义 |  | 代码 |
| 272 | TypeGpuLightning3D |  | 0x338 | e108250 | 180 |  | 代码 |
| 273 | RayTracingTarget |  | 0x58 | e1083b0 | 20 |  | 0x540 |
| 274 | PointCloudEmitter |  | 0x48 | e1084b0 | 8 | 8 | 代码 |
| 275 | DumpGpuParticle |  | 0x48 | e1085b0 | 4 |  | 0x578 |
| 276 | RibbonJointOffsetVelocity |  | 0xa0 | e1086b0 | 88 |  | 0x568 |
| 277 | RibbonJointScale |  | 0x68 | e1087b0 | 32 |  | 0x570 |
| 278 | FluidParticleEmitter |  | 0x60 | — |  | 20 |  |
| 279 | FluidParticleEmitterTarget |  | 0x78 | e1089f0 | 20 + R | 92,96,100… | 代码 |
| 280 | FluidParticle2DSimulatorExpression |  | 0x88 | — |  | 152 |  |
| 281 | FluidParticleEmitter3D |  | 0x50 | e108c80 | 自定义 |  |  |
