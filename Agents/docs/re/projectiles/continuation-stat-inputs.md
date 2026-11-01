# Native continuation stat inputs (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [012 — projectile execution](../012-projectile-execution.md).
This chapter recovers selected native formula callbacks upstream of the shared
[continuation consumers](continuation-and-memory.md). It does not replace their qualification order,
action snapshots, counters, history or random gates with a single stat check.

## 1. Formula evaluation and effective action inputs are separate contracts

**S:** fifteen registered outputs cover Split, Chain, Fork and always/cannot-Pierce inputs.
The registration index is a discovery aid; each selected key, callback pointer, registration call and
callback body is independently checked against the pinned original PE.
Native key `k` maps to `Stats.json[k-1].Id`; keys below are hexadecimal.

`S[k]` denotes a read in the native computation context: an available context/cache entry first,
otherwise the selected provider's own record/parent path.
It does **not** mean that every ordinary network Stats object automatically recalculates this output
when a raw modifier changes. Existing explicit overrides, including zero, can supply the effective value;
native computation dispatch has its own eligibility/transaction contract.
The ordinary-client startup flag also bypasses selected action overlay/materialization paths, as described
in [actions and emission](actions-and-emission.md).

The peer's fixed `85c648b7ef83056fb88154fc9cccc7ca20a30619`
`Agents/docs/re/client-stat-evaluation.md` supplies the already recovered provider/dispatcher contract.
Round21 `peer-reuse.json` verifies its exact Git reference alongside the other 27 peer documents.
No peer implementation is imported, and no ordinary wire object is assumed to activate these formulas.

## 2. Registered output rules

All arithmetic additions and products below wrap to signed32 unless a float32 operation is shown.
`bool(x)` returns 0 or 1 based on nonzero, including negative values.
The absence of a local clamp must not be replaced with an intuitive nonnegative quantity rule.

| Output key / name | Callback | Recovered rule |
|---|---|---|
| `29c9 virtual_projectile_number_to_split` | `0x142810dc0` | `S[47c9] ? 0 : S[47cc]` |
| `29ca virtual_arrow_number_to_split` | `0x142811c30` | Separate raw/converted branches in section 3 |
| `2241 virtual_projectiles_cannot_chain` | `0x1428135a0` | `(S[2242] && S[1b6]) || S[2b44] || S[4680]` |
| `223f virtual_number_of_chains_for_projectiles` | `0x1428151f0` | `S[2241] ? 0 : trunc0(wrap32((S[471]+100)*S[47cb])/100)` |
| `223d virtual_projectiles_cannot_fork` | `0x142816100` | `(S[223e] && S[1b6]) || S[2b44] || S[2e99] || S[4680]` |
| `2247 virtual_projectiles_cannot_pierce` | `0x142864690` | `S[1e78] || (S[2248] && S[1b6]) || S[2b44] || S[47cf] || S[47d0]` |
| `2a0f virtual_number_of_forks_for_projectiles_final` | `0x142895cd0` | `S[223d] ? 0 : S[2e97]` |
| `2e97 theoretical_number_of_forks` | `0x142895680` | `bool(S[354] && S[3]==9 ? S[37fb] : S[223b]) + (S[bad] ? S[343c] : 0) + S[2a10]` |
| `223b virtual_projectiles_fork` | `0x142816340` | `bool(S[460] || (S[223c] && S[1b6]) || (S[343b] && S[bad]))` |
| `37fb virtual_arrows_fork` | `0x1428167f0` | `S[3]==9 && S[354] && (S[223b] || S[37f5])` |
| `18dd virtual_always_pierce` | `0x142865fb0` | `S[1211] || (S[13b7] && S[1197])` |
| `18db arrows_always_pierce` | `0x1428662b0` | `S[3]==9 && S[354] && (S[18dd] || S[18dc])` |
| `47cc intermediary_projectile_number_of_targets_to_split_to` | `0x1428110f0` | Converted-count intermediary, section 4 |
| `47cb intermediary_number_of_chains_for_projectiles_from_modifiers` | `0x142813ff0` | Base contributions and converted-count intermediary, section 4 |
| `47ca intermediary_number_of_chains_from_modifiers` | `0x1428137c0` | Conditional base terms, section 2.2 |

Useful input names:

| Key | Stat ID |
|---|---|
| `1b6` | `casting_spell` |
| `354` | `skill_can_fire_arrows` |
| `471` | `support_chain_count_+%_final` |
| `2b44` | `keystone_barbed_tips` |
| `4680` | `vaal_lightning_arrow_fork_and_chain_modifiers_apply_to_number_of_redirects` |
| `2e99` | `kinetic_bolt_forks_apply_to_zig_zags` |
| `47cf` | `projectile_behaviour_only_explode` |
| `47d0` | `projectile_object_collision_behaviour_only_explode` |
| `2a10` | `number_of_additional_forks_base` |
| `343c` | `attack_projectiles_fork_additional_times` |

The main-hand stat type comparison `S[3]==9` is a different enumeration from equipment-object Bow type 14.
Do not interchange them. Theoretical Fork starts with a boolean selected Fork capability, then adds
attack-specific extra forks and the separate base count; it is not simply a raw integer Fork sum.
Chain's final multiply truncates **toward zero after the wrapped product** using signed division by 100;
it neither floors negative values nor computes an unbounded product or floating division.

### 2.1 Fork capability is boolean before additional counts

`460 projectiles_fork`, `223c projectiles_from_spells_fork` and
`343b attack_projectiles_fork` supply the respective generic/spell/attack conditions in `223b`.
The category inputs are `1b6 casting_spell` and `bad skill_is_attack`.
`37fb` admits `37f5 arrows_fork` only under the main-hand type-9/arrow-skill gates.
All conditions use nonzero, not positive-only tests; the returned capabilities are normalized to 0 or 1.
An input value greater than one therefore does not itself grant that many Fork generations.
Theoretical Fork subsequently adds `343c` and `2a10`; final `2a0f` separately applies cannot-Fork.

### 2.2 Conditional Chain base `47ca`

Before the `47cb` additions and projectile-count conversion, this callback returns the wrapped sum:

```text
S[af]
+ (S[bad] ? S[11fe] : 0)
+ (S[6] == S[a8d] ? S[1277] : 0)
+ (S[3c1a] && S[b05] && !S[1946] ? S[3c19] : 0)
+ (S[948] || S[949] ? S[5377] : 0)
```

| Key | Input |
|---|---|
| `af` | `number_of_chains` |
| `11fe` | `attacks_num_of_additional_chains` |
| `6 / a8d` | `current_frenzy_charges / virtual_maximum_frenzy_charges` |
| `1277` | `num_of_additional_chains_at_max_frenzy_charges` |
| `3c1a / b05 / 1946` | `skill_is_chaining_skill / skill_is_lightning_skill / skill_is_projectile_skill` |
| `3c19` | `non_projectile_chaining_lightning_skill_additional_chains` |
| `948 / 949` | `skill_is_trapped / skill_is_mined` |
| `5377` | `trap_mine_skill_num_of_additional_chains` |

The frenzy comparison is literal equality, including **zero equals zero**; there is no positive-maximum guard.
The trap/mine tests describe the skill, not whether its owner is a trap/mine entity.
Native addition order is base plus trap/mine, then nonprojectile lightning, frenzy and attack, each wrap32.
The displayed regrouping preserves the modulo-2^32 result. There is no nonnegative clamp in this callback.
The conditional nonprojectile-lightning term must not be unconditionally added to ordinary projectile skills.

## 3. Arrow Split raw branch and projectile-count conversion

`0x142811c30` applies these branches in order:

1. If `S[341a]` and current steel ammo `S[30f5]==0`, return zero.
2. Set `base=S[46e projectile_number_to_split]`.
3. If `S[29c7 modifiers_to_number_of_projectiles_instead_apply_to_splitting]==0`, return `base` directly.
4. If `S[3181 fires_1_projectile_if_no_steel_ammo]` and `S[30f5]==0`, return zero.
5. Calculate the converted quantity below, clamp, then subtract one.

For normal nonnegative ammo/cap and finite, nonoverflowing conversion inputs:

```text
steel = 1f
if S[3147] != 0:
    used = clamp(S[30f5], 0, S[373c])
    steel = max32(0f, f32(wrap32(100 + S[3147]*used)) / 100f)
skill = max32(0f, f32(wrap32(100 + S[26f2])) / 100f)
locus = max32(0f, f32(wrap32(100 + S[4a36])) / 100f)
n = cvttss2si(f32(f32(f32(f32(wrap32(1+base+S[29cb])) * skill) * locus) * steel))
lo = bool(S[4a37])
hi = S[56ee] ? 1 : INT_MAX
return wrap32(min(max(n,lo),hi) - 1)
```

`3147` is `number_of_projectiles_to_fire_+%_final_per_steel_ammo_consumed`;
`373c` is the per-use steel consumption cap. `4a37` and `56ee` select minimum/maximum one projectile.
The subtraction belongs only to the conversion branch. For example, a converted zero with no minimum
can return -1 here, while the downstream shared Split getter applies its own signed nonnegative clamp.

The exact native ammo selection first computes `a = current>=0 ? current : 0`, then
`used = cap>=current ? a : cap`, comparing the **original current** to cap.
The idealized clamp above is not claimed equivalent for exceptional negative caps.
`max32` is the ordered `comiss/cmovb` pointer-selection leaf `0x14013c620..0x14013c62f`;
operand order and NaN behavior cannot be substituted with an arbitrary host-language `max`.
Each displayed division/multiplication rounds to float32; `cvttss2si` invalid/out-of-range behavior
also needs preservation if exceptional inputs are reachable. Reachability is not established here.

## 4. Split and Chain intermediaries

The two intermediary callbacks are not raw additive aliases. Use `SteelFactor` for the section 3 steel
calculation, including its **100-percent baseline** and exact exceptional ammo selection.
Use `skill/locus/lo/hi` as defined there; they are computed only when their conversion branch is reached.

### 4.1 Non-arrow Split intermediary `47cc`

The complete `0x1428110f0` callback has these branches:

```text
if S[341a] != 0 and S[30f5] == 0: return 0
base = S[46e]
if S[46d9] != 0: return max_signed(base,0)
if S[47c1] != 0 and S[4787] == 0: return max_signed(base,0)
if S[29c7] == 0: return max_signed(base,0)
if S[3181] != 0 and S[30f5] == 0: return 0
total = cvttss2si(f32(f32(f32(f32(wrap32(1+base+S[29c8])) * skill) * locus) * steel))
split = wrap32(total-1)
return min_signed(max_signed(split,lo),hi)
```

`46d9` is `modifiers_to_projectile_count_do_not_apply`.
`47c1` is `vaal_ice_shot_modifiers_to_projectile_count_do_not_apply_to_mirages`;
it bypasses conversion only together with zero `4787 maximum_number_of_vaal_ice_shot_mirages`.
`29c8 intermediary_number_of_additional_projectiles` differs from Arrow Split's `29cb` input.
The steel baseline is explicit at `0x142811867 add edi,0x64`.

**This intermediary subtracts one before clamping; Arrow Split clamps before subtracting one.**
For converted `total=5` with maximum-one set, `47cc` returns 1 and `29ca` returns 0.
This is a static branch difference, not evidence that every such combination is reachable on an official skill
or that the game has a defect. `29c9` and contact qualification separately apply cannot-Split afterwards.

### 4.2 Chain intermediary `47cb`

The complete `0x142813ff0` callback first composes a base quantity, then optionally converts projectile count:

```text
arrow = S[354]!=0 and S[3]==9
base = wrap32(S[47ca] + S[575c]
       + (S[1b6] ? S[2240] : 0)
       + (S[1197] ? S[32ce] : 0)
       + (arrow ? S[37f4] : 0))
if S[46d9] != 0: return base
if S[47c1] != 0 and S[4787] == 0: return base
if S[4ead] == 0: return base
if S[3181] != 0 and S[30f5] == 0: return 0
total = cvttss2si(f32(f32(f32(f32(wrap32(1+base+S[29c8])) * skill) * locus) * steel))
chains = wrap32(total-1)
return min_signed(max_signed(chains,lo),hi)
```

The base terms are `47ca intermediary_number_of_chains_from_modifiers`,
`575c number_of_additional_chains_for_projectiles`,
`2240 number_of_additional_chains_for_spell_projectiles`,
`32ce number_of_additional_chains_for_projectiles_while_phasing` and `37f4 arrow_chains_+`.
`1197` is `virtual_phase_through_objects`; `4ead` is
`modifiers_to_number_of_projectiles_instead_apply_to_chaining`.
Native base-add order is `(575c+47ca)+arrowTerm+phaseTerm+spellTerm`, each step wrap32;
the displayed regrouping preserves the same modulo-2^32 integer result.
Bypassed conversion returns the raw base, without the nonnegative clamp of `47cc`.
The converted branch uses the same subtract-before-clamp order as `47cc`;
its steel baseline is explicit at `0x142814e34 add r14d,0x64`.
Only the later `223f` callback applies cannot-Chain and the `471` final percentage.

### 4.3 Registered dependencies are not a complete read inventory

The selected registration routines build inline dependency arrays, including SIMD-loaded constant keys.
The source audit reconstructs these arrays directly from PE instructions/data rather than trusting the discovery index.
For example, `2241/223d/2247` read `casting_spell[1b6]`, which their declared arrays omit;
`2e97` reads `skill_is_attack[bad]`, also omitted there.
These fields remain explicit formula inputs. The reason for those omissions and every initialization/transaction
ordering guarantee are not established by this bounded recovery.
The upstream `223b` registration likewise omits its actual `1b6/bad` reads, and `47ca` omits
`bad/948/949`. `37fb`'s four declared dependencies match its four formula inputs.

## 5. Concrete consumers and state lifetimes

The five final tables cover all six witnesses: ordinary bow/wand `0x14326d930`, Fireball `0x14333ac40`,
KB `0x14326b9c0`, TS `0x143423898` and BA `0x1432ec7e8`.

| Slot | Final binding |
|---|---|
| `+0x5f8` Split count | All `0x141ba6210` |
| `+0x638` Chain eligibility/budget | All `0x141bb6530` |
| `+0x640` Fork eligibility/budget | All `0x141bb6600` |
| `+0x648` | All constant true |
| `+0x678` Pierce qualification | All `0x141ba96a0` |
| `+0x680/+0x688/+0x690` | All constant false |
| `+0x698` Return qualification | All `0x141bb04f0` |
| `+0x6a0` Return phase | All `0x141241550`, reads byte `+0x461` |
| `+0x598` arrow selection | Fireball false; others `0x141b6fa10`, equipment/type and `skill_can_fire_arrows` |
| `+0x578` | Fireball false; others true |
| `+0x498` preparation | TS wrapper `0x141a82590`; others `0x141ba2b70` |

These bindings confirm applicability of the shared consumers; they do not prove successful release in every mode.
Split selects `29c9/29ca`, conditionally adds action count snapshots and then clamps the signed sum.
Contact cannot/explode-only/already-Split gates and the target's own Split contribution are additional decisions.
Chain adds cached bonuses and applicable action/mode/Volley terms beyond its final stat.
Fork compares the final budget with the inherited generation counter.
Pierce checks cannot/probability **before** forced/always-pierce and only spends its constructor-cached finite budget
on the last branch. Recovered always-Pierce outputs do not override those earlier refusals.
See the [ordered qualification and history contracts](continuation-and-memory.md) for exact consumers.

Return continues to read selected effective `5480/46d/9c0/3216` inputs and action phase/cache state.
The reused candidate index has no selected producer entries for those keys; this is not proof that the PE has none.
Existing [Return qualification and fresh random contexts](random-inputs.md) remain the evidence for those rules.

## 6. Evidence and remaining boundaries

Round21 local evidence is listed in [the evidence ledger](evidence-and-open-questions.md).
`continuation-input-audit.py/json` verifies 36 functions, 53 aligned ranges / 5129 instructions,
12 registrations and dependency arrays, 65 final-table slots, 105 instruction anchors and 12 float constant references.
The ignored peer discovery index is identified by its own path/SHA; it is not a committed peer reference.
The independent review separately checks all callback bodies, registrations, final slots and arithmetic ordering.
Offline vectors cover 4111 signed divisions, 49 wrapped products, 37 count-conversion cases and 64 boolean combinations.
Round22 `base-input-audit.py/json` adds six complete function ranges / 1101 instructions,
three original registrations/dependency arrays and 36 instruction anchors.
`base-input-vectors.py/json` interprets cache-ready original instructions with synthetic inputs and compares
4513 results against separate formulas, covering 216 distinct instructions; provider cache-miss paths and the
previously recovered boolean context getter remain explicit input boundaries.
`base-independent-review.py/json/md` separately checks the original bytes, registrations, names and formulas.
No client code has been executed or attached, and no server implementation has been changed.
The full raw dependency graph beyond these callbacks, dispatcher activation on a particular network object,
actual official wire values, later effective-view/cache mutation and all special skill overrides remain open.
Closed formulas are usable static computation evidence; these remaining input/activation questions must not
be silently treated as solved by the existence of a native computation library.
