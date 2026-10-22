# Secrets consent and diagnostics acceptance

Status: attended session concluded on 2026-10-21. Core native consent and execution
checks passed, with the unverified portions listed below. The ordinary voice-working
`artifacts/Joydex/local-voice-runtime-20261020` package, all five normal processes
and LocalAppData discovery profile were restored at 21:36 UTC. No test candidate
or task helper remains running. The isolated store and evidence remain at
`D:/Temp/joydex-secrets-finish-20261020/acceptance-20261021`.

The first baseline attempt (`baseline-allow-e108a47776e94c7f85207a8c425fecb3`)
stopped before consent or an attempt record, with helper exit 2. Credential
bootstrap tried to set an already-correct owner without WRITE_OWNER permission
in the new D:/Temp store. A regression reproduced the exact native error. The
source fix avoids the redundant ownership write and verifies the resulting
current-user owner and private ACL. All 131 tests in the audited persistence,
consent-availability, transport, diagnostics, CLI, broker-core and exec-operation
classes pass, including the new regression. Bootstrap then succeeded in the
same isolated store. That failed setup attempt recorded no approval or execution.

The first corrected package, `candidate-aclfix-20261021`, covered baseline,
exact grants, pending/fresh lock behavior and accepted one-use redemption. The
same redundant ownership write was then reproduced in detached task directory
setup and fixed with a second regression. The final frozen package is
`D:/Temp/joydex-secrets-finish-20261020/candidate-aclfix2-20261021`, with
`candidate-aclfix2-manifest.json` beside it. Only `Joydex.Secrets.dll` and its PDB
differ from the original 134-file package; the overlays were published with SDK
8.0.423. The synthetic source was registered through the configuration API;
source-editor acceptance is untested. Neither remedy is deployed to production.

Final validation: 144 audited automated tests passed with zero failures or skips,
plus the explicitly enabled native Explorer-parent/stop test. Both new permission
regressions failed before their remedies and pass afterward. Final focused diff
review retained current-user ownership and protected, user-only ACL verification;
`git diff --check` passed. All 134 final package hashes still match the manifest.

## Unattended integration follow-up, 2026-10-21

The final reliance review found one additional defect: after requester revocation
or lost broker correlation, a rejected replay could report `not_started` with an
empty attempt ID despite an older execution. Broker replies now omit empty IDs
and preserve unknown certainty. Offline diagnosis handles null and legacy empty
attempt IDs without assigning their failures to an older sole attempt sharing the
request ID; explicitly selecting the original real attempt still reads its ledger.
The protocol regression and both CLI regression cases failed before the remedy
and pass afterward. Independent remedy review found no remaining code blockers.

Additional unattended tests verify that a one-use approval is rejected after a
restart into Locked while it is still unexpired. Policy-only reviews survive
Locked, Disconnected and Unknown availability, reject one-use redemption, and can
authorize only future requests after availability returns. These close the logic
coverage gaps; they do not replace the missing native UI/session observations.

A clean export of commit `6696530ff2a076b79547ebd3eccb34f6247564d6` with only
the 28-file staged Secrets patch builds and passes 159 Secrets and 72 IPC tests,
with zero failures or skips. The audit excludes the attended Explorer method,
which passed in the earlier explicitly attended session. The selected classes
and complete TRX results are retained in
`D:/Temp/joydex-secrets-integration-20261021/validation`.
The full `Publish-Joydex.ps1` package build from that export also passed; its only
reported warning was the existing HidSharp IL2104 trim warning in Guardian.
The packaged helper diagnosed the real legacy empty-attempt record from the
attended evidence as uncorrelated/unknown, retained the original completed
attempt as finished, and changed no evidence files or credential state.

Integration artifacts are at `D:/Temp/joydex-secrets-integration-20261021`:
`secrets.patch`, `scope.txt`, `integration-manifest.json`, the package manifest,
TRX results and a prepared PR description. The canonical index contains only
the 28 intended Secrets files; the live profile still selects the voice-working
production package. No commit, PR, startup change or deployment was made here.

The integration patch deliberately includes MessagePack 2.5.303: the earlier
fresh package restore failed NU1902 on StreamJsonRpc's transitive version, so the
patched dependency is required without suppressing NuGet audit. The approved
skill remains byte-identical to its accepted SkillSpector scan. All seven pending
Voice files remain outside the staged patch. The running package is unchanged.

Native Settings visuals, editor/mode warning/activity-button interaction,
policy-review lock/unlock presentation and physical disconnect/reconnect remain
unverified. No known implementation blocker remains for code integration;
complete native presentation acceptance remains partial. The current deployed
package includes the separate uncommitted Voice compatibility remedy. A package
built from the Secrets-only export contains baseline Voice code, so deployment
must account for that separate remedy before replacing the working package.

Integration review checklist:

- [x] Deep review matched the authorization/diagnostic risks.
- [x] Scope is code integration, with production deployment separate.
- [x] Claims distinguish native observations from runtime-injected tests.
- [x] Confirmed defects have reproduced failures and passing regressions.
- [x] Correlation, identity changes, restart and unavailable-session edges checked.
- [x] Scoped export excludes unrelated Voice edits and retains the audited skill.
- [x] Concrete integration evidence and residual checks are recorded.
- [ ] Flagged: the native presentation/disconnect observations above remain open.

## Recorded attended results

- Allow once: exit 0, injection true, empty child output, one completed launch.
- Deny this time: exit 11, no execution or launch commit.
- Fresh requests while locked: exit 16, `consent_unavailable`, `not_started`,
  zero launches. The run-without-secrets option did not execute its fallback.
- Exact 24-hour and always grants: both execute successfully while locked;
  a changed operation fails with exit 16. Policy epoch stayed 3 throughout.
- Client-scoped attached approval: a changed operation from the separate
  `codex-scope` requester executes while locked. Its attached grant did not
  authorize detached execution; that first detached request recorded pending
  and required a separate human approval.
- Accepted one-use approval: redeemed while locked, child exit 0; replay
  returns revoked/exit 11 without another execution.
- Pending then lock: the deliberately pre-registered request
  `pending-before-lock-546690532f6747018a07713c1be7f1d9`, attempt
  `edad7176-da80-43e2-aeba-243467d7bc1a`, records pending then consentUnavailable,
  exit 16, zero launches and `not_started`. Polling after unlock remains terminal.
  A distinct new request prompts again and can be denied.
- The earlier request named `pending-lock` arrived after lock, so it counts only
  as another fresh-while-locked case.
- Explorer detached launch: native parent is `explorer.exe` (PID 12876), with
  30-second deadlines and running receipts. Immediate stop returns stopped both
  unlocked and under a saved detached grant while locked. All three synthetic
  tasks have terminal receipts. A receipt-parser error in the first test harness
  missed its immediate stop check; that task completed normally with exit 0.
  The corrected harness confirmed stop on distinct subsequent tasks.
- Broker restart while locked: the tray replaced PID 222292 with 89560. Native
  startup detection reported Locked; a saved grant ran and an unapproved command
  failed with exit 16. The held one-use reservation had already expired before
  this restart, so its rejection does **not** prove restart invalidation.
- Operating modes while locked: isolated store API setup enabled auto-allow,
  which executed with injection; deny-all then rejected a saved exact grant.
  Ask was restored. The warning/confirmation UI was not tested.
- Policy-only review: leave-pending timed out with exit 12, `agentDetached`, no
  reservation and no launch. Its native lock/unlock and disabled-button behavior
  were not verified. Computer Use did not enumerate the review window.
- Offline diagnosis: with every candidate process stopped, 18 requests were
  diagnosed without changing any Secrets file's name, length or write time.
  Five consent-unavailable failures retained exact request/attempt correlation,
  safe reason, `not_started` certainty and zero launches.
- Activity cleanup: after preserving evidence, the isolated store API cleared
  audit and diagnostics. Policy and durable operations hashes were unchanged.
  The Settings Activity columns and Clear activity button were not tested.

Still unverified as native observations: Settings overlap/focus/responsiveness; source-editor and mode
confirmation UI; Activity display/clear-button wiring; detached policy review
across lock/unlock; an unexpired one-use reservation across restart; and session
disconnect/reconnect (no reversible route was agreed). The earlier combined
Settings/lock observation question was confusing and establishes no visual result.

Per-request metadata and diagnoses are in the isolated test folder, including
`attended-summary.json`, both `lock-phase*-result.json` files, per-request
`*.offline-diagnose.json`, `offline-result.json`, `cleanup-result.json`, and
`saved-native-evidence/`. These records contain synthetic-test metadata only.

This checks the Windows session boundary that runtime-injected unit tests cannot
prove. The assistant operates the commands and records evidence; Mattie approves
the displayed requests and performs the agreed lock/disconnect/unlock actions.
Run only after Mattie explicitly starts the attended session. Do not sign out.
Give one human instruction at a time. Keep Settings visibility checks separate
from locking. Register and confirm Pending before asking for a pending-request
lock. Arm the runner before asking for a lock, allow a generous fixed duration,
and state when to unlock before the user locks; never depend on reading chat
while Windows is locked.

## Candidate and prerequisites

- Latest package: `D:/Temp/joydex-secrets-integration-20261021/package`.
- Package identity: `D:/Temp/joydex-secrets-integration-20261021/package-manifest.json`.
- Build with repository-local .NET SDK 8.0.423 through `scripts/Publish-Joydex.ps1`.
- The ordinary profile-selected package remains the rollback package. Record its
  paths from `%LOCALAPPDATA%/Joydex/profile.yaml` before changing the active tray.
- Exit the ordinary tray before running the candidate, so only one application
  owns hardware. Copy `config/joydex.example.json` to a new acceptance-data folder
  under `D:/Temp/joydex-secrets-finish-20261020/`, preserving its dry-run setting.
  Start the candidate with `--config` pointing to that copy. Keep Voice and panel
  integrations disabled for the initial isolated pass.
- Normal candidate startup replaces the discovery profile. Verify the candidate
  helper and acceptance data root in that profile before making any requests.
- Create a synthetic source file in the acceptance folder containing only the
  known test entry `JOYDEX_ACCEPTANCE_TOKEN=synthetic-consent-check`. Add that source
  through the candidate's Secrets settings. No real credential is needed.
- Use the same scratch working directory, client `codex`, and project
  `joydex-consent-check` throughout. Each intentional command attempt gets a fresh
  request ID. Capture the returned attempt ID; IDs are metadata, not authority.

Use this harmless attached command for consent cases. Success is exit 0; missing
injection is exit 33. It never prints the token and makes no network request.

```powershell
& $helper exec --data-root $dataRoot --client codex --project joydex-consent-check `
  --reason 'Verify Windows consent availability with a synthetic token' `
  --request $requestId --secret JOYDEX_ACCEPTANCE_TOKEN `
  -- $env:ComSpec /d /c 'if defined JOYDEX_ACCEPTANCE_TOKEN (exit /b 0) else (exit /b 33)'
```

`$helper` and `$dataRoot` come from the validated candidate profile. Do not use an
existing grant from a real project or enable auto-allow on the production store.
Remove only acceptance-store test grants between cases that need fresh consent.

## Cases

| Case | Human action and operator setup | Required evidence |
| --- | --- | --- |
| Interactive baseline | Keep settings open; approve once, then deny a separate fresh request. | Toast is visible above other windows without stealing focus; allowed command exits 0; denied command never launches. Settings remain responsive. |
| Pending request then lock | Leave a fresh request unanswered; lock Windows while it is pending, then unlock. | Request ends as `consentUnavailable`, wrapper exits 16, no launch commit, no resurrected toast/command. An explicit new request can prompt after unlock. |
| Fresh request while locked | Lock first; assistant submits a new request lacking a grant. Repeat with the `run-without-secrets` timeout option. | Both fail promptly with exit 16. No timeout fallback runs. Diagnosis identifies the same request and attempt. |
| Saved approvals while locked | While unlocked, create exact-command 24-hour and always approvals. Repeat the identical operation with new request IDs while locked. Also cover client-scoped approval separately. | Exit 0 with no new toast. No policy epoch change caused solely by locking. A changed exact operation still requires consent and fails while locked. |
| Accepted one-use approval | Use advanced `request` with a metadata-only operation file, wait for Mattie's Allow once decision, then lock before calling `run` with its reservation. Redeem within its expiry. | One approved execution exits 0 while locked; replay cannot execute again. This case cannot use ordinary `exec`, which redeems immediately. |
| Detached policy-only review | Use `leave-pending` with a short approval timeout, let the helper detach, then lock/unlock. | Review returns only for future policy decisions. Old command never becomes executable; Allow once remains unavailable. |
| Auto-allow and deny-all | In the isolated acceptance store only, enable auto-allow, run while locked, then set deny-all and repeat. | Auto-allow executes; deny-all still rejects. Restore Ask afterward. |
| Broker restart while locked | With a saved test grant, restart only the acceptance broker while Windows is locked. | Native startup detection reports unavailable presentation; saved grant still executes; unapproved operation fails. One-use reservations do not survive restart. |
| Session disconnect/reconnect | If a reversible disconnect route is available, agree it with Mattie before using it. | Diagnostics record Disconnected; saved approval works; new consent fails; reconnect does not revive old requests. Otherwise record this case as untested. |
| Explorer detached launch | With Explorer running, launch a synthetic-token task with `--detach` and a 30-second execution limit. Repeat under a saved detached grant while locked. | Hidden helper has Explorer as parent; task receipt/status shows the correct identity; stop terminates its tree; no fallback launch path. Attached grants do not authorize detached execution. |
| Offline diagnosis | Exit the acceptance tray/broker, then diagnose captured failures with the candidate helper and the acceptance data root. | Safe reason, request ID, attempt ID and execution certainty remain readable; no enrollment, provider fetch or file changes. |
| Settings and cleanup | Review the failed request in Activity. In the isolated store, clear activity after saving evidence. | Reason/request columns match the failure. Diagnostics/activity clear; test grants and durable operation history remain intact. |

For offline diagnosis prefer the exact attempt from the original reply:

```powershell
& $helper diagnose --data-root $dataRoot --request $requestId --attempt $attemptId --output-mode json
```

`--last-failure` follows its recorded attempt, which may belong to another request.
An ambiguous request ID requires selecting the original attempt, never guessing.
Unknown or unconfirmed launch certainty must not trigger automatic relaunch.

## Evidence, stop conditions and rollback

For each case record package hashes, request/attempt IDs, observed Windows state,
wrapper and child exit codes, sanitized reason/launch state, human toast observation,
and pass/fail/untested. Record only metadata; no environment dumps or real values.

Stop on unexpected launch, duplicated execution, secret disclosure, a consent prompt
that cannot be seen, inability to terminate a test child, or stalled/unavailable
Windows controls. Preserve the original request/task IDs and diagnose before retrying.

After isolated acceptance, exit the candidate and restart the recorded ordinary tray
package. Confirm its normal profile/data root is restored. Preserve acceptance
evidence; remove only the explicitly identified synthetic source, grants and tasks
if cleaning up. Production grants and operation history are not modified by the
isolated cases. A later production smoke check needs its own recorded attended result.

Automation, CI, and package publication do not mark any case above passed.
