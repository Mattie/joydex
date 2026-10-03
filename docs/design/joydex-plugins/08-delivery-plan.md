# Delivery plan and acceptance

Status: proposed. Implementation units below are intended as independently reviewable changes, not estimates or a single large rewrite.

## Starting point

The refreshed implementation baseline is main at `3d7c673` on 2026-09-11. The original design was saved on wip/joydex-plugin-design at `c4dc635`; only its design documents were restored and refreshed here. The reviewed plan and any subsequent main changes must form one recorded checkpoint before implementation worktrees branch. Preserve uncommitted work and distinguish pre-existing failures from extraction regressions. See the [baseline ledger](11-main-refresh.md).

The initial implementation objective is specific: opening and using Configuration no longer stops active runtime work, and input capture cannot accidentally invoke a live binding. Move ownership before changing the visual framework.

## Ordered work

| Slice | Deliverable | Depends on | Exit evidence |
|---|---|---|---|
| **BASELINE** | Current behavior/paths/catalog recorded; baseline tests and fixtures; installed Desktop capability investigation | None | Existing gaps named; no accidental catalog or preference changes |
| **HOSTSEAM** | Runtime aggregate, explicit lifetime, per-source key ownership, action/input observation and capture lease; UI draft/apply interface and transitional same-process modeless form | BASELINE | Shared-key holds survive another device's capture; ordinary settings use keeps Voice/PAD/controllers active; UI-process survival comes in PROCESSUI |
| **PROCESSUI** | RuntimeHost and host client; independent Voice media STA/worker; settings process attaches | HOSTSEAM | Kill/restart UI during active media and input without restarting them |
| **PLUGINPILOT** | Manifest/registration/lifecycle/config/health contract; PAD wrapper proves a small extraction | PROCESSUI | Missing/disabled/restarted PAD leaves remaining runtime healthy |
| **VOICEPLUGIN** | Voice aggregate and owner supervision registered as plugin; visible Room Voice uses snapshots | PLUGINPILOT | Existing media/ownership tests and attended continuity scenario pass |
| **PEBBLEPLUGIN** | Independent worker retaining hardened ingress/recovery and tracked shutdown; add explicit held/attempt state only where still needed; exact token/path compatibility | VOICEPLUGIN, shared Desktop gateway | Existing dedupe/auth/startup/save regressions and new crash fixtures pass; phone sender remains compatible |
| **DEVICEPLUGINS** | DirectInput source and VIRPIL aggregate, per-source key ownership and Guardian alignment | HOSTSEAM, PLUGINPILOT | Existing profiles plus multi-device hold/crash/backend canaries pass |
| **SECRETSV1** | Named-client enrollment, native popup, four choices, exact remembered rules, .env provider, exec helper | PROCESSUI, action registry | Identity/consent/failure tests, latency measurement and attended binding test |
| **AGENTSKILL** | Helper/MCP integration and staged skill | SECRETSV1 | Authorized skill scan/install process and one-use/cached/denied agent scenarios |
| **DESKTOPGATEWAY** | Integrator or one xhigh delegate adds gateway-owned creation intents, provenance, durable bindings and ambiguous-result reconciliation | Desktop gate, PLUGINPILOT | Creation/binding crash fixtures and installed-version canary; existing Voice/Pebble method scopes remain constrained |
| **DISCORDV1** | Discord.Net worker, allowed projects, fresh creation, per-binding delivery and recovery | DESKTOPGATEWAY | End-to-end new Desktop task and reconnect test in restricted test channel |
| **RELEASEPARITY** | Packaging, update/rollback, docs and compatibility release | All selected release features | Full behavior matrix, rollback and independent process recovery |

BASELINE's Desktop research can run alongside HOSTSEAM. SECRETSV1 and DISCORDV1 do not need to wait for every hardware extraction once their capabilities are stable. Voice is completed before the Pebble migration in the default sequence; the small PAD pilot keeps the initial plugin contract grounded.

PROCESSUI may use provisional worker composition before public plugin contracts are frozen. It must include media STA ownership; a runtime process split that still creates Voice WebView2 on the ordinary UI thread does not pass.

Use the [agent implementation plan](10-agent-orchestration.md) for lane ownership and handoffs. R1 ends with PROCESSUI/PLUGINPILOT and working settings continuity; R2 completes existing-feature extraction; R3 adds Secrets and gated Discord. R1 may be reviewed and shipped independently when packaging/rollback and its acceptance scenarios pass. A blocked new integration must not delay the first useful runtime/UI outcome.

## Dependency map

~~~mermaid
flowchart LR
    A["Baseline"] --> B["Host + capture + draft settings"]
    B --> C["Separate UI / independent Voice media"]
    C --> D["Plugin pilot"]
    D --> R1["R1 package + rollback gate"]
    D --> V["Voice plugin"]
    V --> P["Pebble plugin"]
    D --> H["Controller / VIRPIL plugins"]
    C --> S["Secrets broker"]
    S --> K["Agent helper + skill"]
    A --> G["Desktop capability gate"]
    G --> DG["Durable Desktop gateway"]
    D --> DG
    DG --> R["Discord"]
    P --> R2["R2 package + rollback gate"]
    H --> R2
    K --> R3["Selected R3 package + rollback gate"]
    R --> R3
~~~

RELEASEPARITY applies separately to each selected train. The R3 diagram shows both planned features; an unavailable optional feature is excluded from that package's gate and remains explicitly deferred.

## Desktop gate details

The read-only inspection already found an installed packaged adapter with dynamic tool inventory and executor-context requirements. It did not prove external creation.

The implementation canary must verify a legitimate enrollment/bootstrap, allowed project enumeration, new task creation, worktree setup-handle resolution, first-turn recovery, exact-task observation, continuation while Desktop is open, local approval visibility and ambiguous-dispatch recovery.

Use a disposable project and dedicated test conversation when live capability work is authorized as part of implementation. Do not claim a mock, a synthetic task UUID, the current assistant's own tools, or a second App Server demonstrates the external Desktop contract.

If the gate fails, complete engine/UI/migration/secrets work and leave Discord creation disabled with its unresolved capability clearly recorded. Do not add another agent backend to meet a different requirement silently.

## Behavior acceptance matrix

| Scenario | Required observable result | Test level |
|---|---|---|
| Open/cancel settings during a call | Same Voice session/owner and Sendspin stream; no lifecycle command caused by opening | Host integration + attended media |
| UI process terminates | Input/media/inbound workers continue; new UI obtains fresh state | Process integration |
| Capture held switch or release | No synthetic press, normal action or accidental approval; unrelated inputs continue | Deterministic domain + device integration |
| Apply a binding during hold | Keys released/reconciled by owner; held switch must release before new action | Domain + Windows injection |
| Voice setting edited mid-call | Desired revision saved; active revision unchanged until safe close | Host integration |
| Enter dry-run during owned Voice | Active session closes/drains and writer is released before Voice reports dry-run | Host/media integration |
| Two UIs or external edit | Stale base rejected; dirty draft preserved | Store/client integration |
| External Pebble edit, then open/cancel settings | Active receiver retains its Desktop dependency; file candidate does not replace active ownership | Host integration |
| Optional worker crashes | Only its leases/health change; bounded restart, no stale events | Process integration |
| PAD reconnect / reassignment | Full current snapshot and latest task target used | Adapter/transport integration |
| VIRPIL crash/backend swap | Guardian restores correct baseline; no competing writer | Windows helper + attended device |
| Pebble replay after lost ACK | Existing receipt, no duplicate send | Persistent-store + transport integration |
| Pebble cancellation after dispatch | DeliveryUncertain survives restart | Fault injection |
| Legacy Pebble Received record | Manual review unless independent evidence proves no prior dispatch | Migration integration |
| Concurrent Discord /new | One gateway-owned creation intent for one Discord thread | Durable store concurrency |
| Discord reply before binding ready | Initial reply recovered once from creation/task identity | Gateway integration |
| Desktop create/send ACK lost | Unconfirmed state; no blind retry/new writer | Fault injection + canary |
| Discord permission removed | Queued inbound/outbound work held or rejected | Policy + transport integration |
| Caller invents task/project label | Does not gain verified identity or another grant | Broker boundary tests |
| YES redeemed twice concurrently | At most one launch/delivery | Broker/store concurrency |
| YES_ALWAYS exact repeat | No popup, measured fast decision | Broker performance integration |
| Extra secret/project/recipient | Cache miss and fresh consent | Deterministic policy |
| Stale popup/held controller | Rejected; next request remains pending | Domain + attended UI/input |
| Revoke/lock between allow and use | No new secret delivery | Broker concurrency |
| Child launch ACK lost | Unconfirmed launch, no automatic relaunch | Process fault injection |
| Crash after committed launch intent | Durable uncertainty survives loss of ephemeral reservation | Store/process integration |
| Broker logs/errors/events | Canary secret values absent | Integration content assertions |
| Upgrade then rollback | Old binary/config/state still usable; no duplicate owners or deliveries | Packaged release integration |

## Test strategy

Keep existing tests next to their behavior through the moves. Add tests for actual risk boundaries rather than tests that merely assert a new interface calls a mocked method.

Use deterministic unit tests for policy matching, capture/hold transitions, input routing and configuration impact classification. Use real temporary stores for persistence/concurrency/crash-state behavior. Use fake transports that can fail before dispatch, after dispatch and after acknowledgement to test delivery outcomes.

Named-pipe ACLs, process identity, Windows message pumps, input injection, DPAPI, WebView2 and hardware behavior are integration tests. They cannot be proven with mocked object construction. A green mocked project/create test does not prove installed Codex Desktop compatibility.

Run hardware and media canaries only for the changed boundary. Preserve source/binary/firmware hashes and baseline counters. Do not change firmware as part of plugin extraction.

All .NET restore/build/test/run/publish commands use SDK 8.0.423 from global.json. A matching installed SDK or the optional repository-local .tools/dotnet/dotnet.exe is valid; verify the version from each worktree. A local ignored SDK directory is not automatically copied into worktrees, so use its verified absolute executable path when needed. Retain global.json. Runtime upgrades and visual-framework upgrades are separate work if later justified.

Use scripts/Publish-Joydex.ps1 for packaged acceptance and preserve its complete helper/native-asset layout. Hardware, Voice media and installed Desktop canaries run serially from one designated integration checkout. Synthetic tests use separate temporary config/data roots so worker lanes cannot touch the same profile, token, port or pipe. If firmware compilation is separately required, follow AGENTS.md's patch.exe/PATH prerequisite.

## Configuration and data rollout

1. Save a baseline of exact active configuration, plugin state versions and installed binaries without exposing secret values.
2. Keep old paths through the first extraction. Import only when a namespaced storage change becomes necessary.
3. Each importer writes a new version beside the old, validates it, then commits a single version marker. Failed import leaves old data authoritative.
4. Record schema compatibility in plugin catalog and deny downgrade when new state cannot be read safely; use an explicit compatible backup for rollback.
5. Preserve existing tokens and endpoint addresses. Never rotate them incidentally during enable/start.
6. Keep uncertain-send/creation records across upgrade and rollback. Old binaries must not treat newer uncertain states as unsent.

Initially ship the same Joydex entry point and startup registration. A second launch attaches to RuntimeHost. Plugin enable/disable is independent from the window lifecycle. New plugins remain disabled until configured; pre-existing configured integrations preserve their enablement.

No third-party package, firmware, or skill is installed by this design turn. Candidate license evidence supports planning; implementation pins distributable artifacts and checks transitive notices before adoption.

## Open questions with concrete resolution

| Question | Current design default | Smallest check that settles it |
|---|---|---|
| Can our external bridge create Desktop-owned tasks? | Conditional private adapter, no fallback owner | Installed Desktop gate above |
| Can a tool prove originating Codex task? | NAMEDCLIENT first | Verified host metadata / per-task launch-capability prototype |
| How are controller keys released after hard process death? | Preserve existing orderly cleanup; explicitly implement any stronger crash guarantee | Inspect injected-key mechanisms, then kill-owner integration test |
| Does separate media STA preserve Voice timing? | Voice worker retains all media traffic | Existing host canary + attended continuity |
| Which IPC/package versions work on pinned .NET 8? | StreamJsonRpc and Discord.Net candidates | Minimal transport/build spike with exact artifacts |
| Is the existing .env syntax supported safely? | Exact path, no external interpolation, duplicates rejected | Provider fixtures using synthetic values |
| Do we need a new UI framework? | KEEPFORMS | Reconsider only after independent lifetime and parity land |
| Should every worktree share remembered grants? | Exact worktree by default | Local policy choice when there is a demonstrated workflow need |

These questions do not prevent completing the plan. The first two remain capability dependencies rather than assumptions hidden in implementation estimates.

## Planning review

The review checked the recommendations against the working implementation and primary protocol documentation. It concentrated on media thread ownership, capture/execute collisions, durable dispatch boundaries, Desktop writer ownership and truthful secret attribution.

Maintain the distinction between observed behavior, proposed changes and unverified capabilities as implementation progresses. Do not close existing Voice physical acceptance gaps merely because the plugin tests pass.
