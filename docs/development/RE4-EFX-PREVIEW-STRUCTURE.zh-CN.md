# RE4 离线预览：窗口里的结构编辑

预览窗口可以直接增删、复制粘贴属性和 entry，不再只能靠 MCP。每个操作是一个新修订，可以撤销。属性面板有未应用的草稿、编辑器正忙或处于压力模式时，这些操作都不可用。

编辑和预览不绑定：文件里任何顶层 entry 都可以作为编辑对象，包括预览还画不了的类型（Mesh、GPU 类型、StrainRibbon 等）和没有 Type 的 Root。预览只绘制它支持的 entry；所选 entry 不绘制时，主面板给出原因，属性照常编辑。节点图里不绘制的 entry 显示为灰色节点，但同样可以单击打开、右键操作。

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
- 回归：MCP 端到端 57/57、多 entry 27/27、通用编辑通过。LiveEditor 的自检这次没有运行（之前因缺少 Wilds 测试素材会提前中止）。

## 未做

- Action 和内嵌 entry 的结构编辑。
- MCP 还没有 entry 级操作（新建、删除、副本）的工具，也没有材质参数覆盖的工具。
- 预览不绘制 TypeMesh。
- 新 entry 加入 effect group。
