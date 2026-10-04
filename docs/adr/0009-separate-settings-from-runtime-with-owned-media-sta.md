# Separate settings from the runtime with an owned media STA

Status: accepted for PROCESSUI implementation, 2026-09-11.

Settings will connect to a per-user/session RuntimeHost that owns integrations and
configuration; closing or losing a settings connection releases only its client
resources. For this milestone, Voice remains a runtime-contained aggregate with
its own STA/message pump, which removes its dependency on ordinary UI lifetime
without introducing the additional Voice RPC and packaging boundary yet.

A fatal media/native failure can still terminate RuntimeHost. Independent Voice
process isolation remains part of VOICEPLUGIN; this decision establishes survival
of settings-process loss and does not claim survival of runtime-process loss.

The runtime is the sole application writer of settings. External file edits remain
pending candidates during a running session and cause stale Apply requests to
conflict; opening or cancelling settings cannot adopt them. This favors explicit
activation and draft protection over automatic live reload, while preserving the
existing validated-file load on runtime startup.

See the [PROCESSUI contract](../design/joydex-plugins/13-process-settings-contract.md)
and [milestone checks](../design/joydex-plugins/12-milestone-demos.md). The broader
plugin proposal in ADR 0006 and the accepted managed-runtime policy in ADR 0005
retain their scopes.
