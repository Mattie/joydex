# Joydex plugin engine and integration designs

Original design: 2026-09-08. Refreshed: 2026-09-11 against main at `3d7c673`. Status: **implementation underway in reviewed milestones**; later plugin stages remain proposed.

Our recommended direction is **ENGINEFIRST**: keep the working .NET and Windows integrations, give Joydex an independent runtime, and extract features into explicitly registered plugins. Settings becomes a client of that runtime. Voice, controller input, PAD updates, and inbound messages keep running while settings is open.

The refresh incorporates the committed Voice, Pebble, configuration and controller changes since the original draft. The latest merge itself was README-only; the [baseline refresh](11-main-refresh.md) distinguishes it from the broader changes. Accepted ADRs remain in force, including ADR 0005's managed Codex runtime policy. ADRs 0006–0008 remain proposals; ADR 0009 records the accepted PROCESSUI boundary. The baseline refresh changed documentation only; subsequent implementation is tracked in [milestone demos](12-milestone-demos.md).

## Read the designs

| Document | Decisions and implementation detail |
|---|---|
| [Plugin engine](01-plugin-engine.md) | Ownership, plugin contracts, process boundaries, actions, configuration, persistence, compatibility, installation |
| [Settings that stays open](02-live-settings-ui.md) | Independent UI lifetime, input capture, Apply, per-feature restarts, technology choices and parity |
| [Voice PE plugin](03-voice-pe.md) | Dedicated task ownership, invisible media window, archives, audio invariants, extraction and recovery |
| [Joydex PAD and controllers](04-pad-and-controllers.md) | Wireless touchscreen plugin, DirectInput plugin, VIRPIL outputs, shared binding engine and task alerts |
| [Pebble Index plugin](05-pebble-index.md) | Independent ingress, durable delivery, bearer storage, migration and failure cases |
| [Discord bot plugin](06-discord.md) | Fresh Desktop tasks in whitelisted projects, Discord thread mapping, authorization, delivery and compatibility gate |
| [Secrets plugin](07-secrets.md) | Five choices, attributable clients, remembered grants, native popup, agent access and experimental .env provider |
| [Delivery plan and acceptance](08-delivery-plan.md) | Ordered implementation slices, dependencies, acceptance matrix, rollout and unresolved checks |
| [Prior art and evidence](09-prior-art.md) | Reuse shortlist, source links, license evidence and research limits |
| [Agent implementation plan](10-agent-orchestration.md) | Overseer, Sol xhigh/medium assignments, worktree ownership, waves and integration gates |
| [Main baseline refresh](11-main-refresh.md) | Changed assumptions, existing fixes to preserve, source evidence and remaining gaps |
| [Milestone demos](12-milestone-demos.md) | Current implementation scope, acceptance evidence and user feedback stops |
| [PROCESSUI contract](13-process-settings-contract.md) | Accepted settings/runtime boundary, revisions, client loss and operation reconciliation |

## Recommended shape

~~~mermaid
flowchart TB
    UI["Joydex settings / tray / task windows"] <-->|"local commands + snapshots"| E["Joydex runtime"]
    E --- C["Bindings • action routing • task alerts • config • supervision"]
    E <-->|"bounded capabilities"| D["Shared Codex Desktop gateway"]
    E <-->|"control and status"| V["Voice PE worker + private media STA"]
    E <--> P["Joydex PAD plugin"]
    E <--> H["DirectInput plugin + VIRPIL output plugin"]
    E <--> I["Pebble Index worker"]
    E <--> B["Discord worker"]
    E <-->|"approval metadata only"| S["Secrets broker + consent window"]
    B <--> DC["Discord threads"]
    S <-->|"stable local requester"| A["Agent helper"]
    D <--> CD["Codex Desktop-owned tasks"]
~~~

The engine provides only services with actual reuse. Voice keeps its audio pipeline and dedicated App Server owner. The shared Desktop gateway handles Desktop-owned tasks. Those two ownership paths stay distinct.

## Decisions to start from

| Keyword | Recommendation |
|---|---|
| **ENGINEFIRST** | Recommended architecture: a per-user runtime with bundled plugins, a separate settings process, and narrow shared services. |
| **KEEPFORMS** | Recommended UI: preserve our themed WinForms controls first. Moving ownership fixes the interruption; a visual rewrite is optional later. |
| **WORKERPLUGINS** | Recommended deployment: process workers for Voice, Discord, Pebble and Secrets; trusted local device modules may initially share the engine process. |
| **FRESHONLY** | Required Discord behavior: create a new Desktop task in an allowed saved project, then bind only that task to the new Discord thread. |
| **LOCALREQUESTER** | Initial secrets identity: the helper automatically creates a protected same-user identity tied to the current project. It is stable for remembered decisions but does not prove task origin. |
| **ENVFIRST** | Recommended experimental secret source: a user-selected .env read only by the broker, with exact secret references and a clear later provider boundary. |

These keywords identify proposed choices; they do not need separate approval before implementation planning can proceed. The implementation plan also names capability checks whose failure changes what we can deliver.

## The three important limits

1. **Desktop creation needs proof.** Our existing private compatibility bridge deliberately excludes creation and project enumeration. The Codex app tools available inside this conversation establish product capabilities, but they are not an external API contract for a shipped Joydex plugin. Discord must pass an installed-version probe before its transport is considered settled.
2. **Names are labels, not proof of origin.** The helper's protected local credential keeps one cooperative requester stable, but a caller can still supply a real task ID or project name without originating from that task. Verified task identity requires trusted host context.
3. **A process boundary contains crashes, not a hostile Windows user.** Bundled plugins run as our logged-in user. The .env experiment and DPAPI storage cannot prevent another unrestricted process under that user from reading accessible files or inspecting processes.

## Proposed architecture records

- [ADR 0006: independent runtime and bundled plugins](../../adr/0006-independent-runtime-and-bundled-plugins.md)
- [ADR 0007: attributable secrets approvals](../../adr/0007-attributable-secrets-approvals.md)
- [ADR 0008: Discord creates Desktop-owned tasks](../../adr/0008-discord-creates-desktop-owned-tasks.md)

ADR 0005 is already [accepted for managed Codex runtimes](../../adr/0005-follow-managed-codex-runtime-with-capability-checks.md); its number and policy remain intact.

[ADR 0009](../../adr/0009-separate-settings-from-runtime-with-owned-media-sta.md) accepts the settings process split and runtime-owned Voice STA for PROCESSUI; separate Voice process isolation remains a later extraction.

Begin with a recorded baseline, then one runtime/input owner and an independent reviewer. Expand parallel implementation only after the shared contracts pass their first behavior tests. The [agent plan](10-agent-orchestration.md) makes those handoffs explicit.
