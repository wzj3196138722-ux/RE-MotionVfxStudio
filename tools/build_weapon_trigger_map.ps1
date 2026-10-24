param(
    [Parameter(Mandatory)][string]$Candidates,
    [Parameter(Mandatory)][string]$FixedIds,
    [Parameter(Mandatory)][string]$EffectIds,
    [Parameter(Mandatory)][string]$Output
)

$fixedByValue = @{}
foreach ($entry in Import-Csv -LiteralPath $FixedIds) { $fixedByValue[$entry.Value] = $entry.Name }
$idByName = @{}
foreach ($entry in Import-Csv -LiteralPath $EffectIds) { $idByName[$entry.Name] = $entry }
$rows = [Collections.Generic.List[object]]::new()
$unmatched = [Collections.Generic.List[object]]::new()
foreach ($candidate in Import-Csv -LiteralPath $Candidates) {
    $fixed = $candidate.'fixed ID'
    if ([string]::IsNullOrWhiteSpace($fixed)) { continue }
    $trigger = $fixedByValue[$fixed]
    if (-not $trigger) { $unmatched.Add($candidate); continue }
    $effect = $idByName[$trigger]
    $rows.Add([pscustomobject]@{
        Weapon = $candidate.weapon
        TriggerName = $trigger
        TriggerId = if ($effect) { $effect.Value } else { '' }
        TriggerHex = if ($effect) { $effect.Hex } else { '' }
        FixedId = $fixed
        Provider = $candidate.provider
        SourceObject = $candidate.'source object'
        RawResource = $candidate.'raw resource'
        LocalEfx = $candidate.'local EFX'
    })
}
$rows | Sort-Object Weapon, TriggerName, RawResource, Provider -Unique |
    Export-Csv -LiteralPath $Output -NoTypeInformation -Encoding utf8
Write-Output "Matched $($rows.Count) PFB resource references across $((($rows | Select-Object -ExpandProperty Weapon -Unique) | Measure-Object).Count) weapons; unmatched fixed IDs: $($unmatched.Count)"
