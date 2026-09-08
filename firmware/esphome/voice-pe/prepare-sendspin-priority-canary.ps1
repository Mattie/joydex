[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-sendspin-priority-0.1.27-repro"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
$sendspinRepository = "https://github.com/esphome/esphome.git"
$sendspinCommit = "aef3a4afd9c7186751fc9d097742325b3390572f"
$expectedStockSourceSha256 = "16332336CE308EB6C6B3A29F7FF98F0AA9A37C28DBE083D2CF6FCB9F3B6D53FE"
$expectedStockHeaderSha256 = "9C7D6B630A071261AE8E6FE87FF86ADA735FBBEB9080AA4CF16F47EDA256794E"
$expectedPatchedSourceSha256 = "40E1EF496328FAB880E1E60DED1A3517083801DAEC28D7AF122D718D06F521BE"
$expectedPatchedHeaderSha256 = "5DB5612E718CECEF257B9E38446ABA29F78F9B234AE520A44E7BBEDAC10CFD4F"
$expectedPatchSha256 = "9E46D1E7B17C810B4D2618D0B8452C219A518EA145E4AD2D41128FD163176F6F"
$expectedOverlaySha256 = "22635E4D01FA6C73FC8C46153106C3D9C4EF545EAD2F35027F83C1E76717167B"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$basePreparationScript = Join-Path $PSScriptRoot "prepare-audio-canary.ps1"
$priorityPatchPath = Join-Path $PSScriptRoot "sendspin-player-priority.patch"
$overlayPath = Join-Path $PSScriptRoot "joydex-voice-pe-sendspin-priority-canary.yaml"

foreach ($requiredPath in @($basePreparationScript, $priorityPatchPath, $overlayPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "A required Sendspin priority canary source is missing: $requiredPath"
    }
}
if ((Get-FileHash -LiteralPath $priorityPatchPath -Algorithm SHA256).Hash -ne $expectedPatchSha256) {
    throw "The Sendspin player-priority patch differs from the reviewed candidate."
}
if ((Get-FileHash -LiteralPath $overlayPath -Algorithm SHA256).Hash -ne $expectedOverlaySha256) {
    throw "The Sendspin player-priority overlay differs from the reviewed 0.1.27 candidate."
}

& $basePreparationScript -Destination $destinationPath -SecretsPath $SecretsPath
if ($LASTEXITCODE -ne 0) {
    throw "The exact 0.1.26 audio-canary preparation failed."
}

$sendspinPath = Join-Path $destinationPath ".joydex-sendspin"
if (-not (Test-Path -LiteralPath $sendspinPath)) {
    git clone --filter=blob:none --no-checkout $sendspinRepository $sendspinPath
    if ($LASTEXITCODE -ne 0) {
        throw "Could not clone the pinned Sendspin component source."
    }

    git -C $sendspinPath sparse-checkout set esphome/components/sendspin
    if ($LASTEXITCODE -ne 0) {
        throw "Could not select the Sendspin component source."
    }

    git -C $sendspinPath checkout --detach $sendspinCommit
    if ($LASTEXITCODE -ne 0) {
        throw "Could not check out the pinned Sendspin component revision."
    }
}

$resolvedSendspinCommit = (git -C $sendspinPath rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $resolvedSendspinCommit -ne $sendspinCommit) {
    throw "The Sendspin component workspace is not at pinned commit $sendspinCommit."
}

$sendspinSourcePath = Join-Path $sendspinPath "esphome\components\sendspin\media_source\sendspin_media_source.cpp"
$sendspinHeaderPath = Join-Path $sendspinPath "esphome\components\sendspin\media_source\sendspin_media_source.h"
$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = "SilentlyContinue"
git -C $sendspinPath apply --reverse --check $priorityPatchPath 2>$null
$patchAlreadyApplied = $LASTEXITCODE -eq 0
$ErrorActionPreference = $previousErrorActionPreference

if (-not $patchAlreadyApplied) {
    if ((Get-FileHash -LiteralPath $sendspinSourcePath -Algorithm SHA256).Hash -ne $expectedStockSourceSha256 -or
        (Get-FileHash -LiteralPath $sendspinHeaderPath -Algorithm SHA256).Hash -ne $expectedStockHeaderSha256) {
        throw "The pinned Sendspin source differs from the exact stock revision expected by the patch."
    }

    git -C $sendspinPath apply --check $priorityPatchPath
    if ($LASTEXITCODE -ne 0) {
        throw "The Sendspin player-priority patch does not apply cleanly."
    }
    git -C $sendspinPath apply $priorityPatchPath
    if ($LASTEXITCODE -ne 0) {
        throw "Could not apply the Sendspin player-priority patch."
    }
}

if ((Get-FileHash -LiteralPath $sendspinSourcePath -Algorithm SHA256).Hash -ne $expectedPatchedSourceSha256 -or
    (Get-FileHash -LiteralPath $sendspinHeaderPath -Algorithm SHA256).Hash -ne $expectedPatchedHeaderSha256) {
    throw "The prepared Sendspin component differs from the reviewed player-priority patch."
}

$componentChanges = @(git -C $sendspinPath diff --name-only)
if (($LASTEXITCODE -ne 0) -or
    ($componentChanges.Count -ne 2) -or
    ($componentChanges -notcontains "esphome/components/sendspin/media_source/sendspin_media_source.cpp") -or
    ($componentChanges -notcontains "esphome/components/sendspin/media_source/sendspin_media_source.h")) {
    throw "The Sendspin component workspace contains unexpected changes: $componentChanges"
}

$preparedOverlayPath = Join-Path $destinationPath "joydex-voice-pe-sendspin-priority-canary.yaml"
Copy-Item -LiteralPath $overlayPath -Destination $preparedOverlayPath -Force
if ((Get-FileHash -LiteralPath $preparedOverlayPath -Algorithm SHA256).Hash -ne $expectedOverlaySha256) {
    throw "The prepared 0.1.27 overlay differs from tracked source."
}

Write-Output "Prepared source-only Voice PE 0.1.27 Sendspin player-priority canary at $destinationPath"
Write-Output "Pinned Sendspin commit: $resolvedSendspinCommit"
Write-Output "Patched Sendspin source and overlay parity: PASS"
Write-Output "No device connection or upload was attempted."
