# Plugin engine

Status: proposed. See [delivery order](08-delivery-plan.md) and [source evidence](09-prior-art.md).

## Outcome and scope

Joydex runs independently of its configuration window. A plugin can be disabled, restarted, upgraded, or unavailable without stopping unrelated integrations. Existing profiles keep their meanings and familiar setup flows. A hardware-free installation can run Voice, Discord, Pebble or Secrets.

The first plugin release supports bundled, trusted plugins. It includes an explicit catalog and versioned contracts, with an external worker package shape that can later become a public SDK. It does not need a marketplace, arbitrary downloaded DLL execution, or a general workflow language.

## Current seams

| Current location | Responsibility to retain or extract |
|---|---|
| src/Joydex.Core/Runtime/CompanionEngine.cs | Input detection, binding resolution, task-alert interception, prompt picker and map requests |
| src/Joydex.Windows/Runtime/CompanionWorker.cs | Device polling and execution; separate acquisition from slow action dispatch |
| src/Joydex.RuntimeHost/Production/ProductionRuntimeComposition.cs | Runtime aggregate composition, replacement and lifetime ownership |
| src/Joydex.App/RuntimeTrayApplicationContext.cs | Tray and window projections over the independent runtime |
| src/Joydex.Windows/Actions | Codex catalog, keybinding resolution, foreground guards and injected-key lifecycle |
| src/Joydex.Windows/TaskAlerts | Hook pipe, task correlation, navigation, Guardian and LED coordination |
| src/Joydex.DesktopBridgeHost | Existing experimental singleton Desktop broker; retain its constrained voice surface |
| src/Joydex.Core/Voice | Currently mixes voice contracts with reusable Desktop bridge and Pebble contracts |

Our core already has useful behavior boundaries. Preserve their algorithms and tests while changing who constructs and owns them.

The 2026-09-11 baseline also includes CancellableRuntimeCoordinator for Pebble startup/publication, configuration save rollback, hardened Pebble ingress and managed Codex runtime resolution. These are existing behavior to retain. The [refresh ledger](11-main-refresh.md) separates remaining ownership work from fixes already on main.

## Process and project layout

Proposed project names are illustrative implementation targets; extraction should follow useful commit boundaries.

| Component | Process / purpose |
|---|---|
| Joydex.RuntimeHost | One background process per Windows user/session. Owns runtime state, device resources, shared services and worker supervision. |
| Joydex.App | Existing executable remains the user entry point. Starts or attaches to RuntimeHost, supplies tray, settings and ordinary windows. Closing a window disconnects only that window. |
| Joydex.Contracts | Small versioned DTOs, capabilities, action definitions, config/result types. No WinForms, device, Discord or vault dependencies. |
| Joydex.Core | Domain behavior: binding engine, action routing policy, task alerts and validated configuration. |
| Joydex.Windows | Windows implementations of input injection, foreground checks, process/pipe plumbing and power/session notifications. |
| Joydex.Plugins.* | Bundled plugin implementation and manifest. Runtime and optional trusted UI contribution are separate entry points. |
| Joydex.PluginWorker | Common process bootstrap and protocol plumbing; one plugin instance per worker process. Voice supplies its own STA loop. |
| Joydex.DesktopBridgeHost | Supervised private compatibility worker for the shared Desktop gateway. |
| Joydex.SecretsBroker | Separate secrets process with its own minimal consent STA window; raw values stay out of the ordinary engine and settings UI. |

RuntimeHost needs a Windows message loop and hidden cooperative HWND for existing DirectInput/device/power behavior. Keep those platform services alive without a visible window. Voice has a separate hidden media HWND and STA in its worker. Do not move WebView2 audio into the settings process.

Preserve a single interactive owner and acquire an exclusive data-root lease as well as the session singleton. A second logon session or alternate launcher cannot become another writer for the same settings/state directory.

Use the repository-pinned .NET 8 SDK 8.0.423. The .NET Generic Host supplies DI, lifetime and logging; add a small Joydex supervisor around plugin tasks. An unhandled BackgroundService exception can otherwise stop a host, so optional plugin failures must be observed and translated into plugin health. Do not globally ignore exceptions and leave dead workers marked healthy. [Microsoft hosting documentation](https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host).

## Shared services and ownership

| Engine capability | Owner and consumers |
|---|---|
| Action registry/router | Core owns definitions, argument validation, trust classes, dispatch and outcomes; input plugins emit requests |
| Input observation/capture | Device owner publishes snapshots; settings subscribes and requests a bounded capture lease |
| Task status | One hook ingestion/correlation service publishes snapshots to PAD, LEDs, tray and other readers |
| Desktop tasks/projects | One gateway delegates to the verified Desktop broker; Voice, Pebble and Discord receive different method scopes |
| Configuration | One writer for desired/active revisions and plugin settings; UI edits drafts |
| Plugin lifecycle | Catalog, version negotiation, instance health, resource leases and shutdown |
| Client/project identity | Local registration records and canonical project references; credentials are broker-specific |
| Approval presentation routing | Notification/attention metadata; Secrets owns its decisions and secret values |
| Diagnostics | Structured metadata, counters and bounded health history; plugin-specific content stays in its own archive |

Do not extract a generic audio bus, universal conversation engine, universal webhook server, vault database shared by all plugins, or arbitrary event graph. Voice and chat delivery have materially different lifecycles. Share task references, operation outcomes and transport plumbing where behavior really matches.

Each plugin owns its domain records. The engine owns configuration envelopes and lifecycle records. Ingress/outbox records remain separate from the transient event stream; reconnecting a UI must not replay side effects.

The Desktop gateway additionally owns authoritative Discord creation intents and task bindings, derived only from its own confirmed creation results. The Discord worker owns ingress receipts and reply-mirror state. Worker-supplied records cannot establish creation provenance or authorize an arbitrary task.

## Plugin identity and manifest

Use stable publisher-qualified IDs, independent of labels:

~~~json
{
  "schemaVersion": 1,
  "id": "joydex.pebble-index",
  "version": "1.0.0",
  "hostApi": { "major": 1, "minimumMinor": 0 },
  "execution": "worker",
  "entryPoint": "Joydex.Plugins.PebbleIndex.exe",
  "settingsSchemaVersion": 1,
  "requires": ["desktop.tasks.send.v1"],
  "optional": ["secrets.references.v1"],
  "contributes": {
    "actions": ["joydex.pebble-index.open-deliveries"],
    "settingsPage": "pebble-index"
  }
}
~~~

The manifest declares needs. The installed catalog assigns actual grants; declaring a capability never grants it. Missing optional capabilities disable only the dependent feature. Reject duplicate IDs, path traversal, incompatible major versions, unsupported required capabilities and dependency cycles before launching a worker.

The host exports only negotiated methods to a worker. A Voice connection cannot invoke a Discord creation method merely because both reach the same broker. No root IServiceProvider, arbitrary service lookup, reflected method invocation or general filesystem API crosses the plugin contract.

For bundled local modules, use explicit factories and compile-time references first. AssemblyLoadContext can support later dependency isolation, but it provides no security boundary; Microsoft's plugin guide explicitly warns against treating it as one. [Plugin guide](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support).

Ordinary lifecycle exceptions from an in-process module can be isolated; a fatal native/CLR failure in that module still takes down the host. Use a worker when independent crash survival is a requirement, and keep that distinction visible in plugin diagnostics and release claims.

## Runtime contract

The contract describes behavior rather than a large inheritance hierarchy:

~~~csharp
// Conceptual contract; signatures will be frozen by the first extraction.
public interface IJoydexPlugin
{
    Task<PluginReady> StartAsync(PluginContext context, CancellationToken lifetime);
    Task<ConfigPlan> PrepareConfigAsync(ConfigCandidate candidate, CancellationToken ct);
    Task<ConfigApplyResult> ApplyConfigAsync(PreparedConfig prepared, CancellationToken ct);
    Task StopAsync(StopReason reason, CancellationToken deadline);
}
~~~

Preparation validates and explains the affected resources without mutating active state. Applying consumes the exact prepared revision. Worker adapters expose equivalent RPC methods. Health, contributed actions and UI snapshots are explicit messages.

Lifecycle: Disabled → Starting → Ready; temporary dependency failure → Degraded/Retrying; invalid config or protocol → Blocked; crash → Faulted → bounded restart; deliberate shutdown → Stopping → Stopped. Report last error, active revision and next retry time without sensitive payloads.

Give each instance a lifetime cancellation token, startup deadline, stop deadline and resource lease generation. Use bounded exponential backoff with jitter and a circuit breaker after repeated startup crashes. Reset the breaker on an explicit retry or a relevant configuration change. Late events from an old worker generation are discarded.

Resource leases cover DirectInput instances, direct HID LED ownership, panel endpoints, ingress ports, the Desktop broker singleton and the dedicated Voice owner. Exclusive resources cannot run blue/green simultaneously. A restart drains the prior owner before the replacement acquires the resource.

## Local IPC

Use versioned JSON-RPC over local named pipes, with StreamJsonRpc as the leading library candidate. It supplies request correlation and cancellation; Joydex still owns authorization, message bounds, event recovery and operation semantics. Pin a compatible package after the .NET 8 transport spike. [Library](https://microsoft.github.io/vs-streamjsonrpc/docs/getting-started.html).

Require explicit current-user/session ACLs, local-only pipe access, non-elevated peers and a launch credential for child workers. Confirm peer SID and expected child identity where relevant. Pipe names and caller-provided task IDs are not authentication. Verify ACL behavior on our actual .NET 8 runtime rather than assuming it from later documentation.

Negotiate protocol major/minor, capabilities, session generation and maximum payload. Initial control-message limit: 1 MiB; feature-specific lower limits override it. Bound queued requests and subscriptions. Exclude arbitrary .NET type metadata and deserialization type names.

Use a request ID for correlation and a separate operation ID for side-effect reconciliation. Timeouts mean the result may be unknown; cancellation acknowledgement is explicit. Safe queries can retry. Creation, message delivery and secret release follow their feature-specific uncertainty rules.

Publish monotonically sequenced events with an engine epoch. UI connects by subscribing and obtaining a snapshot with a cursor; events after that cursor follow in order. A gap or new epoch forces resynchronization. State snapshots may coalesce; commands, approvals and durable ingress acknowledgements must never silently drop.

Audio PCM, secret values and bulk transcripts are excluded from the shared control stream. Plugin-specific data paths handle those payloads.

## Actions and existing bindings

Retain all current CodexAction names and persisted representations through an adapter to namespaced action descriptors. Existing files and profiles load without edits. Inventory the actual serializer/catalog mapping before moving enum declarations; source enum ordinals alone are not the compatibility contract. New plugin actions use IDs such as joydex.secrets.approve-once. IDs, labels and default keyboard bindings are separate fields.

Every request carries action ID, validated parameters, origin plugin/device, event sequence, target reference where required, and capability context. Action definitions declare where they can run: local UI, guarded desktop injection, Desktop task operation or sensitive local consent.

Enforce existing foreground, simulator suppression, dry-run, cooldown and injected-key cleanup at the host boundary. Remote Discord or Pebble input cannot invoke secret consent actions. Sensitive approval actions require a current visible request challenge and a trusted physical/UI source.

The input loop publishes events and schedules work; it never waits for a network call. Preserve per-device gesture order and per-target message order. Bound action queues; show rejected/full states rather than silently accumulating stale button presses.

Existing enum/catalog migration must not change Codex command IDs, aliases or precedence. Any later deliberate compatibility change follows AGENTS.md and updates the validated Windows package, app release, Codex build and date.

## Settings and state

Keep existing file locations during extraction. Wrap existing stores; do not combine a plugin extraction with an irreversible data migration. Later namespaced settings may live under LocalAppData/Joydex/plugins/<id>, but import must be one-time, atomic, backed up and reversible. The configured companion JSON path remains supported.

Bring every current writer into this boundary, including PromptPickerEditorForm and the WirelessPanel.Configure tool. Online edits go through the host; an offline tool must acquire the same data lease and leave a revision the next host can validate. File changes from external editors enter as candidate revisions rather than bypassing active-state validation.

Maintain desired revision, active revision and optional pending revision separately. One authoritative envelope/commit marker identifies a complete persisted revision. Multiple JSON files cannot be called atomic merely because each was atomically replaced.

The UI submits a typed patch with its base revision. Validate the whole proposed change, compute affected plugins, prepare them, then commit durable desired state and activate the applicable components. Report Applied, PendingIdle, Conflict, Rejected or FailedRolledBack. On crash, the commit marker and last-known-good generation determine recovery. See the [UI transaction rules](02-live-settings-ui.md).

Logs include plugin ID, operation ID, duration, state and sanitized error category. They exclude prompts, webhook bodies, raw Discord content, secret values and full child environments. Existing voice archives and Pebble delivery records retain their own deliberate content-storage policy.

## Packaging, updates and recovery

Initially ship catalog and plugins in the same Joydex release with exact hashes and notices. New plugins default off. Enabling a previously configured built-in feature preserves its preference. A worker update is staged beside the old version, checked for protocol/settings compatibility, and switched at idle.

Package hashes here apply to Joydex's own release artifacts. The external Codex runtime follows accepted ADR 0005's managed discovery and capability checks; do not reintroduce a Codex hash allowlist. Extend the current scripts/Publish-Joydex.ps1 layout and helper packaging, including DesktopBridgeHost, HookRelay, Guardian, WirelessPanel.Configure and WebView2 native assets, before adding plugin assets.

Host update requests a graceful shutdown and preserves old binaries/config backup for rollback. A worker crash restarts only that worker. UI upgrade/restart leaves RuntimeHost alive. An engine crash terminates stale workers through a Windows Job Object or equivalent verified lifetime control; restart performs existing stale-key cleanup. Immediate key release after hard process death needs a separately implemented and tested mechanism: the existing Guardian restores LEDs/LinkTool only.

Keep Guardian outside the owner's kill-on-close job so it survives owner failure. A replacement hardware owner waits for bounded Guardian recovery and lease release before writing a new baseline. Otherwise old recovery could overwrite the new owner's output.

An external plugin installer is a later slice: it must stage packages, validate manifest/hashes, show publisher and requested permissions, record grants and support uninstall without deleting user data by default. Process workers remain trusted same-user code until an actual Windows sandbox design is adopted.

## Acceptance

- Settings is open, resized or closed while input, task alerts, PAD, Voice and inbound plugins remain active.
- One unavailable plugin leaves other plugins Ready; dependency status is visible.
- Old configs and existing binding behavior are preserved across import and rollback.
- Duplicate registration, incompatible version, stale generation and oversized messages fail predictably.
- No plugin obtains an undeclared gateway method, raw secret event or controller ownership.
- Engine restart, worker restart, UI reconnect and machine sleep each have separate, exercised recovery paths.
