# Voice PE plugin

Status: proposed. Plugin ID: joydex.voice-pe.

## Outcome

Room Voice starts, listens, speaks, archives sessions and delivers explicitly requested messages independently of a controller and independently of settings. Extract our working aggregate with its current media and task-ownership behavior intact.

The initial plugin contains both existing modes: JoydexOwner and the explicit LastVoiceFallback. Keep their configuration, UI labels and compatibility policies. Do not silently switch to native Last Voice when the dedicated owner fails.

## Existing implementation to move

| Source | Destination responsibility |
|---|---|
| src/Joydex.App/VoicePeBridgeRuntime.cs | Plugin composition, startup and ordered disposal |
| ProductionRuntimeComposition and VoiceProductionOwner | Voice worker supervision with generation-aware publication |
| src/Joydex.Windows/Voice/CodexDedicatedVoiceOwner.cs | Voice-specific App Server owner and writer lock |
| DedicatedVoiceCoordinator, VoicePeControlAdapter | Session lifecycle, wake, hangup, readiness and rearm |
| VoicePeLanAudioTransport, Sendspin transports and speaker session | Media/control path inside worker |
| src/Joydex.App/WebView2VoiceDuplexAudioSession.cs and Assets/Voice | Worker-owned hidden Form/WebView2 on a dedicated STA |
| src/Joydex.App/VoiceSessionArchive.cs | Worker-owned transcript and diagnostic archive |
| RoomVoiceConversationModel and RoomVoiceTranscriptView | Snapshot/timeline contract and visible UI projection |
| DesktopTaskBridgeClient and general bridge contracts | Shared Desktop gateway, with a constrained Voice capability |

Do not move the entire Voice namespace into engine contracts. Audio formats and lifecycle types can stay in the Voice plugin until another actual consumer needs them.

## Runtime and UI contract

The worker owns the dedicated App Server process, microphone/control connection, Sendspin session, media STA, transcript model and archive writer. The ordinary Joydex UI owns only the Room Voice window and draft settings controls.

Expose Start/EndSession, RestartWhenIdle, SetMute, supported live tuning commands, GetSnapshot, ReadTimeline and sequenced timeline/status events. Preserve partial/final transcript distinction and follow-latest behavior. Keep audio frames, raw WebRTC events, private tool arguments and reasoning inside the worker.

Start the STA/message pump before constructing WebView2. Hold that pump for the worker lifetime. Use its own SynchronizationContext for browser creation, microphone posts, callbacks and disposal. Closing Room Voice or Configuration must have no connection to its disposal token.

UI resubscription reads a session snapshot and cursor, then fills a timeline gap from the archive/model. A slow UI may coalesce partial text, but final utterances remain recoverable. Existing viewport preservation and “Latest ↓” behavior remain a UI responsibility.

Register the Dedicated Voice Task exclusion with the shared task-alert service using a lease. Dispose the exclusion on plugin shutdown, without inserting it into the user's ignored-task rules.

## Required ownership boundaries

The Voice Agent Workspace remains the authoritative normalized cwd and archive root. A local Codex project remains grouping metadata for this mode. Preserve the dedicated owner's current explicit filesystem/approval policy; do not make it the default for Discord or other plugins.

Only the Voice owner may resume the Dedicated Voice Task for its realtime sessions. The Desktop gateway never resumes it. Outbound messages to a Desktop Voice Target continue through Desktop's own task operation. Shared task references do not imply shared writer ownership.

Preserve CodexAppServerRuntimeResolver and accepted ADR 0005. Blank configuration, or a saved path inside the Codex-managed runtime directory, follows the newest structurally complete runtime containing nonempty codex.exe and codex-code-mode-host.exe together. Explicit paths outside that directory remain exact overrides. Preserve update-race retries and skip partial managed candidates; an invalid explicit override stays blocked. Versions and hashes are diagnostics, with no version/hash admission list.

Startup initializes and resumes the exact dedicated task/cwd, then validates the interactive tool inventory and voice-list capability when those features are enabled. Actual realtime/start, SDP/data-channel/media behavior and typed close events still need a real session canary. Startup checks alone do not establish them. The singleton Desktop broker remains external to the Voice worker, so a Voice restart does not interrupt Pebble or Discord.

Keep Voice's existing constrained send_message_to_codex_task tool and its per-session instructions. Introducing a general shared Desktop gateway does not broaden that tool inventory.

## Media invariants

Accepted [ADR 0002](../../adr/0002-use-codex-app-server-realtime-for-dedicated-voice.md), [ADR 0003](../../adr/0003-use-sendspin-opus-for-voice-pe-speaker-downlink.md) and [ADR 0004](../../adr/0004-stage-modern-sendspin-player-behind-the-legacy-media-source-abi.md) remain authoritative. [ADR 0005](../../adr/0005-follow-managed-codex-runtime-with-capability-checks.md) supersedes ADR 0002's version/hash admission clauses.

- Port 8765 carries microphone/control; Sendspin on 8927 owns speaker output.
- Keep the production uplink-only handshake. Preserve supported host diagnostics; do not restore removed firmware raw-PCM/UDP routes or archived canary scripts.
- One Voice Session owns one Sendspin stream. Response completion does not restart the decoder.
- Keep 20 ms packet cadence, clocked silence between responses, one-second target lead and existing bounded queue/late-send rules.
- Preserve ordered playback boundaries and draining of accepted audio before stream close.
- A scheduled send/connection failure terminates the session; retrying a suffix on a new stream cannot claim complete audio delivery.
- Preserve barge-in requirements, mute behavior, wake/ready cues, spoken hangup and rearm.
- Preserve cancellation/publication checks so an old startup attempt cannot become the current owner after restart.
- Preserve the accepted Opus priming frame and bounded eight-second speaker startup wait, internal-RAM decoder stack, and disabled no-client API reboot from ADR 0004.

This refactor changes no firmware. Physical acceptance gaps already recorded in the ADRs, including short-reply onset behavior, remain open until separately verified. A clean extraction does not establish improved device audio quality.

## Configuration and persistent state

Keep the current Voice preferences file and normalization. Keep existing workspace paths under .joydex/voice-sessions and .joydex/voice-outbox, along with the independent legacy LocalAppData audio diagnostics. Do not relocate or rewrite prior archives.

Voice endpoint/task/workspace/binary changes default to PendingIdle during a call. Store the desired revision and apply after confirmed session close. An explicit End call and apply command may close immediately. Unrelated configuration changes never stop Voice.

Task provisioning and device tuning are explicit commands, distinct from editing settings. Provisioning creates a new dedicated task only when requested and preserves the old task/archive. A failed probe leaves the active configuration unchanged.

Retain manual recovery for the Voice Message Outbox. Reconnecting the shared Desktop broker does not automatically retry uncertain outbound messages. Preserve the existing short-window dedupe of identical confirmed Voice sends.

## Failure and restart

Move current bounded owner-restart behavior into Voice supervision: retry transient startup/owner failures, cap delay as currently implemented, reset after stability, and keep invalid explicit runtime/config/capability errors visibly blocked. Re-resolve managed runtimes at the existing safe owner-start boundary; do not switch an active call to a newly installed runtime.

On orderly shutdown: stop accepting wakes, close/drain the session, complete the archive, dispose observer/control/media, release the dedicated owner, then stop the STA. Disposal is idempotent. An engine restart terminates stale workers and waits for ownership release before another owner starts.

If the settings process disappears, do nothing to media. If WebView2/media fails, end that session visibly and recover the Voice worker according to its own policy. If the Desktop gateway fails, ordinary voice can continue; outbound messages become manual-review drafts.

## Extraction slices and tests

1. Introduce injectable host capabilities around paths, task exclusion, activity and Desktop messaging while retaining the current in-process aggregate.
2. Move owner supervision out of the tray. Verify existing startup, cancellation and session tests.
3. Introduce a dedicated media STA while still in one process; verify audio canary and UI responsiveness.
4. Move the aggregate to its worker. Replace visible Form callbacks with typed commands/events.
5. Verify restart, archive continuity and unchanged wire behavior; then remove tray-owned Voice lifetime code.

Existing anchors include VoicePeControlAdapterTests, DedicatedVoiceCoordinatorTests, CodexDedicatedVoiceOwnerTests, CodexRealtimeSessionTests, VoicePeSendspinAudioTransportTests, VoiceAudioBridgeContractTests, VoiceSessionArchiveTests and RoomVoice timeline tests.

Also preserve CodexAppServerRuntimeResolverTests, including partial installs, explicit overrides and update races. Validate the published package using scripts/Publish-Joydex.ps1: the Desktop bridge executable, WebView2 loader and required notices must travel with the app. A source-directory launch does not prove that package works. Firmware remains source-only and unchanged by this extraction.

The decisive new attended scenario is one multi-response call while Configuration opens, applies an unrelated binding change, closes, and the UI process restarts. Session ID, owner process, microphone continuity and Sendspin stream must remain continuous. Also test shutdown during Starting, stale startup completion, disconnected UI, native fallback and unavailable Desktop messaging.
