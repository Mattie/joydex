# Project instructions

Follow this file when working in the repository. A contributor may also have
personal agent guidance outside the checkout; treat it as supplemental and do
not make the public project depend on it.

## Concepts and architectural decisions

Before changing domain language or architecture, review the applicable `CONCEPTS.md` and the ADRs in `docs/adr/`. These files may live at the repository root or at the root of the relevant project, app, or service. Keep them current when concepts are resolved or durable architectural decisions are made.

## Codex App compatibility

The Codex command IDs, Windows default bindings, and keybinding precedence behavior in this repository were last validated on 2026-08-05 against:

- Windows package: `OpenAI.Codex 26.730.8199.0`
- Bundled app release: `26.730`
- Codex build: `0.147.0-alpha.1.2`

Any change to command IDs, default bindings, aliases, or precedence behavior must be revalidated against the installed Codex App for Windows. Update all three version values and the validation date in this file with that change.

Runtime code must never inspect or parse `app.asar`; compatibility is maintained through verified catalogs and tests.

## Windows ESPHome firmware builds

Before compiling Voice PE ESPHome firmware on Windows, prepend `C:\Program Files\Git\usr\bin` to that process's `PATH` and verify that `patch.exe` exists. The pinned `micro-opus` component invokes `patch.exe` by name.

## .NET SDK

This repository pins .NET SDK 8.0.423 in `global.json`. Use a matching SDK for
restore, build, test, run, and publish commands. A workstation may keep an
untracked repository-local copy at `.tools\dotnet\dotnet.exe`.

Do not change `global.json` or substitute another SDK version to work around SDK resolution.
