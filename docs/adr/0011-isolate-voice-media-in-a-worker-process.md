# Isolate Voice media in a worker process

Status: accepted for VOICEPLUGIN implementation, 2026-09-12.

Complete the process boundary deferred by [ADR 0009](0009-separate-settings-from-runtime-with-owned-media-sta.md)
with a bundled `Joydex.VoiceWorker.exe`, registered as `joydex.voice-pe`.
The worker owns the existing Voice bridge, dedicated task owner, media STA,
WebView2 session, conversation model and archive writer. Reuse those implementations
without changing firmware, audio timing, fallback mode or archive paths.

RuntimeHost remains the settings authority and supervises the worker. It sends the
active configuration and retains PendingIdle, activation rollback, the shared
Desktop broker lease and guarded action capabilities. The worker cannot adopt
external preference edits independently. Existing Room Voice commands and snapshot
presentation remain the user-facing interface; plugin inspection adds bounded
lifecycle health without a duplicate set of controls.

Use an exact-child, current-user pipe for narrow commands, snapshots and callbacks.
Do not move audio frames or private tool arguments through the visible UI contract.
An old worker's callbacks cannot publish into a replacement generation. Confirm
worker cleanup before replacement; uncertain cleanup blocks Voice replacement.
Each generation belongs to a kill-on-close Windows job before authenticated media
startup. Orderly stop has a bounded deadline, followed by job termination and
confirmed exit when needed. Host exit must also terminate worker descendants.
Use Windows [job object](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)
and [nested job](https://learn.microsoft.com/en-us/windows/win32/procthread/nested-jobs)
semantics without granting breakaway from the outer Voice generation job.
Ordinary Voice worker failure must remain local to Voice while unrelated runtime
owners continue. This trusted same-user worker is not a hostile-code sandbox.

Preserve the existing capped restart backoff and stability reset. A rejected
activation candidate must still fail and roll back rather than being reported as
applied merely because supervision exists. Keep settings-process lifetime separate
from media and worker lifetime.
The task-alert exclusion follows the committed Dedicated Voice Task through
candidate startup, rollback and retries; failed candidates cannot replace it.
This temporary exclusion never changes the user's persisted ignored-task rules.

Background tests establish command, state, ownership and recovery behavior.
Physical microphone, speaker and live-call continuity remain separate attended
evidence. The running PAD canary is not replaced as part of implementation.
