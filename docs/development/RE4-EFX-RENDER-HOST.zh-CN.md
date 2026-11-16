# RE4R EFX 离线预览：渲染承载器设计

适用：生化危机 4 重制版（RE4R），EFX 版本 3539837，re4.exe 当前补丁（文件大小 233,689,576，PE 时间戳 0x69A8F652）。
读者：实现离线预览的开发者，以及要判断某处画面差异来自模拟、数据传递还是绘制的人。
依据：运行时规格 [RE4-EFX-RUNTIME.zh-CN.md](RE4-EFX-RUNTIME.zh-CN.md)（下称"规格"，`§` 指其章节），原生发射器 [tools/Re4Native/README.md](../../tools/Re4Native/README.md)。

本文只写设计。引擎行为的结论沿用规格的四种状态（已确认 / 部分待确认 / 折中实现 / 待清理）；设计取舍写"推荐"，需要拍板的集中在第 10 章。

## 1. 前提

| 前提 | 内容 |
|---|---|
| 模拟 | CPU 侧粒子逻辑由 `tools/Re4Native` 在本机调用 re4.exe 的发射器主循环 3d09690 / 3d08480 完成。出生、Life、积分、碰撞、PtLife 触发、Expression 与规格逐位一致（README「结果」表，已确认） |
| 承载器 | 自研，C#。不使用 Blender：Blender 的 Python 在千级粒子时只有个位数帧率 |
| 版本 | 游戏不再更新，re4.exe 版本锁定。用户自备游戏，exe 与解包资源只读 |
| 游戏进程 | 预览不连接、不注入正在运行的游戏 |

## 2. 架构与进程划分

### 2.1 进程

```
 +-------------------------------+        控制管道（stdin/stdout，JSON 行）       +------------------------------+
 | 承载器 VfxStudio.Re4Preview   | ---------------------------------------------> | 原生辅助进程 re4host.py      |
 | 窗口、ImGui、相机、场景、绘制  | <--------------------------------------------- | 映射 re4.exe，运行发射器      |
 | 时间轴与时钟（唯一时钟源）     |                                                 | 每帧整块复制粒子数据          |
 +-------------------------------+        共享内存（命名文件映射，3 个帧槽）        +------------------------------+
        ^        |                  <=============================================
        |        | 邮箱协议（JSON 文件，与游戏内 UI、MCP 相同）
        |        v
 +-------------------------------+
 | LiveEditor 后台（EditorEngine）|  EFX 文档、撤销、字段修改、序列化
 +-------------------------------+
```

| 进程 | 职责 | 不负责 |
|---|---|---|
| 承载器 | 窗口与 UI；相机；角色、武器等场景网格；时间轴与时钟；把粒子数据解码为 GPU 实例并绘制；贴图、.uvs、网格资源的 GPU 侧 | 粒子模拟 |
| 原生辅助进程 | 映射 re4.exe；用引擎加载器解析 entry；构造发射器 E；按承载器给出的 dt 序列推进；把粒子数组、逐粒子附加数据、属性块整块写入共享内存 | 解码字段含义；任何相机相关计算 |
| LiveEditor 后台 | EFX 文档的唯一权威：字段修改、撤销、序列化成字节；MCP 与游戏内 UI 已经通过它工作 | 预览 |

辅助进程独立的理由：re4.exe 的 fast fail（0xC0000409）无法在进程内捕获，访问违例之后映射镜像的状态也不再可信。崩溃只结束辅助进程，承载器保留窗口与时间轴并重启它（§2.6）。

相机不进入辅助进程。规格中 CPU 侧只有 Velocity3D 的 ScreenSpace 可能依赖相机（§8 第 15 项，部分待确认）；FadeByAngle、FadeByEmitterAngle、FadeByDepth 都在顶点着色器里用相机计算（§4.6，已确认），由承载器完成。

### 2.2 控制管道

承载器以子进程方式启动辅助进程，命令行传入共享内存名、re4.exe 路径和解包资源根目录；stdin 发命令，stdout 回应答，一行一个 JSON。stderr 作为日志。辅助进程退出即视为崩溃。

| 命令 | 参数 | 辅助进程动作 | 应答 |
|---|---|---|---|
| `load` | EFX 文件路径（LiveEditor 序列化出的临时文件）、entry 下标列表、资源表 | 用引擎加载器读 entry，为每个 entry 构造 E；记录"加载后状态"（§2.5） | 每个 entry 的 MaxParticles、类型号、逐粒子附加数据大小、每帧所需共享内存字节数 |
| `track` | 输入区中 dt 轨道与发射器矩阵轨道的长度 | 读取输入区（§2.3） | 确认 |
| `frame` | 目标 tick、写入的帧槽号 | 从当前游标推进到目标 tick；目标在游标之前时先恢复加载后状态再从 0 重跑 | 实际 tick、耗时、各 entry 的存活粒子数 |
| `stop_emitter` | entry、tick | 在该 tick 置发射器停止请求（Life 模式 1/2 依赖它，§8 第 12 项） | 确认 |
| `set_globals` | `[g+0xeb0]`、`[g+0xeb2]` | 修改设置单例（规格 §8 第 4 项，发行版取值未知） | 确认 |
| `shutdown` | | 退出 | |

应答带错误时（`NativeFault`、`UnresolvedImportCalled`）给出 entry、故障 RVA 和 exe 内回溯。

### 2.3 共享内存布局

承载器创建命名文件映射（`Local\Re4EfxPreview_<承载器 pid>`），`load` 应答给出所需大小后按需重建。整段为小端序。

```
文件头（256 字节）
  magic 'R4FX', 布局版本, exe 时间戳 0x69A8F652, 帧槽数 3, 帧槽字节数, 输入区偏移与字节数
输入区
  dt 轨道：float32[T]（秒）
  发射器矩阵轨道：float32[T][16]，行向量约定，平移在第 4 行（规格 §0.2）
帧槽 ×3，每个：
  帧头：tick, 模拟时间（double）, dt, n, N, entry 数
  entry 表（每 entry 一项）：
    entry 下标, ctx 类型号（ctx[0]）, E+0x168 / +0x16c / +0x170 / +0x174 四个标志字,
    MaxParticles, 粒子步长 0x140, 附加数据步长（E+0x148）, 本 tick 发射器世界矩阵,
    各数据段的偏移与长度（粒子数组、附加数据、属性块副本、条带点缓冲）
  数据区：
    粒子数组 E+0x190 的原样副本（含空槽）
    逐粒子附加数据 E+0x198 的原样副本
    属性块副本：Type、ShaderSettings、FadeByDepth、FadeByAngle、FadeByEmitterAngle、AlphaCorrection、
               UVSequence、RgbCommon、RgbWater、Distortion（存在的才复制）
    条带点缓冲：按类型从块或附加数据中的指针取出（§4.5）
```

推荐整块原样复制，字段解码放在承载器。理由：

- 辅助进程每帧每个 entry 只做几次 `memmove`，不在 Python 里逐粒子读字段。README 中 `state()` 逐字段读取的写法只适合试验。
- 字段布局的知识集中在 C# 一处，新识别出一个偏移只改承载器。
- 原样记录同时供承载器的"粒子检查器"显示任意偏移，排查差异时不必改辅助进程。
- 属性块复制而不是从承载器自己解析的 EFX 取常量：Expression 每帧写块（规格 §6.2，例如 FadeByDepthExpression 写块后才拷到常量缓冲），块副本是引擎当帧真正使用的值。贴图、.uvs 路径这类不可驱动的字段仍由承载器从文档读取。

### 2.4 帧槽的所有权

承载器驱动、辅助进程应答，不需要无锁同步：

1. 承载器选一个空闲帧槽，发 `frame`。
2. 辅助进程推进、写满该槽后才发应答。
3. 承载器收到应答后该槽归它所有，解码、上传，用完标回空闲。

三个槽分别处于"正在写""待解码""正在绘制"。承载器在绘制第 k 帧时已经请求第 k+1 帧，模拟与绘制重叠，代价是画面比时间轴晚一帧。

### 2.5 时间控制

承载器是唯一时钟源。时间轴的单位是 tick；每个 tick 的 dt 来自 dt 轨道。

| 时钟模式 | dt 轨道 | 用途 |
|---|---|---|
| 固定 60 Hz（推荐默认） | 全部 1/60 | 与规格的帧单位对齐，n = N = 1（规格 §1.1 指出浮点误差可能让个别 tick 出现 N = 0 再 N = 2，已确认） |
| 指定帧率 | 全部 1/f | 观察 30 fps 等帧率下阶段计数一次走多帧的效果 |
| 自定义序列 | 用户给出或从游戏录制的 dt 序列 | 复现游戏中的追帧与不均匀帧间隔 |

辅助进程直接调用 3d09690，不经过外层驱动 3d03fa0 的追帧子步（README「卡点」）。"指定帧率""自定义序列"等同于游戏每次只走一个子步的情形。

| 操作 | 实现 |
|---|---|
| 播放 | 承载器按墙钟累积，每累计一个 tick 的 dt 请求下一 tick；显示刷新率高于 60 Hz 时重复绘制同一帧，不插值 |
| 暂停 | 停止请求 |
| 单帧步进 | 请求当前 tick + 1；后退一步按拖动处理 |
| 拖动时间轴 | 请求目标 tick；目标在游标之前时辅助进程恢复加载后状态并从 tick 0 重跑 |
| 改字段 | 重新 `load`，重跑到当前 tick，时间轴位置不变（§8.3） |

重跑的确定性依赖"加载后状态"完整：

- 推荐：`load` 完成后保存 re4.exe 可写节（.data、.bss）与分配器竞技场已用部分的副本，每次重跑前整体恢复。全局状态至少包括全局 xorshift128（d6a3150..5c，无 FixRandomGenerator 时每 tick 给 E.seed 取新值，README「结果」已确认）、全局帧计数 `[g+0x2378]`（Action 实例池时间戳，规格 §5.3）和静态 TLS。逐项列举容易漏，整节恢复不必知道引擎写了哪些全局。可写节的实际大小未测，恢复耗时需要实测。
- 验收：同一 dt 序列重跑两次，每 tick 粒子数组的摘要相同。README 已在两个进程之间验证过确定性（已确认），同一进程内多次重跑尚未验证。

重跑耗时估算：5 秒的特效为 300 tick。粒子循环是原生代码，Python 每 tick 每 entry 只有一次调用，主要成本是引擎本身。拖动卡顿时再加检查点（在若干 tick 保存竞技场与可写节），先不做。

### 2.6 崩溃与恢复

| 事件 | 承载器动作 |
|---|---|
| 应答含 `NativeFault` | 显示 entry、RVA、回溯；重启辅助进程（访问违例之后镜像状态不可信）；该 entry 标记为"原生故障"，默认跳过，其余 entry 继续 |
| 辅助进程退出（fast fail 等） | 同上；无法定位到 entry 时按二分法逐步排除 |
| `UnresolvedImportCalled` | 显示导入名。说明该 entry 走到了尚未打桩的引擎服务，记入桩缺口清单 |

辅助进程启动需要读入并映射约 239 MB 的镜像（SizeOfImage 0xe405000），重启耗时需要实测；若超过 1 秒，改为预先启动一个备用进程。

## 3. 承载器选型

### 3.1 宿主程序

现有 `VfxStudio.App` / `VfxStudio.Renderer`：.NET 8，Silk.NET OpenGL 4.6 core，Hexa ImGui / ImGuizmo；已有 `GpuTexture`（.tex，含 BC1..BC7、BC4 单通道转 alpha）、`GpuMesh`、`BoneAnimationEvaluator`、`WeaponAttachmentManager`、`HdrBloomPipeline`（RGBA16F）、`OrbitCamera`、`ViewportGridRenderer`、`ImGuiController`。`VfxProxyRenderer` 按单个粒子逐次 `DrawArrays`，固定加法混合；`EfxSimulator` 是按 MHWilds 代理逻辑写的近似模拟；`MainWindow.cs` 约 4100 行，与预览轨道、动作槽耦合。

| 方案 | 优点 | 缺点 |
|---|---|---|
| A. 在 `VfxStudio.App` 上扩展 | 场景、相机、ImGui 直接可用 | 粒子绘制与模拟两条路径都要替换；`MainWindow` 继续膨胀；`EfxSimulator` 有另一会话持续修改，冲突概率高 |
| B. 新建 `VfxStudio.Re4Preview`（exe）+ `VfxStudio.Re4Render`（库），引用 `VfxStudio.Renderer` | 绘制、数据通道、时钟从零按本文设计；与现有预览互不影响 | `ImGuiController`、`OrbitCamera`、`ViewportGridRenderer`、`StudioTheme` 位于 App 内，需要移到共享库 |

推荐 B（待决 D1）。复用 `VfxStudio.Renderer` 的贴图、网格、骨骼动画、武器挂载与 HDR 管线；App 内的四个 UI 类单独一次提交移入新的共享库 `VfxStudio.Ui`，App 改为引用它。`VfxStudio.Re4Render` 不依赖 `EfxSimulator`。

### 3.2 图形 API

| | OpenGL 4.6 + 自写 GLSL | D3D12 + 游戏 DXIL |
|---|---|---|
| 现状 | 现有渲染全部在 GL 上 | 仓库无 D3D12 代码 |
| 已读公式 | FadeBy*、AlphaCorrection、Rgb*、Distortion、条带朝向可直接写成 GLSL | 直接执行游戏着色器，公式无需重写 |
| 未读部分 | 合成链第 9、10 步（规格 §4.10）先用折中，并做成可调配置（§6.9） | 需要重建根签名、描述符布局、Polygon3DConstant（672 字节）以外的 cbuffer、输入实例流格式（slot 27 写入器，§4.6）、管线混合状态（推测在 vfxstates.vsdf，未解包）。这些未读项正好是合成链的空白，只是换了位置 |
| GPU 类型 | 计算着色器未读，只能近似（§5.10） | 可直接运行 billboardcompute 等计算着色器 |
| 代价 | 小 | 大：需要先逆向 RE Engine 渲染器的资源绑定模型 |

推荐先用 OpenGL 4.6 加自写着色器（待决 D2）。D3D12 留作后期独立的离屏"对照渲染器"：给定同一帧的实例流与常量，用游戏 DXIL 画出参考图，与 GL 画面逐像素比较，用来裁定折中项。它不进入交互窗口，也就不需要 GL / D3D12 互操作。

## 4. 粒子数据：从原生记录到 GPU 实例

### 4.1 粒子记录（0x140 字节）的已知字段

| 偏移 | 内容 | 状态 | 依据 |
|---|---|---|---|
| +0x00 | 标志字：位 0 存活、位 1 新粒子、位 2–9 生命淡出字节、位 10–17 UV 序列号、位 18–25 UV 帧 | 已确认 | Re4Native `emitter.state()`，规格 §1.3 |
| +0x04 | 标志字 2：位 0–2 RotationOrder、位 3–6 生命阶段、0x200 待杀、0x400 冻结、0x800 | 已确认 | 规格 §1.3、§3.6、types-exe-A |
| +0x18 | 粒子编号（E.counter） | 已确认 | 规格 §1.3 |
| +0x1c / +0x28 | 上一 tick 位置 / 当前位置（发射器或粒子参考空间，§4.3） | 已确认（空间见 §4.3） | 规格 §1.3 |
| +0x34 | 位置附加偏移：条带采样在 `[p+4] & 0x1000` 时加到 +0x28（3d78c6f；3dfd040 另需调用方 r8b ≠ 0）。`[p+0]` 位 12 属于 UV 序列号，与此无关 | 已确认（条件）；作用部分待确认 | 规格 §4.3.5、§4.3.8 |
| +0x4c | 出生颜色 RGBA8 | 已确认 | 规格 §4.2 |
| +0x50 | Blink 系数（默认 1.0） | 已确认 | 规格 §4.5 |
| +0x58 | 年龄（float 帧） | 已确认 | 规格 §0.2 |
| +0x5c / +0x74 / +0x78 / +0x7c | 速度 / SpeedCoef / 重力加速度 / 重力速度 | 已确认 | Re4Native |
| +0x80 / +0x84 / +0x88 / +0x8c | 尺寸槽 U、X、Y、Z | 已确认 | 规格 §3.3 |
| +0x9c / +0xa0 / +0xa4 | 旋转 X / Y / Z（弧度；Billboard3D 只用 +0x9c 滚转） | 已确认 | 规格 §4.3.1 |
| +0xb4..+0xc0 | AlphaCorrection 参数，slot 27 写入器 3d25f10 读取并打包 | 部分待确认 | alphacorrection.md |
| +0xe8 / +0xec | 发射器缩放因子（max 轴；ParticleIgnoreScale 时 1.0） | 已确认 | 规格 §4.3.1 |

### 4.2 待定位的字段

以下数据由引擎维护，偏移尚未定位。承载器在定位前按括号内的折中处理：

| 数据 | 用途 | 折中 |
|---|---|---|
| FrameBlend 的前一帧与权重 acc/T | 翻页交叉淡化 | 不混合，只画当前帧 |
| FlipU / FlipV / Reverse 的逐粒子取值 | UV 翻转 | 只处理"总是"（三态值 1），随机态当作关 |
| RgbCommon 的逐粒子随机 ug、ur 与各通道寿命因子（附加数据内） | 规格 §4.9 | ug = ur = 0，寿命因子 1 |
| 类型后处理写入的朝向（Polygon Flags2 0x40 运动朝向、Mesh FrontAxis，3d6f5c0 / 3d31390） | 世界朝向 | 按欧拉角与 RotationOrder 构造，忽略运动朝向 |
| 粒子参考矩阵 P | 位置的世界变换 | 见 §4.3 |

定位方法：在辅助进程里对单个属性做"改一个字段、比对记录前后差异"，与 README 已有实验同一套路。

### 4.3 粒子位置的世界变换

条带采样把 `+0x28（+0x34）` 乘发射器矩阵或 3dfca50 返回的矩阵得到世界坐标（规格 §4.3.5、§4.3.8），粒子参考矩阵 P 的出生初值与 ParentOptions 跟随目标未追踪（规格 §8 第 6 项）。

- 推荐：在辅助进程里对若干组输入直接调用 3dfca50，确认它的输入（粒子、发射器、ParentOptions 状态）与输出，然后在承载器里用 C# 重写同一公式，并保留一个"原生比对"调试开关：每 N 帧对少量粒子调用原生函数核对。逐粒子经 ctypes 调用约每次数微秒，万级粒子每帧几十毫秒，不能作为常规路径。
- 折中实现（定位前）：面片的世界位置 = `+0x28` × 本 tick 发射器世界矩阵，不加 `+0x34`（面片实例写入器是否加这项未追踪）。无 ParentOptions 或 ParentOptions 全跟随时与引擎一致；粒子出生后不跟随发射器的情形在发射器运动时会出错。阶段 1 的发射器矩阵为静止的单位阵，不受影响。

### 4.4 GPU 实例格式

承载器每帧把存活粒子解码为紧凑实例，按 entry 连续存放在一个大缓冲中（std430）。

公共实例（Billboard3D、Polygon，64 字节）：

| 成员 | 类型 | 来源 |
|---|---|---|
| `pos` | vec3 | §4.3 |
| `color` | u32 RGBA8 | RGB = `+0x4c` 的 RGB；A = 顶点 alpha（§6.1 第 4 步） |
| `size` | vec2 | 全宽 = U·X·`+0xe8`，全高 = U·Y·`+0xec`（Polygon 不乘发射器缩放，规格 §4.3.3） |
| `roll` | float | `+0x9c`，镜像（E+0x16c 位 7）时取反 |
| `uv` | u32 | 序列号 \| 帧 << 8 \| 前一帧 << 16 \| 翻转位 << 24 |
| `rot` | vec4 | Polygon：四元数（欧拉角 + RotationOrder）；Billboard3D 不用 |
| `frameBlend` | float | FrameBlend 权重 |
| `alphaCorr` | u32 | AlphaCorrection 4 字节打包（规格 §4.7 的表） |
| `id` | u32 | `+0x18`，供拾取与检查器 |
| `age` | float | `+0x58` |

Mesh 实例（64 字节）：`mat4x3` 世界矩阵（48）+ 颜色 + 部件索引 + AlphaCorrection + 编号。

条带点（48 字节）：世界位置、V（`+0x40`）、切线、颜色、尺寸，取自引擎的 0x54 字节点记录（§4.5）。另有每条带一项的条带表（首点、点数、粒子颜色与 alpha、宽度、BasingPoint.width、朝向模式）。

UV 矩形不放进实例：每个 .uvs 资源上传一张"（序列，帧）到矩形"的表，着色器按 `uv` 查表。

### 4.5 条带点缓冲

引擎的条带点记录为 0x54 字节：+0x00 世界位置、+0x0c 局部位置、+0x18 切线、+0x40 累计长度 / 总长（V）、+0x44 颜色、+0x48 尺寸（types-exe-fixend、types-exe-strain，已确认）。各类型的缓冲位置：

| 类型 | 点缓冲 | 状态 |
|---|---|---|
| RibbonFollow | 每粒子 ShapeDivision × 0x54，另有样条细分预留点；计数在 +0x48 | 已确认（缓冲所在区域：附加数据或块，待定位） |
| RibbonFixEnd | 块 +0x08 指向输出点记录 | 已确认 |
| StrainRibbon | 块 +0x4e0，块 +0x80 = (N+1)·容量 | 已确认 |
| RibbonTrail | 历史指针与点数在绘制时提交给渲染器 vfunc +0x160 | 已确认（指针所在位置待定位） |
| PolygonTrail | 采样 {pos, axis}；顶点由样条路径生成，顶点步长 48、+0x14 颜色 | 部分待确认 |
| RibbonChain | 关节位置与速度 | 部分待确认 |
| RibbonLength | 无历史；头尾由粒子位置、方向 d、L、BasingPoint 在布局函数 3d7c2b0 中算出 | 已确认（公式）；d 的存放偏移待定位 |

辅助进程按类型维护一张"指针偏移表"，把点缓冲复制进帧槽。FixEnd、Strain 的头尾渐变颜色与尺寸已由 CPU 烘进点记录（+0x44、+0x48），承载器直接使用。

### 4.6 引擎实例写入器（slot 27）

每个 Type 与部分属性有 vtable slot 27 的"渲染实例写入器"（Billboard3D 3d18a60、Polygon 3d6f5e0、AlphaCorrection 3d25f10），它们向实例流追加各自的数据包：滚转角（半精度、镜像取反）、四元数（dfae10）、AlphaCorrection 4 字节等（types-exe-A、alphacorrection.md，已确认）。这条流就是游戏绘制时的输入。

如果在辅助进程里运行 slot 27，并给它一个记录数据的渲染器替身，就能直接得到与游戏一致的实例流，§4.2、§4.3 的多数待定位项随之消失，D3D12 对照渲染器也需要这条流。推荐阶段 1 先按 §4.1 解码记录，同时调研 slot 27 的调用约定与渲染器接口，确认可行后把数据来源切换过去（待决 D3）。

### 4.7 资源

| 资源 | 承载器 | 辅助进程 |
|---|---|---|
| .tex | `GpuTexture` 从解包目录或游戏 pak 读取；`_SRGB` 格式用 sRGB 纹理，混合在线性 HDR 中进行 | 不需要 |
| .uvs | ReeLib `UvsFile`（`VfxAssetCache.GetUvs` 已有）生成矩形表 | 资源查找替身必须按真实 .uvs 填写序列数与每序列帧数。当前替身合成 4 × 16 / 64 帧（README「替身与桩」），帧循环位置与 PlayMode 2 的杀死时刻都会错。承载器在 `load` 的资源表中把每个 .uvs 的帧数传过去 |
| .mesh / .mdf2 | `GpuMesh` 与材质贴图 | 替身需要真实部件数（TypeMesh 部件动画按 MaxPartsNum 与部件数循环，规格 §4.3.11）；材质句柄 attr+0xc8 只需非空 |

## 5. 各 Type 的绘制方案

共同组织：每帧所有实例写进一个持久映射的实例缓冲；每个 entry 一次实例化绘制调用（`glDrawArraysInstancedBaseInstance`，用 base instance 指向该 entry 的实例段），同一 entry 的混合方式、贴图、常量相同。条带族用"顶点拉取"：顶点着色器按 `gl_VertexID` 从点缓冲与条带表取数据，每个 entry 一次 `glDrawArrays`。

### 5.1 TypeBillboard3D

- 朝向：primitivevfx 家族的面片构建未读（规格 §7.3）。折中实现：面向相机平面，右轴 = 视图矩阵第 0 列、上轴 = 第 1 列，在该平面内按 `roll` 旋转；四角 = pos ± 0.5·size 沿两轴。`Offset`（块 +0x154 / +0x158）作用方向未读，折中为不偏移。
- 实例：公共实例 64 字节。
- 绘制：每 entry 一次，4 顶点三角带 × 实例数。
- 尺寸用全宽还是半宽未从着色器读出。GPU 条带的全宽 = SizeScalar × Width 与实机一致（规格 §7.2），Billboard3D 折中为同一约定，阶段 1 用游戏录像核对。

### 5.2 TypeBillboard2D

尺寸为像素（规格 §4.3.2）。屏幕空间四边形，后期实现。

### 5.3 TypePolygon

- 朝向：世界朝向四边形，四元数由欧拉角 + RotationOrder 构造（实例写入器调用 dfae10，已确认）。承载器用 C# 重写，四元数约定先在辅助进程里对 dfae10 做一次原生比对；Flags2 0x40 / 0x80 待定位（§4.2）。
- 实例：公共实例，`rot` 有效；尺寸不乘发射器缩放。
- 绘制：同 Billboard3D。

### 5.4 条带族共用的宽度展开

规格 §7.2 的 VSGpuRibbon（GpuParticleVS 家族，已确认）：

```
n = 模式 0: −viewInv.col2（相机前向） | 模式 1: normalize(顶点 − 相机) | 模式 2: 解包法线
w = normalize(n × tangent)
角点 = pos − 2s(1−b)·w（U 0）, pos + 2s·b·w（U 1）;  V 取点记录 +0x40
```

CPU 条带走 primitivevfx 家族，宽度展开未读，折中实现为同一公式（规格 §7.3 推定相同）。每段 6 个顶点（两个三角形），段 i 由点 i、i+1 组成；顶点着色器由 `gl_VertexID / 6` 得段号，查条带表得条带与点。点数 < 2 的条带不画。

| 类型 | 点来源 | 宽度 | 颜色 | 状态 |
|---|---|---|---|---|
| RibbonLength | 承载器按 `tail = P − (1−b)·L·d`、`head = P + b·L·d` 均分 ShapeDivision 个点（L = `+0x88`·`+0x80`） | U·X（实机宽 = SizeScalar × Width） | 粒子颜色；HeadColor / ColorPlace 的 CPU 烘焙未追踪，折中为恒定 | 公式已确认；渐变折中 |
| RibbonFollow | 历史点记录（已含样条细分 3d78de0） | 粒子尺寸 | 点记录颜色；FollowFlags 位 2\|3 时为调试色 | 部分待确认（点缓冲位置） |
| RibbonChain | 关节位置，切线由相邻关节差分 | 粒子尺寸 | 粒子颜色 | 部分待确认 |
| RibbonFixEnd | 输出点记录（块 +0x08） | 点记录 +0x48 × 粒子尺寸 | 点记录 +0x44 | 已确认（数据）；展开折中 |
| RibbonTrail | 历史点记录，世界空间 | X = `+0x84`·`+0x80`·`+0xe8` | 粒子 RGB，A = 生命淡出 × 颜色 alpha；点颜色恒白 | 已确认（数据）；头部布局与 C# 冲突（规格 §8 第 17 项，待清理） |
| StrainRibbon | 块 +0x4e0 的 N+1 个点（直线或悬链线已由 CPU 算好） | `+0x84` | 点记录 +0x44（Flags 0x40\|0x80 时渐变） | 已确认（数据）；展开折中 |

### 5.5 TypePolygonTrail

采样点是条带的一边：顶点 (col, row) = pos + axis·(`+0x80`·row/(rows−1))，rows = NumVerticalDivision + 2（规格 §4.3.9，已确认）。承载器由采样 {pos, axis} 生成网格；样条列由引擎生成时直接取引擎顶点（步长 48、+0x14 颜色）。每 entry 一次 `glDrawArrays`，每个四边形 6 顶点。

### 5.6 TypeMesh

- 朝向与缩放：欧拉角 + RotationOrder，缩放 (U·X, U·Y, U·Z) × 发射器缩放（乘积为推定，规格 §4.3.11）；Flags 位 1 的运动朝向待定位。
- 实例：Mesh 实例 64 字节；部件索引来自粒子（写入位置待定位）。
- 绘制：每个（entry，网格部件）一次 `glDrawElementsInstanced`。meshvfx 像素着色器未读，折中为不受光：贴图 × 粒子颜色 × ColorRate + EmissiveColor × EmissiveRate。

### 5.7 TypeNoDraw

不绘制。检查器中显示尺寸与旋转（子 Action 跟随缩放使用这些值）。

### 5.8 GPU 类型

GPU 类型的出生、运动、寿命都在计算着色器中（规格 §4.3.12），原生 CPU 循环只上传常量，辅助进程拿不到逐粒子数据。阶段 1–5 中显示为占位：发射器位置处的包围框与类型名。后续两条路线：在承载器里按 CPU 公式近似（`EfxSimulator` 的经验可复用，§8.1），或在 D3D12 对照渲染器里运行游戏的计算着色器。GPU 类型在语料中的占比未统计，按占比决定先后。

## 6. 颜色与 alpha 合成

### 6.1 合成链

步骤与规格 §4.10 一一对应：

| 步 | 内容 | 承载器位置 | 状态 |
|---|---|---|---|
| 1 | 出生颜色 RGBA8 | 引擎（`+0x4c`） | 已确认 |
| 2 | 生命淡出字节 life（标志字位 2–9）；E+0x174 位 0x1000（AlphaCorrection 模式 3）时视为 255 | 解码 | 已确认 |
| 3 | Blink 系数 `+0x50` | 解码 | 已确认 |
| 4 | 顶点 alpha `A = trunc(粒子A · life/255 · blink)`，按字节截断（乘积超过 255 时回绕） | 解码，写进实例 `color.a` | 已确认 |
| 5 | 顶点着色器：A ×= FadeByDepth k · max(FadeByAngle, FadeByEmitterAngle)；乘积 < 2⁻²³ 时整段剔除 | VS | GPU 家族已确认；CPU 家族部分待确认 |
| 6 | 纹素采样（UV 矩形、FrameBlend、UVScroll） | PS | 部分待确认 |
| 7 | AlphaCorrection 按模式重映射 alpha | PS | GPU Billboard 已确认；CPU 家族部分待确认 |
| 8 | RgbCommon / RgbWater 替换颜色计算 | PS | 已确认（一个变体） |
| 9 | ColorRate、Intensity、EdgeBlendRange、AlphaRate、DetonemapRatio 的组合 | PS | 未读，折中见 §6.2 |
| 10 | 混合因子 | 管线状态 | 未读，折中见 §6.3 |

第 5、7、8 步按规格公式原样实现。规格中的 `dof` 项来源未写，承载器取 1。

### 6.2 颜色头的别名与折中（第 9 步）

颜色头 7 个 dword（规格 §4.2）中，ColorRate（+0x10c）与 Intensity（+0xec）按混合方式另有引擎名。下表的折中是规格 §4.10 默认式 `rgb = 顶点RGB · 纹素RGB · ColorRate`、`alpha = 顶点A · 纹素A` 的扩展：

| BlendType | ColorRate 别名 | Intensity 别名 | 承载器折中 |
|---|---|---|---|
| 0 AlphaBlend | ColorRate | BackIntensity | 默认式 |
| 1 Physical | EmissionRate | （无专名） | 默认式；RgbCommon 存在时 lit 部分的增益 K 取 1 |
| 2 AddContrast | ContrastRate | （无专名） | 默认式 |
| 3 EdgeBlend | InnerIntensity | OuterIntensity | `t = saturate(纹素A / max(EdgeBlendRange, 1e-4))`；`rgb = lerp(ColorRange·OuterIntensity, 顶点RGB·InnerIntensity, t) · 纹素RGB`；丢弃 lit 部分（规格 §4.9） |
| 4 Multiply | MultiplyColorRate | MultiplyBlackRate | `rgb = lerp(1, 顶点RGB · 纹素RGB · MultiplyColorRate, alpha)`，MultiplyBlackRate 不参与 |

- AlphaRate（+0xe4）：折中为 alpha 再乘 AlphaRate。先统计语料取值；若几乎全为 1，该折中对画面没有影响。
- DetonemapRatio：1 表示作者颜色原样到达屏幕（规格 §4.4，已确认）。承载器的色调映射曲线已知，折中为 `rgb = lerp(rgb, 逆色调映射(rgb), DetonemapRatio)`。
- 头部位 5（常量 +0x110 = 1.0）作用未知，忽略。

### 6.3 混合方式与排序（第 10 步）

| BlendType | 折中混合 |
|---|---|
| 0 AlphaBlend | `SrcAlpha, OneMinusSrcAlpha` |
| 1 Physical | `SrcAlpha, One`（规格 §4.10 的折中） |
| 2 AddContrast | `SrcAlpha, One` |
| 3 EdgeBlend | `SrcAlpha, OneMinusSrcAlpha` |
| 4 Multiply | `DstColor, Zero`（颜色已按 §6.2 预先向 1 插值） |

排序：发射器级按 (PriorityBias, 到相机距离 + DistanceBias) 从远到近；发射器内按槽号顺序，不排序。游戏的排序键与发射器内是否排序都未读（ShaderSettings 的 DistanceBias 单位为推定，规格 §4.4），折中实现；检查器提供"发射器内按深度排序"开关用于对照。粒子写深度测试、不写深度。

### 6.4 ShaderSettings

| 字段 | 承载器 |
|---|---|
| SoftParticleDistance（Flags 位 2） | `alpha ×= saturate((场景视深 − 片段视深) / SoftParticleDistance)`，需要深度纹理。公式为折中，参数含义已确认 |
| DepthBias | 片段视深朝相机偏移 DepthBias 米后再做深度测试，折中 |
| DistanceBias、PriorityBias | 排序键（§6.3） |
| DetonemapRatio | §6.2 |
| ParticleLighting、ShadowColor、体积与散射参数 | 不实现：受光粒子按不受光绘制（折中） |

ShaderSettings 不改变 CPU 模拟（规格 §4.4），只进入绘制常量。

### 6.5 FadeByDepth / FadeByAngle / FadeByEmitterAngle

顶点着色器按规格 §4.6 原样实现，包括：FadeByDepth 用裁剪空间 w；FadeByAngle 的过渡在 cos 空间线性、文件位到着色器位的重排（文件位 0 双锥、位 1 反转、位 2 用粒子位置）；两种角度淡出取较大者而不是相乘。

`ConeDir_world` 在发射器实例创建时算一次、之后不刷新（规格 §4.6，已确认）。辅助进程从 FadeByAngle 块副本中导出这个值，承载器不自行用当前矩阵重算。常量来自块副本，FadeByDepthExpression、FadeByAngleExpression 写入的值由此进入画面。

### 6.6 AlphaCorrection

像素着色器按规格 §4.7 的四个模式实现；实例里的 4 字节按 §4.7 的打包表从 `+0xb4..+0xc0` 生成，字节截断规则相同。模式 3 的 z 取生命淡出（规格中推定）。CPU 家族与 GPU Billboard 公式相同为推定，部分待确认。

### 6.7 RgbCommon / RgbWater

像素着色器按规格 §4.9 实现；cbEffect3DFlags2 两位同时置位时 RgbCommon 优先。常量的"小浮点"打包（精确 1.0 解码为 1.0156 或 1.0313）在承载器端按同样方式量化，保证与游戏相同的偏差。lit / unlit 拆分在无光照的承载器里合并；Physical 增益 K 取 1（折中）。逐粒子随机与寿命因子待定位（§4.2）。

### 6.8 Distortion

需要场景颜色副本与深度纹理。像素着色器按规格 §4.8 实现 Refract（视空间重建后偏移、重投影采样）、Blur（水平 9 抽头）、BlurTexture（沿方向单侧 9 抽头）与高光项。混合状态未读：折中为覆盖值 1 的像素直接写入（`One, Zero`），覆盖值 0 的像素丢弃。Gpu\* 宿主上的 Distortion 在游戏中不可见（部分待确认），承载器同样不画。

### 6.9 合成配置

§6.2、§6.3、§6.4、§6.8 中的折中项集中在一个"合成配置"里（每个 BlendType 的颜色式、混合因子、AlphaRate 是否参与、软粒子曲线等），着色器用 uniform 与少量变体切换，UI 中可实时修改。读出着色器或拿到对照图后，只改配置默认值，不改架构。

### 6.10 UVSequence 与 .uvs

- 当前帧由引擎维护（标志字位 10–25，规格 §4.1 的推进规则已确认），承载器只查表：（序列，帧）到矩形，越界夹到最后一个。
- `UVSPath` 为空时整张贴图。
- FrameBlend：权重与前一帧待定位，定位前不混合（§4.2）。Rotate90 只进入 GPU 实例字（规格 §4.1），CPU 类型不处理。
- 辅助进程的 .uvs 替身按真实帧数填写（§4.7）。

### 6.11 渲染通道

| 序 | 通道 | 目标 |
|---|---|---|
| 1 | 不透明场景（角色、武器、地面、网格） | RGBA16F 颜色 + 深度纹理。`HdrBloomPipeline` 当前用 Depth24Stencil8 渲染缓冲，需改为可采样的深度纹理 |
| 2 | 半透明粒子：按 §6.3 排序逐 entry 绘制 | 同上 |
| 3 | 复制颜色缓冲；绘制 Distortion entry | 同上 |
| 4 | Bloom 与色调映射（现有 `HdrBloomPipeline`） | 屏幕 |

Distortion 在游戏中相对其他半透明物的绘制顺序未读，第 3 步的位置为折中。

## 7. 性能目标

目标：一万个 Billboard3D 粒子（可由多个 entry 组成）在 1080p 下稳定 60 fps，数据通道与绘制都不成为瓶颈。

每帧数据量估算（一万粒子；条带按 1000 条 × 32 点）：

| 环节 | 数据量 | 说明 |
|---|---|---|
| 辅助进程写共享内存 | 粒子数组 10,000 × 0x140 ≈ 3.2 MB，另加附加数据与点缓冲（1000 × 32 × 0x54 ≈ 2.7 MB） | 每 entry 几次 `memmove`；空槽也复制 |
| 承载器解码 | 一次遍历，C# `Span` / unsafe 读字段 | 存活判断、§4.3 变换、§6.1 第 4 步 |
| 上传到 GPU | 实例 10,000 × 64 B = 640 KB；条带点 32,000 × 48 B ≈ 1.5 MB | 合计约 2.2 MB / 帧，60 fps 下约 130 MB/s |
| 绘制调用 | 每 entry 1 次（Mesh 每部件 1 次） | 一个特效通常几十个 entry |

GPU 侧措施：实例与点缓冲使用 `glBufferStorage` 的持久映射（`MAP_PERSISTENT | MAP_COHERENT`），三份轮换，与 §2.4 的帧槽对应，用 fence 保证 GPU 读完再覆写；全部绘制走实例化或顶点拉取。

主要风险不在数据量：

- 填充率：烟雾类面片常为数米大小，满屏叠加的 RGBA16F 混合是 GPU 的主要开销。必要时增加半分辨率粒子通道（游戏有 DeinterlaceDistance 对应机制，规格 §4.4）。
- 原生模拟本身：每 tick 的引擎耗时未测。辅助进程在应答中报告每帧耗时，阶段 1 的压力测试给出实测值。
- 粒子上限：Re4Native 的 `Emitter` 默认给 E 分配 1 MB，一万槽需要 3.2 MB 以上，E 的大小改为按 MaxParticles 与附加数据步长计算。

## 8. 与现有代码的关系

### 8.1 EfxSimulator

`VfxStudio.Renderer/Vfx/EfxSimulator.cs` 自带随机数（SplitMix 风格，与引擎的 xorshift32 不同）与按 MHWilds 代理逻辑写的模拟；会话 `offline-efx-simulator-re` 持续按规格修改它，分支 `claude/efx-sim` 上另有 Python 版 `tools/EfxSim`。

推荐（待决 D6）：

- RE4 的 CPU 类型以原生辅助进程为准，新承载器不依赖 `EfxSimulator`。
- `EfxSimulator` 保留给现有 `VfxStudio.App` 的 MHWilds 预览路径。
- 承载器定义粒子数据源接口，原生辅助进程是一个实现；`EfxSimulator` 可改写为第二个实现，输出同一实例格式，用于两种场合：GPU 类型的近似（§5.8），以及没有 re4.exe 的环境。
- 因此 `offline-efx-simulator-re` 在 RE4 CPU 类型上继续追求逐位一致的收益很低，建议转向 GPU 类型的近似公式。

### 8.2 VfxStudio.App 与 VfxStudio.Renderer

复用 `VfxStudio.Renderer` 的 `GpuTexture`、`GpuMesh`、`Shader`、`BoneAnimationEvaluator`、`WeaponAttachmentManager`、`HdrBloomPipeline`、`VfxAssetCache`（.uvs / .tex 路径解析）。`VfxProxyRenderer` 不复用：它逐粒子提交绘制、固定加法混合，与 §5 的组织方式不兼容。App 内的 UI 类按 §3.1 移入共享库。

### 8.3 编辑流程

LiveEditor 后台（`EditorEngine`）是 EFX 文档的唯一权威，游戏内 UI 与 MCP 通过邮箱协议操作它。离线预览接入方式：

1. LiveEditor 增加"预览输出"：每次修改成功后把当前文档序列化到预览目录，并写一个带递增修订号的清单（文件路径、修订号、改动涉及的 entry）。
2. 承载器监视清单。修订号变化时向辅助进程发 `load`，再请求当前 tick。时间轴位置、相机与播放状态不变。
3. 承载器自带的属性面板也通过邮箱协议向 LiveEditor 发 `set` / `set_color` / `set_fields` / `undo`，不维护第二份文档。MCP 的 `motionvfx_set_field` 等工具因此同时驱动游戏内预览和离线预览。

目标延迟：修改到画面更新 200 ms 以内（邮箱轮询 + 序列化 + 引擎解析 + 重跑到当前 tick）。只有部分 entry 改动时，只重建这些 entry 并重跑；有 PtLife / PtColliderAction 引用子 Action 时整体重建。

### 8.4 属性模块组装（功能愿望，P2 扩展预留）

用户希望未来可把一个属性拖入发射器，组装 EFX，并在放入后重载效果。**结构修改后从 tick 0 重新播放即可，不要求接续原来进度**。本项只纳入后续计划，尚未实现，也不扩大当前 P1 的交付范围。

安排在 P2 的字段编辑、撤销、文档序列化和重载通道稳定之后。它复用同一份 LiveEditor 文档和预览修订号，不另建组装文档。先使用“属性库 + 发射器属性列表 + 参数面板”的形式；多发射器和子 Action 连接关系随后续类型支持扩展。

- 拖入、删除或复制属性通过 EditorEngine 的结构编辑操作完成，作为一次可撤销事务；分配正确的 UniqueID，按文件要求排序和序列化。
- 按 RE4 的重复限制、单一 Type 约束、必要属性与资源依赖验证组合；支持范围之外的属性应说明原因。属性默认值应来自已验证的模板或类型规则。
- 一次拖放提交后发布新修订；预览合并排队修改、重建发射器并从 tick 0 播放。保留相机和场景设置，错误时显示原因并保留可恢复的文档。
- 验收：添加一个受支持属性后从头出现新效果；删除、复制与撤销能够往返；保存再读取仍是合法 EFX；加载失败可修正并重试。

本项为预留扩展，不作为进入 P3 外观链的阻塞条件。第一步仍是已有 entry 的参数编辑闭环。

## 9. 分阶段路线

| 阶段 | 内容 | 验收 |
|---|---|---|
| P0 数据通道 | 辅助进程 `serve` 模式（控制管道、共享内存、`load` / `frame` / `shutdown`）；新承载器骨架（窗口、ImGui、相机、网格）；粒子以点显示；粒子检查器按偏移显示原样记录 | `exp_emitter.py` 的 bb、bb2 场景经共享内存读出的每 tick 位置、年龄、生命淡出字节与直接运行一致；人为触发 `NativeFault` 后承载器显示故障并自动重启辅助进程 |
| P1 最小可用 | 单个 Billboard3D entry 带贴图：.tex 加载；真实 .uvs 帧数传入辅助进程，承载器查矩形；面向相机的面片、尺寸、滚转、顶点 alpha（life × blink）；BlendType 0、1 的折中混合；播放、暂停、单帧步进、拖动；固定 60 Hz；加载后状态的保存与恢复；持久映射实例缓冲 | bb2（sm82_320_00 root 13）出生时刻与粒子数同 Re4Native；UV 帧按真实帧数循环；拖回同一 tick 时粒子数组摘要相同；一万粒子压力 entry（放大 MaxParticles 的语料 entry）60 fps，并记录原生每 tick 耗时；与游戏录像并列，尺寸量级与翻页节奏一致 |
| P2 编辑闭环 | LiveEditor 预览输出、修订号与加载状态；承载器属性面板经邮箱协议修改字段；重载与撤销；优化重载延迟，保留进度与播放状态；局部重建移至 P3 多 entry 阶段 | 先验收单个 Billboard3D entry 的颜色、尺寸、Life 修改、撤销、保存再读与预览修订一致；首个增量可用显式从头重播模式。完整目标仍是 200 ms 内更新、保持时间轴位置、撤销后同 tick 摘要一致；延迟单独实测 |
| P2 扩展预留：属性模块组装（功能愿望） | §8.4；在基础编辑与重载稳定后，支持把属性拖入发射器、删除、复制和结构撤销；每次结构修改从 tick 0 重播 | 受支持属性组装后能重载并从头出现效果；组合规则和资源依赖有效；结构撤销与保存再读正确。不阻塞 P3 |
| P3 外观链 | 一个 EFX 文件的全部 CPU entry；多 entry 的局部重建（有子 Action 依赖时整体重建）；FadeBy*、AlphaCorrection、ShaderSettings（软粒子、DepthBias、DetonemapRatio）、RgbCommon / RgbWater、BlendType 2–4；排序；合成配置面板；深度纹理 | 各公式的着色器单元测试（给定输入与规格公式的 C# 实现逐值比较）；含 10 个以上 entry 的官方文件无 NaN、无原生故障 |
| P4 条带族与 Polygon | §4.5 点缓冲定位与导出；§5.3–§5.5 | 每种类型至少一个官方 entry：承载器解码出的点与辅助进程原样记录一致；宽度、V 方向与游戏录像一致 |
| P5 Mesh、Distortion、挂点 | .mesh / .mdf2 真实部件数进入辅助进程；实例化网格；Distortion 通道；角色骨骼动画驱动发射器矩阵轨道；ParentOptions 骨骼查找改为承载器提供的关节矩阵 | 挂在骨骼上的官方特效随动作移动；§4.3 的世界变换按 3dfca50 的 C# 重写，原生比对开关下误差 < 1e-5 |
| P6 子 Action 与 GPU 类型 | 嵌套资源加载器 3cfadc0，子 Action 真正运行；GPU 类型的数据源（§5.8、§8.1） | PtLife、PtColliderAction 触发的子发射器在正确位置出现；GPU 类型有近似画面并标注"近似" |
| P7（可选）引擎实例流与 D3D12 对照 | slot 27 实例写入器 + 渲染器替身（§4.6）；离屏 D3D12 用游戏 DXIL 绘制对照图 | 同一帧 GL 与 D3D12 对照图的差异可定位到具体折中项 |

P1 完成即满足"单个 Billboard3D entry 带贴图跑起来"。P2 放在外观链之前，是为了尽早让编辑工作流可用。

### 9.1 当前下一步（P2 窗口验收已补，2026-11-15）

P2 已实现单个已有 Billboard3D entry 的通用属性面板：选择已有属性，按 Inspector 描述编辑字段、颜色与枚举，同属性批量应用，管理只读与过期草稿。完整 EFX 修订发布、同进程快速重载、保持已显示 tick 与播放／暂停状态、撤销和保存再读取均已接通。发布修订与成功显示修订分别记录，过期加载结果会被丢弃。真实窗口已验收暂停与播放中修改、撤销/重做、故障恢复、保存保护及不支持混合方式的错误提示。

清单轮询已从 250 ms 改为默认 25 ms（`--manifest-poll-ms` 可配置），发现修订后快照加载与表单 status 查询并行。通用面板接入后，bb2 在 tick 45/300/900 的窗口提交路径，修改到首帧 GPU 完成的 P95 为 162–179 ms；外部修订路径为 120–129 ms。MCP 已连接离线预览窗口，并包含属性增删、跨文件复制粘贴与曲线关键帧编辑，见 [MCP 接入](RE4-EFX-PREVIEW-MCP.zh-CN.md)；属性组装目前只有 MCP 入口，窗口内还没有拖放界面。P3 多 entry 已实现（整文件模拟与绘制、编辑对象切换、原生故障按 entry 排除），见 [多 entry](RE4-EFX-PREVIEW-P3-MULTI.zh-CN.md)；局部重建尚未实现，整文件修改延迟目前高于 200 ms。P3 外观链已实现（淡出、AlphaCorrection、Rgb 层、混合方式 0–4、软粒子、深度偏移、反色调映射、排序、合成配置），GPU 与 C# 公式逐值一致，见 [外观链](RE4-EFX-PREVIEW-P3-APPEARANCE.zh-CN.md)；与游戏画面的校准尚未进行。TypePolygon 与 TypeBillboard2D 已接入，见 [Polygon 与 Billboard2D](RE4-EFX-PREVIEW-P4-POLYGON-2D.zh-CN.md)；Billboard2D 的屏幕坐标系为折中。TypeRibbonFollow 已接入，点数据取自引擎点池（类型块 +0x38），同时帧解码改为用粒子参考矩阵 P 求世界位置，RibbonLength 用同一种点池（引擎布局结果）一并接入，见 [条带族](RE4-EFX-PREVIEW-P4-RIBBON.zh-CN.md)。测量边界与早期对照见 [P2 窗口验收记录](RE4-EFX-PREVIEW-P2-ACCEPTANCE.zh-CN.md)，通用面板实现、验证和最新复测见 [通用属性面板](RE4-EFX-PREVIEW-P2-GENERIC.zh-CN.md)。

P1 的游戏录像外观对照和完整分辨率一万粒子 60 fps 仍是未完成项，继续跟踪，不能因进入 P2 标记为已通过。当前实现与测量见 [RE4-EFX-PREVIEW-P1.zh-CN.md](RE4-EFX-PREVIEW-P1.zh-CN.md)。


## 10. 待决

| # | 问题 | 选项 | 推荐与理由 |
|---|---|---|---|
| D1 | 宿主程序 | A 扩展 `VfxStudio.App`；B 新建 `VfxStudio.Re4Preview` + `VfxStudio.Re4Render`，UI 类移入共享库 | B。粒子绘制、模拟、时钟都要换，扩展 App 只能复用场景与 UI；`MainWindow` 与 `EfxSimulator` 有并行修改，新建可避免冲突 |
| D2 | 图形 API | OpenGL 4.6 自写着色器；D3D12 游戏 DXIL | 先 OpenGL。现有渲染全在 GL 上；D3D12 需要的渲染器绑定模型与合成链空白是同一批未读项，换 API 不减少逆向工作。D3D12 作为 P7 离屏对照渲染器 |
| D3 | 粒子数据来源 | 解码粒子记录；运行引擎 slot 27 实例写入器捕获实例流 | 先解码记录（字段已知，P0 即可开始），并行调研 slot 27，可行后切换：它给出与游戏一致的实例数据，同时消除 §4.2、§4.3 的多数待定位项 |
| D4 | 辅助进程语言 | 保留 Python（ctypes + numpy）；移植到 C# 或 C++ | 先保留 Python：现有映射、桩与全部比对都在 Python 上完成，热路径是原生代码。分发给他人时需要附带嵌入式 Python 与 numpy，届时再评估移植 |
| D5 | 默认时钟 | 固定 60 Hz；跟随显示帧率 | 固定 60 Hz。时间轴与规格的帧单位一一对应，拖动可复现；其他帧率作为显式模式 |
| D6 | `EfxSimulator` 去留 | 继续作为 RE4 主模拟；冻结 RE4 CPU 类型，转为后备数据源 | 后备数据源（§8.1）。需要通知 `offline-efx-simulator-re` 会话调整方向 |
| D7 | 文档权威 | LiveEditor 后台；承载器自带文档 | LiveEditor。撤销、序列化、MCP 已在其中，承载器只做客户端 |

## 11. 风险与未知

按对画面的影响排列。

| # | 内容 | 影响 | 处理 |
|---|---|---|---|
| 1 | primitivevfx 家族的面片构建、条带宽度展开（规格 §7.3） | CPU 类型的几何 | 折中按 GPU 家族公式；P7 或读 DXIL 裁定 |
| 2 | 像素着色器的颜色组合与混合因子（规格 §8 第 2 项） | 所有画面的亮度与叠加 | 合成配置（§6.9）；与游戏录像对照 |
| 3 | 粒子世界变换 3dfca50 与 ParentOptions 跟随目标（规格 §8 第 6 项） | 发射器运动时粒子是否跟随 | P5 原生比对后 C# 重写 |
| 4 | §4.2、§4.5 的待定位偏移 | FrameBlend、翻转、Rgb* 随机、条带点 | 逐属性差分实验；或 D3 切换到 slot 27 |
| 5 | 同一进程内重跑的确定性 | 拖动与编辑重跑 | 整节恢复可写节与竞技场；P1 验收 |
| 6 | LegacyMode2016 / 2018 发行版取值（规格 §8 第 4 项） | 首批出生时刻、ScaleAnim 首帧、类型分派位置 | 按 0 运行；`set_globals` 提供切换 |
| 7 | 子 Action 不运行（嵌套加载器 3cfadc0 未解析） | 依赖 PtLife / PtColliderAction 的特效不完整 | P6 |
| 8 | GPU 类型没有原生逐粒子数据 | GPU 类型只有近似 | §5.8 |
| 9 | 辅助进程启动耗时与可写节恢复耗时未测 | 崩溃恢复、重跑延迟 | P0 / P1 实测，必要时预启动备用进程 |

## 12. 实施状态补充（2026-11-15）

P2 已通过独立离线 LiveEditor 会话编辑已有 Billboard3D 的颜色、尺寸和 Life；完整 EFX 修订发布、同进程重载、保持 tick 与播放／暂停状态、撤销/重做、保存副本/重新打开，以及发布与加载失败恢复均已接通。界面区分文档和画面修订，过时加载不会覆盖最新目标。使用与验证见 [RE4-EFX-PREVIEW-P2.zh-CN.md](RE4-EFX-PREVIEW-P2.zh-CN.md)。

首次进程加载仍约需 4.5–5.3 秒；常规同进程原生重载约 30–37 ms。窗口绘制实测见 §9.1，代表性窗口与外部修订样本已低于 200 ms，手动操作仍有超过 200 ms 的个别样本。通用表单与 MCP 离线接入已完成，局部重建移至 P3。§8.4 属性模块组装已有 MCP 工具（增删、复制粘贴属性，结构撤销），窗口内拖放界面仍为预留。
