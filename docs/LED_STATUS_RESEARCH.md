# Joydex task-status LEDs and alert routing

Status: implementation updated on 2026-07-18 for VIRPIL Controls LinkTool v3. Automated tests pass. The Windows Desktop `UserPromptSubmit` canary passes on the updated Codex package. The generated 50-rule LinkTool profile passes the full M2 white/yellow/green/red, Alpha, bank-baseline, and clear canary without flicker or a device-wide color reset. M2 B1 also passes physical interception and deep-link navigation. A hardware canary verified that a running assignment stays white and continues routing after successful navigation; completed and fault assignments acknowledge and restore immediately. The unchanged hook relay remains functional, but its p95 latency gate needs an idle-machine recheck after exceeding 25 ms under system load. Other-bank routing and overflow canaries are still pending.

The Codex behavior was checked against the installed Windows package `OpenAI.Codex 26.715.3651.0`. The current hardware work uses VIRPIL Controls LinkTool v3.0 and the firmware/toolset installed on 2026-07-18. Earlier quick-utility experiments used VPC Software Suite `20220720`.

## Hardware canary

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

## Final design: a four-channel dynamic pool

The earlier six persistent session slots were dropped. They required a calibration workflow and would have tied physical buttons to chats long after those chats stopped mattering.

Joydex now keeps a runtime pool on B1, B2, B4, and B5. Any Codex task can claim a free channel. Assignments use the lowest free B-position. Another event from the same task updates its existing channel.

Overflow is intentionally lossy. When all four channels are occupied, the event is dropped. There is no backlog and no preemption. A dropped task can claim a channel when it emits a later lifecycle event after one becomes free.

B3 and B6 are excluded from the pool. They receive bank-baseline rules only, so their profile colors remain visible as bank indicators and they never show task state.

The master enabled flag and selected pool channels live in `task-alerts.json` beside the normal Joydex configuration. Writes use a flushed temporary file followed by atomic replacement. Live task assignments are memory-only and start empty after a restart.

## Codex lifecycle hooks

Joydex installs only three handlers:

| Hook | State |
| --- | --- |
| `UserPromptSubmit` | Running, steady white `FF FF FF` |
| `PermissionRequest` | Approval needed, steady yellow `FF FF 00` |
| `Stop` | Completed after a one-second continuation grace, steady low green `00 40 00` |

Red `FF 00 00` is reserved for a future fault source. Hooks do not reliably distinguish a failed task from a completed one, so the current hook path never invents a failure state.

The one-second Stop grace prevents a green flash when another handler or automatic continuation starts a new turn. A later running or approval event for that session cancels the pending completion.

Running assignments expire after 12 hours. Approval, completed, and fault assignments expire after 24 hours. These leases recover a channel after a lost event without creating a short timer that interrupts real work.

`SessionStart` and `PostToolUse` were left out. They add relay launches without supplying a state needed by this design.

### Installed Windows Desktop compatibility finding

The earlier `OpenAI.Codex 26.715.2305.0` package bundled `codex-cli 0.145.0-alpha.18`. Its app-server hook catalog reported all three Joydex handlers as enabled and trusted, with no discovery warnings or errors, but a temporary relay probe saw `Stop` launch while `UserPromptSubmit` never reached the relay. Direct invocation through the exact Windows `%COMSPEC% /C` command path succeeded, which isolated the failure to Codex's hook launch path. This matched the upstream Windows Desktop regression reported in [openai/codex#33564](https://github.com/openai/codex/issues/33564).

After updating to `OpenAI.Codex 26.715.3651.0`, the same metadata-only probe captured two consecutive real `UserPromptSubmit` invocations with session and turn fields present, and both named-pipe writes reached Joydex. The handler's outer timeout remained one second throughout this canary. The updated package still bundles `codex-cli 0.145.0-alpha.18`, so the observed fix is associated with the Windows Desktop package rather than a CLI version change. The supported three-hook design now works without a `PreToolUse` fallback.

### Relay protocol

`Joydex.HookRelay.exe` is a NativeAOT `win-x64` executable. Its source-generated streaming parser materializes only `hook_event_name`, `session_id`, and `turn_id`. Prompt text, tool input, transcript paths, and assistant messages are skipped instead of being retained in the relay.

For supported events it writes one compact JSON object to `Joydex.TaskAlerts.v1`:

```json
{"event":"PermissionRequest","sessionId":"...","turnId":"...","receivedAtUnixMs":1784300000000}
```

The relay makes one immediate named-pipe open inside a 20 ms connection budget and never retries. It uses `CreateFileW` directly because `NamedPipeClientStream.Connect(0)` still waits for a Windows pipe-default timeout when every instance is busy. A missing or busy Joydex instance is a successful no-op. `Stop` writes the protocol-required `{}` response to stdout. The other two hooks write nothing. Every path exits with code zero.

The long-running receiver uses `PipeOptions.CurrentUserOnly`, accepts simultaneous pipe clients, and rejects messages over 16 KiB. It places validated events onto one reducer queue. It does not wait for routing or VIRPIL output.

Normal-payload benchmarks produced:

| Receiver state | Samples | p50 | p95 | Maximum |
| --- | ---: | ---: | ---: | ---: |
| Absent, 8 KiB payload | 160 | 13.23 ms | 16.57 ms | 44.45 ms |
| Present | 100 | 13.42 ms | 18.31 ms | 26.79 ms |
| All instances busy | 100 | 12.65 ms | 14.88 ms | 49.10 ms |

The required gate is p95 at most 25 ms and maximum at most 75 ms.

Hooks were not installed during that benchmark.

### Installation and removal

The Task Alerts window has `Install / Repair hooks` and `Remove hooks` controls. Installation appends one marked Joydex handler to each supported event. Existing ErrorHelp, request-timer, and unknown handlers remain byte-for-byte equivalent JSON values, although formatting may change when the file is rewritten.

Repair removes stale handlers carrying Joydex's marker and writes the current absolute relay path to both `command` and Codex's Windows-specific `commandWindows` field. Paths without whitespace are left unquoted so `%COMSPEC% /C` receives the executable name directly. Removal deletes only the marked handlers. The outer Codex timeout is one second; the relay's own connection deadline is much shorter.

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

An occupied channel intercepts both the press and matching release before Joydex's normal binding engine. The override works in every bank listed above. A channel that appears while its physical control is already held is armed only after release, which prevents a stray release-only action.

The 2026-07-18 VIRPIL firmware update changed this throttle's DirectInput identity to `LEFT VPC MongoosT-50CM3`, instance GUID `[machine-specific value omitted]`, and product GUID `81943344-0000-0000-0000-504944564944`. Joydex must be reconfigured after a firmware update that changes these identifiers; USB disconnect/reconnect with stable identifiers requires no reconfiguration.

The routed target is:

```text
codex://threads/<escaped-session-id>
```

Routing uses the existing foreground guard in explicit deep-link mode. A configured simulator in the foreground still blocks it. Dry-run mode logs the target without navigating.

A successful shell navigation preserves running and approval assignments because opening the task is not evidence that the turn ended or the approval was resolved. Their white or yellow status stays visible and the occupied button continues to route to that task. Completed and fault assignments are terminal, so successful navigation acknowledges them, clears their overlay, and returns the button to its normal binding. A blocked or failed navigation keeps every state assigned.

The button-map window paints active task overlays across every bank variant. The Task Alerts window shows each channel, state, color, session ID, and deep-link target.

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

- running, approval, completed, and fault rules for B1, B2, B4, and B5;
- a baseline rule for each of M1-M5 on all six throttle LEDs, ordered after the alert rules;
- four priority rules for the Alpha grip;
- no alert-state rules for B3 or B6.

LinkTool offers no leave-current behavior for LEDs without rules: its fallback choices are firmware defaults, one configured color, or off. The initial 40-rule profile therefore turned unruled B3 and B6 yellow on M2. Baseline-only rules are the explicit compromise that preserves their bank-indicator colors without allowing task assignment or routing on those buttons.

The Task Alerts window stores an explicit current bank, defaulting to M2. This keeps restoration deterministic during the first LinkTool release. Automatic physical mode detection remains a later canary because the selector's internal logical buttons overlap some shifted B-button numbers. Changing the selected bank sends a new atomic snapshot and makes LinkTool select the corresponding baseline rules.

The serialized output worker enforces a 250 ms minimum interval, suppresses identical snapshots, and coalesces changes to the latest complete state. A snapshot contains `JoydexBank`, the four B-channel states, and the highest-priority Alpha state. Clearing an assignment sends state `0` while retaining `JoydexBank`, so the lower baseline rule takes over immediately. Joydex never issues a device-wide restore in this backend.

An empty startup is hardware-silent. Joydex treats the persisted no-alert baseline as already satisfied and waits for a real alert, an explicit current-bank change, or cleanup of an alert it previously sent before publishing telemetry.

The worker checks for a UDP listener on port 4123. If LinkTool is closed or stopped, the tray status reports `LinkTool inactive`; the latest state remains pending and is sent when the listener appears. External VPC setup, Shift, LED, test, and analysis tools pause Joydex telemetry. Resume, device reconnect, and wake replay one complete snapshot.

The Alpha shows the highest active state:

```text
fault > approval > completed > running > profile baseline
```

On Windows suspend or session end, Joydex sends the no-alert snapshot when LinkTool is available. Resume rebuilds the current reducer snapshot. USB reconnect requests the same replay.

Runtime output is limited to temporary LED commands. Joydex does not flash firmware, calibrate a device, write EEPROM, or edit a VPC profile.

### Guardian process

The packaged app starts `Joydex.Guardian.exe` once an alert overlay is active. The guardian is a crash-only fallback. If the parent disappears while an alert is active, it sends one partial UDP message that sets B1, B2, B4, B5, and Alpha states to zero. It deliberately omits `JoydexBank`, allowing LinkTool to retain the last baseline selection. A clean exit clears the state in the main process and signals the guardian to stop.

If LinkTool is already stopped during a crash, the firmware owns the LEDs and the lost UDP cleanup is harmless. A power loss can terminate both processes; reconnecting or power-cycling the devices remains the final recovery because Joydex never writes firmware, EEPROM, calibration, or the controller profile.

## App-server note

Codex app-server exposes richer turn results and approval state when a client owns its stdio transport. The desktop app's process has private standard streams and no supported shared listener, so Joydex cannot passively subscribe to that instance. Starting a second app-server would observe a different runtime.

These states resemble those shown by Codex Pets, but Joydex has no supported Pet integration.

## Code map

| Area | Main implementation |
| --- | --- |
| Reducer, leases, channel allocation | `src/Joydex.Core/TaskAlerts` |
| Press/release interception | `TaskAlertInputInterceptor` and `CompanionEngine` |
| Pipe receiver and coordinator | `src/Joydex.Windows/TaskAlerts` |
| Deep-link navigation | `TaskDeepLinkNavigator` |
| Hook-file merge/remove | `CodexHookManager` |
| LinkTool telemetry, profile generation, and conflict recovery | `LinkToolLedService` and `LinkToolTelemetry` |
| Crash cleanup | `src/Joydex.Guardian` |
| Native hook command | `src/Joydex.HookRelay` |
| Tray, status window, button-map overlay | `src/Joydex.App` |
| Packaged output | `scripts/Publish-Joydex.ps1` |

## Remaining operator canaries

Automated tests do not write to the controllers. Completed canaries include hook trust and delivery, B1-B6 LED addressing, Alpha output, the official-utility/direct-HID comparison, and the M2 LinkTool baseline/alert/clear sequence.

1. Press B1-B6 in M1 through M5 and confirm the logical ranges in the table.
2. Generate five task events. Confirm B1, B2, B4, and B5 fill in order, the fifth event is dropped, and a later event can claim a freed channel.
3. Load the generated 50-rule LinkTool profile, enable profile autoload and LinkTool autostart, then confirm white, yellow, low green, and the reserved red test color on each enabled throttle channel and the Alpha while B3 and B6 retain their bank colors. Completed on M2.
4. Press an alerting channel from several banks. Successful navigation must preserve running/approval status and free completed/fault status. Simulator-blocked navigation must leave every state assigned. M2 B1 navigation and running-state preservation are verified; the other states and banks still need operator canaries.
5. Exercise tray disable, clean exit, forced exit, USB reconnect, and sleep/resume.

Final acceptance means normal bindings return immediately after a terminal acknowledgement or disable, non-terminal assignments stay visible after navigation, both devices recover their profile colors, and Codex shows no visible delay attributable to Joydex.

## References

- [Codex lifecycle hooks](https://learn.chatgpt.com/docs/hooks)
- [Codex app-server protocol](https://learn.chatgpt.com/docs/app-server)
- [Codex desktop deep links](https://learn.chatgpt.com/docs/reference/commands#deep-links)
- [VIRPIL VPC Software Suite and LED controls](https://support.virpil.com/en/support/solutions/articles/47001249267-vpc-software-suite)
- [VIRPIL VPC Software Setup 20230328 release notes](https://support.virpil.com/en/support/solutions/articles/47001241573-vpc-software-setup-version-20230328-)
- [VLEDCONTROL direct-HID implementation](https://github.com/Nereid42/VLEDCONTROL)
- [Reverse-engineered VIRPIL LED feature-report layout](https://gist.github.com/charliefoxtwo/d294636e322402d1ea4a0f7b7e8aa52c)
