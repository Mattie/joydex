# Asynchronous question hook validation

Validated on 2026-10-13 against Windows package `26.1007.2314.0`, app `26.1007.21434` and
managed Codex `0.162.0-alpha.17.2`, the same runtime as our October 11 compatibility baseline.

## Exact-runtime probe

A temporary loopback Responses fixture supplied one scripted `request_user_input_async` call
and a final assistant message to the installed executable. It used no remote model inference,
API key, real user question or persisted Codex task. The recorder saved only fixture metadata.
User config and execpolicy rules were omitted; inline, inspected diagnostic hooks used the
one-invocation hook-trust bypass. The ephemeral session intentionally had no transcript path,
so the production relay's persistent-top-level-session gate would ignore it.

The observed sequence was `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `Stop`.
Both tool hooks carried `tool_name=request_user_input_async` and the original `tool_use_id`.
`PostToolUse.tool_response` was the JSON string `{"accepted":true}`. The turn completed without
any user answer. Repeating the probe with Code Mode and Code Mode Only enabled produced the
same direct-call hook sequence and acknowledgment. This validates the builtin handler with
those features enabled; it does not establish nested Code Mode callback behavior.

Development-time inspection of the verified Desktop bundle found that question-card reply IDs
encode `["request_user_input_async", callId, questionIndex]`. Replies use the
`<send_user_message_question_reply>` envelope, with either one reply object or an array.
The current [official hook documentation](https://learn.chatgpt.com/docs/hooks) specifies
function-name matching and the `UserPromptSubmit`, `PreToolUse` and `PostToolUse` paths.

## Correction

The previous matcher omitted the async tool and the relay mapped only blocking user-input calls.
Adding the async tool to that blocking path would falsely treat acceptance as an answer.

The updated completion matcher includes `request_user_input_async`. The relay creates pending
question events only after an accepted acknowledgment, hashes each session/call/index key and
resolves matching reply IDs from `UserPromptSubmit`. Failed calls do not create orphan attention.
Blocking question and approval correlation keep their existing behavior.

Task alerts keep the attention color while any async question is pending, including while the
agent continues work or completes its turn. Duplicate acknowledgments are idempotent; other tool
completions and ordinary prompts cannot dismiss the question. Partial answers resolve only their
matching question. Restart snapshots add an optional `pendingAsyncQuestions` hash array; existing
schema-v1 files without that field still load. Question and answer contents are not persisted.
The existing fault, manual-clear, disable and 24-hour attention-lease cleanup still apply.

All events from one hook travel in one bounded pipe packet. This avoids losing later questions
or answers when the best-effort relay encounters a busy pipe between separate connections.
Legacy single-event packets remain supported. The receiver validates the whole batch before
publishing its events in order.

The focused unattended filter passed **110 tests**, including the compiled relay and production
named-pipe path, partial and unknown replies, failed acknowledgments, duplicate events, stop,
restart, existing blocking approval behavior, suppression and runtime alert connections. A
separate Native AOT publish and pipe probe passed with two question events followed by a prompt
and two matching answer events; this also checks the relay without reflection-based JSON
serialization. These checks did not operate physical controller LEDs.

## Activation

This is a source correction. The running package and live user hook definitions were not replaced
during CHECKHOOKS. Install the next Joydex package, use its hook Install/Repair action and review
the changed completion matcher in Codex's Hooks UI. Codex requires trust for the exact changed
hook definition. The temporary fixture server was stopped after validation.

The probe used generated input. Retained diagnostic evidence contains status and correlation
metadata, without user prompts, answer text, credentials or room audio.

## Installation

The tested source snapshot was activated on 2026-10-13 through the normal Joydex profile after
graceful shutdown and rollback backup. Installed files matched the clean published candidate
hashes. Explorer relaunched the normal tray, and its control-pipe probe confirmed attachment
to RuntimeHost.

Hook Repair corrected stale relay paths in all five handlers to use the relay beside the active
application, and added the async completion matcher. Unrelated hook definitions were unchanged.
Codex marked all five Joydex definitions modified; after the user reviewed them in Hooks, the runtime's
`hooks/list` reported all five enabled and trusted, with no errors or warnings. Joydex's own
inspection reported `Installed`.

The installed native relay passed the two-question/two-answer pipe probe. Saved configuration,
voice-engine targets and task-alert settings retained their pre-installation hashes. Rollback
files were preserved before replacement. The attended pending-question and live completion
checks are recorded below.

## Attended pending-question and reply check

HOOKTEST on 2026-10-13 used a real question card in the installed Desktop chat. The production
state assigned this chat to overflow slot 5, M1 B1. An accepted question changed it from Running
to Approval with one pending hash; the user confirmed yellow while the card waited. Submitting
the card cleared that hash and returned the assignment to Running. The user observed white,
consistent with the configured neutral RGB `#555555` running color on the hardware.

A bounded, content-free observer recorded the actual pending-to-reply transition. It did not
record a Stop/CompleteAfter transition in the pre-reply interval, so this attended check does not
establish stop-grace behavior with a question
still pending. Synthetic stop/restart coverage and the exact-runtime CLI Stop probe passed as
described above.

GREENCHECK subsequently confirmed the live completion path after the answer: the observer recorded
Stop scheduling, the one-second grace period elapsing and the assignment reaching Completed with
no pending questions. It stayed Completed until the next user request returned it to Running.
The configured completion color is green (`#004000`). GREENOK confirmed physical green after the
response finished, completing the attended yellow attention, neutral running and green completion
cycle. The bounded diagnostic observers were stopped after recording these results.
