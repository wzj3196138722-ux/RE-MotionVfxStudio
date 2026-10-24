param([switch]$NonInteractive)
. (Join-Path $PSScriptRoot 'Common.ps1')
$configuration = Read-Config
$mailbox = Mailbox-Path $configuration
$hasher = [Security.Cryptography.SHA256]::Create()
try { $key = [BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($mailbox.ToLowerInvariant()))).Replace('-','') }
finally { $hasher.Dispose() }
$mutex = [Threading.Mutex]::new($false, ('Local\MotionVfxLiveBackendStart_' + $key))
$owns = $false
try {
    try { $owns = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owns=$true }
    if (-not $owns) { throw '已有启动操作正在进行，请等待。' }
    $status = Live-Backend $configuration
    if ($status) {
        $release = Read-JsonFile (Join-Path $packageRoot 'release.json')
        if ($release -and $status.version -ne $release.backendVersion) { throw '正在运行其他版本后台，请先运行「安装或更新.cmd」。' }
        Write-Host '编辑器后台已就绪。'; return
    }
    New-Item -ItemType Directory -Path $mailbox,$configuration.output,$configuration.localAssets -Force | Out-Null
    $lock = Join-Path $mailbox 'backend.lock'
    if (Test-Path -LiteralPath $lock) {
        $probe = $null
        try { $probe=[IO.File]::Open($lock,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None) }
        catch [IO.IOException] { throw '已有后台占用编辑器目录，但未确认就绪；请检查游戏目录下 MotionVfxLive 的日志。' }
        finally { if($probe) {$probe.Dispose()} }
    }
    $argsList = @('--mailbox',$mailbox,'--assets',$configuration.localAssets,'--game-root',$configuration.gameRoot,'--output',$configuration.output,'--log-directory',$mailbox)
    $argumentLine = ($argsList | ForEach-Object {Quote-Native $_}) -join ' '
    $process = Start-Process -FilePath (Join-Path $packageRoot 'backend/VfxStudio.LiveEditor.exe') -ArgumentList $argumentLine -WorkingDirectory $packageRoot -WindowStyle Hidden -PassThru
    $ready=$false
    for ($attempt=0;$attempt -lt 80;$attempt++) {
        Start-Sleep -Milliseconds 100
        if($process.HasExited) {throw '后台启动失败，请查看 backend.stderr.log。'}
        $status=Live-Backend $configuration
        if($status -and $status.pid -eq $process.Id) {$ready=$true;break}
    }
    if(-not $ready) {throw '后台尚未确认就绪；未重复启动。'}
    $sessionPath=Join-Path $packageRoot 'saved-session.json'
    $session=Read-JsonFile $sessionPath
    if($session -and $session.checkpoint) {
        $opened=Send-Backend $configuration @{action='open';path=$session.checkpoint}
        if($session.document.sourceKind -eq 'game_files' -and $session.document.gameSource) {
            try {
                $ui=Read-JsonFile (Join-Path $mailbox 'ui-status.json')
                if(-not $ui.captureActor.sceneEpoch) {throw '尚无可核验的角色动作记录'}
                $restored=Send-Backend $configuration @{action='restore_game_checkpoint';checkpointPath=$session.checkpoint;sourceDescriptor=$session.document.gameSource;currentSceneEpoch=$ui.captureActor.sceneEpoch;expectedContentHash=$session.document.gameSource.contentHash;revision=$opened.document.revision}
                # 0.3.4 normalized duplicate-named native groups incorrectly.
                # A completely unedited document can be reacquired from source;
                # edited checkpoints are kept and never silently replaced.
                if($session.oldBackendVersion -eq '0.3.4' -and -not $session.document.dirty -and -not $session.document.canUndo -and -not $session.document.canRedo) {
                    Send-Backend $configuration @{action='open_game_resource';sourceDescriptor=$session.document.gameSource;currentSceneEpoch=$ui.captureActor.sceneEpoch;revision=$restored.document.revision} | Out-Null
                }
            } catch {Write-Warning ('编辑副本已恢复，游戏来源需从动作列表重新选择：' + $_.Exception.Message)}
        }
        Move-Item -LiteralPath $sessionPath -Destination (Join-Path $packageRoot ('saved-session-restored-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.json'))
    }
    Write-Host '编辑器后台已启动。现在可以在游戏内使用 MotionVfx Live。'
} finally {if($owns){$mutex.ReleaseMutex()};$mutex.Dispose()}
if(-not $NonInteractive){Read-Host '按回车关闭' | Out-Null}
