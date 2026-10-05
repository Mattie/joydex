# ADR 0013: Keep device plugins with the runtime hardware owner

Status: accepted for DEVICEPLUGINS implementation, 2026-09-12.

Register `joydex.directinput` and `joydex.virpil` as bundled in-process plugins.
DirectInput retains the hidden cooperative window, Background/NonExclusive acquisition,
configured device identities, startup warmup and reconnect behavior. A bounded acquisition
queue separates physical polling from ordered binding resolution and asynchronous actions.
Snapshots and buffered edges belong to one acquisition generation. Queue exhaustion ends
that generation rather than silently losing button transitions.

Capture continues to wait for previously routed actions before it becomes active. Shutdown
and disconnect cancel and join dispatch before releasing that source's injected keys. A
replacement cannot start while cleanup remains uncertain. Another source's shared hold
remains owned independently.

VIRPIL shift reads, direct HID output, LinkTool output, device-change recovery and Guardian
remain one hardware aggregate inside RuntimeHost. Their existing process-local HID locks
therefore retain their meaning. Guardian watches RuntimeHost, outside its termination job,
and a confirmed clean shutdown follows successful hardware cleanup. Uncertain cleanup
retains the recovery record and prevents overlapping replacement ownership.

The existing settings Apply transaction remains authoritative for controller profiles and
LED backend changes. Generic plugin restart/reload commands cannot bypass it. Inspection
reports lifecycle health without acquiring hardware; a running plugin does not prove a
physical device is connected or that an LED write succeeded.

This extraction retains paths, schemas, bindings and firmware. In-process native failures
can still terminate RuntimeHost. Guardian restores LED/LinkTool state; hard-process-death
keyboard release remains an explicit acceptance gap.

See [device design](../design/joydex-plugins/04-pad-and-controllers.md) and
[ADR 0001](0001-selectable-direct-virpil-led-output.md).
