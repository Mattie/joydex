[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-source"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$basePreparationScript = Join-Path $PSScriptRoot "Prepare-VoicePeSendspin.ps1"
$overlayPath = Join-Path $PSScriptRoot "joydex-voice-pe-wake-tuning.yaml"

foreach ($requiredPath in @($basePreparationScript, $overlayPath, $SecretsPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "A required wake-tuning source is missing: $requiredPath"
    }
}

& $basePreparationScript -Destination $destinationPath -SecretsPath $SecretsPath
if ($LASTEXITCODE -ne 0) {
    throw "The Sendspin source preparation failed."
}

$preparedOverlayPath = Join-Path $destinationPath "joydex-voice-pe-wake-tuning.yaml"
Copy-Item -LiteralPath $overlayPath -Destination $preparedOverlayPath -Force

if ((Get-FileHash -LiteralPath $overlayPath -Algorithm SHA256).Hash -cne
    (Get-FileHash -LiteralPath $preparedOverlayPath -Algorithm SHA256).Hash) {
    throw "Prepared wake-tuning overlay parity failed."
}

Write-Output "Prepared the Voice PE wake-tuning layer at $destinationPath"
Write-Output "Wake-tuning overlay parity: PASS"
Write-Output "No device connection or upload was attempted."
