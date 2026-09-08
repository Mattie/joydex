[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-sendspin-lease-fix-0.1.29-repro"),
    [string]$StageRoot = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-sendspin-lease-fix-0.1.29-staged"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$stageRootPath = [System.IO.Path]::GetFullPath($StageRoot)
$resolvedSecretsPath = [System.IO.Path]::GetFullPath($SecretsPath)
$preparationScript = Join-Path $PSScriptRoot "prepare-sendspin-lease-fix-canary.ps1"
$parityScript = Join-Path $PSScriptRoot "verify-network-credential-parity.py"
$espHomeExecutable = Join-Path $PSScriptRoot "..\.venv\Scripts\esphome.exe"
$pythonExecutable = Join-Path $PSScriptRoot "..\.venv\Scripts\python.exe"
$gitUnixTools = "C:\Program Files\Git\usr\bin"
$configurationName = "joydex-voice-pe-sendspin-lease-fix-canary.yaml"
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
        throw "A required Voice PE lease-fix build dependency is missing: $requiredPath"
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

function Invoke-LeaseFixCompile([string]$Phase) {
    Push-Location $destinationPath
    try {
        & $espHomeExecutable compile $configurationName
        if ($LASTEXITCODE -ne 0) {
            throw "The Voice PE lease-fix $Phase compile failed."
        }
    } finally {
        Pop-Location
    }
}

$generatedMainPath = Join-Path $destinationPath ".esphome\build\home-assistant-voice\src\main.cpp"
$generatedDefinesPath = Join-Path $destinationPath ".esphome\build\home-assistant-voice\src\esphome\core\defines.h"
function Assert-GeneratedIdentityAndCredentials([string]$Phase) {
    & $pythonExecutable $parityScript --secrets $resolvedSecretsPath --generated-main $generatedMainPath
    if ($LASTEXITCODE -ne 0) {
        throw "The $Phase Voice PE network credential parity check failed."
    }
    foreach ($evidence in @(
        '#define ESPHOME_PROJECT_NAME "Joydex.Voice PE Audio Bridge"',
        '#define ESPHOME_PROJECT_VERSION "0.1.29"',
        '#define ESPHOME_PROJECT_VERSION_30 "0.1.29"'
    )) {
        if ($null -eq (Select-String -LiteralPath $generatedDefinesPath -Pattern $evidence -SimpleMatch)) {
            throw "The $Phase generated firmware identity is missing: $evidence"
        }
    }
}

# The first compile exists only to resolve configuration and verify the selected
# credentials. OTA-eligible bytes come from the second compile below.
Invoke-LeaseFixCompile "preflight"
Assert-GeneratedIdentityAndCredentials "preflight"
Write-Output "Preflight generated identity and NETWORK CREDENTIAL AND RECOVERY PARITY: PASS"

Invoke-LeaseFixCompile "final"
Assert-GeneratedIdentityAndCredentials "final"
Write-Output "Final generated identity and NETWORK CREDENTIAL AND RECOVERY PARITY: PASS"

$sourceComponentPath = Join-Path $PSScriptRoot "components\joydex_lan_audio"
$preparedComponentPath = Join-Path $destinationPath "components\joydex_lan_audio"
$generatedComponentPath = Join-Path $destinationPath ".esphome\build\home-assistant-voice\src\esphome\components\joydex_lan_audio"
foreach ($fileName in @("joydex_lan_audio.cpp", "joydex_lan_audio.h")) {
    $sourcePath = Join-Path $sourceComponentPath $fileName
    $preparedPath = Join-Path $preparedComponentPath $fileName
    $generatedPath = Join-Path $generatedComponentPath $fileName
    $expectedHash = if ($fileName.EndsWith(".cpp")) {
        $expectedJoydexSourceSha256
    } else {
        (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
    }
    if ((Get-FileHash -LiteralPath $preparedPath -Algorithm SHA256).Hash -ne $expectedHash -or
        (Get-FileHash -LiteralPath $generatedPath -Algorithm SHA256).Hash -ne $expectedHash) {
        throw "Prepared/generated lease-fix Joydex component parity failed for $fileName."
    }
}

$preparedSendspinRoot = Join-Path $destinationPath ".joydex-sendspin\esphome\components\sendspin"
$generatedSendspinRoot = Join-Path $destinationPath ".esphome\build\home-assistant-voice\src\esphome\components\sendspin"
$sendspinFiles = @(
    @{ RelativePath = "sendspin_hub.cpp"; ExpectedHash = $expectedLeaseFixedHubSha256 },
    @{ RelativePath = "media_source\sendspin_media_source.cpp"; ExpectedHash = $expectedSendspinSourceSha256 },
    @{ RelativePath = "media_source\sendspin_media_source.h"; ExpectedHash = $expectedSendspinHeaderSha256 }
)
foreach ($entry in $sendspinFiles) {
    $preparedPath = Join-Path $preparedSendspinRoot $entry.RelativePath
    $generatedPath = Join-Path $generatedSendspinRoot $entry.RelativePath
    if ((Get-FileHash -LiteralPath $preparedPath -Algorithm SHA256).Hash -ne $entry.ExpectedHash -or
        (Get-FileHash -LiteralPath $generatedPath -Algorithm SHA256).Hash -ne $entry.ExpectedHash) {
        throw "Prepared/generated lease-fix Sendspin parity failed for $($entry.RelativePath)."
    }
}

$generatedSendspinSourcePath = Join-Path $generatedSendspinRoot "media_source\sendspin_media_source.cpp"
$generatedSendspinHubPath = Join-Path $generatedSendspinRoot "sendspin_hub.cpp"
$generatedJoydexSourcePath = Join-Path $generatedComponentPath "joydex_lan_audio.cpp"
foreach ($evidence in @(
    @{ Path = $generatedSendspinSourcePath; Pattern = "SYNC_TASK_PRIORITY = 18" },
    @{ Path = $generatedSendspinSourcePath; Pattern = "params, SYNC_TASK_PRIORITY" },
    @{ Path = $generatedSendspinHubPath; Pattern = "WEBSOCKET_TASK_PRIORITY = 18" },
    @{ Path = $generatedSendspinHubPath; Pattern = "high_performance_networking_requested_for_time_ = false" },
    @{ Path = $generatedJoydexSourcePath; Pattern = "config.task_priority = 18" }
)) {
    if ($null -eq (Select-String -LiteralPath $evidence.Path -Pattern $evidence.Pattern -SimpleMatch)) {
        throw "Generated lease-fix evidence is missing: $($evidence.Pattern)"
    }
}
$timeRequestTrueAssignments = @(
    Select-String -LiteralPath $generatedSendspinHubPath `
        -Pattern "high_performance_networking_requested_for_time_ = true" `
        -SimpleMatch
)
if ($timeRequestTrueAssignments.Count -ne 1) {
    throw "The generated Sendspin hub contains an unexpected number of clock-request acquisitions."
}
Write-Output "Generated Joydex, Sendspin priority, and Wi-Fi lease-fix parity: PASS"

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
    throw "The lease-fix OTA image identity, geometry, checksum, or validation hash is invalid."
}
$validationHash = [regex]::Match(
    $imageInfo,
    'Validation hash:\s+([0-9a-f]{64})\s+\(valid\)',
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
).Groups[1].Value.ToUpperInvariant()
$otaRecord = $artifactRecords | Where-Object { $_.fileName -eq "firmware.ota.bin" }
$otaRecord.validationHash = $validationHash
Write-Output "OTA image identity, flash geometry, checksum, and validation hash: PASS"

$stagedDirectory = Join-Path $stageRootPath $otaRecord.sha256
$stagedManifestPath = Join-Path $stagedDirectory "manifest.json"
$credentialSourceSha256 = (Get-FileHash -LiteralPath $resolvedSecretsPath -Algorithm SHA256).Hash
$manifest = [ordered]@{
    schemaVersion = 1
    firmwareVersion = "0.1.29"
    configuration = $configurationName
    createdUtc = [DateTimeOffset]::UtcNow.ToString("O")
    credentialSourceSha256 = $credentialSourceSha256
    change = "Clear the Sendspin clock-sync high-performance Wi-Fi request flag after release"
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
if ($stagedManifest.firmwareVersion -ne "0.1.29" -or
    $stagedManifest.credentialSourceSha256 -ne $credentialSourceSha256 -or
    $stagedManifest.sourceHashes.sendspinHub -ne $expectedLeaseFixedHubSha256) {
    throw "The immutable staged manifest differs from this final lease-fix build."
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
if ($stagedManifest.artifacts[0].validationHash -ne $validationHash) {
    throw "The immutable staged OTA validation hash differs from the final image."
}
foreach ($stagedFile in Get-ChildItem -LiteralPath $stagedDirectory -File) {
    $stagedFile.IsReadOnly = $true
}
Write-Output "Immutable post-gate manifest: $stagedManifestPath"
Write-Output "Verified Voice PE 0.1.29 Wi-Fi lease-fix candidate build complete. No device connection or upload was attempted."
