param(
    [Parameter(Mandatory)]
    [ValidateSet('compile_status', 'get_plugins', 'get_info', 'get_errors', 'get_log', 'wait_compile')]
    [string]$Method
)
$ErrorActionPreference = 'Stop'
$pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'REFrameworkNET', [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
$writer = $null
$reader = $null
$cancellation = [Threading.CancellationTokenSource]::new(20000)
try {
    $pipe.Connect(2000)
    $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 1024, $true)
    $writer.AutoFlush = $true
    $reader = [IO.StreamReader]::new($pipe, [Text.UTF8Encoding]::new($false), $false, 1024, $true)
    $request = @{ method = $Method; id = [Guid]::NewGuid().ToString('N'); timeout = 15000 }
    $writer.WriteLine(($request | ConvertTo-Json -Compress))
    $response = $reader.ReadLineAsync($cancellation.Token).AsTask().GetAwaiter().GetResult()
    if ($null -eq $response) { throw 'REFramework pipe closed without a response.' }
    Write-Output $response
} finally {
    if ($null -ne $reader) { $reader.Dispose() }
    if ($null -ne $writer) { $writer.Dispose() }
    $pipe.Dispose()
    $cancellation.Dispose()
}
