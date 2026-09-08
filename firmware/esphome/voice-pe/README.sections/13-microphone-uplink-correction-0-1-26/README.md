### Rejected `0.1.25` and prepared `0.1.26` microphone-uplink correction

The first natural conversation on the accepted host remedy sounded materially clear across multiple
assistant responses, then the Voice PE rebooted. Joydex, Guardian, and the owned App Server remained
alive. A 90-second continuous-silence duplex soak and a 60-second repeated-speech-plus-silence duplex
soak on unchanged `0.1.23` both completed without reset, so continuous Sendspin silence and ordinary
decoder load do not independently reproduce the failure.

Those controls did expose a separate full-duplex defect. The port-`8765` microphone sender accumulated
21 WebSocket send stalls, reached a 2,488 ms blocked write and the full 12,800-byte uplink queue, and
dropped 236,928 microphone bytes. A subsequent 60-second microphone-only control delivered 3,051
frames without adding one stall or dropped byte. Speaker activity is therefore a necessary condition
for the observed uplink backpressure, even though the rare reboot is not yet reproduced.

Candidate `0.1.25` left the accepted wake tuning, cues, LEDs, uplink-only capability, and stock
Sendspin source unchanged. It kept one persistent-buffer microphone frame queued to the ESP-IDF
HTTP-server worker through `httpd_queue_work()` and `httpd_ws_send_frame_async()`, removing the
synchronous event-group allocation/wait from every frame. `ASYNCUPLINKOTA` authorized its attended
deployment. Exact endpoint identity, credentials, passworded fallback AP, rollback image, ports, and
Joydex processes passed preflight; web OTA returned `Update Successful!`; `0.1.25` rejoined healthy.

The first 60-second quiet microphone canary rejected that image. The device sent 1,897 uplink frames,
dropped 728,448 bytes, reached a 1,024-byte uplink high-water mark, and reported zero send stalls with
a 3 ms maximum send. The arithmetic is exact: `1,897 × (1,024 - 640) = 728,448`. The Voice PE supplies
normal 1,024-byte microphone callbacks while the bridge sends 640-byte 20 ms frames; the unconditional
latest-frame selector therefore discarded the 384-byte partial next frame as if it were stale after
every send. This was deterministic local framing loss, independent of network backpressure. The
attended rollback restored the exact `0.1.23` image and verified its name, MAC, project, version,
Armed state, wake engine, microphone, mute, barge-in, HTTP recovery, native API, port `8765`, Sendspin,
Joydex owner, Guardian, and App Server. `0.1.25` must not be deployed unchanged.

Firmware `0.1.26` keeps normal microphone callback batching FIFO. A successful HTTP-worker send
that takes at least the existing 100 ms stall threshold marks exactly one backlog compaction after
the in-flight frame completes. That compaction removes only complete stale 640-byte frames and
preserves any partial next frame; normal 1,024-byte callbacks never trigger it. A logical-session
generation also prevents queued microphone work from crossing a close/reopen on the same WebSocket.
The physical canary now publishes `Listening` immediately after its media process starts, matching
the production lifecycle instead of timing out in `Starting`.

The canonical isolated workspace is `.tools\voice-pe-audio-bridge-0.1.26-repro`. A clean build and a
second guarded build passed under ESPHome `2025.12.2`. `prepare-audio-canary.ps1` and
`build-audio-canary.ps1` fail closed unless the pinned repositories, tracked/prepared/generated
Joydex component, exact stock Sendspin source, credential parity, ready cue, and overlay all match.
Static RAM is 45,584 bytes and application flash is 3,108,227 bytes.

| Deployed `0.1.26` artifact | Bytes | SHA-256 | Validation hash |
|---|---:|---|---|
| `firmware.ota.bin` | 3,108,624 | `29E6913D66DBBD51A43A13922D73B81BF82F580A2904F46D80F0C35C4737E73F` | `2D292BF95FA2C785ACCB8AB4482C6702EFF148D42A2DBB1FA8ED635EFF6E806C` |
| `firmware.factory.bin` | 3,174,160 | `C5F2AAF7A523AD11F6B28D3E49DC308D1AC4D210BC1E2409968FADC807C51D85` | — |

Image inspection reports ESP32-S3, 16 MB DIO flash, a valid checksum, and a valid validation hash.
`FRAMEALIGNOTA` authorized the fresh build and attended web OTA. The updater returned
`Update Successful!`; the exact endpoint rejoined as `0.1.26`, completed its safe-mode boot window,
and reported Armed, wake and microphone on, mute off, barge-in on, and zeroed audio diagnostics.
HTTP recovery, native API, port `8765`, and Sendspin were reachable; Joydex and Guardian remained
responsive.

The first 60-second microphone-only gate delivered 3,036 frames and 1,943,040 bytes with zero dropped
bytes, send stalls, send failures, receive failures, or forced closes. Its queue high-water mark was
the expected 1,024-byte source callback and maximum send time was 4 ms. The device rearmed cleanly.
A second identical microphone gate overlapped stock Sendspin playback for about 47 seconds while
Sendspin accepted 3,000 clocked 20 ms Opus frames at a 1,000 ms lead. Uplink again delivered 3,036
frames with zero drops, stalls, failures, or forced closes; maximum send time was 7 ms. Sendspin
ended its stream and stopped the media pipeline before closing. The stock endpoint logged its known
error `259` receive diagnostic on that deliberate socket close, after the stream and pipeline had
already ended; it caused no audio-counter change or reboot. The final device is Armed on
`0.1.26` with 6,072 cumulative uplink frames, zero cumulative uplink loss, and no reboot. A natural
multi-turn call must still accept acoustic quality, interruption, Spoken Hangup, and repeated-turn
behavior.

