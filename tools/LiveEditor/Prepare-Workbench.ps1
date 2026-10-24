param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'prepared-0.7.0'))
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
# This prepares binaries only. It never reads the live mailbox or starts the game.
& dotnet publish (Join-Path $project 'VfxStudio.LiveEditor/VfxStudio.LiveEditor.csproj') -c Release -r win-x64 --self-contained true -p:MotionVfxPortable=true -p:RuntimeFrameworkVersion=10.0.8 -p:NuGetAudit=false --ignore-failed-sources -o (Join-Path $output 'backend') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Backend preparation failed.' }
& dotnet publish (Join-Path $project 'tools/DesktopBridge/MotionVfxDesktop.csproj') -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.8 -p:NuGetAudit=false --ignore-failed-sources -o (Join-Path $output 'backend/desktop') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Desktop bridge preparation failed.' }
New-Item -ItemType Directory -Path (Join-Path $output 'backend/formats') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $project 'Assets/Lists/rsz_pfb_classes.json') -Destination (Join-Path $output 'backend/formats/rsz_pfb_classes.json') -Force
$files=@(Get-ChildItem -LiteralPath $output -Recurse -File | Where-Object { $_.Name -ne 'SHA256.json' } | ForEach-Object {
    @{path=$_.FullName.Substring($output.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
$files | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'SHA256.json') -Encoding utf8
Write-Output ('Prepared, not installed: '+$output)
