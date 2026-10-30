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
  → per-type resistance / mitigation (the critical-strike elemental rules resolve first, §4)
  → roll a whole number per type and send the life change
```

`DamagePacket` is immutable and holds one `DamageRange` per type. `DamageConversion` holds global conversion,
skill conversion and gain-as-extra percentages. `DamageConversion.Apply` processes the types in the table's order, so a
converted portion reaches the next type and can be converted again. Skill conversion is applied first; global
conversion fills whatever remains of the 100% cap, and the final conversion for a pair is stored as a whole percent,
as in Path of Building. Gains are added to the destination without reducing the source, matching the wiki's
"gain as extra" rule.

A player hit on a monster rolls each type once and then takes the monster's own base resistance on each type
(MonsterResistanceCatalog, §4), before the critical multiplier and the life update. A monster's attack on the player
does the mirror pass through the player's resistances. Damage over time is outside this pass: it has no hit, and
Righteous Fire's monster burn keeps its per-tick percentage-of-life model with no resistance or penetration.

Penetration lowers the target's resistance before the damage is reduced. The server caps the monster's base
resistance first (the 75 default, the 90 absolute maximum, the −200 floor) and subtracts the hit's penetration from
that capped value with no second clamp, as Path of Building's `calcResistForType` and hit branch do (16de4b8, lines
494..509 and 3490..3500): the effective resistance can go below the floor, so penetration against a negative
resistance deals more than the raw damage. Penetration is read per hit from the player's and the skill's own stats
and never touches the rolled input; the critical multiplier still applies once, to the mitigated total.
Player and skill contributions to the same raw penetration stat add in `Penetration.Of`.
All three hit paths collect those contributions before the general damage-stat dictionary overlays skill values.
Nine real-client checks read the target's native Life component after each authoritative update.
A level-68 fire skeleton took 60 from fixed 100 fire, then 80, 90 and 100 as penetration sources stacked.
The prohibition restored 60; its zero cold resistance took 100, then 120 with 20 cold penetration.
Map Hillock's 25% chaos resistance took 75, then 105 with 30 attack chaos penetration.

The unconditional stats the server reads (whole percents, this client's Stats.dat names):

| Stats | Scope |
|---|---|
| `base_reduce_enemy_{fire,cold,lightning}_resistance_%` | every hit on that element; the historical penetration stats despite the name (the per-element Penetration support gems grant them, as do the Heart of Flame/Ice/Thunder passives and the jewel mods) |
| `base_penetrate_elemental_resistances_%` | every hit on fire, cold and lightning (the Inquisitor's start node, Martyr's Legacy) |
| `base_attack_damage_penetrates_{fire,cold,lightning,chaos}_resist_%` | attacks only |
| `base_attack_damage_penetrates_elemental_resist_%` | attacks only, on fire, cold and lightning (Forces of Nature, Warden) |
| `cannot_penetrate_or_ignore_elemental_resistances` | a flag: every elemental penetration is off for the player's hits; chaos is not elemental |

The `main_hand_…`/`off_hand_…` and `local_…` weapon-hand aliases are not read (the server attacks with the main
hand's global stats, and a weapon-local scope guess would double-count), and neither are the non-base
`reduce_enemy_elemental/chaos_resistance_%` variants (Penetration Support's own granted stat in this client) or the
conditional ones tied to auras or enemy state.
The non-critical companion is handled by the critical resistance rules below; other conditions remain separate work.
Spells take the supported general stats only; this consumer supports chaos through the attack-only stat in this
client, only the attack-only one; physical damage never penetrates.

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
and panel stages.
Ordinary attacks use `AttackHit` or the bow release snapshot; attack gems retain main's `SupportedHit` pipeline.
Both constructors read the same raw `DamageStats.ManaAddedLightning` grant before increases and effectiveness.
Support restrictions, support multipliers, conversion, target blocking and post-hit stages remain in that pipeline.
This stack does not introduce the separate derived-damage engine from archival PR #206.
Automated production-path cases cover ranged and melee gems, effectiveness and a spell isolation control.
Native evidence here belongs to original head `378ffde`; supported-skill composition needs a new native check.
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

## 2c. Whisperer: spell increases also apply to attacks

Whisperer's "your mind is destroyed" (PassiveSkills graph node 50932) grants
`additive_spell_damage_modifiers_apply_to_attack_damage_at_150%_value` with value 1.
For this 3.29.3b data, its stat key is 11811; `spell_damage_+%` is key 26.
The flag enables a fixed 150% transfer of the resolved spell increase/reduction into the attack's increased bucket.
It is additive with the existing increases: base 100, generic increased 100% and spell increased 20% produce 230.
One percent spell increase contributes 1.5%, retained until the existing panel/hit rounding stages.
Spell-only more multipliers and cast speed do not transfer, and a spell keeps its ordinary spell-damage calculation.

`DamageStats.SpellAttackIncrease` feeds weapon physical damage, added attack ranges and supported attack packets.
This includes maximum-mana lightning; effectiveness, restrictions and conversion retain their existing order.
Ordinary bows capture the resulting packet at release.
The current consumer reads resolved `spell_damage_+%`; new conditional source evaluators remain separate work.
Path of Building's pinned
[PoB](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/16de4b82/src/Modules/CalcOffence.lua#L618)
copies increased/reduced modifiers into attacks using the configured multiplier.

Automated checks cover the real node, signed/fractional/additive cases, weapon and added ranges, bow snapshots,
effectiveness/conversion, and socketed Burning Arrow, Cyclone, Fireball and Brutality consumers.
Nine native checks on a fresh unascended Witch used this raw flag and read the actual target's life after each hit.
Fixed 100 chaos damage became 250 with 100% spell increase; generic 100% plus spell 20% produced 230.
Base 200 with 1% spell increase produced exactly 203 in both the native panel and the hit.
Generic 100% and spell -20% produced 170, and a flag value of 2 still used the fixed 150% transfer.
Spell-only more/cast-speed inputs added no attack damage; removing the flag and relogging cleared the derived effect.
These checks verify the raw-stat consumer, not allocation/refund of a Phrecia node on the unmodified client.
Class selection and Phrecia client compatibility are separate work; this does not enable a new ascendancy chooser.

## 2d. Behemoth: attack damage from final armour

Forest Tracking (Daughter of Oshabi node 6281) now shares this increased-attack calculation.
Client key 2434, `attack_damage_+%_per_450_evasion`, contributes 3 per complete 450 final evasion.
Both armour and evasion contributions add to the raw attack increase before one signed clamp.
Final evasion after Iron Reflexes supplies no evasion groups; the resulting armour can still supply Steel Willed groups.
See [Forest Tracking](../game/ascendancies.md#forest-tracking-attack-damage) for scope and lifecycle checks.

Steel Willed (3.29.3b PassiveSkills graph node 2103) grants
`attack_damage_+%_per_450_physical_damage_reduction_rating` with value 2, stat key 13019.
`ArmourAttackDamage` adds `floor(max(0, final armour) / 450) * value` to resolved `attack_damage_+%` once.
Thus 449 armour gives no bonus, 450 gives 2%, 899 still gives 2%, and 900 gives 4%.
This is an increased modifier, additive with other increases; base 100 plus generic 100% and this 4% gives 204.
The complete groups use the final rating after existing defence increases and conversions.
An explicit development override of the final rating replaces that input; it is not raised by armour increases again.
The calculation changes only the temporary character-stat dictionary, never the raw passive/equipment/dev sources.
Repeated builds cannot compound it, and removing the modifier or changing armour immediately changes the next packet.

Path of Building pinned 16de4b82 maps "per N armour" to a PerStat Armour/div tag in
[ModParser](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/16de4b82/src/Modules/ModParser.lua#L1472).
[ModStore](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/16de4b82/src/Classes/ModStore.lua#L460)
floors the groups before multiplying the modifier.
The separate chance to double armour against a hit does not change this rating; see section 2f.

The shared increased-attack stat feeds supported attacks and added damage, including maximum-mana lightning.
Weapon physical damage now consumes that same generic attack stat, with signed sums kept wide before cancellation.
All three attack constructors retain fractional damage and multiply before dividing to preserve whole endpoints.
Existing effectiveness, conversion, support restrictions and bow release snapshots retain their order.
Spell damage receives none of this attack-only increase.
Automated checks cover the actual node/key, thresholds, additive and signed cases, repeated builds, removal,
final-defence composition, bow snapshots, mana lightning and socketed Burning Arrow/Cyclone/Fireball/Brutality paths.
Fourteen native checks on a fresh unascended Witch use raw development inputs rather than Phrecia allocation.
Ten fixed-chaos cases read both panel ranges and actual monster life: 100 at 449 armour, 102 at 450/899 and 104 at 900.
Generic 100% gives 204; another raw 10% attack increase gives 214, and a modifier value of 3 at 900 armour gives 106.
Negative armour and modifier removal restore 100; maximum-mana lightning composes to a native 62-damage hit.
Two additional panel-only checks show weapon physical 2â€“5 becoming 6â€“15 at 45000 armour.
Relog clears the development effect.
Phrecia node admission and the rest of Behemoth remain separate work; this consumer does not enable a new chooser.

## 2e. Armour against incoming chaos hits

`base_armour_applies_to_chaos_damage` (client key 12064) is a boolean source.
The current `PassiveSkills` node 29437, Incorruptible, grants value 1.
`PassiveBonus` carries it through `CharacterStats.Bonus` into `Pools.ArmourAppliesToChaosDamage`.
It never changes `Pools.Armour`, the native final rating or Steel Willed's offensive input.

Incoming chaos hits first take the ordinary capped chaos resistance, including the Kitava penalty.
`PlayerHitMitigation.AfterResistance` then uses the existing rounded armour model against that remaining damage.
For example, 1000 chaos against 50% resistance and 1000 armour leaves 500 before armour.
The armour reduction is 29%, so 355 remains; using the raw 1000 in the denominator would incorrectly leave 415.
The existing 90% armour mitigation limit applies, with no additional physical reduction contributed to chaos.
Physical and elemental reduction retain their existing rules and per-type rounding.
Immunity zeroes the component before this pass.
Fortification follows mitigation; suppression and block keep their existing command/contact ordering.
Energy Shield then absorbs non-chaos damage, while the mitigated chaos share still bypasses it.
Mixed monster hits mitigate each type before computing that bypass share and the existing Fortification floors.

The calculation order comes from Path of Building commit `16de4b82d57f1c0de6eb40f37143c32d4da36a02`.
Its [CalcDefence.lua][pob-chaos-armour], `applyDmgTakenConversion` lines 97-116, uses resistance-adjusted damage
in the armour denominator and keeps additional reduction separate per damage type.
LocalExile retains main's existing integer rounding instead of changing the global physical model.

[pob-chaos-armour]:
  https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/16de4b82/src/Modules/CalcDefence.lua#L97-L116

`MonsterSwing`, `/hit`, `/spellhit` and `/blockhit` share the hit-only mitigation pass.
DoT uses its resistance-only path and never enters this helper.
`/set_stat base_armour_applies_to_chaos_damage 1` enables the raw development source.
Value 0 replaces a passive source with false; `off` removes the replacement and rebuilds from current sources.
Development values are connection-local and do not survive relog.
Raw-node tests inject 29437 only as a stat fixture and do not establish Phrecia allocation or class selection.

Automated checks cover resistance ordering/caps, flag-off identity, immunity, zero/extreme armour,
physical-reduction isolation, final rating/offence, source replacement/removal and fresh connection reload.
Framed chat dispatch and real monster swing tests cover suppression, mixed damage, block, ES and Fortification.
Current-main integration preserves Strength-derived Mana and Chaos Inoculation in the same pool calculation.
Regression controls cover complete Strength groups, independent raw-source removal and current-Mana clamping.
The parsed Mana refresh retains its send-gate snapshot; restoring a source does not refill current Mana.
Eleven native controls use a new unascended Marauder, raw key 12064 and a level-one Plate Vest fixture.
With 969 Armour, a raw 20-chaos hit removes 20 Life while disabled and two while enabled.
At 50% chaos resistance it removes one Life with the enabled armour pass.
Physical and fire controls preserve their ordinary behavior.
At 50 Armour and 50% chaos resistance, the same chaos hit removes five Life even with 80% additional physical
and elemental reductions active; controls confirm those reductions still affect their own types.
The native Armour rating stays unchanged when the flag toggles.
Explicit zero, removing the development replacement, source restoration and relog are verified.
These controls establish actual player Life loss for the development hit route.
Mixed monster packets, suppression, block, ES, Fortification and DoT separation retain automated coverage.
The raw-source controls do not select Incorruptible or establish whole-class gameplay.
The test records the exact embedded Git versions, DLL hash and process working directory.
Recent-hit armour composition is covered below; partial armour percentages, other class mechanics and local item
defences remain separate work.

## 2f. Steel Willed: temporary incoming-hit armour

`RecentEnemyHits` implements the bounded double-armour consumer, independently of the outgoing bonus in section 2d.
The prior four seconds of accepted enemy contacts supply key 4534.
Key 20680 scales that count with a 30% contribution cap, then generic chance key 8404 adds before a 0..100 clamp.
The result is published as virtual key 12268.
One lazy decision per unblocked relevant hit supplies physical and enabled chaos mitigation without changing pools.
The current contact records after resolution, including blocked/zero-damage contacts, under the same combat lock.
Death, shutdown, area entry and delayed-swing epochs use the explicit connection lifecycle.
The existing stat send gate and player tick publish current counts, including idle expiry.
See [Steel Willed armour](../game/steel-willed-armour.md) for primary addresses, model v1, dev-input semantics,
current-main regressions and the eighteen historical raw-input native checks from original head `777b292`.
The rebased head and exact batch composition await fresh native verification.

## 2f. Spell skills deal no damage (#293)

The extracted 3.29.3b `Stats.dat` row 5340 is `spell_skills_deal_no_damage`, client key 5341.
`PassiveSkills.dat` node 49937, Rampager, supplies value 1.
This consumer reads the raw stat already carried by the caster's character-stat dictionary.
It adds no pool field or pool rebuild, class admission, passive allocation exception or graph bypass.

`SkillDamageContext` takes an explicit skill kind and the actual caster's stats, with no implicit owner lookup.
A nonzero flag suppresses the entire outgoing Spell packet, including physical, elemental and chaos components.
Attack classification takes precedence for mixed Attack/Spell skills, matching `SkillHitPlan`.
Default melee/bow attacks and attack gems keep their damage, including bonuses granted by Spell-tagged auras.
Aura activation, reservation and buffs remain available.
The rule does not prohibit skill use: casting, mana costs, action timing, cooldowns and visuals retain their paths.

`SupportedHit` suppresses the final supported packet after added damage, conversion and support multipliers.
Existing projectile and area execution consumes that packet before rolling damage and applying target mitigation.
The standalone `SpellHit.Resolve` seam applies the same rule with its separate caster dictionary.
The final `GemSkills` record publishes the supported packet's spell and main-hand ranges, per-use damage and DPS.
Explicit zeros replace inherited weapon ranges, so server Life loss and native panel inputs agree.
Removing or zeroing the source restores the next packet without rebuilding pools.

Gem projectiles retain main's impact-time stat resolution, including flag addition or removal during flight.
Area hits resolve at their existing hit time; ordinary bows retain their existing release snapshots.
No projectile snapshot, Fireball impact art/shape (#262) or broader projectile execution (#258) is added here.
Their future spell impact paths can reuse the same explicit context.

RF enemy burn passes its outgoing `DamageOverTime` through the context on every existing monster tick.
Suppression sets the rate to zero, preserving fractional carry and enemy aura membership.
The resource-recovery loop's RF self-burn remains an independent self-damage cost, including ES absorption.
Restoring the source's damage resumes enemy burn without restarting RF.
This change does not implement a separate RF or other DoT panel; existing hitless-spell hit rows remain zero.

Zero-damage hit policy is preserved deliberately.
Supported hits still roll block/critical state, accept the hit, update the existing hit timestamp and run callbacks.
On-hit/noncritical charge events and existing notifications therefore still occur on an accepted zero-damage hit.
Main's generic on-hit Rage gain also remains eligible on accepted spell contacts, including suppressed hits.
Attack/melee Rage gains and Fortification still require their own attack/melee eligibility.
No primary evidence in this slice establishes a different callback rule; no damage-based hit-admission gate is added.
RF remains a DoT tick and does not create hit callbacks.

Main has no full owned-actor executor.
Independent minions or daemons must supply their own actual-caster stats to this seam when that executor is built.
An owner's suppression is not inherited merely because it owns the actor.
Future owner-derived integration needs explicit per-skill ownership rules and separate production-route coverage.
No archived actor executor, Rampage, Vaal or new resource cost is imported.

Automated checks cover actual supported spell constructors, selected native records and encrypted dispatch to Life.
They include physical/elemental/chaos hits, source default/on/off/removal, supported added chaos and charge callbacks.
Released Fireball tests witness the glide before adding or removing the source, then check impact Life.
Default melee/bow, attack gems, Anger/Wrath/Hatred attacks and RF enemy/self ticks are covered.
Current-main regressions compose local weapon quality, supported additions, charges, conversion and Rage.
Parsed source replacements preserve Strength-derived Mana, chaos-hit armour and Mageblood flask deduplication.
Maximum-Mana added lightning remains attack-only, and restoring a source does not refill current Mana.
Unpaid casts keep main's action-intent ordering without releasing damage or granting hit resources.
Utility reservation/cooldowns, cosmetic target identity and RF fractional carry retain combined regression coverage.
Historical native checks at original head `e80a81c` used an unascended Witch and raw development inputs.
The eleven controls used a synthetic level-one Plate Vest; they do not verify the rebased candidate.
Fireball dealt a fixed 200 chaos and Ice Nova 130 to actual native target Life; the flag reduced both to zero.
Native Fireball mouse actions exposed matching selected ranges and DPS, including explicit zeros with the flag.
Paid casts still corrected native Mana, signed nonzero flags suppressed damage, and removing the flag restored it.
A default attack still dealt 100 with the spell flag active.
With RF installed, enemy Life stayed at 4516 while actual player Life fell from 259 to 209.
Removing the flag resumed enemy burn to 4455; reapplying it stopped that existing burn without restarting RF.
Relog cleared the development source.
In-flight source changes, conversion/gain, supported additions and aura attack bonuses have automated coverage.
The ordered #290 / #294 / #296 / #300 integration at `c9cd1864` passes 28 fresh native controls.
On its new level-one unascended Marauder, a level-one Fireball deals fixed 200 chaos, then zero with the flag.
An attack still removes 100 native target Life while flagged; removing the flag restores Fireball's 200.
The same owner retains converted mana 29 and Armour 251; two existing synthetic accuracy mods ensure a 100% attack roll.
The real native Life reads and accepted-hit logs agree, with Gateway/Core/Storage/Host revisions and hashes pinned.
RF, Ice Nova, relog and selected-panel controls above remain attributed to the original head.
No native allocation of Rampager, owned-actor execution or whole-class acceptance is claimed.

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
average range. The caller applies the target's resistance. The player's chill, freeze and shock on monsters are
modelled ([below](#outgoing-chill-freeze-and-shock-on-monsters)); critical ailment damage and the damaging ailments'
chance, application and duration on monsters are not modelled yet. Righteous Fire still uses its existing per-tick
percentage-of-life burn; it is a fire DoT but not an ignite.

## 4. What the server uses now

- The default attack builds a physical packet from Characters.dat (`Unarmed.HitDamage`), applies the player's
  conversion stats, rolls one number per type and applies the critical multiplier to the rolled total.
- Fireball builds its packet from `spell_minimum/maximum_base_<type>_damage` (`SkillCatalog.HitDamage`), applies the
  spell's increased-damage stats per type (`SpellDamage.Packet`), then the player's and the skill's conversion stats,
  rolls it and sends the life change.
- Monster melee is a physical packet (`MonsterAttack.Damage`); the server rolls it when the swing lands.
  Each type takes resistance, then shared incoming hit mitigation, including enabled chaos armour (section 2e).
- Player hits on monsters (default attack, gem projectile and area hits) roll each type once and reduce it by the
  monster's base resistance: `MonsterTypes.Resistances` → `MonsterResistances`, the level's
  `DefaultMonsterStats.Difficulty` (1/2/3) picking the Normal/Cruel/Merciless columns (`MonsterResistanceCatalog`,
  `MonsterCatalog.BaseResistance`, the shared pass is `HitMitigation`). Without the extracted tables every type reads
  0 for the base input.
  Spawned-monster resistance modifiers add before the cap; physical reduction and damage taken apply after mitigation.
  Ordinary bows snapshot player/skill penetration at release and resolve target resistance at contact.
  Main has no connection-aware resistance or presence engine; those archival #206 systems are outside this stack.
  Automated mixed fractional-hit, target-modifier, bow RNG and same-key player/skill cases cover the resolved paths.
  Native penetration checks belong to original head `0d800d1`; recheck the target-modifier and bow composition natively.
- The hit's penetration (`Penetration.cs`) subtracts from the capped resistance with no second clamp: the general
  per-element and elemental stats on every hit, the attack-only variants on attacks (§2), the
  `cannot_penetrate_or_ignore_elemental_resistances` flag suppressing the elemental ones. Damage over time and the
  physical part take no penetration.
- Critical elemental hits with `critical_strikes_ignore_elemental_resistances` use effective resistance zero,
  including when the target's resistance or maximum is negative; ignore overrides penetration.
  `attack_critical_strikes_ignore_elemental_resistances` covers attacks only.
  Non-critical elemental hits add `non_critical_strikes_penetrate_elemental_resistances_%` to general penetration.
  Player and skill contributions to the same key sum; the prohibition suppresses all elemental penetration and ignore.
  Physical, chaos and DoT keep their separate rules.
  Damage types roll once, followed by the original single critical draw; deterministic mitigation uses that result.
  Target physical reduction and damage taken follow mitigation, preserving fractional components until the final total.
  Ordinary bows retain offensive resistance rules in their release snapshot and resolve the target at contact.
  Native checks on original head `13c41e5` verified the basic ignore and non-critical rules.
  Ten composed native groups pass at source `1366f48`, with Gateway/Core/Host embedded Git versions matching it.
  Critical panels show 657/505, then 657/755 after a spell-only increase; active Sea Legs gives 1204 and empty-set zero.
  Fixed mana lightning produces actual Life losses 90/120/60 for ranged/melee/ranged-only contexts.
  Against a level-68 fire skeleton, fixed 100 fire + 100 cold deals 160, or 220 after 40/20 penetration.
  A 150% critical multiplier then gives 300 with ignore, 330 without it and 240 with the prohibition.
  These values are read from the native Life component after the authoritative hit.
  The run uses a fresh unascended Witch and exercises default attacks, not the ascendancy chooser or whole classes.
  Supported-gem, modifier/bow and Purity-replacement combinations retain their automated integration coverage.
  Earlier native evidence remains attributed to its original heads; a stale #225 DLL did not test #226.
  Rebuild after the final checkout and verify embedded Git versions, hashes and the running DLL before `-NoBuild`.
  See [native build provenance](../dev/client-control.md#build-provenance-for-native-verification).
- `DamageModel.Mitigate(DamagePacket, ...)` applies each type's own capped resistance.
  Incoming hit commands and monster swings share `PlayerHitMitigation` after resistance.
  Righteous Fire keeps the single-value resistance-only DoT path.
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

- Energy shield, exposure and damage-taken modifiers other than shock and the monster mods'; monster armour (the
  monsters' base resistances are in, §4, with unconditional penetration).
- Added flat damage besides the maximum-mana granted lightning stat (supports/items, the other added-damage
  stats), increased/more modifiers on the base damage, and skill-specific added-damage scaling.
- Damaging-ailment application, duration and effect on monsters; alternative ailments (scorch, brittle, sap); Shatter;
  critical ailments; bleed movement bonus; poison stacking bookkeeping.
- Per-monster damage conversion and monster-specific mitigation.
- Damage reflection, leech, life/mana gain on hit, and recoup.
- Party/minion damage and the full support-gem effect set.

The next support-gem work (#64) can add its effects as packet operations: `Add` for added damage, `Scale` for
increased/more, and `AddGain`/`Convert` for conversion, without touching the life-update code.

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

## Incoming ordinary elemental ailments

The Gateway's supported monster melee contact now calls `ApplyPlayerAilmentsLocked` after hit mitigation/block.
`PlayerElementalAilments` owns ordinary ignite/chill/freeze/shock on the pauseable connection clock.
Risk Aversion's final avoidance stats are consumed independently per eligible ailment, with no lucky avoidance.
The resource writer integrates strongest ignite with live resistance, ES and lethal Life damage; RF remains nonlethal.
Chill changes action timing/movement, freeze cancels/gates actions, and shock increases subsequent damage taken.
See [Gambler's exact source, threshold, formula and presentation limits](../game/gambler.md).

## Outgoing chill, freeze and shock on monsters

Each `SpawnedMonster` has a `MonsterElementalAilments`, changed under the Monsters lock on the connection's pauseable
clock (`ProjectileWorldTime`). The default attack, area skills and projectiles (ordinary bow shots included) call
`AilmentsOnHitLocked` (`Program.MonsterAilments.cs`) after a hit's damage when the monster still lives, so a hit does
not gain from its own shock. The damage per type is the resisted packet × the monster's damage taken × the critical
multiplier on a critical strike; the threshold is its maximum life (wiki: equal to it for most monsters). The
attacker's stats are `CharacterPlayerStats` plus the skill's own, summed through the panel rows (`PanelStats.Of`).

| Ailment | Applied by | Effect | Duration |
|---|---|---|---|
| Chill | every hit with cold damage | floor(50 × (cold / threshold)^0.4 × modifiers), at most 30%; none under 5% | 2 s |
| Shock | a critical strike or `chance_to_shock_%`, with lightning damage, or cold damage with `cold_damage_can_shock` | the same formula on that damage, at most 50% | 2 s |
| Freeze | a critical strike or `chance_to_freeze_%`, with cold damage | the monster cannot act | 0.06 s per 1% of the threshold, at most 3 s; none under 0.3 s |

The effect modifiers are the attacker's `virtual_chill_effect_+%`/`virtual_shock_effect_+%` and the monster's
`chill_effectiveness_on_self_+%`/`shocked_effect_on_self_+%`, both counted before the cap and the 5% floor. Durations
take the attacker's `virtual_*_duration_+%` and the monster's `base_self_*_duration_-%` (positive reduces, as on the
player's jewels). Of the Ice carries −80 of the freeze one in 3.29.3b Mods.dat, so by that sign its freeze lasts 80%
longer; the game's text for it is not checked. `immune_to_*`, `cannot_be_*` and `base_cannot_be_*` on the monster's
stats block each kind. The strongest of a kind applies, and each keeps its own duration (wiki); a weaker one that also
ends sooner is dropped. A monster that dies keeps them; it no longer acts.

- **Shock** adds to the mods' `base_damage_taken_+%` (PoB DamageTaken INC), before a spell's less damage taken, on
  the player's hits and Righteous Fire's burn (`MonsterDamageTaken`).
- **Chill** divides the monster's attack time (`BusyUntil`, the swing's contact delay) by 1 − chill and re-times the
  server's walk by the same factor.
- **Freeze** stops its walk, chase and swings; a freeze that lands during a swing cancels it at contact.
- Unstoppable (`action_speed_cannot_be_reduced_below_base`, `movement_speed_cannot_be_reduced_below_base`) keeps both
  at base, as the player's action floor does (forum bug report 3494363: freeze only interrupts channelling there).
- **Stormy Seas.** `cold_damage_can_shock` lets the cold damage count for the shock's magnitude; the chance is still
  a critical strike or `chance_to_shock_%`. The client's calculator `0x1426f2660` makes `virtual_cold_damage_can_shock`
  (0x1bc6) from keys 0x451a, 0x1bc9 and 0x1b96, and nothing else in the client reads it.
- **Mirrored** (`reflect_non_damaging_ailments_%_chance`, 100): the ailments a hit added act on the player as well
  (wiki Reflect: "in addition to its target"), through `PlayerElementalAilments.Inflict` with the player's own effect
  modifiers, caps and floor. The exact reflected magnitude is not verified.
- **Glacial Wave.** A chill carries the attacker's `enemies_chilled_by_hits_take_damage_increased_by_chill_effect` and
  `..._deal_damage_lessened_by_chill_effect` as they were when it landed. While such a chill lasts, the monster takes
  increased damage by its strongest chill (any source, wiki Glacial Wave), added to shock and the mods' damage taken
  (PoB DamageTaken INC), and its swings deal less by half of it: `MonsterSwing` scales the rolled hit by
  1 − chill / 200 after the monster mods and before the player's mitigation (PoB Damage MORE −chill / 2).
- **Freeze minimum.** `freeze_minimum_duration_X_ms` (Brine Bound, 1000) lifts any freeze the attacker inflicts that
  passed the 0.3 s threshold to at least that long; no duration modifier changes the minimum (wiki Brine Bound).
- **Freeze when hit.** `chance_to_freeze_enemies_for_1_second_when_hit_%` (Brine Bound) rolls once for each monster
  hit that lands in `MonsterSwing`, blocked hits included (wiki Blocking: blocking keeps the defender's when-hit
  effects), never for an evaded one. The freeze is 1 s through the usual duration modifiers, threshold, minimum and
  immunity; whether duration modifiers apply to this fixed 1 s is not verified. It draws from `Random.Shared`, so the
  incoming hit's own draws keep their order.
- **Shock on kill.** `enemy_shock_on_kill` (Within the Tempest) shocks a monster the player kills, from a hit or
  Righteous Fire (`ShockOnKillLocked` at the three death sites): 15% base (wiki Shock: shocks without damage) and 2 s,
  through the player's shock effect and duration modifiers and the monster's own, at most 50%. Skill-local shock
  modifiers of the killing skill are not counted; whether the game counts them is not verified. A killing hit's own
  ailments are not put on the corpse (they apply only while the monster lives), although the wiki's ailment timing
  note implies a killing hit inflicts its elemental ailments.
- **Shock proliferation.** A shock takes the attacker's `virtual_shock_proliferation_radius` when it lands, as the
  client's calculator `0x142828340` builds it: the largest of `gloves_shock_proliferation_radius`,
  `virtual_elemental_status_effect_aura_radius` (the larger of `elemental_status_effect_aura_radius` and
  `elementalist_...`, `0x142828e10`), `unique_vinktars_flask_shock_proliferation_radius`,
  `ascendancy_non_damaging_elemental_ailment_proliferation_radius` and `passive_mastery_shock_proliferation_radius`,
  and 15 with `shock_proliferation_radius_is_at_least_15`. Nothing else in the client reads the radius. The calculator
  scales none of them by area of effect; the server grows the radius by the area modifier like a skill's area
  (`SpreadRadius`: × the square root of `SkillArea.Modifier`, truncated to two places), because the wiki's
  Elemental proliferation page says modifiers to area of effect affect proliferation. A hit's shock uses the hitting
  skill's area stats over the player's, a kill's shock the player's own. Whether the game scales Within the Tempest's
  fixed 1.5 m this way is not verified beyond that general rule.
  Each AI tick (`ProliferateShocksLocked`), every living hostile monster within that radius (cells) of a monster with
  such a shock, a corpse included, has the strongest one at the same effect until it ends, like an aura (wiki
  Elemental proliferation). The copy does not spread again, does not count as inflicting, and goes as soon as the
  monster is out of range or the source shock ends. The receiving monster's immunity counts; its own
  `shocked_effect_on_self_+%` does not change the copy (not verified). Chill, freeze and ignite proliferation are not
  modelled, nor a reflected shock spreading from the player. In the game a non-unique monster killed while frozen
  shatters and leaves no corpse to spread from (wiki Freeze); Shatter is not modelled, so such a corpse still spreads.

Presentation runs on the AI tick (`MonsterAilmentChangesLocked`), so with `/ai off` the server still applies the
effects but sends nothing, and shocks do not spread. A kind is re-sent only when its strongest magnitude changes: S→C 0x1c2 for the old instance,
then 0x1c1 with BuffDefinitions 1 chilled, 2 frozen or 3 shocked (BuffVisuals 3, 4, 5), the player as source and no
duration; 0x1c2 removes it at expiry, so a pause cannot desync it. All three rows have the +0xff bool, so 0x1c1
carries a u16 at +0x82 ([11](11-skills.md) §12); chilled and shocked are `IsRecovery` and carry their one GrantedStats
value (`action_speed_-%_from_chill`, `base_damage_taken_+%_from_shock`). The magnitude goes in both; what the client
does with +0x82 is not known. The chill is also sent as 0x1b3 `virtual_action_speed_+%` (−chill), which the native
walk `0x141e7c0b0` and the action clips read; network stats run no calculators ([04](04-player-entity.md) §4.1a). A
freeze is not sent there (−100 would divide the native clip by zero); the server stops the monster instead.

Not modelled: Shatter, unique and boss thresholds and their reduced effects, immunities from the monster's variety
(.ot), Glacial Wave's chill reflected onto the player by Mirrored (wiki: it then acts on the player too), and the
client's frozen animation (not checked live).
