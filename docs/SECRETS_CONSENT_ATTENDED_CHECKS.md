# Secrets consent and diagnostics acceptance

This procedure checks the native Windows session boundary that runtime-injected
tests cannot prove. Run it only in an explicitly requested attended session.
Keep per-run evidence, identifiers, paths, timestamps and operator notes outside
the public repository. Publish only behavior, validation scope and remaining gaps.

## Recorded coverage

Core attended checks passed for allow/deny, pending-request termination without
revival, fresh consent failure while locked, saved exact/client grants, accepted
one-use redemption, Explorer-launched detached execution and stop, and broker
restart while locked. Offline diagnosis and activity cleanup were also checked
against an isolated synthetic store.

Automated regressions additionally cover private-folder ownership, unknown launch
certainty when attempt correlation is missing, unexpired one-use rejection after
restart, and future-only policy review across unavailable-session transitions.
These results do not establish native UI or hardware behavior.

Native Settings overlap/focus/responsiveness, source-editor and mode-confirmation
UI, Activity display/clear-button wiring, detached policy-review presentation
across lock/unlock, unexpired one-use behavior across a native broker restart,
and session disconnect/reconnect remain unverified. Core coverage is not full
native presentation sign-off.

## Candidate and prerequisites

- Publish a complete candidate with the pinned SDK using
  `scripts/Publish-Joydex.ps1`. Record its source revision and file hashes locally.
- Discover the ordinary installation and configuration through
  `%LOCALAPPDATA%/Joydex/profile.yaml`; keep a local rollback record.
- Exit the ordinary tray before starting a candidate. Only one production
  instance may own hardware in a Windows session.
- Copy `config/joydex.example.json` into a new disposable acceptance folder.
  Preserve dry-run mode and disable Voice and panel integrations for isolated
  consent checks. Start the candidate with `--config` pointing to that copy.
- Candidate startup replaces the discovery profile. Verify its helper and data
  root before issuing requests, and restore ordinary discovery after testing.
- Create a synthetic source containing only
  `JOYDEX_ACCEPTANCE_TOKEN=synthetic-consent-check` in the isolated folder.
  Register it through Secrets settings. Real credentials are unnecessary.
- Use a consistent scratch working directory, test client and test project.
  Assign a new request ID to each intentional attempt. Retain returned IDs only
  in local evidence; use variables or placeholders in public examples.

In the example below, `$helper` and `$dataRoot` identify the validated candidate
and isolated store. `$requestId` is a newly generated test request identifier.
The command checks injection without printing a value or making a network call.
Success is exit 0; missing injection is exit 33.

```powershell
& $helper exec --data-root $dataRoot --client codex --project joydex-consent-check `
  --reason 'Verify Windows consent availability with a synthetic token' `
  --request $requestId --secret JOYDEX_ACCEPTANCE_TOKEN `
  -- $env:ComSpec /d /c 'if defined JOYDEX_ACCEPTANCE_TOKEN (exit /b 0) else (exit /b 33)'
```

Do not reuse a real project's grants or enable auto-allow in the production store.
Remove only isolated test grants when a case requires fresh consent.

## Cases

| Case | Operator setup and human action | Required evidence |
| --- | --- | --- |
| Interactive baseline | Keep Settings open; approve once, then deny a separate fresh request. | Toast remains visible without stealing focus; Settings responds; only the allowed command launches. |
| Pending request then lock | Register and confirm Pending before locking; leave it unanswered. | Request ends with `consentUnavailable`, exit 16 and no launch; unlock cannot revive it. A new request can prompt. |
| Fresh request while locked | Lock first, then submit an unapproved request. Repeat with `run-without-secrets`. | Both fail promptly with exit 16; no fallback runs; diagnosis correlates the failure. |
| Saved approvals while locked | Create exact-command 24-hour and always grants while unlocked; repeat while locked. Cover client scope separately. | Matching approvals execute without a new prompt; a changed exact operation needs consent. Attached grants do not authorize detached execution. |
| Accepted one-use approval | Use advanced `request` with metadata-only operation input, obtain Allow once, then lock before calling `run`. Redeem before expiry. | One execution succeeds while locked; replay cannot execute. Ordinary `exec` redeems immediately and cannot stage this case. |
| Detached policy-only review | Use `leave-pending` with a short approval timeout, then lock/unlock. | Review affects future decisions only; the old command cannot execute and Allow once stays unavailable. |
| Auto-allow and deny-all | Change modes only in the isolated store. | Auto-allow executes while locked; deny-all rejects a saved grant. Restore Ask afterward. |
| Broker restart while locked | Save a test grant and separately hold an unexpired one-use reservation; restart only the acceptance broker. | Native startup detects unavailable presentation; the saved grant works, new consent fails, and the still-unexpired one-use reservation is rejected. Expiry alone does not prove restart invalidation. |
| Session disconnect/reconnect | Agree a reversible route before disconnecting. Otherwise record untested. | Saved approval works while disconnected; fresh consent fails; reconnect does not revive requests. |
| Explorer detached launch | Launch a synthetic-token task with `--detach` and a short execution limit; repeat under a saved detached grant while locked. | Explorer is the helper's parent; status identifies the test task; stop ends its tree. No fallback launch path is used. |
| Offline diagnosis | Stop the acceptance tray and broker, then diagnose captured failures. | Safe reason, request/attempt correlation and launch certainty remain readable without enrollment, provider fetch or file mutation. |
| Settings and cleanup | Inspect a failed request in Activity; clear activity only after preserving local evidence. | Columns match the safe failure metadata; clearing activity preserves test grants and durable operation history. |

Keep Settings presentation checks separate from locking. Give one human
instruction at a time. Arm the runner before a lock, agree a generous fixed lock
duration, and state when to unlock in advance. Never depend on reading chat while
Windows is locked. Do not sign out.

For offline diagnosis, prefer the original attempt ID:

```powershell
& $helper diagnose --data-root $dataRoot --request $requestId --attempt $attemptId --output-mode json
```

`--last-failure` follows its recorded attempt, which may belong to another request.
An ambiguous request requires selecting the original attempt. Unknown or
unconfirmed launch certainty must not trigger automatic relaunch.

## Evidence, stop conditions and rollback

Keep package hashes, request/attempt identifiers, session observations, exit
codes, safe reasons, launch state and pass/fail/untested outcomes in private
local evidence. Never record real secret values, environment dumps or child
output in public documentation. See [public documentation rules](PUBLIC_DOCUMENTATION.md).

Stop on an unexpected or duplicated launch, secret disclosure, an invisible
consent prompt, an unstoppable test child or unresponsive Windows controls.
Preserve the original correlation locally and diagnose before retrying.

Exit the candidate and restart the recorded ordinary installation. Verify its
normal profile, data root and readiness. Preserve local evidence and remove only
explicitly identified synthetic sources, grants and tasks when cleaning up.
Production grants and history must remain intact. A production smoke check needs
its own explicitly attended result.

CI and package publication do not mark native cases passed.
