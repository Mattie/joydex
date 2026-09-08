[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-source"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
$officialRepository = "https://github.com/esphome/home-assistant-voice-pe.git"
$officialCommit = "a163e7b980c572df9d3811ff98094350f5aae541"
$expectedPatchedSha256 = "09579644C0988822222A07ED72BC92ACC15993220D4ECE244B220F952763AFB9"
$espHomeCommit = "99f7e9aeb74447fa489fefb80b8129623e916816"
$expectedMicroWakeWordHeaderSha256 = "27B7B0F83D23CA2BC98AA82E6E128AE4B4B88E6E98FBBD8957E0D9C81A5C86E6"
$expectedMicroWakeWordSourceSha256 = "CE591854772F6597F81AE3C05C5589D72EDE8DBC835BC3B96B82AE928484BB61"
$expectedMicroWakeWordStockHashes = [ordered]@{
    "__init__.py" = "11DFE73389813AE43AF33470FE0B8D6C6BC06430FA6080ABBC1F4300A32BA122"
    "automation.h" = "BC3BA4D17B69242A19FB708B78EB0536775D56992AA115AD025DA2FF803C5D09"
    "micro_wake_word.cpp" = "9947C7C51C88153AD8075E9152ABA2E65A1B0F197A4CCE565195D308A36B3512"
    "micro_wake_word.h" = "394813FCA401E0BAFD256B922809F53E0B658614797FDF0B2A823314BFA7CF22"
    "preprocessor_settings.h" = "ABE6CD182D148D49B1E1656F8C8F93DD184F24788CA13EB9352941B366115FCB"
    "streaming_model.cpp" = "C7A192240E75B32483A12BD37C11021DCDF5E95969D6EB8AC98F51656054D013"
    "streaming_model.h" = "D804FBE9E00A206AAD10588DB6C3004534D5397C638E3467C6060164EBA674CD"
}
$expectedOverlaySha256 = "1FEC2109C0F76A1C99C5CFA48309FA199148458EEA3852B8BFCB6F23EA180420"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$basePatchPath = Join-Path $PSScriptRoot "exclusive-joydex-wake.patch"
$telemetryPatchPath = Join-Path $PSScriptRoot "micro-wake-score-telemetry.patch"
$overlayPath = Join-Path $PSScriptRoot "joydex-voice-pe-base.yaml"
$secretsCopyScript = Join-Path $PSScriptRoot "Copy-VerifiedVoicePeSecrets.ps1"
if (-not (Test-Path -LiteralPath $secretsCopyScript -PathType Leaf)) {
    throw "The Voice PE secrets safety helper is missing: $secretsCopyScript"
}

$overlaySha256 = (Get-FileHash -LiteralPath $overlayPath -Algorithm SHA256).Hash
if ($overlaySha256 -ne $expectedOverlaySha256) {
    throw "The Voice PE base overlay differs from the reviewed source."
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
    throw "The Voice PE source workspace is not at pinned commit $officialCommit."
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
    throw "The prepared Voice PE base differs from the reviewed wake-routing patch."
}

$otherTrackedChanges = git -C $destinationPath diff --name-only -- . ":(exclude)home-assistant-voice.yaml"
if ($LASTEXITCODE -ne 0 -or $null -ne $otherTrackedChanges) {
    throw "The Voice PE source workspace contains unexpected tracked changes: $otherTrackedChanges"
}

$espHomePath = [System.IO.Path]::GetFullPath((Join-Path $destinationPath ".joydex-esphome"))
$destinationPrefix = $destinationPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
if (-not $espHomePath.StartsWith($destinationPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace ESPHome sources outside the selected destination: $espHomePath"
}
if (Test-Path -LiteralPath $espHomePath) {
    Remove-Item -LiteralPath $espHomePath -Recurse -Force
}

$microWakeWordPath = Join-Path $espHomePath "esphome\components\micro_wake_word"
New-Item -ItemType Directory -Path $microWakeWordPath -Force | Out-Null
foreach ($entry in $expectedMicroWakeWordStockHashes.GetEnumerator()) {
    $targetPath = Join-Path $microWakeWordPath $entry.Key
    $sourceUri = "https://raw.githubusercontent.com/esphome/esphome/$espHomeCommit/esphome/components/micro_wake_word/$($entry.Key)"
    Invoke-WebRequest -Uri $sourceUri -OutFile $targetPath
    if ((Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash -ne $entry.Value) {
        throw "Pinned ESPHome source hash mismatch before patching: $($entry.Key)"
    }
}

git apply --no-index --unsafe-paths --whitespace=nowarn "--directory=$espHomePath" $telemetryPatchPath
if ($LASTEXITCODE -ne 0) {
    throw "The score telemetry patch does not apply cleanly to pinned ESPHome."
}

$microWakeWordHeaderPath = Join-Path $microWakeWordPath "micro_wake_word.h"
$microWakeWordHeaderSha256 = (Get-FileHash -LiteralPath $microWakeWordHeaderPath -Algorithm SHA256).Hash
if ($microWakeWordHeaderSha256 -ne $expectedMicroWakeWordHeaderSha256) {
    throw "The telemetry header differs from the exact reviewed patch."
}

$microWakeWordSourcePath = Join-Path $microWakeWordPath "micro_wake_word.cpp"
$microWakeWordSourceSha256 = (Get-FileHash -LiteralPath $microWakeWordSourcePath -Algorithm SHA256).Hash
if ($microWakeWordSourceSha256 -ne $expectedMicroWakeWordSourceSha256) {
    throw "The telemetry implementation differs from the exact reviewed patch."
}

$preparedWakeFiles = @(Get-ChildItem -LiteralPath $microWakeWordPath -File)
if ($preparedWakeFiles.Count -ne $expectedMicroWakeWordStockHashes.Count) {
    throw "The prepared microWakeWord component file set differs from the pinned source."
}
foreach ($entry in $expectedMicroWakeWordStockHashes.GetEnumerator()) {
    if ($entry.Key -in @("micro_wake_word.h", "micro_wake_word.cpp")) {
        continue
    }
    $preparedPath = Join-Path $microWakeWordPath $entry.Key
    if ((Get-FileHash -LiteralPath $preparedPath -Algorithm SHA256).Hash -ne $entry.Value) {
        throw "Prepared ESPHome source parity failed: $($entry.Key)"
    }
}

Copy-Item -LiteralPath $overlayPath -Destination (Join-Path $destinationPath "joydex-voice-pe-base.yaml") -Force
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

Write-Output "Prepared the pinned Voice PE base at $destinationPath"
Write-Output "Pinned Voice PE commit: $resolvedCommit"
Write-Output "Pinned ESPHome component commit: $espHomeCommit"
Write-Output "Copied the explicitly selected ESPHome secrets file to $preparedSecretsPath"
Write-Output "No device connection or upload was attempted."
