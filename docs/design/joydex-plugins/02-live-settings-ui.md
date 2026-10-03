# Settings that stays open while Joydex runs

Status: proposed. Recommended choice: **KEEPFORMS**, with a separate runtime process.

## What currently interrupts operation

TrayApplicationContext.ShowConfigurationAsync explicitly asks to end an active Room Voice session, stops it, closes activity/maps, dismisses prompt selection, marks Voice paused, and calls StopWorkersAsync before showing ConfigurationForm.ShowDialog. That shutdown covers Voice, wireless panel and controller workers. Pebble and the shared Desktop broker already have more independent lifetimes.

ConfigurationForm independently constructs DirectInputJoystickSource and polls it with a Windows Forms timer for binding capture. Simply removing the shutdown would allow capture gestures to trigger live bindings as well.

Voice media also relies on an invisible WinForms/WebView2 window created using the tray's SynchronizationContext. Moving only the visible settings window would leave audio dependent on that same UI thread.

Main now injects configuration saving through a callback, rolls back earlier file writes if a later save fails, and keeps Configuration open after a failed save. CancellableRuntimeCoordinator guards Pebble startup/reload publication. Preserve these improvements while replacing the modal lifetime. They do not yet provide revision conflicts, a durable cross-file commit marker, selective Apply or UI-crash continuity.

Pebble already continues through opening/cancelling Configuration. A successful Save still restarts it even for unrelated edits, and its settings page receives an initial status snapshot rather than a live subscription. The new client must preserve the existing continuity and close those narrower remaining gaps.

Source review also found that opening settings assigns freshly read Pebble preferences to the field used for Desktop-broker dependency decisions while the old receiver can still be running. An external edit that disables or corrupts the file could therefore affect broker lifetime on Cancel. Reproduce this boundary during implementation, keep active config distinct from the draft/desired config, and derive dependency leases from active owners. Opening/cancelling settings must not adopt an external candidate implicitly.

## User experience

Opening Configure shows the current settings and health immediately. Room Voice continues speaking and listening. Controller bindings, maps, prompt pickers, PAD updates and inbound messages continue subject to their normal foreground/simulator safety rules.

The window has Apply, Save and close, and Cancel. Apply commits the current draft and keeps the window open. Save and close applies then closes on success. Cancel discards unapplied edits. Opening a page never creates a task, rotates a token, tunes a device or reconnects a plugin.

An existing settings window is brought forward on repeated Configure actions. The tray's mode/status controls stay available. Closing settings releases subscriptions and capture leases only. Closing the ordinary UI process also leaves the engine and Voice media worker alive; explicit Exit Joydex is the operation that stops runtime work.

Navigation remains familiar: General, Bindings, Prompt Pickers, Button Maps, Room Voice and Pebble Index, plus Plugins, Joydex PAD, Discord and Secrets as applicable. Plugin pages appear from the installed catalog. Disabled features remain configurable with an explicit disabled state.

The main surface shows concise runtime state and only actionable problems. Pending voice changes say, for example, “Saved. Applies after this call.” A details expander can show affected components and restart errors. Internal IPC names, protocol versions and worker names belong in diagnostics.

## Independent lifetimes

~~~mermaid
sequenceDiagram
    participant U as Settings UI
    participant E as Runtime
    participant H as Input owner
    participant V as Voice worker
    U->>E: Attach, snapshot + event cursor
    E-->>U: Active settings and health
    H->>E: Continuous input events
    V->>V: Continuous media session
    U->>E: Prepare patch at revision 12
    E-->>U: Only controller A binding changes
    U->>E: Apply prepared patch
    E->>E: Swap controller A resolver safely
    E-->>U: Applied revision 13
    U->>E: Disconnect / close settings
    H->>E: Continuous input events
    V->>V: Continuous media session
~~~

Use a small host-client interface even for the first same-process transitional slice. Then move the host behind a named pipe without rewriting every page. Once split, runtime code has no references to Form, Control, message boxes or the settings SynchronizationContext. Platform HWND services and the Voice STA are explicit exceptions owned by their runtime components.

The .NET 8 UI must marshal updates with existing BeginInvoke/SynchronizationContext patterns. Newer Control.InvokeAsync examples target .NET 9+ and cannot be adopted blindly under our pinned SDK. Avoid .Result, .Wait and synchronous Control.Invoke on callback paths that the UI itself is awaiting. [WinForms threading guidance](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/controls/how-to-make-thread-safe-calls).

## Observe input once, capture deliberately

The device plugin owns each acquisition handle. Settings receives current buttons/axes and sequenced input edges from the host. Device discovery comes from that owner too; the UI never opens a second controller handle.

Per-source injected-key ownership is a prerequisite to live capture and selective restarts. Current executor cleanup can release a global PTT chord; replace that behavior before one device's capture/reset is allowed to disturb another device's hold. The two-device shared-chord test gates the first modeless capture milestone.

Capture is an explicit host lease with a UI connection ID, selected device, purpose, expiry and generation. Before capture, reject a device already reserved by another capture client. Ignore controls held when capture starts until released. Capture the next eligible edge; suppress its normal binding through the complete release, then restore normal routing.

When the button is not yet known, capture briefly reserves ordinary actions for the selected device. Other controllers, Voice, PAD and inbound plugins continue. This local suppression is visible beside Capture and ends on success, cancellation, timeout, device disconnect or UI loss.

Do not replay suppressed presses later. Reset applicable toggle/hold state and release injected keys before a resolver or capture lease changes. Existing startup-held-button and buffered-event filtering must survive extraction.

Approval actions and capture cannot consume the same edge. Route reserved safety/release behavior first, then active capture, then a current consent challenge, then task alerts/pickers/ordinary bindings. A consent challenge requires a fresh post-display press; a release from a prior capture or held throttle switch can never approve a request.

Default capture timeout: 15 seconds; renewable only through an active UI gesture. A disconnected UI expires immediately on observed disconnect, with the timeout as a fallback. Engine-local timers enforce it.

## Drafts and Apply semantics

The settings client holds a draft and base revision. Live status updates never overwrite dirty fields. Runtime changes from another UI, a device action or an external config edit are shown as a conflict affecting those fields.

1. Read active/desired/pending settings with revision and validation metadata.
2. Edit locally. Local validation provides quick feedback; host validation remains authoritative.
3. Prepare a patch against the base revision. Host resolves resources and reports exact effects, including live changes, deferred changes and required stops.
4. Apply that prepared patch. If the base revision changed, return Conflict without mutating runtime or files.
5. Write the complete desired revision using a commit record; activate independent components according to their plan.
6. Return per-component active/desired revision and result. Never show “Applied” for a component still on old settings.

At v1, Apply is atomic per settings aggregate/plugin. Cross-page Apply validates all changes first, then may produce a visible partial result across independent plugins. Failed aggregates retain or restore their old active configuration; successfully applied aggregates remain applied. The UI preserves failed drafts for correction. Do not promise a distributed all-or-nothing transaction across audio sessions, network listeners and device handles.

Persist enough information to recover a crash between desired-state commit and activation: revision, previous active revision, affected plugin and Pending/Activating/Applied state. At restart, revalidate pending settings and retry activation only where safe. Irreversible network/task operations are excluded from configuration transactions.

During compatibility stages, existing config files remain authoritative for their aggregates. Each store gains compare-and-swap revision metadata or a companion commit envelope. A later storage consolidation gets its own importer; it must not silently change what a manual edit affects.

Carry the current save-failure fixtures into this boundary: a later file-write failure must restore earlier writes and preserve the editable draft. Exercise crash recovery separately from handled-exception rollback; today's callback transaction does not prove recovery after process death.

## What can apply live

| Change | Application behavior |
|---|---|
| Theme, layout, window size | UI-only; immediate |
| Binding label, map, prompt list | Replace relevant immutable snapshot; preserve input ordering |
| Binding action/bank/control | At safe device boundary; release owned keys and require currently held controls to release |
| Global dry-run / injection safety | Atomically update action gate and cancel queued actions that no longer qualify |
| Dry-run change affecting Voice | Entering dry-run blocks new owned starts, closes/drains the active session and releases the owner before Voice reports dry-run. Leaving it restarts only the affected Voice runtime. |
| Device selector, driver mode | Restart only affected device after releasing keys; other devices continue |
| Task-alert rendering/LED preference | Recompose output; ownership backend change has an exclusive lease handoff |
| PAD host/token | Restart only panel transport after validation |
| Voice gain/wake tuning | Only live where current transport supports it; explicit tuning command with visible result |
| Voice task/workspace/binary/endpoint | Default PendingIdle; apply after session close. “End call and apply” is an explicit user action |
| Pebble port/auth configuration | Drain in-flight accepted ingress, rotate/rebind that receiver only |
| Discord allowlist | Restrict immediately at authorization boundary; edits to bot credentials reconnect only Discord |
| Secret grant revocation | Immediate broker policy generation change; recheck before each redemption |

Save never implicitly provisions a new Codex task. Existing Room Voice Create/Provision and device tuning controls become explicit commands with their own progress and outcome. Cancel cannot undo those completed commands; distinguish them from draft fields.

The existing dry-run setting affects Voice ownership as well as injected actions. Preparation must show that changing it can close a call. Do not display a completed dry-run transition while a live dedicated owner still exists.

## UI technology options

| Keyword | Fit and tradeoff |
|---|---|
| **KEEPFORMS** | Recommended. Reuse our themed controls, high-DPI behavior, binding grid, window state and map rendering. Best path to behavior parity with little visual churn. |
| **WEBSETTINGS** | Later option: local bundled web UI in WebView2, using the same host-client contract. Useful for richer layouts; requires accessibility, asset, navigation and message validation work. Voice uses a separate WebView2 environment/lifetime. |
| **NATIVEUI** | Later option: WinUI/WPF/Avalonia replacement after parity tests. Requires a larger control/window migration; no evidence currently requires it to solve the interruption. |

Keep Segoe UI or our existing theme font; never introduce Arial defaults. Use keyboard-accessible labels and focus order, screen-reader status, scalable layouts and the existing DPI/window persistence behavior.

Trusted bundled UI contributions can supply WinForms controls initially. Runtime contracts expose settings metadata and commands without a WinForms dependency. External worker plugins later get schema-driven pages; arbitrary plugin HTML does not receive privileged host objects.

If WEBSETTINGS is selected later, load only packaged content, deny navigation/new windows by default, validate each web message, and keep secrets out of DOM/state. A secrets approval popup remains broker-owned native UI.

## Verification and rollout

First ship the same-process host seam and modeless form only after host-owned observation/capture is ready. This transitional milestone keeps work running during ordinary settings use but does not promise survival of a UI process crash.

The separate-process milestone must pass the stronger test: terminate the settings process during active Voice playback and controller operation; those paths continue. Restart the UI and restore a fresh snapshot without reinitializing devices or losing a delivery.

Exercise Apply, Cancel, conflict, invalid settings, UI loss during capture, device unplug/replug, sleep/resume, display scaling and dark/light themes. Run the existing settings documentation renderer and compare all existing pages as a parity aid. Hardware/media canaries remain necessary for the audio and controller boundaries.
