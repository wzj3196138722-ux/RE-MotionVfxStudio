param(
    [Parameter(Mandatory=$true)][string]$GameRoot,
    [Parameter(Mandatory)][string]$LogicalResourcePath,
    [switch]$InspectDirectPointers,
    [switch]$InspectStringTableHeader,
    [switch]$InspectNativeTypeMetadata
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($InspectNativeTypeMetadata -and ($InspectDirectPointers -or $InspectStringTableHeader)) {
    throw 'Native type metadata inspection is a separate diagnostic action.'
}
$root = (Resolve-Path -LiteralPath $GameRoot).Path
if (!(Test-Path -LiteralPath (Join-Path $root 'MonsterHunterWilds.exe'))) { throw 'Expected MonsterHunterWilds.exe in GameRoot.' }
$mailbox = Join-Path $root 'reframework/data/MotionVfxLive'
$plugin = Get-Content -LiteralPath (Join-Path $mailbox 'cache-probe-plugin-status.json') -Raw | ConvertFrom-Json -AsHashtable
if ($plugin['ready'] -ne $true) { throw 'The in-game read-only diagnostics component is not ready.' }
$ui = Get-Content -LiteralPath (Join-Path $mailbox 'ui-status.json') -Raw | ConvertFrom-Json -AsHashtable
$age = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - [long]$ui['observedAt']
if ($age -lt -2 -or $age -gt 8) { throw 'UI source diagnostics are stale.' }
$logical = $LogicalResourcePath.Trim().Replace('\', '/').ToLowerInvariant()
$matches = @($ui['captureResources'] | Where-Object { $_ -is [Collections.IDictionary] -and $_['canonicalPath'] -eq $logical })
if ($matches.Count -ne 1 -or $matches[0]['localConfirmed'] -ne $true) { throw 'No unique confirmed resource is retained; record the action and keep its list.' }
$source = $matches[0]['latestSource']
if ($source -isnot [Collections.IDictionary] -or $source['availability'] -ne 'retained_observation' -or
    $source['sourceKind'] -ne 'actual_effect_player_holder' -or $source['localConfirmed'] -ne $true) { throw 'No retained actual resource holder is available.' }
$request = [ordered]@{
    id = 'cache-probe-' + [Guid]::NewGuid().ToString('N')
    action = $(if ($InspectNativeTypeMetadata) { 'native_type_metadata' } else { 'probe' })
    logicalResourcePath = $logical
    sourceId = $source['sourceId']
    eventId = $source['eventId']
    holderIdentity = $source['actualHolderIdentity']
    sceneEpoch = $source['sceneEpoch']
    loadedAt = $ui['loadedAt']
    expectedActualPath = $source['actualPath']
    inspectDirectPointers = $InspectDirectPointers.IsPresent
    inspectStringTableHeader = $InspectStringTableHeader.IsPresent
}
$requestPath = Join-Path $mailbox 'cache-probe-request.json'
$temporary = Join-Path $mailbox ($request.id + '.tmp')
[IO.File]::WriteAllText($temporary, ($request | ConvertTo-Json -Depth 6 -Compress), [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporary -Destination $requestPath -Force
$responsePath = Join-Path $mailbox 'cache-probe-response.json'
$timer = [Diagnostics.Stopwatch]::StartNew()
while ($timer.Elapsed.TotalSeconds -lt 40) {
    Start-Sleep -Milliseconds 100
    if (!(Test-Path -LiteralPath $responsePath)) { continue }
    try {
        $text = Get-Content -LiteralPath $responsePath -Raw
        $response = $text | ConvertFrom-Json -AsHashtable
    } catch [IO.IOException] { continue } catch [ArgumentException] { continue }
    if ($response['id'] -ne $request.id) { continue }
    $output = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../test-output'))
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $report = Join-Path $output ($request.id + '-ingame.json')
    [IO.File]::WriteAllText($report, $text, [Text.UTF8Encoding]::new($false))
    Write-Output $report
    $expectedStatus = if ($InspectNativeTypeMetadata) { 'native_type_metadata_complete_not_authenticated' } else { 'diagnostic_complete_not_authenticated' }
    if ($response['status'] -ne $expectedStatus) { throw ('Read-only in-game probe rejected: ' + $response['error']) }
    Write-Output 'Read-only diagnosis returned. Results are unverified candidates and are not available to the editor.'
    exit 0
}
throw 'No matching diagnostics response within 40 seconds. No request was repeated.'
