# Joydex Voice PE firmware

This is the source-only firmware recipe used by Joydex Room Voice on a Home
Assistant Voice Preview Edition. It provides exclusive `Okay Computer` wake
handling, LAN wake signaling, full-duplex microphone and speaker transport,
runtime wake tuning, physical session controls, and distinct wake, ready, and
hangup cues.

This firmware subtree uses multiple compatible licenses. ESPHome runtime-derived C/C++ code and patches are GPLv3, while Joydex-authored scripts and configuration remain MIT and referenced Sendspin and wake-model projects remain Apache-2.0. See [`LICENSE.md`](LICENSE.md) for the exact boundary and upstream revisions.

This directory does not contain compiled firmware or credentials. The build
recipe fetches exact upstream revisions and applies the checked-in Joydex
overlays. Device-specific upload, rollback, and recovery remain attended
operations.

The three working cue binaries are first-party MIT assets distributed with the source. See
[`sounds/README.md`](sounds/README.md) for their roles, formats, and licensing.

## Supported source build

The supported entry point is [`Build-JoydexVoicePe.ps1`](Build-JoydexVoicePe.ps1).
It prepares the pinned source, validates the final configuration, and optionally
compiles it once. It never connects to or updates a device.

Prerequisites:

- Windows, Python, and Git for Windows with `patch.exe` under `usr\bin`.
- A local ESPHome virtual environment created from the pinned
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

The compiled OTA and factory image paths are printed when the build completes.
Review ESPHome's device-specific installation and recovery guidance before
writing either image. Keep a factory backup and verify the selected device and
credentials before every OTA update.

## Runtime behavior

- `WakeBlip.flac` acknowledges the recognized wake word.
- `ReadyBlip.flac` plays after the Codex and audio session are ready.
- `EndBlip.flac` plays after a session that reached Listening or Muted ends.
- A short center-button press ends an active session.
- Holding the center button for one second toggles mute; muted sessions use a
  yellow ring and active unmuted sessions use white.
- The device exposes wake sensitivity settings that Joydex can read and update
  from **Configure → Room Voice** without rebuilding the firmware.

Room Voice media and control traffic are intentionally plaintext on the local
network. Use this only on a trusted LAN. The normal device web page is
intentionally unauthenticated, including its web updater. The fallback recovery
access point uses the configured `web_server_password` secret. That key name is
retained for compatibility even though it no longer protects the web page.

## Engineering history

The remaining files with `canary`, `prototype`, and `regression` in their names
are retained evidence from the audio and wake-word investigation. They are not
alternate supported installation paths. Start with the wrapper above; consult
the historical sections when diagnosing or adapting the design.

If useful, read the [overview and source-lineage details](README.sections/01-overview/README.md).

## Preparation internals

Documents the layered source preparation used by the supported wrapper. The
older attended release verifier requires a private staged baseline and is not
needed for a normal source build.

If useful, read this [section's technical details](README.sections/02-prepare-and-validate/README.md).

## Physical deployment gate

Sets the recovery-first OTA safeguards: verify image identity, target identity, power, physical recovery, flash geometry, and explicitly selected non-template Wi-Fi secrets before any device write.

If useful, read this [section's technical details](README.sections/03-physical-deployment-gate/README.md).

## Wake-engine diagnosis and single-model correction

Records the tensor-allocation diagnosis, the `esp-nn 1.1.2` compatibility pin, and the progression from failed multi-model inference to the accepted single-model wake profile and its score diagnostics.

If useful, read this [section's technical details](README.sections/04-wake-engine-diagnosis/README.md).

## Joydex LAN audio bridge candidate (`0.1.12`)

Introduces the original custom `joydex_lan_audio` WebSocket bridge: 16 kHz PCM16 microphone uplink, 24 kHz PCM16 speaker downlink, and a dedicated mixer lane layered on the accepted wake and cue behavior.

If useful, read this [section's technical details](README.sections/05-lan-audio-bridge-0-1-12/README.md).

## Full-duplex stabilization (`0.1.17`–`0.1.18`)

Captures the first valid full-rate pacing tests, speaker-backpressure instrumentation, and the conclusion that wake inference could be paused during a call without resolving active full-duplex pressure.

If useful, read this [section's technical details](README.sections/06-full-duplex-stabilization/README.md).

### `0.1.19` socket telemetry and bounded recovery

Adds durable device socket diagnostics and ownership guards, alongside Joydex-side send deadlines and bounded reconnect cycles that prevent a stale transport failure from corrupting a replacement session.

If useful, read this [section's technical details](README.sections/07-socket-telemetry-recovery-0-1-19/README.md).

### `0.1.20` cue-role swap

Moves the wake acknowledgement to the local ready cue and reserves the connected cue for confirmed Listening, avoiding duplicate connected audio on the speaker downlink.

If useful, read this [section's technical details](README.sections/08-cue-role-swap-0-1-20/README.md).

### Sustained-stream finding and `0.1.21` UDP prototype

Shows why short audio gates missed roughly 30-second steady-state failures, then documents the failed UDP/raw-PCM attempt to isolate the custom downlink path.

If useful, read this [section's technical details](README.sections/09-udp-prototype-0-1-21/README.md).

### `0.1.19` hybrid microphone plus Sendspin Opus path

Establishes the hybrid transport that remains architectural context: Joydex keeps microphone/control on port `8765`, while ESPHome's stock Sendspin player takes clocked 48 kHz Opus speaker playback on port `8927`.

If useful, read this [section's technical details](README.sections/10-hybrid-microphone-sendspin/README.md).

### Rejected `0.1.22` settle delay and deployed `0.1.23` microphone-only route

Rejects a fixed-delay fix for the custom speaker lane and makes the production custom component structurally uplink-only, leaving Sendspin as the single owner of speaker lifecycle.

If useful, read this [section's technical details](README.sections/11-microphone-only-route-0-1-23/README.md).

### Rejected `0.1.24` Sendspin playback-readiness canary

Proves that a playback-readiness acknowledgement could not cure missing opening speech, which established a single Sendspin stream per Voice Session and a 1,000 ms scheduling lead.

If useful, read this [section's technical details](README.sections/12-playback-readiness-0-1-24/README.md).

### Rejected `0.1.25` and prepared `0.1.26` microphone-uplink correction

Identifies microphone loss caused by discarding normal callback remainders, then records the queued, one-send-in-flight implementation that preserves complete frames across ordinary batching and socket generations.

If useful, read this [section's technical details](README.sections/13-microphone-uplink-correction-0-1-26/README.md).

### Deployed `0.1.27` Sendspin player-priority canary

Tests raising the pinned Sendspin player above its WebSocket server. The observed microphone stalls and loss show that the always-fed player can starve its network workers during a Voice Session.

If useful, read this [section's technical details](README.sections/14-player-priority-0-1-27/README.md).

### Deployed `0.1.28` balanced-media-priority canary

Places the Sendspin player and both HTTP workers at equal priority to time-slice continuously ready media work. It avoided a reboot while still failing reliable duplex playback and revealing a cross-session poisoned state.

If useful, read this [section's technical details](README.sections/15-balanced-media-priority-0-1-28/README.md).

### Deployed and rejected `0.1.29` Sendspin Wi-Fi lease-fix canary

Applies the narrow correction to Sendspin's clock-sync Wi-Fi lease flag. The regression retained the cross-session late-chunk failure, showing that the bookkeeping fix alone is insufficient.

If useful, read this [section's technical details](README.sections/16-wifi-lease-fix-0-1-29/README.md).

### Deployed and rejected `0.1.30` Wi-Fi always-on canary

Disables Wi-Fi power saving to bypass runtime high-performance lease transitions. Split controls still accumulated late chunks, moving the investigation toward host pacing and the device synchronizer/scheduler lifecycle.

If useful, read this [section's technical details](README.sections/17-wifi-always-on-0-1-30/README.md).

### Deployed and rejected: `0.1.31` modern Sendspin adapter

Replaces only the device-side decoder and synchronizer with `sendspin-cpp` `0.7.2`, preserving the player-v1 protocol, port assignments, and pinned `MediaSource` boundary. The staged `0.1.31` image was deployed and failed its physical acceptance sequence.

The first natural call and two host-driven playback canaries instead made the endpoint panic and
reboot after playback began, including a reproduction with wake inference stopped. The candidate is
rejected; USB serial subsequently identified the stack overflow documented in the next section. Do
not redeploy `0.1.31` unchanged.

If useful, read this [section's technical details](README.sections/18-modern-sendspin-adapter-0-1-31/README.md).

### Corrected modern Sendspin line (`0.1.32`–`0.1.33`)

Records the USB-serial stack-overflow diagnosis, keeps the modern player worker's 6,192-byte stack
in internal RAM, disables the unused native-API client reboot watchdog, and pairs the firmware with
Joydex's primed Sendspin connection and bounded eight-second startup send.

If useful, read this [section's technical details](README.sections/19-modern-sendspin-stabilization-0-1-32-0-1-33/README.md).
