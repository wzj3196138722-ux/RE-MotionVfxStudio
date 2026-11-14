# RE4R（生化危机 4 重制版）支持：现状与手动部署

只在 `re4r` 分支上。配置层的设计见 [VfxStudio.Core/Games/README.md](../../VfxStudio.Core/Games/README.md)，
EFX 格式层的来源和修复见 [VfxStudio.Core/Formats/UPSTREAM.md](../../VfxStudio.Core/Formats/UPSTREAM.md)。

## 已在游戏里验证（2026-11-14）

手枪枪口火光 `_Chainsaw/VFX/EffectEditor/EFD_Weapon/EFD_wp_common/efd_0015_wp_common_muzzle_0000.efx` 跑通了完整流程：

1. 记录：开枪后列出枪口火光、弹壳落地、脚步三项，都确认属于本地角色。
2. 打开：从安装目录的 PAK 补丁链读出原文件（`.efx.3539837`）。
3. 修改：通过 MCP `set_color` 把 8 个块改成绿色。
4. 应用：新版本发布到 `natives/STM/_Chainsaw/VFX/EffectEditor/MotionVfxLive/…`，并绑定到枪口火光的槽位；下一枪看到绿色火光。
5. 预览：MCP `preview_effect` 不需要开枪，在武器上独立播放（配合 `scene_time` 慢放更容易看清）。
6. 还原：MCP `restore` 之后，火光恢复原样，场景速度也恢复。

EFX 语料门禁：RE4 的 3946 个文件全部和原文件逐字节相同（见 `tools/EfxCorpusGate`）。

## RE4 和 Wilds 的关键差异

| 项 | RE4 | 依据 |
|---|---|---|
| 本地角色 | `chainsaw.CharacterManager.getPlayerContextRef()` → `get_BodyUpdater()` | TDB |
| 武器 | `chainsaw.PlayerEquipment` 不在身体或头部 GameObject 上，要在场景里找 `get_Context()` 等于玩家上下文的那个；再用 `getEquipWeapon` / `get_EquipWeaponID` / `get_ActiveType` | 实机列出身体 GameObject 的 66 个组件 |
| 角色身上的特效管理器 | 只有 `ObjectEffectManager`，没有 `ObjectEffectManager2` | 同上 |
| 武器特效的创建入口 | `EffectManager.createEffectInternal(GameObject, ProviderData, …)`（直接传数据的重载）；带 `RequestInfo` 的重载和 OEM 的请求方法在开枪、挥刀时都没有被调用 | 实机记录钩子调用次数 |
| 资源来源 | 上面这个重载返回的 GameObject 读不到 EffectPlayer，所以把 ProviderData 槽位的持有器记为来源（`provider_resource_holder`） | 实机 |
| PAK 依据 | REFramework 不写 `[IntegrityCheckBypass]: Pak name:`；按 `re_chunk_000.pak` 加上连续的 `patch_NNN` 来读 | 日志 |
| 热重载 | `create_resource`（逻辑路径不带后缀）→ holder → 惰性 EffectPlayer 等就绪 → 在 LockScene 时 `setResources`，和 Wilds 同一套 | 实机 |
| 资源缓存标记 | 只有 `EffectCache`，没有 `IgnoreEffectCache` | TDB |

## 尚未支持

- 动作特效槽位、独立 PFB 编辑、装备重建：配置里已关闭，后台会提示“待实现”。要给武器加常驻 EFX、给动作配 EFX，需要先确认 MOTLIST/MOT 版本号、RE4 的 RSZ 类型表（REasy `rszre4.json`），以及怎样重新加载武器 prefab。
- 挥刀：一次录制里没有捕获到刀的特效，可能只在命中时才会生成，需要继续查。
- `chainsaw.*` 游戏层的特效入口还没有专门挂钩；目前武器特效都是从引擎层的 `createEffectInternal` 抓到的。
- 安装器（`tools/Installer`）只识别 Wilds；RE4 目前要按下面的方法手动部署。
- 表达式语义（B4）还没有在 RE4 上复核。

## 手动部署（开发用）

以下均为新增文件，不覆盖游戏原有文件。删除这些文件即可撤销。

1. Lua 插件：把 `game-plugin/MotionVfxLive.lua` 复制到 `<RE4>/reframework/autorun/`，把 `game-plugin/MotionVfxLive/fx_*.lua`
   复制到 `<RE4>/reframework/autorun/MotionVfxLive/`。
2. 手动脚本：把 `game-plugin/手动重载MotionVfxLive.lua`、`刷新MotionVfxLive输入框.lua`、`修复MotionVfxLive资源还原.lua` 分别复制到
   `<RE4>/reframework/data/MotionVfxLive/` 下的 `Reload.lua`、`RefreshTextInputs.lua`、`RepairBindings.lua`。
3. 图标：把 `game-plugin/images/MotionVfxLive/*.png` 复制到 `<RE4>/reframework/images/MotionVfxLive/`。
4. D2D：RE4 需要已安装 reframework-d2d（DLL 和 Lua）。输入 DLL（MotionVfxDebugInput）可以不装。
5. 后台：编译 `VfxStudio.LiveEditor` 后，**把输出目录复制一份再运行**，否则改代码重新编译时文件会被正在运行的后台锁住：
   ```
   dotnet <复制出的目录>/VfxStudio.LiveEditor.dll --game re4 --game-root "<RE4>" \
     --mailbox "<RE4>/reframework/data/MotionVfxLive" --log-directory "<RE4>/reframework/data/MotionVfxLive" \
     --assets <素材目录> --output <输出目录>
   ```
   `status.json` 里应显示 `"game":"re4"`。
6. 进游戏后点 REFramework 的 **Reset Scripts**。改动了捕获模块或配置后，也必须完整 Reset；编辑器里的“重载”不会重新加载捕获模块。
7. MCP：`VfxStudio.Mcp --game-root "<RE4>"`。

## 坑

- 不要在游戏运行时新建或修改 `reframework/plugins/source/*.cs`，REFramework.NET 的热重载会卡死 RE4。探测用 Lua 加命令文件。
- reframework MCP 带参数调用 native 方法会静默返回 null，只有零参数调用可信。
- 编辑器状态栏的“等待后台就绪”只是启动时的初始提示，判断是否连上后台要看 `ui-status.json` 里的 `backendReady`（已修复为连上后自动更新）。
