# MotionVfx Live tutorials

[Project overview](../README.en.md) · [简体中文](tutorials.zh-CN.md)

For **0.8.5**. The workbench controls are primarily Chinese, so this guide includes their actual labels. Install the editor, enable REFramework's Loose File Loader, and start both the backend and the game. The training area is a useful place for a first edit.

## Finding your way around

The left sidebar separates action capture, effect capture, EFX editing, PFB editing and time/replay. The top bar contains resource precaching, files, editing tools and settings. Its main apply button changes with the active page.

Drag the upper-left logo/title area to move the window, or its edges to resize. Hiding leaves a docked logo orb; click it to reopen. Press Insert first if you need the REFramework mouse cursor. Settings provide centering, scaling and Chinese/English attribute/parameter labels. Untranslated fields keep their original names.

In the in-game file browser, single-click to select and double-click to enter a folder or open a file. Enter opens, Esc cancels, Alt+Left/Right navigates history, Alt+Up opens the parent folder and F5 refreshes. Asset selection supports Ctrl/Shift multi-select. You can paste a complete Unicode path into the address bar.

<a id="first-edit"></a>
## 1. Edit an effect triggered by a real action

Goal: identify an EFX actually used by an action, change it, verify the result in game, then export or restore.

1. Open **动作记录** (Action capture) and select **开始记录调用** (Start capturing calls). The current action is also displayed while capture is off; recording preserves information for later selection.
2. Hide the editor and perform a short, specific action such as an attack, reload or Demon Mode activation. Reopen it and select **结束记录调用** (Stop capturing calls).
3. Open **特效记录** (Effect capture). Inspect the selected action's effects. If the filtered list is empty, use **全部特效** (All effects). Nearby timestamps alone do not prove a direct call; prefer confirmed ownership and source evidence.
4. Select **打开并编辑** (Open and edit) for the target resource. Check its complete logical path, then expand **Group → Entry → Attribute**. Several actions may share this file.
5. Record one parameter's original value, make a small observable change and select its commit button. Text typed into a field is not committed automatically.
6. Select **应用特效参数** (Apply effect parameters) in the top bar and wait. Hide the editor and repeat the original action. Existing particles do not necessarily update in place.
7. Check that the character actually created the new resource version, then inspect the visual change. An accepted apply request alone does not prove that the intended effect changed.
8. To keep the result, select **文件 → 导出文件** (File → Export file) and choose an output location. Finish with **停止并还原** (Stop and restore), and wait for completion.

Captured resources are opened from the effective file for that full logical path in the current game installation, including loose-file and PAK overrides. This is not a memory dump. Opening a local file with the same filename does not establish that the character uses it.

<a id="parameters"></a>
## 2. Colors, curves, Groups and entries

### Color and combined inputs

For explicit RGBA fields, the color control and the four numeric channels stay synchronized. Recognized `via.Color` values use 0–255, for example `[0,180,255,255]`. Brightness/intensity is a separate field; it is not Alpha.

Use the color area's commit button to submit all four channels as one transaction. One undo restores the entire color. Arbitrary four-dimensional vectors are not treated as colors. Recognized XY, XYZ and base/random values are displayed together while preserving their underlying field paths.

### Expression curves

1. Select an attribute such as `SpawnExpression` and expand **表达式曲线 · 公式与数值** (Expression curves: formulas and values).
2. Identify the channel and its committed values. `spawnNum` and `spawnNumRange` are separate channels even if both contain a `Lerp.from` value.
3. Change one understood numeric value, select **提交数值** (Commit value), apply, then repeat the relevant action.

Do not copy another file's Entry index or assumed initial values. Mods and game versions change these. If nothing changes visually, check whether the Group/Entry was triggered and whether the edited channel affects it. Increasing values repeatedly is not a substitute for identifying the right field. Arbitrary restructuring of every complex expression graph is not supported.

### Notes, deletion and restoring one entry

- **编辑分组备注** (Edit Group note) changes an editor label only. Native Group names remain unchanged and are used for action calls. Notes are saved separately; EFX undo does not undo them.
- Select an entry or attribute, then use **删除当前特效块** (Delete current entry) or **删除当前属性** (Delete current attribute). Use these structural operations rather than manually altering binary counts. Reopen an exported copy to inspect its structure when needed.
- **还原当前特效块** (Restore current entry) restores that entry's original state for this editing session, preserving other entries. A new entry with no original state is removed. The operation can be undone.
- Document editing and game reference replacement are separate. Apply document changes to preview them. Stop and restore returns the game's references, rather than undoing every committed field.

<a id="action-slots"></a>
## 3. Add an effect to an action

This changes both an action's trigger information and slots in its corresponding PFB. First identify the PFB actually referenced by the action.

1. Capture and select the target action, then open **动作特效槽位** (Action effect slots).
2. Select **核实当前动作的 PFB** (Verify this action's PFB). Choose from the detected character/shared or weapon/external containers and select **编辑此 PFB 的动作槽位** (Edit action slots in this PFB). Recheck after changing equipment or scenes; do not guess a PFB from a similar filename.
3. Enter the EFX logical game path, such as the complete `art/.../file.efx` path from capture, or a precache alias. Do not enter a local `C:\...` path.
4. Select **读取此 EFX 的全部 Group** (Read all Groups in this EFX), then choose its native Group name, or **全部 / 不指定分组** (All / unspecified). Read Groups again after changing the EFX path.
5. Select **人物** (Character) or **当前武器** (Current weapon), then the actual object and bone. Character attachment uses `RelationChildId=-1`; weapon IDs are detected from the live objects, not inferred from weapon type numbers. If the object is absent, verify the source again.
6. Choose **持续跟随挂点** (Follow the attachment) for effects attached to a moving character or weapon, or **仅在触发时定位** (Position only when triggered) for ground marks. No bone means the object's origin, not automatic placement at the foot or on the ground.
7. Set **出现帧** (Start frame) and **触发区间结束帧** (Trigger interval end). These are frames within this action; transitions may enter an action partway through. Initially use the displayed valid frame range to confirm a trigger, then narrow it down.
8. Choose a lifetime mode: end with the trigger interval/action, or **独立寿命（切换动作继续播放）** (Independent lifetime, continue after action changes). For independent instances, set their maximum lifetime in effect frames. The EFX itself may finish sooner.
9. Expand **选择挂接模板（来自上述 PFB）** (Choose an attachment template from this PFB) and select an existing slot with a similar purpose.
10. Select **添加特效槽位** (Add effect slot), then **热重载到游戏** (Hot reload into game). Wait and repeat the target action. Further changes require another reload.

**Why select a template?** A PFB slot contains more than a resource path and bone. It also carries offsets, rotation, scale, color, mirroring, delay and engine-specific parameters. A template provides a complete structure for the new slot. The new slot receives its own ID; your EFX, Group, attachment, bone, frame interval and lifetime replace the relevant settings. The source template is unchanged, and its action trigger frames are not copied. If position, scale or delay looks wrong, inspect inherited values or choose a more suitable template.

**Footprint example:** identify the movement clip actually running, choose an appropriate foot bone, use positioning at trigger time and an independent lifetime, then check orientation and ground placement in the EFX. Wilds changes movement clips with terrain and direction. Adding a slot to one clip does not cover every walking clip automatically. Capture and verify the required clips individually. A trigger interval does not mean emitting a new instance every frame.

Use **删除此槽位** (Delete this slot) for one added slot. **还原并清除新增槽位** (Restore and clear added slots) clears the session's added slots and restores the action/PFB, while retaining EFX parameter edits. **导出动作文件与对应 PFB** (Export action file and corresponding PFB) asks for each output location separately; either can be skipped. A complete mod normally needs both corresponding changes and their dependencies installed.

<a id="pfb"></a>
## 4. Independent PFB editing

Open **文件 → 打开 PFB 文件** (File → Open PFB file), or use the PFB editing page. The editor identifies the file type from its components:

| Detected type | Main operations |
|---|---|
| Equipment PFB | Add effect-PFB, Chain/Chain2, CLSP or JCNS resource slots |
| Effect PFB | Copy/paste slots, edit parameters, delete, undo and redo |

Enter logical paths that the game can load. Commit values and export the PFB independently. Hot reload also requires a verified target PFB currently used by the character or equipment. Opening an arbitrary local PFB does not authorize replacing an unrelated live object; source mismatches are rejected.

Equipment instance rebuilding, every component combination and all weapons have not been verified in game. For a new type, retain the original and validate a small slot edit, reload and restore before expanding it.

<a id="precache"></a>
## 5. Precache external resources

Use files already converted to game-compatible formats, retaining the correct version suffix, such as `.tex.<version>`. Precaching does not convert general PNG or FBX assets into game formats.

1. Open **资源预缓存** (Resource precache) and select **选择外部文件** (Choose external files). Keep the source `natives/stm` or `art` folder structure where possible to help resolve dependencies.
2. Leave the custom path blank for an automatic `art/` alias. To assign a custom alias, select one file at a time.
3. Select **加入预缓存** (Add to precache), inspect the resource/dependency list, then **预加载资源** (Preload resources) to inspect loading status.
4. Use **复制路径** (Copy path), paste the alias into the appropriate EFX resource field and commit. Alternatively, enter via the parameter's precache selector and use **填入所选参数** (Fill selected parameter).
5. Apply the EFX and trigger the action again.
6. After updating the original asset, use **重新读取并热重载** (Read again and hot reload). First commit or discard parameter drafts. An already applied current EFX is reapplied; newly created instances use the updated version.

`art/...` is a logical resource name, not a local Windows folder. The backend stores a separate copy and generates preview versions without overwriting the source. Dependency discovery still requires the referenced files to exist. Unsupported structures or versions need to be resolved from the reported error, not by repeatedly retrying.

Exporting an EFX does not embed its textures, materials or meshes. Distribute the dependencies it references and keep their installed logical paths consistent.

<a id="time-replay"></a>
## 6. Slow motion, pause and reverse playback

### Live game time

**时间与回看** (Time and replay) provides 0.1×, 0.25×, 0.5×, 1× and pause/resume. Resume returns to the selected speed. Use **停止并还原** (Stop and restore) when finished; choosing 1× alone does not release time control.

Settings accept a pause shortcut such as `Ctrl + Shift + Alt + P`, with 1–8 distinct keys. Save it and enable **调试模式** (Debug mode). Release every key between triggers, then press again to toggle pause/resume. All constituent keys are reserved by the editor while enabled, even when you press only part of the chord. Disable debug mode to restore game input. Text editing temporarily suppresses the shortcut. Insert and reserved system combinations cannot be bound.

### Record and review footage

1. Select 5/15/30 seconds and **开始录制画面** (Start screen recording). The workbench hides with about three seconds to prepare.
2. Keep the game in the foreground while performing the action. Capture pauses while another application is foreground. You can also select **结束并保存录像** (Stop and save recording) early.
3. After saving, use **正放重播** (Forward replay), **倒放** (Reverse), **前一帧 / 后一帧** (Previous/next frame), playback rates and the seek bar.
4. **打开录像保存文件夹** (Open recording folder) shows the saved files. **读取最近录制** (Load latest recording) reloads the latest clip.

Clips are saved under `Game folder/reframework/images/MotionVfxLive/replays/<clip ID>/` as JPG frames and `replay.json`. The target limits are 20 fps and 1280-pixel width, without audio; this is not an MP4 export. The UI shows the actual frame count and location. Failed capture is reported instead of presenting an empty successful clip. **Reverse playback reverses these images, not the live game or particle simulation.**

<a id="export-restore"></a>
## 7. Export, restore and session state

| Operation | Result |
|---|---|
| Commit parameter | Changes the editing document; does not prove an in-game change |
| Apply / hot reload | Publishes preview resources and replaces verified live references |
| File → Export file | Saves the active page's file to your chosen location; overwrites require confirmation |
| Undo / redo | Changes the document's editing history |
| Restore current entry | Restores only that Entry's original state for the session |
| Stop and restore | Stops capture/preview and restores game references and time; does not undo all EFX document edits |

Committed documents are retained when switching files. Treat unexported edits as session state, not permanent storage. Before closing the game or updating, export the EFX, action and PFB changes you need, and wait for resource/time restoration to finish.

Group notes live separately in `Game folder/reframework/data/MotionVfxLive/group-notes.json`. Recordings and precached resources also have their own files. Backend document recovery does not include every input draft, action slot or undo history and is not a replacement for export.

<a id="mcp"></a>
## 8. Connect an AI client over MCP

After installation, select **打开安装目录** (Open installation directory) and locate `mcp-config.json` and `mcp-config.toml`. Merge the appropriate server section into your AI client's MCP configuration, preserving its other servers. The generated paths are specific to your installation. Attaching the JSON in a chat is not configuration.

This is a local **stdio MCP** server. The client starts the package's `mcp/VfxStudio.Mcp.exe` with `--config` and the path to `runtime-config.json`. Start the backend and game separately. Double-clicking the MCP executable does not open an editor window.

For a first task, capture an action and open the EFX in the UI, then ask the AI:

> Use MotionVfx MCP to inspect my currently open EFX. Read its status, source, entries and actual fields first. Change only the target color I identify to cyan, preserving alpha and brightness. Do not overwrite uncommitted UI inputs. Record original values, use the latest expectedRevision, read changes back and save an independent copy. Apply the change, then let me repeat the original action. Check that the character created the new version and wait for my visual confirmation. Finally restore resources and scene time, and report the saved path and verification results.

Typical tool sequence:

1. `motionvfx_status`: connection, current document and pending drafts.
2. `motionvfx_list_actions` / `motionvfx_list_effects`: locate captured targets; use `motionvfx_open_effect` if needed.
3. `motionvfx_inspect`: obtain real indices, field paths, color groups or curve descriptions.
4. `motionvfx_set_field` / `motionvfx_set_color`: submit with the latest `expectedRevision`, then read back.
5. `motionvfx_save_copy`: save and retain the returned output path.
6. `motionvfx_apply`: apply, repeat the action, then inspect status. `verification.phase=created` establishes that the character created this resource version.
7. `motionvfx_restore`: verify the restore result.

Use `motionvfx_set_group_note` for labels; it does not rename native Groups or enter EFX undo history. Use `motionvfx_scene_time` for speed and pause. MCP does not currently expose arbitrary scripts, screen recording, disk asset import, or action/PFB slot writing; use the UI for these.

For `stale_revision`, reread the document. `drafts_present` requires resolving human input first. After a timeout or `uncertain:true`, do not automatically repeat a mutation or delete request journals to retry: the original operation may have completed. Full schemas are available in the release's `mcp-tools.json`; the package also includes the detailed Chinese AI guide, `AI修改特效教程.md`.

## Troubleshooting

| Symptom | Next step |
|---|---|
| Backend disconnected | Start the MotionVfx Live desktop shortcut; check the configured game folder |
| Hidden editor | Press Insert for the cursor, click the docked orb, or reopen/center from Script Generated UI |
| Captured actions will not replay | Call capture is not footage; use Time and replay → Start screen recording |
| Empty effect list | Perform an action with effects and check All effects; recapture after equipment changes |
| Applied change is invisible | Repeat the actual trigger and check Group, Entry, effective source and new-version creation |
| Add slot is unavailable | Check loaded Groups, verified attachment, template, frame range and the page's error |
| Reload fails after equipment change | Recapture and verify action/PFB sources rather than reusing old objects |
| Restore fails | Keep the game and scripts intact and retain the error/logs; do not Reset scripts to erase pending backups |

Diagnostics are normally in `Game folder/reframework/data/MotionVfxLive/`; installation logs are in the installation directory. Include reproduction steps, versions, resource path and relevant errors in a report. Review logs for personal local paths before sharing them.

Project dependencies, references and acknowledgements are listed in [CREDITS](../CREDITS.md).
