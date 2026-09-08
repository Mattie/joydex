[CmdletBinding()]
param(
    [ValidateRange(1, 3600)]
    [int]$Seconds = 30,
    [string]$DeviceHost,
    [ValidateSet("speaker-only", "microphone-only", "duplex")]
    [string]$Direction = "duplex",
    [ValidateRange(0, 10000)]
    [int]$SettleMilliseconds = 3000,
    [string]$ExpectedName = "joydex-voice-pe",
    [string]$ExpectedMac = "02:00:00:00:00:01",
    [string]$ExpectedVersion = "0.1.21"
)

$ErrorActionPreference = "Stop"
$repositoryPath = Split-Path -Parent $PSScriptRoot
$dotnetPath = Join-Path $repositoryPath ".tools\dotnet\dotnet.exe"
$projectPath = Join-Path $repositoryPath "tools\Joydex.VoicePeUdpPrototype\Joydex.VoicePeUdpPrototype.csproj"
$assemblyPath = Join-Path $repositoryPath "tools\Joydex.VoicePeUdpPrototype\bin\Release\net8.0-windows\Joydex.VoicePeUdpPrototype.dll"
$pythonPath = Join-Path $repositoryPath ".tools\esphome-2025.12.2-pip\Scripts\python.exe"
$deviceCanaryPath = Join-Path $repositoryPath "tools\Joydex.VoicePeUdpPrototype\run_device_canary.py"

if (-not (Test-Path -LiteralPath $dotnetPath -PathType Leaf)) {
    throw "The repository-local .NET runtime is missing: $dotnetPath"
}

& $dotnetPath build $projectPath -c Release
if ($LASTEXITCODE -ne 0) {
    throw "The UDPPCM prototype failed to build."
}

if ([string]::IsNullOrWhiteSpace($DeviceHost)) {
    & $dotnetPath $assemblyPath self-test --seconds $Seconds
} else {
    if (-not (Test-Path -LiteralPath $pythonPath -PathType Leaf)) {
        throw "The pinned ESPHome Python runtime is missing: $pythonPath"
    }
    & $pythonPath $deviceCanaryPath `
        --host $DeviceHost `
        --expected-name $ExpectedName `
        --expected-mac $ExpectedMac `
        --expected-version $ExpectedVersion `
        --direction $Direction `
        --seconds $Seconds `
        --settle-ms $SettleMilliseconds `
        --dotnet $dotnetPath `
        --assembly $assemblyPath
}
exit $LASTEXITCODE
