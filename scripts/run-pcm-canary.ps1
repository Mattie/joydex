[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InputWav,

    [Parameter(Mandatory)]
    [string]$OutputWav,

    [string]$CodexPath,

    [string]$ThreadTitle = "Codex Voice Chat",

    [string]$ThreadId
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

    $temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "joydex-pcm-canary\$($package.Version)"
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

if (-not (Test-Path -LiteralPath $InputWav -PathType Leaf)) {
    throw "The PCM input WAVE file was not found at $InputWav."
}

$codexVersion = (& $selectedCodex --version 2>&1) -join " "
if ($LASTEXITCODE -ne 0) {
    throw "Could not read the selected Codex version: $codexVersion"
}

$project = Join-Path $repositoryRoot "tools\Joydex.WebRtcCanary\Joydex.WebRtcCanary.csproj"
$dotnet = Join-Path $repositoryRoot ".tools\dotnet\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "The repository-local .NET SDK was not found at $dotnet."
}

$command = @(
    "run", "--project", $project, "--",
    "--mode", "pcm",
    "--codex-path", $selectedCodex,
    "--codex-version", $codexVersion,
    "--thread-title", $ThreadTitle,
    "--input-wav", (Resolve-Path -LiteralPath $InputWav),
    "--output-wav", [System.IO.Path]::GetFullPath($OutputWav)
)

if (-not [string]::IsNullOrWhiteSpace($ThreadId)) {
    $command += @("--thread-id", $ThreadId)
}

& $dotnet @command
if ($LASTEXITCODE -ne 0) {
    throw "The PCM canary failed with exit code $LASTEXITCODE."
}
