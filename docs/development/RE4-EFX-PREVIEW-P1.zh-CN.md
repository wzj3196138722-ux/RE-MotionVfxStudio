# RE4 离线预览 P1：贴图 Billboard3D

本文保留 P1 的渲染与性能记录。当前入口已增加参数编辑，最新操作见 [P2a 使用说明](RE4-EFX-PREVIEW-P2.zh-CN.md)。

当前已实现单个 CPU Billboard3D entry 的贴图预览。出生、运动、寿命、UV 推进仍由原生辅助进程执行；画面使用自写 OpenGL 着色器，颜色合成是设计中明确的折中。游戏录像并列对照尚未完成，不能据此宣称与游戏画面一致。

## 使用

从仓库根目录运行：

```powershell
dotnet run -c Release --project VfxStudio.Re4Preview -- --fixture bb2
dotnet run -c Release --project VfxStudio.Re4Preview -- --fixture bb
```

`--exe` / `RE4_EXE` 指定锁定版本的 re4.exe，`--python` 指定 64 位 Python。自选 entry 使用 `--efx <文件> --offset <十六进制字节偏移>`。`--resources` 可指定解包根目录、natives 目录或 natives/stm 目录。缺失资源时显示错误，不用默认白图伪装成已加载。

资源按以下顺序查找：打开的文件自己所在的 natives 根目录（从文件往上找名为 natives 的文件夹，最多 16 层；用于发布目录或 mod 里自带的贴图和 .uvs）、`--resources` 指定的目录、游戏目录下的松散文件（`<游戏>/natives/STM`，mod 装在这里，游戏加载时也优先于 pak）、解包目录，找不到时按补丁顺序从游戏目录中的 `re_chunk_000*.pak` 读取；只读目录表和所需文件，不解包整套游戏，也不修改游戏。RE4 使用 `.uvs.7` 和 `.tex.143221013`。两个样本的实际数据为：

| 样本 | 文件内 entry index | 实际 UV 序列帧数 | BlendType |
|---|---:|---:|---:|
| bb | 6 | 1 | 0 |
| bb2 | 11 | 64 | 1 |

这些是文件里存储的 index；样本历史注释里的“root 13”等描述不用于替代实际 index。

窗口支持播放、暂停、步进、回拖、重载、相机操作、粒子检查器、粒子点对照、曝光与辉光。粒子精度可选自动 / 完整 / 一半 / 四分之一；后三者指每轴分辨率。自动模式：少于 500 粒子用完整，500–1999 用一半，2000 以上用四分之一。网格、后期合成、界面仍以窗口分辨率绘制。

## 数据与绘制

- 实际 UV 序列帧数在 entry 加载前传给辅助进程，包括各序列长度不同的情形。辅助进程只需要计数替身；C# 读取真实矩形和贴图数据。
- 协议升级到 v2，附带每帧 TypeBillboard3D 的 0x2f8 字节属性块。BlendType、ColorRate 从当帧块解码，避免只读取文件初始常量。偏移由构建器 3d18580 和上传器 3e0ebe0 核对：常量起点为块 +0x54，混合位在块 +0x98 的位 11–14，ColorRate 在块 +0x160。
- 粒子位置为 +0x28 × 发射器矩阵，不加 +0x34。引擎只在条带采样时、`[p+4] & 0x1000` 置位才加该偏移（3d78c6f、3dfd040）；`[p+0]` 的位 12 属于 UV 序列号。
- 宽、高分别为 U·X·缩放和 U·Y·缩放；滚转采用 slot 27 写入器 3d18a60 实际读取的 +0x9c，镜像时取反，并量化到 half。未把运行时规格另一段中“X+Y”的概括直接套在实例写入路径上。
- 顶点 alpha 使用出生颜色 A × life/255 × blink，按字节截断、溢出回绕；E+0x174 的 0x1000 模式忽略 life。固定 FlipU / FlipV 已接入；随机翻转和 FrameBlend 尚未定位。
- UV 矩形表、序列范围表上传为只读 GPU 缓冲；每帧只更新 64 字节实例。单贴图 entry 一次实例化绘制；多贴图 UVS 按连续贴图段提交，保持粒子槽顺序。
- 实例缓冲三份持久映射，覆写前等待对应 GPU fence；暂停重画也更新 fence，避免仅保护第一次绘制。
- 原始帧包与粒子结构数组使用缓冲池。`PreviewFrame` 拥有帧数据，调用方必须 Dispose；其 `Arrays`、`Particles[*].Raw` 在帧释放后不可继续读取。UI 在接收下一帧后释放旧帧；GPU 已持有独立实例副本。保持一条原生命令在途。
- RGBA16F HDR 中进行 BlendType 0 / 1 折中混合，再执行现有辉光和色调映射。低分辨率粒子通道复制场景深度、积累预乘颜色，然后线性放大合成；它会降低面片边缘与贴图细节精度，不能视为完整分辨率画质。

## 验证

```powershell
dotnet run --project tools/Re4PreviewTests
dotnet run --project tools/Re4PreviewP1Tests
dotnet run -c Release --project VfxStudio.Re4Preview -- --smoke --capture test-output/re4-preview/p1-bb2.png
dotnet run -c Release --project VfxStudio.Re4Preview -- --fixture bb --quality quarter --smoke --capture test-output/re4-preview/p1-bb-quarter.png
```

P0 的五组 120 tick、同进程两次重跑、真实访问违例和突然退出恢复已回归通过。P1 的 bb / bb2 各 180 tick 与独立原生运行对照，检查出生、年龄、尺寸、滚转、UV 号与实际帧数边界，随后逐 tick 比较完整粒子数组摘要。尺寸与滚转另在每个粒子的出生 tick 对照 EFX 文件：全宽落在 SizeScalar×SizeX 区间、全高落在 SizeScalar×SizeY 区间、滚转落在 Rotation 区间，镜像只取反滚转；这一项不依赖解码器读取的偏移。另有字节 alpha 截断、溢出、life 忽略和固定翻转检查。

真实一帧 UVS 的 PlayMode 2 验证发现：第一粒子 tick 7 出生，tick 8 设置待杀 0x200，tick 9 从存活槽移除。测试区分待杀请求与实际移除，不把两者写成同一时刻。

窗口冒烟检查读取 HDR 场景在粒子绘制前后的像素，要求有非零变化且无 OpenGL 错误；同时输出尺寸、混合方式和 ColorRate。截图由实际窗口帧生成。播放、暂停、单步、回到起点、时间轴拖动和重新加载已在真实窗口点击验证；故障按钮尚未手动点击，底层故障恢复已有集成测试。游戏录像外观对照仍待验证。

## 压力测试

```powershell
dotnet run -c Release --project VfxStudio.Re4Preview -- --benchmark --stress 10000 --quality full
dotnet run -c Release --project VfxStudio.Re4Preview -- --benchmark --stress 10000 --quality auto
```

压力参数只修改辅助进程中的 entry 副本：将 Spawn 容量、出生数改为指定数量并清除出生延迟，KeepFrame 改为 1000，保证采样期间完整一万粒子存活。必须在后续属性加载前修改 Spawn，因其余属性会按容量建立表。原始游戏文件保持只读。

测试用 1920×1080 窗口、关闭垂直同步，记录 tick 10–310 的 300 个显示帧间隔；每个新 tick 完成绘制后等待 GPU，包含原生模拟、共享内存、解码、实例上传、RGBA16F 粒子、辉光、UI。它不是仅测 Draw 调用提交时间。原生模拟、读取解码、实例上传另有分项均值。结果在 `test-output/re4-preview/p1-benchmark-full.json` 与 `p1-benchmark-auto.json`。

本机 Release 实测（RTX 4060 Laptop GPU；300 帧期间始终 10000 粒子）：

| 粒子精度 | 最终窗口 | 平均 fps | 帧耗时均值 / p95 / 最大 | 原生模拟均值 |
|---|---|---:|---|---:|
| 完整（1920×1080 粒子） | 1920×1080 | 12.49 | 80.07 / 188.17 / 194.49 ms | 1.66 ms |
| 自动（480×270 粒子） | 1920×1080 | 118.88 | 8.41 / 14.43 / 18.67 ms | 1.64 ms |

自动模式的读取解码均值 2.86 ms，实例上传均值 0.67 ms，Billboard 绘制 1 次。它以降低粒子分辨率达到高于 60 fps 的平均值，p95 也在 16.67 ms 内；最大帧耗时仍超过 16.67 ms，不能宣称每一帧都达到 60 fps。完整分辨率压力目标未达成。

## 剩余边界

目前仍只支持单个 Billboard3D entry、BlendType 0/1、有 UVSPath 且所有贴图可读的场景。文件中混合方式为 2–4 的 entry 在加载阶段被拒绝并显示原因；若运行中的类型块给出未实现的混合方式，该帧不绘制并在面板提示，窗口不退出。尚无空 UVSPath 的直接贴图选择、完整 ParentOptions 世界变换、UVScroll、随机翻转、FrameBlend、FadeBy*、AlphaCorrection、RgbCommon/RgbWater 外观链或编辑联动。面片全宽约定、Offset、合成亮度尚未与游戏录像裁定。GPU 类型不在本阶段。

P0 历史记录见 [RE4-EFX-PREVIEW-P0.zh-CN.md](RE4-EFX-PREVIEW-P0.zh-CN.md)，共享内存协议见 [PROTOCOL.md](../../tools/Re4Native/PROTOCOL.md)。

## 播放控制修复（2026-11-15）

修复了原生帧请求在途时禁用整组按钮、丢弃重置/拖动请求的问题。播放控制仅在初始加载/重载期间禁用；帧计算期间可随时暂停、单步、回到起点或拖动时间轴。暂停冻结当前已显示帧；新目标替换队列中的旧目标，过时响应只释放、不呈现。重新加载等待在途请求结束后执行，并保留已显示的时间轴位置。

`PreviewPlaybackController` 在 UI 线程管理播放状态和请求修订号，后台任务只捕获不可变的 tick 请求。回归测试覆盖在途暂停/重置、快速连续拖动、连续单步、重载与旧结果的先后次序，并通过实际辅助进程检查暂停同一 tick 的摘要和重置到空 tick 0：

```powershell
dotnet run --project tools/Re4PlaybackTests -- --native
```

预览程序还会从自身目录向上查找仓库，因此在仓库内直接双击构建出的 exe 不再依赖启动时的工作目录；仍可用 `--repo` 显式指定。
