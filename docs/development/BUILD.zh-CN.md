# 从源码构建

[English](BUILD.en.md)

当前发布产品是游戏内的 **MotionVfx Live 0.8.5**。其界面是 `game-plugin` 内的 Lua，后台是 `VfxStudio.LiveEditor`，AI 通道是 `VfxStudio.Mcp`。`VfxStudio.App`、`VfxStudio.Renderer`、`VfxStudio.Unpacker` 保留早期桌面预览与素材研究代码，不是安装版的启动入口。

## 环境与依赖

- Windows x64；构建自包含安装包需要 .NET 10 SDK。普通后台与 MCP 源码项目以 net8.0 为目标，开发运行还需要相应运行时。
- 构建输入 DLL 或 Lua 测试执行器需要 Visual Studio C++ Build Tools、MSVC 和 Windows SDK。
- NuGet 依赖由项目文件声明并恢复；第一次构建需能够访问 NuGet。
- `third-party/reframework-sdk` 提供所需 REFramework API 头文件与 Lua 源码，目录应有 `include/reframework/API.h` 和 `dependencies/lua/src/lua.h`。版本对应 API 1.15、Lua 5.4.3。可通过 `-REFrameworkRoot` 或 `REFRAMEWORK_ROOT` 选择自己的同布局依赖，不要求原作者的参考资料目录。
- D2D 1.4.1 的运行组件在 `third-party/reframework-d2d/package/reframework`，许可在同级 `LICENSE`。构建不下载或捆绑完整 REFramework；使用者需要自行安装适用于游戏的 REFramework。

仓库包含必要 PFB 格式 JSON、编辑器/作者 Logo 和有出处的 UI 图标。游戏、解包模型/贴图/动作/特效、用户 MOD、PAK、真实录制与测试夹具均不随源码发布。

## 后台、MCP 与辅助程序

在仓库根目录执行：

```powershell
dotnet build VfxStudio.LiveEditor/VfxStudio.LiveEditor.csproj -c Release
dotnet build VfxStudio.Mcp/VfxStudio.Mcp.csproj -c Release
dotnet build tools/DesktopBridge/MotionVfxDesktop.csproj -c Release
```

`VfxStudio.Core` 依赖 `REE-Lib.Generators`，LiveEditor 还链接 `tools/DesktopBridge/AuthorLinks.cs`，因此不要只复制某一个子目录。根目录历史 `.sln`/`.slnx` 尚未代表当前全部产品；以上显式项目命令是推荐入口。

生产运行时共 18 个 `fx_*.lua`，全部在 `game-plugin/MotionVfxLive`，包括 `fx_preview_runtime.lua`。打包脚本从这个目录收集并检查模块依赖，不需要项目外的 Lua 材料目录。

## 输入 DLL 与离线测试

```powershell
./tools/DebugInput/Build.ps1
./tools/LuaValidation/Run-OfflineTests.ps1
dotnet run --project tools/GameCaptureTests/GameCaptureTests.csproj -c Release
dotnet run --project tools/DesktopBridgeTests/DesktopBridgeTests.csproj -c Release
```

输入构建会运行自己的输入、轮询、DLL/Lua 接口测试。离线 Lua 入口检查生产脚本语法，验证游戏内文件浏览器与工作台，使用模拟游戏接口，不启动游戏、不采集屏幕。测试输出写入已忽略的 `test-output`。若使用自己的依赖：

```powershell
$env:REFRAMEWORK_ROOT = 'C:\Dependencies\reframework-sdk'
./tools/DebugInput/Build.ps1
./tools/LuaValidation/Run-OfflineTests.ps1
```

更完整的 UI 与原生 ImGui 检查入口为 `Run-LiveUiTests.ps1` 和 `native-imgui/Build.ps1`；前者在 `build.ps1` 生成 Lua runner 后运行。旧 `run-tests.ps1 -MaterialsRoot ...` 是历史独立 Lua 材料测试，不属于当前产品的默认验证。

后台部分 `*Tests.cs`、EFX/PFB/MOT 审计和 `distribution/Test-Package.ps1` 需要使用者自己的合法游戏夹具，源码不附这些文件。不能将“可编译”当作全部游戏实测通过。`tools/LiveEditor` 中连接游戏的安装、探针和交互测试也不是默认离线测试，必须显式提供游戏目录。

## 构建便携包和 EXE 安装器

```powershell
./distribution/Build-Package.ps1 -OutputRoot artifacts/release
./tools/Installer/Build.ps1 `
  -PayloadArchive artifacts/release/MotionVfxLive-0.8.5-win-x64.zip `
  -OutputDirectory artifacts/release
```

便携包会发布后台、MCP 与辅助程序为 .NET 10.0.8 win-x64 自包含版本，编译输入 DLL，收集 D2D、Lua、图像、对应源码与第三方声明，并生成 SHA256 清单。安装器将 ZIP 内嵌到单个 EXE。最终用户无需另装 SDK、Python 或 .NET 运行时。

每次应使用新的输出目录；脚本拒绝覆盖已有分发目录。`-ReusePublished` 仅用于已经自行验证构建缓存的开发流程，正常发布不需要。如果发布自包含运行时版本有调整，必须同步更新构建脚本、第三方声明与验证预期。

`Build-Package.ps1` 可使用 `-REFrameworkRoot`，会传给输入构建。编译与打包不会安装到游戏；安装/更新另由用户运行安装程序。安装器的 `--self-test --output <新目录> --quiet` 使用隔离的模拟游戏目录，可以检查基础安装及升级回退，不等于真实游戏兼容性测试。

## 旧桌面预览项目

这三个项目的源码保留供后续开发，可能需要额外 NuGet 依赖、自备素材和原生纹理解压组件。`GDeflateWrapper.dll` 来源未整理，因此未收入公开源码；缺少它时压缩纹理路径不可用。`Assets/RuntimeEvidence` 的历史记录也不发布，现在仅在本地存在时才复制。

`Assets/Lists/MHWS_STM.list` 是可选的外部游戏资源路径清单，不随仓库发布。使用旧 Unpacker 的 PAK 提取功能时，应自行准备适用版本的清单并放在该路径；当前游戏内编辑器和安装包不依赖它。

- `MOTIONVFX_ASSETS`：自行准备的 Assets 根目录，替代开发机绝对路径。
- `MOTIONVFX_GAME_ROOT`：旧 Unpacker/研究探针的游戏目录；Unpacker 也接受游戏目录作为位置参数。
- `MOTIONVFX_RSZ_SCHEMA`：可选的 RSZ 类型表覆盖路径，默认使用仓库 PFB JSON。

作者代码按仓库根 `LICENSE` 分发；ReeLib、REasy、Lua、REFramework、D2D、Lucide、ImGui 与 NuGet 依赖保留各自许可。不要把根 MIT 许可视为游戏素材或所有第三方组件的统一许可证。
