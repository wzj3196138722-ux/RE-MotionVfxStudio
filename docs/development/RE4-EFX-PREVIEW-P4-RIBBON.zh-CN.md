# RE4 离线预览：条带族（RibbonFollow、RibbonLength）

预览现在绘制 RibbonFollow（语料 1079 个 entry）和 RibbonLength（5799 个）。条带的点不由预览自己模拟：原生模拟器里运行的引擎代码每 tick 采样一次历史点，预览把引擎生成的点池原样导出，再在着色器里展开宽度。外观链（淡出、AlphaCorrection、Rgb 层、混合方式、软粒子、反色调映射）与面片共用。

## 引擎数据（已确认）

逐粒子管线 3d05350 → 3d045d0（按类型分派）→ 3d79cb0（RibbonFollow 采样）每 tick 运行。它维护的数据在类型块里：

| 位置 | 内容 |
|---|---|
| 类型块 +0x38 | 点池：每粒子 `(ShapeDivision − 1) × SplineDivision + ShapeDivision` 个点（ShapeDivision 夹在 [2, 1000]），按槽号连续排列 |
| 类型块 +0x40 | 每粒子状态记录，长 0x68：+0x08 指向该粒子在点池中的起点，+0x10 每粒子点数，+0x18 有效点数，+0x48 已采样次数 |
| 点记录（0x54 字节） | +0x00 世界坐标，+0x18 单位切线，+0x40 V（头 0 → 尾 1），+0x44 逐点颜色 RGBA8，+0x48 逐点尺寸系数 |
| 类型块 +0x50 | Polygon3DConstant（颜色头：混合方式、ColorRate、ColorRange、AlphaRate 等），与 Billboard3D 的 +0x54、Polygon 的 +0x58 同结构 |

依据：`efd_0015_wp5837_explosion_0000` 第 5 个 entry（ShapeDivision 5、SplineDivision 2）。有效点数随 tick 为 4、7、10、13，然后保持 13，即 `(count − 1) × 2 + count`。下标 0 为最新点，是粒子在本 tick 移动之前的世界位置，所以条带比面片位置滞后一帧，这是引擎自身的行为。

状态记录只在第一次更新时填写，加载后的快照里还是 0。所以每粒子点数由预览按文件字段计算，随加载命令的 `ribbon_points` 传给原生侧。

## RibbonLength（已确认）

RibbonLength 不存历史，引擎的布局函数（3d7c2b0，按 BasingPoint、长度和方向 d 计算头尾）每 tick 把结果写进同一种点池：类型块 +0x38 / +0x40，记录格式与 RibbonFollow 相同，每粒子 ShapeDivision 个点（夹在 [2, 1000]，没有样条细分；语料 4915/5799 为 2）。下标 0 为尾（V 1），最后一个为头（V 0）；点是世界坐标，已包含 Transform3D 与锚点偏移。颜色常量同样在类型块 +0x50。

依据：`efd_capcom_spark_loop00_l_0000` 第 1–4 个 entry（静止粒子、竖直条带，长约 2 m）；`efd_0015_ao_chd7_gimmick_mot_0011` 第 8 个 entry（Transform3D 平移约 6 m、LengthFlags 1，点随粒子运动，含 6 m 平移）。预览直接使用这些点，不在 C# 里按 RUNTIME §4.3.4 的公式重算。

## 粒子世界位置（顺带修正）

粒子 +0x28 是发射器局部坐标。世界位置 = 局部位置 × 粒子参考矩阵 P（粒子 +0x100..+0x13f，RUNTIME §2.1），P 包含 Transform3D 与 ParentOptions。帧解码原先乘的是帧级世界矩阵（预览里恒为单位阵），带 Transform3D 平移或旋转的发射器因此画偏了；现在改用 P。记录里 P 无效（第 4 行第 4 列不为 1）时，退回帧级矩阵。

条带头点与粒子世界位置的比对验证了这一点：40 tick、390 条条带，头点都恰好落在粒子上一 tick 或本 tick 的世界位置上。

## 绘制

- 每段条带一个实例，即相邻两个有效点。实例数据包括两端的世界坐标、切线、V、半宽、颜色，以及 UV 序列 / 帧、生命字节。
- 宽度展开（折中，RENDER-HOST §5.4）：展开方向 = 视线方向 × 切线，切线为零时取相机右轴；半宽 = 0.5 × U·X·`+0xe8` × 逐点尺寸系数。全宽约定与 Billboard3D 相同。
- 颜色 = 粒子颜色（含生命淡出与 Blink）× 逐点颜色。贴图横向 0 → 1，纵向取点的 V；UV 序列与帧取自粒子。
- 有效点数小于 2 的粒子不画。

## 协议 v3

帧条目从 128 字节扩到 160 字节，新增 8 个 u32：点池偏移、字节数、点长度（0x54）、每粒子点数、状态记录偏移、字节数、记录长度（0x68）、保留。没有条带数据的条目这些字段为 0。见 `tools/Re4Native/PROTOCOL.md`。

## 验证

- `tools/Re4MultiEntryTests` 第 5b 节：火花文件 4 个 RibbonLength entry 每粒子 2 点，37 条条带都满足有效点 2、尾 V 1 / 头 V 0、坐标有限、长度大于 0。
- `tools/Re4MultiEntryTests` 第 5 节：爆炸文件 entry 5 每粒子 13 点；40 tick 内 390 条条带的布局与有效点数正确，点坐标有限，V 在 [0, 1] 内从头到尾单调；头点与粒子世界位置一致。
- `tools/Re4PreviewTests`：合成帧验证位置按 P 变换；直接比对的位置检查改为从原始记录读 P，再独立计算期望值。
- 外观语料测试新增两个 RibbonFollow 文件（爆炸、火花），渲染结果没有 NaN / Inf。
- 窗口截图：`test-output/re4-ribbon-solo.png`（RibbonFollow，tick 12）、`test-output/re4-ribbonlength-solo.png`（RibbonLength，tick 10）。

## 贴图解码修正（共用的 GpuTexture）

部分 .tex（例如库贴图 `tex_capcom_spark_00_0023_ALPG`，1024×512）没有图像头表，mip 0 紧接在 mip 表之后。解码器把像素数据当作头表，读到“大小 128、偏移 0”，就把这 128 字节当成整张图，按长度推出 16×8，结果完全透明，用它的条带因此看不见。现在未压缩的头表数据必须至少有 mip 0 的大小才采信，否则退回 mip 表给出的 mip 0 偏移。连同上一轮修正的 32 位越界回绕，这一类贴图都按真实尺寸解码；外观测试对两张样本贴图做了尺寸检查。修正只收紧了判断条件，对其他贴图的解码结果没有影响。

## 窗口的故障处理修正

条带 entry 加入后，sm82_320_00 的 FireLine0 在 tick 9 原生除零，暴露了两个问题，均已修正：

- 新文档第一帧（尚未显示时）就出错的 entry，原先只会原样重新加载，同一故障反复出现；冒烟模式则直接退出。现在无论是否第一帧，都先按 entry 排除，再在同一 tick 重新加载。
- MCP 的 `frame` / `screenshot` 带目标 tick 时，途中有 entry 被排除，时间轴会退回上一个已显示的 tick。现在画面稳定后如果不在目标 tick，会再跳一次。

## 已知问题

- sm82_320_00 的 FireLine0（RibbonFollow，带 PtCollision）在 tick 9 原生除零，按运行时故障排除。
- 尚未实现：FollowFlags 位 2|3 的调试色、绳模式与 IntervalFrame 的单独核对（数据来自引擎，理论上已包含）、HeadColor / ColorPlace 渐变（逐点颜色由引擎烘焙，样本里为白色）、FadeByTwist。
- RibbonLength 的 HeadColor / ColorPlace 渐变与 GhostStretch 未单独核对（逐点颜色由引擎写入点池）。
- 其余条带类型（Chain、FixEnd、Trail、Strain、PolygonTrail）仍未绘制。
