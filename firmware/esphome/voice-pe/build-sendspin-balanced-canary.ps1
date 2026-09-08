[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-sendspin-balanced-0.1.28-repro"),
    [string]$StageRoot = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-sendspin-balanced-0.1.28-staged"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$stageRootPath = [System.IO.Path]::GetFullPath($StageRoot)
$resolvedSecretsPath = [System.IO.Path]::GetFullPath($SecretsPath)
$preparationScript = Join-Path $PSScriptRoot "prepare-sendspin-balanced-canary.ps1"
$parityScript = Join-Path $PSScriptRoot "verify-network-credential-parity.py"
$espHomeExecutable = Join-Path $PSScriptRoot "..\.venv\Scripts\esphome.exe"
$pythonExecutable = Join-Path $PSScriptRoot "..\.venv\Scripts\python.exe"
$gitUnixTools = "C:\Program Files\Git\usr\bin"
$configurationName = "joydex-voice-pe-sendspin-balanced-canary.yaml"
$expectedEspHomeVersion = "2025.12.2"
$expectedPatchedSendspinSourceSha256 = "35348C9ED2D01ABF39865A6F32C99BD93F906E6CFC67D10432BDEACE758D790D"
$expectedPatchedSendspinHeaderSha256 = "5DB5612E718CECEF257B9E38446ABA29F78F9B234AE520A44E7BBEDAC10CFD4F"
$expectedPatchedSendspinHubSha256 = "D06FFE8A609588F3CA880016F3613EC499B54074C55AAFAC8EB64B2F9BA67F43"
$expectedPatchedJoydexSourceSha256 = "173FCF9E2F477461E39B7A4E665ABD610918A320C2865F23BE51CCA41788D692"

foreach ($requiredPath in @(
    $preparationScript,
    $parityScript,
    $espHomeExecutable,
    $pythonExecutable,
    $gitUnixTools
)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "A required Voice PE build dependency is missing: $requiredPath"
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

function Invoke-BalancedCompile([string]$Phase) {
    Push-Location $destinationPath
    try {
        & $espHomeExecutable compile $configurationName
        if ($LASTEXITCODE -ne 0) {
            throw "The Voice PE balanced-priority $Phase compile failed."
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
    $requiredIdentityEvidence = @(
        '#define ESPHOME_PROJECT_NAME "Joydex.Voice PE Audio Bridge"',
        '#define ESPHOME_PROJECT_VERSION "0.1.28"',
        '#define ESPHOME_PROJECT_VERSION_30 "0.1.28"'
    )
    foreach ($evidence in $requiredIdentityEvidence) {
        if ($null -eq (Select-String -LiteralPath $generatedDefinesPath -Pattern $evidence -SimpleMatch)) {
            throw "The $Phase generated firmware identity is missing: $evidence"
        }
    }
}

# Resolve and compile once so credential parity can be checked against generated
# station configuration. The artifact from this pass is never staged.
Invoke-BalancedCompile "preflight"
Assert-GeneratedIdentityAndCredentials "preflight"
Write-Output "Preflight generated identity and network credential parity: PASS"

# OTA safety requires a compile after resolved credential parity. Only artifacts
# from this final pass are eligible for hashing and immutable staging.
Invoke-BalancedCompile "final"
Assert-GeneratedIdentityAndCredentials "final"
Write-Output "Final generated identity and network credential parity: PASS"

$sourceComponentPath = Join-Path $PSScriptRoot "components\joydex_lan_audio"
$preparedComponentPath = Join-Path $destinationPath "components\joydex_lan_audio"
$generatedComponentPath = Join-Path $destinationPath ".esphome\build\home-assistant-voice\src\esphome\components\joydex_lan_audio"
foreach ($fileName in @("joydex_lan_audio.cpp", "joydex_lan_audio.h")) {
    $sourcePath = Join-Path $sourceComponentPath $fileName
    $preparedPath = Join-Path $preparedComponentPath $fileName
    $generatedPath = Join-Path $generatedComponentPath $fileName
    $preparedHash = (Get-FileHash -LiteralPath $preparedPath -Algorithm SHA256).Hash
    $generatedHash = (Get-FileHash -LiteralPath $generatedPath -Algorithm SHA256).Hash
    $expectedHash = if ($fileName.EndsWith(".cpp")) {
        $expectedPatchedJoydexSourceSha256
    } else {
        (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
    }
    if ($preparedHash -ne $expectedHash -or $generatedHash -ne $expectedHash) {
        throw "Prepared/generated balanced Joydex component parity failed for $fileName."
    }
}

$preparedSendspinRoot = Join-Path $destinationPath ".joydex-sendspin\esphome\components\sendspin"
$generatedSendspinRoot = Join-Path $destinationPath ".esphome\build\home-assistant-voice\src\esphome\components\sendspin"
$sendspinFiles = @(
    @{ RelativePath = "sendspin_hub.cpp"; ExpectedHash = $expectedPatchedSendspinHubSha256 },
    @{ RelativePath = "media_source\sendspin_media_source.cpp"; ExpectedHash = $expectedPatchedSendspinSourceSha256 },
    @{ RelativePath = "media_source\sendspin_media_source.h"; ExpectedHash = $expectedPatchedSendspinHeaderSha256 }
)
foreach ($entry in $sendspinFiles) {
    $preparedPath = Join-Path $preparedSendspinRoot $entry.RelativePath
    $generatedPath = Join-Path $generatedSendspinRoot $entry.RelativePath
    if ((Get-FileHash -LiteralPath $preparedPath -Algorithm SHA256).Hash -ne $entry.ExpectedHash -or
        (Get-FileHash -LiteralPath $generatedPath -Algorithm SHA256).Hash -ne $entry.ExpectedHash) {
        throw "Prepared/generated balanced Sendspin parity failed for $($entry.RelativePath)."
    }
}

$generatedSendspinSourcePath = Join-Path $generatedSendspinRoot "media_source\sendspin_media_source.cpp"
$generatedSendspinHubPath = Join-Path $generatedSendspinRoot "sendspin_hub.cpp"
$generatedJoydexSourcePath = Join-Path $generatedComponentPath "joydex_lan_audio.cpp"
$requiredPriorityEvidence = @(
    @{ Path = $generatedSendspinSourcePath; Pattern = "SYNC_TASK_PRIORITY = 18" },
    @{ Path = $generatedSendspinSourcePath; Pattern = "params, SYNC_TASK_PRIORITY" },
    @{ Path = $generatedSendspinHubPath; Pattern = "WEBSOCKET_TASK_PRIORITY = 18" },
    @{ Path = $generatedJoydexSourcePath; Pattern = "config.task_priority = 18" }
)
foreach ($evidence in $requiredPriorityEvidence) {
    if ($null -eq (Select-String -LiteralPath $evidence.Path -Pattern $evidence.Pattern -SimpleMatch)) {
        throw "Generated priority evidence is missing: $($evidence.Pattern)"
    }
}
Write-Output "Generated Joydex and Sendspin balanced-priority parity: PASS"

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
if ($LASTEXITCODE -ne 0) {
    throw "Could not inspect the balanced-priority OTA image."
}
$requiredImageEvidence = @(
    "Detected image type: ESP32-S3",
    "Flash size: 16MB",
    "Flash mode: DIO",
    "Checksum: ",
    "(valid)",
    "Validation hash: "
)
foreach ($evidence in $requiredImageEvidence) {
    if ($null -eq (Select-String -InputObject $imageInfo -Pattern $evidence -SimpleMatch)) {
        throw "The balanced-priority OTA image is missing required image evidence: $evidence"
    }
}
if (($imageInfo -notmatch 'Checksum:\s+0x[0-9a-f]+\s+\(valid\)') -or
    ($imageInfo -notmatch 'Validation hash:\s+[0-9a-f]{64}\s+\(valid\)')) {
    throw "The balanced-priority OTA image checksum or validation hash is invalid."
}
$validationHash = [regex]::Match(
    $imageInfo,
    'Validation hash:\s+([0-9a-f]{64})\s+\(valid\)',
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
).Groups[1].Value.ToUpperInvariant()
$otaRecord = $artifactRecords | Where-Object { $_.fileName -eq "firmware.ota.bin" }
$otaRecord.validationHash = $validationHash
Write-Output "OTA image identity, flash geometry, checksum, and validation hash: PASS"

# Rebuilds are not byte-identical because the toolchain embeds build metadata.
# Freeze this exact post-gate payload under its content hash; future attended OTA
# must consume the staged manifest and may not compile into this directory.
$stagedDirectory = Join-Path $stageRootPath $otaRecord.sha256
$stagedManifestPath = Join-Path $stagedDirectory "manifest.json"
$credentialSourceSha256 = (Get-FileHash -LiteralPath $resolvedSecretsPath -Algorithm SHA256).Hash
$manifest = [ordered]@{
    schemaVersion = 1
    firmwareVersion = "0.1.28"
    configuration = $configurationName
    createdUtc = [DateTimeOffset]::UtcNow.ToString("O")
    credentialSourceSha256 = $credentialSourceSha256
    priorities = [ordered]@{
        sendspinPlayer = 18
        sendspinHttpd = 18
        joydexHttpd = 18
        joydexUplinkFeeder = 1
    }
    sourceHashes = [ordered]@{
        sendspinMediaSource = $expectedPatchedSendspinSourceSha256
        sendspinMediaHeader = $expectedPatchedSendspinHeaderSha256
        sendspinHub = $expectedPatchedSendspinHubSha256
        joydexLanAudio = $expectedPatchedJoydexSourceSha256
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
if ($stagedManifest.firmwareVersion -ne "0.1.28" -or
    $stagedManifest.credentialSourceSha256 -ne $credentialSourceSha256) {
    throw "The immutable staged manifest identity or credential-source hash differs from this final build."
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

Write-Output "Verified Voice PE 0.1.28 balanced-priority candidate build complete. No device connection or upload was attempted."
