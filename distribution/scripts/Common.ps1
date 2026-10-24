$ErrorActionPreference = 'Stop'
$packageRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$configPath = Join-Path $packageRoot 'runtime-config.json'
function Read-Config {
    if (-not (Test-Path -LiteralPath $configPath)) { throw '请先运行「安装或更新.cmd」，选择游戏目录。' }
    $configuration = Get-Content -LiteralPath $configPath -Raw -Encoding utf8 | ConvertFrom-Json
    if (-not (Test-Path -LiteralPath (Join-Path $configuration.gameRoot 'MonsterHunterWilds.exe'))) { throw '配置中的游戏目录无效，请重新运行安装程序。' }
    return $configuration
}
function Read-JsonFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $stream = [IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader = [IO.StreamReader]::new($stream)
    try { return ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
}
function Write-JsonFile([string]$Path, $Value) {
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 35), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
function Mailbox-Path($Configuration) { return (Join-Path $Configuration.gameRoot 'reframework/data/MotionVfxLive') }
function Live-Backend($Configuration) {
    $status = Read-JsonFile (Join-Path (Mailbox-Path $Configuration) 'status.json')
    if ($status -and $status.ready) {
        $age = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - [long]$status.heartbeatUnixSeconds
        if ($age -ge -5 -and $age -le 8) { return $status }
    }
    return $null
}
function Assert-EditorIdle($Configuration) {
    $ui = Read-JsonFile (Join-Path (Mailbox-Path $Configuration) 'ui-status.json')
    if ($ui) {
        $age = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - [long]$ui.observedAt
        if ($age -ge -5 -and $age -le 8 -and ($ui.pendingId -or $ui.aiBusy -or $ui.applyRequested -or $ui.bindings.busy -or
            $ui.bindings.appliedCount -gt 0 -or $ui.bindings.requiresRestoreBeforeReset -or $ui.bindingsRestorePending -or
            $ui.sceneRestorePending -or $ui.actionPreview.busy -or $ui.actionPreview.requiresRestore -or
            $ui.pfbPreview.busy -or $ui.pfbPreview.requiresRestore -or $ui.ownedPreview -or $ui.captureRecording -or $ui.hasDrafts)) {
            throw '请先提交或还原输入，并点击游戏窗口顶部「停止记录及预览，并还原资源与场景」，等完成后再更新或停止后台。'
        }
    }
    return $ui
}
function Send-Backend($Configuration, [hashtable]$Data) {
    $mailbox = Mailbox-Path $Configuration
    $Data.id = 'package-' + [Guid]::NewGuid().ToString('N')
    Write-JsonFile (Join-Path $mailbox 'request.json') $Data
    for ($attempt=0; $attempt -lt 300; $attempt++) {
        Start-Sleep -Milliseconds 100
        try { $reply = Read-JsonFile (Join-Path $mailbox 'response.json') } catch { continue }
        if ($reply -and $reply.id -eq $Data.id) {
            if (-not $reply.ok) { throw ('后台拒绝操作：' + $reply.error) }
            return $reply
        }
    }
    throw '后台响应超时。未强制结束进程，也未重试编辑操作。'
}
function Quote-Native([string]$Value) {
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}
