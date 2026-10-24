param([string]$BackendPath)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $BackendPath) { $BackendPath = Join-Path $projectRoot 'VfxStudio.LiveEditor/publish/VfxStudio.LiveEditor.exe' }
$runRoot = Join-Path $PSScriptRoot ('test-output/host-reliability-' + [Guid]::NewGuid().ToString('N'))
$mailbox = Join-Path $runRoot 'mailbox'
New-Item -ItemType Directory -Path $mailbox -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()
$script:process = $null
$script:launch = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('FAIL: ' + $Name) }
    $results.Add(@{name=$Name;passed=$true})
    Write-Output ('PASS: ' + $Name)
}
function Read-Json([string]$Path) {
    $stream = [IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader = [IO.StreamReader]::new($stream)
    try { $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
}
function Put([hashtable]$Request) {
    if (-not $Request.ContainsKey('id')) { $Request.id = [Guid]::NewGuid().ToString('N') }
    $tmp = Join-Path $mailbox 'request.tmp'
    $Request | ConvertTo-Json -Depth 10 -Compress | Set-Content -LiteralPath $tmp -Encoding utf8
    Move-Item -LiteralPath $tmp -Destination (Join-Path $mailbox 'request.json') -Force
    return $Request.id
}
function Await([string]$Id) {
    for ($n=0; $n -lt 200; $n++) {
        Start-Sleep -Milliseconds 25
        try {
            $response = Read-Json (Join-Path $mailbox 'response.json')
            if ($response.id -eq $Id) { return $response }
        } catch { }
        if ($script:process.HasExited) { throw 'Host exited before sending response.' }
    }
    throw ('Response timeout: ' + $Id)
}
function Send([hashtable]$Request) { return Await (Put $Request) }
function Start-TestHost {
    $script:launch++
    $arguments = '--mailbox "{0}" --assets "{1}" --output "{2}"' -f $mailbox,(Join-Path $projectRoot 'Assets'),(Join-Path $runRoot 'unused-output')
    $script:process = Start-Process -FilePath $BackendPath -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runRoot ('stdout' + $script:launch + '.log')) -RedirectStandardError (Join-Path $runRoot ('stderr' + $script:launch + '.log'))
    for ($n=0; $n -lt 80; $n++) {
        Start-Sleep -Milliseconds 50
        try { $status=Read-Json (Join-Path $mailbox 'status.json'); if($status.ready -and $status.pid -eq $script:process.Id) {return} } catch { }
        if ($script:process.HasExited) { throw 'Host exited during startup.' }
    }
    throw 'Readiness timeout.'
}
$locks = [Collections.Generic.List[IO.FileStream]]::new()
try {
    # Deliberately orphaned: no matching response exists to deduplicate this shutdown.
    $oldId=Put @{action='shutdown'}
    Start-TestHost
    Start-Sleep -Milliseconds 250
    Check (-not $script:process.HasExited) 'orphaned shutdown left by previous session is ignored at startup'
    $opened=Send @{action='open';path=(Join-Path $projectRoot 'Assets/WeaponBases/wp02/11_pl_touch_w_006.efx.5571972')}
    Check ($opened.ok -and $opened.document.entries.Count -gt 0) 'new request after readiness executes normally'
    $count=$opened.document.entries.Count
    $responseLock=[IO.File]::Open((Join-Path $mailbox 'response.json'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $locks.Add($responseLock)
    $mutationId=Put @{action='duplicate_entry';index=0;revision=$opened.document.revision}
    Start-Sleep -Milliseconds 600
    Check (-not $script:process.HasExited) 'host survives Windows replacement denial while response is being read'
    $responseLock.Dispose()
    $modified=Await $mutationId
    Check ($modified.ok -and $modified.document.entries.Count -eq $count+1) 'held response is delivered after reader releases lock and mutation happens once'
    $repeat=Send @{id=$mutationId;action='duplicate_entry';index=0;revision=$opened.document.revision}
    Check ($repeat.ok -and $repeat.document.entries.Count -eq $count+1 -and $repeat.revision -eq $modified.revision) 'repeating acknowledged mutation ID still cannot duplicate it'
    $statusLock=[IO.File]::Open((Join-Path $mailbox 'status.json'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $locks.Add($statusLock)
    Start-Sleep -Milliseconds 2400
    $active=Send @{action='status'}
    Check ($active.ok -and -not $script:process.HasExited) 'heartbeat replacement denial does not terminate request handling'
    $statusLock.Dispose()
    Start-Sleep -Milliseconds 300
    $status=Read-Json (Join-Path $mailbox 'status.json')
    Check ($status.ready -and ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds()-$status.heartbeatUnixSeconds) -le 2) 'heartbeat resumes after reader releases lock'
    $responseLock=[IO.File]::Open((Join-Path $mailbox 'response.json'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $locks.Add($responseLock)
    $shutdownId=Put @{action='shutdown'}
    Start-Sleep -Milliseconds 350
    Check (-not $script:process.HasExited) 'shutdown waits until its response can be published'
    $responseLock.Dispose()
    $stopped=Await $shutdownId
    $script:process.WaitForExit(5000) | Out-Null
    Check ($stopped.ok -and $script:process.HasExited) 'shutdown response is delivered before process exit'
    Start-TestHost
    Start-Sleep -Milliseconds 250
    $fresh=Send @{action='status'}
    Check ($fresh.ok -and -not $script:process.HasExited) 'restarting same mailbox with completed shutdown residue stays ready'
    Send @{action='shutdown'} | Out-Null
    $script:process.WaitForExit(5000) | Out-Null
    [ordered]@{type='isolated host lifecycle and Windows sharing tests';backend=$BackendPath;version='0.1.2';noGameWrites=$true;checks=$results} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'results.json') -Encoding utf8
    Write-Output ('REPORT ' + (Join-Path $runRoot 'results.json'))
} finally {
    foreach($lock in $locks) { $lock.Dispose() }
    if ($script:process -and -not $script:process.HasExited) { Send @{action='shutdown'} | Out-Null; $script:process.WaitForExit(5000) | Out-Null }
}
