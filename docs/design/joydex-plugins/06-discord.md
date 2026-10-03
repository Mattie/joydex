# Discord bot plugin

Status: proposed, with a required Desktop compatibility gate. Plugin ID: joydex.discord.

## Product behavior

An authorized person selects an existing locally whitelisted Codex project and starts a new task from a Discord thread. That Discord thread becomes a conversation surface for that one task. The task is owned by Codex Desktop and can be opened there while the Discord conversation continues.

There is no connect-existing-task command, task-ID input, title-search binding, general Desktop history browser, or remote project creation. The only routable tasks are those created by this plugin and recorded in its durable binding store.

Recommended first workflow:

1. In a permitted Discord channel, create an ordinary Discord thread.
2. Inside that unbound thread, invoke /joydex new project:joydex prompt:… .
3. The bot acknowledges, creates a fresh Desktop task in that enrolled project, and records the returned identity.
4. It posts a concise linked-task receipt. Subsequent authorized text messages in that thread go to the same task.
5. Final assistant messages and compact task state appear in the Discord thread.

Creating the Discord thread from a parent-channel slash command is an optional next slice. It adds a second non-transactional creation boundary; record the Discord creation result before beginning Desktop creation.

## Local setup and authorization

Setup in Joydex enrolls:

- Bot token as a secret reference or initial DPAPI-protected application credential.
- Numeric application, guild, allowed parent-channel and allowed user IDs.
- Existing Desktop projects, resolved by host ID + project ID + canonical root.
- A friendly project alias, per-project channel/user rules, and execution location.
- Locally configured model/reasoning preferences if desired; otherwise use Desktop defaults.

Project aliases and task titles are labels. Desktop project IDs and verified roots are authoritative. Resolve Windows case, separators, junction/reparse targets and worktree identity consistently. A project rename is harmless; a project moved to a different root requires local re-enrollment.

For Git projects, recommend a Desktop-managed worktree by default; non-Git projects use their saved local directory. An explicit local checkout preference is supported per project. The bot cannot supply arbitrary paths, branch refs, sandbox flags or approval policies.

Local allowlisting authorizes creating/using a task in that project; it does not make a broad-access Codex task a filesystem sandbox. Use Desktop's configured task policy. Never copy the Voice owner's full-access/no-approval defaults.

Reject DMs, bots, webhooks, unauthorized users/channels/guilds and unsupported attachments in v1. Recheck authorization before every queued send and mirrored reply. Removed permissions suspend a binding immediately.

Channel visibility determines who can read mirrored content. A user allowlist controls who may send commands, but everyone with access to the Discord thread can see its replies. Setup therefore records the destination channel as an approved place for that project's conversation.

## Desktop gateway: proof before implementation commitment

Current local evidence:

| Surface | Verified boundary |
|---|---|
| DesktopTaskBridgeProtocol / broker MapTool | Existing status/list/read/send only |
| DesktopAppToolsEnvironmentResolver | Verified Desktop process chain and packaged adapter discovery without app.asar parsing |
| PackagedCodexAppToolsClient | Calls require Desktop executor task metadata |
| CodexVoiceWorkspaceService | Uses a separate Joydex-owned App Server; mocked project APIs do not prove Desktop support |
| CodexAppServerClient | Current Voice client rejects server-originated requests; it is not a general approval-aware client |

The installed adapter was inspected read-only under OpenAI.Codex 26.901.6511.0. Its tool definitions are dynamic. No live creation or external identity probe was executed during planning.

The official App Server interface describes its own transport and versioned task APIs. It does not establish an externally joinable Desktop project-management contract. A new App Server process would have a different ownership path. [Official App Server documentation](https://learn.chatgpt.com/docs/app-server).

Add a separately scoped gateway capability, conceptually CreateDesktopTaskInAllowedProject, rather than broadening the existing Voice tool. Its implementation must:

1. Establish a legitimate locally enrolled Desktop executor context. A dedicated user-created bridge control task is a candidate bootstrap only if the installed adapter accepts it. Never invent a task UUID or borrow an unrelated task's identity.
2. Enumerate actual saved Desktop projects and verify ID/root.
3. Create a fresh task in the requested allowed project, preserving chosen local/worktree semantics.
4. Resolve any pending setup handle into an actual task ID and host ID.
5. Observe and continue exactly that task while Desktop remains its owner.
6. Show native approvals/user-input needs in Desktop and confirm completion/reconnect behavior.

Failure of any required capability leaves Discord creation unavailable with a local diagnostic. Existing integrations continue. A separately owned agent backend could be designed later under a distinct user-selected mode; it is not a fallback for this requirement.

Future supported Desktop APIs should replace the private adapter behind this capability without changing Discord policy or mapping records. Compatibility fixtures and an installed-version canary are required after app upgrades.

## Plugin and gateway boundary

Discord.Net is the recommended .NET 8 transport candidate. Use its Gateway, REST and interactions support in a dedicated worker; pin a released compatible artifact before implementation. Keep handlers short and schedule durable work through bounded channels. [Library event guidance](https://docs.discordnet.dev/guides/concepts/events.html).

The engine/gateway owns the project whitelist, creation intents and authoritative task bindings. It records a binding only from its own confirmed creation result. The worker cannot supply an arbitrary destination task in a send call or establish provenance by writing a mapping. It supplies a gateway-issued binding ID and event ID; the gateway resolves the enrolled destination and confirms scope.

Scope private bridge methods by caller capability: Voice retains constrained messaging, Pebble retains its configured destination, Discord gets creation plus read/send/observe for its own created tasks. Avoid giving the Discord worker direct access to the packaged adapter.

## Persistent records and creation

Use a local transactional store or equivalently serialized durable records with uniqueness constraints. Choose the concrete implementation in the first persistence slice; simple independent JSON writes are insufficient for concurrent binding creation. Gateway-owned records carry creation intent, task identity and binding authority; worker-owned records carry Discord receipts, queue state and reply mirrors, referencing gateway-issued binding IDs.

Store:

- Bot instance identity and schema version.
- Binding key: bot instance + guild ID + Discord thread ID.
- Initiating user, authorized project identity/root, execution preference and policy revision.
- Creation operation ID and originating Discord interaction ID.
- Pending setup handle, eventual host/task ID, and creation state.
- Inbound event receipts and delivery attempts.
- Outbound task/turn/item cursor and Discord message IDs.

Persist a creation intent and claim the unbound thread atomically before dispatch. Concurrent /new requests in one thread resolve to the same in-progress receipt or an explicit already-bound result. An intentionally new Discord thread always creates a new task, even with an identical title/prompt.

Creation states: Requested → Dispatching → PendingSetup → Bound. A transport loss after dispatch becomes CreationUnconfirmed. Do not call creation again merely because a recent-task list did not show the result. Only verified server idempotency or an exact returned setup/task handle can support automatic reconciliation.

A user can review an unconfirmed creation locally, confirm evidence of the created task, or abandon the intent. Binding a task during recovery requires evidence tying that task to this exact creation operation; recovery must not become a general attach-existing feature.

## Message flow and ordering

~~~mermaid
sequenceDiagram
    participant D as Discord thread
    participant B as Discord worker
    participant G as Scoped Desktop gateway
    participant C as Codex Desktop task
    D->>B: Authorized message event
    B->>B: Persist event receipt
    B->>G: Send via owned binding + operation ID
    G->>G: Recheck project, user and binding scope
    G->>C: Continue exact Desktop-owned task
    C-->>G: Task state / final assistant item
    G-->>B: Item ID + text + cursor
    B->>B: Persist outbound item
    B-->>D: Post final text, save Discord message ID
~~~

Serialize inbound sends per binding. Different tasks can progress concurrently. Persist before enqueue, and cap pending count/bytes; report backpressure. Default to waiting until the current turn completes before delivering another queued Discord message. A future explicit steer command can be added only with verified semantics.

Deduplicate Discord message/interaction IDs across reconnects. Store Dispatching before each Desktop send; missing confirmation becomes SendUnconfirmed, with explicit recovery. At-least-once Gateway events must not create duplicate Codex turns.

Edits and deletions do not rewrite an already submitted prompt. V1 ignores edit events for dispatch; the user sends a follow-up correction. Deleting a Discord message does not erase a Codex record.

## Replies, state and reconnect

Mirror final assistant text and concise Running/Waiting for local approval/Complete/Failed states. Do not mirror reasoning, tool arguments, terminal output, arbitrary files, raw credentials or all historical task content.

Observe by exact task/turn/item IDs and a stable cursor. Prefer verified native events; if unavailable, bounded polling of the exact task is acceptable for v1. The current recent-task list is not a complete event feed. Capability validation must establish how a final item and its identity are recovered after reconnect.

Recover the initial turn from the creation operation, including final items completed before the binding becomes Bound; then advance the durable cursor. V1 subsequently mirrors new final assistant items, including replies caused by someone continuing that task locally in Desktop. Setup makes this behavior explicit. Do not rebroadcast local user prompts automatically. If the adapter cannot recover the initial turn or distinguish old and new items reliably, keep reply mirroring blocked rather than guessing.

Split long output at safe boundaries under Discord's current content limits, preserve code fences, disable allowed mentions, suppress unwanted link previews and retain item-to-message mapping. A lost Discord post acknowledgement also becomes uncertain unless the API supplies usable dedupe/reconciliation; blindly reposting can duplicate replies.

Defer slash interactions within three seconds. Tokens for interaction follow-ups expire after fifteen minutes, so long-running replies use ordinary bot messages with durable delivery records. [Discord interaction contract](https://docs.discord.com/developers/interactions/receiving-and-responding).

Follow library/Gateway resume behavior and Discord rate-limit headers. Respect Retry-After rather than inventing fixed global request rates. Pending outbound items survive disconnect, but permission loss holds them. [Rate limits](https://docs.discord.com/developers/topics/rate-limits).

## Commands and conversation lifecycle

| Command | Scope |
|---|---|
| /joydex new | Unbound thread only; allowed project selection and initial prompt |
| /joydex say prompt:… | Authorized bound thread only; same durable dispatch path as ordinary text, including slash-only mode |
| /joydex status | This binding's state and latest delivery result |
| /joydex projects | Only project aliases allowed for this user/channel |
| /joydex close | Stop accepting and mirroring this binding; preserve task and records |
| /joydex help | Short available-command guide |

No remote stop/interrupt or native approval response in v1 unless its actual transport is verified and explicitly enabled later. Waiting for local approval is a valid state and must not be treated as an automatic failure/retry.

An archived/locked/deleted Discord thread suspends delivery; no automatic task deletion or archive follows. Deliberately closing a binding does not stop or delete its Desktop task. The local UI can re-enable the same plugin-created binding after checking access, or create a new conversation in a different Discord thread.

Keep native Codex approvals and Joydex secret approvals distinct. Discord has no permission to press YES_ALWAYS or any other secret consent action.

## Intents and permissions

For ordinary conversation use Guilds, GuildMessages and MessageContent. Enable the applicable privileged intent in the bot portal and obtain Discord approval where required. A slash-only mode is a deliberate limited fallback when normal message content is unavailable. [Gateway documentation](https://docs.discord.com/developers/events/gateway).

Use least required bot permissions: channel visibility, message/history access and sending in threads. Thread creation permissions are needed only for the optional parent-channel workflow. Private threads require explicit access. Do not request Administrator to bypass setup. [Thread permissions](https://docs.discord.com/developers/topics/threads).

## Acceptance and rollout

Start disabled, with one private test channel, one approved user and one disposable allowed project. The compatibility canary is the first gate; transport implementation follows.

Test unauthorized numeric IDs, alias spoofing, project relocation, concurrent /new, identical titles, archived threads, bot loops, missing MessageContent, duplicate Gateway events, queue overflow, rate limits and permission removal during queued work.

Inject crashes around Desktop creation/send and Discord posting. Verify no automatic duplicate creation/send and useful local recovery records. Test pending worktree setup without passing a temporary client handle as a task ID, and an initial answer that finishes before binding/observer setup.

Finally create a new task from Discord, open it in Desktop, continue from each surface, require a native local approval, disconnect/restart the bot, and recover only new final replies. Keep settings and an active Voice session running throughout. This is a behavioral acceptance gate, not a capability proven by planning research.
