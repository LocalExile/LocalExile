# Retained action initialization and wall caches (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [012 — projectile execution](../012-projectile-execution.md).
This closes the typed registration/initialization edge between the previously recovered
[copy chains](release-and-repeat.md#31-concrete-flying-clone-handoff) and emission binding.
It also identifies the cached wall inputs passed through the concrete initialization callback.
The pinned original PE supplies the code; native Stats contribution semantics are reused from the
peer's fixed `7d8ee15d9e145af1ef66dcfea95349d5d5ce9220`
`Agents/docs/re/client-stat-evaluation.md`. No server implementation is introduced.

## 1. Registration retains first, then initializes

Registration `0x14193daf0` writes action `11e=1` and the supplied derived byte to `11c`, resolves
the Actor through the action's owner link, and appends the action to that Actor's retained vector
at `+1e8..+1f0`. It then calls action `+8` only if action byte `+124` is zero.
Actor vector `+1e8` and action flag `+1e8` are different typed fields.
The diagnostic branch selected by action `122 & 0x10` precedes this ordinary registration region.

All five final tables covering bow, wand, Fireball, KB, TS and BA bind:

| Slot | Concrete target | Meaning in this cut |
|---|---|---|
| `+8` | `0x14193a700` | Guard and latch initialization |
| `+3a8` | `0x141bb73f0` | Inherited contributions followed by conditional wall-cache contributions |

The complete no-unwind leaf `0x14193a700..0x14193a716` first tests `+124`, sets it to one when
zero, then tail-calls `0x14193bae0`. It sets the latch **before** that helper tests startup global
`0x1445b38cd`. A nonzero global bypasses the helper body, leaving `+124=1`.
Calling slot `+8` again therefore does not retry publication merely because the global later changes.
This says nothing about another caller explicitly invoking the unguarded helper or clearing the latch.

Base copy `0x141938020` clears the WORD at `+124` at `0x14193825a`, while preserving action `1e8`
at `0x1419384bc/4c4`. Shared projectile copy preserves `448` separately at `0x141ba267b/2681`.
A copied projectile can consequently retain the synthetic context and source ordinal while being eligible
for initialization again. Copying is not equivalent to retaining the source's initialized state.

## 2. Conditional publication and its exact source token

When startup global `0x1445b38cd` is zero, `0x14193bae0` performs the following ordered work:

1. Increment the contribution-adaptor nesting DWORD at action `+1a4`.
2. If the adaptor already has a destination, begin native updates on its selected destination(s).
3. Remove contributions for the adaptor's eight-byte token at action `+19c`, through `0x14193c910`.
4. Invoke final action `+3a8`.
5. Decrement the nesting DWORD, then end native updates on the currently selected destination(s).

The adaptor begins at action `+178`: its owner pointer, mode DWORD `+8` and resolved destination
pointer `+10` are distinct from the token at adaptor `+24`. Mode zero fans out through the action's
`+1b8..+1c0` 32-byte records; nonzero mode uses the resolved destination directly.
Begin and remove see the incremented nesting depth; end sees the decremented value.
The destination is read again after the callback, rather than frozen across the entire operation.

Concrete `0x141bb73f0` first calls `0x14193bbe0`, iterating the inherited `+1a8` vector's
eight-byte `(stat key, signed amount)` pairs in stored order. It then handles the two groups below.
Every contribution passes through `0x14193c850`. That adaptor returns immediately for amount zero;
it does not use zero as a request to remove an existing contribution. If needed, it calls lazy
materializer `0x14193be90`, then routes nonzero amounts to native add `0x1424fc480` using the token
address, stat key and original 32-bit amount. Negative values are not filtered out.
Inherited pairs can use the same key as a later cache contribution; this is an ordered contribution
sequence, not a last-write-wins dictionary assignment.

The native update, add/remove, materialization and notification contracts belong to the peer's
already recovered Stats machinery. Their callbacks are not presumed incapable of changing an action.
This chapter establishes the actual caller order and arguments, not all numerical consequences.

## 3. These cached fields describe walls, not continuation counts

The key/name binding is checked against `Stats.json` SHA256
`91c2ed42f6d8094a2704f56aea32f6f16243881b04aadbe4fdbbdd3a566f05da`.
Native key `k` selects JSON row `k-1`.

| Group gate | Action value | Native key / stat |
|---|---|---|
| byte `428 != 0` | signed DWORD `410` | `34fa firewall_minimum_added_fire_damage` |
| same | signed DWORD `414` | `34fb firewall_maximum_added_fire_damage` |
| same | signed DWORD `418` | `3118 firewall_buffed_projectile_burn_duration` |
| same | signed DWORD `41c` | `3119 firewall_buffed_projectile_burn_magnitude` |
| same | signed DWORD `420` | `34f9 firewall_team` |
| byte `430 != 0` | signed DWORD `42c` | `568f damage_+%_final_from_kinetic_wall` |

Construction clears both group flags. Copy `0x141ba2410` transfers the 16 bytes at `410`,
eight bytes at `420`, four bytes at `428`, and eight bytes at `42c`, including the two group flags.
The `424` field is carried in that block but is not among this callback's five Fire Wall numerical keys.
Thus already acquired cache values/flags survive the checked flying-clone copy chains;
actual acquisition, qualification and all later replacement/removal rules are separate obligations.

There is also a concrete Kinetic Wall cache adapter at `0x1413a62b0`:

```text
if incoming R8 is null or incoming RDX is null: return
value = selected view of incoming RCX action [5643]
      # kinetic_wall_damage_+%_final_for_projectiles_chaining_off_wall
0x141bb70a0(incoming RDX, value)
```

The receiver sets byte `430=1` if not already set, then **always replaces** DWORD `42c` with the
supplied value, before checking startup global `0x1445b38cd`. The old flag does not gate the replacement.
When the global is zero, it additionally submits key `568f` through `0x14193c850`; a zero amount is
therefore cached but does not submit a native contribution. With a nonzero global, the cache write
still occurs while that native publication is skipped. Animated-component/effect processing follows
these operations and can return early when the component is absent.
The [KineticBarrier contact bridge](kinetic-wall-contact.md) subsequently binds the original registration,
final client `+4f8` and a concrete owner/type/sequence-qualified contact caller to this adapter.
Actual input publication, complete collision policy and proof of authoritative damage remain separate.

## 4. Consequences for synthetic startup and later implementation

For the mapped successful ordinary-startup path, the peer's startup chain writes the global to one;
this remains conditional on no unestablished later change. In that state, registration's initialization
leaf/helper exits without executing the native contribution or notification calls above.
With spare retained-vector capacity and no diagnostic branch, the checked ordinary registration/initialization
path itself does not reset `448` or clear action `1e8`; vector-growth callbacks are a separate boundary.
It therefore supplies neither an implicit ordinary-zero ordinal nor a generic synthetic-clone cleanup.

Under global zero, native callbacks are additional stateful edges. Tests which replace them with inert
fixtures do not prove transitive preservation of those fields. The [common start](release-and-repeat.md)
still runs preparation before its synthetic-flag check, and the emitter/binder has further callbacks.
Actual pre-emission synthetic ordinals and the remaining cloned-flag lifetime are not closed by this page.
Likewise, inherited wall caches do not establish that native damage recomputation runs on ordinary
network skills. A later port must keep cache state, effective network inputs and authoritative results distinct.

## 5. Verification

Round34 `audit_initialization.py/json` retains nine functions/leaves, nineteen code ranges,
496 instructions, ten final-table bindings, nineteen anchors and seventeen rechecked prior-body anchors.
The complete 22-byte initialization leaf is bound from actual final slots rather than assigned an unwind owner.
Startup-global bytes are explicit offline inputs, not values read from a running client.

`check_initialization.py` interprets 4,465 controls / 466,066 instruction steps / 302 visited sites:
all five tables, nonzero/zero globals, initialized/copied contexts, retained insertion, direct/fanout/empty
destinations, available/absent materialization, signed/zero contributions, group flags, no retry after bypass,
and Kinetic Wall adapter null/overwrite/publication cases. The retained vector has spare capacity;
diagnostic registration, allocation failure and renderer/resource construction are outside those controls.
Native Stats primitives, source getter and missing Animated component are disclosed fixtures.

Fresh PE/Capstone review rechecks the ranges/bindings/stat names, thirty additional semantic anchors,
five literal contribution-order controls and ten adversarial callback-boundary controls.
The latter deliberately invent a callback mutation to show why the active-native branch is not a
field-preservation proof; they do not report an observed game effect. Controls share the disclosed offline
integer interpreter, not an independent native execution engine. No client run, attachment, packet capture,
server change or CI operation was performed.

Round56 [generated KB group composition](fireball-and-kinetic-blast.md#32-joining-the-generated-group-to-actor-snapshots-and-target-references)
executes this actual leaf/helper on a source-generated group under the ordinary startup-global premise.
It also executes the separate copy-time area-cache refresh, including its surviving placed-duration tail;
the initialization contribution bypass is not evidence that every cache computation is bypassed.
Post-root Actor snapshots/update/removal and target-reference operations are joined with explicit frame,
lookup, event-delivery and final cleanup boundaries. This does not repeat or extend the global-zero native
Stats contribution matrices above.

The later [target-removal/action-cleanup join](fireball-and-kinetic-blast.md#33-deferred-target-removal-and-action-cleanup-are-separate-events)
replaces explicit target delivery with the original deferred Entity queue/publisher and extends Actor retirement
into the installed KB destructor and base target unsubscribe. The completion event's `126` latch differs from
the initialization `124` latch. Nonempty component/task destruction, component lookup, resource/storage cleanup,
actual root/frame inputs and official scheduling remain outside that finite composition.
