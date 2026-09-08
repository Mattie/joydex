[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-canary"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
$officialRepository = "https://github.com/esphome/home-assistant-voice-pe.git"
$officialCommit = "a163e7b980c572df9d3811ff98094350f5aae541"
$expectedPatchedSha256 = "09579644C0988822222A07ED72BC92ACC15993220D4ECE244B220F952763AFB9"
$espHomeRepository = "https://github.com/esphome/esphome.git"
$espHomeCommit = "99f7e9aeb74447fa489fefb80b8129623e916816"
$expectedStreamingModelSha256 = "FF2D5B0A3142A52DF8CBB7AA0CBE946BEADC770F59D451705CA75251E1671BA8"
$expectedMicroWakeWordSha256 = "2EF3D91AD48E96CDC29057C858C5AB67516D4749EECF96DE43878D428C5828A4"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$patchPath = Join-Path $PSScriptRoot "exclusive-joydex-wake.patch"
$runtimePatchPath = Join-Path $PSScriptRoot "micro-wake-window-runtime.patch"
$overlayPath = Join-Path $PSScriptRoot "joydex-voice-pe-canary.yaml"
$secretsCopyScript = Join-Path $PSScriptRoot "Copy-VerifiedVoicePeSecrets.ps1"
if (-not (Test-Path -LiteralPath $secretsCopyScript -PathType Leaf)) {
    throw "The Voice PE secrets safety helper is missing: $secretsCopyScript"
}

if (-not (Test-Path -LiteralPath $destinationPath)) {
    git clone --no-checkout $officialRepository $destinationPath
    if ($LASTEXITCODE -ne 0) {
        throw "Could not clone the official Voice PE source."
    }

    git -C $destinationPath checkout --detach $officialCommit
    if ($LASTEXITCODE -ne 0) {
        throw "Could not check out the pinned Voice PE commit."
    }
}

$resolvedCommit = (git -C $destinationPath rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $resolvedCommit -ne $officialCommit) {
    throw "The canary workspace is not at the pinned official commit $officialCommit."
}

$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = "SilentlyContinue"
git -C $destinationPath apply --reverse --check $patchPath 2>$null
$reverseCheckExitCode = $LASTEXITCODE
$ErrorActionPreference = $previousErrorActionPreference
$alreadyPatched = $reverseCheckExitCode -eq 0
if (-not $alreadyPatched) {
    git -C $destinationPath apply --check $patchPath
    if ($LASTEXITCODE -ne 0) {
        throw "The exclusive Joydex wake patch does not apply cleanly to the pinned source."
    }

    git -C $destinationPath apply $patchPath
    if ($LASTEXITCODE -ne 0) {
        throw "Could not apply the exclusive Joydex wake patch."
    }
}

$preparedBasePath = Join-Path $destinationPath "home-assistant-voice.yaml"
$normalizedBase = [System.IO.File]::ReadAllText($preparedBasePath).Replace("`r`n", "`n")
$normalizedBytes = [System.Text.Encoding]::UTF8.GetBytes($normalizedBase)
$patchedSha256 = [Convert]::ToHexString(
    [System.Security.Cryptography.SHA256]::HashData($normalizedBytes))
if ($patchedSha256 -ne $expectedPatchedSha256) {
    throw "The prepared Voice PE base differs from the exact reviewed Joydex patch."
}

$otherTrackedChanges = git -C $destinationPath diff --name-only -- . ":(exclude)home-assistant-voice.yaml"
if ($LASTEXITCODE -ne 0 -or $null -ne $otherTrackedChanges) {
    throw "The canary workspace contains unexpected tracked changes: $otherTrackedChanges"
}

$espHomePath = Join-Path $destinationPath ".joydex-esphome"
if (-not (Test-Path -LiteralPath $espHomePath)) {
    git clone --filter=blob:none --no-checkout $espHomeRepository $espHomePath
    if ($LASTEXITCODE -ne 0) {
        throw "Could not clone the pinned ESPHome component source."
    }

    git -C $espHomePath sparse-checkout set esphome/components/micro_wake_word
    if ($LASTEXITCODE -ne 0) {
        throw "Could not select the microWakeWord component source."
    }

    git -C $espHomePath checkout --detach $espHomeCommit
    if ($LASTEXITCODE -ne 0) {
        throw "Could not check out the pinned ESPHome component revision."
    }
}

$resolvedEspHomeCommit = (git -C $espHomePath rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $resolvedEspHomeCommit -ne $espHomeCommit) {
    throw "The runtime component workspace is not at pinned ESPHome commit $espHomeCommit."
}

$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = "SilentlyContinue"
git -C $espHomePath apply --reverse --check $runtimePatchPath 2>$null
$runtimeReverseCheckExitCode = $LASTEXITCODE
$ErrorActionPreference = $previousErrorActionPreference
$runtimeAlreadyPatched = $runtimeReverseCheckExitCode -eq 0
if (-not $runtimeAlreadyPatched) {
    git -C $espHomePath apply --check $runtimePatchPath
    if ($LASTEXITCODE -ne 0) {
        throw "The runtime wake-window patch does not apply cleanly to pinned ESPHome."
    }

    git -C $espHomePath apply $runtimePatchPath
    if ($LASTEXITCODE -ne 0) {
        throw "Could not apply the runtime wake-window patch."
    }
}

$streamingModelPath = Join-Path $espHomePath "esphome\components\micro_wake_word\streaming_model.h"
$streamingModelSha256 = (Get-FileHash -LiteralPath $streamingModelPath -Algorithm SHA256).Hash
if ($streamingModelSha256 -ne $expectedStreamingModelSha256) {
    throw "The runtime microWakeWord component differs from the exact reviewed patch."
}

$microWakeWordPath = Join-Path $espHomePath "esphome\components\micro_wake_word\micro_wake_word.h"
$microWakeWordSha256 = (Get-FileHash -LiteralPath $microWakeWordPath -Algorithm SHA256).Hash
if ($microWakeWordSha256 -ne $expectedMicroWakeWordSha256) {
    throw "The runtime VAD tuning extension differs from the exact reviewed patch."
}

$componentChanges = @(git -C $espHomePath diff --name-only)
if (($LASTEXITCODE -ne 0) -or
    ($componentChanges.Count -ne 2) -or
    ($componentChanges -notcontains "esphome/components/micro_wake_word/micro_wake_word.h") -or
    ($componentChanges -notcontains "esphome/components/micro_wake_word/streaming_model.h")) {
    throw "The runtime component workspace contains unexpected changes: $componentChanges"
}

Copy-Item -LiteralPath $overlayPath -Destination (Join-Path $destinationPath "joydex-voice-pe-canary.yaml") -Force
$preparedSecretsPath = Join-Path $destinationPath "secrets.yaml"
& $secretsCopyScript -SourcePath $SecretsPath -DestinationPath $preparedSecretsPath

$remainingStarts = Select-String -LiteralPath $preparedBasePath -Pattern "voice_assistant.start" -SimpleMatch
if ($null -ne $remainingStarts) {
    throw "The patched Voice PE source still contains a voice_assistant.start action: $remainingStarts"
}

$movingReferences = Select-String -LiteralPath $preparedBasePath -Pattern "raw/dev", "ref: dev", "raw/main", "/main/", "model: hey_" -SimpleMatch
if ($null -ne $movingReferences) {
    throw "The patched Voice PE source still contains a moving upstream reference: $movingReferences"
}

Write-Output "Prepared source-only Voice PE canary at $destinationPath"
Write-Output "Pinned official commit: $resolvedCommit"
Write-Output "Pinned ESPHome component commit: $resolvedEspHomeCommit"
Write-Output "Copied the explicitly selected ESPHome secrets file to $preparedSecretsPath"
Write-Output "No device connection or upload was attempted."
