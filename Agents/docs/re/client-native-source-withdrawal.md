# Generic source withdrawal through DoT rates into resources (3.29.3b)

## Result and boundary

Original whole-source and per-key removal now operate on the records created by the
[raw-add composition](client-native-source-rate-composition.md). The same interpreted memory then runs
the original outer-close numerical prefix, native getters and Life/ES loss bodies. This replaces the earlier
negative-contribution stand-in with actual removal of matching contributions and source-index entries.

Five supplied-state compositions cover **22 resource integration steps**. They establish generic source
retirement to selected numerical rates and resource changes. They do not bind an active RF, ignite, poison
or other gameplay instance to this store, its target selection, lifetime expiry or withdrawal call. Original
R2/R3/R4 remain Partial; shared-cap reachability and accepted numerical Hit deduction remain open.

Executable SHA256 is `78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5`,
preferred image base `0x140000000`. Table-derived masks and provider distinctions retain the identities in
[context eligibility](client-native-context-eligibility.md) and
[network/native resource composition](client-network-native-dot-composition.md).

## Two original removal operations

Whole-source removal `0x1424fc770(owner, token)` uses the two token words to find its inverse-index entry.
It detaches that entry's affected-key vector, removes the source-index entry, then calls per-key removal
`0x1424fc9e0(owner, token, key)` for every retained key. Repeated keys remain repeated calls: the loop does
not first deduplicate them. Both words participate in matching; a source with the same first word and a
different second word survives the controls.

Per-key removal filters **all** matching token contributions from the selected raw record, compacts the
remaining 12-byte entries, reduces the local contribution count and rebuilds the signed32 raw total from
the survivors. It publishes that total and its valid bit, then calls inverse-key cleanup `0x1424fc8f0`.
The cleanup removes matching key references from that source's key vector but retains an empty source-index
entry. A later whole-source removal can erase that empty entry.

Neither operation selects a strongest ailment, substitutes an expired instance or identifies a target.
The supplied native root has no inherited parent; inherited-record creation and refresh remain separate.
Removing the last local contribution leaves a present zero-valued raw record in these controls. Its presence
bit and active Object context remain set; source retirement is not context deactivation.

## Zero differences do not imply identical scheduling

| Path | Original scheduling in the supplied nested transaction |
|---|---|
| Nonzero per-key removal difference | Accumulates the wrapped signed32 difference in the pending map |
| Existing raw record, but no matching contribution | Rebuilds the same total and directly queues dependencies/changed-key notification despite a zero difference |
| Repeated affected key after its contributions were already removed | Takes that same zero-difference scheduling branch |
| Per-key removal with no raw record and no parent | Returns through nested transaction end without queuing this key |
| Whole-source removal with no matching inverse-index entry | Ends its nested transaction without a per-key call |

The zero-difference branch is explicit: `0x1424fcc1c..0x1424fcc1e` jumps to the direct scheduling path at
`0x1424fcc4c`, which calls `0x1424fb5f0` and `0x1424fcec0`. It is distinct from the
[outer-close pending-map rule](client-native-source-rate-composition.md), which skips a net-zero pending entry.
An existing-record/missing-token control executes one selected numerical callback after outer close even
though its final rate is unchanged. Missing-record and missing-whole-source controls execute none.
This is a cache/invalidation contract, not an additional damage event.

## Numerical withdrawal and resource witnesses

The starting supplied contributions are A: 60,000 and 30,000 incoming fire/minute; B: 30,000 fire/minute;
C: 75% fire DoT resistance; and D: fire prohibition. A and B have identical first token words and different
second words. The Life and ES cases use separate supplied routing states and run the same sequence:

| Operation, followed by outer-close numerical prefix | Raw fire/minute | Final selected loss/minute | Resource current after integration |
|---|---:|---:|---:|
| Initial state, prohibited | 120,000 | 0 | 1,000 |
| Remove whole source D | 120,000 | 30,000 | 984 |
| Remove whole source A, including both fire entries | 30,000 | 7,500 | 980 |
| Remove whole source C | 30,000 | 30,000 | 964 |
| Remove source B from fire key only | 0 | 0 | 964 |
| Remove now-empty whole source B | 0 | 0 | 964 |
| Remove an absent whole source | 0 | 0 | 964 |

Each row integrates a supplied binary32 `dt = 0.033`, starting with fractional carry 0.25. The original
resource arithmetic retains carry across withdrawals. The interval is a witness input, not a measured client
cadence or proof of server scheduling. Original native value/boolean getters supply each resource body's
rate; no scalar rate adapter or copied rate array is inserted between source records and resource loss.

Three further compositions cover removing a **negative** contribution (loss rate rises from 30,000 to
60,000/minute), removing one source that affects fire, resistance and prohibition together (0 becomes
30,000/minute from the surviving source), and the three no-change scheduling cases above. All source-list
totals/counts and selected cache values are checked against an independently expressed filter-and-sum model.
This generic summation does not recover an ailment's selection or aggregation policy.

## Evidence and remaining fixtures

Ignored evidence lives under `Tools/re/out/client-damage-audit/r70-native-source-withdrawal/`, with producer
`r70-native-source-withdrawal.py` and verifier `r70-native-source-withdrawal-verify.py` in its parent directory.
The producer reuses sealed R69 definitions without executing its main or output writes; retained source and
snapshot hashes are checked. No sealed producer or report is changed.

The five compositions interpret **482,544 instructions at 7,818 original sites**. Fresh verification checks
34 function records / 54 ranges / 2,538 decoded instructions, 31 semantic anchors, three retained assembly
views and 16 narrow original MOVSD token-copy controls. These are same-agent original-instruction and scalar
checks, not native target execution or independent-engine verification.

Still supplied: generic tokens and amounts, an open outer transaction, retained derived records and external
ready inputs, type permission/actual context masks, reserved top-level capacity, allocation/free/memory support
and an observational metadata updater. The ES case changes its supplied external bypass input and queues its
original dependencies before source activation; the earlier cached Life-bypass value is not silently reused.
Resource objects, native provider binding, reservations and empty notification/protection/checkpoint states
reuse the earlier explicit pool fixtures.

Full nested removal runs from entry to return. The outer-close run stops after the original numerical queue;
later notification/child/context-disposal processing is not executed, and queue reset/reopening remain explicit
fixtures. Source changes do not become numerical callback executions until this selected outer-close prefix.

The missing gameplay connection remains an actual applicable effect instance publishing or retiring a target
contribution/rate, with selection, cap and authority/timing rules. Independently received network rate overrides
retain their precedence, and Buff transport/removal remains a separate contract. This result does not prove
that a network Buff removal automatically clears its target's explicit aggregate rate.
