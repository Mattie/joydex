# Project instructions

Defer to `%USERPROFILE%\.agents\AGENTS.md`.

## Codex App compatibility

The Codex command IDs, Windows default bindings, and keybinding precedence behavior in this repository were last validated on 2026-08-04 against:

- Windows package: `OpenAI.Codex 26.727.6591.0`
- Bundled app version: `26.727.51351`
- Codex build: `0.146.0-alpha.9.2`

Any change to command IDs, default bindings, aliases, or precedence behavior must be revalidated against the installed Codex App for Windows. Update all three version values and the validation date in this file with that change.

Runtime code must never inspect or parse `app.asar`; compatibility is maintained through verified catalogs and tests.
