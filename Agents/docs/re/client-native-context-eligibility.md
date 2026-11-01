# Native context eligibility and the Stats type upstream (3.29.3b)

## Result and boundary

The selected incoming DoT chain can use **actual table-derived context masks**, rather than assigning
eligibility `1` to every callback. The important enabling step is in the Stats **type configuration**:
the bare native root permits activation of context classes 1 and 2; the configuration constructor additionally
permits class 0, which includes Object. First materialization of a suitable input can then activate Object.

The client configuration uses this native object as the upstream of its network Stats wrapper. This binds
the recovered eligibility policy to that type/provider path. It does not identify a per-Actor damage-instance
publisher: the network wrapper is a separate object and does not become a native evaluation root.

Original instructions reproduce all **23,346** pinned Stats rows' eligibility and activation masks, using
40 context rows and the retained 2,539 registered callbacks / 20,384 declared dependency entries. In the
selected 105-output incoming DoT slice, 103 callbacks require Object; two have no context gate. A supplied
fire input reaches the existing mitigation/routing equations after the actual configuration/activation slices.

This remains offline static analysis and original-instruction interpretation. It does not create a real
ailment/RF instance, execute full process startup, or recover selection/aggregation/publication, shared-cap
reachability, expiry withdrawal or concrete numerical Hit deduction. Original R2/R3/R4 remain Partial.

| Input | Identity |
|---|---|
| Executable | `Client/Game.exe`, 3.29.3b, preferred base `0x140000000` |
| Executable SHA256 | `78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5` |
| Stats SHA256 | `91c2ed42f6d8094a2704f56aea32f6f16243881b04aadbe4fdbbdd3a566f05da` |
| VirtualStatContextFlags SHA256 | `cddd987614a0e55dcb9c990242c0cbd94fb679950c44ddfa9fbe7c28e1475c2b` |
| Index conventions | Stats key = JSON row + 1; context bit = zero-based context row |

## Three distinct masks

These fields have different roles; having a formula or permitting its category does not mean it has run.

| Field | Meaning recovered from its producers/consumers |
|---|---|
| Registry entry `+0x40` | Contexts accepted by that formula; zero means no context gate |
| Registry entry `+0x48` | Context bits a stat can activate when materialized/published, subject to object permission |
| Native object `+0x240` | Permission mask for such activation |
| Native object `+0x238` | Currently active context bits tested by the dispatcher |

Initializer `0x1428e68b0` partitions context rows by their unnamed `__col1`. The three values are not
client/server modes, and this report does not invent friendly names for those classes.

| `__col1` | Global | Actual mask | Selected members |
|---:|---|---|---|
| 0 | `0x14458e838` | `0xa83c0ffc1f` | Object, Skill, AttackHit, SpellHit, Damage, DamageOverTime and others |
| 1 | `0x14458e848` | `0x40000003e0` | Five typed DoT contexts and SanctumDefence |
| 2 | `0x14458e840` | `0x17c3f00000` | Ailment, attack/cast speed/time, Duration and others |

For each pinned key, the original loops implement:

```text
required(key) = OR(ContextFlags bits), if a callback is registered; otherwise 0
activation(key) = OR(DotFlag bits)
               | OR(non-class-1 ContextFlags of directly declared dependents)
```

The second term uses the original dependency-rank mapping and reverse vectors. It is not an assumed
transitive closure of `activation` masks. Repeated declarations remain repeated loop inputs; bitwise OR
does not multiply a contribution. Class-1 contexts are excluded from that dependent-context term, while
an explicit DotFlag can still include them.

The complete current table rows agree with a separately expressed scalar reconstruction. Empty ContextFlags
do not imply that a raw key's activation mask is empty. Conversely, an empty required mask does not prevent
the callback from running: `0x1424fccc2..0x1424fccd7` skips the active-context test when that mask is zero.

## Root construction, type configuration and the client wrapper

The original bare root constructor `0x1424fb790` initializes active bits to zero at `0x1424fb916`.
Its `0x1424fbafd..0x1424fbb0b` assignment sets permission to **class 1 OR class 2**, namely
`0x57c3f003e0`. It does not include Object merely because the object is a native Stats root.

Stats configuration constructor `0x141e40560` provides the next positive step:

1. `0x141e496c0` allocates/constructs a native root and stores its shared pointer at configuration `+0x10`.
2. The configuration constructor begins a transaction and a nested transaction on that same pointer.
3. At `0x141e405c7..0x141e405ce`, it ORs global class-0 mask `0x14458e838` into native `+0x240`.
4. It closes the nested transaction at `0x141e405d8`. The permission assignment itself does not set active bits.

The resulting permission is `0xffffffffff`, all 40 pinned context bits. Transaction ownership and parser
processing remain separate from the mask assignment executed in these controls.

The client configuration factory `0x142182790` calls that constructor at `0x142182808` and then installs
table `0x1435dd458`. The common and client configuration tables retain the same named-field parser
`0x141e406e0` at `+0x38`, already bound in the [type-source parser audit](client-effect-source-catalogue.md).
The client table's `+8` is `0x1421c2ad0`, the owner network-Stats construction method.

That method retains its configuration argument in RDI, passes **configuration `+0x10`** as the shared
upstream pair to `0x1421c3700` at `0x1421c2b75..0x1421c2b7c`, and stores the returned network wrapper in
component `+0x20/+0x40`. Thus the audited chain is:

```text
Stats type configuration
  → native root with class-0 activation permission
  → client configuration's component-construction method
  → separate owner network wrapper with that native root as upstream
```

This is configuration/provider provenance, not evidence that the client inserts each active poison or
ignite into that upstream object. The [network/native/resource composition](client-network-native-dot-composition.md)
retains the separate explicit override store and its final-rate precedence.

## First materialization and actual incoming-DoT masks

On the first nonzero raw record, the original native-add slice `0x1424fc5e9..0x1424fc62f` performs:

```text
active |= registry[key].activation & permitted
presence_bitmap[key] = true
```

The earlier existing-record branch at `0x1424fc540` skips this slice. The full zero-amount add fast path
is also exercised: `test r9d,r9d; je ...; ret` takes three original instructions, makes no call and leaves
the masks/presence word unchanged. A zero declaration is therefore not this first-nonzero activation event.
These controls execute the eligibility/presence slice under its stated entry condition; they do not
replace the missing source-instance producer with a simulated complete native-add operation.

| Input key / role | Actual activation | Bare root gains | Type-configured root gains |
|---|---|---|---|
| 2023 incoming fire base | Object (`1`) | none | Object |
| 8215 incoming ignite base | Object (`1`) | none | Object |
| 8216 incoming poison base | Object (`1`) | none | Object |
| 883 / 1947 / 3399 outgoing RF Life/ES/Mana constants | FireDot (`64`) | FireDot | FireDot |
| 125 base maximum Life | Object (`1`) | none | Object |
| 139 ES recharge-rate percentage | Object (`1`) | none | Object |
| 131 Mana regeneration-rate percentage | zero | none | none |

For 2023/8215/8216, both their ContextFlags and DotFlag lists are empty; Object comes from directly
declared dependent formulas. The RF constants explicitly name FireDot in DotFlag. Incoming fire damage
and outgoing fire-skill damage therefore need not share the same formula context just because both are fire DoT.

The selected incoming slice has exactly two ungated callbacks: 18922 `cold_damage_over_time_resistance_%`
and 18923 `lightning_damage_over_time_resistance_%`. Its other 103 outputs require Object. Before activation,
only those two callbacks execute in the supplied selected queue. A bare root cannot gain Object from the
2023 first-materialization slice, so the supplied incoming fire/loss outputs remain zero in that control.

In a separately initialized type-configured root, that same slice activates Object. The original selected
queue then executes all 105 callbacks. With incoming fire 60,000/minute and Life bypass supplied, the fire
and Life loss rates become 60,000; changing fire DoT resistance to 75 produces 15,000, enabling fire prohibition
produces zero, and clearing it restores 15,000. The changes use the original dirty queue and numerical callbacks.

## Evidence, limits and next connection

Ignored evidence under `Tools/re/out/client-damage-audit/r68-real-context-masks/` retains all mask rows,
original function/range records, supplied-state histories, visited sites and the finite navigation inventory.
Producers/verifiers are `r68-real-context-masks.py` and `r68-real-context-masks-verify.py` in the audit parent.
They reuse prior definitions and immutable declaration/rank data; no sealed producer main is rerun.

The run interprets **2,565,173 instructions at 6,249 original sites**, including 18 activation-slice/zero-add
controls, bare/type-configured incoming comparisons and all 23,346 mask initializations. Fresh verification
checks original instructions, table hashes, configuration slots and constructor/activation anchors. These are
same-agent original-instruction and scalar controls, not an independent engine or an actual client execution.

A retained navigation pass found 432 untyped qword-write candidates with exact positive displacements
`0x238/0x240`, and four direct native-root constructor calls. Those offsets alone identify no Stats object.
The configuration constructor became a positive lead through its native-root construction and field dataflow;
the remaining candidates are not globally classified or used as an absence proof. Do not repeat that scan
as a new source-publisher search.

Still supplied: packed table rows/handles and lookup support, retained dependency ranks/reverse-vector storage,
selected native records/cache/initial queue and between-drain cleanup. Only the root/configuration mask
assignments and first-materialization slice are executed from those constructors/addition paths. Full configuration
parsing, native raw record creation/source accounting, complete Actor creation and event-driven publication are
not executed as one composition.

The next required evidence remains a concrete active damage source and applicable target publishing or
withdrawing a numerical contribution/rate, with selection/aggregation/cap policy. Type-level eligibility and
formula existence cannot supply that missing relationship. Hit damage-to-resource and generic existing-Buff
timing refresh also retain their separate open criteria.
