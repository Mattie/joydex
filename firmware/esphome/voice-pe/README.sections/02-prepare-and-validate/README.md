## Prepare and validate without touching the device

From this directory:

```powershell
.\prepare-canary.ps1 -SecretsPath ..\secrets.yaml

py.exe -3.12 -m venv ..\.venv
..\.venv\Scripts\python.exe -m pip install --requirement .\requirements.txt
..\.venv\Scripts\esphome.exe config ..\..\..\.tools\voice-pe-canary\joydex-voice-pe-canary.yaml
```

`esphome config` resolves and validates configuration only. A later `compile` remains host-only, but it downloads toolchains and produces a device-specific firmware image:

```powershell
$gitPatchDirectory = Join-Path $env:ProgramFiles 'Git\usr\bin'
if (-not (Test-Path -LiteralPath (Join-Path $gitPatchDirectory 'patch.exe'))) {
    throw 'ESPHome micro-opus compilation requires patch.exe from Git for Windows.'
}
$env:Path = "$gitPatchDirectory;$env:Path"

..\.venv\Scripts\esphome.exe compile ..\..\..\.tools\voice-pe-canary\joydex-voice-pe-canary.yaml
```

Git for Windows ships the `patch.exe` required by the pinned ESPHome `micro-opus` component, but its `usr\bin` directory is not normally on the Windows `PATH`. The scoped `PATH` addition above applies only to the current PowerShell process.

The reviewed canary completed a host-only compile on 2026-08-23 with ESPHome `2025.12.2`. PlatformIO reported 3,055,883 bytes of flash (37.6% of the configured application capacity) and 44,272 bytes of RAM (13.5%). The ignored build artifacts were:

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| `firmware.bin` | 3,056,288 | `ABCB6BF09BA441A2355ACBC5ECA8242AAEEAF9C7FFD55C1181CB7372DD691F09` |
| `firmware.ota.bin` | 3,056,288 | `ABCB6BF09BA441A2355ACBC5ECA8242AAEEAF9C7FFD55C1181CB7372DD691F09` |
| `firmware.factory.bin` | 3,121,824 | `80B435255172B308E8AFB662891116DAA3ED5DA69D80C06036FA1D67292507B3` |

These hashes identify this local verification build only. Rebuilding can legitimately change them because the firmware embeds build metadata and local configuration. The binaries remain under ignored `.tools` storage and are not release artifacts. The verified build above is currently at `.tools\voice-pe-canary-reviewed\.esphome\build\home-assistant-voice\.pioenvs\home-assistant-voice\firmware.bin`.
