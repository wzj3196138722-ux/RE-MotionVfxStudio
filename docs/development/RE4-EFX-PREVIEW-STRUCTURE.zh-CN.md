# RE4 离线预览：窗口里的结构编辑

预览窗口可以直接增删、复制粘贴属性和 entry，不再只能靠 MCP。每个操作是一个新修订，可以撤销。属性面板有未应用的草稿、编辑器正忙或处于压力模式时，这些操作都不可用。

编辑和预览不绑定：文件里任何顶层 entry 都可以作为编辑对象，包括预览还画不了的类型（Mesh、GPU 类型、StrainRibbon 等）和没有 Type 的 Root。预览只绘制它支持的 entry；所选 entry 不绘制时，主面板给出原因，属性照常编辑。节点图里不绘制的 entry 显示为灰色节点，但同样可以单击打开、右键操作。

## 内嵌 entry

Action 的 PlayEmitter 里内嵌的 entry 也可以编辑。在节点图里单击内嵌 entry 节点或它的属性行，属性面板就切换到它，顶部显示“编辑内嵌：Action 0 › 属性 0 › [2]”这样的路径。字段、曲线、材质参数覆盖、属性的增删和复制粘贴，都和顶层 entry 一样可用；属性目录的拖拽和双击也加到这个内嵌 entry 上。

- **预览不跟着切换**：3D 视图仍停在原来的顶层 entry 上。每次修改都会重新发布整个文件，子 Action 被触发时就会显示改动（预览运行由 PtLife 触发的内嵌 Billboard3D、Polygon、Billboard2D）。
- **回到顶层**：在节点图里单击任一顶层 entry，或用“编辑 entry”下拉框。
- **失效时退回**：撤销或删除属性后，所编辑的内嵌 entry 不存在了（例如撤销了创建 PlayEmitter 的粘贴），就退回编辑预览的顶层 entry。
- **只到第一层**：节点图只展开顶层 Action 里内嵌的 entry。内嵌文件自己的 Action 不展开，所以第二层以下只能通过 MCP 的 `embeddedPath` 选中。
- **entry 级操作**：节点图里右键内嵌 entry，有复制 entry、创建副本、删除 entry，以及在同一个内嵌文件里新建 entry（按 Type 或脚本载体）、粘贴 entry。新建、副本、粘贴的 entry 会切换为当前编辑对象。新 entry 的 id 在内嵌文件里取最大值加一，不占用顶层文件的 id。
- **删除**先确认。内嵌文件里唯一的 entry 不能删（要去掉整个子特效，删 Action 的 PlayEmitter）。正在编辑同一内嵌文件里的 entry 时，先改为编辑预览所在的顶层 entry：内嵌 entry 按位置定位，删除后后面的 entry 会前移，会话层不按 id 跟随它们。撤销、重做同样可能让内嵌位置前后移动，这时属性面板显示的是同一位置上的 entry。

会话层用 `PreviewEditTarget`（kind、index、embeddedPath）表示编辑目标，和预览清单的 entry 分开保存。属性草稿、检查结果、曲线时间轴按这个目标区分，所以在内嵌 entry 上读到的草稿不能应用到顶层 entry，反之亦然。

## Action

Action 节点也可以单击打开，属性面板顶部显示“编辑 Action：Action 0”。RE4 语料里 536 个 Action 每个只有一个属性：PlayEmitter（475 个）或 PlayEfx（61 个）。

- **PlayEmitter** 自己没有可编辑的参数，读出来的字段全是内嵌文件的内容（damage_0000 的一个 PlayEmitter 有 994 个字段，只有 efxrSize、type、UniqueID 等 5 个是它自己的，且都只读）。属性面板不列这些字段，改为列出内嵌的 entry，按钮直接打开对应的内嵌 entry。
- **PlayEfx** 的字段（外部 EFX 路径等）照常列出、编辑。
- **属性目录**：Action 只能加 PlayEmitter 和 PlayEfx，按 RE4 里的使用次数排序；其他属性标为“entry 专用（entryOnly）”，不能加。entry 上的 PlayEmitter / PlayEfx 仍是“Action 专用”。
- **结构操作**：属性的删除、复制粘贴可用，例如把 PlayEmitter 换成 PlayEfx。删除 PlayEmitter 会连同内嵌的 entry 一起删掉，可以撤销。Action 本身的新建、副本、删除还不支持。
- 预览同样不跟着切换，改动随文件重新发布。

MCP：`select_entry` 增加可选的 `embeddedPath`（每一步是 `{kind, index, attributeIndex}`，指向一个 PlayEmitter），选中后 `inspect`、`set_fields`、`curve_key` 和属性的结构工具都作用于这个内嵌 entry。`status.preview.embeddedEntries` 列出节点图里的内嵌 entry 及其参数，`status.document.editTarget` 给出当前选中的内嵌目标（顶层 entry 时为空）。entry 级工具（`add_entry`、`duplicate_entry`、`remove_entry`、`copy_entry`、`paste_entry`）也接受 `embeddedPath`，位置按那个内嵌文件计。`select_entry` 用 `actionIndex` 代替 `entryPosition` 时选中 Action（`status.preview.actions` 列出 Action 及其属性），这时 `attribute_types` 的目录 `owner` 为 `action`。

## 入口

| 位置 | 操作 |
|---|---|
| 属性面板（选中的属性） | 复制属性、粘贴属性、删除属性 |
| 主面板“entry 操作…” | 新建 entry（按 Type）、粘贴 entry；当前 entry 的粘贴属性、复制 entry、创建副本、删除 entry |
| 节点图右键：属性行 | 复制属性、删除属性，以及所在 entry 的各项操作 |
| 节点图右键：entry 节点 | 粘贴属性、复制 entry、创建副本、删除 entry，以及新建 entry |
| 节点图右键：空白处 | 新建 entry、粘贴 entry |
| 属性目录 | 拖拽或双击添加属性（见 RE4-EFX-PREVIEW-CATALOG） |

- **复制**放进后端的复制缓冲区，打开其他文件后仍可粘贴，所以属性和 entry 都能跨文件搬。主面板显示当前复制的内容。两份文件的表达式／变量表不同时，后端拒绝粘贴。
- **粘贴属性**：目标 entry 已有同类属性时先确认替换。内容完全相同时，后端不生成新修订。粘贴后新属性在属性面板里打开。
- **删除属性**：不能删 Type 属性（删了就无法预览，换类型请新建 entry）；有 Expression / Clip 依赖它时，要先删依赖的属性。
- **新建 entry**：可选的 Type 是预览能画的 Billboard3D、Polygon、Billboard2D、RibbonFollow、RibbonLength、PolygonTrail。新 entry 带上 RE4 里同 Type 的 entry 有 95% 以上都带的属性，字段取默认值（见下）。例如 Billboard3D 会带 Transform3D、ParentOptions、Spawn、Life、UVSequence、ShaderSettings。新建、创建副本、粘贴的 entry 会切换为当前 entry。
- **删除 entry** 先弹出确认。删除的是当前 entry 时，先改为编辑位置最近的另一个 entry；它是文件里唯一的 entry 时不能删。

新 entry 不加入任何 effect group。游戏是否播放不在 group 里的 entry，还没有验证。

## 材质参数覆盖（TypeMesh 等）

TypeMesh 的 `properties`（以及 DD2 起材质类 Type 的 `material.properties`）是材质参数的覆盖表：每一项用参数名的 UTF-8 MurMur3 哈希指向材质里的一个参数，并重复它在 .mdf2 参数表里的下标和分量数；贴图项下标恒为 −1。所以名字、能加哪些参数、下标该填多少，只能从材质文件里得到。RE4 语料核对：5065 条数值覆盖的下标和分量数与所引用的 .mdf2 全部一致，298 条贴图覆盖下标全为 −1；另有 17 条覆盖指向 .mdf2 里已经没有的参数，45 个网格的 .mdf2 在本机找不到。

- TypeMesh entry 预览还不绘制，但和其他 entry 一样可以选中编辑（见开头）。
- **参考材质**：属性面板的“材质参数”区默认按属性自己的材质路径读 .mdf2（RE4 为 `.mdf2.32`），查找顺序与贴图相同（文档所在 natives、游戏散文件、解包目录、PAK）；也可以填一个 `.mdf2.<版本>` 文件作为参考。多材质的 .mdf2 拒绝使用（覆盖表没有材质下标）。
- **显示**：每条覆盖显示参数名、类型（贴图 / 随机区间 / float / float4），与参考材质不一致（参数不存在、下标或分量数不同）时标橙并在提示里说明。字段表里覆盖项按参数名列在最前：Range 的值标为 base · random · min · max（引擎出生时在 [min, max] 抽 random，送进材质的是 base + random，见 RE4-EFX-RUNTIME §4.3.11），`flags` 标为“0-1 钳制”；哈希、下标、分量数等收在默认折叠的“材质参数结构（只读）”。
- **添加**：从参考材质里没被覆盖的参数和贴图槽中选一个。下标、分量数按材质填写，初值取材质自己的值；单分量参数加成 Range (0, 0, 值, 值)，即不随机。
- **删除**：同一 entry 里有材质曲线（`*Clip`）或材质表达式按哈希引用这个参数时，后端拒绝删除。
- 只有 TypeMeshV2 和 `EfxMaterialStructV2` 可以增删（写出时会重建贴图表）；其他带覆盖表的结构只显示名字和核对结果。

后端动作 `material_parameter`：`{target, listPath, op: "add" | "remove", ...}`。`add` 需要 `hash`、`type`（Float / Range / Texture）、`count`、`mdfIndex`，以及 `value`（四个数）或 `texturePath`；`remove` 需要 `index`。一个修订、一步撤销，按写出后读回的哈希序列核对。

同时修正了两处写出问题：`EFXAttributeTypeMeshV2.propertiesDataSize` 与 `EfxMaterialStructV2.propDataSize` 原来不会随条目数重算，增删覆盖后文件会错位（读回时报 attribute 类型 0 / 3）。两份语料里这两个值都等于条目数 × 32，修正后门禁不变。

## 新属性的默认值

新加的属性（目录拖拽、双击、新建 entry、MCP 的 `add_attribute`）字段不再是编辑器库的零值，而是从语料里选出的一个真实属性的值。`tools/Re4AttributeCatalog` 对每种属性最多取 1500 个实例，先算每个字段最常见的值，再选与这些值相符最多的那一个实例，所以各字段来自同一个属性，彼此一致。只保留新建属性本来就有、并且可以编辑的字段，生成时逐个回写验证。结果在 `VfxStudio.Re4Render/Data/re4-attribute-defaults.json`，覆盖 113 种属性。

部分默认值：

- Billboard3D：颜色 (128, 94, 57, 204)，SizeScalar 2 ± 0.2，随机旋转 ±π。
- Life：KeepFrame 100。
- Spawn：MaxParticles 1，每次发射 1 个。
- UVSequence：64 帧，贴图 `tex_capcom_smoke_00_0050.uvs`。

按这些值新建的 Billboard3D entry 在预览里是一团烟。

## 后端改动

改动都是可选参数，游戏内编辑器原来的请求不受影响。

- `add_attribute` 接受 `fields`（同 `set_fields` 的 path / value），在同一个修订里写入新属性。给出的字段必须存在且可编辑，否则整个请求被拒绝。
- `add_entry` 接受 `attributes`（`[{type, fields?}]`），entry 和属性一起作为一个修订，回复里的 `selection` 给出新 entry 的位置。
- 离线预览的目标改为跟随 entry ID：前面的 entry 被删除、恢复或插入时，位置跟着移动。目标 entry 本身消失时（例如撤销创建它的粘贴或副本），改到位置最近的 entry。编辑目标可以是任何顶层 entry，不要求预览能绘制。以前这两种情况都会报“预览目标已变化”。
- `add_entry`、`duplicate_entry`、`remove_entry` 接受请求顶层的 `embeddedPath`，作用于那个内嵌文件；`duplicate_entry` 和 `add_entry` 的回复都带 `selection`（内嵌时含 `embeddedPath`）。`restore_entry` 和组备注仍只用于顶层。
- `EfxFieldValues`（LiveEditor 公开类）按 `set_fields` 的路径读写可编辑字段，供语料工具和后端共用。

## 验证

- `tools/Re4StructureTests`（12 项，用 PreviewEditorSession 直接驱动后端）：
  - `add_attribute` 写入默认字段并逐项读回；未知字段整体拒绝。
  - `add_entry` 一个修订、能选中、属性与模板一致；新建的 Billboard3D entry 在 re4.exe 的发射器里第 45 帧有粒子。
  - 删除前面的 entry 后预览目标跟着移动，撤销后复原。
  - 撤销正在编辑的副本后，预览改到最近的 entry。
  - 创建副本、复制粘贴 entry、删除其他 entry。
- 窗口（用鼠标实际操作）：
  - 复制 Life、在同一 entry 粘贴弹出替换确认、删除 Life、再粘贴回来。
  - 从“entry 操作…”新建 Billboard3D entry，单独显示是一团烟。
  - 删除当前 entry 时确认框提示改为编辑 [30]。
  - 节点图右键 entry 节点创建副本。
- 材质参数（`tools/Re4GenericEditorTests` 末段）：在 TypeMesh entry 上按参考材质添加一个参数和一个贴图、重复添加被拒、删除、撤销；带 TypeMeshClip 的 ripple 文件里，被曲线驱动的参数不能删。窗口截图核对了 acid_dead 第 12 个 entry 的 4 条覆盖（BaseMetalMap、NormalRoughnessMap、Metallic、Roughness）全部按名字显示、与参考材质一致。
- 内嵌 entry（ao_chfa damage_0000，Action 0 的 PlayEmitter 内嵌 3 个 entry）：`tools/Re4GenericEditorTests` 选中内嵌 entry 后属性表与文档树一致、预览清单仍指向顶层 entry；改一个 float 字段是一个修订并重新发布；切回顶层后，内嵌 entry 上读到的草稿被拒；不存在的内嵌 entry 选中被拒且目标不变；撤销后字段复原。`tools/Re4McpTests` 新增 7 项：`embeddedEntries` 列出路径，`select_entry` + `embeddedPath` 选中、`inspect` 读 Life、`set_fields` 后预览显示新修订并读回、回到顶层、拒绝不存在的目标（共 76/76）。
- Action（同一文件的 Action 0）：`tools/Re4GenericEditorTests` 选中 Action 后列出 PlayEmitter、预览清单不变；目录里 PlayEmitter 为已有、PlayEfx 可加、Life 为 entry 专用；删 PlayEmitter、加带默认字段的 PlayEfx 各一个修订，两次撤销后 PlayEmitter 和内嵌 entry 都回来。`tools/Re4McpTests` 再加 5 项（`status.preview.actions`、`actionIndex` 选中、`attribute_types` 的 Action 目录、同时给两个参数被拒、回到顶层），共 81/81；MCP 自检 107 项通过。
- 内嵌文件里的 entry 级操作：`tools/Re4GenericEditorTests` 在 damage_0000 的内嵌文件里新建（回复带路径、能选中）、创建副本、复制粘贴、删除，顶层 entry 不变；`target` 里带路径的 entry 操作被拒（以前会落到同位置的顶层 entry）；内嵌文件只剩一个 entry 时拒绝删除；撤销全部复原。`tools/Re4McpTests` 再加 3 项（共 84/84）；`tools/Re4StructureTests` 13/13，MCP 自检 107 项。后端自测 `EmbeddedActionTests` 加了对应用例，但它依赖的 Wilds 测试文件不在本机，没有运行。
- 节点图和属性面板的点击还没有用鼠标实际试过。
- 回归：MCP 端到端 57/57、多 entry 27/27、通用编辑通过。LiveEditor 的自检这次没有运行（之前因缺少 Wilds 测试素材会提前中止）。

## 未做

- Action 本身的新建、副本、删除、复制。
- 内嵌 entry 不加入内嵌文件的 effect group。
- MCP 还没有 entry 级操作（新建、删除、副本）的工具，也没有材质参数覆盖的工具。
- 预览不绘制 TypeMesh。
- 新 entry 加入 effect group。
