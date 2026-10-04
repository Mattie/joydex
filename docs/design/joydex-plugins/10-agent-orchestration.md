# Agent implementation plan

Status: implementation approach authorized 2026-09-11. Baseline: main `3d7c673` plus the reviewed design refresh checkpoint at `185b828`. The original refresh dispatched read-only planning reviewers. Subsequent implementation and user feedback stops are tracked in [milestone demos](12-milestone-demos.md).

The filename's 10 is this document's sequence number in the design set, not an agent count.

## Recommendation

Use **OVERSEERWAVES**: one overseer owns integration and the shared contracts, with GPT-5.6 Sol xhigh builders/reviewers for difficult boundaries and Sol medium for bounded work after those boundaries settle. Start with one builder and one independent reviewer. Expand to two builders plus one reviewer, and occasionally three builders when their files and resources are independent. A role can be reused across waves; we do not need a permanent agent for every plugin.

The benefit is independent investigation and implementation of separate adapters. The constraint is shared ownership: runtime composition, tray and settings projections, actions, key injection and worker lifecycle still connect most features. Sending several agents to split those files simultaneously would increase integration work. This concurrency recommendation is based on those code boundaries; it is not a measured model speed or cost claim.

Keep the overseer on the capable model configured for the task unless a model is explicitly selected. If the whole team should use Sol, use Sol xhigh for that role too. Official Codex guidance supports role-specific model/effort choices and cautions that subagents add token usage and parallel writes need care. [Codex subagents](https://developers.openai.com/codex/multi-agent).

## Ownership and model effort

| Role | Default effort | Exclusive work or responsibility |
|---|---|---|
| Overseer / integrator | Current capable model; Sol xhigh when selected | Accepted baseline, shared contracts, configuration stores/revision envelopes through R1, solution/project/package files, catalog registration, composition edits, merge order, acceptance ledger and packaged release readiness |
| Runtime/input builder | Sol xhigh | Runtime lifecycle, action routing, per-source key ownership, capture lease and resource handoff; sole delegated writer for these shared files during its slice |
| Voice builder | Sol xhigh | Dedicated owner/media aggregate, STA lifetime, worker adapter and Voice tests; preserves managed-runtime resolution and accepted media behavior |
| Device or Pebble builder | Sol xhigh for lifecycle/durability; medium for later mechanical moves | One specifically assigned adapter and its tests; never acquires another lane's controller, receiver or state directory |
| Settings builder | Sol medium after contract freeze; xhigh for transaction/concurrency design | Draft pages, typed client wiring, UI parity; the integrator owns host configuration stores, transactions and revision envelopes through R1 |
| Secrets builder | Sol xhigh | Named-client identity, exact consent scope, native broker popup, durable launch uncertainty and synthetic-secret tests |
| Discord builder | Sol xhigh | Fresh-only authorization, ingress/mirror state and uncertainty; receives an established gateway contract |
| Independent reviewer | Sol xhigh for risk boundaries; medium for narrow docs/parity checks | Read-only review of the exact proposed or integrated revision; challenges ownership, tests and user outcome |

The overseer may delegate a shared file to exactly one builder for a slice. Everyone else proposes the needed seam and waits for its accepted revision. No simultaneous ownership of ProductionRuntimeComposition, RuntimeTrayApplicationContext, ConfigurationForm, shared action contracts, configuration stores, solution files or publish scripts. File moves and their references belong to the same owner until the move merges.

The integrator also owns DESKTOPGATEWAY, or explicitly delegates it to one Sol xhigh builder: creation intents, provenance, durable bindings and ambiguous-result reconciliation. Discord owns ingress/mirror state and consumes that completed capability. Readiness work does not itself authorize external publication; follow the user's release instructions and any applicable approval gates.

Use medium for catalog inventories, documentation, synthetic fixtures, straightforward page/client wiring and mechanical moves behind an accepted API. Escalate to xhigh when a change exposes lifetime, cancellation, state migration, security or multi-writer questions. Effort level never substitutes for observed acceptance evidence.

## Checkouts and common baseline

1. Record the source SHA, design revision, clean/dirty state, applicable AGENTS/CONCEPTS/ADRs and baseline failures. Preserve all unrelated work. The untracked restored design needs an explicit checkpoint before it can be a common worker baseline.
2. Create a separate Git worktree and branch for each writing lane from the same accepted checkpoint. Use project branch conventions, such as feat/runtime-host or feat/voice-worker. A spawned subagent does not automatically get an isolated checkout: pass its absolute worktree path and require an explicit working directory for commands.
3. Keep one designated integration checkout. Only the integrator updates shared composition and merges reviewed worker commits. Do not update main beneath running builders; stop at a wave barrier, integrate, then refresh their bases.
4. Verify SDK 8.0.423 in every checkout. An ignored .tools directory is absent from a new worktree unless deliberately provisioned. A verified absolute SDK executable may be shared; each checkout keeps its own build outputs.
5. Tests use disposable, lane-specific config/data roots and synthetic credentials. Avoid the normal LocalAppData Joydex state, live ports, singleton pipes and hardware. One integration operator owns attended hardware, Voice, Desktop and phone canaries.

Git worktrees provide separate working files while sharing repository metadata. Read-only reviewers may inspect the accepted integration checkout. The existing research/agent-activity-visualizer is a simulated UI prototype and is not a prerequisite or a source of real orchestration telemetry. [Codex worktree guidance](https://developers.openai.com/codex/app/worktrees).

## Waves and useful stopping points

| Wave | Implementation lane(s) | Independent work allowed | Exit gate |
|---|---|---|---|
| 0 — BASELINE | Overseer records current main, tests, paths, actions and packaging | Medium inventory; xhigh read-only Desktop capability investigation | Existing fixes and open gaps named; one checkpoint and exclusive file map |
| 1 — HOSTSEAM | One xhigh runtime/input builder; integrator switches the transitional form only after seam review | Medium UI/parity inventory; xhigh contract review | Per-source holds/capture tests and host/client boundary accepted; same-process modeless settings keeps integrations active, without a UI-crash survival claim |
| 2 — PROCESSUI | Xhigh Voice/media lifetime lane and medium settings client lane after their shared protocol is fixed | Independent review; integrator alone wires composition/config transaction | Settings open/Apply and UI-kill continuity scenario on integrated package; no second media/device owner |
| 3 — PLUGINPILOT | One PAD/catalog pilot builder; integrator owns catalog/solution changes | Release parity review | PAD disconnect/restart isolated; plugin contract proven with real behavior; R1 acceptance complete |
| 4 — EXISTING FEATURES | Xhigh Voice plugin lane and a disjoint controller/VIRPIL lane; Pebble follows Voice in the default sequence | Medium mechanical moves/docs after each seam; independent risk review | Existing-feature parity, Pebble regressions, leases and hardware recovery; R2 acceptance complete |
| 5 — NEW FEATURES | Xhigh Secrets lane; integrator or one xhigh delegate completes DESKTOPGATEWAY before the Discord lane | Medium helper/UI/docs work after each lane's API settles | Identity/consent gates for Secrets, installed Desktop creation and durable gateway gates for Discord; selected R3 features complete |
| RELEASEPARITY — after each train | Integrator packages R1 after Wave 3, R2 after Wave 4, and selected R3 features after Wave 5 | Independent integrated revision review | That train's relevant acceptance matrix, rollback and notices; no dependence on later optional features |

R1 delivers the user's first outcome: settings stays usable while integrations run, then survives a settings-process restart. Moving enough Voice lifetime code to pass that outcome is part of R1 even though formal Voice plugin registration finishes in R2. R2 preserves existing features as plugins. R3 adds Secrets and Discord; it can proceed independently of the remaining hardware extractions once its dependencies pass. Apply release/rollback checks to each train, rather than deferring all packaged validation to the final wave.

The same Voice lane continues from Wave 2 media ownership into Wave 4 plugin registration, using a fresh branch/worktree from the Wave 3 integration checkpoint. Do not launch a second competing extraction of the same aggregate.

~~~mermaid
flowchart LR
    B["Common baseline"] --> H["One host/input owner"]
    H --> V["Voice media lifetime"]
    H --> U["Settings client"]
    V --> I["Integrate + continuity canary"]
    U --> I
    I --> P["PAD pilot / R1"]
    P --> R1["R1 package + rollback gate"]
    P --> E["Existing plugins / R2"]
    E --> R2["R2 package + rollback gate"]
    P --> S["Secrets identity gate"]
    B --> D["Desktop capability gate"]
    P --> G["Durable Desktop gateway"]
    D --> G
    G --> C["Discord"]
    S --> R["Selected R3 package + rollback gate"]
    C --> R
~~~

## A concrete first assignment

Assign the first xhigh builder HOSTSEAM: separate runtime lifetime from window lifetime, introduce one input observation/capture boundary and establish per-source injected-key ownership. Initially delegate only its agreed Runtime/Actions files and a new host seam; the integrator owns ProductionRuntimeComposition, RuntimeTrayApplicationContext and ConfigurationForm edits. The builder requests any composition changes through a small patch proposal.

Acceptance must include two devices holding the same chord while one enters capture or disconnects, a control held before capture, release after capture, UI loss during capture, and generation changes during shutdown. Do not enable modeless capture until those scenarios pass. Then the integrator completes HOSTSEAM with a transitional same-process modeless form and the ordinary settings-continuity check. PROCESSUI supplies the stronger process-kill continuity guarantee.

In parallel, medium inventories existing settings fields/actions/paths and existing tests; an xhigh reviewer checks the proposed seam against those scenarios. That reviewer can inspect source while the builder works without modifying its files. This is useful parallelism before plugin contracts are mature.

## Worker assignment and handoff

Every assignment includes:

- Exact base SHA, absolute checkout path, file ownership and prohibited shared resources.
- One user-visible outcome and the relevant rows from the delivery-plan acceptance matrix.
- Applicable accepted ADRs and current behavior to retain, including known limits.
- Expected seam, permitted dependencies, explicit scope exclusions and escalation conditions.
- Required checks with a distinction between synthetic, Windows process and attended acceptance.

Every handoff includes the proposed commit SHA and base, changed files, behavior/invariants preserved, tests actually run with results, tests not run, migrations/rollback impact and unresolved concerns. A successful build alone is insufficient. Workers do not approve their own changes or mark a hardware/capability gate passed from mocks.

The reviewer checks the exact revision; the integrator merges or cherry-picks one accepted slice, then validates affected shared behavior on the resulting integrated SHA. Run the full relevant suite at wave boundaries. Repeat or broaden checks when integration changes, failures or new risks justify it. Save sanitized evidence with the plan/PR, excluding credentials, transcripts and private request contents.

## When the overseer narrows or stops a lane

- Two lanes need the same shared contract/file: serialize that seam, settle it, then resume from its accepted revision.
- A regression cannot be distinguished from the baseline: reproduce on the baseline before changing more architecture.
- A canary needs a real user/session/device: reserve the single integration operator and preserve any genuine approval gate.
- Desktop creation or task-origin verification fails: keep the affected capability disabled; continue independent runtime and named-client work. Do not substitute a second task owner or caller-supplied identity.
- Hard-crash key release is still unproven: retain the explicit limitation until the mechanism and kill-owner test exist. Guardian currently restores LEDs/LinkTool.
- Scope expands into a public SDK, arbitrary third-party plugins, a new UI framework or OS-level hostile-user isolation: record a separate proposal instead of growing the extraction silently.

No fixed completion-time or token-cost estimate is justified by this design alone. Measure throughput after the first integrated slice, then adjust concurrency based on review backlog, merge conflicts and acceptance failures.
