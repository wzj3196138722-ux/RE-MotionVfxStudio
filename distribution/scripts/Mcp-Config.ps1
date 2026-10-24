param([switch]$NonInteractive)
. (Join-Path $PSScriptRoot 'Common.ps1')
$configuration=Read-Config
$executable=Join-Path $packageRoot 'mcp/VfxStudio.Mcp.exe'
$server=[ordered]@{command=$executable;args=@('--config',$configPath)}
Write-JsonFile (Join-Path $packageRoot 'mcp-config.json') @{mcpServers=@{motionvfx=$server}}
function Quote-Toml([string]$Value) {return '"' + $Value.Replace('\','\\').Replace('"','\"') + '"'}
$toml="[mcp_servers.motionvfx]`r`ncommand = " + (Quote-Toml $executable) + "`r`nargs = [" + (Quote-Toml '--config') + ', ' + (Quote-Toml $configPath) + "]`r`n"
[IO.File]::WriteAllText((Join-Path $packageRoot 'mcp-config.toml'),$toml,[Text.UTF8Encoding]::new($false))
Write-Host '已生成 mcp-config.json 与 mcp-config.toml；按 AI 客户端所需格式添加此服务器。'
if(-not $NonInteractive){Read-Host '按回车关闭' | Out-Null}
