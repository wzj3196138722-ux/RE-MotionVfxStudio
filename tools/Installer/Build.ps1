param([Parameter(Mandatory=$true)][string]$PayloadArchive, [string]$OutputDirectory)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$payload=(Resolve-Path -LiteralPath $PayloadArchive).Path
$version=([xml][IO.File]::ReadAllText((Join-Path $PSScriptRoot 'MotionVfxSetup.csproj'))).Project.PropertyGroup.Version
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'InstallEngine.cs'))
if($source -notmatch ('const string Version = "'+[regex]::Escape($version)+'"')){throw 'Installer source and assembly versions differ.'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $project 'releases'}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$prepared=Join-Path $PSScriptRoot 'published'
& dotnet publish (Join-Path $PSScriptRoot 'MotionVfxSetup.csproj') -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.8 -p:NuGetAudit=false -p:PayloadPath=$payload --ignore-failed-sources -o $prepared --nologo
if($LASTEXITCODE -ne 0){throw 'Installer publish failed.'}
$destination=Join-Path $OutputDirectory ('MotionVfxLive-'+$version+'-Setup.exe')
Copy-Item -LiteralPath (Join-Path $prepared 'MotionVfxLive-Setup.exe') -Destination $destination -Force
$hash=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
[IO.File]::WriteAllText(($destination+'.sha256'),($hash+'  '+[IO.Path]::GetFileName($destination)+"`r`n"),[Text.Encoding]::ASCII)
Write-Output ('One-file installer: '+[IO.Path]::GetFullPath($destination))
Write-Output ('SHA256: '+$hash)
