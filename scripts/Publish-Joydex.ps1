[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [string] $DotnetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\artifacts\Joydex\win-x64'
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$dotnet = (Get-Command $DotnetPath -ErrorAction Stop).Source

& $dotnet publish (Join-Path $repositoryRoot 'src\Joydex.App\Joydex.App.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --output $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet publish (Join-Path $repositoryRoot 'src\Joydex.HookRelay\Joydex.HookRelay.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --output $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet publish (Join-Path $repositoryRoot 'src\Joydex.Guardian\Joydex.Guardian.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --output $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet publish (Join-Path $repositoryRoot 'tools\Joydex.WirelessPanel.Configure\Joydex.WirelessPanel.Configure.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --output $output
exit $LASTEXITCODE
