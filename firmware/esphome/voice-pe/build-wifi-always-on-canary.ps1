[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-wifi-always-on-0.1.30-repro"),
    [string]$StageRoot = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-wifi-always-on-0.1.30-staged"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$stageRootPath = [System.IO.Path]::GetFullPath($StageRoot)
$resolvedSecretsPath = [System.IO.Path]::GetFullPath($SecretsPath)
$preparationScript = Join-Path $PSScriptRoot "prepare-wifi-always-on-canary.ps1"
$parityScript = Join-Path $PSScriptRoot "verify-network-credential-parity.py"
$espHomeExecutable = Join-Path $PSScriptRoot "..\.venv\Scripts\esphome.exe"
$pythonExecutable = Join-Path $PSScriptRoot "..\.venv\Scripts\python.exe"
$gitUnixTools = "C:\Program Files\Git\usr\bin"
$configurationName = "joydex-voice-pe-wifi-always-on-canary.yaml"
$expectedEspHomeVersion = "2025.12.2"
$expectedSendspinSourceSha256 = "35348C9ED2D01ABF39865A6F32C99BD93F906E6CFC67D10432BDEACE758D790D"
$expectedSendspinHeaderSha256 = "5DB5612E718CECEF257B9E38446ABA29F78F9B234AE520A44E7BBEDAC10CFD4F"
$expectedLeaseFixedHubSha256 = "CBAE7507ECBA8BF31A177A446E8E95BF6F01B5B13F3EE52B4446F05C00B334DA"
$expectedJoydexSourceSha256 = "173FCF9E2F477461E39B7A4E665ABD610918A320C2865F23BE51CCA41788D692"

foreach ($requiredPath in @(
    $preparationScript,
    $parityScript,
    $espHomeExecutable,
    $pythonExecutable,
    $gitUnixTools,
    $resolvedSecretsPath
)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "A required Voice PE always-on Wi-Fi build dependency is missing: $requiredPath"
    }
}

$env:PATH = "$gitUnixTools;$env:PATH"
if ($null -eq (Get-Command patch.exe -ErrorAction SilentlyContinue)) {
    throw "patch.exe is unavailable after adding Git for Windows to PATH."
}
$espHomeVersionOutput = (& $espHomeExecutable version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $espHomeVersionOutput -ne "Version: $expectedEspHomeVersion") {
    throw "Expected ESPHome $expectedEspHomeVersion, found: $espHomeVersionOutput"
}

& $preparationScript -Destination $destinationPath -SecretsPath $resolvedSecretsPath

function Invoke-CanaryCompile([string]$Phase) {
    Push-Location $destinationPath
    try {
        & $espHomeExecutable compile $configurationName
        if ($LASTEXITCODE -ne 0) {
            throw "The Voice PE always-on Wi-Fi $Phase compile failed."
        }
    } finally {
        Pop-Location
    }
}

$generatedMainPath = Join-Path $destinationPath ".esphome\build\home-assistant-voice\src\main.cpp"
$generatedDefinesPath = Join-Path $destinationPath ".esphome\build\home-assistant-voice\src\esphome\core\defines.h"
function Assert-GeneratedConfiguration([string]$Phase) {
    & $pythonExecutable $parityScript --secrets $resolvedSecretsPath --generated-main $generatedMainPath
    if ($LASTEXITCODE -ne 0) {
        throw "The $Phase Voice PE network/recovery credential parity check failed."
    }
    foreach ($evidence in @(
        @{ Path = $generatedDefinesPath; Pattern = '#define ESPHOME_PROJECT_NAME "Joydex.Voice PE Audio Bridge"' },
        @{ Path = $generatedDefinesPath; Pattern = '#define ESPHOME_PROJECT_VERSION "0.1.30"' },
        @{ Path = $generatedDefinesPath; Pattern = '#define ESPHOME_PROJECT_VERSION_30 "0.1.30"' },
        @{ Path = $generatedMainPath; Pattern = "wifi_id->set_power_save_mode(wifi::WIFI_POWER_SAVE_NONE);" }
    )) {
        if ($null -eq (Select-String -LiteralPath $evidence.Path -Pattern $evidence.Pattern -SimpleMatch)) {
            throw "The $Phase generated always-on Wi-Fi evidence is missing: $($evidence.Pattern)"
        }
    }
}

Invoke-CanaryCompile "preflight"
Assert-GeneratedConfiguration "preflight"
Write-Output "Preflight identity, NETWORK CREDENTIAL AND RECOVERY PARITY, and always-on Wi-Fi: PASS"

Invoke-CanaryCompile "final"
Assert-GeneratedConfiguration "final"
Write-Output "Final identity, NETWORK CREDENTIAL AND RECOVERY PARITY, and always-on Wi-Fi: PASS"

$preparedSendspinRoot = Join-Path $destinationPath ".joydex-sendspin\esphome\components\sendspin"
$generatedSendspinRoot = Join-Path $destinationPath ".esphome\build\home-assistant-voice\src\esphome\components\sendspin"
$generatedJoydexSourcePath = Join-Path $destinationPath ".esphome\build\home-assistant-voice\src\esphome\components\joydex_lan_audio\joydex_lan_audio.cpp"
$sourceChecks = @(
    @{ Prepared = Join-Path $preparedSendspinRoot "sendspin_hub.cpp"; Generated = Join-Path $generatedSendspinRoot "sendspin_hub.cpp"; Hash = $expectedLeaseFixedHubSha256 },
    @{ Prepared = Join-Path $preparedSendspinRoot "media_source\sendspin_media_source.cpp"; Generated = Join-Path $generatedSendspinRoot "media_source\sendspin_media_source.cpp"; Hash = $expectedSendspinSourceSha256 },
    @{ Prepared = Join-Path $preparedSendspinRoot "media_source\sendspin_media_source.h"; Generated = Join-Path $generatedSendspinRoot "media_source\sendspin_media_source.h"; Hash = $expectedSendspinHeaderSha256 }
)
foreach ($check in $sourceChecks) {
    if ((Get-FileHash -LiteralPath $check.Prepared -Algorithm SHA256).Hash -ne $check.Hash -or
        (Get-FileHash -LiteralPath $check.Generated -Algorithm SHA256).Hash -ne $check.Hash) {
        throw "Prepared/generated always-on Wi-Fi Sendspin source parity failed."
    }
}
if ((Get-FileHash -LiteralPath $generatedJoydexSourcePath -Algorithm SHA256).Hash -ne $expectedJoydexSourceSha256) {
    throw "Generated always-on Wi-Fi Joydex source parity failed."
}
foreach ($evidence in @(
    @{ Path = Join-Path $generatedSendspinRoot "media_source\sendspin_media_source.cpp"; Pattern = "SYNC_TASK_PRIORITY = 18" },
    @{ Path = Join-Path $generatedSendspinRoot "sendspin_hub.cpp"; Pattern = "WEBSOCKET_TASK_PRIORITY = 18" },
    @{ Path = Join-Path $generatedSendspinRoot "sendspin_hub.cpp"; Pattern = "high_performance_networking_requested_for_time_ = false" },
    @{ Path = $generatedJoydexSourcePath; Pattern = "config.task_priority = 18" }
)) {
    if ($null -eq (Select-String -LiteralPath $evidence.Path -Pattern $evidence.Pattern -SimpleMatch)) {
        throw "Generated always-on Wi-Fi source evidence is missing: $($evidence.Pattern)"
    }
}
Write-Output "Generated Joydex, Sendspin, priority, lease-fix, and always-on Wi-Fi parity: PASS"

$otaImagePath = Join-Path $destinationPath ".esphome\build\home-assistant-voice\.pioenvs\home-assistant-voice\firmware.ota.bin"
$factoryImagePath = Join-Path $destinationPath ".esphome\build\home-assistant-voice\.pioenvs\home-assistant-voice\firmware.factory.bin"
$artifactRecords = @()
foreach ($imagePath in @($otaImagePath, $factoryImagePath)) {
    if (-not (Test-Path -LiteralPath $imagePath -PathType Leaf)) {
        throw "The expected Voice PE firmware artifact is missing: $imagePath"
    }
    $image = Get-Item -LiteralPath $imagePath
    $imageHash = (Get-FileHash -LiteralPath $imagePath -Algorithm SHA256).Hash
    $artifactRecords += [ordered]@{
        fileName = $image.Name
        bytes = $image.Length
        sha256 = $imageHash
        validationHash = $null
    }
    Write-Output "$($image.Name): $($image.Length) bytes; SHA-256 $imageHash"
}

$imageInfo = (& $pythonExecutable -m esptool image-info $otaImagePath 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0 -or
    $imageInfo -notmatch 'Detected image type:\s+ESP32-S3' -or
    $imageInfo -notmatch 'Flash size:\s+16MB' -or
    $imageInfo -notmatch 'Flash mode:\s+DIO' -or
    $imageInfo -notmatch 'Checksum:\s+0x[0-9a-f]+\s+\(valid\)' -or
    $imageInfo -notmatch 'Validation hash:\s+[0-9a-f]{64}\s+\(valid\)') {
    throw "The always-on Wi-Fi OTA image identity, geometry, checksum, or validation hash is invalid."
}
$validationHash = [regex]::Match(
    $imageInfo,
    'Validation hash:\s+([0-9a-f]{64})\s+\(valid\)',
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
).Groups[1].Value.ToUpperInvariant()
$otaRecord = $artifactRecords | Where-Object { $_.fileName -eq "firmware.ota.bin" }
$otaRecord.validationHash = $validationHash

$stagedDirectory = Join-Path $stageRootPath $otaRecord.sha256
$stagedManifestPath = Join-Path $stagedDirectory "manifest.json"
$credentialSourceSha256 = (Get-FileHash -LiteralPath $resolvedSecretsPath -Algorithm SHA256).Hash
$manifest = [ordered]@{
    schemaVersion = 1
    firmwareVersion = "0.1.30"
    configuration = $configurationName
    createdUtc = [DateTimeOffset]::UtcNow.ToString("O")
    credentialSourceSha256 = $credentialSourceSha256
    change = "Disable configured Wi-Fi power saving to bypass runtime audio lease transitions"
    wifiPowerSaveMode = "none"
    priorities = [ordered]@{
        sendspinPlayer = 18
        sendspinHttpd = 18
        joydexHttpd = 18
        joydexUplinkFeeder = 1
    }
    sourceHashes = [ordered]@{
        sendspinMediaSource = $expectedSendspinSourceSha256
        sendspinMediaHeader = $expectedSendspinHeaderSha256
        sendspinHub = $expectedLeaseFixedHubSha256
        joydexLanAudio = $expectedJoydexSourceSha256
    }
    artifacts = $artifactRecords
}

if (Test-Path -LiteralPath $stagedDirectory) {
    if (-not (Test-Path -LiteralPath $stagedManifestPath -PathType Leaf)) {
        throw "The immutable stage directory exists without its manifest: $stagedDirectory"
    }
} else {
    New-Item -ItemType Directory -Path $stagedDirectory -Force | Out-Null
    Copy-Item -LiteralPath $otaImagePath -Destination (Join-Path $stagedDirectory "firmware.ota.bin")
    Copy-Item -LiteralPath $factoryImagePath -Destination (Join-Path $stagedDirectory "firmware.factory.bin")
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $stagedManifestPath -Encoding utf8NoBOM
}

$stagedManifest = Get-Content -LiteralPath $stagedManifestPath -Raw | ConvertFrom-Json
if ($stagedManifest.firmwareVersion -ne "0.1.30" -or
    $stagedManifest.credentialSourceSha256 -ne $credentialSourceSha256 -or
    $stagedManifest.wifiPowerSaveMode -ne "none" -or
    $stagedManifest.sourceHashes.sendspinHub -ne $expectedLeaseFixedHubSha256) {
    throw "The immutable staged manifest differs from this final always-on Wi-Fi build."
}
foreach ($record in $artifactRecords) {
    $stagedArtifactPath = Join-Path $stagedDirectory $record.fileName
    $manifestRecord = $stagedManifest.artifacts | Where-Object { $_.fileName -eq $record.fileName }
    if ($null -eq $manifestRecord -or
        -not (Test-Path -LiteralPath $stagedArtifactPath -PathType Leaf) -or
        (Get-Item -LiteralPath $stagedArtifactPath).Length -ne $record.bytes -or
        (Get-FileHash -LiteralPath $stagedArtifactPath -Algorithm SHA256).Hash -ne $record.sha256 -or
        $manifestRecord.bytes -ne $record.bytes -or
        $manifestRecord.sha256 -ne $record.sha256) {
        throw "Immutable staged artifact verification failed for $($record.fileName)."
    }
}
if (($stagedManifest.artifacts | Where-Object fileName -eq "firmware.ota.bin").validationHash -ne $validationHash) {
    throw "The immutable staged OTA validation hash differs from the final image."
}
foreach ($stagedFile in Get-ChildItem -LiteralPath $stagedDirectory -File) {
    $stagedFile.IsReadOnly = $true
}
Write-Output "Immutable post-gate manifest: $stagedManifestPath"
Write-Output "Verified Voice PE 0.1.30 always-on Wi-Fi candidate build complete. No device connection or upload was attempted."
