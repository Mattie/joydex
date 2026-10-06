---
name: joydex-secrets
description: If you have one or more secrets you need to pass to a local command, run the command through Joydex so it can configure environment secrets without you needing to read their values.
---

# Joydex Secrets

Use `joydex-secrets exec` when a command needs a configured secret. Name the environment variables the command expects, then pass the executable and each argument after `--`. Joydex asks the user, injects approved values into that child process, and passes command stdin, stdout, stderr, and the exit code through unchanged.

## Rules

- Make one exact request when the need arises. The user does not register or enroll the agent first; the helper creates its protected local requester identity on first use.
- Use the environment variable name as the public alias: `--secret AGENTMAIL_API_KEY` requests the alias `AGENTMAIL_API_KEY` and injects it under that same name.
- Put the executable and every argument after `--`. Use a shell only when the command itself requires one.
- Keep the reason short and factual. It is shown to the user but does not broaden approval scope.
- Let the default approval timeout cancel the command. Use `run-without-secrets` only when the work remains useful without Joydex-supplied credentials. Use `leave-pending` only when the command should stop while the user may still set a rule for later requests.
- Stop after a denied, expired, revoked, identity-unverified, provider-unavailable, queue-full, detached, or launch-unconfirmed result. Do not retry in a loop, switch identities, edit approval rules, or weaken the command to obtain approval.
- Never open or read `.env`, inspect the credential store, echo environment values, put secret values in messages or files, or bypass the broker.

## Help the user configure a credential

Give setup instructions when the user asks or when a requested variable is unavailable. Name the project and environment variable needed for the current command. Never ask the user to paste a secret value into chat.

Ask the user to open the Joydex tray icon, choose **Configure…**, and open **Secrets**:

1. If the credential is not already in a `.env` file, tell the user to add a normal `ENV_NAME=value` entry with their local editor. Name the variable, but do not ask them to reveal its value.
2. Under **Secret sources**, choose **Add source** and select that `.env` file. Joydex imports its variable names automatically; **Import variable names** refreshes them later.
3. Leave a variable as `ENV_NAME` to expose and inject it under that same name. Use `PUBLIC_NAME=ENV_NAME` only when the user wants a different public name.
4. Save the source, then choose **Save and close**.

There is no agent setup step. The first valid helper call creates its protected same-user identity and binds the supplied project name to the current workspace folder before the consent request appears.

## What the user will see

Explain that a normal request appears as a bottom-right Joydex toast resembling this summary:

```text
Agent Secret Request

Codex (cmd.exe) requests to use AGENTMAIL_API_KEY.

Who:    Codex
What:   AGENTMAIL_API_KEY
Where:  Project my-project; Worktree preview
Why:    Send the requested message
Via:    agentmail.exe send ...

Choices: Allow once, Allow for 24 hours, Allow always,
         Deny this time, Deny forever
Option:  Apply my decision to all future commands from this agent
```

Help the user review the agent and program, variable name, project and worktree, reason, and command. The secret value is never displayed. **Allow once** is the best default for a first expected request. The 24-hour and always choices remember an expected use; the deny choices stop it temporarily or until revoked. Selecting **Apply my decision to all future commands from this agent** disables the one-request choices and broadens a remembered answer to future commands from that agent within the shown project and secret set. A newly requested secret set asks again.

## Run a command

Read the local Joydex profile instead of inspecting running processes or searching installations. Normal Joydex tray startup rewrites this value-free file at `%LOCALAPPDATA%\Joydex\profile.yaml`. It identifies the most recently started normal package and data root; it is not proof that the broker is still running. `--client` is a stable public requester label, commonly `codex`; it requires no prior configuration. `--project` is the project name shown in the toast and policy. Joydex binds it to the current working folder.

```powershell
$localData = [Environment]::GetFolderPath('LocalApplicationData')
$profilePath = Join-Path $localData 'Joydex\profile.yaml'
if (!(Test-Path -LiteralPath $profilePath -PathType Leaf)) {
    throw 'Joydex has not published its local profile. Start the normal Joydex tray app once.'
}
$profileLines = Get-Content -LiteralPath $profilePath
if (@($profileLines | Where-Object { $_ -match '^schema_version:\s*1\s*$' }).Count -ne 1) {
    throw 'The Joydex local profile schema is missing or unsupported.'
}
function Get-JoydexProfileScalar([string] $name) {
    $prefix = $name + ':'
    $fieldLines = @($profileLines | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) })
    if ($fieldLines.Count -ne 1) { throw "Joydex local profile field '$name' is missing or repeated." }
    $value = $fieldLines[0].Substring($prefix.Length).Trim()
    if ($value.Length -lt 2 -or $value[0] -ne "'" -or $value[$value.Length - 1] -ne "'") {
        throw "Joydex local profile field '$name' is malformed."
    }
    return $value.Substring(1, $value.Length - 2).Replace("''", "'")
}
$helper = [IO.Path]::GetFullPath((Get-JoydexProfileScalar 'secrets_cli_path'))
$application = [IO.Path]::GetFullPath((Get-JoydexProfileScalar 'application_path'))
$dataRoot = [IO.Path]::GetFullPath((Get-JoydexProfileScalar 'data_root'))
if ([IO.Path]::GetFileName($helper) -ine 'joydex-secrets.exe' `
    -or [IO.Path]::GetFileName($application) -ine 'Joydex.App.exe' `
    -or [IO.Path]::GetDirectoryName($helper) -ine [IO.Path]::GetDirectoryName($application) `
    -or !(Test-Path -LiteralPath $helper -PathType Leaf) `
    -or !(Test-Path -LiteralPath $application -PathType Leaf)) {
    throw 'The Joydex local profile does not identify a complete Joydex package. Start the current Joydex package to refresh it.'
}

& $helper exec `
  --data-root $dataRoot `
  --client codex `
  --project my-project `
  --reason 'Send the requested message' `
  --secret AGENTMAIL_API_KEY `
  --fingerprint '.\message.json' `
  -- agentmail.exe send --draft '.\message.json'
```

For mutable files that control the approved action, include each script, configuration, or draft with `--fingerprint PATH` before `--`. Repeat the option for multiple inputs. Relative paths resolve from the caller's working directory. Their contents become part of exact-operation approval, so changing a fingerprinted file requires a new decision. Joydex holds these files read-only for the child's lifetime, including detached execution. Explicit agent-wide grants still authorize future operations in their approved scope.

Repeat `--secret` when a command needs more than one variable. The child runs in the caller's current directory with its inherited environment; Joydex-approved values replace matching variable names. By default, the helper streams child stdout and stderr directly, forwards stdin, and returns the exact child exit code. It adds no success message or JSON envelope. Output is not truncated or redacted, so use commands that do not print credentials. Normal shell output redirection works. These are byte streams, not a terminal emulator.

Use `--output-mode json` before `--` only when structured results are needed. That mode redacts Joydex-injected values, not unrelated values inherited from the caller, and includes `secretsInjected`. Wrapper failures are reported on stderr in default mode.

If the public name is uncertain, list value-free names first. This also prepares the same local requester identity without asking for secret access:

```powershell
& $helper aliases --data-root $dataRoot --client codex --project my-project
```

## Timeouts

The normal approval timeout is 90 seconds and cancels safely. Override it from 1 to 110 seconds with `--approval-timeout SECONDS`.

`--on-approval-timeout run-without-secrets` detaches the approval and starts the command with the caller's environment but no Joydex-injected values. An inherited variable may already have a requested name. `--on-approval-timeout leave-pending` exits without starting the command and leaves a short policy-only popup. That popup cannot revive the old command; its remembered choices affect future requests only.

Attached execution is the default for ordinary commands. Its entire process tree stops when the caller disconnects, Joydex exits, or the execution timeout expires. Use `--execution-timeout SECONDS` (1–3600) to shorten its one-hour limit.

## Long-running tasks

Use `exec --detach` only for a long-running server or job the user has authorized. It asks for separate approval: existing attached approvals do not authorize detached execution. The toast explains that the task survives caller and Joydex exits.

```powershell
$launch = & $helper exec --detach --data-root $dataRoot `
  --client codex --project my-project --reason 'Run the requested background job' `
  --secret AGENTMAIL_API_KEY -- worker.exe --run | ConvertFrom-Json
$taskId = $launch.taskId
& $helper status --data-root $dataRoot --task $taskId
& $helper stop --data-root $dataRoot --task $taskId
```

The CLI reports the stable task ID on stderr before submission and returns a JSON receipt after launch. Save the ID and the receipt's stdout/stderr log paths. Exit code zero confirms launch only; separately check the application's health or expected output before declaring it ready. Do not use `--output-mode` with `--detach`, or either alternative approval-timeout action.

Detached tasks have no timeout by default. Set `--execution-timeout SECONDS` (1–2,147,483) when a deadline is wanted. Each task has one hidden helper launched through the existing Explorer desktop. There is no fallback when Explorer is unavailable. The helper owns the child and all descendants; helper failure, explicit stop, timeout, or root-process exit terminates the remaining tree. These tasks survive caller and Joydex exits, but do not recover after logout or reboot.

Stdin is closed. Separate stdout and stderr logs rotate at 10 MiB with three files retained per stream. Logs are not guaranteed to redact credentials printed by the application: avoid commands that print secrets. Use the receipt's paths to inspect task output with a reader that permits concurrent writes and rotation.

If the launch reply is lost or reports `unknown`, check `status --task` with that same ID before considering another launch. Never automatically retry an uncertain launch. A missing helper without a final receipt means unknown, not successful completion. Status and stop work with Joydex closed; repeated stop is harmless. A stopped task does not restart automatically. Approval changes govern future launches; denying future access cannot remove a key already delivered to a running task. Use explicit stop for that task.

## Result statuses

These describe broker failures and opt-in JSON results. A normally completed passthrough command produces only its own output and exit code.

| Status | Meaning |
|---|---|
| `completed` | The command finished. Report its child exit code and relevant command output. |
| `denied` | The user, a remembered rule, or the default timeout stopped this request. |
| `agentDetached` | The helper stopped waiting. The old command cannot later receive secrets or run through this request. |
| `expired` | Joydex's two-minute review window ended before the request was resolved. Nothing ran and no secret was delivered. |
| `revoked` | Approval or requester access is no longer valid, or an execution timeout stopped the child. |
| `identityUnverified` | Joydex could not validate the helper's protected local identity and current project folder. |
| `providerUnavailable` | A requested public name or its configured source is unavailable. |
| `queueFull` | Joydex has too many requests awaiting review. |
| `launchUnconfirmed` | Joydex cannot prove whether a timed-out child stopped. Do not retry it. |

The lower-level `request`, `wait`, `cancel`, and `run` commands and operation files remain available for advanced integrations. Saved recipes are optional automation plumbing and do not appear in the normal Secrets UI.
