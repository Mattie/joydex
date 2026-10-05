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
and 29 wireless-panel tests), independently run with SDK 8.0.423. At that checkpoint,
the legacy tray regression checked exact worker identities and source generations
before opening settings, while settings was open, and after closing. Capture tests
covered held controls, dispatch barriers, disposal, device removal and shared chords.

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

### Implementation result

Milestone 2 implements the [PROCESSUI contract](13-process-settings-contract.md) and
[ADR 0009](../../adr/0009-separate-settings-from-runtime-with-owned-media-sta.md).
RuntimeHost owns integration lifetimes, active media and the settings authority; the App
runs as a replaceable tray or Settings client. Apply uses revisioned transactions, preserves
desired state across activation failures, and keeps stale drafts from overwriting newer
settings. Runtime owners are replaced in dependency order with explicit cleanup boundaries,
and unconfirmed cleanup remains terminal for that process generation.

The final checkpoint below records the integrated background evidence and package result.
The attended findings retain the startup lessons that still affect launcher behavior. The
earlier slice-by-slice checkpoint diary was removed after integration because its pending
statuses and intermediate commit inventory no longer describe the finished boundary.

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

Status: implemented 2026-09-12, through `b6024f3`. The user requested continued
implementation and no further agent-driven demo. A PAD/catalog builder and a
client/tray builder completed the bounded pilot with independent review.

Give the existing wireless panel adapter a stable `joydex.pad` registration and
independent lifecycle/configuration/health boundary. Preserve its settings path,
DPAPI credentials, fixed command numbers, current-slot recheck and reconnect output.
Prove with injected owners/transports that missing, disabled, failed or restarted
PAD leaves unrelated runtime components healthy. Keep the first catalog explicit
and bundled; public SDK loading and firmware changes are outside this milestone.

The typed contracts are integrated at `a081264` (reviewed `74f5577`), the client
and tray at `0571bab` (reviewed `7479c2b`), and the independent PAD owner at
`517abc3` plus `b6024f3` (reviewed `98af614` plus `a3fd9aa`). The tray exposes
**Plugins > PAD** lifecycle, Restart and Reload configuration. Protocol minor 3
advertises `plugin-management.v1`; older negotiated clients cannot invoke the new
commands. Opening the submenu inspects health without reloading configuration.

PAD is no longer owned by the Companion controller aggregate. It keeps the existing
transport reconnect behavior, while its supervisor handles unexpected loop
termination with bounded retries. A replacement waits for confirmed prior cleanup;
uncertain cleanup blocks PAD replacement until process exit. Restart uses the
effective configuration, invalid reload preserves a live prior generation, and
Companion policy updates do not silently adopt external panel-file edits. Client
recovery resolves an uncertain operation before another Restart or Reload, and
stale connection results cannot overwrite the new connection's tray state.

Final integrated validation ran from a clean detached checkout of `b6024f3`, using
SDK 8.0.423, Release warnings as errors and `JOYDEX_RUN_ATTENDED_TESTS=0`. Restore
used the existing local package cache. Both selected runs passed, with no failures,
skips or build warnings:

| Audited selection | Passed |
|---|---:|
| `PadPluginTests` | 18 |
| `RuntimeEngineTests` | 25 |
| Three `ProductionRuntimeCompositionTests` methods below | 3 |
| `EspHomePanelAdapterTests` | 39 |
| `RuntimePluginConnectionServicesTests` | 8 |
| **Total** | **93** |

The composition selection was exactly `RefreshStartsEveryOwnerInDependencyOrder`,
`PadLossAndRetryStayOutsideCompositionAndUnrelatedOwnerLifetimes`, and
`PadPolicyRefreshFollowsOnlySuccessfulCompanionCommitAndRollback`. These checks use
fake owners/transports and synthetic settings stores. They exercise production
command dispatch, isolation, cleanup/retry, reload rollback, current action policy,
fixed panel behavior, protocol compatibility and client reply recovery. They do
not open the tray, drive Settings, or contact a physical panel.

The next implementation slice is VOICEPLUGIN. Do not treat this checkpoint as a
new packaged release or as evidence that the remaining attended scenarios passed.

### Physical PAD canary

The 2026-10-04 mainline migration retained the reviewed RuntimeHost recovery fixes.
Its Release solution build passed without warnings; 91 focused host tests and 72
focused client/panel tests passed. PAD recovery now distinguishes a replacement
engine from a reconnect and releases expired terminal results only for a later
explicit action. The physical evidence below is historical, not a new hardware run.

On 2026-09-12 the user exited the prior Joydex normally. A process check confirmed
all Joydex owners had exited before the separate `pad-canary-b6024f3-win-x64`
package launched with the existing Companion and PAD configuration. Both files
were backed up first. Publishing succeeded with the known Guardian/HidSharp IL2104
warning; all 111 package files were nonempty and recorded in a SHA-256 manifest.

The user confirmed current tasks appeared on the real PAD. **Restart PAD** caused
temporary display flicker, then recovered; the flicker stopped and task buttons
opened the correct tasks. Task navigation also worked while Configure was open
and after it closed. These are user-observed physical results, with the user
operating the tray and panel.

This closes the small PAD restart/navigation and Settings open/close canary.
The temporary restart flicker remains a recorded observation. Settings Capture,
Apply, Settings-process termination/restart, live Voice/media continuity, physical
PAD disconnect/reconnect, and full release/rollback acceptance remain unverified.

## Milestone 4 — Voice plugin extraction

Status: implemented 2026-09-12 through `4c4d465`, following the physical PAD canary.
Independent review accepted `0d5b22fc6c3610bc7f48d3d8d5953c16cbc761db`. The integrated
source, tests, project and publish files match that reviewed checkpoint exactly.

The 2026-10-04 mainline migration preserves the later reviewed settings and PAD
fixes. Synthetic worker authentication tests use the existing current-process
test seam; the native child probe still requires an unelevated runner. The
checkpoint results below are historical evidence, not a new physical Voice canary.

`Joydex.VoiceWorker.exe` owns the existing Voice aggregate, media STA, dedicated
owner, conversation model and archive. RuntimeHost supervises authenticated worker
generations and retains settings activation, PendingIdle, rollback, task exclusion
and guarded actions. Voice is registered as `joydex.voice-pe`; existing Room Voice
controls remain the management surface. See [ADR 0011](../../adr/0011-isolate-voice-media-in-a-worker-process.md).

Review corrections cover bounded authenticated startup, confirmed process/job
cleanup before replacement, retained cleanup uncertainty, stale-generation and
pre-commit callback rejection, runtime-owned supervision cancellation, persistent
restart backoff, committed task exclusion, fallback lifetime, coalesced snapshot
publication, synchronized idle edges and bounded complete-entry conversation pages.
The five reused media source files and two Voice assets are unchanged from the
physical PAD checkpoint. RuntimeHost no longer compiles those media sources or
references WebView2; the worker carries that dependency and its native loader.

Final verification ran from a clean detached checkout of `4c4d465`, using SDK
8.0.423, Release warnings as errors and `JOYDEX_RUN_ATTENDED_TESTS=0`. Both selected
runs passed with no failures, skips or build warnings:

| Audited selection | Passed |
|---|---:|
| `VoiceProductionOwnerTests` | 6 |
| `VoiceWorkerProcessGenerationTests` | 4 |
| `VoiceWorkerServiceTests` | 3 |
| Selected `ProductionRuntimeCompositionTests` | 5 |
| Selected `PadPluginTests` | 2 |
| Selected `RuntimeRoomVoiceWindowAdapterTests` | 4 |
| Selected `VoiceMediaStaHostTests` | 2 |
| Selected `CodexDedicatedVoiceOwnerTests` | 2 |
| Selected `VoiceSessionArchiveTests` | 1 |
| **Total** | **29** |

The selected composition checks were `ActiveVoiceDefersActivationAndIdlePublishesBoundary`,
`IncompleteVoiceStartupCleanupIsTerminalAndNeverRetried`,
`DependentVoiceFailureRollsBackCompanionAndVoice`,
`VoiceExclusionAuthorityCommitsOnlySuccessfulGenerationAndSurvivesRollback`, and
`DesktopTaskListWithoutAnExplicitSourcePreservesVoiceRoutingAndExclusion`.
The PAD checks were `CatalogRegistersCanonicalPadAndVoiceWorker` and
`TypedDispatchInspectsFullCatalogAndRejectsUnknownPlugin`.

The Room Voice checks were `MatchingSnapshotsAndEventsDriveTheExistingWindowModels`,
`CompleteConversationUsesVersionedPagesAndKeepsRawText`,
`MessagingUsesPrivateReadsTypedActionsAndAuthorityOwnedTargetSelection`, and
`DisposeCancelsPendingWorkAndRejectsLaterUse`. The media selection used only the
fake `RuntimeGateCancelsAndDrainsOldGenerationPublications` and
`StartupRollbackDisposesEveryPartialOwnerInRuntimeOrder` methods. Dedicated-owner
checks were `ResumesExactTaskAndRetainsClientUntilOwnerDisposal` and
`RejectsOwnerReadinessWhenRealtimeV3VoiceCapabilityIsMissing`; the archive check was
`WritesUtf8ConversationAndSessionLifecycleMetadata`.

The process-generation class uses isolated current-user pipes and a synthetic
ProcessProbe. Its native case launches only the probe and a no-media descendant,
then confirms the Windows job is empty on disposal. It does not initialize Voice,
WebView2, hardware or normal configuration. The other selections use fake services
and temporary state; no visible UI was driven.

The clean package is `artifacts/Joydex/voice-plugin-4c4d465-win-x64`, with a sibling
SHA-256 manifest covering all 116 nonempty files. Publishing succeeded with only
the known Guardian/HidSharp IL2104 trimming warning. Eleven selected required
components were present; ten assets, notices and native-loader files matched their
sources by hash. Published dependency metadata confirms WebView2 moved out of
RuntimeHost and is present in VoiceWorker. No executable from this package was
launched, and the running PAD canary was left untouched.

Physical Voice microphone/speaker and multi-response call continuity remain
unverified for the new worker package. The earlier Settings Capture/Apply/process
restart and full release/rollback gaps also remain open. The next implementation
slice is PEBBLEPLUGIN; this checkpoint does not claim attended Voice acceptance.
