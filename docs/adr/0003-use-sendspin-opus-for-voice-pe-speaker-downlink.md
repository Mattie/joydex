# ADR 0003: Use Sendspin Opus for Voice PE Speaker Downlink

- Status: Accepted
- Date: 2026-08-28

## Context

Joydex needs sustained simultaneous microphone uplink and speaker downlink on the deployed Voice PE
`0.1.19` image. The custom WebSocket PCM bridge passed short tests and later failed sustained media.
The source-only UDP PCM replacement passed host loopback, failed its first physical smoke, and was
rolled back. The deployed image also contains ESPHome's stock legacy Sendspin v1 player on port
`8927`, including device clock synchronization, a timestamped jitter buffer, decoder, mixer, and a
high-performance Wi-Fi lease.

A raw-PCM Sendspin canary reached the speaker, but sustained duplex playback failed while the
device's HTTPD handler tried to allocate large PCM WebSocket payloads. The device logged
`Failed to calloc memory for buffer`. A separate close at exactly 15 seconds traced to .NET's
WebSocket keepalive control frame; the pinned ESPHome endpoint rejected it with
`ESP_ERR_INVALID_STATE`. Sendspin already exchanges clock messages every second.

The same legacy wire protocol accepts one raw Opus packet per timestamped audio message. A managed
Concentus `2.2.2` encoder reduced a 20 ms, 48 kHz mono frame from 1,920 PCM bytes to at most 98 bytes
in the physical canaries while retaining the device's stock playback scheduling.

Natural multi-turn sessions later showed a distinct lifecycle failure: Joydex received and sent
every later assistant frame with zero host overflow, while the device became silent after an
interruption. The deployed legacy media source has no downstream packet-consumption counters. Its
source shows that `stream/end` and `stream/clear` empty the encoded queue and stop an asynchronous
playback task, while a fresh `stream/start` creates and synchronizes another task. Joydex temporarily
changed to one stream per assistant response to make those transitions explicit and testable.

A deterministic six-response replay of the same preserved assistant PCM isolated the failure
without Codex or WebRTC in the path. Firmware candidate `0.1.24` moved
`client/state=synchronized` after playback-task creation and exposed device discard counters. Its
physical replay still produced six initial skips and 95 total late skips; the room microphone
recognized only four of six responses, one clipped. The candidate was rejected and the exact
`0.1.23` image restored.

Host-only isolation on unchanged `0.1.23` separated the two remaining variables. Six fresh streams
with a 1,000 ms lead produced five complete responses and one 118 ms blip. One continuous stream
with the old 500 ms lead produced three complete responses and part of a fourth before playback
fell silent. One continuous stream with a 1,000 ms lead produced all six complete phrases in the
room recording. The remedy therefore requires both call-scoped stream continuity and enough clock
lead; either change alone is insufficient.

The first natural conversation on the combined host remedy sounded clear for multiple assistant
responses and then ended when the Voice PE rebooted. A 90-second continuous-silence duplex soak and
a 60-second repeated-speech-plus-silence duplex soak both completed without a reset. The latter
controls nevertheless exposed 21 cumulative microphone WebSocket send stalls, a 2,488 ms maximum
blocked write, a full 12,800-byte uplink queue, and 236,928 dropped bytes. A subsequent 60-second
microphone-only control delivered 3,051 frames with no additional stall or drop. The reset is not
yet reproduced, but full-duplex microphone backpressure is a measured independent defect.

Firmware `0.1.27` adapted the upstream player-priority fix to the pinned Sendspin source. Its first
attended conversation stayed usable for about one minute and ended cleanly after 89 seconds, but the
device recorded 771 late speaker chunks, a full microphone queue, 99,712 dropped microphone bytes,
and seven send stalls up to 1,284 ms. Host transcripts and responses continued, and the device did
not reboot. A call-scoped stream continuously supplies Opus silence, so the priority-`18` player does
not reach the empty-ring yield condition assumed by a priority-`17` WebSocket server.

Firmware `0.1.28` then placed the player and both media HTTPD workers at equal priority `18`. Its
first 93-second natural call stayed booted and ended by Spoken Hangup. Joydex continued receiving
completed user transcripts and generated assistant-audio events after the perceived failure, with
one Sendspin stream, a host queue high-water mark of two, and zero host overflow. The endpoint still
discarded 2,214 late chunks, dropped 71,424 microphone bytes, and recorded five microphone sends
stalled up to 717 ms. Equal priority therefore failed to remove the always-runnable-player failure.

## Decision

The Voice PE audio bridge will combine two proven lanes:

- The deployed custom endpoint at port `8765` remains the microphone uplink and control lane.
- The stock legacy Sendspin v1 endpoint at port `8927` becomes the speaker downlink.
- Firmware `0.1.23` configures port `8765` as uplink-only. Its raw-PCM speaker dependency is
  optional, and the production overlay creates no Joydex mixer/resampler source. Port `8765`
  rejects binary downlink frames and never starts, stops, or restarts the shared speaker graph.
- A follow-up microphone sender may have only one frame in flight on the HTTP-server worker. Normal
  source-callback batching drains FIFO. A successful send delayed at least 100 ms marks one backlog
  compaction; that compaction discards complete stale frames, preserves a partial next frame, and
  resumes with the latest complete frame instead of bursting old speech. Logical-session generation
  checks invalidate queued work across close/reopen even when the WebSocket remains connected.
  ESP-IDF's native `httpd_queue_work()` plus `httpd_ws_send_frame_async()` pattern is used so the
  50 Hz path does not allocate and wait on a second task event group for every frame.
- The host routes speaker playback boundaries only to Sendspin. `output_audio_buffer.cleared`
  clears Sendspin without forwarding a duplicate `flush` to the microphone/control lane, and an
  ordinary playback end has no port `8765` side effect.
- The production hybrid handshake requires `mode: "uplink_only"` and `downlink: null`. Legacy
  protocol-v1 duplex firmware remains usable by the diagnostic transport but cannot silently enter
  the Sendspin production route.
- Joydex keeps 24 kHz mono PCM as its internal speaker boundary, upsamples each 20 ms frame to
  48 kHz, and encodes exactly one mono Opus packet at 32 kbit/s for each Sendspin message.
- Each packet carries a server-clock timestamp with a 1,000 ms target lead. The host rebases before
  lead falls below 500 ms, leaving margin above the pinned decoder's 400 ms rejection threshold.
  The Voice PE owns the jitter buffer, decoder, synchronization, and final mixer path.
- One Voice Session owns one Sendspin stream. `output_audio_buffer.stopped` ends a logical response
  but leaves the stream and Opus encoder alive. Joydex sends clocked Opus silence until later speech
  or final Voice Session close, preserving one contiguous timestamp series for the pinned player.
- `output_audio_buffer.cleared` remains ordered behind all assistant PCM Joydex already received.
  It no longer sends `stream/clear`; already accepted speech can finish, after which the same stream
  carries silence. This deliberately favors complete speech and decoder stability over immediate
  acoustic interruption.
- Final Voice Session close waits through the last scheduled timestamp plus a 100 ms margin, sends
  `stream/end`, gives the legacy playback task 500 ms to settle, and closes the Sendspin socket.
  There is no per-response playback-task teardown.
- A 500-frame host dispatcher ingests speaker audio and lifecycle boundaries concurrently. During
  an active Voice Session, it advances the Sendspin stream at one packet per 20 ms. It drains bursty
  decoded PCM at that fixed cadence and inserts Opus silence whenever the host delivery queue is
  empty after playback has begun. The dispatcher anchors small timer delays to an
  absolute cadence and rebases after more than 15 ms of lateness to prevent a catch-up burst. It
  retains a hard queue bound and serializes transport writes. Reaching the queue bound fails the
  Voice Session instead of silently losing assistant audio.
- The recovery decorator captures the first successful transport's scheduling capability and
  rejects a replacement with different pacing semantics. Call-scoped scheduled speaker sends and
  connection failures fail the Voice Session: retrying only the interrupted operation on a new
  stream could silently abandon audio already delivered to the previous stream. Ordinary host-paced
  diagnostic transports retain bounded reconnect behavior.
- Normal Realtime completion waits up to 15 seconds for the accepted speaker queue and its final
  device boundary to drain before closing the transport. If the source closes after audio without
  an explicit end or clear, Joydex synthesizes an ending boundary so the scheduled tail is drained.
- Startup requires three successful Sendspin clock samples before the speaker lane reports ready.
- Firmware `0.1.24` is rejected physical evidence, not the remedy. Its readiness patch and counters
  remain available for diagnosis, but the device is restored to `0.1.23`; the accepted fix is in
  Joydex's host scheduling and stream lifecycle.
- .NET WebSocket keepalive frames remain disabled for this pinned legacy endpoint. Sendspin clock
  messages provide continuous liveness traffic.
- Joydex owner mode requires the persisted **Joydex Audio Barge In** switch to stay on. Firmware
  `0.1.19` cannot signal Sendspin playback start over the separate port `8765` control lane; a later
  firmware protocol addition is required for correct microphone gating with barge-in off.
- Firmware `0.1.20` owns both lifecycle cues locally. The xylophone acknowledges wake detection;
  the other cue plays when Joydex selects Listening after Realtime and both media lanes are ready.
  Joydex no longer prepends a duplicate ready cue to Sendspin, leaving that lane for assistant
  audio. The `0.1.19` rollback retains the earlier host-injected ready-cue behavior.
- The legacy dialect and Concentus version remain pinned and must be revalidated when the deployed
  ESPHome Sendspin implementation changes.
- Candidate firmware `0.1.28` tested one equal-priority media class: the Sendspin player, Sendspin
  WebSocket HTTPD worker, and Joydex microphone/control HTTPD worker all run at priority `18`.
  lwIP also runs at `18`; I²S microphone and speaker tasks remain at `23` and `19`; the Joydex uplink
  feeder remains at `1`. Best-effort time-slicing leaves feeder starvation, network jitter, and
  watchdog pressure as canary risks. Its first natural call produced 2,214 late chunks plus
  microphone stalls/loss, so the scheduling change is rejected unchanged. A later 45-second
  speaker-only control discarded 1,282 of 2,250 silent Opus chunks with no microphone-session
  activity; subsequent cold and forced-clock-close controls localized that result to persistent
  Wi-Fi lease corruption left by a prior Sendspin close rather than unconditional speaker-only load.

The rejected UDP `0.1.21` transport remains diagnostic prior art and is not deployed or connected
to the Joydex owner runtime.

## Consequences

- Firmware `0.1.23` was deployed by attended HTTP OTA on 2026-08-27. The exact `0.1.20` image
  remains its immediate rollback. The intermediate `0.1.22` mixer-settle candidate was rejected
  before deployment: its fixed
  250 ms delay reduced the observed race to a timing assumption while retaining the unused
  speaker lifecycle that caused it.
- Speaker data on the LAN is compressed Opus; microphone data remains signed PCM16. Joydex's
  WebRTC-facing media boundary remains PCM in both directions.
- The device's stock clock synchronization and buffering schedule playback while Joydex supplies a
  steady host packet cadence.
- Bounded host ingestion and clear preemption remain in place even though the device owns cadence.
- Host delivery gaps and inter-response idle time become measurable silent padding on the same
  Sendspin stream. A Windows scheduler pause beyond the catch-up bound rebases host cadence; falling
  below the safe lead also rebases the device timestamp and remains visible in diagnostics.
- The downlink no longer creates the large transient HTTPD allocations that failed raw PCM.
- Joydex gains one pure-managed runtime dependency, Concentus `2.2.2`.
- LAN media remains plaintext by explicit product choice.
- Deployed `0.1.23` is an ESP32-S3, 16 MB DIO image with a valid checksum and validation hash.
  Post-OTA identity, recovery services, Armed state, wake/microphone state, and the live
  `uplink_only`/null-downlink capability handshake passed. Host regression tests prove Sendspin
  clear does not touch the microphone/control lane and reject any production hello that can own
  raw speaker downlink.
- Natural wake-driven conversations still need to validate acoustic quality, interruption,
  Spoken Hangup, rearm, and repeated-session behavior.
- Candidate `0.1.24` was deployed under an attended canary, failed the repeated-speech gate, and was
  rolled back to exact `0.1.23`. It must not supersede `0.1.23` unchanged.
- The host remedy is published as `artifacts\Joydex\call-scoped-sendspin-win-x64`. After one
  controlled Joydex restart it reacquired the Dedicated Voice Task, both controllers, seven task
  alerts, direct LEDs, and Guardian. Two consecutive production-build replays each produced all six
  complete phrases through the physical speaker.
- Candidate `0.1.25` implemented the bounded microphone sender and diagnostics without modifying
  stock Sendspin. Its attended quiet microphone canary exposed deterministic callback-frame loss,
  so it was rejected and rolled back to exact `0.1.23`.
- Deployed `0.1.26` separates normal FIFO draining from post-stall compaction, preserves partial
  frame tails, and prevents queued uplink work from crossing logical sessions. Its microphone-only
  and silent production-path duplex controls passed; natural conversation remains the acoustic gate.
- Deployed firmware canary `0.1.27` adapts the upstream Sendspin scheduling fix to the pinned
  legacy component by running decode/playback at priority `18`, above the priority-`17` WebSocket
  task. It retains current startup and stream semantics and adds device-side task/late-skip counters.
  `PRIORITYOTA` deployed the exact fresh artifact on 2026-08-28 and its identity, recovery, safe-boot,
  owner-process, and zero-counter postflight gates passed. The first 89-second natural call rejected
  it with 771 late speaker chunks, 99,712 dropped microphone bytes, and seven send stalls up to
  1,284 ms. The exact `0.1.26` image is its immediate rollback; `0.1.23` is secondary recovery.
- `0.1.28` is a deployed, unaccepted balanced-priority canary. Its 3,109,808-byte OTA image has
  SHA-256 `5E929769626BC438D1119DA70E1FFFAEC574E2F4CA2D4F363C6DD3A17679D948`
  and valid image hash `50EAD7835E9F87AACD59321BB90D9FDF4AA0E3D23FEEEECCD804632E261D879E`.
  The final builder performs resolved credential parity before its last compile, repeats generated
  parity and image inspection afterward, and freezes the exact artifacts plus manifest under the OTA
  content hash. Attended upload must use that stage without rebuilding. The upload wrapper's full
  preflight-only path passed against live `0.1.27` and refused an invalid authorization keyword.
  `BALANCEDOTA` authorized the exact staged upload; web OTA returned `Update Successful!`. The device
  rejoined with exact name, MAC, project, version, ESPHome release, safe-boot, Armed/wake/microphone,
  four-service, and zero-counter postflight gates passing. Joydex and Guardian remained responsive.
  Its first 93-second natural call then rejected the candidate unchanged: user transcripts and
  assistant-audio generation continued after one minute with zero host overflow, while the device
  discarded 2,214 late chunks, dropped 71,424 microphone bytes, and recorded five send stalls up to
  717 ms. The host-only response-idle package is
  `artifacts\Joydex\response-idle-sendspin-win-x64`; it keeps one stream open while stopping packet
  cadence at response boundaries. Its host suites passed, but `IDLEGAPCANARY` physically rejected it:
  the 46-second call sounded worse, discarded 311 of 753 transmitted chunks, dropped 50,816
  microphone bytes, and stalled three sends up to 1,017 ms. The same-stream idle/resume also leaves
  device synchronization history alive across timestamp discontinuities, which the host-only tests
  did not model. Joydex was rolled back to `call-scoped-sendspin-win-x64`; Guardian, four task alerts,
  direct LEDs, M2, both controllers, the Dedicated Voice Task, and Armed state recovered.
- A post-rollback 45-second speaker-only control on unchanged firmware `0.1.28` sent 2,250 silent
  Opus chunks with a 1,000 ms lead and no microphone connection. Host maximum send lateness was
  40 ms, while the device discarded 1,282 chunks and all microphone counters stayed unchanged.
  After a cold reboot, two sequential copies of that control added only two late chunks across 4,500
  packets. Matched 60-second controls with 50 seconds of microphone overlap then completed at both
  60 ms and 20 ms Opus packet durations with zero late chunks, zero microphone loss/stalls, and clean
  rearm. Packet aggregation therefore remains a diagnostic option rather than an accepted remedy.
- Source inspection found a shared close-handler typo: after successfully releasing an outstanding
  clock-sync high-performance Wi-Fi request, the legacy Sendspin component sets its local request
  flag to `true` instead of `false`. A deterministic host canary withheld one final `server/time`
  response only after its 1,000-chunk stream had fully drained, then closed. The next normal
  speaker-only stream discarded 1,275 of 2,250 chunks, with zero initial skips and no microphone
  connection; reboot restored clean behavior. This confirms the persistent cross-session failure
  mechanism. A firmware correction and a natural two-call acceptance run remain required.
- Prepared firmware `0.1.29` keeps the exact `0.1.28` runtime component source except for the
  one-line correction that clears the clock-sync high-performance request flag after release; its
  overlay advances the project version. Its frozen 3,109,808-byte OTA image
  has SHA-256 `456B015932A4BF3A9D986A7C507099133E1BFD092E5B136B228DC072A822D90A`
  and valid image hash `8490BB3913D65B2E34A7065CBC4BCB4BC993D5B74508E1E13E06131C7406ED53`.
  Station and passworded recovery-AP parity, pinned/generated source hashes, project identity,
  priority evidence, ESP32-S3 image validation, exact target identity and service ports, exact
  `0.1.28` rollback, invalid-keyword/missing-recovery refusal, and live preflight-only checks passed.
  `LEASEFIXOTA` deployed the exact reviewed image after these gates passed.
- The direct `0.1.29` regression failed before natural-call testing. Its withheld-clock setup and
  following 45-second control added 1,261 late chunks, zero initial-late chunks, and no microphone
  loss, send stall/failure, or forced close. Restart cleared the state. The local flag correction is
  insufficient; the next isolated candidate disables configured Wi-Fi power saving so runtime lease
  transitions cannot affect either stream.
- Prepared firmware `0.1.30` keeps the exact `0.1.29` audio sources and task priorities while setting
  configured Wi-Fi power saving to `none`. Its frozen 3,109,808-byte OTA image has SHA-256
  `AEEF8552B5DEF5459A0868DA5A83F89D5AEE1D5287A3AC776C4FE5BCFE2D0685` and valid image hash
  `37EE0C96FECFE9FEC7B313567BA89561D4444229C0748764E96F7FE9A5B13E0E`. The two-pass build,
  source/credential/generated-code parity, exact `0.1.29` rollback, refusal gates, and live
  preflight-only path passed. `ALWAYSONOTA` deployed it and all postflight plus rollback-preflight
  gates passed. The direct test rejected it: the normal two-second-gap run added 0 setup and 565
  control late chunks; after restart, a ten-second-gap repeat added 104 setup and 191 control late
  chunks. Both kept microphone and socket counters clean. Restart restored zero counters after each
  failure. Configured Wi-Fi power saving and teardown delay are not complete remedies. These failure
  records did not retain host maximum-lateness metrics, so further work must compare host pacing with
  the device speaker synchronizer/scheduler before assigning the remaining cause.

## Evidence

- Host Opus round trip: one 20 ms frame encoded to 112 bytes and decoded to 960 samples.
- Audible speaker-only physical smoke: the device produced sound while Joydex sent 600 timestamped
  packets and 27,356 payload bytes over a synchronized 12-second connection.
- Corrected duplex physical canary: Joydex sent 1,500 timestamped speaker packets while 1,500
  concurrent microphone frames arrived over 30 seconds.
- Duplex soak: Joydex sent 9,000 timestamped speaker packets and 388,206 payload bytes while 9,000
  concurrent microphone frames arrived over three minutes. The connection stayed synchronized,
  closed cleanly, and produced no device allocation error in the logs.
- Production-transport canary: Joydex sent 1,500 speaker frames while microphone uplink remained
  active over 30 seconds.
- Production interruption canary: `stream/clear`, a fresh `stream/start`, 600 total speaker frames
  sent, and clean `stream/end`/close.
- Response-scoped physical canary on deployed `0.1.23`: three separately started, synchronized,
  drained, and ended streams sent 300 of 300 frames with no reconnect. The user heard all three
  two-second tones; the concurrent microphone lane captured the full run. Each end took roughly
  1.1 seconds including scheduled-tail drain and legacy-task settle time. A repeat produced all
  three audible tones again, with slight static reported on the first tone.
- Twelve-response tone stress on deployed `0.1.23` completed every start and end. A 250 ms
  post-teardown gap produced one tolerable static event; removing that gap increased static around
  the second or third tone in two loops. This makes teardown delay a contributing symptom, not a
  sufficient fix for missing speech.
- Six identical preserved-speech streams on deployed `0.1.23` sent 798 of 798 host frames and
  completed six of six starts and ends. The external microphone captured incomplete early replies
  even though the source PCM contained a complete short acknowledgement. This reproduces the playback
  failure below the host transport boundary.
- The attended `0.1.24` replay started six playback tasks and increased its device counters by six
  initial skips and 95 total late skips. The room recording recognized four of six phrases, including
  one clipped phrase, so the exact `0.1.23` image was restored.
- On restored `0.1.23`, six fresh streams at 1,000 ms lead produced five complete phrases and one
  118 ms blip. One continuous stream at 500 ms produced three complete phrases and part of a fourth.
  One continuous stream at 1,000 ms produced six of six complete phrases in the external room-mic
  transcript. This is the adoption evidence for the combined host remedy.
- Natural acceptance on the production host build delivered good-quality audio for multiple turns,
  then the device reset. Host logs recorded two completed assistant responses and 559 accepted
  speaker frames before the microphone/control socket disappeared without a close handshake;
  post-rejoin firmware counters had reset to zero while Joydex, Guardian, and App Server remained
  alive.
- On unchanged `0.1.23`, a 4,500-frame silent full-duplex soak and a 3,000-frame repeated-speech plus
  transmitted-silence soak completed without reset. Full-duplex controls accumulated 21 microphone
  send stalls and 236,928 dropped bytes; the 3,000-frame microphone-only control added neither.
- The attended `0.1.25` quiet microphone canary sent 1,897 frames and dropped 728,448 bytes with a
  1,024-byte queue high-water mark, zero send stalls, and a 3 ms maximum send. The exact equality
  `1,897 × (1,024 - 640) = 728,448` proves deterministic local callback-frame loss rather than
  network backpressure. The exact `0.1.23` rollback restored identity, lifecycle, audio controls,
  recovery ports, and the running Joydex owner.
- Deployed `0.1.26` compiles at 45,584 bytes static RAM and 3,108,227 bytes application flash. Its
  freshly rebuilt 3,108,624-byte OTA image has SHA-256
  `29E6913D66DBBD51A43A13922D73B81BF82F580A2904F46D80F0C35C4737E73F` and valid image hash
  `2D292BF95FA2C785ACCB8AB4482C6702EFF148D42A2DBB1FA8ED635EFF6E806C`. Source/prepared/generated,
  exact stock-Sendspin, credential, fallback-recovery, rollback, and image gates passed before
  `FRAMEALIGNOTA` uploaded it.
- The first 60-second microphone-only gate delivered 3,036 frames with zero drops, stalls, failures,
  or forced closes and a 4 ms maximum send. A second 3,036-frame gate overlapped 3,000 clocked
  Sendspin Opus frames for about 47 seconds and again produced zero uplink loss or stalls with a 7 ms
  maximum send. The device rearmed after both and remained on `0.1.26` without reboot. Stock Sendspin
  logged its known error `259` receive diagnostic only after the deliberate socket close, following
  orderly stream and media-pipeline completion.
- The first natural `0.1.26` acceptance call delivered all host assistant frames with no queue
  overflow or pacing defect. About 1.5 seconds into the sustained third response, the device logged
  microphone-worker stalls up to 858 ms, filled its 12,800-byte uplink queue, and dropped 50,560
  bytes. The pinned Sendspin source creates its player at literal priority `1` while the WebSocket
  HTTPD task runs at priority `17`; its unused constant says `5`. ESPHome PR #16178 independently
  identifies and fixes this player-starvation ordering. This is the evidence for the narrow `0.1.27`
  canary, not proof of physical acceptance.
- The attended `0.1.27` canary completed an 89-second Voice Session and six assistant responses while
  the device accumulated 771 late Sendspin chunks, seven uplink stalls, a 1,284 ms maximum send,
  a full 12,800-byte uplink queue, and 99,712 dropped microphone bytes. Spoken Hangup, rearm, device
  uptime, and Joydex/Guardian health passed; sustained device media scheduling failed.
- The source-only `0.1.24` preparation pins Sendspin commit
  `aef3a4afd9c7186751fc9d097742325b3390572f`, verifies the two patched files by SHA-256, passes a
  second idempotent preparation, and places the local source last in ESPHome component resolution.
  Configuration validation, generated-source parity, and a clean ESPHome `2025.12.2` compile pass.
- Deterministic dispatcher tests verify that bursty PCM is emitted at 20 ms intervals, timer
  oversleep is corrected or safely rebased, source gaps inside a response produce silent padding,
  response endings stop that padding, and queue exhaustion fails explicitly. Coordinator tests also
  verify normal-close draining, missing-boundary synthesis, and fail-closed response-scoped recovery.
- `tools/Joydex.SendspinCanary` reproduces the legacy handshake, clock exchange, PCM failure mode,
  and Opus success independently of Joydex.
- The modern-client compatibility harness in `tools/Joydex.SendspinModernCanary` built the exact
  Apache-2.0 `sendspin-cpp` `v0.7.2` source at commit
  `30514d5102c269a0c7fa6a13932d6bf7f2ae1abc` without network access. The exact production
  `VoicePeSendspinSpeakerSession` negotiated the same plaintext player-v1 contract and sent three
  two-second responses in one call-scoped stream, with one second of clocked silence between them.
  The client accepted and played all 429,372 decoded/synchronization PCM frames with zero write
  timeouts, detected all three content segments, exceeded 90,000 audible frames in every response,
  and preserved the expected rising-energy order. A separate three-stream lifecycle control also
  passed. ADR 0004 therefore stages the modern player behind a narrow legacy ESPHome media-source
  adapter while preserving this ADR's host and product behavior.
