# ADR 0002: Use Codex App Server Realtime for Dedicated Voice

- Status: Accepted experimentally
- Date: 2026-08-25

## Context

Joydex needs to start a voice session from the Voice PE while the user is away from the desk, bind
that session to a configured Dedicated Voice Task, carry bidirectional audio, and observe session
closure without depending on the selected Codex Desktop task or a physical VIRPIL controller.

The native Desktop Voice shortcut is a proven fallback, but it resumes the Last Voice Task and
exposes no supported typed lifecycle hook. Codex App Server exposes experimental, thread-scoped
Realtime methods with PCM and WebRTC transports plus started, error, and closed notifications.

An installed Codex `0.149.0-alpha.4.1` canary failed during legacy WebRTC call creation. A verified
upstream `0.150.0-alpha.9` canary using the cached ChatGPT login resolved the configured Desktop-owned Voice Task,
accepted deterministic microphone speech, returned the exact requested response, decoded its Opus
audio through the Windows speaker path, wrote a valid remote-audio capture, and emitted typed
closure. It required no first-party attestation token. The raw PCM transport reached App Server and
then failed with `realtime conversation requires API key auth`.

The installed Codex `0.153.4` runtime was revalidated after the corresponding Desktop upgrade. Its
schema checks passed, and the host-only WebRTC canary again completed deterministic microphone
uplink, remote Opus audio playback through the Windows speaker path, capture writing, and typed
session closure without requesting first-party attestation.

Codex task rollouts have an exclusive local writer. After the first canary updated the configured Voice
Chat, the running Codex Desktop App Server held that task's writer lock. A separately launched App
Server could no longer resume it. An ephemeral task reproduced the full media result without
contending with Desktop, which separates the proven audio path from the unresolved durable-task
ownership path.

A later JOYDEXOWNER canary closed the ownership gap for a Joydex-managed task. A Joydex-owned App
Server created a disposable durable task and kept it loaded through its live stdio control
connection. A rival App Server was rejected by the active writer lock. After the owner exited, a
fresh App Server resumed the same task and deleted the canary. The configured Desktop-owned Voice Task
probe still found Desktop holding its writer.

The installed Windows Desktop package `26.818.5229.0` launched its App Server on the default stdio
transport and exposed no TCP listener. The documented WebSocket listener is explicitly experimental
and unsupported for production, while the managed daemon/control-socket lifecycle is Unix-only.
No supported Desktop Attach Endpoint was present in this build.

Because the existing Voice Task remains Desktop-owned, Joydex created a separate durable task named
`Joydex Voice Chat - Owned` through
its private App Server. The creator released it, and fresh App Servers reacquired it by ID without
starting a turn. A temporary Desktop-created setup task was archived after proving that Desktop
retained its writer.

## Decision

Joydex will use capability-compatible Codex App Server Realtime as the primary Codex boundary for
Dedicated Voice sessions. WebRTC is the primary ChatGPT-authenticated App Server transport. Joydex
keeps a PCM boundary around the WebRTC audio tracks. The device LAN wire format is refined by
[ADR 0003](0003-use-sendspin-opus-for-voice-pe-speaker-downlink.md): PCM microphone uplink and
Sendspin Opus speaker downlink.
Raw App Server PCM is reserved for an explicitly selected API-key mode.

A separately launched App Server may target a durable Dedicated Voice Task only after it acquires
that task's writer lock. Joydex will keep its owner control connection alive and the task loaded,
then release both on orderly shutdown. If Codex Desktop already owns the configured task, the
explicit-task path fails closed and reports the ownership conflict. The existing Desktop shortcut
and allowlisted lifecycle observer remain the compatibility fallback while a supported route into
Desktop's running App Server is unavailable.

`Joydex Voice Chat — Owned (joydex_voice)` is the configured App Server target. The Desktop-owned Voice Task remains only the
native LASTVOICE compatibility fallback until the Joydex-owned Realtime path replaces that shortcut.
Joydex must not navigate Desktop to the new task before acquiring it through its own App Server.

The owner has a configured Voice Agent Workspace. Joydex starts the App Server process in that
directory, supplies the same normalized path as `thread/start` or `thread/resume` `cwd` and the
runtime workspace root, and refuses readiness when Codex returns a different directory. A matching
local Codex project is created or reused when project APIs are available; its ID groups the task but
does not replace `cwd`. Folder-only operation remains valid if project APIs are unavailable. The
task retains full filesystem access with `approvalPolicy: never`, so the workspace is an execution
location rather than a security boundary.

The WebRTC implementation must attach the remote stream to a real audio sink before evaluating
speaker readiness. Chromium received RTP while an analyser-only client still reported zero decoded
samples; adding an autoplay audio element activated Opus decoding and produced the successful
capture.

The WebRTC data channel's `output_audio_buffer.started`, `output_audio_buffer.stopped`, and
`output_audio_buffer.cleared` events describe assistant playback boundaries; assistant turn events
provide a compatibility fallback when those finer events are absent. Because the data channel and RTP
track have no cross-channel ordering guarantee, these events are annotations rather than audio gates.
Joydex continuously forwards every complete decoded PCM frame. A stopped boundary records response
completion while the Sendspin transport continues its session timeline with silence. A clear is recorded
but does not discard already accepted or subsequently decoded speech or restart Sendspin. The ordered
host dispatcher bounds queued audio, and bounded-size session-level ungated raw WAV segments are retained
before speaker gain when assistant-audio diagnostics are enabled. The adopted hybrid
route keeps the microphone open with the persisted Barge In switch on; firmware `0.1.19` cannot mark
Sendspin playback as started on its separate microphone/control lane.

Joydex will pin the tested App Server executable and matching `codex-code-mode-host` hashes, resume
the exact configured task, call `thread/realtime/listVoices`, require a valid Realtime v2 default
voice, and fail closed before starting device media when any owner preflight fails. This preserves
the Dedicated Voice Task's normal interactive tools instead of accepting an incomplete one-file
runtime. Session activation separately requires the WebRTC data channel, remote audio track, and
Voice PE LAN transport before readiness is reported.
Runtime code will use generated schemas and protocol responses; it will not inspect or parse
`app.asar`, emulate first-party attestation, or rewrite private upstream call requests.

Joydex exposes the owned task through a Room Voice Workspace. `thread/read` with `includeTurns=true`
provides canonical dialogue history, while the active WebRTC data channel provides immediate user
and assistant transcript updates. The UI keeps its presentation state in memory and reloads canonical
history after each session. Separately, Joydex writes a readable per-session `transcript.md` and
`session.json` below the Voice Agent Workspace as each final user or assistant transcript arrives.
These records exclude protocol wrappers, reasoning, tool arguments, command output, and other private
payloads, and transcript text does not enter `joydex.log`. Diagnostic assistant WAVs join the active
session's `audio` subfolder when explicitly enabled; older LocalAppData captures are left untouched.
The Dedicated Voice Task is internally excluded from ordinary task alerts while remaining absent
from the user's ignored-task rules.

Outbound commands to other Codex tasks use a separate experimental Desktop Task Bridge. Joydex
supervises one singleton `Joydex.DesktopBridgeHost` worker behind a cross-process named semaphore,
which discovers only a verified
`ChatGPT -> codex app-server` process and uses the packaged Codex App Tools adapter. Codex Desktop's
built-in `codex_app` override receives a private broker pipe that a separately configured MCP server
does not reliably inherit. The worker therefore extracts the exact `codex_app` pipe and packaged-
adapter directory from the verified Desktop App Server's command line, reconnecting if that App
Server restarts. It does not inspect `app.asar`. Joydex receives a current-user named
pipe that allows only bridge status, local task list/read, and `send_message_to_thread`. The bridge
never calls `thread/resume` and exposes no create, fork, handoff, archive, or ownership operation.
The former marker-managed MCP entry is kept disabled as migration metadata, preventing Desktop from
launching one competing broker per task. It exposes no model-callable tools. The Dedicated Voice Task
receives a separate MCP server exposing only `send_message_to_codex_task`, which prevents the transport
host from competing in tool selection. Owner readiness verifies that exact inventory. Each Realtime
session also supplies developer instructions requiring a fresh tool call for every outbound-message
request, so a prior bridge failure cannot poison later delivery decisions in the durable task history.
The voice-tool MCP boundary forces strict UTF-8 console I/O, and an identical confirmed delivery to
the same target and Voice Session is deduplicated for 15 seconds to absorb repeated Realtime handoffs.
This is not a Desktop Attach Endpoint and does not let Joydex's App Server acquire a Desktop-owned
task writer lock.

The Room Voice Workspace persists a Voice Target independently from the Dedicated Voice Task. The
saved target defines “this task”; a spoken title may resolve an exact, uniquely prefixed, or uniquely
contained title for one delivery. Ambiguous names do not send. A delivery that Desktop does not
confirm is written atomically to the Voice Message Outbox with its exact body and target snapshot.
Reconnect never auto-sends drafts; retry, retarget, copy, and discard are explicit user actions.
Prompt bodies do not enter `joydex.log`.

Desktop task messaging remains disabled by default and must be revalidated after Desktop upgrades.
Missing broker state, adapter failure, approval requirements, or an unmanaged configuration conflict
leave normal Room Voice operation intact and hold attempted outbound delivery for review.

The same singleton Desktop Task Bridge may serve Joydex's optional Pebble Index Receiver without a
Room Voice session. Joydex starts the broker only while configuration is open or a messaging feature
is enabled, and its current-user-only transport uses a randomized per-process pipe name. The receiver
binds only to loopback, authenticates before parsing, rejects audio,
and maps every accepted transcript to one locally configured Desktop task. It persists an ingress
record before returning success, suppresses duplicate webhook identities across restarts, and never
automatically retries an unconfirmed Desktop send. Before delivery, it resolves the saved target
directly by ID through the bridge's read operation, so older unpinned tasks do not depend on the
bounded recent-task catalog. The selected target task is also the legitimate
Desktop source identity for the constrained send; the public HTTP surface exposes no task listing,
selection, reading, or other generic bridge operation.

## Consequences

- A Joydex-owned Dedicated Voice Task remains independent of Desktop selection and Last Voice Task
  state while Joydex owns its writer lock.
- The App Server process, task `cwd`, runtime root, and session records share one verified Voice Agent
  Workspace; local Codex project identity remains optional grouping metadata.
- Moving to another workspace creates a fresh owned task and leaves the previous task and legacy WAV
  captures intact.
- The Room Voice Workspace becomes the readable and recoverable desktop surface for that owned task,
  including direct End Session and Restart controls.
- Desktop-owned tasks can receive explicit outbound Room Voice commands through Desktop's own task
  tool without transferring ownership; unavailable deliveries remain manual-review drafts.
- Typed `thread/realtime/closed` becomes the primary end-of-conversation signal for Joydex-owned
  sessions.
- App Server process exit is supervised; Joydex rebuilds the complete owner bridge with bounded
  exponential backoff instead of leaving the wake path silently unavailable.
- Transient owner-start failures retry with the same bounded backoff. Configuration, pinned-binary,
  and Realtime-capability failures remain terminal and visible until corrected.
- Recoverable Voice PE control-stream protocol failures reconnect instead of permanently ending
  wake monitoring.
- ChatGPT-authenticated full-duplex WebRTC is proven on the host with deterministic audio.
- Codex Desktop and a separate Joydex App Server cannot write the same task concurrently. Product
  setup needs a clear ownership check and recovery path.
- JOYDEXOWNER is now proven for a Joydex-managed durable task, including rival rejection, release,
  and handoff. The separate `Joydex Voice Chat - Owned` task removes the Desktop writer conflict from the
  primary App Server path.
- DESKTOPATTACH remains capability discovery on Windows. Parent-owned Desktop stdio cannot be joined
  by another process through a documented transport.
- The PC must remain awake, online, and authenticated to ChatGPT.
- A compatible App Server must be supplied or selected independently of the installed Desktop build
  when that build is outside the tested range.
- App Server Realtime is experimental, so every Codex upgrade requires schema and end-to-end media
  canaries before automatic wake is enabled.
- The hybrid Voice PE microphone and Sendspin Opus speaker transport passes a three-minute physical
  duplex soak and repeated attended natural sessions. The best restart canary carried multiple
  intelligible exchanges with no speaker-queue overflow and rearmed for a second session. Spoken
  Hangup eventually closed both sessions; interruption and first-attempt hangup reliability remain
  open attended checks.

## Evidence

- `tools/Joydex.WebRtcCanary` host-only canary
- Revalidated on 2026-09-08 against Windows package `OpenAI.Codex 26.901.6511.0`, bundled app
  release `26.901`, and Codex `0.153.4`
- Verified Codex `0.153.4` binary SHA-256
  `e5aa76d19c7c94e2e9ef9b707d590206a73ac0e97c8ddc8382181242494bef75`
- Verified matching `codex-code-mode-host.exe` SHA-256
  `3eb2083b58f0982506e5c3cb7a550fb6538d718c29f0a75ca4848852a0aff0c7`
- Successful `0.153.4` deterministic microphone uplink, remote model-audio track, Windows speaker
  playback with nonzero audio energy, 273,243-byte WebM capture, and typed requested close
- Verified upstream Codex `0.150.0-alpha.9` binary SHA-256
  `5ffd7a27694e1529d717a0247858d7650438273ba10d4d8a4f0a73f5e1414082`
- Verified matching `codex-code-mode-host.exe` SHA-256
  `39db8fcd843b6dbfd4e5fb4e92bcb1c74b17a00bff2ae24d9afdedd6ea26b0c0`
- Successful deterministic microphone uplink and exact user transcript
- Decoded 48 kHz mono Opus downlink with 1,281,120 samples and nonzero audio energy
- Valid 47,343-byte WebM capture converted to 24 kHz mono PCM and transcribed locally as
  `Joydex PCM Roundtrip Connected.`
- Successful Realtime v3 start, SDP, connected media, and typed requested close
- ChatGPT-authenticated PCM rejection: `realtime conversation requires API key auth`
- Confirmed Codex Desktop writer-lock conflict on the configured Voice Task
- Successful JOYDEXOWNER durable-task create, rival rejection, owner exit, fresh-process resume, and
  exact canary-task deletion
- DESKTOPATTACH observation on Desktop package `26.818.5229.0`: default stdio and no TCP listener
- Successful creation and fresh-process reacquisition of `Joydex Voice Chat - Owned` without
  starting a turn; the machine-specific task ID is intentionally omitted
- JOYDEXOWNER configured-target readiness probe returned `AVAILABLE` and exit code `0` for the new task
- Historical installed-build failure on Codex `0.149.0-alpha.4.1`
