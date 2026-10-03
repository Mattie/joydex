# Joydex PAD, joystick/throttle and VIRPIL plugins

Status: proposed. “Joydex PAD” here means our existing wireless ESPHome touchscreen. Joysticks and throttles are separate physical input sources.

## Recommended split

| Plugin | Owns | Shares |
|---|---|---|
| joydex.pad | ESPHome panel transport, SSE parsing, panel projection, physical button protocol and panel preferences | Task-status snapshots, action execution, task navigation |
| joydex.directinput | Controller discovery/acquisition, polling, reconnect, device identity and input snapshots | Binding engine, action policy, input observation/capture |
| joydex.virpil | Direct HID and LinkTool LED backends, device shift reads, backend switching and hardware recovery | Task-status snapshots, device leases, Guardian supervision |

Extract PAD early because EspHomePanelAdapter already takes narrow dependencies. Extract DirectInput after settings observation/capture is established. Keep direct VIRPIL reads and writes in one ownership aggregate initially; their current locks are process-local.

Refresh the parity fixture from current main before extraction. The catalog now includes archive-chat and toggle-review; the starter CM3 E2 push uses toggle-voice-mic, T4 retains press/release PTT, and T6 down archives the chat. Preserve user-defined bindings exactly rather than applying new starter defaults to saved profiles. The catalog's validated Windows package/release/build/date in AGENTS.md remain the authority; an extraction alone must not change them.

## Joydex PAD behavior to preserve

The current adapter reads task state, renders four slots with bounded/sanitized workspace labels, consumes a fixed numeric command-button enum, and rechecks the latest slot assignment on press. Terminal task alerts are acknowledged only after successful navigation.

State publishing coalesces changes, skips identical projections, sends deltas and forces a full refresh on reconnect. Task-status callbacks replace desired state and signal the publisher; they never block on network I/O.

Extract these existing components together:

- src/Joydex.Windows/WirelessPanel/EspHomePanelAdapter.cs
- EspHomePanelTransport, EspHomeSseParser and wire models
- src/Joydex.WirelessPanel configuration/provisioning contracts
- tools/Joydex.WirelessPanel.Configure and the existing firmware configuration

The host supplies a task snapshot, navigation capability, acknowledgement capability, semantic action executor and diagnostics. Adapter start/stop remains cancel-and-join for observer and publisher loops. Snapshot generation prevents stale output from a prior connection from winning after reconnect.

## PAD configuration and pairing

Preserve LocalAppData/Joydex/WirelessPanel/panel.json and its CurrentUser DPAPI credential behavior. It is independent of the selected companion --config path today; document that association explicitly in the settings page.

Expose endpoint, enablement, connection health, pairing/test command and existing provisioning workflow. A token/host change reconnects only PAD. Validate before replacing active settings, and retain old credentials if the test fails.

No firmware or numeric command changes are required for extraction. A future dynamic layout or arbitrary plugin action surface needs a separately versioned protocol and coordinated firmware rollout. Existing command numbers never get renumbered to match a new registry.

## PAD and secret approvals

Existing fixed panel commands remain supported. Register new secrets actions in the engine so controls capable of arbitrary bindings can use them immediately.

Treat dedicated PAD approval controls as a later protocol addition: a panel must display the active request identity/revision and return that challenge with a fresh press. Show client/project and secret aliases, never values. The host rejects stale acknowledgements and untrusted panel sources.

The initial Secrets release uses the native desktop popup plus enrolled controller bindings. This avoids implying that the current four-slot firmware already provides a secure approval surface. Adding PAD approval pages requires explicit pairing/trust and attended firmware validation.

## DirectInput boundary

CompanionWorker currently combines acquisition, CompanionEngine and execution. Refactor into one acquisition owner per configured device, a domain resolver, and an asynchronous action dispatcher. Keep polling cadence and buffered input handling independent of slow task navigation or network operations.

Preserve device profile IDs, instance/product selectors, legacy single-device normalization, banks, cooldown, press/release/toggle semantics, wheel notch counts, maps, prompt picker behavior and open-working-directory actions.

Preserve Background/NonExclusive acquisition and the hidden cooperative HWND. Maintain startup warmup/baseline, held-button suppression, reconnect seeding and removal detection. Configuration subscribes to this same event source.

The shared binding engine retains task-alert interception and prompt picker/map priorities. A device plugin emits physical events and device metadata; it does not receive unrestricted Desktop commands or define its own foreground policy.

## Key ownership across plugins

Track injected holds by source device/plugin and action instance. A source restart releases only its holds. Shared keys remain pressed while another legitimate source still owns a hold; engine shutdown releases all owned injected state through existing cleanup.

This needs meaningful transition tests: two devices holding the same modifier/PTT; one device disconnecting; config reload during a hold; capture starting while a switch is held. Replacing a resolver must not synthesize a new press from physical state already held.

Dry-run and simulator/foreground suppression remain centralized and unchanged in meaning. Settings in the foreground does not bypass those guards. Input observation, PAD updates and Voice continue even when an ordinary Codex keystroke is correctly suppressed.

## VIRPIL boundary and Guardian

Keep both LED backends selectable. SetLedOutputAsync currently includes external-writer checks, expected-device checks, LinkTool startup changes, pause/swap/dispose, persistence rollback, Guardian update and output replay. Extract that complete transaction.

Keep VirpilShiftModeReader and VirpilHidTransport in the same process as direct LED output until a single cross-process HID owner exists. Per-process locks on device path cannot protect competing worker processes.

Maintain [ADR 0001](../../adr/0001-selectable-direct-virpil-led-output.md): restore a complete baseline, retain LinkTool compatibility, avoid ordinary throttle firmware reset and refuse competing writers.

Guardian watches the actual hardware owner after extraction: RuntimeHost while VIRPIL is in-process, or the dedicated VIRPIL worker if later separated. Retain version/session-token/backend/frame validation, clean/restore events, external-writer checks and session-bound restoration. Do not make visible UI exit look like hardware-owner failure.

Guardian restores LED/LinkTool state; it does not currently clean up injected keyboard state. Keep it outside the owner's termination job and wait for its recovery before a replacement owner begins HID writes.

HookRelay stays a minimal fire-and-forget helper into the shared task-alert pipe. Its current immediate drop on missing/busy pipe and always-successful hook exit preserve Codex responsiveness. Plugin setup, network requests and durable message delivery never enter this hook path.

## Implementation sequence

1. Wrap PAD's existing adapter with lifecycle/config/health contracts. Preserve protocol and settings path.
2. Centralize per-source injected-key ownership without changing profiles; verify shared holds across devices.
3. Introduce host input observation and capture using that ownership before moving DirectInput.
4. Extract acquisition and reconnect as the DirectInput plugin, retaining CompanionEngine in Core.
5. Extract VIRPIL backend switching and Guardian integration together.
6. Add optional PAD approval page only after the Secrets contract is stable and firmware work is explicitly scheduled.

## Acceptance

Use EspHomePanelAdapterTests and transport/provisioning tests for fixed button IDs, fresh slot assignment, acknowledgement, coalescing and reconnect. Use binding/input tests for profile parity, startup held switches and buffered event order. Extend CompanionWorker coverage around real source lifecycle, not only constructor wiring.

Use DirectVirpilLedServiceTests for unavailable-device isolation, competing writers, baseline restore and partial-write recovery. Perform attended unplug/replug, suspend/resume, backend switch, crash recovery and complete baseline checks on each supported controller.

Keep settings open during those scenarios. Kill/reopen only the UI and verify no acquisition, task pool, PAD reconnect or Guardian restoration occurs as a side effect.
