# Joydex Secrets

Joydex Secrets lets a local agent run a specific command with selected secrets added to that child process. The agent requests public names; the broker supplies their values to the approved process environment. Command output passes through normally.

This is a cooperative consent tool for one Windows account. An unrestricted process running as the same user may still be able to open a plaintext `.env` file or use that user's DPAPI data. The approval window does not claim to provide an operating-system sandbox.

## Set it up

Build the complete local package:

```powershell
.\scripts\Publish-Joydex.ps1 -DotnetPath .\.tools\dotnet\dotnet.exe
```

Start `artifacts\Joydex\win-x64\Joydex.App.exe`, then open **Configure… → Secrets**.

1. Under **Secret sources**, add each exact `.env` file Joydex may use. Joydex imports variables under their environment names by default; you can rename a public alias when needed. Public names must currently be unique across sources. Joydex stores paths and mappings, not values.

There is no agent enrollment step. On its first valid `joydex-secrets exec` call, the helper creates its protected local identity and binds the supplied project name to the current working folder before asking for consent.

Changing a source mapping advances its configuration generation. Any earlier approval that has not run yet is rejected, so the agent must make a fresh request for the changed configuration.

After upgrading from the minimal-environment version, exact-command approvals ask again. Existing approvals explicitly applied to all future commands from an agent remain active within their existing agent, project/worktree, secret-set, and delivery-mode scope.

The Joydex tray process supervises the packaged Secrets broker and starts a replacement if it exits unexpectedly. The broker is tied to that tray session and closes when Joydex exits.

The page also shows approval mode, usage counts, remembered decisions, and sanitized activity. A decision keeps its agent, project, and public alias labels even after activity is cleared. Revoking a decision makes the next matching command ask again. Activity is retained for 30 days and can be cleared from this page without changing grants, usage counts, or duplicate-launch protection.

## Use the helper

`joydex-secrets.exe` is published beside `Joydex.App.exe`. It creates or loads its protected local credential itself; credentials do not belong in command arguments or operation files.

Normal tray startup atomically writes `%LOCALAPPDATA%\Joydex\profile.yaml`. Local clients read its `secrets_cli_path` and `data_root` fields to find the helper and the matching broker without inspecting running processes. The profile points to the most recently started normal Joydex package; it does not claim that Joydex is currently running. Demo and settings-only processes do not replace it.

Ask Joydex to run the exact command. Repeat `--secret` for every environment variable it needs; each name also identifies the same-named public alias. Everything after `--` is the executable followed by its individual arguments.

```powershell
$profilePath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Joydex\profile.yaml'
$fields = @{}
foreach ($line in Get-Content -LiteralPath $profilePath) {
    if ($line -match "^([a-z_]+):\s+'(.*)'\s*$") {
        $fields[$Matches[1]] = $Matches[2].Replace("''", "'")
    }
}
$helper = [IO.Path]::GetFullPath([string]$fields.secrets_cli_path)
$dataRoot = [IO.Path]::GetFullPath([string]$fields.data_root)
& $helper exec `
  --data-root $dataRoot `
  --client codex `
  --project my-project `
  --reason 'Publish the requested preview build' `
  --secret GITHUB_TOKEN `
  --secret NPM_TOKEN `
  -- publish.exe --target preview
```

The helper submits the request, waits for the local decision, and runs the command after approval in the caller's current directory with the caller's environment. Joydex-approved values replace any inherited variables with the same names. The caller environment travels to the broker only for the run and is not written to the activity log. By default the helper forwards stdin and streams stdout and stderr as unchanged bytes, then returns the exact child exit code. It adds no success message or JSON envelope. Output is neither truncated nor redacted. Ordinary shell redirection works; the relay provides byte streams, not terminal emulation. Closing the wrapper cancels its approved child.

To retain the previous structured result, add `--output-mode json` before `--`. This captures bounded stdout/stderr, replaces Joydex-injected values with `[REDACTED]`, and prints one JSON result. It does not redact other values inherited from the caller. Its `secretsInjected` field says whether the broker supplied the requested variables. Output mode is part of the approval scope.

The normal approval timeout is 90 seconds. It cancels the request and does not start the command. You can set a shorter or longer wait from 1 to 110 seconds:

```powershell
--approval-timeout 30
```

Two deliberate alternatives are available:

- `--on-approval-timeout run-without-secrets` starts the command with the caller's environment and no Joydex-injected values. An inherited variable may already have a requested name. The popup becomes policy-only; a later answer affects future requests and cannot change the running command.
- `--on-approval-timeout leave-pending` exits without starting the command but leaves a short policy-only popup. That request can never launch later.

Use `--execution-timeout SECONDS` to shorten the one-hour child-process limit. If you need to check configured names without fetching their values, use `joydex-secrets aliases --client codex --project my-project`.

When no child starts, exit code 11 means denied, cancelled, expired, or revoked; 12 means the helper detached or a lower-level wait timed out; 13 means the provider is unavailable; 14 means the client identity was rejected; and 15 covers other broker statuses. Invalid arguments and local transport failures return 2. Default mode reports wrapper failures on stderr and leaves stdout for the command. A completed child keeps its own exit code; use JSON mode when a structured distinction matters.

The lower-level `request`, `wait`, `cancel`, and `run` commands, operation files, and saved-recipe storage remain available for advanced automation. They are not part of the normal Secrets UI or agent workflow.

For an advanced operation file with `outputDisclosure: passthrough`, redeem its reservation with `run --output-mode passthrough` to connect stdin, stdout, and stderr and return the exact child exit code. Other output disclosures use the default JSON result. The selected run mode must match the approved operation.

## Attached and detached execution

Attached execution is the default. The broker owns a Windows job containing the command and
its descendants. Caller disconnect (including JSON mode), broker shutdown, timeout, and root
process exit terminate the remaining tree. The job is assigned atomically at process creation;
closing its last owner handle also kills the tree after a broker crash.

For an authorized long-running job, use explicit detached execution:

```powershell
$launch = & $helper exec --detach --data-root $dataRoot `
  --client codex --project my-project --reason 'Run the requested background worker' `
  --secret GITHUB_TOKEN -- worker.exe --run | ConvertFrom-Json
$taskId = $launch.taskId
& $helper status --data-root $dataRoot --task $taskId
& $helper stop --data-root $dataRoot --task $taskId
```

The task ID is printed to stderr before submission. A successful JSON receipt confirms launch,
not application readiness or eventual success. Check the application's health separately.
The receipt contains helper/child identities, state, log paths, and optional deadline. Save it.
If the reply is lost, query that same task ID before considering another launch; uncertain
launches are never automatically retried. Reusing a task or request ID cannot launch twice.

Each detached task has a hidden single-task helper launched through the existing Explorer
desktop. Explorer must be available; there is no attached fallback. After launch commit,
the task survives caller and Joydex exits. It ends on completion, explicit stop, optional
timeout, or helper/session termination. No service, startup registration, or reboot recovery
is created. Status and stop communicate directly with the helper while Joydex is closed.
A stale PID cannot authorize a kill. If no helper or final receipt is available, status is
`unknown`; repeated stop requests are harmless.

Detached execution defaults to no timeout. `--execution-timeout SECONDS` accepts 1–2,147,483;
attached execution continues to accept 1–3600 and defaults to one hour. Detached execution
rejects `--output-mode` and alternative approval-timeout actions. Stdin is closed. Stdout and
stderr are captured separately, each rotating at 10 MiB with three files retained. Logs can
contain secrets printed by the application; they are not guaranteed to be redacted. Live log
readers must permit concurrent writes and file rotation.

Lifetime participates in exact and client-scoped approval matching. Legacy approvals mean
attached; they do not authorize detached tasks. Detached timeout participates in exact command
identity. The toast says that the task survives Joydex/caller exits and shows its duration or
“until stopped.” Approval changes govern future launches. Use explicit stop to terminate an
existing detached task; denying future access cannot remove a delivered credential.

Task receipts and logs live under `secrets/tasks/TASK_ID` with current-user permissions.
Receipts contain only identities, timestamps, state, process IDs/start times, deadline, exit
code, and log paths. Commands, arguments, environments, and injected values travel only through
the one-use authenticated handoff pipe. Permanent value-free request claims under
`secrets/task-claims` preserve duplicate protection independently of audit retention.

## Approval choices

- **Allow once** creates one single-use reservation for the request on screen.
- **Allow for 24 hours** remembers the decision for exactly 24 hours.
- **Allow always** remembers the decision until it is revoked on the Secrets page.
- **Deny this time** stops this request and allows a later deliberate request to ask again.
- **Deny forever** remembers the denial until it is revoked.

Remembered decisions apply to the exact command by default. Selecting **Apply my decision to all future commands from this agent** broadens a remembered choice to that local requester within the displayed project, secret set, and delivery mode. The two one-request choices are disabled because they have nothing to remember.

The toast marks secrets that are new for this agent and project. A changed secret set is a changed request scope and asks again even when the earlier command was remembered.

If the agent stops waiting, the toast explains whether it continued without Joydex injecting the requested values or stopped the command. **Allow once** is unavailable, **Deny this time** becomes **Dismiss**, and remembered choices remain disabled until **Apply my decision to all future commands from this agent** is selected. Any remembered answer applies only to future commands.

The tray's **Plugins → Secrets** submenu and the Secrets settings page also provide three global modes:

- **Use normal approvals** evaluates remembered decisions and shows a toast when no decision matches.
- **Auto-allow all for 24 hours** allows every otherwise-valid request without a toast until the displayed fixed expiry. Joydex warns before enabling it because it temporarily overrides remembered denials.
- **Deny all until further notice** rejects new and waiting requests and blocks unredeemed approvals until normal approvals or auto-allow is selected.

Changing modes cannot bypass local requester authentication, project/worktree checks, operation validation, source availability, or audit. Both surfaces show requests, secrets used, approvals, and denials as lifetime totals and rolling past-24-hour counts. **Secrets used** counts commands that crossed the broker's launch boundary.

Status `expired` means the broker's two-minute approval window ended before a decision or timeout action reached it. Nothing ran through that request and no secret was delivered. The ordinary `exec` flow cancels sooner, so this status mainly indicates an interrupted or lower-level client.

## Local files

Joydex writes `%LOCALAPPDATA%\Joydex\profile.yaml` at normal tray startup. It contains schema version `1`, a UTC write time, and value-free paths for the selected data root, configuration, application, and Secrets CLI.

Secrets state lives below `%LOCALAPPDATA%\Joydex\secrets` for the normal Joydex configuration:

- `configuration.json` contains source paths, public aliases, and advanced saved-operation data.
- `clients.json` contains internal local-requester metadata and one-way credential verifiers created by the helper.
- `credentials` contains CurrentUser-DPAPI-protected helper credentials with a verified user-only ACL.
- `policy.json` contains remembered allow and deny rules.
- `operating-mode.json` contains the global approval mode, change epoch, and fixed auto-allow expiry.
- `metrics.json` contains bounded minute buckets and lifetime request, execution, approval, and denial counts. Clearing activity does not clear these counts.
- `audit.jsonl` contains bounded request and launch metadata. It excludes reasons, commands, paths, credentials, and values.
- `operations.json` contains a bounded durable launch ledger so completed or uncertain request IDs stay terminal across broker restarts. Clearing activity does not remove this ledger.

The broker suspends approval presentation and redemption while the Windows session is locked. It shows one toast at a time and leaves the rest of Joydex running.
