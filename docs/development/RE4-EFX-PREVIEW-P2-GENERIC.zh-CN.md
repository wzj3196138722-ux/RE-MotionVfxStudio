# RE4 离线预览 P2：通用属性面板

2026-11-15。首版已实现当前选中 Billboard3D entry 的已有属性列表、通用字段表单、同属性批量提交，以及撤销/重做、保存重开和预览修订同步。

## 使用

```powershell
dotnet run -c Release --project VfxStudio.Re4Preview -- --fixture bb2
```

右侧“EFX 属性编辑”：

1. 从下拉列表选择属性，例如 TypeBillboard3D、Transform3D、Spawn、Life、UVSequence。所列属性来自当前文档。
2. 按字段路径分组浏览，也可搜索、清空搜索或隐藏只读字段；每页最多显示 64 项。切换属性会清空搜索，避免沿用上一属性的过滤词造成空白。
3. 数字和字符串使用精确文本输入，布尔值使用勾选框，枚举使用下拉列表；枚举保留“精确值”输入，可提交后台接受的原始数值或组合。显式 RGBA8 颜色组使用颜色控件，不把任意向量当颜色。
4. 点“应用本属性修改”提交全部变化字段，每次最多 64 个字段、一次撤销步骤。多个区间分量可以一起提交，颜色四通道计为四个字段。
5. “放弃草稿”恢复文档值。未应用时禁止切换属性、打开副本或重新打开源文件，防止静默丢弃草稿；保存只保存已应用内容。遇到外部新修订时保留原草稿并停用提交，可明确“丢弃草稿并刷新”。

### 字段说明、标志位与成对数值

- 字段名后面灰字显示单位和成对数值的读法；悬停显示含义、公式、单位和“未确定：”部分，空一行后是字段路径与类型。只有说明、没有单位的字段显示 `(?)`。
- 成对数值按类型显示：CenterSpread(I) 为“中心 ± 幅度”，MinMax(I) 为“最小 ~ 最大”，MinMaxExclusiveI 为 `[最小, 上限)`，MinExtra(I) 为“最小 + [0, 增量]”，InnerOuter、StartSpan、ClampRange、AnchorPair 各有标签。两个输入框之间显示对应符号。仍是 via.Range / via.RangeI 的字段以橙色标出“s / r（用法未确认）”，两者都是先 s 后 r。
- 有位说明的 u32 标志字段显示为勾选框和下拉框（互斥位段，如 Life 保持模式、UVSequence 的 PlayMode / FlipU / FlipV / Reverse / Rotate90）。没有说明的已置位位显示为“位 N（未列出）”，其余未列出的位折叠在“其他位”里；决定字段是否存在的位（PtCollision / PtColliderAction 位 0、1）只能在“精确值”里改。勾选只改草稿，应用时仍走 `set_fields`。

说明数据在 `VfxStudio.LiveEditor/FieldSemantics.re4.json`（嵌入资源），按属性类型名和字段路径索引；键也覆盖它下面的路径（`AppearFrame` 覆盖 `AppearFrame.min`）。`ContentInspector` 只对 RE4 文件把它附到 `inspect` 每个字段的 `semantic`（`help`、`unit`、`status`、`bits`），MCP `inspect` 原样带出。成对数值的读法由前端按字段的 `ownerType` 判断，不进 JSON。新增说明时以 `VfxStudio.Core/Formats/EFX` 的注释和 [RE4-EFX-RUNTIME.zh-CN.md](RE4-EFX-RUNTIME.zh-CN.md) 为准，没有依据的字段不写。

数字不经过 float/double 统一转换，避免 Int64/UInt64、位标志或未知数值丢失精度。底层类型、版本条件、只读状态、数字解析和区间合法性均由后台决定；失败时文档修订与历史不变，窗口草稿保留供修正。

## 实现与边界

- `PreviewEditorSession` 从现有文档树读取属性列表，通过 `inspect` 读取 `fields`、`colorGroups` 和曲线数量。表单快照绑定文档 ID、修订、entry 位置及属性下标。
- `PreviewPropertyDraft` 只保存文本值与变化集合，不维护第二份 EFX 文档。变化集合缓存后复用，大字段列表不会在每帧重新分配全部修改记录。
- `ApplyFieldsAsync` 在已有会话请求锁内检查目标和修订、只读字段、重复路径与 64 字段上限，然后通过现有 `set_fields` 原子提交。后台仍执行完整序列化、再读校验、回滚和撤销。
- 成功应用、撤销/重做或外部修订后，干净表单重新 inspect，以刷新条件字段。失败的 inspect 不会每帧自动重试；可点“刷新属性”重试。过期读取被拒绝，后端已推进时预览仍跟进最新有效清单。
- 只读字段显示原因，包括版本不适用、派生元数据、缓存、空复杂节点和需结构编辑的集合操作。颜色组只提交实际变化通道，隐藏重复的 packed rgba 别名，避免同一颜色出现两套相互覆盖的草稿。
- 当前仍只渲染单 entry 和已支持的 Billboard3D 外观。格式合法的字段未必已有对应渲染效果；例如设置未支持的混合方式会保留可编辑文档和旧画面、显示加载错误，允许撤销恢复。
- 现有数组/结构的可编辑叶字段可修改；属性增删、集合扩容、创建 null 节点、图形曲线编辑、资源浏览器、多 entry 和拖入属性组装继续作为后续工作。MCP 接入见 [RE4-EFX-PREVIEW-MCP.zh-CN.md](RE4-EFX-PREVIEW-MCP.zh-CN.md)。

## 验证

```powershell
dotnet run -c Release --project tools/Re4GenericEditorTests
dotnet run -c Release --project tools/Re4PreviewP2Tests
```

通用集成测试使用真实 bb2，遍历 10 个属性（每属性 12–60 个描述字段）：

- 字段读取不增加修订或撤销记录；正确保留属性目标与只读原因。
- 颜色、ColorRate 与尺寸一次提交，只产生一次修订；尺寸和颜色变化进入实际原生粒子；撤销后同 tick 45 的原生数组 SHA256 恢复。
- NaN、UInt32 溢出、只读字段（客户端及原始后台请求）、反向 Life 区间整笔拒绝，修订与文件摘要不变。
- 旧修订、错误文档，以及打开副本后的旧草稿被拒绝；原本的草稿值没有自动重定基准。
- Boolean EnableGroupColor、Transform3D 的 RotationOrder 枚举、UVSPath 字符串和 Life 整数修改，经保存/重开回读一致。UInt64 最大值的草稿精度另以本地模型测试验证。
- 不支持的 BlendType 2 可被加载阶段拒绝，随后一次撤销恢复原文档。
- `inspect` 结果带出字段说明：Life.AppearFrame 的单位与 MinMaxI 读法、Life.Flags 的保持模式位段、Billboard3D 颜色头的位；JSON 里每个键都能对应到 RE4 属性的实际字段路径（拼错的键会让测试失败）。

最终通用测试记录：`test-output/re4-generic-089c0705300f48a6bf4fad776513d566/generic-result.json`。既有 P2 回归也通过，覆盖旧专用接口、其他 entry 不变、源文件保护、保存保护、发布重试及损坏快照恢复；输出在 `test-output/re4-generic-p2-regression-20261115.log`。

真实窗口已操作：

- 搜索 ColorRate，0.05 → 0.2，应用与撤销后均暂停在 tick 1765。
- 保留草稿 0.25 时外部发布 0.3：画面推进至 r4，旧草稿仍显示 0.25，提交停用；明确丢弃后刷新为 0.3。
- Life 最小值 50 / 最大值 40 被拒绝且草稿保留；修正为 50 / 60 后一次应用，随后一次撤销同时恢复 20 / 40。
- Boolean 勾选、枚举下拉选择、保存副本和打开副本；切换属性时搜索重置。打开副本后重新读取新文档字段并从 tick 0 播放。
- 播放中应用通用字段，加载接续 tick 368 并继续播放；播放中撤销接续 tick 1539，未回到 0。

窗口会话：`test-output/re4-generic-window-20261115-01/`、`test-output/re4-generic-controls-20261115-01/`、`test-output/re4-generic-playback-window-20261115-01/`。

## 延迟复测

沿用相同 bb2、1280 × 800、完整粒子分辨率、VSync、RTX 4060 Laptop 条件。每组预热 2 次、测量 20 次，计时仍到首帧 GPU 完成，不含交换缓冲、桌面合成和扫描。基准使用既有专用提交入口，通用表单自动读取/刷新实际运行。

| 路径 | tick | 中位数 ms | P95 ms | 最大值 ms |
|---|---:|---:|---:|---:|
| 窗口提交 | 45 | 169.6 | 178.5 | 186.3 |
| 窗口提交 | 300 | 168.6 | 178.9 | 186.6 |
| 窗口提交 | 900 | 160.4 | 162.2 | 168.5 |
| 外部修订 | 45 | 111.5 | 119.9 | 120.3 |
| 外部修订 | 300 | 111.9 | 120.2 | 126.9 |
| 外部修订 | 900 | 119.8 | 128.9 | 129.0 |

数据在 `test-output/re4-generic-latency-20261115-01/`。132 个修订 Key 唯一、无失败；120 次正式样本均低于 200 ms，全部保持目标 tick 和暂停状态。它不能保证所有字段、所有文件或任意 tick 均低于 200 ms；真实按钮操作中，曾观察到约 203 ms（播放中）和 211 ms（tick 1765 暂停）的单次修改。
