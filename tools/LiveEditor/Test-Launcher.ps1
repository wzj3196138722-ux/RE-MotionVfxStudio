param([string]$BackendPath)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $BackendPath) { $BackendPath = Join-Path $projectRoot 'VfxStudio.LiveEditor/publish/VfxStudio.LiveEditor.exe' }
$runRoot = Join-Path $PSScriptRoot ('test-output/launcher-' + [Guid]::NewGuid().ToString('N'))
$game = Join-Path $runRoot 'mock-game'
$mailbox = Join-Path $game 'reframework/data/MotionVfxLive'
New-Item -ItemType Directory -Path $mailbox -Force | Out-Null
[IO.File]::WriteAllBytes((Join-Path $game 'MonsterHunterWilds.exe'), [byte[]]@())
$arguments = '--mailbox "{0}" --assets "{1}" --game-root "{2}" --output "{3}"' -f $mailbox,(Join-Path $projectRoot 'Assets'),$game,(Join-Path $runRoot 'saved')
$process = Start-Process -FilePath $BackendPath -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runRoot 'stdout.log') -RedirectStandardError (Join-Path $runRoot 'stderr.log')
$checks = [Collections.Generic.List[object]]::new()
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('FAIL: ' + $Name) }
    $checks.Add(@{name=$Name;passed=$true})
    Write-Output ('PASS: ' + $Name)
}
$windowsPowerShell = Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
$launcher = Join-Path $PSScriptRoot 'Start-Backend.ps1'
try {
    $status = $null
    for ($n=0; $n -lt 50; $n++) {
        Start-Sleep -Milliseconds 100
        $statusPath = Join-Path $mailbox 'status.json'
        if (Test-Path -LiteralPath $statusPath) {
            $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
            if ($status.ready) { break }
        }
    }
    Check ($status.ready -and $status.pid -eq $process.Id) 'isolated backend starts from an alternate publish directory'
    $stdoutBefore = Get-Item -LiteralPath (Join-Path $runRoot 'stdout.log')
    $stdoutLength = $stdoutBefore.Length
    $stdoutWritten = $stdoutBefore.LastWriteTimeUtc
    $binaryHash = (Get-FileHash -LiteralPath $BackendPath).Hash
    $output = & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $launcher -GameRoot $game
    Check ($LASTEXITCODE -eq 0 -and ($output -join ' ') -match 'already running') 'Windows PowerShell 5.1 reuses the alternate-path backend'
    $output = & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $launcher -GameRoot $game -Build
    Check ($LASTEXITCODE -eq 0 -and ($output -join ' ') -match 'already running') '-Build does not replace or restart an active backend'
    $after = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    Check ($after.pid -eq $process.Id -and $after.startedAt -eq $status.startedAt) 'repeated startup retains the same backend session'
    Check ((Get-FileHash -LiteralPath $BackendPath).Hash -eq $binaryHash) 'active executable bytes remain unchanged'
    $stdoutAfter = Get-Item -LiteralPath (Join-Path $runRoot 'stdout.log')
    Check ($stdoutAfter.Length -eq $stdoutLength -and $stdoutAfter.LastWriteTimeUtc -eq $stdoutWritten) 'repeated startup does not truncate existing process logs'
    $lockName = 'Local\MotionVfxLiveBackendStart_' + $(
        $hasher=[Security.Cryptography.SHA256]::Create()
        try { [BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($mailbox.ToLowerInvariant()))).Replace('-', '') }
        finally { $hasher.Dispose() }
    )
    $launchMutex = [Threading.Mutex]::new($false, $lockName)
    $owned = $launchMutex.WaitOne(0)
    try {
        Check $owned 'test acquires the launch coordination mutex'
        $blockedOutput = & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $launcher -GameRoot $game 2>&1
        Check ($LASTEXITCODE -ne 0 -and ($blockedOutput -join ' ') -match 'Another launcher') 'concurrent launch attempts fail before starting another process'
    } finally {
        if ($owned) { $launchMutex.ReleaseMutex() }
        $launchMutex.Dispose()
    }
    [ordered]@{type='isolated launcher integration';backendPath=$BackendPath;gamePathIsMock=$true;checks=$checks} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'results.json') -Encoding utf8
    Write-Output ('RESULT launcher: ' + $checks.Count + ' passed; report ' + (Join-Path $runRoot 'results.json'))
} finally {
    if (-not $process.HasExited) {
        @{id=[Guid]::NewGuid().ToString('N');action='shutdown'} | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $mailbox 'request.json') -Encoding utf8
        $process.WaitForExit(5000) | Out-Null
        if (-not $process.HasExited) { Write-Warning ('Test backend is still running: ' + $process.Id) }
    }
}
