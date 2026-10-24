param([Parameter(Mandatory=$true)][string]$GameRoot, [string]$RecoveryFile)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name MonsterHunterWilds -ErrorAction SilentlyContinue) {
    throw '请关闭游戏后再安装 D2D 工作台。尚未替换任何文件。'
}
$prepared = Join-Path $PSScriptRoot 'prepared-0.6.3'
$manifest = Get-Content -LiteralPath (Join-Path $prepared 'SHA256.json') -Raw -Encoding utf8 | ConvertFrom-Json
foreach ($entry in $manifest) {
    $path=[IO.Path]::GetFullPath((Join-Path $prepared $entry.path))
    if (-not $path.StartsWith([IO.Path]::GetFullPath($prepared).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Prepared manifest path escaped its directory.' }
    if ((Get-FileHash -LiteralPath $path).Hash -ne $entry.sha256) { throw ('预备文件已变化：'+$entry.path) }
}
& (Join-Path $PSScriptRoot 'Install-GamePlugin.ps1') -GameRoot $GameRoot -ValidateOnly
& (Join-Path $PSScriptRoot 'Update-Backend.ps1') -GameRoot $GameRoot -PreparedDirectory (Join-Path $prepared 'backend') -RecoveryFile $RecoveryFile
& (Join-Path $PSScriptRoot 'Install-GamePlugin.ps1') -GameRoot $GameRoot
Write-Output 'D2D 工作台已安装；游戏未启动。下次启动游戏后加载 0.6.3。'
