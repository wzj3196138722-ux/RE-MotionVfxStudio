# 致谢与来源 / Acknowledgements and credits

MotionVfx Live 由「尝试给推搓推的推」开发与维护。感谢下列上游项目、组件作者和参考工具的贡献。项目自己的代码采用仓库根目录的 MIT 许可证；这不替代第三方组件的版权和许可。

MotionVfx Live is developed and maintained by 尝试给推搓推的推. We thank the upstream projects, component authors and reference tools below. The root MIT license covers this project's own code; it does not replace third-party copyright notices or licenses.

## 当前游戏内发行组件 / Current in-game release components

| 项目 / Project | 作者或署名 / Attribution | 使用关系 / Relationship |
|---|---|---|
| [ReeLib / RE-Engine-Lib](https://github.com/kagenocookie/RE-Engine-Lib) | shadowcookie / kagenocookie | 文件格式解析和写入代码及本项目修改 / File-format reading/writing code and local modifications |
| [REFramework](https://github.com/praydog/REFramework) | praydog and contributors | 游戏内 Lua、对象访问与原生插件 API；需单独安装 / In-game Lua, object access and native plugin APIs; installed separately |
| [REFramework D2D](https://github.com/cursey/reframework-d2d) | cursey and contributors | D2D 绘图与文字/图片接口；分发 1.4.1 / D2D rendering and text/image APIs; version 1.4.1 is bundled |
| [REasy](https://github.com/seifhassine/REasy) | Project contributors; bundled license credits Battlezone | Wilds RSZ 类型定义，包括匹配角色 PFB 的 `FootLandingCustomData.Element` / Wilds RSZ definitions, including the matching character-PFB type |
| [ZstdSharp](https://github.com/oleg-st/ZstdSharp) | oleg-st and contributors | ZstdSharp.Port 0.8.5，PAK 解压 / PAK decompression |
| [xxHash .NET](https://github.com/uranium62/xxHash) | uranium62 and contributors | Standart.Hash.xxHash 4.0.5，资源路径哈希 / Resource path hashing |
| Microsoft .NET / Windows Desktop | Microsoft and contributors | 独立后台、MCP、安装器及录制助手的运行环境 / Runtime for the backend, MCP, installer and capture helper |
| Lua 5.4.3 / REFramework API 1.15 headers | Respective upstream authors | 输入组件编译接口 / Build interfaces for the input component |

源码中的许可副本位于 [distribution/licenses](distribution/licenses/) 和相关第三方目录。最终分发包还带有构建时采用的 .NET、D2D、Lua/REFramework API 等对应许可与通知；具体组件清单见 [第三方说明](distribution/第三方说明.md)，实际构建产物以 Release 附件及随包说明为准。

Source license copies are kept in [distribution/licenses](distribution/licenses/) and the relevant third-party directories. The final package also includes the notices for its .NET, D2D and Lua/REFramework API components. See the [third-party inventory](distribution/第三方说明.md) and the Release assets for the actual distribution contents.

## 桌面原型、图标和验证工具 / Desktop prototype, icons and validation tools

仓库还保留早期桌面工作台和开发验证工具。下表描述它们的直接依赖及随源码保留的素材；它们不是当前 MotionVfx Live 游戏内安装器所需的整套运行依赖。包名、版本、项目链接与许可来自相应 `.csproj`、NuGet 包元数据及包内许可；构建这些工具时也须保留各传递依赖的通知。

The repository also retains the earlier desktop workbench and development/validation tools. The table below describes their direct dependencies and source assets, not an additional set of runtime requirements for the current in-game installer. Names, versions, project links and licenses were checked against the project files, NuGet metadata and included license texts. Builds of these tools must also retain applicable transitive-dependency notices.

| 组件 / Component | 署名与用途 / Attribution and use | 许可 / License |
|---|---|---|
| Lucide icons | Lucide Icons and Contributors；`Assets/UI` 的图标素材 / UI icon assets | ISC；其中注明来自 Feather 的图标另保留 Cole Bemis 的 MIT 许可。完整文本见 `Assets/UI/LUCIDE-LICENSE.txt` / Includes the separately attributed MIT-licensed Feather-derived icons |
| [Dear ImGui 1.92.4](https://github.com/ocornut/imgui) | Omar Cornut；`tools/LuaValidation/native-imgui` 的真实输入验证库 / Native input validation | MIT；完整文本见 `tools/LuaValidation/native-imgui/imgui-1.92.4/LICENSE.txt` |
| [Hexa.NET.ImGui / Hexa.NET.ImGuizmo 2.2.9](https://github.com/HexaEngine/Hexa.NET.ImGui) | Juna Meinhold；桌面工作台的 ImGui/ImGuizmo .NET 绑定，ImGui 绑定也用于 `tools/LiveEditor/UiPreview` / Desktop UI/gizmo bindings and UI preview tooling | 绑定代码为 MIT，包内 `LICENSE.txt`；底层 Dear ImGui、ImGuizmo 的通知也须保留 / MIT bindings; retain native-library notices too |
| [Silk.NET 2.22.0](https://github.com/dotnet/Silk.NET) | .NET Foundation and Contributors；桌面窗口、输入、OpenGL 与数学 API / Desktop windowing, input, OpenGL and mathematics | MIT，所用原生依赖按各自许可 / MIT, with separate native dependency licenses |
| [SixLabors.ImageSharp 3.1.11](https://github.com/SixLabors/ImageSharp) | Six Labors and contributors；桌面原型和渲染器的图像处理 / Desktop application and renderer image processing | **Six Labors Split License 1.0**；按许可条件适用 Apache-2.0 或商业许可，不能标成 MIT。以该版本包内 `LICENSE` 为准 / Apache-2.0 or a commercial license under the package's stated conditions; not MIT |
| [AssimpNetter 6.0.2](https://github.com/Saalvage/AssimpNetter) | Nicholas Woodfield；Stefan Koch / Salvage；桌面渲染器模型导入 / Desktop renderer model import | .NET 包装层 MIT；原生 Assimp 使用 BSD-3-Clause，并有其他组件声明，见包内完整 `License.txt` / MIT wrapper, BSD-3-Clause Assimp, and additional third-party notices |
| [Roslyn / Microsoft.CodeAnalysis.CSharp 4.9.2](https://github.com/dotnet/roslyn) | Microsoft；`REE-Lib.Generators` 的源码生成编译依赖 / Source-generator build dependency | MIT；不作为当前编辑器的独立运行组件分发 / Not distributed as a separate runtime component of the current editor |
| [Microsoft.CodeAnalysis.Analyzers 3.11.0](https://github.com/dotnet/roslyn-analyzers) | Microsoft；源码生成器的开发期静态分析 / Development-time source-generator analysis | MIT；NuGet 元数据标注 development dependency |
| [LuaCSharp 0.5.3](https://github.com/nuskey8/Lua-CSharp) | nuskey, Akeit0；`tools/LiveEditor/UiPreview` 的离线 Lua/UI 验证 / Offline Lua/UI validation | MIT |
| [Svg.Skia 5.2.3](https://github.com/wieslawsoltes/Svg.Skia) | Wiesław Šoltés；`tools/IconRasterizer` 的 SVG 栅格化 / SVG rasterization tooling | MIT；Skia 等传递依赖另见其声明 / See separate notices for transitive dependencies such as Skia |

Hexa.NET 2.2.9 包元数据注明其 Dear ImGui 绑定面向 1.92.2b；这与本仓库另存的 **1.92.4 验证源码**是两个不同的版本来源，不应混写。项目根目录的 MIT 许可证也不会把 Lucide 的 ISC、ImageSharp 的分流许可或 Assimp 的 BSD 等许可证改成 MIT。

Hexa.NET 2.2.9 metadata identifies its Dear ImGui bindings as targeting 1.92.2b. This is separate from the **1.92.4 validation sources** retained in this repository. The root MIT license does not relicense Lucide's ISC assets, ImageSharp's split-licensed package, Assimp's BSD code or other third-party components.

## 开发参考 / Development references

这些工具在开发时作为格式、编辑流程或运行时观察的参考，不是要求用户额外安装的一组插件，也不意味着 MotionVfx Live 整体是每个项目的衍生版本。

These tools informed format research, editing workflows or runtime investigation. They are not an additional plugin bundle users must install, nor does this list describe the whole editor as a derivative of every listed project.

- **MHWs EFX Editor 0.3.0 — Dimcirui**。作者与版本来自本地参考包 `blender_manifest.toml` / `__init__.py`；该插件为 GPL-3.0-or-later。这里保留实际名称，不用另一款面向《世界》的编辑器链接代替。/ The name, author and version are recorded in the supplied reference package's metadata. That plugin is GPL-3.0-or-later. No unrelated World-editor repository is substituted for its source link.
- **RE Engine VFX Slot & Motion Editor — Antigravity & AZUSA**。署名来自本地参考插件的 `bl_info`，用于动作与特效槽位工作流参考。/ Attribution comes from its local `bl_info`; it informed action/effect-slot workflows.
- **WeaponEfxReload v3.1**。本地参考包提供早期资源联动重载流程；现有元数据未提供可确认的独立作者及公开仓库，因此不另造署名或地址。/ The supplied package informed early resource-reload workflows; its available metadata does not establish an additional author or public repository.
- **[re-engine-mcp](https://github.com/praydog/re-engine-mcp) — praydog**。开发期间的游戏对象和接口观察工具。本项目的 MotionVfx MCP 提供自己的编辑器级工具，不将上游的任意游戏对象操作接口作为本版功能。/ Used for inspecting game objects and APIs during development. MotionVfx MCP exposes its own editor-level tools, not the upstream server's arbitrary game-object operations.
- **[REE-Content-Editor](https://github.com/kagenocookie/REE-Content-Editor) — shadowcookie / kagenocookie and contributors**。作为 RE Engine 文件编辑参考，并提供本工作区使用的 RE-Engine-Lib 上游来源。/ A reference for RE Engine file editing and the upstream RE-Engine-Lib source available in the development materials.

- **[MDF-XL — SilverEzredes](https://github.com/SilverEzredes/MDF-XL)**：0.8.6 增加与已安装 MDF-XL 1.5.36 的局部帧回调兼容处理。发布包仅包含本项目编写的保护与备份工具，不分发 MDF-XL 原始脚本、配置或材质预设。/ Version 0.8.6 adds a scoped frame-callback compatibility guard for an existing MDF-XL 1.5.36 installation. Only the guard and backup tooling are included; MDF-XL's original script, settings and material presets are not redistributed.

感谢参与实机测试、反馈问题及研究 RE Engine 文件格式的社区成员。游戏和相关资产仍属于各自权利人，不在本项目安装器中分发。

Thanks also to community members who tested the editor, reported problems and researched RE Engine formats. The game and its assets remain the property of their respective rights holders and are not included in this installer.
