# Joydex task-status LEDs: implementation research archive

> **Maintainer archive, recorded July 18-24, 2026.** This file preserves the experiments, compatibility findings, protocol details, benchmarks, and acceptance notes behind the LED integration. Dates and version numbers below describe the environment used for each finding. For current setup and behavior, read the [LED status guide](LED_STATUS.md).

**Archived status (July 18, 2026):** implementation updated for VIRPIL Controls LinkTool v3. The ten-slot reducer, bank-specific interception, 106-rule profile generation, M5 isolation, and legacy-settings migration pass automated tests. Hardware canaries pass for the complete M2 primary pattern, complete M1 overflow pattern, empty M1 off baseline, M5 baseline isolation, and Global Alpha across those banks. The Windows Desktop `UserPromptSubmit` canary passes on the updated Codex package, including a fresh real prompt after automatic bank detection was staged. The Task Alerts status viewer also reports the repaired canonical hooks as installed. Earlier canaries cover M2 deep-link routing, running-state preservation, and M2-M3-M2 bank following. The unchanged hook relay remains functional, but its p95 latency gate needs an idle-machine recheck after exceeding 25 ms under system load.

**Follow-up (July 23, 2026):** the installed `OpenAI.Codex 26.715.10079.0` package, with bundled CLI `0.145.0-alpha.30`, was observed sending a subagent's ephemeral thread ID through the ordinary task hooks. That release also supplies optional `agent_id` and `agent_type` fields on those hook payloads. Joydex now drops payloads carrying `agent_id`, which keeps delegated work out of the ten physical task slots. The generated 106-rule LinkTool profile also uses black baselines for empty M2-M4 task positions, preserves bank colors on B3/B6, and uses RGB `80 20 60` across M5. Automated generation tests cover these rules; the adjusted colors still need a hardware appearance canary.

**Follow-up (July 24, 2026):** the installed `OpenAI.Codex 26.721.3404.0` package, with active sessions reporting CLI `0.146.0-alpha.3`, generated an internal ephemeral thread for hyperpersonalized task suggestions. It ran ordinary hooks without `agent_id`, was absent from the persisted thread catalog, and rejected a turn read with `ephemeral threads do not support includeTurns`. In this release, ephemeral sessions skip thread persistence, so their hook payloads have `transcript_path: null`; persistent sidebar tasks materialize a transcript path before hooks run. Joydex now requires that path in addition to rejecting explicit subagent IDs.

The Codex behavior was checked against the installed Windows package `OpenAI.Codex 26.715.3651.0`. The current hardware work uses VIRPIL Controls LinkTool v3.0 and the firmware/toolset installed on 2026-07-18. Earlier quick-utility experiments used VPC Software Suite `20220720`.

## Hardware canary (historical)

The official `VPC_LED_Control.exe` changed temporary colors on both attached controllers:

| Device | USB identity | Verified output |
| --- | --- | --- |
| VPC MongoosT-50CM3 throttle | VID `3344`, current PID `8194` | LinkTool `LED 1` is B1 |
| R-VPC Stick WarBRD with Constellation Alpha grip | VID `3344`, current PID `40CC` | LinkTool `LED 1` is the grip LED |

Before the 2026-07-18 firmware update, the quick utility exposed these devices as PIDs `8197` and `00CB`. The historical command shape was:

```text
VPC_LED_Control.exe <vid> <pid> <led-id> <red> <green> <blue>
```

Color components use `00`, `40`, `80`, or `FF`. The yellow canary was `FF FF 00`:

```powershell
& $ledTool 3344 8197 5 FF FF 00
& $ledTool 3344 00CB 1 FF FF 00
```

LED ID `0` restores the active VPC profile colors for the whole device:

```powershell
& $ledTool 3344 8197 0 00 00 00
& $ledTool 3344 00CB 0 00 00 00
```

The throttle's restored color depends on its current bank. It is blue in some banks and green in others. Joydex must ask the device to restore its profile; a hard-coded "default blue" would be wrong.

The first blink experiment accidentally sent a device-wide yellow command. All six throttle lights changed. The corrected test addressed B1/ID `5` for both halves of the animation, and the operator confirmed that only B1 blinked and then returned to green. This established that device-wide restore commands were too destructive for normal alert cleanup; the LinkTool backend no longer uses them.

VIRPIL describes Quick LED Color Control as temporary test control. A one-shot B1 command from the official utility was visibly reclaimed by the device profile after roughly one to three seconds on M1. A one-shot direct-HID command behaved the same way. Repeated writes could keep an alert visible most of the time, although the profile still produced visible gaps and the utility launch loop could make the machine stall.

LinkTool v3 provides the missing durable rule engine. Joydex sends custom DCS-style JSON telemetry on `127.0.0.1:4123`. A high-priority `JoydexB1State == 1` rule held B1 solid white. A lower-priority `JoydexBank == 2` rule kept B1 blue when the alert cleared. The M2 canary produced this sequence:

1. Start LinkTool with `Reset LEDs before start` disabled: all six buttons stayed blue.
2. Send `{"JoydexBank":2,"JoydexB1State":0}`: the blue baseline rule matched and all six stayed blue.
3. Send `{"JoydexBank":2,"JoydexB1State":1}`: B1 became solid white while B2-B6 stayed blue.
4. Send `{"JoydexBank":2,"JoydexB1State":0}`: B1 returned directly to blue and no button reset to yellow.

This result replaces the direct-HID refresh loop. Joydex now emits one atomic telemetry snapshot per state change, and LinkTool maintains the steady output inside its own process. Blinking remains deferred.

## Final design: a ten-slot bank-aware pool

The earlier six persistent session slots were dropped. They required a calibration workflow and would have tied physical buttons to chats long after those chats stopped mattering.

Joydex keeps ten stable runtime slots. Primary slots 1-4 use B1, B2, B4, and B5 across M2-M4. Overflow slots 5-10 use all six B-positions on M1. M1 uses an off baseline, so unoccupied overflow slots are dark and the entire page is dark when no overflow task exists. M5 never displays or routes a task alert, leaving all six controls available for ordinary commands. The Alpha remains global and shows the highest state across all ten assignments on every bank.

Any Codex task claims the lowest free slot. Slots do not move when another slot becomes free, so a remembered physical target stays stable. Another event from the same task updates its existing slot. When all ten slots are occupied, later events are dropped without a backlog or preemption. A dropped task can retry on its next lifecycle event after a slot becomes available.

On M2-M4, B3 and B6 remain ordinary controls with baseline colors. On the dedicated M1 overflow page, they become overflow slots 7 and 10. An unoccupied slot always falls through to its ordinary binding.

The master enabled flag and fallback bank live in `task-alerts.json` beside the normal Joydex configuration. Earlier per-channel settings are accepted during migration and ignored because the bank layout is now fixed. Live assignments, completion deadlines, and pending-attention counts are saved separately in `task-alert-state.json`. Correlated attention is stored as SHA-256 keys; prompt text, commands, patches, and tool responses are never written to this file. Both stores use a flushed temporary file followed by atomic replacement.

When Joydex restarts with task alerts enabled, it restores the saved slot numbers and attention state, immediately expires stale leases, and resumes publishing the resulting LinkTool state. Invalid state is moved to a timestamped quarantine file so startup can continue empty. Disabling task alerts clears the saved assignments.

## Codex lifecycle hooks

Joydex installs five handlers:

| Hook | State |
| --- | --- |
| `UserPromptSubmit` | Running, steady dim gray `55 55 55` |
| `PermissionRequest` | Approval or safety decision observed, steady yellow `FF FF 00` |
| `PreToolUse` matching `^request_user_input$` | Explicit question or plan feedback needed, steady yellow `FF FF 00` |
| `PostToolUse` matching approval-capable tools | Matching attention request resolved; returns to running when none remain |
| `Stop` | Completed after a one-second continuation grace, steady low green `00 40 00` |

Red `FF 00 00` is reserved for a future fault source. Hooks do not reliably distinguish a failed task from a completed one, so the current hook path never invents a failure state.

The one-second Stop grace prevents a green flash when another handler or automatic continuation starts a new turn. A later running or approval event for that session cancels the pending completion.

Permission and explicit-input events carry a SHA-256 attention key derived from the session, turn, tool name, and canonical tool input. Joydex counts pending keys per task. A successful `PostToolUse` removes only its matching key, so an unrelated parallel tool cannot clear a real approval. The completion matcher covers `Bash`, `apply_patch`, `request_user_input`, and `mcp__*`; other tool families keep the existing fallback. Current Codex builds skip command hooks marked `async`, so this completion handler is synchronous and narrowly matched to limit added tool latency. If no key is available, the tool fails, or the action is declined, the task keeps the previous yellow-until-`Stop` behavior.

Running assignments expire after 12 hours. Approval, completed, and fault assignments expire after 24 hours. These leases recover a slot after a lost event without creating a short timer that interrupts real work.

`SessionStart` remains excluded because it does not supply a state needed by this design.

### Installed Windows Desktop compatibility finding (historical)

The earlier `OpenAI.Codex 26.715.2305.0` package bundled `codex-cli 0.145.0-alpha.18`. Its app-server hook catalog reported all three Joydex handlers as enabled and trusted, with no discovery warnings or errors, but a temporary relay probe saw `Stop` launch while `UserPromptSubmit` never reached the relay. Direct invocation through the exact Windows `%COMSPEC% /C` command path succeeded, which isolated the failure to Codex's hook launch path. This matched the upstream Windows Desktop regression reported in [openai/codex#33564](https://github.com/openai/codex/issues/33564).

After updating to `OpenAI.Codex 26.715.3651.0`, the same metadata-only probe captured two consecutive real `UserPromptSubmit` invocations with session and turn fields present, and both named-pipe writes reached Joydex. The handler's outer timeout remained one second throughout this canary. The updated package still bundles `codex-cli 0.145.0-alpha.18`, so the observed fix is associated with the Windows Desktop package rather than a CLI version change. The then-current three-hook design worked without a `PreToolUse` fallback.

### Relay protocol

`Joydex.HookRelay.exe` is a NativeAOT `win-x64` executable. Its source-generated parser reads `hook_event_name`, `session_id`, `turn_id`, `tool_name`, and `tool_input`. Tool input is canonicalized in memory only long enough to calculate the attention key. Raw commands, patches, prompts, tool responses, transcript paths, and assistant messages are never sent to Joydex or retained by the relay.

For supported events it writes one compact JSON object to `Joydex.TaskAlerts.v1`:

```json
{"event":"PermissionRequest","sessionId":"...","turnId":"...","attentionKey":"A SHA-256 hex digest","receivedAtUnixMs":1784300000000}
```

The relay makes one immediate named-pipe open inside a 20 ms connection budget and never retries. It uses `CreateFileW` directly because `NamedPipeClientStream.Connect(0)` still waits for a Windows pipe-default timeout when every instance is busy. A missing or busy Joydex instance is a successful no-op. `Stop` writes the protocol-required `{}` response to stdout. The other hooks write nothing. Every path exits with code zero.

The long-running receiver uses `PipeOptions.CurrentUserOnly`, accepts simultaneous pipe clients, and rejects messages over 16 KiB. It places validated events onto one reducer queue. It does not wait for routing or VIRPIL output.

The original lifecycle-payload benchmarks produced:

| Receiver state | Samples | p50 | p95 | Maximum |
| --- | ---: | ---: | ---: | ---: |
| Absent, 8 KiB payload | 160 | 13.23 ms | 16.57 ms | 44.45 ms |
| Present | 100 | 13.42 ms | 18.31 ms | 26.79 ms |
| All instances busy | 100 | 12.65 ms | 14.88 ms | 49.10 ms |

The required gate is p95 at most 25 ms and maximum at most 75 ms.

The benchmark now defaults to an 8 KiB correlated `PostToolUse` payload and accepts `-PayloadKind Lifecycle` for a no-hash baseline. A 2026-07-18 comparison while total CPU was sampled at 88-97% was intentionally treated as inconclusive: both the deployed relay and the staged no-hash lifecycle path missed the historical p95 gate. In the staged build, correlation added about 5 ms to median present-receiver latency, and every measured invocation remained below 152 ms. The five-second hook timeout provides substantial headroom, but the strict latency gate still needs a quiescent-host rerun before using those loaded measurements as a new baseline.

Hooks were not installed during that benchmark.

### Installation and removal

The Task Alerts window has `Install / Repair hooks` and `Remove hooks` controls. Installation appends one marked Joydex handler to each supported event. Existing ErrorHelp, request-timer, and unknown handlers remain byte-for-byte equivalent JSON values, although formatting may change when the file is rewritten.

Repair removes stale handlers carrying Joydex's marker and writes the current absolute relay path to both `command` and Codex's Windows-specific `commandWindows` field. Paths without whitespace are left unquoted so `%COMSPEC% /C` receives the executable name directly. Removal deletes only the marked handlers. The outer Codex timeout is five seconds to tolerate transient process-launch contention; the relay's own connection deadline is much shorter.

Codex may ask the operator to trust the new handler definitions. Joydex does not edit Codex's trust state.

## Physical button mapping

All five CM3 bank variants are known:

| Bank | B1 | B2 | B3 | B4 | B5 | B6 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| M1 | 38 | 39 | 40 | 41 | 42 | 43 |
| M2 | 56 | 57 | 58 | 59 | 60 | 61 |
| M3 | 62 | 63 | 64 | 65 | 66 | 67 |
| M4 | 68 | 69 | 70 | 71 | 72 | 73 |
| M5 | 74 | 75 | 76 | 77 | 78 | 79 |

An occupied slot intercepts both the press and matching release before Joydex's normal binding engine. Primary slots intercept their B-position only on M2-M4. Overflow slots intercept only their M1 B-position. M5 and every unassigned control pass through. A slot that appears while its physical control is already held is armed only after release, which prevents a stray release-only action.

The 2026-07-18 VIRPIL firmware update changed this throttle's DirectInput identity to `LEFT VPC MongoosT-50CM3` and product GUID `81943344-0000-0000-0000-504944564944`; the machine-specific instance GUID is intentionally omitted. Joydex must be reconfigured after a firmware update that changes these identifiers; USB disconnect/reconnect with stable identifiers requires no reconfiguration.

The routed target is:

```text
codex://threads/<escaped-session-id>
```

Routing uses the existing foreground guard in explicit deep-link mode. A configured simulator in the foreground still blocks it. Dry-run mode logs the target without navigating.

A successful shell navigation preserves running and approval assignments because opening the task is not evidence that the turn ended or the approval was resolved. Their white or yellow status stays visible and the occupied button continues to route to that task. Completed and fault assignments are terminal, so successful navigation acknowledges them, clears their overlay, and returns the button to its normal binding. A blocked or failed navigation keeps every state assigned.

The button-map window paints primary overlays on M2-M4, overflow overlays on M1, and none on M5. The Task Alerts window separates primary and overflow slot labels and shows each control, state, color, session ID, and deep-link target. Its Event stream retains the last 100 received lifecycle events in memory with receive time, reducer result, slot, state, session ID, and turn ID. The header shows the physical bank, dropped-event count, and the exact primary, overflow, and Alpha telemetry values. This distinguishes a delayed Codex hook from a received event that Joydex dropped or reduced unexpectedly.

## Tray master switch

`Task alerts` is checked by default and persisted. Turning it off performs the safety-critical work in this order:

1. Disable routing and reject later hook events.
2. Clear all pool assignments and queued desired LED values.
3. Request profile restoration on the throttle and Alpha.
4. Leave ordinary Joydex bindings running.

If a device is disconnected or a VPC utility owns the interface, the status menu shows `restore pending` and the LED worker retries. Hooks stay installed while the master switch is off. `Remove hooks` is available when zero relay launches are wanted.

Re-enabling begins with an empty pool. If Joydex has not written an alert overlay, toggling the master switch and starting the app issue no LED commands at all. This avoids disturbing a correctly rendered mode bank merely to establish a baseline.

## LinkTool state output and recovery

Joydex writes `joydex-linktool.led.json` beside its task-alert settings. The generated profile contains:

- primary state rules for B1, B2, B4, and B5, gated to M2-M4;
- overflow state rules for B1-B6, gated to M1;
- a baseline rule for each of M1-M5 on all six throttle LEDs, ordered after the alert rules, with M1 and empty M2-M4 task positions black/off, M2-M4 B3/B6 retaining their bank colors, and M5 medium pink;
- four priority rules for the Alpha grip;
- no task-state rule gated to M5.

The generated profile has 106 rules: 48 primary rules, 24 overflow rules, 30 bank baselines, and four global Alpha rules. LinkTool evaluates the task state and `JoydexBank` conditions together, which prevents primary state values from appearing on M1 or M5 and prevents overflow state values from appearing outside M1.

Joydex reads the throttle's current physical bank from VIRPIL's read-only software-link HID feature report. Report ID `4` contains the shift-channel mask in byte `2`; one active bit maps directly to M1-M5. The M2 hardware probe returned `0x02`. Empty, multi-channel, and out-of-range masks are ignored so a transient selector position cannot replace the last valid bank. The Task Alerts window shows the detected bank as automatic and retains the persisted M2 value only as a startup fallback if no valid report is available.

The bank monitor polls every 200 ms, matching LinkTool's own default Shift Link interval, but emits only a change. A physical dial change updates `JoydexBank` in the next atomic telemetry snapshot, so LinkTool selects the new baseline while preserving every active task state on the same B-position. This path only calls HID `GetFeature`; it does not send a feature report or alter controller configuration.

The serialized output worker enforces a 250 ms minimum interval, suppresses identical snapshots, and coalesces changes to the latest complete state. A snapshot contains `JoydexBank`, four primary states, six overflow states, and the highest-priority global Alpha state. Clearing an assignment sends state `0` while retaining `JoydexBank`, so the lower baseline rule takes over immediately. Joydex never issues a device-wide restore in this backend.

On startup, Joydex compares the first automatically detected bank with the persisted fallback. If they differ, it sends one zero-alert snapshot so LinkTool selects the physical baseline. If they match, duplicate suppression keeps startup hardware-silent. Later telemetry is limited to task-state or physical-bank changes.

The worker checks for a UDP listener on port 4123. If LinkTool is closed or stopped, the tray status reports `LinkTool inactive`; the latest state remains pending and is sent when the listener appears. Existing conflict, resume, and reconnect recovery remain best-effort safeguards, outside the requested v1 acceptance scope.

The Alpha shows the highest active state:

```text
fault > approval > completed > running > profile baseline
```

Runtime output is limited to temporary LED commands. Joydex does not flash firmware, calibrate a device, write EEPROM, or edit a VPC profile.

### Guardian process

The packaged app starts `Joydex.Guardian.exe` once an alert overlay is active. The guardian is a crash-only fallback. If the parent disappears while an alert is active, it sends one partial UDP message that clears all four primary states, all six overflow states, and Alpha. It deliberately omits `JoydexBank`, allowing LinkTool to retain the last baseline selection. A clean exit clears the state in the main process and signals the guardian to stop.

If LinkTool is already stopped during a crash, the firmware owns the LEDs and the lost UDP cleanup is harmless. A power loss can terminate both processes; reconnecting or power-cycling the devices remains the final recovery because Joydex never writes firmware, EEPROM, calibration, or the controller profile.

## App-server note (historical design constraint)

Codex app-server exposes richer turn results and approval state when a client owns its stdio transport. The desktop app's process has private standard streams and no supported shared listener, so Joydex cannot passively subscribe to that instance. Starting a second app-server would observe a different runtime.

These states resemble those shown by Codex Pets, but Joydex has no supported Pet integration.

## Code map (implementation snapshot)

| Area | Main implementation |
| --- | --- |
| Reducer, leases, slot allocation, and bank mapping | `src/Joydex.Core/TaskAlerts` |
| Press/release interception | `TaskAlertInputInterceptor` and `CompanionEngine` |
| Pipe receiver and coordinator | `src/Joydex.Windows/TaskAlerts` |
| Deep-link navigation | `TaskDeepLinkNavigator` |
| Hook-file merge/remove | `CodexHookManager` |
| Physical mode detection | `VirpilShiftModeReader` and `VirpilShiftModeMonitor` |
| LinkTool telemetry and profile generation | `LinkToolLedService` and `LinkToolTelemetry` |
| Crash cleanup | `src/Joydex.Guardian` |
| Native hook command | `src/Joydex.HookRelay` |
| Tray, status window, button-map overlay | `src/Joydex.App` |
| Packaged output | `scripts/Publish-Joydex.ps1` |

## Development acceptance canaries (historical)

Automated tests do not write to the controllers. Completed canaries include hook trust and delivery, B1-B6 LED addressing, Alpha output, the official-utility/direct-HID comparison, the M2 LinkTool baseline/alert/clear sequence, and automatic M2/M3 bank tracking while an alert is active.

1. Press B1-B6 in M1 through M5 and confirm the logical ranges in the table.
2. Generate eleven task events. Confirm primary slots 1-4 fill first, overflow slots 5-10 fill next, and the eleventh event is dropped. Freeing a primary slot must not move an existing overflow assignment; a later event can claim the free primary slot.
3. Load the generated 106-rule LinkTool profile. Confirm primary statuses appear on M2-M4, overflow statuses appear on M1, empty M1 slots are off, M5 shows only baseline colors, and Alpha shows the highest state globally. Completed for the M2 primary page, occupied and empty M1 overflow page, M5 isolation, and Global Alpha on 2026-07-18; M3/M4 reuse the verified primary rules and bank gating.
4. Press primary and overflow alerts from their intended banks. Successful navigation must preserve running/approval status and free completed/fault status. Buttons on other banks and simulator-blocked navigation must keep their expected assignment/binding behavior.
5. Exercise tray disable, clean exit, and forced exit.

Final acceptance means normal bindings return immediately after a terminal acknowledgement or disable, non-terminal assignments stay visible after navigation, both devices recover their profile colors, and Codex shows no visible delay attributable to Joydex.

## References

- [Codex lifecycle hooks](https://learn.chatgpt.com/docs/hooks)
- [Codex app-server protocol](https://learn.chatgpt.com/docs/app-server)
- [Codex desktop deep links](https://learn.chatgpt.com/docs/reference/commands#deep-links)
- [VIRPIL VPC Software Suite and LED controls](https://support.virpil.com/en/support/solutions/articles/47001249267-vpc-software-suite)
- [VIRPIL VPC Software Setup 20230328 release notes](https://support.virpil.com/en/support/solutions/articles/47001241573-vpc-software-setup-version-20230328-)
- [VLEDCONTROL direct-HID implementation](https://github.com/Nereid42/VLEDCONTROL)
- [Reverse-engineered VIRPIL LED feature-report layout](https://gist.github.com/charliefoxtwo/d294636e322402d1ea4a0f7b7e8aa52c)

![Joydex task-status LEDs active on the CM3 throttle](images/20260720_064606c.jpg)

Here, agents 1, 3, and 4 are done (green), while agent 2 is running (white). A task waiting on me would be yellow. The blue buttons are ordinary controls I use often: Plan mode and Submit.
