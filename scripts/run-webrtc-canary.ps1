[CmdletBinding()]
param(
    [ValidateRange(1, 65535)]
    [int]$Port = 8766,

    [string]$ThreadTitle = "Codex Voice Chat",

    [string]$ThreadId,

    [string]$CodexPath,

    [string]$InputWav,

    [string]$CaptureWebm,

    [ValidateSet("disabled", "observe")]
    [string]$AttestationMode = "disabled",

    [switch]$EphemeralThread,

    [switch]$NoOpen
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$selectedCodex = $CodexPath
if ([string]::IsNullOrWhiteSpace($selectedCodex)) {
    $package = Get-AppxPackage -Name OpenAI.Codex |
        Sort-Object Version -Descending |
        Select-Object -First 1
    if ($null -eq $package) {
        throw "The installed OpenAI Codex Windows package was not found."
    }

    $packagedCodex = Join-Path $package.InstallLocation "app\resources\codex.exe"
    if (-not (Test-Path -LiteralPath $packagedCodex -PathType Leaf)) {
        throw "The installed Codex App Server executable was not found at $packagedCodex."
    }

    $temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "joydex-webrtc-canary\$($package.Version)"
    $selectedCodex = Join-Path $temporaryRoot "codex.exe"
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null

    $sourceHash = (Get-FileHash -LiteralPath $packagedCodex -Algorithm SHA256).Hash
    $copyHash = if (Test-Path -LiteralPath $selectedCodex -PathType Leaf) {
        (Get-FileHash -LiteralPath $selectedCodex -Algorithm SHA256).Hash
    } else {
        $null
    }

    if ($sourceHash -ne $copyHash) {
        Copy-Item -LiteralPath $packagedCodex -Destination $selectedCodex -Force
        $copyHash = (Get-FileHash -LiteralPath $selectedCodex -Algorithm SHA256).Hash
        if ($sourceHash -ne $copyHash) {
            throw "The temporary Codex executable did not match the installed package."
        }
    }
}

if (-not (Test-Path -LiteralPath $selectedCodex -PathType Leaf)) {
    throw "The selected Codex executable was not found at $selectedCodex."
}

$codexVersion = (& $selectedCodex --version 2>&1) -join " "
if ($LASTEXITCODE -ne 0) {
    throw "Could not read the selected Codex version: $codexVersion"
}

$devServerTool = Join-Path $env:USERPROFILE ".agents\skills\local-dev-servers\scripts\devservers.py"
if (-not (Test-Path -LiteralPath $devServerTool -PathType Leaf)) {
    throw "The local development-server registry was not found at $devServerTool."
}

$project = Join-Path $repositoryRoot "tools\Joydex.WebRtcCanary\Joydex.WebRtcCanary.csproj"
$dotnet = Join-Path $repositoryRoot ".tools\dotnet\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "The repository-local .NET SDK was not found at $dotnet."
}

$url = "http://localhost:$Port/"
$command = @(
    "start",
    "--name", "joydex-webrtc-canary",
    "--reason", "Host-only Codex App Server WebRTC audio and lifecycle canary",
    "--lifetime", "session",
    "--cwd", $repositoryRoot,
    "--port", "$Port",
    "--url", $url,
    "--health-url", "${url}health",
    "--audience", "local-user",
    "--wait", "30",
    "--",
    $dotnet, "run", "--project", $project, "--",
    "--port", "$Port",
    "--codex-path", $selectedCodex,
    "--codex-version", $codexVersion,
    "--attestation-mode", $AttestationMode,
    "--thread-title", $ThreadTitle
)

if (-not [string]::IsNullOrWhiteSpace($ThreadId)) {
    $command += @("--thread-id", $ThreadId)
}

if ($EphemeralThread) {
    $command += "--ephemeral-thread"
}

if (-not [string]::IsNullOrWhiteSpace($InputWav)) {
    if (-not (Test-Path -LiteralPath $InputWav -PathType Leaf)) {
        throw "The WebRTC input WAVE file was not found at $InputWav."
    }

    $command += @("--input-wav", (Resolve-Path -LiteralPath $InputWav))
}

if (-not [string]::IsNullOrWhiteSpace($CaptureWebm)) {
    $command += @("--capture-webm", [System.IO.Path]::GetFullPath($CaptureWebm))
}

if ($NoOpen) {
    $command += "--no-open"
}

& python $devServerTool @command
if ($LASTEXITCODE -ne 0) {
    throw "The WebRTC canary host failed to start."
}

Write-Host "WebRTC canary: $url"
