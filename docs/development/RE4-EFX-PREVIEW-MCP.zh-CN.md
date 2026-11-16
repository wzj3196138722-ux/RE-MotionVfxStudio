# RE4 离线预览：MCP 接入

`VfxStudio.Mcp --re4-preview` 让 AI 客户端操作正在运行的 `VfxStudio.Re4Preview` 窗口：打开发射器、读写字段、增删和跨文件复制属性、编辑曲线关键帧、撤销重做、拖时间轴、读粒子数据和截图。AI 的修改走窗口自带的 LiveEditor 后台，窗口实时显示，撤销历史与面板操作共用。

这一模式与游戏内的 `motionvfx_*` 工具分开：工具名前缀为 `re4preview_`，服务器名为 `motionvfx-re4-preview`，不连接游戏，也不写游戏目录。

## 使用

```powershell
dotnet build VfxStudio.Mcp -c Release
dotnet build VfxStudio.Re4Preview -c Release
```

仓库根目录的 `.mcp.json` 已登记服务器 `re4-preview`。在仓库内启动 Claude Code 时，按提示批准即可。其他客户端的启动命令：

```text
dotnet <仓库>/VfxStudio.Mcp/bin/Release/net8.0/VfxStudio.Mcp.dll --re4-preview [--pipe <名称>] [--repo <仓库>]
```

没有窗口时，调用 `re4preview_launch` 启动一个，也可以手动运行预览程序。窗口启动后，主面板显示控制通道名称；AI 请求执行期间显示“AI 正在执行：…”。

典型流程：

1. `status`，或用 `launch` 启动窗口。
2. `inspect` 列出属性；`inspect {attributeIndex}` 读取字段路径与当前值。
3. `set_fields {attributeIndex, fields, expectedRevision}`。
4. `frame {tick}` 读数值，`screenshot {tick}` 看画面。
5. 需要时 `undo`，最后 `save_copy`。

## 工具

| 工具 | 作用 |
|---|---|
| `status` | 文档修订、dirty/撤销状态、属性列表；预览显示的修订与 tick、加载或运行错误、未支持的功能；窗口里是否有未应用的草稿 |
| `launch` | 启动窗口并等到首帧显示；已有窗口应答时直接返回其状态 |
| `open` | `fixture`（bb / bb2），或 `path` 加 `entryPosition` / `entryId` / `offset` 之一；整个文件都会加载，所给 entry 作为编辑对象；从 tick 0 暂停开始 |
| `select_entry` | 切换编辑对象（Billboard3D、Polygon 或 Billboard2D entry）；不重载，不改变 tick |
| `inspect` | 不带参数列属性；带 `attributeIndex` 返回字段、颜色组和曲线（路径、通道、关键帧） |
| `set_fields` | 一个属性的 1–64 个字段，作为一个修订、一步撤销 |
| `attribute_types` | 当前 EFX 版本可添加的属性类型名；`catalog` 给出当前 entry 的属性目录（能否添加、RE4 同 Type entry 的使用比例，见 RE4-EFX-PREVIEW-CATALOG） |
| `add_attribute` | 按类型添加一个属性，字段取自 RE4 里同类属性的典型值（与窗口相同）；PtBehavior 还可用 `script` 指定脚本类（默认 EffectDecal），参数按 RE4 常见写法填入；后台拒绝重复类型、第二个 Type 属性和缺少基础属性的情况 |
| `remove_attribute` | 删除一个属性；其他属性和分组回读不一致时整笔回滚 |
| `copy_attribute` | 把一个属性复制到后台的复制缓冲区；缓冲区是快照，`open` 换文档后仍保留 |
| `paste_attribute` | 粘贴到当前发射器；已有同类型属性时需传 `replaceAttributeIndex` 指定替换对象。来源与目标的文件级表达式／变量表不同时拒绝 |
| `add_entry` | 追加一个新 entry（Type 限预览能画的六种），带同 Type 在 RE4 里 95% 以上都有的属性，字段取典型值；或者用 `script`（PtBehavior 脚本类，如 `via.effect.script.EffectDecal`）新建 RE4 常见的脚本载体（NoDraw + PtBehavior，参数取 RE4 常见值）。新 entry 成为当前 entry，不加入 effect group |
| `duplicate_entry` | 在文件末尾追加一个 entry 的副本，预览能画时选中它 |
| `remove_entry` | 删除一个 entry；删的是当前 entry 时先改选位置最近的可预览 entry，没有可选的则拒绝。后面的 entry 位置前移 |
| `copy_entry` / `paste_entry` | 把 entry 复制进复制缓冲区（打开其他文件后仍在），再追加到当前文件（名字加 `_copy`）。表达式／变量表不同时拒绝 |
| `curve_key` | 在已有曲线通道上增、改、删一个关键帧；不改变通道数量和映射，通道至少保留一个关键帧，Bezier 需给全四个切线值 |
| `undo` / `redo` | 需要 `expectedRevision` |
| `save_copy` | 省略路径时存到会话目录；游戏目录和解包目录拒绝写入 |
| `playback` | play / pause / seek / step / replay |
| `frame` | tick、存活数、各 entry 的存活数、位置范围、宽高与顶点 alpha 的范围和均值、UV 序列分布；可列出前若干个粒子。带 `entryPosition` 时只统计该 entry |
| `screenshot` | 场景画面（不含 UI 面板）的 PNG，以 MCP 图片返回，同时存到会话目录 `ai-captures/` |
| `view` | 相机、曝光、辉光、粒子点对照、粒子分辨率、只显示所选 entry |
| `composition` | 合成配置：各折中步骤的开关、Physical 增益、每种混合方式的混合因子；结果附带上一帧的绘制顺序。见 [外观链](RE4-EFX-PREVIEW-P3-APPEARANCE.zh-CN.md) |
| `close` | 关闭窗口；文档有未保存修改时需 `discardUnsaved` |

## 行为约定

- **修订号**：所有修改都要传当前 `document.revision`，旧修订由后台拒绝，字段校验和区间检查同样由后台执行。被拒绝的请求不改变修订和撤销历史。
- **等待画面**：`set_fields`、`undo`、`redo`、`open` 等到新修订加载完、首帧显示后才返回。新修订无法显示时（例如混合方式不受支持），修改仍然生效并可撤销，结果用 `previewError` 说明原因。
- **时间轴**：编辑保持当前暂停的 tick；`open` 和 `replay` 回到 tick 0。`frame` 和 `screenshot` 带 `tick` 时，先跳到该 tick 并暂停，等这一帧显示后再读取。
- **草稿优先**：窗口属性面板里有未应用的草稿时，AI 的修改和 `open` 被拒绝，AI 不会覆盖或丢弃用户的输入。
- **不确定结果**：超时或连接中断时返回 `outcome_uncertain`。修改可能已经生效，应先调用 `status` 再决定，不要直接重试。
- **结构编辑**：增删、粘贴属性后属性下标会变化，后续操作要用最新结果里的 `attributes`。窗口属性面板随之重新选择属性。`paste_attribute` 返回 `selection.attributeIndex`，即粘贴后属性所在位置。用内容完全相同的属性替换时，后台不生成新修订。
- **跨文件组装**：`copy_attribute` → `open` 另一个文件 → `paste_attribute`。复制缓冲区在后台会话内保留，`status` 的 `copyBuffer` 显示当前内容。
- **执行顺序**：同一时刻只执行一个 AI 请求。窗口仍在处理上一个请求或面板操作时，新请求排队等待。

## 实现

| 部分 | 位置 |
|---|---|
| 控制通道 | `VfxStudio.Re4Preview/PreviewControlServer.cs`：命名管道 `MotionVfx.Re4Preview.Control`，只允许当前 Windows 用户连接；每次连接一行 JSON 请求、一行回复（协议 1）。窗口独占这一名称，第二个窗口会显示通道已被占用 |
| 请求执行 | `VfxStudio.Re4Preview/Program.cs`：每帧在窗口线程取一个请求，按阶段推进（等编辑槽、等后台、等画面稳定、等截图像素、等 PNG 编码）。AI 编辑占用与面板相同的编辑槽，动作名带 `ai:` 前缀，因此不会清掉面板草稿 |
| 结果格式 | `VfxStudio.Re4Preview/PreviewControlReports.cs` |
| 后台调用 | `PreviewEditorSession.SendRawAsync` 返回后台完整回复（inspect 的字段表需要它）；`OpenAtAsync` 按 entry 位置打开。结构编辑分别对应后台的 `add_attribute`、`remove_attribute`、`copy_efx`、`paste_efx`、`curve_key` |
| MCP 服务器 | `VfxStudio.Mcp/Re4PreviewTools.cs`（工具表与参数规则）、`PreviewPipeBridge.cs`（管道客户端与 `launch`）。`McpServer` 改为按工具集工作；回复里带 `mimeType` 与 base64 `data` 的结果转成 MCP 图片块，文本和结构化副本不再重复这份数据 |

窗口启动参数：`--ai-pipe <名称>` 改用其他管道名，`--no-ai-control` 关闭控制通道。`--smoke` 和 `--benchmark` 模式不开启控制通道。

`launch` 启动的窗口不继承 MCP 服务器的标准输入输出，输出写入 `runtime/re4-preview/mcp-launch-*.log`。MCP 服务器退出后，窗口继续运行。

- 控制管道：客户端连上后没发请求就断开（或 10 秒内没发完），服务端只丢弃这次连接。以前这种情况会让管道停在“断开但未重置”的状态，或把读超时当成窗口关闭而退出循环，之后整个窗口生命周期里 AI 都连不上。
- `Re4McpTests` 可用环境变量 `RE4_MCP_DLL` 指定另一份 MCP 构建（Release 的 dll 被正在运行的 MCP 进程占用时用 Debug 版）。

## 验证

```powershell
dotnet VfxStudio.Mcp/bin/Release/net8.0/VfxStudio.Mcp.dll --self-test
dotnet run -c Release --project tools/Re4McpTests
```

- MCP 自检共 107 项，新增的预览工具检查覆盖工具表隔离、参数规则（含 `curve_key` 各操作的参数组合）和图片块转换。
- `Re4McpTests` 启动真实的 MCP 进程，用私有管道名拉起真实窗口，共 56 项检查：
  - 无窗口时报告 `preview_not_running`；`launch` 启动并复用窗口。
  - 多 entry：`status` 列出 sm82_320_00 的 31 个 entry；`select_entry` 切换编辑对象，修订号和 tick 不变；条带 entry 不能选为编辑对象。
  - inspect、tick 45 读帧、截图。
  - 加倍 SizeScalar.center 后原生粒子宽度增大，编辑保持 tick；旧修订和反向 Life 区间被拒绝，修订不变。
  - 不存在的混合方式 5 提交后报告 `previewError`；其他 entry 照常绘制，场景截图成功，只显示所选 entry 时截图拒绝；撤销后恢复；再撤销一次，粒子宽度回到原值。
  - 保存到游戏目录被拒绝，保存到会话目录成功。
  - 结构编辑（bb2）：列出属性类型；复制 Life 不产生修订；重复添加和不带替换的粘贴被拒绝；删除 Life 后再粘贴回来，tick 45 的原生粒子数与宽度和原来一致；相同内容替换不生成修订；两次撤销回到删除前。
  - 跨文件组装：把 bb2 的 Life 替换进 bb。
  - 曲线：按路径加 entryPosition 打开 `efd_0015_cse010_s00_muzzleflash_0000` 第 4 个 entry，在 `PtUvSequenceClip` 上修改并新增关键帧，两次撤销后关键帧恢复；对不存在的曲线路径操作被拒绝。
  - seek / step / replay 和 view。
  - 用 `open` 切换到 bb，`close` 后窗口进程退出。

本机记录（bb2，RTX 4060 Laptop）：`launch` 到首帧约 10 s；`set_fields` 从 MCP 请求到新修订首帧显示约 200–250 ms（含管道往返）。输出在 `test-output/re4-mcp-20261115_202328/`，包含修改前后的截图和完整 JSON-RPC 记录。

## 未做

- 增删曲线通道、集合扩容、新建空复杂节点。后台目前也不支持这些操作。
- 复制、粘贴、增删整个 entry（后台的 `duplicate_entry`、`add_entry`、`remove_entry` 和 entry 级粘贴）。预览只显示一个 entry，暂未接入。
- 原生模拟器还不能运行上述 muzzleflash entry：加载时在 47ce9e4 读取空指针（调用链 3dfe4d1 ← 3d28158 ← 3d068a5），可能与 PtUvSequence 有关。窗口和 MCP 照常打开、编辑这个文档，预览部分报告加载失败。
- 多 entry 预览，以及 Billboard3D 以外的类型。
- 同时连接游戏内编辑器和离线预览的单一服务器。目前需要登记两个服务器。
