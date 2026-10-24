param([string]$GameRoot, [switch]$NonInteractive)
. (Join-Path $PSScriptRoot 'Common.ps1')
if (-not $GameRoot) {
    if (Test-Path -LiteralPath $configPath) { $GameRoot = (Read-Config).gameRoot }
    elseif ($NonInteractive) { throw '请指定 -GameRoot。' }
    else { $GameRoot = (Read-Host '请输入包含 MonsterHunterWilds.exe 的游戏目录').Trim('"') }
}
$gamePath = (Resolve-Path -LiteralPath $GameRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $gamePath 'MonsterHunterWilds.exe'))) { throw '该目录不包含 MonsterHunterWilds.exe。' }
if (-not (Test-Path -LiteralPath (Join-Path $gamePath 'dinput8.dll'))) { throw '请先安装 REFramework，再运行本安装程序。详见包内 README.md 与 docs/tutorials.zh-CN.md。' }
$configuration = [ordered]@{ gameRoot=$gamePath; output=(Join-Path $packageRoot 'Exports'); localAssets=(Join-Path $packageRoot 'LocalFiles') }
Assert-EditorIdle $configuration | Out-Null
# Preflight every distributed source before stopping a running editor.
$items = @(
    @{source='plugin/MotionVfxLive.lua'; target='reframework/autorun/MotionVfxLive.lua'},
    @{source='plugin/Reload.lua'; target='reframework/data/MotionVfxLive/Reload.lua'},
    @{source='plugin/RefreshTextInputs.lua'; target='reframework/data/MotionVfxLive/RefreshTextInputs.lua'},
    @{source='plugin/RepairBindings.lua'; target='reframework/data/MotionVfxLive/RepairBindings.lua'},
    @{source='images/MotionVfxLive/logo.png'; target='reframework/images/MotionVfxLive/logo.png'},
    @{source='images/MotionVfxLive/author-logo.png'; target='reframework/images/MotionVfxLive/author-logo.png'},
    @{source='plugin/reframework-d2d.lua'; target='reframework/autorun/reframework-d2d.lua'},
    @{source='native/reframework-d2d.dll'; target='reframework/plugins/reframework-d2d.dll'},
    @{source='licenses/REFramework-D2D-LICENSE.txt'; target='reframework/plugins/reframework-d2d-LICENSE.txt'},
    @{source='native/MotionVfxDebugInput.dll'; target='reframework/plugins/MotionVfxDebugInput.dll'},
    @{source='licenses/MotionVfxDebugInput-THIRD-PARTY.txt'; target='reframework/plugins/MotionVfxDebugInput-LICENSE.txt'}
)
$modules=@(Get-ChildItem -LiteralPath (Join-Path $packageRoot 'plugin/MotionVfxLive') -Filter '*.lua' -File | Sort-Object Name)
if(-not $modules.Count){throw '包体缺少编辑器模块。'}
foreach($module in $modules){
    if($module.Name -notmatch '^fx_[a-z0-9_]+\.lua$'){throw ('包体包含无效模块：'+$module.Name)}
    $items+=@{source=('plugin/MotionVfxLive/'+$module.Name);target=('reframework/autorun/MotionVfxLive/'+$module.Name)}
}
foreach ($item in $items) { if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $item.source))) { throw ('包体缺少文件：' + $item.source) } }
foreach ($item in $items | Where-Object { $_.target.EndsWith('.dll') }) {
    $nativeSource = Join-Path $packageRoot $item.source
    $nativeTarget = Join-Path $gamePath $item.target
    $changed = -not (Test-Path -LiteralPath $nativeTarget)
    if (-not $changed) { $changed = (Get-FileHash -LiteralPath $nativeSource).Hash -ne (Get-FileHash -LiteralPath $nativeTarget).Hash }
    if ($changed -and (Get-Process -Name MonsterHunterWilds -ErrorAction SilentlyContinue)) {
        throw '本次新增或更新了输入/D2D 组件，请先关闭游戏后再安装。尚未替换任何脚本。'
    }
}
foreach ($binary in @('backend/VfxStudio.LiveEditor.exe','backend/desktop/MotionVfxDesktop.exe','mcp/VfxStudio.Mcp.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $binary))) { throw ('包体缺少程序：' + $binary) }
}
Write-JsonFile $configPath $configuration
if (Live-Backend $configuration) { & (Join-Path $PSScriptRoot 'Stop-Backend.ps1') -NonInteractive }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$backupRoot = Join-Path $gamePath ('reframework/data/MotionVfxLive/backups/package-' + $stamp)
$records = @()
foreach ($item in $items) {
    $source = Join-Path $packageRoot $item.source
    $destination = [IO.Path]::GetFullPath((Join-Path $gamePath $item.target))
    if (-not $destination.StartsWith($gamePath.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '安装路径超出选定的游戏目录。' }
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination).Hash -eq $hash) { continue }
    $backup = $null
    if (Test-Path -LiteralPath $destination) {
        $backup = Join-Path $backupRoot $item.target
        New-Item -ItemType Directory -Path (Split-Path $backup -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $destination -Destination $backup
    }
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
    $records += @{destination=$destination;backup=$backup;sha256=$hash}
}
Write-JsonFile (Join-Path $gamePath ('reframework/data/MotionVfxLive/package-install-' + $stamp + '.json')) @{at=[DateTimeOffset]::UtcNow.ToString('o');files=$records}
New-Item -ItemType Directory -Path $configuration.output,$configuration.localAssets -Force | Out-Null
& (Join-Path $PSScriptRoot 'Mcp-Config.ps1') -NonInteractive
& (Join-Path $PSScriptRoot 'Start-Backend.ps1') -NonInteractive
Write-Host '安装完成。新增或更新输入拦截 DLL 后请重启游戏；仅重载 Lua 不能加载该组件。只更新界面时可以运行：'
Write-Host (Join-Path $gamePath 'reframework/data/MotionVfxLive/Reload.lua')
Write-Host '请完整保留此文件夹；保存副本位于 Exports，MCP 配置已生成。'
if (-not $NonInteractive) { Read-Host '按回车关闭' | Out-Null }
