[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath,
    [string]$Destination,
    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($PSVersionTable.PSVersion -lt [version]"7.2") {
    throw "The Voice PE build requires PowerShell 7.2 or later."
}

$usesDefaultDestination = [string]::IsNullOrWhiteSpace($Destination)
if ($usesDefaultDestination) {
    $directoryName = if ($ValidateOnly) { "voice-pe-validate" } else { "voice-pe-build" }
    $Destination = Join-Path $PSScriptRoot "..\..\..\.tools\$directoryName"
}

$destinationPath = [IO.Path]::GetFullPath($Destination)
$resolvedSecretsPath = [IO.Path]::GetFullPath($SecretsPath)
$preparationScript = Join-Path $PSScriptRoot "prepare-session-controls-canary.ps1"
$configurationName = "joydex-voice-pe-session-controls-canary.yaml"
$configurationPath = Join-Path $destinationPath $configurationName
$espHomeExecutable = Join-Path $PSScriptRoot "..\.venv\Scripts\esphome.exe"
$gitUnixTools = Join-Path $env:ProgramFiles "Git\usr\bin"

foreach ($requiredPath in @($resolvedSecretsPath, $preparationScript, $espHomeExecutable)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "A required Voice PE build input is missing: $requiredPath"
    }
}

$gitPatch = Join-Path $gitUnixTools "patch.exe"
if (Test-Path -LiteralPath $gitPatch -PathType Leaf) {
    $env:PATH = "$gitUnixTools;$env:PATH"
}
if ($null -eq (Get-Command patch.exe -ErrorAction SilentlyContinue)) {
    throw "patch.exe is required. Install Git for Windows with its Unix tools."
}

if ($usesDefaultDestination -and (Test-Path -LiteralPath $destinationPath)) {
    $expectedDefaultPath = [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot "..\..\..\.tools\$directoryName"))
    if (-not $destinationPath.Equals($expectedDefaultPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to recreate an unexpected Voice PE build directory: $destinationPath"
    }

    Remove-Item -LiteralPath $destinationPath -Recurse -Force
}

& $preparationScript -Destination $destinationPath -SecretsPath $resolvedSecretsPath

& $espHomeExecutable config $configurationPath
if ($LASTEXITCODE -ne 0) {
    throw "The prepared Voice PE configuration did not validate."
}

if ($ValidateOnly) {
    Write-Output "Validated the prepared Voice PE source at $destinationPath"
    Write-Output "No firmware was compiled and no device connection was attempted."
    return
}

& $espHomeExecutable compile $configurationPath
if ($LASTEXITCODE -ne 0) {
    throw "The Voice PE firmware build failed."
}

$pioEnvironment = Join-Path $destinationPath ".esphome\build\home-assistant-voice\.pioenvs\home-assistant-voice"
$otaImagePath = Join-Path $pioEnvironment "firmware.ota.bin"
$factoryImagePath = Join-Path $pioEnvironment "firmware.factory.bin"
foreach ($imagePath in @($otaImagePath, $factoryImagePath)) {
    if (-not (Test-Path -LiteralPath $imagePath -PathType Leaf)) {
        throw "The build completed without producing the expected image: $imagePath"
    }
}

Write-Output "Built the source-only Joydex Voice PE firmware."
Write-Output "OTA image: $otaImagePath"
Write-Output "Factory image: $factoryImagePath"
Write-Output "No device connection or upload was attempted."
