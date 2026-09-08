[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-source"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
$expectedOverlaySha256 = "2C3BECFC26437360FD6D4CC02BAA6CE67078555028CA35C79E00C0858A72C13B"
$expectedWakeSoundSha256 = "8AAF3B4EA12C77571B0F750EE3891BF4517F3F212B908A93563E6FC57A26F6AD"
$basePreparationScript = Join-Path $PSScriptRoot "Prepare-VoicePeBase.ps1"
$overlayPath = Join-Path $PSScriptRoot "joydex-voice-pe-cues.yaml"
$wakeSoundPath = Join-Path $PSScriptRoot "sounds\WakeBlip.flac"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)

if (-not (Test-Path -LiteralPath $SecretsPath -PathType Leaf)) {
    throw "The explicit ESPHome secrets file does not exist: $SecretsPath"
}

$overlaySha256 = (Get-FileHash -LiteralPath $overlayPath -Algorithm SHA256).Hash
if ($overlaySha256 -ne $expectedOverlaySha256) {
    throw "The Voice PE cue and session-state overlay differs from the reviewed source."
}

$wakeSoundSha256 = (Get-FileHash -LiteralPath $wakeSoundPath -Algorithm SHA256).Hash
if ($wakeSoundSha256 -ne $expectedWakeSoundSha256) {
    throw "WakeBlip.flac differs from the reviewed wake acknowledgement."
}

& $basePreparationScript -Destination $destinationPath -SecretsPath $SecretsPath
if ($LASTEXITCODE -ne 0) {
    throw "The pinned Voice PE base preparation failed."
}

$preparedSoundDirectory = Join-Path $destinationPath "sounds"
if (-not (Test-Path -LiteralPath $preparedSoundDirectory)) {
    New-Item -ItemType Directory -Path $preparedSoundDirectory | Out-Null
}

Copy-Item -LiteralPath $overlayPath -Destination (Join-Path $destinationPath "joydex-voice-pe-cues.yaml") -Force
Copy-Item -LiteralPath $wakeSoundPath -Destination (Join-Path $preparedSoundDirectory "WakeBlip.flac") -Force

$preparedOverlaySha256 = (Get-FileHash -LiteralPath (Join-Path $destinationPath "joydex-voice-pe-cues.yaml") -Algorithm SHA256).Hash
$preparedSoundSha256 = (Get-FileHash -LiteralPath (Join-Path $preparedSoundDirectory "WakeBlip.flac") -Algorithm SHA256).Hash
if ($preparedOverlaySha256 -ne $expectedOverlaySha256 -or $preparedSoundSha256 -ne $expectedWakeSoundSha256) {
    throw "The prepared cue overlay or wake sound failed source parity."
}

Write-Output "Prepared the Voice PE cue and session-state layer at $destinationPath"
Write-Output "Wake sound source parity: PASS"
Write-Output "No device connection or upload was attempted."
