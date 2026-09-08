### Deployed and rejected `0.1.30` Wi-Fi always-on canary

Candidate `0.1.30` keeps the exact `0.1.29` Sendspin/Joydex sources, task priorities, protocol,
buffers, wake tuning, cues, LEDs, and recovery behavior. Its only runtime configuration change is
`wifi.power_save_mode: none`. This plugged-in room endpoint therefore bypasses all Wi-Fi
high-performance request/release transitions while leaving the suspected audio path unchanged.

The two-pass build verified ESPHome `2025.12.2`, station and passworded recovery-AP credential
parity, project identity, generated `WIFI_POWER_SAVE_NONE`, pinned prepared/generated component
hashes, task priorities, and ESP32-S3 image integrity. The result is frozen under its OTA hash:

| Prepared `0.1.30` artifact | Bytes | SHA-256 | Validation hash |
|---|---:|---|---|
| `firmware.ota.bin` | 3,109,808 | `AEEF8552B5DEF5459A0868DA5A83F89D5AEE1D5287A3AC776C4FE5BCFE2D0685` | `37EE0C96FECFE9FEC7B313567BA89561D4444229C0748764E96F7FE9A5B13E0E` |
| `firmware.factory.bin` | 3,175,344 | `7B12F3DB8003FC6F437761FF0F14A0866E1EF6437230E597AC49979C9AB33CF9` | — |

Its manifest is
`.tools\voice-pe-wifi-always-on-0.1.30-staged\AEEF8552B5DEF5459A0868DA5A83F89D5AEE1D5287A3AC776C4FE5BCFE2D0685\manifest.json`
with SHA-256 `972E4D1858C63CB338781019E36A3C3B125DDC94C2F8C29BA84B28632E7B0922`.
`upload-staged-wifi-always-on-canary.ps1` pins that manifest, both payloads, every relevant source
hash and priority, selected credential source, exact current `0.1.29` identity, recovery and service
endpoints, and the exact frozen `0.1.29` rollback image. It refused the old keyword and a false
recovery acknowledgement, then passed its complete live preflight-only path without uploading.
The separate `rollback-staged-wifi-always-on-canary.ps1` pins that frozen rollback and accepts only
an exact live `0.1.30` target plus a fresh `ROLLBACK129OTA` authorization. `ALWAYSONOTA` authorized
the exact staged upload. The endpoint rejoined as `0.1.30`, cleared its safe-boot window, returned
Armed with wake and microphone on, mute off, barge-in on, zeroed audio counters, and all four service
ports healthy. Joydex and Guardian retained their original responsive processes. The rollback
wrapper then passed its complete live preflight-only path against `0.1.30`.

The direct split regression rejected the candidate before natural-call testing. With the normal
two-second inter-stream delay, the 20-second withheld-clock setup added zero late chunks and the
following 45-second normal stream added 565. A clean restart reset every boot-local counter. A
second run with a ten-second inter-stream delay also failed: the setup added 104 late chunks and the
following stream added 191. Both runs started one playback task per phase, stayed Armed, and added
zero initial-late chunks, microphone drops, send stalls/failures, or forced closes. A second restart
returned `0.1.30` to a clean Armed state.

Configured Wi-Fi power saving and a short teardown interval are therefore ruled out as complete
remedies. The variable late-chunk distribution focuses the next investigation on host timestamp
pacing and the pinned device speaker synchronizer/scheduler lifecycle. The first two failure records
did not retain host maximum-lateness metrics, so they cannot separate those boundaries by themselves;
the harness now includes those metrics in both success and failure output. The device remains on
clean `0.1.30`; restoring frozen `0.1.29` requires fresh exact `ROLLBACK129OTA` authorization.

The exact `0.1.26` rollback OTA image remains at
`.tools\voice-pe-audio-bridge-0.1.26-repro\.esphome\build\home-assistant-voice\.pioenvs\home-assistant-voice\firmware.ota.bin`,
3,108,624 bytes with SHA-256
`29E6913D66DBBD51A43A13922D73B81BF82F580A2904F46D80F0C35C4737E73F` and validation hash
`2D292BF95FA2C785ACCB8AB4482C6702EFF148D42A2DBB1FA8ED635EFF6E806C`. Exact `0.1.23` remains a
secondary known-working fallback, not the faithful revert from this canary.

