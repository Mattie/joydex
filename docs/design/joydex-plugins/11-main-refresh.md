# Main baseline refresh

Refreshed 2026-09-11 by source and documentation inspection. This ledger records changed planning assumptions; it does not certify new runtime or hardware tests.

## Compared revisions

The September 8 design was saved at `c4dc635` on wip/joydex-plugin-design. Current main is `3d7c673`. That last merge brought README changes from `a338961`; the material runtime changes below landed between the original draft and current main, including PRs #9, #10 and #11. Only the design directory was restored from the old branch. Older source, firmware experiments and discarded research were not restored.

The original proposed ADR numbers overlapped the now-accepted ADR 0005. The plugin, secrets and Discord proposals are now 0006, 0007 and 0008. Their proposed status is unchanged.

## Changed assumptions and consequences

| Area | Current main evidence | Design consequence |
|---|---|---|
| Codex runtime selection | Accepted [ADR 0005](../../adr/0005-follow-managed-codex-runtime-with-capability-checks.md), CodexAppServerRuntimeResolver, commits `b905068` and `7f5f183` | Preserve managed complete-runtime discovery, explicit external overrides and update-race handling. Remove the old Codex version/hash admission requirement. Startup capability checks still need a real realtime-session canary. |
| Voice delivery and packaging | Accepted ADRs 0003/0004; [Room Voice guide](../../ROOM_VOICE.md); scripts/Publish-Joydex.ps1 | Preserve continuous Sendspin timing, priming/startup bounds and current source-only firmware workflow. Package the bridge and WebView2 assets. Short-reply onset remains a physical acceptance limitation. |
| Pebble durable ingress | PebbleIndexDeliveryStore; atomic ingress and payload checks including `274f371` | Preserve atomic duplicate acceptance, original destination and same-ID content rejection. Do not redesign a fix already present. |
| Pebble send uncertainty | PebbleIndexReceiverRuntime.DeliverOneAsync; `d417ea4` | Current code records DeliveryUncertain before send and retains it on cancellation/failure. Add explicit attempt state only when recovery consumes it. |
| Pebble authentication/parser | `bfd2b41`, `d417639`, `c43ebe5`, `1b44bff` | Preserve authentication before body reads, structural multipart handling and rejection of non-text/audio/file parts. Kestrel replacement remains a separate follow-up. |
| Pebble target/status/shutdown | `e9329ed`, `13778eb`, receiver client tracking | Preserve direct lookup of the saved task, held/unreadable status and joined active clients. Remaining work is live subscriptions, recovery controls and exclusive data ownership. |
| Startup/reload lifecycle | CancellableRuntimeCoordinator; `011e483`, `4e34322` | Reuse stale-start/stop/cleanup guarantees. Opening/cancelling settings already leaves Pebble alive; successful Save still restarts it. Worker extraction must avoid restarts on unrelated edits. |
| Configuration save | `7646d1e`, `aedc4e7`; injected save callback | Preserve exact-file rollback on handled failure and keep the editable form open. Durable revisions, active/desired separation and crash recovery remain new work. |
| Actions and starter mappings | Controller PR #9; Codex action catalog, defaults and AGENTS compatibility record | Include archive-chat, toggle-review and current mic-toggle/PTT assignments in parity checks. Preserve all saved custom bindings. |
| Agent visualizer research | `76649c1`, research/agent-activity-visualizer | A simulated design prototype provides no runtime telemetry. Agent orchestration can proceed without implementing it. |

## Remaining boundaries

The fundamental runtime/UI recommendation still holds. ShowConfigurationAsync continues to stop Voice/controller/PAD work and open a modal form. ConfigurationForm still opens its own DirectInput source for capture. Voice's media window still depends on tray-thread lifetime. Shared key ownership and capture leases must precede modeless input capture, and an independent media STA must precede the UI-process survival claim.

Pebble has a safe conservative pre-send marker but no exclusive receiver/data-root lease or structured recovery actions. Queue-full records remain Received with a held detail. Normal tray startup validates before secret loading; the public startup factory can still load/create a token before validation or listener bind. These are narrower residuals than the original draft described.

One additional boundary needs a regression fixture: ShowConfigurationAsync assigns freshly loaded Pebble settings to the shared field consulted by DesktopTaskBrokerNeeded while an old receiver can still be active. Cancelling after an external disable/corrupt edit could stop its broker dependency. This is a source-review finding, not an exercised reproduction. Active owner leases and separate desired/draft state must settle the behavior.

The current Desktop bridge still does not prove external project enumeration/fresh task creation for Discord. A legitimate external executor context, creation receipt, first-turn recovery and observation/continuation remain an installed-version gate. The assistant's own create-task tool does not establish that plugin contract. Verified Codex task origin for secrets remains separate from caller-supplied labels; NAMEDCLIENT is still the initial design.

Guardian continues to recover LED/LinkTool state. Immediate injected-key release after hard process death remains an open mechanism and attended integration check. Same-user plugins and secret storage continue to provide cooperative controls, with no hostile-user isolation claim.

## Regression evidence to carry forward

The following existing sources are implementation anchors, inspected during this refresh:

- [PebbleIndexTests](../../../tests/Joydex.Tests/PebbleIndexTests.cs): duplicate concurrency/content, direct target lookup, authorization before body arrival, multipart rejection, state-at-send uncertainty, active-client shutdown, unreadable status, coordinator cancellation/restart and multi-file save rollback.
- [CodexAppServerRuntimeResolverTests](../../../tests/Joydex.Tests/CodexAppServerRuntimeResolverTests.cs): complete managed candidates, explicit overrides and update races.
- [Pebble receiver](../../../src/Joydex.App/PebbleIndexReceiverRuntime.cs), [production runtime composition](../../../src/Joydex.RuntimeHost/Production/ProductionRuntimeComposition.cs) and [tray projection](../../../src/Joydex.App/RuntimeTrayApplicationContext.cs): current lifetime and delivery boundaries.
- [Publish-Joydex.ps1](../../../scripts/Publish-Joydex.ps1): current release assembly, helper and native-asset requirements.

Implementation adds targeted evidence for capture/key ownership, UI/process/media continuity, selective Apply, active-versus-desired dependency lifetime, data leases, queue saturation and crash/recovery boundaries. Do not rerun hardware indiscriminately; exercise the changed boundary from one integrated package and retain the accepted baseline limits.

All .NET commands must resolve SDK 8.0.423. The optional .tools/dotnet/dotnet.exe is a workstation convenience, not a tracked dependency automatically supplied to worktrees.

## Refresh validation

This pass uses source inspection and independent Sol planning reviews. Runtime behavior, dependencies, firmware and credentials are unchanged. No .NET or attended hardware/media/phone tests were run for the documentation refresh. Local document links, example JSON and whitespace are checked before handoff; implementation still needs the baseline runs and gates in the [delivery plan](08-delivery-plan.md).
