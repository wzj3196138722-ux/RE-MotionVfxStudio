# MotionVfx Live

<img src="game-plugin/images/MotionVfxLive/logo.png" alt="MotionVfx Live" width="112" />

**在《怪物猎人 荒野》中，直接编辑角色动作真正触发的特效。**

[English](README.en.md) · [使用教程](docs/tutorials.zh-CN.md) · [下载 0.8.7 安装器](https://github.com/wzj3196138722-ux/RE-MotionVfxStudio/releases/tag/v0.8.7)

MotionVfx Live 是基于 REFramework 与 REFramework D2D 的游戏内特效编辑器。先记录角色动作及特效调用，定位 EFX 文件，再编辑参数、热重载并在原动作中观察变化。预览由游戏自身的引擎完成，适合制作和调整特效 MOD。

这个仓库源自 RE-MotionVfxStudio。**当前发布产品是 MotionVfx Live 游戏内编辑器**；仓库中的早期桌面渲染原型不代表本版安装器的使用方式或效果保证。

## 可以做什么

| 工作 | 支持内容 |
|---|---|
| 找到目标特效 | 记录角色动作、动作编号和特效调用；根据实际来源核实动作与 PFB 的关联 |
| 修改 EFX | 按文件、Group、特效块、Action 内嵌块和属性浏览；修改可解析字段、颜色和曲线数值；删除块与属性；撤销、重做、单块还原 |
| 看清参数 | 属性和参数名称中英切换；RGBA、XY/XYZ、普通值与随机值组合显示；Group 中文备注 |
| 游戏内验证 | 应用真实资源并重新触发动作；慢放、暂停、继续；自定义调试暂停组合键 |
| 为动作加特效 | 选择真实 PFB、原有槽位模板、EFX Group 和人物/武器挂点，设置触发帧与寿命，再热重载 |
| 独立编辑 PFB | 按识别的组件类型提供装备资源槽或特效槽位编辑；支持导出，来源可核实时可热重载 |
| 使用外部资源 | 为已转换好的 Mesh、MDF、UVS、TEX、PFB、MOT、MOTLIST、EFX 建立 `art/` 虚拟路径与预缓存 |
| 检查画面 | 录制图像帧，正放、倒放、逐帧回看；动作/特效调用记录与画面录像分开 |
| 让 AI 协助 | 本地 MCP 提供检查字段、修改颜色/数值、应用、恢复与场景时间控制等 17 个工具 |

窗口可拖动、缩放、隐藏成贴边悬浮球。打开、导入和导出都使用游戏内文件窗口，支持双击目录/文件、中文路径及常用文件管理器操作。

## 安装与更新

需要 Windows x64、《怪物猎人 荒野》和适用于当前游戏版本的 [REFramework](https://github.com/praydog/REFramework/releases)。启用 REFramework 的 **Loose File Loader**。

1. 从 [Releases](https://github.com/wzj3196138722-ux/RE-MotionVfxStudio/releases/tag/v0.8.7) 下载 `MotionVfxLive-0.8.7-Setup.exe`。
2. 正常退出游戏，运行安装器，确认包含 `MonsterHunterWilds.exe` 的游戏目录。
3. 点击「一键安装」或「一键更新」。同版本可选择修复安装。
4. 安装完成后后台已启动；进入游戏，通过 **REFramework → Script Generated UI → MotionVfx Live** 打开编辑器。
5. 以后使用时，先双击桌面 **MotionVfx Live** 启动后台，再启动游戏。

安装器自带运行库、D2D 和输入组件，无需安装 Python、.NET SDK 或开发工具。游戏与 REFramework 不包含在安装器中。

更新支持以前的 EXE、ZIP 和手动脚本安装。

## 第一次修改

**记录动作 → 选择特效 → 提交参数 → 应用特效参数 → 再做原动作 → 导出或还原。**

从 [第一次修改真实触发的特效](docs/tutorials.zh-CN.md#first-edit) 开始。教程还包含：

- [颜色、曲线、分组与特效块](docs/tutorials.zh-CN.md#parameters)
- [给动作添加特效：模板、挂点与触发帧](docs/tutorials.zh-CN.md#action-slots)
- [独立 PFB 编辑](docs/tutorials.zh-CN.md#pfb)
- [外部资源预缓存与热重载](docs/tutorials.zh-CN.md#precache)
- [暂停、慢放和画面倒放](docs/tutorials.zh-CN.md#time-replay)
- [导出与还原](docs/tutorials.zh-CN.md#export-restore)
- [接入 AI MCP](docs/tutorials.zh-CN.md#mcp)

## 使用前要了解

- 通过捕获打开 EFX 时，读取的是**当前游戏安装目录中该完整路径的有效松散文件或 PAK 覆盖版本**。这不是内存导出，也不会用素材库里的同名文件替代。
- 编辑与游戏试改不会直接覆盖原始文件。需要永久保存或分发时，使用「导出文件」，并同时准备所引用的依赖。
- 修改同一个 EFX 资源会影响引用它的动作，不是只改当前一招。
- Group 备注只用于编辑器显示，不改变 EFX 原生分组名、哈希或调用关系。
- 「动作记录」「特效记录」是调用信息；「录制画面」才可重播。倒放的是录制的图像帧，游戏场景不会倒退。
- 参数中英文切换只改变显示。工作台按钮目前以中文为主，英文教程保留中文按钮名方便对照。
- 已实测过颜色、亮度、尺寸、寿命、表达式数值、真实资源替换与还原、双刀动作槽位触发、暂停/继续及拖尾暂停。**全部武器、复杂 PFB 类型、所有外部资源和游戏更新后的兼容性尚未全部验证**。加载成功不等于画面效果已通过验证。
- 热重载后不会保存到实际的文件中，该状态仅维持到下一次切换武器，需要自己手动导出覆盖文件并重新启动游戏
## 反馈与参与

欢迎通过 [Issues](https://github.com/wzj3196138722-ux/RE-MotionVfxStudio/issues) 报告问题或提出改进。请提供编辑器、游戏和 REFramework 版本，复现步骤、目标资源路径、预期/实际结果，以及必要的截图或错误日志。分享日志前先检查其中的本机路径等个人信息；无需上传游戏 PAK 或整套个人素材。

代码改动请说明解决的问题、测试方法以及是否经过游戏内验证。涉及二进制文件写入、引用替换或还原逻辑时，请覆盖失败与恢复路径。


源码编译与离线检查见 [开发文档](docs/development/BUILD.zh-CN.md)。

## 作者

**尝试给推搓推的推**

[b站](https://space.bilibili.com/437668597) · [踩蘑菇](https://www.caimogu.cc/user/1472999.html)

项目代码采用 [MIT 许可证](https://github.com/wzj3196138722-ux/RE-MotionVfxStudio/blob/main/LICENSE)，第三方组件继续遵循各自许可证。本项目不随发布包分发游戏本体、游戏 PAK、个人 MOD 或参考插件素材库。

## 致谢与来源

感谢以下作者和项目，让本工具的格式编辑、游戏内运行与开发验证成为可能：

- **shadowcookie / kagenocookie — [ReeLib / RE-Engine-Lib](https://github.com/kagenocookie/RE-Engine-Lib)**：文件格式解析与写入部分使用并修改了该项目代码；保留上游 MIT 许可与版权声明。
- **praydog 及贡献者 — [REFramework](https://github.com/praydog/REFramework)**：游戏内脚本、对象访问与插件接口。
- **cursey 及贡献者 — [REFramework D2D](https://github.com/cursey/reframework-d2d)**：Direct2D 界面、文字和图像绘制。
- **[REasy](https://github.com/seifhassine/REasy) 项目及贡献者**：使用 Wilds RSZ 类型定义；随包许可保留 Battlezone 的版权署名。
- **[ZstdSharp](https://github.com/oleg-st/ZstdSharp)、[xxHash .NET](https://github.com/uranium62/xxHash)、Microsoft .NET 与 Lua 的维护者**：压缩、哈希、运行环境和原生插件接口依赖。
- **Dimcirui 的 MHWs EFX Editor 0.3.0**：本地参考材料中的 Blender 特效编辑器，为开发提供结构与编辑流程参考；不是本安装器的必装插件。
- **RE Engine VFX Slot & Motion Editor（本地元数据署名 Antigravity & AZUSA）与 WeaponEfxReload v3.1**：动作槽位、PFB 与资源重载的早期参考材料。
- **praydog 的 [re-engine-mcp](https://github.com/praydog/re-engine-mcp)**：开发期间用于理解游戏对象及运行接口；本包提供的 MotionVfx MCP 是针对编辑器操作的独立接口。
- **[REE-Content-Editor](https://github.com/kagenocookie/REE-Content-Editor) 及其贡献者**：项目研究和 RE Engine 文件编辑工作的参考。

具体使用关系与许可位置见 [CREDITS](CREDITS.md)。开发参考不等同于直接包含该项目全部代码；上游项目的工作与署名应分别保留。
