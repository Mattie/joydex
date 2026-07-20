[CmdletBinding()]
param(
    [string] $RelayPath,
    [ValidateRange(20, 1000)]
    [int] $Samples = 100,
    [ValidateRange(0, 100)]
    [int] $WarmupSamples = 10,
    [ValidateSet('Lifecycle', 'Correlation')]
    [string] $PayloadKind = 'Correlation'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($RelayPath)) {
    $RelayPath = Join-Path $PSScriptRoot '..\artifacts\Joydex\win-x64\Joydex.HookRelay.exe'
}

$relay = (Resolve-Path -LiteralPath $RelayPath).Path
$payloadPadding = 'x' * 8000
$payload = if ($PayloadKind -eq 'Correlation') {
    '{"hook_event_name":"PostToolUse","session_id":"latency-gate","turn_id":"turn","tool_name":"Bash","tool_input":{"command":"' + $payloadPadding + '"},"tool_response":{}}'
}
else {
    '{"hook_event_name":"UserPromptSubmit","session_id":"latency-gate","turn_id":"turn","prompt":"' + $payloadPadding + '"}'
}

function Invoke-RelaySample {
    param(
        [Parameter(Mandatory)]
        [string] $PipeName
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new($relay)
    $startInfo.Arguments = "--pipe $PipeName"
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        $process.StandardInput.Write($payload)
        $process.StandardInput.Close()
        $null = $process.StandardOutput.ReadToEnd()
        $errorText = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        $stopwatch.Stop()
        if ($process.ExitCode -ne 0 -or $errorText.Length -ne 0) {
            throw "Hook relay failed with exit code $($process.ExitCode): $errorText"
        }

        return $stopwatch.Elapsed.TotalMilliseconds
    }
    finally {
        $process.Dispose()
    }
}

function Measure-Relay {
    param(
        [Parameter(Mandatory)]
        [string] $Name,
        [Parameter(Mandatory)]
        [string] $PipeName
    )

    for ($index = 0; $index -lt $WarmupSamples; $index++) {
        $null = Invoke-RelaySample -PipeName $PipeName
    }

    $measurements = for ($index = 0; $index -lt $Samples; $index++) {
        Invoke-RelaySample -PipeName $PipeName
    }
    $ordered = @($measurements | Sort-Object)
    $p50Index = [Math]::Max(0, [Math]::Ceiling($ordered.Count * 0.50) - 1)
    $p95Index = [Math]::Max(0, [Math]::Ceiling($ordered.Count * 0.95) - 1)
    [pscustomobject]@{
        State = $Name
        Samples = $Samples
        P50Ms = [Math]::Round($ordered[$p50Index], 2)
        P95Ms = [Math]::Round($ordered[$p95Index], 2)
        MaximumMs = [Math]::Round($ordered[-1], 2)
        Passed = $ordered[$p95Index] -le 25 -and $ordered[-1] -le 75
    }
}

$results = [Collections.Generic.List[object]]::new()
$absentPipe = "Joydex.Latency.Absent.$([Guid]::NewGuid().ToString('N'))"
$results.Add((Measure-Relay -Name 'Absent' -PipeName $absentPipe))

$presentPipe = "Joydex.Latency.Present.$([Guid]::NewGuid().ToString('N'))"
$presentReadyName = "Local\Joydex.Latency.PresentReady.$([Guid]::NewGuid().ToString('N'))"
$presentReady = [Threading.EventWaitHandle]::new(
    $false,
    [Threading.EventResetMode]::ManualReset,
    $presentReadyName)
$presentConnections = $Samples + $WarmupSamples
$presentJob = Start-Job -ScriptBlock {
    param($PipeName, $Connections, $ReadyEventName)
    $ready = [Threading.EventWaitHandle]::OpenExisting($ReadyEventName)
    for ($index = 0; $index -lt $Connections; $index++) {
        $server = [IO.Pipes.NamedPipeServerStream]::new(
            $PipeName,
            [IO.Pipes.PipeDirection]::In,
            1,
            [IO.Pipes.PipeTransmissionMode]::Byte,
            [IO.Pipes.PipeOptions]::CurrentUserOnly)
        try {
            if ($index -eq 0) {
                $ready.Set()
                $ready.Dispose()
            }
            $server.WaitForConnection()
            $server.CopyTo([IO.Stream]::Null)
        }
        finally {
            $server.Dispose()
        }
    }
} -ArgumentList $presentPipe, $presentConnections, $presentReadyName
try {
    if (-not $presentReady.WaitOne(5000)) {
        throw 'The present-pipe benchmark server did not become ready.'
    }
    $results.Add((Measure-Relay -Name 'Present' -PipeName $presentPipe))
}
finally {
    $presentReady.Dispose()
    Stop-Job -Job $presentJob -ErrorAction SilentlyContinue
    Remove-Job -Job $presentJob -Force -ErrorAction SilentlyContinue
}

$busyPipe = "Joydex.Latency.Busy.$([Guid]::NewGuid().ToString('N'))"
$busyServer = [IO.Pipes.NamedPipeServerStream]::new(
    $busyPipe,
    [IO.Pipes.PipeDirection]::In,
    1,
    [IO.Pipes.PipeTransmissionMode]::Byte,
    [IO.Pipes.PipeOptions]::Asynchronous)
$busyWait = $busyServer.WaitForConnectionAsync()
$holder = $null
try {
    $holder = [IO.Pipes.NamedPipeClientStream]::new(
        '.',
        $busyPipe,
        [IO.Pipes.PipeDirection]::Out,
        [IO.Pipes.PipeOptions]::None)
    $holder.Connect(2000)
    $null = $busyWait.GetAwaiter().GetResult()
    $results.Add((Measure-Relay -Name 'Busy' -PipeName $busyPipe))
}
finally {
    if ($null -ne $holder) {
        $holder.Dispose()
    }
    $busyServer.Dispose()
}

$results | Format-Table -AutoSize
if (@($results | Where-Object { -not $_.Passed }).Count -ne 0) {
    exit 1
}
