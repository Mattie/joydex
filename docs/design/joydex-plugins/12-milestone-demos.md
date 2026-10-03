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
