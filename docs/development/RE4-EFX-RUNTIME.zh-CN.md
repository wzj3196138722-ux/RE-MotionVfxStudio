# RE4R 粒子特效运行时规格（EFX 3539837）

适用：生化危机 4 重制版（RE4R），EFX 版本 3539837，re4.exe 当前补丁。RVA 只对该补丁有效。
读者：编写或核对离线 EFX 模拟 / 预览器（`VfxStudio.Renderer/Vfx/EfxSimulator.cs` 及独立的离线模拟器）的开发者，以及查证某个字段行为的人。
字段名以 `VfxStudio.Core/Formats/EFX/` 的 C# 读取器为准，成对数值类型见 `EfxPairTypes.cs`。笔记里的旧名在本文中一律换成当前 C# 名。

## 0. 范围与约定

### 0.1 状态标记

| 标记 | 本文用法 |
|---|---|
| 已确认 | exe 指令或着色器 DXIL 直接读出，且与 RE4 官方语料一致；笔记本身没有标出疑点 |
| 部分待确认 | 一部分环节读码确认，其余靠代码形态、命名或语料推断；或只在一条渲染路径上读过 |
| 折中实现 | 证据不足，模拟器必须先选一种做法才能运行；随时可能被推翻 |
| 待清理 | 已知与引擎不符的现有 C# 布局或旧理解 |

除非另行注明"实机"，所有结论均来自静态读码与语料统计，没有在游戏内运行验证。

### 0.2 单位与时间

| 量 | 约定 | 状态 | 依据 |
|---|---|---|---|
| 帧 | 1/60 秒。文件中所有"帧"字段按 60 Hz 计 | 已确认 | SEM, LF |
| `dt` | 本 tick 的秒数，float，ctx+0x128；正常为实际帧间隔，追帧子步时固定 1/60 | 已确认 | SEM, BL, ORD |
| `n` | `n = dt·60`（常量 60.0 @79c2ee0），60 fps 时为 1。Velocity3D 积分、ScaleAnim 年龄、RotateAnim、Transform3DModifier、UVSequenceModifier 使用 | 已确认 | SEM, SCA, ROT, MOD |
| `step`（= §1.1 的 N） | 整数帧步长，`trunc(E+0x10c·60)`，余量带到下一 tick（3d09690），存于 ctx+0x12c / 发射器帧 +0x14。Life、Spawn 倒计时、ParentOptions 保持计数、RibbonFollow / RibbonTrail / PolygonTrail 采样累加器、RgbCommon / RgbWater 寿命段、StrainRibbon 断裂计数使用 | 已确认 | LF, SP, PO, TB, RT, PT, RW, ST, ORD |
| 粒子年龄 | 粒子 +0x58，float 帧数，每 tick `+= dt·60`（3d09228） | 已确认 | SCA |
| 其他时钟 | Blink、UVSequence 直接累加 `dt`（秒）；RibbonChain、RibbonFixEnd 用自己的 60 Hz 累加器 `steps = floor(acc·60)`；RibbonFixEnd 的历史采样和 ReleaseInterpolation 按游戏帧推进（随帧率变化） | 已确认 | BL, UVS, TC, FE |
| 长度 | 米（世界单位） | 已确认 | SEM |
| 角度 | 文件与运行时为弧度；引擎文本解析器与 Expression 槽按度换算（×π/180、×57.29578） | 已确认 | T3D, ROT, EXP |
| 颜色 | `via.Color` = 一个 u32，R 在最低字节、A 在最高字节，运算时 /255 | 已确认 | TA, RW |
| 矩阵 | 行向量约定 `v' = v·M`，平移在第 4 行 | 已确认 | T3D |

两个时钟的来源与使用者见 §1.1。

### 0.3 随机数

- 生成器：xorshift32，`x ^= x<<13; x ^= x>>17; x ^= x<<5`；`u = x·2⁻³²`（常量 @79c2c4c），u ∈ [0, 1)。已确认（SEM, BL, FRG）。
- 整数抽样多数直接用 32 位原始值取模（`rand % k`），不经过 u。
- 种子与各属性的抽取顺序见 §1.6。

### 0.4 区间对的语义

文件里成对数值只有三种存法：`via.Range`（两个 float，先 s 后 r）、`via.RangeI`（两个 int，先 r 后 s，即 C# `{int r; int s;}`）、`via.Int2`（先 x 后 y）。
C# 按引擎读码把它们换成下表的语义类型，字节不变，字段顺序 = 文件顺序。仍声明为 `via.*` 的字段表示语义尚未核对，按"第一个 / 第二个"理解。

| C# 类型（成员按文件顺序） | 抽样 | 失效与边界 | 状态 | 依据 |
|---|---|---|---|---|
| `CenterSpread`（center, spread） | `center + (2u−1)·spread`，每粒子出生时一次 | 有门控的字段（ScaleAnim、Billboard3D 尺寸与旋转、Velocity3D Speed / GravityRate）在 \|spread\| ≤ 1e-6 时直接取 center 且**不消耗**随机数；其余字段始终消耗（§1.6.3） | 已确认 | SEM, SCA, BL, MOD, ORD |
| `CenterSpreadI`（center, spread） | `center + randint[−spread, +spread]`（`rand % (2·spread+1)`） | | 已确认 | SEM 3d1aee5, PC 3d6462b |
| `MinMax`（min, max） | `min + (max−min)·u` | | 已确认 | UVS 3d1ed02, MOD 3d9c328, PC 3d6505b |
| `MinMaxI`（min, max） | `min + rand % (max−min+1)`（无符号除法），两端都含 | `max = min−1` 除以零（游戏崩溃）；`max < min−1` 得到巨大值 | 已确认 | SP 3d14f5f, LF 3d1bf56 |
| `MinMaxExclusiveI`（min, maxExclusive） | UVSequence：`min + rand % max(maxEx−min, 1)`，仅 min ≠ maxEx 时抽；Mesh.PartsStartNo：`min + rand % (maxEx−min)`；PtCollision.BounceNum：`trunc(min + u·(maxEx−min))` | 固定值写 `{n, n+1}` | 已确认 | UVS 3d1ec5b, TD 3d30eb6, PC 3d650ad |
| `MinExtra`（min, extra） | `min + u·extra` | | 已确认 | PC 3d63fff, ST, TB |
| `MinExtraI`（min, extra） | `min + rand % (extra+1)` | `extra = −1` 除以零 | 已确认 | SP 3d14c12, PO 3d36b5d, RW 3dfe120 |
| `InnerOuter`（inner, outer） | 在两者之间按形状采样的 t 插值（§3.1） | | 已确认 | SEM |
| `StartSpan`（start, span） | `start + t·span`，t 为随机数或均分位置 | | 已确认 | SEM |
| `ClampRange`（min, max） | 不随机，把结果夹在 [min, max] | | 已确认 | SEM |
| `AnchorPair`（width, length） | 两个独立锚点，不随机 | width 只在 GPU 着色器路径读到 | 部分待确认 | SEM, SHE |

`RibbonFixEnd.ReleaseFrame` 声明为 `MinMaxI`，但读码得到的是 `min + trunc((max−min)·u) + 1`（3d91b08..3d91b3d，+1 在 3d91e5b），见 §4.3.7。

### 0.5 证据索引

笔记（共享笔记目录 `side-session-notes/local_7b1b3ae8…/`，SEM 在 `local_9026d044…/`）：

| 代号 | 文件 | 代号 | 文件 |
|---|---|---|---|
| T3D | skeleton-transform3d.md | SS | shadersettings.md |
| PO | skeleton-parentoptions.md | UVS | uvsequence.md |
| SP | skeleton-spawn.md | SCA | scaleanim.md |
| LF | skeleton-life.md | ROT | rotateanim.md |
| SEM | re4-efx-semantics-from-exe.md | FBD | fadebydepth.md |
| TA / TB / TC / TD | types-exe-A / B / C / D.md | FBA | fadebyangle.md |
| FE | types-exe-fixend.md | SFD | shader-fadeangle-distortion.md |
| ST | types-exe-strain.md | BL | blink.md |
| RT | types-exe-ribbontrail.md | FRG | fixrandomgenerator.md |
| PT | types-exe-polygontrail.md | AC | alphacorrection.md |
| SHE | types-shader-E.md | DIS | distortion.md |
| PL | ptlife.md | PC | ptcollision.md |
| RW | rgbwater.md | MOD | modifiers.md |
| EXP | expressions.md（附 expr-slots-A.json / expr-slots-B.json） | ORD | exec-order-rng.md（参考实现 ORD/refsim.py） |

其他输入：

| 输入 | 说明 |
|---|---|
| re4.exe | 当前补丁，静态反汇编（capstone）。RVA 为该补丁的相对地址 |
| 着色器 | `natives/stm/systems/shader/vfx/*.vsdf.221007880`（billboarddraw、ribbonlengthdraw、ribbonlengthcompute 等，GpuParticleVS 家族）与 `systems/shader/primitivevfx*.vsdf`（Polygon3DConstant 家族），按块切出后 `dxc -dumpbin`；行号指反汇编文本的行 |
| 语料 | RE4 官方 .efx 全量解析（re4.jsonl，50,761 个发射器）。计数写在"依据"栏，形如"语料 2954/3755" |
| 实机 | skill `re-efx-authoring/references/attributes.md` §8 的逐粒子探针 |

---

## 1. 执行顺序与随机数

本章依据为 exec-order-rng.md（下记 ORD），参考实现为 ORD 脚本目录中的 `refsim.py`。下文 `E` = 发射器实例（`[ctx+0x120]`），`ctx` = 每 tick 在更新函数栈上构造的上下文。

### 1.1 一个 tick：驱动与帧步长

| 步 | RVA | 内容 | 状态 | 依据 |
|---|---|---|---|---|
| 外层驱动 | 3d03fa0（及 3d040d0） | 需 `E+0x168 & 0x400`。追帧次数 `k = update+0x88`：`k > 1` 时以**固定 dt = 1/60**（7a04e68）调用 3d09690 共 k 次；否则以实际 dt（`update+0x80`）调用一次。每次之后运行 3d06bf0 | 已确认 | ORD 3d03fe3..3d03ff5 |
| 发射器状态机 | 3d09690 | 读 `E+0x170 & 7`：状态 1 清零时钟 `E+0x108 = E+0x10c = 0` 并启动发射器（3d35e00）；状态 2 `E+0x108 += dt`；3、4 切换运行位（状态名未解码）。运行时 `E+0x10c += dt`，**`N = trunc(E+0x10c·60)`**，以 N 调用粒子更新，再 `E+0x10c −= N/60`（余量保留） | 已确认（状态名部分待确认） | ORD 3d098ec..3d098fd |
| 路径选择 | 3d098a0 | `E+0x168 & 1` 置位 → **3d08480**（3D 族：ctx 类型 2..0x23，Billboard3D、Mesh、Polygon、条带…）；清零 → **3d079c0**（2D 族：Billboard2D、Velocity2D、EmitterShape2D） | 分支已确认；"3D / 2D"含义按被调函数推定 | ORD |
| 前导 | 3d04160 | 构造 ctx：`ctx+0x128 = dt`（float 秒），`ctx+0x12c = N`（int 帧） | 已确认 | ORD |

两个时钟：

| 量 | 取值 | 使用者 | 状态 |
|---|---|---|---|
| `n`（float 帧） | `dt·60`，每 tick 由本 tick 的 dt 重算 | 粒子年龄、重力速度、位置积分、`powf(SpeedCoef, n)`、ScaleAnim 的 t、RotateAnim、Transform3DModifier、UVSequenceModifier；Blink 与 UVSequence 直接用 `dt` | 已确认 |
| `N`（int 帧，即 §0.2 的 `step`） | `trunc(E+0x10c·60)`，余量带到下一 tick | Life 计数（3d1c161）、Spawn 计时（倒计时减 `min(counter, N)`，3d15268）、SpeedDelayFrame / GravityDelayFrame（3d1af5d）、RgbCommon / RgbWater 寿命段、各采样间隔累加器 | 已确认 |

60 fps 时 n = 1、N = 1（浮点误差可能让个别 tick 出现 N = 0 再 N = 2）；30 fps 时 n = 2、N = 2，连续量与帧率无关，阶段计数一次走两帧。追帧子步一律 dt = 1/60。要与游戏一致，模拟器必须逐 tick 输入真实 dt，同时维护 n 与 N 的余量累加器，不能固定 60 Hz。已确认（ORD §5）。

### 1.2 属性遍历

每 tick 发射器把属性数组 `E+0x288[0..n)` 完整走三遍：先对全部属性调用 vtable slot 22，再全部 slot 23，再全部 slot 24（3d08480：3d086a4 / 3d086f4 / 3d08734；2D 循环 3d079c0 只有 slot 22、23 两遍，3d07b88 / 3d07bd4）。slot 22 返回后若 `ctx+0xd0` 字节位 0 置位，整个 tick 中止。三遍后调用 3d88690 收尾。已确认（ORD）。

数组按 entry 的属性链表顺序填充（构造函数 3d06440，循环 3d06870..3d068b2），引擎没有排序（未找到排序代码：部分待确认）。语料 50,761 个发射器的链表全部按 RE4 类型号严格升序，所以访问顺序 = 类型号顺序：

```
FixRandomGenerator 1 → Transform3D 9 → Transform3DModifier 11 → Transform3DClip 12 → Transform3DExpression 13 → ParentOptions 14
→ Spawn 16 → Type* 21..76 → Velocity3D 81 → RotateAnim 84 → ScaleAnim 87 → Life 91 → UVSequence 93 → EmitterShape3D 102
→ AlphaCorrection 104 → Blink 108 → Noise 109 → …
```

编辑器写文件时按类型号升序排列属性，与官方文件一致；乱序文件的行为没有核对过（例如 Transform3DModifier 排在 Transform3D 之前时的结果）。

有实际工作的槽（其余为空函数 0xae850）：

| 遍 | 属性（类型号） | RVA | 作用 |
|---|---|---|---|
| 22 | Spawn（16） | 3d15a90 | 块指针存 `ctx+0x178`；重启 tick 令 `E.state = xs(E.seed)`；`E+8 & 2` 时 3d154d0 初始化计时；然后 **Spawn 调度 3d14c20**（批次判定、SpawnNum / IntervalFrame 抽取、`E.state` 推进）。SpawnExpression 的应用器也在 slot 22 |
| 23 | FixRandomGenerator（1） | 3d88680 | `ctx+0x170` |
| 23 | Transform3D（9） | 3d145a0 | animated ← base（3d146ae），组合矩阵；`ctx+0x138` |
| 23 | Transform3DModifier / Clip / Expression（11 / 12 / 13） | 3d9b300 / 3d655a0 / 3d46bc0 | 改写 animated TRS 并重组；按类型号总在 Transform3D 之后 |
| 23 | ParentOptions（14） | 3d35ef0 | 父级跟随；`ctx+0x268`；在 Transform3D 之后、Spawn 之前 |
| 23 | Spawn（16） | 3d15c90 | 距离发射，使用本 tick 的发射器变换 |
| 23 | Type*（21..76） | 如 3d18710（Billboard3D）、3d308e0（Mesh） | 写 `ctx[0]` 类型与 `ctx+0x1a0` 块 |
| 23 | Velocity3D（81） | 3d1a7a0 | `ctx+0x1c0`；由上 tick 以来的位移计算继承用的发射器速度 |
| 23 | RotateAnim 84 / ScaleAnim 87 / Life 91 / UVSequence 93 | 3d1ca60 / 3d1d720 / 3d1bd50 / 3d1e880 | `ctx+0x1e8 / 0x1f0 / 0x1e0 / 0x1f8` |
| 23 | EmitterShape3D（102） | 3d24710 | `ctx+0x208` |
| 23 | AlphaCorrection 104 / Blink 108 / Noise 109 | 3d25e80 / 3dc7950 / 3dc6f60 | `ctx+0x210 / 0x230 / 0x238` |
| 23 | 各 Expression | 各自应用器 | §6 |
| 24 | **EmitterShape3D** | 3d210d0 | 重建本 tick 的 R 与 R⁻¹（3d242d0）。在全部 slot 23 之后、粒子循环之前，所以 Velocity3D Normal 用的是当前 R |
| 24 | RgbCommon / RgbWater | 3dec520 / 3deedf0 | 发射器级常量 |

FadeByAngle 的 slot 23 只在发射器实例建造（3d09690 → 3d08480 / 3d079c0 的首次调用）时有效，见 §4.6。状态：已确认（ORD §2、ctxmap）。

### 1.3 粒子循环（3d08480，3D 路径）

三遍属性之后，按槽号 `i = 0 .. E+0xfc−1` 顺序处理每个槽（正常 tick 3d08d90..3d09346；重启 tick 的初始化遍 3d088e0 结构相同；`& 8` 为清空全部粒子）。单槽顺序（`p = E+0x190 + i·0x140`）：

| # | 步骤 | RVA |
|---|---|---|
| 1 | **击杀检查**：`[p+4]` 位 9 置位时清 alive、相位 := 9、年龄 = 0，PtLife `3d3d110(…, 4)` + 解链 `3d3df80`，PtBehavior `3d3a760`。PtCollision 的 Kill 设置该位，于**下一 tick** 访问该槽时处理 | 3d08dd1 |
| 2 | 投影模式出生（仅 PtCollision Flags 位 4） | 3d08e3b..3d08f5f |
| 3 | **认领**：`3d15e60`（Spawn 块）。槽空闲且本批剩余 `blk+0x1c > 0` 时 `[p] |= 3`（alive \| new）、`blk+0x1c −= 1`、写插值比例、末批置 0x20000；随后另两个出生源 3d43b90（ctx+0x2f0）、3d45520（ctx+0x2f8） | 3d08f8a |
| 4 | **编号**：新粒子 `p+0x18 = E.counter++`（`E+0x140`；构造与重启时清零） | 3d08fca..3d08fe1 |
| 5 | **出生前置步骤**（仅新粒子生效）：3dfb4f0 → 3df15e0 → **Life 3d1be80** → 3df17f0 → **ParentOptions 逐粒子 3d36a30** → TexelChannelOperator 3dcaed0 → AssignCSV 3df20e0 → 3dfb360 → **EmitterShape3D 采样 3d24ce0 → 3d24d00**（`[p] & 0x10000002 == 2` 时）→ 3dfb360 → ShaderSettings 3d28480 | 3d09005..3d090f6 |
| 6 | 此时已死（`[p] & 3 == 0`）：PtLife 状态 4 + 解链 | 3d09124..3d09195 |
| 7 | 否则：3dfda60（`p+0x28 → p+0x1c` 保存上一位置）、复位逐帧默认值；**`age += dt·60`**（3d09224）；**调用 3d05350**（§1.4）；PtBehavior；类型后处理（3d058d0 → 3d04e00：类型 2 → 3d17270，6 → 3d31390，0xa → 3d6f5c0，然后 **ScaleAnim 基值捕获 3d1dd00**；类型 0x22/0x23 跳过）；PtLife 状态步（比较调用前后相位）；`[p] &= 0xeffffffd`（清 new） | 3d0919a.. |
| 8 | 循环后：粒子条带类型（0x22/0x23）批量构建；PtLife 3d3e0a0、PtColliderAction 3d62230、3db3030、3db4100 的发射器级步骤（带 dt）；`E+0x168 |= 0x80000000` | 3d09550..3d095d3 |

Life 结束在 Life 自身的更新里置相位 9 并清 `[p] & 3`；被清空的槽在同一 tick 的后续认领中即可复用。顶点 / 实例数据不在此循环中构建，而在渲染收集阶段（推定）。状态：已确认（ORD §3）。

### 1.4 逐粒子管线（3d05350，每个存活粒子每 tick 一次）

`D` = 类型分派 3d045d0（跳转表 3d04d70，以 `ctx[0]−2` 为下标：Billboard3D → 3d187e0，Mesh → 3d30b60 + 3d6b900 + 3d56eb0，Polygon → 3d6ee60，NoDraw → 3d6c0c0，RibbonLength → 3d7c730…）。

| # | 调用 | 内容 |
|---|---|---|
| 1 | 3dc9e30 | VanishArea3D |
| 2 | 3d65d10 / 3d662a0 / 3d66ad0 | PtTransform3D、Clip、Expression |
| 3 | 3d956a0、3d97c30 | AngularVelocity 族 |
| 4 | 3dea8e0 | PtPathTranslate |
| 5 | 3dc7050 | Noise |
| 6 | **D，当 `[g+0xeb0] == 0`**（3d05470） | 类型出生 / 更新；Billboard3D 的出生抽样（颜色、SizeScalar、SizeX、SizeY、Rotation）在这里 |
| 7 | **RotateAnim 3d1cb60** | 新粒子抽样，否则逐帧自转 |
| 8 | **ScaleAnim 3d1d830**（r9d = 1） | 新粒子抽 8 个区间，否则重算尺寸 |
| 9 | **Velocity3D 3d1ada0** | 仅 `[p] & 0x10000001 == 1`；`init = ([p]>>1)&1` → 3d1ae00。**普通粒子的初速度在它的第一个 tick 于此设置**（形状采样之后、RotateAnim / ScaleAnim 出生抽样之后） |
| 10 | 3d676b0 / 3d67bf0 | PtVelocity3D、Clip |
| 11 | 3db6ae0 | WindInfluence3D |
| 12 | 3d07680、**积分 3d06d30** | `gvel += n·accel; pos += (vel + gdir·gvel)·n; vel *= powf(coef, n)`；`[p+4]` 位 10 时跳过 |
| 13 | 3d62430 | PtColliderAction |
| 14 | **PtCollision 3d64580** | |
| 15 | 3dfa810（ctx+0x298） | 碰撞后处理 |
| 16 | **D，当 `[g+0xeb0] != 0`**（3d055dc） | 同一分派，移到碰撞之后 |
| 17 | 3dfb850 / 3dfc110、3db4330 / 3db3240 | ctx+0x538 / 0x540 族、程序扭曲 |
| 18 | **Blink 3dc7a20** | |
| 19 | **UVSequence 3d1ebc0** | 新粒子抽样，否则推进帧 |
| 20 | **UVSequenceModifier 3d9c260** | |
| 21 | PtUvSequence 3d6fad0 / 3d702a0、UVScroll 3d1fcb0 / 3d9d8f0 | |
| 22 | **AlphaCorrection 3d25e90** | |
| 23 | FadeBy*（ctx+0x2b8..0x2d0） | RE4 中为空函数 |
| 24 | 3d44220、3de9a70、**RgbCommon 3decb10**、**RgbWater 3def2c0**、3df7080 | |
| 25 | PtColor 3d69ab0 / 3d6a840 / 3d6ae90、3df2c20、3dfa1d0 | |

状态：已确认（ORD §4）。`[g+0xeb0]` 推定为 LegacyMode2018 的姊妹开关 LegacyMode2016，发行版取值未知；**折中实现**按 0（分派在积分之前）。

### 1.5 一个粒子出生时的调用顺序（3D CPU 路径，标准模式）

1. slot 22：Spawn 调度决定本批，抽取下一次 SpawnNum 与 IntervalFrame，本批计数 > 0 时推进 `E.state`。
2. 认领 3d15e60，`[p] = alive | new`，编号 `id = E.counter++`。
3. 出生前置：**Life**（3 次抽取）→ **ParentOptions 逐粒子**（至多 3 次抽取，按槽号播种）→ TexelChannelOperator → AssignCSV → **EmitterShape3D 采样**（位置 = p_raw · R）→ ShaderSettings。
4. `age += dt·60`。例外：重启后的第一批在出生 tick 的年龄为 0，之后各批为 1（60 fps 时；Re4Native 原生运行观察到，分支位置未定位：部分待确认）。
5. 3d05350：Noise → **类型出生抽样**（Billboard3D：颜色、SizeScalar、SizeX、SizeY、Rotation；Mesh：颜色、RotX、SizeX、RotY、SizeY、RotZ、SizeZ、SizeScalar、部件、播放速度）→ **RotateAnim 出生** → **ScaleAnim 出生** → **Velocity3D 初始化** → 积分 3d06d30 遇到"新粒子"位直接跳过（3d06da4），**出生 tick 不积分**，粒子停在形状采样位置；从下一 tick 起按速度推进（Re4Native `exp_emitter.py` 原生运行，已确认）→ 碰撞 → Blink 出生 → **UVSequence 出生** → UVSequenceModifier → AlphaCorrection → Rgb*。
6. 后处理：ScaleAnim 基值捕获（所以基值 = Type 的抽样值）；PtLife 状态 0 / 1 触发。ScaleAnim 的逐帧求值从下一个 tick 开始（需要"存活且非新"）。

`[g+0xeb0] != 0` 时类型抽样移到碰撞之后（仍在 Blink / UV 之前）；基值捕获在后处理中，两种情况相同。状态：已确认（ORD §6）。

2D 路径（3d079c0）的出生管线是 3d04e90：Life 3d1be80 → 3d04e90（其中 RotateAnim 3d04f80、ScaleAnim 3d04fa0、Blink 3d051a7、UVSequence 3d051ca、UVSequenceModifier 3d051fb，Billboard2D 出生 3d17320）→ ScaleAnim 基值捕获 3d1dd00 → PtLife 3d3d110 → 3d3df80。该路径只读过结构：部分待确认。

### 1.6 随机数

#### 1.6.1 模型

引擎没有供各属性依次消耗的发射器级随机流。**每个采样函数每次调用都重新播种一个私有 xorshift32**：

```
xs(x):  x ^= x<<13; x ^= x>>17; x ^= x<<5                (32 位)
seed(salt, key) = xs( xs(salt + E.state + key) + E.seed )   // 3d09997..3d099d5
next():  x = xs(x); return x                               // 第一次抽取已是 xs(种子)
u()   = f32( f32(next()) · f32(2⁻³²) )                     // cvtsi2ss 舍入到 24 位，u 可能恰为 1.0
rnd(c, r) = f32( f32( f32(u()·2.0) − 1.0 ) · r ) + c       // CenterSpread，float32 逐步运算
整数区间直接用 next() 取模（无符号除法），不经过 u
```

| 播种函数 | E 来源 | key |
|---|---|---|
| 3d09990 | `[owner+8]` | 粒子编号 `[p+0x18]`（多数属性） |
| 3d09940 | `[owner+8]` | 槽号（ParentOptions、Noise、Rgb*、PtPathTranslate 等） |
| 3d099e0 | 直接给出 E | 0 或循环下标（发射器级：Spawn 复活、Transform3DModifier、GPU 类型） |

推论：

- 属性的执行顺序不影响任何随机值；值只取决于 `(E.seed, 该 tick 的 E.state, key)`，只有同一属性内部的抽取顺序有意义。
- 同一批粒子共享 `E.state`，只靠 key 区分；相邻两批 state 与 key 都不同。
- 粒子编号 = 认领时的 `E.counter`（发射器生命期内按认领顺序 0、1、2…，重启时清零），**不是槽号**。
- 同一粒子之后再次调用同一采样函数（Velocity3D 的延迟路径、Billboard3D 每 tick、Life 每 tick）会以相同 salt 与 key 重新播种，重放相同的前几次抽取；是否真正抽取由各函数的门控决定。例如延迟到期时的速度方向与出生时相同。

`E.seed`（`E+0x110`）：有 FixRandomGenerator 时取表项（§1.7）；否则 **每个 tick** 在收尾 3d88690 里重新取 `next(G)`（不只构造时取一次），G 为引擎全局 4 字生成器，不可复现；每个粒子用的是它出生那个 tick 的种子。全局生成器全零时（新映射的映像）种子恒为 0，结果确定。已确认（Re4Native 原生运行）。`E+0x118 / 0x11c` 不被本表任何采样函数读取。
`E.state`（`E+0x120`）：构造时 1（3d066e8）→ 重启 tick 的 Spawn slot 22 置为 `xs(E.seed)`（3d15ad4）→ Spawn 调度在 slot 22 中、批次判定之后、粒子循环之前，每个本批计数 > 0 的 tick 推进一次 `xs`（3d15271）。所以本 tick 出生的粒子看到的是推进后的 state，而同一 tick 的批量抽取用的是推进前的 state。LegacyMode2018 置位时两处都跳过。

状态：已确认（ORD §7.1，3d09990 / 3d09940 / 3d099e0；参考实现 `refsim.py` 尚未对照实机数值）。

#### 1.6.2 Salt 表

| salt | 播种函数 | key | 函数 | 属性 |
|---|---|---|---|---|
| 1 | 990 | 0 | 3d14c20（3d14c64） | Spawn 调度抽取 |
| 0xe4bcc19e | 9e0 | 0 | 3d15bbc | Spawn 复活抽取 |
| 8 / 4 | 9e0 / 990 | 0 | 3d9b300 / 3d9abe0 | Transform3DModifier / Transform2DModifier |
| 0x0d | 990 | id | 3d17320 | TypeBillboard2D 出生 |
| 0x0f | 990 | id | 3d187e0 | TypeBillboard3D 出生 |
| 0x11 | 990 | id | 3d30b60 | TypeMesh 出生 |
| 0x1b / 0x1e / 0x20 | 990 | id | 3d6ee60 / 3d77320 / 3d6c0c0 | Polygon / RibbonTrail / NoDraw 出生（名称按函数推定） |
| 0x21 | 990 | id | 3d19230 | Velocity2D |
| 0x23 | 990 | id | 3d1ae00 | Velocity3D |
| 0x25 | 990 | id | 3d1cb60 | RotateAnim |
| 0x27 | 990 | id | 3d1d830 | ScaleAnim |
| 0x29 | 990 | id | 3d1be80 | Life |
| 0x2b | 990 | id | 3d1ebc0 | UVSequence |
| 0x2d | 990 | id | 3d9d8f0 | TextureUnit 族 |
| 0x2e | 990 | id | 3d25970 | EmitterShape2D |
| 0x30 | 990 | id | 3d24d00、3d23060 | EmitterShape3D |
| 0x44 / 0x46 | 990 | id | 3d75aa0 / 3de0e70 | PolygonTrail / 另一条带类型出生（推定） |
| 0x4a | 990 | id | 3d43a90、3d43b90、3d44220 | 类型 150 / 147 出生源 |
| 0x54 / 0x55 | 990 | id | 3d62430 / 3d64580 | PtColliderAction / PtCollision |
| 0x58 / 0x59 | 990 | id | 3d6fad0 / 3d702a0 | PtUvSequence / Clip |
| 0x64 / 0x65 | 990 | id | 3d96130 / 3d97320 | AngularVelocity 族（推定） |
| `[blk+0x18]` | 990 | id | 3d7c730、3d79cb0、3d82850、3d83e60 | 条带类型（salt 为块内值，未追踪） |
| 0x087f7c20 | 990 | id | 3dc7a20 | Blink |
| 0x6f696867 | 990 | id | 3dc9e30 | VanishArea3D |
| 0x519eabd5 / 0x906acc4a / 0xe9787f2f | 990 | id / `[r8+8]` / `[r8+8]` | 3dcaed0 / 3df20e0 / 3df2c20 | TexelChannelOperator / AssignCSV / 类型 232 |
| 0xe2ce689e | 990 | id | 3d9c260 | UVSequenceModifier |
| 0xe1e4ecc7 | 990 | 0 / id | 3db68f0 / 3db6ae0 | WindInfluence3D |
| 0xc526a279 | 990 | id | 3d1fcb0 | UVScroll |
| 0xf62e295d | 940 | 槽号 | 3d36a30 | ParentOptions 逐粒子 |
| 0xd17f868f | 940 | 槽号 | 3dc7050 | Noise |
| 0x7625a743 / 0xe107eec2 | 940 | 槽号 | 3decb10 / 3def2c0 | RgbCommon / RgbWater（每次调用重播种） |
| 0x67d08de3 | 940 | 槽号 | 3dea8e0 | PtPathTranslate |

另有一批 990 salt（0xfe146a8e、0x235c0c1b、0x5db2fcbe、0x523a9250、0x5608fa50、0x160e6b9c、0xc83463ca、0x21289327、0xd71481a7）属于材质变体、Lightning3D 等，未对应到名字；GPU 类型的发射器级种子（9e0）未追踪。状态：已确认（ORD §7.3，saltmap2）。

#### 1.6.3 各属性内的抽取顺序与门控

"有门控"指 \|spread\| ≤ 1e-6 时直接取 center、不消耗抽取；"无门控"指 spread = 0 也照样抽取。

| 属性（salt） | 抽取顺序 | 门控 | 状态 | 依据 |
|---|---|---|---|---|
| Spawn（1，key 0） | 启动时：LoopNum（3d14b60，未解码）、EmitterDelayFrame、IntervalFrame、SpawnNum；**每批：SpawnNum，然后 IntervalFrame**。复活（0xe4bcc19e）：1 次 `min + x % (extra+1)` | 整数取模，无门控；16 位存储截断。每 tick 用当前 `E.state` 重新播种，所以每次都是新流的头几次抽取 | LoopNum 与 3d154d0 内顺序部分待确认，其余已确认 | ORD 3d14c64..3d14f97 |
| Life（0x29） | 出生：x1 → AppearFrame，x2 → KeepFrame，x3 → VanishFrame；KeepHoldFrame 复用 x2；KeepHold {0,0} → 0x7fff | 整数，无门控；KeepHold 仅 Flags & 3 ≠ 0 | 已确认 | ORD 3d1bf04..3d1bff8 |
| ParentOptions（0xf62e295d，槽号） | ① ConstInheritRate `clamp(rnd, 0, 1)` ② ConstFrame ③ ConstReleaseFrame（负值置 0） | 仅 ParticleUseLocal 且新粒子；"不跟随"时跳过 ②③ | 已确认 | ORD 3d36ad0..3d36bde |
| EmitterShape3D（0x30） | Box：轴 `k = x % 3`，再 u_x、u_y、u_z（各 ∈ [−1,1)），壳逻辑不再抽取；均分时不抽轴号，均分轴的 u 也不抽。Sphere 默认：3 次方向（[−1,1)，归一化，过小时为 0），再 1 次 t。UseExtension / 均分：角 a（未设时抽）、角 b（同）、再 t。Cylinder 默认：2 次 XZ 方向、t、h（均分未设时）；横向均分或 UseExtension：角（未设时抽）、t、h（未设时抽）。等距圆柱（3d22b00）：a、h、t（各在未设时抽） | a、b、h 在对应均分数为 0 时为 −1（需抽），否则由形状计数器 `shape+0x158` 按 §3.1 的交错算出，不抽 | 已确认 | ORD 3d22444..3d22613、3d22e0b..3d22fdd、3d228a1..3d22a55；3d231b0、3d223c0、3d22b00 |
| Velocity3D（0x23） | init 时延迟：Flags 位 5 时 SpeedDelayFrame、位 6 时 GravityDelayFrame（`x % (2·spread+1) − spread + center`）；无待定延迟时方向：Direction = 3 次 `rnd(DirX/Y/Z)`，Normal / Radial = 0 次，Spread = 2 次（极角、方位角），有形状块的其他类型 = 0 次；然后 **Speed**、**GravityRate**、**SpeedCoef**、**InheritRate**（Flags 位 0） | 方向无门控；Speed 有门控；Gravity 在 center 与 spread 都为 0 时跳过；SpeedCoef、Inherit 无门控 | 已确认 | ORD 3d1af0e、3d1b3f6..3d1b704 |
| RotateAnim（0x25） | X-Add、X-Coef、[X 符号]、Y-Add、Y-Coef、[Y 符号]、Z-Add、Z-Coef、[Z 符号]；符号抽取仅 Flags 位 0，x 为偶数时取反 | **无门控** | 已确认 | ORD 3d1ce3d..3d1d036 |
| ScaleAnim（0x27） | ScalarAdd、ScalarCoef、XAdd、XAddCoef、YAdd、YAddCoef、ZAdd、ZAddCoef | 各自有门控 | 已确认 | SCA |
| Billboard3D（0x0f；2D 为 0x0d） | ① 颜色（头部 Flags 位 4 且 `p+8 & 0x200000 == 0` 时 1 次）② SizeScalar ③ SizeX ④ SizeY ⑤ Rotation | ②–⑤ 有门控；仅新粒子 | 已确认 | ORD 3d18824..3d189db |
| TypeMesh（0x11） | ① 颜色（按标志）；然后交错 RotX、SizeX、RotY、SizeY、RotZ、SizeZ、SizeScalar；PartsStartNo（顺序模式关时 1 次）；PlaySpeed（1 次）；PlaySpeedCoef（1 次）；PlayOrder 2 时 1 次（x 偶数倒放） | 旋转与尺寸**无门控** | 已确认 | ORD 3d30bbc..3d30fc4 |
| RibbonLength（`[blk+0x18]`） | Length、ReleaseFixEnd；LengthFlags 位 0 清零时再 3 次方向 `rnd(DirX/Y/Z)` | 无门控 | 已确认 | ORD 3d7a4a8、3cff9ea |
| UVSequence（0x2b） | SequenceNo（min ≠ maxEx 时）、PatternNo（同）、PlaySpeed（总是 1 次）；再按加载器顺序 FlipU、FlipV、Reverse、Rotate90：取值 2 时各 1 次（`x & 1`） | min == maxEx 时不抽 | 已确认 | ORD 3d1ec84..3d1ee69 |
| Blink（0x087f7c20） | LowFrequency、LowFrequencyWidth、HighFrequency、HighFrequencyWidth、初相 u | 无门控 | 已确认 | BL |
| Noise（0xd17f868f，槽号） | 出生 8 次：高频相位、低频相位（各 2π·u）、k2、k0、k3、k1（各 2u − 1）、方向 x、y（2u − 1，再归一化），见 §3.8 | 无门控；仅新粒子 | 已确认 | 3dc70bb..3dc72b6；Re4Native `exp_noise.py` 原生运行 |
| PtCollision（0x55）/ PtColliderAction（0x54） | Radius（PtCollision 仅 Flags 位 2；PtColliderAction 仅位 3）、BounceNum、BounceRate | 抽取次数未逐条复核 | 部分待确认 | PC |
| Transform3DModifier（8）/ UVSequenceModifier（0xe2ce689e） | 每组 起始值 XYZ、速度 XYZ、系数 XYZ、符号；PlaySpeedInit、Final、ChangeTimeCoef | 无门控；Transform3DModifier 的具体抽取位置未逐条复核 | 部分待确认 | MOD |
| RgbCommon / RgbWater、AngularVelocity、Polygon、Trail、Strain、Lightning、材质变体 | 未解码 | | 部分待确认 | ORD §8 |

按消耗方式归类：

| 类别 | spread / 区间宽为 0 时是否抽取 | 成员 |
|---|---|---|
| CenterSpread，有门控 | 否 | ScaleAnim 8 项；Billboard3D SizeScalar / SizeX / SizeY / Rotation；Velocity3D Speed、GravityRate（两者都为 0 时跳过） |
| CenterSpread，无门控 | 是 | Velocity3D 方向、SpeedCoef、InheritRate；RotateAnim；Blink；TypeMesh 旋转与尺寸；RibbonLength Length / ReleaseFixEnd / 方向；Transform3DModifier；ParentOptions ConstInheritRate |
| MinMaxI、MinExtraI、CenterSpreadI（取模） | 是 | Life、Spawn、复活、ConstFrame / ConstReleaseFrame、Velocity3D 延迟（启用时） |
| MinMaxExclusiveI | min == maxEx 时否 | UVSequence SequenceNo / PatternNo |
| MinMax（float lerp） | 是 | UVSequence PlaySpeed、类型颜色插值（启用时） |
| 1 位抽取 | 是 | RotateAnim 符号、UVSequence 翻转 / 倒放 / 旋转、TypeMesh PlayOrder 2 |

### 1.7 发射器种子与 FixRandomGenerator

字段（无文本解析器，名字来自反射名）：

| 字段 | 含义 | 状态 | 依据 |
|---|---|---|---|
| `useRandomSeedTableCount` | 候选槽数 N，作取模除数；N = 0 除以零 | 已确认 | FRG 3d884dd |
| `randomSeedTable0..7` | 候选种子；只读前 N 个；−1 = 不固定，改为生成随机种子 | 已确认 | FRG 3d884e4；语料 seed6/7 恒为 −1 |
| `tableSelectionGroup` | 分组键（不是位掩码）。−1 = 不分组 | 已确认 | FRG；语料 11082/11101 为 −1 |

```
if group == -1:  h = next(G);                            idx = h % N;  seed = S[idx]; if seed == -1: seed = next(G)
else:            k = ((E+0x10) >> 4) & 0xfff;  h = xs(xs(k + group));  idx = h % N;  seed = S[idx]; if seed == -1: seed = xs(h)
E.seed = seed
```

构建器在发射器实例构造（3d06440）中每个发射器实例运行一次；存在时跳过默认种子生成（3d068ee）。N = 1 且种子有效时每次播放完全相同。多个图层要重合：同一 N、同一种子表，并且 N = 1 或使用同一个 group。`E+0x10` 来自效果实例 +0x148，两次播放是否相同未确定（§8）。

### 1.8 全局兼容开关

| 开关 | 影响 | 状态 | 依据 |
|---|---|---|---|
| `[g+0xeb2]` = EffectManager LegacyMode2018（写入者：setLegacyMode2018 的包装 c012c0 → e2e070；启动时从配置复制 `eb2 = cfg+0x530`（7896b12），`[g+0xeb0] = cfg+0x51d`（7896afe）；构造函数 3cdf250 先清零。发行版的实际取值需要实机读取） | Spawn 倒计时：关时首批在 tick `max(D,1)`、周期 `max(I,1)`，开时为 `D+1`、`I+1`（3e0e840）；ScaleAnim：开时抽样后 Add 换成 `Add/Coef`（首帧增量为 Add）；`E.state` 不再推进；Velocity3D GPU 常量另有分支（3d1abc9，未追踪） | 已确认 | FRG, SP, SCA, ORD |
| `[g+0xeb0]`（推定为 LegacyMode2016） | 类型分派在积分之前（0）或碰撞之后（≠ 0） | 已确认（作用）；名字部分待确认 | ORD |

两个字节在发行版中的值都未知。**折中实现**：模拟器都按 0 处理，并保留开关。

### 1.9 尚未解码

| 项 | 影响 |
|---|---|
| Spawn LoopNum 抽取（3d14b60）、3d154d0 内的取模顺序、三个 Spawn 调用点之间的流衔接 | 多轮发射的批数 |
| PtCollision / PtColliderAction 的抽取次数、Transform3DModifier 抽取位置、Rgb* / AngularVelocity / Polygon / Trail / Strain / Lightning / 材质变体的抽取顺序 | 这些属性与游戏逐粒子对齐 |
| GPU 类型的计算着色器种子（CPU 种子是否传入未知） | GPU 类型的随机值 |
| CRT 的 `sqrtf / sinf / cosf / powf` 与 numpy 末位差异 | 浮点逐位一致 |
| 参考实现未对照实机。最低成本校验：FixRandomGenerator N = 1、已知种子、LoopNum {1,0} 单批发射器，读首批粒子的 `p+0x18`、`p+0x80..0x8c`、`p+0x5c..0x64` 与 `Rng(salt, xs(seed)→xs(·), seed, id)` 比较 | 全部 |

---

## 2. 骨架

### 2.1 Transform3D（发射器局部摆放）

| 字段 | 类型 | 运行时含义 | 状态 | 依据 |
|---|---|---|---|---|
| `LocalPosition` | Vector3，米 | 发射器局部平移，矩阵最后一步 | 已确认 | T3D；语料常见 ±0.05..±1 |
| `LocalRotation` | Vector3，弧度 | 按 `RotationOrder` 组合的欧拉角；右手系，绕 +Z 正转把 +X 转向 +Y | 已确认（unicorn 模拟 6 种顺序） | T3D 3d13e50 |
| `LocalScale` | Vector3 | 旋转之前、沿发射器自身轴的缩放 | 已确认 | T3D；语料 98.8% 为 1，从不为 0 或负 |
| `RotationOrder` | u32 | 0 XYZ、1 YZX、2 ZXY、3 ZYX、4 YXZ、5 XZY；越界 = 单位阵。"ABC" 得 `R = R_A·R_B·R_C`，A 最先作用 | 已确认 | T3D；语料 2 占 86.6% |

```
M_emitter = S(LocalScale) · R(LocalRotation, RotationOrder) · T(LocalPosition)     // v_e = v_l·S·R + pos
```

块里有三份变换：base（文件值）、animated（驱动属性写入处）、current（实际组合用）。每帧的规则：

| 情形 | 行为 | 状态 | 依据 |
|---|---|---|---|
| 无驱动属性 | 安装时（slot 23）组合一次 | 已确认 | T3D 3d145a0 |
| 每帧开始 | animated ← base（3d146ae），驱动属性在此基础上改写 | 已确认 | EXP |
| Transform3DExpression | 改写 animated 后以 perFrame=1 调用组合函数 3d13250 | 已确认 | EXP 3d46bc0 |
| Transform3DModifier | 改写 base 与 animated 后调用 3d13250 | 已确认 | MOD 3d9b300 |
| Transform3DClip | 3d655a0 以 perFrame=1 调用 3d13250 | 部分待确认（vtable 归属推定） | T3D |
| perFrame=1 时 | `rot = fmodf(animated rot, 2π)`（保留符号），pos、scale 取 animated | 已确认 | T3D 3d132de |

组合函数只把 Scale 写进 ctx+0x90 的对角线，再右乘 R 与 T；只有该矩阵在进入时为单位阵才正确。每帧路径上没有找到复位处（§8）。

**粒子坐标模型。** 粒子位置 +0x28 存在发射器局部空间；每个粒子另有参考矩阵 P（粒子 +0x100..+0x13f），世界位置 = 局部位置 · P（3dfd040）。P 由 ParentOptions 的逐粒子跟随更新（§2.2.3）；EmitterShape3D 与 Velocity3D 的出生计算都在局部空间进行，与 Transform3D 无关。状态：部分待确认（P 的出生初值未单独追踪）。依据：SEM、PO、FE、PT、RT。

### 2.2 ParentOptions（跟随父级与骨骼）

#### 2.2.1 字段

| 字段 | 类型 | 运行时含义 | 状态 | 依据 |
|---|---|---|---|---|
| `RelationPos` | Int3，每轴 0..3 | 平移每轴的跟随模式（2.2.2） | 已确认 | PO 3d352cc；语料 (1,1,1) 96.2% |
| `RelationRot` | Int3 | 父级欧拉角每个分量的跟随模式 | 已确认 | PO；语料 (0,0,0) 12.9%、(0,1,0) 4.5% |
| `RelationScl` | Int3 | 父级缩放（矩阵行长）每轴的跟随模式 | 已确认 | PO |
| `ParticleUseLocal` | u8 | 逐粒子跟随总开关；0 时所有粒子跟随率为 0，`Const*` 全部失效 | 已确认 | PO 3d353ac；语料约 900 个发射器 `Const*` 有值但此项为 0 |
| `ConstInheritRate` | CenterSpread | 出生时的跟随率，结果夹在 [0,1] | 已确认 | PO 3d36ad0；语料 center = 1 占 99.4% |
| `ConstFrame` | MinExtraI | 跟随保持帧数 | 已确认 | PO 3d36b5d |
| `ConstReleaseFrame` | MinExtraI | 保持结束后，从出生跟随率过渡到 `ConstInheritReleaseRate` 的帧数 | 已确认 | PO |
| `ConstInheritReleaseRate` | float | 过渡结束时的跟随率 | 已确认 | PO；语料 99.98% 为 0 |
| `BoneName` | UTF-16，u32 字节长度前缀 | 挂点骨骼名。非空才会查骨骼；字节长度必须 < 0x80，否则整个文件加载失败 | 已确认 | PO 3d351d4 |
| `ParentBone`（C# 辅助属性） | BoneRelations 表下标 | 只在路径 A 使用，且要求 `BoneName` 非空 | 已确认 | PO 3d35220 |

#### 2.2.2 发射器跟随（每帧，组合函数 3d342c0）

父矩阵 P 拆成：缩放 = 第 0..2 行长度；旋转 = 归一化各行 → 欧拉角（3ad7f80，欧拉顺序未读）；平移 = 第 3 行。每轴按模式取值，再以 `S·R(euler)·T` 重建，发射器世界矩阵 = 局部矩阵 × 该矩阵。

| 模式 | 平移轴 | 旋转分量 | 缩放轴 | 状态 |
|---|---|---|---|---|
| 0 | 复位帧取场景 / 世界原点（双精度全局值），之后保持：不随父级平移 | 0：与世界对齐 | 1.0 | 平移：部分待确认（原点来源推定）；旋转、缩放：已确认 |
| 1 | 每帧取父级 | 每帧取父级 | 每帧取父级 | 已确认 |
| 2 | 只在复位帧取父级，之后冻结 | 同左 | 同左 | 已确认 |
| 3 | 读未初始化栈槽（无效） | 把 pos.z 当角度（无效） | 等同 1 | 已确认 |

复位帧 = `[ctx+8]` 位 1，推定为首帧或重启帧（部分待确认）。发射器 `+0x16c` 位 7 时再做镜像（3d020a0）。依据：PO。

骨骼解析（安装函数 3d35ef0，每帧）：仅当 `BoneName` 非空。路径 A（全局 `[g+0xeac]` ≠ 0、表标志允许、所有者 `+0x184` 位 20）用 BoneRelations 表；否则路径 B：首帧在所有者 Transform 上按名字查（镜像效果查镜像骨骼），找不到再查父 Transform，找到后缓存名字哈希，之后按哈希查；骨骼消失时保留最后矩阵。已确认（PO, ST 同一机制）。

#### 2.2.3 粒子跟随（3d36a30；每粒子 16 字节状态 {rate, hold, rel, relTotal}）

```
出生：
  rate   = ParticleUseLocal ? clamp(CIR.center + (2u−1)·CIR.spread, 0, 1) : 0
  active = !(ConstFrame == 默认 && ConstReleaseFrame == 默认)      // 默认 = 全局 d6a3050，推定 {0,0}
  if ParticleUseLocal && active:
      hold = ConstFrame.min        + rand % (ConstFrame.extra + 1)
      rel  = relTotal = ConstReleaseFrame.min + rand % (ConstReleaseFrame.extra + 1)    // 负值置 0
  else hold = rel = relTotal = 0
每帧：
  if ParticleUseLocal && active:
      hold -= step
      if hold < 0: rel = max(rel + hold, 0); hold = 0
  if ParticleUseLocal && active && hold <= 0:
      eff = relTotal != 0 ? RR + (rel/relTotal)·(rate − RR) : RR          // RR = ConstInheritReleaseRate
  else eff = rate
  P = blend(P, target, eff)        // eff ≤ 1.19e-7 保持 P；≥ 1 取 target；否则 TRS 插值（3d35860）
```

- 计时用粒子自己的年龄，不是发射器时间轴。已确认。
- `target` 来自 3d34d40（发射器 / 父级矩阵），具体哪一个随发射器帧字节 +0x1e 变化：部分待确认。
- `ParticleUseLocal = 0` 且该字节为 0 时，P 出生后不再更新，粒子留在世界中。已确认。
- GPU 类型把同样的数据打包进 0x50 字节常量（3d36920），着色器侧未读。

### 2.3 Spawn（发多少、何时发）

#### 2.3.1 字段

| 字段 | 类型 | 运行时含义 | 状态 | 依据 |
|---|---|---|---|---|
| `MaxParticles` | u32 | CPU 粒子池容量（ctx+0xfc）。只填空槽，满了这一批多余部分丢弃；LOD q < 1 时乘 q（至少 1） | 已确认 | SP 3d064fb |
| `ParticleInterval` | u32 | 粒子池分组数：加载器存 q = MaxParticles / ParticleInterval（为 0 时 q = 1），构造时进 E+0x104。q > 1 时池子分成 ParticleInterval 组、每组 q 个连续槽位，只有组头（槽号 % q == 0）从本批认领；组内下一个槽位在前一个槽位存活且已过出生帧后才认领，所以一组每 tick 接上一个粒子 | 已确认 | 3d15720、3d06440、3d15e60；Re4Native `exp_particle_interval.py` 原生运行；语料中 ParticleInterval 全部等于 MaxParticles（q = 1，不分组） |
| `SpawnNum` | MinMaxI | 每批粒子数，每批重抽 | 已确认 | SP 3d14f5f |
| `IntervalFrame` | MinMaxI | 批间隔帧数（16 位），每批重抽；周期 `max(I,1)` | 已确认 | SP |
| `UseSpawnFrameInt` | u32 布尔 | 停止条件改为按帧（`SpawnFrame`）而不是按批（`LoopNum`） | 已确认 | SP |
| `SpawnFrame` | MinExtraI | 仅上项开启时：可出生的帧数；{0,0} = 永远 | 已确认 | SP |
| `LoopNum` | MinExtraI | 每轮批数；**{0,0} = 无限** | 已确认 | SP 3d14b60；语料 {1,0} 61%、{0,0} 22% |
| `EmitterDelayFrame` | MinMaxI | 初始延迟帧数（16 位），只在（重）启动时 | 已确认 | SP |
| `RingBufferModeInt` | u32 布尔 | 仅 GPU：写入游标在池内循环，覆盖最旧粒子 | 已确认 | SP 3e1a7d3 |
| `UseLoopNumRatioInt` | u32 布尔 | 轮内批数改为 `min + extra·t`，t 由 SpawnExpression 槽 6 给出并夹在 [0,1] | 已确认 | SP, EXP |
| `InterpolateInt` | u32 布尔 | 开启按距离发射，并把本帧粒子沿发射器路径摊开 | 已确认 | SP 3d15c90 |
| `DistancePerSpawn` | float，米 | 按距离发射的间距；不得为 0 | 已确认 | SP |
| `UseRevivalInt` | u32 布尔 | 开启复活（多轮） | 已确认 | SP |
| `RevivalNum` | MinExtraI | 额外轮数；{0,0} = 无限 | 已确认 | SP |
| `RevivalInterval` | MinExtraI | 一轮最后一批之后、下一轮第一批之前等待的帧数 | 已确认 | SP |

RE4 文件里没有 TrySpawnAllParticles、InitializeFull、SpawnChanceFrame、OriginalMaxParticles，也没有逐粒子出生延迟。

#### 2.3.2 时间表（默认倒计时，LegacyMode2018 = 0）

```
启动（tick 1）：
  L    = UseSpawnFrameInt ? roll(SpawnFrame) : roll(LoopNum)     // {0,0} → −1，永不递减（ctx+0x168 |= 0x80000）
  R    = UseRevivalInt ? roll(RevivalNum) : 0                     // {0,0} → −1
  c    = roll(EmitterDelayFrame); wait = 0
每 tick：
  if wait > 0:                    不出生
  elif L == 0:                    不出生
  elif c > step:                  c -= step（延迟期间 E+0xd0 位 0 置位）
  else:
      c = 0; 出一批：
        count = roll(SpawnNum) × max(1, floor(n / (I_prev + 1)))     // 低帧率补发；全局字节 [d62f8f0]+0xa69 可把它限回 roll 值
        if Interpolate: count = max(count, 距离发射数)
        I = roll(IntervalFrame); c = I
        if 本批前有非零延迟: E+8 |= 2; ctx+0x108 = 0                  // 发射器时间清零
        if !UseSpawnFrameInt: L -= 1     // 低帧率补发时按补发批数递减：n = 3 的一个 tick 出 3 批、L 减 3（Re4Native 原生运行）
        // 批数还乘 LOD 因子（ctx+0x100 所指对象 +8 的 float）；为 0 时退化成每批 1 个粒子
  if UseSpawnFrameInt 且不在延迟中: L -= step
  if L == 0 且 R != 0（无符号，−1 也算）:
      L = 重抽; wait = roll(RevivalInterval); c = 0; if R != −1: R -= 1
  if wait > 0: wait -= step（在 tick 末尾）
```

- 第一批在 tick `max(D,1)`，之后每 `max(I,1)` 帧一批。LegacyMode2018 = 1 时改为 tick `D+1`、周期 `I+1`（§1.8）。哪种模式在游戏中生效未定：**折中实现**按默认模式。
- 最后一批的粒子带标志 0x20000（3d15fbd）。发射器结束 = L 为 0 且没有剩余轮数；已有粒子按 Life 活完。
- 复活的等待从最后一批算起，不从粒子死亡算起；复活不再走 EmitterDelayFrame。
- 距离发射（slot 23 3d15c90，需 L > 0、不在等待和延迟中）：`d = |pos_now − pos_last| + carry; k = floor(d / DistancePerSpawn); carry = d − k·DistancePerSpawn`；pos_last 每 tick 更新。定时批次照常运行，纯拖尾需 `SpawnNum {0,0}`。摊开：本帧第 k 个（剩余计数）粒子取比例 `1 − (k−1)/count`，沿上一帧到本帧的发射器变换插值（3d36c21 → 3d35860，插值内部部分待确认）。
- LOD 因子 q（3b89d00 × E+0x100+8）：q < 1 时缩放计数（SpawnNum.max > 1 时）或把间隔拉长 1/q，并把 LoopNum / SpawnFrame 乘 q。何时 q < 1 未查。部分待确认。

依据：SP（3d14c20、3d14e96..3d14ece、3d14fc1、3d150d6..3d15136、3d15d40..3d15dcb）。

### 2.4 Life（单粒子寿命与 alpha 包络）

#### 2.4.1 字段

| 字段 | 类型 | 运行时含义 | 状态 | 依据 |
|---|---|---|---|---|
| `AppearFrame` | MinMaxI | 淡入帧数 A；0 = 出生即满 alpha | 已确认 | LF 3d1bf56；语料 r = s 98.2% |
| `KeepFrame` | MinMaxI | 满 alpha 平台帧数 K | 已确认 | LF |
| `VanishFrame` | MinMaxI | 淡出帧数 V；0 = 硬切 | 已确认 | LF |
| `KeepHoldFrame` | MinMaxI | Keep 之后的额外保持 H；仅 `Flags & 3 ≠ 0` 时使用；**{0,0} = 无限（0x7fff）** | 已确认 | LF |
| `Flags` | u32，只读低 2 位 | 保持模式：0 无、1 KeepHold、2 KeepHold 且释放时重放 Keep、3 CPU 每帧 alpha = 0 | 已确认 | LF；语料 0：86.5%、1：12.9%、2：0.6%、3：从未出现 |

抽样：三次抽取依次给 A、K、V；H 复用 K 那次抽取，所以 K 与 H 相关。GPU 路径把每对规范成 `(min(a,b), |a−b|)`，倒置无害；CPU 路径倒置会除以零或得到巨大值。已确认（LF 3d1bfe8、3d1bd60）。
存储为 int16：`A+K+V ≤ 32767`，溢出后粒子在第一次更新时死亡。已确认（LF）。

#### 2.4.2 状态机（CPU，3d1be80）

逐粒子记录（int16）：`remain`（初值 T = A+K+V）、A、A+K、T、H、重放计数。`elapsed = T − remain`。

| 码 | 状态 | 进入条件 |
|---|---|---|
| 2 | Initialize | 出生 |
| 3 | Appear | `elapsed < A` |
| 5 | Keep | `elapsed < A+K` |
| 6 | KeepHold | 模式 1/2，`elapsed ≥ A+K` 且 H > 0 |
| 8 | Vanish | `elapsed < T` |
| 9 | Terminate | `remain ≤ 0` 且 `H ≤ 0` |

```
出生：抽 A、K、V（Flags&3 ≠ 0 时再定 H）；alpha = (A == 0) ? 255 : 0；state = 2
      if H <= 0 && remain <= 0: 当帧 Terminate（全 0 且 Flags 0 的粒子出生即死）
每帧（存活时）：
  if 粒子标志 [p+4] & 0x8000: H = 0（唯一写入者在 RibbonLength 处理函数附近 3d7c6ff）
  remain -= step               // 模式 2 在重放计数 > 0 时跳过
  if remain <= 0 && H <= 0: Terminate（alpha 0）
  模式 0：Appear: alpha = trunc(elapsed/A·255)；Keep: 255；Vanish: alpha = trunc(remain/V·255)
  模式 1：elapsed ≥ A+K 且 H > 0 → KeepHold：alpha 255，每帧 remain = V（elapsed 冻结在 A+K），H -= step（H = 0x7fff 时不减）；
          发射器状态 [+0x170]&7 == 4 时 H 强制为 0；H 到 0 后正常 Vanish
  模式 2：同模式 1，另外：Keep 期间所有者状态 [+0x184]&0xf ∈ {4,5} 时直接跳到 Keep 末尾（H = 0，重放计数 = K）；
          保持结束时重放计数 = K，再满 alpha 保持 K 帧（状态 6）后 Vanish
  模式 3：每帧 alpha = 0，T 时死亡（GPU 视作模式 0）
```

- alpha 包络线性、8 位，存在粒子头 `[p]` 位 2..9。Appear 最后一帧是 `elapsed = A−1`，到不了 255；Vanish 从 255 开始，结束于 `⌊255/V⌋`。已确认（LF 3d1c1e3、3d1b8a0）。
- `step = 1` 时每阶段的 tick 数：状态 2 一帧 + Appear A−1 帧、Keep K 帧、Vanish V 帧，再一帧状态 9。实机探针多出的 +1 帧尚未解释；候选原因是 N 的浮点余量让个别 tick 出现 N = 0 再 N = 2（§1.1、§8）。
- 发射器状态 4、所有者状态 4/5 推定为"停止 / 结束请求"：部分待确认。
- **委托淡出**：发射器帧字节 +0x1f（ctx+0x137）置位时，Vanish 和寿命结束不淡出也不杀，而是设块字节 +0x30 = 1，由 `[ctx+0x1a0]+0x60` 的计时器倒数到 0 再杀。该字节只由 EmitterContextType 7 的安装函数（3d53b40，vtable 7a0eb78）写入，类型 7 未识别。部分待确认。
- GPU 类型：3d1bd60 打包 `{min, max−min}` 四组与模式码（Flags 1 → 3、Flags 2 → 4、否则 0）为 16 字节常量，着色器侧未读。
- 块构建副作用：发射器 `+0x168 |= 0x100000`；若 `+0x16c` 位 2 置位，`0x200000 = (Flags&3 ∈ {1,2})`（与 PtLife 写同一位，§5.1）。

用法对应：无限寿命 = Flags 1 + KeepHold {0,0}（语料主要在 RibbonLength 上，3006/6237）；有限保持 = Flags 1 + KeepHold {n,m}，平台长 K + H。

---

## 3. 发射与运动

### 3.1 EmitterShape3D（出生位置）

| 字段 | 类型 | 运行时含义 | 状态 | 依据 |
|---|---|---|---|---|
| `RangeX` / `RangeY` / `RangeZ` | InnerOuter | Box：内空心半边长 / 外半边长；Sphere：每轴内 / 外半径；Cylinder：X、Z 为内 / 外半径，Y 为高度起点 / 终点（单侧，不是 ±） | 已确认 | SEM 3d223c0, 3d22d50, 3d22800 |
| `ShapeType` | Shape3DType | Box 0、Sphere 1、Cylinder 2 | 已确认 | SEM（枚举注册 4b12a00..） |
| `RangeDivideNum`、`RangeDivideAxis` | u32 | 只有 Box 读：均分轴的坐标按块计数步进，不再抽轴号（见下方采样） | 已确认 | 3d223c0 |
| `RangeDivideHorizontalNum`、`RangeDivideVerticalNum` | u32 | 只有 Sphere、Cylinder 读：把随机的 a、b 换成按块计数的均分位置（3d231b0，见下方采样） | 已确认 | 3d231b0 及调用处 3d22854、3d22b44、3d22da2 |
| `LocalRotation` | Vector3，弧度 | 形状旋转 R = euler(LocalRotation, RotationOrder)，每帧重建（3d242d0），同时保存 R⁻¹ | 已确认 | SEM |
| `RotationOrder` | 同 Transform3D | | 已确认 | SEM |
| `RotationCorrect` | 枚举 | 形状旋转按相机修正（3d21210，结果在形状块 +0x40）。1 ParallelCamera：R = 相机矩阵（3a17660）3×3 的转置；2 ParallelCameraAxisY：z = normalize(C[0][2], 0, C[2][2])，R = ((z.z, 0, −z.x), (0, 1, 0), z)，只保留偏航；3 ToCamera / 4 ToCameraAxisY：用相机位置（3a17ad0 第 4 行）减发射器位置与相机上方向拼出朝向相机的基，只在每帧的 slot 23（3d24710）里求 | 1、2 已确认；3、4 部分待确认（每帧路径未在原生运行中执行） | 3d21210、3d24710；Re4Native `exp_rotation_correct.py`（伪造相机，两组相机） |
| `DivideEquidistant` | bool | 只有 Cylinder 读：方位角按椭圆弧长等分（3d22b00，见下方采样） | 已确认 | 3d22b00、3d23470 |
| `DivideEquidistantCalcOuterCurveData` | bool | 内、外椭圆各建一张表，按 t 在两点之间插值；关时用平均半径建一张表 | 读码已确认；原生运行未覆盖（语料无） | 3d210d0、3d22b00 |
| `DivideEquidistantRecalcEveryFrameData` | bool | 每帧重建表（半径或形状矩阵会变时）；关时只在首次建一次 | 读码已确认；原生运行未覆盖（语料无） | 3d210d0 |
| `UseExtension` | bool | 0 时下两项被默认值覆盖：Box H=(1,0)、V=(1,0)；Sphere H=(0,2π)、V=(−π/2,π)；Cylinder H=(0,2π)、V=(1,0) | 已确认 | SEM |
| `ScaleHorizontal` / `ScaleVertical` | StartSpan | Sphere / Cylinder 的角度区间（θ、φ）；Box 的锥形收缩系数；Cylinder 的 V 为沿高度的收缩 | 已确认 | SEM |

采样（3d24d00），`u3 ∈ [−1,1]³` 等均为新的随机数：

```
Box:      k = 随机轴（x % 3），均分时为 RangeDivideAxis 且不抽取；v[k] = outer[k]·u[k]
          均分（Dn = max(RangeDivideNum·q, 1)，RangeDivideNum 为 0 时不均分）：u[k] = (c / (Dn − 1))·2 − 1，Dn = 1 时为 0；
                c = 块 +0x158，每次采样后加 1，到 Dn − 1 后回到 0（Box 自己维护）；其余两轴照常抽取
          inside = |v[k]| <= inner[k]
          第一条其余轴 j1 = k+1：inside 时 v = (outer − inner)·u + inner·sign(u)（落在壳里），inside = |v[j1]| <= inner[j1]；否则 v = outer·u
          第二条其余轴 j2 = k+2：同上，用更新后的 inside（级联：只有前两轴都在 inner 以内才走壳公式）
          UseExtension: t = (y + RangeY.outer)/(2·RangeY.outer);  x *= H.start + t·H.span;  z *= V.start + t·V.span
Sphere:   dir = normalize(u3)                                         // 归一化立方体，不是球面均匀
          UseExtension 或均分时: θ = H.start + a·H.span; φ = V.start + b·V.span; dir = (cosφ·cosθ, sinφ, cosφ·sinθ)
          t = u;  p = (lerp(X.inner,X.outer,t)·dir.x, lerp(Y.inner,Y.outer,t)·dir.y, lerp(Z.inner,Z.outer,t)·dir.z)   // 半径对 t 线性
Cylinder: c = normalize(u2) 或 (cosθ, sinθ), θ = H.start + a·H.span
          x = lerp(X.inner,X.outer,t)·c.x;  z = lerp(Z.inner,Z.outer,t)·c.y;  y = lerp(Y.inner, Y.outer, h)
          UseExtension: x、z 再乘 V.start + h·V.span
粒子位置 = p·R（只旋转，不平移）；粒子标志 0x40000 时为 +=

Sphere / Cylinder 的均分（3d231b0；c = 块 +0x158，调用处每次采样后加 1，不回绕）：
  Hn = H 均分数·q、Vn = V 均分数·q，各自取整且至少 1；均分数为 0 时该方向不均分（a 或 b 照常抽取）
  q = 3b89d00(…) × float([ctx+0x100] + 8)（LOD 系数，常为 1；Box 的 Dn 用同一个 q）
  纵向下标 iv = c % Vn；横向下标 ih = (c / Vn + c % Vn) % Hn            // 对角交错
  b = iv / (Vn − 1)，Vn = 1 时 0.5
  a = ih / Hn，当 H.span 与 float 2π（0x40C90FDB）逐位相等（避免首尾重合）；否则 ih / (Hn − 1)，Hn = 1 时 0.5
  H.span 取 UseExtension 处理后的值（UseExtension 0 时为默认的 2π）
```

- RangeDivideNum / Axis 只有 Box 读；H / V 均分数只有 Sphere、Cylinder 读。语料里存在 Sphere、Cylinder 带 RangeDivideNum、Box 带 H / V 均分数的条目，这些字段对它们不起作用。
- 级联的壳判断与旧版本文"两条其余轴都走壳公式"不同。已比过的 Box 条目 inner 都是 0，两种写法结果相同，原生运行没能区分。

等距圆柱（Cylinder 且 DivideEquidistant；属性对象 +0x6c / +0x6d / +0x6e 依次是 DivideEquidistant、CalcOuterCurveData、RecalcEveryFrameData）：方位角不再与 a 成正比，而是按椭圆弧长等分，所以椭圆截面上相邻粒子的间距相等。

```
抽取（3d22b00）：a、b 先取 3d231b0 的均分值（规则同上），未均分的依次抽 a、b；再抽 t
  y = lerp(Y.inner, Y.outer, b)
  单表（CalcOuterCurveData 关，语料全部如此）：rX = lerp(X.inner, X.outer, t)，rZ = 同理；点 = 3d22030(表 0, rX, y, rZ)
  双表（CalcOuterCurveData 开）：内、外椭圆各取一点（表 0 用 inner 半径，表 1 用 outer），x、z 再按 t 插值，y 取外点
3d22030：UseExtension 开：v = 查表(a·(N−1))
         UseExtension 关（半圈表）：a > 0.5 时 v = 查表((a − 0.5)·2·(N−1)) + 0.5（double 运算），否则 v = 查表(a·2·(N−1))
         θ = H.start + v·H.span;  x = cosθ·rX;  z = sinθ·rZ;  UseExtension 开时 x、z 再乘 V.start + b·V.span
查表（3d20e20）：i = floorf(s)，i = N − 1 时取 table[i]，否则 (1 − f)·table[i] + f·table[i+1]
建表（3d23470，由 3d210d0 在块 +0x164 / +0x165 置位时调用；之后只在 RecalcEveryFrameData 开时每帧重建）：
  表 0 存在形状块 +0x168，N = 属性对象 +0x84 项（RE4 读到 64）；表 1 紧随其后
  半径：单表时用平均值 (inner + outer)/2，双表时分别用 inner、outer；再各自除以 max(min(rx, rz), 1)
  角度范围：UseExtension 关时只取半圈（比例 0.5），开时取整个 H
  N 个点 θ_i = H.start + (i/(N−1))·比例·H.span，点 = (cosθ·rx, 0, sinθ·rz, 1)·M（M = 形状块 +0x80 的 4×4 矩阵：形状旋转与发射器平移，含透视除法）
  L[0] = 0，L[i] = L[i−1] + |点_i − 点_{i−1}|（累积弦长）
  对 j = 0..N−1：目标 = j·L[N−1]/(N−1)；在 [下界, N−1] 上二分 10 次，下界从上一个 j 的结果开始；
    每步 mid = (lo + hi)/2，(目标 − L(mid))·(目标 − L(lo)) > 0 时 lo = mid，否则 hi = mid（L(s) 为对 L 的线性插值）
    table[j] = ((lo + hi)/2)/(N − 1)·比例
```

- 64 点的弦长和 10 次二分都是近似。和理想的等弧长分布相比，粒子位置差约 1e-3，所以离线实现必须照搬这些步骤（`tools/Re4Native/equidistant_ref.py`）。
- 发射器平移不改变弦长，所以 M 实际上只有缩放会影响结果。

状态：已确认（读码 3d24d00、3d223c0、3d231b0 及调用处，等距圆柱 3d22b00、3d22030、3d20e20、3d23470、3d210d0）。核对方式如下：
- re4.exe：`tools/Re4Native/exp_equidistant.py` 原生运行 RE4 语料里唯一的等距条目（csa071 smoke，半径 0.2，横向均分 3）。64 项表逐位一致，粒子位置误差 < 2e-8。
- MonsterHunterWilds.exe：同一套采样代码原生运行。39 个带均分的条目，以及 23 个带等距的语料条目（含椭圆截面、UseExtension、横向不足一圈），逐粒子一致（`docs/development/WILDS-EFX-VS-RE4.zh-CN.md` §6.5）。
- 未在原生运行中覆盖：双表（CalcOuterCurveData）和每帧重建（RecalcEveryFrameData），两个语料里都没有打开的条目。

### 3.2 Velocity3D（初速度与积分）

| 字段 | 类型 | 运行时含义 | 状态 | 依据 |
|---|---|---|---|---|
| `Flags` | u32 | 位 0 继承发射器速度；位 2（0x4）法线方向不再乘形状旋转、重力方向固定 (0,1,0)、位移累加到 +0x40；位 5（0x20）启用 SpeedDelayFrame；位 6（0x40）启用 GravityDelayFrame | 已确认 | SEM 3d1aee5, 3d1af7a, 3d06f56 |
| `DirectionVectorX/Y/Z` | CenterSpread | Direction 模式的方向分量；Spread 模式取三者的 center 作锥轴 | 已确认 | SEM |
| `Speed` | CenterSpread，米/秒 | 出生速度大小，存储为 `spd/60`（米/帧） | 已确认 | SEM 3d1b565；实机 1±1 → [0,2] |
| `SpeedCoef` | CenterSpread | 每 1/60 秒速度乘一次 | 已确认 | SEM 3d07589 |
| `SpeedDelayFrame` | CenterSpreadI | 仅 Flags 位 5：速度延迟帧数 | 已确认 | SEM |
| `Offset`、`Size` | Vector3 | Normal 模式参数 | 已确认 | SEM |
| `VelocityType` | 枚举 | 0 Direction、1 Normal、2 Radial、3 Spread、4 ScreenSpace | 已确认 | SEM 3d198a0 |
| `GravityRate` | CenterSpread，g | 1.0 = 9.80665 m/s²；{0,0} 时无重力 | 已确认 | SEM |
| `GravityDelayFrame` | CenterSpreadI | 仅 Flags 位 6 | 已确认 | SEM |
| `InheritRate` | CenterSpread | 继承发射器速度的比例（Flags 位 0） | 已确认 | SEM |
| `InheritDistance` | ClampRange，米 | 发射器本帧位移长度夹在 [min, max] | 已确认 | SEM 3d1a7a0 |
| `Spread` | MinExtra，弧度 | Spread 模式：极角 `0.5·(min + u·extra)` 对角度线性抽样（不是按立体角均匀），方位角 `2π·u`；min、extra 是全锥角 | 已确认 | 3d1a470；Re4Native `exp_spread.py`（RE4 语料全部 12 个 CPU 条目） |

出生（3d1ae00，init = 1；3D 路径中在粒子第一个 tick 由 3d05350 的 3d1ada0 调用，位于形状采样与 RotateAnim / ScaleAnim 出生抽样之后，§1.4），`R` 为形状旋转，`P` 为形状采样后的粒子位置：

```
VelocityType 0: d = normalize(rnd(DirX), rnd(DirY), rnd(DirZ))                         // 不乘 R
             1: p = P·R⁻¹（无形状时 p = 粒子位置）
                d = (Size == (1,1,1)) ? Offset : (Size − 1) ⊙ p + Offset              // 代码为 Size·p + Offset − p
                d = normalize(d)（|d| < 1.19e-7 原样返回）;  if !(Flags & 4): d = d·R(3×3)
             2: d = normalize(P)                                                       // P 已含 R
             3: 锥轴 n = normalize(DirX.center, DirY.center, DirZ.center)（粒子标志 0x100000 时用已有速度）；Spread == (0,0) 时 d = n
                θ = 0.5·(Spread.min + u1·Spread.extra);  φ = 2π·u2;  局部 d0 = (cosθ, sinθ·cosφ, sinθ·sinφ)（以 +X 为轴）
                d = 把 +X 转到 n 的最短弧旋转作用于 d0（3cfdd50）；|n.x| >= 0.999999 时只按 n.x 的符号翻转 d0.x;  不乘 R
             4: CPU 无处理（落入 Direction 分支或沿用旧值），只在 GPU 常量路径 3d1a9c0 处理
vel   = d · rnd(Speed) / 60                     // 延迟到期时为 +=
coef  = rnd(SpeedCoef)                          // 延迟未到时为 1.0
accel = (GravityRate == {0,0}) ? 0 : rnd(GravityRate) · (−9.80665 / 3600);  gvel = 0
if Flags & 1: vel += rnd(InheritRate) · emitterVel / 60      // emitterVel = 本帧位移方向 × clamp(|Δ|, InheritDistance.min, .max)；
                                                             // 再经 3dfdf10 旋转（全局标志或粒子标志位 1 时不旋转）
```

延迟：`delay = center + randint[−spread, +spread]`，每粒子存放，每帧减 `step`；到 ≤ 0 时一次性加上 `d·speed` 并赋 coef（重力延迟则赋 accel）。之前初速度为 0、coef 为 1、无重力。Flags 位 5/6 未置时两个延迟字段被忽略（语料中全部为 0，"GravityDelayFrame = 20 无效"即此原因）。已确认（SEM）。独立属性 Velocity3DDelayFrame（类型 80）未追踪。

逐帧积分（3d06d30）：

```
gvel += n · accel
gdir  = (Flags & 4) ? (0,1,0) : normalize(P[+0x104], P[+0x114], P[+0x124])   // 世界上方向在粒子空间的表示
v_eff = vel·k1 + gdir·gvel·k2            // k1、k2 = 1，除非 ctx+0x298 的属性缩放它们
pos  += v_eff · n                         // Flags & 4 时累加到 +0x40；粒子标志 0x400 时不积分
vel  *= powf(coef, n)                     // 先移动后衰减
```

状态：公式、单位、常量、标志门控已确认；gdir 取自 P 的哪几个分量为部分待确认。GPU 类型只上传常量（3d1a9c0，VelocityType 重映射 0→3、1→2 或 1、2→1、3→4、4→5），模拟在计算着色器里。

### 3.3 ScaleAnim（尺寸随时间变化）

| 字段 | 类型 | 作用槽 | 状态 | 依据 |
|---|---|---|---|---|
| `SizeScalarAdd` / `SizeScalarCoef` | CenterSpread | 统一尺寸槽 U（粒子 +0x80）的每帧增量 / 增量的每帧系数 | 已确认 | SCA |
| `SizeXAdd` / `SizeXAddCoef` | CenterSpread | 逐轴槽 X（+0x84） | 已确认 | SCA |
| `SizeYAdd` / `SizeYAddCoef` | CenterSpread | 槽 Y（+0x88）；**TypeRibbonLength 的 Y 槽就是条带长度** | 已确认 | SCA 3d7a4de |
| `SizeZAdd` / `SizeZAddCoef` | CenterSpread | 槽 Z（+0x8c）；只有 Mesh / GpuMesh / NoDraw 读 Z | 已确认 | SCA；语料 Z Coef 0 占 79% |

RE4 文件没有 SizeDelayFrame（加载器只读 0x44 字节）；延迟由独立属性 ScaleAnimDelayFrame（类型 86）实现，未追踪。

出生：8 个区间依次抽样存入逐粒子槽（§1.6.3）；随后 3d1dd00 捕获 Type 刚写入的 U、X、Y、Z 作基值，并记录出生年龄 age0。

```
t = age − age0                                          // 帧，float
f(t, base, add, c) = (c == 1.0) ? base + add·t                                   // 精确浮点比较
                                : base + add·c·(1 − c^t)/(1 − c)                 // c^t = powf(c, t)
U = max(0, f(t, baseU, SizeScalarAdd, SizeScalarCoef))      // 每槽各自夹在 0 以上
X = max(0, f(t, baseX, SizeXAdd, SizeXAddCoef));  Y、Z 同理
```

- 每帧从基值重算，不是增量累加；与帧率无关。首帧增量为 `Add·Coef`（LegacyMode2018 = 1 时为 `Add`，§1.8）。c < 1 时收敛于 `base + Add·c/(1−c)`。
- **Coef = 0 冻结该槽**（Add 失效，不当作 1）。Coef < 0 时：t 恰为整数（60 fps）`powf` 有定义，尺寸逐帧振荡后收敛；t 非整数（如 50 fps）得 NaN，并写进尺寸槽，夹紧挡不住（Re4Native `exp_scaleanim.py` 原生运行）。
- 门控：粒子标志 `[p+4] & 0x800`（碰撞冻结）时整段跳过；`[ctx+0x130] & 0x10` 跳过（推定为 GPU 模拟）；`[ctx+0x130] & 0x20` 时每 tick 重走初始化与基值捕获（动画静止），含义未知。
- GPU 类型把 16 个 float 原样上传（3d1d730），着色器公式未读。

状态：已确认（SCA 3d1d830、3cfd420）。

各 Type 对四个槽的用法（出生默认值 U = X = Y = Z = 1.0）：

| Type | U（+0x80） | X（+0x84） | Y（+0x88） | Z（+0x8c） | 使用方式 | 状态 |
|---|---|---|---|---|---|---|
| Billboard3D | rnd(SizeScalar) | rnd(SizeX) | rnd(SizeY) | — | 宽 = U·X·p[+0xe8]，高 = U·Y·p[+0xec] | 已确认 |
| Polygon | rnd(SizeScalar) | rnd(Width) | rnd(Height) | — | | 已确认 |
| Mesh | rnd(SizeScalar) | rnd(SizeX) | rnd(SizeY) | rnd(SizeZ) | 缩放 = (U·X, U·Y, U·Z) | 已确认 |
| RibbonLength | rnd(SizeScalar) | 宽度路径未追踪 | rnd(Length) | — | 长度 L = Y·U | 部分待确认 |
| RibbonTrail | 发射器最大轴 × rnd(WidthSize)（仅出生） | 1 | 1 | — | 宽 X = X·U·p[+0xe8]，Y = Y·U·p[+0xec] | 已确认 |
| NoDraw | 1 | rnd(SizeX) | rnd(SizeY) | rnd(SizeZ) | 供子 Action / PtLife 跟随缩放 | 已确认 |

粒子标志 0x4000 时上述乘积再乘 +0x90/+0x94/+0x98。

### 3.4 RotateAnim（持续自转）

| 字段 | 类型 | 运行时含义 | 状态 | 依据 |
|---|---|---|---|---|
| `Flags` | u32，只读位 0 | 随机方向：每轴独立，再抽一次，低位为 0 时把该轴速度取反 | 已确认 | ROT 3d1ceb3 |
| `RotationAddX/Y/Z` | CenterSpread，弧度/帧 | 每轴自转速度，写入角度槽 +0x9c / +0xa0 / +0xa4 | 已确认 | ROT |
| `RotationCoefX/Y/Z` | CenterSpread | 速度的每帧系数；**抽样值恰为 0 或 1 时不衰减** | 已确认 | ROT 3d1cc71 |

RE4 文件没有 RotationDelayFrame；独立属性 RotateAnimDelayFrame 未追踪。

```
出生（[p]&2 或 ctx2[+0x18]&8）：角度槽已清零并由 Type 加入初始角；每轴 v = rnd(Add), c = rnd(Coef), 随机方向时可能取反；本次不推进角度
每帧：for a in X,Y,Z:
        angle[a] += n · v[a]
        if !(粒子 [p+8] & 0x400000): 把 angle 折回 [0, 2π)
        if c[a] != 0 && c[a] != 1 && n > 0: v[a] *= powf(c[a], n)
门控：粒子 [p+4] & 0x800 或 ctx2[+0x18] & 4 时整段跳过
```

角度槽 = Type 初始角 + 累积自转。读取方：Billboard3D 的滚转 = 槽 X + 槽 Y（粒子标志 0x2000 时再加 +0xa8），Z 不用；Mesh 用 (X, Y, Z) 按粒子 +4 低 3 位的顺序组成欧拉角，某标志下 Y、Z 取反（3d29776）；Polygon / NoDraw / Billboard2D 的读取未追踪。负 Coef 在 60 fps 下逐帧翻号，其他帧率得 NaN。状态：已确认（ROT 3d1cb60），列出的未追踪读取方除外。

### 3.5 Transform3DModifier（发射器变换的随机起始值与速度）

发射器级，每帧一次（slot 23 = 3d9b300），改写同一 entry 的 Transform3D 块，然后以 perFrame=1 重组矩阵。无文本解析器，字段名按行为命名。

| 字段 | 类型 | 含义 | 状态 | 依据 |
|---|---|---|---|---|
| `Flags` | u32 | 位 0/1/2：平移 / 旋转 / 缩放速度启用；位 3/4/5：对应速度随机符号（需 0/1/2）；位 6/7/8：平移 / 旋转 / 缩放起始值替换 Transform3D 的值；其余位不读 | 已确认 | MOD 3d9a8a6；语料 128 占 117/141 |
| `TranslationStartX..Z`、`RotationStartX..Z`、`ScaleStartX..Z` | CenterSpread | 起始值（旋转为弧度） | 已确认 | MOD |
| `{Translation,Rotation,Scale}Velocity{X,Y,Z}` 与对应 `…Coef` | CenterSpread | 每 1/60 秒的速度与速度系数；文件中每轴按 速度、系数 交错 | 已确认 | MOD |

```
初始化（[ctx+8]&2 = 发射器（重）启动，或 [ctx+0x130] 位 11）：
  for g in T, R, S:
    if 起始位: base[g] = (rnd(StartX), rnd(StartY), rnd(StartZ)); animated[g] = base[g]   // 三轴全部替换，{0,0} 的轴置 0
    if 速度位: vel[g] = rnd(Velocity xyz); coef[g] = rnd(Coef xyz)
               if 符号位: 每轴新抽一次，低位为 0 时 vel 取反     // 缩放组的 Z 取反错乘到 Y（引擎缺陷）
每帧（[ctx+0x130] 位 10 时跳过，只重组）：
  for 启用速度的组 g, 每轴 a:
    base[g][a] += n · vel[g][a]
    vel[g][a]  *= powf(coef[g][a], n)
  animated[g] = base[g]
  调用 3d13250(perFrame=1)
```

- 起始值替换文件值，速度累加在 base 上；缩放无下限夹紧（负缩放会镜像）；Coef = 0 时速度在第一步之后归零。
- `value(k) = start + v0·(1 − c^k)/(1 − c)`，首步为 v0（先移动后衰减）。
- 语料中 23 个轴在 Transform3D 有非零旋转、起始区间为 {0,0}，按代码会被置 0，实机未验证。
- Transform2DModifier（类型 6，0x80 字节）结构相同、二维轴；RE4 语料 0 次使用，C# 无该类。

状态：已确认（MOD）。

### 3.6 PtCollision（粒子与物理碰撞体）

逐粒子向物理系统提交扫掠查询（射线，或带半径的球扫掠），查询结果延迟一帧。不使用深度缓冲，GPU 类型上不生效（GPU 路径不调用，计算着色器无相关成员；部分待确认）。

| 字段 | 类型 | 运行时含义 | 状态 | 依据 |
|---|---|---|---|---|
| `Flags` | u32 | 见下表 | 已确认 | PC 3d63744 |
| `Radius` | MinMax，米 | 仅 Flags 位 2：球扫掠半径；反弹后沿法线推出该距离 | 已确认 | PC 3d6505b |
| `BounceNum` | MinMaxExclusiveI | 处理的命中次数（含最后执行 FinishType 的那次）；2 = 弹一次、第二次命中结束；0 = 不碰撞 | 已确认 | PC 3d650ad |
| `BounceRate` | MinMax | 反射后速度乘数 | 已确认 | PC |
| `VerticalBounce` | float | 反射速度的世界 Y 分量再乘 `BounceRate·VerticalBounce` | 已确认 | PC 3d64ddd |
| `HorizontalBounce` | float | 世界 X、Z 分量乘 `BounceRate·HorizontalBounce` | 已确认 | PC |
| `FinishType` | 枚举 | 0 None（穿过）、1 Kill、2 Freeze | 已确认 | PC 3d64810 |
| `ProjectionOffset`、`ProjectionDist`、`ProjectionCorrectDist`、`ProjectionCorrectDistExtra` | float | 仅投影模式（Flags 位 4）；后两者组成 MinExtra（离面抬升距离） | 已确认 | PC 3d639f0 |
| `LookNormalDirectionOffset` | float | Freeze 时沿法线的偏移 | 已确认 | PC 3d648be |
| `DelayFrameCollision` | CenterSpreadI | 粒子年龄达到该帧数前整段跳过；投影模式忽略 | 已确认 | PC 3d6462b |
| `LayerName` / `MaskName` | UTF-16（Flags 位 0 / 位 1 时存在，按此顺序） | 碰撞层名（按名查索引）/ 掩码名（单比特）。空掩码名 = 全部层；两位都不置时用发射器上下文默认值 | 部分待确认（两张 CollisionFilter 表的含义未对照 .cfil） | PC 3d64480；语料 "Bullet" 598/774 |

Flags：位 0 层名、位 1 掩码名、位 2 用 Radius、位 3 最后一次命中时同时置粒子标志 0x800（冻结 ScaleAnim / RotateAnim / UV 等）、位 4 投影模式、位 5-6 / 7-8 / 9-11 投影模式下速度重定向 / 旋转重定向 / 欧拉顺序、位 12 Freeze 时对齐法线（仅 Billboard3D、Polygon、RibbonParticle）、位 13 Freeze 以命中点为基准。更高位不读。已确认（PC）。

```
跳过：粒子未存活、[ctx+0x100]->byte[1] != 0、delay > age、hits == 0
初始化（延迟结束后首次或新粒子）：handle = −1; prev = 世界位置; radius = 位2 ? rnd : 0;
                                  hits = rnd(BounceNum); rate = rnd(BounceRate)；本帧不提交
之后每帧：若上帧查询已完成且命中 → 处理命中；否则提交 prev → 当前世界位置 的扫掠，prev = 当前
命中（位置 H、法线 N）：hits -= 1; handle = −1
  hits > 0（反弹）：
    v = 粒子世界速度（含下一步重力，3d630c0）
    if |v|² < 1e-4: 直接按结束处理
    d = normalize(reflect(v/|v|, N)); v' = d·|v|                 // 速度大小保持
    v'.y *= rate·VerticalBounce; v'.x *= rate·HorizontalBounce; v'.z *= rate·HorizontalBounce
    pos = H + (位2 ? N·radius : 0) + 块偏移(+0x48..0x50，实际为 0)
    写回局部速度；重力速度 gvel = 0（accel 保留）
  hits == 0（结束）：
    None: 不处理（位 3 时置 0x800）
    Kill: [p+4] |= 0x200（下一帧开头死亡，触发死亡处理）
    Freeze: 位 3 时置 0x800；[p+4] |= 0x400（停止位置积分）；
            if Look != 0 || 位13: pos = (位13 ? H : 当前世界位置) + Look·N；位 12 时对齐法线
```

- 命中后粒子被移回命中点，所以穿透最多持续一帧。
- 每次命中还会把 ctx+0x268 属性的逐粒子状态的第一个 dword 清零（3d647e0）。ctx+0x268 是 ParentOptions 的块，该 dword 即 §2.2.3 的跟随率 rate，推定命中后粒子不再随发射器移动：部分待确认。
- 投影模式（语料 1/774）：出生时把粒子投到发射器局部 XZ 平面（沿局部 Y 偏移 ProjectionOffset），沿 −局部 Y 射线长 ProjectionDist，命中则贴到表面并按位 5-11 重定向。旋转组合细节部分待确认。

### 3.7 PtColliderAction（命中触发子 Action：碰撞部分）

独立于 PtCollision 的另一套扫掠（自己的层、掩码、半径），只负责在命中时启动子 Action，不改变粒子运动（除非位 2）。

| 字段 | 类型 | 运行时含义 | 状态 | 依据 |
|---|---|---|---|---|
| `Flags` | u32 | 位 0 层名、位 1 掩码名、位 2 命中即杀死粒子、位 3 用 Radius 球扫掠；位 10 及其附加块不由 RE4 加载器读取 | 已确认 | PC 3d61c64 |
| `Radius` | MinMax | 仅位 3 | 已确认 | PC |
| `linkedAction` | u32 | 文件 Action 表下标；0xFFFFFFFF = 无 | 已确认 | PC 3cfb400 |
| `LayerName` / `MaskName` | UTF-16 | 先位 0 的层名、后位 1 的掩码名 | 已确认 | PC 3d61c67 |

每帧（3d62430，在 PtCollision 之前）：新粒子只初始化；之后每帧提交 prev → 当前位置的扫掠，查询完成且命中时触发（§5.2）。没有命中计数、没有冷却：静止在表面上的粒子（未置位 2）每帧都会重新触发。门控字节为 `[ctx+0x100]->byte[0]`（PtCollision 用 byte[1]）。已确认（PC）。

### 3.8 Noise（逐帧位置抖动）

| 字段 | 类型 | 运行时含义 | 状态 | 依据 |
|---|---|---|---|---|
| `LowFrequency` | CenterSpread | 低频相位的速度：每粒子 `center + spread·k2`，**周 / 秒** | 已确认 | 3dc7326..3dc7344 |
| `LowFrequencyWidth` | CenterSpread | 低频分量的幅度：每粒子 `center + spread·k3`，米 | 已确认 | 3dc6da8..3dc6dbf |
| `HighFrequency` | CenterSpread | 高频相位的速度：每粒子 `center + spread·k0`，周 / 秒 | 已确认 | 3dc7326..3dc7344 |
| `HighFrequencyWidth` | CenterSpread | 高频分量的幅度：每粒子 `center + spread·k1`，米 | 已确认 | 3dc6d94..3dc6db4 |

四对值都是 CenterSpread（C# 原先声明为 `via.Range`，文件字节不变）：中心 + 幅度 × 出生时抽一次的系数 k ∈ [−1, 1)，之后固定。块布局 +0x10..+0x2c 依次是上表四对 (center, spread)。

逐粒子记录（9 个 float，属性块 +8 给出在逐粒子附加数据里的偏移）：k0、k1、k2、k3、高频相位 b、低频相位 a、（空）、方向 x、方向 y。

```
出生（3dc7050，[p] & 2；salt 0xd17f868f，key = 槽号）：
  b = 2π·u1;  a = 2π·u2;  k2 = 2u3 − 1;  k0 = 2u4 − 1;  k3 = 2u5 − 1;  k1 = 2u6 − 1
  dir = normalize(2u7 − 1, 2u8 − 1)；长度 < 1e-6 时为 (1, 0)
每帧，出生帧也算（3dc6d50）：
  LW = LowFrequencyWidth.center + .spread·k3;   HW = HighFrequencyWidth.center + .spread·k1
  C = cosf(a)·LW + cosf(b)·HW;         S = sinf(a)·LW + sinf(b)·HW
  偏移 = (C·dir.x, C·dir.y, S)（E+0x168 位 0，3D 族），否则 (C, S, 0)
  p[+0x34..+0x3c] += 偏移；[p+4] |= 0x1000
  a += (LowFrequency.center + .spread·k2)·dt·2π;   b += (HighFrequency.center + .spread·k0)·dt·2π      // dt = ctx+0x10，秒
  a、b 各自 fmodf(·, 2π)；结果为负时取 2π − r
```

- 偏移加到粒子 +0x34 起的独立字段，位置 +0x28 本身不变。原生运行里 +0x34 每帧正好等于当帧的偏移，不是累积量，所以它在别处每帧清零（清零位置没有找）。它怎样进入渲染位置没有追踪。
- 新粒子且 `E+0x174` 符号位置位、位 8 清零时，3dc735c 另调一次 3dc6d50，相位增量取负，结果加到 +0x1c..+0x24（上一位置）。这条路径在语料里的触发条件未核对。
- GPU 类型只上传常量，着色器公式未读。

状态：已确认（读码 3dc7050、3dc6d50）。`tools/Re4Native/exp_noise.py` 在 re4.exe 上原生运行 csa114 dust（25 个粒子，600 tick），偏移与逐粒子记录 5,424 帧与上式逐位一致，参考实现为 `tools/Re4Native/noise_ref.py`。MonsterHunterWilds.exe 的同一属性也与上式逐位一致（偏移在 Wilds 粒子 +0x40）。

---

## 4. 外观

### 4.1 UVSequence 与 UVSequenceModifier（翻页动画）

| 字段 | 类型 | 运行时含义 | 状态 | 依据 |
|---|---|---|---|---|
| `SequenceNo` | MinMaxExclusiveI | .uvs 内的序列号，8 位；越界夹到最后一个 | 已确认 | UVS 3d1ec5b；语料恒为 {0,1} |
| `PatternNo` | MinMaxExclusiveI | **起始帧**，8 位；越界夹到最后一帧 | 已确认 | UVS 3d1eca4 |
| `PlaySpeed` | MinMax | 每 1/60 秒前进的帧数（1.0 = 60 帧/秒）；≤ 0 不播放 | 已确认 | UVS 3d1ed02 |
| `Flags` | u32，用 12 位 | 见下表 | 已确认 | UVS 3d1e139 |
| `UVSPath` | UTF-16（字符数含结尾 NUL） | .uvs 资源路径；空路径 = 整张贴图、不动画 | 已确认 | UVS 3d1e0b1 |

| Flags 位 | 字段 | 取值 |
|---|---|---|
| 0-1 | PlayMode | 0 静止（只显示起始帧，忽略 PlaySpeed）、1 循环、2 播一遍后粒子立即死亡（无淡出）、3 播一遍停在末帧 |
| 2-3 / 4-5 | FlipU / FlipV | 0 关、1 总是、2 每粒子 50% 随机、3 保留槽内旧值（不要用） |
| 6-7 | Reverse | 同上三态；倒放 |
| 8-9 | Rotate90 | 同上三态；只进 GPU 实例字（slot 27），CPU 矩形路径不处理；旋转方向未定 |
| 10 | FrameBlend | 前一帧与当前帧交叉淡化 |
| 11 | 未知 | 复制到发射器 +0x174 位 11 |

```
出生：seq = roll(SequenceNo); frame = roll(PatternNo); v = roll(PlaySpeed)
      T = (v > 0) ? (1/60)/v : 0;  acc = 0                       // T = 每帧秒数
每帧（存活、PlayMode != 0、T > 0）：
  acc += dt
  if acc >= T:
     steps = int(acc/T);  acc = frac(acc/T)·(1/60)                 // 余量乘 1/60 而不是 T
     count = 该序列的帧数
     正放: frame += steps; if frame >= count: 模式3 → count−1；模式2 → count−1 且 [p+4] |= 0x200；模式1 → 0（不是起始帧）
     倒放: if frame < steps: 模式3 → 0；模式2 → 0 并杀；模式1 → count − (steps % count)；else frame −= steps
FrameBlend: 另存前一帧矩形，权重 = acc/T
```

- 一遍的时长 = `(count − 起始帧)/(60·speed)` 秒；循环模式第二遍起从 0 开始。
- 余量公式 `frac·(1/60)` 在 speed ≠ 1 时会漂移：部分待确认（读码中等把握）。其余已确认（UVS 3d1ee93..3d1f018，杀死在 3d08093 执行）。

UVSequenceModifier（逐粒子，替换宿主 UVSequence 的 T；调用紧跟 UVSequence，所以从下一次 UV 步进开始生效）：

| 字段 | 类型 | 含义 |
|---|---|---|
| `Flags` | u32 | 位 0 启用；位 1：第三项为系数（1）或帧数（0） |
| `PlaySpeedInit` / `PlaySpeedFinal` | MinMax | 出生速度 / 最终速度 |
| `PlaySpeedChangeTimeCoef` | MinMax | 线性模式：过渡帧数；指数模式：每帧保留系数 c |

```
出生：Init、Final、Third 各抽一次；acc = (Flags & 2) ? 1 : 0
每帧（n = 0 于出生调用）：
  线性：acc += n; w = min(acc/Third, 1)        指数：acc = powf(Third, n)·acc; w = 1 − acc
  speed = (1−w)·Init + w·Final;   T = (speed > 0) ? (1/60)/speed : 0
```

门控：需宿主 PlayMode ≠ 0、粒子未冻结（0x800）、资源标志位 29 清零；资源 `+0x168` 位 16 与 Type 首个标志字位 1 同时置位时跳过（推定 GPU 路径）。状态：已确认（MOD 3d9c260），跳过条件部分待确认。

### 4.2 Type* 公共颜色头

所有 Type 的头部是 7 个 dword，`3e0eae0` 原样复制：

| 序 | C# 名 | 运行时 | 状态 | 依据 |
|---|---|---|---|---|
| 0 | `Flags`（Billboard3D、Polygon、Billboard2D）/ `BlendFlags`（条带与 GPU 类） | 低 4 位 = 混合方式，写入渲染键 `dst+0x44` 位 11-14（= Polygon3DConstant 的 cbEffect3DFlags）；位 4（0x10）= 出生颜色在 Color 与 ColorRange 之间随机；位 5（0x20）= 常量 `+0x110 = 1.0`，含义未知 | 位 4 已确认；低 4 位 = BlendType 部分待确认 | TA 3e0ebe5, 3d18835；RW（PS 按此分支） |
| 1 | `Color` | 粒子出生颜色 | 已确认 | TA 3cfcf50 |
| 2 | `ColorRange` | 随机颜色的另一端；EdgeBlend 时引擎名 EdgeOuterColor；另以 float4/255 上传到 +0xf0 | 已确认 | TA |
| 3 | `ColorRate` | 上传 +0x10c。按混合方式另名 ContrastRate / InnerIntensity / MultiplyColorRate / EmissionRate | 上传已确认，着色器用法未读 | TA, EXP |
| 4 | `Intensity` | 无条件上传 +0xec。另名 BackIntensity / OuterIntensity / MultiplyBlackRate（引擎没有 "Intensity" 这个名字） | 上传已确认，着色器用法未读 | TA |
| 5 | `EdgeBlendRange` | 上传 +0x108 | 上传已确认 | TA, RT |
| 6 | `AlphaRate` | 上传 +0xe4（GpuParticleVS 的 alphaRate 在 148） | 上传已确认 | TA, SHE |

- BlendType 枚举：AlphaBlend 0、Physical 1、AddContrast 2、EdgeBlend 3、Multiply 4；没有单纯的"加法"。语料：0 = 烟，1 = 辉光 / 火 / 火花，3 总配 Intensity > 0 和非白 ColorRange，4 总配 Intensity > 0。
- 出生颜色：`(Flags>>4)&1` 为 1 时每通道 `lerp(Color, ColorRange, u)`，四通道共用一个 u；否则恰为 Color。写入粒子 +0x4c..+0x4f（RGBA8）。粒子标志 0x200000 时跳过（3cfcf50）。已确认（TA, RT）。
- 条带头（3e10150，RibbonLength / Follow / Chain / FixEnd）在 7 个 dword 后接：`Flags`、`SizeScalar`（CenterSpread）、`WidthSize`（CenterSpread）、`TextureRepeatNum`、`ShadowMultiplier`（→ 常量 +0x178）。
- 混合因子（src/dst）属于管线状态（推测在 vfxstates.vsdf，未解包）；ColorRate、Intensity、EdgeBlendRange、AlphaRate 在像素着色器里的组合未读（§7）。

### 4.3 各 Type 的几何

#### 4.3.1 TypeBillboard3D（EmitterContextType 2）

| 字段 | 含义 | 状态 | 依据 |
|---|---|---|---|
| `Rotation`、`SizeScalar`、`SizeX`、`SizeY` | CenterSpread；出生时写 +0x9c（滚转，弧度）、+0x80、+0x84、+0x88 | 已确认 | TA 3d187e0 |
| `ShadowMultiplier` | → 着色器常量 +0x178 | 部分待确认 | TA |
| `ParticleIgnoreScale`（u32 位域的首字节） | 位 3 = 忽略发射器缩放；位 0-2 → attr+0x68（未知）；位 4、5 写发射器标志（未知）。C# 的 EnableGroupColor / OcclusionByParticleShadow / Reserved 在 RE4 恒为 0 | 已确认（位 3） | TA 3d18511；语料值 8 |
| `Offset` | via.Range：`±first`（镜像取反）→ 块 +0x154，second → +0x158，作为着色器常量；作用方向未读 | 部分待确认 | TA 3d1864d |

每帧 `p[+0xe8] = p[+0xec] = max(emitterScale.x, .y, .z)`（ParticleIgnoreScale 时为 1.0），尺寸随发射器最大缩放轴实时变化。宽 = U·X·p[+0xe8]、高 = U·Y·p[+0xec]（§3.3）。CPU 只把滚转角（镜像时 ×−1）写入实例；面向相机由着色器完成，未读。依据：TA、SCA、ROT。

#### 4.3.2 TypeBillboard2D、TypeNoDraw

- Billboard2D（类型 1）：出生抽样同 3D；SizeX / SizeY 为像素（语料 128 / 256）；`Repeat` 低 2 位写粒子标志位 30-31。不在 3d045d0 分派表中（由 3d05193 调用）。已确认（TA）。
- NoDraw（类型 9）：`Flags` 位 0 = 颜色随机；`RotationX/Y/Z`、`SizeX/Y/Z`（CenterSpread）写 +0x9c..+0xa4、+0x84..+0x8c，+0x80 = 1，RotationOrder 写粒子 +4。不绘制，数值供子 Action（PtLife 跟随缩放）使用。已确认（TA 3d6c123）。

#### 4.3.3 TypePolygon（类型 10）

| 字段 | 含义 | 状态 | 依据 |
|---|---|---|---|
| `RotationOrder`、`RotationX/Y/Z` | 出生抽样写 +0x9c..+0xa4（弧度）；实例写入时由欧拉角 + 顺序构造四元数（dfae10），即世界朝向的四边形，不面向相机 | 已确认 | TA 3d6f5e0；语料 RotationX = π/2 占 65% |
| `SizeScalar`、`Width`、`Height` | 写 U、X、Y 槽；不乘发射器缩放（+0xe8/+0xec = 1） | 已确认 | TA |
| `Offset` | first（镜像取反）、second 以半精度写入实例，作用轴在着色器 | 部分待确认 | TA |
| `Flags2` | 0x40：朝向跟随运动方向，`OrientDirectionUpVector` 选上方向轴；0x80：非新粒子时锁定朝向；0x04：某槽向量置 (1,1,1) | 部分待确认 | TA；语料 22/1698 非零 |
| `ShadowMultiplier` | → 常量 +0x178 | 部分待确认 | TA |

#### 4.3.4 TypeRibbonLength（类型 4）

| 字段 | 含义 | 状态 | 依据 |
|---|---|---|---|
| `LengthFlags` | 位 0：条带轴取本帧位移方向；位 1：首次取到方向后锁定；位 2：位移改由上下文矩阵变体（3dfd840）测量；位 3：固定起点模式；位 9：固定起点模式下起点不随发射器重新变换；位 10 → +0x20c 位 16，未追踪 | 已确认（位 0-3、9） | SEM 3d7b3d0 |
| `Length` | CenterSpread；出生写粒子 +0x88（= ScaleAnim 的 Y 槽） | 已确认 | SEM |
| `ShapeDivision` | 沿长度的点数，夹在 [2, 1000] | 已确认 | SEM |
| `BasingPoint` | AnchorPair：`length` 为纵向锚点 b；`width` 每粒子复制但 CPU 布局不用，GPU 路径上是横向锚点 | b 已确认；width 部分待确认 | SEM, SHE |
| `ReleaseFixEnd` | CenterSpread；固定起点模式下的释放距离 | 已确认 | SEM |
| `DirX/Y/Z` | CenterSpread；位 0 关时轴 = `normalize(rnd(DirX), rnd(DirY), rnd(DirZ))`，出生定死 | 已确认 | SEM |
| `HeadColor`、`ColorPlace*`、`HeadScale`、`ScalePlace*` | 头尾渐变；CPU 路径怎样烘进逐段颜色 / 尺寸未追踪；GPU 路径不使用 | 部分待确认 | SHE |

```
出生：L0 = rnd(Length) → p[+0x88]；d = 位0 ? 0（首次更新时填） : normalize(rnd(DirX..Z))
每帧：if 位0 且 |Δ|² > 1e-6: d = normalize(pos_now − pos_prev)（位1 时只做一次）
布局（3d7c2b0）：L = p[+0x88]·p[+0x80]（× 全局缩放项）;  b = BasingPoint.length
  tail = P − (1 − b)·L·d;   head = P + b·L·d          // b = 0 全在粒子后，0.5 居中，1 全在前
固定起点（位 3，3d7c440）：tail 留在出生位置，head = 粒子，d = normalize(head − tail)，L = 距离；
  距离 × 缩放 ≥ ReleaseFixEnd 时释放（标志 2），之后按普通条带布局
```

宽度展开与朝向在着色器中完成（§7.2）。状态：已确认（SEM 3d7a460、3d7cac0）。

#### 4.3.5 TypeRibbonFollow（类型 3）

| 字段 | 含义 | 状态 | 依据 |
|---|---|---|---|
| `FollowFlags` | 位 0：定长绳模式；位 1：历史点存发射器局部坐标（拖尾随发射器刚性移动）；位 2\|3：跳过颜色求值，改用全局调试色；位 6 → +0x20c 位 16；位 4、5 未追踪 | 位 0、1 已确认 | TB 3d79720, 3d78cc0 |
| `StretchDistance` | CenterSpread，米；绳长 = `max(FLT_MIN, rnd)`，仅绳模式 | 已确认 | TB 3d78226 |
| `ShapeDivision` | 历史点数，夹在 [2, 1000] | 已确认 | TB 3d78833 |
| `SplineDivision` | 相邻历史点之间插入的样条点数；顶点数 = (n−1)·该值 + n | 已确认 | TB 3d79fb1 |
| `IntervalFrame` | 采样间隔帧数；0 = 每帧 | 已确认 | TB |
| `GhostStretch` | MinExtra：出生时 `min + u·extra`，参与一个渲染标量（推定为贴图拉伸归一化） | 部分待确认 | TB 3d7a105 |
| `HeadColor`…`ScalePlace2Ratio`、`FadeByTwistMin/Max` | 渐变参数与扭转淡出，渲染侧，未追踪 | 部分待确认 | TB |

```
出生：count = 0; acc = 0; stretch = max(FLT_MIN, rnd(StretchDistance))
每帧（3d79cb0）：acc += step
  sample = 粒子位置（+0x28，`[p+4] & 0x1000` 时加 +0x34..，3d78c6f）× 发射器矩阵 → 世界；FollowFlags 位1 时存局部坐标
  默认（位0 清零）：if acc >= IntervalFrame: 历史整体后移一格，sample 入槽 0，count = min(count+1, ShapeDivision)
                   else: 只覆盖槽 0（头部跟随粒子）
                   acc %= IntervalFrame（0 视为每帧）
  绳模式（位0）：头部移动 > 1.19e-7 时，沿上一帧路径按间距 stretch·k/(ShapeDivision−1) 重新排布全部点（k = 1，除非某标志组合）
  然后：SplineDivision > 0 时样条细分（3d78de0）→ 3d77a80 → 3e0fda0（点 +0x40 线性重映射，推定为沿拖尾的 V）
```

拖尾时长 = `(ShapeDivision − 1) × IntervalFrame` 帧粒子自身的运动；世界长度 = 速度 × 时长。Life 只决定何时消失。绳模式松弛的精确步骤部分待确认。

#### 4.3.6 TypeRibbonChain（类型 5）

| 字段 | 含义 | 状态 | 依据 |
|---|---|---|---|
| `ChainFlags` | 位 0\|1：方向取根部本帧位移（==2 时取反），否则用 Dir；位 3：方向原样使用；位 4（0x10）：局部、不乘发射器矩阵；位 5（0x20）：Dir 与 BlendDir 沿链混合；0x1010（且无 0x8）+ 镜像：X 取反；0x2000：起点沿链后移（未追踪）；0x4000：不推进模拟 | 已确认（未追踪项除外） | TC |
| `Length` | CenterSpread；链全长 = rnd × 粒子缩放 | 已确认 | TC 3d7daca |
| `Acceleration` | CenterSpread，m/s²，沿 Dir 方向（无内置重力：重力 = Dir (0,−1,0) + 9.8） | 已确认 | TC |
| `FrameInfluence` | CenterSpread，夹在 [0,1]；每 1/60 秒速度保留系数（阻尼） | 已确认 | TC 3d81635 |
| `VertexInfluence` | CenterSpread，夹在 [0,1]；约束修正量转成速度的比例 | 已确认 | TC |
| `ShapeDivision` | 关节数 N，夹在 [2, 1000] | 已确认 | TC 3d7ec29 |
| `BasingPoint` | 几何阶段读 length 分量，作用未追踪 | 部分待确认 | TC 3d81372 |
| `DirX/Y/Z`、`BlendDirX/Y/Z` | CenterSpread；出生 `normalize(rnd(...))` | 已确认 | TC |
| `AxisBlendRate` | CenterSpread；仅 0x20 | 已确认 | TC |

```
每帧：acc += dt; steps = floor(acc·60); 模拟步长 = steps/60 秒（固定 60 Hz 子步）
关节 0 = 粒子位置；i = 1..N−1：
  d = 方向（按 ChainFlags；0x20 时 t = clamp((i/(N−1) + AxisBlendRate)·joint[+0x4c], 0, 1)，d = normalize(lerp(Dir, BlendDir, t))）
  vel += Acceleration·d·dt;  pos += vel·dt
  seg = |scale·L|/(N−1);  if |pos − prev| > seg: 拉回到 seg，vel += (修正量/dt)·VertexInfluence     // 不可伸长
  vel *= FrameInfluence^(dt·60)
```

状态：已确认（TC 3d81580、3d82850）。宽度与朝向在着色器。

#### 4.3.7 TypeRibbonFixEnd（类型 15）

| 字段 | 含义 | 状态 | 依据 |
|---|---|---|---|
| `FixEndFlags` | 位 0：释放时给粒子置 0x400 并冻结整条（推定粒子死亡）；&6：逐点颜色取头尾渐变（否则恒定）；&0x18：逐点尺寸取渐变（否则 1.0） | 位 0 部分待确认，其余已确认 | FE 3d9297b |
| `BasingPoint` | via.Range，推定同 Chain / RibbonLength；CPU 不读 | 部分待确认 | FE |
| `ReleaseFrame` | 倒计时 = `min + trunc((max−min)·u)`，再 +1；min = 0 → 永不释放 | 已确认（C# 类型声明为 MinMaxI，与读码不符） | FE 3d91b08, 3d91e5b |
| `ShapeDivision` | 点数 N（语料 169/183 为 2，即锚点到粒子的直线） | 已确认 | FE 3d921ef |
| `ReleaseInterpolation` | 释放后每帧 `t += RI/60`（不乘 dt） | 已确认 | FE 3d9244d |

```
出生：A[k] = B[k] = 出生位置（P 空间）；c = 1；countdown 如上
每帧（一游戏帧一次采样，随帧率变化）：
  A 后移一格，A[0] = 粒子位置；未释放时 B 也后移，B[0] = inv(P)·E·spawnPos     // 锚点 = 出生位置随发射器平移与旋转
  c = min(c+1, N)
  60 Hz 累加器给出 steps；countdown −= steps；到 0 → 释放（位 0）
  已释放且 t < 1：t = min(1, t + RI/60)；B[c−1−i] = lerp(B[c−1−i], A[i], t)
  输出 out[i] = (t >= 1) ? A[i] : lerp(B[c−1−i], A[i], 1 − i/(c−1))
```

E 推定为当前发射器 / 所有者世界矩阵（部分待确认）。释放后条带缩成 N 帧运动拖尾（N = 2 时几乎消失）。

#### 4.3.8 TypeRibbonTrail（类型 11）

| 字段（C# 顺序） | 引擎读取 | 状态 | 依据 |
|---|---|---|---|
| `BlendFlags`（fo 4） | 头部 [0] | 已确认 | RT 3d76f53 |
| `FlagsRe4`（fo 8）、`Color`（0xc）…`AlphaRate`（0x20） | 引擎把 fo 4..0x1f 当标准 7-dword 头、fo 0x20 当一个不读的 dword；C# 把多出的 dword 放在 fo 8。按引擎读法，官方 52 个文件的 Color = 0x00000001（alpha 0）、ColorRate = NaN | 待清理（C# 与 exe 不一致；语料与 C# 布局吻合，矛盾未解，§8） | RT |
| `WidthSize` | CenterSpread；出生 `w = rnd`，粒子 U = 发射器最大轴长 × w（只在出生时） | 已确认 | RT 3d773f0 |
| `ShadowMultiplier` | → 常量 +0x178 | 已确认 | RT |
| `Division` | 历史点数 = Division + 1 | 已确认 | RT |
| `IntervalFrame` | 采样间隔；**0 = 整数除以零，游戏崩溃** | 已确认 | RT 3d7783e |

```
出生：acc = 0; count = 1; 点 0 = 当前采样
每帧：acc += step；if acc >= IntervalFrame: 历史后移，点 0 = 新采样，count 至多 Division+1；否则头部不更新
      acc %= IntervalFrame
采样 = 粒子位置（+0x28，`[p+4] & 0x1000` 且调用方 r8b ≠ 0 时加 +0x34，3dfd040）× 3dfca50 的矩阵 → 世界坐标；历史永远在世界空间
点数据：方向 = 指向尾部的单位向量；+0x40 = 累计长度 / 总长（推定为 V）；点颜色恒为白、alpha 1
绘制：count ≤ 1 不画；颜色 = 粒子 RGB，A = 生命淡出字节 × 颜色 alpha；宽 X = +0x84·+0x80·+0xe8，Y = +0x88·+0x80·+0xec
```

拖尾覆盖最近 `Division × IntervalFrame` 帧；头部最多滞后 IntervalFrame−1 帧。块 +0x24 位 0（ctx 标志 4 置、2 清）时整段跳过，推定为暂停。

#### 4.3.9 TypePolygonTrail（类型 12）

| 字段 | 含义 | 状态 | 依据 |
|---|---|---|---|
| `TrailFlags` | 位 3：定长模式（语料全部置位），清零为间隔模式；0x60：粒子色强制白，逐顶点用 HeadColor / Place1 / Place2 渐变（否则顶点白、颜色来自头部）；0x10、0x80、0x100 写标志未追踪；位 0 无 CPU 读取 | 已确认（位 3、0x60） | PT 3d75d2d, 3d75b51 |
| `ShadowMultiplier` | → 头部构造 xmm2 | 部分待确认 | PT |
| `Axis` | 0/1/2 = 采样矩阵的 X/Y/Z 行，3/4/5 = 取反 | 已确认 | PT 3d72560 |
| `Length` | CenterSpread；每粒子 `rnd × 发射器世界矩阵最大行长` → +0x80 | 已确认 | PT 3d75c11 |
| `StretchDistance` | 定长模式的长度倍数 | 已确认 | PT |
| `NumTrailDivision` | 历史容量 N+2 | 已确认 | PT |
| `NumVerticalDivision` | 横向行数 V+2 | 已确认 | PT |
| `NumSplineDivision` | 相邻历史点之间插入的 Catmull-Rom 点数 | 已确认 | PT |
| `IntervalFrame` | 间隔模式的采样间隔，读入时 `max(1, v)` | 已确认 | PT 3d733fa |
| `HeadColor`、`Place1`、`Place2`、`Place1Ratio`、`Place2Ratio` | 仅 0x60 时有效；关键点位置 = 比例 × 列数 | 已确认 | PT |

```
采样 = {pos = 粒子局部位置 × 粒子矩阵 P（旋转行归一化）, axis = normalize(P 的第 Axis 行)}；粒子 +8 位 1 时旋转换成单位四元数
出生：采样复制到槽 0、1、N+1；count = 1
间隔模式：acc += step；acc >= IntervalFrame 时后移，count = min(count+1, N+2)；新采样总在槽 0；acc %= IntervalFrame
定长模式：新采样与槽 0 完全相同则不变；否则 count = min(count+1, N+2)，把（新头 + 旧历史）按等弧长重采样，
          步长 = (+0x80 × StretchDistance)/count，弧长取 max(|Δbase|, |Δtip|)（tip = pos + axis），base 与 tip 各自 Catmull-Rom
顶点：列数 = (count−1)·NumSplineDivision + count，行数 = V+2；vertex(col,row) = pos + axis·(+0x80·row/(rows−1))
```

历史存世界坐标、不再变换，形成扫掠面；采样点是条带的一边而不是中心。count < 2 不画。定长模式最大弧长 ≈ Length × 最大缩放 × StretchDistance。依据：PT。

#### 4.3.10 TypeStrainRibbon（类型 8）

| 字段 | 含义 | 状态 | 依据 |
|---|---|---|---|
| `Flags` | 0x1：U 从 0 到 N−1（每段重复一次）；0x2：终点用内嵌形状抖动（出生抽一次）；0x4：起点 = InitialPosition 原值（镜像时 X 取反）；0x40\|0x80：逐点颜色渐变；0x100：总用悬链线；0x400：启用断裂；0x2000：U 按世界长度平铺（`U_end = LengthUVScale·D`）；0x4000：平铺锚在远端；0x8000：平铺除以粒子缩放 | 已确认 | ST |
| `InitialPosition` / `TerminalPosition` | Vector3；起点相对粒子、终点相对骨骼（或发射器） | 已确认 | ST |
| `SegmentNum` | 段数 N（≥1），N+1 个点 | 已确认 | ST |
| `Width` / `Length` | CenterSpread；出生写 +0x84 / +0x88；Length 为绳长 | 已确认 | ST |
| `SagAxis` | 下垂方向索引 {+X,+Y,+Z,−X,−Y,−Z}（世界轴） | 已确认 | ST |
| `AxisRotation` | CenterSpread，**度**（出生 ×π/180） | 已确认 | ST 3d60608 |
| `Cutoff*` | 断裂：EnduranceLength（MinExtra）、EnduranceFrame（MinMaxI）已读；CutoffRatio / Gravity / BendScaleCoef / BendSmooth 只按名字推断 | 部分待确认 | ST |
| `Shape*` | 内嵌 EmitterShape3D，仅 Flags 0x2 使用（语料从未置位） | 已确认 | ST |
| 颜色头 | 位于文件末尾（BlendFlags、Color、ColorRange、ColorRate、Intensity、[5]、[6]） | 已确认 | ST |
| `boneName` | 终点骨骼名，查找方式同 ParentOptions | 已确认 | ST |

```
每帧：start = 粒子世界位置 + InitialPosition·P（旋转部分）
      end   = TerminalPosition（+ 形状抖动）× 骨骼矩阵（找到骨骼时）或 × 发射器世界矩阵
      D = |end − start|; L = Length·最大发射器缩放
      if (D >= L 且无 0x100) 或点数 < 3: 直线，N+1 个等距点
      else: 长度为 L 的悬链线（coshf 求解，3d5d3c0；竖直情形 3d5e0b0），所在平面由弦与 SagAxis 确定；N = 1 永不下垂
      断裂（0x400）：D >= L + EnduranceLength 时计数 += step，否则清零；超过 EnduranceFrame 时断开，之后走 3d5fb30（未追踪）
UV：点 +0x40 = 累计弧长 / 总长（默认整条贴一次）
```

#### 4.3.11 TypeMesh（类型 6）

| 字段 | 含义 | 状态 | 依据 |
|---|---|---|---|
| `Flags` | 位 1：FrontAxis 对准运动方向（带 `min(dt·k,1)` 平滑；位 2 跳过该路径）；位 24：部件按顺序取；位 8-9：渲染对象位 27-28（语料 256 = 值 1，含义在渲染侧）；位 27：把 Emissive 复制进粒子；其余位见 TD | 位 1、24 已确认 | TD 3d31390, 3d30e68 |
| `Color`、`ColorRange`、`ColorRate`、`EmissiveColor`、`EmissiveRate` | 颜色参数；材质响应在着色器 | 部分待确认 | TD |
| `MaxPartsNum` | 部件序列长度（循环长度） | 已确认 | TD |
| `PartsStartNo` | MinMaxExclusiveI；起始部件（位 24 时按顺序在 [min, maxEx) 内循环） | 已确认 | TD 3d30eb6 |
| `PlaySpeed` | MinMax；`lerp × 60`，单位 部件 / (1/60 秒) | 已确认 | TD 3d30f1c |
| `PlaySpeedCoef` | CenterSpread；`speed *= powf(coef, dt·60)` | 已确认 | TD |
| `PlayType` | 0 Pause、1 Loop（取模）、2 Finish（停在末部件并杀死粒子）、3 停在末部件 | 已确认 | TD 3d311d8 |
| `PlayOrder` | 1 倒放、2 50% 倒放 | 已确认 | TD |
| `RotationOrder`、`RotationX/Y/Z` | CenterSpread，弧度；写角度槽 | 已确认 | TD |
| `SizeX/Y/Z`、`SizeScalar` | CenterSpread；写 X/Y/Z 与 U 槽；最终缩放 ≈ (U·X, U·Y, U·Z) × 发射器缩放 | 已确认（乘积为推定） | TD, SCA |
| `FrontAxis` | 0 X、1 Y、2 Z（默认 Z） | 已确认 | TD |

部件索引每帧 `index += speed·dt`，越过 MaxPartsNum 时按 PlayType 处理；索引写入实例，部件选择与插值在渲染侧。

材质参数覆盖表（`properties`，每条 32 字节：哈希、mdf2 参数下标、分量数 u16、类型 i16、`flags`、值 X Y Z W）：

| 项 | 引擎行为 | 状态 | 依据 |
|---|---|---|---|
| 粒子参数槽 | 每个粒子每条参数一个 36 字节槽：+0 当前 X Y Z W，+16 文件原值 X Y Z W，+32 字节"曲线已写"标志；创建时两份都填文件原值 | 已确认 | 3e10680（由 3d2e910 调用，缓冲在 ctx +0x108） |
| Range（类型 2） | 出生时 Y = Z + u·(W − Z)（W ≤ Z 时取 Z），u 为种子随机（3d09990，salt = 类型 + 0x10）；送进材质的值 = **X + Y**，组成 (X+Y, 0, 0, 0)。即 X = 固定偏移（REasy 叫 base），Y = 抽到的随机值（random），Z / W = 抽样下限 / 上限（min / max） | 已确认 | 3d334e0、3d3364c 等（抽样）；3d30350（上传，`mdfPropertyIndex` < 0 的跳过） |
| Float（类型 1） | 四个分量原样送进材质 | 已确认 | 3d30350 |
| `flags` | 非 0 时把结果钳到 [0, 1]：Range 钳抽到的 Y，Float 按分量数逐个钳（REasy 的"0-1"勾选） | 已确认 | 3d334e0、3d335d0 |
| `TypeMeshClip` 的 `valueComponent` | 曲线值写进槽的第 `valueComponent` 个分量（0 X、1 Y、2 Z、3 W），并置"曲线已写"标志。RE4 语料 3 条全部为 0，即曲线驱动 X（base），最终值 = 曲线 + 随机值 | 已确认（写入位置）；语料只有 3 条 | 3e14960 |

RE4 语料的 4141 条 Range 全部 X == Y、基本为 0，取值写在 Z、W（如 (0, 0, 0.25, 0.25)、(0, 0, 1, 3)）。文件里的 Y 只在不抽样的路径上原样参与 X + Y。

#### 4.3.12 GPU 类型

GPU 类型的出生、运动、动画都在计算着色器里完成；CPU 只把各属性的区间与参数作为常量上传（共享 tick 3e1b4e0 依次调用每个属性的 GPU 常量写入函数）。GpuBillboard 的计算着色器见 [RE4-EFX-GPU-BILLBOARD](RE4-EFX-GPU-BILLBOARD.zh-CN.md)（Velocity3D、Attractor、ScaleAnim 与更新顺序已读）；其余 GPU 类型仍按 CPU 公式近似：**折中实现**。

| 类型 | CPU 侧已知 | 状态 | 依据 |
|---|---|---|---|
| GpuBillboard（13） | `ParticleNum` = 池容量，向上取 64 的倍数；`Flags` 位 0 = 尺寸不乘发射器缩放；BlendFlags 低 4 位 → GPU 对象 +0x774 位 10-13 | 已确认 | TD 3e0b550, 3d89137 |
| GpuPolygon（14） | 头部后 3 个标量再接 6 个区间（旧 C# 错开一个 dword） | 已确认 | TA 3db8d63 |
| GpuRibbonFollow（16） | 加载器读 0x44 字节；C# 布局约 0x58 字节，与 exe 不一致 | 待清理 | TB 3d999e0 |
| GpuRibbonLength（25） | fo 0x24 为独立 dword；fo 0x38 浮点仅 flags 位 0 时使用（推定 TextureRepeatNum）；BasingPoint、Direction 对齐 | 部分待确认 | TB 3de32f0 |
| GpuMesh（20） | Flags 位 2-3 同 Mesh 位 8-9；随机抽样、部件动画、OrientDirectionUpVector 都在着色器 | 部分待确认 | TD |

### 4.4 ShaderSettings（渲染参数）

| 字段 | 运行时含义 | 状态 | 依据 |
|---|---|---|---|
| `SoftParticleDistance` | 软粒子过渡深度（米），仅 `Flags` 位 2 时生效（否则 0）→ 着色器 softParticleDist | 已确认 | SS；语料位 2 在 36748/36824 |
| `ParticleLighting` | 0 不受光；1..4 受光，光照分辨率 n = 1/2/4/8（每粒子 n² 图集）；5 当 0 分配；6 当 n=1；低画质选项把 2..4 压到 1 | 门控已确认，取值名部分待确认 | SS |
| `DistanceBias` | 排序距离偏移（米，负值视为更近） | 部分待确认（单位推定） | SS |
| `PriorityBias` | 整数绘制优先级偏移 | 已确认 | SS |
| `DepthBias` | 深度偏移（负值朝向相机）→ 着色器 depthBias | 已确认 | SS |
| `Flags` | 位 2 启用软粒子；位 13 启用 StencilTarget；位 16 每粒子光照缓冲；位 9、10 光照相关渲染标志；位 11 恒置 | 位 2、13 已确认 | SS |
| `ShadowColor` | RGB 乘进受光 / 体积粒子的阴影部分（alpha 忽略） | 部分待确认 | SS |
| `RayleighCoeff`、`MieCoeff`、`MieShadowRatio` | 仅 ScatteringMode ≠ 0 时读取，否则用默认 0.15 / 0.15 / 0.2；RE4 从不开启 | 已确认 | SS |
| `MieDropoff`、`AlphaScale`、`DensityScale`、`DensityMax`、`DensityMin`、`VolumeDepth`、`VolumetricIntensityBlend` | 体积参数；Density* 为百分数（×0.01） | 已确认（去向） | SS |
| `unkn13` | 只读位 0 → 发射器标志 `+0x168` 位 13，含义未知 | 部分待确认 | SS |
| `ScatteringMode` | 0 关、1..6 | 门控已确认 | SS |
| `LightShadowRatio` | 阴影使受光粒子变暗的程度 → cbLightShadowRatio | 已确认 | SS |
| `BackfaceLightRatio` | 背光透射；运行时 = 文件值 × 8 | 已确认 | SS |
| `LightingOffsetX/Y/Z`、`LightingVelocityOffset` | 光照采样点偏移 / 沿速度的偏移 | 已确认（去向） | SS |
| `InShadowDelay`、`TimeToFullShadow`、`OutShadowDelay`、`TimeToNoShadow` | 进出阴影的过渡时间，**毫秒** | 已确认 | SS |
| `toggle_re4` | 按 `== 1` 读成布尔 → 发射器 `+0x168` 位 22，含义未知 | 部分待确认 | SS |
| `DetonemapRatio` | 反色调映射比例：1 = 颜色按作者值到达屏幕，0 = 普通色调映射；不是亮度倍数 | 已确认 | SS → shader detonemapRate |
| `DeinterlaceDistance` | 半分辨率 / 交错渲染距离阈值 | 部分待确认 | SS |
| `AlphaCoverage` | 随机透明覆盖缩放 | 已确认（去向） | SS |
| `SSRAlphaThreshold` | alpha 超过此值写入屏幕空间反射；0 = 不写 | 已确认 | SS |
| `StencilTarget` | 只读低字节，需 Flags 位 13 | 已确认 | SS |
| `unkn24` | ≠ 0 时向实例写一个逐粒子 dword | 部分待确认 | SS |
| `*ShadingRate` ×5 | 可变速率着色，低 3 位，7 / 255 = 默认 | 已确认 | SS |

ShaderSettings 不改变 CPU 模拟；离线预览器只需处理软粒子、深度偏移与 DetonemapRatio。没有雾相关字段。

### 4.5 Blink（alpha 闪烁）

| 字段 | 类型 | 含义 | 状态 | 依据 |
|---|---|---|---|---|
| `MinRate` / `MaxRate` | float | 结果夹紧下限 / 上限 | 已确认 | BL；语料 Min 0.2 占 1112/1796，Max 1 占 1786 |
| `LowFrequency` | CenterSpread | 慢通道频率，**周期 / 秒（实时）** | 已确认 | BL |
| `LowFrequencyWidth` | CenterSpread | 慢通道峰值 | 已确认 | BL |
| `HighFrequency` | CenterSpread | 每个慢周期内的波纹数（不是时间频率）；0 = 关 | 已确认 | BL |
| `HighFrequencyWidth` | CenterSpread | 波纹通道峰值 | 已确认 | BL |

RE4 文件没有 `Flags`（DD2 起才有）。

```
出生（5 次抽取，始终消耗随机数）：fL = rnd(LowFrequency); AL = 2·rnd(LowFrequencyWidth)
                                fH = rnd(HighFrequency); AH = 2·rnd(HighFrequencyWidth); ph = u
每次求值（出生 tick 与每帧，存活时）：
  ph   = fract(ph + fL·dt)                                   // dt 为秒
  low  = (ph <= 0.5) ? ph·AL : AL·(1 − ph)                    // 三角波 0 → W → 0
  high = (fH != 0) ? tri(fract(fH·ph), AH) : 0                // 由慢相位驱动
  p[+0x50] = clamp(low + high, MinRate, MaxRate)
```

- 三角波、非负，不是正弦。fL = 0 时冻结在随机初相（恒定的随机 alpha）；fH 非整数时在慢相位回绕处跳变。
- `p[+0x50]` 乘进最终 alpha（§4.11）。乘积超过 255 时字节截断回绕。
- GPU 类型上传 10 个 float，着色器公式未读。

状态：已确认（BL 3dc7a20）。

### 4.6 FadeByDepth / FadeByAngle / FadeByEmitterAngle

三者 CPU 只上传常量，公式在顶点着色器里逐顶点计算。下列公式读自 `ribbonlengthdraw.vsdf` VS 块 000（GpuParticleVS 家族，下记 RLD）；FadeByAngle 另在 `primitivevfx.vsdf` VS b016（Polygon3DConstant 家族，下记 PV）读到相同逻辑。

**FadeByDepth**（`NearStart`、`NearEnd`、`FarStart`、`FarEnd`，四个独立 float，米）：

```
d = 片段中心的裁剪空间 w（= 视空间深度，不是到相机的直线距离）
k = (d < NearStart || d > FarEnd) ? 0
  : (d < NearEnd)  ? (d − NearStart)/(NearEnd − NearStart)
  : (d > FarStart) ? 1 − (d − FarStart)/(FarEnd − FarStart)
  : 1
```

- Start == End 为硬切；Near 0/0 关闭近端；远端关闭需写大值（FarEnd = 0 会隐藏一切）。属性不存在时默认 (−FLT_MAX, −FLT_MAX, +FLT_MAX, +FLT_MAX)。
- 值原样拷贝，不乘发射器或效果缩放（未见乘法）。
- 状态：GpuParticleVS 家族已确认（RLD b00.ll 1399-1431；FBD 3d3f210）；CPU 类型走 Polygon3DConstant（cbFadeDepth@32，偏移相同），该家族的深度公式未单独读：部分待确认。

**FadeByAngle**（`Flags` 只读位 0..2；`Cone`、`Spread` 弧度；`AlphaRate`；`ConeDir` 发射器局部单位向量）：

```
ConeDir_world = normalize(ConeDir · 发射器 3×3)     // 发射器实例创建时算一次（3d3e6c5），之后不刷新
P   = (Flags & 4) ? 顶点 / 段中心位置 : 发射器位置
v   = normalize(P − cameraPos)
dot = dot(ConeDir_world, v)
c   = (Flags & 1) ? |dot| : −dot                           // 位 0 双锥；c = cos(ConeDir 与 点→相机 的夹角)
cosCone  = cos(Cone);  cosOuter = cos(clamp(Cone + Spread, 0, π))
if   c >= cosCone:  f = (Flags & 2) ? AlphaRate : 1        // 位 1 反转
elif c <  cosOuter: f = (Flags & 2) ? 1 : AlphaRate
else: t = (c − cosOuter)/(cosCone − cosOuter);  f = lerp(AlphaRate, 1, (Flags & 2) ? 1 − t : t)
factor = lerp(1, f, fadeEnableAngle)                       // 属性存在时 enable = 1，否则 factor = 1
```

- 过渡带在 cos(角) 上线性，不是在角度上线性。Spread = 0 为硬边。
- 文件位到着色器位的映射：文件位 0 → effect 位 2（双锥），位 1 → 位 1（反转），位 2 → 位 0（用粒子位置）。kind 1（CPU Billboard3D / Polygon / 条带）直接写进 cbEffect3DFlags，无重排；GpuBillboard 直接写 GpuParticleVS.effectFlags；kind 3（Mesh）与 kind 5（其他 GPU 类型）的紧凑结构保持相对顺序，最终拷贝位置未找到。
- 状态：kind 1 与 GpuBillboard 已确认（SFD：PV b016.ll 800-806、847-897；RLD b00.ll 1394-1494；FBA 3d3ecb0）；kind 3 / 5 部分待确认。

**FadeByEmitterAngle**（`Cone`、`Spread`、`AlphaRate`、`FadeInStart`、`FadeInEnd`）：夹角取（发射器→粒子）与（发射器→相机）之间，同样的余弦空间锥形与 AlphaRate；再加相机距离项 `+(1 − AlphaRate)·(1 − clamp((dist − FadeInEnd)/(FadeInStart − FadeInEnd)))`，结果夹在 [0,1]。部分待确认（只读过指令，未逐项核对）。依据：FBA（RLD b00.ll 1500-1564）。

**组合**：`angleTerm` 取 FadeByAngle 与 FadeByEmitterAngle 中的较大者（`a = enableA·(1+fA)`，`e = enableE·(1+fE)`，`a >= e` 取 fA），不是相乘。顶点 alpha 乘 `dof · k · angleTerm`；乘积 < 2⁻²³ 时顶点输出 NaN/Inf 位置，整段被剔除。已确认（RLD b00.ll 1564-1600）。

### 4.7 AlphaCorrection（逐纹素 alpha 修正）

| 字段 | 含义 | 状态 | 依据 |
|---|---|---|---|
| `Flags` | 只读低 2 位 = 模式：0 Disable、1 AlphaCurve、2 AlphaThreshold、3 LifePower | 已确认 | AC；语料 1：2954、2：801，0 与 3 从未出现 |
| `HiPass`（文件 +8；别名 Max、LifePow0） | 模式 1/2 的上限电平；模式 3 为指数（[0,8)） | 已确认 | AC 3d25b10 |
| `LoPass`（文件 +0xc；别名 Min、LifePow1） | 模式 1/2 的下限电平；模式 3 为指数 | 已确认 | AC |
| `CurveConst` | 模式 0/1/2 的有符号曲线参数，有效范围 −3..+3 | 已确认 | AC 3d25ca0 |

CPU（3d25e90，每帧每粒子）打包 4 字节给着色器：

| 模式 | byte0 | byte1 | byte2 | byte3 |
|---|---|---|---|---|
| 0、1、2 | `trunc(Hi·255.5)` | `trunc(Lo·255.5)` | `trunc(Curve/3·127 + 127.5)` | 模式 |
| 3 | `trunc(Hi·31.875)` | `trunc(Lo·31.875)` | `trunc(生命淡出字节/255·255)` | 3 |

每字节取 `cvttss2si` 的低 8 位，越界回绕。模式 3 还给发射器置 `+0x174 |= 0x1000`，CPU 顶点 alpha 不再乘生命淡出（改由着色器处理）。已确认（AC 3d25ca0、3d16a3c、3d03b0c）。

着色器（`billboarddraw.vsdf` VS b000.ll 1519-1597、PS b006.ll 1005-1080；GPU Billboard 路径），V = 修正前的顶点 alpha（含各种淡出），A = 纹理滤波后的纹素 alpha：

```
模式 0：alpha = A·V
模式 1：s = (byte2 − 127)·0.2141272（≈ 9.065·CurveConst）
        f(V) = |s| 极小 ? V : saturate((1 − exp(−s·V))/(1 − exp(−s)))
        T = 1 − Hi·f(V);  alpha = saturate((A − T)/max(1e-6, Lo − T))           // V 不再单独相乘
模式 2：alpha = smoothstep(Lo, Hi, A·V)
模式 3：x = Hi·8, y = Lo·8, z = 逐粒子值（推定为生命淡出）;  alpha = pow(A·V, lerp(x, y, saturate(z)))·z
最终 alpha < 2⁻²³ 时丢弃像素
```

- 模式 1 是侵蚀：顶点 alpha 下降时切割阈值从 1−Hi 升到 1，纹素 alpha 高于 Lo 的部分保持不透明。官方 Hi 1.0、Lo 0.2-0.25 即"边缘被吃掉"的消散。
- 状态：GPU Billboard 路径已确认；CPU Billboard3D 走 primitivevfx（Polygon3DConstant），公式按相同打包推定为相同：部分待确认。模式 3 的 z 身份部分待确认。

### 4.8 Distortion（屏幕扭曲）

| 字段 | 含义 | 状态 | 依据 |
|---|---|---|---|
| `DistortionType` | 0 Blur、1 Refract、2 BlurTexture（2 位进入着色器） | 已确认 | DIS 4b133e8 |
| `Influence` | 强度，单位随类型：见下 | 已确认 | SFD |
| `SpecularPower` | 高光指数；< 1e-6 关闭 | 已确认 | SFD |
| `SpecularIntensity` | 高光强度，按 2 的幂（2^I） | 已确认 | SFD |
| `SpecularColor` | RGBA8；A 缩放强度 | 已确认 | SFD |

像素着色器只在 `primitivevfx_2.vsdf` b025（软粒子）/ b026 与 `primitivevfx_3.vsdf` b093（Billboard2D）中。GPU 绘制着色器不采样场景颜色，**Gpu\* 宿主上的 Distortion 不可见**（部分待确认）。

```
纹素 (X, Y, B, a) = primTex（cbEffect3DFlags2 位 10 时为亮度-alpha 纹理：X=Y=B=R，a=G）
A = pow(a, 4.84)（除非 flags2 & 0x1800）→ TextureFilter 整形 → × 顶点 alpha（b025 再乘软粒子因子）
switch DistortionType:
  1 Refract：n = (2X−1, 2Y−1); off = Influence·A·n（flags2 位 15 时 X 取反）
             由 SV_Position 与深度重建视空间位置，在视空间 XY 上加 off（米，粒子深度处）后重投影采样场景
  0 Blur：   A' = Influence·A·cbInterleaveScale；水平 9 抽头高斯，间距 A' 像素（权重 0.2042, 0.1900, 0.1216, 0.0578, 0.0204）
  2 BlurTexture：d = normalize(2(X,Y) − 1)；单侧 9 抽头沿 d，步长 Influence·A 像素（负 Influence 反向）
高光：S = 2^SpecularIntensity · (Spec.a/255) · B^(2.2·SpecularPower);  spec = Spec.rgb/255 · S
输出：rgb = 场景采样 · lerp(1, 顶点颜色 + spec, A);  覆盖 = (A < 0.01) ? 0 : 1
Billboard2D：Refract 偏移为半 NDC 单位（UV' = pixelUV + 0.5·Influence·A·(2R−1, −(2G−1))），无深度重建
```

状态：已确认（SFD pv2 b026.ll 517-723，pv3 b093.ll 199-404）；混合状态与是否有第二遍 Blur 未读。

### 4.9 RgbCommon / RgbWater（按贴图通道分层上色）

两者字段与 C# 一一对应，全部开关为 1 字节；`*AppearFrame`、`*KeepFrame`、`*VanishFrame` 为 MinExtraI；`*LifeType` 为 i32 枚举（None 0、AppearOnly 1、SyncKeepHold 2、KeepHold 3、FinishKeep 4，CPU 只区分 1 和 2）。语料中所有寿命、Flags、LifeType 字段均为 0。依据：RW。

RgbCommon `Flags`：位 0 绿色在 GreenChColor / GreenChColorRange 间逐粒子随机；位 1 红色同理；位 2 `RedChAlphaToB` 只作用于红层覆盖，颜色源保持 R。

像素着色器（`primitivevfx.vsdf` PS pv0/b020；cbEffect3DFlags2：0x800 RgbCommon、0x1000 RgbWater，同时置位时 RgbCommon 优先），纹素 (R,G,B,A)，颜色字节 /255：

```
RgbCommon:
  cg = lerp(GreenChColor, Flags&1 ? GreenChColorRange : GreenChColor, ug)      // 逐字节，RGBA
  cr = lerp(RedChColor,   Flags&2 ? RedChColorRange   : RedChColor,   ur)
  M   = pow(max(G·greenLife, 1e-7), GreenChCurve)
  Ig  = cg.a · M;   Igi = Ig · GreenChIntensity
  w   = saturate(Igi / max(GreenChSaturate, 1e-6))²
  greenRGB = Igi · lerp(cg.rgb, 1, w)                                          // 接近 Saturate 时变白
  toB = UseRedChLife ? 1 + (RedChAlphaToB − 1)·redLife : RedChAlphaToB
  v1  = (Flags & 4) ? 0 : toB;   v2 = toB
  redRGB = lerp(R, B, v1) · RedChIntensity · cr.rgb
  redA   = lerp(A, B, v2) · cr.a
  alpha  = saturate((Ig + redA)·Alpha);  alpha <= 1e-4 → 0
  lit    = (GreenChLighting ? greenRGB : 0) + (RedChLighting ? redRGB : 0);  unlit = 其余
RgbWater（全部进 lit）:
  mix   = lerp(G, B, GtoB·gtobLife)
  rgb   = (SpecularColor.rgb·R·SpecularIntensity + SheetColor.rgb·mix·SheetIntensity)/255
  alpha = saturate(A·Alpha·(SheetColor.a·sheetLife·mix + SpecularColor.a·specLife·R)/255)
lit / unlit 按 BlendType：0、2、4 直接相加；1 Physical：lit 乘增益 K（cbEvOffsetPow2 + 37.5·luma(lit)²·…）后加 unlit，再乘顶点颜色；3 EdgeBlend：丢弃 lit
```

- Intensity 只缩放 RGB，不缩放 alpha；关闭一层要用颜色的 A 字节。
- 寿命包络（3dfe120 / 3dfd100，每通道）：Appear `dur = roll`；Appear 结束时 LifeType 1 永久保持；否则 Keep；Keep 结束时 LifeType 2 无限保持（粒子有有限寿命时保持到粒子死亡）；然后 Vanish、Terminate。因子：Appear `1 − clamp(rem/dur, 0, 1)`（首帧为 1/dur），Keep 类 1.0，Vanish `clamp(rem/dur, 0, 1)`，Terminate 0。UseLife 且三段全 0 时出生帧即 Terminate，因子为 0。
- 绿层寿命乘在 Curve 之前的 G 上；红层寿命不淡出红层，而是驱动 AlphaToB（因子 0 时为 1）；水的高光 / 薄层寿命只乘 alpha 项；GtoB 寿命乘 GtoB。
- 常量打包用"小浮点"（半精度丢低位并强制半个 ulp），精确 1.0 解码为 1.0156（11 位）或 1.0313（10 位）。
- 状态：已确认（RW）；36 个 PS 变体中只读了 b020；GPU 粒子的寿命与随机色在计算着色器中，未读。

### 4.10 颜色与 alpha 合成链（CPU 类型）

下表按数据流排列；"未读"表示该环节在着色器中、尚无读码结果。

| 步 | 内容 | 状态 | 依据 |
|---|---|---|---|
| 1 | 出生颜色 RGBA8 写粒子 +0x4c..+0x4f：`Color` 或 `lerp(Color, ColorRange, u)`（头部位 4） | 已确认 | TA, RT |
| 2 | 生命淡出字节 `life`（8 位，§2.4）；AlphaCorrection 模式 3 时 CPU 视为 255 | 已确认 | LF, AC |
| 3 | Blink 系数 `blink` = p[+0x50]（默认 1.0） | 已确认 | BL |
| 4 | 顶点颜色：RGB = 粒子 RGB；`A = trunc(粒子A · life/255 · blink)`（Billboard3D 3d16a62；条带 3d03ad0 同式） | 已确认 | BL, RT |
| 5 | 顶点着色器：A ×= `dof · FadeByDepth k · max(FadeByAngle, FadeByEmitterAngle)`；< 2⁻²³ 剔除 | 已确认（GpuParticleVS）；CPU 家族部分待确认 | FBD, FBA, SFD |
| 6 | 像素着色器：纹素采样（UVSequence 矩形、FrameBlend、UVScroll）；TexelChannelOperator 把纹素换成黑体色（Krystek 色度近似 → sRGB，乘 EmissiveRate·(T·4e-4)⁴，色相偏移、去饱和、加阴影色；常量 cbTexelChannelOperatorParam @512 由 CPU 打包）；TextureFilter 整形 `a' = (t²(3−2t)·a)^x`，`t = saturate((a−z)/(y−z))`，(x,y,z) = cbTextureFilter @432，无该属性时为恒等的 (1,0,0)，字段对应推定为 (TexelAlphaRate, HPThreshold, HPMinValue)；细节见 RE4-EFX-PREVIEW-P3-APPEARANCE | TCO 与 TextureFilter 公式已确认（pv4 b013、pv0 b020）；TextureFilter 字段对应、FrameBlend 采样部分待确认 | SFD, UVS, pv0/pv4 着色器 |
| 7 | AlphaCorrection 按模式重映射 alpha | 已确认（GPU Billboard）；CPU 家族部分待确认 | AC |
| 8 | RgbCommon / RgbWater 替换颜色计算（若存在） | 已确认（pv0/b020） | RW |
| 9 | ColorRate、Intensity、EdgeBlendRange、AlphaRate、ShaderSettings.DetonemapRatio 进入最终颜色的方式 | 未读 | SHE, SS |
| 10 | 混合：BlendType 决定 lit/unlit 处理与管线混合因子；混合因子在管线状态中 | 未读（因子）；分支部分待确认 | RW, SHE |

模拟器在第 9、10 步需要自行选择：**折中实现**为 `rgb = 顶点RGB · 纹素RGB · ColorRate`，`alpha = 顶点A · 纹素A`；BlendType 0 用标准 alpha 混合，1 用加法。

---

## 5. 子 Action

Action 指 EFX 文件级 Action 列表（C# `EfxFile.Actions`）中的一项：一组属性节点（PlayEmitter 等），整体作为"启动子发射器"的容器。按下标取（3cfb400：`下标 < [file+0x8c] ? [file+0x58] + 下标·0x38 : null`）。已确认（PL）。

### 5.1 PtLife（在粒子生命阶段触发）

| 字段 | 类型 | 运行时含义 | 状态 | 依据 |
|---|---|---|---|---|
| `Flags` | u32，只读位 0..4 | 位 0：发射器停止或粒子死亡时释放子实例（写发射器 `+0x16c` 位 0x200000）；位 1：Mesh 粒子时子矩阵再乘该粒子旋转；位 2：每帧跟随位置；位 3：每帧跟随旋转 / 基；位 4：每帧跟随缩放（U·X、U·Y，Z 仅 Mesh / NoDraw / GpuMesh） | 位 1-4 已确认；位 0 的净效果部分待确认 | PL 3d3d6b5, 3d3c31e, 3d3c710 |
| `Status` | i32 | 进入该阶段时触发：0 出生帧、1 Appear、2 Keep（含 KeepHold）、3 Vanish、4 Terminate。**−1 会匹配每个"无变化"调用，即每帧触发** | 已确认 | PL 3d3d197；语料 0：367、2：110、3：93、4：9 |
| `ActionIndex` | i32 | 文件 Action 下标；−1 或越界 = 不触发 | 已确认 | PL |

阶段码（更新循环比较 Life 更新前后的状态）：进入 Appear → 1；进入 Keep 或 KeepHold → 2（Keep → KeepHold 不算）；进入 Vanish → 3；死亡（Life 到期或 kill 位）→ 4；出生帧 → 0。只有状态**变化**才触发，所以 AppearFrame = 0 时 1 永不触发，KeepFrame = 0 且无保持时 2 不触发，VanishFrame = 0 时 3 不触发（改用 4）。出生帧即被 Life 杀死的粒子没有 0。已确认（PL 3d08391..3d083d4）。

触发时子实例只拿到一个 4×4 变换（位置、旋转基、缩放），不继承速度、颜色、UV、寿命。父粒子死亡后子实例继续运行，矩阵冻结在最后一帧的值。已确认（PL）。

### 5.2 PtColliderAction（命中时触发）

命中（位置 H、法线 N）时：子变换平移 = H（+ 块偏移，实际为 0），局部 **+Y = N**，X/Z 由 `(0,1,0) × N` 构造，统一缩放 = 粒子世界矩阵最大轴；`|N.y|` 与 1 相差 1e-6 以内（地面 / 天花板）时改用粒子自身矩阵；镜像标志翻转手性。然后取实例池槽位（§5.3），把每个子发射器标记重启（`|= 2`），把命中碰撞体数据传给子发射器；Flags 位 2 时粒子死亡。已确认（PC 3d62793..3d62bf6）。

### 5.3 共用的 Action 实例池

| 项 | 内容 | 状态 | 依据 |
|---|---|---|---|
| 容量 | 2 × Spawn.MaxParticles（无 Spawn 时 2）；每个实例带 Action 内每个节点的参数块副本 | 已确认 | PL 3d3d45b, PC |
| 分配 | PtLife：粒子已有映射的实例则复用；否则取第一个空闲（`[inst+8]&1 == 0`）；全满时**覆盖最旧**（`[inst+0xc]` 时间戳最小，取自全局帧计数 `[g+0x2378]`）。PtColliderAction 同规则 | 已确认 | PL 3d3d1c2..3d3d210 |
| 启动 | `[inst+8] = 0x23`（活动、待启动、归粒子所有），写时间戳与 `inst+0x10..0x4f` 的矩阵 | 已确认 | PL |
| 执行 | 发射器帧末尾 3d3e0a0：对每个活动实例调用 3d122b0（ctx+0x135 置位时 3d0ee50），按节点标志依次调用 slot 31（标志 0x10，释放）、slot 29（标志 2，启动）、slot 30（标志 8）、slot 32（每帧更新），然后清标志 2、8；所有节点都不再运行时实例回收 | 已确认 | PL |
| 跟随更新 | 3d3df80：PtLife 位 2/3/4 时每帧从粒子重建矩阵（3d3d960 → 3d3c710）；粒子死亡时清 0x20、解除映射，矩阵冻结 | 已确认 | PL |
| 释放 | 所有者停止（`+0x170 & 7 == 4`）时：位 0 置位总是释放；位 0 清零只释放已脱离粒子的实例（子实例可在发射器停止后存活） | 部分待确认 | PL 3d3e1d3 |
| 门控 | `*[ctx+0x100]` 字节为非零时不触发（同一字节也门控 Type 分派） | 部分待确认 | PL |

Action 节点（PlayEmitter 等）如何使用实例矩阵、释放槽对子发射器做什么，未追踪。

---

## 6. Expression

### 6.1 两类实现

| | A 类（固定布局） | B 类（按名字分组） |
|---|---|---|
| 基类 | 3d28ef0 / 3d0e910 / 3d28e20 / 3cfae40 | 3e14ca0（构造）、3e15270（加载）、3e158b0（单槽） |
| 槽数 | 一般 `N = (加载字节数 − 12)/4`，一个位集字；Velocity3D 与 Life 例外（见 6.3） | 各组对象的 slot 0 返回槽数，加载器累加后读 `ceil(N/32)` 个位集字与 N 个赋值类型 |
| 槽名 | exe 中无名字，只有（源属性偏移，目标块偏移） | 每槽带 UTF-16 名字，按 `hash("<Xxx>ExpressionList")` + 组内下标查找 |
| 应用 | 3d0d570（float）/ 3e076e0（角度，度） | 3e158b0；访问器 0 = 颜色 RGB、1 = alpha、2 = float |

文件布局（两类相同）：`UniqueID | 位集 | N × ExpressionAssignType | 表达式块大小 | 表达式块`。应用器为 vtable slot 23（SpawnExpression 为 slot 22），每次发射器更新由 3d086f4 / 3d07bd4 的属性循环调用。已确认（EXP）。

### 6.2 求值规则

| 规则 | 内容 | 状态 | 依据 |
|---|---|---|---|
| 位集 | 位 i（从 0 起）= 槽 i 有表达式；表达式块只含置位的槽，按槽号升序。B 类的槽号 = 各组按构造顺序累加的下标。C# `BitNames` 按槽顺序列出 | 已确认 | EXP 3e152fd |
| 赋值类型 | `Add 0: expr + cur`、`Subtract 1: cur − expr`、`Multiply 2: expr·cur`、`Divide 3: cur/expr`（无零检查）、`Assign 4: expr` | 已确认 | EXP 3e06330 |
| cur | 宿主的文件值（属性对象中的值）；Transform2D/3D 与 VectorFieldParameter 为每帧从 base 复位的 animated 副本。结果每帧重算，不累积 | 已确认 | EXP |
| 求值失败 | 目标写 0（不是文件值） | 已确认 | EXP 3d46c2d |
| 首帧 | `[ctx+8]` 位 1 时先重置表达式状态（3b28400） | 已确认 | EXP |
| 角度槽 | `cur_deg = cur_rad·57.2957763671875`，按度运算，`结果 rad = 结果 deg·π/180`。表达式必须输出度 | 已确认 | EXP 3e076e0 |
| 颜色槽 | 打包 RGBA，逐字节：Add / Subtract 饱和到 0..255，Multiply = c·e/255；Color 槽保留宿主 A，Alpha 槽保留宿主 RGB 并取表达式颜色的 A | 已确认 | EXP 3e06400 |
| 整数槽 | `cvtdq2ps` 进、`cvttss2si` 出；Mesh PartStartNo 夹在 [0, MaxPartsNum−1]，Range 槽结果 +1（保持不含上限） | 已确认 | EXP 3d56bb7 |
| 区间对 | 一个区间占两个槽：值槽 = 第一个成员，`…Range` 槽 = 第二个成员（CenterSpread 的 spread、MinMax 的 max） | 已确认 | EXP |
| 作用时机 | 出生时抽样的字段（尺寸、颜色、旋转、播放速度等）写在块里，只影响之后出生的粒子 | 部分待确认 | EXP |
| 不可驱动 | 标志、布尔、枚举、位域、路径、MaxPartsNum 一类上限；另有部分数值无槽（ScaleAnim 的四个 Coef、Mesh 的 EmissiveColor / EmissiveRate、Velocity3D 的 InheritRate / InheritDistance / Spread / 两个延迟） | 已确认 | EXP |

### 6.3 槽表

槽名与顺序以 C# 各 `*Expression` 类的 `BitNames` 为准（已按 exe 命名，提交 bda7d81）。下表只列结构与特例。

| 类（RE4 类型号） | 类别 | 槽 | 特例 | 依据 |
|---|---|---|---|---|
| Transform3DExpression（13） | A | 9：LocalPosition XYZ、LocalRotation XYZ（度）、LocalScale XYZ | 写 animated，随后重组矩阵；RotationOrder 无槽 | EXP 3d46bc0 |
| Transform2DExpression（8） | A | 5 | LocalRotation 为度 | EXP |
| SpawnExpression（17） | A | 7：SpawnNum(+Range)、IntervalFrame(+Range)、EmitterDelayFrame(+Range)、LoopNumRatio | Range 槽 = max 并夹到 ≥ min；EmitterDelayFrame 两槽的 cur 都取 y（引擎缺陷）且只在首帧运行；LoopNumRatio 仅 UseLoopNumRatioInt 时求值，忽略赋值类型（强制 Assign、cur 0），夹 [0,1] | EXP 3d48df0 |
| Velocity3DExpression（82） | A | 19：Speed、SpeedCoef、GravityRate、DirX/Y/Z（各带 Range）、OffsetXYZ、SizeXYZ、VelocityType | 加载器多 32 字节头，槽数不是 (size−12)/4；VelocityType 为整数（<5）；全局字节 `[..+0xeb1]` 置位时 Dir 槽走另一函数 | EXP 3d4af30 |
| Velocity2DExpression（79） | A | 13 | Angle 为度 | EXP |
| LifeExpression（92） | A | 6：Appear/Keep/Vanish 各 (值, Range)，Range = max | 文件只有 3 个赋值类型，每两个槽共用一个；整数，无 min/max 修正 | EXP 3d4c700 |
| RotateAnimExpression（85） | A | 12：RotationAdd XYZ(+Range，度/帧)、RotationCoef XYZ(+Range) | | EXP |
| ScaleAnimExpression（88） | A | 8：SizeScalarAdd、SizeXAdd、SizeYAdd、SizeZAdd 各 (值, Range) | 四个 Coef 无槽 | EXP 3d11ce0 |
| EmitterShape3DExpression（103） | A | 9：RangeX/Y/Z(inner, outer)、RangeDivideNum、RangeDivideHorizontalNum、RangeDivideVerticalNum | 后三个为整数 | EXP |
| EmitterShape2DExpression（101） | A | 5 | | EXP |
| FadeByDepthExpression（139） | A | 4：NearStart、NearEnd、FarStart、FarEnd | 写块后拷贝到常量缓冲；存在时 FadeByDepth 的静态写入被跳过 | EXP, FBD |
| FadeByAngleExpression（136） | A | 2：Cone、Spread（度） | 每帧重算两个余弦 | EXP 3d4e430 |
| UVSequenceExpression（95） | A | 6：PlaySpeed、SequenceNo、PatternNo 各 (值, Range) | SequenceNo 两槽只在首帧应用 | EXP |
| NoiseExpression（110）、AttractorExpression（194）、VanishArea3DExpression（90）、VectorFieldParameterExpression（177） | A | 8 / 10 / 14 / 20 | Attractor 按模式只应用 Position 或 Offset 组；VanishArea3D 槽序 AreaOffset 在前、AreaAngle 为度；VectorFieldParameter 槽 0/1 与 9/10 写同一目标、Rotation 为度 | EXP |
| TypeMeshExpression（30） | B | 25：Color、ColorRange、Alpha、AlphaRange、ColorRate、PlaySpeed(+Range)、PlaySpeedCoef(+Range)、Rotation XYZ(+Range，度)、SizeScalar、SizeX、SizeY、SizeZ（各 +Range）、PartStartNo(+Range) | 仅 ctx 类型 6 生效；槽序与宿主文件序不同；语料 308 个全部无表达式 | EXP 3d55690 |
| TypeBillboard3D / 2DExpression（24） | B | 13 = BlendType 组 6 + Billboard 组 7 | 两个 Billboard 构造都用 id 24（推定第二个为 2D） | EXP |
| TypePolygonExpression（65）、TypeRibbonLengthExpression（58）、TypeGpuBillboardExpression（244）、TypeGpuRibbonFollowExpression（248） | B | 19 / 19 / 14 / 9 | 组：BlendType(6) + 类型组；条带另有 RibbonBase(5)、ColorPlace(6) | EXP |
| Ribbon Follow / Chain / FixEnd 及 id 62 | B | 17 = BlendType + RibbonBase + ColorPlace | 无类型专属组 | EXP |
| RgbCommon（227）、RgbWater（229）、StrainRibbon（115）、Lightning3D（117）、MeshEmitter（172）、PtTransform3D（196）等 | B | 见 expr-slots-B.json | C# 中 StrainRibbon、Lightning3D、MeshEmitter、RgbWater 的槽数与 exe 不一致；MeshEmitter 槽 11/12 为死槽 | EXP |

BlendType 组的 6 个槽：Color、ColorAlpha、ColorRange、ColorRangeAlpha、`ColorRate_EmissionRate_ContrastRate_InnerIntensity_MultiplyColorRate`、`BackIntensity_OuterIntensity_MultiplyBlackRate`（后两槽按宿主混合方式取义）。

---

## 7. 着色器侧

### 7.1 两个绘制家族

| 家族 | 常量结构 | 包 | 宿主 | 状态 | 依据 |
|---|---|---|---|---|---|
| kind 1 | `Polygon3DConstant`（672 字节；cbEffect3DFlags@68、cbFadeCone@8、cbFadeDepth@32、cbEmitterPos@208、cbDistInfluence@116、cbParamRgbItem3D@624…） | `primitivevfx.vsdf`、`primitivevfx_1..13`、`primitivevfx_2`（Distortion） | CPU 类型 Billboard3D、Polygon、条带家族（类型 vtable slot 30 = 3e0ffa0 创建 0x2a0 字节对象） | 已确认（CPU 写入偏移与结构逐项吻合，无重排） | SFD |
| kind 2 | `Polygon2DConstant` | `primitivevfx_3.vsdf` | Billboard2D（无 FadeByAngle） | 已确认 | SFD |
| kind 3 / kind 5 | CPU 紧凑结构（0x70 / 0xc0 字节），没有同尺寸的 cbuffer，最终拷贝位置未找到 | | Mesh / GpuMesh；其他 GPU 类型 | 部分待确认 | SFD |
| GPU | `GpuParticleVS`（320 字节；fadeCone@8、fadeDepth@32、effectFlags@60、fadeEnableAngle@92、emitterPos@128、alphaCorrection@144、alphaRate@148、usePhysicalAlphaBlend@152、intensityScale@260…） | `vfx/billboarddraw`、`polygondraw`、`ribbondraw`、`ribbonlengthdraw`、`lightningdraw`、`meshvfx` | Gpu\* 类型（GpuBillboard 由 3d54244 → 3d4f9e0 直接填写） | 已确认 | SHE, SFD |

两个家族的 FadeByAngle 代码相同。计算着色器（billboardcompute、ribbonlengthcompute 等）没有任何淡出成员。

### 7.2 已读公式汇总

| 内容 | 文件与位置 | 本文位置 | 状态 |
|---|---|---|---|
| FadeByDepth | ribbonlengthdraw VS 000，b00.ll 1399-1431 | §4.6 | 已确认（GPU 家族） |
| FadeByAngle、位映射 | primitivevfx VS b016.ll 800-897；ribbonlengthdraw b00.ll 1394-1494 | §4.6 | 已确认 |
| FadeByEmitterAngle、组合与剔除 | ribbonlengthdraw b00.ll 1500-1600 | §4.6 | 部分待确认 / 已确认 |
| AlphaCorrection | billboarddraw VS b000.ll 1519-1597，PS b006.ll 1005-1080 | §4.7 | 已确认（GPU Billboard） |
| Distortion | primitivevfx_2 PS b025 / b026（b026.ll 517-723）；primitivevfx_3 PS b093 | §4.8 | 已确认 |
| RgbCommon / RgbWater | primitivevfx PS pv0/b020 | §4.9 | 已确认（一个变体） |
| 条带宽度展开与朝向 | ribbonlengthdraw VS 000（`VSGpuRibbon`） | 下文 | 已确认（GPU 家族） |
| GpuRibbonLength 段构建 | ribbonlengthcompute 块 061（`PostUpdateRibbonLengthCS`） | 下文 | 已确认 |

**条带朝向（VSGpuRibbon，每段 128 字节 `RibbonParticleSegment`）**：

```
n = frontDirType == 0 ? −viewInv.col2                       // 相机前向，所有顶点相同：贴相机平面
  : frontDirType == 1 ? normalize(vertexPos − cameraPos)     // 逐顶点视线
  : 解包法线（每轴 8 位，x/127.5 − 1；切线再对它正交化）       // 固定朝向
w = normalize(n × tangent)
角点（s = size0/1，b = basingPointX）：pos0 − 2s(1−b)·w (U 0)，pos0 + 2s·b·w (U 1)，pos1 同理；V = texCoordV0/1
size ×= (1 + scaleByDepth)；effectFlags 0x80000 时 size 取反（镜像宽度）
```

frontDirType 0、1 时条带绕自身长度轴转向相机，端视时退化成线（与语料中 RibbonLength 37% 搭配 FadeByAngle 一致）。

**GpuRibbonLength 段构建**：条带从 `P − (1−bp.length)·L·d` 到 `P + bp.length·L·d` 分 N 段（与 CPU 公式一致）；`size0 = size1 = 0.5·a·b`（全宽 = a·b，推定 a、b 为 SizeScalar 与 Width，与实机"宽 = SizeScalar × Width"一致）；`basingPointX = BasingPoint.width`；`texCoordV = 1 − (i/N)·k`（k 推定为 TextureRepeatNum）；frontDirType = 粒子标志位 0 或类型 Flags 位 0（语料几乎总为模式 0）；段颜色 = 粒子颜色，无头尾渐变。

### 7.3 未读部分

| 内容 | 影响 |
|---|---|
| primitivevfx 家族的面片四边形构建（Billboard3D 的面向相机、Rotation / Offset / SizeX / SizeY 的使用）| CPU 面片的几何 |
| primitivevfx 家族的条带宽度展开（CPU RibbonLength / Follow / Chain / FixEnd / Trail / Strain） | 推定与 VSGpuRibbon 相同，部分待确认 |
| primitivevfx 家族的 FadeByDepth 与 AlphaCorrection（偏移与打包相同，公式推定相同） | 低 |
| ColorRate、Intensity、EdgeBlendRange、AlphaRate、DetonemapRatio、ContrastHighlighter 在 PS 中的组合；头部位 5 的作用 | 最终亮度与 alpha |
| 混合状态（src/dst 因子，推测在 vfxstates.vsdf） | 叠加方式 |
| Polygon VS（`VSGpuPolygon`）、meshvfx PS | Polygon / Mesh 外观 |
| 全部 GPU 计算着色器（GPU 类型的出生、运动、寿命、ScaleAnim、RotateAnim、Blink、Rgb* 寿命） | GPU 类型的逐粒子行为 |
| CPU RibbonLength 的 HeadColor / ColorPlace / HeadScale / ScalePlace 烘焙 | 条带渐变 |

---

## 8. 未解决问题

按对离线模拟的影响从大到小排列。

| # | 问题 | 影响 | 现有线索 | 依据 |
|---|---|---|---|---|
| 1 | 随机数的剩余缺口（§1.9）：Spawn LoopNum 抽取、PtCollision / Transform3DModifier / Rgb* 等的抽取位置；参考实现未对照实机 | 与游戏逐粒子对齐 | 执行顺序、播种公式、salt 与主要属性的抽取顺序已确认（§1） | ORD |
| 2 | PS 中 ColorRate / Intensity / EdgeBlendRange / AlphaRate 的组合与各 BlendType 的混合因子 | 所有画面的亮度与叠加 | 常量偏移已知（+0x10c、+0xec、+0x108、+0xe4）；Physical 增益式见 §4.9 | SHE, RW |
| 3 | primitivevfx 家族的面片构建与条带宽度展开 | CPU 类型的几何 | GPU 家族公式（§7.2） | SFD, SHE |
| 4 | LegacyMode2018（`[g+0xeb2]`）与 `[g+0xeb0]` 在发行版中的值 | Spawn 首批时刻与周期、ScaleAnim 首帧增量、批次种子；类型分派在积分之前或之后 | 只有 setter 写入 | FRG, SP, SCA, ORD |
| 5 | Life 探针多出的 +1 帧（N 的来源已确认，见 §1.1；是否由 N = 0 / 2 的 tick 造成未验证）；发射器状态机 3d09690 各状态的名字 | 非 60 fps 的阶段边界 | 实机 boom 5/15(+1)/10(+1) | LF, ORD |
| 6 | 粒子参考矩阵 P 的出生初值与 ParentOptions 跟随目标（3d34d40）、发射器帧字节 +0x1e、复位位 `[ctx+8]` 位 1 | 粒子是否随发射器移动 | 公式已知，目标矩阵未追踪 | PO |
| 7 | ParentOptions 欧拉顺序（3ad7f80）；模式 0 平移的原点来源 | 旋转跟随 0/1 混合时的结果 | | PO |
| 8 | GPU 计算着色器 | GPU 类型全部逐粒子行为 | 常量布局已知 | TD, SCA, ROT, BL |
| 9 | RibbonFollow 绳模式的松弛细节；FollowFlags 位 4、5 | 绳模式外观 | 3d79990..3d79c8b | TB |
| 10 | Transform3D 组合函数只写对角线，每帧路径上 ctx+0x90 的复位位置 | 有驱动属性时的发射器矩阵 | | T3D |
| 11 | EmitterContextType 7 的委托淡出计时器公式 | 该类型粒子的寿命结束 | 安装 3d53b40 | LF |
| 12 | Life 模式 1/2 依赖的发射器状态 4、所有者状态 4/5 的含义 | 无限寿命粒子何时进入 Vanish | 推定为停止请求 | LF |
| 13 | ScaleAnimDelayFrame（86）、RotateAnimDelayFrame（83）、Velocity3DDelayFrame（80）、Transform3DModifierDelayFrame（10） | 延迟起始 | 均未追踪 | SCA, ROT, SEM, MOD |
| 14 | EmitterShape3D 的 RotationCorrect 3/4（每帧 slot 23 路径）、等距圆柱的双表与每帧重建（读码所得，语料无实例）；Box 壳判断的级联（读码所得，原生运行尚未区分）；Noise 偏移（粒子 +0x34）进入渲染位置的方式与它的清零位置 | 相机朝向形状的点位；Noise 的显示位置 | 3d21210、3d210d0；§3.1、§3.8 | SEM |
| 15 | Velocity3D ScreenSpace 在 CPU 上的实际行为 | ScreenSpace 速度方向 | 3d1a9c0 | SEM |
| 16 | UVSequence 余量 `frac·(1/60)`；Rotate90 方向 | speed ≠ 1 时的帧时刻 | 3d1eee9 | UVS |
| 17 | RibbonTrail 头部布局：exe 读法与语料、C# 布局冲突（按 exe 读法官方文件 alpha 为 0） | 52 个官方 RibbonTrail 是否可见 | 一次只读实机检查粒子 +0x4c 即可判定 | RT |
| 18 | CPU RibbonLength 的头尾颜色 / 尺寸渐变烘焙 | 条带渐变 | | SHE |
| 19 | PtCollision 命中时清零 ctx+0x268（ParentOptions）逐粒子状态的第一个 dword（即跟随率）的效果；ctx+0x298 属性（每命中碰撞体缩放尺寸 / UV 旋转速度） | 命中后粒子是否脱离发射器 | 3d647e0、3dfa810 | PC |
| 20 | CollisionFilter 两张表的含义（层 vs 掩码）、上下文默认层 / 掩码 | 碰撞对象选择 | 需对照 .cfil | PC |
| 21 | PtLife Flags 位 0 与 Life 写同一发射器位 0x200000 的先后 | 子 Action 的释放 | 两个构建器都写 | PL, LF |
| 22 | Action 节点（PlayEmitter）如何使用实例矩阵；释放槽的作用 | 子发射器的位置与停止 | | PL |
| 23 | FixRandomGenerator 分组键 `[effectInstance+0x148]` 在两次播放间是否变化；E+0x118 / E+0x11c 的用途 | 分组发射器的变体选择 | | FRG |
| 24 | Distortion 混合状态与是否存在第二遍 Blur；cbEffect3DFlags2 位 11-12、15 与 cbInterleaveScale 的来源 | 扭曲强度 | | SFD |
| 25 | AlphaCorrection 模式 3 的 z 身份；模式 0 是否直通 | 未在语料中出现，影响小 | | AC |
| 26 | Billboard3D 位域位 0-2、4-5，Polygon Flags2，Mesh 其余 Flags 位，ShaderSettings unkn13 / toggle_re4 / unkn24 | 少数文件 | | TA, TD, SS |

### 8.1 笔记之间的矛盾

| 主题 | 一方 | 另一方 | 本文采用 |
|---|---|---|---|
| CPU 类型的绘制着色器 | SHE：CPU 类型（Billboard3D、RibbonLength、RibbonFollow、Polygon）经同一套 Gpu VS 绘制（推断） | SFD：kind 1 的 CPU 缓冲就是 primitivevfx 的 Polygon3DConstant，CPU 写入偏移逐项吻合 | SFD（读码证据） |
| FadeByAngle 的 CPU→GPU 位映射 | FBA §6：kind 1 结构与 GpuParticleVS 偏移不同，存在未追踪的重排，位语义为推断 | SFD：与 Polygon3DConstant 对照无重排，位语义已定 | SFD |
| 条带头部的文件起点 | TB：fo 0 = UniqueID，fo 0x38 = ShadowMultiplier，C# 的 FadeSide 在 RE4 不存在 | TC：Type 加载器不以 UniqueID 开头，文件 0 = BlendFlags，0x38 = FadeSide（C# `dd2_unkn0` 须在 RE4 读入）；两种读法下自有字段都从 0x3c 开始 | 未裁定。当前 C#：RibbonLength 与 RibbonFollow 在 RE4 无 FadeSide（与 TB 一致），RibbonChain 与 RibbonFixEnd 声明了 FadeSide |
| Transform3DModifier 是否重组发射器矩阵 | T3D：未见 Transform3DModifier 调用 3d13250 | MOD：3d9b300 以 perFrame=1 调用 3d13250 | MOD（逐指令追踪） |
| RibbonTrail 头部 | exe 读法（RT）：fo 4..0x1f 为标准头，fo 0x20 不读 | 语料数值与当前 C#（`FlagsRe4` 在 fo 8）吻合 | 未裁定，列为 §8 第 17 项 |
| RibbonFixEnd.ReleaseFrame | C# 类型 MinMaxI（[min, max] 两端含） | FE 读码：`min + trunc((max−min)·u) + 1` | FE |
| 3D 路径的出生顺序 | PL 的"3d07d12.. Life → 3d04e90 → 3d1dd00 → PtLife"与 SCA 的"ScaleAnim 出生 3d04fa0" | ORD：两者属于 2D 循环 3d079c0；3D 路径中 ScaleAnim 出生在 3d05350（3d054ba），类型出生抽样也在 3d05350 内 | ORD（§1.3–§1.5） |
| Velocity3D 初速度的设置时机 | SEM：出生循环在形状采样后调用速度初始化 3d1a420 | ORD：3D 出生前置步骤里没有速度；初速度在粒子第一个 tick 由 3d05350 的 3d1ada0 设置，并在同一调用里先积分一步 | ORD |
| Billboard3D 出生函数 | SCA 的表格把 3d17320 记为 Billboard3D | ORD：3d17320 是 Billboard2D 出生（salt 0x0d），Billboard3D 是 3d187e0（salt 0x0f）；抽样公式相同 | ORD |
| CenterSpread 是否在 spread = 0 时消耗随机数 | 早期笔记只列出 Blink、Transform3DModifier 为始终消耗 | ORD：只有 ScaleAnim、Billboard3D 尺寸与旋转、Velocity3D Speed / GravityRate 有门控，RotateAnim、Velocity3D 方向 / SpeedCoef / Inherit、Mesh、RibbonLength、ParentOptions 均无门控 | ORD |
| TypeMesh 随机倒放的字段 | TD：PlayOrder 2 = 50% 倒放 | ORD 表中写作 PlayType 2 时多抽 1 次 | 未裁定，按 TD 写作 PlayOrder |
| Velocity3D 延迟帧的成员命名 | SEM 写作 `s + randint[−r, +r]`；TD 提醒 RangeI 文件顺序为 {r, s}，需复核 | PC 的 DelayFrameCollision 读码确认"第一个 = 中心" | 第一个成员 = 中心（C# CenterSpreadI.center），两者一致 |

