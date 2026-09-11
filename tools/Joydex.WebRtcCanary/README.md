# Joydex WebRTC Canary

This is a host-only compatibility tool. It answers three App Server questions:

> Can a Joydex-owned client start Codex App Server Realtime on the configured Dedicated Voice Task, complete a browser WebRTC audio negotiation, receive model audio, and observe typed session closure?

> Can Joydex keep a durable task loaded in its own App Server, reject a rival App Server writer,
> release ownership cleanly, and allow a new owner to resume the same task?

> Does the already-running Codex Desktop App Server expose a documented endpoint that Joydex can
> join without starting another App Server?

## Ownership and Desktop-attach result — 2026-08-25

**The JOYDEXOWNER mechanism passed.** Against upstream Codex `0.150.0-alpha.9`, the automated prototype:

- launched a Joydex-owned App Server over its supported stdio transport;
- created and named a disposable durable task;
- kept that task loaded on the owner's live control connection;
- observed a separate App Server fail with `already has an active writer`;
- stopped the owner and successfully resumed the task from a fresh App Server; and
- deleted the uniquely identified canary task after the handoff proof.

The follow-up probe resolved the configured Desktop-owned Voice Task and confirmed that Codex
Desktop currently owns its writer. Device-specific task IDs are intentionally omitted.
This means JOYDEXOWNER is not ready on the configured task in the current Desktop run. The no-turn
ownership probe temporarily acquires the writer when the task is available and may initialize its
configured integrations; in the observed Desktop-owned case it was rejected before acquisition and
changed no task content.

The writer guarantee is loaded-task scoped. Upstream unloads an idle task after 30 minutes when it
has no subscribers, so product JOYDEXOWNER must retain its stdio connection and subscription across
voice sessions. The upstream test suite independently proves the same two-process conflict and
handoff sequence.

**DESKTOPATTACH found no supported endpoint in the installed Desktop build.** Desktop package
`26.818.5229.0` launched its App Server as PID `31096` without `--listen`, which makes its effective
transport the documented `stdio://` default. The process had no TCP listener. Those stdio streams
belong to the Desktop parent process, so an external Joydex process has no documented transport to
join.

Codex's explicit `ws://` listener remains a useful upstream diagnostic, while the official App
Server README labels it experimental and unsupported for production. The managed daemon/control
socket path is Unix-only and therefore does not solve Windows Desktop attachment. DESKTOPATTACH
stays as a discovery probe. A future declared endpoint remains a candidate until the prototype also
connects, completes App Server initialization, and verifies that the observed Desktop process owns
the endpoint.

Run both prototypes from the repository root:

```powershell
.\scripts\run-appserver-ownership-prototypes.ps1 -CodexPath C:\path\to\codex.exe
```

The ownership prototype is an interactive terminal state display. Press `O` for the disposable
owner/handoff proof, `P` for a no-turn configured-task ownership probe, and `Q` to continue to the Desktop
discovery result. Pass `-Auto` to run both ownership actions without input. Exit code `0` means both
the mechanism and configured-task acquisition passed; exit code `3` means the mechanism passed while
another App Server still owns the configured task. The runner accepts `3` and labels it explicitly.
The required `-CodexPath` identifies the exact App Server build under test; the runner reports its
version and SHA-256 in the result.

## Dedicated Joydex task — 2026-08-25

The `dedicated-create` mode created `Joydex Voice Chat - Owned`. It released the creator's writer,
then a fresh App Server
resumed the exact ID without starting a turn. The task is intentionally absent from the native
fallback's `voice-pe.json`; navigating Desktop to it would let Desktop acquire its writer before
JOYDEXOWNER starts.

A temporary task named `Joydex Voice Chat` was created through Desktop during setup and immediately
proved Desktop-owned. It is archived and is not a valid JOYDEXOWNER target.

## Upstream result — 2026-08-25

**Yes for full-duplex WebRTC with ChatGPT authentication.** A verified standalone copy of upstream Codex `0.150.0-alpha.9` accepted deterministic microphone audio, transcribed it into a user turn, returned decoded Opus model audio, and emitted `thread/realtime/closed` with reason `requested`. The Voice PE was not changed.

The upstream release executable SHA-256 was `5ffd7a27694e1529d717a0247858d7650438273ba10d4d8a4f0a73f5e1414082`. `codex login status` reported `Logged in using ChatGPT`. The isolated child removes `OPENAI_API_KEY` from its environment, reads neither project `.env`, and calls the normal upstream endpoint directly. The old localhost compatibility proxy has been removed.

Attestation capability was disabled for this baseline, matching an ordinary third-party client that cannot mint a first-party token. The upstream request succeeded without one. Observation mode remains available to prove whether a future App Server asks the client for `attestation/generate`; it returns an explicit unsupported error and never fabricates a token.

The first run resolved and resumed the configured Desktop-owned Voice Task, proving explicit-task routing and microphone uplink before Codex Desktop reacquired that task's writer lock. The repeatable media run used an ephemeral App Server task so it could measure audio without changing or contending with a durable task. Nine observable checks passed:

- Realtime v3 session accepted.
- SDP answer returned.
- WebRTC media connected.
- Deterministic 24 kHz mono microphone fixture sent through the outbound audio track.
- Fixture transcribed as `Please say exactly, Joy Deck's PCM round trip connected.`
- Model answered `Joy Deck's PCM round trip connected.`
- Remote Opus track decoded through the Windows speaker path.
- Browser capture wrote a valid 48 kHz mono Opus WebM file.
- Explicit stop produced typed closure with reason `requested`.

The successful receiver reported 1,281,120 decoded samples and nonzero audio energy. Its 47,343-byte capture converted to 24 kHz mono PCM and local transcription returned `Joydex PCM Roundtrip Connected.` Attaching the remote stream to an autoplay `<audio>` element was required to activate Chromium's decoder; an analyser and `MediaRecorder` alone received RTP packets without producing decoded samples.

The installed Codex Desktop App Server later held the configured Voice Task's local writer lock. A separately launched App Server then failed closed with `thread ... already has an active writer`. Joydex therefore needs an explicit task-ownership design: either the Joydex App Server owns the Dedicated Voice Task for its lifetime, or Joydex reaches the already-running Desktop App Server through a supported transport. Spawning a second App Server on demand cannot be assumed to coexist with Desktop ownership.

## Installed-runtime revalidation — 2026-09-10

Codex `0.153.4` from Windows package `OpenAI.Codex 26.903.9818.0` was revalidated after the Desktop
upgrade. The generated experimental schema retained `thread/realtime/start`, `stop`, `listVoices`,
`started`, `sdp`, and `closed`, including the `realtimeStartInstructions` field used by Joydex.

The deterministic ephemeral-task WebRTC canary then completed SDP negotiation, microphone-fixture
uplink, remote model-audio reception, playback through the Windows speaker path with nonzero energy,
a valid 74,175-byte 48 kHz mono Opus WebM capture, and typed closure with reason `requested`. The
verified executable SHA-256 was
`3d6ca7085c932b62ef4ee4877e92f15b050fb94b2eb8e6c10a346a06248c6004`; the matching
`codex-code-mode-host.exe` SHA-256 was
`5343b7a0f1645b9bfeef1d15e63facfba3c59ffc48e0f22a0dc53ae6a1a3b9c2`.

The production build using that validated runtime then reacquired its owned task and completed an attended Voice PE
session from wake through spoken hangup. Two user turns produced device-speaker replies, the
continuous speaker lane reported 588 frames with zero overflow and zero underruns, the transcript
and diagnostic WAV files were written, and the device returned to `Armed` with wake inference on.

These hashes preserve the exact revalidation evidence. Production runtime selection now follows the
most recently written structurally complete Codex Desktop candidate and uses live App Server capability
checks; the hashes are not an admission list.

## Installed-build baseline — 2026-08-25

The earlier standalone run with installed Codex `0.149.0-alpha.4.1` resolved and resumed the configured Voice Task but failed before SDP because its legacy call-creation request no longer matched the live endpoint:

Observed with the installed Codex `0.149.0-alpha.4.1`:

- The unmodified AVAS session object was rejected because `session.model` is not allowed.
- Removing every client `model` field produced the same rejection.
- Reducing `session` to `{}` still produced the same `session.model` rejection.
- Omitting `session` was rejected because the field must be an object.

The current canary retains this result as a compatibility baseline and contains no proxy or request rewriting.

A localhost page supplies the Windows microphone, speaker, echo cancellation, and `RTCPeerConnection`. The C# host persists no credentials and reads no Codex logs; it launches a separate authenticated App Server process and uses only `thread/list` plus `thread/realtime/*` JSON-RPC.

Run from the repository root:

```powershell
.\scripts\run-webrtc-canary.ps1
```

By default the runner copies the installed Codex package's `codex.exe` to a versioned temporary directory because Windows package ACLs do not permit launching the package binary directly from a normal shell. Pass `-CodexPath` to test an isolated upstream executable, `-AttestationMode observe` only for the explicit attestation diagnostic, and `-NoOpen` when a browser-native test client will open the page itself. It registers the localhost process as a session-scoped development server, then opens the canary page unless `-NoOpen` is set. **Start canary** negotiates with a muted synthetic input; **Use live microphone** performs the separate permission and capture check. Say “hang up” after attaching the microphone, or press **Stop session** to test closure.

Pass `-InputWav` to expose a deterministic WAVE fixture, `-CaptureWebm` to save the remote track, and `-EphemeralThread` to run the media canary without creating or modifying a durable task. Pass `-ThreadId` or `-ThreadTitle` only when deliberately testing task ownership and routing.
