# Client damage research: first delivery and follow-up queue

## Current objective

Prepare PR #371 as a reviewable first research baseline for client Hit/DoT
calculations and resource synchronization in 3.29.3b. Finish the selected DoT
and Hit receiving paths in depth, explain their exact numerical and C/S
contracts, and preserve unresolved upstream algorithms as named follow-ups.
Package the existing evidence for review and verify the final PR against the
current main. Stop when the delivery gates below pass; do not resume broad
mechanic discovery as part of this PR.

This delivery objective replaces **all original R0–R4 Established before PR
readiness** as the current work-selection and readiness rule. The user's
2026-11-01 scope change authorizes this separation. Original R0–R4 findings
and their evidence standards remain unchanged: R2/R3/R4 are still Partial.
Issue #370 remains the umbrella investigation after this delivery merges.

The first delivery's evidence and publication checks have passed. Start with the
[baseline reading guide](client-damage-baseline.md) and [evidence review](client-damage-evidence-review.md).
The PR records its exact validated head; verify that head before the Ready transition,
then stop. Reuse the sealed records rather than resuming broad exploration.

## Two selected paths

| Path | Start and endpoint | Existing evidence | Required explanation |
|---|---|---|---|
| DoT, first | Received owner Stats rate through override/fallback and notifications to actual Life loss, fractional carry and later rate/absolute-pool changes | [Network rate composition](client-network-rate-composition.md), [initial Stats](client-initial-stats-rate.md), [routing](client-dot-routing.md), [Buff/rate independence](client-buff-rate-independence.md) | Which values are received, which calculations execute locally, exact units/rounding/order, explicit-zero precedence, and why removing a Buff does not itself establish withdrawal of its damage rate |
| Hit, second | One Fireball release/result continuation through the actual result consumer and its checkpoints, with the independently received resource assignment | [Hit/checkpoint composition](client-hit-checkpoint-resource-composition.md), [action-present continuation](client-hit-action-present-composition.md), [combat protocol](../protocol/29-client-combat-signals.md), [PR #373 comparison](client-damage-pr373-comparison.md) | What contact/feedback establishes, which fields are qualifiers rather than damage amounts, when absolute resources can change, and where the accepted-Hit amount/mitigation connection remains unknown |

For both, reusable offence/defence formulas are a separate calculation layer.
The reading guide must link their recovered equations without drawing an
unproven edge from a panel estimate to actual damage. A supplied rate or
parsed message is an explicit starting contract, not an actual source instance
or official-server send. No whole-client absence claim is required or allowed.

## Delivery gates, in execution order

| Gate | Concrete result required before passing | Current state |
|---|---|---|
| D0: freeze scope | This objective, the two paths, the follow-up queue and the same readiness rule appear in the workbench and PR; issue #370 distinguishes the initial delivery from ongoing research | Passed: scope and baseline publication verified against PR/issue bodies; umbrella issue remains open |
| D1: close the DoT explanation | One compact annotated path and a numerical witness from existing checked evidence. Every edge identifies inputs, outputs, guards and source anchors; every fixture and unbound edge is visible. Cover rate change, fractional carry and independent absolute correction. Audit the cited records against the claims | Passed: [path, edge contracts and witnesses](client-damage-baseline.md#dot-received-target-rate-to-life); [claim audit](client-damage-delivery-audit.md) |
| D2: close the Hit explanation | One compact annotated Fireball path and the existing result/assignment witness. Separate offence estimates, contact/feedback and resource authority. State the missing numerical resolver explicitly; retain PR #373 source revision and capture-availability limits | Passed: [Fireball path and numerical witness](client-damage-baseline.md#hit-one-fireball-continuation-and-independent-resources); 42 retained cases inspected; no locally computed Hit amount claimed |
| D3: make the PR reviewable | A short entry guide and PR body, clear core versus supplementary references, and an explicit treatment of the three large machine-readable JSON files. Preserve evidence content, source identities and sealed artifacts; verify any packaging change. Reviewers must not need to read the full chronological evidence inventory | Passed: [entry guide](client-damage-baseline.md), [evidence review](client-damage-evidence-review.md), default-collapsed machine diffs; only two public support locators normalized for privacy |
| D4: final integration and claim audit | Verify latest main and exact PR head, resolve any real documentation conflict, validate links/privacy/manifest and affected reproduction tools, and check D0–D3 claim by claim. No new server behavior; no game/CI claims for documentation-only checks. Only then change Draft to Ready | Passed: [claim-by-claim evidence](client-damage-delivery-audit.md), latest main included, tools/bytes/links/privacy checked; published head, PR/issue bodies and GitHub mergeability verified. Repeat affected checks and exact-head verification for any subsequent change before Ready |

Ready means D0–D4 passed for this **explicit first delivery**. It does not mean
all damage mechanics, the original R2–R4, or official-server parity are complete.
Do not close issue #370 automatically when this PR merges.

## How an agent selects work

1. Work on the first unfinished D gate, with at most one active research
   question. Reuse the published R75 baseline and immutable prior evidence.
2. Before inspecting a new function, name the exact missing edge in D1 or D2
   and the result that would resolve it. A new formula, helper classification
   or larger function inventory is not by itself progress on that edge.
3. Follow one selected caller/data path to its next meaningful output or
   authority boundary. Once a path is classified as dependency retention,
   rendering, configuration or unrelated storage, stop that branch.
4. After two bounded attempts fail to change the same edge's evidence state,
   stop exploration and record the question, attempted coverage, known facts
   and next evidence needed. Do not silently open a third neighboring branch.
   If it blocks a required D claim, report the gate as blocked; a failed search
   cannot turn an unknown into an established boundary or a passing gate.
5. Questions outside the selected paths go to the queue below. Do not open
   new branches, issues, broad scanners or formula families just to fill them.
   Record already encountered facts, but defer further investigation.
6. Once D1/D2 are audited, work only on packaging and final review. Do not
   extend the research because another interesting candidate was found.

Original static dataflow remains valid within its version and guards without
live replay. No client execution, attachment, new captures, server implementation,
CI/deployment, shared REA/Ghidra mutation, peer file edits or new agents are
authorized by this plan. Continue sharing through the existing owned ledger.

## Follow-up queue: not required for this PR

| ID | Question for a separately scoped batch | Starting point and boundary |
|---|---|---|
| F1 | How do actual DoT instances select/stack, publish a target aggregate, reach a shared cap and withdraw on expiry? | Existing source-authority, instance-rule and native source-add/remove studies. Distinguish client evidence, versioned mechanics references and any required server evidence. Do not assume the missing implementation must exist in this client |
| F2 | Where is accepted-Hit amount sampling, critical confirmation, mitigation and final resource routing performed? | Existing offence equations, avoidance/result paths and typed pool receivers. Pick one attack or spell path for the next batch, not every skill or defence |
| F3 | How do owner timing changes refresh an existing effect's clock and numerical contribution? | Existing Buff network-clock and timing-input studies. Separate specific established contracts from the unresolved generic refresh |
| F4 | Who consumes the Mod-derived Buff descriptor as a gameplay effect? | Published R75 descriptor preparation. R76 navigation is unsealed local work, parked without a completion claim; configuration provenance alone is not target application |
| F5 | Which additional keystones, uniques, ailments and ground-effect variants need research? | Reuse already recovered special cases. Select one mechanism family only after prerequisites and its own finish condition are defined |

These are parked questions, not promised implementation tasks or newly opened
PRs. Existing supporting reports stay available; their length or verification
counts do not change a queue item's completion state.
