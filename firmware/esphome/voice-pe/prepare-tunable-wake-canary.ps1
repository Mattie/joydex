[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-tunable-wake-0.1.34-repro"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$basePreparationScript = Join-Path $PSScriptRoot "prepare-modern-sendspin-canary.ps1"
$overlayPath = Join-Path $PSScriptRoot "joydex-voice-pe-tunable-wake-canary.yaml"

foreach ($requiredPath in @($basePreparationScript, $overlayPath, $SecretsPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "A required tunable-wake source is missing: $requiredPath"
    }
}

& $basePreparationScript -Destination $destinationPath -SecretsPath $SecretsPath
if ($LASTEXITCODE -ne 0) {
    throw "The reviewed 0.1.33 source preparation failed."
}

$preparedOverlayPath = Join-Path $destinationPath "joydex-voice-pe-tunable-wake-canary.yaml"
Copy-Item -LiteralPath $overlayPath -Destination $preparedOverlayPath -Force

if ((Get-FileHash -LiteralPath $overlayPath -Algorithm SHA256).Hash -cne
    (Get-FileHash -LiteralPath $preparedOverlayPath -Algorithm SHA256).Hash) {
    throw "Prepared tunable-wake overlay parity failed."
}

Write-Output "Prepared source-only Voice PE 0.1.34 tunable-wake candidate at $destinationPath"
Write-Output "Reviewed 0.1.33 base source and tunable-wake overlay parity: PASS"
Write-Output "No device connection or upload was attempted."
