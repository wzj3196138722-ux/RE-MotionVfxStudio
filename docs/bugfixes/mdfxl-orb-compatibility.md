# MDF-XL 导致悬浮球停止响应的兼容处理

用户已通过脚本排查确认 MDF-XL 是冲突对象。**MotionVfx Live 0.8.6 正式安装程序已集成针对 MDF-XL 1.5.36 的兼容处理**，安装或覆盖升级编辑器时自动检测并处理；无需另外下载修复包。安装程序不替换用户的 REFramework。

## 机制与证据范围

REFramework 的 `ScriptState::on_frame` 用同一个异常边界遍历共享 Lua 状态的逐帧回调。前面的回调抛出错误时，本轮后续回调会被跳过。

MDF-XL 的 `re.on_frame` 调用快捷键更新和材质主更新，未在入口保护异常。默认材质初始化完成后的武器、护符可见性更新包含直接的组件 `get_GameObject()` 调用；这些分支不在其 coroutine.resume 的错误保护内。SDK 调用失败可以转为 Lua 错误。

MotionVfx Live 的输入和工作台更新位于后面的逐帧回调，而 D2D 可以继续绘制缓存内容。这与用户诊断中 D2D 有回调、普通更新和工作台更新为零相符。

用户日志没有对应 MDF-XL 的完整 Lua 栈，所以不能把其中某一行或某一武器组件定为唯一触发源。补丁会保存该错误栈，以便后续修复 MDF-XL 本身的数据或引用问题。

## 实现

正式安装程序调用内置的 `compatibility/MDFXL/Apply-MdfxlCompatibility.ps1`，将兼容代码前置到现有 MDF-XL 脚本中。对应源码位于 `tools/MdfxlCompatibility/MDF-XL-frame-guard.lua`。新增的 `local re` 代理只属于 MDF-XL 这一 Lua chunk，只包装 `on_frame`；全局 `re` 和其他脚本的注册行为保持原样。

- 正常更新的参数、返回值与后续行为保留。
- 失败时捕获 Lua 栈、返回 nil，让后续脚本继续更新。
- 下一帧仍尝试 MDF-XL 更新，不返回注销回调的 STOP 标记。
- 兼容层错误日志每个回调最多 10 秒一条，日志函数自身异常也被保护。游戏原生接口自身产生的日志不受这个限流控制。
- 当前核对版本的 MDF-XL 逐帧入口不包含 ImGui 绘制，避免捕获异常后留下未配对的界面栈。

这修复的是异常传播导致的编辑器失去响应，不声称修好了 MDF-XL 内部失效引用，也不处理其启动阶段异常、原生进程崩溃或无限循环。

## 安装、更新与备份

- 游戏关闭后，正常运行 **0.8.6 正式 EXE 安装程序**。未安装 MDF-XL 时跳过该步骤；已安装且符合支持条件时自动处理。
- 编辑器文件完成安装后才进入兼容步骤。编辑器文件复制阶段的失败回滚不会提前修改 MDF-XL；MDF-XL 及其备份也不进入编辑器旧文件清理清单。
- 首次修改前保存原始字节备份 `reframework/autorun/MDF-XL.lua.motionvfx-compat.bak` 和 SHA-256 校验记录 `MDF-XL.lua.motionvfx-compat.json`。重复安装不会叠加兼容代码或替换原始备份。
- 检测到未知版本、安装后自行修改的脚本或损坏的校验记录时，保留当前文件，报告“编辑器已安装，但 MDF-XL 兼容修复未应用”。详细原因写入编辑器安装目录的 `install-*.log`；该状态不代表兼容问题已解决。
- 维护者需要还原时，关闭游戏，在安装版本目录的 `compatibility/MDFXL` 中运行 `Apply-MdfxlCompatibility.ps1 -GameDirectory '<实际游戏目录>' -Restore`。工具先校验当前文件与备份，再按原始字节还原；不覆盖无法验证的后续修改。操作说明见 [兼容组件说明](../../tools/MdfxlCompatibility/README.zh-CN.md)。

## 验证

- `tools/LuaValidation/mdfxl-compat-tests.lua`：33 项通过。包括完整 MDF-XL 原文和前置补丁的编译、局部接口隔离、参数和返回值保留、故障恢复、日志限流及日志写入失败。
- `tools/LuaValidation/mdfxl-orb-native-tests.lua`：176 项通过。使用真实 ImGui、生产工作台代码和实际 MDF-XL 最终回调，覆盖不同分辨率及四个屏幕边缘。未加补丁时复现更新中断和点击失败；加补丁后点击、拖动、恢复均通过。
- 安装工具的回归入口为 `tools/MdfxlCompatibility/Test-MdfxlCompatibility.ps1`。Windows PowerShell 5.1 下 120 条断言、58 次调用通过；PowerShell 7.6.5 下 43 条断言、13 次调用通过。验证备份、校验、重复安装、还原、拒绝覆盖用户更新及目录连接拒绝，仅操作项目内临时目录。
- 本机真实 MDF-XL 源码仅复制到隔离目录，实际修补、重复运行与逐字节还原均通过。修正了测试发现的 PowerShell 5.1 原子替换空参数转换问题，以及 PowerShell 7 的 JSON 数字、日期类型差异。
- 正式安装器的集成回归位于 `tools/Installer/InstallerTests.cs`，覆盖自动调用、未知版本提示、原始备份保留、重复安装、用户后续修改保护、无 MDF-XL 时跳过和安装失败前不修改第三方脚本。正式 EXE 的执行结果应以对应构建的安装测试报告为准，以上 Lua 和脚本测试不代替 EXE 验证。

本机已安装兼容处理，并核对当前脚本及原始备份的 SHA-256。此轮未启动游戏；受影响电脑上的实机验证尚未完成。

## 交付范围

推荐交付物为包含兼容处理的 **MotionVfx Live 0.8.6 正式 EXE 安装程序**，可覆盖升级旧版编辑器。其中的 MDF-XL 兼容组件仅包含本项目新增的兼容代码、内部安装/还原工具及说明，不包含 MDF-XL 原始代码、配置、预设或素材。安装前要求游戏关闭；原始脚本的字节备份和 SHA-256 校验用于安全还原。

感谢 [SilverEzredes / MDF-XL](https://github.com/SilverEzredes/MDF-XL) 和 [praydog / REFramework](https://github.com/praydog/REFramework)。
