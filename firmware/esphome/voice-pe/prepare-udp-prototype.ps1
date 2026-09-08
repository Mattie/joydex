[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-udp-prototype-0.1.21"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
$basePreparationScript = Join-Path $PSScriptRoot "prepare-score-sound-canary.ps1"
$overlayName = "joydex-voice-pe-udp-prototype.yaml"
$overlayPath = Join-Path $PSScriptRoot $overlayName
$componentName = "joydex_udp_audio_prototype"
$componentPath = Join-Path $PSScriptRoot "components\$componentName"
$readySoundPath = Join-Path $PSScriptRoot "sounds\ReadyBlip.flac"
$expectedReadySoundSha256 = "DD8BBF928C4740378310C883323F9D1DC7DED0BA011875D027BA9364255B6377"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)

if (-not (Test-Path -LiteralPath $SecretsPath -PathType Leaf)) {
    throw "The explicit ESPHome secrets file does not exist: $SecretsPath"
}
if (-not (Test-Path -LiteralPath $readySoundPath -PathType Leaf)) {
    throw "The local ready sound does not exist: $readySoundPath"
}
if ((Get-FileHash -LiteralPath $readySoundPath -Algorithm SHA256).Hash -ne $expectedReadySoundSha256) {
    throw "ReadyBlip.flac differs from the reviewed xylophone ready cue."
}

& $basePreparationScript -Destination $destinationPath -SecretsPath $SecretsPath
if ($LASTEXITCODE -ne 0) {
    throw "The exact 0.1.11 score, sound, and LED preparation failed."
}

$preparedComponentsPath = Join-Path $destinationPath "components"
if (-not (Test-Path -LiteralPath $preparedComponentsPath)) {
    New-Item -ItemType Directory -Path $preparedComponentsPath | Out-Null
}

$preparedComponentPath = Join-Path $preparedComponentsPath $componentName
if (-not (Test-Path -LiteralPath $preparedComponentPath)) {
    New-Item -ItemType Directory -Path $preparedComponentPath | Out-Null
}
Get-ChildItem -LiteralPath $componentPath -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $preparedComponentPath $_.Name) -Force
}
Copy-Item -LiteralPath $overlayPath -Destination (Join-Path $destinationPath $overlayName) -Force
$preparedSoundDirectory = Join-Path $destinationPath "sounds"
if (-not (Test-Path -LiteralPath $preparedSoundDirectory)) {
    New-Item -ItemType Directory -Path $preparedSoundDirectory | Out-Null
}
Copy-Item -LiteralPath $readySoundPath -Destination (Join-Path $preparedSoundDirectory "ReadyBlip.flac") -Force

$sourceFiles = Get-ChildItem -LiteralPath $componentPath -File | Sort-Object Name
$preparedFiles = Get-ChildItem -LiteralPath $preparedComponentPath -File | Sort-Object Name
if ($sourceFiles.Count -ne $preparedFiles.Count) {
    throw "The prepared UDPPCM prototype component file count differs from source."
}

for ($index = 0; $index -lt $sourceFiles.Count; $index++) {
    if ($sourceFiles[$index].Name -ne $preparedFiles[$index].Name) {
        throw "The prepared UDPPCM prototype component names differ from source."
    }
    $sourceHash = (Get-FileHash -LiteralPath $sourceFiles[$index].FullName -Algorithm SHA256).Hash
    $preparedHash = (Get-FileHash -LiteralPath $preparedFiles[$index].FullName -Algorithm SHA256).Hash
    if ($sourceHash -ne $preparedHash) {
        throw "Prepared component parity failed for $($sourceFiles[$index].Name)."
    }
}

$sourceOverlayHash = (Get-FileHash -LiteralPath $overlayPath -Algorithm SHA256).Hash
$preparedOverlayHash = (Get-FileHash -LiteralPath (Join-Path $destinationPath $overlayName) -Algorithm SHA256).Hash
if ($sourceOverlayHash -ne $preparedOverlayHash) {
    throw "The prepared 0.1.21 UDPPCM overlay differs from source."
}
$preparedReadySoundHash = (Get-FileHash -LiteralPath (Join-Path $preparedSoundDirectory "ReadyBlip.flac") -Algorithm SHA256).Hash
if ($preparedReadySoundHash -ne $expectedReadySoundSha256) {
    throw "The prepared ready cue differs from its reviewed source."
}

Write-Output "Prepared throwaway Voice PE 0.1.21 UDPPCM prototype at $destinationPath"
Write-Output "Component, overlay, and ready-cue source parity: PASS"
Write-Output "No device connection or upload was attempted."
