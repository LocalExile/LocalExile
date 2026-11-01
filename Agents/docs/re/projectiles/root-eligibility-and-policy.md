# Root action eligibility and Actor startup policy (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [RE 012](../012-projectile-execution.md). This extends the
[Actor runner](world-scheduling.md#21-native-runner-current-root-admission-and-startup-ordering)
at its two explicit boolean boundaries. It does not rerun the full runner with these replacements.
All addresses refer to the pinned executable in the [evidence ledger](evidence-and-open-questions.md).

## 1. Concrete bindings and ordering

**S:** the five final action tables for ordinary bow/wand, Fireball, KB, TS and BA bind:

| Slot | Concrete target | Role in the checked caller |
|---|---|---|
| Action `+0x128` | `0x14193c990` | ordered callback qualification |
| Action `+0x130` | `0x14193c9e0`; Fireball `0x1419eeee0` | action eligibility |
| Action `+0x208` | `0x1400ed4a0` | false leaf, queried by the speed gate |
| Action `+0x210` | `0x14193eb80` | category `0x30` and action byte `0x11e == 0` |
| Action `+0x358` | `0x14193aa00` | owner Stats `0xa17`, `uses_no_mana` |
| Actor `+0x198` | `0x141e9d930` | subsequent startup policy |
| Actor `+0x140` | `0x141651830` | returns embedded manager `Actor +0x598` |

The runner's `0x141e94620` first checks its flags/metadata, then action `+0x130`, then Actor `+0x198`
with skill and action `+0x340` result. These action tables' `+0x340` is an actual integer-zero leaf.
Startup follows only after both predicates pass. Earlier facing/metadata failure does not reach these bodies.
The action `+0x128` callback slot is unrelated to the data field at action `+0x128`.

## 2. Shared action eligibility

**S:** `0x14193c9e0` rejects in the following order; later tests are not executed after failure:

1. Call `+0x128`. `0x14193c990` walks action `+0x250..+0x258` in 16-byte records, invoking the object at
   record `+8` through slot zero. First false stops traversal; an empty vector succeeds.
2. If `+0x210` is true, reject when action `+0x122` bit 5 is set.
3. Read selected-view boolean `0x3db6`, `skill_disabled_by_stat`. A nonzero value rejects unless action's
   `u16 +0x118` is exactly `0xc266` or `0xd24f`. The latter identity is not resolved here.
4. If the owner has a Player component, check target distance, then the optional action-speed test below.
5. Call cost-affordability wrapper `0x14193aa30`.
6. Call reservation wrapper `0x14193ab70(action, 1)`.
7. When action byte `+0x154 == 0` and owner Actor exists, require Actor `+0x1a0(skill)` to pass.
   The underlying callback remains a supplied input in these controls.
8. For nonnull skill in category 1, reject selected `0x4c31`, `cannot_attack`.

Fireball's `0x1419eeee0` first rejects a nonnull category-2 skill with selected boolean `0x28ea`,
`virtual_cannot_cast_spells`; otherwise it tailcalls the shared predicate. A null skill passing this prefix
does not establish overall success: later shared helpers dereference the skill.

### 2.1 Player distance and speed

The source is owner Positioned's packed integer pair at `+0x294`; the target is action `+0x130`.
If action data `+0x128` is nonnull, its Positioned pointer `+0x98` supplies **signed byte** `+0x1e3`.
`0x141939ec0` computes wrapped integer coordinate differences, obtains the native length via
`0x14015d1e0`, and for positive radius subtracts it with a zero lower bound.
The caller squares the binary32 result and rejects only when it exceeds binary32 **14400**, at `0x1436a4808`.
For axis-aligned finite controls, 120 passes, 121 fails, and radius 1 permits 121. A negative radius is ignored.
These are grid-coordinate distances, not a proved conversion to displayed metres. Wrapped arithmetic must
not be replaced by an unbounded mathematical distance when describing the original code.

Still inside the Player branch, action `+0x122` bit 7 or 11 skips the speed test. Otherwise `+0x208` may
also skip it, but the checked final bindings return false. If owner Stats exists, its provider `+0x20`
reads integer `0xce6`, `virtual_action_speed_+%`; signed values **<= -100** reject. This reads owner Stats,
not the action-selected provider used by the disable tests. Non-Player owners skip both distance and speed.

### 2.2 Cost and reservation are separate preflights

Cost wrapper `0x14193aa30` returns true for nonzero action `+0x11f`, true `+0x358`, or null skill `+0x48`.
Otherwise it retains the skill handle, prepares a 32-byte cost record through `0x141ee7320` and passes a
copy to `0x1419418e0`. Preparation and the underlying affordability API remain explicit boundaries;
this call is not evidence that a cost was paid. The second `+0x358` read in preparation is retained.

Reservation wrapper `0x14193ab70` separately returns true for null skill `+0x48` or true `+0x358`.
Action `+0x11f` alone does **not** skip it. With action `+0x122` bit 7, it enumerates skill-associated Buffs;
types `0x5cf`, `0x988`, `0x989` permit success after temporary-vector cleanup. Their names are not bound here.
Otherwise it reads the selected view in this order:

| Key | Stats row name | Destination argument |
|---|---|---|
| `0x2eb8` | `mana_reservation` | Mana absolute |
| `0x3771` | `mana_reservation_permyriad` | Mana permyriad |
| `0x376b` | `life_reservation` | Life absolute |
| `0x3772` | `life_reservation_permyriad` | Life permyriad |

Any `INT_MAX` value rejects before pool calls. Each pair is multiplied by the supplied count with dword
wrapping; the root caller supplies 1. It calls `0x141e6b8e0` for Mana at `Life +0x1c8`, then only on success
for Life at `Life +0x178`. These pool checks remain API inputs here. Reuse the fixed peer resource research
for numerical semantics; do not conflate reservation with ordinary use-cost payment.
The native `+0x358` owner-Stats lookup itself has no null-component guard.
The fixed peer numerical reference is commit `a7d4ca28da21028d530efdef083a833f3bc90dca`,
`Agents/docs/re/client-integer-resource-mutations.md`; its immutable manifest and exact source bytes are
verified by the R72 publication checks. It is not a new numerical result from this round.

## 3. Actor policy, mode gate and embedded manager

**S:** `0x141e9d930` computes `special = Actor.byte1c8 != 0 || selected(0x56c5, is_graft_skill) != 0`.
The concrete World `+0x1b0` getter `0x14164dc70` returns 2 iff raw World byte `+0x984 == 2`, otherwise 1.
Value 1 is ordinary Predictive; value 2 is Lockstep in the existing mode bindings.
Getter 1 with `special == false` succeeds without calling inner policy `0x141e9d9e0`.
This does not skip the action eligibility which the root caller already performed.

The inner function receives Actor, skill and a pointer to the outer third argument. Its ordered routes are:

1. Require `0x141ec5550(World, owner, skill)` and nonnull Actor `+0x140` result.
   The former can update World timers; its return and side effects are an explicit boundary here.
2. Category `0x30` with selected `0x35a`, `is_totem`, equal to zero tailcalls manager `+0x28(skill, false)`.
3. Otherwise, a nonzero configuration byte read at `0x141e9da56`, World getter 1 and category `0x67`
   together select the same `+0x28` route. Its configuration producer is not recovered here.
4. Normal route calls manager `+0x20(skill, getter == 1, false, thirdArgument, false)`.
   A true result succeeds; false continues to the charge fallback.

The actual client Actor factory `0x1416640c0` reaches constructor `0x142192860`.
At `0x1421929a9..0x1421929b0` it installs table `0x1435db980` into `Actor +0x598`.
Slots `+0x20/+0x28` bind `0x141ee9d30/0x141eea3a0`, respectively.
These are the cooldown/soul manager methods already covered by [Vaal souls](../../protocol/27-vaal-souls.md).
The `+0x28` route is check-only; the normal method contains conditional mutation/spend paths.
R72 controls observe arguments and supply method results, so they do not newly prove any actual soul debit.
Do not label this embedded manager as a generic Mana payment callback.

### 3.1 Charge fallback after normal-manager failure

The fallback gets Buffs and selected integer `0x1fc8`,
`active_skill_cooldown_bypass_type_override_to_power_charge`. Nonzero override selects Buff type `0x1e`;
otherwise skill dword `+0xcc` selects `1 -> 0x1d`, `2 -> 0x1c`, `3 -> 0x1e`.
Other values leave count zero. Selected count comes from `0x141e108e0`.
World `+0x90` notification is called **before** testing that count, including the no-route/zero-count cases.
Zero rejects; nonzero re-reads routing inputs, calls `0x141e10b90` with count minus one and succeeds.
The count reader, notification effects and count setter are supplied APIs, not executed Buff internals.
The second routing read is not assumed stable in all real executions.

## 4. Validation and remaining contract

Ignored `logs/projectile-execution-model/round72/` retains reproducible navigation, `audit_admission.py`,
`admission-audit.asm/json`, `check_admission.py` and `admission-controls.json`.
Eighty finite controls execute 17,600 original instructions across 715 sites. They cover six supplied final-class
witnesses, callback short circuits, disable exceptions, signed radius/120-grid boundary, speed and cost bypasses,
reservation order/sentinels/wrapping, final-owner rejection, mode/configuration routes, manager ABI and fallback
notification/count-write ordering. Stack and eight nonvolatile GP registers are checked at native returns.
The resource/category/selected-stat/component APIs, loaded state and side effects remain explicit inputs.

This closes the conditional bodies behind the two R70 booleans as separate subcontracts; their full composition
with the runner, actual input publication, Actor `+0x1a0`, timer preflight, manager numerical effects, alternate
owner resources and official scheduling remain incomplete. No outgoing projectile-contact report, authoritative
damage, universal tick, complete render behavior or server implementation follows from these controls.
