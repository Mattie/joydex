# Codex Windows compatibility

Static validation on 2026-10-06 used these installed versions:

| Component | Version |
|---|---|
| Windows package | `OpenAI.Codex 26.930.7945.0` |
| Bundled app | `26.930.61225` |
| Codex runtime | `0.160.1` |

The inspected `app.asar` SHA-256 was
`611D6DA979D8BBABFEC97DD90DCCE27A9522E7016E6CCF135D59CAB693AB08DA`.
Inspection was a development-time check. Joydex never reads that archive at runtime.

## Commands and bindings

The installed bootstrap command catalog contains 176 command IDs. Evaluating its Windows
default-binding helper for both sidebar and tab numeric layouts produced 105 commands with
defaults. The resulting inventory is checked in as
[`CodexWindows26930.json`](../src/Joydex.Windows/Actions/Compatibility/CodexWindows26930.json).
Every command mapped by Joydex is present in the installed catalog.

- `openSideChat` declares `Ctrl+Alt+S`.
- `toggleSidebar` declares `Ctrl+Shift+S` and `Ctrl+B`.
- Numeric task and tab shortcuts depend on the selected layout. Joydex therefore does not
  treat `Ctrl+1` through `Ctrl+6` as reliable task shortcuts.
- Previous/next task shortcuts overlap tab navigation. Joydex requires a distinct explicit
  binding, or a deliberately provisioned free chord, rather than assuming which scope wins.
- `composer.startVoiceMode` has no Windows default. Real native LASTVOICE starts remain
  unavailable because Joydex has no verified guarantee that native Voice targets the saved chat.
- `composer.startDictation` is absent. Foreground dictation continues through the accessible
  composer controls described in [the dictation validation](CODEX_WINDOWS_26_930_DICTATION_VALIDATION.md).

Relative to the previously preserved 26.928 inventory, the current default set removes the
obsolete composer dictation command and adds eight artifact-tool keys. The inventory is used
for conservative task-shortcut provisioning checks; it does not emulate renderer scope or
priority. Existing installs receive guidance instead of silently gaining new task shortcuts.
An explicit removal stays removed, and conditional entries remain conservative when Joydex
cannot prove the active context.

## Alias normalization

The installed `bootstrap-C8gUBg5L.js` normalization helpers (`Uz`, `fz`, and `Bz`) were evaluated
with the ten checked-in alias fixture inputs in an isolated JavaScript context. Every output
matched the fixtures, including canonical-name precedence, close-tab/window expansion, and
legacy dictation hold/single-tap migration. Provenance and expected outputs are stored in
[`CodexWindows26930Aliases.json`](../tests/Joydex.Tests/Fixtures/CodexWindows26930Aliases.json).

`CodexKeybindingServiceTests` compares Joydex normalization against those outputs and exercises
explicit overrides/removals, conditional collisions, task/tab ambiguity, both numeric layouts,
existing-install behavior, and provisioning collisions with unmapped upstream commands.

## Room Voice ownership

Room Voice acquires its Dedicated Voice Task only after wake and releases it during teardown.
History refresh uses `thread/read` without resuming the task. When Desktop holds an idle writer,
the narrow bridge archives and restores that same chat, then Joydex retries acquisition once.
Active tasks are not eligible for this handoff. The decision and earlier runtime evidence are
recorded in [ADR 0002](adr/0002-use-codex-app-server-realtime-for-dedicated-voice.md).

Focused tests cover history reads without writer acquisition, startup context, owner cleanup
after media failures, bounded handoff retries, cancellation while connecting, and return to Armed.
The firmware change lets a short center-button press cancel Starting and clear its waiting ring.
These automated and static checks do not constitute a new attended Voice PE or dictation canary
on this installed Codex build; historical physical evidence remains historical.
