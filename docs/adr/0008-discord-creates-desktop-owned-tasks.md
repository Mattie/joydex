# Discord creates Desktop-owned tasks in allowed projects

Status: proposed, 2026-09-08; refreshed and renumbered 2026-09-11.

Bind each authorized Discord thread only to a fresh task created by Joydex through Codex Desktop in a locally enrolled project. The shared gateway records creation provenance and enforces that binding for subsequent operations. No existing-task attachment, arbitrary task ID, project-path input or second App Server fallback is exposed.

This requires a verified external Desktop creation/project/observation capability and legitimate executor context. Our current bridge supports only status/list/read/send; its Voice and Pebble scopes remain constrained while Discord receives a separately validated capability. Lost creation/send acknowledgements remain unconfirmed and cannot trigger blind retries.

See the [Discord design](../design/joydex-plugins/06-discord.md). This proposal extends future bridge capabilities without changing current accepted ownership restrictions.
