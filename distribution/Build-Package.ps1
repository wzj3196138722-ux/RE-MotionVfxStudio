param([string]$OutputRoot, [switch]$ReusePublished, [string]$REFrameworkRoot = $env:REFRAMEWORK_ROOT)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspaceRoot=Split-Path $projectRoot -Parent
if(-not $OutputRoot){$OutputRoot=Join-Path $projectRoot 'releases'}
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
$uiSource=[IO.File]::ReadAllText((Join-Path $projectRoot 'game-plugin/MotionVfxLive.lua'))
$uiVersion=[regex]::Match($uiSource,'local M = \{ version = "([^"]+)"').Groups[1].Value
if(-not $uiVersion){throw 'Cannot identify the UI version.'}
if(Test-Path -LiteralPath (Join-Path $projectRoot 'game-plugin/MotionVfxLive/fx_json_reader.lua')){
    throw 'The unpublished fx_json_reader.lua draft must not be included in a release source tree.'
}
$name='MotionVfxLive-'+$uiVersion+'-win-x64'
$stage=Join-Path $OutputRoot $name
$cache=Join-Path $PSScriptRoot 'build-cache'
if(Test-Path -LiteralPath $stage){throw 'Release folder already exists. Choose a new OutputRoot; existing releases are never deleted.'}
New-Item -ItemType Directory -Path $stage -Force | Out-Null
function Copy-Prepared([string]$Source,[string]$Relative){
    if(-not(Test-Path -LiteralPath $Source -PathType Leaf)){throw ('Missing release input: '+$Source)}
    $destination=Join-Path $stage $Relative
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $destination
}
if(-not $ReusePublished){
    foreach($component in @(@{project='VfxStudio.LiveEditor';directory='backend'},@{project='VfxStudio.Mcp';directory='mcp'})){
        & dotnet publish (Join-Path $projectRoot ($component.project+'/'+$component.project+'.csproj')) -c Release -r win-x64 --self-contained true -p:MotionVfxPortable=true -p:RuntimeFrameworkVersion=10.0.8 -p:NuGetAudit=false --ignore-failed-sources -o (Join-Path $cache $component.directory)
        if($LASTEXITCODE -ne 0){throw ('Publish failed: '+$component.project)}
    }
    & dotnet publish (Join-Path $projectRoot 'tools/DesktopBridge/MotionVfxDesktop.csproj') -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.8 -p:NuGetAudit=false --ignore-failed-sources -o (Join-Path $cache 'backend/desktop')
    if($LASTEXITCODE -ne 0){throw 'Desktop bridge publish failed.'}
}
foreach($component in @('backend','mcp')){
    $componentRoot=[IO.Path]::GetFullPath((Join-Path $cache $component))
    foreach($file in Get-ChildItem -LiteralPath $componentRoot -Recurse -File){
        if($file.Extension -ne '.pdb'){Copy-Prepared $file.FullName ($component+'/'+$file.FullName.Substring($componentRoot.Length+1))}
    }
    if(-not(Test-Path -LiteralPath (Join-Path $stage ($component+'/coreclr.dll')))){throw 'Self-contained runtime missing.'}
}
if(-not(Test-Path -LiteralPath (Join-Path $stage 'backend/desktop/MotionVfxDesktop.exe'))){throw 'Desktop bridge is missing; rebuild the published cache.'}
Copy-Prepared (Join-Path $projectRoot 'Assets/Lists/rsz_pfb_classes.json') 'backend/formats/rsz_pfb_classes.json'
$pluginSources=@(
    @{source=(Join-Path $projectRoot 'game-plugin/MotionVfxLive.lua');target='MotionVfxLive.lua'},
    @{source=(Join-Path $projectRoot 'game-plugin/手动重载MotionVfxLive.lua');target='Reload.lua'},
    @{source=(Join-Path $projectRoot 'game-plugin/刷新MotionVfxLive输入框.lua');target='RefreshTextInputs.lua'},
    @{source=(Join-Path $projectRoot 'game-plugin/修复MotionVfxLive资源还原.lua');target='RepairBindings.lua'}
)
# The production module directory is the single source of truth. Do not maintain
# a second hand-written list which can silently omit newly added UI/runtime code.
$moduleRoot=Join-Path $projectRoot 'game-plugin/MotionVfxLive'
$modules=@(Get-ChildItem -LiteralPath $moduleRoot -Filter '*.lua' -File | Sort-Object Name)
if(-not $modules.Count){throw 'The production Lua module directory is empty.'}
foreach($module in $modules){
    if($module.Name -notmatch '^fx_[a-z0-9_]+\.lua$'){throw ('Unexpected production module: '+$module.Name)}
    $pluginSources+=@{source=$module.FullName;target=('MotionVfxLive/'+$module.Name)}
}
$moduleNames=@($pluginSources | ForEach-Object {[IO.Path]::GetFileName($_.target)})
foreach($item in $pluginSources){
    $lua=[IO.File]::ReadAllText($item.source)
    foreach($dependency in [regex]::Matches($lua,'MotionVfxLive[./](fx_[a-z0-9_]+)')){
        if(($dependency.Groups[1].Value+'.lua') -notin $moduleNames){throw ('Unpackaged Lua dependency in '+$item.target+': '+$dependency.Value)}
    }
}
foreach($item in $pluginSources){Copy-Prepared $item.source ('plugin/'+$item.target)}
# The normal installer invokes this compatibility tool when MDF-XL is present.
# Ship only our guard/tool, never the third-party MDF-XL script or its user data.
foreach($compatibilityName in @('Apply-MdfxlCompatibility.ps1','MDF-XL-frame-guard.lua')){
    $source=Join-Path $projectRoot ('tools/MdfxlCompatibility/'+$compatibilityName)
    Copy-Prepared $source ('compatibility/MDFXL/'+$compatibilityName)
    Copy-Prepared $source ('Source/tools/MdfxlCompatibility/'+$compatibilityName)
}
$compatibilityReadme=@'
# MDF-XL compatibility / MDF-XL 兼容

MotionVfx Live's normal installer uses this component for a supported existing
MDF-XL installation. No separate compatibility package needs to be installed.
It preserves the original MDF-XL.lua bytes and verifies their SHA256 before
patching or restoring. MDF settings, presets and REFramework are not replaced.
The Lua guard belongs inside the supported MDF-XL chunk; do not copy it to autorun.

正式安装器会处理已安装且受支持的 MDF-XL，无需另装修复包。
原始 MDF-XL.lua 按字节备份并通过 SHA256 核对；保留其配置、预设及 REFramework。
此 Lua 文件是局部保护代码，不是独立的 autorun 脚本，请勿手动放入 autorun。

This package contains only MotionVfx Live's compatibility code, not MDF-XL source.
Thanks to SilverEzredes for MDF-XL and praydog and contributors for REFramework.
本包不分发 MDF-XL 原始代码。感谢 MDF-XL 作者 SilverEzredes 及 REFramework 的作者和贡献者。
'@
[IO.File]::WriteAllText((Join-Path $stage 'compatibility/MDFXL/README.md'),$compatibilityReadme,[Text.UTF8Encoding]::new($false))
Copy-Prepared (Join-Path $projectRoot 'third-party/reframework-d2d/package/reframework/plugins/reframework-d2d.dll') 'native/reframework-d2d.dll'
Copy-Prepared (Join-Path $projectRoot 'third-party/reframework-d2d/package/reframework/autorun/reframework-d2d.lua') 'plugin/reframework-d2d.lua'
Copy-Prepared (Join-Path $projectRoot 'third-party/reframework-d2d/LICENSE') 'licenses/REFramework-D2D-LICENSE.txt'
Copy-Prepared (Join-Path $projectRoot 'game-plugin/images/MotionVfxLive/logo.png') 'images/MotionVfxLive/logo.png'
Copy-Prepared (Join-Path $projectRoot 'game-plugin/images/MotionVfxLive/logo.png') 'game-plugin/images/MotionVfxLive/logo.png'
Copy-Prepared (Join-Path $projectRoot 'game-plugin/images/MotionVfxLive/author-logo.png') 'images/MotionVfxLive/author-logo.png'
Copy-Prepared (Join-Path $projectRoot 'game-plugin/images/MotionVfxLive/logo.png') 'Source/game-plugin/images/MotionVfxLive/logo.png'
foreach($file in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tools/DesktopBridge') -File){
    if($file.Extension -in @('.cs','.csproj')){Copy-Prepared $file.FullName ('Source/tools/DesktopBridge/'+$file.Name)}
}
foreach($file in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tools/Installer') -File){
    if($file.Extension -in @('.cs','.csproj','.ps1')){Copy-Prepared $file.FullName ('Source/tools/Installer/'+$file.Name)}
}
& (Join-Path $projectRoot 'tools/DebugInput/Build.ps1') -REFrameworkRoot $REFrameworkRoot
if($LASTEXITCODE -ne 0){throw 'Debug input plugin build failed.'}
Copy-Prepared (Join-Path $projectRoot 'tools/DebugInput/build/MotionVfxDebugInput.dll') 'native/MotionVfxDebugInput.dll'
Copy-Prepared (Join-Path $projectRoot 'tools/DebugInput/THIRD-PARTY.txt') 'licenses/MotionVfxDebugInput-THIRD-PARTY.txt'
foreach($file in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tools/DebugInput') -File){
    Copy-Prepared $file.FullName ('Source/tools/DebugInput/'+$file.Name)
}
$utf8Bom=[Text.UTF8Encoding]::new($true)
foreach($script in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'scripts') -Filter '*.ps1'){
    $destination=Join-Path $stage ('scripts/'+$script.Name)
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    [IO.File]::WriteAllText($destination,[IO.File]::ReadAllText($script.FullName),$utf8Bom)
}
$launchers=@{'安装或更新.cmd'='Install.ps1';'启动编辑器.cmd'='Start-Backend.ps1';'停止后台.cmd'='Stop-Backend.ps1';'生成MCP配置.cmd'='Mcp-Config.ps1'}
foreach($entry in $launchers.GetEnumerator()){
    $body='@echo off'+"`r`n"+'powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\'+$entry.Value+'" %*'+"`r`n"+'if errorlevel 1 pause'+"`r`n"
    [IO.File]::WriteAllText((Join-Path $stage $entry.Key),$body,[Text.Encoding]::ASCII)
}
foreach($doc in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.md'){
    if($doc.Name -like '*更新说明.md' -and $doc.Name -ne ($uiVersion+'更新说明.md')){continue}
    Copy-Prepared $doc.FullName $doc.Name
    if($doc.Name -in @('第三方说明.md','AI修改特效教程.md')){
        $packagedDocument=Join-Path $stage $doc.Name
        $body=[IO.File]::ReadAllText($packagedDocument)
        $body=$body.Replace('(../README.md)','(README.md)').Replace('(../README.en.md)','(README.en.md)').Replace('(../CREDITS.md)','(CREDITS.md)').Replace('(../docs/','(docs/')
        [IO.File]::WriteAllText($packagedDocument,$body,[Text.UTF8Encoding]::new($false))
    }
}
# Keep the repository license and bilingual product documentation with the
# binaries as well as on GitHub. A local source checkout may omit optional docs.
foreach($relative in @('LICENSE','CREDITS.md','README.md','README.en.md','docs/tutorials.zh-CN.md','docs/tutorials.en.md','docs/development/BUILD.zh-CN.md','docs/development/BUILD.en.md')){
    $document=Join-Path $projectRoot $relative
    if(Test-Path -LiteralPath $document -PathType Leaf){
        Copy-Prepared $document $relative
        if($relative -eq 'CREDITS.md'){
            $packagedCredits=Join-Path $stage $relative
            $body=[IO.File]::ReadAllText($packagedCredits).Replace('(distribution/licenses/)','(licenses/)').Replace('(distribution/第三方说明.md)','(第三方说明.md)')
            [IO.File]::WriteAllText($packagedCredits,$body,[Text.UTF8Encoding]::new($false))
        }
    }
}
foreach($notice in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'licenses') -File){Copy-Prepared $notice.FullName ('licenses/'+$notice.Name)}
$nugetRoot=if($env:NUGET_PACKAGES){$env:NUGET_PACKAGES}else{Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'}
foreach($notice in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')){
    Copy-Prepared (Join-Path $nugetRoot ('microsoft.netcore.app.runtime.win-x64/10.0.8/'+$notice)) ('licenses/dotnet-'+$notice)
}
Copy-Prepared (Join-Path $nugetRoot 'microsoft.windowsdesktop.app.runtime.win-x64/10.0.8/LICENSE') 'licenses/dotnet-desktop-LICENSE.txt'
# Keep corresponding first-party/modified library source, never game content or machine state.
foreach($directory in @('VfxStudio.Core','REE-Lib.Generators','VfxStudio.LiveEditor','VfxStudio.Mcp')){
    $sourceRoot=Join-Path $projectRoot $directory
    foreach($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File){
        $relative=$file.FullName.Substring($sourceRoot.Length+1)
        if($relative -match '(^|[\\/])(bin|obj)([\\/]|$)' -or $file.Extension -notin @('.cs','.csproj')){continue}
        Copy-Prepared $file.FullName ('Source/'+$directory+'/'+$relative)
    }
}
$sourceReadme=@'
# Source

This directory contains the corresponding C# and native input source. Lua source is in ../plugin.
The scoped MDF-XL compatibility tool and Lua guard are in tools/MdfxlCompatibility.
They are integrated into the normal installer; no third-party MDF-XL source is included.
Build with the .NET SDK: dotnet build VfxStudio.LiveEditor -c Release
For self-contained binaries, use the .NET 10 SDK and publish with
--self-contained true -r win-x64 -p:MotionVfxPortable=true.
NuGet dependencies are restored during a source build. The distributed executables
need no SDK, Python or separately installed .NET runtime. Proprietary game test
fixtures, assets, PAKs, exports and local machine configuration are not included.
Keep all notices in ../licenses when redistributing this package.
The native input component is in tools/DebugInput. Building it requires MSVC,
the Windows SDK, REFramework API 1.15 headers and Lua 5.4.3 source. This package
contains corresponding product source, not the complete development checkout.
The vendored SDK, complete build scripts, test sources, and bilingual build guide
are available at https://github.com/wzj3196138722-ux/RE-MotionVfxStudio.
Clone that repository to build the entire installer. Its native build defaults
to third-party/reframework-sdk. Alternatively pass -REFrameworkRoot or set
REFRAMEWORK_ROOT to an equivalent API/Lua source checkout.
'@
[IO.File]::WriteAllText((Join-Path $stage 'Source/README.md'),$sourceReadme,[Text.UTF8Encoding]::new($false))
$mcpVersion=& (Join-Path $stage 'mcp/VfxStudio.Mcp.exe') --version
if($LASTEXITCODE -ne 0 -or $mcpVersion -ne '0.1.3'){throw 'The current UI requires MCP 0.1.3. Rebuild the published cache.'}
$backendSource=[IO.File]::ReadAllText((Join-Path $projectRoot 'VfxStudio.LiveEditor/Program.cs'))
$backendVersion=[regex]::Match($backendSource,'const string Version = "([^"]+)"').Groups[1].Value
if(-not $backendVersion){throw 'Cannot identify backend version.'}
$release=[ordered]@{name='MotionVfx Live';version=$uiVersion;backendVersion=$backendVersion;mcpVersion=$mcpVersion;debugInputVersion='1.1.0';d2dVersion='1.4.1';mdfxlCompatibilityVersion='1.0.0';aiProtocol=1;runtime='self-contained .NET 10.0.8 win-x64';builtAtUtc=[DateTimeOffset]::UtcNow.ToString('o');prerequisites=@('Monster Hunter Wilds','REFramework with Lua support');containsGameAssets=$false}
[IO.File]::WriteAllText((Join-Path $stage 'release.json'),($release|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
$schema=& (Join-Path $stage 'mcp/VfxStudio.Mcp.exe') --tools-schema
if($LASTEXITCODE -ne 0){throw 'Packaged MCP did not start.'}
[IO.File]::WriteAllText((Join-Path $stage 'mcp-tools.json'),($schema -join "`n"),[Text.UTF8Encoding]::new($false))
$manifest=@(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{path=$_.FullName.Substring($stage.Length+1).Replace('\','/');size=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
$privateFiles=@($manifest | Where-Object {
    $_.path -match '(?i)(^|/)(Assets|fixtures|test-output|Exports|LocalFiles)/|\.(efx|pfb|mesh|mdf2?|tex|uvs|mot|motlist|chain2?|clsp|jcns)(\.\d+)?$|\.pak$|(^|/)(runtime-config|mcp-config|group-notes|last-replay)\.(json|toml)$|saved-session'
})
if($privateFiles.Count){throw ('Refusing to package machine state or game assets: '+(($privateFiles | ForEach-Object {$_.path}) -join ', '))}
[IO.File]::WriteAllText((Join-Path $stage 'SHA256.json'),($manifest|ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=Join-Path $OutputRoot ($name+'.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$archive,[IO.Compression.CompressionLevel]::Optimal,$true)
$hash=Get-FileHash -LiteralPath $archive -Algorithm SHA256
[IO.File]::WriteAllText(($archive+'.sha256'),($hash.Hash+'  '+[IO.Path]::GetFileName($archive)+"`r`n"),[Text.Encoding]::ASCII)
Write-Output ('Release: '+$archive)
Write-Output ('SHA256: '+$hash.Hash)
