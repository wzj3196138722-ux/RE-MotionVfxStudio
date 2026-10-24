# Building and publishing / 构建与发布

The public Windows release is built from the repository by `.github/workflows/release.yml`. A push to `main` in `wzj3196138722-ux/RE-MotionVfxStudio`, including the initial source import, checks the version in `game-plugin/MotionVfxLive.lua` against the installer project. If neither its `v<version>` tag nor its GitHub Release exists, it builds and publishes that version. A manual workflow run on `main` follows the same checks.

The workflow uses only the repository's temporary `GITHUB_TOKEN` with `contents: write`. Checkout does not retain credentials. No personal access token, local user account, game installation, or game file is needed. Forks do not publish automatically. Repository or organization rules can still prevent Actions from running or creating a release; a failure must be inspected, not treated as a successful publication.

## Build inputs

- Windows 2022 hosted runner with Visual C++ tools and Windows SDK.
- .NET SDK **10.0.300**; published Windows x64 apps include the **10.0.8** runtime.
- Vendored REFramework API **1.15.0** headers and Lua **5.4.3** source under `third-party/reframework-sdk`, checked against `SHA256.json` before compiling.
- Bundled REFramework-D2D **1.4.1** with its license. Proprietary game assets and local edit sessions are excluded.
- Bilingual notes at `.github/release-notes/<version>.md`. Update UI and installer versions together before publishing a later version.

The native build runs its input tests. The pipeline also checks all production Lua syntax and the offline file-dialog, file-explorer and D2D-workbench suites. The resulting EXE runs `--self-test` in a new simulated installation directory, checking installation, repair, rollback, cleanup policy and user-file preservation. The simulated executable is only a marker and is never launched. The workflow does not pass a proprietary EFX fixture and does not run the earlier six-version upgrade matrix or live-game visual tests. The Release includes the actual check report and build metadata so these scopes remain visible.

## Published files

The Release contains the standalone `MotionVfxLive-<version>-Setup.exe`, a portable ZIP, one SHA256 sidecar for each, the installer check report and build metadata. The EXE alone is enough for normal installation; the user must already have the game and a working REFramework installation and close the game before installing/updating.

These are **fresh CI builds**, not byte-for-byte uploads of the earlier local installer. Source packaging, build timestamps and compilation can change hashes even with the same app and runtime version. Use the `.sha256` file attached to the particular Release you download. The earlier local package's six-version test record belongs to that earlier binary and is not presented as evidence for the CI binary.

```powershell
Get-FileHash -Algorithm SHA256 .\MotionVfxLive-0.8.5-Setup.exe
Get-Content .\MotionVfxLive-0.8.5-Setup.exe.sha256
```

## Existing tags and failed publication

An existing tag **or** Release makes the workflow skip publication. It never deletes a tag, replaces an uploaded asset, or uses a force update. Immediately before publication it checks again, then creates a new tag pointing to the tested commit using the create-reference API. An existing reference causes an error instead of being replaced.

If an upload fails after creating the tag, the workflow keeps the tag and any partial Release intact. A later automatic run will skip it. Inspect the failed run and retained build artifacts; finish that publication deliberately or increment the version. The workflow must not silently replace something users may already have downloaded. Action artifacts are retained for 14 days; published Release assets are separate.

## 中文

推送到本仓库的 `main` 后（包括首次上传源码），工作流读取 UI 版本并与安装器核对。对应的标签和 Release 都不存在时，自动构建、测试并发布 EXE、便携 ZIP、校验文件和测试记录。也可以在 Actions 页面手动运行，仍需选择 `main`。

构建使用固定的 .NET SDK 10.0.300，包内运行库固定为 10.0.8。原生组件使用仓库内已记录哈希的 REFramework API 1.15.0 和 Lua 5.4.3；不临时下载浮动版本的 SDK，也不需要游戏文件。工作流只在模拟目录测试安装、修复、回滚和用户文件保留；它不会启动游戏，不含带真实 EFX 的测试，也没有重新执行旧安装包六版本矩阵。

GitHub Release 中的文件是从公开源码重新构建的版本，和之前本地交付的安装包哈希可能不同。下载后应使用同一次 Release 附带的 SHA256 对照；不能把原本本地安装包的测试记录算成这个新文件的测试结果。

已有标签或 Release 会被完整保留并跳过，不覆盖、不强推、不删除。如果标签创建后上传失败，会保留现场；请查看失败日志和 14 天内保留的 Actions 构建产物，确认后补完发布或提升版本号。正常用户只需下载 EXE，先退出游戏，再安装或更新；游戏与 REFramework 需自行准备。

## References and acknowledgements / 参考与感谢

The automation follows GitHub's [token permissions documentation](https://docs.github.com/en/actions/tutorials/authenticate-with-github_token), [release creation CLI](https://cli.github.com/manual/gh_release_create), and official [checkout](https://github.com/actions/checkout), [setup-dotnet](https://github.com/actions/setup-dotnet), and [upload-artifact](https://github.com/actions/upload-artifact) actions. Thanks to their maintainers, REFramework/praydog, REFramework-D2D and Lua.org / PUC-Rio. See the repository notices for the complete adapted-code and dependency credits.

感谢 GitHub Actions、REFramework、REFramework-D2D、Lua 及本项目注明的上游作者；完整改编来源和许可见仓库中的第三方说明。
