# Joydex

Joydex connects physical controls and room devices to explicit Codex actions. Its Voice PE feature gives a dedicated room endpoint a predictable task and conversation lifecycle without requiring a VIRPIL controller.

## Language

**Dedicated Voice Endpoint**:
A room device assigned to Joydex voice conversations instead of shared concurrently with Home Assistant Assist.
_Avoid_: Controller, satellite

**Dedicated Voice Task**:
The Codex task intentionally reserved for room conversations through the Joydex-owned App Server.
_Avoid_: Pinned task, workspace

**Dedicated Voice Task Owner**:
The one Codex App Server with the Dedicated Voice Task loaded under its local writer lock. Joydex must keep its owning control connection alive and the task loaded before it can start explicit-task Realtime; Codex Desktop may already own it.
_Avoid_: Selected task, foreground task

Owner readiness means the pinned Codex executable and its interactive-tool helper hashes passed,
the exact configured task resumed, and the App Server returned a compatible Realtime v2 voice
list. WebRTC and room-endpoint media readiness are checked separately.

**Voice Agent Workspace**:
The local filesystem root that supplies the Joydex-owned App Server process directory, Dedicated
Voice Task `cwd`, runtime workspace root, and durable Voice Session archive. A local Codex project
may group that root in Codex, but its project ID is metadata; the normalized working directory is
authoritative. The current owner keeps full filesystem access with no approval prompts, so this
location communicates where the agent should work and does not contain it.
_Avoid_: Room Voice Workspace, sandbox, transcript database

Each Voice Session has a directory under `.joydex/voice-sessions/<date>/<time>-<session-id>` in the
Voice Agent Workspace. `transcript.md` contains only final user and assistant speech,
`session.json` records lifecycle and workspace identity, and an `audio` subfolder receives new
assistant WAV diagnostics when preservation is enabled. Existing LocalAppData captures remain a
separate legacy diagnostic archive.

**Desktop Attach Endpoint**:
A documented, externally joinable transport launched by Codex Desktop for its existing App Server.
_Avoid_: Desktop process handle, private relay

**Desktop Task Bridge**:
An experimental, current-user compatibility broker that Joydex supervises as one singleton worker,
guarded by a cross-process named semaphore that may be released safely after asynchronous work.
The worker discovers only a verified `ChatGPT -> codex app-server` process, recovers the exact private
App Tools pipe descriptor and current packaged-adapter directory from that App Server's command line,
and reconnects after Desktop restarts. It launches Desktop's packaged Codex App Tools adapter, then exposes only bounded
task status, list, read, and send operations to Joydex over a same-user named pipe. It does not expose
task creation, resume, fork, handoff, or lifecycle mutation and therefore never becomes another task
writer. A Desktop Attach Endpoint would expose the existing App Server itself; the Desktop Task
Bridge exposes only this narrow outbound-command surface. The former marker-managed MCP entry remains
disabled as migration metadata so Desktop cannot create one competing broker per task. It exposes no
model-callable tools; the Dedicated Voice Task sees only Joydex's constrained
`send_message_to_codex_task` tool. Owner readiness verifies that exact tool inventory, and every
Realtime session tells its backing Codex turn to call the tool for each outbound-message request
instead of inferring current availability from earlier conversation history. The voice-tool process
uses strict UTF-8 console I/O for spoken task titles and suppresses only an identical confirmed send
repeated to the same target within 15 seconds of the same Voice Session.
_Avoid_: Desktop attach, second App Server, writer proxy

**Voice Target**:
The saved, local Desktop-owned Codex task that receives an outbound Room Voice command when the user
says “this task” or does not name a target. An explicitly spoken task title temporarily overrides the
saved selection after exact-title, unique-prefix, then unique-substring resolution. The Voice Target
is separate from the Joydex-owned Dedicated Voice Task where the voice conversation runs.
_Avoid_: Dedicated Voice Task, selected Desktop task, foreground task

**Voice Message Outbox**:
The manual-review archive below `.joydex/voice-outbox` in the Voice Agent Workspace. Joydex records an
exact outbound message there when Desktop delivery cannot be confirmed. Reconnection never retries a
draft; the user must retry, retarget, copy, or discard it.
_Avoid_: retry queue, dead-letter log

**Last Voice Task**:
The task Codex remembers from its most recent native Voice use and resumes when Joydex starts native Voice. Using Voice in another task changes this task, which is accepted behavior for the Dedicated Voice Endpoint.
_Avoid_: Current task, selected task, pinned task

**Voice Session**:
One continuous spoken interaction from accepted wake through confirmed close.
_Avoid_: Turn, request

**Room Voice Workspace**:
The Joydex window for watching and recovering the Dedicated Voice Task while Joydex owns it. It is the normal desktop surface for room conversations that Codex Desktop cannot open under the same writer lock.
_Avoid_: Voice PE settings window, transcript debugger

**Conversation Timeline**:
The ordered, readable history and live dialogue shown in the Room Voice Workspace. It includes user and assistant speech plus concise activity, while omitting reasoning and private tool payloads.
_Avoid_: Voice log, transcript database

The timeline explicitly follows the newest message until the user scrolls upward. Returning to the
actual bottom or choosing **Latest ↓** restores follow mode. Live partial growth and layout changes
preserve either the bottom-follow state or the reader's earlier viewport.

**Voice Audio Bridge**:
The concurrent microphone-uplink and speaker-downlink path between the Dedicated Voice Endpoint and Codex Realtime. Joydex uses signed PCM16 at its media boundary. The Voice PE LAN uses the custom WebSocket for microphone/control and the stock clocked Sendspin player with Opus for speaker output; the ChatGPT-authenticated App Server side uses WebRTC. Production firmware configures the custom endpoint as uplink-only, without a Joydex raw-PCM speaker or resampler source.
_Avoid_: Voice shortcut, log observer

**Speaker Playback Boundary**:
An ordered annotation derived from Realtime data-channel `output_audio_buffer` events, with assistant `turn.created`/`turn.done` as the compatibility fallback. It describes the best-known logical assistant-response interval but never gates the independently delivered WebRTC RTP audio. Ending boundaries stay ordered behind every complete speaker frame Joydex already received. They end the logical response without ending the Voice Session's physical Sendspin stream.
_Avoid_: Silence timeout, response transcript boundary

Decoded speaker frames and ending annotations share one ordered host queue. The audio worklet captures
the remote track continuously and emits a pending boundary after the current complete 20 ms frame,
without padding, resetting its resampler phase, or suppressing later RTP audio. Preserved session-level
raw WAV segments are the authoritative diagnostic for everything decoded before speaker gain;
boundary-scoped post-gain WAVs remain segmentation and playout aids.

One Voice Session owns one Sendspin playback stream. Joydex starts it on the first assistant frame,
sends one clocked packet every 20 ms, and carries encoded silence between assistant responses and
after ordered clears. A stopped boundary records response completion without sending `stream/end`.
A cleared boundary preserves every frame already accepted by Joydex and does not tear down or
restart the device decoder. This keeps one contiguous timestamp series for the pinned synchronizer.
The stream ends only when the Voice Session closes, after Joydex drains the final scheduled timestamp
and gives the legacy playback task its bounded teardown window.

Joydex targets a 1,000 ms device-clock lead and rebases before that lead falls below 500 ms. The
pinned decoder discards chunks under 400 ms of lead, so the extra margin protects long playback from
host pacing drift and ordinary scheduler stalls. Small Windows timer oversleeps are corrected
against the absolute cadence; a delay beyond the bounded catch-up window rebases the host cadence
without a catch-up burst. Transport silence is not stored as assistant audio or counted as a decoded
Realtime frame.

A normal Realtime close drains every speaker item already accepted by Joydex and synthesizes a final
ending boundary when the source closes without one. Any scheduled speaker send or connection failure
is terminal because reconnecting and retrying only a suffix would silently abandon audio already
sent to the old stream. Reaching the bounded host queue likewise fails the Voice Session instead of
dropping assistant PCM.

The `0.1.24` readiness-acknowledgement firmware candidate was physically rejected and rolled back to
`0.1.23`. Its counters proved six initial skips and 95 total late chunks during a six-response replay;
moving synchronization after task creation did not make fresh per-response streams reliable. This
established the one-stream lifetime and 1,000 ms lead. Later natural calls showed that filling every
inter-response idle interval with silence keeps the device player continuously runnable. A host-only
response-idle canary tested the apparent yield opportunity, but pausing and resuming one live stream
also introduced timestamp discontinuities while the device retained its synchronization state. Its
46-second natural call sounded worse and discarded 311 of 753 transmitted chunks. The package was
rejected and Joydex returned to continuous cadence. Neither policy repairs the underlying firmware
speaker scheduling defect.

The first natural call with that remedy delivered clear multi-response audio before the physical
endpoint rebooted. Silent and speech-plus-silence full-duplex controls did not reproduce the reset,
but device telemetry isolated microphone WebSocket stalls and queue loss to periods when Sendspin
playback was active; a microphone-only control was clean. Candidate `0.1.25` moved microphone sends
onto ESP-IDF's HTTP-server worker and added uptime, RSSI, and reset diagnostics. Its attended quiet
canary physically rejected it: 1,897 sent frames coincided with exactly 1,897 × 384 dropped bytes
despite zero stalls and a 3 ms maximum send. The latest-frame selector had treated each normal
1,024-byte microphone callback's remainder as stale against a 640-byte wire frame.

Firmware `0.1.26` keeps one send in flight, drains normal callback batching FIFO, and marks a
single backlog compaction only after a successful send exceeds the 100 ms stall threshold. That
compaction drops only complete stale frames and preserves a partial next frame. Logical-session
generation checks also prevent queued microphone work from crossing a close/reopen on the same
socket. Its attended OTA, 60-second microphone-only gate, and silent production-path duplex control
passed with 6,072 total microphone frames and zero drops, stalls, failures, or forced closes. A
later natural multi-turn call exposed player starvation below the host boundary.

Deployed canary `0.1.27` moved the pinned Sendspin player from priority `1` to `18`, above its
priority-`17` WebSocket server. Its first attended call stayed usable for roughly one minute and
closed cleanly after 89 seconds, while the device skipped 771 late speaker chunks, filled its
12,800-byte microphone queue, dropped 99,712 microphone bytes, and stalled seven sends for up to
1,284 ms. Joydex and Codex remained healthy. Because a Voice Session continuously feeds Opus silence,
the priority-`18` player does not receive the empty-ring yield assumed by the upstream fix and can
starve both network workers.

Deployed canary `0.1.28` groups the Sendspin player, Sendspin WebSocket HTTPD worker, and Joydex
microphone/control HTTPD worker at equal priority `18`, allowing those continuously ready media tasks
to time-slice. lwIP shares priority `18`; I²S capture/playback remain above them and the uplink feeder
remains below them. Its first natural call stayed alive and continued carrying recognized user speech
and generated assistant audio, but the endpoint discarded 2,214 late speaker chunks and recorded
five microphone send stalls, 71,424 dropped microphone bytes, and a 717 ms maximum send. Equal
priority avoided a crash without restoring reliable duplex scheduling, so `0.1.28` is rejected
unchanged. A subsequent 45-second speaker-only control sent 2,250 silent Opus chunks at a 1,000 ms
lead with at most 40 ms host lateness; the device discarded 1,282 of them while all microphone
counters remained unchanged. Cold-reboot controls later showed that this was a poisoned-state
result rather than an unconditional speaker-path failure: two sequential 45-second speaker-only
streams added only two late chunks across 4,500 packets, and matched 20 ms and 60 ms duplex controls
both completed with zero late chunks and zero microphone loss while the state was healthy.

The persistent poison is now reproduced deterministically. The pinned Sendspin close callback
releases an outstanding clock-sync high-performance Wi-Fi request and then incorrectly leaves its
local request flag `true`. A host canary withheld exactly one final `server/time` response only after
the first stream had fully drained, then closed normally. The immediately following ordinary
speaker-only stream discarded 1,275 of 2,250 chunks with no microphone connection. Rebooting clears
the state. This confirms a Wi-Fi high-performance lease bookkeeping defect; it does not rehabilitate
`0.1.28`, whose first natural call still rejected the equal-priority scheduler from a fresh boot.

Prepared canary `0.1.29` changes only that runtime close-bookkeeping assignment relative to
`0.1.28`, while its overlay advances the project version; the clock-request flag clears after a
successful release. Its frozen OTA candidate has SHA-256
`456B015932A4BF3A9D986A7C507099133E1BFD092E5B136B228DC072A822D90A`.

Physical regression rejected `0.1.29`: the withheld-clock setup plus following 45-second control
again accumulated 1,261 late chunks while microphone and socket-failure counters stayed clean. The
device remained connected and recovered to zero boot-local counters after restart. Clearing the
stale local flag is therefore necessary bookkeeping but does not remove the persistent failure.
The next isolated hypothesis is configured Wi-Fi power saving itself: an always-on Wi-Fi canary can
bypass all runtime lease transitions without changing the audio protocol or media priorities.

Prepared canary `0.1.30` performs that isolated test. It keeps the exact `0.1.29` component sources,
protocol, and priorities while setting configured Wi-Fi power saving to `none`. Its frozen OTA image
has SHA-256 `AEEF8552B5DEF5459A0868DA5A83F89D5AEE1D5287A3AC776C4FE5BCFE2D0685`.
Physical regression rejected it. With a two-second inter-stream gap, the withheld-clock setup was
clean and the following control added 565 late chunks. After restart, a ten-second-gap repeat added
104 late chunks in setup and 191 in control. Both runs kept microphone/socket counters clean and
recovered fully after restart. Wi-Fi power saving and short reconnect delay are no longer complete
explanations. The next evidence must retain host pacing metrics while examining the device speaker
synchronizer/scheduler lifecycle; the first two `0.1.30` failures did not preserve those host metrics.

The deployed hybrid route uses Sendspin for speaker playback and port `8765` for microphone/control.
Firmware `0.1.19` has no control message that marks Sendspin playback as started, so Joydex owner
mode requires the persisted **Joydex Audio Barge In** switch to remain on. Supporting microphone
gating with that switch off requires a later firmware protocol addition.

Deployed firmware `0.1.23` makes the port `8765` separation structural: its `uplink_only` mode
does not instantiate, start, stop, or restart a Joydex raw-PCM speaker lane. Sendspin alone owns
conversation speaker lifecycle. Rejected `0.1.24` preserved that separation but did not correct
physical playback reliability. Raw-PCM duplex remains an explicit diagnostic mode.

**Modern Sendspin Player Adapter**:
The narrow candidate boundary that replaces the pinned device-side Sendspin decoder and
synchronizer with `sendspin-cpp` `v0.7.2` while preserving Joydex's existing plaintext player-v1
wire contract and all other Voice Session behavior. It maps the modern `PlayerRoleListener` to
pipeline `0` of the pinned ESPHome `MediaSource` interface and returns actual speaker progress to
the modern clock model. Readiness is published only after pipeline installation and pending-frame
reset; atomic source/frame accounting defines the cross-task boundary through the mixer and speaker
callback. The exact production session passes one call-scoped stream containing three content-bearing
responses separated by clocked silence against the host compatibility canary. Firmware `0.1.31`
proved the adapter boundary but was rejected after USB serial identified a silent pthread-configuration
failure and a 3,072-byte stack overflow. The corrected line keeps the modern player worker's
6,192-byte stack in internal RAM while large media buffers and the independent Sendspin HTTP-server
stack can remain in PSRAM. Firmware `0.1.33` also disables the native API's no-client reboot watchdog
because a Joydex-owned endpoint intentionally has no Home Assistant API client; the Wi-Fi recovery
watchdog remains enabled. Joydex primes the synchronized stream before publishing speaker readiness
and permits the observed first-stream synchronization delay without abandoning queued audio.
_Avoid_: New voice stack, current Sendspin firmware, protocol rewrite

**Armed**:
The endpoint state in which a fresh wake phrase may start a Voice Session.
_Avoid_: Idle, listening

**Starting**:
The endpoint state after an accepted wake and before Codex confirms that the realtime Voice Session started. The Voice PE shows its waiting animation and automatically rearms after a bounded failure timeout.
_Avoid_: Active, listening

**Listening**:
The Voice PE ring state shown only after Joydex confirms that the wake-latched Codex realtime session and both room-media lanes are ready. On firmware `0.1.20`, Joydex selects Listening as room-microphone forwarding begins, and the device plays its local connected cue. Joydex does not duplicate that cue through Sendspin. It is the device indication for the active Voice Session and returns to Armed after the matching stop marker.
_Avoid_: Starting, merely requested

**Muted**:
The active Joydex-owned Voice Session state in which Joydex replaces room-microphone uplink frames with timed silence while speaker playback, WebRTC timing, and session ownership remain live. The Voice PE blinks yellow. In JOYDEXOWNER mode, a one-second center-button hold toggles between Muted and Listening; any confirmed close or disconnect clears this state and returns the endpoint to Armed. Native LASTVOICE fallback does not expose these direct session controls.
_Avoid_: Hardware mute, paused, Armed

**Spoken Hangup**:
A voice-only request such as “hang up,” “cancel,” “goodbye,” “bye,” “shut up,” “end,” or “die” that closes the active Voice Session and returns the endpoint to Armed. Short stop words are exact-command forms so ordinary questions ending in words such as “die” or “end” remain conversation.
_Avoid_: End turn, stop response

**Pebble Index Receiver**:
An opt-in, loopback-only Joydex HTTP endpoint for authenticated, transcript-only multipart webhooks from the Pebble Index mobile app. Each accepted transcription is durably recorded before acknowledgement and receives at most one automatic delivery attempt to one configured Desktop Task through the Desktop Task Bridge. A stable delivery header is preferred for duplicate identity; otherwise the recorded timestamp, client, trigger, and transcript form the identity. Duplicate requests never cause another send, and any unconfirmed send remains Delivery Uncertain for manual review rather than automatic retry. The receiver stores its preferences and secret independently from Room Voice and rejects uploaded audio.
_Avoid_: Voice session, generic public task bridge, automatic retry queue
