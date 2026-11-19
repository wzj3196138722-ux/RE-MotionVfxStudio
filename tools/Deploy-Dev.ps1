<#
.SYNOPSIS
  开发用一键部署：把当前源码的 MotionVfx Live 插件和后台部署到本机游戏里（不是给别人用的安装器）。

.DESCRIPTION
  1. 编译 VfxStudio.LiveEditor（-SkipBuild 跳过）。
  2. 停掉正在为这个游戏运行的后台（只认 status.json 里的 pid，且必须是运行 VfxStudio.LiveEditor.dll 的 dotnet 进程）。
  3. 把编译输出复制到独立的运行目录（默认 %LOCALAPPDATA%\MotionVfxLiveDev\<game>\backend），这样之后重新编译不会被运行中的后台锁住。
  4. 同步 Lua 插件、手动脚本和图标到 <游戏>\reframework\...；MotionVfxLive\ 目录里源码已删除的 fx_*.lua 会一并删除。
  5. 以 --game <id> 启动后台，等 status.json 报告就绪且 game 一致。
  只新增或覆盖本编辑器自己的文件，不碰游戏的其他文件。Lua 有变化时，需要在游戏里点 REFramework 的 Reset Scripts。

.EXAMPLE
  .\tools\Deploy-Dev.ps1 -Game re4 -GameRoot "E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4"
.EXAMPLE
  $env:MOTIONVFX_RE4_ROOT = "E:\...\RESIDENT EVIL 4  BIOHAZARD RE4"; .\tools\Deploy-Dev.ps1 -LuaOnly
#>
param(
    [ValidateSet('re4', 'mhwilds')][string]$Game = 're4',
    [string]$GameRoot,
    [string]$StageRoot = (Join-Path $env:LOCALAPPDATA 'MotionVfxLiveDev'),
    [switch]$SkipBuild,
    [switch]$LuaOnly,
    [switch]$BackendOnly
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

# 与 VfxStudio.Core/Games/GameProfile.cs 一致（ExecutableName）。
$exe = @{ re4 = 're4.exe'; mhwilds = 'MonsterHunterWilds.exe' }[$Game]
if (-not $GameRoot) { $GameRoot = [Environment]::GetEnvironmentVariable('MOTIONVFX_' + $Game.ToUpperInvariant() + '_ROOT') }
if (-not $GameRoot) { throw "请用 -GameRoot 指定游戏目录，或设置环境变量 MOTIONVFX_$($Game.ToUpperInvariant())_ROOT。" }
$GameRoot = [IO.Path]::GetFullPath($GameRoot)
if (-not (Test-Path -LiteralPath (Join-Path $GameRoot $exe))) { throw "$GameRoot 里没有 $exe，游戏与 -Game $Game 不符。" }

$reframework = Join-Path $GameRoot 'reframework'
$mailbox = Join-Path $reframework 'data\MotionVfxLive'
$stage = Join-Path $StageRoot $Game
$backendDir = Join-Path $stage 'backend'

function Sync-File([string]$Source, [string]$Target) {
    New-Item -ItemType Directory -Path (Split-Path $Target -Parent) -Force | Out-Null
    if ((Test-Path -LiteralPath $Target) -and
        (Get-FileHash -LiteralPath $Source).Hash -eq (Get-FileHash -LiteralPath $Target).Hash) { return $false }
    Copy-Item -LiteralPath $Source -Destination $Target -Force
    return $true
}

function Read-Status {
    $path = Join-Path $mailbox 'status.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { return Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json } catch { return $null }
}

function Stop-Backend {
    $status = Read-Status
    if (-not $status -or -not $status.pid) { return }
    $process = Get-CimInstance Win32_Process -Filter ("ProcessId = " + [int]$status.pid) -ErrorAction SilentlyContinue
    if (-not $process) { return }
    if ($process.Name -ne 'dotnet.exe' -or $process.CommandLine -notlike '*VfxStudio.LiveEditor.dll*') {
        Write-Warning "status.json 的 pid $($status.pid) 不是 MotionVfx 后台（$($process.Name)），未结束它。"
        return
    }
    Write-Output "结束旧后台 pid $($status.pid)"
    Stop-Process -Id $status.pid -Force
    for ($i = 0; $i -lt 50 -and (Get-Process -Id $status.pid -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 100 }
}

# ---- Lua 插件 ----
if (-not $BackendOnly) {
    $plugin = Join-Path $project 'game-plugin'
    $changed = @()
    $map = @(
        @{ s = 'MotionVfxLive.lua'; t = 'autorun\MotionVfxLive.lua' },
        @{ s = '手动重载MotionVfxLive.lua'; t = 'data\MotionVfxLive\Reload.lua' },
        @{ s = '刷新MotionVfxLive输入框.lua'; t = 'data\MotionVfxLive\RefreshTextInputs.lua' },
        @{ s = '修复MotionVfxLive资源还原.lua'; t = 'data\MotionVfxLive\RepairBindings.lua' }
    )
    foreach ($item in $map) {
        if (Sync-File (Join-Path $plugin $item.s) (Join-Path $reframework $item.t)) { $changed += $item.t }
    }
    $moduleTarget = Join-Path $reframework 'autorun\MotionVfxLive'
    $modules = Get-ChildItem -LiteralPath (Join-Path $plugin 'MotionVfxLive') -Filter 'fx_*.lua' -File
    foreach ($module in $modules) {
        if (Sync-File $module.FullName (Join-Path $moduleTarget $module.Name)) { $changed += ('autorun\MotionVfxLive\' + $module.Name) }
    }
    $names = $modules | ForEach-Object { $_.Name }
    foreach ($stale in Get-ChildItem -LiteralPath $moduleTarget -Filter 'fx_*.lua' -File) {
        if ($names -notcontains $stale.Name) { Remove-Item -LiteralPath $stale.FullName; $changed += ('已删除 ' + $stale.Name) }
    }
    foreach ($image in Get-ChildItem -LiteralPath (Join-Path $plugin 'images\MotionVfxLive') -Filter '*.png' -File) {
        if (Sync-File $image.FullName (Join-Path $reframework ('images\MotionVfxLive\' + $image.Name))) { $changed += ('images\' + $image.Name) }
    }
    if ($changed.Count -gt 0) {
        Write-Output ('Lua 有 ' + $changed.Count + ' 处更新：' + ($changed -join ', '))
        Write-Output '>>> 需要在游戏里点 REFramework 的 Reset Scripts。'
    } else { Write-Output 'Lua 无变化。' }
}

# ---- 后台 ----
if (-not $LuaOnly) {
    if (-not $SkipBuild) {
        & dotnet build (Join-Path $project 'VfxStudio.LiveEditor\VfxStudio.LiveEditor.csproj') -c Release -v q -nologo
        if ($LASTEXITCODE -ne 0) { throw '后台编译失败。' }
    }
    $build = Join-Path $project 'VfxStudio.LiveEditor\bin\Release\net8.0'
    Stop-Backend
    if (Test-Path -LiteralPath $backendDir) { Remove-Item -LiteralPath $backendDir -Recurse -Force }
    New-Item -ItemType Directory -Path $backendDir, (Join-Path $stage 'assets'), (Join-Path $stage 'output') -Force | Out-Null
    Copy-Item -Path (Join-Path $build '*') -Destination $backendDir -Recurse -Force
    New-Item -ItemType Directory -Path $mailbox -Force | Out-Null

    $arguments = @((Join-Path $backendDir 'VfxStudio.LiveEditor.dll'), '--game', $Game, '--game-root', $GameRoot,
        '--mailbox', $mailbox, '--log-directory', $mailbox, '--assets', (Join-Path $stage 'assets'), '--output', (Join-Path $stage 'output'))
    $line = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $process = Start-Process -FilePath 'dotnet' -ArgumentList $line -WorkingDirectory $backendDir -WindowStyle Hidden -PassThru
    for ($i = 0; $i -lt 100; $i++) {
        Start-Sleep -Milliseconds 200
        if ($process.HasExited) { throw "后台启动后立即退出（代码 $($process.ExitCode)）；日志在 $mailbox。" }
        $status = Read-Status
        if ($status -and $status.ready -and $status.pid -eq $process.Id) {
            if ($status.game -ne $Game) { throw "后台报告的游戏是 $($status.game)，不是 $Game。" }
            Write-Output "后台已就绪：pid $($process.Id)，game $($status.game)，运行目录 $backendDir"
            return
        }
    }
    throw "后台 20 秒内没有报告就绪；pid $($process.Id)，日志在 $mailbox。"
}
