# Reconcile task lights from positive Desktop observations

Hook delivery alone cannot keep lights correct when the receiver is briefly unavailable or
Codex exits before sending Stop. The task-alert runtime therefore owns a cancellable background
reconciler and a lease on the existing shared Desktop Task Bridge, independently of Room Voice.
It releases that lease after joining the reconciler during shutdown. It creates no extra
production hardware owner, hook registration or task writer.

Codex App Tools requires a real source task identity. Reconciliation uses an identity already
received through a local lifecycle hook or restored assignment, including a PostToolUse hook
whose start was lost. Without one it waits and records a diagnostic. The scan requests the
bounded recent catalog and reads the latest turn of known assignments directly, even when those
tasks are absent from the catalog. Only local saved Codex tasks are eligible. Remote and cloud
tasks remain outside this recovery path.

An active task with an in-progress latest turn can recover Running. An idle or not-loaded task
with a confirmed completed, failed or interrupted latest turn can retire an existing assignment.
Completed, failed and interrupted turns clear question attention; failed and interrupted turns
become Fault. A positive new-turn observation also retires the prior turn's questions.
Terminal observations never allocate historical idle tasks. Missing,
unavailable, contradictory or unfamiliar states preserve the existing assignment. A legacy hook
without a turn ID requires an observed turn spanning that hook's timestamp. Generic waiting
flags cannot identify questions or user dismissals and do not create attention.
Any UserPromptSubmit hook retires questions that preceded it, including question-card replies
and hooks with missing or unchanged turn IDs. A matching Stop hook retires question attention
and retains the normal completion grace. A known turn's late question cannot reopen it after
Stop, and a mismatched or missing Stop turn ID cannot close a different known turn; positive
Desktop terminal confirmation supplies that fallback. Question hashes, originating turn IDs and
receipt times survive restart without storing their text. Receipt times let a delayed prompt
retire old questions while preserving a question already received after that prompt. Relay receipt
time is not native event time: missing or contradictory identities remain a compatibility gap,
rather than proof that a different turn ended.
Questions arriving for a different known turn wait without changing its light or event watermark.
At most 32 content-free question origins are deferred per assigned chat, matching one hook batch;
overflow is counted as dropped. This bounded state survives restart. A matching prompt or active
Desktop observation promotes the questions; terminal confirmation drops them. The event trace
labels these receipts Deferred rather than presenting them as a visible question update.
The accepted turn boundary is persisted separately from generic hook receipt time. A late Stop
for the previous turn therefore cannot make a genuinely newer prompt or native turn look older.
Cross-turn timestamp ordering uses whole seconds, matching Desktop metadata precision, so a
fast next turn is not rejected solely because the prior hook had a millisecond timestamp.

Each scan captures per-task hook revisions and the current ignore/enable policy revision before
reading Desktop. A hook accepted during the read, including one still queued for reduction,
invalidates that task's observation. Ignores, internal Voice-task suppression, question dismissals
and terminal acknowledgments retain authority. Acknowledged turns cannot be resurrected by a
lagging active observation during the same runtime lifetime. Positive active observations renew
a separate persisted lease heartbeat; they do not advance hook ordering timestamps.

The broker projects only task/turn identity, lifecycle timestamps and workspace identity. It
does not forward titles, messages or tool output into task alerts. Oversized projected frames
omit workspace paths while retaining their ignore hashes. Unknown states and transport failures
produce content-free diagnostics; hooks continue independently. Scans run roughly every five
seconds after the prior read finishes and retry reads after recovery, without replaying mutations.

HookRelay also retries unavailable or busy pipe opens for at most approximately 250 milliseconds.
It preserves the original event timestamp and never retries after connecting or attempting a
write, where delivery may already have happened. Normal startup and hook paths stay unchanged.
An extended outage can still lose a hook. Recovery needs a usable Desktop connection and at least
one real local identity; if neither a start nor any later hook reaches Joydex, an empty installation
cannot discover that task until a hook does arrive. The bounded catalog can also omit older active
tasks whose identity Joydex never received. Physical LED behavior still needs attended validation
after deployment; software state and successful output writes alone do not prove it.
