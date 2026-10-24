# 0.8.7 performance validation / 性能验证

These are offline CPU measurements and regression checks. They exclude the game's simulation, GPU rendering and Windows IME candidate window, and must not be presented as measured in-game FPS.

以下是离线 CPU 测量及回归结果，不包含游戏模拟、GPU 渲染及 Windows 输入法候选框，不能换算为已验证的游戏帧率。

## Parameter UI / 参数界面

The comparison uses the same real Dear ImGui runner and production editor, with a 1,104-field PlayEmitter response. The baseline is the locally installed 0.8.7 Action hotfix before this optimization. Both versions retain all 60 InputText controls on the current page and have identical scroll ranges in each scenario. Earlier measurements using partially mocked geometry are excluded.

对照使用同一套真实 Dear ImGui 测试环境和生产编辑器代码，输入为含 1104 个字段的 PlayEmitter 属性。基线为优化前本机安装的 0.8.7 Action 修复版。两版当前页的 60 个输入框均保留，各场景的滚动范围一致。此前部分几何接口使用模拟值的测量不计入此对照。

| Scenario / 操作 | Before / 修改前 | After / 修改后 |
| --- | ---: | ---: |
| Scroll / 滚动 | 6.09 ms | 3.11 ms |
| Drag / 拖动 | 7.61 ms | 3.27 ms |
| Resize continuously / 连续缩放 | 12.42 ms | 3.24 ms |

Optimizations reuse font metrics and wrapping results, skip clipped display work, and batch native input styling. They do not omit input submission or change field pagination. The actual mouse/keyboard regression covers drafts, offscreen scrolling, Tab/Shift+Tab, dragging, resizing, insertion position and a single native caret.

优化复用文字测量及换行结果、跳过屏幕外显示处理，并合并输入控件样式设置；不省略输入控件提交，也不改变字段分页。真实鼠标和键盘事件测试覆盖草稿、滚动离屏、Tab/Shift+Tab、拖动、缩放、插入位置及唯一原生光标。

## Background work / 后台工作

- In a deterministic 600-frame, 10-second hidden-window sample, full history snapshots drop from 45 to 5. The current action still updates every frame. A hidden recording retains all 175 test events and shows them immediately on entering the history page. Capture hooks and sampling are unchanged; apply/restore ownership checks still request a fresh full snapshot.
- A large real EFX inspection decreases from 211.68 to 115.40 ms and allocated memory from 64.35 to 33.80 MB. These figures measure initial inspection, not response serialization. The bounded cache holds immutable JSON descriptions and invalidates on document identity, revision and relevant metadata changes.
- The in-memory EFX serialization path no longer writes a temporary `output.efx` file. Tests compare bytes with the prior writer and exercise concurrent serialization with that filename exclusively locked.

- 在模拟 600 帧、10 秒的隐藏界面场景中，完整历史快照从 45 次降至 5 次。当前动作仍逐帧更新；隐藏录制保留全部 175 个测试事件，进入列表后立即显示。捕获和采样不变，应用及还原仍重新读取完整归属证据。
- 大型真实 EFX 首次属性检查从 211.68 降至 115.40 毫秒，内存分配从 64.35 降至 33.80 MB。这些数字不包含响应序列化。缓存有容量上限，只保存 JSON 描述，并根据文档身份、修订和相关元数据失效。
- 内存 EFX 序列化不再额外写入 `output.efx` 文件。检查包含与旧写入器的逐字节对比，以及同名文件被独占锁定时的并发序列化。

## Validation boundaries / 验证范围

Local backend validation passed 603 checks across 520 unique real EFX files, 364 snapshot regressions and 25 memory-serialization checks. These game-content fixtures are not distributed. CI separately runs portable Lua tests, isolated installer tests and the packaged MCP's protocol/mailbox tests; release build provenance records the checks actually run.

本地后台验证通过 603 项检查、520 个不重复的真实 EFX 文件、364 项快照回归和 25 项内存序列化检查。游戏素材测试文件不随仓库或安装包分发。CI 另行运行便携 Lua 测试、隔离安装器测试及包内 MCP 协议／邮箱测试；发布构建记录会列明实际执行的检查。
