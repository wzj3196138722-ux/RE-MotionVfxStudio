$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'GameWindowSelection.ps1')
$checks = 0
function Check([bool]$condition, [string]$label) {
    if (-not $condition) { throw $label }
    $script:checks++
    Write-Output "PASS $label"
}
function Window([long]$handle,[uint32]$process,[string]$class,[string]$title,[bool]$visible=$true,[int]$width=1920,[int]$height=1080) {
    $result=[MotionVfxWindowCandidate]::new()
    $result.Handle=[IntPtr]$handle;$result.ProcessId=$process;$result.ClassName=$class;$result.Title=$title
    $result.Visible=$visible;$result.Width=$width;$result.Height=$height
    return $result
}
$console=Window 1 300 'ConsoleWindowClass' 'Monster Hunter Wilds'
$render=Window 2 300 'via' 'Monster Hunter Wilds'
$other=Window 3 400 'via' 'Monster Hunter Wilds'
$dialog=Window 4 300 '#32770' 'Confirmation'
$hidden=Window 5 300 'via' 'Monster Hunter Wilds' $false
$small=Window 6 300 'via' 'Monster Hunter Wilds' $true 20 20
$chosen=[MotionVfxRenderWindowSelector]::Choose([MotionVfxWindowCandidate[]]@($console,$render,$other,$dialog,$hidden,$small),@([uint32]300),[IntPtr]3)
Check ($chosen.Handle -eq [IntPtr]2) 'same-PID via render selected despite a console MainWindowHandle and another foreground application'
Check ($null -eq [MotionVfxRenderWindowSelector]::Choose([MotionVfxWindowCandidate[]]@($console,$dialog),@([uint32]300),[IntPtr]1)) 'same-PID console and native dialog never used as render fallback'
Check ($null -eq [MotionVfxRenderWindowSelector]::Choose([MotionVfxWindowCandidate[]]@($other),@([uint32]300),[IntPtr]3)) 'matching title/class in another process is rejected'
Check ($null -eq [MotionVfxRenderWindowSelector]::Choose([MotionVfxWindowCandidate[]]@($hidden,$small),@([uint32]300),[IntPtr]5)) 'hidden and invalid-size windows are rejected'
$extra=Window 7 300 'via' 'Other render surface'
Check ([MotionVfxRenderWindowSelector]::Choose([MotionVfxWindowCandidate[]]@($extra,$render),@([uint32]300),[IntPtr]7).Handle -eq [IntPtr]7) 'already foreground eligible game surface preferred'
Check ([MotionVfxRenderWindowSelector]::Choose([MotionVfxWindowCandidate[]]@($extra,$render),@([uint32]300),[IntPtr]3).Handle -eq [IntPtr]2) 'known game title preferred among eligible game surfaces'
foreach($name in @('Capture-GameWindow.ps1','Input-GameWindow.ps1')) {
    $path=Join-Path $PSScriptRoot $name
    $tokens=$null;$errors=$null
    $ast=[System.Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
    Check ($errors.Count -eq 0) "$name PowerShell syntax valid"
    $source=Get-Content $path -Raw
    Check (-not $source.Contains('$game.MainWindowHandle')) "$name never targets Process.MainWindowHandle"
    Check ($source.Contains('Get-MotionVfxRenderWindow')) "$name uses strict render selector"
    # Compile only embedded declarations. No script body, input, window capture,
    # process enumeration or foreground manipulation is executed by this test.
    $declaration=$ast.FindAll({param($node) $node -is [System.Management.Automation.Language.StringConstantExpressionAst] -and $node.Value.Contains('public static class LiveEditor')},$true)|Select-Object -First 1
    Add-Type $declaration.Value
    Check ($true) "$name embedded C# compiles without executing it"
}
Write-Output "Game window selector: $checks checks passed; no input or capture executed."
