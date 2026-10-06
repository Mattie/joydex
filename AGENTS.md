# Project instructions

Follow this file when working in the repository. A contributor may also have
personal agent guidance outside the checkout; treat it as supplemental and do
not make the public project depend on it.

## Concepts and architectural decisions

Before changing domain language or architecture, review the applicable `CONCEPTS.md` and the ADRs in `docs/adr/`. These files may live at the repository root or at the root of the relevant project, app, or service. Keep them current when concepts are resolved or durable architectural decisions are made.

## Codex App compatibility

The Codex command IDs, Windows defaults, and keybinding precedence behavior in this repository were statically revalidated on 2026-10-06 against:

- Windows package: `OpenAI.Codex 26.930.7945.0`
- Bundled app release: `26.930.61225`
- Codex build: `0.160.1`

This release removed `composer.startDictation`. Foreground throttle dictation invokes the accessible
composer `Dictate` button and returns to the recorded window's stop or startup-cancel button on release.
See `docs/CODEX_WINDOWS_26_930_DICTATION_VALIDATION.md` for the current dictation evidence.
See `docs/CODEX_WINDOWS_26_930_COMPATIBILITY.md` for the current command and alias validation.

Any change to command IDs, default bindings, aliases, or precedence behavior must be revalidated against the installed Codex App for Windows. Update all three version values and the validation date in this file with that change.

Runtime code must never inspect or parse `app.asar`; compatibility is maintained through verified catalogs and tests.

## Background test execution

Use explicitly audited test-class or method filters for unattended validation.
Tests that display native windows or tray icons use `AttendedFact` and are skipped
by default. Set `JOYDEX_RUN_ATTENDED_TESTS=1` only for an explicitly requested
attended test session. Building or reviewing those tests does not require opt-in.

## Windows ESPHome firmware builds

Before compiling Voice PE ESPHome firmware on Windows, prepend `C:\Program Files\Git\usr\bin` to that process's `PATH` and verify that `patch.exe` exists. The pinned `micro-opus` component invokes `patch.exe` by name.

## .NET SDK

This repository pins .NET SDK 8.0.423 in `global.json`. Use a matching SDK for
restore, build, test, run, and publish commands. A workstation may keep an
untracked repository-local copy at `.tools\dotnet\dotnet.exe`.

Do not change `global.json` or substitute another SDK version to work around SDK resolution.
