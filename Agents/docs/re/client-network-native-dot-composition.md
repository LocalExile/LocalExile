# Network override, native DoT records and resource integration (3.29.3b)

## Result and scope

The original network-wrapper constructor, override getter/setter and notification dispatcher compose with
the recovered native DoT dependency queue, materialized records and Life/ES loss bodies in one interpreted
address space. This removes two earlier numerical adapters: R29's scalar upstream fallback and R45's direct
native-provider binding at the resource boundary.

An explicit final incoming-rate entry wins over the native result, **including an explicit zero**. With that
entry absent, the original wrapper getter delegates to the native getter and reads the computed record.
The resource step therefore does not need a copied-rate adapter to consume either representation.

This is offline interpretation of original instructions and supplied-state composition, not native execution.
Native eligibility, ready external inputs, selected records and enclosing Entity/Stats construction remain
supplied. This does not identify an actual damage-source instance or prove that ordinary client Actors evaluate
the full native chain. It does not close source selection/aggregation, shared-cap reachability, expiry withdrawal
or concrete numerical Hit deduction. Original R2/R3/R4 remain Partial.

| Input | Identity |
|---|---|
| Executable | `Client/Game.exe`, 3.29.3b, preferred base `0x140000000` |
| Executable SHA256 | `78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5` |
| Stats SHA256 | `91c2ed42f6d8094a2704f56aea32f6f16243881b04aadbe4fdbbdd3a566f05da` |
| Key convention | Native/wire key = zero-based `Stats.json` row + 1 |

## Original object and dispatch boundaries

The owner-Stats constructor writes byte `1` at `0x1421c2af9`, passes its address in R8 at
`0x1421c2b69`, and calls wrapper constructor `0x1421c3700` at `0x1421c2b7c`.
The complete wrapper constructor is interpreted with that flag. It installs primary table `0x14363bd90`,
the publisher/listener subobjects, native parent at `+0xe0`, control block at `+0xe8`, and an empty override
vector at `+0xf0/+0xf8/+0x100`. The nonzero flag skips its native-parent subscription block and leaves
subscription token `+0x108` null. This flag choice is original static evidence; enclosing component creation
and selection of the supplied native owner are not executed here.

Both null and nonnull control-block variants are exercised. For the nonnull variant, supplied count seven
becomes eight through two original `lock inc` instructions and one original `lock xadd` decrement. The
interpreter models their sequential arithmetic and flags, with wrap/carry controls; it does not simulate
concurrency, final release or destruction.

| Object/operation | Original entry or table slot | Executed contract |
|---|---|---|
| Network numeric getter | `0x14363bd90 + 0 → 0x1424cfa40` | Binary-search explicit entries; otherwise tail-dispatch to native parent; absent parent returns zero |
| Network Boolean getter | `0x14363bd90 + 8 → 0x1424cfad0` | Numeric getter followed by nonzero test |
| Native numeric getter | `0x14363d3b0 + 0 → 0x1424fc1e0` | Read the supplied, present native record |
| Native Boolean getter | `0x14363d3b0 + 8 → 0x1424fb5a0` | Original Boolean boundary |
| Network update | `0x1424cfaf0 → 0x1424cfd00` | Insert/replace one explicit key and notify subscribers |
| Life incoming loss | `0x141e6ae70` | Read effective incoming Life rate 3362 through the wrapper and integrate it |
| ES incoming loss | `0x141e6b480` | Read effective incoming ES rate 3361 through the wrapper and integrate it |

The getter's explicit-entry branch returns the stored signed value; it does not test whether the value is
positive before choosing between explicit and native storage. A stored zero consequently suppresses native
fallback. Negative values are also preserved by the getter; the selected resource loss steps integrate only
positive rates. Setting zero is not removal of an override entry.

The parent getter reads materialized records. This composition does **not** turn fallback into a claim of
lazy evaluation of every native dependency at the time of a resource read.

## Numerical composition

The selected native slice reuses the 105 registrations, rank/dirty/dispatch instructions and original
previous-value getter from [native queue composition](client-dot-native-composition.md) and
[native records to pool loss](client-native-dot-pool-composition.md). Fire input 2023 starts at 60,000
damage per minute, with effective fire DoT resistance input 17171 initially zero. The Life case additionally
supplies ES bypass 9677 = 100; the ES case routes to ES. These are supplied state inputs.

Each following row occurs in sequence; override entries remain present after insertion.

| State change | Native resource rate | Explicit final rate | Resource step reads |
|---|---:|---:|---:|
| Initial state | 60,000 | absent | 60,000 |
| Native fire DoT resistance becomes 75 | 15,000 | absent | 15,000 |
| Native fire prohibition becomes active | 0 | absent | 0 |
| Native prohibition clears | 15,000 | absent | 15,000 |
| Network final rate becomes 6,000 | 15,000 | 6,000 | 6,000 |
| Native prohibition becomes active again | 0 | 6,000 | 6,000 |
| Network final rate becomes zero; native prohibition clears | 15,000 | 0 | 0 |
| Network raw fire input 2023 becomes 120,000 | 15,000 | 0 | 0 |
| Network final rate becomes 9,000 | 15,000 | 9,000 | 9,000 |

This sequence is checked for Life and ES, three supplied time deltas (0.016, 0.033 and 0.1 seconds), and
both control-block variants: 12 sequences / 108 resource steps. These deltas test integration arithmetic;
they are not measurements of official frame cadence.
The explicit final rate is a downstream input at this client boundary. Its precedence over a zero native
result does not prove that the official server allows fire damage through immunity; upstream authoritative
rate calculation and sending policy remain separate.

Two additional raw-input isolation controls leave the final-rate override **absent**. Setting network key
2023 to 120,000 makes the wrapper report that raw value, while the native cache still contains 60,000 and
the resource rate remains 15,000. Updating the supplied native input to 120,000 through its original dirty
queue then changes the fallback resource rate to 30,000.

Thus, within the executed subscriber set, a network override of an upstream raw key is not an automatic
write into the native formula context. The setter preserves the native cache, selected records and pending
queue, and invokes none of the native scheduler/getter/mutation entries checked by this composition.
Other actual subscribers or enclosing update orchestration must be established separately; this is not a
whole-client claim that raw updates can never affect native state.

The real notification dispatcher invokes an observational subscriber that reads the newly stored value
through the original getter. The observer uses the actual sender argument, so two separately constructed
wrappers sharing one supplied native owner retain independent override stores. That is an object-isolation
control, not evidence that ordinary Actors share native owners.

## Per-minute rates and fractional integration

The resource rate is damage **per minute**, rather than an amount accumulated during a rolling minute.
For positive effective rate `r` and supplied step `dt`, the original binary32 operation orders remain:

```text
Life delta = -F(F(F(r) / 60) * F(dt))
ES delta   = -F(F(F(r) * F(dt)) / 60)
```

`F` denotes binary32 rounding. The resource reducer combines this delta with retained fractional carry,
truncates the integer portion and retains the remainder. Each step is compared against the separately
expressed scalar reducer. The distinction in multiplication/division order is preserved.

This proves how a selected effective rate reaches a frame amount through this boundary. It does not
recover an instance-level shared cap publisher or justify applying a new cap to a final network override.
The earlier [typed routing and cap audit](client-dot-routing.md) retains its own boundaries.

## Reproduction and limitations

Local ignored evidence:

- `Tools/re/out/client-damage-audit/r67-network-native-pool.py`
- `Tools/re/out/client-damage-audit/r67-network-native-pool-verify.py`
- `Tools/re/out/client-damage-audit/r67-network-native-pool/{audit,verification,byte-verification,visited-instructions}.json`

The producer makes definition-only imports of prior models; no sealed producer main is rerun. The retained
run covers 17 composition cases, ten sequential atomic-arithmetic controls and **124 resource steps**.
It interprets **1,567,280 instructions at 6,893 original sites**. Signed overrides additionally cover
`INT32_MIN`, -1, zero, one and `INT32_MAX`, using a small surviving step. Fresh verification checks original
instruction boundaries, constructor/getter/setter anchors, four original virtual slots, and prior sealed evidence.

Remaining fixtures include:

- Selected native declaration graph, materialized records, ready external cache and eligible masks;
  explicit queue-end cleanup between native drains and observational function-index updater.
- Supplied resource/Entity/component environment, World checkpoints, reservations and empty gameplay listeners.
- Imported synchronization, reserved vector capacity, allocation/memmove support and the observational subscriber's
  ABI stack space. Constructor flag-zero subscription, vector growth and refcount destruction are not executed.
- Direct invocation of the network setter. The existing [parsed Stats update composition](client-network-rate-composition.md)
  establishes its separate inbound entry; no new packet capture or official send order is claimed here.

The result strengthens the numerical network/native/resource boundary. Actual instance infliction,
selection/aggregation/publication, ordinary native eligibility, cap reachability and expiry withdrawal,
generic existing-Buff timing refresh, numerical Hit deduction and their full C/S timing remain open.
