$ErrorActionPreference='Stop'
$game=Get-Process -Name MonsterHunterWilds -ErrorAction SilentlyContinue | Select-Object -First 1
if(-not $game){ Write-Output 'Game already closed';exit 0 }
if(-not $game.CloseMainWindow()){throw 'The game did not accept a normal window close request.'}
if(-not $game.WaitForExit(20000)){throw 'Game is still closing or waiting for its own exit confirmation; it was not terminated.'}
Write-Output 'Game closed normally'
