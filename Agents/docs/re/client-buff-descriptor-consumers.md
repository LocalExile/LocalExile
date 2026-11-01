# Mod-derived Buff descriptors: ownership and transfer boundaries

## Objective and current state

Document the established ownership, storage and structural transfer of one
prepared Mod-derived Buff descriptor. The selected slice begins with Mod row 7300
`ChaosDegenerationAuraPlayersUniqueBodyStr3`, BuffTemplates row 130 and its
foreign-key-linked `unique_deaths_oath_aura` definition. Rows are zero-based
extracted ordinals for the pinned 3.29.3b artifact.

PR #466 delivers this structural evidence under issue #370. Its scope was
explicitly revised to publish the established findings and retain unfinished
consumer research as Known issues. Readiness applies to this documentation
delivery; it does not mean the original consumer investigation passed.
Original N1 remains partial, original N2 remains open, and R2/R3/R4 stay Partial.

## Base and inherited evidence

This branch starts from main `d377bd256036242d2fb43f53eb7110b4897e1862`.
PR #371 is still open at setup time; its documentation is an explicit dependency,
not an implementation dependency or a stack of changes imported into this PR.
Reuse its fixed head `801358d732adbc558d657d8773e273a02bc891b1`:

- [Prepared descriptor evidence](https://github.com/LocalExile/LocalExile/blob/801358d732adbc558d657d8773e273a02bc891b1/Agents/docs/re/client-mod-buff-descriptors.md).
- [First Hit/DoT baseline](https://github.com/LocalExile/LocalExile/blob/801358d732adbc558d657d8773e273a02bc891b1/Agents/docs/re/client-damage-baseline.md).
- [Original-byte and support identities](https://github.com/LocalExile/LocalExile/blob/801358d732adbc558d657d8773e273a02bc891b1/Agents/docs/re/client-damage-evidence.json).

The inherited executable SHA256 is
`78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5`;
addresses use preferred image base `0x140000000`. Recheck identities before new
byte claims. Unsealed earlier navigation is a lead, not an accepted finding.

| Existing boundary | Established starting fact | Next question |
|---|---|---|
| Mod/template association | Actual foreign rows link the selected Mod, template and Buff definition | Do not infer recipient or gameplay semantics from the names |
| Descriptor preparation | `0x1421ff3c0` calls the selected prefix of `0x1421ff8f0`; defaults are copied and matching Mod values overwrite slots | Who reads this same descriptor after preparation? |
| Distinct storage | `0x68`-byte descriptors in destination vector `+0x160/+0x168/+0x170`; ordinary aggregate is separate at `+0x140` | Prove object provenance; matching offsets in another structure are insufficient |
| Numerical boundary | The inherited prefix stops at `0x1421fff44`; selected supplied Mod input produces descriptor value `27000/min` | This is not yet target effective damage, a gameplay Buff or a snapshot-time result |
| Separate downstream contract | Received final rates can reach local Life integration | Do not invent an edge from this descriptor to that rate |

## Owner and transfer findings

Original bytes bind two embeddings of the prepared structure. For the selected
item Mod, use the **Mods component's `+0x38` embedding**, not the entity
ObjectMagicProperties component's `+0xc8` embedding. The component names and
vtables are independently indexed in [components](components.md); the new
[focused byte index](client-buff-descriptor-owner-evidence.json) checks their
actual constructor/reader/assignment links against the pinned executable.

```mermaid
flowchart TD
    M["Item Mods component M"] -->|"reader 1421df6e0: P = M + 38"| P["Prepared properties P"]
    P -->|"142201d70 tail-calls 1421ff3c0"| B["R75 preparation: selected Mod if supplied"]
    B --> V["P + 160 vector; 0x68-byte descriptors"]
    V -->|"1421debb0 → 1421deda0"| C["Structural assignment to another Mods object"]
    V -. "K1: actual reader unresolved" .-> U["K2: consumer purpose / output unresolved"]
    P --> K["Separate pointer P + 1d8"]
    K --> Q["Cache + 160: 0x28-byte records; different vector"]
```

All diagram offsets are hexadecimal. Solid edges describe conditional static
code paths, not a recorded Death's Oath activation. The selected Mod/template
mapping and `27000/min` preparation remain inherited R75 evidence.

| Relation | Original anchors | What is established |
|---|---|---|
| Item embedding | `0x1421df1d0/1d4`, `0x1421fec57/5e/65` | Mods constructor initializes `P = M + 0x38`; descriptor vector is at `M + 0x198/+0x1a0/+0x1a8` |
| Item reader to preparation | `0x1421df9a0/9a7`, `0x1422024bf/4dc` | Same `P` enters the base reader and reaches the preparation tail-call after successful parsing |
| Entity embedding, separate owner | `0x141e7762b/658`, `0x141e779b8/9bf` | ObjectMagicProperties uses `P = E + 0xc8`; its descriptor vector would be at `E + 0x228/+0x230/+0x238` |
| Item structural transfer | `0x1421dec21/25/29`, `0x1421defb5/bc`, `0x1421deff0` | The actual Mods assignment slot passes source/destination `+0x38`; the prepared-object assignment dispatches its `+0x160` vector to the `0x68`-record assignment helper |
| Assignment loop | `0x1421e20d0/d3/d6/df/e3` | Existing destination/source elements are passed to `0x1421e23d0` and advanced by `0x68`; this is a transfer, not an identified gameplay/configuration output |
| Misleading cache candidate | `0x14220469f/6b5/6bc`, `0x142203bdd/be4/beb/c91` | The getter first dereferences `P + 0x1d8`, then reads another object's `+0x160/+0x168`; its count uses `0x28`, not `0x68` |

The byte index retains 11 PE-unwind function identities, 56 selected instruction
anchors and three exact vtable slots. Five retained R76 function records were
also compared with fresh decoding and original range hashes. This verifies the
locations and conditional structural paths; it does not execute an item, prove
arbitrary intervening calls preserve state, or classify a meaningful consumer.

### Bounded investigation and next evidence

Investigation A started at the prepared vector and followed the selected owner
methods, exact descriptor lifetime-helper references and typed initialization
callers. It established the embeddings and assignment path above. A bounded
direct-caller query of `0x1421ff1b0` covered 229 sites in 161 unwind owners;
an embedded-offset/stride prefilter and the earlier R76 navigation were used
only to propose candidates. Offset/stride matches were not accepted as types.
These are search coverage, not discovered consumers or new damage formulas.

Stop expanding these offset inventories. The `0x142204680` route is excluded
from this descriptor path by positive pointer provenance. Copy, move and
cleanup routes remain structural evidence only. The unbound `0x1423f5ef0`
getter is not promoted to a descriptor reader because its object type is unknown.

The next admissible investigation needs a concrete escape or non-lifetime read
of **this** vector: for example, an original caller carrying `P + 0x160` into a
reader, or independently retained typed evidence identifying an indirect reader.
Then follow that one reader to its actual output. Do not infer no consumer,
server ownership or presentation-only behavior from unsuccessful searches.

Two bounded getter-provenance investigations did not bind a descriptor escape;
that route is closed until new typed evidence appears. One getter's inspected
bindings use a separately initialized state pointer. The other returns an
unresolved resource-owned row; its identity is insufficient to classify it as
this prepared object. Neither result establishes a meaningful descriptor consumer.

A final coverage repair checked previously omitted `rbp` operands within the
same 161 retained ensure-call owners. Six candidate functions contained 29
matching operands. Their prologues establish `rbp` as a stack-frame base, with
no subsequent business-object reassignment in the decoded functions. Those
operand matches therefore do not establish prepared-object fields. This does
not exclude descriptor-related stack temporaries or other consumers. Stop this
finite query as well; do not replace the missing typed escape with another
global offset, stride or getter inventory. These results delimit the unfinished
consumer research; they do not prevent delivery of the structural findings.

## Known issues and re-entry evidence

| ID | Unresolved connection | Evidence needed to resume |
|---|---|---|
| K1: actual reader | No purpose-bearing reader is bound to this same descriptor; original N1 is partial | An original call/field path carrying this vector or a proven descriptor into a non-lifetime reader, or independently typed evidence for an indirect reader |
| K2: reader output | Original N2 is open: numerical use versus configuration/dependency/presentation use is unclassified | After K1, trace guarded inputs and operations to an actual output; for numerical use identify the recipient, Stat/provider, contribution token or equivalent endpoint |
| K3: damage connection | Target eligibility, actual Buff application, snapshot/update/removal and target rate publication remain unknown | Independent original-byte or separately scoped runtime evidence connecting the descriptor consumer to those operations; the separate received-rate contract alone is insufficient |

The inherited `27000/min` remains descriptor preparation input, not observed
target damage or an established effective rate. Failed searches do not prove
that no consumer exists, that all consumers are presentation-only, or that
the missing operation belongs to the server.

K1–K3 remain follow-up research under issue #370. They are not acceptance
requirements for the revised ownership/transfer delivery. A later batch must
define its own entry evidence and acceptance criteria before reopening them.

## Claim audit at this boundary

| Claim | State |
|---|---|
| Prepared-object embeddings and selected structural assignment | Supported by the focused original-byte index |
| The cache getter reads these prepared descriptors | Rejected for this candidate: the intermediate object and stride differ |
| A meaningful reader/output for the selected descriptor | K1/K2 unresolved; copying and cleanup do not complete the original N1/N2 |
| `27000/min` is actual target damage, or client DoT publication is recovered | Not established; original R2/R3/R4 remain Partial |
| The original consumer investigation passed N0–N3 | Not established; original N1/N2 remain unfinished |
| PR #466 can be reviewed as an ownership/transfer delivery | Revised scope: structural findings plus explicit Known issues; final publication checks apply below |

## Delivery acceptance

| Requirement | Acceptance evidence | State |
|---|---|---|
| Fixed slice and dependency | Selected Mod/template, pinned artifact and PR #371 references above; no imported baseline diff | Established |
| Owner/storage and transfer | Diagram, offset/call contract and focused original-byte index; distinct cache positively excluded | Established within the stated static guards |
| Limits and follow-up | K1–K3, bounded coverage and claim audit; original consumer gates and R2/R3/R4 not promoted | Explicit |
| Publication validation | Current main ancestry, exact published head/body, pinned source identities, byte/anchor/slot checks, relative links/privacy/whitespace and required build/test results | Record in the PR; Ready only after these checks pass |

The former N0–N3 plan required a meaningful consumer and its output. It is
retained here as an unfinished research objective, not retroactively marked
complete. This delivery accepts the owner/transfer findings and their explicit
boundary. Neither documentation readiness nor passing build/tests establishes
gameplay numerical behavior or live parity.

## Follow-up boundaries

The stopped getter and operand-inventory routes remain stopped until new typed
evidence appears. Do not repeat broad offset/stride/getter searches, preparation
catalogues or parent-table scans. Once a future path is positively classified,
record its actual output and stop unrelated configuration/rendering branches.

Protect sealed scripts, reports and snapshots; do not rerun producer mains.
Use bounded one-shot tools and the owned coordination ledger. Do not duplicate
Actor/projectile/World/protocol-ingress work or send routine peer messages.
After this revised delivery passes validation, mark #466 Ready and stop.
Do not merge automatically, close issue #370 or start another research batch.

## Exclusions

Research/documentation only: no server mechanic changes, client launch/attach/UI,
new capture, CI/deployment, shared REA/Ghidra mutation, peer-file edits or new
subagents. No global Mod/unique/passive/Buff/aura catalogue; no ignite arbitration,
poison aggregation, shared DoT cap, generic time warp, accepted-Hit numerical
application or other special-mechanic expansion. Reuse #371's established
formulas and receiving/integration contracts instead of rederiving them.

Publish only repository-relative references or fixed public source links;
omit machine-specific paths/usernames. Keep target binaries, extracted data,
full decompiler output and private evidence out of Git. Synchronize the relevant
Status/Roadmap and issue #370 when actual findings change, with evidence classes
and unresolved links intact.
