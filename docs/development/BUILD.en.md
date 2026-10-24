# Building from source

[简体中文](BUILD.zh-CN.md)

The current product is the in-game **MotionVfx Live 0.8.5** editor: Lua UI in `game-plugin`, backend in `VfxStudio.LiveEditor`, and AI integration in `VfxStudio.Mcp`. `VfxStudio.App`, `VfxStudio.Renderer`, and `VfxStudio.Unpacker` retain the earlier desktop preview and asset research work; they are not the installed product's entry point.

## Requirements and dependencies

- Windows x64 and .NET 10 SDK for self-contained release builds. Normal backend/MCP projects target net8.0; development execution also needs the corresponding runtime.
- Visual Studio C++ Build Tools, MSVC, and Windows SDK for the input DLL and Lua test runner.
- NuGet access for the first restore. Dependencies and versions are declared in each project file.
- `third-party/reframework-sdk` supplies the required REFramework API headers and Lua source, with `include/reframework/API.h` and `dependencies/lua/src/lua.h`. These correspond to API 1.15 and Lua 5.4.3. Pass `-REFrameworkRoot` or set `REFRAMEWORK_ROOT` to use another dependency directory with this layout.
- D2D 1.4.1 runtime components live under `third-party/reframework-d2d/package/reframework`, with their `LICENSE` alongside the package. The build does not bundle a complete REFramework installation; users install a compatible REFramework themselves.

The repository includes the PFB format schema, editor/author logos, and attributed UI icons. It excludes the game, extracted models/textures/motions/effects, user mods, PAK archives, real recordings, and proprietary test fixtures.

## Backend, MCP, and helper

Run from the repository root:

```powershell
dotnet build VfxStudio.LiveEditor/VfxStudio.LiveEditor.csproj -c Release
dotnet build VfxStudio.Mcp/VfxStudio.Mcp.csproj -c Release
dotnet build tools/DesktopBridge/MotionVfxDesktop.csproj -c Release
```

`VfxStudio.Core` uses the `REE-Lib.Generators` analyzer project. LiveEditor also links `tools/DesktopBridge/AuthorLinks.cs`; copying only one project directory is insufficient. The historical root solutions do not describe all current product components. Use the explicit project commands above.

All 18 production `fx_*.lua` modules, including `fx_preview_runtime.lua`, are in `game-plugin/MotionVfxLive`. Packaging discovers these modules and checks their dependency closure. No adjacent reference-material directory is needed.

## Native input and offline checks

```powershell
./tools/DebugInput/Build.ps1
./tools/LuaValidation/Run-OfflineTests.ps1
dotnet run --project tools/GameCaptureTests/GameCaptureTests.csproj -c Release
dotnet run --project tools/DesktopBridgeTests/DesktopBridgeTests.csproj -c Release
```

The input build runs input, polling, and DLL/Lua integration tests. The offline Lua entry checks production syntax, the themed file browser, and workbench behavior through mocked game APIs. It neither launches the game nor captures the screen. Generated reports go into ignored `test-output` directories. To select your own dependencies:

```powershell
$env:REFRAMEWORK_ROOT = 'C:\Dependencies\reframework-sdk'
./tools/DebugInput/Build.ps1
./tools/LuaValidation/Run-OfflineTests.ps1
```

Additional UI/native ImGui entry points are `Run-LiveUiTests.ps1` and `native-imgui/Build.ps1`. Build the Lua runner with `build.ps1` before the former. The old `run-tests.ps1 -MaterialsRoot ...` tests separate historical Lua materials and is not the current product's default suite.

Some backend `*Tests.cs`, EFX/PFB/MOT audits, and `distribution/Test-Package.ps1` require your own legally obtained game fixtures. These are not included. A successful build is not a claim that all in-game scenarios passed. Installation scripts, probes, and interactive tests under `tools/LiveEditor` require an explicit game directory and are not part of the default offline checks.

## Portable payload and EXE installer

```powershell
./distribution/Build-Package.ps1 -OutputRoot artifacts/release
./tools/Installer/Build.ps1 `
  -PayloadArchive artifacts/release/MotionVfxLive-0.8.5-win-x64.zip `
  -OutputDirectory artifacts/release
```

Packaging publishes the backend, MCP, and helper as self-contained .NET 10.0.8 win-x64 applications; builds native input; collects D2D, Lua, images, corresponding source, and notices; and creates SHA256 manifests. The installer embeds the payload ZIP in one EXE. End users do not need an SDK, Python, or a separately installed .NET runtime.

Choose a new output directory for each build: existing release folders are never overwritten. `-ReusePublished` is only for a developer who has verified the publish cache; it is unnecessary for normal releases. Runtime version changes must be reflected in the scripts, notices, and validation expectations.

`Build-Package.ps1` accepts `-REFrameworkRoot` and forwards it to the input build. Compilation and packaging do not install into the game. Users run the resulting installer separately. Its `--self-test --output <new directory> --quiet` mode checks base installation and rollback using an isolated simulated game directory, not real-game compatibility.

## Earlier desktop projects

The earlier desktop project sources are retained for development. They may require additional NuGet dependencies, your own assets, and native texture decompression support. `GDeflateWrapper.dll` is omitted because its provenance has not been documented; compressed texture paths are unavailable without an appropriate implementation. Historical `Assets/RuntimeEvidence` data is excluded and copied only when present locally.

`Assets/Lists/MHWS_STM.list` is an optional external game resource path list and is not included. Supply a version-appropriate list at that path to use the old Unpacker's PAK extraction workflow. The current in-game editor and installer do not depend on it.

- `MOTIONVFX_ASSETS`: your own Assets root, replacing developer-machine paths.
- `MOTIONVFX_GAME_ROOT`: game directory for the old Unpacker/research probes. Unpacker also accepts a positional game-directory argument.
- `MOTIONVFX_RSZ_SCHEMA`: optional RSZ schema override; the repository PFB schema is the default.

Author code follows the root `LICENSE`. ReeLib, REasy, Lua, REFramework, D2D, Lucide, ImGui, and NuGet packages retain their own terms. The root MIT license does not relicense game content or every third-party component.
