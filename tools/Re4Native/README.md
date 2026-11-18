# Re4Native：在本进程里运行 re4.exe 的粒子代码

把 RE4R 的 `re4.exe`（EFX 版本 3539837，文件大小 233,689,576，时间戳 0x69A8F652）原样映射到
0x140000000，直接调用其中的 EFX 函数，与 `docs/development/RE4-EFX-RUNTIME.zh-CN.md`（下称"规格"）逐位比较。
不连接游戏进程，exe 与 RE4_EXTRACT 只读。

## 运行

```bash
python -I -X utf8 -u run_all.py [输出目录] [实验名 ...]     # 默认输出到 %TEMP%\re4native
python -I -X utf8 -u exp_scaleanim.py                      # 单独运行一个实验
python -I -X utf8 -u exp_emitter.py run bb_60               # 一个发射器场景，逐 tick 状态以 JSON 输出
python -I -X utf8 -u pe_image.py "<re4.exe>" 3d1d830 80     # 反汇编一个 RVA
```

依赖 numpy；`scan_loops.py` 与 `pe_image.py` 另需 pefile、capstone。exe 路径可用环境变量 `RE4_EXE`（Wilds 为 `MHWILDS_EXE`）覆盖。

接入新游戏：在 `games/` 加一个版本配置，再写一个像 `mhwildsnative.py` 那样的绑定，放这个游戏的代码在映射后还缺的东西（分配器、单例）。`emitter.py`、`world.py`、`factory.py`、`re4host.py` 里的地址只属于 RE4；Wilds 的发射器驱动在 `mhwilds_emitter.py`，两边对应的函数见 [WILDS-EFX-VS-RE4.zh-CN.md](../../docs/development/WILDS-EFX-VS-RE4.zh-CN.md) §6.4。静态 TLS 与加载器读取流（`install_static_tls`、`ReadStream`）在 `native_core.py`，两个游戏共用。
`run_all.py` 让每个实验在独立子进程中运行：fast fail（0xC0000409）或未捕获的访问违例只结束该子进程，
汇总里记为 `CRASH`。

## 离线预览（P0 / P1 / P2a）

独立窗口、贴图 Billboard3D、真实 UVS、参数编辑、撤销与保存副本已接入。运行方法及当前边界见
[RE4-EFX-PREVIEW-P2.zh-CN.md](../../docs/development/RE4-EFX-PREVIEW-P2.zh-CN.md)，
二进制协议见 [PROTOCOL.md](PROTOCOL.md)。服务入口为 `re4host.py`，原有实验入口保持不变。

## 文件

| 文件 | 内容 |
|---|---|
| `native_core.py` | 与游戏无关的映射与调用框架：按节复制（不重定位、不运行入口点与 TLS 回调）；kernel32 / MSVC 运行库 / UCRT 导入按名绑定，其余导入指向不可访问的陷阱槽，调用时抛 `UnresolvedImportCalled`（带导入名）；向量化异常处理把访问违例变成 `NativeFault`（故障 RVA、访问地址、exe 内回溯）；`patch_jump`、`install_bump_allocator`、`install_global` 替换引擎分配器和单例。只认 `games/` 里登记的 exe 版本，一个进程只能映射一个 exe |
| `games/` | 每个游戏一个版本配置：游戏 id（与 `GameProfile.cs` 相同）、EFX 版本、默认 exe 路径与覆盖用的环境变量、文件大小、PE 时间戳。`re4.py`：`RE4_EXE`；`mhwilds.py`：`MHWILDS_EXE`，默认 `~\Downloads\MonsterHunterWilds.exe` |
| `re4native.py` | RE4 绑定：`load()` 只映射 RE4 的版本；`install_settings` 伪造特效设置单例；`Emitter` 提供 E 与 ctx+0x118。实验和 `re4host.py` 都以 `rn` 导入它 |
| `mhwildsnative.py` | Wilds 绑定：`load()` 映射 Wilds 的版本并把分配器 b020830 / f5b0 换成 bump 分配器；工厂表、步骤表的地址。地址出处见 [WILDS-EFX-VS-RE4.zh-CN.md](../../docs/development/WILDS-EFX-VS-RE4.zh-CN.md) |
| `mhwilds_emitter.py` | Wilds 的发射器驱动，对应 `emitter.py`：工厂表从静态初始化代码读出；`Entry` 用各类型自己的加载器读条目并组成描述对象 X；`Emitter` 由 1dec20 登记步骤、aaba300 构造 E、1c1c0 推进（rcx = X）。单例、字节补丁、桩函数与 RE4 的对应见文件开头 |
| `exp_wilds_emitter.py` | Wilds 语料条目上的发射器场景：步骤列表、槽数、逐帧存活数、执行过的步骤；`sweep <entries.json>` 批量跑 |
| `refsim.py` | 规格 §1.6 的参考随机数（种子、xorshift32、CenterSpread、ScaleAnim 与 Blink 的抽取顺序） |
| `exp_map.py` | 映射、导入绑定、故障与陷阱的捕获 |
| `exp_scaleanim.py` | ScaleAnim 闭式函数 3cfd420 与逐粒子步 3d1d830 + 基值捕获 3d1dd00 |
| `exp_blink.py` | Blink 3dc7a20 |
| `exp_loader.py` | 引擎自己的构造函数 → 加载器（槽 5）→ 块构建器（槽 15），输入为语料 .efx 的原始字节 |
| `exp_rng.py` | 两个播种函数与 ScaleAnim / Blink 出生抽取对照 `refsim.py` |
| `exp_pipeline.py` | 整个逐粒子管线 3d05350，ctx 中只挂 ScaleAnim 与 Blink |
| `scan_loops.py` | 静态扫描 3d08480 与 3d05350 可达函数的导入、全局变量、间接调用、TLS 访问 |
| `factory.py` | 属性工厂表：运行每个"分配 + 构造"小函数，读出 `attr+8` 的类型号与虚表，得到 240 个类型 |
| `emitter.py` | 用引擎代码建发射器并推进：分配器入口补丁、单例与引擎服务桩、静态 TLS 垫片；`Entry` 用各类型自己的加载器读条目（每个属性读多少字节由引擎决定）；`Emitter` 由 3d06440 原地构造 E，经 3d09690 推进；`state()` 输出逐粒子状态 |
| `exp_emitter.py` | 真实语料条目上的发射器场景，与规格比较出生时刻、Life、积分、出生速度、确定性 |
| `world.py` | 平地碰撞查询池（替代 d62f8e0）、PtLife 触发记录器、Action 执行记录器、替身 Action 表 |
| `exp_world.py` | PtCollision / PtColliderAction 碰平地，PtLife 按阶段触发 |
| `exp_expr.py` | Transform3DExpression：表达式虚拟机求值，结果按 BitNames 落到槽位 |

## 结果

| 实验 | 结果 | 状态 |
|---|---|---|
| 映射 | 映射在 0x140000000 成功，代码字节与文件一致；398 个导入绑定，208 个指向陷阱（USER32、WS2_32、WINHTTP、d3d11、dxgi 等）。映射没有障碍：重定位表、TLS 目录、DYNAMIC_BASE 在固定基址下都不需要处理。访问违例与陷阱调用都以 Python 异常返回 | 已确认 |
| ScaleAnim 闭式 3cfd420 | 29,280 组 (t, base, add, coef) 与规格公式（float32、引擎运算顺序、CRT powf）逐位一致，含 Coef 0、1、>1、<0 | 已确认 |
| ScaleAnim 逐粒子步 | 9 种情形 × LegacyMode2018 两种取值共 18 组，每帧四个尺寸槽逐位一致：Coef 1 线性、Coef 0 冻结、夹在 0 以上、30 fps 与 60 fps 同一曲线、槽位步长 0x34 | 已确认 |
| ScaleAnim 首帧增量 | Add 0.1、Coef 0.5：`[g+0xeb2]` = 0 时首帧 +0.05（Add·Coef），= 1 时 +0.1（Add） | 已确认 |
| ScaleAnim Coef < 0 | 60 fps 下 t 为整数，powf 不出 NaN，尺寸正负交替地趋于定值；t 非整数（50 fps）时得 NaN 并写入尺寸槽，夹紧挡不住 | 已确认 |
| ScaleAnim 门控 | `[p+4] & 0x800`、`[ctx+0x130] & 0x10` 时不写；`& 0x20` 时每次重走出生分支，age0 跟随年龄，尺寸不变；r9d = 0 分支照常算 U，X、Y 的 Add 乘 100.0（常量 79c2eec），不算 Z | 已确认 |
| Blink 3dc7a20 | 6 种情形共 840 帧，`p+0x50` 与相位逐位一致：三角波、HighFrequency 由慢相位驱动、fL = 0 冻结、非整数 fH、30 fps；宽度以 2W 存入槽；粒子未存活时不写；无块的新粒子写 1.0 | 已确认 |
| 加载器 + 构建器 | ScaleAnim（类型 87）与 Blink（类型 108）各取一个语料属性：加载器经伪造流对象各读一次（0x44 / 0x2C 字节），块内容与规格给出的顺序一致，块大小与槽 6 的公式一致；用引擎构建的块继续驱动逐粒子函数，出生抽取与逐帧值逐位一致。用游戏自己的代码解析 EFX 属性可行 | 已确认（两个属性类型） |
| 随机数 | 3d09990 与 3d099e0 在 20,000 组随机 (salt, state, seed, key) 上与 `xs(xs(salt+state+key)+seed)` 一致；ScaleAnim 3,000 个粒子（含 |r| ≤ 1e-6 不消耗抽取的字段）与 Blink 3,000 个粒子的出生值逐位一致 | 已确认 |
| 逐粒子管线 3d05350 | 只给 ctx 挂 ScaleAnim、Blink 两个块，另补三个指针（`[g]` 设置单例、类型上下文 `[cs+0x10]→ctx`、`ctx+0x100`），整条管线连续运行 61 次无故障；ScaleAnim、Blink 结果与单独调用一致；手动给粒子速度后位置按 `pos += vel·n` 前进 | 已确认 |
| 条目解析 | 4 个语料条目（Billboard3D 两个、Mesh、RibbonLength）全部由引擎加载器读完，读到的末尾正好是下一个条目的开头 | 已确认 |
| 发射器构造 | 3d06440 在一块内存上原地构造 E：E[0] = 条目对象，属性块依次由槽 15 构建，粒子数组与逐粒子附加数据随后；FixRandomGenerator 的种子进入 E+0x110 | 已确认 |
| 推进 | 3d09690（状态机 → N 累加 → 3d08480）连续运行 120 tick，4 个条目都不故障。Mesh 与 RibbonLength 各 1 个粒子 | 已确认 |
| 出生时刻 | 首批在 tick `max(D,1)`，之后每 `max(I,1)` 帧一批（I = 30：tick 1、31、61、91）；可变 dt 下按整数帧 N 倒数 | 已确认 |
| 低帧率补发 | n = 3、IntervalFrame 0、LoopNum 3：同一 tick 出 3 个粒子，之后不再出生。批数 k = max(1, ⌊n/(I+1)⌋) 同时消耗 k 轮 LoopNum | 已确认（消耗 k 轮为观测结果） |
| E.state | 本批粒子看到 `xs(xs(seed))`（重启置 xs(seed)，出批前推进一次） | 已确认 |
| Life | 抽样（salt 0x29）与 `refsim` 一致；每个粒子每 tick 的阶段码与 8 位 alpha 都符合规格 §2.4.2 模式 0（含 Appear 20..40 的条目、可变 dt） | 已确认 |
| 积分 | 每个粒子每 tick 按 `gvel += n·accel; pos += (vel + (0,1,0)·gvel)·n; vel *= powf(coef, n)` 逐位一致（世界矩阵为单位阵；60 fps 与可变 dt） | 已确认 |
| 出生速度 | Velocity3D 抽样（salt 0x23）得到的方向·速度/60、SpeedCoef、重力加速度与 `refsim` 差 < 2e-7 | 已确认 |
| 出生 tick | 新粒子在出生 tick 不积分：3d06d30 在 `[p] & 2` 时走 3d06da4（Interpolate 分支），标准模式下位置停在形状采样值。重启后第一批的粒子在出生 tick 年龄为 0（之后的批为 1.0） | 已确认 |
| 确定性 | 同一条目在两个进程中逐 tick 结果相同（摘要一致） | 已确认 |
| 无 FixRandomGenerator | 3d88690 在每个 tick 的属性遍历之后令 `E.seed = next(全局 xorshift128 d6a3150..5c)`，不止构造时一次。新映射镜像里该生成器为全 0，种子恒为 0，结果确定；给生成器设初值后每 tick 种子都变，粒子仍各自按出生 tick 的种子抽样 | 已确认 |
| RibbonLength | 尺寸槽 U = SizeScalar、X = Width（0.8）、Y = Length（1.3）；Life Flags 1 + KeepHold {0,0} 进入阶段 6 后保持 | 已确认 |
| UV 帧 | 粒子头 `[p]` 位 10–17 = 序列号，位 18–25 = 帧；PlaySpeed 1 时每 tick 前进 1 帧并循环 | 已确认（.uvs 为合成替身） |
| PtCollision 反弹 | 平地（法线 +Y）上 47 次反弹逐次符合 `v = vel·coef^n + (0, gvel + 2·accel, 0)` 反射后 Y 乘 rate·Vertical、XZ 乘 rate·Horizontal，重力速度清零（误差 < 1e-8） | 已确认 |
| BounceNum | 22 个粒子的命中次数都等于抽样的 BounceNum，最后一次命中执行 FinishType | 已确认 |
| FinishType | Freeze：`[p+4] |= 0x400`，Flags 位 3 时再加 0x800，位置停止；Kill：下一 tick 粒子消失 | 已确认 |
| DelayFrameCollision | 15 帧：状态在年龄 ≥ 15 时才初始化 | 已确认 |
| 一帧延迟 | 每次处理的命中都对应上一 tick 提交的查询（69 次） | 已确认 |
| PtColliderAction | 命中的下一 tick 激活一个 Action 实例，平移 = 命中点，+Y 轴 = 法线 | 已确认 |
| PtLife | Status 0、2、3 三个条目：每个粒子每个阶段码只传一次；状态码等于 Status 时恰好触发一次（life3：25 个粒子中进入 Vanish 的 15 个各一次）；AppearFrame 0 时从不传 1；Flags 位 2 时实例平移随粒子移动 | 已确认 |
| 表达式 | Transform3DExpression 位集 0x18 = 槽 3、4（LocalRotationX、Y），赋值类型 Add；每 tick 动画副本 = (文件值度数 + e)·π/180，其余 7 槽保持文件值（3 组共 3,240 次比较） | 已确认 |
| 表达式外部变量 | 两条表达式都是 `外部值 × 常数`（−0.1、−0.2）。外部值节点（vtable 79f4110）指向表达式自带的值槽（标志 0x400004），默认 0，由游戏的参数系统写入；本目录不写时 e = 0，写 10 / −25 时 e 随之变化 | 已确认 |

## 替身与桩

运行发射器时，引擎外的对象全部是替身。它们决定了哪些结论可信：

| 位置 | 替身 | 影响 |
|---|---|---|
| 分配器 3ab29d0 / 3acc670 | 固定地址上的递增分配；释放为空操作 | 无 |
| 资源查找 39ef530 | 按路径返回清零对象（引用计数 0x10000）；.uvs 合成为 4 个序列 × 16 或 64 帧，无 UV 矩形；.mesh 只有空的数据对象 | UV 帧数与真实 .uvs 不同；Mesh 的部件、动画不可信 |
| 渲染槽 4748b80、网格实例 4659170、网格实例设置 46565c0 / 4658830 / 4656200 | 返回清零对象或直接返回 | 只影响渲染侧 |
| 骨骼查找 3d00f80 | 找不到 | ParentOptions 按无父级处理 |
| 碰撞查询池 d62f8e0 | `world.py`：提交射线 3ce34a0 / 球体 3ce3330 时立即对平面 y = GROUND 求交；轮询走引擎自己的槽表与查询对象 vtable（完成 = 1，结果 +0 命中点、+0x10 法线）。离线预览（re4host.py）总是安装，并关闭查询记录（`world.RECORD`），否则 PtCollision 的第一次查询就因队列容量为 0 除零（3ce336c） | 只有一个水平地面 |
| 碰撞层名查找 43d8570 / 43d8800 | 层号 0、掩码位 0 | 不分层 |
| Action 表（加载器上下文 [0] = 文件对象） | 64 个替身 Action：各含 1 个零数据 FixRandomGenerator 节点，vtable 槽 1 返回节点块大小；Action 执行 3d122b0 / 3d0ee50 与 PtLife 发射器步 3d3e0a0 只记录不运行 | 子 Action 不运行，实例保持激活 |
| 单例 d62f8f0、d62f9f0、d62f9b0、d62fa38 | 清零对象；`[d62fa38]+0x4340 → +0x18` 与 `[ctx+0x100]+8`（LOD 因子 q）设 1.0 | q 为 0 时出生数按 max(·,1) 缩小 |
| 场景系统单例 d62fcc0 | 清零对象：+0x90 指向清零的场景（+0x180d70 / +0x180d78 两个 double 是世界原点偏移，731600 读取），+0x88 指向清零对象（EmitterShape3D 经 3a3f920 查视图，`[+0x18]` 为 0 时返回 0 并跳过） | 世界原点为 0；不跟随相机 |
| 粒子受光图集大小 3d26850 | 返回 0：ShaderSettings 构建函数（3d2812a）不再经 `[d62fa38]+8` 的渲染管理器分配受光缓冲 | 只影响渲染侧 |
| 类型 id 18 的工厂 | 两个工厂都写 id 18，取加载器读 0x10 字节的 3cfa430（EmitterColor）；另一个（3cf9610，读 0x58）会让后续属性错位 | 无 |
| 世界矩阵（更新结构 +0x40..0x7f） | 单位阵 | 无父级运动 |
| 静态 TLS | 新的 ThreadLocalStoragePointer 数组，re4.exe 用槽 100；另初始化线程表的临界区 d78b380 | 表达式加载器经 3ab52b0 → 3b0b350 读取 |

## 卡点

- 抽样扫描（每 40 个官方文件取 1 个，共 99 个文件、779 个可预览 entry、各 120 tick）只剩 1 个 entry 出错：
  `efd_0015_chd3_deadbody_0000` 第 58 个 entry 在 tick 37 于 3d62ce8（PtColliderAction）访问违例，按运行时故障排除。
  整数除零现在也报告为 NativeFault（带指令地址与回溯）。

- `[g+0xeb2]`（LegacyMode2018）与 `[g+0xeb0]` 发行版取值未知：构造函数 3cdf250 清零，启动时 7896afe / 7896b12
  从配置块 `+0x51d` / `+0x530` 复制。本目录按 0 运行。
- Mesh 的模拟依赖网格资源：部件数、动画、材质句柄（attr+0xc8）都来自 .mesh / .mdf2 资源，这里是空替身。
- 子 Action 不运行：语料中的 Action（例如类型 151）内嵌一个 EFX，加载器需要嵌套资源加载器 3cfadc0，没有解析。
- 碰撞对齐（Flags 位 12）、投影模式、外部驱动 3d03fa0 的补帧路径没有经过。
- 表达式外部变量的来源（游戏写入值槽的那一侧）没有找到；名字也没有对上。
