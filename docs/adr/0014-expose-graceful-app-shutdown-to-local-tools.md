# Expose graceful App shutdown to local tools

Status: accepted, 2026-09-22.

Updated 2026-09-23: `Joydex.App.exe --restart` reuses the shutdown request, then
launches the same package with the active tray's configuration path. It confirms
the new tray has connected to RuntimeHost through a read-only control-pipe probe.
When no other Joydex App or runtime process is running, it starts the exact
`application_path` and `configuration_path` recorded in the local profile. The
command checks that its own executable matches the profile before shutdown. A
running Joydex instance that cannot answer the control request blocks relaunch
to avoid a competing instance. Exit code `4` reports an invalid profile or
launch failure; `5` means the new tray did not connect before the deadline.
The probe confirms the App and
RuntimeHost connection; optional plugin health remains a separate check.

The normal Joydex tray has no main window. Local tools need a way to request its
**Exit Joydex** operation without controlling the notification area or terminating
the process tree. `Joydex.App.exe --shutdown` now connects to a current-user named
pipe served by the connected production tray. The pipe name is scoped to the
Windows user, session, and exact packaged App path. Demo and settings-only
processes do not serve it.

The tray acknowledges the request with its process identity, then runs the same
exit method as the tray item. The command verifies that identity, waits for the
App to exit, and waits for the RuntimeHost ownership mutex to disappear before
returning success. Exit code `0` confirms shutdown, `2` means no matching tray
responded or its identity could not be verified, and `3` means shutdown did not
finish within the deadline. The command does not
launch Joydex; callers may start the `application_path` recorded in the local
profile after shutdown succeeds.

This uses a small App-owned control pipe because runtime RPC permits only the
connected tray to request runtime shutdown. Keeping the request at the tray
preserves its cleanup of settings, input, SecretsHost, and runtime ownership.
