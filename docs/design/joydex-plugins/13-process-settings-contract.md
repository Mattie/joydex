# PROCESSUI contract checkpoint

Status: behavior accepted for implementation, 2026-09-11. Source base: `a4d3356`.
Exact code contracts and integration revisions require review before adoption.

## Lifetime and composition

Keep `Joydex.App` as the installed entry point and startup registration. It launches
or attaches to one production RuntimeHost for the current user/session, bound to
its selected data root and exact configuration file. Preserve a custom `--config`
filename. Two configuration files in the same directory are distinct selections;
equivalent normalized Windows paths identify the same selection. A second normal
selection must not create another hardware owner; report a configuration mismatch
rather than weakening the existing singleton.
Strictly synthetic demo/test instances may have independent roots and endpoints. Ordinary
tray and settings processes are clients. Repeated Configure brings forward the
existing settings client; explicit Exit Joydex stops the runtime.
Production ownership also retains the legacy `Local\Joydex` guard for the entire
hardware-owning lifetime, including after client loss, so older builds cannot
create a competing owner.

RuntimeHost owns controller/input, PAD, Pebble, task-alert and Desktop broker
lifetimes. Voice remains a runtime-contained aggregate for this milestone, with a
dedicated STA/message pump that starts before WebView2 and remains through ordered
media disposal. Client cancellation never owns those lifetimes. Fatal media/native
failure isolation is deferred to VOICEPLUGIN, as recorded in [ADR 0009](../../adr/0009-separate-settings-from-runtime-with-owned-media-sta.md).

## Connection and observations

Use versioned duplex local named pipes with a .NET 8-compatible pinned transport,
current-user/session access restrictions, non-elevated peer checks and child launch
tickets. The host issues connection identity; callers cannot assert it. Negotiate
protocol/capabilities and message bounds before exposing commands. Normal and demo
instances use separate roots and endpoints.

Production launch tickets are bound to the expected peer process. The headless
synthetic test entry may issue short-lived, single-use HeadlessTest tickets on its
inherited stdout before test clients start. This exception requires a unique
synthetic endpoint, current-user pipe access, and an existing validated scratch
configuration outside the normal Joydex data tree with dry-run enabled. It cannot
authorize a production, tray or settings connection. This test-owned entry exits
when its stdin closes; production runtime lifetime remains independent of clients.

Production tray admission uses a current-user rendezvous. In addition to peer
user/session/elevation checks, verify the final executable path and file identity
of the expected `Joydex.App` in the host's frozen deployment root. Bind the ticket
to that process ID and creation time, and atomically reserve the sole tray role at
issuance through attach or expiry. Release failed reservations and disconnected
tray roles without releasing runtime ownership. A replacement tray uses the same
verified rendezvous; it need not be a child of the surviving host. The host launches
settings with a process-bound ticket passed through an inherited pipe. Application
identity is cooperative; hostile code running as the same user remains outside
this boundary. Process handles bind PID, creation time and session; the file checks
verify the current deployment entries. Hostile same-user file replacement or code
injection is outside this guarantee. Replacing the deployed files while a host is
running can require stopping that host before reconnecting with the new package.
Competing App launches retry a starting rendezvous within a bound; they
cannot acquire devices while another host owns the session.
Unexpected completion of either the runtime RPC listener or tray rendezvous is
terminal for RuntimeHost. Unwind the owners and release the guards after cleanup;
a failed listener cannot leave hardware running with no way for clients to reconnect.
Listener disposal finishes its tracked connection cleanup before reporting a failure.

The tray's `OpenSettings` command asks the host to launch or bring forward its one
settings child. Only the tray may issue this command. The host owns the child's
launch and ticket delivery; losing that child releases its client resources while
the runtime owners continue.

The inherited host-to-child channel carries bounded Bootstrap, Activate and Reconnect
messages. A reconnect uses a fresh one-use ticket bound to the same child's verified
process identity. An old connection's delayed disconnect cannot invalidate a newer
attachment. The child keeps its editor and uncertain-operation state across the new
connection, disables Apply and capture while disconnected, and looks up accepted
operations without resubmitting them. Channel EOF ends the child lifetime.

The Windows inherited stdin reader must start off the UI thread: its `ReadAsync`
can block synchronously while waiting for the next control message. Shutdown cancels
the coordinator and retires its RPC attachment without waiting for that OS read.
Closing the .NET console stream cannot reliably interrupt the read, including when
no RPC attachment exists to trigger another host message. This one background read
has the Settings process's lifetime and is released when that process exits.

Failure to reconnect an already-attached, still-living editor preserves that child
and its sole-child reservation. A later explicit Configure can retry the same child;
a reconnect timeout cannot justify destroying its unsaved draft. Initial startup
that never reached attachment may be cleaned up, and normal host shutdown closes
the owned child. Unconfirmed cleanup blocks replacement and remains visible during
host disposal. A successful authenticated late attachment supersedes a prior timeout.

Attach returns a snapshot, engine epoch and event cursor. Events carry epoch and
sequence; a gap or epoch change requires resynchronization. Bound subscriptions and
requests. The shared retained stream includes source inventory and settings/health
changes. Raw input observations and capture results use an independently ordered,
connection-scoped stream; raw observations may coalesce. Capture completion remains
recoverable by its owning live connection and cannot be displaced by observations.
Operation results and recoverable final Voice entries must remain observable.
Reconnection obtains fresh state without overwriting a dirty editor draft.
Bundled UI clients require minor 2 and `reliable-cursors.v1`. Snapshots include the
connection's acknowledged input cursor; the runtime cursor is sampled conservatively
before reading state. Clients retain callbacks received during refresh, preserve
operation/capture notifications and merge replayed activity by its sequence identity.
Initialization also renders the current state explicitly, since its notification can
arrive before the UI subscribes.

Captures belong to the connection and use the existing host-owned input path.
Observed disconnect immediately releases them; the bounded lease timeout remains
a fallback. Preserve held-control filtering, release suppression, source generations
and shared-key ownership from HOSTSEAM.
Observation release identifies the individual capture and the source generation it
acquired. A delayed release from an earlier capture cannot release a newer capture
of the same source after owner replacement.
Capture snapshots contain only that connection's captures. A reconnecting client
sees the previous connection's capture as ended, with no transfer of raw results.

Valid settings can exceed the single-message limit, including full prompt libraries.
Protocol minor 2 negotiates `settings-transfer.v1`: small values retain their inline
wire format; larger settings requests, responses and callbacks use bounded sequential
chunks. Private transfer references belong to one physical connection, expire, and
are consumed once. Verify the complete length and SHA-256 before deserializing or
passing a transferred request to the settings authority. Bound concurrent transfers
and clean their temporary storage on connection disposal. Callers never supply
storage paths. Older peers receive an explicit size/upgrade error for oversized
values. Transfer limits must not truncate valid prompts or introduce new domain
limits on configuration contents.

## Settings and commands

The host is the sole application writer of companion, Voice, Pebble and task-alert
settings. Standalone prompt edits and tray commands participate in the same
authority. UI-only window placement stays local to the UI. Attach is read-only.

The Voice tool helper reads a separate, host-owned projection of active Voice
preferences. It must never reload Desired from the canonical settings file during
a send. The host publishes the projection before advancing active routing; a failed
publication preserves the prior active target. The helper's read is noncreating
and does not migrate or rewrite the file. This derived file is outside the settings
journal and can be rebuilt from the host's active state.

Snapshots distinguish desired, active and pending state, validation and revisions.
Prepare validates the changed aggregates and returns the exact effects plus an
expiring token bound to the connection, epoch, base revision and normalized payload.
Apply rechecks the base and current file fingerprints under the mutation gate.
External edits observed during validation or backup capture become pending
candidates and cause Conflict with zero authoritative-file/runtime mutation; the
editor retains its draft. Fingerprints do not provide atomic compare-and-swap
against an arbitrary external editor: a nonparticipating writer can still race
file replacement after the last backup capture. All Joydex application writers
must use the host authority. This limitation applies to concurrent out-of-band
filesystem edits and must remain explicit in acceptance evidence.

Apply binds an operation ID to the prepared payload. A retry after a lost reply
returns the recorded outcome for that operation, including after its preparation
token has been consumed. Status, commit/close flags, errors, per-aggregate outcomes
and revisions, and completion revision remain immutable. The snapshot returned by
Apply replay or operation lookup describes current authoritative settings at that
request; it is not a historical configuration archive. This lets reconnecting
editors recover an outcome and rebase against current state after intervening edits.
Persist compact outcome records so retained operations do not duplicate complete
configuration snapshots. Retain the last 64 completed operations in completion order,
including across restart; several completions may share a settings revision. Replay
and payload binding apply while an outcome is retained. A missing result after
eviction leaves an uncertain outcome and cannot authorize blind resubmission.
Within retained history, an ID cannot be reused with a different payload. Preserve
handled-failure exact-byte rollback and journal enough state for crash recovery
between persistence and activation.

Bind the journal to the exact normalized configuration selection before any recovery
write. A different selection cannot recover a pending transaction from that journal.
When a completed journal is reused for another selection, start its operation history
from the newly selected files. This preserves custom configuration filenames even
when they share a directory with another selection.

Distinguish persistence failure/rollback from activation failure after a durable
desired-state commit. Report per-aggregate active/desired state truthfully. Voice
changes during a call default to PendingIdle; ending the call to apply is explicit.
Apply keeps settings open and rebases successful changes. Save and close uses the
same operation and closes only when every changed aggregate is durably saved and
Applied or PendingIdle. Conflict or failure preserves the draft.

Provisioning, task navigation, tuning and other existing explicit
actions remain typed host commands. Opening pages or saving ordinary edits cannot
invoke them implicitly. Reconcile uncertain accepted operations by operation ID;
do not blindly retry side effects after losing a reply.

Preserve the existing tray and auxiliary windows through typed state and commands:

- Prompt selection, guarded insertion and submission stay host-owned. The overlay
  receives selection state and can request dismissal. The standalone prompt editor
  saves through the same Companion settings authority.
- Controller maps keep local window placement and receive host visibility requests,
  active bindings and task-alert colors. Controller status and bounded dry-run
  activity remain available to the tray and Test Controls window.
- Task-alert snapshots include assignments, bank, suppressions, event traces and
  effective LED output. Ignore/re-enable, LED changes and Codex hook installation
  keep their existing behavior, with preference and hardware changes owned by the
  host. Codex hooks are separate from the Desktop Task Bridge.
- Room Voice retains live state, conversation updates, final history, saved target
  changes and manual outbox recovery. Clear view, copying text and window placement
  remain local UI actions. An otherwise target-only Voice change takes effect
  through the settings authority without restarting its Voice owner. If another
  Voice change is already pending, update the target in Desired while preserving
  that edit; the combined change remains PendingIdle. Keep showing the active
  target and report that the new target is saved for after the call.
- Pebble retains live receiver/recovery status, task selection, explicit access
  copying and opening its inbox. This milestone adds no token-rotation or delivery
  retry action.

Each client can recover current presentation state after reconnecting without
restarting its owners. Bound live updates and use snapshot resynchronization when
history is unavailable. Do not include Pebble transcript bodies or access secrets
in shared status events.

## Ownership and validation

The settings lane owns contracts, transport, host transactions, RuntimeHost and
shared composition in serialized slices. The Voice lane owns VoicePeBridgeRuntime,
WebView2 media and the dedicated STA boundary until those files integrate. The
validation lane adds independent headless client/process tests after the code
contract is reviewed. The overseer owns decisions, integration and demo evidence.

Use real temporary stores and local pipes, headless child clients, synthetic owners
and unique endpoints for background tests. Cover stale/out-of-band edits, rollback,
journal recovery, operation replay, event resync, client death during capture and
reconnection to unchanged owner identities. Visible UI and physical media checks
wait for an explicitly started demo; their absence must remain visible in results.
