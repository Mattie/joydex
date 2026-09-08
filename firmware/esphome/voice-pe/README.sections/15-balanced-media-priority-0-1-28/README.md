### Deployed `0.1.28` balanced-media-priority canary

Candidate `0.1.28` places the three always-hot media workers in one priority-`18` scheduling class:
the Sendspin player, Sendspin WebSocket HTTPD server, and Joydex microphone/control HTTPD server.
ESP-IDF can then time-slice those equal-priority ready tasks while leaving the I²S microphone and
speaker tasks above them at priorities `23` and `19`. The Joydex uplink feeder remains at priority
`1`, and lwIP also runs at priority `18`. ESP-IDF time-slicing is best-effort, so feeder starvation,
network jitter, and watchdog pressure remain physical-canary risks. No protocol, wake tuning, cue,
LED, buffer, stream-lifecycle, or recovery behavior changes.

The guarded preparation applies only the reviewed priority patches to the pinned components and is
idempotent. The build verifies ESPHome `2025.12.2`, exact network-credential parity without printing
values, tracked/prepared/generated source hashes, and all three generated priority uses. It performs
a preflight compile and generated-credential check, compiles once more for the final payload, repeats
all postflight parity and image checks, and freezes that exact result in a content-hash directory with
an immutable manifest. Rebuilds may embed different build metadata and receive a different identity.
The final local staging pass completed on 2026-08-28 without contacting the device:

| Deployed `0.1.28` artifact | Bytes | SHA-256 | Validation hash |
|---|---:|---|---|
| `firmware.ota.bin` | 3,109,808 | `5E929769626BC438D1119DA70E1FFFAEC574E2F4CA2D4F363C6DD3A17679D948` | `50EAD7835E9F87AACD59321BB90D9FDF4AA0E3D23FEEEECCD804632E261D879E` |
| `firmware.factory.bin` | 3,175,344 | `FD852C21DE90D9681F08013215C87FD189A3EEF0407CF58BCD45BCA607D58433` | — |

Image inspection reports ESP32-S3, 16 MB DIO flash, a valid checksum and validation hash, 45,632
bytes of static RAM, and 3,109,399 bytes of application flash. `0.1.28` was deployed as an attended
canary and is rejected unchanged by its first natural call. The session stayed booted for 93 seconds
and ended by Spoken Hangup. Joydex continued receiving completed user transcripts and generating
assistant audio after one minute; its one Sendspin stream had a queue high-water mark of two and
zero host overflow. The device nevertheless discarded 2,214 late chunks, dropped 71,424 microphone
bytes, filled its 12,800-byte uplink queue, and recorded five sends stalled up to 717 ms. Equal media
priority avoided a reboot without restoring reliable full-duplex scheduling.

A post-call 45-second speaker-only control then isolated the failure from microphone traffic. The
host sent 2,250 silent 20 ms Opus chunks at a 1,000 ms lead; maximum host send lateness was 40 ms and
the maximum interval was 59 ms. The device discarded 1,282 chunks while uplink frames, drops, and
stalls remained unchanged. Cold controls later showed that this was persistent state left by an
earlier session: after reboot, two sequential copies added only two late chunks across 4,500 packets.
Matched 60-second Sendspin controls with 50 seconds of microphone overlap completed at both 60 ms
and 20 ms Opus packet durations with zero late chunks, zero microphone loss/stalls, and clean rearm.
Packet duration alone therefore does not explain or remedy the failure.

The pinned Sendspin close callback contains a concrete Wi-Fi lease bookkeeping defect. When a
clock-sync high-performance request is outstanding, close releases it and then assigns its request
flag `true` instead of `false`. On the next stream, the first clock response can consequently release
the playback lease and restore light Wi-Fi power saving while audio is active. A new host-canary mode
confirmed this without changing firmware: it withheld exactly one final `server/time` response only
after a 1,000-chunk stream had drained, then closed normally. The following ordinary speaker-only
stream discarded 1,275 of 2,250 chunks with no microphone connection and zero initial skips. A reboot
cleared the condition. Firmware `0.1.28` remains rejected, now for both its fresh-boot natural-call
result and this confirmed cross-session lease corruption.

The upload-eligible manifest is
`.tools\voice-pe-sendspin-balanced-0.1.28-staged\5E929769626BC438D1119DA70E1FFFAEC574E2F4CA2D4F363C6DD3A17679D948\manifest.json`,
1,285 bytes with SHA-256
`BA2B78FD0F699C48D196047F11B8CC93D7D9589BD84F06FD59F2D916511C6205`.
An attended OTA must consume the artifact named by this manifest without rebuilding it.
`upload-staged-sendspin-balanced-canary.ps1` fails closed unless the manifest, selected known-working
secrets, exact OTA and `0.1.26` rollback images, image geometry/checksums, recovery acknowledgement,
device web endpoint, and exact `BALANCEDOTA` authorization all pass. Its complete preflight-only mode
passed against the live `0.1.27` endpoint without uploading. The first authorized write attempt then
failed before sending bytes because PowerShell's multipart helper would not open the read-only staged
image. The wrapper now uses `curl` only for the final multipart transfer while preserving every
preflight gate. Its second authorized run returned `Update Successful!` for the exact staged image.

The endpoint rejoined as `joydex-voice-pe`, MAC `02:00:00:00:00:01`, project
`Joydex.Voice PE Audio Bridge` version `0.1.28`, ESPHome `2025.12.2`. It cleared the safe-mode boot-loop
counter after 60 seconds, returned Armed with wake inference and microphone capture on, mute off and
barge-in on, and exposed zero playback-task, late-chunk, microphone-loss, send-stall/failure,
receive-failure, and forced-close counters. HTTP recovery, native API, microphone/control port `8765`,
and Sendspin port `8927` all respond. Joydex.App and Joydex.Guardian retained their original processes
and remained responsive. The later natural-call result above rejected physical acceptance.

The rejected host-only canary package
`artifacts\Joydex\response-idle-sendspin-win-x64` keeps one Sendspin stream and decoder task alive
for the Voice Session, sends 20 ms packets only while an assistant response is active, and pauses
cadence at ordered stopped/cleared boundaries. Short source underruns inside a response still receive
silence padding. Although all 654 main and 29 wireless-panel tests passed, those tests did not model
the pinned device synchronizer retaining correction history across an idle timestamp discontinuity.
`IDLEGAPCANARY` physically rejected the package: its 46-second call sounded worse, discarded 311 of
753 transmitted chunks, dropped 50,816 microphone bytes, and recorded three stalls up to 1,017 ms.
Joydex was rolled back to `call-scoped-sendspin-win-x64`; its Guardian, four task-alert assignments,
direct LEDs, M2, both controllers, Dedicated Voice Task, and Armed device state recovered.
