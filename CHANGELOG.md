# Changelog

This file records the major capabilities and fixes added since Joydex was first uploaded to GitHub. Entries are grouped by date because the project has not used versioned releases yet. New entries go at the top.

## 2026-09-08

- Added experimental Room Voice support for a dedicated Home Assistant Voice Preview Edition, including wake-triggered Codex conversations, full-duplex microphone and speaker transport, device mute and hangup controls, live transcript display, and per-session records.
- Added an optional dedicated Codex workspace and owned task for Room Voice. The working directory is organizational rather than a filesystem security boundary, and the task retains full-access/no-approval behavior.
- Added an experimental Desktop Task Bridge for sending explicit voice-authored follow-up prompts to a selected local Codex Desktop task. Failed deliveries are held for manual review.
- Added the source-only Voice PE firmware recipe, three Mattie Casper MIT-licensed cue recordings, reproducible upstream pins, and the GPL/Apache licensing boundary for the combined firmware.

## 2026-08-18

- Added an experimental Direct USB option for task-status LEDs on the CM3 throttle and Constellation Alpha. LED colors are configurable in the app, with recovery handling for alerts, device changes, Windows resume, and unexpected exits.
- Changed maintained switches so one already on when Joydex starts must be turned off and on again before it triggers an action.

## 2026-08-17

- Added tray options to start Joydex and VIRPIL LinkTool at Windows sign-in.

## 2026-08-08

- Added centered workspace-name footers to the four bridge-v2 touchscreen task
  cards. Joydex sends only the display-safe working-directory folder name,
  updates changed labels without redrawing unrelated cards, and restores all
  labels after a panel reconnect.
- Added persistent TASK and WORKSPACE ignore rules for Codex task signaling.
  The tray status window can immediately remove one task, durably suppress all
  task IDs launched from one exact workspace, and re-enable either scope. The
  shared filter applies to both VIRPIL LEDs and the wireless Joydex pad.
- Separated the Task Alerts page tabs from suppression actions. Ignoring now
  uses a scoped **Ignore selected ▾** menu, while saved rules live in a
  dedicated **Ignored sources** manager with explicit re-enable controls.

## 2026-08-06

- Added a local TASK CONTROLS touchscreen page with APPROVE, DECLINE, NEW TASK,
  FORK, PREV, SUBMIT, and NEXT actions. Page arrows are handled entirely by
  ESPHome, the last forward arrow remains disabled until a third page exists,
  and the panel returns to the task page after reboot.
- Added an opt-in bridge-v2 touchscreen firmware with five wider bottom controls for Plan Mode, Fast Mode, Side Chat, Voice Chat microphone mute, and the forward-navigation foundation. The original neutral and bridge firmware remain available as rollback configurations.

## 2026-08-05

- Added a Voice Chat microphone mute toggle action backed by Codex's current `realtimeVoice.toggleMicrophoneMute` command, verified to mute and unmute an active call on physical VIRPIL hardware.
- Verified the experimental maintained Voice Chat switch on physical VIRPIL hardware: switching on opens Voice Chat, and switching off ends the active call.

## 2026-07-27

- Added an experimental direct ESPHome touchscreen example for the
  ESP32-4848S040C_I, including neutral and bridge-console skins, live task
  state, four task controls, PLAN MODE, authenticated REST/SSE transport,
  DPAPI-protected host configuration, physical-device documentation, and
  recovery guidance.

## 2026-07-25

- Promoted the earliest M1 overflow task into any newly empty M2-M4 primary position after a five-second dark pause, with the remaining overflow tasks compacted in their existing order.
- Added experimental live Voice Chat controls for starting and explicitly ending a call, including maintained-switch bindings.

## 2026-07-24

- Kept the physical task monitor focused on real Codex sidebar tasks. The [hook relay](src/Joydex.HookRelay/Program.cs) now ignores delegated agents identified by `agent_id` and internal ephemeral sessions that have no persistent `transcript_path`.
- Changed the generated [LinkTool LED profile](src/Joydex.Windows/TaskAlerts/LinkToolTelemetry.cs) so empty task positions on M2-M4 stay dark, B3 and B6 retain their normal bank colors, and all six M5 buttons use a medium-pink baseline.
- Expanded the automated coverage and LED documentation for the new filtering and baseline behavior.

## 2026-07-21

- Reworked the WinForms dialogs around a shared Joydex theme, clearer grouping, and updated screenshots.
- Hardened configuration, prompt-picker, task-alert, dry-run, and button-map windows across DPI changes and repeated open/close cycles.
- Clarified setup, licensing, trademarks, LED support, and the source-project purpose in the README and guides.

## 2026-07-20

- Added multi-controller support with device-qualified bindings, independent reconnect behavior, and separate floating button maps.
- Added three configurable prompt pickers with controller navigation, default prompts, optional submission, and a non-activating overlay.
- Persisted task assignments across Joydex restarts and added privacy-preserving attention correlation for parallel tool activity.
- Added complete CM3 and CM3-plus-Alpha example configurations, the project case study, and a shorter LED setup guide.
- Reorganized the tray menu around connected controllers and their individual status.

## 2026-07-18

- Added Codex task-status LEDs through command hooks, a NativeAOT relay, VIRPIL LinkTool telemetry, and a guardian process that clears stale LED state after a crash.
- Expanded the task monitor to ten stable slots: four primary positions across M2-M4 and six overflow positions on M1, with the Alpha grip showing the highest-priority state.
- Followed the throttle's physical M1-M5 selector through VIRPIL's read-only Software Link report.
- Added task deep links, terminal-state acknowledgement, active-task preservation after navigation, diagnostics, generated LinkTool profiles, and hardware canaries.

## 2026-07-17

- Added the CM3 button-map template attribution and the repository's third-party notices.

## 2026-07-16

- Published the first source release on GitHub as VIRPIL Codex Pad. It included buffered DirectInput handling, configurable hardware bindings, foreground and simulator safety checks, dry-run inspection, a floating CM3 map, a tracing utility, tests, and CI.
- Replaced fixed shortcut assumptions with Codex command IDs resolved from the user's current keybindings, including support for chords, sequences, held modifiers, conflict detection, and injected-key cleanup.
- Renamed the project and application to Joydex, refreshed the example configuration, and added generated documentation screenshots.
