[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-sendspin-lease-fix-0.1.29-repro"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$basePreparationScript = Join-Path $PSScriptRoot "prepare-sendspin-balanced-canary.ps1"
$leasePatchPath = Join-Path $PSScriptRoot "sendspin-wifi-lease-fix.patch"
$overlayPath = Join-Path $PSScriptRoot "joydex-voice-pe-sendspin-lease-fix-canary.yaml"
$expectedLeasePatchSha256 = "2258DAB4B51958F63F288D1561CB6BC6627C5B276BB4067BC6C3B9E2386D2883"
$expectedOverlaySha256 = "5A4CF9E6F4A214B7DF962E05C91E55925BC8E1F0CF70B43D098ABACCC2D2C233"
$expectedBalancedHubSha256 = "D06FFE8A609588F3CA880016F3613EC499B54074C55AAFAC8EB64B2F9BA67F43"
$expectedLeaseFixedHubSha256 = "CBAE7507ECBA8BF31A177A446E8E95BF6F01B5B13F3EE52B4446F05C00B334DA"

foreach ($requiredPath in @($basePreparationScript, $leasePatchPath, $overlayPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "A required Wi-Fi lease-fix canary source is missing: $requiredPath"
    }
}
if ((Get-FileHash -LiteralPath $leasePatchPath -Algorithm SHA256).Hash -ne $expectedLeasePatchSha256) {
    throw "The Sendspin Wi-Fi lease patch differs from the reviewed candidate."
}
if ((Get-FileHash -LiteralPath $overlayPath -Algorithm SHA256).Hash -ne $expectedOverlaySha256) {
    throw "The Wi-Fi lease-fix overlay differs from the reviewed 0.1.29 candidate."
}

# Restore the exact 0.1.28 hub before replaying its guarded preparation. This
# keeps repeated preparations idempotent without weakening the base hash gates.
$sendspinPath = Join-Path $destinationPath ".joydex-sendspin"
if (Test-Path -LiteralPath $sendspinPath -PathType Container) {
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    git -C $sendspinPath apply --reverse --check $leasePatchPath 2>$null
    $leasePatchApplied = $LASTEXITCODE -eq 0
    $ErrorActionPreference = $previousErrorActionPreference
    if ($leasePatchApplied) {
        git -C $sendspinPath apply --reverse $leasePatchPath
        if ($LASTEXITCODE -ne 0) {
            throw "Could not restore the balanced Sendspin hub before guarded preparation."
        }
    }
}

& $basePreparationScript -Destination $destinationPath -SecretsPath $SecretsPath
if ($LASTEXITCODE -ne 0) {
    throw "The exact 0.1.28 balanced-priority preparation failed."
}

$sendspinHubPath = Join-Path $sendspinPath "esphome\components\sendspin\sendspin_hub.cpp"
if ((Get-FileHash -LiteralPath $sendspinHubPath -Algorithm SHA256).Hash -ne $expectedBalancedHubSha256) {
    throw "The prepared Sendspin hub differs from the exact source expected by the lease patch."
}
git -C $sendspinPath apply --check $leasePatchPath
if ($LASTEXITCODE -ne 0) {
    throw "The Sendspin Wi-Fi lease patch does not apply cleanly."
}
git -C $sendspinPath apply $leasePatchPath
if ($LASTEXITCODE -ne 0) {
    throw "Could not apply the Sendspin Wi-Fi lease patch."
}
if ((Get-FileHash -LiteralPath $sendspinHubPath -Algorithm SHA256).Hash -ne $expectedLeaseFixedHubSha256) {
    throw "The prepared Sendspin hub differs from the reviewed lease-fix source."
}

$componentChanges = @(git -C $sendspinPath diff --name-only)
if (($LASTEXITCODE -ne 0) -or
    ($componentChanges.Count -ne 3) -or
    ($componentChanges -notcontains "esphome/components/sendspin/sendspin_hub.cpp") -or
    ($componentChanges -notcontains "esphome/components/sendspin/media_source/sendspin_media_source.cpp") -or
    ($componentChanges -notcontains "esphome/components/sendspin/media_source/sendspin_media_source.h")) {
    throw "The lease-fix Sendspin workspace contains unexpected changes: $componentChanges"
}

$preparedOverlayPath = Join-Path $destinationPath "joydex-voice-pe-sendspin-lease-fix-canary.yaml"
Copy-Item -LiteralPath $overlayPath -Destination $preparedOverlayPath -Force
if ((Get-FileHash -LiteralPath $preparedOverlayPath -Algorithm SHA256).Hash -ne $expectedOverlaySha256) {
    throw "The prepared 0.1.29 overlay differs from tracked source."
}

Write-Output "Prepared source-only Voice PE 0.1.29 Wi-Fi lease-fix canary at $destinationPath"
Write-Output "Sendspin clock-request close bookkeeping: released flag clears to false"
Write-Output "Balanced 0.1.28 base, lease patch, and overlay parity: PASS"
Write-Output "No device connection or upload was attempted."
