# Codex Windows 26.930 dictation validation — October 2, 2026

This note records the compatibility repair for Joydex's in-app throttle dictation after the installed
Codex app updated from the 26.928 line.

| Baseline | Verified value |
| --- | --- |
| Installed Windows package | `OpenAI.Codex 26.930.2377.0` |
| Bundled app | `26.930.21537` |
| Bundled Codex CLI | `0.159.0-alpha.12.1` |
| SDK used for Joydex | `8.0.423` |

## Finding

Joydex continued to receive and execute all T4 press and release events, but Codex ignored the
injected `Ctrl+Shift+D` hold. Static inspection of the exact installed package showed that
`composer.startDictation` and its Windows default were removed from the command catalog. Exact-build
validation also found the registered global dictation commands capability-gated, so changing T4 back to
`globalDictationHold` would not repair this installation.

The visible composer continues to expose an enabled Windows accessibility button named `Dictate`.
During startup and recording, that same control exposes `Starting dictation; click to cancel`,
`Preparing dictation`, or `Stop dictation`. A read-only accessibility probe found the foreground
button as an enabled, on-screen Button with `InvokePattern` support.

`openOrbit` remains the only explicit Dot command in the inspected catalog and keeps its Windows
default of `Ctrl+.`. `openAvatarOverlay` opens the Mini surface with the global `Win+Alt+P` default.
Voice remains under the generic `realtimeVoice` command family; there is no separate Dots Voice
command or shortcut in this build.

## Repair

`in-app-push-to-talk` now invokes the foreground composer's accessible `Dictate` button and records
that Codex window for the physical control that started the hold. Release bypasses the foreground
check and invokes the stop or startup-cancel state in the recorded window. Held-input cleanup uses
the same recorded target. The existing global `push-to-talk` key-hold path is
unchanged.

The older `dictation` action also starts through the accessible composer button instead of resolving
the removed command. Product code does not inspect or parse `app.asar`; the archive was used only for
this exact-build compatibility research.

## Evidence

- The focused dictation-control, executor, keybinding, starter-profile, validation, binding-engine,
  and worker suite passed 159 tests with no failures or skips, including immediate-release startup
  settling and retained-owner retry coverage.
- A live smoke check invoked `Dictate` through the new production control, observed the button change,
  and invoked `Stop dictation` in the same recorded window successfully.
- The starter profile and public examples map CM3 button 37 press and release to
  `in-app-push-to-talk`.
