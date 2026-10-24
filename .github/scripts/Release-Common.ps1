$ErrorActionPreference = 'Stop'

function Invoke-ReleaseApi {
    param([Parameter(Mandatory)][string]$Path, [string]$Method = 'GET', [object]$Body, [switch]$AllowMissing)
    if (-not $env:GH_TOKEN) { throw 'GH_TOKEN is required for the release API.' }
    if ($env:GITHUB_REPOSITORY -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid GITHUB_REPOSITORY.' }
    $parameters = @{
        Uri = "https://api.github.com/repos/$($env:GITHUB_REPOSITORY)/$Path"
        Method = $Method
        Headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
        ErrorAction = 'Stop'
    }
    if ($null -ne $Body) { $parameters.Body = $Body | ConvertTo-Json -Depth 10; $parameters.ContentType = 'application/json' }
    try { Invoke-RestMethod @parameters }
    catch {
        if ($AllowMissing -and $_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { return $null }
        throw
    }
}

function Assert-ReleaseVersion {
    param([Parameter(Mandatory)][string]$Version)
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Release version must be major.minor.patch.' }
    $lua = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../game-plugin/MotionVfxLive.lua'))
    $ui = [regex]::Match($lua, 'local M = \{ version = "([^"]+)"').Groups[1].Value
    $csproj = [xml][IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../tools/Installer/MotionVfxSetup.csproj'))
    $installer = [string]$csproj.Project.PropertyGroup.Version
    if ($Version -ne $ui -or $Version -ne $installer) { throw "Version mismatch: requested=$Version, UI=$ui, installer=$installer." }
}

function Test-ReleaseExists {
    param([Parameter(Mandatory)][string]$Tag)
    $encoded = [Uri]::EscapeDataString($Tag)
    $release = Invoke-ReleaseApi -Path "releases/tags/$encoded" -AllowMissing
    $tagRef = Invoke-ReleaseApi -Path "git/ref/tags/$encoded" -AllowMissing
    return ($null -ne $release -or $null -ne $tagRef)
}
