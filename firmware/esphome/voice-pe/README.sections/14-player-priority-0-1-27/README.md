### Deployed `0.1.27` Sendspin player-priority canary

The natural acceptance call on deployed `0.1.26` began clearly, then audibly degraded during its
third sustained assistant response. Joydex accepted and paced all four assistant responses without
source-frame loss, queue overflow, or abnormal host timing. During that third response, the device
logged five microphone WebSocket-worker stalls, including one 858 ms stall, filled its 12,800-byte
uplink queue, and dropped 50,560 microphone bytes. This locates the observed failure below the host
speaker boundary and in the device's shared full-duplex execution path.

The exact pinned legacy Sendspin source has a priority inversion: its WebSocket HTTPD task runs at
priority `17`, while `xTaskCreateStatic()` creates the decode/player task at literal priority `1`.
The nearby `SYNC_TASK_PRIORITY = 5` constant is not used. Upstream ESPHome subsequently fixed the
same starvation pattern by putting the player above the WebSocket server in
[PR #16178](https://github.com/esphome/esphome/pull/16178). A full ESPHome upgrade would also change
the board's wake, audio, and dependency stack, so candidate `0.1.27` narrowly adapts only this
scheduling relationship to the pinned Sendspin commit: the player runs at priority `18`, one level
above WebSocket intake. It retains the accepted call-scoped stream, 1,000 ms lead, startup state
publication, uplink framing, wake tuning, sounds, LEDs, and recovery behavior.

Priority `18` also equals the image's lwIP TCP/IP task and outranks the Joydex microphone HTTPD
worker at default priority `5` and its uplink task at priority `1`; the I²S microphone and speaker
tasks remain above it at `23` and `19`. The change can therefore repair downlink while worsening
microphone or interruption latency. This is an attended-canary risk, and any uplink stall or drop
rejects the candidate.

The patch also exposes playback-task starts, total late audio skips, and initial late audio skips.
`prepare-sendspin-priority-canary.ps1` pins the exact component commit, checks stock and patched
source hashes, applies the patch idempotently, and rejects unrelated component changes.
`build-sendspin-priority-canary.ps1` additionally verifies ESPHome `2025.12.2`, network-credential
parity, tracked/prepared/generated Joydex and Sendspin parity, generated priority use, and both image
artifacts. Two guarded local builds passed. `PRIORITYOTA` authorized a fresh post-parity build and
attended deployment on 2026-08-28. The same device identity returned as version `0.1.27`, all four
recovery/service ports returned, HTTP recovery returned 200, ESPHome cleared its boot-loop counter
after 60 seconds, and the new task/skip counters plus all existing audio failure counters started at
zero. Joydex and its Guardian remained responsive.

| Deployed `0.1.27` artifact | Bytes | SHA-256 | Validation hash |
|---|---:|---|---|
| `firmware.ota.bin` | 3,109,792 | `0375A72027892CB1ABA7206B207621411E16FDF6CBFE6085278E360432164C26` | `34A21CFA90CD550A05F2A15688B79E5637D72D57814CD199CA4215BDCF846942` |
| `firmware.factory.bin` | 3,175,328 | `658225C894A190754260F08676FCD37175C8A035BC0FADB48A1A0BF957FB200F` | — |

Image inspection reports ESP32-S3, 16 MB DIO flash, a valid checksum and validation hash, 45,632
bytes of static RAM, and 3,109,387 bytes of application flash.

The first attended `0.1.27` conversation stayed usable for about one minute and closed cleanly by
Spoken Hangup after 89 seconds. Joydex received every Realtime transcript and produced six completed
assistant responses; its call-scoped Sendspin stream sent 3,776 frames, including 2,730 silence
frames. The device nevertheless skipped 771 late Sendspin chunks. Its microphone queue reached the
full 12,800-byte bound, dropped 99,712 bytes, and recorded seven HTTPD-worker stalls with a 1,284 ms
maximum send. There was no device reboot, host transport failure, or Joydex/Guardian failure. These
counters reject `0.1.27` as the final remedy and locate the perceived silence in device media-task
scheduling.

The upstream priority-`18` player fix assumes the ring buffer periodically empties and lets the
lower-priority WebSocket server run. Joydex deliberately keeps one stream alive for the whole Voice
Session and sends Opus silence between responses, so that yield condition does not occur. On
`0.1.27`, the always-runnable player can therefore starve both the priority-`17` Sendspin WebSocket
worker and the default-priority-`5` Joydex microphone HTTPD worker.

