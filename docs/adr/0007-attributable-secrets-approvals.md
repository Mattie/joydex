# Attributable secrets approvals

Status: accepted, 2026-09-13; proposed 2026-09-08; refreshed and renumbered 2026-09-11.

Authorize secret use through a separate local broker using a stable same-user requester identity, canonical project/worktree and an immutable request operation. The helper provisions that protected local identity automatically on its first valid `exec` call; the user configures secret sources and responds to the consent toast, with no agent-registration step. **Allow once** is single-use, **Allow always** persists an allow until revoked, **Allow for 24 hours** persists an allow with a fixed 24-hour expiry, **Deny this time** denies one request, and **Deny forever** persists a deny until revoked. The toast defaults **Apply my decision to all future commands from this agent** to unchecked. Unchecked remembered decisions stay bound to the displayed operation. Checking it disables the two one-request choices and creates a client-scoped allow or deny for the same local requester, canonical project/worktree, exact secret set and delivery mode, without binding one operation. The same-user requester label provides stable cooperative attribution, not proof of a particular Codex task; enable verified task attribution only when origin is supplied by trusted host context rather than caller text.

Make direct execution the ordinary agent workflow: one `joydex-secrets exec` call names same-named environment aliases, the exact executable and its argument vector, then requests, waits and runs. Keep operation-file and saved-recipe support as advanced automation plumbing and omit recipes from the main Secrets UI. The toast marks aliases that are new for the authenticated agent and project; changing the requested secret set changes the approval scope and asks again.

Publish `%LOCALAPPDATA%\Joydex\profile.yaml` atomically whenever the normal tray starts. This value-free profile records the current data root and packaged helper path so agents can locate the matching `joydex-secrets.exe` without process inspection. Demo and settings-only processes do not replace it. Treat the file as a discovery pointer rather than proof that the broker is running; use one file instead of maintaining a duplicate registry locator.

The helper cancels by default when its approval wait ends. Two explicit fallbacks detach the helper before doing anything else. **Run without secrets** may start the exact command with none of the requested variables. **Leave pending** stops without starting it. Both leave a policy-only **Agent Detached** review state. It has no reservation and can never become executable; one-use approval is unavailable, dismiss replaces one-use denial, and a future remembered rule requires the user to select the agent-wide checkbox.

Keep raw values out of ordinary engine/UI events and prefer approved child-process injection. Log every request attempt before policy matching, including preapproved allows and denies, consent-pending requests, and failed requests, then append its linked outcome without raw values. Future provider work includes pasting a token into the local Secrets UI and writing it directly to a broker-owned encrypted store, with a replaceable store boundary for later vault work. The implementation slice must also ship agent skills that explain alias discovery, approval, waiting and approved secret use without exposing values or bypassing consent. The initial same-user design provides cooperative consent and audit; it does not claim to isolate unrestricted tasks from accessible .env files or same-user DPAPI. This trades a small, useful experiment for postponing a substantially different OS/vault isolation design.

Keep global operating mode separate from scoped remembered decisions. The normal mode evaluates remembered decisions and asks when none match. A local user may temporarily auto-allow every otherwise-valid request for a fixed 24-hour window after acknowledging a warning, or deny every request until the mode is changed. Auto-allow takes precedence over remembered denials only while its fixed window is active; deny-all takes precedence over remembered approvals. Both modes continue to require a valid protected local requester identity, a current canonical project/worktree, a valid operation, and an available configured secret. A mode change advances an epoch and invalidates unredeemed reservations. Record durable lifetime and rolling 24-hour counts for requests, executions, approvals, and denials independently of the clearable activity log.

Updated 2026-09-15: Default `exec` to byte passthrough for stdin, stdout and stderr, with the exact child exit code and no success envelope. Retain bounded, redacted results through explicit `--output-mode json`. Output mode stays in the authorized operation scope. Separate current-user pipes carry child bytes outside broker replies and audit records, and the broker verifies that their server belongs to the requesting process. Losing the wrapper connection cancels the child. Approved programs control what their output reveals; default output forwarding does not scrub credentials.

Updated 2026-09-23: Direct execution retains the requester's current directory and environment. The helper sends its environment to the broker only in the authenticated run command after approval; the broker overlays approved secret values before launch and never records the inherited values in audit or activity. The operation digest marks this changed launch policy, so earlier exact-operation approvals do not carry over. Client-scoped rules continue to apply to future commands in their existing agent, project, and secret scope. Remembered approvals do not bind individual inherited environment values. The detached run-without-secrets fallback inherits the same caller environment and omits only broker injection.

See the [secrets design](../design/joydex-plugins/07-secrets.md).

Updated 2026-09-29: Support explicit attached and detached execution lifetimes. Attached
execution remains the default and is owned by the broker. Detached execution uses one
headless SecretsHost per task, launched through the existing Explorer desktop; no persistent
service or reboot recovery is introduced. Both use an unnamed, non-inherited kill-on-close
Windows job, assigned atomically with PROC_THREAD_ATTRIBUTE_JOB_LIST and restricted inherited
stdio handles. Root exit, cancellation, timeout, relay failure, and explicit stop clean up
descendants and confirm the job is empty. Broker shutdown cancels attached work and waits up
to ten seconds before forced cleanup. Detached helpers are outside that cleanup.

Use a bounded current-user pipe with verified peer PID, start time where available, session,
and executable for the 15-second handoff. Command, environment, and credentials remain in
memory. The helper prepares its job and holds validated file leases before ready; the broker
rechecks authorization, consumes the reservation, and durably records commit before sending
the single launch commit. A lost acknowledgement yields an uncertain task ID, never an
automatic retry. Permanent request and task claims survive bounded audit pruning.

Detached timeout defaults to absent, with an optional 1–2,147,483-second duration. Lifetime
separates attached and detached exact/client grants; missing legacy lifetime means attached.
Duration participates in exact operation identity. Existing tasks require explicit stop;
future denial cannot retract delivered credentials. The helper's verified control pipe and
atomic value-free receipts support broker-independent status/stop. Missing helper and final
receipt mean unknown. Stdout/stderr logs each retain three 10 MiB files and do not promise
redaction. This favors a small per-task owner over a second long-lived service.
