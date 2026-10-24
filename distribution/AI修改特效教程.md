# 给 AI 的 MotionVfx Live 使用教程

## 接入与准备

先按 [安装说明](../README.md) 与 [使用教程](../docs/tutorials.zh-CN.md) 启动后台和游戏内 0.8.5 界面，使用包内 MCP 0.1.2。EXE 安装器会在安装目录顶层生成 MCP 配置；从安装完成页打开安装目录即可找到。ZIP 便携包也可运行“生成MCP配置.cmd”重新生成配置。支持 JSON 配置的客户端，合并 `mcp-config.json` 中的 `mcpServers.motionvfx`；支持相应 TOML 配置的客户端，加入 `mcp-config.toml` 的服务器段。保留原有其他服务器配置。

D2D 改版不改变现有 MCP 编辑协议。外部素材先通过界面加入预缓存，AI 再将其 art/ 别名写入对应资源字段。现有 MCP 没有新增录屏、任意磁盘导入或动作/PFB 槽位写入工具；后者目前通过界面操作。不要把回放图片当成游戏时间已经倒退的证据。新增动作槽位的触发效果仍需实机验收。

服务为本地 stdio，启动命令是包内 `mcp/VfxStudio.Mcp.exe`，参数为 `--config` 与包根目录 `runtime-config.json` 的完整路径。客户端应管理 MCP 进程；手工双击 MCP EXE 不会出现编辑窗口。后台与游戏必须另行启动。工具列表亦保存在包内 `mcp-tools.json`。

协议使用 MCP 2025-11-25，并兼容 2025-06-18 和 2024-11-05 的初始化方式。参考：[官方协议](https://modelcontextprotocol.io/specification/2025-11-25)。服务只暴露下表操作，不提供任意 Lua、脚本、命令或 SDK 执行接口。

## 工具表

| 工具 | 用途 | 关键参数 |
|---|---|---|
| `motionvfx_status` | 当前文档、修订、引用替换、场景、草稿状态 | 无 |
| `motionvfx_capture_start` | 开始记录角色动作触发的资源 | 无 |
| `motionvfx_capture_stop` | 结束记录 | 无 |
| `motionvfx_list_effects` | 列出已确认角色归属的资源 | 无 |
| `motionvfx_list_actions` | 列出观察到的动画片段及其特效证据 | 可选 `actionId`，须来自返回列表 |
| `motionvfx_open_effect` | 打开捕获资源的有效游戏安装版本 | `path`，列表返回的完整逻辑路径 |
| `motionvfx_inspect` | 查看块、属性、组、字段和颜色/曲线描述 | `target`, `expectedRevision` |
| `motionvfx_set_field` | 提交一个字段的值 | `target`, `path`, 字符串 `value`, `expectedRevision` |
| `motionvfx_set_color` | 原子提交一组 RGBA | `target`, `path`, `values:[r,g,b,a]`, `expectedRevision` |
| `motionvfx_set_group_note` | 设置分组的中文备注，只写独立 JSON | `index`, `note`, `expectedRevision` |
| `motionvfx_rename_group` | 旧接口兼容别名，同样只改备注 | `index`, `name`（备注内容）, `expectedRevision` |
| `motionvfx_undo` / `motionvfx_redo` | 撤销/重做一次文档事务 | `expectedRevision` |
| `motionvfx_save_copy` | 保存独立副本 | `expectedRevision`，可选 `path` |
| `motionvfx_apply` | 应用到真实角色资源 | `expectedRevision` |
| `motionvfx_restore` | 停止记录与预览，恢复资源及场景 | 无 |
| `motionvfx_scene_time` | 慢放、暂停、继续 | `operation:"speed"/"pause"/"resume"`；speed 时附 `speed` |

所有编号从 0 开始。`target` 形如 `{"kind":"entry","index":0,"attributeIndex":2}`；查看整个块时省略 `attributeIndex`，组使用 `{"kind":"group","index":0}`。不要根据本教程示例猜真实文件的编号或路径。

## 标准操作流程

1. 调用 `motionvfx_status`，确认后台就绪、没有未完成的操作和人工草稿。人工已打开正确文件时，保留当前文档，直接检查其来源和内容。
2. 没有目标记录时，调用 `capture_start`，请用户做指定动作，再 `capture_stop`、`list_effects`。不能用名字相似、时间接近或效果颜色猜角色归属。
   需要定位动作时调用 `list_actions`，保留武器、Bank、层、MotionID 与帧信息。`direct_motion_request` 是直接请求证据；`contemporaneous_only` 只表示时间邻近，不能作为该动作调用特效的证明。
3. 使用列表中完整路径 `open_effect`。文档的 `sourceKind` 应为 `game_files`；检查 `gameSource` 的实际来源、目标路径和内容哈希。不要从项目素材目录挑同名文件冒充当前游戏版本。
4. 从返回文档的 `entries`、`groups`、属性清单取得正确编号，调用 `inspect`。先记录实际旧值，再修改。`path` 必须使用检查返回的字段路径或 `colorGroups` 路径，不编造名称。
5. 所有要求 `expectedRevision` 的调用必须用最近返回的 `document.revision`。一次成功的 EFX 参数修改后修订会变化，下一步重新使用新值。分组备注独立保存，不改变该修订。旧修订被拒绝时先重读状态，不自动套用旧输入。
6. 参数提交后再次 `inspect` 核对值。需要保留时调用 `save_copy` 并记录 `outputPath`。保存不代表已经应用到游戏。
7. 用户任务包含游戏试改时调用 `apply`。返回 `accepted` 只表示替换流程已接受/完成；请用户做原动作，再 `status`。只有 `verification.phase` 为 `created`，且其路径对应这次发布的 `generatedPath`，才报告角色确实创建新版本。画面是否符合预期仍需实际观察或用户确认。
8. 完成后调用 `restore`，确认 `restored:true`。必要时再查 `status`，确保无待还原引用和场景接管。失败时报告具体失败，保留现场，不能把 Reset 后的零计数当还原证据。

## 三个修改示例

以下数字是操作格式示例，不是某个文件的固定位置。应先检查真实目标。

**颜色：** `inspect` 返回显式 `colorGroups` 后，使用该组的 `path` 和目标提交 `values:[255,80,20,255]`。四通道以一次事务提交，撤销一次全部恢复。现有 rgba8 使用原始 0–255 整数，不发送 0–1 小数；亮度/强度是独立字段，只有用户要求时才改。

**分组备注：** 从 `document.groups` 取得目标的 `index`、原生 `name`、当前 `note` 与成员数组，调用 `set_group_note`，例如 `{"index":0,"note":"角色脚下的蓝色火焰","expectedRevision":7}`。备注支持中文，以独立 JSON 自动持久保存；`displayName` 优先显示备注，原生 `name` 保持不变。不同组可以使用同一备注，空字符串清除备注；保留输入空格，不截断内容。备注限单行有效 Unicode、最多 4096 个 UTF-8 字节。

备注不会写入 EFX，不改变原生组名、哈希、成员、`revision`、`dirty` 或撤销/重做记录。回读确认备注和原始数据；恢复备注时再次提交原 `note`，不能使用 `undo`。仅修改备注不需要保存 EFX 或应用到游戏。旧 `rename_group` 的 `name` 参数现在也是备注，禁止将它解释为原生改名；不要尝试通过 `set_field` 修改原生组名或名称哈希。

**曲线：** 先从 `inspect.expressionCurves` 或 `curves` 确认通道与当前常数，再用 `set_field` 修改已经返回的具体字段。两个通道可能都有 `from`，必须按完整路径区分。不要将表达式外部变量直接理解为帧时间。此版 MCP 不提供新增公式节点、增删关键帧或复杂集合扩容工具，不能声称已经通过普通字段工具完成这些结构操作。

## 慢放与暂停

依次调用 `scene_time`，参数例如 `{"operation":"speed","speed":0.1}`、`{"operation":"pause"}`、`{"operation":"resume"}`。暂停时更改速度只改变继续播放的速度。最后用 `restore` 交还场景时间控制；设置 1× 不等于退出接管。

当前版本暂停时同时控制场景时间、全局时间与引擎的特效更新开关，以处理仅将速度设为零仍会更新的拖尾。继续时恢复原特效更新状态、全局倍率和所选场景速度；完成后调用 `restore` 释放剩余接管并核对还原结果。接口回读不能替代拖尾形状和贴图静止的实际视觉确认。

`globalTimeRestorePending` 与 `effectUpdateRestorePending` 分别表示仍有全局时间或特效更新状态需要还原。若特效更新开关、时间状态或场景引用还原失败，应保留错误与恢复记录，不重置脚本清除现场。画面录像的倒放由界面完成，不是本 MCP 的负速游戏时间功能。

## 错误处理规则

- `drafts_present`：请用户先提交或还原界面草稿，不绕过保护直接写后台邮箱。
- `busy`：等当前动作结束后读取状态，不并行发送多个修改。
- `stale_revision`：重读当前文档与目标，再决定修改。
- 来源或场景改变：保留编辑副本，重新记录确认来源；不强行沿用旧对象或相似路径。
- 超时或返回 `uncertain:true`：记录 requestId，不自动重复修改、应用或恢复。原请求可能已经执行。旧请求未答复时同一通道的状态工具也会被阻止，应先在游戏界面观察或停止并还原，等原操作最终答复后再调用状态工具。
- 捕获旧记录、游戏未运行、Lua 未就绪：修复连接后继续，不把离线模拟结果当实机成功。

MCP 通过独立请求通道交给 Lua 主线程执行，串行处理并记录请求身份。AI 不应自行编辑 `request.json`、`ai-request.json` 或去重记录，也不应通过删除日志来重试不确定操作。

去重记录保存在 `ai-journal.json`，普通更新会保留它。记录损坏、写入不能核验或达到 8192 个请求上限时，AI 修改会停止并明确报错；不要删除记录后重放旧请求。

## 可直接复制给 AI 的任务模板

> 使用 MotionVfx MCP 修改我当前角色动作所触发的特效。先读取状态及已捕获资源，确认完整路径和来源，再检查实际字段；保留我的未提交输入和其他修改。按我的要求只改目标参数，记录旧值与新值，使用当前 expectedRevision，每次写入后回读。分组标签只用 set_group_note 写中文备注，禁止改 EFX 原生组名或哈希；备注单独保存，恢复时重新设置旧备注。修改 EFX 参数时保存独立副本。若本次要求游戏试改，则应用后让我重做原动作，检查是否实际创建新版本，等我确认画面；不要只凭应用成功宣布视觉验收。结束后恢复资源与场景并确认完成。遇到不确定超时不要自动重放。最后报告文件、块、属性、字段、改动、保存位置、实际验证结果和未完成事项。

再补上具体要求，例如：“把我已打开特效中的目标火焰改为青色，保持透明度和亮度；先列出你检查到的颜色字段，再进行修改。”

## 致谢与来源

组件作者、上游代码与开发参考见 [CREDITS](../CREDITS.md)；具体分发组件许可见 [第三方说明](第三方说明.md)。
