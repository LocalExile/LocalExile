# One monster hit: client/server authority contract workbench

## Objective and scope

Recover the externally observable contract needed for a server-authoritative
monster hit on the player in client 3.29.3b. **Contract before detail:** establish
responsibilities, exchanged data, event identity and client state application.
Inspect a formula only when its result or ownership blocks one of those edges.

Begin with one monster, one player and one attributable ordinary hit, separating
non-critical/unblocked damage from controls. Prefer the already reported Sand
Spitter case if available evidence can identify its action, target and resource
update; freeze that choice in Q0 before new investigation. Reuse projectile
research for any contact boundary rather than reopening motion or RNG internals.
Do not catalogue every monster, damage formula or defence.

This is a Draft research plan under issue #370. Q0-Q3 are pending; creating this
workbench establishes no new runtime observation, per-hit attribution or official
server algorithm. No new goal or runtime experiment is started by this bootstrap.

## Fixed inputs to reuse

The branch starts from main `d377bd256036242d2fb43f53eb7110b4897e1862`.
PR #371 is an unmerged, Ready documentation dependency pinned at
`801358d732adbc558d657d8773e273a02bc891b1`; use its fixed sources without
importing the baseline branch:

- [Combat messages and resource authority](https://github.com/LocalExile/LocalExile/blob/801358d732adbc558d657d8773e273a02bc891b1/Agents/docs/protocol/29-client-combat-signals.md).
- [Received-rate and independent-resource baseline](https://github.com/LocalExile/LocalExile/blob/801358d732adbc558d657d8773e273a02bc891b1/Agents/docs/re/client-damage-baseline.md).
- [PR #373 evidence comparison](https://github.com/LocalExile/LocalExile/blob/801358d732adbc558d657d8773e273a02bc891b1/Agents/docs/re/client-damage-pr373-comparison.md).

The fixed comparison inspected PR #373 at
`b0afa90ea5c58e9082099d887180df8299dcdaf3`; that PR is now merged.
Its report about historical official traffic is useful, but the private raw
captures/decoder were unavailable to the comparison. Recheck availability in Q0;
retain those samples as author-reported observations until independently checked.

Inherited static facts include a conditional `0x1af` result/feedback/control
consumer and separate `0x1b1` absolute pool assignment. Neither proves that a
particular feedback record caused a particular resource change. Network Stat
overrides and native formula evaluation also have separate contracts.

PR #466's descriptor ownership/transfer findings are a separate delivery.
Its unresolved consumers do not block or become the subject of this batch.

## Questions the contract must answer

| Edge | Required answer | Evidence discipline |
|---|---|---|
| Action identity | Which source, target, action/sequence and client mode identify this one hit? | Bind actual fields and receiver objects; do not rename an unknown field from sample values |
| Client contribution | What does the client predict or report, if anything, for this event? | Inspect the relevant send/receive paths; do not assume a per-hit C-to-S report exists |
| Server responsibility | Which externally visible decisions/results must the server provide? | Recover the external obligation without inventing the official server's internal algorithm |
| Feedback versus state | Which messages affect presentation/control, and which actually assign or change resources? | Follow concrete consumers and state writes; packet names or visible flinches are insufficient |
| Event/resource association | Can the result and resource update be tied to this event? Is an identity encoded or correlation only contextual? | Packet adjacency and a Life delta alone are not proof of causality |
| Application/order | Which guards, queueing and ordering constraints must a compatible server satisfy? | Separate arrival order, scheduled application and causal dependence; retain client-mode conditions |
| Correction/duplication | Does the selected path require reconciliation, result retention or duplicate handling? | Cover necessary branches only; do not assume a universal predict-then-correct architecture |

## Work gates

| Gate | Deliverable and acceptance | Initial state |
|---|---|---|
| Q0: evidence and witness | Freeze one scenario and client mode; inventory relevant records by exact revision/hash, availability and provenance; identify the smallest missing observation | Pending |
| Q1: responsibilities and data | Bind required messages/fields, action/entity identity and receiver/state-write paths; distinguish prediction, report, feedback/control and resource authority | Pending |
| Q2: minimal authority contract | One evidence-annotated sequence/state diagram and field/obligation table sufficient to describe the selected server interaction; establish mandatory associations and application conditions | Pending |
| Q3: delivery audit | Separate established obligations, inference and Known issues; preserve source identities and old findings, update related docs/#370, verify current main/exact head and appropriate checks before Ready | Pending |

Q0 comes first. Use existing static and historical evidence before proposing
new collection. Missing raw traffic does not invalidate a verified static
consumer; it does prevent claiming an observed packet sequence or measured
timing from that consumer alone. A current main sender describes our server,
not official-server behavior. Synthetic replay proves decoder or state-machine
behavior only within its supplied inputs.

If evidence cannot close a mandatory association, report the exact missing
record/condition and prepare the smallest observation plan, including useful
controls and provenance requirements. That plan is not itself a passing Q2.
New collection or runtime attachment requires its own explicitly authorized
scope; the bootstrap authorizes neither. The user operates the game.

## Completion and convergence

Ready requires one usable, evidence-backed external authority contract for the
selected hit, not a larger formula inventory or a list of disconnected packet
readers. Unknown seeds, numerical internals or optional branches can remain
Known issues when they do not conceal a missing mandatory contract edge.
Do not call a receiver write an attributed damage event without its association.

Work on one missing edge at a time, recording the expected evidence and bounded
search before investigating. After two investigations fail to narrow the same
edge, stop that route and review the evidence/method or scope instead of
starting another broad scan. Keep Draft while a mandatory edge is unproven;
any changed acceptance needs an explicit scope revision.

A later implementation can consume this contract. This PR performs research
and documentation only: no server changes, CI/deployment, client launch/UI,
new capture/attach, shared-analysis mutation, peer-file edits or new subagents.
Do not reopen descriptor consumers, DoT aggregation/caps, every defence, panel
formula coverage or the projectile thread's geometry/TLS work.
Use the owned coordination ledger and fixed peer references. Preserve sealed
reports/scripts/snapshots; use bounded one-shot tools without rerunning producer
mains. Public material omits local paths, usernames, raw credentials and game
artifacts. Stop after the scoped delivery is Ready; do not auto-merge, close
#370 or begin another research batch.
