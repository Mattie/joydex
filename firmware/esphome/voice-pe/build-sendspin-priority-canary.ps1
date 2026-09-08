[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\.tools\voice-pe-sendspin-priority-0.1.27-repro"),
    [Parameter(Mandatory = $true)]
    [string]$SecretsPath
)

$ErrorActionPreference = "Stop"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$resolvedSecretsPath = [System.IO.Path]::GetFullPath($SecretsPath)
$preparationScript = Join-Path $PSScriptRoot "prepare-sendspin-priority-canary.ps1"
$parityScript = Join-Path $PSScriptRoot "verify-network-credential-parity.py"
$espHomeExecutable = Join-Path $PSScriptRoot "..\.venv\Scripts\esphome.exe"
$pythonExecutable = Join-Path $PSScriptRoot "..\.venv\Scripts\python.exe"
$gitUnixTools = "C:\Program Files\Git\usr\bin"
$configurationName = "joydex-voice-pe-sendspin-priority-canary.yaml"
$expectedEspHomeVersion = "2025.12.2"
$expectedPatchedSendspinSourceSha256 = "40E1EF496328FAB880E1E60DED1A3517083801DAEC28D7AF122D718D06F521BE"
$expectedPatchedSendspinHeaderSha256 = "5DB5612E718CECEF257B9E38446ABA29F78F9B234AE520A44E7BBEDAC10CFD4F"

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
        throw "The Voice PE Sendspin priority compile failed."
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

$preparedSendspinPath = Join-Path $destinationPath ".joydex-sendspin\esphome\components\sendspin\media_source"
$generatedSendspinPath = Join-Path $destinationPath ".esphome\build\home-assistant-voice\src\esphome\components\sendspin\media_source"
foreach ($fileName in @("sendspin_media_source.cpp", "sendspin_media_source.h")) {
    $preparedPath = Join-Path $preparedSendspinPath $fileName
    $generatedPath = Join-Path $generatedSendspinPath $fileName
    $expectedHash = if ($fileName.EndsWith(".cpp")) {
        $expectedPatchedSendspinSourceSha256
    } else {
        $expectedPatchedSendspinHeaderSha256
    }
    if ((Get-FileHash -LiteralPath $preparedPath -Algorithm SHA256).Hash -ne $expectedHash -or
        (Get-FileHash -LiteralPath $generatedPath -Algorithm SHA256).Hash -ne $expectedHash) {
        throw "Prepared/generated Sendspin parity failed for $fileName."
    }
}

$generatedSendspinSourcePath = Join-Path $generatedSendspinPath "sendspin_media_source.cpp"
$priorityDefinition = Select-String -LiteralPath $generatedSendspinSourcePath -Pattern "SYNC_TASK_PRIORITY = 18" -SimpleMatch
$priorityUse = Select-String -LiteralPath $generatedSendspinSourcePath -Pattern "params, SYNC_TASK_PRIORITY" -SimpleMatch
if ($null -eq $priorityDefinition -or $null -eq $priorityUse) {
    throw "The generated Sendspin source does not run its player task at the reviewed priority."
}
Write-Output "Generated Joydex component and patched Sendspin parity: PASS"

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

Write-Output "Verified Voice PE 0.1.27 candidate build complete. No device connection or upload was attempted."
