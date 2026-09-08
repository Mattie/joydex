[CmdletBinding()]
# Historical attended-release verifier. It intentionally requires a private
# staged baseline manifest. New source builders should use
# Build-JoydexVoicePe.ps1 instead.
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-session-controls-0.1.41-repro"),
    [string]$StageRoot = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-session-controls-0.1.41-staged"),
    [string]$BaselineManifestPath = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-session-controls-0.1.37-staged\B431930EA9F4DCF2B8545A4B7F27BA77290B1587A2C121150BC5373AE64006A0\manifest.json"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$expectedEspHomeVersion = "2025.12.2"
$expectedFirmwareVersion = "0.1.41"
$configurationName = "joydex-voice-pe-session-controls-canary.yaml"
$sourceDateEpoch = "1787961600"
$maximumTotalImageBytes = 3300000
$maximumStaticRamBytes = 50000
$maximumDiramBytes = 150000
$expectedSourceHashes = [ordered]@{
    "prepare-score-sound-canary.ps1" = "375DCEB2787B07557A8AE376D5647CD5D3CF76EC1FA18C7C59AFEA8C704BA471"
    "prepare-audio-canary.ps1" = "E03E34F7DEB05D7832EFA9C45AEA6D436623EADA2AC4A5CCA7098A66F5A857BE"
    "joydex-voice-pe-audio-canary.yaml" = "CF85BB974DDDAF0878305E4A60CB5D7CF8B56A2EEC016517929AFC21BC0B7CAD"
    "sounds\ReadyBlip.flac" = "2E56276BCDB96F538427E9840D7FB01F4D435F69B79B857E682D5CCB219F3D58"
    "sounds\WakeBlip.flac" = "8AAF3B4EA12C77571B0F750EE3891BF4517F3F212B908A93563E6FC57A26F6AD"
    "sounds\EndBlip.flac" = "FC0365A43D7D0666FD3BFBEAC490825A22C77964AABC7407F2D39D0FCDA483D5"
    "prepare-tunable-wake-canary.ps1" = "FCBD8A89FFC0902FE9C90EFB5C7EA15E57618E36B19D58D2A4CB82F5D9280252"
    "joydex-voice-pe-tunable-wake-canary.yaml" = "C5986C8DCA8D26783769A352977809D926A7DE960012B7BCBC08FC4E7E20FE34"
    "prepare-session-controls-canary.ps1" = "57A00D546CBFA75F14C47462D4F35A3C036F2BD1963A10EEC2E2B2A88BE074C2"
    "joydex-voice-pe-session-controls-canary.yaml" = "CD7650073301E89E3378322F00A6497BFF2B7815D042E24473D1986549C83159"
    "voice-session-button-controls.patch" = "92A661A3AC7B6F016EBE82C4127C9BD2E852877151726F528EA884C5796ECB3F"
    "voice-session-state.patch" = "27DD2131356E7439B85E50D5F00B5AA5AEB651D507292848A4695DD50B740A84"
    "voice-session-ready-cue.patch" = "7ACCEA4B402529F5FA472280884AD02ED0DE23710134178688AC5BED5C7E6A83"
    "voice-session-end-cue.patch" = "DEDCA620F2FA64DA7BE7718E0542EBBFBC31E01E9D364EB362AAF1B06C3CA0BE"
}

$destinationPath = [IO.Path]::GetFullPath($Destination)
$stageRootPath = [IO.Path]::GetFullPath($StageRoot)
$resolvedSecretsPath = [IO.Path]::GetFullPath($SecretsPath)
$resolvedBaselineManifestPath = [IO.Path]::GetFullPath($BaselineManifestPath)
$preparationScript = Join-Path $PSScriptRoot "prepare-session-controls-canary.ps1"
$parityScript = Join-Path $PSScriptRoot "verify-network-credential-parity.py"
$espHomeExecutable = Join-Path $PSScriptRoot "..\.venv\Scripts\esphome.exe"
$pythonExecutable = Join-Path $PSScriptRoot "..\.venv\Scripts\python.exe"
$platformIoPython = Join-Path $env:USERPROFILE ".platformio\penv\Scripts\python.exe"
$gitUnixTools = "C:\Program Files\Git\usr\bin"

foreach ($requiredPath in @(
    $resolvedSecretsPath,
    $resolvedBaselineManifestPath,
    $preparationScript,
    $parityScript,
    $espHomeExecutable,
    $pythonExecutable,
    $platformIoPython,
    (Join-Path $gitUnixTools "patch.exe")
)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "A required session-controls build input is missing: $requiredPath"
    }
}

foreach ($entry in $expectedSourceHashes.GetEnumerator()) {
    $sourcePath = Join-Path $PSScriptRoot $entry.Key
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -cne $entry.Value) {
        throw "The reviewed source hash differs for $($entry.Key)."
    }
}

$baselineManifest = Get-Content -LiteralPath $resolvedBaselineManifestPath -Raw | ConvertFrom-Json
$credentialSourceSha256 = (Get-FileHash -LiteralPath $resolvedSecretsPath -Algorithm SHA256).Hash
if ($baselineManifest.schemaVersion -ne 1 -or
    $baselineManifest.firmwareVersion -cne "0.1.37" -or
    $baselineManifest.credentialSourceSha256 -cne $credentialSourceSha256 -or
    $baselineManifest.baseline.taskStackInPsram -ne $false) {
    throw "The selected 0.1.37 baseline or credential source differs from the deployed cue-lifecycle baseline."
}

$versionOutput = (& $espHomeExecutable version 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0 -or $versionOutput -notmatch [regex]::Escape($expectedEspHomeVersion)) {
    throw "ESPHome $expectedEspHomeVersion is required for this reviewed build."
}

$env:PATH = "$gitUnixTools;$env:PATH"
if ($null -eq (Get-Command patch.exe -ErrorAction SilentlyContinue)) {
    throw "patch.exe is unavailable after adding Git for Windows to PATH."
}
$env:SOURCE_DATE_EPOCH = $sourceDateEpoch

& $preparationScript -Destination $destinationPath -SecretsPath $resolvedSecretsPath
if ($LASTEXITCODE -ne 0) {
    throw "Voice PE 0.1.41 source preparation failed."
}

$configurationPath = Join-Path $destinationPath $configurationName
& $espHomeExecutable config $configurationPath
if ($LASTEXITCODE -ne 0) {
    throw "Voice PE 0.1.41 configuration validation failed."
}

$pioEnvironment = Join-Path $destinationPath ".esphome\build\home-assistant-voice\.pioenvs\home-assistant-voice"
$otaImagePath = Join-Path $pioEnvironment "firmware.ota.bin"
$factoryImagePath = Join-Path $pioEnvironment "firmware.factory.bin"
$mapPath = Join-Path $pioEnvironment "firmware.map"
$generatedMainPath = Join-Path $destinationPath ".esphome\build\home-assistant-voice\src\main.cpp"

$buildHashes = @()
for ($buildIndex = 1; $buildIndex -le 2; $buildIndex++) {
    & $espHomeExecutable compile $configurationPath
    if ($LASTEXITCODE -ne 0) {
        throw "Voice PE 0.1.41 build $buildIndex failed."
    }
    if (-not (Test-Path -LiteralPath $otaImagePath -PathType Leaf)) {
        throw "Voice PE 0.1.41 build $buildIndex did not produce firmware.ota.bin."
    }
    $buildHashes += (Get-FileHash -LiteralPath $otaImagePath -Algorithm SHA256).Hash
}
if ($buildHashes[0] -cne $buildHashes[1]) {
    throw "The two Voice PE 0.1.41 builds were not reproducible."
}

$generatedMain = Get-Content -LiteralPath $generatedMainPath -Raw
foreach ($evidence in @(
    'Joydex.Voice PE Audio Bridge',
    '0.1.41',
    'joydex_voice_hangup',
    'joydex_voice_toggle_mute',
    'joydex_voice_ready_cue_pending',
    'joydex_voice_end_cue_pending',
    'joydex_voice_session_was_active',
    'joydex_end_sound',
    'Joydex Voice Muted',
    'state == "Listening" || state == "Muted"'
)) {
    if (-not $generatedMain.Contains($evidence, [StringComparison]::Ordinal)) {
        throw "Generated firmware is missing reviewed session-control evidence: $evidence"
    }
}
foreach ($assignment in @(
    'joydex_voice_ready_cue_pending->value() = true;',
    'joydex_voice_ready_cue_pending->value() = false;',
    'joydex_voice_end_cue_pending->value() = true;',
    'joydex_voice_end_cue_pending->value() = false;',
    'joydex_voice_session_was_active->value() = true;',
    'joydex_voice_session_was_active->value() = false;'
)) {
    if ([regex]::Matches($generatedMain, [regex]::Escape($assignment)).Count -ne 1) {
        throw "Generated firmware must contain exactly one reviewed ready-cue assignment: $assignment"
    }
}

& $pythonExecutable $parityScript --secrets $resolvedSecretsPath --generated-main $generatedMainPath
if ($LASTEXITCODE -ne 0) {
    throw "Generated network credential parity failed."
}

$sizeJson = (& $platformIoPython -m esp_idf_size --format json2 $mapPath 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "ESP-IDF size analysis failed."
}
$sizeReport = $sizeJson | ConvertFrom-Json
$diram = $sizeReport.layout | Where-Object { $_.name -eq "DIRAM" }
$staticRamBytes = [int64]$diram.parts.'.data'.size + [int64]$diram.parts.'.bss'.size
$totalImageBytes = [int64]$sizeReport.total_size
$diramBytes = [int64]$diram.used
if ($totalImageBytes -gt $maximumTotalImageBytes -or
    $staticRamBytes -gt $maximumStaticRamBytes -or
    $diramBytes -gt $maximumDiramBytes) {
    throw "The candidate exceeds a reviewed ESP32 resource ceiling: image=$totalImageBytes, static RAM=$staticRamBytes, DIRAM=$diramBytes."
}

$imageInfo = (& $pythonExecutable -m esptool image-info $otaImagePath 2>&1 | Out-String)
$validationMatch = [regex]::Match(
    $imageInfo,
    'Validation hash:\s+([0-9a-f]{64})\s+\(valid\)',
    [Text.RegularExpressions.RegexOptions]::IgnoreCase)
if ($LASTEXITCODE -ne 0 -or
    $imageInfo -notmatch 'Detected image type:\s+ESP32-S3' -or
    $imageInfo -notmatch 'Flash size:\s+16MB' -or
    $imageInfo -notmatch 'Flash mode:\s+DIO' -or
    $imageInfo -notmatch 'Checksum:\s+0x[0-9a-f]+\s+\(valid\)' -or
    -not $validationMatch.Success) {
    throw "The OTA image identity, geometry, checksum, or validation hash is invalid."
}

$artifactRecords = @()
foreach ($imagePath in @($otaImagePath, $factoryImagePath)) {
    $image = Get-Item -LiteralPath $imagePath
    $artifactRecords += [ordered]@{
        fileName = $image.Name
        bytes = $image.Length
        sha256 = (Get-FileHash -LiteralPath $imagePath -Algorithm SHA256).Hash
        validationHash = if ($image.Name -ceq "firmware.ota.bin") {
            $validationMatch.Groups[1].Value.ToUpperInvariant()
        } else {
            $null
        }
    }
}

$otaRecord = $artifactRecords | Where-Object fileName -eq "firmware.ota.bin"
$stagedDirectory = [IO.Path]::GetFullPath((Join-Path $stageRootPath $otaRecord.sha256))
$stagePrefix = $stageRootPath.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $stagedDirectory.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to stage outside the selected stage root: $stagedDirectory"
}
$stagedManifestPath = Join-Path $stagedDirectory "manifest.json"
$manifest = [ordered]@{
    schemaVersion = 1
    firmwareVersion = $expectedFirmwareVersion
    configuration = $configurationName
    createdUtc = [DateTimeOffset]::UtcNow.ToString("O")
    credentialSourceSha256 = $credentialSourceSha256
    change = "Refresh the wake, ready, and hangup cue recordings"
    deviceConnectionAttempted = $false
    reproducibleBuild = $true
    sourceDateEpoch = $sourceDateEpoch
    baseline = [ordered]@{
        firmwareVersion = $baselineManifest.firmwareVersion
        otaSha256 = ($baselineManifest.artifacts | Where-Object fileName -eq "firmware.ota.bin").sha256
        taskStackInPsram = $baselineManifest.baseline.taskStackInPsram
    }
    sourceHashes = $expectedSourceHashes
    resources = [ordered]@{
        totalImageBytes = $totalImageBytes
        staticRamBytes = $staticRamBytes
        diramBytes = $diramBytes
        baselineTotalImageBytes = $baselineManifest.resources.totalImageBytes
        baselineStaticRamBytes = $baselineManifest.resources.staticRamBytes
    }
    artifacts = $artifactRecords
}

if (Test-Path -LiteralPath $stagedDirectory) {
    if (-not (Test-Path -LiteralPath $stagedManifestPath -PathType Leaf)) {
        throw "The immutable stage directory exists without its manifest: $stagedDirectory"
    }
} else {
    New-Item -ItemType Directory -Path $stagedDirectory | Out-Null
    Copy-Item -LiteralPath $otaImagePath -Destination (Join-Path $stagedDirectory "firmware.ota.bin")
    Copy-Item -LiteralPath $factoryImagePath -Destination (Join-Path $stagedDirectory "firmware.factory.bin")
    $manifest | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $stagedManifestPath -Encoding utf8NoBOM
}

$stagedManifest = Get-Content -LiteralPath $stagedManifestPath -Raw | ConvertFrom-Json
if ($stagedManifest.firmwareVersion -cne $expectedFirmwareVersion -or
    $stagedManifest.credentialSourceSha256 -cne $credentialSourceSha256 -or
    $stagedManifest.reproducibleBuild -ne $true -or
    $stagedManifest.baseline.otaSha256 -cne $manifest.baseline.otaSha256) {
    throw "The immutable staged manifest differs from this final build."
}
foreach ($record in $artifactRecords) {
    $stagedArtifactPath = Join-Path $stagedDirectory $record.fileName
    $manifestRecord = $stagedManifest.artifacts | Where-Object fileName -eq $record.fileName
    if ($null -eq $manifestRecord -or
        (Get-Item -LiteralPath $stagedArtifactPath).Length -ne $record.bytes -or
        (Get-FileHash -LiteralPath $stagedArtifactPath -Algorithm SHA256).Hash -cne $record.sha256 -or
        $manifestRecord.sha256 -cne $record.sha256) {
        throw "Immutable staged artifact verification failed for $($record.fileName)."
    }
}
foreach ($stagedFile in Get-ChildItem -LiteralPath $stagedDirectory -File) {
    $stagedFile.IsReadOnly = $true
}

Write-Output "Resource gate: image $totalImageBytes bytes; static RAM $staticRamBytes bytes; DIRAM $diramBytes bytes."
foreach ($record in $artifactRecords) {
    Write-Output "$($record.fileName): $($record.bytes) bytes; SHA-256 $($record.sha256)"
}
Write-Output "Immutable no-OTA manifest: $stagedManifestPath"
Write-Output "Verified Voice PE $expectedFirmwareVersion session-controls build complete. No device connection or upload was attempted."
