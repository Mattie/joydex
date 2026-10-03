# Implementation milestones and demos

Current direction, 2026-09-12: continue implementation without agent-driven demos.
Use background-safe checks and retain uncompleted attended scenarios as explicit
verification gaps. The historical demo instructions and results below remain evidence.

Implementation authorized 2026-09-11 using OVERSEERWAVES. Stop after each sizable
UX-visible milestone, demonstrate verified capabilities, and wait for user feedback
before starting the next milestone. The overseer owns scope, integration and the
acceptance summary; implementation agents own bounded details in separate worktrees.
Report readiness in chat before an interactive demo. Start desktop interaction only
when the user explicitly asks to begin; background validation must not open windows.

Before each demo, explain the original product behavior, the visible difference in
this milestone, and exactly what each step tests. State the expected result before
acting. Distinguish new capabilities from regression checks of existing behavior,
and identify simulated inputs and anything the walkthrough cannot establish.
Finish with observed results and a focused request for feedback.

## Milestone 1 — Keep settings open while Joydex works

Status: attended capture/Cancel/reopen demo completed. User reported that it seemed
fine and requested clearer before/after framing and explicit test purposes in demos.
Common source/design checkpoint: `185b828`.

This is the same-process HOSTSEAM milestone. Opening and cancelling settings should
leave Voice, PAD, controller bindings and Pebble operating. Binding capture observes
the runtime's input source and suppresses the captured gesture safely. Unrelated
controllers keep working. Settings remains an editable draft until saved.

The later PROCESSUI milestone adds survival of a settings-process restart. Plugin
packaging, Secrets and Discord follow subsequent feedback gates.

### What changed from the original product

Previously, opening Configuration stopped the controller workers, Room Voice and
wireless-panel adapter before showing a modal settings dialog. This milestone keeps
the runtime alive while a modeless settings window edits a separate draft.

The attended demo exercised three checks: simulated input continued with settings
open; Capture updated the draft using runtime input; Cancel closed settings and
reopening restored the saved binding. Capture and Cancel already existed, so those
steps checked their behavior under the new runtime arrangement. Shared-key safety
and exact worker continuity have automated test evidence. Physical Voice/PAD
continuity was not demonstrated with the simulated controllers.

### Acceptance and demo

| Capability | Evidence required before calling it working |
|---|---|
| Open, interact with and cancel settings | Runtime session/worker identities remain stable; no stop caused by opening |
| Continue ordinary controller actions | Existing foreground/dry-run/hold behavior preserved while settings is open |
| Capture a binding safely | Runtime observes input once; capture consumes its gesture through release; no normal action fires from it |
| Start capture with an action already queued | Capture activation forms a dispatch barrier; a stale queued action cannot execute after capture is acknowledged |
| Hold a control before capture | Pre-existing hold cannot become a captured press; fresh eligible input is required |
| Use two devices with the same held chord | Capture/disconnect of one source preserves the other source's hold |
| Close settings during capture | Lease releases and ordinary input recovers without replaying suppressed input |
| Save or cancel drafts | No implicit task provisioning, token rotation or adoption of external candidate settings on Cancel; failed saves preserve the draft |
| Save an unrelated binding change | Unchanged Voice/Pebble integrations retain their runtime; a Voice-affecting save during a call requires an explicit stop-and-apply decision |

Use deterministic input/runtime integration tests and a packaged Windows smoke
check. Demo actual settings behavior with synthetic inputs where useful, clearly
distinguishing synthetic evidence from attended hardware and Voice continuity.
Document any unexercised physical checks before asking for feedback.

### Ownership

- Input/runtime lane: input observation, capture, held-key ownership and targeted tests.
- Baseline lane: existing test results, settings parity inventory and safe demo route.
- UI/composition lane: assigned after the input API is reviewed; sole delegated writer
  of settings/tray composition files during that slice.
- Overseer: shared integration, design status, milestone scope and release readiness.
- Independent reviewer: exact integrated diff, regression risks and test quality.

### Evidence and feedback

Baseline at `185b828`: SDK 8.0.423, Release solution tests **846 passed, 0 failed,
0 skipped** (817 Joydex tests and 29 wireless-panel tests). Run from the isolated
baseline worktree using the verified absolute SDK executable; no existing failures.

Integrated runtime/capture/modeless checkpoint `42d9c46`: **869 passed, 0 failed,
0 skipped**. Independent review accepted the subsequent `513a3fd` checkpoint after
fixes for embedded capture disposal and settings-only Desktop bridge cleanup.
The post-fix focused suite passed 58 tests.

The solution suite at `519cd9a` passed **890 tests, 0 failed, 0 skipped** (861 Joydex
and 29 wireless-panel tests), independently run with SDK 8.0.423. The real
`TrayApplicationContext` test checks exact worker identities and source generations
before opening settings, while settings is open, and after closing. Capture tests
cover held controls, dispatch barriers, disposal, device removal and shared chords.

The packaged walkthrough captured a fresh simulated press and changed a draft
binding from button 120 to button 12. It also exposed a modeless Cancel oversight:
setting `DialogResult.Cancel` alone did not close the window. Commit `0057dce`
adds explicit closure and updates the real Tray regression to click the actual
Cancel button, checking form disposal and unchanged workers/source generations.
The focused UI suite passed **6/6** in the implementation worktree before desktop
work was paused. Independent source review accepted the integrated fix. The full
890-test run predates this final fix.
At `0057dce`, a separate Release solution build passed with no warnings or errors,
and 17 background-safe tests passed. These cover demo launch policy, synthetic
sources and unchanged preference comparison. UI tests were not repeated because
they display windows and the user was using the PC.

Independent review accepted the runtime/settings changes and the synthetic demo
route. The Windows package build at `0057dce` succeeded, including the app, Desktop bridge,
native relay/Guardian and wireless-panel configurator. Guardian publishing emitted
an IL2104 trimming warning from HidSharp; there were no build errors.

The demo uses a separate instance with synthetic joystick sources and disabled
external integrations. The existing Joydex instance remains running. This exercises
the actual settings and controller runtime code; it does not verify physical
DirectInput or attended Voice/PAD media continuity.

The updated local package is `artifacts/Joydex/live-settings-m1-0057dce-win-x64`.
Launch only when the user is ready, with `--demo --config` pointing to an existing
absolute scratch configuration outside the normal Joydex data directory, with
`safety.dryRun` enabled. Demo input comes from two simulated sources; no physical
controller input is needed. Existing production Joydex can remain running.

After the user explicitly started the attended demo, the updated package captured
button 12 into the draft, and clicking the actual Cancel button closed settings.
The scratch configuration still contained button 120, and both demo and production
processes remained running. The desktop tool could not target the tray control;
reopening was handed to the user through the separately labeled Joydex — Demo icon.
The user reopened settings, and the displayed binding was confirmed as button 120.
Saving a scratch change remains an unexercised part of the attended walkthrough.
Stop for feedback after this milestone. Physical Voice/PAD continuity and
settings-process survival remain unverified; process separation belongs to the next
milestone.

## Milestone 2 — Restart settings without interrupting Joydex

Status: implementation checkpoint accepted for continued development, 2026-09-12.
The packaged inspector opened at `a52175a` after two attended startup fixes.
Capture, Apply, Settings-process restart and physical integration continuity were
not completed in the walkthrough. The user stopped desktop automation and explicitly
requested continued implementation without an agent-driven demo. These remaining
checks stay open; continuing to the plugin pilot does not mark them passed.
This is PROCESSUI, implemented from the `728a48a` baseline after milestone 1 feedback.

### Before, after and test purpose

Originally, Configuration stopped controller workers, Room Voice and the wireless
panel while the dialog was open. Pebble had a separate lifetime and stayed running.
Save closed the dialog. Milestone 1 keeps integrations running with a modeless editor,
but settings and runtime still share a process. Milestone 2 gives settings its own
process and client connection. Input, media and inbound integrations keep their
owners when that client disappears. Apply saves a draft without closing the window,
and a stale draft must be rejected instead of overwriting newer settings.

| Demo step | Original behavior | What this step tests | Expected result |
|---|---|---|---|
| Open settings during simulated input | Opening Configuration stopped controller workers | Preserve milestone 1 continuity across the new process boundary | Activity continues with the same runtime/source identities |
| Apply a binding edit | Save closed the editor and restarted workers | New Apply commits through the host while the editor remains open | New binding takes effect; unrelated owners stay running |
| Terminate only the demo settings process | Settings shared the runtime process, so process loss ended both | New separation of editor and runtime lifetime | Simulated input continues; runtime process and sources are unchanged |
| Reopen settings | Recovering from process loss restarted the whole application | Reconnection reads current saved state from the surviving host | Saved binding and current health appear without reacquiring devices |
| Interrupt capture by losing its client | Capture and runtime shared a process | Connection loss releases the capture without ending the runtime | Fresh ordinary input recovers without replaying the captured gesture |
| Try Apply after another editor saves | Save had rollback on failure but no host revision check | New draft review protects edits made since the draft began | Apply requires review; the unsaved draft and newer saved settings remain available |

For the final step, leave a binding edit unsaved in Configuration, save a separate
prompt edit through the tray's **Prompt pickers...** editor, then return to
Configuration. Check that it retains the binding edit and requires Review/Rebase
before Apply. This exercises two client editors sharing one authority. The automated
host checks separately verify rejection of an actual stale-revision request.

Prepare a fresh scratch copy of `config/joydex.advanced.example.json` outside the
normal Joydex data directory. Set the top-level and `cm3` selectors to
`Simulated Joydex CM3`, and the `alpha-warbrd` selector to
`Simulated Joydex WarBRD`. Keep the profile IDs/templates. Change only the CM3
`Joystick left - Back` binding's button from 12 to 120. Keep both `safety.dryRun`
and `safety.requireCodexForeground` true so the saved configuration remains valid.
The demo runtime projects foreground gating off and records dry-run activity.

After the user begins the walkthrough, launch the packaged App with
`--demo --config "<absolute-existing-scratch-config.json>"`. It opens the demo tray
and then Settings automatically. The simulated CM3 pulses button 12 and the WarBRD
pulses button 1 in a five-second cycle. Capturing `Joystick left - Back` changes its
draft from 120 to 12; Apply should keep Settings open. Reopen **Advanced > Test
controls** after Apply because that window snapshots active bindings when created,
then clear its replayed results and wait for a fresh pulse. The normal runtime can
remain running throughout this isolated demonstration.

Use real local pipe/process tests with synthetic owners for unattended checks.
Background validation must not display windows, launch real integrations or touch
normal Joydex state. The visible demo starts only on the user's explicit request.
State separately which media/physical checks remain for an attended canary; a
simulated owner cannot establish continuous physical Voice/PAD operation.

Keep the current UI framework, existing settings paths, managed Codex runtime policy
and media protocols. Plugin packaging and new integrations remain later milestones.
Record the accepted contract, implementation revisions, actual checks and remaining
limits here as this milestone progresses.

### Implementation checkpoints

These record intermediate gates; the final M2 checkpoint below supersedes their
earlier pending statuses.

The [PROCESSUI contract](13-process-settings-contract.md) and ADR 0009 freeze the
behavioral boundary. The runtime-owned Voice STA slice is integrated at `67584c8`
(reviewed builder revision `361c317`). The pinned-SDK Release suite for that slice
passed **124 tests, 0 failed, 0 skipped**; independent review also repeated the eight
focused lifetime tests. Partial startup cleanup and logging-failure paths were
corrected during review. RuntimeHost composition must treat unconfirmed ownership
cleanup or an unjoined STA as terminal and must not start a replacement generation.
Real WebView2/media continuity remains an attended check. Settings IPC, Apply and
the complete process boundary are still in progress.

The settings-authority slice is integrated at `8844d63` (reviewed builder revisions
`15d71e5` plus `f1c3729`, followed by test-only friend-assembly declarations).
The root pinned-SDK Release run passed **15 tests, 0 failed, 0 skipped**. Review
caught and corrected an external-edit race during backup capture. Tests cover
revision conflicts, handled rollback, journal recovery, operation replay, event
history and isolation of active state from pending desired settings.

Independent coordinator child-process tests are integrated at `196ee21` (reviewed
builder revisions `fda0173` plus `d132a61`, with both projects registered in the
solution). The root Release solution build passed with no warnings or errors, and
the process suite passed **6 tests, 0 failed, 0 skipped**. The checks cover process
loss before writes, after one file in a two-file commit, after desired-state commit,
and after a completed operation whose reply was lost. They also cover external-edit
conflicts and pending Voice isolation across restart. The partial-write checks
inspect actual intermediate file bytes before recovery. This proves coordinator
recovery using temporary stores; actual RuntimeHost/client-loss continuity is still
pending composition and pipe integration.

External-edit detection uses snapshots and fingerprints. It does not provide a
strict no-clobber guarantee against an arbitrary editor writing after the final
backup capture; see the [contract limitation](13-process-settings-contract.md).
The host becomes the sole application writer when composition is complete.

Capture and existing-action contracts are integrated at `ca537ec` (reviewed builder
tip `2cba9ff`). Review corrected connection-scoped capture delivery and preserved
existing Voice action behavior, including List/Create against an unsaved custom
app-server path. The root Release solution build passed with no warnings or errors;
the 15 coordinator and six child-process tests also passed after integration.
These are contract and regression checks. Their RuntimeEngine, pipe and UI
implementations still require the full client-loss acceptance scenario.

The pipe transport is integrated at `74cc56c` (reviewed builder revisions `1f504e7`
plus `6f659ce`, with source and tests registered in the solution). The root Release
solution build passed with no warnings or errors, and the transport suite passed
**21 tests, 0 failed, 0 skipped**. Real local pipe checks cover peer verification,
ticket-bound roles, attach gating, protocol negotiation, bounded request admission,
cancellation, disconnect and callback delivery. These tests use one process with
isolated pipe endpoints; they do not replace the actual RuntimeHost/client process
acceptance gate. Subsequent reviewed changes at `eff1ed2` and `4cfb18d` add an
idempotent per-connection abort and bind endpoint identity to the exact selected
configuration file. The root transport suite now passes **24 tests**. A two-client
test proves aborting one connection leaves the other usable; path tests distinguish
custom filenames while accepting equivalent Windows paths.

Dependency notices are integrated at `55d83c9` (reviewed builder revision `ecf4d21`).
The review matched 11 redistributed library entries, package-provided notices and
source licenses at their package-recorded commits. System.Memory and Unsafe use
empty assets for this target and add no separate redistributed DLL. Copying these
notices and verifying the complete executable package remain packaging gates.

The runtime engine is integrated at `ff1aa8c` (reviewed builder revisions `30e1aeb`
plus `9202d73`). The root host suite passed **15 tests**: nine engine checks and the
six coordinator child-process checks. The separate 15 coordinator tests also passed
after this integration. Callback delivery failures end the connection and release
its captures; running command admission remains bounded when callers cancel their
RPC waits. Independent review repeated the concurrency checks and verified direct
callback failure in a disposable test. Actual Program wiring must supply the abort
delegate and endpoint identity. Production runtime composition, client UI wiring,
real executable continuity and packaging remain in progress.

RuntimeHost build support is integrated at `65b1e66` (reviewed builder revision
`07464f5`). It compiles the existing Voice, Pebble and Desktop broker sources into
the host without referencing the App assembly. Independent host publish checks
confirmed the WebView2 loader and Voice assets. The solution test project uses an
assembly alias at `019b237` to distinguish the shared internal types. The root
Release solution build passed with no warnings or errors; the host suite passed
**15 tests**, and the background-safe settings/Voice suite passed **139 tests**.
Whole-package publishing and real executable acceptance remain pending. Integration
review identified ownership-guard disposal and capture-observation cleanup issues.
Their reviewed fixes are integrated through `52e42b9` (builder revisions `4ce26c1`,
`1effc89` and `888a1ae`); the root host suite now passes **18 tests**. The race check
holds observation startup while the client begins disposal and verifies exactly
one release. A private test mutex checks exclusion and release without touching
the production guard.

The active-settings constructor for task alerts is integrated at `52bd2f2`
(reviewed builder revision `1906fbf`). Its root suite passed **17 tests**, including
a sentinel-file check that startup neither reads nor rewrites desired preferences.
Production activation still must use the settings authority for subsequent edits.

The precise Voice cleanup-failure signal is integrated through `292e379` (reviewed
builder revisions `a2c9e48` and `0959cb8`). It also covers failed cleanup inside STA
startup, before the outer owner receives the STA reference. The root background-safe
Voice suite passed **125 tests**. Production must use that signal to prevent retries
after uncertain cleanup. Startup activation now follows TaskAlerts, Companion, Voice,
then PebbleIndex at `6f97965`, so slow Voice startup cannot delay task-alert startup.
The root host suite passed **18 tests** after that ordering change.

The presentation contracts and protocol negotiation are integrated through `a2280c3`
(reviewed builder revisions `81cf1ac`, `3facf83` and `4a6d817`). They carry current
UI state and preserve complete prompt selection through the active configuration.
The root host suite passed **20 tests** and the pipe suite passed **24 tests**.
The large-settings transfer implementation remains a separate acceptance gate.

The synthetic RuntimeHost executable and actual host/client process tests are
integrated through `3ad8a29` (reviewed Program revisions `2f00056` and `3ba7d1c`,
test revisions `85dc05b` and `dd59421`). The root host suite passed **28 tests**,
including four actual-host scenarios: exact custom configuration and owner continuity
after client loss, capture cleanup without transfer to a new connection, retained
events with stale-revision rejection, and completed-operation lookup after client
loss. The last scenario does not force a lost reply. Each test uses a fresh regular
scratch file, synthetic owners, unique endpoints and exact owned-process cleanup.
The launch guard rejects missing/live configurations and lexical paths under the
normal Joydex data root; its path check does not resolve junctions or symlinks.

The Desktop broker terminal-failure guard is integrated through `a6775df` (reviewed
builder revisions `7f2a1fa`, `c015559` and `bca3ec2`). Its three headless root tests
passed. The guard prevents both direct and delayed legacy-tray replacement after
unconfirmed cleanup. Production composition must preserve the same terminal state.
Client windows, complete production wiring, large transfers and package acceptance
remain in progress; these executable tests do not establish physical media continuity.

The settings transfer is integrated through `f2bcff8` (reviewed builder revisions
`09ea8f9`, `a8d1dd7` and `76ac52f`). The root pipe suite passed **49 tests**, and the
host suite passed **28 tests** after integration. Coverage includes valid payloads
above 16 MiB, legacy-peer rejection, attach replay, exact length/hash/order checks,
expired references, cancellation/disconnect cleanup and concurrent duplicate reads.
Transfers use 48 KiB chunks, at most four active transfers per connection and a
two-minute receive deadline. Temporary storage is owned by the connection and removed
on close; complete configuration contents remain intact.

Client/package build support is integrated at `32c2e9b` (reviewed builder revision
`b6815d0`). App references the contracts and pipe client, the publish script includes
RuntimeHost, and the accepted dependency notice is copied. The root Release solution
build passed with no warnings or errors. A clean-directory package publish and its
complete-content checks remain pending the final client/composition integration.

The prompt-picker window adapter is integrated at `7463aab` (reviewed builder
revision `e472177`). Its root and independent Release runs each passed **9 headless
tests**. It projects the complete active prompt library, preserves selection and
Exit indexing, restores authoritative state after a failed dismissal, and marshals
window creation and cleanup through the UI context. These checks use a fake view;
actual overlay presentation remains part of the attended demo after client wiring.

Production composition is integrated through `2e51f63` (reviewed builder revisions
`f7466c1`, capture seam `6695519` and correction `f3ac5d1`). The root audited host suite
passed **58 tests**. The new checks use fake integration owners and a hidden Windows
STA to verify activation boundaries, cleanup failure handling, thread affinity and
capture ownership across replacement. A delayed old capture release cannot stop a
new capture of the same source. The combined run also includes the existing isolated
RuntimeHost/client process checks. Production Program wiring, actual client windows,
the complete package and attended media/device continuity remain separate gates.

Authority commands and recovery corrections are integrated through `6278158`
(reviewed builder revisions `ddcfcfd`, `8e69be3`, `f89968c` and test `5397862`). The
root audited host suite passed **61 tests**; the focused coordinator, configuration,
Voice preferences and task-alert preferences run passed **71 tests**. The checks
cover exact captured external-file bytes, journal failure before and after activation,
custom configuration selection, compact current-state replay and chronological
retention of the last 64 results across restart. Unsupported early nested-result
journals fail closed; this does not claim migration of that unshipped prototype format.
The existing demo source is linked into RuntimeHost at `a12aa65`; its functional
composition and visible client wiring are still in progress.

The tray rendezvous is integrated through `9a810e6` (reviewed builder revisions
`8af04bc`, `b15177b` and `af35fc1`). The root and independent Release IPC runs each
passed **61 tests**. These use isolated synthetic pipes, fake image checks and the
test process's own image identity. They cover sole-tray admission, exact consumed
ticket identity, disconnect and failed-attach cleanup, and expired acknowledgements.
Production and demo entry points share authentication while rejecting the opposite
instance kind. Final Program integration must observe listener termination and
complete cleanup before releasing runtime ownership.

Unfiltered builder and reviewer test runs opened settings windows and a demo tray
icon, contrary to the background-only instruction. This recurred in two later
validation runs despite the restriction. Source and result audits established that
the five native-window tests passed their normal window and temporary-state cleanup;
their demo paths used synthetic owners and did not launch real integrations. These
runs were not attended M2 demos. The two later runs each had an unrelated timing-test
failure that passed on an isolated retry; neither established a passing full suite.
The five window-opening tests now require an explicit attended opt-in and skip by
default. Background validation still requires explicitly audited headless filters.
A focused check with the opt-in disabled discovered all five and skipped all five,
with no test body executed.

The functional demo composition is integrated at `6c4e8a6` (reviewed builder
revision `09c81b8`). The root and independent host suites each passed **66 tests**.
Its five new checks drive the existing simulated controllers through the real
Companion workers, binding engine and dry-run action executor. A Companion Apply
changes the next eligible action; an unrelated Voice Apply preserves both source
generations. Capture suppresses the captured gesture until release, and a later
fresh press resumes ordinary dispatch. The demo uses injected action boundaries
and creates no physical integration owner. App/Program wiring remains pending.

A preliminary flat publish at `3f9c34f` completed to an isolated temporary directory;
all 24 selected package files, supporting assets and notices were present. The known
Guardian HidSharp IL2104 trimming warning remains. No packaged executable was run.
Repeat the package check on the final integrated client/Program revision.

The button-map adapter is integrated through `042617c` (reviewed builder revisions
`662604c` and `2abb0e6`). Root and independent filtered runs each passed **7 headless
tests**. They cover active configuration and task-alert projection, controller
recovery, manual/mapped visibility, reconnect, and reopening after Escape or the
title-bar close action. All window operations in these tests use a fake view.

The client foundation is integrated through `62bec8a` (reviewed builder revisions
`a624fb9`, `8238c23` and `816bd78`). Root checks passed **11 client-state tests**,
**70 host tests** and **61 IPC tests**. The checked protocol preserves command
ordinals, requires reliable cursor support for bundled UI clients, and retains
operation/capture notifications across snapshot refresh. Replayed activity is
idempotent. This is the connection/state foundation; the complete App lifecycle
and editor integration remain in progress.

Listener lifecycle fixes are integrated through `c3622cf` (reviewed builder revisions
`4645ecd` and `e3b3b85`). The root and independent IPC runs each passed **67 tests**.
Both listeners expose termination, finish tracked cleanup before reporting accept
failure, and isolate ordinary failed clients with bounded cleanup-failure retention.
RuntimeHost Program will consume those termination signals in the startup slice.

Room Voice and active Voice routing corrections are integrated through `6de1d2b`
(reviewed Room Voice revisions `64d9277` and `6f70a53`, routing revisions `5636ad6`
and `7b24937`, plus the demo factory interface adjustment). The root combined
Room Voice, preferences-store and Desktop bridge selection passed **84 tests**;
the production-composition and functional-demo selection passed **39 tests**.
These explicitly audited selections used fake views, injected owners and a recording
send sink. They cover stable timeline entries, complete conversation/outbox copy,
delivery-ID recovery actions, and the actual routing helper choosing active settings
while Desired differs. The final App construction still must connect the full-copy
callback and the remaining settings/tray adapters. No attended M2 demo has run.

The settings child channel and launcher are integrated through `5dcf8d2` (reviewed
revisions `2af4ed3` and `a983f1c`). Root checks passed **5 channel tests** and
**13 launcher tests** with explicit class filters. They use memory streams and fake
processes to check bounded Bootstrap/Activate/Reconnect messages, one-child ownership,
fresh reconnect tickets, draft-preserving reconnect failure, late attachment and
observable cleanup failure. The Program/server callbacks and App channel consumer
remain integration gates; these tests did not launch an executable or show a window.

The shared settings writer and configuration input client are integrated through
`6e406f1` (reviewed revisions `ad90497` and `d4b1b75`). Root exact-class checks passed
**8 writer tests** and **11 input tests**. They cover caller-owned operation IDs,
lost-reply lookup without resubmission, stale connection rejection, source mapping,
capture-before-acknowledgement races and exactly-once recovered capture notification.
Both selections use in-memory state and fake RPC implementations. The actual editor
must retain its draft/operation state and feed generation-tagged updates to these
clients; the Voice target consumer remains under separate recovery review.

Draft retention and the Voice target consumer are integrated through `9a290dc`
(reviewed draft revision `f927a09`, Voice revisions `66d8757`, `32c51de` and
`592c7cb`). Root exact-class checks passed **5 draft tests** and **24 Voice tests**.
The editor keeps its draft and operation ID in memory, can recover a retained
operation across a host epoch change, and requires explicit review/rebase after the
authoritative baseline changes. Target selection recovers the same operation before
submitting another change and can retry an uncertain lookup on the same connection.
The actual form must disable editing during Apply/Recover and expose those recovery
choices; these fake-only tests do not establish visible UI behavior.

RuntimeHost Program is integrated through `917b808` (reviewed `c14a75c` plus an early
cancellation check). Root exact-class checks passed **16 startup/Program tests**.
They cover ownership before listeners, delayed engine startup and fresh tray
admission after an early disconnect, listener/composition termination, cleanup order,
configuration selection and the existing isolated synthetic entry point. New cases
use injected components; the retained synthetic cases use temporary stores and local
pipes. No production host, settings executable or GUI was launched. App startup,
the settings channel client and remaining window adapters are the final wiring gates.

The draft review correction is integrated at `54dbae3` (reviewed `eed9849`). Its
root exact-class run passed **6 draft tests**. A changed authority now requires
review even before the form has projected its first edit, so stale visible controls
cannot silently overwrite newer settings. The new regression uses only in-memory
state and a recording writer.

The runtime Task Alerts UI adapter is integrated at `6cd5d94` (reviewed `7e1af74`).
Its root exact-class run passed **12 tests** with the pinned SDK, Release warnings
as errors and attended opt-in disabled. The fake-only checks cover full desired
suppression lists, isolated preference patches, typed hook commands, connection
replacement and bounded lookup of retained operations before another action. Source
review also checked the two existing forms' runtime action and disconnect paths.
The focused tests construct no forms; final tray construction and visible behavior
remain part of the App wiring and attended checks.

The settings-process connection client is integrated at `15b9195` (reviewed
`5ecc0c6`). Its root exact-class run passed **9 tests** with the same pinned-SDK,
warnings-as-errors and attended-opt-in-disabled settings. Memory streams and fake
connections cover bootstrap, activation, fresh-ticket reconnection, cursor recovery,
stale completion, failed reconnect, channel failure and shared disposal. Channel
shutdown cancels and joins an unfinished connection. The real App entrypoint and
settings application context still must consume this client.

The authenticated settings-process lifecycle is integrated at `e190eaa` (reviewed
`ee0af5e`). Both focused host checks passed in the implementation and root checkouts
with the pinned SDK, Release warnings as errors and attended opt-in disabled. The
host notifies the launcher only after a successful Settings attachment, then once
after that connection's cleanup. Failed or incomplete attachments and other client
kinds do not notify it; engine shutdown suppresses reconnect. These checks use
synthetic owners, a fake launcher and isolated temporary stores. Final App startup,
window composition and package validation remain pending.

The Desktop task-list source correction is integrated at `d256809` (reviewed
`7ff8717`). Both pure routing checks passed in the implementation and root checkouts
with the same pinned-SDK validation settings. An explicit task source supports
Pebble independently of Voice and remains in the returned catalog; a request with
no explicit source preserves active Voice routing and excludes its source task.
These checks validate source selection and request canonicalization without starting
a Desktop bridge. The final window adapter supplies its visible or draft source
for this command.

### Final M2 demo-ready checkpoint

Direct LASTVOICE task navigation is integrated at `e7df13d` (reviewed `9513e93`).
Its two pure checks passed in both checkouts. The explicit settings action validates
and opens the requested local task under active Companion safety, without requiring
a Dedicated Voice Task or a Desktop Task Bridge source. The command ID and schema
are unchanged. These checks do not perform actual desktop navigation.

The complete App startup, settings and tray composition is integrated through
`00a4afd`. Independent review accepted the full UI chain through `d3d22fd`, startup
`145e2f4` and the changed-draft regression `629321e`. The integrated App files and
regression fixture match those reviewed sources exactly. Review corrections cover
capture cleanup before edits/save/close, old-connection result rejection, exact
interrupted-operation identity, stale standalone prompt saves, typed host actions,
and bounded startup failure reporting. The host retains runtime ownership when its
settings client closes; explicit tray Exit requests runtime shutdown.

The final root run used .NET SDK 8.0.423, Release warnings as errors and
`JOYDEX_RUN_ATTENDED_TESTS=0`. Only these audited fake-only classes ran:

| Test class | Passed |
|---|---:|
| `RuntimeConfigurationInputClientTests` | 11 |
| `RuntimeSettingsDraftControllerTests` | 7 |
| `RuntimeSettingsWriterTests` | 8 |
| `RuntimeTaskAlertsConnectionServicesTests` | 12 |
| `RuntimeSettingsProcessClientTests` | 9 |
| `RuntimeAppStartupTests` | 6 |
| **Total** | **53** |

There were no failures or skips. The new draft check loses the reply for draft A,
changes the draft to B, then recovers A without submitting B. B stays dirty over
the authoritative A state until a later explicit Apply. The three focused host
selections described above also passed two checks each in the root checkout.
The final Release solution build passed with zero warnings and zero errors.

The clean local package is
`artifacts/Joydex/process-settings-m2-00a4afd-win-x64`, with a sibling
`process-settings-m2-00a4afd-win-x64.sha256` manifest. Publishing completed
successfully; Guardian emitted the previously known HidSharp IL2104 trimming
warning. Static verification found all 45 selected required files nonempty and
matched nine published assets/notices to their sources by SHA-256. The manifest
records all 111 package files. No packaged executable was run.

Independent review accepted this as an attended-demo checkpoint. The visible
Settings/tray workflow and physical controller, PAD, Pebble, task-alert/Desktop
integration and Voice/WebView2/media continuity remain unexercised here. Start
the synthetic walkthrough only when the user explicitly begins it, and describe
each step's original behavior, purpose and expected result before acting. Physical
integration checks require a separate attended canary. Do not start the plugin
pilot before milestone 2 feedback.

### Attended startup findings

The user authorized the attended walkthrough on 2026-09-12. The first packaged
launch at `00a4afd` created the isolated demo tray, host and Settings child, but
never showed Settings. The inherited Windows stdin read could block synchronously
before runtime attachment; its console stream also does not interrupt that read on
disposal. The correction is integrated at `dba705a` (reviewed `346da97`). It moves
the reader off the UI thread and lets Settings finish shutdown while the remaining
OS read has the child process's lifetime, including after initial attach failure.

Two bounded fake-only regressions failed against the original implementation and
again against a startup-only fix. Both passed with the complete correction; all
11 `RuntimeSettingsProcessClientTests` then passed in the implementation and root
checkouts with SDK 8.0.423, Release warnings as errors and attended tests disabled.
The fresh `process-settings-m2-dba705a-win-x64` publish passed the same 45-file,
nine-asset and 111-entry manifest checks; the known Guardian trim warning remained.

A second attended launch reached the Windows Forms message loop, confirmed with a
stack-only diagnostic on the exact demo Settings process, but still showed no
window. An initial-error reporting improvement at `5cc5a67` (reviewed `4e3b127`)
preserves a visible failure and child cleanup if presentation fails before a usable
form exists. It did not reveal an exception in this launch.

The second startup cause was the launcher's `WindowStyle.Hidden`: .NET 8 honors it
with `UseShellExecute=false`, and Windows applies it to the first form shown.
The correction at `a52175a` (reviewed `378f95a`) uses Normal while retaining console
suppression and redirected stdio. The existing fake launcher test passed with its
new visibility assertion. The fresh package published successfully, and the actual
`Joydex — Demo / dry-run inspector` window appeared and was brought forward.
The user stopped Computer Use before the capture step completed. No Apply or
Settings-process restart result was observed. Previous failed demo process trees
were closed after exact path, parent and role checks; normal Joydex stayed running.

## Milestone 3 — Bundled PAD plugin pilot

Status: implementation authorized 2026-09-12. The user requested continued
implementation and no further agent-driven demo. Begin with one PAD/catalog builder
and independent review, using background-safe validation only.

Give the existing wireless panel adapter a stable `joydex.pad` registration and
independent lifecycle/configuration/health boundary. Preserve its settings path,
DPAPI credentials, fixed command numbers, current-slot recheck and reconnect output.
Prove with injected owners/transports that missing, disabled, failed or restarted
PAD leaves unrelated runtime components healthy. Keep the first catalog explicit
and bundled; public SDK loading and firmware changes are outside this milestone.

The remaining M2 attended checks and physical PAD canary remain release evidence
gaps. Record them beside the completed background evidence; do not infer real
hardware or media continuity from fake lifecycle tests.
