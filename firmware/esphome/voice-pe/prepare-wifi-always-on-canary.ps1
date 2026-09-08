[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-wifi-always-on-0.1.30-repro"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$basePreparationScript = Join-Path $PSScriptRoot "prepare-sendspin-lease-fix-canary.ps1"
$overlayPath = Join-Path $PSScriptRoot "joydex-voice-pe-wifi-always-on-canary.yaml"
$expectedOverlaySha256 = "5EB1876991FDF53B674C9C008790EF32F12405B77158B14F667835E21B46D118"
$expectedLeaseFixedHubSha256 = "CBAE7507ECBA8BF31A177A446E8E95BF6F01B5B13F3EE52B4446F05C00B334DA"

foreach ($requiredPath in @($basePreparationScript, $overlayPath, $SecretsPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "A required always-on Wi-Fi canary source is missing: $requiredPath"
    }
}
if ((Get-FileHash -LiteralPath $overlayPath -Algorithm SHA256).Hash -ne $expectedOverlaySha256) {
    throw "The always-on Wi-Fi overlay differs from the reviewed 0.1.30 source."
}

& $basePreparationScript -Destination $destinationPath -SecretsPath $SecretsPath
if ($LASTEXITCODE -ne 0) {
    throw "The exact 0.1.29 lease-fix preparation failed."
}

$sendspinHubPath = Join-Path $destinationPath ".joydex-sendspin\esphome\components\sendspin\sendspin_hub.cpp"
if ((Get-FileHash -LiteralPath $sendspinHubPath -Algorithm SHA256).Hash -ne $expectedLeaseFixedHubSha256) {
    throw "The prepared always-on Wi-Fi canary does not retain the reviewed Sendspin source."
}

$preparedOverlayPath = Join-Path $destinationPath "joydex-voice-pe-wifi-always-on-canary.yaml"
Copy-Item -LiteralPath $overlayPath -Destination $preparedOverlayPath -Force
if ((Get-FileHash -LiteralPath $preparedOverlayPath -Algorithm SHA256).Hash -ne $expectedOverlaySha256) {
    throw "The prepared 0.1.30 overlay differs from tracked source."
}

Write-Output "Prepared source-only Voice PE 0.1.30 always-on Wi-Fi canary at $destinationPath"
Write-Output "Configured Wi-Fi power saving: none"
Write-Output "0.1.29 source and 0.1.30 overlay parity: PASS"
Write-Output "No device connection or upload was attempted."
