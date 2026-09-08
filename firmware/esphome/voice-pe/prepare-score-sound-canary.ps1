[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-score-sound-diagnostic-0.1.11"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
$expectedOverlaySha256 = "FA8366AF735031CF3D5A623E91534801B31ABD4F82CC2A59B8E6E854978749D4"
$expectedWakeSoundSha256 = "8AAF3B4EA12C77571B0F750EE3891BF4517F3F212B908A93563E6FC57A26F6AD"
$basePreparationScript = Join-Path $PSScriptRoot "prepare-score-canary.ps1"
$overlayPath = Join-Path $PSScriptRoot "joydex-voice-pe-score-sound-canary.yaml"
$wakeSoundPath = Join-Path $PSScriptRoot "sounds\WakeBlip.flac"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)

if (-not (Test-Path -LiteralPath $SecretsPath -PathType Leaf)) {
    throw "The explicit ESPHome secrets file does not exist: $SecretsPath"
}

$overlaySha256 = (Get-FileHash -LiteralPath $overlayPath -Algorithm SHA256).Hash
if ($overlaySha256 -ne $expectedOverlaySha256) {
    throw "The score, sound, and session-LED overlay differs from the exact reviewed 0.1.11 configuration."
}

$wakeSoundSha256 = (Get-FileHash -LiteralPath $wakeSoundPath -Algorithm SHA256).Hash
if ($wakeSoundSha256 -ne $expectedWakeSoundSha256) {
    throw "WakeBlip.flac differs from the reviewed wake acknowledgement."
}

& $basePreparationScript -Destination $destinationPath -SecretsPath $SecretsPath
if ($LASTEXITCODE -ne 0) {
    throw "The pinned 0.1.9 score diagnostic preparation failed."
}

$preparedSoundDirectory = Join-Path $destinationPath "sounds"
if (-not (Test-Path -LiteralPath $preparedSoundDirectory)) {
    New-Item -ItemType Directory -Path $preparedSoundDirectory | Out-Null
}

Copy-Item -LiteralPath $overlayPath -Destination (Join-Path $destinationPath "joydex-voice-pe-score-sound-canary.yaml") -Force
Copy-Item -LiteralPath $wakeSoundPath -Destination (Join-Path $preparedSoundDirectory "WakeBlip.flac") -Force

$preparedOverlaySha256 = (Get-FileHash -LiteralPath (Join-Path $destinationPath "joydex-voice-pe-score-sound-canary.yaml") -Algorithm SHA256).Hash
$preparedSoundSha256 = (Get-FileHash -LiteralPath (Join-Path $preparedSoundDirectory "WakeBlip.flac") -Algorithm SHA256).Hash
if ($preparedOverlaySha256 -ne $expectedOverlaySha256 -or $preparedSoundSha256 -ne $expectedWakeSoundSha256) {
    throw "The prepared 0.1.11 overlay or wake sound failed source parity."
}

Write-Output "Prepared source-only Voice PE 0.1.11 score, sound, and session-LED diagnostic at $destinationPath"
Write-Output "Wake sound source parity: PASS"
Write-Output "No device connection or upload was attempted."
