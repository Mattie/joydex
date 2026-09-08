[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-source"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
$basePreparationScript = Join-Path $PSScriptRoot "Prepare-VoicePeCues.ps1"
$overlayPath = Join-Path $PSScriptRoot "joydex-voice-pe-audio.yaml"
$componentPath = Join-Path $PSScriptRoot "components\joydex_lan_audio"
$readySoundPath = Join-Path $PSScriptRoot "sounds\ReadyBlip.flac"
$endSoundPath = Join-Path $PSScriptRoot "sounds\EndBlip.flac"
$expectedReadySoundSha256 = "2E56276BCDB96F538427E9840D7FB01F4D435F69B79B857E682D5CCB219F3D58"
$expectedEndSoundSha256 = "FC0365A43D7D0666FD3BFBEAC490825A22C77964AABC7407F2D39D0FCDA483D5"
$expectedOverlaySha256 = "6EA0D5A7102E0DB9E01612AFB7806C0C541F4932D66E672B9C5D8379887A9D0A"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)

if (-not (Test-Path -LiteralPath $SecretsPath -PathType Leaf)) {
    throw "The explicit ESPHome secrets file does not exist: $SecretsPath"
}
if (-not (Test-Path -LiteralPath $readySoundPath -PathType Leaf)) {
    throw "The local ready sound does not exist: $readySoundPath"
}
if (-not (Test-Path -LiteralPath $endSoundPath -PathType Leaf)) {
    throw "The local end sound does not exist: $endSoundPath"
}
if ((Get-FileHash -LiteralPath $readySoundPath -Algorithm SHA256).Hash -ne $expectedReadySoundSha256) {
    throw "ReadyBlip.flac differs from the reviewed connected-session cue."
}
if ((Get-FileHash -LiteralPath $endSoundPath -Algorithm SHA256).Hash -ne $expectedEndSoundSha256) {
    throw "EndBlip.flac differs from the reviewed completed-session cue."
}
if ((Get-FileHash -LiteralPath $overlayPath -Algorithm SHA256).Hash -ne $expectedOverlaySha256) {
    throw "The Voice PE audio overlay differs from the reviewed source."
}

& $basePreparationScript -Destination $destinationPath -SecretsPath $SecretsPath
if ($LASTEXITCODE -ne 0) {
    throw "The Voice PE cue and session-state preparation failed."
}

$preparedComponentsPath = Join-Path $destinationPath "components"
if (-not (Test-Path -LiteralPath $preparedComponentsPath)) {
    New-Item -ItemType Directory -Path $preparedComponentsPath | Out-Null
}

$preparedComponentPath = Join-Path $preparedComponentsPath "joydex_lan_audio"
if (-not (Test-Path -LiteralPath $preparedComponentPath)) {
    New-Item -ItemType Directory -Path $preparedComponentPath | Out-Null
}
Get-ChildItem -LiteralPath $componentPath -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $preparedComponentPath $_.Name) -Force
}
Copy-Item -LiteralPath $overlayPath -Destination (Join-Path $destinationPath "joydex-voice-pe-audio.yaml") -Force
$preparedSoundDirectory = Join-Path $destinationPath "sounds"
if (-not (Test-Path -LiteralPath $preparedSoundDirectory)) {
    New-Item -ItemType Directory -Path $preparedSoundDirectory | Out-Null
}
Copy-Item -LiteralPath $readySoundPath -Destination (Join-Path $preparedSoundDirectory "ReadyBlip.flac") -Force
Copy-Item -LiteralPath $endSoundPath -Destination (Join-Path $preparedSoundDirectory "EndBlip.flac") -Force

$sourceFiles = Get-ChildItem -LiteralPath $componentPath -File | Sort-Object Name
$preparedFiles = Get-ChildItem -LiteralPath $preparedComponentPath -File | Sort-Object Name
if ($sourceFiles.Count -ne $preparedFiles.Count) {
    throw "The prepared Joydex LAN audio component file count differs from tracked source."
}

for ($index = 0; $index -lt $sourceFiles.Count; $index++) {
    if ($sourceFiles[$index].Name -ne $preparedFiles[$index].Name) {
        throw "The prepared Joydex LAN audio component names differ from tracked source."
    }
    $sourceHash = (Get-FileHash -LiteralPath $sourceFiles[$index].FullName -Algorithm SHA256).Hash
    $preparedHash = (Get-FileHash -LiteralPath $preparedFiles[$index].FullName -Algorithm SHA256).Hash
    if ($sourceHash -ne $preparedHash) {
        throw "Prepared component parity failed for $($sourceFiles[$index].Name)."
    }
}

$sourceOverlayHash = (Get-FileHash -LiteralPath $overlayPath -Algorithm SHA256).Hash
$preparedOverlayHash = (Get-FileHash -LiteralPath (Join-Path $destinationPath "joydex-voice-pe-audio.yaml") -Algorithm SHA256).Hash
if ($sourceOverlayHash -ne $preparedOverlayHash) {
    throw "The prepared audio overlay differs from tracked source."
}
$preparedReadySoundHash = (Get-FileHash -LiteralPath (Join-Path $preparedSoundDirectory "ReadyBlip.flac") -Algorithm SHA256).Hash
if ($preparedReadySoundHash -ne $expectedReadySoundSha256) {
    throw "The prepared ready cue differs from its reviewed source."
}
$preparedEndSoundHash = (Get-FileHash -LiteralPath (Join-Path $preparedSoundDirectory "EndBlip.flac") -Algorithm SHA256).Hash
if ($preparedEndSoundHash -ne $expectedEndSoundSha256) {
    throw "The prepared end cue differs from its reviewed source."
}

Write-Output "Prepared the Voice PE microphone and control layer at $destinationPath"
Write-Output "LAN component and cue source parity: PASS"
Write-Output "No device connection or upload was attempted."
