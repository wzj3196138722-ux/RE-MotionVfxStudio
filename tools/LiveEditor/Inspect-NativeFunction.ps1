param([Parameter(Mandatory=$true)][UInt64]$Address,[Parameter(Mandatory=$true)][string]$OutputBase,[int]$Size=4096)
$ErrorActionPreference='Stop'
if($Address -eq 0 -or $Size -lt 1 -or $Size -gt 8192){throw 'A nonzero live TDB function address and 1..8192 bytes are required.'}
$hex='0x'+$Address.ToString('X')
$response=Invoke-RestMethod -Uri ('http://localhost:8899/api/memory?address='+$hex+'&size='+$Size) -NoProxy
if($response.error){throw $response.error}
$code=[Collections.Generic.List[byte]]::new()
foreach($line in $response.dump){
    $columns=($line -split '\|')[0].Trim() -split '\s+'
    for($i=1;$i -lt $columns.Length;$i++){if($columns[$i] -match '^[0-9A-Fa-f]{2}$'){$code.Add([Convert]::ToByte($columns[$i],16))}}
}
if($code.Count -ne $Size){throw 'Incomplete function-byte read.'}
$base=[IO.Path]::GetFullPath($OutputBase)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($base))|Out-Null
$response|ConvertTo-Json -Depth 4|Set-Content -LiteralPath ($base+'.json') -Encoding UTF8
# A minimal AMD64 COFF .text section lets Visual Studio disassemble the copied
# bytes offline. No executable image is created or loaded. Addresses in the
# dump are offsets from the captured function address recorded in the JSON.
$stream=[IO.File]::Create($base+'.obj');$w=[IO.BinaryWriter]::new($stream)
try{
    $w.Write([UInt16]0x8664);$w.Write([UInt16]1);$w.Write([UInt32]0);$w.Write([UInt32]0);$w.Write([UInt32]0);$w.Write([UInt16]0);$w.Write([UInt16]0)
    $w.Write([Text.Encoding]::ASCII.GetBytes(".text`0`0`0"));$w.Write([UInt32]0);$w.Write([UInt32]0);$w.Write([UInt32]$code.Count);$w.Write([UInt32]60)
    $w.Write([UInt32]0);$w.Write([UInt32]0);$w.Write([UInt16]0);$w.Write([UInt16]0);$w.Write([UInt32]0x60000020);$w.Write($code.ToArray())
}finally{$w.Dispose();$stream.Dispose()}
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$dump=Get-ChildItem -Path (Join-Path $vs 'VC/Tools/MSVC/*/bin/Hostx64/x64/dumpbin.exe')|Sort-Object FullName -Descending|Select-Object -First 1
if(-not $dump){throw 'Visual Studio dumpbin was not found.'}
& $dump.FullName /nologo /disasm ($base+'.obj')|Set-Content -LiteralPath ($base+'.asm') -Encoding UTF8
if($LASTEXITCODE -ne 0){throw 'Offline disassembly failed.'}
Write-Output ($base+'.asm')
