# Attributable secrets approvals

Status: proposed, 2026-09-08; refreshed and renumbered 2026-09-11.

Authorize secret use through a separate local broker using authenticated client identity, canonical project/worktree and an immutable operation scope. YES is single-use, YES_ALWAYS persists exact-scope allow, NO denies one request, and NEVER persists exact-scope deny. Start with registered named clients and a broker-read .env provider; enable verified Codex task attribution only when origin is supplied by trusted host context rather than caller text.

Keep raw values out of ordinary engine/UI events and prefer approved child-process injection. The initial same-user design provides cooperative consent and audit; it does not claim to isolate unrestricted tasks from accessible .env files or same-user DPAPI. This trades a small, useful experiment for postponing a substantially different OS/vault isolation design.

See the [secrets design](../design/joydex-plugins/07-secrets.md).
