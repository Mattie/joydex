# Secrets approval plugin

Status: initial implementation complete for **NATIVEBROKER + LOCALREQUESTER + ENVFIRST + EXECINJECT**. Plugin ID: joydex.secrets. Bindable controller approval actions, verified per-Codex-task attribution, and consent-time routing across multiple named secret sources remain follow-up work.

## Outcome

An agent asks to use specific secrets for a project. The helper establishes its protected local identity on first use, then Joydex immediately reuses a matching remembered decision or shows a small native toast with **Allow once / Allow for 24 hours / Allow always / Deny this time / Deny forever**. Waiting for a decision leaves every unrelated plugin running.

The first version is a local consent and audit tool for cooperative clients. Our user-selected plaintext .env and same-user DPAPI storage cannot contain an adversarial process with unrestricted access to the same Windows account. A stricter vault/OS isolation mode is a separate design and must not be implied by a friendly approval UI.

## Prior art and starting point

Use native .NET/Windows IPC/UI and our existing DPAPI/atomic-save patterns. Agent Secret is the closest permissive behavioral reference for approved child-process injection and reusable request scopes, but its native implementation targets macOS. DotNetEnv informed the accepted syntax, but its 3.2.0 package still resolves legacy configuration dependencies with high-severity NuGet advisories under this repository's warnings-as-errors policy. Secrets V1 therefore uses a small broker-owned parser rather than suppressing dependency auditing. [Full reuse shortlist and licenses](09-prior-art.md).

1Password and KeePassXC provide useful consent UX references; they do not supply our complete project/task/five-choice contract. Optional vault adapters can follow a concrete request to use one.

## Identity: what the popup can truthfully say

| Mode | Authentication and displayed attribution |
|---|---|
| **LOCALREQUESTER** | The helper automatically creates a same-user protected credential and binds a stable requester label to the current canonical project on first use. Suitable for cooperative consent and remembered decisions; the label is not proof of a particular task. |
| **CODEXTASK** | A verified host request context establishes the originating Codex host/task. The broker resolves its project/root/title through the trusted catalog and binds an issued capability to that context. |

A client ID string, --thread flag, environment variable, real task UUID, task title, cwd or process ancestry alone is insufficient to prove a particular task made a request. Our current Desktop bridge's SourceThreadId is caller-supplied routing context. Looking up that task proves existence, not origin.

CODEXTASK is gated on an authenticated context mechanism outside model-authored arguments. Candidates are a verified Desktop tool context or a per-task helper capability delivered by a trusted launcher. A shared App Server process cannot identify its individual tasks by PID alone.

Until that works, a request is labeled “Client: release-helper” with its canonical project. A claimed task title may appear only as explicitly unverified secondary text; it never enters the grant key.

## Local requester and project identity

The common `joydex-secrets exec` path automatically creates or reuses a record with client ID, display label, canonical project, allowed delivery mode and credential generation. The settings page does not ask the user to register agents. IDs may be memorable; credentials must be random, revocable internally, and kept outside repository config, command lines, logs and model text.

The helper creates or retrieves its credential through a CurrentUser-DPAPI-protected local store. The task may know the public client ID without receiving credential bytes in its context. In LOCALREQUESTER mode, other processes running as the same Windows user may be able to act as that requester; do not describe this as task isolation.

Use a canonical project record: supplied project reference, resolved root, worktree ID/root and policy revision. The helper captures it from the current working directory on first use and rechecks it for later requests. Exact worktree scope is the default. Trusting all worktrees of a project is a separately displayed local policy choice; a sibling checkout does not inherit permission by name.

Resolve path case, junction/reparse targets and root identity at registration and launch. Revalidate at use so replacing a path with a different target cannot reuse an old grant. Project rename does not invalidate a grant; root/identity replacement does.

## Immutable request

An authorization request contains:

~~~json
{
  "requestId": "caller-generated-unique-id",
  "projectRef": "project-reference",
  "secrets": ["GITHUB_TOKEN"],
  "operation": {
    "mode": "exec-inject",
    "executable": "publish.exe",
    "arguments": ["--target", "preview"]
  },
  "reason": "Publish the requested preview build"
}
~~~

The broker adds authenticated principal, resolved project/worktree, provider and stable secret IDs, alias mapping revision, effective operation scope, creation/expiry, request revision and digest. The caller cannot override these trusted fields.

Grant matching includes every authorization-relevant field unless a remembered allow is explicitly client-scoped. A client-scoped allow omits only operation matching. The reason is displayed as caller text and excluded from the permission scope; changing prose cannot broaden access. Identical request IDs with changed authoritative content return Conflict.

For execution, the effective scope explicitly includes the resolved argument vector, effective cwd, executable identity, script/config fingerprint policy, secret-to-environment mappings and output-disclosure mode. `--target preview` and `--target production` are different scopes. Advanced saved operations also include their generation and resolved parameters.

## The five decisions

| Choice | Exact effect |
|---|---|
| **YES** | Approve this immutable request for one atomic redemption. No remembered allow is created. |
| **YES_24H** | Persist an allow that expires exactly 24 hours after approval. It stays operation-bound unless **Apply my decision to all future commands from this agent** is checked. The broader scope still requires the same authenticated client, canonical project/worktree, exact secret set and delivery mode. |
| **YES_ALWAYS** | Persist an allow until locally revoked. It follows the same operation-bound or client-scoped choice and bounds as YES_24H. |
| **NO** | Deny this request. A deliberate future request may ask again; the client must not loop. |
| **NEVER** | Persist an operation-bound deny until revoked, or a client-scoped deny within the displayed bounds when the broader checkbox is checked. |

YES_24H has a fixed wall-clock expiry and never extends on use. No time-based expiry is imposed on ALWAYS/NEVER in v1; the UI shows persistence or remaining time and provides revocation. Client revocation, project identity change or secret alias remapping invalidates every stored decision. A recipe scope change invalidates an operation-bound decision. A matching NEVER deny is evaluated before every stored allow.

A separate global operating mode supports ordinary approval behavior, auto-allowing all otherwise-valid requests for one fixed 24-hour window, and denying all requests until changed. Enabling global auto-allow requires an explicit warning because it bypasses remembered denials while active. Global deny-all blocks remembered approvals. Mode changes advance a durable epoch, invalidate unredeemed reservations, and immediately resolve waiting requests. Neither override bypasses local requester authentication, canonical project/worktree checks, operation validation, configured source availability, or audit.

Default matching is exact, including the sorted set of secret IDs. A client-scoped stored decision deliberately omits only operation matching; it does not cross client, project/worktree, secret-set or delivery-mode boundaries. More secrets, a different set, another project or exec-inject → raw-read requires a new decision. Do not add wildcard inheritance initially. A matching deny takes precedence over allow; replacing a saved decision is a deliberate local edit.

Normal rotation of a value under the same stable provider secret ID preserves permission. Changing an alias to refer to a different secret invalidates it. Deleting/recreating a client or secret gives a new internal generation even when its visible name is reused.

## Policy evaluation and fast path

~~~mermaid
flowchart LR
    R["Inbound request"] --> L["Durably log request"]
    L --> V["Authenticate and resolve scope"]
    V -->|"Rejected"| N["Return denied"]
    V --> P{"Matching policy?"}
    P -->|"Deny"| N["Return denied"]
    P -->|"Allow"| A["Reserve redemption"]
    P -->|"No match"| Q["Queue native popup"]
    Q --> D{"User choice"}
    D -->|"YES / YES_24H / YES_ALWAYS"| A
    D -->|"NO / NEVER"| N
    A --> C["Recheck policy + provider state"]
    C --> X["Fetch selected values and execute"]
~~~

Persist rules atomically before reporting a persistent choice as saved. Every remembered allow records its scope kind; a 24-hour rule also records its issued time and fixed expiry. Expired rules cannot match and are removed during normal policy maintenance. Keep a memory index of decision metadata and policy epoch, not a global plaintext-secret cache. The warm path authenticates, resolves scope, checks deny/allow and reserves a redemption without creating a window.

Initial performance targets, to measure rather than assume: p95 below 25 ms for a cached decision over an established local pipe; p95 below 150 ms for broker-side .env resolution and authorization excluding child startup. Report cold start, provider unlock, process startup and human wait separately.

Recheck client/project/alias generations, any operation/recipe generation required by the stored scope, and policy epoch immediately before delivery. Revocation invalidates unredeemed reservations. It cannot recall bytes already delivered to a child.

Use a single-use reservation with atomic consume; two concurrent retries cannot redeem YES twice. Deduplicate principal + request ID. Pending duplicates join the same request; distinct requests remain distinct. A client retry after an uncertain child launch receives LaunchUnconfirmed, never an automatic second launch.

Maintain a durable operation journal independent of ephemeral reservations. Flush LaunchCommitted, meaning an irrevocable intent to attempt this launch, before creating a child. Record Started with PID and process creation time immediately afterward. On broker restart, a committed launch without conclusive outcome is LaunchUnconfirmed even if no PID was recorded; the journal survives reservation cleanup.

## Popup

Use a broker-owned native modeless toast on an independent STA. Place it above the taskbar at the lower-right edge of the active screen, keep it out of the taskbar, and do not block other Joydex windows. It can be shown while the main Settings window is closed, blocked or being recreated. It never runs on the Voice media thread. The tray process supervises the broker and starts a replacement if it exits unexpectedly.

Wireframe:

~~~text
Agent Secret Request                     Approve this request?
release-helper (cmd.exe) requests        Choose how long Joydex should
to use DEPLOY_TOKEN.                     remember your answer.

REQUEST DETAILS                          [ Allow once ]
Who    release-helper                      Use the secret for this request only
What   DEPLOY_TOKEN                      [ Allow for 24 hours ]
Where  Project  Joydex                     Remember this command until tomorrow
       Worktree  preview                 [ Allow always ]
Why    Publish the requested preview       Remember this command until revoked
Via    cmd.exe /d /c deploy-preview

                                         [ ] Apply my decision to all future
                                             commands from this agent

                                         [ Deny this time ] [ Deny forever ]
~~~

The checkbox is unchecked whenever a toast opens. Checking it disables Allow once and Deny this time because those choices have no remembered scope. It broadens Allow for 24 hours, Allow always, and Deny forever only within the displayed authenticated client, canonical project/worktree, exact secret set and delivery mode. Grant management shows every rule's scope; 24-hour rules also show creation time, fixed expiry and remaining time. The toast prominently marks aliases that have not previously appeared in a valid request from this authenticated agent and project.

## Settings page

Give Secrets one page in Joydex configuration. Show the current global approval mode first, with the fixed auto-allow expiry when active and actions for the same three modes available from **Plugins → Secrets** in the tray. Show lifetime and rolling 24-hour counts for requests, executions, approvals, and denials. Follow with remembered decisions: show allow or deny, requester, project, secret aliases, command-bound or agent-wide scope, and expiry, with a deliberate revoke action. Recent activity uses sanitized audit metadata only. The page owns secret-source availability. Local requester credentials are automatic internal bookkeeping and have no setup section. Saved recipes stay out of the main UI; their existing storage and broker support remain available for advanced unattended automation. Never display secret values, credential verifiers, full environments or stored command history.

For CODEXTASK, show verified task title and project, with host/short ID in details. Titles/reasons are escaped, length-limited text; never treat them as markup or window commands. Secret values never appear.

Use Segoe UI, readable contrast, accessible labels, keyboard navigation and saved placement. Do not select a positive choice by default. Escape/close means cancel this request, with no persistent deny. Focus failure leaves the request pending and signals via tray/attention; do not inject Enter into another app.

One request owns the visible approval slot. Show a small pending count and allow review/next without granting. Initially cap at 64 pending requests globally and eight per client, with a 120-second request expiry. Return queue-full/expired results promptly. These defaults are configurable locally and bounded by host limits.

The common helper waits 90 seconds by default and cancels before broker expiry. It may instead detach with one explicit fallback. `run-without-secrets` starts the exact command with the caller's environment but without Joydex-injected values after removing the request's authority to receive them. `leave-pending` does not start the command. Both leave a short policy-only review state called **Agent Detached**. It has no reservation and can never transition back to an executable request. The popup explains what the agent did, hides Allow once, renames Deny this time to Dismiss, and disables remembered choices until the user checks **Apply my decision to all future commands from this agent**. A recorded choice applies only to future requests.

When the Windows session locks, suspend presentation and all new redemptions, including cached approvals. On unlock require provider readiness; expired requests stay expired. Broker exit cancels pending requests. Persistent decisions survive; one-use reservations do not.

## Bindable actions

Use the common registry:

| Action ID | Meaning |
|---|---|
| joydex.secrets.approve-always | YES_ALWAYS for the shown request, using the visible checkbox scope |
| joydex.secrets.approve-24-hours | YES_24H for the shown request, using the visible checkbox scope |
| joydex.secrets.approve-once | YES for the shown request |
| joydex.secrets.deny-once | NO for the shown request |
| joydex.secrets.deny-always | NEVER for the shown request |
| joydex.secrets.show-request | Show pending approval |
| joydex.secrets.next-request | Move review to the next pending request |
| joydex.secrets.lock | Lock broker/provider access |
| joydex.secrets.open-grants | Open local grant management |

A decision carries the active request ID, revision, display challenge and, for every remembered decision, the visible checkbox scope. The broker checks the challenge and the registered input/UI role. Buttons held before display, repeated key events, stale releases and queued events cannot approve this or the following popup.

Require a fresh physical press after presentation, consume it through release and reject it if capture is active for that device. A single press never both approves and triggers a normal Codex binding. Persistent choices must be explicitly bound; do not add default YES_ALWAYS/YES_24H/NEVER mappings to existing controllers.

Sensitive approval actions have a local-consent policy independent of Codex foreground requirements. The ordinary action executor's foreground/simulator guards remain intact for ordinary actions.

Discord, Pebble, the agent helper and arbitrary plugin messages have no approval-response capability. A later PAD approval page requires a paired trusted source and request challenge on its wire protocol; current fixed PAD firmware does not establish that capability.

## Transport and trust roles

Use local named pipes initially; no browser or LAN secret endpoint is required. Separate request, local administration and consent-response roles. A requester can submit/cancel/poll its own requests and redeem its own reservations; it cannot approve or edit grants.

The broker and trusted UI/input channel authenticate through a private launch capability plus verified process/connection identity. Apply explicit pipe DACLs and local-only access; verify SID/session, peer process creation time and expected binary where useful. CurrentUserOnly and PID checks help establish a process/account boundary, while the registered credential establishes the client.

The Windows APIs expose connected pipe client PID and explicit pipe access control. They do not establish Codex task identity. [Pipe client identity](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid), [pipe security](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights).

Engine subscriptions contain request ID, principal/project labels, aliases, state and expiry only. Raw values bypass engine logs/events/settings. Use separate provider and process-launch interfaces so a provider cannot execute arbitrary client commands.

## Secret providers

Start with the smallest provider contract: resolve a configured reference, report stable ID/mapping generation, fetch value at redemption, lock/unlock where supported, and report availability. Metadata listing exposes only aliases available to that requester and project.

| Provider choice | Behavior |
|---|---|
| **ENVFIRST** | User selects an exact .env path in local UI. Broker alone parses that file into a private dictionary; no parent-directory search or shell sourcing. |
| **DPAPIIMPORT** | Future step: import selected keys, or paste a token into the local Secrets UI, for the broker to write into its own CurrentUser-protected store. Verify user-only ACLs and preserve stable IDs during value rotation. |
| **VAULTADAPTERS** | Later: an explicitly chosen existing vault provides values; Joydex retains its own scoped consent policy. |

ENVFIRST accepts a deliberately small dotenv subset: exact keys, comments, `export`, unquoted values, single-quoted values and double-quoted values with bounded escapes and multiline content. It rejects duplicate keys and all interpolation, so values never depend on external broker environment state. The parser reads one exact path and never loads values into the entire engine/broker environment. Tests cover quoting, multiline data, duplicates, interpolation and environment non-mutation.

Parse/configure files inside the management tool. Agents never open .env or receive its contents. Store the source path and alias mapping locally outside projects; show only secret names/availability. Handle file changes as provider generations, and do not use partially written files.

### Future per-project source routing

ENVFIRST supports multiple named source files when their public aliases are unique across sources. Extend that support to allow the same logical secret in more than one source. The consent toast then shows a compact source dropdown beside that secret. The dropdown contains the configured source names only; it never shows values and does not expose file paths unless the user opens source details. A request with several secrets gets one dropdown for each secret that has more than one valid source. Requests with only one available route keep the current uncluttered layout.

The initially selected item comes from a remembered route for that canonical project and logical secret, falling back to the configured default source. Selecting another source in the toast and submitting a decision saves that route as the default for that project going forward. Source routing is configuration rather than an approval: changing the route does not create an allow, and an allow or deny does not make the selected source a global default for other projects.

The broker resolves the selected source, then recomputes the trusted request scope and display challenge before it accepts the decision. The scope includes source ID, stable secret ID and mapping generation, so a remembered approval for one source cannot authorize another source's version of the same named secret. The requesting agent cannot choose or override the source. Removed, unavailable or remapped sources never fall through silently under an existing grant; Joydex returns provider unavailable or asks again with the new route displayed. Source names that no longer provide the requested secret are disabled or omitted from the dropdown.

The future paste flow needs a local secure-entry control, an explicit alias/provider confirmation and a one-way write directly to the broker-owned store. It clears the UI buffer after the broker accepts it, never echoes the token into events, logs, settings drafts or agent context, and never uses the clipboard as storage. Make the storage provider replaceable so a later dedicated encrypted store can retain the same stable IDs and consent grants. This is planning work only; the first implementation must write pasted values to an encrypted broker-owned store such as a CurrentUser DPAPI store rather than plaintext configuration.

The current WirelessPanelConfigurationStore has useful DPAPI, byte clearing and atomic replacement patterns. Its ACL changes are best effort; a new store claiming verified ACL protection must fail provisioning if those ACLs cannot be enforced. Same-user DPAPI still does not separate tasks under the same user. [DPAPI scope](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope?view=net-8.0).

Keep token migration explicit for Pebble/PAD/Discord. Preserve existing values and test the consumer before changing its reference. A plugin's bootstrap credential should not require a recursive approval request from that same plugin to bring the broker online.

## Deliver secrets through an operation

Recommended first mode **EXECINJECT** starts an approved child process in the requester's working directory with its inherited environment. The helper sends that environment over the authenticated run pipe only after approval; the broker overlays the selected secret values without putting them in argv, the helper environment, normal tool JSON or the clipboard. Use ProcessStartInfo.ArgumentList, a validated executable and working directory, and no implicit shell. Do not log or audit inherited environment values. The operation digest includes the inherited-environment policy, so earlier exact-operation grants do not apply; client-scoped rules still cover future commands in their existing scope. Individual caller values remain outside remembered grant matching.

Every approved operation defines executable identity, the exact argument vector, target cwd, secret-to-environment mapping and effective operation scope. Scripts and imported config are mutable: fingerprint their relevant inputs when the executable alone does not identify the behavior.

The normal agent path submits a complete operation proposal and receives the five-choice popup directly. The broker validates and freezes that proposal, and YES/YES_24H/YES_ALWAYS authorizes the scope displayed for that choice. A proposal cannot execute before consent or widen an existing remembered scope.

For an inline proposal, derive its operation identity from a broker-computed canonical digest of the resolved authorization scope. Ignore caller-supplied recipe IDs/generations. Equivalent proposals reuse that identity across request IDs and reason text, so YES_ALWAYS remains fast; a changed parameter, fingerprint or output mode produces a different scope.

Saved recipes remain advanced plumbing for deliberately preauthorized automation. They are not shown on the main Secrets page and are not part of the ordinary agent guidance.

~~~json
{
  "id": "deploy-preview",
  "generation": 1,
  "executable": "<locally selected absolute executable>",
  "arguments": ["deploy", "--target", "{target}"],
  "parameters": { "target": { "enum": ["preview"] } },
  "cwd": "<canonical worktree root>",
  "secretEnvironment": { "DEPLOY_TOKEN": "deploy-token" },
  "scopeMode": "exact-resolved-operation",
  "fingerprintInputs": ["<selected script/config inputs>"],
  "outputDisclosure": "summary"
}
~~~

The default scope mode includes resolved parameters and selected input fingerprints. A deliberately broader mutable-recipe mode requires separate local enablement and popup wording; it cannot inherit existing exact-operation grants. Recipe edits increment generation and invalidate old grants. Missing executables, unresolved templates or unverified path/fingerprint inputs fail before requesting consent.

The broker-controlled launch helper validates the recipe and consent, flushes the launch intent, fetches values at the last moment, rechecks policy, starts the child, records its identity, and clears temporary buffers where possible. A provider failure before launch can be marked FailedBeforeLaunch conclusively; a crash in the creation window stays uncertain. Immutable managed strings and child environments limit reliable zeroing; do not promise complete memory erasure.

An arbitrary approved child can print or transmit its secrets. The ordinary `exec` wrapper defaults to unchanged byte passthrough for stdin, stdout and stderr, preserving the child exit code without adding a result envelope. `--output-mode json` opts into the previous bounded, redacted summary. Output mode remains part of the authorized operation scope. The broker connects three current-user stream pipes owned by the authenticated requesting process; stream contents stay outside metadata and audit records. A disconnected wrapper cancels its child. This is stream forwarding, not terminal emulation. Output filtering in JSON mode is a diagnostic safeguard, not containment.

Modes to add only when required:

- **OPERATIONPROXY**: a bounded named API operation inside the broker with destination/method controls; stronger credential non-disclosure, additional per-service implementation.
- **RAWREAD**: an explicitly enabled value-release capability with its own popup wording and grants; it may place values in agent/tool context and is off by default.

EXECINJECT should satisfy routine CLI-based tasks; it does not pretend to populate the environment of a process that has already started.

## Agent helper and skill deliverable

Ship a small authenticated local helper plus optional MCP wrapper. The primary operation is:

~~~text
joydex-secrets exec --client release-helper --project <project-ref> --reason "Publish the package" --secret GITHUB_TOKEN --secret NPM_TOKEN -- publish.exe --target preview
~~~

The normal tray atomically publishes `%LOCALAPPDATA%\Joydex\profile.yaml` with schema version, write time, current data root, configuration path and packaged application/helper paths. The skill reads this fixed location and passes the recorded data root to the helper. Demo and settings-only processes never overwrite it. The profile is value-free discovery metadata for the most recently started normal package; broker liveness still comes from the helper transport. Keep this one file authoritative rather than duplicating its fields in the registry.

Each `--secret ENV_NAME` selects the same-named public alias and injects it under that environment name. Every token after `--` is an executable or individual argument. The helper performs request, wait and run as one operation, emits one sanitized JSON result and returns the child exit code. The public client ID names the stable local requester; the helper creates or obtains its credential outside flags and model text. Request parameters never claim verified task identity.

The lower-level aliases/request/wait/cancel/run operations, operation files, and saved recipes remain available for advanced clients. Every invocation authenticates the same registration. Request IDs are lookup keys with no authority. A reservation is an opaque, short-lived handle bound to that authenticated principal and request; possession alone is insufficient. Operation files contain aliases, executable/arguments and other metadata only, never secret values or client credentials.

A request returns allowed, pending, agent_detached, denied, expired, revoked, identity_unverified, provider_unavailable, queue_full, completed or launch_unconfirmed, with an opaque request/reservation handle where applicable. A durable completed or unconfirmed request ID remains terminal across broker restart; a deliberate later attempt uses a new request ID. Permission-check queries expose only this client's own scope and never consume YES or reveal values.

The implementation slice includes a tested agent skill that clearly documents how an agent retrieves authorized secret *use* through this tool. It explains that alias discovery and approved operation delivery are the supported retrieval path; it never presents raw-value retrieval as a normal agent capability. The skill tells the agent to:

1. State the exact project, environment-variable names, executable arguments, and short reason in one `exec` call.
2. Request once; use an already allowed decision immediately. Explain that remembered decisions stay operation-bound unless the local user checks **Apply my decision to all future commands from this agent** in the toast.
3. Let the helper await approval and run the operation while unrelated Joydex work continues.
4. Keep the default timeout action as cancel unless the work explicitly calls for one documented fallback.
5. Stop on denial/expiry/detachment; never retry in a loop, switch identities, broaden scope or edit rules.
6. Report the sanitized outcome and child exit code.
7. Never read .env, echo resolved values, place them in messages, or implement an approval bypass.

The skill must include a runnable, sanitized `exec` example, timeout behavior, advanced alias discovery, and status handling for completed, denied, agent_detached, expired, revoked and provider_unavailable. Tests must prove that the common path preserves argument boundaries, never reads .env, and receives no raw value. Stage changes outside active skill directories, run the required SkillSpector static gate, resolve findings according to local policy, then install and rescan the exact result.

## Audit, recovery and tests

Durably create a sanitized `RequestReceived` audit record before policy matching for every structurally valid authorization submission that reaches broker evaluation, including preapproved allow/deny cache hits, requests that queue for consent, invalid submission fields, unauthenticated attempts and provider-unavailable requests. Follow it with a linked outcome record, including whether a preapproval matched and whether delivery started. A raw transport frame that cannot be parsed into a submission is rejected without inventing request metadata. Audit request ID, verified principal or authentication failure, project reference, aliases, operation digest, policy decision, grant scope kind, issue/expiry timestamps, source of consent and delivery outcome. Keep reasons, commands and paths minimized; never store values, full environments or raw protocol payloads. Retain visible activity for 30 days with user-controlled cleanup. Keep the bounded durable operation ledger separate so cleanup does not remove launch tombstones or persistent grant rules.

Maintain counters independently from the clearable activity log: requests from `RequestReceived`, executions from `LaunchStarted`, and approvals or denials from terminal consent/request outcomes. Store lifetime totals and bounded per-minute buckets for the rolling past 24 hours. Counter failure must not prevent an already-written audit event or an authorized launch.

Grant storage can begin as one atomic versioned file with a broker-owned writer and in-memory index; high throughput does not require a new database service. Serialize rule updates and reservation consumption. Corrupt policy data blocks grants and asks for local recovery rather than silently accepting defaults.

Test spoofed labels, invalid registration, wrong pipe roles, PID reuse, project/path replacement, client recreation, alias remapping, delivery-mode widening, additional secrets, parameter/output-mode changes, script changes, equivalent inline-proposal cache reuse, concurrent one-use redemption and revocation immediately before delivery. Cover both 24-hour scopes and permanent YES_ALWAYS scopes: operation-bound cannot run a changed operation, client-scoped still rejects another client, project/worktree, secret set or delivery mode, and 24-hour rules do not match after fixed expiry. Verify a linked sanitized audit pair for preapproved allow/deny hits, queued requests, malformed or unauthenticated requests, provider failures and delivery outcomes.

Exercise stale popup challenges, held controller input, capture collision, no focus, session lock, UI crash, broker restart, provider unavailable, expiry and queue exhaustion. Include canary values that must never appear in logs/errors/events.

Crash the broker after LaunchCommitted but before process creation, after creation but before Started is persisted, and after Started but before the reply. Every inconclusive case retains a durable launch record and refuses automatic relaunch.

Measure warm/cold latency separately. Run an attended scenario with active Voice, controller/PAD activity and settings open while a request waits, is denied, is allowed once, receives each 24-hour scope and is allowed permanently. Verify the next exact request avoids the popup, a changed operation prompts after operation-bound approval, client-scoped approval remains bounded as displayed and expiry prompts again.
