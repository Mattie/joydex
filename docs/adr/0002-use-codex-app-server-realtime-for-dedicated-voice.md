# ADR 0002: Use Codex App Server Realtime for Dedicated Voice

- Status: Accepted experimentally; runtime selection superseded by [ADR 0005](0005-follow-managed-codex-runtime-with-capability-checks.md)
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
that task's writer lock. Joydex acquires the writer after an accepted wake, keeps the owner control
connection alive for that Voice Session, and closes the private App Server during session teardown.
The endpoint remains Armed without owning the task. If Codex Desktop retained an idle writer when a
wake arrives, Joydex asks Desktop to archive and immediately restore that chat, then retries
acquisition once. That supported lifecycle tears down Desktop's loaded task while preserving the
chat and leaving it visible. Joydex never attempts this release for an active task. A failed handoff
rejects the call and returns the endpoint to Armed.

The configured Dedicated Voice Task is the App Server target. Native LASTVOICE remains available
only as a dry-run simulation because current compatibility checks cannot guarantee its destination.
Joydex must not navigate Desktop to the task before acquiring it through its own App Server.

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

Joydex will select the Codex App Server runtime as refined by
[ADR 0005](0005-follow-managed-codex-runtime-with-capability-checks.md). For every accepted wake, it
starts a private App Server, resumes the exact configured task, calls `thread/realtime/listVoices`,
and requires a compatible voice catalog before starting device media. This preserves the Dedicated
Voice Task's normal interactive tools instead of accepting an incomplete one-file runtime. Session
activation separately requires the WebRTC data channel, remote audio track, and Voice PE LAN
transport before readiness is reported.
Runtime code will use generated schemas and protocol responses; it will not inspect or parse
`app.asar`, emulate first-party attestation, or rewrite private upstream call requests.

Joydex exposes the owned task through a Room Voice Workspace. `thread/read` with `includeTurns=true`
provides canonical dialogue history, while the active WebRTC data channel provides immediate user
and assistant transcript updates. History refresh uses a separate short-lived App Server that calls
`thread/read` without `thread/resume`, so a refresh does not claim the task writer. The UI keeps its
presentation state in memory and reloads canonical history before and after each session. Realtime
starts with startup context enabled so a later call can use the backing task's bounded conversation
history. Separately, Joydex writes a readable per-session `transcript.md` and
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
pipe that allows bridge status, local task list/read, `send_message_to_thread`, and the narrow idle
Voice-task release above. The release calls Codex's archive and unarchive task operations as one
paired action. The bridge never calls `thread/resume` and exposes no create, fork, or general
ownership operation.
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
Room Voice session. Joydex starts the broker while configuration is open, a messaging feature is
enabled, or the Joydex-owned Voice route needs ownership handoff. Handoff does not enable outbound
voice messaging tools. Its current-user-only transport uses a randomized per-process pipe name. The receiver
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
  state. Joydex holds its writer lock only from accepted wake through Voice Session teardown.
- The App Server process, task `cwd`, runtime root, and session records share one verified Voice Agent
  Workspace; local Codex project identity remains optional grouping metadata.
- Moving to another workspace creates a fresh owned task and leaves the previous task and legacy WAV
  captures intact.
- The Room Voice Workspace remains a readable and recoverable surface for the task, including direct
  End Session and Restart controls. Codex Desktop can open the same chat between calls.
- Desktop-owned tasks can receive explicit outbound Room Voice commands through Desktop's own task
  tool without transferring ownership; unavailable deliveries remain manual-review drafts.
- Typed `thread/realtime/closed` becomes the primary end-of-conversation signal for Joydex-owned
  sessions.
- The long-lived Voice media host is supervised. Each Voice Session owns a private App Server, and
  session teardown closes that process even when media disposal fails.
- An acquisition or compatibility failure rejects that call visibly and returns the endpoint to
  Armed. The next wake makes a fresh acquisition attempt.
- Recoverable Voice PE control-stream protocol failures reconnect instead of permanently ending
  wake monitoring.
- ChatGPT-authenticated full-duplex WebRTC is proven on the host with deterministic audio.
- Codex Desktop and a separate Joydex App Server cannot write the same task concurrently. Desktop
  can keep an idle task loaded after the user navigates away, so navigation alone is not a handoff.
  On wake, Joydex releases that idle Desktop writer through the paired archive/restore operation and
  retries acquisition once. The chat becomes available to Desktop again after hangup.
- JOYDEXOWNER is proven for a Joydex-managed durable task, including rival rejection, release, and
  fresh-process reacquisition. Per-session handoff still requires attended validation with Codex
  Desktop and the physical Voice PE.
- DESKTOPATTACH remains capability discovery on Windows. Parent-owned Desktop stdio cannot be joined
  by another process through a documented transport.
- The PC must remain awake, online, and authenticated to ChatGPT.
- Joydex follows the most recently written structurally complete candidate in Codex Desktop's
  managed runtime folder. A runtime outside that location can still be selected as an explicit
  override.
- App Server Realtime is experimental. Schema and end-to-end media canaries remain regression
  evidence after upstream changes, while ordinary Desktop upgrades no longer wait on a new hash
  allowlist before automatic wake can resume.
- The hybrid Voice PE microphone and Sendspin Opus speaker transport passes a three-minute physical
  duplex soak and repeated attended natural sessions. The best restart canary carried multiple
  intelligible exchanges with no speaker-queue overflow and rearmed for a second session. Spoken
  Hangup eventually closed both sessions; interruption and first-attempt hangup reliability remain
  open attended checks.

## Evidence

- `tools/Joydex.WebRtcCanary` host-only canary
- HANDOFF unit coverage verifies startup-context inclusion, read-only history without
  `thread/resume`, and owner release after media cleanup success or failure
- Exact Codex `0.159.2` read-only probe returned the configured task's history while the old
  production Voice worker retained that task's writer; full per-session Desktop/Voice PE handoff
  remains an attended gate
- Exact-runtime ownership probe acquired the configured task, rejected a rival owner, released it,
  and reacquired it from a fresh process; the deployed Armed worker has no Codex child process and
  Desktop reports the task as `notLoaded`
- Revalidated on 2026-09-10 against Windows package `OpenAI.Codex 26.903.9818.0`, bundled app
  release `26.903`, and Codex `0.153.4`
- Historical validation evidence: Codex `0.153.4` binary SHA-256
  `3d6ca7085c932b62ef4ee4877e92f15b050fb94b2eb8e6c10a346a06248c6004`
- Historical validation evidence: matching `codex-code-mode-host.exe` SHA-256
  `5343b7a0f1645b9bfeef1d15e63facfba3c59ffc48e0f22a0dc53ae6a1a3b9c2`
- Successful `0.153.4` deterministic microphone uplink, remote model-audio track, Windows speaker
  playback with nonzero audio energy, valid 74,175-byte 48 kHz mono Opus WebM capture, and typed
  requested close
- Successful attended Voice PE wake, two user turns, device-speaker replies, spoken hangup, saved
  transcript and diagnostic audio, zero speaker overflow or underruns, and return to `Armed`
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
