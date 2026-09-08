[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-sendspin-balanced-0.1.28-repro"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$basePreparationScript = Join-Path $PSScriptRoot "prepare-sendspin-priority-canary.ps1"
$sendspinPatchPath = Join-Path $PSScriptRoot "sendspin-websocket-priority-balance.patch"
$joydexPatchPath = Join-Path $PSScriptRoot "joydex-httpd-priority-balance.patch"
$overlayPath = Join-Path $PSScriptRoot "joydex-voice-pe-sendspin-balanced-canary.yaml"
$expectedSendspinPatchSha256 = "26D08139678360C294209E7123AF7B8552CA7CF962FDC7FEAD346DFB0076786E"
$expectedJoydexPatchSha256 = "7856A88EBFC103B7EC9DB9FD2B09F04FCBF4BDDCBA081555732C4577C0CE9AAA"
$expectedOverlaySha256 = "67400C45F8775B24940E17A05B03B4E1CC617130E255793C356D76300D1F095C"
$expectedStockSendspinHubSha256 = "B30822022CEC4EB6DCEBBAD28144768E125D68D940E3A4A1B88AD024C1B5AF92"
$expectedPatchedSendspinHubSha256 = "D06FFE8A609588F3CA880016F3613EC499B54074C55AAFAC8EB64B2F9BA67F43"
$expectedStockJoydexSourceSha256 = "1DB76B08DEF2011BFCC680F300CCA535B4D81D8139134C2C297ACE04045DF0E4"
$expectedPatchedJoydexSourceSha256 = "173FCF9E2F477461E39B7A4E665ABD610918A320C2865F23BE51CCA41788D692"

foreach ($requiredPath in @($basePreparationScript, $sendspinPatchPath, $joydexPatchPath, $overlayPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "A required balanced-priority canary source is missing: $requiredPath"
    }
}
if ((Get-FileHash -LiteralPath $sendspinPatchPath -Algorithm SHA256).Hash -ne $expectedSendspinPatchSha256) {
    throw "The Sendspin WebSocket-priority patch differs from the reviewed candidate."
}
if ((Get-FileHash -LiteralPath $joydexPatchPath -Algorithm SHA256).Hash -ne $expectedJoydexPatchSha256) {
    throw "The Joydex HTTPD-priority patch differs from the reviewed candidate."
}
if ((Get-FileHash -LiteralPath $overlayPath -Algorithm SHA256).Hash -ne $expectedOverlaySha256) {
    throw "The balanced-priority overlay differs from the reviewed 0.1.28 candidate."
}

# The 0.1.27 preparation rejects any third Sendspin diff. If this destination
# was already prepared as 0.1.28, temporarily remove our one additional patch
# before replaying the guarded base preparation.
$sendspinPath = Join-Path $destinationPath ".joydex-sendspin"
if (Test-Path -LiteralPath $sendspinPath -PathType Container) {
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    git -C $sendspinPath apply --reverse --check $sendspinPatchPath 2>$null
    $balancePatchApplied = $LASTEXITCODE -eq 0
    $ErrorActionPreference = $previousErrorActionPreference
    if ($balancePatchApplied) {
        git -C $sendspinPath apply --reverse $sendspinPatchPath
        if ($LASTEXITCODE -ne 0) {
            throw "Could not restore the pinned Sendspin hub before guarded preparation."
        }
    }
}

& $basePreparationScript -Destination $destinationPath -SecretsPath $SecretsPath
if ($LASTEXITCODE -ne 0) {
    throw "The exact 0.1.27 Sendspin player-priority preparation failed."
}

$sendspinHubPath = Join-Path $sendspinPath "esphome\components\sendspin\sendspin_hub.cpp"
if ((Get-FileHash -LiteralPath $sendspinHubPath -Algorithm SHA256).Hash -ne $expectedStockSendspinHubSha256) {
    throw "The pinned Sendspin hub differs from the exact source expected by the balance patch."
}
git -C $sendspinPath apply --check $sendspinPatchPath
if ($LASTEXITCODE -ne 0) {
    throw "The Sendspin WebSocket-priority patch does not apply cleanly."
}
git -C $sendspinPath apply $sendspinPatchPath
if ($LASTEXITCODE -ne 0) {
    throw "Could not apply the Sendspin WebSocket-priority patch."
}
if ((Get-FileHash -LiteralPath $sendspinHubPath -Algorithm SHA256).Hash -ne $expectedPatchedSendspinHubSha256) {
    throw "The prepared Sendspin hub differs from the reviewed balanced-priority patch."
}

$componentChanges = @(git -C $sendspinPath diff --name-only)
if (($LASTEXITCODE -ne 0) -or
    ($componentChanges.Count -ne 3) -or
    ($componentChanges -notcontains "esphome/components/sendspin/sendspin_hub.cpp") -or
    ($componentChanges -notcontains "esphome/components/sendspin/media_source/sendspin_media_source.cpp") -or
    ($componentChanges -notcontains "esphome/components/sendspin/media_source/sendspin_media_source.h")) {
    throw "The balanced Sendspin workspace contains unexpected changes: $componentChanges"
}

$preparedJoydexSourcePath = Join-Path $destinationPath "components\joydex_lan_audio\joydex_lan_audio.cpp"
if ((Get-FileHash -LiteralPath $preparedJoydexSourcePath -Algorithm SHA256).Hash -ne $expectedStockJoydexSourceSha256) {
    throw "The prepared Joydex HTTPD source differs from the exact source expected by the balance patch."
}
Push-Location $destinationPath
try {
    git apply --check --no-index $joydexPatchPath
    if ($LASTEXITCODE -ne 0) {
        throw "The Joydex HTTPD-priority patch does not apply cleanly."
    }
    git apply --no-index $joydexPatchPath
    if ($LASTEXITCODE -ne 0) {
        throw "Could not apply the Joydex HTTPD-priority patch."
    }
} finally {
    Pop-Location
}
if ((Get-FileHash -LiteralPath $preparedJoydexSourcePath -Algorithm SHA256).Hash -ne $expectedPatchedJoydexSourceSha256) {
    throw "The prepared Joydex HTTPD source differs from the reviewed balanced-priority patch."
}

$preparedOverlayPath = Join-Path $destinationPath "joydex-voice-pe-sendspin-balanced-canary.yaml"
Copy-Item -LiteralPath $overlayPath -Destination $preparedOverlayPath -Force
if ((Get-FileHash -LiteralPath $preparedOverlayPath -Algorithm SHA256).Hash -ne $expectedOverlaySha256) {
    throw "The prepared 0.1.28 overlay differs from tracked source."
}

Write-Output "Prepared source-only Voice PE 0.1.28 balanced-media-priority canary at $destinationPath"
Write-Output "Sendspin player, Sendspin WebSocket, and Joydex microphone HTTPD priorities: 18"
Write-Output "Patched Sendspin, patched Joydex HTTPD, and overlay parity: PASS"
Write-Output "No device connection or upload was attempted."
