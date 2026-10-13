# Codex Windows 26.1007 compatibility

Validation on 2026-10-11 confirms the installed command catalog, declared Windows shortcuts,
alias normalization, and the App Server schema used by Joydex. Room Voice's current request
still matches the generated Realtime V3 schema. Dots use a separate conversation surface;
the implemented integration and physical acceptance are described in
[Dots voice integration research](DOTS_VOICE_INTEGRATION_RESEARCH.md).

## Installed versions

| Component | Verified value |
|---|---|
| Windows package | `OpenAI.Codex 26.1007.2314.0` |
| Bundled app release | `26.1007.21434` |
| Bundled and managed Codex runtime | `0.162.0-alpha.17.2` |
| Joydex validation SDK | `8.0.423` |

The packaged Windows binaries and the managed runtime selected by Desktop have matching hashes:

| File | SHA-256 |
|---|---|
| `app.asar` | `C5FDEF328CFA2AD9095786F9C3A7AAA447E68834D31299D6EF2592DA02029038` |
| `codex.exe` | `D13914CED6C7AF174D8D638DB938284231227312104B06A25E9608C632A7DAEC` |
| `codex-code-mode-host.exe` | `BABAF50818BCE44CEBAFFF9BCE1F517E441BCCBD6468EEF9B3029B51470209E4` |

Archive inspection was a development-time check. Joydex continues to load a checked-in catalog
and discover complete managed runtime bundles as described in [ADR 0005](adr/0005-follow-managed-codex-runtime-with-capability-checks.md).
Runtime code never reads `app.asar`.

## Commands and Windows defaults

The installed catalog in `.vite/build/src-BJv5Mk7n.js` contains 182 command IDs, up from the
176 recorded for 26.930. All 29 command IDs mapped by Joydex remain present. Evaluating the
catalog's `YG` helper with `isMacOS=false`, both numeric layouts, and the unspecified-layout
case produces 105 commands with declared defaults. The unspecified case retains the three
mode shortcuts that the helper suppresses when a numeric layout is explicitly selected.

The new inventory is [CodexWindows261007.json](../src/Joydex.Windows/Actions/Compatibility/CodexWindows261007.json).
Equivalent numeric key sets retain their previous ordering. Two default sets changed:

| Command | Previous Windows default | Current Windows default |
|---|---|---|
| `quickChat` | `Ctrl+Alt+N` | Command removed |
| `browser.toggleAnnotationMode` | None | `Ctrl+Shift+.` |

The annotation chord participates in conservative provisioning collision checks, including
for commands Joydex does not map. Existing user overrides and explicit removals retain their
meaning. The older inventory remains as historical evidence.

- `openOrbit` remains `Ctrl+.` and opens the dot surface.
- `openAvatarOverlay` remains `Super+Alt+P` (`Win+Alt+P`).
- `openSideChat` remains `Ctrl+Alt+S`; `toggleSidebar` retains `Ctrl+Shift+S` and `Ctrl+B`.
- `composer.startVoiceMode`, `realtimeVoice`, and `realtimeVoice.endCall` have no declared
  Windows default. Opening a dot does not supply a verified target-specific call operation.
- `composer.startDictation` remains absent. The accessible composer-control repair and its
  earlier physical validation are recorded in [the dictation note](CODEX_WINDOWS_26_930_DICTATION_VALIDATION.md).
- Numeric task/tab defaults and overlapping previous/next shortcuts still require our
  conservative treatment. A declared chord alone does not establish renderer scope precedence.

## Aliases and precedence

The installed `iK` alias map and `AK`/`kK` normalization helpers were evaluated in an isolated
JavaScript context with Windows platform selection. All ten existing fixture inputs produced
their recorded outputs, including canonical overlay precedence, close-tab/window expansion,
and legacy dictation hold/single-tap migration. Current provenance is saved in
[CodexWindows261007Aliases.json](../tests/Joydex.Tests/Fixtures/CodexWindows261007Aliases.json).

Static inspection also confirms that `ZG` resolves explicit bindings before defaults, treats
a null binding as removal, and filters selected default chords when an explicit binding claims
them. `openOrbit` is among those filtered defaults. The new browser annotation default is also
filtered, and its collision comparison normalizes `>` to `.`. These helpers do not establish
which focused renderer scope receives an injected chord. Joydex keeps its existing explicit,
conditional, and task/tab collision safeguards.

## App Server voice surface

The exact managed binary generated its schema with:

```powershell
& $CodexExecutable app-server generate-json-schema --experimental --out $SchemaDirectory
```

The bundle retains `thread/realtime/start`, `listVoices`, `appendAudio`, `appendText`,
`appendSpeech`, and `stop`, plus typed started, SDP, transcript, output-audio, error, and
closed notifications. `ThreadRealtimeStartParams` requires `threadId` and `outputModality`.
Joydex already supplies both, with `version="v3"`, `outputModality="audio"`, startup context,
and a WebRTC SDP offer in [CodexRealtimeSession](../src/Joydex.Windows/Voice/CodexRealtimeSession.cs).

The schema also exposes `existingCall` transport, `clientManagedHandoffs`, and V3 `initialItems`.
An existing-call identifier means a call the client has already created and negotiated;
it does not provide dot discovery or a way to create a call with a selected dot. `threadSource` on
`thread/start` is described as an analytics classification, so setting it to `orbit` would
not select our existing dot or its memory. No dedicated dots method was found in the
generated request method catalog.

Subsequent desktop-client inspection found a separate native-dot WebRTC create/attach/stop
protocol and legacy `getAuthStatus` authentication helper. A read-only managed-runtime probe
accepted `getAuthStatus` with `includeToken=false` and `refreshToken=false`, reported ChatGPT
authentication, and returned no credential. This legacy method is absent from the generated
request schema. A subsequent host canary used cached ChatGPT authentication and the native dot
protocol to exchange audio with a selected dot and stop cleanly, with a separate HTTP signaling worker
and local-media-first teardown. This does not establish that task-scoped Realtime or `existingCall`
selects the dot's identity. The separate production dot adapter subsequently passed physical Voice PE wake,
conversation and spoken hangup. Details and limits are in
[Dots voice integration research](DOTS_VOICE_INTEGRATION_RESEARCH.md).

The official [App Server documentation](https://learn.chatgpt.com/docs/app-server) describes
the JSON-RPC transports and marks the app-server command and WebSocket transport experimental.
The verified local Desktop App Server still launches without a `--listen` argument. New
transport options in the documentation do not imply that this process offers a joinable
Desktop Attach Endpoint.

## Validation and practical limits

The audited unattended filter ran `CodexKeybindingServiceTests`, `CodexRealtimeSessionTests`,
`CodexAppServerRuntimeResolverTests`, and `CodexDedicatedVoiceOwnerTests` with .NET SDK
8.0.423: **90 passed, 0 failed, 0 skipped**. These classes use temporary files and fake
protocol/media boundaries; they do not invoke native dictation or start a physical voice call.

This establishes static protocol compatibility and our existing lifecycle behavior. Fresh
task-scoped WebRTC negotiation, remote audio, Voice PE wake/playback, spoken hangup, and Desktop
bridge delivery on this build remain attended acceptance checks. Native dot voice now has separate
physical acceptance, recorded in the dots research. The older physical evidence in
[ADR 0002](adr/0002-use-codex-app-server-realtime-for-dedicated-voice.md) remains historical.

### Supplemental compatibility review

A follow-up review on 2026-10-11 reconfirmed the installed archive and both managed-runtime
hashes above. The previous 2026-10-06 baseline was package `26.930.7945.0`, app `26.930.61225`
and runtime `0.160.1`; no new drift was found after the current validation.

The committed bridge's read-only `--probe-desktop-app-server` operation recovered the running
Desktop App Server's app-tools pipe from its environment, initialized the packaged adapter and
loaded its required `list_threads`, `read_thread` and `send_message_to_thread` catalog. This probe
sent no task message and did not archive, resume or change a task. Catalog availability does not
establish live delivery or the archive/restore ownership handoff.

An additional audited filter passed **174 tests**, with no failures or skips, against the clean
committed snapshot. It covered Desktop task transport and broker process behavior, hook
configuration/relay, dictation controls, action dispatch, App Server messages, conversation reads,
Realtime lifecycle, dedicated ownership and managed-runtime resolution. Physical controls and
service failure recovery remain separate checks.

The asynchronous-user-input status gap was confirmed and corrected on 2026-10-13. The exact
runtime emits `PreToolUse` and `PostToolUse` for `request_user_input_async`; its completion output
is an acknowledgment, not an answer. Joydex now tracks accepted questions separately and clears
each only on its corresponding question-card reply. Pending attention survives turn completion
and restart. The evidence and deployment requirements are recorded in
[the hook validation](CODEX_WINDOWS_26_1007_HOOK_VALIDATION.md).

Remaining practical checks are current-build physical throttle dictation; legacy Voice PE calls
and saved-thread ownership after switching engines; Desktop message delivery and idle-writer
handoff; and dot startup cancellation, mute/interruption, remote closure and endpoint loss.
These are acceptance gaps, not demonstrated regressions from 26.930.

For public release context, the [official changelog](https://learn.chatgpt.com/docs/changelog)
was checked on the same date. The installed package and binary observations above determine
this Joydex compatibility baseline.
