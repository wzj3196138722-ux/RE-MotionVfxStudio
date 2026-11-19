# RE4 离线预览：EFX 节点图

节点图把一个 EFX 文件的结构画成图：Root → 各 entry（属性列表内嵌在节点里）→ Action → Action 内嵌的 entry。布局参照 Capcom 的 RE 引擎特效编辑器（RE:2023 讲座）。点击节点里的属性，会在“EFX 属性编辑”面板中打开它。

这一版只能查看和选择，还不能在图上增删节点或改连线。

## 数据模型（`VfxStudio.Re4Render/PreviewEffectGraph.cs`）

模型不依赖任何界面库，以后换 Avalonia 等界面时可以直接复用。

| 类型 | 内容 |
|---|---|
| `PreviewEffectGraph` | `Nodes`、`Links`、`Warnings`。用 `Read(path)` 或 `Build(EfxFile, title)` 构建。`PreviewScene.Read` 读预览快照时顺带构建，结果放在 `PreviewScene.Graph` |
| `PreviewGraphNode` | 种类（Root / Emitter / Action / EmbeddedEmitter）、标题、副标题、默认列、输入引脚、行列表；entry 节点带 `EntryPosition` 和 `Previewable` |
| `PreviewGraphRow` | 一个属性或一行说明。`AttributeIndex` 是该属性在列表中的下标，与编辑器里的属性下标一致。`Role` 分为普通属性、Type、触发（PtLife / PtColliderAction）、目标（PlayEmitter / PlayEfx）、说明 |
| `PreviewGraphLink` | 从输出引脚连到输入引脚，带标签（例如“进入 Keep”“命中”） |
| `Layout(visibleRows)` | 默认分层布局，坐标以文本行为单位。传入每个节点的可见行数，就能算出收起状态下的紧凑布局 |

连线规则：

- Root 连到每个顶层 entry。
- PtLife 的 `ActionIndex`、PtColliderAction 的 `linkedAction` 连到文件 Action 列表中对应的项。`-1` 或 `0xFFFFFFFF` 表示不触发，不画连线。下标越界时写进 `Warnings`，也不画连线。
- PlayEmitter 连到它内嵌文件里的每个 entry。PlayEfx 只显示外部路径。
- 内嵌文件自己的 Action 不展开，只在节点里注明数量。

节点 ID 由 entry 位置和 Action 下标算出，所以同一文件的不同修订之间 ID 不变，用户拖动过的节点位置会保留。每个节点最多 1024 个引脚，一个 Action 最多显示 1024 个内嵌 entry，超出部分写进警告。

语料统计（3946 个可读文件）：312 个文件带 Action，共 536 个 Action，其中 PlayEmitter 475 个、PlayEfx 61 个。PtLife 和 PtColliderAction 共 795 条，指向的 Action 下标都在范围内，另有 2 条为 `-1`。

## 界面（`VfxStudio.Re4Preview/PreviewGraphPanel.cs`）

基于 Hexa.NET.ImNodes 2.2.9，与 Hexa.NET.ImGui 同版本。在主面板勾选“节点图”可以显示或隐藏这个窗口。

- **节点颜色**：Root 灰色；可预览的 entry 蓝色，不可预览的灰色；当前编辑的 entry 橙色；Action 绿色；内嵌 entry 蓝灰色。Type 行显示为浅蓝，触发和目标行显示为浅绿，当前编辑的属性以 `>` 标出。
- **选择**：点击可预览 entry 中的属性行，属性面板会切换到该 entry（`select_entry`）并打开这个属性。属性面板有未应用的草稿、编辑器忙或处于压力模式时不能选择，工具栏会说明原因。内嵌 entry 也可以点击打开：属性面板改为编辑它，3D 视图仍停在原来的顶层 entry（见 RE4-EFX-PREVIEW-STRUCTURE“内嵌 entry”）。Action 节点只能查看。
- **缩放**：ImNodes 本身不支持缩放，这里自己实现。滚轮以鼠标位置为中心缩放（20%–200%），节点位置随之缩放；字体用 ImGui 1.92 的 `PushFont(font, size)` 按比例重新烘焙，放大后不会模糊。节点内边距、网格、引脚和连线粗细也一起缩放。“100%”恢复原始比例。
- **适配全部**：保持现有排列，缩放并平移，让所有节点都显示在画布内。节点多于 12 个的文件打开时自动适配。
- **收起**：每个节点标题栏有 `-` / `+` 按钮。收起后只保留带连线的行（触发、PlayEmitter），因为 ImNodes 要求连线两端的引脚必须存在；其余行折叠成“已收起 N 项”。“全部收起”和“全部展开”会重新紧凑排列并适配画布，用于查看整体结构。
- **重新排列**：按默认布局重新放置所有节点。
- **定位所选 entry**：把画布移到当前编辑的 entry。
- **小地图**：在右下角。

一起改动的地方：

- “编辑 entry”下拉框原先只允许选 Billboard3D，现在凡是预览支持的类型都能选，判断依据是节点的 `Previewable`。
- 属性面板默认打开第一个 Type* 属性，原先只找 TypeBillboard3D。

## 测试

`tools/Re4MultiEntryTests` 第 6 节，不需要原生进程：

- gimmick_mot_0011：Root 连到 20 个 entry，3 个 Action，3 条触发连线，每条连线的引脚都能找到对应节点。
- 默认布局中每个节点只出现一次，收起状态的布局比展开时短。
- rage_0000：两个 PlayEmitter 分别连到 4 个和 8 个内嵌 entry。
- 3946 个官方文件都能构建节点图，节点、引脚、连线 ID 都唯一且都能对上，没有越界警告。

## 未做

- 在图上编辑：从目录拖入属性、删除属性、新增 entry 或 Action、改连线。底层的结构编辑操作已经有了（MCP 的 add / remove / paste attribute）。
- 编辑内嵌 entry：后端已经能定位内嵌属性，但预览不模拟子 Action。子 Action 的预览联动属于 P6。
- Extern Parameter、EffectGroup 节点。分组目前只显示在 entry 副标题里。
- 属性目录、负载警告图标、底部时间轴和曲线编辑。
- 收起状态和节点位置不会写入磁盘：窗口关闭了 ImGui ini，重开文件后恢复默认布局。
