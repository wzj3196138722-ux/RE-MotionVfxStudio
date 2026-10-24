param([Parameter(Mandatory=$true)][string]$Archive)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$run=Join-Path $projectRoot ('tools/LiveEditor/test-output/package-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $Archive).Path,$run)
$package=Join-Path $run ([IO.Path]::GetFileNameWithoutExtension($Archive))
$manifest=Get-Content -LiteralPath (Join-Path $package 'SHA256.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$manifestPaths=@{}
foreach($file in $manifest){
    $full=[IO.Path]::GetFullPath((Join-Path $package $file.path))
    if(-not $full.StartsWith($package.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw ('Manifest path escaped package: '+$file.path)}
    if($manifestPaths.ContainsKey($file.path)){throw ('Duplicate manifest entry: '+$file.path)}
    $manifestPaths[$file.path]=$true
    if((Get-Item -LiteralPath $full).Length -ne $file.size -or (Get-FileHash -LiteralPath $full).Hash -ne $file.sha256){throw ('Content mismatch: '+$file.path)}
}
foreach($file in Get-ChildItem -LiteralPath $package -Recurse -File){
    $relative=$file.FullName.Substring($package.Length+1).Replace('\','/')
    if($relative -ne 'SHA256.json' -and -not $manifestPaths.ContainsKey($relative)){throw ('Unmanifested release file: '+$relative)}
}
$required=@('plugin/MotionVfxLive.lua','plugin/Reload.lua','plugin/reframework-d2d.lua',
    'plugin/MotionVfxLive/fx_preview_runtime.lua','native/reframework-d2d.dll','native/MotionVfxDebugInput.dll',
    'images/MotionVfxLive/logo.png','images/MotionVfxLive/author-logo.png','backend/formats/rsz_pfb_classes.json',
    'backend/VfxStudio.LiveEditor.exe','backend/coreclr.dll','backend/hostfxr.dll',
    'backend/desktop/MotionVfxDesktop.exe','backend/desktop/coreclr.dll','backend/desktop/System.Windows.Forms.dll',
    'mcp/VfxStudio.Mcp.exe','mcp/coreclr.dll','mcp/hostfxr.dll','mcp-tools.json',
    'licenses/REFramework-D2D-LICENSE.txt','licenses/MotionVfxDebugInput-THIRD-PARTY.txt',
    'licenses/dotnet-LICENSE.TXT','licenses/dotnet-THIRD-PARTY-NOTICES.TXT','licenses/dotnet-desktop-LICENSE.txt')
$productionModules=@(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'game-plugin/MotionVfxLive') -Filter '*.lua' -File)
foreach($module in $productionModules){
    $relative='plugin/MotionVfxLive/'+$module.Name
    $required+=$relative
    if(-not(Test-Path -LiteralPath (Join-Path $package $relative)) -or
        (Get-FileHash -LiteralPath (Join-Path $package $relative)).Hash -ne (Get-FileHash -LiteralPath $module.FullName).Hash){throw ('Missing or stale production module: '+$relative)}
}
foreach($relative in $required){if(-not $manifestPaths.ContainsKey($relative)){throw ('Required component missing: '+$relative)}}
$packagedLua=@(Get-ChildItem -LiteralPath (Join-Path $package 'plugin') -Recurse -Filter '*.lua' -File)
foreach($module in $packagedLua){
    foreach($dependency in [regex]::Matches([IO.File]::ReadAllText($module.FullName),'MotionVfxLive[./](fx_[a-z0-9_]+)')){
        if(-not $manifestPaths.ContainsKey('plugin/MotionVfxLive/'+$dependency.Groups[1].Value+'.lua')){throw ('Lua dependency missing: '+$dependency.Value)}
    }
}
foreach($script in Get-ChildItem -LiteralPath (Join-Path $package 'scripts') -Filter '*.ps1'){
    $tokens=$null;$parseErrors=$null
    [System.Management.Automation.Language.Parser]::ParseFile($script.FullName,[ref]$tokens,[ref]$parseErrors) | Out-Null
    if($parseErrors.Count){throw ($script.Name+': '+($parseErrors.Message -join '; '))}
    $bytes=[IO.File]::ReadAllBytes($script.FullName)
    if($bytes[0] -ne 239 -or $bytes[1] -ne 187 -or $bytes[2] -ne 191){throw 'PowerShell 5 distribution scripts need a UTF-8 BOM.'}
}
$game=Join-Path $run '模拟游戏 Game'
New-Item -ItemType Directory -Path $game -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $game 'MonsterHunterWilds.exe'),'fixture only')
[IO.File]::WriteAllText((Join-Path $game 'dinput8.dll'),'fixture only')
# Upgrade over older script bytes while preserving user-owned data and other mods.
$userData=Join-Path $game 'reframework/data/MotionVfxLive/user-content.txt'
$otherMod=Join-Path $game 'reframework/autorun/UnrelatedMod.lua'
New-Item -ItemType Directory -Path (Split-Path $userData -Parent),(Split-Path $otherMod -Parent) -Force | Out-Null
[IO.File]::WriteAllText($userData,'keep user data')
[IO.File]::WriteAllText($otherMod,'-- keep other mods')
foreach($module in $packagedLua | Where-Object {$_.Directory.Name -eq 'MotionVfxLive'}){
    $oldTarget=Join-Path $game ('reframework/autorun/MotionVfxLive/'+$module.Name)
    New-Item -ItemType Directory -Path (Split-Path $oldTarget -Parent) -Force | Out-Null
    [IO.File]::WriteAllText($oldTarget,'-- old installed module')
}
$oldDotnet=$env:DOTNET_ROOT
$env:DOTNET_ROOT=Join-Path $run 'nonexistent-dotnet'
try{
    & (Join-Path $package 'scripts/Install.ps1') -GameRoot $game -NonInteractive
    foreach($module in $packagedLua | Where-Object {$_.Directory.Name -eq 'MotionVfxLive'}){
        $installed=Join-Path $game ('reframework/autorun/MotionVfxLive/'+$module.Name)
        if((Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath $module.FullName).Hash){throw ('Installed module differs: '+$module.Name)}
    }
    if([IO.File]::ReadAllText($userData) -ne 'keep user data' -or [IO.File]::ReadAllText($otherMod) -ne '-- keep other mods'){throw 'Upgrade changed user data or another mod.'}
    . (Join-Path $package 'scripts/Common.ps1')
    $configuration=Read-Config
    $status=Live-Backend $configuration
    $release=Read-JsonFile (Join-Path $package 'release.json')
    if(-not $status -or $status.version -ne $release.backendVersion){throw 'Packaged backend did not become ready with the declared version.'}
    foreach($field in @('aiBusy','pendingId','ownedPreview','captureRecording','bindingsRestorePending','sceneRestorePending','hasDrafts')){
        $ui=@{observedAt=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds();loaded=$true;bindings=@{appliedCount=0}}
        $ui[$field]=if($field -eq 'pendingId'){'pending-test'}else{$true}
        Write-JsonFile (Join-Path (Mailbox-Path $configuration) 'ui-status.json') $ui
        $blocked=$false
        try{Assert-EditorIdle $configuration | Out-Null}catch{$blocked=$true}
        if(-not $blocked){throw ('Idle guard did not block '+$field)}
    }
    foreach($preview in @('actionPreview','pfbPreview')){
        foreach($field in @('busy','requiresRestore')){
            $ui=@{observedAt=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds();loaded=$true;bindings=@{appliedCount=0}}
            $ui[$preview]=@{};$ui[$preview][$field]=$true
            Write-JsonFile (Join-Path (Mailbox-Path $configuration) 'ui-status.json') $ui
            $blocked=$false
            try{Assert-EditorIdle $configuration | Out-Null}catch{$blocked=$true}
            if(-not $blocked){throw ('Idle guard did not block '+$preview+'.'+$field)}
        }
    }
    Write-JsonFile (Join-Path (Mailbox-Path $configuration) 'ui-status.json') @{observedAt=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds();loaded=$true;bindings=@{appliedCount=0}}
    $fixture=Join-Path $projectRoot 'tools/LiveEditor/fixtures/game-install-it02-sub021.efx.5571972'
    $opened=Send-Backend $configuration @{action='open';path=$fixture}
    $counts=@($opened.document.groups | ForEach-Object {$_.entries.Count})
    if(($counts -join ',') -ne '27,27,8,11,11'){throw 'Duplicate group membership changed.'}
    $beforeNote=Send-Backend $configuration @{action='save';revision=$opened.revision}
    $noted=Send-Backend $configuration @{action='set_group_note';groupIndex=0;note='中文备注：火焰组';revision=$opened.revision}
    if($noted.document.groups[0].name -ne $opened.document.groups[0].name -or $noted.document.groups[0].note -ne '中文备注：火焰组'){throw 'Group note changed native name or lost Chinese text.'}
    if($noted.revision -ne $opened.revision -or $noted.document.dirty -or $noted.document.canUndo -or $noted.document.canRedo){throw 'Group note changed EFX editing state.'}
    $afterNote=Send-Backend $configuration @{action='save';revision=$noted.revision}
    if((Get-FileHash -LiteralPath $beforeNote.outputPath).Hash -ne (Get-FileHash -LiteralPath $afterNote.outputPath).Hash){throw 'Group note changed serialized EFX bytes.'}
    $edited=Send-Backend $configuration @{action='set';target=@{kind='entry';index=0};path='name';value='测试块 Package';revision=$noted.revision}
    & (Join-Path $package 'scripts/Stop-Backend.ps1') -NonInteractive
    & (Join-Path $package 'scripts/Start-Backend.ps1') -NonInteractive
    $restored=Send-Backend $configuration @{action='status'}
    if($restored.document.entries[0].name -ne '测试块 Package'){throw 'Stop/start lost the edited checkpoint.'}
    $countsAfter=@($restored.document.groups | ForEach-Object {$_.entries.Count})
    if(($countsAfter -join ',') -ne '27,27,8,11,11'){throw 'Checkpoint changed native membership.'}
    $reopened=Send-Backend $configuration @{action='open';path=$fixture}
    if($reopened.document.groups[0].note -ne '中文备注：火焰组' -or $reopened.document.groups[0].name -ne $opened.document.groups[0].name){throw 'Chinese group note did not persist independently across restart and reopen.'}
    & (Join-Path $package 'scripts/Stop-Backend.ps1') -NonInteractive
    $version=& (Join-Path $package 'mcp/VfxStudio.Mcp.exe') --version
    if($LASTEXITCODE -ne 0 -or $version -ne '0.1.2'){throw 'Packaged MCP did not report its version.'}
    $schema=& (Join-Path $package 'mcp/VfxStudio.Mcp.exe') --tools-schema
    if($LASTEXITCODE -ne 0){throw 'MCP tool schema failed.'}
    $parsed=($schema -join "`n") | ConvertFrom-Json
    $toolCount=if($parsed.tools){@($parsed.tools).Count}else{@($parsed).Count}
    if($toolCount -ne 17){throw ('Unexpected tool count '+$toolCount)}
    $mcpConfig=Read-JsonFile (Join-Path $package 'mcp-config.json')
    if($mcpConfig.mcpServers.motionvfx.args[1] -ne (Join-Path $package 'runtime-config.json')){throw 'MCP config did not use relocated package path.'}
    $bad=@($manifest | Where-Object {$_.path -match '(?i)(^|/)(Assets|fixtures|test-output|Exports|LocalFiles)/|\.(efx|pfb|mesh|mdf2?|tex|uvs|mot|motlist|chain2?|clsp|jcns)(\.\d+)?$|\.pak$|(^|/)(runtime-config|mcp-config|group-notes|last-replay)\.(json|toml)$|saved-session'})
    if($bad.Count){throw 'Distribution contains machine state or game content.'}
    $report=[ordered]@{passed=$true;at=[DateTimeOffset]::UtcNow.ToString('o');archive=(Resolve-Path -LiteralPath $Archive).Path;archiveSha256=(Get-FileHash -LiteralPath $Archive).Hash;verifiedFiles=$manifest.Count;powerShell=$PSVersionTable.PSVersion.ToString();selfContainedBackend=$true;selfContainedMcp=$true;productionLuaModules=$productionModules.Count;luaDependencyClosure=$true;upgradePreservesUserData=$true;duplicateGroupCounts=$countsAfter;editedCheckpointRestored=$true;chineseNotePersisted=$true;noteLeavesEfxBytesIdentical=$true;tools=$toolCount;idleGuardCases=11;liveGameTouched=$false}
    Write-JsonFile (Join-Path $run 'result.json') $report
    $report | ConvertTo-Json -Depth 5
}finally{
    $env:DOTNET_ROOT=$oldDotnet
    # Never leave this isolated test backend running. It owns no live game state.
    if($configuration -and (Live-Backend $configuration)){
        Send-Backend $configuration @{action='shutdown'} | Out-Null
    }
}
