## Joydex LAN audio bridge candidate (`0.1.12`)

`joydex-voice-pe-audio-canary.yaml` derives from the exact `0.1.11` sound-and-LED profile and leaves its wake path unchanged: gain `4`, wake cutoff `0.35`, five-frame window, VAD cutoff `0.10`, `esp-nn 1.1.2`, WakeBlip, SSE wake pulse, and host-driven session LEDs. It adds a local `joydex_lan_audio` external component and a third mixer lane dedicated to Codex conversation audio.

Joydex connects outbound to `ws://<device>:8765/joydex/audio`. The device sends the exact `wake_word_mic` stream as 16 kHz mono signed PCM16 and accepts 24 kHz mono signed PCM16 for the dedicated resampler/speaker path. Both directions have 400 ms PSRAM-backed queues with oldest-audio eviction and diagnostic counters. Session opening waits for the wake acknowledgement plus a 300 ms playback tail. The remotely exposed **Joydex Audio Barge In** switch defaults off; enabling it allows simultaneous microphone uplink and speaker downlink for an attended XMOS AEC test, but acoustic full-duplex quality is not accepted until that device test passes. The wire contract is in `components/joydex_lan_audio/PROTOCOL.md`.

The isolated preparation command is `prepare-audio-canary.ps1`; its default ignored workspace is `.tools\voice-pe-audio-bridge-0.1.12`. Source/prepared parity and `esphome config` passed. Runtime review then corrected HTTP-server context ownership, reconnect teardown, speaker backpressure, asynchronous resampler startup, acoustic-tail timing, and stale-uplink handling. The initial post-review host-only ESPHome `2025.12.2` compile passed on 2026-08-25, using 45,264 bytes of static RAM and 3,062,267 bytes of application flash.

| Audio-bridge candidate | Bytes | SHA-256 |
|---|---:|---|
| `firmware.bin` / `firmware.ota.bin` | 3,062,672 | `F731E0CC4B8415E79E522471641F239C4D3A65096AC88049DAC19A5898B84CF1` |
| `firmware.factory.bin` | 3,128,208 | `DE97569EA87B6E21542D30EFD3DAB7455950D6C63D2437217A6E7D6308C5AA55` |

These hashes identify the initial local verification build only. The binaries stay under ignored `.tools` storage and are not release artifacts.

The separately authorized **VOICE012OTA** deployment on 2026-08-25 used a new isolated workspace, copied the accepted Wi-Fi secrets whose whole-file SHA-256 was `<redacted-device-credential-file-hash>`, and confirmed the copied file retained that hash before and after compilation. The generated C++ matched the reviewed build exactly after source-location directives and generator comments were removed. Absolute rather than relative `#line` paths account for the deployed build's 76-byte application-code difference. Its ESP32-S3 image reported 16 MB DIO flash, a valid checksum, and validation hash `5B2287BA4AE983B60677C387E352C708A28EA42BC9A127451B433D102727526E`.

| Deployed **VOICE012OTA** build | Bytes | SHA-256 |
|---|---:|---|
| `firmware.bin` / `firmware.ota.bin` | 3,062,752 | `201619A8CA00F470FB6F74B751E2E02FA500402024DF7DE4805AE723A8691219` |
| `firmware.factory.bin` | 3,128,288 | `6DC5C54502D62F640E5EE05B694ED1037CB95BF442CD417E8E566CAF56E84B60` |

Native OTA reached 100%. The endpoint rejoined as project `Joydex.Voice PE Audio Bridge` version `0.1.12`; its log confirmed Wi-Fi, HTTP, native API, native OTA, fallback safe mode, and `ws://<device>:8765/joydex/audio`. The accepted gain `4`, wake cutoff `0.35`, five-frame window, VAD cutoff `0.10`, WakeBlip, and barge-in default OFF were all retained. Joydex then started from the staged owner package, acquired its configured Dedicated Voice Task, and established its persistent device-control connections. The first physical end-to-end audio session remains the attended acceptance gate. Machine-specific endpoint and task identifiers are omitted.

Post-deployment validation exposed a host-side startup stall: Joydex opened `/events` but did not drain ESPHome's initial SSE entity snapshot until after a second REST wake-state request. The larger `0.1.12` snapshot could therefore starve that request. The physical device's stream was then verified to include the wake sensor's current state in its initial snapshot and a periodic ping, so `EspHomeVoicePeTransport` now uses that one ordered stream: the first observed wake state seeds the edge detector without dispatch, and only a later `OFF -> ON` transition starts Voice. All 542 main and 29 wireless tests passed, the owner candidate was republished and relaunched, and its single control stream remained established beyond the former 45-second failure boundary. A direct physical-device WebSocket canary also received the protocol-1 hello with 16 kHz uplink and 24 kHz downlink declarations. The first wake-driven media session and spoken hangup remain the attended acceptance gate.
