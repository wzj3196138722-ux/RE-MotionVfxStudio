param([string]$BackendDirectory=(Join-Path $PSScriptRoot 'test-output/file-browser-backend'))
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root=Join-Path $PSScriptRoot ('test-output/file-browser-'+[guid]::NewGuid().ToString('N'))
$mailbox=Join-Path $root 'mailbox'
$fixture=Join-Path $root '中文 素材'
New-Item -ItemType Directory -Path $mailbox,$fixture,(Join-Path $fixture 'A 子文件夹'),(Join-Path $fixture 'z 文件夹') -Force | Out-Null
foreach($name in @('A.efx.5571972','B.EFX','彩虹.efx.5571972','贴图.tex.760230703','动作.motlist.1','模型.mesh.1','prefab.pfb.18','bad.efx.fake','readme.txt','unsupported.motbank.1','unsupported.motfsm.1','physics.chain.1','collision.clsp.1','joint.jcns.1')) {
    [IO.File]::WriteAllText((Join-Path $fixture $name),'test '+$name)
}
$arguments=@('--assets',(Join-Path $project 'Assets'),'--mailbox',$mailbox,'--output',(Join-Path $root 'output'))
$line=($arguments | ForEach-Object {'"'+$_.Replace('"','\"')+'"'}) -join ' '
$process=Start-Process -FilePath (Join-Path ([IO.Path]::GetFullPath($BackendDirectory)) 'VfxStudio.LiveEditor.exe') -ArgumentList $line -WorkingDirectory $project -WindowStyle Hidden -PassThru
$checks=[Collections.Generic.List[string]]::new()
function Check([bool]$Ok,[string]$Message){if(-not $Ok){throw $Message};$checks.Add($Message);Write-Output ('PASS '+$Message)}
function Request([string]$Action,[hashtable]$Data=@{},[bool]$Success=$true){
    $Data.id=[guid]::NewGuid().ToString('N');$Data.action=$Action
    $temporary=Join-Path $mailbox 'request.tmp';$Data | ConvertTo-Json -Depth 20 -Compress | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination (Join-Path $mailbox 'request.json') -Force
    for($i=0;$i -lt 200;$i++){
        Start-Sleep -Milliseconds 50
        try{$r=Get-Content -LiteralPath (Join-Path $mailbox 'response.json') -Raw | ConvertFrom-Json}catch{continue}
        if($r.id -ne $Data.id){continue}
        if($r.ok -ne $Success){throw ($Action+': '+$r.error)}
        return $r
    }
    throw ('No result for '+$Action)
}
try {
    for($i=0;$i -lt 200;$i++){
        if($process.HasExited){throw 'Test backend exited before readiness'}
        try{$ready=Get-Content -LiteralPath (Join-Path $mailbox 'status.json') -Raw | ConvertFrom-Json}catch{$ready=$null}
        if($ready.ready -and $ready.pid -eq $process.Id){break}
        Start-Sleep -Milliseconds 100
    }
    if(-not $ready.ready){throw 'Test backend did not become ready'}
    $efx=Join-Path $project 'Assets/WeaponBases/wp02/11_it02_000.efx.5571972'
    $opened=Request 'open' @{path=$efx}
    $initialHash=(Get-FileHash -LiteralPath $efx).Hash
    $first=Request 'file_browse' @{path=$fixture;kind='efx';limit=2;revision=-99}
    Check ($first.fileBrowser.total -eq 5 -and $first.fileBrowser.entries.Count -eq 2 -and $first.fileBrowser.entries[0].isDirectory -and $first.fileBrowser.entries[1].isDirectory) 'Directories sort first; EFX filter includes versioned and plain files; browse ignores editing revision'
    $page=Request 'file_browse' @{path=$fixture;kind='efx';offset=2;limit=2}
    Check (($page.fileBrowser.entries.name -join '|') -ceq 'A.efx.5571972|B.EFX') 'Pagination is stable and starts at exact requested offset'
    $last=Request 'file_browse' @{path=$fixture;kind='efx';offset=4;limit=2000}
    Check ($last.fileBrowser.entries[0].name -ceq '彩虹.efx.5571972' -and $last.fileBrowser.limit -eq 200) 'Unicode filenames survive and page size is bounded'
    $selected=Join-Path $fixture '彩虹.efx.5571972'
    $byFile=Request 'file_browse' @{path=$selected;kind='efx'}
    Check ($byFile.fileBrowser.path -eq $fixture -and $byFile.fileBrowser.selectedPath -eq $selected) 'Pasted full file path navigates to its parent and preserves the selection'
    $byFilePage=Request 'file_browse' @{path=$selected;kind='efx';limit=2}
    Check ($byFilePage.fileBrowser.offset -eq 4 -and $byFilePage.fileBrowser.entries[0].path -eq $selected) 'Pasted full path automatically reveals its file beyond the first page'
    $byFileFilter=Request 'file_browse' @{path=$selected;kind='efx';filter='not-this-name'}
    Check ($byFileFilter.fileBrowser.entries[0].path -eq $selected) 'An explicit full file path remains selectable despite a prior search filter'
    $wrongType=Request 'file_browse' @{path=(Join-Path $fixture '动作.motlist.1');kind='efx'} $false
    Check ($wrongType.error -match '文件类型') 'Pasting an unsupported file type reports the mismatch instead of silently selecting nothing'
    $filter=Request 'file_browse' @{path=$fixture;kind='all';filter='彩虹'}
    Check ($filter.fileBrowser.total -eq 1 -and $filter.fileBrowser.entries[0].path -eq $selected) 'Filename search supports Chinese and does not recurse'
    $assets=Request 'file_browse' @{path=$fixture;kind='assets'}
    Check ($assets.fileBrowser.total -eq 8 -and -not ($assets.fileBrowser.entries.name -contains 'B.EFX')) 'Asset filter matches precache formats and requires their numeric version suffix'
    $motions=Request 'file_browse' @{path=$fixture;kind='motion'}
    Check ($motions.fileBrowser.total -eq 3 -and $motions.fileBrowser.entries[2].name -eq '动作.motlist.1') 'Motion filter supports numeric versions'
    $pfb=Request 'file_browse' @{path=$fixture;kind='pfb'}
    Check ($pfb.fileBrowser.total -eq 3 -and $pfb.fileBrowser.entries[2].name -eq 'prefab.pfb.18') 'PFB filter preserves navigable folders'
    $defaults=Request 'file_browse' @{}
    Check ((Test-Path -LiteralPath $defaults.fileBrowser.path) -and $defaults.fileBrowser.roots.Count -gt 0 -and $defaults.fileBrowser.shortcuts.Count -gt 0) 'Default location, roots and shortcuts are usable'
    $missing=Request 'file_browse' @{path=(Join-Path $fixture '不存在')} $false
    Check ($missing.error -match '找不到') 'Missing directories report a visible actionable error'
    $relative=Request 'file_browse' @{path='art/vfx/file.efx'} $false
    Check ($relative.error -match '完整的本地路径') 'Game resource paths are not mistaken for disk addresses'
    $invalid=Request 'file_browse' @{path=$fixture;kind='invalid'} $false
    Check ($invalid.error -match '文件类型') 'Invalid file type is rejected explicitly'
    $stat=Request 'file_stat' @{path=$selected}
    Check ($stat.fileStat.exists -and -not $stat.fileStat.isDirectory -and $stat.fileStat.sha256 -ceq (Get-FileHash -LiteralPath $selected).Hash) 'Existing file stat returns the exact export overwrite checksum'
    $dir=Request 'file_stat' @{path=$fixture}
    Check ($dir.fileStat.isDirectory -and -not $dir.fileStat.sha256) 'Directory stat does not hash or read directory content'
    $newPath=Join-Path $fixture '导出.efx.5571972'
    $newStat=Request 'file_stat' @{path=$newPath}
    Check (-not $newStat.fileStat.exists) 'New export destination can be checked before it exists'
    $plan=Request 'prepare_export' @{scope='efx';revision=$opened.revision}
    Check ($plan.exportRequest.exportToken -and $plan.exportRequest.items.Count -eq 1 -and $plan.exportRequest.items[0].key -eq 'efx' -and -not $plan.desktop) 'Export preparation returns file choices without a desktop request'
    Check (@(Get-ChildItem -LiteralPath $mailbox -Filter 'desktop-*' -File).Count -eq 0) 'No desktop helper jobs or requests are created by in-game navigation and export'
    $export=Request 'finish_export' @{exportToken=$plan.exportRequest.exportToken;destinations=@(@{key='efx';path=$newPath})}
    Check ((Test-Path -LiteralPath $newPath) -and $export.exportedFiles[0] -eq $newPath) 'Prepared export writes to the in-game selected path'
    $overwritePlan=Request 'prepare_export' @{scope='efx';revision=$opened.revision}
    $exportStat=Request 'file_stat' @{path=$newPath}
    $overwrite=Request 'finish_export' @{exportToken=$overwritePlan.exportRequest.exportToken;destinations=@(@{key='efx';path=$newPath;expectedHash=$exportStat.fileStat.sha256})}
    Check ($overwrite.exportedFiles[0] -eq $newPath) 'Explicit overwrite accepts file_stat checksum'
    $changedPlan=Request 'prepare_export' @{scope='efx';revision=$opened.revision}
    $beforeChange=Request 'file_stat' @{path=$newPath}
    [IO.File]::WriteAllText($newPath,'changed after confirmation')
    $changed=Request 'finish_export' @{exportToken=$changedPlan.exportRequest.exportToken;destinations=@(@{key='efx';path=$newPath;expectedHash=$beforeChange.fileStat.sha256})} $false
    Check ($changed.error -match '变化' -and [IO.File]::ReadAllText($newPath) -eq 'changed after confirmation') 'Changed destination is rejected without overwriting the new content'
    $protectPlan=Request 'prepare_export' @{scope='efx';revision=$opened.revision}
    $protect=Request 'finish_export' @{exportToken=$protectPlan.exportRequest.exportToken;destinations=@(@{key='efx';path=$efx;expectedHash=$initialHash})} $false
    Check ($protect.error -match '原文件' -and (Get-FileHash -LiteralPath $efx).Hash -ceq $initialHash) 'In-game export preserves imported originals'
    $status=Request 'status'
    Check ($status.revision -eq $opened.revision -and $status.document.source -eq $opened.document.source) 'All file browsing and export checks preserve the editing document'
    Request 'shutdown' | Out-Null
    if(-not $process.WaitForExit(8000)){throw 'Test backend did not shut down normally'}
    @{ok=$true;count=$checks.Count;checks=$checks;root=$root} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $root 'results.json') -Encoding utf8
    Write-Output ('RESULT '+$checks.Count+' in-game file browser/export checks passed; '+$root)
} finally {
    if(-not $process.HasExited){try{Request 'shutdown' | Out-Null;$process.WaitForExit(8000)|Out-Null}catch{Write-Warning $_}}
}
