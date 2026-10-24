param([switch]$NonInteractive)
. (Join-Path $PSScriptRoot 'Common.ps1')
$configuration = Read-Config
$status = Live-Backend $configuration
if (-not $status) { Write-Host '后台当前未运行。'; return }
Assert-EditorIdle $configuration | Out-Null
$current = Send-Backend $configuration @{action='status'}
$checkpoint = $null
if ($current.document) {
    $checkpoint = (Send-Backend $configuration @{action='save';revision=$current.document.revision}).outputPath
    $latest = Send-Backend $configuration @{action='status'}
    if ($latest.document.revision -ne $current.document.revision) { throw '文档刚刚发生变化；请结束编辑后再停止后台。' }
}
Write-JsonFile (Join-Path $packageRoot 'saved-session.json') @{checkpoint=$checkpoint;document=$current.document;oldBackendVersion=$status.version;at=[DateTimeOffset]::UtcNow.ToString('o')}
Assert-EditorIdle $configuration | Out-Null
Send-Backend $configuration @{action='shutdown';revision=$current.revision} | Out-Null
$stopped = $false
for ($attempt=0; $attempt -lt 100; $attempt++) {
    Start-Sleep -Milliseconds 100
    $latestStatus = Read-JsonFile (Join-Path (Mailbox-Path $configuration) 'status.json')
    if ($latestStatus -and -not $latestStatus.ready) { $stopped=$true; break }
}
if (-not $stopped) { throw '后台尚未确认停止；未强制关闭进程。' }
Write-Host '后台已停止，当前文档已保存到恢复副本。下次启动会恢复。'
if (-not $NonInteractive) { Read-Host '按回车关闭' | Out-Null }
