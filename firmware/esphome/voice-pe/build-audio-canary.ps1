[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-audio-bridge-0.1.26-repro"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$resolvedSecretsPath = [System.IO.Path]::GetFullPath($SecretsPath)
$preparationScript = Join-Path $PSScriptRoot "prepare-audio-canary.ps1"
$parityScript = Join-Path $PSScriptRoot "verify-network-credential-parity.py"
$espHomeExecutable = Join-Path $PSScriptRoot "..\.venv\Scripts\esphome.exe"
$pythonExecutable = Join-Path $PSScriptRoot "..\.venv\Scripts\python.exe"
$gitUnixTools = "C:\Program Files\Git\usr\bin"
$configurationName = "joydex-voice-pe-audio-canary.yaml"
$expectedEspHomeVersion = "2025.12.2"
$expectedStockSendspinSourceSha256 = "16332336CE308EB6C6B3A29F7FF98F0AA9A37C28DBE083D2CF6FCB9F3B6D53FE"
$expectedStockSendspinHeaderSha256 = "9C7D6B630A071261AE8E6FE87FF86ADA735FBBEB9080AA4CF16F47EDA256794E"

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

Push-Location $destinationPath
try {
    & $espHomeExecutable compile $configurationName
    if ($LASTEXITCODE -ne 0) {
        throw "The Voice PE ESPHome compile failed."
    }
} finally {
    Pop-Location
}

$generatedMainPath = Join-Path $destinationPath ".esphome\build\home-assistant-voice\src\main.cpp"
& $pythonExecutable $parityScript --secrets $resolvedSecretsPath --generated-main $generatedMainPath
if ($LASTEXITCODE -ne 0) {
    throw "The compiled Voice PE network credential parity check failed."
}

$sourceComponentPath = Join-Path $PSScriptRoot "components\joydex_lan_audio"
$generatedComponentPath = Join-Path $destinationPath ".esphome\build\home-assistant-voice\src\esphome\components\joydex_lan_audio"
$sourceFiles = Get-ChildItem -LiteralPath $sourceComponentPath -File |
    Where-Object { $_.Extension -in @(".cpp", ".h") } |
    Sort-Object Name
$generatedFiles = Get-ChildItem -LiteralPath $generatedComponentPath -File | Sort-Object Name
if ($sourceFiles.Count -ne $generatedFiles.Count) {
    throw "The generated Joydex LAN audio component file count differs from tracked source."
}
for ($index = 0; $index -lt $sourceFiles.Count; $index++) {
    if ($sourceFiles[$index].Name -ne $generatedFiles[$index].Name) {
        throw "The generated Joydex LAN audio component names differ from tracked source."
    }
    $sourceHash = (Get-FileHash -LiteralPath $sourceFiles[$index].FullName -Algorithm SHA256).Hash
    $generatedHash = (Get-FileHash -LiteralPath $generatedFiles[$index].FullName -Algorithm SHA256).Hash
    if ($sourceHash -ne $generatedHash) {
        throw "Generated component parity failed for $($sourceFiles[$index].Name)."
    }
}

$generatedSendspinPath = Join-Path $destinationPath ".esphome\build\home-assistant-voice\src\esphome\components\sendspin\media_source"
$generatedSendspinSourcePath = Join-Path $generatedSendspinPath "sendspin_media_source.cpp"
$generatedSendspinHeaderPath = Join-Path $generatedSendspinPath "sendspin_media_source.h"
if ((Get-FileHash -LiteralPath $generatedSendspinSourcePath -Algorithm SHA256).Hash -ne $expectedStockSendspinSourceSha256 -or
    (Get-FileHash -LiteralPath $generatedSendspinHeaderPath -Algorithm SHA256).Hash -ne $expectedStockSendspinHeaderSha256) {
    throw "Generated Sendspin differs from the exact stock source accepted on deployed 0.1.23."
}
Write-Output "Generated Joydex component and stock Sendspin parity: PASS"

$otaImagePath = Join-Path $destinationPath ".esphome\build\home-assistant-voice\.pioenvs\home-assistant-voice\firmware.ota.bin"
$factoryImagePath = Join-Path $destinationPath ".esphome\build\home-assistant-voice\.pioenvs\home-assistant-voice\firmware.factory.bin"
foreach ($imagePath in @($otaImagePath, $factoryImagePath)) {
    if (-not (Test-Path -LiteralPath $imagePath -PathType Leaf)) {
        throw "The expected Voice PE firmware artifact is missing: $imagePath"
    }

    $image = Get-Item -LiteralPath $imagePath
    $imageHash = (Get-FileHash -LiteralPath $imagePath -Algorithm SHA256).Hash
    Write-Output "$($image.Name): $($image.Length) bytes; SHA-256 $imageHash"
}

Write-Output "Verified Voice PE firmware build complete. No device connection or upload was attempted."
