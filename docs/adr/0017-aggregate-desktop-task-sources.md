# Aggregate desktop task sources in the existing task pool

## Decision

Local Codex and optional T3 environments share the existing coordinator, primary/overflow
slots, retention and hardware owner. No additional production owner or server daemon is added.
T3 is disabled until explicitly configured and connection-tested. Monitoring never changes
keybindings, hooks, hosting or approvals.

Desktop-following mode discovers the local T3 server and the desktop app's enabled remote
environments. Native environment identity deduplicates routes; connection IDs are route
metadata, never task identity. Disabled sources are not read. Every discovered environment has
independent health, freshness and failure backoff. Discovery failures and unsupported routes
are visible and preserve retained observations. A successful local read cannot certify a remote
source. The existing pinned single-SSH-source mode remains supported.

Identity is `(backend, environment, native thread)`. Legacy Codex keys retain their spelling,
comparison and slots. T3 components remain ordinal through a canonical encoded pool key that
cannot collide with legacy hook IDs. State schema 2 migrates schema 1 to local Codex while
retaining a recoverable `.v1.bak` file. Runtime protocol 1.4 advertises `aggregate-tasks.v1`;
older clients receive only local assignments and cannot modify a configured T3 source or
issue remote navigation/dismissal commands.
Protocol 1.5 adds `t3-desktop-sources.v1` and per-environment health. Older settings clients
cannot replace Task Alerts preferences while desktop-following mode is configured, which
prevents a full-object update from silently erasing the discovery setting.

Each source carries connection generation, snapshot sequence, capture time and completeness.
Tracked T3 threads missing from the shell catalog are read directly. Detailed runtime requests
supplement its single pending-request summary. Missing fields, partial lists and timeouts
cannot establish completion or retire an async question. Unavailable sources retain their
last state and cease renewing the existing 12-hour Running and 24-hour Attention leases.
Restored T3 observations start unavailable until positively refreshed.

Question acknowledgements persist with task, run and request identity in a bounded collection.
A positive ordinary user-turn timestamp or explicit request resolution retires the matching
question. A successful Joydex visit acknowledges only the questions captured before navigation;
newer questions and blocking permissions remain pending. Turn-end cleanup remains opt-in and
defaults off. Generic update timestamps and catalog absence are not user engagement evidence.

The packaged Python projector runs through a configured current-user SSH alias. Each finite
sample issues a short-lived, read-only orchestration grant, reads loopback APIs and revokes
the grant before returning whitelisted metadata. Credentials stay remote. Five-minute expiry
bounds a grant whose cleanup is interrupted. Samples do not overlap: the next starts five
seconds after a successful sample finishes, with bounded deadlines, payloads and failure
backoff. Source identity is pinned, independently of its display name.

Titles, projects and optional provider/model labels live in a bounded private metadata cache,
separate from alert restart state. Prompts, previews, messages and credentials are excluded.
Codex labels use its existing Desktop bridge catalog independently of lifecycle reconciliation.

Completed history does not allocate new slots at startup. Consecutive complete observations
from the same source generation can recover a run that both starts and finishes between polls,
using its native run identity and timestamps. Partial reads, outages and source restarts reset
that recovery baseline; a generic update timestamp never establishes new work. Recovery uses
the previous shell catalog's observation boundary, recorded before its fetch, rather than the
end of later detail reads. End-of-read capture time still records freshness. A missing or
backwards catalog boundary disables recovery without discarding positive task observations.

Task navigation preserves the assigned identity. Codex uses its desktop URI association;
T3 launches the verified installed executable with its desktop URI as a single argument.
The current-user protocol command takes precedence; the merged registration is used only when
the user command is absent. Invalid overrides and failed file/product verification do not
fall through to another executable. Read diagnostics retain safe verification stages and
numeric CLI exit codes, excluding raw process output and exception details.
When the fixed native registry probes confirm both protocol commands are absent, the
current-user local server's runtime descriptor can identify one live executable instead.
That process must match the current user and desktop session, have started no later than the
descriptor's timestamp, and pass the same executable/product checks. This never scans by process name or
overrides an invalid present registration. PID reuse and unavailable process evidence fail closed.
This avoids a Windows application chooser when protocol dispatch fails despite an existing
T3 registration. An unverified executable or failed launch acknowledges no question alerts.
Shared physical New/Previous/Next/Stop actions inspect the foreground app. T3 dispatch requires
verified executable, exact window, active environment, effective bindings and applicable context;
foreground is checked again immediately before input. Codex-specific actions cannot fall back
to Codex when T3 is foreground. Held-key cleanup and internal device actions retain precedence.
Room Voice and prompt-picker submission remain Codex-owned.

Stock T3 exposes no verified external active-environment/keymap context. Its common actions
therefore remain unavailable. The semantic `stop-task` action preserves older action IDs but
has no invented Codex command or shortcut; Codex Stop is unavailable until independently verified.
Newer versions receive capability checks rather than a version-only rejection.

Explicit shortcut setup reads the effective server keybindings before writing. Adding a custom
rule can replace all defaults for that command, so setup preserves those resolved default rules,
skips already customized commands and rejects a candidate that changes another rule's winner.
It preserves order, journals additions, detects intervening edits, verifies the resulting profile
and removes only unchanged Joydex additions. If the effective profile cannot be proven, setup
returns an unsupported preview without writing. A successful setup does not enable desktop
dispatch without active-context proof.

## Acceptance boundary

Offline state, projection, protocol, navigation and input-fake tests establish software behavior.
Actual desktop arrival, physical lights/buttons and every enabled action require separate attended
acceptance after an explicitly authorized installation. Unavailable stock-client actions are
limitations, not passing acceptance. CI and completed GitHub reviews precede human integration;
this decision does not authorize a merge or live rollout.
