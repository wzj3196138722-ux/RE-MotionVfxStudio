# MDF-XL 与 MotionVfx Live 兼容修复

MotionVfx Live **0.8.6 正式安装程序已集成本兼容处理**，用于 MDF-XL 1.5.36 更新出错、后续脚本不再收到逐帧更新，导致悬浮球可见但无法点击的情况。

## 使用方法

1. 关闭《怪物猎人：荒野》。
2. 运行 MotionVfx Live **0.8.6 的正式 EXE 安装程序**，选择包含 `MonsterHunterWilds.exe` 的游戏目录，正常安装或覆盖升级旧版编辑器。
3. 安装程序会自动检查现有 `reframework/autorun/MDF-XL.lua`。符合支持条件时自动备份并应用兼容处理；未安装 MDF-XL 时自动跳过。
4. 正常启动游戏，进入场景，测试编辑器隐藏后再点击悬浮球打开。

**不需要下载单独修复包，也不需要复制脚本或运行额外安装命令。** 如果安装完成时提示“MDF-XL 兼容修复未应用”，编辑器程序仍已安装，但兼容问题尚未处理；点击“打开安装目录”查看 `install-*.log` 中的具体原因。未知 MDF-XL 版本、兼容处理后又改变的脚本或无法校验的备份不会被强行覆盖。

不要将 `MDF-XL-frame-guard.lua` 单独放进 `autorun`。它是安装程序使用的代码片段，安装程序会将其加入正确位置。

## 修复范围

只为游戏内现有的 `reframework/autorun/MDF-XL.lua` 添加局部异常保护。MDF-XL 正常更新时保持原行为；发生 Lua 错误时，保存真实错误栈，让编辑器等后续脚本继续更新，并在下一帧重试 MDF-XL。

这项兼容修复解决错误向其他脚本传播的问题，并不意味着 MDF-XL 内部导致错误的装备引用、数据或配置问题已经修好。如果它的某项材质功能仍然异常，需要根据新增错误栈继续定位。

正式安装程序会正常安装或升级 MotionVfx Live；其中的 MDF-XL 兼容步骤只处理现有的 `MDF-XL.lua`，不覆盖 REFramework、MDF-XL 配置或材质预设。不会停用 MDF-XL，也不会改写全局 `re` 接口。当前检查作者、版本和脚本入口布局，仅接受支持的 MDF-XL 1.5.36；遇到未知版本或不一致的备份状态会停止兼容步骤并明确提示。

## 验证与日志

编辑器界面版本为 **0.8.6**，其内置兼容层版本为 **1.0.0**。

启动后，可在游戏根目录的 `re2_framework_log.txt` 搜索：

```text
[MotionVfxLive MDF-XL compatibility] 1.0.0 active for MDF-XL on_frame.
```

若 MDF-XL 的更新再次报错，同一日志中会出现带有 `[MotionVfxLive MDF-XL compatibility]` 前缀的错误及 Lua 文件行号，每个回调最多每 10 秒记录一次。该错误记录不依赖 ScriptRunner 的“Log Lua Errors to Disk”开关。

离线验证包括：模拟 REFramework 的共享回调队列、使用本机 MDF-XL 的实际回调代码注入异常、运行真实 ImGui 的悬浮球点击和拖动测试。它们已通过，但不能等同于受影响电脑上的实机验收；该电脑的装备和运行状态仍需通过实机结果确认。

## 还原与更新

普通安装、升级无需手动操作。以下仅供维护者在移除兼容处理或更新 MDF-XL 前进行还原。

先关闭游戏。在编辑器安装目录的 `installation.json` 中找到 `package` 对应的版本目录，进入其 `compatibility/MDFXL` 文件夹，再执行：

```powershell
.\Apply-MdfxlCompatibility.ps1 -GameDirectory 'G:\steam\steamapps\common\MonsterHunterWilds' -Restore
```

把示例中的游戏路径替换为实际路径。源码维护者也可在本目录运行同一命令。

原始文件备份与校验记录位于：

- `reframework/autorun/MDF-XL.lua.motionvfx-compat.bak`
- `reframework/autorun/MDF-XL.lua.motionvfx-compat.json`

还原会校验当前文件，防止覆盖用户之后更新或自行修改过的脚本。保留这两份文件。如果之后需要更新 MDF-XL，建议先还原，再安装其新版本。重复运行正式安装程序不会重复插入兼容代码，也不会覆盖首次保留的原始备份。还原后重新安装 0.8.6 时，符合支持条件的 MDF-XL 会再次自动应用兼容处理。

## 致谢与来源

感谢 [SilverEzredes 的 MDF-XL](https://github.com/SilverEzredes/MDF-XL) 与 [praydog 的 REFramework](https://github.com/praydog/REFramework)。MDF-XL 的官方发布页为 [Nexus Mods](https://www.nexusmods.com/monsterhunterwilds/mods/125)。

正式发行版内的 MDF-XL 兼容组件只提供本项目的兼容代码和本地修补工具，不包含或重新分发 MDF-XL 原始脚本、预设或素材。
