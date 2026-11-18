# RE4 GpuBillboard 计算着色器

适用：生化危机 4 重制版（RE4R）TypeGpuBillboard，`natives/stm/systems/shader/vfx/billboardcompute.vsdf.221007880`。CPU 只上传各属性的参数（[RE4-EFX-RUNTIME](RE4-EFX-RUNTIME.zh-CN.md) §4.3.12），出生与逐帧模拟都在本文件的计算着色器里。

## 1. 依据与读法

| 代号 | 来源 |
|---|---|
| BC | `billboardcompute.vsdf` 按 `DXBC` 切块后 `dxc -dumpbin`。下文行号指第一个 `UpdateCS` 块（文件偏移 0x7b637，183 个块中的一个）的反汇编文本 |
| EXE | re4.exe 当前补丁静态反汇编（capstone，`tools/Re4Native/factory.py`） |

- 入口三个：`EmitCS`（120 个变体）、`UpdateCS`（60 个）、`InitializeParticleItemCS`（3 个）。变体按启用的功能组合编译，同一功能在各变体里代码相同。
- 函数全部内联。归属靠 LLVM 别名元数据：作用域节点带函数的修饰名（如 `?UpdateAttractor@@…`），函数体内的指令在 `!noalias` 列表里引用这些节点。
- 各属性参数放在 `ItemBuffer`（t0），偏移由组共享内存 `ItemOffsetTable[22]` 给出；逐粒子状态放在 `GpuParticleBuffer`（u2），偏移由 `ParticleOffsetTable[16]` 给出。

### 1.1 ItemOffsetTable

每个元素打包三个 10 位偏移（位 0–9、10–19、20–29），各对应一个属性；值 1023 表示该属性不存在，偏移 ×4 为字节偏移。EmitCS 与 UpdateCS 的排布不同。状态：已确认（BC）。

| 入口 | 元素[位] → 属性 |
|---|---|
| EmitCS | [0] 0–9 类型（_GpuBillboard）；[1] 20–29 Life；[2] 0–9 Blink、20–29 Velocity3D；[3] 0–9 RotateAnim、10–19 ScaleAnim；[8] ParentOptions；[10] 0–9 MeshEmitter；[11] 10–19 AngularVelocity3D、20–29 ListenForEmit；[12] 0–9 EmitterShape3D；[13] 10–19 PtColorMixer；[14] PointCloudEmitter |
| UpdateCS | [5] 20–29 Attractor；[9] 10–19 Velocity3D；其余元素按读取它的函数：[1] Life，[2] Blink，[3] ScaleAnim / RotateAnim，[4] VectorFieldParameter，[6] VanishArea3D，[7] Noise，[8] ParentOptions / RgbCommon / RgbWater，[11] AngularVelocity3D，[12] GpuPhysics / TexelChannelOperator，[13] PtFreezer / PtColorMixer，[14] PointCloudEmitter（位段未逐个核对） |

## 2. 时间步

`dt = cbDeltaTimeRaw`（cb `Emitter` 偏移 244，秒）。凡是"每帧乘一次"的系数都写成 `x^(dt·60)`，与帧率无关。状态：已确认（BC 1001、10082）。

## 3. 每帧更新顺序（UpdateCS）

Life → ScaleAnim → RotateAnim → Blink → ParentOptions → GpuPhysics → Noise → PtVortexelWind → RgbCommon / RgbWater → PtPathTranslate → AngularVelocity3D → TexelChannelOperator → VectorField → Velocity3D → Attractor → VectorField2 / 速度与碰撞重置 → WindInfluence → UVSequence / UVScroll → VanishArea3D

状态：已确认（BC，按各函数首条指令的行号排序）。

## 4. 运动

### 4.1 Velocity3D

| 项 | 公式 | 状态 | 依据 |
|---|---|---|---|
| 速度单位 | m/s；位移每帧 `v·dt` | 已确认 | BC 11867–11990 |
| 随机量 | `center + spread·(2u − 1)`，u 为 32 位整数 × 2⁻³² | 已确认 | BC 9016、9199 |
| 延迟到期的出生速度 | `v += dir · rnd(Speed)`，Speed 不除以 60；dir 按 VelocityType：0 Direction（`normalize(rnd(DirX,DirY,DirZ))`）、1 Normal、2 沿出生位置、4 锥形 | 部分待确认（分支已读，各类型方向公式未逐项核对） | BC 9003–9254 |
| 阻尼 | `v ← v · SpeedCoef^(dt·60)`；SpeedCoef 出生时抽样，存在粒子上 | 已确认 | BC 11224–11229 |
| 重力 | 阻尼之后：`v.y −= rnd(GravityRate) · 9.80665 · dt'`（另一模式沿发射器 −Y）；`dt'` 扣除重力延迟内的部分 | 已确认（公式）；dt' 的扣除部分待确认 | BC 9285、11230–11256 |

与 CPU 的差异：CPU 速度按 m/帧存（`Speed/60`），GPU 按 m/s 存；文件里的数值含义相同。

### 4.2 Attractor

| 项 | 内容 | 状态 | 依据 |
|---|---|---|---|
| 上传 | 8 个 float 原样上传：`[AttractPosition xyz, ForceScale]`、`[ReversalForceScale × 1.0, ReversalDistance, ForceResist]`，字段名同 C# `EFXAttributeAttractorRE4`（文件顺序：Flags、AttractPosition、ForceScale、ReversalForceScale、ReversalDistance、ForceResist）。DD2 起的布局插入了新字段，见该类注释 | 已确认 | EXE 0x3dc81c0（块构建）、0x3dc8690（GPU 写入，由 0x3e1b4e0 调用） |
| 目标 | 两条分支：经发射器矩阵变换的点、世界坐标点。Flags 2 走世界坐标（re4-preview 原生模拟实测），分支条件与 Flags 的对应未在 BC 中核对 | 部分待确认 | BC 11264–11330 |
| 吸引 | `d = 目标 − 位置`；`k = |d| > ReversalDistance ? 1 : −ReversalForceScale`；`v += normalize(d) · ForceScale · k · dt` | 已确认 | BC 11330–11360 |
| 阻力 | `v −= 0.5 · ForceResist · v_a² · min(dt, 1/60)`（逐分量，方向沿 v_a），`v_a` 为 Attractor 自己累积的速度 | 已确认（公式）；v_a 的复位时机未读 | BC 11360–11440 |

吸引加速度大小恒定、与距离无关，吸向一点必然过冲；ReversalDistance 内反向推出，粒子停在该半径的球面附近，配合 SpeedCoef 收敛。

### 4.3 ParentOptions 与网格跟随

| 项 | 内容 | 状态 | 依据 |
|---|---|---|---|
| 跟随率 | 每粒子：先保持 `ConstInheritRate` `ConstFrame` 帧，再在 `ConstReleaseFrame` 帧内线性过渡到 `ConstInheritReleaseRate`；两个帧数都为 0 时恒为 `ConstInheritRate`；跟随率 < 1e-5 时不跟随 | 已确认 | BC 1846–1912 |
| 跟随方式 | 绝对式：父矩阵 = 出生时矩阵与当前矩阵按跟随率插值（缩放线性、旋转四元数 slerp），粒子位置 = 父矩阵 × 局部位置 | 已确认 | BC 2775–3050 |
| 网格三角形坐标系 | `cbFlags & 0x2000`（= MeshEmitter.Flags 位 8，EXE 0x3d8bfa0 → 0x3d8e0c0）置位时，当前矩阵取出生三角形的坐标系，每帧从蒙皮顶点缓冲 `MeshContourVertices` 重算，粒子随姿态变形 | 已确认 | BC 2423–2490；EXE |
| 与 Attractor 的关系 | Attractor 目标在粒子自己的父矩阵里表达，参照点为出生形状位置；位 8 置位时 `AttractPosition (0,0,0)` 即粒子自己的出生点 | 已确认（公式）；实机效果未确认 | BC 9995–10060、11264–11360 |

### 4.4 ScaleAnim

`size ← max(0, size + Add · Coef^age · (dt·60) · s)`，逐轴；`s = cbMaxScale`（cb `Emitter` 偏移 252），粒子标志位 26 置位时 `s = 1`。与 CPU 闭式解 `base + Add·Σ Coef^k` 等价的增量形式。状态：已确认（公式）；age 的单位部分待确认（BC 1713–1781）。

## 5. 出生（EmitCS）

行号指 EmitCS 块 `b_01fe5d7`（同时内联 InitializeMeshEmitter、EmitBoxShape、InitializeVelocity 的最小变体）。

### 5.1 随机数

| 项 | 内容 | 状态 | 依据 |
|---|---|---|---|
| 生成器 | Wang hash 链，状态 (s, c)：每抽一次 `s = wang(s + c)`，`c += 1`；`wang(k)`：`h = (k^61) ^ (k>>16); h *= 9; h ^= h>>4; h *= 0x27d4eb2d; h ^= h>>15`。与 CPU 的 xorshift32 不同 | 已确认 | BC 226 起 |
| 种子 | `s0 = EmitterInstance.randSeed.x`，`c0 = randSeed.y + 全局线程号`；每次派发线程 0 把 randSeed 改为 `(wang(x+y), y+1)` | 已确认 | BC 211–229、476–480 |
| 延续 | 发射阶段的状态存入粒子结构，InitializeParticleItem 读回继续抽 | 已确认 | BC 3666、3848 |
| 浮点 | `u = s·2⁻³² ∈ [0,1)`；`2u−1 ∈ [−1,1)` | 已确认 | BC |
| 区间 | CenterSpread `c + (2u−1)·s`，spread 为 0 也消耗一次抽取（CPU 在 \|spread\| ≤ 1e-6 时不抽）；颜色 `lerp(C0, C1, u)`，四通道共用 u；整数区间 `trunc(min + 0.5 + u·span)`，两端点各半概率（CPU 为均匀整数） | 已确认 | BC |

### 5.2 出生位置

| 形状 | 采样 | 状态 | 依据 |
|---|---|---|---|
| 类型选择 | `cbFlags & 3`：1 Sphere、2 Cylinder、3 Box；`cbFlags & 16` = UseExtension；`cbRangeX/Y/Z = (inner, outer)`；最后 `pos = v · cbTransform`（3×3） | 已确认 | BC 836–2180 |
| Sphere | 无均分且无 UseExtension：`dir = normalize(立方体内随机点)`（不均匀）；否则 `θ = H.start + H.span·a`，`φ = V.start + V.span·b`，`dir = (cosθcosφ, sinφ, sinθcosφ)`；`p = lerp(inner, outer, u)·dir` 逐轴，半径不开立方根。与 CPU 相同 | 已确认 | BC 874–1116 |
| 均分 | `q = idx / Vn`，`r = idx % Vn`，`hIdx = (r+q) % Hn`；`H.span ≥ 2π` 时 `a = hIdx/Hn`，否则 `hIdx/(Hn−1)`（Hn = 1 取 0.5）；`b = r/(Vn−1)`；idx = `totalSpawnNum + 槽号` | 已确认 | BC 874–910 |
| Cylinder | `x = lerp(X, u)·c.x`，`z = lerp(Z, u)·c.y`，`y = lerp(Yi, Yo, h)`；UseExtension 时 x、z 再乘 `V.start + V.span·h`。等距模式查 item 内的两张表 | 已确认 | BC 1117–1890 |
| Box | 主轴 `k = DivideNum ? DivideAxis : floor(u·3)`；`v ∈ [−1,1]³`；`v[k] *= outer[k]`，`|v[k]| ≤ inner[k]` 时只把下一个轴映射到壳 `(outer−inner)·v + sign(v)·inner`，第三轴通常为 `outer·v`。CPU 文档写两轴都映射到壳 | 已确认 | BC 1891–2162、10217–10415 |
| MeshEmitter 选三角形 | `cbFlags & 16384 == 0`：`tri = floor(u·(索引数/3))`，各三角形等概率，不按面积；否则查 `TriangleAreas[发射槽号]`（−1 时退回等概率），推测为 CPU 预算的面积加权表 | 已确认（等概率分支）；面积表部分待确认 | BC 608–776 |
| MeshEmitter 三角形内 | 两个 u 量化为 16 位 b0、b1；`p = p0 + b0·((p2−p0) + b1·(p1−p2))`，b0 不开方，点偏向 p0 | 已确认 | BC 3189–3520 |
| MeshEmitter 坐标系 | `X = ±normalize(p1−p0)`（符号由 `cbFlags & 32`），`Y = normalize(X × (p2−p0))`（面法线，方向随绕序），`Z = Y × X`。`cbFlags & 8192` 置位时形状在三角形坐标系里施加（局部 Y 对齐法线）；否则保留发射器矩阵，只把网格点加到形状位置上 | 已确认 | BC 3189–3520 |
| PointCloudEmitter | `index = trunc(u·(n−1) + 0.5)` | 已确认 | BC 788–835 |

### 5.3 初速度

item 布局（dword）：DirX/Y/Z 的 (c, s) 在 0–5，Speed 6–7，3×3 矩阵 8–18（推测为形状旋转的逆），GravityRate 24–25，SpeedCoef 26–27，GPU 速度类型在 35 的低 4 位，Offset 36–38，Size 40–42，SpeedDelay 44–45，GravityDelay 46–47。状态：已确认（BC 8579–9205）。

| 步骤 | 内容 | 状态 |
|---|---|---|
| 延迟 | SpeedDelay 非零时 `delay = floor(c + (2u−1)s)`；delay > 0 则出生速度 0、coef 1，到期在 UpdateCS 里补算（§4.1）。CPU 为 `c + randint[−s, s]` | 已确认 |
| 大小 | `speed = Speed.c + (2u−1)·Speed.s`，m/s | 已确认 |
| 方向（GPU 类型 0） | `normalize(Offset)`，不抽随机 | 已确认 |
| 方向（1） | `normalize(出生位置)`（含 cbTransform） | 已确认 |
| 方向（2） | `d = (Size−1)⊙(M·P) + Offset`，`dir = normalize(Mᵀ·d)`，最小长度 1e-5 | 已确认 |
| 方向（4） | 锥轴 = `normalize(DirX.c, DirY.c, DirZ.c)`；若 d1 = d3 = 0 直接用轴，否则极角 `0.5·(d1 + u·d3)`、方位角 `u·2π`，四元数把 (1,0,0) 转到锥轴 | 已确认（公式）；d1、d3 装 Spread 的 min / extra 部分待确认 |
| 方向（3、5 及其他） | `normalize(rnd(DirX), rnd(DirY), rnd(DirZ))` | 已确认 |
| 重力 | `rnd(GravityRate) · 9.80665`，m/s² | 已确认 |
| SpeedCoef | `max(0, rnd(SpeedCoef))`；延迟中为 1 | 已确认 |

CPU 侧 VelocityType 到 GPU 类型的重映射（3d1a9c0，[RUNTIME](RE4-EFX-RUNTIME.zh-CN.md) §3.2）与上表编号的对应：GPU 1 是"沿出生位置"，Normal 退化情形推测映射到 GPU 0。部分待确认。

### 5.4 Life

| 项 | 内容 | 状态 |
|---|---|---|
| item | A、K、V、H 四个 dword，各为 (min u16, span u16)；H 的位 29–31 为保持模式 m | 已确认（BC 4253–4546） |
| 抽取时机 | 出生时只抽第一个非零阶段：A；A < 1 时按 m 进入 KeepHold（m = 1/2 无限）或抽 K；K < 1 时按 m 进入 KeepHold（m = 3/4 抽 H，H 为 {0,0} 时无限）；之后的阶段推测在 UpdateCS 到点才抽。CPU 在出生时一次抽完 A、K、V，H 复用 K 的抽取 | 已确认（出生部分） |
| 打包 | 低 14 位剩余帧数，位 14–27 阶段长度，位 28–31 状态；每阶段上限 16383 | 已确认 |
| 出生 alpha | Appear 中 `1 − saturate(剩余/长度)`，A = 0 时出生即满 alpha | 已确认 |

### 5.5 类型参数、尺寸、颜色

| 项 | 内容 | 状态 |
|---|---|---|
| `_GpuBillboard` item | Color0/Color1 在 dword 0/1；rotation (c, s) 4/5；SizeScalar 8/9；Offset 10/11；SizeX 12/13；SizeY 14/15；dword 2、3、6（float）与 7（int）原样存入，含义未知 | 已确认（BC 3845–4048） |
| 抽取顺序 | 颜色 → SizeScalar → SizeX → SizeY → rotation | 已确认 |
| SizeScalar | `lo + u·(hi − lo)`，`lo = max(0, c−s)`，`hi = c+s`（只夹下界；CPU 不夹） | 已确认 |
| 颜色 | `lerp(Color0, Color1, u)`；PtColorMixer 存在时在调色板中取色再与之插值；最终 `RGB = 白 × 颜色`，`A = 颜色.A × Life 系数 × Blink` | 已确认（BC 4049–4252、10157–10215） |
| ScaleAnim 出生 | 8 个 `c ± s`，抽取顺序按 dword 对 (0,1)(2,3)(4,5)(10,11)(6,7)(14,15)(8,9)(12,13)；各轴的 Add / Coef 落位与 C# 字段顺序不一致，需对照 CPU 上传 3d1d730 | 部分待确认 |
| RotateAnim 出生 | 6 个 `c ± s`；flags 位 0 置位时三个 Add 各以 50% 取反 | 已确认（BC 8378–8565） |

## 6. 未读部分

| 部分 | 影响 |
|---|---|
| RotateAnim、Life 阶段推进、Blink、Noise、VectorField 的公式 | 对应属性的逐粒子行为 |
| Velocity 各 VelocityType 的方向细节、重力延迟 | 出生方向、重力起点 |
| 位置写回与 ParticleUseLocal 的跟随 | 粒子随发射器移动的方式 |
