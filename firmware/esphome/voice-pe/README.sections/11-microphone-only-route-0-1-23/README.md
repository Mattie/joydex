### Rejected `0.1.22` settle delay and deployed `0.1.23` microphone-only route

Repeated natural conversations exposed a second-session failure around interruption or teardown.
Device logs showed the custom raw-PCM resampler stop, restart, and then lose its shared mixer when
the downstream asynchronous STOP completed after the queued START. Candidate `0.1.22` added a
fixed 250 ms wait. It was not deployed: the delay retained the unused speaker lane and converted a
known ownership error into a timing assumption.

Version `0.1.23` removes that ownership from the production route. The `joydex_lan_audio` component
now supports explicit `uplink_only` mode with an optional speaker. The production overlay creates
no `joydex_voice_mixing_input` or `joydex_voice_resampling_speaker`, allocates no raw downlink
queue, rejects binary downlink frames, and never starts, stops, flushes, or restarts a speaker.
Port `8765` carries only microphone PCM and session control; Sendspin port `8927` alone owns
assistant playback. Raw-PCM duplex remains available as a diagnostic component mode.

Joydex mirrors the same boundary: assistant frames and clear go only to Sendspin, while playback
end has no microphone/control side effect. The production handshake requires
`mode: "uplink_only"` plus `downlink: null`; legacy duplex firmware remains available only to the
diagnostic transport. Eight focused tests cover frame routing, clear, playback end, capability
acceptance, and legacy/ambiguous rejection. All 644 main tests and 29 wireless-panel tests pass.

The isolated build used the exact previously accepted credential file, rejected the example and
placeholder cases, verified resolved station plus passworded fallback-AP parity without printing
values, and compiled only after that gate. Prepared component, overlay, cue, and credentials remain
byte-identical after compilation. Generated C++ enables uplink-only mode, contains project version
`0.1.23`, and neither rejected Joydex speaker ID.

| Deployed `0.1.23` artifact | Bytes | SHA-256 | Validation hash |
|---|---:|---|---|
| `firmware.bin` / `firmware.ota.bin` | 3,105,424 | `21ED34B1E73BB7286E277D9CA98C01D42E95AF9EBE2645DEA47AB5FB0FB1D7E5` | `7C8917EF0086E805C090AF7276D5A83B4C823F8C142B705FD9F279B4CC4A48A9` |

Image inspection reports ESP32-S3, 16 MB flash, DIO mode, a valid checksum, and a valid validation
hash. Static RAM is 45,552 bytes and application flash is 3,105,023 bytes. The expected base-board
strapping-pin warnings and pinned upstream TensorFlow Lite/micro-opus compiler warnings remain.
The attended HTTP OTA returned `Update Successful!` on 2026-08-27. The exact endpoint returned at
`192.0.2.10` with MAC `02:00:00:00:00:01`, project version `0.1.23`, ESPHome `2025.12.2`, Armed,
wake inference and microphone capture on, mute off, and barge-in on. HTTP recovery, API,
microphone/control port `8765`, and Sendspin port `8927` responded; native OTA remained absent by
design. A live port-`8765` probe returned `mode: "uplink_only"` and `downlink: null`. The matching
Joydex package at `artifacts\Joydex\microphone-only-0.1.23-win-x64` then reacquired its dedicated
task, both VIRPIL controllers, four task-alert assignments, and direct LEDs. Natural wake, reply,
interruption, Spoken Hangup, rearm, and repeated-session gates remain attended checks.
