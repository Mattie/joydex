# Pebble Index plugin

Status: proposed. Plugin ID: joydex.pebble-index. Baseline: merged Pebble PR #11 on main at `3d7c673`, refreshed 2026-09-11.

## Outcome

An optional independent receiver accepts authenticated transcript-only webhooks from the Pebble Index Android integration and delivers each accepted transcription to one configured Desktop task. It runs without Room Voice, controllers, PAD or an open Joydex window.

Preserve loopback-only exposure, existing phone authorization syntax, fixed target behavior and manual review of unconfirmed delivery. A phone-to-PC forwarding mechanism is separate deployment infrastructure; plugin extraction does not expose a new public listener.

## Existing aggregate

- src/Joydex.App/PebbleIndexReceiverRuntime.cs owns listener, parsing, accept and delivery loops.
- PebbleIndexSettingsControl owns its existing settings page.
- src/Joydex.Core/Voice/PebbleIndexPreferences.cs and PreferencesStore own preferences.
- PebbleIndexDeliveryStore owns the durable inbox and duplicate identity.
- PebbleIndexSecretStore owns the currently plaintext bearer token.
- tests/Joydex.Tests/PebbleIndexTests.cs covers normalization, dedupe, target freezing, authentication and file rejection.

Move these feature-specific types into the plugin domain. Inject the shared Desktop send capability, paths, diagnostics and health publication. Do not make Pebble part of the Voice plugin simply because its current files share that namespace.

## Wire and storage compatibility

Preserve the initial HTTP contract:

| Surface | Existing contract |
|---|---|
| Bind | 127.0.0.1, default port 5187 |
| Health | GET /health, no token; health only, no task/content/secret metadata |
| Ingress | POST /pebble-index, authenticated before multipart parsing |
| Content | Transcript-only multipart; audio/files rejected |
| Bounds | 16 KiB headers, 64 KiB request, 4,000-character transcript, eight concurrent clients, 15-second request deadline |
| Queue | Current scheduling capacity 32; durable records are separate from this in-memory queue |
| Duplicate key | X-Index-Delivery-Id when supplied; otherwise recorded timestamp, client, trigger and transcript |
| State paths | pebble-index.json, pebble-index.secret and pebble-index/inbox adjacent to selected companion config |

Keep constant-time bearer comparison and existing compatibility normalization. Never print token or transcript in general logs.

Retain current response compatibility: a duplicate receipt contains status, id and duplicate; reuse of an explicit delivery ID with changed content is rejected with HTTP 400. A semantic conflict does not imply a new HTTP 409 response during extraction.

Preserve exact existing secret bytes during migration. The Secrets plugin may later import that token under a stable secret reference, with sender compatibility verified before the old source is retired. A disabled Secrets plugin must not prevent existing Pebble installations from starting; retain the legacy provider until migration is deliberate.

## Delivery record and state machine

The accepted record freezes task ID, host ID and transcript. Changing the configured target affects new ingress only. Persist and flush before returning success.

Current main already persists DeliveryUncertain before invoking Desktop send, and retains that state after cancellation or failure. Preserve that conservative attempt boundary. The following proposed versioned extension is justified only when the recovery controls consume its phases. A packaging-only move may retain the current schema; do not add attempt machinery solely to match the diagram:

~~~mermaid
stateDiagram-v2
    [*] --> Received: Durable ingress commit
    Received --> HeldBeforeSend: Target unavailable or no scheduler slot
    Received --> Sending: Durable attempt record
    HeldBeforeSend --> Sending: Explicit local retry
    Sending --> Sent: Desktop confirms
    Sending --> DeliveryUncertain: Failure after dispatch or restart
    DeliveryUncertain --> Sent: Explicit reconciliation evidence
    DeliveryUncertain --> Discarded: User disposition
~~~

Record delivery ID, schema version, destination snapshot, content reference, attempt ID, phase and timestamps. Persist Sending before any Desktop side effect. On startup, interrupted Sending becomes DeliveryUncertain. If the process cannot prove it stayed before dispatch, never relabel it safe to retry.

“Delivered once” means duplicate webhook identities cannot initiate another automatic send and confirmed deliveries remain terminal. There is no end-to-end exactly-once guarantee when Desktop offers no idempotent send token. State that limit in the recovery UI.

Preserve the current policy of no automatic replay of old Received records during extraction. A queue-full request may remain durably held, but its status must be visible. Do not acknowledge it as sent. A later durable scheduler can automatically dispatch only records proven never attempted, after a separate tested behavior change.

When repeated ingress has the same delivery key and matching original ingress content, return its existing receipt/state and preserve its original stored destination. The current HTTP receipt has no target field; extraction does not add one. Never compare a replay against the currently configured target: a settings change cannot turn it into a new send or conflict. The same explicit delivery key with different content must return a conflict; never silently overwrite or treat it as a new message.

Records from the older implementation can contain Received after an attempted submission. Current main writes uncertainty before submission, but a record without version/provenance cannot establish which implementation wrote it. Import ambiguous legacy Received records into manual review unless independent evidence proves no dispatch occurred. Absence of an attempt field is not evidence that they can safely become HeldBeforeSend.

## Implemented baseline to preserve

- DeliveryUncertain is durably stored before Desktop send. A duplicate receipt never starts another send, including after a target change. Concurrent duplicate acceptance publishes one record atomically; conflicting content under the same explicit ID is rejected.
- Active client tasks are tracked and awaited during shutdown before shared semaphores are disposed. Diagnostic callback and recoverable persistence failures do not terminate the delivery loop.
- Authorization is checked before body reads. Multipart boundaries are parsed structurally, and non-text/audio/file parts are rejected. Existing deliberate short private tokens remain supported; invalid non-ASCII tokens are rejected.
- Normal tray startup validates preferences and enablement before creating/loading the bearer token. Concurrent token creation returns the atomically published value. Token bytes and configured target ID remain stable.
- The saved target is resolved directly by ID even when absent from the recent-task catalog. Stored/unreadable inbox status is surfaced. Queue-full records already show a held detail and outstanding count.
- CancellableRuntimeCoordinator prevents stale startup publication and serializes stop/cleanup before restart. Configuration save failures roll back earlier writes and keep the editor open.

## Residual extraction gaps

1. Add recovery actions and the minimum versioned state they require while retaining conservative uncertainty. Queue-full records currently remain Received with a held detail; there is no later scheduling path or Retry/Retarget/Discard UI. Add richer attempt phases only with a concrete recovery use.
2. Make startup ordering structural in the worker factory: validate, acquire listener/data ownership, then create a missing token. The current tray validates first, but the public receiver StartAsync evaluates secret loading before its internal validation and listener bind. Explicit local token-copy actions remain separate intentional operations.
3. Prevent overlapping receivers using a receiver/data-directory lease. Atomic cross-instance ingress acceptance does not serialize updates or delivery by multiple owners.
4. Add live status subscriptions and selective configuration apply. Opening/cancelling Configuration already leaves Pebble running; successful Save currently restarts it even if Pebble settings did not change. The status control starts with a construction-time snapshot.
5. Exercise cancellation, queue saturation, crash boundaries, lost acknowledgements and reload/selective-Apply behavior directly. Existing tests cover useful adjacent outcomes but do not settle every one of these cases.

Keep these fixes separate from parser replacement to make regressions attributable.

## Standard server follow-up

The current receiver includes a bespoke TCP HTTP/multipart parser. Prefer an ASP.NET Core/Kestrel endpoint in the worker after extraction, with explicit loopback binding, request/body/header/concurrency limits and multipart file rejection. Use golden wire fixtures to preserve the Android sender contract.

Disable request-body logging, HTTP redirects, broad host binding, automatic proxy trust and filesystem upload buffering. Authenticate before content handling. Validate malformed length/chunked input and partial requests against the new server's actual behavior.

This is a follow-up implementation choice, not a dependency for the first packaging move.

## UI and lifecycle

Settings provides enablement, port, destination picker and token management. Token reveal/copy/rotation remains an explicit local action outside draft Apply. The destination picker uses an authenticated local gateway capability; HTTP ingress never gets task listing, reading, selection or creation.

The deliveries view lists time, target, state and concise error with transcript visible only when deliberately opened. Provide Retry held, Review uncertain, Retarget held and Discard. Retargeting an uncertain delivery cannot pretend the old destination received nothing; require explicit local disposition.

Stop accepts no new clients, allows already durable records to remain safe, joins active handlers, and drains or records the current send attempt before closing the worker. Changing port/token restarts only this plugin. Desktop gateway loss leaves ingress records available for local recovery.

## Verification

Retain PebbleIndexTests coverage for concurrent duplicate publication, reused-ID payload conflicts, direct saved-target lookup, authentication before body arrival, structural multipart rejection, state-at-send uncertainty, active-client shutdown, unreadable inbox reporting, coordinator cancellation/failure and multi-file save rollback. These tests already exist and should move with their behavior.

Add coverage for the remaining boundaries:

- Duplicate receipt before/after restart and simultaneous duplicate submission.
- Same key/different payload conflict and target changes during queued work.
- Crash before durable ingress, after ingress before acknowledgement, before send, after dispatch and after confirmation.
- Cancellation after dispatch resolves to uncertain; a webhook replay does not send again.
- Queue-full accepted records visibly held; no orphaned acknowledgement.
- Shutdown with partial HTTP bodies and active client handlers.
- Independent startup with Voice off and no controller.
- Settings open/close/Apply on another plugin leaves listener and delivery ownership unchanged.

Use fake Desktop transport for failure injection. A later attended end-to-end check confirms the existing phone request still authenticates and delivers to the exact selected Desktop task after packaging.
