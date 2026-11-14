# 游戏配置层

MotionVfx Live 原本只支持《怪物猎人 荒野》。所有因游戏而异的值都集中在两个配置文件里，
其他生产代码只从配置读取，不写死某个游戏的值：

| 位置 | 内容 |
|---|---|
| `VfxStudio.Core/Games/GameProfile.cs` | 后台：EFX/PFB/MOTLIST/MOT 版本后缀、`EfxVersion`、`ClipVersion`、ReeLib `GameName`、RSZ 类型表文件名、exe 与 Steam 目录名、预览发布目录、动作/PFB 资源根目录、装备组件类型、功能开关 |
| `game-plugin/MotionVfxLive/fx_game_profile.lua` | 游戏内插件：EFX 后缀、本地角色访问链（PlayerManager → 主玩家 → 角色 → 武器及其 ID/类型）、资源缓存标记、捕获钩子列表（含引擎 OEM 签名）、远端角色/弹体类型、功能开关 |

## 怎么选中当前游戏

- **后台**：`--game <id>` 参数优先；没有时看 `--game-root` 里有哪个游戏的 exe；都没有就用 MHWilds，
  这样加配置层之前的启动命令照样能用。选中的游戏写进 `status.json` 的 `game` 字段。
- **插件**：用 REFramework 的 `reframework:get_game_name()`，Wilds 是 `mhwilds`、RE4 是 `re4`，
  来源是游戏目录 `re2_framework_log.txt` 里的 `Game name:`。REFramework 报出的游戏不在支持列表里时直接报错，
  不会退回成 Wilds；只有离线测试（没有 REFramework）时才用 Wilds。
- **一致性**：后台报的 `game` 和插件检测到的不一样时，插件认为后台未就绪并给出提示。旧版后台不报 `game`，照常放行。

## 门禁

- `tools/Check-GameCoupling.ps1`：在生产代码里查游戏专属的值（版本号、exe 名、`app.*` 类型名等），配置文件以外出现就失败。
  已接进 `tools/LuaValidation/Run-OfflineTests.ps1`。
- `tools/LuaValidation/game-profile-tests.lua`：检测、Wilds 各项取值，以及捕获钩子的数量和顺序与加配置层之前一致。

## 有意保留为 Wilds 专属、不经配置的部分

- `fx_equipment_sources.lua` 和 `fx_equipment_pfb_preview_runtime.lua`：装备 PFB 预览靠 `changeWeapon` / `changeParts`
  重建角色装备。只有配置里 `features.equipment_rebuild` 为真时才会加载（入口在 `fx_pfb_preview_runtime.lua`）。
- `PfbWorkspace` 中静态的 `NormalizeResource`（`art|gamedesign` 资源根）：PFB 编辑在新游戏上要等功能开启时再接入。
- 旧的桌面原型（`VfxStudio.App`、Renderer、Unpacker，以及 Core/Services 里除 `EfxEditorService` 外的服务）：
  不属于 MotionVfx Live 的运行路径。
- `Formats/`：内嵌的 ReeLib，本来就支持多个游戏。

## 新增一个游戏（A2：RE4R）需要做的

1. 在 `GameProfiles` 里加一个配置并放进 `All`，在 `fx_game_profile.lua` 的 `profiles` 里加同 id 的条目。
2. 提供该游戏的 RSZ 类型表（`Assets/Lists/<RszSchemaFile>` 或随包的 `formats/`）。
3. 补齐捕获钩子：引擎 OEM 方法签名可能不同（RE4R 的 `requestEffectFromRangeSquence`、`requestEffectInternal`），
   游戏层的 `app.*` 换成该游戏的命名空间（RE4R 是 `chainsaw.*`），这些都要在游戏里实测确认。
4. 按该游戏的实际能力设置功能开关。
5. 更新 `game-profile-tests.lua`，再跑 `Check-GameCoupling.ps1` 和全套离线测试。
