[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-score-diagnostic-0.1.9"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
$officialRepository = "https://github.com/esphome/home-assistant-voice-pe.git"
$officialCommit = "a163e7b980c572df9d3811ff98094350f5aae541"
$expectedPatchedSha256 = "09579644C0988822222A07ED72BC92ACC15993220D4ECE244B220F952763AFB9"
$espHomeRepository = "https://github.com/esphome/esphome.git"
$espHomeCommit = "99f7e9aeb74447fa489fefb80b8129623e916816"
$expectedMicroWakeWordHeaderSha256 = "27B7B0F83D23CA2BC98AA82E6E128AE4B4B88E6E98FBBD8957E0D9C81A5C86E6"
$expectedMicroWakeWordSourceSha256 = "CE591854772F6597F81AE3C05C5589D72EDE8DBC835BC3B96B82AE928484BB61"
$expectedOverlaySha256 = "F99CBDB67F87B1DB6C0FE02A45510C9D644686024DB75FBDB71B4F579572FEC0"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$basePatchPath = Join-Path $PSScriptRoot "exclusive-joydex-wake.patch"
$telemetryPatchPath = Join-Path $PSScriptRoot "micro-wake-score-telemetry.patch"
$overlayPath = Join-Path $PSScriptRoot "joydex-voice-pe-score-canary.yaml"
$secretsCopyScript = Join-Path $PSScriptRoot "Copy-VerifiedVoicePeSecrets.ps1"
if (-not (Test-Path -LiteralPath $secretsCopyScript -PathType Leaf)) {
    throw "The Voice PE secrets safety helper is missing: $secretsCopyScript"
}

$overlaySha256 = (Get-FileHash -LiteralPath $overlayPath -Algorithm SHA256).Hash
if ($overlaySha256 -ne $expectedOverlaySha256) {
    throw "The score diagnostic overlay differs from the exact reviewed 0.1.9 configuration."
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
    throw "The score diagnostic workspace is not at pinned Voice PE commit $officialCommit."
}

$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = "SilentlyContinue"
git -C $destinationPath apply --reverse --check $basePatchPath 2>$null
$baseReverseCheckExitCode = $LASTEXITCODE
$ErrorActionPreference = $previousErrorActionPreference
if ($baseReverseCheckExitCode -ne 0) {
    git -C $destinationPath apply --check $basePatchPath
    if ($LASTEXITCODE -ne 0) {
        throw "The exclusive Joydex wake patch does not apply cleanly to pinned Voice PE."
    }

    git -C $destinationPath apply $basePatchPath
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
    throw "The prepared Voice PE base differs from the exact reviewed 0.1.6 patch."
}

$otherTrackedChanges = git -C $destinationPath diff --name-only -- . ":(exclude)home-assistant-voice.yaml"
if ($LASTEXITCODE -ne 0 -or $null -ne $otherTrackedChanges) {
    throw "The score diagnostic workspace contains unexpected tracked changes: $otherTrackedChanges"
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
    throw "The score component workspace is not at pinned ESPHome commit $espHomeCommit."
}

$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = "SilentlyContinue"
git -C $espHomePath apply --reverse --check $telemetryPatchPath 2>$null
$telemetryReverseCheckExitCode = $LASTEXITCODE
$ErrorActionPreference = $previousErrorActionPreference
if ($telemetryReverseCheckExitCode -ne 0) {
    git -C $espHomePath apply --check $telemetryPatchPath
    if ($LASTEXITCODE -ne 0) {
        throw "The score telemetry patch does not apply cleanly to pinned ESPHome."
    }

    git -C $espHomePath apply $telemetryPatchPath
    if ($LASTEXITCODE -ne 0) {
        throw "Could not apply the score telemetry patch."
    }
}

$microWakeWordHeaderPath = Join-Path $espHomePath "esphome\components\micro_wake_word\micro_wake_word.h"
$microWakeWordHeaderSha256 = (Get-FileHash -LiteralPath $microWakeWordHeaderPath -Algorithm SHA256).Hash
if ($microWakeWordHeaderSha256 -ne $expectedMicroWakeWordHeaderSha256) {
    throw "The telemetry header differs from the exact reviewed patch."
}

$microWakeWordSourcePath = Join-Path $espHomePath "esphome\components\micro_wake_word\micro_wake_word.cpp"
$microWakeWordSourceSha256 = (Get-FileHash -LiteralPath $microWakeWordSourcePath -Algorithm SHA256).Hash
if ($microWakeWordSourceSha256 -ne $expectedMicroWakeWordSourceSha256) {
    throw "The telemetry implementation differs from the exact reviewed patch."
}

$componentChanges = @(git -C $espHomePath diff --name-only)
if (($LASTEXITCODE -ne 0) -or
    ($componentChanges.Count -ne 2) -or
    ($componentChanges -notcontains "esphome/components/micro_wake_word/micro_wake_word.h") -or
    ($componentChanges -notcontains "esphome/components/micro_wake_word/micro_wake_word.cpp")) {
    throw "The score component workspace contains unexpected changes: $componentChanges"
}

Copy-Item -LiteralPath $overlayPath -Destination (Join-Path $destinationPath "joydex-voice-pe-score-canary.yaml") -Force
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

Write-Output "Prepared source-only Voice PE score diagnostic at $destinationPath"
Write-Output "Pinned Voice PE commit: $resolvedCommit"
Write-Output "Pinned ESPHome component commit: $resolvedEspHomeCommit"
Write-Output "Copied the explicitly selected ESPHome secrets file to $preparedSecretsPath"
Write-Output "No device connection or upload was attempted."
