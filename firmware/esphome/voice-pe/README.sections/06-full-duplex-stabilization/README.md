## Full-duplex stabilization (`0.1.17`–`0.1.18`)

Version `0.1.17` added speaker-acceptance, backpressure, partial-write, queue-high-water, and maximum-loop-gap counters. It also pauses microWakeWord inference during `Starting` and `Listening` while retaining the shared microphone source for the LAN bridge. That removed wake inference from the active call, but a correctly paced barge-in test still produced sustained speaker backpressure. The earlier apparently clean trial had been paced at roughly 31 ms per 20 ms frame by the Windows timer quantum and was therefore not a valid full-rate test.

An isolation run on `0.1.17` disabled barge-in while leaving downlink unchanged. At a measured 19.396 ms average send interval, all 200 frames and 192,000 bytes were accepted with zero drops, backpressure events, or partial writes. Re-enabling microphone uplink reproduced the failure. The custom uplink task was priority `2`, the ESPHome resampler task was priority `1`, and the otherwise identical barge-in-off path was clean, isolating uplink-task preemption as the device-side cause.

Version `0.1.18` lowers the uplink task to priority `1` so FreeRTOS time-slicing can share the core with the resampler, and increases its empty-ring poll from 5 ms to 10 ms. The wake profile remains gain `4`, cutoff `0.35`, five-frame window, and VAD `0.10`; both audio directions retain 400 ms queues. The generated image contains only HTTP web OTA, retains the passworded fallback AP, and reports ESP32-S3, 16 MB DIO flash, a valid checksum, and validation hash `874022AD0A683489E3562920CE55CB424AFD2EEDE330C1D7C1ADA59966676479`.

| Deployed `0.1.18` artifact | Bytes | SHA-256 |
|---|---:|---|
| `firmware.bin` / `firmware.ota.bin` | 3,064,512 | `3A25C0226A86EE847BCB6FFC0ADE981C9A4A15781FA0A5B9AC572C3714D03EF8` |

The OTA safety gate used the credential file from the previously successful `0.1.12` workspace. Its whole-file hash remained `<redacted-device-credential-file-hash>`, differed from the example template, contained no placeholder values, matched the prepared file and generated station configuration without printing values, and was copied fresh before the final compile. The accepted endpoint rejoined as `joydex-voice-pe`, MAC `02:00:00:00:00:01`, project `Joydex.Voice PE Audio Bridge` version `0.1.18`, ESPHome `2025.12.2`, Armed, with wake inference running and barge-in preserved on.

The corrected `tools/Joydex.VoicePeAudioCanary` sends a deterministic low-level 600 Hz tone, measures microphone frames only inside the speaker-send window, uses a thread-safe log queue, scales its timeout with the requested duration, and disposes the transport before serializing results. An external native-API harness explicitly paused wake inference, verified microphone capture, forced each barge-in state, and compared device-counter deltas:

Run that paired harness with the ESPHome diagnostic environment. It requires the exact endpoint identity and firmware version, restores Armed/barge-in-on in a `finally` path, and exits nonzero if the counter or duplex gates fail:

```powershell
.\.tools\esphome-venv\Scripts\python.exe `
  tools\Joydex.VoicePeAudioCanary\run_device_canary.py `
  --host 192.0.2.10 `
  --expected-name joydex-voice-pe `
  --expected-mac 02:00:00:00:00:01 `
  --expected-version 0.1.18
```

| Physical `0.1.18` run | Speaker frames / bytes | Downlink drops | Backpressure / partial writes | Concurrent microphone frames | Rearmed |
|---|---:|---:|---:|---:|---|
| Barge-in off control | 200 / 192,000 | 0 | 0 / 0 | 0 | Yes |
| Barge-in on full duplex | 200 / 192,000 | 0 | 0 / 0 | 222 | Yes |

The device reported 258 uplink frames and zero uplink dropped bytes during the full-duplex run. The user spoke during its second tone and heard slight but bearable static; transport remained lossless, so that observation is retained as an acoustic/audio-quality follow-up rather than a queue-starvation failure.

Joydex now scopes Windows timer resolution to 1 ms while the real speaker playout loop runs, never sends catch-up bursts after an oversleep, and serializes clear/dequeue against an in-flight send. All 568 main and 29 wireless-panel tests pass. The published host canary is `artifacts\Joydex\voice-owner-timer-pacing-win-x64`; after its one warned restart it reacquired the same dedicated task, restored seven task-alert assignments, reconnected both VIRPIL controllers, and resumed direct task LEDs. Wake-driven native Voice, interruption, and spoken-hangup remain the final attended check.
