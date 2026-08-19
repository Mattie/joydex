# Joydex task-status LED guide

Joydex uses the CM3 throttle buttons and the Constellation Alpha grip LED as a physical task monitor for Codex. Codex lifecycle hooks supply the task state, Joydex assigns that state to an available task button, and either Joydex's experimental Direct USB backend or VIRPIL Controls LinkTool v3 keeps the matching LEDs lit.

## Set it up

1. Connect the CM3 throttle and Alpha grip, then start Joydex.
2. Open **Testing / Advanced > Task alerts / ignored tasks...**, choose **Configure LEDs...**, and select an output:
   - **Direct USB (experimental)**: close LinkTool and every VPC utility, then confirm the warning. Joydex drives the temporary LED state itself and disables its LinkTool login-startup entry.
   - **VIRPIL LinkTool**: use **Show LED profile** to locate `joydex-linktool.led.json`, load it in LinkTool, and start LinkTool's UDP listener on `127.0.0.1:4123`.
3. Use the same LED settings window to change the four task colors, all six baseline colors for M1-M5, or the Alpha idle policy. These settings are saved in `task-alerts.json` beside the normal Joydex configuration.
4. In the task-alert window, choose **Install / Repair hooks** and confirm the status reads `Hooks: installed`. If Codex marks the new handlers for review, open its Hooks screen and trust the Joydex handlers.
5. Make sure **Task alerts** is checked in the top level of the Joydex tray menu.
6. Submit a test prompt in Codex. Confirm that **Event stream** records it, **Current state** gains a running assignment, and the corresponding LED lights.

![Joydex task-alert status window at its compact supported size](images/joydex-task-alerts-compact.png)

## What the lights mean

| State | Color | Meaning |
| --- | --- | --- |
| Running | Dim white/gray | Codex is working on the task |
| Needs attention | Yellow | Codex needs permission, a safety decision, or explicit input |
| Completed | Low green | The task stopped and is ready to acknowledge |
| Fault | Red | Reserved for a future fault source; current hooks do not create this state |

The throttle provides ten task slots:

| Page | Controls | Behavior |
| --- | --- | --- |
| M1 | B1-B6 | Overflow slots 5-10; empty buttons are dark |
| M2-M4 | B1, B2, B4, B5 | Primary slots 1-4; empty task positions are dark, while B3 and B6 keep their bank colors |
| M5 | B1-B6 | Ordinary commands only; medium-pink baseline and no task overlays |
| Alpha grip LED | Global | Highest-priority state across all ten slots |

Each new task claims the lowest free primary slot, then the lowest free overflow slot. When a primary position becomes empty, it remains dark for five seconds before the earliest M1 overflow task moves into it. The open primary position stays reserved during that pause so a newer task cannot jump the overflow queue. Remaining overflow tasks then slide forward in their existing order, keeping M1 compact. When all ten slots are occupied, later events are dropped; a later lifecycle event can claim a slot after one becomes available.

Joydex ignores lifecycle events that Codex marks with a subagent `agent_id`. It also ignores events without a persistent `transcript_path`, which covers Codex's internal ephemeral sessions. Delegated and background-only agent threads therefore do not claim their own slots; the parent sidebar task remains the physical unit of attention.

## Ignore selected tasks or workspaces

Open **Testing / Advanced > Task alerts / ignored tasks...**, select a row under **Current state** or **Event stream**, then open **Ignore selected ▾** and choose one of these scopes:

| Scope | Match | Use it when |
| --- | --- | --- |
| TASK | One exact Codex session ID | Only that sidebar task should stop signaling |
| WORKSPACE | One normalized, exact working-directory path | New task IDs launched from the same dedicated folder should also stay quiet |

Adding either rule immediately removes a matching live assignment from the shared task pool, so both the throttle lights and wireless pad stop showing it. Later matching events remain visible in **Event stream** with the result `Suppressed` and do not claim a slot. **Ignored sources** opens a dedicated list of saved rules; **Re-enable selected** removes one, and the next lifecycle event for that task or workspace can then claim a slot normally.

WORKSPACE scope is intentionally exact-directory matching. It does not ignore parent directories, child directories, similarly named folders, or every task in a saved Codex project. This makes a dedicated folder such as a long-lived coordination chat a durable match without silencing unrelated tasks.

Codex lifecycle hooks do not provide the task title or Codex project name. Joydex therefore labels tasks with the stable task ID and shows the working-directory folder name as the human-readable workspace hint; bridge-v2 also shows that folder name in a centered task-card footer. WORKSPACE rules retain the full selected path, while only a display-safe folder name reaches the pad. Joydex does not parse transcripts, `session_index.jsonl`, the desktop app's private state database, or `app.asar` to infer names. Those persistence formats are not part of the hook contract. The supported Codex App Server can return `thread.name`, but the Windows Store app's bundled server is not an executable interface available to this standalone tray process, and an unrelated CLI installation may be missing or version-incompatible. Title enrichment remains deferred until Codex exposes a supported local endpoint or adds title metadata to hooks.

Pressing an assigned button opens its Codex task. Running and attention states stay assigned after navigation. Opening a completed task acknowledges it, clears the overlay, and returns that button to its normal binding. Blocked or failed navigation leaves the assignment alone.

## Restart and privacy behavior

Joydex saves active assignments, completion deadlines, pending-attention counts, and normalized workspace hashes in `%LOCALAPPDATA%\Joydex\task-alert-state.json`. Correlated attention and workspaces are stored as SHA-256 keys. Prompt text, commands, patches, tool responses, assistant messages, and raw workspace paths are never written to this file.

The enabled flag, fallback bank, LED backend and colors, and user-created TASK or WORKSPACE ignore rules live in `task-alerts.json` beside the normal Joydex configuration. A TASK rule stores the selected session ID. A WORKSPACE rule stores the exact normalized path because Joydex needs it for future matching and for the **Ignored sources** display. Ignore rules survive restarts and remain in place when the master **Task alerts** toggle is turned off.

Assignment state and queue order survive a Joydex restart. During restore, Joydex fills any primary gaps from overflow and compacts the remaining M1 queue. Running assignments expire after 12 hours; attention and terminal assignments expire after 24 hours. Invalid saved state is moved aside and Joydex starts with an empty pool. Turning **Task alerts** off clears the saved assignments.

## How LED output carries the state

Both backends keep the visible task and bank state synchronized when either one changes. The default baseline keeps primary task positions dark when empty, preserves the ordinary M2-M4 bank colors on B3 and B6, and paints all six M5 controls medium pink (`80 20 60` RGB).

Direct USB restores the configured bank baseline when an alert clears. This keeps the expected bank colors visible without requiring a mode-dial movement. CM3 testing suggests that explicit baselines are more predictable than asking the device to reconstruct the selected profile after host control ends.

Direct USB remains experimental. Production host writes have been exercised with the attached CM3 and Alpha, while complete visual Alpha/reset checks and the wider disconnect, sleep, sign-out, and crash-recovery matrix remain pending. LinkTool stays the default during that validation.

Joydex follows the physical M1-M5 selector through a read-only device status interface. Turning the dial updates the active LED page while preserving task states. Joydex does not flash firmware, write EEPROM, calibrate either device, or edit a VPC profile.

If Joydex exits cleanly, it applies the no-alert baseline before closing. If it crashes while an overlay is active, `Joydex.Guardian.exe` is designed to send either a final LinkTool clear snapshot or the precomposed Direct USB baseline. The saved assignments remain available for the next Joydex start.

## Troubleshooting

Open **Testing / Advanced > Task alerts / ignored tasks...** before reinstalling or changing anything. It shows the selected LED backend, detected bank, current assignments, dropped-event count, telemetry state, hook state, the last 100 lifecycle events, and saved ignore rules.

| Status or symptom | Check |
| --- | --- |
| `Hooks: repair needed` | Use **Install / Repair hooks** and verify the packaged relay path |
| `LinkTool inactive` | Start LinkTool and confirm its UDP listener is using port `4123` |
| `LinkTool update pending (VPC tool active)` | Close or release the VPC utility that currently owns the device |
| `Direct VIRPIL LED update pending` | Connect both devices and close LinkTool and every VPC utility; Joydex retries the newest desired state |
| A light appeared late | Compare the event's receive time with the telemetry update; a missing event points to hook delivery, while a received event isolates the delay inside Joydex or LinkTool |
| The wrong LED page is visible | Check that **Current bank** reports the physical selector position as automatic |
| A task never claims a slot | Check for `Suppressed` in **Event stream**, then open **Ignored sources** and re-enable its TASK or WORKSPACE rule if it is no longer wanted |

## LEDs in use

![Joydex task-status LEDs active on the CM3 throttle](images/20260720_064606c.jpg)

Here, agents 1, 3, and 4 are done (green), while agent 2 is running (white). A task waiting on me would be yellow. The blue buttons are ordinary controls I use often: Plan mode and Submit.

## References

- [Codex lifecycle hooks](https://learn.chatgpt.com/docs/hooks)
- [Codex desktop deep links](https://learn.chatgpt.com/docs/reference/commands#deep-links)
- [VIRPIL VPC Software Suite and LED controls](https://support.virpil.com/en/support/solutions/articles/47001249267-vpc-software-suite)
