# T3 desktop tasks

Joydex can combine local Codex tasks with the enabled environments in the local T3 desktop
app in its normal task pool, including the app's local server.
T3 support starts disabled. Historical completed chats do not fill task buttons.

Open **Task Alerts → T3 desktop…**, select **Follow environments in the T3 desktop app** and
choose **Check environments**. Enable monitoring and save. Task Alerts names every
discovered environment and reports its own connection health. A local connection does not
imply that every remote route is available. Disabled environments are excluded; failed or
unsupported routes stay visible as unavailable rather than silently dropping their tasks.
Desktop mode supports the native Windows server, the configured already-running WSL backend
and compatible SSH routes. Monitoring does not install or start a T3 server. Relay or bearer-only
routes need an independently verified read-only route; Joydex does not reuse saved desktop
credentials. A previously pinned SSH route can cover the same enabled native environment.
Unverified WSL topology and SSH user/port overrides are reported as unavailable.

The single-host mode remains available by clearing that option, entering an existing
current-user SSH alias and choosing **Test connection**. Joydex uses noninteractive SSH;
Python 3 and the installed `t3` CLI must
be available on the Linux environment host, using the current user's normal T3 data location.
Each monitored runtime must expose its own loopback orchestration
API. Review the discovered environment identity, enable the source and save. The saved identity
is pinned; a different environment at the same alias is rejected until explicitly retested.

The monitor returns task identity, status, timestamps, pending-request identity and bounded
display labels. Each source uses a temporary read-only grant that is revoked after use, with
five-minute expiry as cleanup fallback. Desktop discovery reads connection metadata without
reusing the app's saved bearer credentials. No transcript, preview or prompt
is copied into Joydex's metadata cache. Failed or partial reads preserve pending attention and
mark retained observations unavailable; local Codex continues independently.

Task Alerts shows each task's title, project, application/environment and provider/model when
available. T3 task buttons launch the verified installed desktop executable with its native
chat URI as one argument, avoiding Windows' custom-protocol association lookup. If the
executable cannot be verified, navigation fails without acknowledging question alerts.
Executable discovery checks the current-user protocol command first, retaining strict file and
product verification. A present invalid user override blocks fallback. Connection diagnostics
distinguish missing registration, invalid command, missing file, product mismatch and verification
failure; CLI failures retain a numeric exit code without exposing process output.
A successful visit acknowledges the async questions already displayed by Joydex.
Newer questions and blocking permissions stay
pending. This does not answer or remove the application's question cards.

**Add missing shortcuts** and **Remove Joydex shortcuts** are explicit, reversible operations.
They preserve existing custom rules and order, retain default shortcuts and refuse conflicts.
Unsupported effective-keymap discovery returns diagnostics without changing the file. Ordinary
monitoring never rewrites shortcuts.

Stock T3 currently provides no verified external active-environment/keymap context. Shared
New Chat, Previous Chat, Next Chat and Stop actions remain unavailable in T3, even after shortcut
setup. Joydex reports that limitation and does not send those actions to Codex instead. Codex's
new semantic Stop action also remains unavailable pending a verified mechanism. Room Voice
continues to target local Codex only.

Software tests do not establish actual desktop arrival or physical output. After a separately
authorized installation, verify chat arrival and physical task buttons/lights with both apps,
then test each enabled action and focus-change/key-release behavior. Do not mark unavailable
actions accepted. See [ADR 0017](adr/0017-aggregate-desktop-task-sources.md) for migration,
freshness and compatibility rules.
