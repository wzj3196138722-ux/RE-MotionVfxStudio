# MotionVfx Live

<img src="game-plugin/images/MotionVfxLive/logo.png" alt="MotionVfx Live" width="112" />

**Edit the effects triggered by your character's actions, inside Monster Hunter Wilds.**

[简体中文](README.md) · [Tutorials](docs/tutorials.en.md) · [Download the 0.8.5 installer](https://github.com/wzj3196138722-ux/RE-MotionVfxStudio/releases/tag/v0.8.5)

MotionVfx Live is an in-game VFX editor built on REFramework and REFramework D2D. Record character actions and effect calls, identify the relevant EFX resource, edit its parameters, and reload it to inspect the result during the original action. The game itself renders the preview.

This repository grew out of RE-MotionVfxStudio. **The current release is the MotionVfx Live in-game editor.** The older desktop renderer in the repository is a separate prototype, not the workflow or a rendering guarantee for this installer.

## Features

| Task | Available tools |
|---|---|
| Find an effect | Capture character actions, motion IDs and effect calls; verify action/PFB relationships from actual game references |
| Edit EFX files | Browse files, Groups, entries and attributes; edit supported fields, colors and curve values; delete entries/attributes; undo, redo and restore individual entries |
| Read parameters | Chinese/English parameter labels; grouped RGBA, XY/XYZ and base/random inputs; separate Group notes |
| Preview in game | Apply resource edits, then trigger the action again; slow down, pause and resume; configure a debug pause key chord |
| Add effects to actions | Use a verified PFB, existing slot template, EFX Group and character/weapon attachment; set trigger frames and lifetime, then hot reload |
| Edit PFB files | Component-aware equipment resource slots or effect slots; export, and hot reload when the live source can be verified |
| Use external assets | Precache converted Mesh, MDF, UVS, TEX, PFB, MOT, MOTLIST and EFX files under logical `art/` aliases |
| Review footage | Record image frames for forward, reverse and frame-by-frame playback, separately from action/effect call capture |
| Connect an AI client | A local MCP server exposes 17 tools for inspection, parameter/color edits, apply/restore and scene time control |

The editor can be moved, resized or hidden as a docked logo orb. The in-game file browser supports double-click navigation, Unicode paths and familiar file selection controls.

## Install or update

You need Windows x64, Monster Hunter Wilds, and a compatible [REFramework](https://github.com/praydog/REFramework/releases) installation. Enable REFramework's **Loose File Loader**.

1. Download `MotionVfxLive-0.8.5-Setup.exe` from [Releases](https://github.com/wzj3196138722-ux/RE-MotionVfxStudio/releases/tag/v0.8.5).
2. Exit the game normally. Run the installer and confirm the folder containing `MonsterHunterWilds.exe`.
3. Select **一键安装** (Install) or **一键更新** (Update). Installing the same version offers repair.
4. The installer starts the backend. Launch the game and open **REFramework → Script Generated UI → MotionVfx Live**.
5. For later sessions, launch the desktop **MotionVfx Live** shortcut before starting the game.

The installer includes the runtime, D2D and input components. Python, the .NET SDK and development tools are not required. The game and REFramework are not included.

Existing EXE, ZIP and manual-script installations can be updated in place. Managed files are backed up before replacement. Old program cleanup requires matching ownership and file content; **the installer does not recursively delete old directories**. Mods, assets, exports, notes, settings and unidentified files are preserved. Personal files in an old portable folder remain there rather than being moved automatically. Before updating, export action/PFB changes you want to keep, commit text inputs, and finish restoring the game state.

The original local 0.8.5 installer was checked in isolated folders for migration from EXE versions 0.6.2 / 0.7.1 / 0.7.2 and ZIP versions 0.3.0 / 0.3.1 / 0.3.2. This does not mean testing on six gaming PCs. GitHub assets are rebuilt from the public source; their own checks and hashes are included with the Release. The installer is not commercially code-signed; the Release includes SHA256 checksums.

## Your first edit

**Capture an action → select an effect → commit a parameter → apply → repeat the action → export or restore.**

Start with [Edit an effect triggered by a real action](docs/tutorials.en.md#first-edit). The guide also covers:

- [Colors, curves, Groups and entries](docs/tutorials.en.md#parameters)
- [Action effect slots: templates, attachments and frames](docs/tutorials.en.md#action-slots)
- [Independent PFB editing](docs/tutorials.en.md#pfb)
- [External resource precaching and reload](docs/tutorials.en.md#precache)
- [Slow motion, pause and reverse footage playback](docs/tutorials.en.md#time-replay)
- [Export and restore](docs/tutorials.en.md#export-restore)
- [AI MCP setup](docs/tutorials.en.md#mcp)

## Scope and limitations

- Opening a captured EFX reads the **effective loose file or PAK override for its full logical path in the current game installation**. It is not a runtime memory dump and does not substitute a similarly named reference asset.
- Editing and previewing do not directly overwrite the original files. Use export for permanent changes or distribution, and include any required dependencies.
- Editing a shared EFX affects actions that reference that resource, not just the currently selected move.
- Group notes are editor labels. They do not rename native Groups or change their hashes or call relationships.
- Action/effect capture stores call information. Screen recording stores replayable images. Reverse playback reverses recorded frames, not the live game simulation.
- Chinese/English switching applies to attribute and parameter labels. The workbench controls are primarily Chinese; the English tutorial includes their actual labels.
- Color, intensity, size, lifetime, expression values, real-resource replacement/restoration, dual-blade action slot triggering, pause/resume and ribbon freezing have prior in-game verification. **All weapons, complex PFB types, external asset combinations and future game versions have not been exhaustively tested.** A successful load is not proof of the intended visual result.

## Feedback and contributions

Use [Issues](https://github.com/wzj3196138722-ux/RE-MotionVfxStudio/issues) for bug reports and requests. Include the editor, game and REFramework versions, reproduction steps, target logical resource path, expected/actual behavior, and relevant screenshots or error logs. Review logs for personal paths before sharing. Do not upload game PAKs or your entire asset library.

For code changes, explain the problem, validation performed and whether the change was tested in game. Changes to binary writing, reference replacement or restoration should exercise failure and recovery paths.


See [build instructions](docs/development/BUILD.en.md) for source builds and offline checks.

## Author

**尝试给推搓推的推**

[Bilibili](https://space.bilibili.com/437668597) · [Caimogu](https://www.caimogu.cc/user/1472999.html)

Project code is released under the [MIT license](https://github.com/wzj3196138722-ux/RE-MotionVfxStudio/blob/main/LICENSE); third-party components retain their own licenses. Releases do not contain the game, game PAKs, personal mods or reference-plugin asset libraries.

## Acknowledgements and credits

Thanks to the authors and projects behind the format editing, in-game runtime and development research used here:

- **shadowcookie / kagenocookie — [ReeLib / RE-Engine-Lib](https://github.com/kagenocookie/RE-Engine-Lib):** the file parsing/writing layer uses and modifies this project's code, retaining its MIT license and copyright notice.
- **praydog and contributors — [REFramework](https://github.com/praydog/REFramework):** in-game scripting, object access and plugin APIs.
- **cursey and contributors — [REFramework D2D](https://github.com/cursey/reframework-d2d):** Direct2D drawing for the interface, text and images.
- **[REasy](https://github.com/seifhassine/REasy) and contributors:** Wilds RSZ type definitions; the bundled license retains the Battlezone copyright attribution.
- **The maintainers of [ZstdSharp](https://github.com/oleg-st/ZstdSharp), [xxHash .NET](https://github.com/uranium62/xxHash), Microsoft .NET and Lua:** compression, hashing, runtime and native plugin interface dependencies.
- **Dimcirui's MHWs EFX Editor 0.3.0:** the Blender editor supplied as local reference material informed structure and editing workflow research; it is not a required plugin for this installer.
- **RE Engine VFX Slot & Motion Editor (local metadata credits Antigravity & AZUSA) and WeaponEfxReload v3.1:** early reference materials for action slots, PFBs and resource reloading.
- **praydog's [re-engine-mcp](https://github.com/praydog/re-engine-mcp):** used during development to investigate game objects and runtime interfaces. The bundled MotionVfx MCP is a separate interface scoped to editor operations.
- **[REE-Content-Editor](https://github.com/kagenocookie/REE-Content-Editor) and contributors:** reference work for RE Engine file editing and project research.

See [CREDITS](CREDITS.md) for relationships and license locations. A development reference does not mean that its entire codebase is included; each upstream project's work and attribution remain distinct.
