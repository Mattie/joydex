[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-source"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$basePreparationScript = Join-Path $PSScriptRoot "Prepare-VoicePeAudio.ps1"
$overlayPath = Join-Path $PSScriptRoot "joydex-voice-pe-sendspin.yaml"
$componentPath = Join-Path $PSScriptRoot "components\sendspin"
$patchRoot = Join-Path $PSScriptRoot "patches"
$mixerPatchPath = Join-Path $patchRoot "mixer-playback-accounting.patch"
$speakerSourcePatchPath = Join-Path $patchRoot "speaker-source-playback-accounting.patch"
$gitUnixTools = "C:\Program Files\Git\usr\bin"
$mixerCommit = "0fe230114c80b6d3378f04c777baadee178e40ad"
$speakerSourceCommit = "f5c1a8111df78e32d07d7a7bb800018808cdc0e7"

$expectedPinnedStockHashes = [ordered]@{
    "mixer\__init__.py" = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855"
    "mixer\speaker\__init__.py" = "1646EC3BD0D011869F1ABF6D7BA52F1AEAD1A2FCBBF5D50027EE3ADDE151DF38"
    "mixer\speaker\automation.h" = "660347A41AC36EA82D30684F012EC8C27A32D089BA0F5B012F467200B0C8A2B7"
    "mixer\speaker\mixer_speaker.cpp" = "777D26D0C320C538E6247730488C9BE4F80AA372101059A86912585E512335D8"
    "mixer\speaker\mixer_speaker.h" = "9A24A40F175FCB7AEF981F47B94EC3A07C37CCBE2557D7EC0713F1D616A4F966"
    "speaker_source\__init__.py" = "68315F67CA67195CAB9D22FAD7D264F2B490BE595AD966FB90007D1078AF6EDB"
    "speaker_source\automation.h" = "3EEB6368AFF3A734C6D7A31119C9CB857BA51A5CB7EB9FA0952BD844C9616DFA"
    "speaker_source\media_player.py" = "E7B3FEF19E64BE74AB6AA29A79E9C2249880276F53BE267C9813FF02315B921C"
    "speaker_source\speaker_source_media_player.cpp" = "20FF4D9EB98A059A6A19C1B2886F34BFB63D45C626F5989DA3D614058F593A95"
    "speaker_source\speaker_source_media_player.h" = "D4E7CDC708F5EDC26E66C7C7A0184689CFC5CAE65CFFECC0EC17EFF4375AA3B6"
}

$expectedPinnedPatchedHashes = [ordered]@{
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

foreach ($requiredPath in @(
    $basePreparationScript,
    $overlayPath,
    $componentPath,
    $mixerPatchPath,
    $speakerSourcePatchPath,
    $gitUnixTools,
    $SecretsPath
)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "A required Sendspin source is missing: $requiredPath"
    }
}

& $basePreparationScript -Destination $destinationPath -SecretsPath $SecretsPath
if ($LASTEXITCODE -ne 0) {
    throw "The Voice PE microphone and control preparation failed."
}

$env:PATH = "$gitUnixTools;$env:PATH"
if ($null -eq (Get-Command patch.exe -ErrorAction SilentlyContinue)) {
    throw "patch.exe is unavailable after adding Git for Windows to PATH."
}

$preparedComponentPath = Join-Path $destinationPath "components\sendspin"
$preparedComponentPath = [System.IO.Path]::GetFullPath($preparedComponentPath)
$destinationPrefix = $destinationPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
if (-not $preparedComponentPath.StartsWith($destinationPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace a prepared component outside the selected destination: $preparedComponentPath"
}
if (Test-Path -LiteralPath $preparedComponentPath) {
    Remove-Item -LiteralPath $preparedComponentPath -Recurse -Force
}
New-Item -ItemType Directory -Path $preparedComponentPath -Force | Out-Null

Get-ChildItem -LiteralPath $componentPath -File -Recurse | ForEach-Object {
    $relativePath = [System.IO.Path]::GetRelativePath($componentPath, $_.FullName)
    $targetPath = Join-Path $preparedComponentPath $relativePath
    $targetDirectory = Split-Path -Parent $targetPath
    if (-not (Test-Path -LiteralPath $targetDirectory)) {
        New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
    }
    Copy-Item -LiteralPath $_.FullName -Destination $targetPath -Force
}

$preparedOverlayPath = Join-Path $destinationPath "joydex-voice-pe-sendspin.yaml"
Copy-Item -LiteralPath $overlayPath -Destination $preparedOverlayPath -Force

$pinnedComponentRoot = Join-Path $destinationPath ".joydex-modern-pinned-components\esphome\components"
$pinnedComponentRoot = [System.IO.Path]::GetFullPath($pinnedComponentRoot)
if (-not $pinnedComponentRoot.StartsWith($destinationPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace prepared pinned components outside the selected destination: $pinnedComponentRoot"
}
if (Test-Path -LiteralPath $pinnedComponentRoot) {
    Remove-Item -LiteralPath $pinnedComponentRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $pinnedComponentRoot -Force | Out-Null

foreach ($entry in $expectedPinnedStockHashes.GetEnumerator()) {
    $componentCommit = if ($entry.Key.StartsWith("mixer\", [System.StringComparison]::Ordinal)) {
        $mixerCommit
    } else {
        $speakerSourceCommit
    }
    $targetPath = Join-Path $pinnedComponentRoot $entry.Key
    $targetDirectory = Split-Path -Parent $targetPath
    if (-not (Test-Path -LiteralPath $targetDirectory)) {
        New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
    }
    $sourceRelativePath = $entry.Key.Replace("\", "/")
    $sourceUri = "https://raw.githubusercontent.com/esphome/esphome/$componentCommit/esphome/components/$sourceRelativePath"
    Invoke-WebRequest -Uri $sourceUri -OutFile $targetPath
    if ((Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash -ne $entry.Value) {
        throw "Pinned ESPHome source hash mismatch before patching: $($entry.Key)"
    }
}

foreach ($patchPath in @($mixerPatchPath, $speakerSourcePatchPath)) {
    & patch.exe --batch --forward --strip=1 "--directory=$pinnedComponentRoot" "--input=$patchPath"
    if ($LASTEXITCODE -ne 0) {
        throw "The reviewed playback-accounting patch did not apply cleanly: $patchPath"
    }
}

$preparedPinnedFiles = Get-ChildItem -LiteralPath $pinnedComponentRoot -File -Recurse |
    Where-Object { $_.Extension -ne ".pyc" }
if ($preparedPinnedFiles.Count -ne $expectedPinnedPatchedHashes.Count) {
    throw "The prepared pinned ESPHome component file set differs from the reviewed set."
}
foreach ($entry in $expectedPinnedPatchedHashes.GetEnumerator()) {
    $preparedPinnedPath = Join-Path $pinnedComponentRoot $entry.Key
    if (-not (Test-Path -LiteralPath $preparedPinnedPath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $preparedPinnedPath -Algorithm SHA256).Hash -ne $entry.Value) {
        throw "Prepared pinned ESPHome patch parity failed: $($entry.Key)"
    }
}

$sourceFiles = Get-ChildItem -LiteralPath $componentPath -File -Recurse | Sort-Object FullName
$preparedFiles = Get-ChildItem -LiteralPath $preparedComponentPath -File -Recurse | Sort-Object FullName
if ($sourceFiles.Count -ne $preparedFiles.Count) {
    throw "Prepared modern Sendspin component file count differs from tracked source."
}
for ($index = 0; $index -lt $sourceFiles.Count; $index++) {
    $sourceRelativePath = [System.IO.Path]::GetRelativePath($componentPath, $sourceFiles[$index].FullName)
    $preparedRelativePath = [System.IO.Path]::GetRelativePath($preparedComponentPath, $preparedFiles[$index].FullName)
    if ($sourceRelativePath -ne $preparedRelativePath -or
        (Get-FileHash -LiteralPath $sourceFiles[$index].FullName -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $preparedFiles[$index].FullName -Algorithm SHA256).Hash) {
        throw "Prepared modern Sendspin source parity failed for $sourceRelativePath."
    }
}

Write-Output "Prepared the Voice PE Sendspin speaker layer at $destinationPath"
Write-Output "Pinned media/speaker ABI, playback backports, adapter source, and overlay parity: PASS"
Write-Output "No device connection or upload was attempted."
