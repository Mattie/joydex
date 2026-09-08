### Rejected `0.1.24` Sendspin playback-readiness canary

The deployed `0.1.23` route sometimes played a response cleanly and sometimes lost most of its
opening speech even though Joydex logged every WebRTC frame, zero host overflow, and successful
socket writes. A six-response replay removed Codex and WebRTC from the path: the canary sent all
798 frames from the same preserved assistant PCM, completed six fresh Sendspin starts and ends, and
did not reconnect. An external microphone still captured missing reply prefixes at the physical
speaker. The source PCM itself contained a complete short acknowledgement.

The exact Sendspin component pinned by the retail Voice PE package publishes
`client/state=synchronized` before allocating the playback-task stack or creating that task. The
decoder later rejects an initial audio chunk when its playback deadline is less than 400 ms away;
Joydex targets a 500 ms lead. A slow task start can consume that margin after Joydex has already
received permission to send.

Candidate `0.1.24` kept the same legacy wire protocol, Opus settings, 500 ms lead, uplink-only
microphone lane, wake tuning, sounds, and LED behavior. Its reviewed patch moves the synchronized
state publication to the main-loop observation of `TASK_RUNNING`, after the media source enters
`PLAYING`. It also exposes three cumulative web diagnostics:

- `Joydex Sendspin Playback Tasks Started`
- `Joydex Sendspin Late Audio Chunks Skipped`
- `Joydex Sendspin Initial Audio Chunks Skipped`

The rejected build was prepared by a now-retired variant of `prepare-audio-canary.ps1` that checked
out Sendspin commit `aef3a4afd9c7186751fc9d097742325b3390572f`, applied
`sendspin-playback-ready.patch`, and verified both changed files by SHA-256. The tracked preparation
and build scripts no longer select or compile that rejected patch; they now reproduce only the
stock-Sendspin `0.1.25` diagnostic candidate described below.

| Candidate `0.1.24` artifact | Bytes | SHA-256 | Validation hash |
|---|---:|---|---|
| `firmware.bin` / `firmware.ota.bin` | 3,106,448 | `ECF86D7713CDA9BE1BF261B3AD379B485B67AAEC0BFF3118A9D91989113F5CF4` | `1062E89C4D13A3C15268ABD4B6283903DB8589CA4A7FF042E608563D4FF1D429` |
| `firmware.factory.bin` | 3,171,984 | `813944FC891CCCB69D57FE651B6E287BF87C4CA8BDE2488AC46A62684CA42322` | — |

Image inspection reports ESP32-S3, 16 MB DIO flash, a valid checksum, and a valid validation hash.
Static RAM is 45,600 bytes and application flash is 3,106,039 bytes. The only compile warnings are
the unchanged base-board strapping-pin and pinned upstream TensorFlow Lite warnings.

`PLAYBACKREADYOTA` authorized the attended deployment on 2026-08-28. Exact endpoint identity,
credential parity, passworded fallback AP, captive portal, authenticated updater, rollback artifact,
Joydex process, and recovery ports passed before upload. HTTP OTA returned `Update Successful!` and
the same name and MAC rejoined as `0.1.24`, Armed, with wake inference, microphone capture, and
barge-in on. All three new counters began at zero.

The physical six-response replay rejected the candidate. Joydex sent all 798 frames, completed six
fresh starts and ends without reconnecting, and increased playback-task starts by six, initial late
skips by six, and total late skips by 95. The room-microphone transcript recognized only four of six
short acknowledgements, including one clipped reply. The exact `0.1.23` artifact above was
then restored through the authenticated web updater. Final identity, project version, Armed state,
wake engine, microphone, barge-in, HTTP recovery, API, ports `8765` and `8927`, and the still-running
Joydex process passed. `0.1.24` must not be deployed unchanged.

The initially accepted remedy was host-only on `0.1.23`: one Sendspin stream stayed alive for the
complete Voice Session with clocked silence between responses, a 1,000 ms target lead, and rebasing
before 500 ms.
The isolation matrix produced five complete replies plus one 118 ms blip for six fresh streams at
1,000 ms, three complete replies plus part of a fourth for one stream at 500 ms, and all six complete
replies for one stream at 1,000 ms. The exact production build repeated the six-of-six result across
two consecutive call-scoped sessions. Package `artifacts\Joydex\call-scoped-sendspin-win-x64` is
running with the owned task, both controllers, seven task-alert assignments, and Guardian restored.
Later natural-call counters superseded only its inter-response silence policy; the one-stream lifetime
and timestamp lead remain required.
