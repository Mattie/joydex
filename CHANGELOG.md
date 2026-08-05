# Changelog

This file records the major capabilities and fixes added since Joydex was first uploaded to GitHub. Entries are grouped by date because the project has not used versioned releases yet. New entries go at the top.

## 2026-08-05

- Verified the experimental maintained Voice Chat switch on physical VIRPIL hardware: switching on opens Voice Chat, and switching off ends the active call.

## 2026-07-27

- Added an experimental direct ESPHome touchscreen example for the
  ESP32-4848S040C_I, including neutral and bridge-console skins, live task
  state, four task controls, PLAN MODE, authenticated REST/SSE transport,
  DPAPI-protected host configuration, physical-device documentation, and
  recovery guidance.

## 2026-07-25

- Promoted the earliest M1 overflow task into any newly empty M2-M4 primary position after a five-second dark pause, with the remaining overflow tasks compacted in their existing order.
- Added experimental live Voice Chat controls for toggling and explicitly ending a call, including maintained-switch bindings.

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
