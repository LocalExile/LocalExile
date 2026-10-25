# 20. Damage: hits, conversion and damage over time

This is the model the Gateway uses for combat. It is deliberately separate from the protocol messages: 0x1b1 carries a
life total, so everything here happens before that total is sent. The reference is Path of Exile's
[Damage](https://www.poewiki.net/wiki/Damage), [Damage conversion](https://www.poewiki.net/wiki/Damage_conversion) and
[Damage over time](https://www.poewiki.net/wiki/Damage_over_time) pages, with the exact conversion bookkeeping checked
against the local Path of Building (`Tools/PathOfBuilding/src/Modules/CalcOffence.lua`: `dmgTypeList`,
`buildConversionTable` and `calcDamage`).

## 1. Types and order

A hit is a **packet** with a separate minimum/maximum range for each type:

| Type | Notes |
|---|---|
| Physical | reduced by the defender's armour and physical damage reduction |
| Lightning | elemental |
| Cold | elemental |
| Fire | elemental; its damage over time is called burning |
| Chaos | not elemental; ignores energy shield in the real game |

Conversion only goes **forward** in this order:

```
physical → lightning → cold → fire → chaos
```

So physical can become fire, lightning or cold; cold can become fire or chaos; fire can become chaos. Fire cannot
become cold, and chaos does not convert. The `DamageType` enum is numbered in this same order (the old enum's numeric
values were not used on the wire).

## 2. Hit pipeline

```
base skill/weapon damage by type
  → added damage (DamageStats.Added; the first added-damage stat is read, see §2)
  → gain X% as extra (does not consume the source)
  → conversion (source is consumed; converted damage can convert again)
  → increased / more multipliers per type
  → critical strike
  → per-type resistance / mitigation
  → roll a whole number per type and send the life change
```

`DamagePacket` is immutable and holds one `DamageRange` per type. `DamageConversion` holds global conversion,
skill conversion and gain-as-extra percentages. `DamageConversion.Apply` processes the types in the table's order, so a
converted portion reaches the next type and can be converted again. Skill conversion is applied first; global
conversion fills whatever remains of the 100% cap, and the final conversion for a pair is stored as a whole percent,
as in Path of Building. Gains are added to the destination without reducing the source, matching the wiki's
"gain as extra" rule.

The same packet is used for attacks, spells and monster hits. The caller decides whether the attack-only or spell-only
stats apply; `DamageStats` reads the Stats.dat names:

| Kind | Example stat |
|---|---|
| base conversion | `base_physical_damage_%_to_convert_to_fire` |
| skill conversion | `skill_physical_damage_%_to_convert_to_cold` |
| global conversion | `physical_damage_%_to_convert_to_lightning` |
| attack conversion | `attack_physical_damage_%_to_convert_to_fire` |
| spell conversion | `spell_physical_damage_%_to_convert_to_fire` |
| gain as extra | `physical_damage_%_to_add_as_fire` |
| non-chaos as chaos | `non_chaos_damage_to_add_as_chaos_damage_%` |

`skill_…` stats use the skill-conversion table; `base_…`, `attack_…`, `spell_…` and the plain
`…_damage_%_to_convert_to_…` names use the global table. Conditional variants ("while affected by Anger", "against
frozen enemies", ...) are not read yet. Skill conversion takes precedence when skill and global conversion together
exceed 100%, matching PoB's `buildConversionTable`.

## 2b. Added damage from stats (the first stat)

Added damage is not fully modelled yet, but one stat is read.
`DamageStats.Added` adds `attack_skills_have_added_lightning_damage_equal_to_%_of_maximum_mana` (Stats.dat row
11881, client key 11882) to an attack's added-lightning range.
The value is the percentage: both the added lightning minimum and maximum become that percent of `maximum_mana`
from the same final stat dictionary, so spending mana and reserved mana never change the damage, while a pool
change (level, equipment, passives) does.
Path of Building pinned 16de4b82 (ModParser.lua lines 5636–5639) maps the node's value to both LightningMin and
LightningMax as BASE with PercentStat Mana and ModFlag Attack; the packet agrees.
At 6% of a 1000 mana pool (Whisperer "we know all", PassiveSkills.dat graph id 22720) the attack gets 60-60
added lightning, not 1-60.
The pool bonus joins the added range before the increases, so the increased attack-damage stats and later
conversion treat it like any other added lightning, and a fractional result is only rounded at the existing roll
and panel stages (`AttackHit` is the shared attack packet constructor, so ordinary attacks and attack gem
projectile/area hits agree).
`attack_damage_+%` scales the added range for every attack.
`melee_damage_+%` applies only in explicit melee context: the default attack uses its active weapon profile,
and attack gems use `ActiveSkillType Melee` through `SkillCatalog.IsMelee`.
An inactive weapon set cannot change that context.
Spell hits and damage over time are untouched, and saved gem quality/experience is not read.
The node's separate mana-cost drawback (`base_mana_cost_-%`) is a mana-cost change outside this damage
consumption's scope.
Fourteen real-client stat checks used a fresh synthetic Witch and development overrides for these inputs.
With the 6% grant, 56 maximum mana showed 3–3 added lightning and 1000 showed 60–60.
Adding 50% increased lightning showed 90–90; 997 maximum mana showed 59–59 after the panel's existing floor.
With 1000 mana, 50% increased attack damage showed 90–90; another 50% melee increase raised unarmed damage to 120–120.
A wand stayed at 90–90 even with a club in the inactive set; switching to the club gave 120–120 and back gave 90–90.
Clearing the attack increase left the ranged wand at 60–60 despite the melee input.
Clearing the grant removed the lightning rows' damage, and relog restored the baseline maximum mana and damage.
These checks validate the generic damage consumer; ascendancy allocation remains in its separate lifecycle change.

## 3. Damage over time and ailments

DoT is one damage type, has no hit, cannot critically strike and uses that type's resistance. `DamageOverTime` carries
the damage per second, the remaining seconds and an optional ailment. `Tick(seconds, carry)` returns whole damage and
the fraction to carry into the next server tick, so a 0.35 s tick does not lose damage; it never runs past
`RemainingSeconds`. Call `Advance` when applying a duration.

The modelled damaging ailments are:

| Ailment | Damage type | Base | Duration |
|---|---|---|---|
| Ignite | fire | 90% of the hit's fire damage per second | 4 s |
| Bleed | physical | 70% of the hit's physical damage per second (200% while moving is not modelled) | 5 s |
| Poison | chaos | 30% of the hit's physical + chaos damage per second, stacking | 2 s |

`Ailments.Ignite/Bleed/Poison` build the effect from a hit packet. Pass the **converted base packet before hit-only
increased/more modifiers**; the method's `increasedPct`/`more` arguments are the ailment-specific multipliers, so a
caller cannot double-count hit modifiers. For a specific hit pass its rolled packet; for expected damage pass the
average range. The caller applies the target's resistance. Non-damaging ailments (chill, freeze, shock), critical
ailment damage, ailment chance/application and ailment duration stats are not modelled yet. Righteous Fire still uses
its existing per-tick percentage-of-life burn; it is a fire DoT but not an ignite.

## 4. What the server uses now

- The default attack builds a physical packet from Characters.dat (`Unarmed.HitDamage`), applies the player's
  conversion stats, rolls one number per type and applies the critical multiplier to the rolled total.
- Fireball builds its packet from `spell_minimum/maximum_base_<type>_damage` (`SkillCatalog.HitDamage`), applies the
  spell's increased-damage stats per type (`SpellDamage.Packet`), then the player's and the skill's conversion stats,
  rolls it and sends the life change.
- Monster melee is a physical packet (`MonsterAttack.Damage`); the server rolls it when the swing lands, applies each
  type's resistance and then armour to the physical part.
- `DamageModel.Mitigate(DamagePacket, ...)` applies each type's own capped resistance. The existing single-value
  `Mitigate` overloads are used by `/hit` and Righteous Fire; physical `/hit`, `/spellhit` and `/blockhit` use
  `MitigatePhysical`.
- `/hit` and `/spellhit` still use a single type; conversion is not applied to those debug commands.

## 5. Continuous resource updates (2026-10-24)

The client Life handler for S→C 0x1b1 stores the current whole pool value; merely sending the
regeneration stat does not make the visible life value advance between these updates. The former
250 ms player tick therefore made /god recovery jump by roughly one quarter of maximum life.

`RunResourceRecovery` now combines life/mana regeneration, flask recovery and the player's nonlethal
Righteous Fire burn in one 16 ms loop. Each step integrates the actual elapsed `ClientState.Clock`
time, including scheduler delays; the clock stops during pause. `RecoveryPool` retains fractional
points, discards overflow at a full pool and negative carry at RF's one-life floor, and never revives
dead life. Only changed whole values generate 0x1b1. The original per-minute rate formulas and
unreserved mana cap are unchanged. Stat aggregation is refreshed at the old 250 ms cadence, with
an additional refresh when RF changes, instead of rebuilding the panel about 60 times a second.

`RunPlayerTick` keeps the 250 ms cadence for RF on monsters, suppression conditions, waypoints,
auto-loot and quests; its RF duration also uses elapsed game time. Instant damage and instant
healing remain immediate. No client executable or packet layout changes are needed. Live visual
verification: enable /god, damage life with `/blockhit attack physical 10000`, then observe recovery;
also check pause, flasks and RF. The new visual behavior awaits that real-client check.

## 6. Not modelled yet

- Energy shield, penetration, exposure, shock and other damage-taken modifiers; monster armour and resistances.
- Added flat damage besides the maximum-mana granted lightning stat (supports/items, the other added-damage
  stats), increased/more modifiers on the base damage, and skill-specific added-damage scaling.
- Ailment application, duration and effect; critical ailments; bleed movement bonus; poison stacking bookkeeping.
- Per-monster damage conversion and monster-specific mitigation.
- Damage reflection, leech, life/mana gain on hit, and recoup.
- Party/minion damage and the full support-gem effect set.

The next support-gem work (#64) can add its effects as packet operations: `Add` for added damage, `Scale` for
increased/more, and `AddGain`/`Convert` for conversion, without touching the life-update code.

## Current Rage and attack damage

`RageState` keeps a character's transient current Rage and mana-spending remainder across area connections.
The target `Character.ot` supplies a base maximum of 30 and a two-second loss delay.
The [GGG Rage rework](https://www.pathofexile.com/forum/view-thread/3531661#rage) establishes ten Rage lost per second,
one percent more attack damage per Rage, and a shared half-second cooldown for gaining Rage from hits.
It grants no inherent attack or movement speed.
`maximum_rage`, increased effect, loss delay/rate and the no-inherent-loss flag modify that state.
Pause suspends its timers; death clears both Rage and partial mana spending.
This state is not permanent character-save data.

Whisperer's `gain_x_rage_per_200_mana_spent` accumulates actual, successfully paid mana costs.
It retains the remainder, crosses multiple thresholds in one payment, and ignores failed payments and life costs.
Removing that source discards its partial accumulator.
Generic, attack, melee and critical hit grants share the hit cooldown, including the incoming-hit grant.
Behemoth's maximum bonus and maximum-Rage-on-hit-gain chance use the same state.
Other generation sources, Rage costs, per-Rage speed/armour bonuses and Soul Gain Prevention are still separate work.

The final Rage multiplier scales the complete attack packet before conversion, critical rolls and mitigation.
Spells and existing reflected Impale stacks do not receive it.
The client receives `current_rage` (7594), `virtual_maximum_rage` (12139), effective Rage and its final attack modifier.
BuffDefinitions 1060 (`rage`) reads those count stats and requires no recovery-value payload or extra u16.
Native verification allocated Whisperer's connected `540 → 863` path and spent 600 mana through Fireball.
It produced 30 current Rage and the actual native Rage buff; a fixed attack reduced native monster life by 130,
compared with 100 before Rage.
That numerical control temporarily disabled inherent loss and waited for the cost-producing projectile to finish.
The subsequent checks restored loss, retained current Rage through an area transfer, respected expiry on relog,
and removed mana-based generation through a paid refund.
Ignored evidence: `logs/phrecia-completion/rage-native.jsonl` and `rage-results.json`.

## Player Ward and timed Adrenaline

`WardState` absorbs one mitigated, unblocked hit before energy shield, then breaks even when it absorbed the whole hit.
Damage over time bypasses Ward.
While broken, later hits do not extend its restoration delay.
The two-second baseline is from [GGG's 3.25 player-balance notes](https://www.pathofexile.com/forum/view-thread/3531661).
Restoration-speed increases divide that delay; Starlight's 100% increase gives one second.
Unbreakable Ward retains its value between hits.

Equipped body armour/evasion conversion retains fractions until the final defence result, matching the
[independent defence calculation](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/dev/src/Modules/CalcDefence.lua).
For this client's Plate Vest, `19 * 50% * 110%` gives 10 Ward after flooring, rather than 9 from premature rounding.
For mixed hits, Ward is consumed in physical/lightning/cold/fire/chaos order.
The surviving types retain their ES-bypass behavior; proportional redistribution would incorrectly absorb more chaos.

The server publishes `ward`/`ward_broken` stats and the native non-recovery `ward_broken` buff, definition 1878.
The normal elapsed-time recovery loop restores Ward and removes that buff.
Native tests verified 10/20 Ward, timed recovery and refunding the source; the trace is in
ignored `logs/phrecia-completion/ward-native.jsonl`.

Runic Tablet uses BuffTemplates row 503, definition 1090, for the six native Adrenaline values.
The resulting 100% increased damage, 25% attack/cast/movement increases and 10% additional physical reduction execute
on the server as well as appearing in the buff UI.
Additional physical reduction adds to armour's reduction, with the shared 90% cap.
The character's transient buff store uses one absolute expiry across area connections and refuses active refreshes.
Death clears it; expiry refreshes stats, speed, skills and native buff instances.
The native area-transfer probe observed Runic Tablet's original 20-second expiry.
Death Rush uses the same template for five seconds after a player kill; its actual projectile-kill path is tested.

## Player energy shield (2026-10-25)

`EnergyShield.cs` owns the ordinary ES pool, fractional recovery, damage interruption, and recharge phase. Equipment
reads ArmourTypes ES bounds (the same minimum-base percentile currently serialized on items), local flat/increased ES,
quality, intelligence, passive ES and global defence increases. Offhand shields count. Non-chaos damage after mitigation
spends ES before life; chaos bypasses it. A positive damaging hit interrupts recharge; a zero/blocked hit does not.
Death empties ES; existing heal/resurrection/entry refills include it. Righteous Fire uses its life and ES burn inputs
and absorbs its non-chaos self-damage through ES before life. Special keystones and conditional mastery effects are
not included (CI, Eldritch Battery, Zealot's Oath, Wicked Ward, leech and related substitutions).

Character.ot supplies 2000% of maximum per minute: base recharge is one third per second, after a two-second delay.
Faster start divides the delay by `1 + energy_shield_delay_-%/100`; recharge/regen recovery increases scale their rates.
Elapsed game time integrates only the part of a tick after the delay boundary, retaining fractions. Base constants
also match [Path of Building's data](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/dev/src/Modules/Data.lua)
and [defence calculation](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/dev/src/Modules/CalcDefence.lua).

Pool 2 of 0x1b1 synchronizes current ES. Updating the virtual `energy_shield_recharge_active` stat through 0x1b3 did
**not** start the client's recharge predictor in the live probe: it predicted only baseline regeneration and jumped
on the next pool correction. During the server recharge phase, publish regeneration plus recharge through the verified
native `energy_shield_recovery_per_minute` input (key 0x590), retaining the separate recharge-rate panel stat. The virtual
recharge-active stat stays zero to avoid double prediction. This is a compatibility bridge; the native server event
that starts the client's distinct recharge phase has not been identified.

Live OfflineWitch had 185 ES: `/hit physical 100` left life at 3250 and ES at 85, then recovered to 185. Chaos reduced
life while leaving ES intact. With the prediction bridge, client values advance between pool packets during recharge.
Measurements used the local client Life component, not orb pixels or an official server trace. ES uses upstream RunResourceRecovery with its 16 ms target and elapsed time/fractional carries; foreground verification saw at most 2-point positive ES updates
instead of the previous 22-point steps. OfflineWitch's selected Energy Shield mastery grants 2% ES regeneration per
second (3.7 at 185 ES); that regeneration correctly continues while recharge is interrupted. A temporary zero-regen
control recovered nothing during the recharge delay. See [recovery timing](../re/recovery-timing.md) for evidence and
measurement limits. Recovery-cadence PR #159 is merged; this change extends its existing recovery loop.
