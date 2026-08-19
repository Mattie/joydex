# ADR 0001: Selectable direct VIRPIL LED output

- Status: Accepted
- Date: 2026-08-18

## Context

Joydex originally delegated task-status LED ownership to VIRPIL Controls LinkTool over UDP. That
kept LED output dependent on a separate process, generated profiles, and a local listener. CM3
testing suggested that explicit bank baselines provide the most predictable alert-clear behavior.

Joydex already uses JSON task-alert preferences, HidSharp, a read-only VIRPIL bank monitor, and a
crash guardian. Reusing that configuration and recovery machinery keeps both LED backends aligned.

## Decision

Joydex supports two persisted task-alert LED backends:

- `linkTool` retains the existing UDP/profile integration;
- `directHid` uses an experimental volatile LED interface on the configured CM3 throttle and
  Constellation Alpha.

Both backends consume the same versioned `task-alerts.json` palette and five explicit six-LED
throttle baselines. Direct throttle output never uses firmware fallback during ordinary operation.
Alert clear, task-alert disable, bank change, pause, and clean exit send the complete no-alert
baseline. The Alpha can either use a configured idle color or return to its firmware-managed idle
state.

The device adapter and HidSharp transport live in `Joydex.Virpil` so the main Windows runtime,
read-only bank monitor, and NativeAOT guardian share device identity and operation locks. Direct
writes are blocked while LinkTool or a VPC writer process is detected. Joydex records output as
accepted only after the operating-system write succeeds and avoids resending unchanged state.

## Consequences

- Direct mode has no LinkTool process, UDP listener, or generated-profile runtime dependency.
- The LinkTool backend remains available as a UI-selectable compatibility path.
- LinkTool remains the default while Direct USB completes physical recovery and lifecycle validation.
- Direct mode is intentionally limited to the attached CM3/Alpha identities; AUX/control-panel
  compatibility is not inferred.
- All six throttle baseline colors are user-configurable and deterministic. Joydex does not attempt
  to read arbitrary firmware/profile colors.
- Switching to direct mode requires both configured devices and disables Joydex's LinkTool
  login-startup entry. Joydex never terminates an external writer process itself.
- Guardian recovery uses a session-bound, versioned recovery document containing safe fallback
  output. Malformed or stale recovery data is rejected before hardware output.
- Firmware, EEPROM, calibration, and VPC controller profiles remain untouched.

## Evidence

- Automated device-adapter and service tests
- Production host writes on the attached CM3 and Alpha
- CM3 persistence and alert-clear canaries
