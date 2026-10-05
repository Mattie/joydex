# Use a bundled in-process PAD pilot

Status: accepted for PLUGINPILOT implementation, 2026-09-12.

Register the existing ESPHome wireless panel as `joydex.pad` through an explicit,
compiled-in catalog. PAD owns its adapter and task-status subscription independently
of the controller aggregate. This first extraction establishes a useful plugin
lifecycle without introducing dynamic loading or a public SDK.

PAD startup, unexpected completion and restart failures are reported as PAD health.
They do not end unrelated runtime owners. A replacement must wait for confirmed
cleanup of the prior generation; uncertain cleanup blocks PAD replacement. This is
ordinary managed failure containment. A fatal native or CLR failure in this trusted
in-process module can still end RuntimeHost.

Keep the current `WirelessPanel/panel.json` location, CurrentUser DPAPI credentials,
firmware commands and adapter transport reconnect behavior. Load panel configuration
at startup or through an explicit PAD reload. Applying unrelated Companion settings
must not adopt external panel-file edits or reconnect PAD. The active Companion
safety and action policy still governs PAD actions and must update coherently when
that shared policy changes.

Use injected transports and owners to verify lifecycle isolation in background tests.
Those tests do not replace physical PAD or Voice continuity checks. The user requested
continued implementation without further agent-driven desktop demos; uncompleted M2
checks remain explicit release evidence gaps.

See [ADR 0006](0006-independent-runtime-and-bundled-plugins.md), the
[PAD design](../design/joydex-plugins/04-pad-and-controllers.md) and
[milestone evidence](../design/joydex-plugins/12-milestone-demos.md).
