[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-modern-sendspin-0.1.33-repro"),
    [string]$StageRoot = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-modern-sendspin-0.1.33-closure-staged"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$stageRootPath = [System.IO.Path]::GetFullPath($StageRoot)
$resolvedSecretsPath = [System.IO.Path]::GetFullPath($SecretsPath)
$preparationScript = Join-Path $PSScriptRoot "prepare-modern-sendspin-canary.ps1"
$parityScript = Join-Path $PSScriptRoot "verify-network-credential-parity.py"
$trackedComponentRoot = Join-Path $PSScriptRoot "components\sendspin"
$patchRoot = Join-Path $PSScriptRoot "patches"
$overlayPath = Join-Path $PSScriptRoot "joydex-voice-pe-modern-sendspin-canary.yaml"
$espHomeExecutable = Join-Path $PSScriptRoot "..\.venv\Scripts\esphome.exe"
$pythonExecutable = Join-Path $PSScriptRoot "..\.venv\Scripts\python.exe"
$platformIoPython = Join-Path $env:USERPROFILE ".platformio\penv\Scripts\python.exe"
$gitUnixTools = "C:\Program Files\Git\usr\bin"
$configurationName = "joydex-voice-pe-modern-sendspin-canary.yaml"
$sourceDateEpoch = "1787961600"

$expectedEspHomeVersion = "2025.12.2"
$expectedFirmwareVersion = "0.1.33"
$expectedSendspinVersion = "0.7.2"
$expectedSendspinCommit = "30514d5102c269a0c7fa6a13932d6bf7f2ae1abc"
$expectedSendspinComponentHash = "79310684d51ac5c6146765989548aacfab230e368fd5892c2d6124fe791425e8"
$expectedMixerCommit = "0fe230114c80b6d3378f04c777baadee178e40ad"
$expectedSpeakerSourceCommit = "f5c1a8111df78e32d07d7a7bb800018808cdc0e7"
$expectedOverlaySha256 = "8E5410530FB2E82454BA570273273641D7F5FE1B6C71B5B17AB9A001BFE460DE"
$expectedDependencyLockSha256 = "C725F1C549D0BD73E84F5B9CDD89272E1CD53930F89E05EC9620A8264D96771B"
$baselineTotalImageBytes = 3109667
$baselineStaticRamBytes = 45632
$maximumTotalImageBytes = 3300000
$maximumStaticRamBytes = 50000
$maximumDiramBytes = 150000

$expectedSourceHashes = [ordered]@{
    "__init__.py" = "96DC1DDA8F57023E7155C17E849AB5B4017047963F5383E6967A51129C0401B6"
    "media_player\__init__.py" = "95ED474512E85E7D8C275900764151992F2981CA174CC033C3A54AEA70CDAAE7"
    "media_player\sendspin_media_player.cpp" = "66C9A48F4281AD912B9549C7A62EEC02067E62CA20867268F8A011712A7606E7"
    "media_player\sendspin_media_player.h" = "4A35B1DC39BBD4A3E1AF6173AE200CBD8DA2242D1D712B67713758767BFE9ABA"
    "media_source\__init__.py" = "A67C94EC4ECE19A2667DA266F0C56030229E286813F59CEDD5E1BD5CC0E4DE33"
    "media_source\sendspin_media_source.cpp" = "BBAB8F438921498356B98F224BE8DB4F64644CD395BF3BEA04B24B29B919027B"
    "media_source\sendspin_media_source.h" = "F1914F681A0D34359332EC05D61C621EC276414553E73AFDF8811B4EF3C4B775"
    "sendspin_hub.cpp" = "E6DC54235E75719DBD19B6A5DCBD9B79312EA953A153B82C4C4F7B0CEAE66C13"
    "sendspin_hub.h" = "8DA5440A662E6F5D1366D502C13D8853516A936C0270E4123283012EB7DF36BD"
}

$expectedPatchHashes = [ordered]@{
    "mixer-playback-accounting.patch" = "60B23F873F33498BEFBC9B7259A9C0BFA12258D2A9428AA29DE5CE695EFF2680"
    "speaker-source-playback-accounting.patch" = "6595C05C009241A2BBE94C153A142175C935599A2135BB05C86A93F0FF977E26"
}

$expectedPreparedPinnedHashes = [ordered]@{
    "mixer\__init__.py" = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855"
    "mixer\speaker\__init__.py" = "1646EC3BD0D011869F1ABF6D7BA52F1AEAD1A2FCBBF5D50027EE3ADDE151DF38"
    "mixer\speaker\automation.h" = "660347A41AC36EA82D30684F012EC8C27A32D089BA0F5B012F467200B0C8A2B7"
    "mixer\speaker\mixer_speaker.cpp" = "08379B69DFC918492EEC33E71135A9CBF638F29133ED465022EFAD89FD24676B"
    "mixer\speaker\mixer_speaker.h" = "9473AC75AD85D97A0A57AD9C0670A1E04103749D72546DC01C40D331A5D952C4"
    "speaker_source\__init__.py" = "68315F67CA67195CAB9D22FAD7D264F2B490BE595AD966FB90007D1078AF6EDB"
    "speaker_source\automation.h" = "3EEB6368AFF3A734C6D7A31119C9CB857BA51A5CB7EB9FA0952BD844C9616DFA"
    "speaker_source\media_player.py" = "E7B3FEF19E64BE74AB6AA29A79E9C2249880276F53BE267C9813FF02315B921C"
    "speaker_source\speaker_source_media_player.cpp" = "C37BC50531B002A936529EF4C2413DCC1047A8165C1A9714248CC51754F0E7C7"
    "speaker_source\speaker_source_media_player.h" = "CACFCC8A989201D653183DAD06B61D101119E2AA49B6CEE089CCE1AF0E7A4C07"
}

$expectedGeneratedAbiHashes = [ordered]@{
    "audio\audio.h" = "053F8C60131AFBA824D403061155F2FD809582526F9E399153FAF7A791A7A1AF"
    "media_source\media_source.h" = "8186F9E0D801DC516D5CCCF6C83EDF5E00B90958F1FF98FEF18F009D721496FE"
    "mixer\speaker\mixer_speaker.cpp" = "08379B69DFC918492EEC33E71135A9CBF638F29133ED465022EFAD89FD24676B"
    "mixer\speaker\mixer_speaker.h" = "9473AC75AD85D97A0A57AD9C0670A1E04103749D72546DC01C40D331A5D952C4"
    "speaker_source\speaker_source_media_player.cpp" = "C37BC50531B002A936529EF4C2413DCC1047A8165C1A9714248CC51754F0E7C7"
    "speaker_source\speaker_source_media_player.h" = "CACFCC8A989201D653183DAD06B61D101119E2AA49B6CEE089CCE1AF0E7A4C07"
}

$expectedInputClosureHashes = [ordered]@{
    "components\joydex_lan_audio\__init__.py" = "81061CF2DD2055BE27776E747A6EB032D16C2FB2475F87B047B4782C7A55E09D"
    "components\joydex_lan_audio\joydex_lan_audio.cpp" = "1DB76B08DEF2011BFCC680F300CCA535B4D81D8139134C2C297ACE04045DF0E4"
    "components\joydex_lan_audio\joydex_lan_audio.h" = "27612F836F6979FC9F355365BFB2A1315ED4E220FB0F045147F75F92726005A0"
    "components\joydex_lan_audio\PROTOCOL.md" = "3400A2E55E8EA4EDC7E4CF9A4B5C2B7486E5E08B11A8A7C496EBB58020D9BD24"
    "Copy-VerifiedVoicePeSecrets.ps1" = "AD2D5ADF88E7A5575668B8C6D6409FE9CCF87200250D853743E6DCAF1A8F45E3"
    "exclusive-joydex-wake.patch" = "04683268A4E03F0FC3BED963F7397109B9DA6BED62B5890DFF4EB4B2647E23A5"
    "joydex-voice-pe-audio-canary.yaml" = "CF85BB974DDDAF0878305E4A60CB5D7CF8B56A2EEC016517929AFC21BC0B7CAD"
    "joydex-voice-pe-modern-sendspin-canary.yaml" = "8E5410530FB2E82454BA570273273641D7F5FE1B6C71B5B17AB9A001BFE460DE"
    "joydex-voice-pe-score-canary.yaml" = "F99CBDB67F87B1DB6C0FE02A45510C9D644686024DB75FBDB71B4F579572FEC0"
    "joydex-voice-pe-score-sound-canary.yaml" = "FA8366AF735031CF3D5A623E91534801B31ABD4F82CC2A59B8E6E854978749D4"
    "micro-wake-score-telemetry.patch" = "B2BB25A5EC3D9002489985967812B70D3F322530B81753F9B179E837B9A8E7E6"
    "patches\mixer-playback-accounting.patch" = "60B23F873F33498BEFBC9B7259A9C0BFA12258D2A9428AA29DE5CE695EFF2680"
    "patches\speaker-source-playback-accounting.patch" = "6595C05C009241A2BBE94C153A142175C935599A2135BB05C86A93F0FF977E26"
    "prepare-audio-canary.ps1" = "E03E34F7DEB05D7832EFA9C45AEA6D436623EADA2AC4A5CCA7098A66F5A857BE"
    "prepare-modern-sendspin-canary.ps1" = "B572D16882CF47646DCB95E8D53089AADAA6A2E5DDDB85744A467BB4BA3700F5"
    "prepare-score-canary.ps1" = "F3B4671A332474C735F0D776994E6FB9C0792F032C64B8DFA4E7A0732B1B07AD"
    "prepare-score-sound-canary.ps1" = "375DCEB2787B07557A8AE376D5647CD5D3CF76EC1FA18C7C59AFEA8C704BA471"
    "sounds\ReadyBlip.flac" = "2E56276BCDB96F538427E9840D7FB01F4D435F69B79B857E682D5CCB219F3D58"
    "sounds\WakeBlip.flac" = "8AAF3B4EA12C77571B0F750EE3891BF4517F3F212B908A93563E6FC57A26F6AD"
    "sounds\EndBlip.flac" = "FC0365A43D7D0666FD3BFBEAC490825A22C77964AABC7407F2D39D0FCDA483D5"
}

function Get-HashMapDigest([System.Collections.IDictionary]$Hashes) {
    $lines = [string[]]@($Hashes.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
    [Array]::Sort($lines, [StringComparer]::Ordinal)
    $bytes = [Text.Encoding]::UTF8.GetBytes($lines -join "`n")
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}

function Get-NormalizedTextSha256([string]$Path) {
    $normalized = ((Get-Content -LiteralPath $Path -Raw) -replace "`r`n", "`n").TrimEnd() + "`n"
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($normalized))
    )
}

$expectedInputClosureSha256 = "6FB170A211FE6576784C4CE152F129CCF8B654AB5AA416691844FFB449324C77"

foreach ($requiredPath in @(
    $preparationScript,
    $parityScript,
    $trackedComponentRoot,
    $patchRoot,
    $overlayPath,
    $espHomeExecutable,
    $pythonExecutable,
    $platformIoPython,
    $gitUnixTools,
    $resolvedSecretsPath
)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "A required modern Sendspin build dependency is missing: $requiredPath"
    }
}

$trackedLanAudioRoot = Join-Path $PSScriptRoot "components\joydex_lan_audio"
$trackedLanAudioFiles = @(Get-ChildItem -LiteralPath $trackedLanAudioRoot -Recurse -File |
    ForEach-Object { [System.IO.Path]::GetRelativePath($PSScriptRoot, $_.FullName) })
$expectedLanAudioFiles = @($expectedInputClosureHashes.Keys |
    Where-Object { $_.StartsWith("components\joydex_lan_audio\", [StringComparison]::Ordinal) })
$actualLanAudioFiles = [string[]]$trackedLanAudioFiles
$reviewedLanAudioFiles = [string[]]$expectedLanAudioFiles
[Array]::Sort($actualLanAudioFiles, [StringComparer]::Ordinal)
[Array]::Sort($reviewedLanAudioFiles, [StringComparer]::Ordinal)
if (($actualLanAudioFiles -join "`n") -cne ($reviewedLanAudioFiles -join "`n")) {
    throw "The tracked Joydex LAN-audio component differs from the reviewed file set."
}
foreach ($entry in $expectedInputClosureHashes.GetEnumerator()) {
    $inputPath = Join-Path $PSScriptRoot $entry.Key
    if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash -cne $entry.Value) {
        throw "The reviewed firmware input closure changed: $($entry.Key)"
    }
}
$inputClosureSha256 = Get-HashMapDigest $expectedInputClosureHashes
if ($inputClosureSha256 -cne $expectedInputClosureSha256) {
    throw "The reviewed firmware input-closure digest changed."
}

if ((Get-FileHash -LiteralPath $overlayPath -Algorithm SHA256).Hash -ne $expectedOverlaySha256) {
    throw "The reviewed modern Sendspin overlay hash changed."
}

$trackedSourceFiles = Get-ChildItem -LiteralPath $trackedComponentRoot -Recurse -File |
    Where-Object { $_.Extension -in @(".py", ".cpp", ".h") }
if ($trackedSourceFiles.Count -ne $expectedSourceHashes.Count) {
    throw "The tracked modern Sendspin source set differs from the reviewed file set."
}
foreach ($entry in $expectedSourceHashes.GetEnumerator()) {
    $trackedPath = Join-Path $trackedComponentRoot $entry.Key
    if (-not (Test-Path -LiteralPath $trackedPath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $trackedPath -Algorithm SHA256).Hash -ne $entry.Value) {
        throw "The reviewed modern Sendspin source changed: $($entry.Key)"
    }
}
foreach ($entry in $expectedPatchHashes.GetEnumerator()) {
    $trackedPatchPath = Join-Path $patchRoot $entry.Key
    if (-not (Test-Path -LiteralPath $trackedPatchPath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $trackedPatchPath -Algorithm SHA256).Hash -ne $entry.Value) {
        throw "The reviewed playback-accounting patch changed: $($entry.Key)"
    }
}

$env:PATH = "$gitUnixTools;$env:PATH"
$env:SOURCE_DATE_EPOCH = $sourceDateEpoch
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
            throw "The modern Sendspin $Phase compile failed."
        }
    } finally {
        Pop-Location
    }
}

$buildRoot = Join-Path $destinationPath ".esphome\build\home-assistant-voice"
$generatedMainPath = Join-Path $buildRoot "src\main.cpp"
$generatedDefinesPath = Join-Path $buildRoot "src\esphome\core\defines.h"
$generatedComponentsRoot = Join-Path $buildRoot "src\esphome\components"
$generatedComponentRoot = Join-Path $buildRoot "src\esphome\components\sendspin"
$preparedComponentRoot = Join-Path $destinationPath "components\sendspin"
$preparedPinnedRoot = Join-Path $destinationPath ".joydex-modern-pinned-components\esphome\components"
$sdkConfigPath = Join-Path $buildRoot "sdkconfig.home-assistant-voice"
$dependencyLockPath = Join-Path $buildRoot "dependencies.lock"
$managedSendspinManifestPath = Join-Path $buildRoot "managed_components\sendspin__sendspin-cpp\idf_component.yml"

function Assert-GeneratedCandidate([string]$Phase) {
    & $pythonExecutable $parityScript --secrets $resolvedSecretsPath --generated-main $generatedMainPath
    if ($LASTEXITCODE -ne 0) {
        throw "The $Phase network/recovery credential parity check failed."
    }

    foreach ($evidence in @(
        @{ Path = $generatedDefinesPath; Pattern = '#define ESPHOME_PROJECT_NAME "Joydex.Voice PE Audio Bridge"' },
        @{ Path = $generatedDefinesPath; Pattern = "#define ESPHOME_PROJECT_VERSION `"$expectedFirmwareVersion`"" },
        @{ Path = $generatedMainPath; Pattern = "wifi_id->set_power_save_mode(wifi::WIFI_POWER_SAVE_NONE);" },
        @{ Path = $generatedMainPath; Pattern = ".audio_buffer_capacity = 1000000," },
        @{ Path = $generatedMainPath; Pattern = ".extra_startup_silence_ms = 50," },
        @{ Path = $generatedMainPath; Pattern = ".psram_stack = false," },
        @{ Path = $generatedMainPath; Pattern = "api_id->set_reboot_timeout(0);" },
        @{ Path = $sdkConfigPath; Pattern = "CONFIG_SENDSPIN_ENABLE_PLAYER=y" },
        @{ Path = $sdkConfigPath; Pattern = "CONFIG_SENDSPIN_ENABLE_CONTROLLER=y" },
        @{ Path = $sdkConfigPath; Pattern = "CONFIG_APP_REPRODUCIBLE_BUILD=y" },
        @{ Path = $sdkConfigPath; Pattern = "# CONFIG_SENDSPIN_ENABLE_METADATA is not set" },
        @{ Path = $sdkConfigPath; Pattern = "# CONFIG_SENDSPIN_ENABLE_COLOR is not set" },
        @{ Path = $sdkConfigPath; Pattern = "# CONFIG_SENDSPIN_ENABLE_ARTWORK is not set" },
        @{ Path = $sdkConfigPath; Pattern = "# CONFIG_SENDSPIN_ENABLE_VISUALIZER is not set" },
        @{ Path = Join-Path $generatedComponentRoot "sendspin_hub.cpp"; Pattern = "config.server_port = 8927;" }
    )) {
        if ($null -eq (Select-String -LiteralPath $evidence.Path -Pattern $evidence.Pattern -SimpleMatch)) {
            throw "The $Phase generated candidate evidence is missing: $($evidence.Pattern)"
        }
    }

    foreach ($entry in $expectedSourceHashes.GetEnumerator()) {
        $preparedPath = Join-Path $preparedComponentRoot $entry.Key
        if (-not (Test-Path -LiteralPath $preparedPath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $preparedPath -Algorithm SHA256).Hash -ne $entry.Value) {
            throw "Prepared modern Sendspin source parity failed: $($entry.Key)"
        }
        if ([System.IO.Path]::GetExtension($entry.Key) -in @(".cpp", ".h")) {
            $generatedPath = Join-Path $generatedComponentRoot $entry.Key
            if (-not (Test-Path -LiteralPath $generatedPath -PathType Leaf) -or
                (Get-FileHash -LiteralPath $generatedPath -Algorithm SHA256).Hash -ne $entry.Value) {
                throw "Generated modern Sendspin source parity failed: $($entry.Key)"
            }
        }
    }

    $preparedPinnedFiles = Get-ChildItem -LiteralPath $preparedPinnedRoot -File -Recurse |
        Where-Object { $_.Extension -ne ".pyc" }
    if ($preparedPinnedFiles.Count -ne $expectedPreparedPinnedHashes.Count) {
        throw "The $Phase prepared pinned ESPHome source set differs from the reviewed set."
    }
    foreach ($entry in $expectedPreparedPinnedHashes.GetEnumerator()) {
        $preparedPinnedPath = Join-Path $preparedPinnedRoot $entry.Key
        if (-not (Test-Path -LiteralPath $preparedPinnedPath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $preparedPinnedPath -Algorithm SHA256).Hash -ne $entry.Value) {
            throw "The $Phase prepared pinned ESPHome source changed: $($entry.Key)"
        }
    }
    foreach ($entry in $expectedGeneratedAbiHashes.GetEnumerator()) {
        $generatedAbiPath = Join-Path $generatedComponentsRoot $entry.Key
        if (-not (Test-Path -LiteralPath $generatedAbiPath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $generatedAbiPath -Algorithm SHA256).Hash -ne $entry.Value) {
            throw "The $Phase generated pinned media/speaker ABI changed: $($entry.Key)"
        }
    }

    $dependencyLock = Get-Content -LiteralPath $dependencyLockPath -Raw
    if ((Get-NormalizedTextSha256 $dependencyLockPath) -cne $expectedDependencyLockSha256) {
        throw "The complete normalized dependency lock differs from the reviewed resolution."
    }
    $lockPattern = "(?ms)^  sendspin/sendspin-cpp:\r?\n    component_hash: $expectedSendspinComponentHash.*?^    version: $([regex]::Escape($expectedSendspinVersion))\r?$"
    if ($dependencyLock -notmatch $lockPattern) {
        throw "The resolved Sendspin dependency is not the reviewed registry component."
    }
    $managedManifest = Get-Content -LiteralPath $managedSendspinManifestPath -Raw
    if ($managedManifest -notmatch "(?m)^version: $([regex]::Escape($expectedSendspinVersion))\r?$" -or
        $managedManifest -notmatch "(?m)^  commit_sha: $expectedSendspinCommit\r?$") {
        throw "The managed Sendspin source version or upstream commit differs from the reviewed source."
    }
}

Invoke-CanaryCompile "preflight"
Assert-GeneratedCandidate "preflight"
Write-Output "Preflight identity, credential, adapter, patched ABI, role, and dependency gates: PASS"

Invoke-CanaryCompile "final"
Assert-GeneratedCandidate "final"
Write-Output "Final identity, credential, adapter, patched ABI, role, and dependency gates: PASS"

$pioEnvironment = Join-Path $buildRoot ".pioenvs\home-assistant-voice"
$otaImagePath = Join-Path $pioEnvironment "firmware.ota.bin"
$factoryImagePath = Join-Path $pioEnvironment "firmware.factory.bin"
$mapPath = Join-Path $pioEnvironment "firmware.map"
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

$artifactRecords = @()
foreach ($imagePath in @($otaImagePath, $factoryImagePath)) {
    if (-not (Test-Path -LiteralPath $imagePath -PathType Leaf)) {
        throw "The expected modern Sendspin firmware artifact is missing: $imagePath"
    }
    $image = Get-Item -LiteralPath $imagePath
    $artifactRecords += [ordered]@{
        fileName = $image.Name
        bytes = $image.Length
        sha256 = (Get-FileHash -LiteralPath $imagePath -Algorithm SHA256).Hash
        validationHash = $null
    }
}

$imageInfo = (& $pythonExecutable -m esptool image-info $otaImagePath 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0 -or
    $imageInfo -notmatch 'Detected image type:\s+ESP32-S3' -or
    $imageInfo -notmatch 'Flash size:\s+16MB' -or
    $imageInfo -notmatch 'Flash mode:\s+DIO' -or
    $imageInfo -notmatch 'Checksum:\s+0x[0-9a-f]+\s+\(valid\)' -or
    $imageInfo -notmatch 'Validation hash:\s+[0-9a-f]{64}\s+\(valid\)') {
    throw "The modern Sendspin OTA image identity, geometry, checksum, or validation hash is invalid."
}
$validationHash = [regex]::Match(
    $imageInfo,
    'Validation hash:\s+([0-9a-f]{64})\s+\(valid\)',
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
).Groups[1].Value.ToUpperInvariant()
($artifactRecords | Where-Object fileName -eq "firmware.ota.bin").validationHash = $validationHash

$otaRecord = $artifactRecords | Where-Object fileName -eq "firmware.ota.bin"
$stagedDirectory = [System.IO.Path]::GetFullPath((Join-Path $stageRootPath $otaRecord.sha256))
$stagePrefix = $stageRootPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
if (-not $stagedDirectory.StartsWith($stagePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to stage outside the selected stage root: $stagedDirectory"
}
$stagedManifestPath = Join-Path $stagedDirectory "manifest.json"
$manifest = [ordered]@{
    schemaVersion = 1
    firmwareVersion = $expectedFirmwareVersion
    configuration = $configurationName
    createdUtc = [DateTimeOffset]::UtcNow.ToString("O")
    credentialSourceSha256 = (Get-FileHash -LiteralPath $resolvedSecretsPath -Algorithm SHA256).Hash
    change = "Keep the modern Sendspin worker stack in internal RAM and disable the unused Home Assistant API client's 15-minute reboot watchdog"
    deviceConnectionAttempted = $false
    reproducibleBuild = $true
    sourceDateEpoch = $sourceDateEpoch
    inputClosureSha256 = $expectedInputClosureSha256
    inputClosureHashes = $expectedInputClosureHashes
    dependencyLockSha256 = $expectedDependencyLockSha256
    sendspin = [ordered]@{
        version = $expectedSendspinVersion
        commit = $expectedSendspinCommit
        componentHash = $expectedSendspinComponentHash
        port = 8927
        taskStackInPsram = $false
        roles = @("player", "controller")
        excludedRoles = @("metadata", "color", "artwork", "visualizer")
    }
    pinnedComponents = [ordered]@{
        mixerCommit = $expectedMixerCommit
        speakerSourceCommit = $expectedSpeakerSourceCommit
        patchHashes = $expectedPatchHashes
        preparedSourceHashes = $expectedPreparedPinnedHashes
        generatedAbiHashes = $expectedGeneratedAbiHashes
    }
    resources = [ordered]@{
        totalImageBytes = $totalImageBytes
        staticRamBytes = $staticRamBytes
        diramBytes = $diramBytes
        baselineVersion = "0.1.30"
        baselineTotalImageBytes = $baselineTotalImageBytes
        baselineStaticRamBytes = $baselineStaticRamBytes
        totalImageDeltaBytes = $totalImageBytes - $baselineTotalImageBytes
        staticRamDeltaBytes = $staticRamBytes - $baselineStaticRamBytes
    }
    overlaySha256 = $expectedOverlaySha256
    sourceHashes = $expectedSourceHashes
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
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $stagedManifestPath -Encoding utf8NoBOM
}

$stagedManifest = Get-Content -LiteralPath $stagedManifestPath -Raw | ConvertFrom-Json
if ($stagedManifest.firmwareVersion -ne $expectedFirmwareVersion -or
    $stagedManifest.credentialSourceSha256 -ne $manifest.credentialSourceSha256 -or
    $stagedManifest.reproducibleBuild -ne $true -or
    $stagedManifest.sourceDateEpoch -ne $sourceDateEpoch -or
    $stagedManifest.inputClosureSha256 -ne $expectedInputClosureSha256 -or
    $stagedManifest.dependencyLockSha256 -ne $expectedDependencyLockSha256 -or
    $stagedManifest.sendspin.version -ne $expectedSendspinVersion -or
    $stagedManifest.sendspin.commit -ne $expectedSendspinCommit -or
    $stagedManifest.sendspin.taskStackInPsram -ne $false -or
    $stagedManifest.pinnedComponents.mixerCommit -ne $expectedMixerCommit -or
    $stagedManifest.pinnedComponents.speakerSourceCommit -ne $expectedSpeakerSourceCommit -or
    $stagedManifest.overlaySha256 -ne $expectedOverlaySha256) {
    throw "The immutable staged manifest differs from this final modern Sendspin build."
}
foreach ($expectedSet in @(
    @{ Name = "inputClosureHashes"; Values = $expectedInputClosureHashes; Parent = $stagedManifest },
    @{ Name = "patchHashes"; Values = $expectedPatchHashes; Parent = $stagedManifest.pinnedComponents },
    @{ Name = "preparedSourceHashes"; Values = $expectedPreparedPinnedHashes; Parent = $stagedManifest.pinnedComponents },
    @{ Name = "generatedAbiHashes"; Values = $expectedGeneratedAbiHashes; Parent = $stagedManifest.pinnedComponents }
)) {
    $stagedSet = $expectedSet.Parent.($expectedSet.Name)
    foreach ($entry in $expectedSet.Values.GetEnumerator()) {
        if ($stagedSet.($entry.Key) -ne $entry.Value) {
            throw "The immutable staged manifest differs for $($expectedSet.Name).$($entry.Key)."
        }
    }
}
foreach ($record in $artifactRecords) {
    $stagedArtifactPath = Join-Path $stagedDirectory $record.fileName
    $manifestRecord = $stagedManifest.artifacts | Where-Object fileName -eq $record.fileName
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

Write-Output "Resource gate: image $totalImageBytes bytes (+$($totalImageBytes - $baselineTotalImageBytes)); static RAM $staticRamBytes bytes (+$($staticRamBytes - $baselineStaticRamBytes)); DIRAM $diramBytes bytes."
foreach ($record in $artifactRecords) {
    Write-Output "$($record.fileName): $($record.bytes) bytes; SHA-256 $($record.sha256)"
}
Write-Output "Immutable no-OTA manifest: $stagedManifestPath"
Write-Output "Verified Voice PE $expectedFirmwareVersion modern Sendspin candidate build complete. No device connection or upload was attempted."
