# ADR 0005: Follow the Managed Codex Runtime with Capability Checks

- Status: Accepted experimentally
- Date: 2026-09-10
- Supersedes: the executable version and hash-selection clauses of [ADR 0002](0002-use-codex-app-server-realtime-for-dedicated-voice.md)

## Context

Codex Desktop installs its App Server runtime below
`%LOCALAPPDATA%\OpenAI\Codex\bin\<build-id>`. The build directory changes during normal Desktop
updates and an older directory may disappear. Joydex previously stored that full path and accepted
only one hard-coded hash pair for `codex.exe` and `codex-code-mode-host.exe`.

That policy turned every ordinary Codex update into a Room Voice outage even when the App Server
protocol remained compatible. Updating the hash allowlist also required rebuilding and redeploying
Joydex. The pin tied startup to a build that had passed schema and end-to-end media canaries. Joydex
also performs narrower live readiness checks: App Server initialization, exact-task resume, returned
working directory, interactive tool inventory, and the Realtime voice catalog.

Codex can leave newer partial extraction directories beside its active runtime. Selecting a folder
only because it was most recently written can therefore choose an unusable installation.

## Decision

An empty App Server executable setting selects the most recently written structurally complete
candidate in Codex Desktop's managed runtime folder. A previously saved executable path inside that
folder has the same automatic meaning, so existing configurations follow an update even while the
older path still exists or after it is removed.

A managed runtime candidate is structurally complete only when non-empty `codex.exe` and
`codex-code-mode-host.exe` files are present together. Joydex skips partial candidates and ranks
the remaining candidates by their latest runtime-file write time. It does not inspect `app.asar`,
infer provenance from the folder name, or treat discovery as binary authentication.

An executable path outside the managed runtime folder is an explicit developer or canary override.
Joydex uses that exact path, requires its companion code-mode host, and reports a missing or
incomplete override without silently substituting a managed runtime.

Runtime selection has no hard-coded Codex version or executable hash allowlist. Before Room Voice
starts device media, Joydex initializes the selected App Server, resumes the exact Dedicated Voice
Task in its configured workspace, verifies the optional Joydex tool inventory, and requires a
compatible result from `thread/realtime/listVoices`. A genuine protocol or capability mismatch
in this preflight remains visible and terminal. Process and update races continue through bounded
retry. Hashes recorded by canaries are reproducibility evidence rather than a runtime admission list.

Removing the pin intentionally trades some pre-use assurance for update availability. The live
preflight does not exercise `thread/realtime/start`, SDP and data-channel events, remote audio, or
typed session closure. The first Voice Session after an update is the end-to-end runtime check;
failure still tears down the session and returns the endpoint to its error/rearm lifecycle. Host-only
schema and media canaries remain the stronger release-validation path.

The managed folder is user-writable and is not a security boundary. This decision relies on the
same-user Codex Desktop installation and does not claim that structural discovery authenticates a
binary against a hostile local user. An explicit override has the same local trust model.

## Consequences

- Normal Codex Desktop updates do not require a Joydex code change or republish when the required
  App Server behavior remains compatible.
- Existing settings that contain a rotated managed build path migrate behavior automatically; the
  settings schema does not need to change.
- Partial update folders are ignored until both required executables are present and non-empty.
- If no managed bundle is complete during an update race, startup keeps retrying with the existing
  bounded backoff. A missing explicit override remains terminal because substituting another binary
  would violate the operator's selection.
- Upstream breakage in initialization, task resume metadata, tool inventory, or the Realtime voice
  catalog stops Room Voice before device media starts and produces the existing compatibility error.
  Later WebRTC protocol breakage is detected when a Voice Session starts.
- Developers can preserve a fixed canary runtime by copying it outside the managed folder and
  selecting that executable explicitly.
- Host-only schema and media canaries remain useful regression evidence without controlling normal
  startup.
