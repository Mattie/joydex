# Implementation milestones and demos

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
