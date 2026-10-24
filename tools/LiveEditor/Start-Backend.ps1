param(
    [Parameter(Mandatory=$true)][string]$GameRoot,
    [switch]$Build,
    [switch]$Install,
    [string]$BackendDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$gamePath = (Resolve-Path -LiteralPath $GameRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $gamePath 'MonsterHunterWilds.exe'))) { throw 'Selected directory does not contain MonsterHunterWilds.exe.' }
$mailbox = Join-Path $gamePath 'reframework/data/MotionVfxLive'
$publishDir = if ($BackendDirectory) { [IO.Path]::GetFullPath($BackendDirectory) } else { Join-Path $PSScriptRoot 'backend' }
$exe = Join-Path $publishDir 'VfxStudio.LiveEditor.exe'
$statusPath = Join-Path $mailbox 'status.json'
$hasher = [Security.Cryptography.SHA256]::Create()
try { $mailboxKey = [BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($mailbox.ToLowerInvariant()))).Replace('-', '') }
finally { $hasher.Dispose() }
$launchMutex = [Threading.Mutex]::new($false, ('Local\MotionVfxLiveBackendStart_' + $mailboxKey))
$ownsLaunchMutex = $false
try {
try { $ownsLaunchMutex = $launchMutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsLaunchMutex = $true }
if (-not $ownsLaunchMutex) { throw 'Another launcher is already starting this mailbox. Wait for its result.' }

$statusFresh = $false
$running = $null
if (Test-Path -LiteralPath $statusPath) {
    try {
        $status = Get-Content -LiteralPath $statusPath -Raw -Encoding utf8 | ConvertFrom-Json
        $running = Get-Process -Id $status.pid -ErrorAction SilentlyContinue
        $age = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - [long]$status.heartbeatUnixSeconds
        $sameMailbox = $status.mailbox -and [IO.Path]::GetFullPath($status.mailbox).TrimEnd('\') -eq [IO.Path]::GetFullPath($mailbox).TrimEnd('\')
        # The backend can legitimately be started from its project publish directory.
        # Match the mailbox, protocol, live process and heartbeat, not one packaging path.
        $statusFresh = $status.ready -and $status.protocol -eq 1 -and $sameMailbox -and
            $age -ge -5 -and $age -le 8 -and (-not $running -or $running.ProcessName -eq 'VfxStudio.LiveEditor')
    } catch { Write-Verbose $_.Exception.Message }
}
$backendLock = Join-Path $mailbox 'backend.lock'
$mailboxLocked = $false
if (Test-Path -LiteralPath $backendLock) {
    $probe = $null
    try { $probe = [IO.File]::Open($backendLock, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None) }
    catch [IO.IOException] { $mailboxLocked = $true }
    finally { if ($probe) { $probe.Dispose() } }
}
if ($mailboxLocked) {
    if (-not $statusFresh) { throw 'This mailbox is already locked, but its ready status and heartbeat could not be verified. No second backend was started; inspect the existing process and logs.' }
    # A fresh heartbeat plus the exclusive mailbox lock also works when Windows
    # does not allow this launcher to enumerate the existing backend process.
    if ($Install) { & (Join-Path $PSScriptRoot 'Install-GamePlugin.ps1') -GameRoot $gamePath }
    if ($Build) { Write-Warning 'Build deferred because this mailbox already has a running backend. Save pending edits and shut it down before rebuilding.' }
    Write-Output ('Live editor backend is already running. PID=' + $status.pid + $(if ($running) { '; ' + $running.Path } else { '; verified by live heartbeat and mailbox lock' }))
    return
}
if ($Build -or -not (Test-Path -LiteralPath $exe)) {
    & dotnet publish (Join-Path $projectRoot 'VfxStudio.LiveEditor/VfxStudio.LiveEditor.csproj') -c Release -p:MotionVfxPortable=true -o $publishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Live editor backend build failed.' }
    & dotnet publish (Join-Path $projectRoot 'tools/DesktopBridge/MotionVfxDesktop.csproj') -c Release -o (Join-Path $publishDir 'desktop') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Desktop file selection and replay component build failed.' }
}
if ($Install) { & (Join-Path $PSScriptRoot 'Install-GamePlugin.ps1') -GameRoot $gamePath }
New-Item -ItemType Directory -Path $mailbox -Force | Out-Null
function Quote-Native([string]$Value) {
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}
$arguments = @('--mailbox', $mailbox, '--assets', (Join-Path $projectRoot 'Assets'), '--game-root', $gamePath, '--output', (Join-Path $projectRoot 'LiveEditorOutput'))
$arguments += @('--log-directory', $mailbox)
$argumentLine = ($arguments | ForEach-Object { Quote-Native $_ }) -join ' '
# The backend owns its log files; no PowerShell pipe pump has to stay alive.
$process = Start-Process -FilePath $exe -ArgumentList $argumentLine -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru
for ($attempt = 0; $attempt -lt 40; $attempt++) {
    Start-Sleep -Milliseconds 200
    if ($process.HasExited) { throw ('Backend exited. See ' + (Join-Path $mailbox 'backend.stderr.log')) }
    if (Test-Path -LiteralPath $statusPath) {
        try {
            $status = Get-Content -LiteralPath $statusPath -Raw -Encoding utf8 | ConvertFrom-Json
            if ($status.ready -and $status.pid -eq $process.Id) { Write-Output ('Live editor backend ready. PID=' + $process.Id); return }
        } catch { Write-Verbose $_.Exception.Message }
    }
}
throw 'Backend did not confirm readiness; inspect its logs.'
} finally {
    if ($ownsLaunchMutex) { $launchMutex.ReleaseMutex() }
    $launchMutex.Dispose()
}
