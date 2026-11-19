# RE4 离线预览 P3：多 entry

预览窗口现在加载整个 EFX 文件：所有能模拟的 CPU Billboard3D entry 在同一个原生工作进程里一起推进、一起绘制，其中一个 entry 是当前编辑对象。

## 使用

```powershell
dotnet run -c Release --project VfxStudio.Re4Preview -- --fixture bb2
```

`--fixture bb2` 打开 sm82_320_00 整个文件，默认编辑 entry 13。主面板里：

- “显示 N / M 个 entry”：文件共 M 个 entry，N 个正在模拟和绘制。
- “编辑 entry”：切换编辑对象。悬停未显示的 entry 可看原因。切换只改变编辑目标，不重载、不改变 tick。属性面板有未应用草稿时不能切换。
- “只显示所选 entry”：只绘制编辑对象，其余 entry 仍在模拟。
- “原生模拟器排除的 entry”：列出加载或运行时出错、已被排除的 entry 及错误信息。

`--single-entry` 只模拟编辑对象。`--stress` 和 `--benchmark` 自动使用这一模式，与 P1 的测量条件保持一致。

MCP：`select_entry {entryPosition}` 切换编辑对象；`status.preview.entries` 列出全部 entry 及其状态；`frame {entryPosition}` 只统计某一个 entry；`view {soloSelected}` 对应“只显示所选 entry”。见 [MCP 接入](RE4-EFX-PREVIEW-MCP.zh-CN.md)。

## 哪些 entry 会显示

| 情况 | 处理 |
|---|---|
| 没有 Type 属性（如 Root） | 不显示，不产生粒子 |
| Type 不是 Billboard3D | 不显示，标注“尚未支持”（Polygon、条带属于 P4，Mesh 属于 P5） |
| Billboard3D，但混合方式不是 0/1、没有 UVSPath，或资源缺失 | 不显示，原因来自资源读取 |
| 原生模拟器加载或推进时出错 | 排除该 entry，其余 entry 重新加载 |
| 贴图无法创建 | 该 entry 照常模拟，但不绘制，面板标红 |
| 超过 64 个可显示的 entry | 第 65 个起不显示（原生协议一次最多 64 个） |

原生排除按文档记录：同一文档后续修订直接沿用，不再重复触发故障；“重载并重播”保留这些记录。

## 实现

| 部分 | 位置 |
|---|---|
| 场景 | `VfxStudio.Re4Render/PreviewScene.cs`：`PreviewScene` 读取整个文件并决定哪些 entry 显示；原生工作进程的 entry 表顺序就是 `Shown` 的顺序 |
| 故障排除 | `PreviewSceneLoader`：加载时出错，按工作进程报告的 entry 位置排除后重试；运行时出错（`HostFaultException` 或非致命的 `NativeCommandException`，都带 entry 位置）由窗口排除后在同一 tick 重新加载 |
| 帧解码 | `PreviewEntryState.First` / `Count` 给出每个 entry 在粒子列表中的连续区间 |
| 绘制 | `PreviewSceneRenderer` 为每个显示的 entry 建一个 `BillboardRenderer`，按文件顺序绘制；`BillboardGpuCache` 让所有 entry 共用一份着色器，并按资源路径缓存贴图。各 entry 混合方式不同时，粒子层固定用完整分辨率，因为低分辨率合成只能用一种混合方式 |
| 选择 | `OfflinePreviewManifest.SceneKey` 不含 entry 位置。新清单与当前清单只差编辑对象时，窗口只更新选择，不重载 |
| 原生循环 | `re4host.py` 只在世界矩阵变化时写 U+0x40，并改为一次内存拷贝；快照恢复后清空记录，下一 tick 重新写入。写入的字节与原来相同 |

原生工作进程本来就接受多个 entry（`load.entries`），协议没有变化。

## 验证

```powershell
dotnet run -c Release --project tools/Re4MultiEntryTests [-- --corpus]
dotnet run -c Release --project tools/Re4McpTests
```

`Re4MultiEntryTests`：

- sm82_320_00 文件共 31 个 entry，模拟其中 24 个；entry 20 在原生加载时出错，被排除。连续推进 600 tick，没有非有限的粒子位置；每帧的 entry 表顺序和各 entry 的粒子区间都与场景一致。
- 同进程重载后，所有 entry 在 tick 300 的粒子数组摘要与重载前相同。
- entry 13 和其他 23 个 entry 一起模拟时，tick 45 的粒子数组与单独加载时逐字节相同（该 entry 有 FixRandomGenerator）。
- muzzleflash 文件里有两个 entry 原生加载出错，都被排除，其余 entry 正常加载。
- `--corpus` 测五个大文件（90–256 个 entry，各显示 64 个），每个推进 600 tick，没有非有限值。其中两个文件各有一个 entry 在 tick 6 / 16 报除零错，按运行时故障排除。

`Re4McpTests`（53 项）新增：`status` 列出 31 个 entry；`frame` 汇总全部 entry；`select_entry` 切到 entry 12，修订号和 tick 不变；选择条带 entry 被后台拒绝。已有检查改为按 entry 13 统计。混合方式检查（现用不存在的 5）改为：其他 entry 照常绘制，场景截图成功；只显示所选 entry 时截图拒绝。

既有回归（Re4PreviewTests、P1、P2、通用编辑、播放控制含原生、run_all、窗口冒烟）均通过。冒烟在 tick 45 绘制 24 个 entry，共 22 个存活粒子；entry 28 的一张贴图解码越界，该 entry 只模拟不绘制。

## 实测（本机，RTX 4060 Laptop）

| 项目 | 单 entry（bb2） | 整个文件（24 个 entry） |
|---|---|---|
| 新进程首次加载 | 4.7–5.0 s | 约 4.8 s；遇到原生排除时每次另加一次新进程（本文件首次打开约 10 s） |
| 同进程重载 | 30–37 ms | 70–76 ms |
| 原生快照 | — | 109 MB |
| 每 tick 模拟 | — | 0.16 ms（改动前 0.28 ms） |
| 窗口修改到首帧 GPU 完成，中位数 tick 45 / 300 / 900 | 约 160 ms | 212 / 258 / 358 ms |
| 外部修订路径，同上 | 112–120 ms | 178 / 213 / 310 ms |

整个文件的修改延迟高于 200 ms 目标。拆开看：后台应用约 100 ms，原生重载约 76 ms（其中快照复制随占位缓冲大小增长），重播约 0.16 ms × tick。后续可以从三处下手：只重建改动的 entry；缩小每个发射器的占位缓冲，从而缩小快照；按 tick 设检查点，缩短重播距离。延迟数据在 `runtime/re4-preview/20261116_020635_4751cf932d684d889355420b64a8dfad/`。

## 未做

- 局部重建：每次修改仍重载全部 entry，并从 tick 0 重播到当前 tick。
- 子 Action、PtBehavior 等 entry 之间的依赖（P6）。目前每个 entry 独立模拟。
- 绘制顺序固定按文件顺序，跨 entry 排序属于 P3 的排序项。
- 原生模拟器在 47ce9e4 读空指针（调用链 3dfe4d1 ← 3d28158 ← 3d068a5），以及部分 entry 运行时除零；原因尚未定位，目前靠排除机制绕开。
