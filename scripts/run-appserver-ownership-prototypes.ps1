[CmdletBinding()]
param(
    [string]$CodexPath,

    [string]$ThreadTitle = "Codex Voice Chat",

    [switch]$Auto
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$selectedCodex = $CodexPath
$usingPinnedDefault = [string]::IsNullOrWhiteSpace($selectedCodex)

if ($usingPinnedDefault) {
    throw "Pass -CodexPath for the previously verified Codex 0.150.0-alpha.9 executable."
}

if (-not (Test-Path -LiteralPath $selectedCodex -PathType Leaf)) {
    throw "The selected Codex executable was not found at $selectedCodex."
}

$codexVersion = (& $selectedCodex --version 2>&1) -join " "
if ($LASTEXITCODE -ne 0) {
    throw "Could not read the selected Codex version: $codexVersion"
}

$codexHash = (Get-FileHash -LiteralPath $selectedCodex -Algorithm SHA256).Hash.ToLowerInvariant()
$pinnedHash = "5ffd7a27694e1529d717a0247858d7650438273ba10d4d8a4f0a73f5e1414082"
if ($usingPinnedDefault -and $codexHash -ne $pinnedHash) {
    throw "The default Codex binary hash is $codexHash; expected the validated 0.150.0-alpha.9 hash $pinnedHash."
}

Write-Host "App Server binary: $selectedCodex"
Write-Host "App Server version: $codexVersion"
Write-Host "App Server SHA-256: $codexHash"

$project = Join-Path $repositoryRoot "tools\Joydex.WebRtcCanary\Joydex.WebRtcCanary.csproj"
$dotnet = Join-Path $repositoryRoot ".tools\dotnet\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "The repository-local .NET SDK was not found at $dotnet."
}

$ownerArguments = @(
    "run", "--project", $project, "--",
    "--mode", "ownership",
    "--codex-path", (Resolve-Path -LiteralPath $selectedCodex),
    "--thread-title", $ThreadTitle
)
if ($Auto) {
    $ownerArguments += "--auto"
}

& $dotnet @ownerArguments
$ownerExitCode = $LASTEXITCODE
if ($ownerExitCode -notin @(0, 3)) {
    throw "JOYDEXOWNER failed with exit code $ownerExitCode."
}

if ($ownerExitCode -eq 3) {
    Write-Host "OWNER-MECHANISM-PASSED; configured task remains owned by another App Server."
} else {
    Write-Host "JOYDEXOWNER-READY; configured task is available to the Joydex App Server."
}

$package = Get-AppxPackage -Name OpenAI.Codex |
    Sort-Object Version -Descending |
    Select-Object -First 1
if ($null -eq $package) {
    throw "The installed OpenAI Codex Windows package was not found."
}

$desktopAppServer = Get-CimInstance Win32_Process |
    Where-Object {
        $_.Name -eq "codex.exe" -and
        $_.ExecutablePath -like "$($package.InstallLocation)*" -and
        $_.CommandLine -like "*app-server*"
    } |
    Select-Object -First 1
if ($null -eq $desktopAppServer) {
    throw "The running Codex Desktop App Server process was not found."
}

$listeners = Get-NetTCPConnection -State Listen -OwningProcess $desktopAppServer.ProcessId -ErrorAction SilentlyContinue |
    ForEach-Object { "$($_.LocalAddress):$($_.LocalPort)" }
$commandLineBase64 = [Convert]::ToBase64String(
    [Text.Encoding]::UTF8.GetBytes($desktopAppServer.CommandLine))

$desktopArguments = @(
    "run", "--project", $project, "--",
    "--mode", "desktop-attach",
    "--desktop-pid", "$($desktopAppServer.ProcessId)",
    "--desktop-command-line-base64", $commandLineBase64
)
foreach ($listener in $listeners) {
    $desktopArguments += @("--desktop-listener", $listener)
}

& $dotnet @desktopArguments
$desktopExitCode = $LASTEXITCODE
if ($desktopExitCode -notin @(0, 2)) {
    throw "DESKTOPATTACH failed with exit code $desktopExitCode."
}

if ($desktopExitCode -eq 2) {
    Write-Host "DESKTOPATTACH completed: the existing Desktop App Server exposes no supported attach endpoint."
}
