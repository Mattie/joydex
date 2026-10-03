# Independent runtime and bundled plugins

Status: proposed, 2026-09-08; refreshed and renumbered 2026-09-11.

Keep Joydex's active integrations in an independent per-user runtime, and make settings a client with draft/apply semantics. Extract bundled plugins around existing device/media aggregates, with narrow shared action, task, configuration and lifecycle capabilities; use separate workers where media/UI or network failure needs independent lifetime. This preserves working behavior while allowing settings to remain open, and avoids committing the first plugin release to a visual-framework rewrite or a public plugin marketplace.

The Voice worker owns its own media STA/window. Visible UI lifetime never owns the dedicated Voice task or device handles. Trusted in-process modules remain possible for low-latency device work; neither a DLL loader nor a same-user worker is a hostile-code sandbox.

See the [plugin engine](../design/joydex-plugins/01-plugin-engine.md), [live settings](../design/joydex-plugins/02-live-settings-ui.md) and [agent implementation plan](../design/joydex-plugins/10-agent-orchestration.md). Existing accepted ADRs remain in force, including [ADR 0005](0005-follow-managed-codex-runtime-with-capability-checks.md) for managed Codex runtime discovery and capability checks.
