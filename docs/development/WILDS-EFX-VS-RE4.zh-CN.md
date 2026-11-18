# MH Wilds 与 RE4R 的 EFX CPU 代码差异

适用：MonsterHunterWilds.exe（546,013,600 字节，PE 时间戳 0x6ACECA63）对照 re4.exe（EFX 3539837，与 `tools/Re4Native` 同一补丁）。RVA 只对这两个版本有效。
读者：修改 `VfxStudio.Core/Formats/EFX/` 的 Wilds 版本字段，或要把 RE4 运行时规格（`RE4-EFX-RUNTIME.zh-CN.md`，下称 RT）套到 Wilds 上的人。
本文以 RE4 为基准，只记差异；没有写到的部分按 RT 理解。GPU 粒子类型和离线预览不在范围内。

状态标记与 RT §0.1 相同（已确认 / 部分待确认 / 折中实现 / 待清理）。结论来自静态反汇编（pefile + capstone）、Wilds 语料（`MHWILDS_EXTRACT/natives/STM/Art/VFX`，9225 个文件）只读统计，以及在本进程里执行 Wilds 代码：混淆的构造函数用 unicorn 模拟，步骤注册（槽 6）和块构建器（槽 16）用 `tools/Re4Native/re4native.py` 原样映射 exe 后直接调用。运行时不碰游戏进程，不写游戏目录，语料只读。

## 1. 读 Wilds exe 的前提

| 项 | 内容 | 状态 |
|---|---|---|
| 节 | 节名被混淆；代码在 `.xtls`（可执行）和 `.trace`（RWX）。`.pdata` 只覆盖 `.xtls` 41 万个函数和 `.trace` 1.2 万个，许多小函数要靠 int3 填充找起点 | 已确认 |
| 常量隐藏 | `.trace` 里的立即数写成"全局变量 ± 立即数"，如类型 id `mov edx,[rip+G]; sub edx,0xa02f`。G 的值在文件里，静态即可求出 | 已确认 |
| 混淆构造函数 | 11 个类型（24、31、62、125、157、207、239、251、261、278、280）的构造函数在 `.trace` 里做了控制流混淆（flags 分支、跳转寄存器）。模拟执行时代码会读 KUSER_SHARED_DATA 和 PEB，补上这两页后 10 个类型跑到写 vtable；31 在写 vtable 前出错，按位置认定（见 §2） | 已确认（31 部分待确认） |

## 2. 类型表的来源

RE4 靠"基类构造函数 3d0e910 的调用者 + edx 立即数"建表（`factory.py`）。Wilds 有一张更直接的表：

- **工厂注册表**：静态初始化函数 0xaab6b5b..0xaab8b72 把 282 个 `std::function` 依次写进全局数组 0x14bf2000（每项 0x40 字节，`+0x38` 是实现指针），**数组下标 = 文件里的类型 id**（0..281，与 C# `EfxAttributeTypes.cs` 的 MHWilds 编号一致）。每个 lambda 的调用槽是 `mov ecx, 对象大小; call 0xb020830（分配）; jmp 构造函数`。已确认。
- **基类构造函数** 0x38970，`edx` = 类型 id，写到对象 +8。共享构造函数（多个类型共用一个类）由外层按 `edx` 传入 id，`*MaterialExpression` 还多传 `r8d` = 所属 Type 的 id（如 29 → 27）。由下标得到的 id 与构造函数里的立即数 id 有 131 项可以互相核对，只有一处不一致：下标 161（PtTransform3DExpression）的构造函数 0xb6dafc0 写入的是 204（模拟全部工厂后又多出 24、239 两处，见下一条）。RE4 也有同类现象（类型 18 有两个工厂，RT 依据见 `factory.py` 的 `PREFERRED_FACTORY`）。部分待确认。
- **逐个调用工厂**（unicorn 模拟，分配器换成线性分配，在基类构造函数 0x38970 处记录 `edx`，并记录对象 +0 / +8 的写入）：281 个下标里 275 个写入的类型 id 等于下标。例外：
  - 24（TypeBillboard2DExpression）写 26，239（EmitterHSV）写 19，161 写 204（与静态结果相同）；
  - 138、255 的工厂返回空指针（没有类型）；
  - 280 在混淆代码里读到运行时才有的数据，id 没有读出，vtable e108af0 是在补零页后记录到的。
  这些对象 +8 的 id 与文件 id 不同，但文件按下标查工厂，读写不受影响；它们在引擎里的用途没有追。
- vtable：279 个类型解出（257 个经构造函数链，11 个直接读构造函数体，10 个模拟工厂得到）。31（TypeMeshClip）的构造函数在写 vtable 前出错；`e0f6cd0` 是唯一一个带属性 vtable 特征（槽 1 = 0xb032750，槽 2–4 = 0xb46f210）、又夹在 TypeMesh（e0f6b90）和 TypeMeshExpression（e0f6df0）之间且没有归属的 vtable，所以认定为 31。部分待确认。完整表见附录。

## 3. vtable 布局

Wilds vtable 比首轮笔记假定的起点早 0x10（多出两个槽），共 32 个槽。以 ScaleAnim（RE4 7a0b508 ↔ Wilds e0fb120）逐槽对照：

| RE4 槽 | Wilds 槽 | 内容 | 状态 |
|---|---|---|---|
| 0–4 | 0–4 | 析构（Wilds 跳进 `.trace`）、若干空函数 | 已确认 |
| 5 加载器 | 5 加载器 | 读法改变，见 §4 | 已确认 |
| 6 块大小 | 6 | Wilds 改成**注册逐粒子步骤**（§6.3），签名 `(attr, ctx)`，`[ctx]` = 发射器描述对象 X；RE4 的块大小计算在 Wilds 中的位置未定 | 部分待确认 |
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

`字段名 → 块偏移`（槽 20 解析器）+ `块偏移 ← 对象偏移`（槽 16 构建器）+ `对象偏移 → 文件偏移`（§4）三步组合，得到引擎字段名在文件中的位置。能完整走通的有 63 个类型；解析器或构建器在 `.trace` 里、或构建器有条件分支（ShaderSettings、Spawn、EmitterShape3D、AngularVelocity2D/3D 部分字段）的类型没有自动走通。

这些类型改用原生运行：把语料里的载荷（每类型最多 400 份不同的字节）按 §4 放到对象 `+A+4`，直接调用构建器；再逐个把一个 4 字节字换成标记值重跑，比较块的差异。标记值原样出现在块的某个偏移，就得到"文件偏移 → 块偏移"，再查解析器的"块偏移 → 名字"。块里其他位置也变了的字是标志位或被换算过的值（表里记为"换算"）。Spawn、EmitterShape3D 的构建器要读引擎单例（`[0x147ca268]+0x5520`、`[0x147ca238]+0x88`），运行时给它们一个全零对象，并把 0xb073170 换成返回 1.0、0xb04fb00 换成返回空。结果见 §7。

另有一种解析器形式静态脚本没有覆盖：外层只保存栈 cookie 再调用内层，内层把字段写到 `[[rdx]] + 偏移`（PtVortexelHeatSource、FluidParticleEmitter、RepeatArea、FluidParticle2DSimulator 等）。这几个手工读出，见 §7。

解析器有两个现象，含义未定：

- 同一个槽有多个名字，按标志位或场类型切换：ShaderSettings 的 SoftParticleDistance / …WithStochastic / …WithVolumetricParticle，ProceduralDistortion 的 Wave* / Vertical* / Horizontal*，VectorFieldParameter 的 Parameter 三分量（Frequency / Range、Amplitude / Detail、SmoothBlend），AlphaCorrection 的 HiPass / Max / LifePow0。
- ScaleAnim 解析一对值后用 `vshufps 0xe1` 交换两个 float 再写入块。

## 6. 发射器循环与逐粒子管线

### 6.1 三遍属性遍历

| 项 | RE4 | Wilds | 状态 |
|---|---|---|---|
| 3D 路径循环 | 3d08480 | 0xaaba8e0 | 已确认 |
| 另一路循环 | 3d079c0（2D，只有两遍） | 0xaabba90，同样三遍 | 已确认是 2D（§6.3） |
| 属性数组 | `E+0x288` | `E+0x2c8` | 已确认 |
| 三遍调用 | vtable +0xb0 / +0xb8 / +0xc0 | +0xc8 / +0xd0 / +0xd8 | 已确认 |

首轮笔记按 RE4 字节模式没搜到，原因是数组偏移变了，且下标寄存器换成 r15（`mov r8,[rax+r15*8+0x2c8]`），指令编码不同。

### 6.2 粒子槽

- 槽跨度 0x140 → **0x150**；粒子编号仍写在 `p+0x18`，来自发射器计数器自增。已确认。
- 认领时同样 `[p] |= 3`（alive | new），出生阶段结束后 `[p] = ([p] & 0xeffffffc) | 0x10000000`。已确认。

### 6.3 步骤列表取代固定调用

这是与 RE4 唯一的结构性差别。RE4 的出生前置步骤（RT §1.3 第 5 步）和逐粒子管线 3d05350（RT §1.4）是写死的直接调用；Wilds 改成数据驱动：

- 每个属性的 vtable 槽 6 在首次调用时（函数级静态初始化）把自己的步骤函数写进全局步骤表 0x14bf66e8（`.didata`）的第 k 项，再把 k 追加到发射器描述对象 X 的步骤列表。已确认。
- 列表有两条，都是 u32 数组：A = `[X+0x50]`（个数 `[X+0x58]`），B = `[X+0x60]`（个数 `[X+0x68]`）。属性按 `[X+0x34] == 1` 选编号：等于 1 时用 3D 编号，否则用 2D 编号。只有 2D 才有的属性（PtVelocity2D、PtTransform2D、AngularVelocity2D 等）在 3D 表里没有函数，所以 1 = 3D。已确认。
- 同一张表分四段，按"同一个属性在不同编号下指向同一个函数"核对（UVSequence、Blink、PtColor、RotateAnim、ParentOptions 四段都对上）。已确认。

  | 列表 | 3D（循环 0xaaba8e0） | 2D（循环 0xaabba90） |
  |---|---|---|
  | A | 表 + 0 | 表 + 230 |
  | B | 表 + 119 | 表 + 281 |

- 循环按模式位 r12 分三路（0xaababe0 起）。r12 来自驱动 0x1c1c0 的 r9：发射器刚开始播放的那一帧（状态 1 → 2）是 3，之后每帧是 1。三遍属性遍历之后，如果某个属性把循环上下文的字节 `+0x12c` 置 1，r12 改成 `(r12 & ~0xa) | 8`；否则 r12 再并上上下文 `+8` 的位 1。已确认。
  - 位 3（0xaabb1b6）：清掉所有槽的存活位，不执行任何步骤。原生运行中，ParentOptions 的父关节查不到、且效果对象 `+0x1c4` 位 20 为 0 时，它的第二遍（0xb02c6a0）把 `+0x12c` 置 1（0xb02d32a；0xb476680 里有同样的写法）。在已能原生加载的类型的三遍函数及其直接调用里，只有 ParentOptions 写这个字节。
  - 位 1（0xaabb25a）：用 B 列表 `[X+0x60]` / `[X+0x68]` 执行，即开始播放的那一帧。用探针确认第 0 帧走这一路、第 1 帧起走位 0。上下文 `+8` 位 1 由谁置位没有找。
  - 位 0（0xaabac46）：逐槽执行 A 列表。
- 排序在 0x1dec20：先调发射器自身的注册函数 0x386e3e0，再沿 `X+0x10` 链表对每个属性调槽 6，然后把 A、B 两条列表各自排成升序（超过 32 项用 std::sort 0xa548de0，否则希尔排序）。所以执行顺序就是号的顺序。已确认。
- 发射器自身的注册函数 0x386e3e0 对每个 3D 发射器无条件追加 1、6、7、12、14、19、25、26、40、47、48（B 列表：1、2、6、9、14、17、18、32、39、40）；2D 发射器追加 7、11、13、14、22、25、44（B：1、3、7、8、16、19）。这些步骤不属于任何属性：Velocity3D、Life、Spawn、Transform3D、EmitterShape3D 等的槽 6 都是空函数 0x6de0，它们的逐粒子工作由这组内置步骤做。已确认。内置步骤各自的作用仍按读的 ctx 偏移推定：6 / 19 同一函数 0xb0ccd90，7 / 40 同一函数 0xb050680（读 Velocity 块），12 = 0xb00ead0，14 = 0xad97170（读 Life 块，出生），25 = 0xb010440，26 = 0xaabc420（Life 逐帧），47 = 0xb11a880、48 = 0xb063730（推定积分）。
- PtLife 在 3D 下还追加 117、118（与 2、3 同一函数），超出 A 列表的所有号段（最高到 0x74），在哪里执行没有找到。部分待确认。

3D 号段与 RT 的对应：

| 号段 | 循环位置 | 对应 RE4 | 步骤（号 = 所属属性；"内置" = 发射器自身的注册函数 0x386e3e0 追加，括号里是按读的 ctx 偏移推定的作用） |
|---|---|---|---|
| 0–4 | 每槽先执行（`[p+4]` 位 10 置位时；RE4 击杀检查看位 9） | 击杀检查 | 2、3 PtLife，4 PtBehavior |
| 5–9 | 认领之后 | 其他出生源 | 5 AssignCSV，6 内置，7 内置（读 ctx+0x1c0 / 0x208，与 40 同一函数），8 Attractor，9 PtProjection |
| 0xa–0x15 | 新粒子 | 出生前置 | 11 PointCloudEmitter，12 内置，13 / 15 PtFreezer，**14 内置（读 Life 块 ctx+0x1e0，即 Life 出生）**，16 ParentOptions，17 TexelChannelOperator，18 AssignCSV，19 内置（与 6 同一函数），20 PointCloudEmitter，21 ShaderSettings |
| 0x17–0x6e | 存活粒子 | 3d05350 | 24 Trigger，25 内置，26 内置（Life 逐帧），27 Attractor，28 VanishArea3D，29–31 PtTransform3D / Clip / Expression，32 AngularVelocity3D，33 PtAngularVelocity3D，34 PtPathTranslate，35 Layout，36 Noise，**38 RotateAnim，39 ScaleAnim，40 内置（Velocity）**，41–42 PtVelocity3D / Clip，43 PtVortexelWind，44 PtVortexelHeatSource，45 WindInfluence3D，47–48 内置（读 ctx+0x1c8 与变换，推定积分），49 PtColliderAction，50 PtProjection，51 PtCollision，52 PtCollisionInfluence，**53–86 各 Type***，87–88 RibbonJoint*，89 PtLightningBranchAction，90 PtLightningColliderAction，91 Blink，92 UVSequence，93 UVSequenceModifier，94–95 PtUvSequence / Clip，96 UVScroll，97 TextureUnit，98 AlphaCorrection，99 FluidSimulator2D，101 FlowMap，102 RgbCommon，103 RgbWater，104 ParticleTimer，105–107 PtColor / Clip / Mixer，108 DestinationCSV，109 RayTracingTarget |
| 0x6f–0x74 | 类型不是 0x22 / 0x23 时 | 类型后处理 | 110 PtBehavior，112 Billboard3D，113 Polygon，114 Mesh，**115 ScaleAnim（基值捕获）**，116 Trigger |

与 RE4 顺序的差别：

- **Type 出生抽样在碰撞之后**。RE4 默认（`[g+0xeb0] == 0`）把类型分派放在 Noise 之后、RotateAnim 之前，`!= 0` 时才移到碰撞之后（RT §1.4 第 6、16 步）。Wilds 的 Type 步骤（53–86）在 PtCollision（51）之后、Blink（91）之前，相当于 RE4 的 `!= 0` 分支，且没有看到另一种排列。因此 Wilds 中 RotateAnim / ScaleAnim / Velocity 的出生抽样先于 Type 的颜色、尺寸抽样。每个采样函数各自重新播种（RT §1.6），这个顺序不改变抽到的值：§6.5 中 Billboard3D、ScaleAnim、RotateAnim、Velocity3D 的抽取都与参照一致。ScaleAnim 基值捕获仍在后处理（115），所以基值仍是 Type 抽样后的值。步骤顺序已确认；步骤 40 = Velocity 是按读的 ctx 偏移推定。
- 新增的步骤属于 RE4 没有的属性：PtFreezer、PointCloudEmitter、Trigger、PtVortexelWind、RibbonJoint*、PtLightningBranchAction、FlowMap、ParticleTimer、DestinationCSV、RayTracingTarget、TypeLightning3D 等。
- 后处理段跳过 0x22 / 0x23 类型，与 RE4 相同。

### 6.4 原生驱动发射器

`tools/Re4Native/mhwilds_emitter.py` 用 Wilds 自己的代码加载条目、建发射器并逐帧推进，结构与 RE4 的 `emitter.py` 相同。对应关系：

| Wilds | RE4 | 作用 |
|---|---|---|
| 0x1c1c0（X, U） | 3d09690（owner, U） | 逐帧驱动：状态机、N 累加，再进 3D / 2D 循环。**rcx 必须是条目描述对象 X**，循环从它读步骤列表；RE4 这里是另一个对象 |
| 0xaaba300（X, args） | 3d06440 | 发射器构造：args+0x10 = E、+0x18 element、+0x20 effect；按 Spawn 对象 +0x3c（RE4 +0x28）定槽数 |
| 0x1dec20（X） | 无（固定调用） | 登记并排序步骤列表 |
| 槽 5（attr, stream, ctx） | 槽 5 | 加载器，流从 UniqueID 开始（类型 id 与声明长度之后） |
| 0x614a0（manager, type, path） | 39ef530 | 资源查找；资源引用计数都在 +0x28，释放 0xb006090 减到 0 才释放 |
| 0x1e1de0 | 4748b80 | Type 构建器的渲染槽分配，结果存块 +0x38 |

E 的偏移：状态 +0x100（RE4 +0x170），槽数 +0x84，粒子槽 +0x120（RE4 +0x190），属性块 +0x2c8，3D 标志 +0xf0 位 0（RE4 +0x168）。UVS 资源的序列头在 +0x58、序列表在 +0x78，比 RE4 各后移 8 字节。

运行前要补的东西都在 `mhwilds_emitter.py` 开头：7 个全零单例、两处字节补丁（0xb073170 返回 1.0、0xb04fb00 返回 0）、两个桩函数、静态 TLS。效果对象 `+0x1c4` 置位 20，否则带父关节的条目每帧走位 3 的重置分支（§6.3）。

原生运行的覆盖面：

- 工厂：281 个里 271 个原生调用成功，与 §2 的模拟结果在两边都成功的类型上逐个一致，另外多出 86 个模拟没跑通的类型。原生失败的 10 个（24、31、62、157、207、239、251、261、278、280）构造函数在 `.trace` 段。138、255 返回空。
- 加载器：语料前 120 个条目里，52 个类型的加载器读的字节数都等于文件里的声明长度。PtLife、PtBehavior、VectorFieldParameter 的加载器还会出错，PtLife 要文件对象（RE4 的 `world.make_file`）。
- 发射器：`exp_wilds_emitter.py` 的 4 个内置条目都能跑完 30 帧：存活数逐帧上升到槽数，最后回到 0。出生段的步骤（12、14、16、19、21）从第 1 帧起每帧对每个槽各调一次；存活段的步骤调用次数等于各帧存活粒子数之和。抽样 60 个条目：18 个有粒子跑完，11 个能跑完但没有粒子（没有 Spawn 等），31 个出错。出错的主要是 GPU 的 Type*（14 个，构建器经 `.trace` 对空对象加引用计数）、PtVortexelWind 一类的逐帧步骤、VortexelCollider、TypeMesh 的网格资源。
- 粒子数值与参照的比对见 §6.5。

### 6.5 与参照比对

参照是 RE4 运行时规格（[RE4-EFX-RUNTIME.zh-CN.md](RE4-EFX-RUNTIME.zh-CN.md)，下称 RT）的公式和 `tools/Re4Native/refsim.py` 的随机数。期望值由 ReeLib 读出的字段值（`VfxStudio.Core` 的 MHWilds 结构）按这些公式算出，`tools/Re4Native/exp_wilds_reference.py` 与原生运行逐帧比较。比较了 7 个 Billboard3D 条目、10 个场景：其中一个用可变 dt，一个在另一个进程里重跑（两次结果相同），一个把合成 .uvs 缩短到 16 个图案，让 PlayMode 2 播到末帧。

粒子槽字段的偏移（括号里是 RE4）：`p+0x18` 编号，`+0x34` 位置（+0x28），`+0x58` 颜色 RGBA8（+0x4c），`+0x5c` Blink 系数（+0x50），`+0x64` 年龄（+0x58），`+0x68` 速度（+0x5c），`+0x80` SpeedCoef（+0x74），`+0x84` / `+0x88` 重力加速度与重力速度（+0x78 / +0x7c），`+0x8c` 四个尺寸槽（+0x80），`+0xa8` 角度（+0x9c），`+0x4c` Velocity3D Flags 位 2 时的位移（+0x40）。`[p+4]` 位 3..6 为 Life 阶段码，`[p]` 位 2..9 为 8 位 alpha、位 10..17 为 UV 序列号、位 18..25 为 UV 帧，与 RE4 相同。各属性的逐粒子记录在 `[E+0x128] + 槽号 × [E+0xd0] + [块+8]`：Life 的内容与 RE4 相同（remain、A、A+K、T）；RgbCommon / RgbWater 是三个通道各一组 (remain, 时长, 阶段码)，后接各通道的系数（float）。播种函数 0xb0510f0 与 RE4 的 3d09990 指令相同，读 E.state `+0xa8`（RE4 +0x120）与 E.seed `+0x98`（RE4 +0x110）；salt 也与 RE4 相同。

| 项 | 规则（RT 章节） | 结果 |
|---|---|---|
| 出生时刻 | §2.3.2 的倒数与 LoopNum；每批 SpawnNum、IntervalFrame 的抽取（salt 1，key 0）；每批后的 E.state | 9 个场景的批次时刻、每批粒子数、每批后的 E.state 都一致，含 IntervalFrame 30..60 随机、EmitterDelayFrame 115、可变 dt |
| Life | §1.6.3 的抽取（salt 0x29）；§2.4.2 模式 0 的逐帧阶段码与 alpha | 88 个粒子全部一致 |
| 积分 | §3.2：`gvel += n·accel; pos += (vel + (0,1,0)·gvel)·n; vel *= powf(coef, n)` | 2,434 步逐位一致 |
| 出生位置 | §3.1：Cylinder、Sphere 的 UseExtension 采样（salt 0x30），`p·R` | 24 个粒子，误差 ≤ 3.7e-7 |
| 出生速度 | §3.2 Normal：`d = (Size − 1) ⊙ (P·R⁻¹) + Offset`，归一化后乘 R（Flags 位 2 时不乘）；Speed、GravityRate、SpeedCoef 的抽取（salt 0x23） | 42 个粒子，误差 < 7e-9 |
| Billboard3D | salt 0x0f：SizeScalar、SizeX、SizeY、Rotation 依次抽取，spread 为 0 的不抽 | 88 个粒子全部一致 |
| 出生颜色 | §4.2：Flags 位 4 时先多抽一次 u，每个字节 `trunc(lerp(Color, ColorRange, u))`，四个通道共用 u；之后每帧不变 | em0009_00_204 的 8 个粒子、295 帧一致；另在 24 个 em0009 条目上用临时脚本跑过一次，76 个粒子都一致 |
| ScaleAnim | §3.3：`U = base + Add·t`（Coef 1）或 `base + Add·c·(1 − c^t)/(1 − c)`（Coef 0.99）；抽取 salt 0x27 | 2,009 帧，误差 ≤ 3e-8 |
| RotateAnim | §3.4：速度、系数、方向的抽取（salt 0x25）；每帧 `angle += n·v`，折回 [0, 2π)，再 `v *= powf(c, n)` | 911 帧，误差 ≤ 3.6e-7 |
| UV 帧 | §4.1：SequenceNo、PatternNo（起始帧）、PlaySpeed 的抽取（salt 0x2b，PlaySpeed 为 MinMax 线性插值）；每帧 `acc += dt`，到 T = (1/60)/speed 时前进 `int(acc/T)` 帧，`acc = frac(acc/T)·(1/60)`；PlayMode 0 不动、1 回到 0、2 停在末帧并杀死粒子（下一帧消失） | 2,596 帧一致，含随机 PlaySpeed 1..1.25、1..1.5，以及 6 次 PlayMode 2 的杀死 |
| UVSequenceModifier | §4.1：从下一帧起替换 T，`speed = m·Init + (1 − m)·Final`，m 从 1 开始、每帧 `m *= powf(c, n)` | em0021 的 918 帧一致 |
| Rgb* 寿命 | §4.9：Keep 段系数 1，Vanish 段 remain/V，结束为 0；RgbWater 的 GtoB 系数再乘 GtoB。第一个启用寿命的通道的 Appear、Keep、Vanish 是第 1..3 次抽取（RgbCommon salt 0x7625a743、RgbWater 0xe107eec2，key = 槽号） | RgbCommon（slinger_055_jimen 的绿、红两层）和 RgbWater（em0009_00_204 的 GtoB）共 957 项一致 |

把参照里的任意一个字段值改一点（速度、Offset、KeepFrame、旋转范围、ScaleAnim Add、形状半径、图案区间、IntervalFrame、ColorRange、PlaySpeed、PlayMode、Modifier 系数、Rgb 的 Keep / Vanish），对应的那一项就报 FAIL，其他项不变。所以上表也说明这些字段在 C# 结构里的位置是对的。

与 RE4 不同的地方：

- **第一批不推进 E.state。** 重启时 E.state = xs(seed)，第一批用它抽取、出生的粒子也看到它；之后每批先抽取，再推进一次 xs。RE4 的第一批也推进，所以它的第一批粒子看到 xs(xs(seed))（`tools/Re4Native/README.md` 的 E.state 一行）。依据是两个带 FixRandomGenerator 的条目：em0021_00_206（种子 1，第一批在第 115 帧）和 slinger_000（种子 406056259，第一批在第 1 帧）。
- **UVSequenceModifier 的 Flags 1 走指数模式。** RT 写的是位 1 置位时第三项为系数（指数模式），否则为帧数（线性模式）。em0021_00_206 的 Flags 为 1（位 1 清零），但按指数模式（m 从 1 开始）918 帧全部一致，按线性模式几乎全错。可能是 Wilds 改了位的含义，也可能是 RT 的这一条有误；RE4 侧没有原生验证过。

RT 没有写、原生运行中观察到的：

- 槽满时这一批不出粒子，但仍消耗一次 LoopNum；同一帧死亡的粒子要到下一帧才让出槽位。slinger_055_jimen（3 个槽、每 2 帧一批、寿命 6 帧）的第 7、15 帧因此没有出生。
- Rgb* 的寿命计数在出生那一帧就减 1（Life 的计数不减）；结束后 remain 继续往负数减，阶段码为 9。
- Rgb* 用槽号作 key，所以 E.state 不变时同一个槽先后出生的粒子抽到相同的时长（jimen 的 0 号、3 号粒子）。

替身的影响：

- 合成的 .uvs 每个序列只有 16 个图案时，PlayMode 2 的条目（UVSequence Flags 低 2 位为 2，如 em0002_00_800 的 554）很快播到末帧并杀掉粒子：em0002 的粒子只活 6 帧，Life 是 49 帧。这是 RT 写的行为，不是错误；但真实 .uvs 的图案数不同，寿命也会不同。其余场景把合成序列加长到 255 个图案。真实的 .uvs 不在解包目录里。

没有比对的：

- 顶点颜色与着色器里的合成（RT §4.10 第 4 步起）、ColorRate / Intensity、RgbCommon 的红层系数（AlphaToB 驱动，记录里第 7、8 个字在红层结束后变为 1.0，含义没有对）、Rgb* 的 Appear 段和第二个通道的抽取位置（样本里都是定值）。
- Noise、ParentOptions 跟随、形状的均分模式（RangeDivide*）、GPU 类型、UV 的翻转 / 倒放（Reverse 只在 CPU 路径影响帧号，样本里都关着）。

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

原生运行后又改了以下几处，同样字节不变（两份语料往返与基线相同，Wilds 骨骼对齐与基线相同，Re4MultiEntryTests 50/50）：

| 类型 | 改动 | 依据 |
|---|---|---|
| ShaderSettings（≥ MHWilds） | BackfaceLightRatio 后新增 LightingGrayscaleRatio；LightingVelocityOffset 在 MHWilds 重新出现；toggle_dd2、dd2_unkn2 只留给 DD2。改后 +72..+136 依次是 LightingGrayscaleRatio、MieShadowRatio、LightingOffsetX/Y/Z、LightingVelocityOffset、InShadowDelay、TimeToFullShadow、OutShadowDelay、TimeToNoShadow、dd2_unkn1、DetonemapRatio、VolumeDepth、VolumetricIntensityBlend、DeinterlaceDistance、AlphaCoverage、SSRAlphaThreshold。原来从 +72 起整段错一个字。mhws_unkn_short → ShadingRates | 构建器在 400 份语料载荷上的标记实验（§5）；+136 → 块 0x98（SSRAlphaThreshold，构建器会截断标记值，单独用 0.25 / 0.5 验证）；+140 只在 Flags 位 13 置位时写到块 0x9c（StencilTarget）；+156 的 u16 写到五个 *ShadingRate 共用的块 0xbc。+112、+148、+152 构建器不读 |
| FakeDoF | NearDistance → Near；其余四个字段名与引擎一致 | 解析器 + 构建器实跑，五个字逐字原样拷贝 |
| PtVortexelHeatSource | Radius、OverwriteType、OverwritePriority（u16）、DurationFrame | 内层解析器 0xd004690；加载器区间 14 字节 = 4 + 4 + 2 + 4 |
| FluidParticleEmitter | DensityScale、VelocityScale、SizeScale、PressureScale（+4..+16）；+0 不在解析器里，保留 Flags | 内层解析器 0xd00f000 + 构建器 |
| RepeatArea | Area → Size（Vector3），Unkn → FadeoutStart | 内层解析器 0xd00b2c0 + 构建器 |
| FluidParticle2DSimulator | 14 个字段命名：VortexelWindTransitionRate / Threshold / MaxSpeed（+24..+32）、PostEdgeDropoff（+48）、DensityColorMin / Max、DensityAlphaMin / Max（+52..+64）、SourceDensityScale / SourceVelocityScale（+80、+84）、WaterVelocityScale / WaterVelocityAddRate（+96、+100）、InternalInherit（+132）、Buoyancy（+136）；这些位置由 UndeterminedFieldType 改为 float（解析器按浮点解析） | 内层解析器 0xcffb940 + 构建器；加载器定长段 168 字节，与 C# 的 Flags .. dataCount 一致 |

运行后确认 C# 已经正确、没有改动的：UVSequenceModifier（三对值与解析器一致，第三对双名 PlaySpeedChangeTime / Coef）、AngularVelocity3D（全部原样拷贝，AddRadiusCoef +24、AddRotationCoef +76 与解析器一致）、Spawn（SpawnNum +8、IntervalFrame +16；其余字段构建器会换算）、PlaneCollider（+4 起与解析器一致）。

exe 给出了、但没有改的：

| 项 | 原因 |
|---|---|
| Velocity3D `DirectionVectorX/Y/Z`（引擎名 DirX/Y/Z，两个 exe 相同） | 离线模拟器与预览按现名读取，改名要连带改它们 |
| VolumetricLighting（VolumeDepth、IntensityToAlphaBlendRatio、ShadowToLightRatio、BackfaceLightRatio，16 字节）、ShapeOperator（Operator、Position、Size、Rotation、Radius，44 字节）、VolumeBufferInfluence（0x08 起 Threshold、BounceRate、DragRatio，20 字节） | C# 是不透明类，语料里没有实例；可按左列结构化，需要单独提交 |
| FadeByEmitterAngle | +16 / +20 两个 float 在解析器里是一对 FadeinRange（块 0x24，8 字节），采样方式没读，保留 FadeInStart / FadeInEnd；Cone、Spread 在块里各占 8 字节（另一半由构建器算出），+0 与 +24 解析器里没有名字 |
| PlaneCollider +0 | 解析器把 RotationOrder 写进这个字，但语料取值（264、650、810 等）是位域，保留 Flags；RotationOrder 占哪几位没读 |
| EmitterShape3D | 槽 20 落到与 AlphaCorrection 共用的解析器（HiPass / Max / LifePow0），不是它自己的，没有名字可用；构建器的拷贝关系已记在 `bm_EmitterShape3D.json` |
| `*MaterialClip` 的 clipBits 位数 | 所有材质 Clip 共用加载器 0xaf0e3c0，位数随材质参数变化；C# 的 `BitSet(4)` 只是占位，读到第 5 位时 ReeLib 记一条错误日志（TypeRibbonLengthMaterialClip，语料 21 处），写出字节不受影响 |
| 第 1 节列出的 11 个混淆类型 | vtable 已补进附录。EmitterHSV（239，载荷 84）、PtLightningColliderAction（125）、各 Expression（24、62、261、280）的解析器是空函数，没有字段名；PtVortexelHeatSource（207）、RepeatArea（251）、FluidParticle2DSimulator（157）、FluidParticleEmitter（278）已改名，见上表 |

## 8. 复核

脚本在共享笔记目录 `side-session-notes/local_73f294ed…/wilds-exe-scripts/`。前几个只读 exe；`emu.py` 起的脚本会执行 exe 代码（模拟或原生），都在独立子进程里跑，不碰游戏进程、不写游戏目录：

| 脚本 | 产出 |
|---|---|
| `wk.py` | PE 读取、E8/E9 与 rip 相对引用索引（缓存为 npz） |
| `wreg.py` | §2 的工厂注册表：下标 → 工厂、对象大小、构造函数 |
| `ctors.py` / `table.py` | 构造函数链 → 类型 id 与 vtable |
| `fmap.py` / `allmap.py` / `diff.py` | §5 的字段名 → 文件偏移，以及与 C# 字段的逐项对照（`diff.txt`） |
| `loop.py` / `seq.py` | §6.1 的三遍循环与调用序列 |
| `steps.py` | §6.3 的步骤表与所属属性（静态部分） |
| `emu.py` | unicorn 模拟：映射 exe、补 KUSER_SHARED_DATA / PEB / TLS、AVX 指令单步补丁、int3 函数替换 |
| `e4.py` | §2 的工厂逐个模拟，结果 `emu_fac.json` |
| `e5.py` | §6.3 的槽 6 与 0x386e3e0 模拟，读两条列表，结果 `emu_steps.json` |
| `wn.py` | 转发到 `tools/Re4Native/mhwildsnative.py`（映射 Wilds exe，分配器 0xb020830 / 0xf5b0 换成线性分配） |
| `n8.py` / `n9.py` | §5 的构建器原生标记实验，结果 `bm_*.json`；`W_GLOBALS`、`W_PATCH` 环境变量给 Spawn / EmitterShape3D 补单例与桩函数 |
| `p20.py` | 内层解析器形式的字段名 → 块偏移 |
| `rawdump/` | C# 工具：从语料读出指定类型的载荷字节（只读内存流，写到内存里的 FileHandler） |
| `tools/Re4Native/exp_wilds_emitter.py` | §6.4：内置条目或 `sweep <entries.json>`，每个条目一个子进程，报告步骤列表、槽数、逐帧存活数、执行过的步骤 |
| `tools/Re4Native/exp_wilds_reference.py` | §6.5：10 个场景，每个一个子进程，逐项对照 RT 的公式与 `refsim.py`，最后一行 `RESULT` 汇总各项 PASS / FAIL |

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
| 24 | TypeBillboard2DExpression | 22 | 0x88 | e0f6350 | 自定义 |  |  |
| 25 | TypeBillboard3D | 23 | 0x90 | e0f6560 | 76 | 76 | 代码 |
| 26 | TypeBillboard3DExpression | 24 | 0x88 | e0f6230 | 自定义 | 80,92,108… | 代码 |
| 27 | TypeBillboard3DMaterial | 25 | 0x100 | e0f6670 | 52 | 362,364,370… | 代码 |
| 28 | TypeBillboard3DMaterialClip | 26 | 0x98 | e0f6780 | 自定义 | 112,296,308… | 代码 |
| 29 | TypeBillboard3DMaterialExpression | 27 | 0xe0 | e0f68a0 | 自定义 | 128,144,152… | 代码 |
| 30 | TypeMesh | 28 | 0x198 | e0f6b90 | 自定义 | 322,330,342… | 代码 |
| 31 | TypeMeshClip | 29 | 0x98 | e0f6cd0（按位置认定） | 自定义 | 100,112,124… |  |
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
| 62 | TypeRibbonChainExpression |  | 0x88 | e0f92f0 | 自定义 | 108,132,340 |  |
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
| 125 | PtLightningColliderAction |  | 0x58 | e0fda60 | 自定义 | 12 |  |
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
| 157 | FluidParticle2DSimulator |  | 0x150 | e100240 | 168 + 自定义 | 214,230,246… |  |
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
| 207 | PtVortexelHeatSource |  | 0x50 | e103ba0 | 14 | 14 |  |
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
| 239 | EmitterHSV |  | 0x98 | e105ce0 | 84 | 84 |  |
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
| 251 | RepeatArea |  | 0x60 | e106ba0 | 自定义 | 20 |  |
| 252 | EmitMask |  | 0x48 | e106ca0 | 4 | 4 |  |
| 253 | ParticleTimer |  | 0x38 | e106da0 | 自定义 |  | 0x530 |
| 254 | Trigger |  | 0x58 | e106f20 | 自定义 | 12 | 0x5a8 |
| 255 | CustomComputeShader |  |  | — |  |  |  |
| 256 | VolumeBufferInfluence |  | 0x58 | e107020 | 20 |  | 0x5b8 |
| 257 | VolumeBufferOperator |  | 0x48 | e107130 | 8 |  | 0x5c8 |
| 258 | TypeGpuBillboard | 243 | 0x98 | e107240 | 自定义 | 76 | 代码 |
| 259 | TypeGpuBillboardExpression | 244 | 0x88 | e107340 | 自定义 | 96,120,124… | 代码 |
| 260 | TypeGpuPolygon | 245 | 0xa0 | e107460 | 自定义 | 88 | 代码 |
| 261 | TypeGpuPolygonExpression |  | 0x88 | e107560 | 自定义 | 132,164,196… |  |
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
| 278 | FluidParticleEmitter |  | 0x60 | e1088f0 | 20 | 20 |  |
| 279 | FluidParticleEmitterTarget |  | 0x78 | e1089f0 | 20 + R | 92,96,100… | 代码 |
| 280 | FluidParticle2DSimulatorExpression |  | 0x88 | e108af0 | 自定义 | 152 |  |
| 281 | FluidParticleEmitter3D |  | 0x50 | e108c80 | 自定义 |  |  |
