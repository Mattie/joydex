[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# Use the normal tray's published package and configuration. Never guess a build directory.
$profilePath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Joydex\profile.yaml'
$fields = @{}
foreach ($line in Get-Content -LiteralPath $profilePath) {
    if ($line -match "^([a-z_]+):\s+'(.*)'\s*$") {
        $fields[$Matches[1]] = $Matches[2].Replace("''", "'")
    }
}
$appPath = [IO.Path]::GetFullPath([string]$fields.application_path)
$configurationPath = [IO.Path]::GetFullPath([string]$fields.configuration_path)
if ([IO.Path]::GetFileName($appPath) -ne 'Joydex.App.exe' -or
    !(Test-Path -LiteralPath $appPath -PathType Leaf) -or
    !(Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
    throw 'The Joydex profile must identify an existing application and configuration.'
}
if (Get-Process -Name 'Joydex.App' -ErrorAction SilentlyContinue) {
    throw 'Joydex is already running. Shut down its tray before using this launcher.'
}

# Obtain the automation object hosted by the existing Explorer desktop, rather than
# creating the child in the calling terminal's process tree or Windows job.
# https://devblogs.microsoft.com/oldnewthing/20131118-00/?p=2643
$shell = New-Object -ComObject Shell.Application
$windows = $shell.Windows()
$desktopHandle = 0
$desktop = $windows.FindWindowSW(0, 0, 8, [ref]$desktopHandle, 1)
if ($null -eq $desktop) {
    throw 'The Windows Explorer desktop is unavailable; no process was launched.'
}
$arguments = '--config "' + $configurationPath + '"'
$desktop.Document.Application.ShellExecute(
    $appPath, $arguments, [IO.Path]::GetDirectoryName($appPath), 'open', 0)

$deadline = (Get-Date).AddSeconds(15)
do {
    $app = Get-CimInstance Win32_Process -Filter "Name = 'Joydex.App.exe'" |
        Where-Object { $_.ExecutablePath -eq $appPath } | Select-Object -First 1
    if ($app) {
        $parent = Get-CimInstance Win32_Process -Filter "ProcessId = $($app.ParentProcessId)"
        if ($parent.Name -ne 'explorer.exe' -or $parent.SessionId -ne $app.SessionId) {
            throw 'Joydex started, but its parent could not be verified as the Explorer desktop.'
        }
        $broker = Get-CimInstance Win32_Process -Filter "Name = 'Joydex.SecretsHost.exe'" |
            Where-Object { $_.ParentProcessId -eq $app.ProcessId } | Select-Object -First 1
        if ($broker) {
            [pscustomobject]@{
                ApplicationPath = $appPath
                AppProcessId = $app.ProcessId
                ParentProcess = $parent.Name
                ParentProcessId = $parent.ProcessId
                SecretsBrokerProcessId = $broker.ProcessId
            }
            return
        }
    }
    Start-Sleep -Milliseconds 250
} while ((Get-Date) -lt $deadline)
throw 'Explorer was asked to launch Joydex, but the tray and Secrets broker were not both ready within 15 seconds.'
