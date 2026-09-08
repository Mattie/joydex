# Joydex Voice PE firmware

This directory contains the supported, source-only firmware recipe for Joydex
Room Voice on a Home Assistant Voice Preview Edition. It provides exclusive
`Okay Computer` wake handling, LAN microphone and speaker transport, runtime
wake tuning, physical session controls, and separate wake, ready, and hangup
cues.

Compiled firmware and credentials are intentionally excluded. The build pins
its upstream sources, verifies reviewed hashes, applies the checked-in patches,
and never connects to or updates a device.

## Build

Use [`Build-JoydexVoicePe.ps1`](Build-JoydexVoicePe.ps1). The other preparation
scripts are its internal source layers, not alternate build or deployment paths.

Prerequisites:

- Windows and PowerShell 7.2 or later.
- Python and Git for Windows, including `patch.exe` under `usr\bin`.
- A local ESPHome virtual environment using the pinned
  [`requirements.txt`](requirements.txt).
- A private secrets file based on [`secrets.example.yaml`](secrets.example.yaml).

From the repository root:

```powershell
py -m venv .\firmware\esphome\.venv
.\firmware\esphome\.venv\Scripts\python.exe -m pip install `
  -r .\firmware\esphome\voice-pe\requirements.txt

Copy-Item .\firmware\esphome\voice-pe\secrets.example.yaml `
  .\firmware\esphome\voice-pe\secrets.yaml
# Edit the ignored secrets.yaml locally before continuing.

.\firmware\esphome\voice-pe\Build-JoydexVoicePe.ps1 `
  -SecretsPath .\firmware\esphome\voice-pe\secrets.yaml `
  -ValidateOnly

.\firmware\esphome\voice-pe\Build-JoydexVoicePe.ps1 `
  -SecretsPath .\firmware\esphome\voice-pe\secrets.yaml
```

The wrapper recreates separate ignored default directories for validation and
compilation. If `-Destination` is supplied, it must be a fresh, dedicated,
disposable directory. A successful build prints the OTA and factory image
paths, but does not flash them. Device installation, rollback, and recovery are
attended operations outside this repository's supported build command.

## Runtime behavior

- `WakeBlip.flac` acknowledges a recognized wake word.
- `ReadyBlip.flac` plays after Codex Realtime and both media paths are ready.
- `EndBlip.flac` plays after a session that reached Listening or Muted ends.
- A short center-button press ends an active session.
- Holding the center button for one second toggles mute. Muted sessions use a
  yellow ring; active unmuted sessions use white.
- **Configure → Room Voice** can update the wake threshold without rebuilding
  firmware.

Room Voice media and control traffic are plaintext by design. Use the firmware
only on a trusted LAN. The normal device web page, including its updater, is
unauthenticated. The fallback recovery access point uses the configured
`web_server_password` secret; that legacy key name no longer describes the
normal web page.

## Research retained in the supported design

The investigation produced many one-off firmware images, upload gates,
regression scripts, and transport prototypes. Those artifacts are not part of
the supported source tree. The durable conclusions are:

- Run only the `Okay Computer` model and VAD. The earlier multi-model setup
  exceeded the Voice PE tensor arenas; the supported build also pins
  `esp-nn 1.1.2`.
- Keep microphone and control traffic on Joydex's port `8765` connection, but
  use clocked Sendspin Opus on port `8927` for speaker playback. Sustained raw
  PCM downlink failed below the host transport boundary.
- Keep one primed, call-scoped Sendspin stream with bounded startup and drain.
  Fixed settle delays, playback-ready acknowledgements, task-priority changes,
  and Wi-Fi lease/power experiments did not independently make repeated duplex
  playback reliable.
- Preserve complete microphone frames across callback batches and scope queued
  work to the current socket generation. Discarding ordinary callback
  remainders caused deterministic microphone loss even without network stalls.
- Pause wake inference while a voice session owns the microphone, then rearm it
  only after the LAN audio session has closed.
- The modern Sendspin worker needs its larger stack in internal RAM. The device
  also disables the unused native-API reboot watchdog because Joydex, rather
  than Home Assistant, owns the endpoint.

The decision trail and failure evidence live in
[`ADR 0003`](../../../docs/adr/0003-use-sendspin-opus-for-voice-pe-speaker-downlink.md)
and
[`ADR 0004`](../../../docs/adr/0004-stage-modern-sendspin-player-behind-the-legacy-media-source-abi.md).
The current host compatibility checks remain in
`tools/Joydex.SendspinModernCanary` and
`tools/Joydex.SendspinProductionCanary`.

## Licensing

The cue files are first-party MIT assets; their formats and roles are described
in [`sounds/README.md`](sounds/README.md). This subtree otherwise combines MIT,
GPLv3, and Apache-2.0 material. See [`LICENSE.md`](LICENSE.md) for the exact
boundaries and pinned upstream revisions.
