# RE4 离线预览：属性目录与拖拽添加

“属性目录”面板按类别列出 RE4 的全部属性类型，并针对当前 entry 标出哪些能加、哪些常用。把属性拖到属性面板或节点图里的 entry 上就会添加；双击则加到当前 entry。添加走后端已有的 `add_attribute`，每次是一个新修订，可以撤销。

## 规则来源

能否添加以后端为准，目录只是提前标出来。后端（`EFXEntryBase.AddAttribute` 和 `EditorEngine` 的 `add_attribute`）拒绝以下情况：

| 状态 | 含义 |
|---|---|
| 已有（present） | entry 已经有这个类型，同类属性只能有一个 |
| 第二个 Type（secondType） | entry 已有 Type* 属性 |
| 缺基础属性（missingBase） | Expression / Clip 类属性的基础属性不在，例如没有 EmitterColor 时不能加 EmitterColorClip |
| Action 专用（actionOnly） | PlayEmitter、PlayEfx 只能挂在 Action 上 |
| 不能新建（notCreatable） | 编辑器库里没有这个类型的类，不知道字段结构，例如 TypePolygonTrailExpression |

“缺基础属性”和“是不是 Type 属性”按属性类实现的接口判断（`IExpressionAttribute`、`IMaterialExpressionAttribute`、`IClipAttribute`），与后端一致，不按名字猜。例如 Transform2DClip、PtVelocity2DClip 的类不是 Clip 接口，后端不要求基础属性。

后端允许的属性再按 RE4 语料分档：

| 状态 | 含义 |
|---|---|
| 常用（common） | RE4 里有同 Type 的 entry 带这个属性；右侧百分比是同 Type 的 entry 中带它的比例 |
| 未见过（unseen） | 同 Type 的 entry 都没用过，其他 entry 用过 |
| 未使用（unused） | RE4 的文件里从未出现 |
| 仅 Root（rootOnly） | Root 属性出现在普通 entry 上 |
| Root 不用（notOnRoot） | 普通属性出现在 Root entry 上 |

后四档可以添加，但游戏是否支持没有依据，默认不列出。同 Type 的 entry 有 95% 以上都带、而当前 entry 缺少的属性用橙色标出，并在面板顶部列出。没有 Type 的普通 entry 列出各种 Type 属性，按 RE4 里用该 Type 的 entry 数排序。

Root entry 是文件的第一个 entry，名为 Root、没有 Type，放全局控制属性：UnitCulling、EmitterPriority、DrawOverlay、RenderTarget。RE4 的 1422 个 Root 只带这四种，普通 entry（含内嵌）从不带它们，两边互不混用。这四种单列为 Root 类别。没有 Type、且名为 Root 或已带 Root 属性的 entry 按 Root 处理：只列 Root 类别；普通属性和 Type 属性标为“Root 不用”。

语料统计由 `tools/Re4AttributeCatalog` 生成，写入 `VfxStudio.Re4Render/Data/re4-attribute-usage.json`，作为资源嵌入程序。统计范围是游戏解包目录 `natives/stm` 下全部 `.efx.3539837`（3946 个，全部能解析），按“顶层 entry 的 Type / 内嵌 entry 的 Type / Action”分组计数，目录合并顶层与内嵌 entry。统计只读打开文件。游戏更新后重新运行：

```bash
dotnet run -c Release --project tools/Re4AttributeCatalog
```

部分统计：Billboard3D entry 23838 个（含内嵌）；没有 Type 的 entry 只带 UnitCulling、EmitterPriority、RenderTarget、DrawOverlay；Action 只带 PlayEmitter（475）和 PlayEfx（61）。RE4 版本的 155 种属性中 117 种在语料里出现过。

## 界面

- 类别：Root、类型、发射、变换与运动、生命与动画、外观、UV、淡化与遮挡、粒子行为与碰撞、Action、其他。Expression、Clip、DelayFrame、Modifier 变体按名字归到基础属性下（去掉后缀，例如 Transform3DModifier → Transform3D），和基础属性同一类别，紧跟在它后面缩进显示。类别内按组排序：组里用得最多的那个属性的次数决定组的位置，基础属性排在组首。以前变体按自己的次数单独排序，会落到无关属性下面（例如 Transform3DModifier 显示在 Attractor 下）。
- 搜索框按名字过滤，搜索时列出所有匹配项（包括不能添加的）。“RE4 未用的组合”“不可添加的”两个勾选框控制默认隐藏的条目。
- 悬停显示原因：使用比例、缺哪个基础属性、为什么不能加。
- 拖动：属性面板整个窗口都可以放下，加到当前 entry。节点图里放到顶层 entry 的节点上，加到那个 entry，并把它切换为当前 entry。放不下的地方（Action、内嵌 entry、预览不支持的类型、目录判定不能加的属性）拖到上面时提示原因。
- 添加成功后，新属性在属性面板中打开。
- 属性面板有未应用的草稿、编辑器正忙或处于压力模式时不能添加，目录顶部显示原因。
- 停靠：默认布局里目录在主面板下方。以前保存过布局的，第一次打开时目录作为标签页停在主面板旁边；“重置布局”恢复新的默认布局。主面板有“属性目录”勾选框可以重新打开它。

## MCP

`attribute_types` 的结果新增 `catalog`：`entryType`、`root`、`sameTypeEntries`、`corpusFiles`，以及 `items`（每种属性的 `type`、`category`、`status`、`canAdd`、`uses`、`usual`、`base`）。状态名与上面两张表的英文名相同。添加仍用 `add_attribute`。

## 验证

- `tools/Re4MultiEntryTests` 第 10 节：Billboard3D entry 和没有 Type 的 entry 上的各类状态；并对 RE4 版本的每一种属性类型，在内存里建同样的 entry 调用 `EFXEntryBase.AddAttribute`，结果与目录的“能否添加”逐一相同。这项检查最初发现了按名字推断的三处错误（两个 Clip 不要求基础属性、TypeBillboard3DMaterialExpression 需要基础属性）和不能新建的类型，现已改为按类判断。
- `tools/Re4McpTests`：`attribute_types` 返回当前 entry 的目录，Life 为 present、PlayEmitter 为 actionOnly、TypeMesh 为 secondType。
- 窗口（用鼠标实际操作）：
  - 从目录拖 RotateAnim 到属性面板，加到 entry 13（r2），面板打开 RotateAnim。
  - 拖 Blink 到节点图里 entry 2 的节点上，当前 entry 切到 2，Blink 加入并打开（r3）。
  - 双击 EmitterShape3D，加到当前 entry；撤销一步即移除。
  - 删除布局文件后启动，默认布局里目录在主面板下方；保留旧布局文件启动，目录作为标签页停在主面板旁边。
- 没有测试的：拖到不能放的节点上时的提示只看过代码，没有截图确认。

## 未做

- 给 Action 和内嵌 entry 添加属性（后端支持 `embeddedPath`，窗口还没有选中它们的方式）。
- 新建 entry、在节点图里拖出新 Action。
- （已完成）新加属性的字段改为取自 RE4 的实际属性，见 RE4-EFX-PREVIEW-STRUCTURE。
