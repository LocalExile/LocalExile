# Native source contributions to incoming DoT rates (3.29.3b)

## Result and boundary

The original raw-add entry `0x1424fc480` now creates and updates selected raw records before the existing
DoT numerical queue runs. The earlier composition supplied raw input cache values and exercised only
the first-materialization eligibility slice. These controls instead execute the complete raw-add function
under a supplied open outer transaction: sorted record insertion, contribution-list allocation/append,
inverse source-key indexing, cache/validity publication, context activation and pending-change accumulation.
An original outer-close prefix then schedules and evaluates the selected incoming DoT graph.

This establishes **generic stat-source contributions to selected numerical rates**, not actual RF/ignite/poison
instance creation or target publication. Source tokens and amounts are supplied. No maximum-ignite selection,
poison stacking rule, actual source withdrawal or per-target shared-cap reachability is inferred from this store.
Original R2/R3/R4 remain Partial.

Executable SHA256 is `78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5`,
preferred base `0x140000000`. Stats and context data retain the identities in
[actual context eligibility](client-native-context-eligibility.md).

## Original contribution and transaction path

| Step | Original connection | Recovered consequence |
|---|---|---|
| Entry | `0x1424fc480` tests the signed amount in R9D | Zero returns in three instructions, before source bookkeeping or transaction changes |
| Nested begin | `0x1424fc4c5` calls `0x1424fd240` | An already open outer transaction increments its nesting depth without replacing the supplied evaluation context |
| New key | `0x1424fe2e0`, reached at `0x1424fc5cd` | Inserts a sorted 40-byte record; the selected middle-insertion path moves existing records with `0x1424fe5d0` |
| Qualification | `0x1424fc5e9..0x1424fc62f` | Activates permitted context bits and marks record presence, using retained actual metadata masks |
| Contribution | `0x1424fc63a..0x1424fc667` | Adds into the signed32 raw total and appends a 12-byte token/amount entry; the first list allocation runs `0x140238ad0` |
| Cache | `0x1424fc67e..0x1424fc68f` | Writes the new raw total and its valid bit into the evaluation context |
| Source index | `0x1424fc6f8` calls `0x1424fdad0` | Records affected keys under the source token; repeated contributions retain repeated keys |
| Deferred changes | `0x1424fc715..0x1424fc71e` | Original key-map lookup/insertion accumulates signed32 net deltas while nested |
| Nested end | `0x1424fc742` calls `0x1424fd390` | Decrements depth and returns while the outer transaction remains open |
| Outer-close prefix | `0x1424fd390..0x1424fd4e7` | Queues nonzero pending changes, marks changed keys, clears the pending list and runs the original growing numerical queue |

The inverse source index compares the token's first 32-bit word as signed and its second word as unsigned.
The controls include negative first words, equal first words with different second words, a zero token and
insertion before existing source records. This is a storage-order contract, not an Entity/source identity encoding.

Raw totals and pending deltas use ordinary 32-bit arithmetic. A supplied `2,147,483,647 + 1` wraps to
`-2,147,483,648` at this raw-store boundary. That is distinct from clamps inside derived DoT callbacks and
does not establish the gameplay shared DoT cap.

## Numerical witnesses

Each operation below closes the supplied outer transaction through the original numerical loop before the
next operation. All amounts are per-minute stat inputs; they are not per-frame damage withdrawals.

| Supplied operation | Raw incoming fire | Final Life-loss rate |
|---|---:|---:|
| Token A contributes 60,000 to key 2023 | 60,000 | 60,000 |
| Token A contributes another 30,000 | 90,000 | 90,000 |
| Token B contributes -15,000 | 75,000 | 75,000 |
| Separate source contributes 75 to fire DoT resistance key 17171 | 75,000 | 18,750 |
| Separate source contributes 1 to fire prohibition key 4522 | 75,000 | 0 |
| That same source contributes -1 to key 4522 | 75,000 | 18,750 |

Token A retains two entries and two affected-key references. The last operation appends another contribution;
it is not source removal. While the outer transaction remains open, raw cache values change but the selected
derived rates and numerical callbacks remain unchanged. The outer-close prefix produces the table above.

Four additional batch controls cover balanced `+60,000/-60,000`, simultaneous fire/resistance inputs, signed32
overflow and six source-token ordering cases. Balanced changes leave a zero pending delta; the original
outer-close prefix skips that key's dependency scheduling, and no selected numerical callback executes.
The simultaneous fire/resistance batch produces 15,000/minute. Six supplied contributions of 1 through 6
produce 21/minute. A separate zero-add control changes neither records, source index, pending changes nor
context activation.

The [native record/getter-to-pool](client-native-dot-pool-composition.md) and
[network/native/pool](client-network-native-dot-composition.md) compositions remain separate reusable evidence.
This run does not itself execute resource integration or create a network wrapper. Native raw cache mutation
here must not be confused with an explicit update in the network wrapper's distinct override store.

## Evidence and remaining fixtures

Ignored evidence is under `Tools/re/out/client-damage-audit/r69-native-source-add/`, produced by
`r69-native-source-add.py` and checked by `r69-native-source-add-verify.py` in its parent directory.
Six supplied-state compositions interpret **469,060 instructions at 6,956 original sites**. Fresh verification
checks 13 function records / 32 ranges / 1,103 decoded instructions, 28 semantic anchors and 80 narrow MOVSD
data-transfer / unsigned multiplication controls. The new unwindless map-insertion leaf is bound by the
actual direct call at `0x1424efa97`; both returns and every direct branch fit its explicit retained range.
These are same-agent original-instruction and scalar controls, not an independent execution engine or live client.

Still supplied: an open outer transaction, the selected dependency graph, existing derived records and external
ready inputs, type-configured permission and table-derived masks, empty stores with reserved record/index/map
capacity, allocator/free/memory support and an observational metadata-function updater. Generic source-list
allocation and scalar/vector movement execute original code; top-level store capacity growth, inherited-parent
record creation and nonempty metadata-observer consequences are not covered.

The outer-close run stops after its numerical queue. Subsequent notifications, native-child propagation and
context disposal are not executed; queue cleanup and preparation for the next supplied outer transaction remain
explicit fixtures. No whole-transaction or ordinary Actor simulation is claimed.

The next missing connection remains an actual applicable damage-source instance choosing/publishing or
withdrawing a target contribution/rate. This generic accumulator supplies neither that selection policy nor
server scheduling/authority. Accepted numerical Hit deduction and generic existing-Buff timing refresh retain
their separate open criteria.
