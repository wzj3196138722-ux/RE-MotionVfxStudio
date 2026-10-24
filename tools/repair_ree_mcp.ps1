param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Destination
)

# The game-specific convenience handlers use enum proxies that fail to compile
# with the current Wilds type dump. Preserve the generic REE MCP explorer.
$content = [IO.File]::ReadAllText($Source)
$handlers = @('GetEquipment', 'GetMapInfo', 'GetInventory', 'GetPlayerInfo', 'GetMonsters', 'GetPalicoStats', 'GetWeather')
foreach ($name in $handlers) {
    $signature = "    static object $name() {"
    $start = $content.IndexOf($signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Missing handler: $name" }
    $opening = $start + $signature.Length - 1
    $depth = 0
    $closing = -1
    for ($i = $opening; $i -lt $content.Length; $i++) {
        if ($content[$i] -eq '{') { $depth++ }
        elseif ($content[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $closing = $i; break }
        }
    }
    if ($closing -lt 0) { throw "Unclosed handler: $name" }
    $replacement = $signature + "`n        return new { error = `"Unavailable with this game type dump; use the generic explorer endpoints.`" };`n    }"
    $content = $content.Substring(0, $start) + $replacement + $content.Substring($closing + 1)
}
$signature = '    static Dictionary<int, string> GetMonsterNameMap() {'
$start = $content.IndexOf($signature, [StringComparison]::Ordinal)
if ($start -lt 0) { throw 'Missing handler: GetMonsterNameMap' }
$opening = $start + $signature.Length - 1
$depth = 0
$closing = -1
for ($i = $opening; $i -lt $content.Length; $i++) {
    if ($content[$i] -eq '{') { $depth++ }
    elseif ($content[$i] -eq '}') {
        $depth--
        if ($depth -eq 0) { $closing = $i; break }
    }
}
if ($closing -lt 0) { throw 'Unclosed handler: GetMonsterNameMap' }
$replacement = $signature + "`n        return new Dictionary<int, string>();`n    }"
$content = $content.Substring(0, $start) + $replacement + $content.Substring($closing + 1)
$parent = Split-Path -Parent $Destination
New-Item -ItemType Directory -Force -Path $parent | Out-Null
[IO.File]::WriteAllText($Destination, $content, [Text.UTF8Encoding]::new($false))
Write-Output "Wrote $Destination with $($handlers.Count) compatibility stubs"
