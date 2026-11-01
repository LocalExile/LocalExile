# Maximum-resistance input equations and declared dependencies (3.29.3b)

## Result and scope

Eight registered callbacks recover the shared, elemental, fire-only and final maximum-resistance equations.
All four final typed callbacks return a **nonzero override before their ordinary signed upper cap of 90**.
The fire-to-cold/lightning modifier adds the uncapped fire-only intermediate, not the already capped final fire
maximum or a second copy of the shared all-elemental value. Divine Flesh's native flag adds five percentage
points to the ordinary chaos maximum in this artifact.

Cold and lightning each conditionally read key 18914 although their registration lists omit it.
This is an original-byte/metadata discrepancy. It is not an observed in-game refresh bug: actual provider
binding, eligibility, alternative invalidation and official-server behavior remain separate.

This is offline static analysis and bounded interpretation of original instructions. No client launch,
attachment, gameplay operation, new capture, server change or CI operation was performed.

| Input | Identity |
|---|---|
| Executable | `Client/Game.exe`, 3.29.3b, preferred base `0x140000000` |
| Executable SHA256 | `78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5` |
| Stats SHA256 | `91c2ed42f6d8094a2704f56aea32f6f16243881b04aadbe4fdbbdd3a566f05da` |
| Key convention | Native Stats key = zero-based JSON row + 1 |

These are stat formulas, not concrete-hit mitigation or an effect-instance publisher. Their use by the
separately recovered [DoT resistance callbacks](client-dot-routing.md#resistance-asymmetries) does not prove
that every ordinary network actor locally evaluates the whole upstream chain. Keep the
[network/native provider contract](client-stat-evaluation.md) explicit.

## Equations

`S(k)` is a signed32 input. `B(k)` means its value is nonzero, including negative values.
`W(x)` wraps to signed32. `G(g,k)` is `B(g) ? S(k) : 0`.
`C(n,k,t)` is `S(n) >= t ? S(k) : 0`, using a signed comparison.
Addition below preserves low 32 bits; none of these sums saturates. Final maximum callbacks have no lower clamp.

| Output key / Id | Callback | Complete equation |
|---|---|---|
| 9869 `virtual_additional_maximum_all_resistances_%` | `0x142715540` | `W(S(18915) + S(949) + (S(5)==0 ? S(5438) : 0) + G(3738,7949) + C(9761,9861,150))` |
| 18012 `virtual_additional_maximum_all_elemental_resistances_%` | `0x142715c80` | `W(S(9869) + S(950) + C(7470,21521,6) + G(7285,16420) + G(7532,17600) + G(17886,17876) + G(18010,18009) + (S(4206)>0 && S(2956)>0 ? S(18532) : 0) - (B(12063) ? 15 : 0))` |
| 18914 `intermediary_maximum_fire_only_damage_resistance_%` | `0x1427167e0` | `W(S(945) + C(21233,21510,4) + G(7604,9293))` |
| 146 `maximum_fire_damage_resistance_%` | `0x142716ca0` | `B(7700) ? S(7700) : min(W(S(18914)+S(18012)),90)` |
| 148 `maximum_cold_damage_resistance_%` | `0x142716fa0` | `B(7700) ? S(7700) : min(W(S(946)+S(18012)+C(21231,21513,4)+G(7605,9295)+G(18908,18914)),90)` |
| 150 `maximum_lightning_damage_resistance_%` | `0x142717810` | `B(7700) ? S(7700) : min(W(S(947)+S(18012)+C(21230,21516,4)+G(7606,9291)+G(18908,18914)),90)` |
| 152 `maximum_chaos_damage_resistance_%` | `0x142718080` | `B(7700) ? S(7700) : min(W(S(948)+S(9869)+C(21232,21507,4)+(B(9587)?5:0)),90)` |
| 15619 `highest_maximum_elemental_resistance_%` | `0x1428ce810` | `max(S(146),S(148),S(150))`, signed; no additional clamp |

The highest-elemental callback passes a three-int range to unwindless entry `0x142c661f0` at
`0x1428cead1`. That actual direct call anchors an explicit leaf range ending at `0x142c662f5`.
The 12-byte range takes its scalar signed-maximum branch; the leaf's longer-range SIMD paths are retained
as bytes but are outside these numerical controls.

### Input identities and qualifications

| Keys | Native meaning |
|---|---|
| 18915; 949; 950 | Base maximum all resistances; additional maximum all resistances; additional maximum all elemental resistances |
| 945 / 946 / 947 / 948 | Base maximum fire / cold / lightning / chaos resistance inputs |
| 7700 | `override_maximum_damage_resistance_%` |
| 5; 5438 | Current endurance charges; additional maximum all resistances with no endurance charges |
| 3738; 7949 | Is poisoned; additional maximum all resistances while poisoned |
| 9761; 9861 | Base devotion; additional maximum all resistances at devotion threshold; actual threshold is inclusive 150 |
| 7470; 21521 | Equipped Shaper-item count; additional maximum elemental resistances if at least six |
| 7285; 16420 | Affected by Purity of Elements; corresponding maximum-elemental bonus |
| 7532; 17600 | Killed a cursed enemy recently; corresponding maximum-elemental bonus |
| 17886; 17876 | Recent spell-suppression count; corresponding maximum-elemental bonus; the count gate is nonzero, not positive |
| 18010; 18009 | All equipment grants armour; corresponding maximum-elemental bonus |
| 4206; 2956; 18532 | Reserved Life; reserved Mana; maximum-elemental bonus requiring both values strictly positive |
| 12063 | `keystone_prismatic_bulwark`; native maximum-elemental penalty is 15 when nonzero |
| 21233 / 21231 / 21230 / 21232 | Equipped Warlord / Redeemer / Crusader / Hunter-item counts |
| 21510 / 21513 / 21516 / 21507 | Corresponding fire / cold / lightning / chaos maximum bonus, gated at four items |
| 7604 / 7605 / 7606 | Affected by Herald of Ash / Ice / Thunder |
| 9293 / 9295 / 9291 | Corresponding maximum fire / cold / lightning resistance bonus |
| 18908 | `modifiers_to_maximum_fire_resistance_apply_to_maximum_cold_and_lightning_resistance` |
| 9587 | `keystone_divine_flesh`; ordinary maximum-chaos bonus is five |

These identities do not establish that every negative/extreme value is reachable in play, or that every
internal key has a currently obtainable player-facing source.

## Numerical consequences

Each example supplies base-all 18915 = 75 and leaves unspecified inputs zero, then executes the eight
original callbacks in dependency order with the preceding outputs supplied to the next callback.

| Additional supplied inputs | Fire max | Cold max | Lightning max | Chaos max |
|---|---:|---:|---:|---:|
| None | 75 | 75 | 75 | 75 |
| Fire-only 945 = 10, cold-only 946 = 3, transfer flag 18908 = 1 | 85 | 88 | 85 | 75 |
| Fire-only 945 = 20, cold-only 946 = -10, transfer flag 18908 = 1 | 90 | 85 | 90 | 75 |
| Divine Flesh flag 9587 = 1 | 75 | 75 | 75 | 80 |
| Override 7700 = 95 | 95 | 95 | 95 | 95 |
| Native flag 12063 = 1 | 60 | 60 | 60 | 75 |

The third example distinguishes copying the fire-only intermediate from copying capped final fire resistance.
The override example shows why adding an unconditional second cap changes the recovered behavior. An override
of zero takes the ordinary branch; a negative nonzero override returns directly. The native sums also permit
negative results and signed overflow; reconstructing them with arbitrary-precision or saturating addition
would change extreme-input behavior.

## Declared dependencies versus actual reads

The eight original registration bodies were interpreted again to their real `0x142513630` call.
Their 57 dependency entries agree with the retained declarations. Unioned numerical reads match those entries
except for the additional conditional **18914** read in each of cold 148 and lightning 150:

- Cold reads the ready value at `0x142717556` from cache offset `0x12788 = 18914 * 4`.
- Lightning reads it at `0x142717dc6` from that same indexed cache offset.
- The corresponding registration bodies are `0x1400b5da0` and `0x1400b5e10`; each declares 18908, but omits 18914.

Scalar reachability over all 2,539 retained registration declarations finds a path from 18914 to fire 146 and
highest-elemental 15619, but none to cold 148 or lightning 150. This describes the **declared graph**;
it does not execute an actual actor's invalidation path. The independently recovered
[native dependency scheduler](client-stat-dependency-order.md) consumes declared edges, so a reconstruction
must not assume every actual input read automatically has a corresponding declared reverse edge.
Alternative invalidation, cache freshness, network overrides and actual source changes require their own evidence.

## Verification and remaining boundary

The private `Tools/re/out/client-damage-audit/r66-max-resistance/` batch retains complete original instructions,
equations, numerical inputs/results and a fresh byte verification. Eight registrars, eight callbacks and the
anchored maximum helper cover **17 identities / 22 ranges / 3,532 decoded instruction records**.
Four identities are newly indexed; the remaining thirteen reuse already retained function ranges.
The **2,186 supplied-cache controls** execute 164,952 instructions across 643 original sites and test gate
combinations, signed thresholds, override short-circuiting, overflow and seven composed examples.
Fresh checks reproduce all retained functions, 20 semantic anchors and the visited instruction bytes;
all 1,145 previously published supports and 111 private R59-R65 files remain unchanged.

These controls supply valid input caches and impose the composition order. They are not a replay of native
actor construction, automatic invalidation, ordinary-client source publication or server authority.
The original R2/R3/R4 gaps remain: actual DoT selection/aggregation/publication/cap/withdrawal, accepted-Hit
numerical damage-to-pool deduction, and timing/authority across those chains. Their status stays Partial.
