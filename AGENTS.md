# Project instructions

Defer to `%USERPROFILE%\.agents\AGENTS.md`.

## Concepts and architectural decisions

Before changing domain language or architecture, review the applicable `CONCEPTS.md` and the ADRs in `docs/adr/`. These files may live at the repository root or at the root of the relevant project, app, or service. Keep them current when concepts are resolved or durable architectural decisions are made.

## Codex App compatibility

The Codex command IDs, Windows default bindings, and keybinding precedence behavior in this repository were last validated on 2026-08-05 against:

- Windows package: `OpenAI.Codex 26.730.8199.0`
- Bundled app release: `26.730`
- Codex build: `0.147.0-alpha.1.2`

Any change to command IDs, default bindings, aliases, or precedence behavior must be revalidated against the installed Codex App for Windows. Update all three version values and the validation date in this file with that change.

Runtime code must never inspect or parse `app.asar`; compatibility is maintained through verified catalogs and tests.
