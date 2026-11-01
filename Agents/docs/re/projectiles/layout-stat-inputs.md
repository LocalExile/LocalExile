# Layout inputs: selected views, precedence and caller arguments

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [RE 012](../012-projectile-execution.md). **S:** fixed 3.29.3b PE,
SHA-256 `78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5`.
This connects the recovered [fan/Volley geometry](actions-and-emission.md#4-ordinary-fan-aim-distance-changes-spread)
to its selected-stat readers and caller ABI. It does not reconstruct every stat publisher,
the complete computed-stat dependency graph or actual thread scheduling.

## 1. Final bindings and selected source

All five installed classes covering the six witnesses share dispatcher
`+618 → 0x141bb13a0` and these profile getters:

| Slot | Default bow/wand, Fireball, KB, Burning Arrow | Tornado Shot |
|---|---|---|
| `+6b0`, radial admission | `0x141bb33f0` | `0x141a82bb0` |
| `+6b8`, Volley points per side | `0x141bb3490` | `0x141a825d0` |
| `+6c0`, angular variance | `0x141bb34c0` | same |
| `+6d0`, ordinary spread input | `0x141bb3520` | same |
| `+6d8`, fixed spread input | `0x141bb3590` | same |
| `+6e0`, distance variance | `0x141bb3560` | same |

Final tables are `0x14326d930`, `0x14333ac40`, `0x14326b9c0`,
`0x1432ec7e8` and `0x143423898`, respectively.
These are final bindings, not conclusions from a shared base name.

Each ordinary getter selects a view through
`0x14193b350(action, action174)` before calling that object's first numerical slot.
The [selected-view contract](actions-and-emission.md#61-selected-stat-views-and-cached-values-have-different-lifetimes)
applies: the last object in the selected action override stack wins; absent that,
selector zero uses the retained skill's primary `+138/+140` pair, while selector
`n>0` uses additional stat-set entry `n-1` at skill `+108`.
Selection assumes a valid indexed record; it is not a bounds-checking API.

This round composes the real selector and skill getter with a **conditional raw
provider**, whose first slot at `0x14363bd90` is `0x1424cfa40`.
That provider's existing local-pair/parent/zero semantics are reused: an explicit
local zero shadows the parent. This does not establish that every selected view
is raw, or that every computed value is refreshed by a raw network update.
Controls retain shared-pointer owners so temporary selection cannot destroy them;
last-release destructors are outside this composition.

## 2. Named inputs and exact precedence

Keys below are hexadecimal native one-based keys. Names come from extracted `Stats.json`,
SHA-256 `91c2ed42f6d8094a2704f56aea32f6f16243881b04aadbe4fdbbdd3a566f05da`.
The names identify records; instruction use establishes their meaning here.

| Key | Stat | Checked use |
|---|---|---|
| `2185` | `additional_projectiles_fired_with_distance_offset` | signed profile `+08`; separate fan distance increment and budget producer input |
| `1b26` | `projectile_angle_variance` | signed i32 → f32 → multiply by `0.01745329238474369f` → multiply by `0.5f`, profile `+10` |
| `222f` | `projectile_distance_variance` | signed i32 → f32, profile `+14` |
| `a0a` | `total_projectile_spread_angle_override` | nonzero signed i32 → f32; zero returns `60.0f`, profile `+00` |
| `2186` | `projectile_remove_default_spread` | nonzero returns fixed input `-1`, without querying `76d` |
| `76d` | `fixed_projectile_spread` | selected signed i32 fixed input if `2186` was zero, profile `+04` |
| `3741` | `random_projectile_direction` | conditionally builds the seed-word vector |
| `1b46` | `support_parallel_projectile_number_of_points_per_side` | signed result of the Volley getter; dispatcher tests nonzero |
| `719` | `projectiles_nova` | radial admission short circuit |
| `57e0` | `support_gluttony_fire_x_additional_projectiles_in_nova_while_affected_by_disgorge` | radial admission with the next key |
| `57e1` | `affected_by_disgorge_buff` | queried only after nonzero `57e0` |
| `3449` | `multiple_projectiles_projectile_spread_+%` | ordinary fan's signed percentage modifier |
| `f90` | `reverse_projectile_spread` | nonzero boolean passed separately from the metadata control byte |
| `18b3` | `additional_projectiles_fire_parallel_y_dist` | Volley Y-distance input |
| `18b2` | `virtual_additional_projectiles_fire_parallel_x_dist` | Volley X-distance input |

The name of `a0a` does not make the ordinary fan's input a final angle in degrees:
that fan uses it in `atan(multiplier*spread/originalLength)`.
The fixed-spread path is different. A negative fixed input is nonzero, selects
that path and produces zero angular step through `max_signed(fixed,0)`.
Thus `2186 != 0` disables ordinary distance-sensitive spread using a sentinel;
it does not simply replace the ordinary spread scalar with zero.
No legal skill/support combination or actual published negative quantity is
inferred from testing the getters' signed branches.

Radial getter `0x141bb33f0` has ordered short circuits:

```text
if action231 != 0 and action248 != 0: return true
if selected(719) != 0: return true
if selected(57e0) != 0:
    if selected(57e1) != 0: return true
return action465 != 0
```

Every `selected(...)` above re-reads selector `action174` and selects the view
again. A zero `57e0` skips `57e1`. The two action bytes can bypass all stat reads.
Their producers and wider meaning are not recovered just by this reader.

## 3. Profile construction and local lifetime

Profile builder `0x141bb0f20..0x141bb11f6` writes a caller-supplied structure:

| Offset | Output |
|---|---|
| `+00` | f32 ordinary spread input |
| `+04` | i32 fixed spread input |
| `+08` | i32 distance offset |
| `+0c` | f32 distance budget, initially exactly `-1.0f` |
| `+10` | f32 angular variance half-width in radians |
| `+14` | f32 distance variance |
| `+18/+20/+28` | seed-word vector begin/end/capacity, initially all zero |

The ordinary read order is **2185 → 1b26 → 222f → a0a → 2186 →
conditionally 76d → conditionally 3741**. Nonzero `2185` runs the separately
recovered [distance-budget producer](actions-and-emission.md#64-profile-distance-budget-and-deferred-stop)
between its initial read and the variance getters. Its geometry/terrain callback
is reused from R24, not newly executed in this round's input controls.

The final `3741` query itself is skipped when the caller's allow-random byte is
zero. If both gates pass, builder passes these **six u32 words**, in order, to
`0x140404a20` for the vector at profile `+18`:

```text
[sequence11a, derived11c, outer2d0, chain458, ownerObject88, virtualSlot540()]
```

These are seed inputs, not six ray directions or six draws. The existing
[random-direction contract](random-inputs.md#5-correction-profile-vector-contains-seeds-not-explicit-directions)
describes their use. This preparation is separate from the dispatcher's later
five-word batch-variance initialization. It does not join the two streams.

For the multi-projectile branch, dispatcher calls this builder at `0x141bb1df2`
with profile at its stack-frame `rbp+88`, then uses those profile fields in its
chosen layout. Each such builder invocation overwrites the six scalar fields
and clears all three vector pointers before optional vector creation. There is
no retained profile cache on **this path**. That does not make the underlying
Stats objects immutable, uncached or automatically recomputed.

## 4. Dispatch priority and the fan/Volley ABI

After building the profile and preparing the separate batch-variance stream,
the selected multi-projectile branch has this priority:

1. Call final `+6b8` once and save its result at caller `rbp+80`.
   If nonzero, enter Volley; its radial flag is a separate `+6b0` call.
2. Otherwise, use random-direction layout if the profile vector is nonempty.
3. Otherwise, call final `+6b0`; use radial layout if true.
4. Otherwise, use the ordinary fan.

This is the checked multi-projectile branch, not the separate count-one path.
The controls supply the earlier caller frame, count and coordinates; they do
not claim the entire dispatcher or animation-to-release chain was executed.

At the ordinary fan call **`0x141bb23a8 → 0x141bb9730`**:

| Caller stack offset before CALL | Value | Fan entry stack offset after return-address push |
|---|---|---|
| `+20` | target height | `+28` |
| `+28` | projectile count | `+30` |
| `+30` | selected Projectiles metadata byte `+3c` | `+38` |
| `+38` | profile `+04`, fixed spread | `+40` |
| `+40` | boolean `f90` | `+48` |
| `+48` | profile `+08`, distance offset | `+50` |
| `+50` | emitter descriptor pointer | `+58` |
| `+58` | signed `3449`, percentage modifier | `+60` |

`xmm0` holds profile `+00` spread, `rdx` origin XY, `xmm2` origin height,
and `r9` aim XY. This matches the previously sealed R46 fan caller frame.
In particular, percentage modifier and distance offset are separate arguments.

Volley call **`0x141bb1ff7 → 0x141bb89d0`** shares stack `+20..+48`,
then passes saved points-per-side at `+50`, X distance `18b2` at `+58`,
Y distance `18b3` at `+60`, radial flag at `+68`, and later pointers.
Its direct selected-stat read order is **18b3 → 18b2 → f90**, after the radial
getter. The helper's subgroup/distribution geometry remains the existing
[Volley contract](actions-and-emission.md#5-volley-translates-both-ends-of-the-ray).

The fan snapshots selector `action174` into ESI before querying `3449`; its
subsequent `f90` query reloads the current byte. Volley reloads that byte for
each of its three direct reads. The profile getters likewise select separately.
This establishes where source selection occurs; it does not prove real stat
mutation between those reads, a race, or a required immutable release snapshot.
Synthetic controls deliberately change the selector to distinguish the branches.

## 5. Tornado Shot's primary and secondary overrides

Complete table-bound leaf **`0x141a82bb0..0x141a82bc1`**:

```text
if action530 != 0: return true
tailcall shared radial getter 0x141bb33f0
```

The complete branch after the first RET matters; a first-RET preview misses
the primary fallback. Its Volley getter `0x141a825d0..0x141a82605` returns zero
immediately for `530 != 0`, otherwise queries the same `1b46` as the base.
Consequently secondary state forces radial admission and bypasses Volley
points **without reading their ordinary stats**. Primary state retains the
shared behavior. If a nonempty random-direction vector is supplied, dispatch
still gives that path priority over radial; the override alone does not
establish unconditional circular emission under every possible input.

Generated children and behavior-variation setup enter `530` through different
producers; see [the TS stage contract](tornado-shot.md#1-installed-implementation).
These overrides are client behavior, not a newly proposed server skill recipe.

## 6. Verification and remaining work

R48 ignored evidence under the projectile execution log includes
`audit_layout.py`, `layout-input-audit.asm/json`, `check_layout.py` and
`layout-input-controls.json`. Fresh fixed-PE decoding checks **19 ranges /
1462 instruction sites, 20 anchors and 36 final-table slots**. Those ranges
include eleven complete unwind bodies and two bounded leaf CFGs; full range
coverage does not mean every instruction executed in the controls.

**531 finite controls / 576516 original-instruction steps / 692 visited sites**
check selected-view precedence/retained ownership, signed getter inputs,
short-circuit read order, profile field conversions/seed words/reinitialization,
TS overrides and actual dispatch-to-layout caller arguments.
Only the supplied profile/count/frame and selected dispatcher slices are composed.
The nonzero budget result, vector allocation helper, virtual `+540` output,
variance initialization and final layout calls are explicit observing fixtures;
geometry and RNG arithmetic reuse earlier evidence. Raw-provider composition is
conditional; reference counts stay above last release. No native client executes.

Still open: actual grant/publication and later pointer replacement for these
selected stats, computed-provider materialization/refresh, other action-field
producers and exceptional inputs, per-arrow admission/final-grid composition,
actual thread/TLS consumers, and wider mode/owner cases. Recovered source
selection and ABI are usable without a live capture; the controls do not
establish actual official builds, mutation timing or client/server parity.
