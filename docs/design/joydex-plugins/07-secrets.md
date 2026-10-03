# Secrets approval plugin

Status: proposed. Plugin ID: joydex.secrets. Recommended implementation: **NATIVEBROKER**, initially **NAMEDCLIENT + ENVFIRST + EXECINJECT**.

## Outcome

An agent or registered client asks to use specific secrets for a project. Joydex immediately reuses a matching remembered decision, or shows a small native popup with **YES_ALWAYS / YES / NO / NEVER**. The popup supports mouse, keyboard and explicit Joydex bindings. Waiting for a decision leaves every unrelated plugin running.

The first version is a local consent and audit tool for cooperative clients. Our user-selected plaintext .env and same-user DPAPI storage cannot contain an adversarial process with unrestricted access to the same Windows account. A stricter vault/OS isolation mode is a separate design and must not be implied by a friendly approval UI.

## Prior art and starting point

Use native .NET/Windows IPC/UI and our existing DPAPI/atomic-save patterns. Agent Secret is the closest permissive behavioral reference for approved child-process injection and reusable request scopes, but its native implementation targets macOS. DotNetEnv can provide exact-path parsing without polluting the process environment. [Full reuse shortlist and licenses](09-prior-art.md).

1Password and KeePassXC provide useful consent UX references; they do not supply our complete project/task/four-choice contract. Optional vault adapters can follow a concrete request to use one.

## Identity: what the popup can truthfully say

| Mode | Authentication and displayed attribution |
|---|---|
| **NAMEDCLIENT** | A locally enrolled client credential maps to a stable client ID, trusted display label and allowed canonical projects. Suitable for a regular Codex task using the helper. |
| **CODEXTASK** | A verified host request context establishes the originating Codex host/task. The broker resolves its project/root/title through the trusted catalog and binds an issued capability to that context. |

A client ID string, --thread flag, environment variable, real task UUID, task title, cwd or process ancestry alone is insufficient to prove a particular task made a request. Our current Desktop bridge's SourceThreadId is caller-supplied routing context. Looking up that task proves existence, not origin.

CODEXTASK is gated on an authenticated context mechanism outside model-authored arguments. Candidates are a verified Desktop tool context or a per-task helper capability delivered by a trusted launcher. A shared App Server process cannot identify its individual tasks by PID alone.

Until that works, a request is labeled “Client: release-helper” with its enrolled project. A claimed task title may appear only as explicitly unverified secondary text; it never enters the grant key.

## Registration and project identity

The local Secrets page creates a registration with client ID, label, allowed projects, allowed delivery modes and credential generation. IDs may be memorable; credentials must be random, revocable and kept outside repository config, command lines, logs and model text.

The trusted helper retrieves its credential through a local protected registration channel or inherited launch handle. The task may know the public client ID without receiving credential bytes in its context. In NAMEDCLIENT mode, other processes able to use the same registration act as that client; do not describe this as task isolation.

Use a canonical project record: enrolled host/project ID where available, resolved root, worktree ID/root and policy revision. Exact worktree scope is the default. Trusting all worktrees of a project is a separately displayed local policy choice; a sibling checkout does not inherit permission by name.

Resolve path case, junction/reparse targets and root identity at registration and launch. Revalidate at use so replacing a path with a different target cannot reuse an old grant. Project rename does not invalidate a grant; root/identity replacement does.

## Immutable request

An authorization request contains:

~~~json
{
  "requestId": "caller-generated-unique-id",
  "projectRef": "enrolled-project-reference",
  "secrets": ["deploy-token"],
  "operation": {
    "mode": "exec-inject",
    "recipeId": "deploy-preview",
    "parameters": { "target": "preview" }
  },
  "reason": "Publish the requested preview build"
}
~~~

The broker adds authenticated principal, resolved project/worktree, provider and stable secret IDs, alias mapping revision, effective recipient/recipe scope, creation/expiry, request revision and digest. The caller cannot override these trusted fields.

Grant matching includes every authorization-relevant field. The reason is displayed as caller text and excluded from the permission scope; changing prose cannot broaden access. Identical request IDs with changed authoritative content return Conflict.

For execution, the effective scope explicitly includes the resolved argument vector and parameter values, effective cwd, executable identity, recipe generation, script/config fingerprint policy, secret-to-environment mappings and output-disclosure mode. target=preview and target=production are different scopes even when the recipe ID is identical.

## The four decisions

| Choice | Exact effect |
|---|---|
| **YES** | Approve this immutable request for one atomic redemption. No remembered allow is created. |
| **YES_ALWAYS** | Persist an allow for this principal + canonical project/worktree + exact secret set + delivery mode + recipient/recipe scope, until locally revoked. |
| **NO** | Deny this request. A deliberate future request may ask again; the client must not loop. |
| **NEVER** | Persist a deny for that same displayed scope until the user changes it in Joydex. |

No time-based expiry is imposed on ALWAYS/NEVER in v1; the UI shows persistence and provides revocation. Client revocation, project identity change, recipe scope change or secret alias remapping invalidates applicable access.

Default matching is exact, including the sorted set of secret IDs. More secrets, a different set, another project, a broader command or exec-inject → raw-read requires a new decision. Do not add wildcard inheritance initially. A matching deny takes precedence over allow; replacing a saved decision is a deliberate local edit.

Normal rotation of a value under the same stable provider secret ID preserves permission. Changing an alias to refer to a different secret invalidates it. Deleting/recreating a client or secret gives a new internal generation even when its visible name is reused.

## Policy evaluation and fast path

~~~mermaid
flowchart LR
    R["Authenticated request"] --> V["Resolve identity and exact scope"]
    V --> P{"Matching policy?"}
    P -->|"Deny"| N["Return denied"]
    P -->|"Allow"| A["Reserve redemption"]
    P -->|"No match"| Q["Queue native popup"]
    Q --> D{"User choice"}
    D -->|"YES / YES_ALWAYS"| A
    D -->|"NO / NEVER"| N
    A --> C["Recheck policy + provider state"]
    C --> X["Fetch selected values and execute"]
~~~

Persist rules atomically before reporting a persistent choice as saved. Keep a memory index of decision metadata and policy epoch, not a global plaintext-secret cache. The warm path authenticates, resolves exact scope, checks deny/allow and reserves a redemption without creating a window.

Initial performance targets, to measure rather than assume: p95 below 25 ms for a cached decision over an established local pipe; p95 below 150 ms for broker-side .env resolution and authorization excluding child startup. Report cold start, provider unlock, process startup and human wait separately.

Recheck client/project/alias/recipe generations and policy epoch immediately before delivery. Revocation invalidates unredeemed reservations. It cannot recall bytes already delivered to a child.

Use a single-use reservation with atomic consume; two concurrent retries cannot redeem YES twice. Deduplicate principal + request ID. Pending duplicates join the same request; distinct requests remain distinct. A client retry after an uncertain child launch receives LaunchUnconfirmed, never an automatic second launch.

Maintain a durable operation journal independent of ephemeral reservations. Flush LaunchCommitted, meaning an irrevocable intent to attempt this launch, before creating a child. Record Started with PID and process creation time immediately afterward. On broker restart, a committed launch without conclusive outcome is LaunchUnconfirmed even if no PID was recorded; the journal survives reservation cleanup.

## Popup

Use a broker-owned native modeless window on an independent STA. It can be shown while main settings is closed, blocked or restarting. It never runs on the Voice media thread.

Wireframe:

~~~text
Use secrets?

Client: release-helper          Project: Joydex / preview worktree
Secrets: deploy-token
Use: Deploy preview → approved preview destination
Reason: Publish the requested preview build

Remembered choices apply to this client, worktree and operation.

[ YES_ALWAYS ]   [ YES ]   [ NO ]   [ NEVER ]
~~~

For CODEXTASK, show verified task title and project, with host/short ID in details. Titles/reasons are escaped, length-limited text; never treat them as markup or window commands. Secret values never appear.

Use Segoe UI, readable contrast, accessible labels, keyboard navigation and saved placement. Do not select a positive choice by default. Escape/close means cancel this request, with no persistent deny. Focus failure leaves the request pending and signals via tray/attention; do not inject Enter into another app.

One request owns the visible approval slot. Show a small pending count and allow review/next without granting. Initially cap at 64 pending requests globally and eight per client, with a 120-second request expiry. Return queue-full/expired results promptly. These defaults are configurable locally and bounded by host limits.

When the Windows session locks, suspend presentation and all new redemptions, including cached approvals. On unlock require provider readiness; expired requests stay expired. Broker exit cancels pending requests. Persistent decisions survive; one-use reservations do not.

## Bindable actions

Use the common registry:

| Action ID | Meaning |
|---|---|
| joydex.secrets.approve-always | YES_ALWAYS for the shown immutable request |
| joydex.secrets.approve-once | YES for the shown request |
| joydex.secrets.deny-once | NO for the shown request |
| joydex.secrets.deny-always | NEVER for the shown request |
| joydex.secrets.show-request | Show pending approval |
| joydex.secrets.next-request | Move review to the next pending request |
| joydex.secrets.lock | Lock broker/provider access |
| joydex.secrets.open-grants | Open local grant management |

A decision carries the active request ID, revision and display challenge. The broker checks the challenge and the registered input/UI role. Buttons held before display, repeated key events, stale releases and queued events cannot approve this or the following popup.

Require a fresh physical press after presentation, consume it through release and reject it if capture is active for that device. A single press never both approves and triggers a normal Codex binding. Persistent choices must be explicitly bound; do not add default YES_ALWAYS/NEVER mappings to existing controllers.

Sensitive approval actions have a local-consent policy independent of Codex foreground requirements. The ordinary action executor's foreground/simulator guards remain intact for ordinary actions.

Discord, Pebble, the agent helper and arbitrary plugin messages have no approval-response capability. A later PAD approval page requires a paired trusted source and request challenge on its wire protocol; current fixed PAD firmware does not establish that capability.

## Transport and trust roles

Use local named pipes initially; no browser or LAN secret endpoint is required. Separate request, local administration and consent-response roles. A requester can submit/cancel/poll its own requests and redeem its own reservations; it cannot approve or edit grants.

The broker and trusted UI/input channel authenticate through a private launch capability plus verified process/connection identity. Apply explicit pipe DACLs and local-only access; verify SID/session, peer process creation time and expected binary where useful. CurrentUserOnly and PID checks help establish a process/account boundary, while the registered credential establishes the client.

The Windows APIs expose connected pipe client PID and explicit pipe access control. They do not establish Codex task identity. [Pipe client identity](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid), [pipe security](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights).

Engine subscriptions contain request ID, principal/project labels, aliases, state and expiry only. Raw values bypass engine logs/events/settings. Use separate provider and process-launch interfaces so a provider cannot execute arbitrary client commands.

## Secret providers

Start with the smallest provider contract: resolve an enrolled reference, report stable ID/mapping generation, fetch value at redemption, lock/unlock where supported, and report availability. Metadata listing exposes only aliases authorized for that registration.

| Provider choice | Behavior |
|---|---|
| **ENVFIRST** | User selects an exact .env path in local UI. Broker alone parses that file into a private dictionary; no parent-directory search or shell sourcing. |
| **DPAPIIMPORT** | Optional next step: import selected keys into a CurrentUser-protected local store and verify user-only ACLs. Preserve stable IDs during value rotation. |
| **VAULTADAPTERS** | Later: an explicitly chosen existing vault provides values; Joydex retains its own scoped consent policy. |

For ENVFIRST use DotNetEnv's non-environment-mutating path, with a pinned package and tests for quoting, multiline data, duplicates and interpolation. Reject duplicate keys and undefined/external environment expansion; never load values into the entire engine/broker environment.

Parse/configure files inside the management tool. Agents never open .env or receive its contents. Store the source path and alias mapping locally outside projects; show only secret names/availability. Handle file changes as provider generations, and do not use partially written files.

The current WirelessPanelConfigurationStore has useful DPAPI, byte clearing and atomic replacement patterns. Its ACL changes are best effort; a new store claiming verified ACL protection must fail provisioning if those ACLs cannot be enforced. Same-user DPAPI still does not separate tasks under the same user. [DPAPI scope](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope?view=net-8.0).

Keep token migration explicit for Pebble/PAD/Discord. Preserve existing values and test the consumer before changing its reference. A plugin's bootstrap credential should not require a recursive approval request from that same plugin to bring the broker online.

## Deliver secrets through an operation

Recommended first mode **EXECINJECT** starts an approved child process with only the selected secrets added to a minimal environment. Use ProcessStartInfo.ArgumentList, a validated executable and working directory, and no implicit shell. Values never enter argv, parent environment, normal tool JSON or the clipboard.

An approved recipe defines executable identity, allowed parameter schema, target cwd, secret-to-environment mapping and effective operation scope. Scripts and imported config are mutable: either fingerprint their relevant inputs or display and approve a broader named recipe honestly. An executable/argv match alone does not prove unchanged script behavior.

Every v1 execution request resolves to a recipe-shaped operation record. For a first experiment, the task can submit a complete operation proposal and receive the normal four-choice popup directly; a separate recipe-editor visit is unnecessary. The broker validates and freezes that proposal, and YES/YES_ALWAYS authorizes its displayed scope. A proposal cannot execute before consent or widen an existing remembered scope.

For an inline proposal, derive its operation identity from a broker-computed canonical digest of the resolved authorization scope. Ignore caller-supplied recipe IDs/generations. Equivalent proposals reuse that identity across request IDs and reason text, so YES_ALWAYS remains fast; a changed parameter, fingerprint or output mode produces a different scope.

For repeated named workflows, the Secrets page also has Add recipe/Edit recipe: choose an executable, argument template, parameter schema, enrolled cwd, secret mappings, fingerprint inputs and output mode. A task can reference that locally saved recipe. The UI validates the fully resolved operation before saving a new generation. Saved recipes are a convenience rather than a prerequisite to asking for a secret.

~~~json
{
  "id": "deploy-preview",
  "generation": 1,
  "executable": "<locally selected absolute executable>",
  "arguments": ["deploy", "--target", "{target}"],
  "parameters": { "target": { "enum": ["preview"] } },
  "cwd": "<enrolled worktree root>",
  "secretEnvironment": { "DEPLOY_TOKEN": "deploy-token" },
  "scopeMode": "exact-resolved-operation",
  "fingerprintInputs": ["<selected script/config inputs>"],
  "outputDisclosure": "summary"
}
~~~

The default scope mode includes resolved parameters and selected input fingerprints. A deliberately broader mutable-recipe mode requires separate local enablement and popup wording; it cannot inherit existing exact-operation grants. Recipe edits increment generation and invalidate old grants. Missing executables, unresolved templates or unverified path/fingerprint inputs fail before requesting consent.

The broker-controlled launch helper validates the recipe and consent, flushes the launch intent, fetches values at the last moment, rechecks policy, starts the child, records its identity, and clears temporary buffers where possible. A provider failure before launch can be marked FailedBeforeLaunch conclusively; a crash in the creation window stays uncertain. Immutable managed strings and child environments limit reliable zeroing; do not promise complete memory erasure.

An arbitrary approved child can print or transmit its secrets. Output filtering is a diagnostic safeguard, not containment. Return bounded stdout/stderr, redact exact known values where practical, and make raw child output opt-in for secret-bearing operations. A raw output opt-in is a separate local policy choice.

Modes to add only when required:

- **OPERATIONPROXY**: a bounded named API operation inside the broker with destination/method controls; stronger credential non-disclosure, additional per-service implementation.
- **RAWREAD**: an explicitly enabled value-release capability with its own popup wording and grants; it may place values in agent/tool context and is off by default.

EXECINJECT should satisfy routine CLI-based tasks; it does not pretend to populate the environment of a process that has already started.

## Agent helper and future skill design

Ship a small authenticated local helper plus optional MCP wrapper. Conceptual operations:

~~~text
joydex-secrets aliases --client release-helper --project <enrolled-ref>
joydex-secrets request --client release-helper --project <enrolled-ref> --recipe deploy-preview --parameter target=preview --reason "Publish the requested preview build"
joydex-secrets request --client release-helper --project <enrolled-ref> --operation-file <metadata-only-json> --reason "Run the requested tool"
joydex-secrets wait --client release-helper --request <id>
joydex-secrets cancel --client release-helper --request <id>
joydex-secrets run --client release-helper --request <id> --reservation <opaque-handle>
~~~

The public client ID chooses an enrolled registration; the helper obtains its credential outside flags and model text. Request parameters never claim verified identity.

Every invocation, including wait/cancel/run, authenticates the same registration. Request IDs are lookup keys with no authority. A reservation is an opaque, short-lived handle bound to that authenticated principal and request; possession alone is insufficient. The request command derives the exact secret set from the selected recipe or complete operation proposal and supplies explicit parameters/reason. Operation files contain aliases, executable/arguments and other metadata only, never secret values or client credentials.

A request returns allowed, pending, denied, expired, revoked, identity_unverified, provider_unavailable or queue_full, with an opaque request/reservation handle. Permission-check queries expose only this client's own scope and never consume YES or reveal values.

The future skill tells the agent to:

1. Discover permitted aliases/recipes without fetching values; use a complete operation proposal when no saved recipe fits.
2. State the exact project, required operation and short reason.
3. Request once; use an already allowed decision immediately.
4. Await pending approval through the helper without blocking unrelated work.
5. Stop on denial/expiry; never retry in a loop, switch identities, broaden scope or edit rules.
6. Run the approved operation and report sanitized outcome.
7. Never read .env, echo resolved values, place them in messages, or implement an approval bypass.

Create that skill only during the implementation slice. Stage it outside active skill directories, run the required SkillSpector static gate, resolve any findings according to local policy, then install and rescan the exact result. This design creates no active SKILL.md.

## Audit, recovery and tests

Audit request ID, verified principal, project reference, aliases, operation digest, policy decision, source of consent, timestamps and delivery outcome. Keep reasons, commands and paths minimized; never store values, full environments or raw protocol payloads. Start with 30-day metadata retention, user-controlled cleanup and explicit separation from persistent grant rules.

Grant storage can begin as one atomic versioned file with a broker-owned writer and in-memory index; high throughput does not require a new database service. Serialize rule updates and reservation consumption. Corrupt policy data blocks grants and asks for local recovery rather than silently accepting defaults.

Test spoofed labels, invalid registration, wrong pipe roles, PID reuse, project/path replacement, client recreation, alias remapping, delivery-mode widening, additional secrets, parameter/output-mode changes, script changes, equivalent inline-proposal cache reuse, concurrent one-use redemption and revocation immediately before delivery.

Exercise stale popup challenges, held controller input, capture collision, no focus, session lock, UI crash, broker restart, provider unavailable, expiry and queue exhaustion. Include canary values that must never appear in logs/errors/events.

Crash the broker after LaunchCommitted but before process creation, after creation but before Started is persisted, and after Started but before the reply. Every inconclusive case retains a durable launch record and refuses automatic relaunch.

Measure warm/cold latency separately. Run an attended scenario with active Voice, controller/PAD activity and settings open while a request waits, is denied, is allowed once and is allowed permanently. Verify the next exact request avoids the popup and a changed scope prompts again.
