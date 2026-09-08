[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,
    [Parameter(Mandatory = $true)]
    [string]$DestinationPath,
    [Alias("TemplateDirectory")]
    [string[]]$TemplateDirectories = @($PSScriptRoot, (Split-Path -Parent $PSScriptRoot))
)

$ErrorActionPreference = "Stop"

function Get-YamlScalar {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Yaml,
        [Parameter(Mandatory = $true)]
        [string]$Key
    )

    $matches = [regex]::Matches(
        $Yaml,
        "(?m)^\s*$([regex]::Escape($Key))\s*:\s*(?<value>.*?)\s*$")
    if ($matches.Count -ne 1) {
        throw "The explicit ESPHome secrets file must contain exactly one $Key value."
    }

    $value = $matches[0].Groups["value"].Value.Trim()
    if (($value.StartsWith('"') -and $value.EndsWith('"')) -or
        ($value.StartsWith("'") -and $value.EndsWith("'"))) {
        $value = $value.Substring(1, $value.Length - 2)
    }

    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "The explicit ESPHome secrets file contains an empty $Key value."
    }

    return $value
}

if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
    throw "The explicit ESPHome secrets file does not exist: $SourcePath"
}
foreach ($templateDirectory in $TemplateDirectories) {
    if (-not (Test-Path -LiteralPath $templateDirectory -PathType Container)) {
        throw "The secrets template directory does not exist: $templateDirectory"
    }
}

$resolvedSourcePath = [System.IO.Path]::GetFullPath($SourcePath)
$resolvedDestinationPath = [System.IO.Path]::GetFullPath($DestinationPath)
$sourceHash = (Get-FileHash -LiteralPath $resolvedSourcePath -Algorithm SHA256).Hash
$templateFiles = @($TemplateDirectories | ForEach-Object {
    Get-ChildItem -LiteralPath $_ -File | Where-Object {
        $_.Name -match '(?i)(example|template).*\.ya?ml$|\.(example|template)\.ya?ml$'
    }
})
if ($templateFiles.Count -eq 0) {
    throw "No ESPHome secrets example or template files were found for comparison."
}
foreach ($templateFile in $templateFiles) {
    $templateHash = (Get-FileHash -LiteralPath $templateFile.FullName -Algorithm SHA256).Hash
    if ($sourceHash -eq $templateHash) {
        throw "The explicit ESPHome secrets file is an example or template file."
    }
}

$sourceText = [System.IO.File]::ReadAllText($resolvedSourcePath)
$wifiSsid = Get-YamlScalar -Yaml $sourceText -Key "wifi_ssid"
$wifiPassword = Get-YamlScalar -Yaml $sourceText -Key "wifi_password"
$recoveryPassword = Get-YamlScalar -Yaml $sourceText -Key "web_server_password"
$placeholderPattern = '(?i)(REPLACE(?:_WITH)?|YOUR(?:_|\b)|CHANGE_ME|EXAMPLE|PLACEHOLDER)'
if ($wifiSsid -match $placeholderPattern -or
    $wifiPassword -match $placeholderPattern -or
    $recoveryPassword -match $placeholderPattern) {
    throw "The explicit ESPHome network or recovery values still contain a preparation placeholder."
}
if ($wifiSsid -ceq $wifiPassword) {
    throw "The ESPHome Wi-Fi SSID and password must differ."
}

$destinationDirectory = Split-Path -Parent $resolvedDestinationPath
if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $destinationDirectory | Out-Null
}
if ($resolvedSourcePath -cne $resolvedDestinationPath) {
    Copy-Item -LiteralPath $resolvedSourcePath -Destination $resolvedDestinationPath -Force
}

$destinationHash = (Get-FileHash -LiteralPath $resolvedDestinationPath -Algorithm SHA256).Hash
if ($sourceHash -ne $destinationHash) {
    throw "The prepared ESPHome secrets file differs from the explicitly selected source."
}

Write-Output "NETWORK CREDENTIAL SOURCE PARITY: PASS"
