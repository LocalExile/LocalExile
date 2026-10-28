# 11 — Skills: how the client gets them, the default attack

Status (live, 2026-10-17): the player has the default attack. It shows in the skill picker, can be put on the left mouse
button, walks into range, swings at the Witch's attack speed (757 ms, or faster with `/set_stat attack_speed_+%`), and
the server lowers the monster's life 2–5 per hit at the swing's contact point until it dies. Code: `GrantedSkill`, `PlayerObject` (`InstanceEntities.cs`), `SkillCatalog.cs` and `Attack` in the
Gateway. Test: `InstanceEntitiesTests.Player_blob_with_stats_and_a_granted_skill`.

## 1. ActiveSkillManager

Every Actor has an `ActiveSkillManager` (RTTI `Skills::ActiveSkillManager`, vtable `0x1435db980`) at **Actor +0x598**,
returned by the Actor's vtable +0x140. Its skills are a vector at **manager +0x158/+0x160** of
`shared_ptr<ActiveSkillInstance>` sorted by the instance's u16 at **+0x40**. Every add ends in the manager's slot 7
(+0x38), `SkillInstanceAdded` `0x141eea6a0` (found through its lambda's RTTI name). Lookups by id: `0x141ee97f0`
(creates the action for a skill id, used by S→C 0x1ab and by the client's own skill use).

ActiveSkillInstance (base vtable `0x1435b9728`, 400-byte `make_shared` block, vtable `0x1435db960`):

| Offset | Meaning |
|---|---|
| +0x40 | u16 **skill id**: what C→S 0x24/0x25 and S→C 0x1ab carry |
| +0x42 | u16 **action id**: an ActionTypes `HASH16` |
| +0x48 | the GrantedEffectsPerLevel reference (row handle, then the effect, its `Id` string) |

## 2. Basic actions: from the object type

The `ClientActor` constructor (`0x142192860`) walks a u16 list on the Actor type (`type+0xf8..+0x100`) and calls
**AddSkill** `0x142198d40(manager, u16)` for each: id = value & 0x3fff, action = value. The list comes from the .ot's
`Actor { basic_action = "…" }` lines (Character.ot: Move, Interaction, InteractionChannelled, WashedUp, Dance,
AreaTransitionAnimation, SinPickup, BloodVortexGetUp, EpilogueKnockdown, AncestralTrialDestroyTotem, …; Monster.ot:
Move, Flee, StrafeLeft, StrafeRight, Advance, DoNothing, MonsterPickup), and the value is the action's
**ActionTypes.dat `HASH16`**: Move = 10505 = **0x2909**, Interaction = 49766 = 0xc266 (id 0x0266). Traced live with
`scripts/re/frida/trace-skills.js`: the player gets 12, a Hungry Corpse 9. Nothing on the wire is needed for them.
`ot_components.py` extracts each type's list (`basic_actions` in `data/object-types.json`). Monsters that rise from the
ground add `Emerge` (16641 = 0x4101, id **0x0101**), which the server starts with 0x1ab to raise them
([10](10-chat-and-commands.md) §5).

## 3. Granted skills: Actor slot 20, flag 0x40

Real skills (gems, the default attack) come from the server, inside the **Actor component's vtable +0xa0 reader
`0x1421932b0`** (the part after the main body, [04](04-player-entity.md) §4.9; also what S→C 0x2ba runs). Its first flag
byte f0:

- **f0 0x40 → `0x142197b10`**: the skill list. It adds each record or replaces the one with the same id; it never
  removes a skill, so a skill missing from a later list stays on the client (and selectable on the skill bar), and it
  does not tell the UI: the character panel's skill list does not show a skill added this way after login.
- **f0 0x20 → `0x142198800`**: the skill update (`InstanceEntities.PlayerSkillsChange`). When it has a record or an id
  it calls `0x140b14b00` before (`0x1402fffbc`) and after (`0x1401d8280`) them, which the character panel's skill list
  follows:
  ```
  u8 a, u8 b
  a × skill record (0x1421981d0, as in the list: add or replace)
  b × u16 skill id (0x142198560: binary search of the manager's skills at +0x158 by id (+0x40), the manager's
                    vtable +0x40 runs for it, then it is erased; an unknown id is skipped)
  u8 c, c × (u32 ActiveSkills row, u32 capacity)                 → soul seeds, current = 0
  u8 d, d × (u32 ActiveSkills row, u32 current, u32 NEW capacity) → soul updates, OLD-capacity clamp
  ```
  After login the Gateway sends every skill change this way (`SendSkills`): all the player's skills as records and the
  ids that left. It remembers the ids the client has per connection (`ClientSkillIds`, from the player object and every
  update since). Live: a re-socketed gem's skill appears in the character panel again (with 0x40 it did not).

`0x142197b10`:
```
u8 n,  n × skill record (0x142196ce0, through 0x1421981d0: insert sorted by id, replacing an equal id, then slot 7)
u8 m,  m × (u32 ActiveSkills row (0x101-byte rows), u32 capacity, u32 current) → manager +0x188
u32 k, k × (u16 skill id, u32 c, c × (f32 remaining base s, f32 total base s, f32 rate, u32 flags))
```

Both soul readers preserve existing entries when their lists are empty.
Collection, explicit removal resets, capacity seeding and the native consumers are documented in
[27-vaal-souls.md](27-vaal-souls.md).
The implementation has automated coverage; native acceptance is pending and unsupported Vaal casts remain refused.

Skill record (`0x142196ce0`):
```
u16  skill id                                      → +0x40
GEPL reference (0x1424a0dc0):
  u8                                               → ref +0x10 (0 for us; a stat-set reference with its u8 set uses it + 1)
  u32  GrantedEffectsPerLevel row (0x78-byte rows)
  stat-set reference (0x1424a0990): u8 a, [u8 if a], u32 GrantedEffectStatSetsPerLevel row (0xb8-byte rows)
  one more stat-set reference per entry of the effect's additional stat sets (GrantedEffects row → its list count)
u8   flags: bit0 → u32 parent object id ("Skill parent object did not exist" if unknown) + u16
            bit1 (0x02) → retain native Vaal soul inputs; bit2 → constructor parameter13 bit1
u8   n supports, n × (GEPL reference, u32)
u32, u32, u32 cooldown ms, u8                       → skill +0xc0, +0xc4, +0xc8, +0xcc (0x141ee73d0)
compact n × (compact stat key, compact value)      the skill's own stats
per additional stat set: the same stats list
```
At `0x1421975f0..0x142197610`, clear wire bit1 and nonzero raw GEPL +0x2c stored uses set `dil = 1`.
At `0x1421976e8..0x1421976f6`, constructor parameter13 is `(wireBit2 * 2) | dil`.
Constructor `0x141ee7571..0x141ee75ba` zeros soul inputs +0xe8/+0xec/+0xf0 when parameter13 bit0 is set.
Otherwise it retains GEPL +0x28 base souls, +0x2c stored uses and the selected soul multiplier.
`GrantedSkill.VaalResourcesEnabled` defaults false and emits only wire bit1 for catalog Vaal resource definitions.
Both catalog lookup paths derive it from raw stored uses, independently of effective cost, capacity or current souls.
Ordinary partners, attacks and supports keep their existing layout; parent bit0 and third bit2 remain clear.
The literal primary constructor regression is source evidence; native Vaal acceptance is still pending.

The additional stat sets are the GrantedEffects row's `StatSet2` list (Volcanic Fissure has one). Each needs its
reference after the first and its stat list after the skill's own: a record without them made the client misread the
rest and crash when Volcanic Fissure was socketed (live 2026-10-23). `GrantedSkill.ExtraStatSets` carries them
(`SkillCatalog.WithExtraSets`: each set's GESSPL row at the gem's level, its implicit, constant and per-level stats).
The action id (+0x42) is not sent: `0x141ee73d0` takes it from the data (GEPL row → GrantedEffect → ActiveSkill →
ActionType → `HASH16`); the default attack's is `Melee` 45448 = 0xb188.

**Skill ids** are the server's choice, but they share the manager with the basic actions, whose ids are `HASH16 &
0x3fff`. 0x0101 collided with a Monster.ot basic action (monsters showed a skill 0x101 / 0x4101). The Gateway uses
**0x0102**, which no ActionType masks to in 3.29.3b (`SkillCatalog.FirstSkillId`).

## 4. The default attack

Characters.dat `StartingMeleeSkill` is the GEPL row of each class's default attack: **4474** = `PlayerMelee` level 1
for the Witch. Its stat set is GrantedEffects `StatSet1` = 476 (`PlayerMelee`), level 1 = GESSPL row **4430**.
`SkillCatalog` looks both up by GrantedEffects `Id` and level (`Game_PLAYER_ATTACK` picks another effect).

Without stats the swing is instant: no animation, and the client repeats C→S 0x24 every ~40 ms while the button is
held (live, first try). The Gateway sends the class's unarmed weapon (Characters.dat `WeaponSpeed`, `MinDamage`,
`MaxDamage`, `MaxAttackDistance`: Witch 833 ms, 2–5, 8) both in the player's Stats component and in the skill's stats:

| Key | Stat |
|---|---|
| 0x117, 0x118 | main_hand_minimum/maximum_physical_damage |
| 0x29e, 0x29f | main_hand_minimum/maximum_total_damage |
| 0x142 | main_hand_base_weapon_attack_duration_ms |
| 0x221a | main_hand_total_base_weapon_attack_duration_ms |
| 0x2a6 | hundred_times_attacks_per_second |
| 0x2a7 | hundred_times_damage_per_second |
| 0x7fc | display_hundred_times_damage_per_skill_use |
| 0x1e70 | skill_uses_main_hand (1: the panel's MH_* rows) |
| 0x126 | main_hand_attack_speed_+% |
| 0x146, 0x4d3 | main_hand_base_maximum_attack_distance, main_hand_maximum_attack_distance |

**The server sends final values; the client derives none of them.** Live 2026-10-17 (`dump-stats-object.js`): a skill
holding `attack_speed_+%` 2000 still swung every ~757 ms and the character panel kept showing 1.2. What the client
uses:

- The **swing time** follows the skill's `main_hand_attack_speed_+%` (with the base 833 ms). When it is not sent the
  getter answers **10**: unarmed attacks alternate hands, i.e. count as dual wielding, worth 10% attack speed. So the
  Witch's real unarmed swing is 833 / 1.1 ≈ 757 ms.
- The panel's **Attacks per Second** is `hundred_times_attacks_per_second / 100`. The panel caches it: it shows a new
  value only after the skill is selected again in the panel. After a passive allocation it stays stale even when the
  panel or its tab is reopened; only a relog rebuilds the character sheet (live 2026-10-19). The server's stats and
  the actual swing are already updated.
- The client walks into range by itself using `main_hand_maximum_attack_distance` (8 cells for the Witch).

**A dual-wielded attack has two hands.** When the off hand holds a weapon, a melee attack swings that one too, and the
client sets the off-hand strike's weapon up from the hand's **own** final stats — `off_hand_…` instead of `main_hand_…`,
the keys next to the main hand's in Stats.dat: `off_hand_minimum/maximum_physical_damage` (0x119/0x11a),
`off_hand_minimum/maximum_total_damage` (0x2a0/0x2a1), `off_hand_base_weapon_attack_duration_ms` (0x143),
`off_hand_total_base_weapon_attack_duration_ms` (0x221b), `off_hand_attack_speed_+%` (0x127),
`off_hand_base_maximum/maximum_attack_distance` (0x147/0x4d4). Sending only the main hand's leaves the second weapon
without attack time, damage or reach, and the off-hand animation of a ground attack then has a null animation set: the
next action crashes the client at 0x141c988c1 (issue #167). `WeaponStats()` sends both hands' sets, from the item in
Offhand1 with its own local mods, in the player's Stats component and in every granted skill's stats. Only Offhand1: the
weapon swap trades the hands' items with rows 14/15, so Offhand2 always holds the inactive set's off hand. The off-hand set
is sent only when the main hand holds a weapon too. `off_hand_attack_speed_+%` is the final main-hand value without the
main weapon's own `local_attack_speed_+%` (CharacterStats multiplies it in like a more), because the off-hand weapon's
local attack speed is already in its attack time (`SkillCatalog.Unarmed.OffHandAttackSpeedPct`). The weapon-type passives
(`axe_` / `sword_` / ... `attack_speed_+%`) apply to both hands alike, as they do to the main hand's value (any equipped
weapon of that type counts).

`CharacterStats`/`PassiveBonus` compute the final attack speed from the player's passives: generic
`attack_speed_+%`, `melee_attack_speed_+%`, one-/two-handed melee, all one-handed, `axe_` / `sword_` / `mace_` /
`claw_` / `dagger_` / `wand_` / `bow_` / `staff_attack_speed_+%`, shield and dual-wield stats. Dual wielding (and
unarmed, which counts as dual wielding for this bonus) adds PoB's `dual_wield_inherent_attack_speed_+%_final` 10% more.
The increased and more parts are combined into one integer `main_hand_attack_speed_+%` (75% increased and 10% more
become 93%), which `SkillCatalog.Unarmed.Stats(player)` sends. Attack time = base / (1 + that / 100), at least one
server tick (`Program.TickRate` = 30/s, so at most 30 attacks per second), and
`hundred_times_attacks_per_second` = 100 / attack time. The panel's **Damage per Second** is the average hit × APS
(`hundred_times_damage_per_second`) and **Damage per Use** is the average hit
(`display_hundred_times_damage_per_skill_use`), both in hundredths. `/set_stat` records raw attack-speed inputs and adds
them on top of the passives, then sends the stats (0x1b3) and the skill again. Minion, totem and timed/conditional
attack-speed stats are not modelled.

**The character's own stats** (`CharacterStats.For`, sent by `ClassAttributes` as final values): the per-level values
come from `Metadata/Characters/Character.ot` (`life_per_level` 12, `mana_per_level` 6,
`accuracy_rating_per_level` 2, `base_evasion_rating` 15; it has no `evasion_rating_per_level`, so evasion does not grow
with level), the bases and attributes from Characters.dat:

| Stat | Value |
|---|---|
| `maximum_life` | BaseMaxLife (38) + 12 × level + strength / 2 |
| `maximum_mana` | BaseMaxMana (34) + 6 × level + intelligence / 2 |
| `accuracy_rating` | (2 × level + 2 × dexterity + flat) × (1 + increased / 100) |
| `physical_damage_reduction_rating` | (flat armour from passives + base body/helmet/gloves/boots) × (1 + increased armour / 100) |
| `evasion_rating` | (15 + flat evasion from passives + base body/helmet/gloves/boots) × (1 + `evasion_rating_+%` / 100) |
| `evasion_rating_+%` | dexterity / 5 + increased evasion passives |
| `physical_damage_reduction_rating_+%` | increased armour passives |
| `maximum_energy_shield_+%` | intelligence / 5 |
| `melee_physical_damage_+%` | strength / 5 |

`AttributeModifiers` combines class attributes and every flat passive/active-equipment source before scaling once.
It supports the existing single, pair and all-attribute flat stats, plus `strength_+%`, `dexterity_+%`,
`intelligence_+%` and `all_attributes_+%`.
For each attribute, its own increase and the all-attribute increase add in the same increased bucket.
Raw attribute totals stay wide through per-key aggregation, overrides, bucket cancellation and scaling.
The result is `floor(flat * (100 + increased) / 100 + 0.5)`, clamped to 0 through `int.MaxValue`.
Only the final attributes and native integer views are bounded; clipped display values never feed the raw calculation.
Derived evasion, Iron Reflexes, energy shield and melee bounds also clamp before integer conversion.
Life and mana stop at `int.MaxValue`, matching their native publication and reservation consumers.
Raw attribute rebuilds clamp current resources to the new caps without refilling or resurrecting the owner.
They publish fresh mana reservation even when current mana does not change.
The shared mana sender samples both values under the combat lock after acquiring the send gate.
Idle-aura and blocked-send reservation behavior has decrypted command-wire coverage.

This rounding model follows PoB commit `16de4b82d57f1c0de6eb40f37143c32d4da36a02`:
[stats](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/16de4b8/src/Modules/CalcPerform.lua#L401-L408),
[buckets](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/16de4b8/src/Modules/CalcTools.lua#L14-L35) and
[rounding](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/16de4b8/src/Modules/Common.lua#L680-L688).
Those functions do not separately floor the signed flat bucket or increased scale factor at zero.
Artificial raw input of -10 flat and -150% increased therefore gives +5; the nonnegative clamp follows rounding.
It is a pinned server model; receiving its computed fractional boundaries does not prove official-server parity.
The extracted Behemoth small nodes (4561, 9053, 22167 and the other `AscendancyCustomBerserker` small nodes)
supply 3% increased Strength and 10% increased Armour through ordinary raw stats.
That source evidence does not enable Phrecia selection or establish native class gameplay.

The final attributes feed life/melee damage, accuracy/evasion and mana/energy shield respectively.
Existing derived-pool divisions stay unchanged, including odd attributes and this server's Intelligence / 5 ES increase.
An explicit `AttributesResolved` pool marker distinguishes calculated zero attributes from legacy uninitialized pools.
Record clones retain the marker; three saturated attributes cannot overflow a sum-based fallback check.
`ClassAttributes` sends the same final values as both panel attributes and `virtual_*_for_requirements`.
Equipment requirements recalculate these values from active raw sources at each existing activation stage
([items](13-items.md#2d-requirements-and-inactive-items)).
Raw attribute `/set_stat` inputs replace their own passive/item key, including zero, and rebuild pools immediately.
Final `/set_stat strength`, `dexterity` or `intelligence` keeps its development display/requirement override semantics.
Those final overrides never become flat sources or change the derived pools.
Clearing a raw source, refunding a passive or swapping weapons rebuilds from raw inputs without compounding increases.
Automated boundary, production-pool, command and equipment regressions pass.
Historical evidence at `60044ad` covers thirteen native controls on an unascended Marauder with raw inputs.
They verify 32 Strength becoming 33 at 3%, 40 at 25%, and 48 when another 25% all-attribute increase is added.
The same all-attribute input sends 18 Dexterity and Intelligence from their flat 14 midpoint.
Life, mana and native requirement stats follow the final values.
A Plate Vest supplies 19 Armour when its 12-Strength requirement is met, stops contributing below it,
and resumes after the final Strength rises again.
Calculated all-zero attributes stay zero; removing the sources and relog restore the expected base pools.
These controls verify delivery and shared consumers, not the official server's fractional attribute algorithm.
Eleven native controls at rebased source `00b246f2` on main `79cc0337` verify the Strength-mana composition.
On a new level-one Marauder, eight mana per four Strength changes maximum mana from 47 to 111.
Strength 40 gives 127 mana; Strength 48 and Intelligence 18 give 145.
A temporary active-vest 100% mana modifier raises that result to 290 once; removing it restores 145.
Reduced attributes disable the ordinary vest at Strength 8 with 58 mana and restore it at Strength 16 with 75 mana.
Clearing the inputs restores Strength 32, mana 47 and Armour 19.
Every control reads the actual native final and requirement stats; Gateway/Core/Storage/Host revisions were pinned.
The check uses raw development inputs and ordinary equipment, without enabling a Phrecia class.
The ordered #290 / #294 / #296 / #300 integration at `c9cd1864` passes 28 fresh native controls.
Eight conversion controls preserve final Strength groups and the half mana from odd final Intelligence.
At final Strength 16 and Intelligence 7, conversion uses B=75.5 and publishes 15 mana and 140 Armour with the vest.
Combined regressions cross the rounded four-Strength boundary, then verify mana increases and truncation once.
Equipment, inactive/current weapon snapshots, zero/off raw replacements and idle aura reservation share that result.
Inactive-weapon, cascading-equipment, passive-refund and active-aura composition still need native coverage.

Maximum mana also includes the ordinary flat and increased mana sources from passives and active equipment.
`X_mana_per_4_strength` (Stats row 4992, client key 4993) contributes
`floor(max(0, final Strength) / 4) * signed raw value` to that same flat mana bucket.
`CharacterStats.For` uses rounded final Strength; Intelligence `/ 2` and final mana truncation are unchanged.
Mana increases apply once to the combined flat total.
The active consumer uses wide signed arithmetic, clamps nonpositive totals or multipliers to zero, and saturates at
`int.MaxValue` for the native maximum and existing reservation/damage consumers.
A missing or zero source keeps ordinary mana rounding and signed flat-mana/multiplier behavior.
`PassiveBonus` reads the generic source; `/set_stat X_mana_per_4_strength <value>` replaces it and rebuilds pools.
The rebuild clamps current mana to the new unreserved cap without filling it when maximum mana increases.
It explicitly sends the mana pool's `0x1b1`, including reservation, even when current mana is unchanged.
`SendPlayerMana` snapshots current mana and reservation together under the combat lock after acquiring the send gate.
Decrypted command-wire tests cover a 50% aura with regeneration disabled, unchanged current mana and a lowered cap.
An explicit zero suppresses the source; repeated builds cannot compound it, and fresh connections clear dev inputs.
Final native `strength` overrides remain presentation/requirement inputs and do not change this pool calculation.
Reservation, native `maximum_mana` and maximum-mana attack lightning all read the resulting pool.
Mental Conditioning (graph node 38080) supplies raw value 8 and conversion key 9548.
With conversion active, `StrengthMana.Flat` joins the unrounded flat bucket once before mana/armour increases.
Both Strength and Intelligence use final scaled attributes; odd Intelligence retains half mana until final rounding.
The ordinary decimal/`Rating` bounds and every raw-attribute `/set_stat` rebuild remain in place.
The full node gives a fresh Witch 16 mana and 128 Armour; display key 21159 alone enables neither consumer.
The consumer does not enable Phrecia selection or claim the full class.
Automated arithmetic, production-source and actual command/reload checks pass.
Thirteen native checks use a fresh unascended Witch, raw key 4993 and synthetic Plate Vest modifiers.
Fourteen and fifteen Strength grant three groups: maximum mana rises from 56 to 80 at raw value 8.
Sixteen Strength grants four groups and 88 mana; 25% increased mana scales the whole bucket to 110.
The maximum-mana attack-lightning panel follows those final values.
Signed input, explicit zero, removal/reapplication and relog are verified.
A final display-only Strength override does not become a raw input during a later rebuild.
These controls do not select Mental Conditioning or verify idle aura reservation in the native client;
that reservation path has the decrypted command-wire coverage above.

Accuracy's flat and increased passives are summed from `accuracy_rating` and `accuracy_rating_+%`, the weapon-specific
stats according to the equipped weapon shape (`sword_accuracy_rating`, `wand_accuracy_rating_+%`, ...), and the Accuracy
Mastery (`accuracy_rating_per_level`, `dexterity_accuracy_bonus_grants_accuracy_rating_+3_per_dexterity_instead`,
`gain_accuracy_rating_equal_to_intelligence`). Two other effects are target conditions applied at the roll, not in
the pools: `accuracy_rating_+%_final_vs_unique_enemies_from_mastery` (row 15547, PassiveSkillMasteryEffects Accuracy1:
40% more) multiplies the rating when the target's rarity is Unique, and
`accuracy_rating_+%_final_vs_enemies_in_close_range_from_mastery` (row 18375, Accuracy5: 50% more) when the target is in
close range (TargetAccuracy.AgainstTarget).
"Close Range" is the ReminderText row `(Close Range is up to 2 metres)`, which at the 10 units per metre of PoB's
ConfigOptions is the constant 20-unit radius; the server reads radius stat units as grid cells (SkillArea, Righteous
Fire's 18 = 1.8 m), so a target whose centre is within 20 cells of the player is close, exactly 20 inclusive, and
"Nearby" is not the same mechanic.
The target's rarity (`SpawnedMonster.Rarity`) and its tracked position at the hit's resolution time decide both; the two
"more" effects multiply (unique and close: × 2.1) and the product floors to a whole rating.
The default attack samples both actors' current tracked positions after its contact delay.
Attack gems currently lack an accuracy roll, so these conditions apply only to the existing default-attack roll.
Ten native checks allocated connected ordinary paths and selected both effects through real C→S 0x35 requests.
The unconditional native panel stayed at 711; logged rolls used 995 against Unique and 1066 against a close normal.
A close Unique used 1493, while a Unique outside close range used 995.
Moving a target back into range restored the close multiplier, relog preserved both choices, and refund restored 30.
Other conditional stats (nearby ally, marked enemy, low life, frenzy charges) are not modelled, and the panel is not
told the conditions: it keeps the unconditional `accuracy_rating` and, for the Offence
panel's Main Hand / Off Hand Accuracy rows, `main_hand_accuracy_rating` / `off_hand_accuracy_rating`. Its Chance to
Hit rows are sent directly as `main_hand_chance_to_hit_%` / `off_hand_chance_to_hit_%` (and the evasive-monster
variants), against a reference monster of the character's level; `character_sheet_reference_evasion` and
`..._high_dex_evasion` carry that monster's evasion. `/accuracy` states the mastery's per-target values.

Armour and evasion are summed from the passive tree and the **base types** of the equipped body armour, helmet, gloves
and boots ([13](13-items.md) §1): `base_physical_damage_reduction_rating` / `base_evasion_rating` are flat, while
`physical_damage_reduction_rating_+%`, `evasion_rating_+%`, `evasion_and_physical_damage_reduction_rating_+%`
(Cloth and Chain and similar nodes), `global_defences_+%` and the combined armour/evasion increase are additive
increased modifiers. The overcapped cold resistance's increase is one of them (§12); it applies before the conversion,
so Iron Reflexes' converted evasion keeps it, and monster attacks roll against the boosted rating.
Power of Purpose's raw `keystone_mental_conditioning` flag converts 80% of flat mana to twice as much armour.
Mana and armour increases add on that branch, and its unrounded contribution joins ordinary armour before rounding.
The remaining maximum mana drives reservation and maximum-mana added lightning; Steel Willed reads final armour once.
See [the calculation boundaries and pending native checks](../game/power-of-purpose.md).
**Iron Reflexes**
(`keystone_iron_reflexes`) converts all evasion rating, including the base 15 and
passive/item flat evasion, to armour and removes dexterity's 1% per 5 evasion; the converted part keeps the armour,
evasion and shared defence increases once each. Item mods, quality, local defence rolls, shields and auras are not counted
yet; the base value is the armour row's minimum, which is the client's default when the item's Armour component is zero.
The final `physical_damage_reduction_rating` and `evasion_rating` (with their `_+%`) go to the client. Armour uses
PoB's `armour / (armour + damage × 5) × 100`, rounded and capped at 90% (`DamageModel.ArmourReduction`); evasion uses
the same hit-chance formula as accuracy, so the chance to evade is `100 - AccuracyHitChance(evasion, attacker accuracy)`
(`DamageModel.EvadeChance`). Monster melee rolls the same formula against `DefaultMonsterStats.Accuracy` at the
monster's level and evades before damage; physical monster hits then go through armour and block. `/defences` prints
both against a reference monster of the character's level. Life and mana still ignore passives and items.
**Chaos Inoculation** (`keystone_chaos_inoculation`) applies to that life: maximum life becomes 1 after the life
modifiers (the character's energy shield stays the real pool), and the player is immune to chaos damage through the
same damage-immunity table as the panel's `chaos_damage_immunity` row. The pre-CI maximum life that the game uses for
stun and ailment thresholds is not modelled.

The default attack's damage (`Unarmed.HitDamage`) is a physical `DamagePacket`: MinDamage..MaxDamage × (1 +
(`damage_+%` + `physical_damage_+%` + `melee_damage_+%` + `melee_physical_damage_+%`) / 100). The packet goes through
the conversion and gain-as-extra stats, then rolls one number per damage type; the server uses the same packet for its
hits. `/set_stat` overrides any of these. The packet, conversion order and damage-over-time model are in
[20-damage.md](20-damage.md); the panel's older `Unarmed.Damage` tuple is only the physical display range.

**Changing a skill after entering:** S→C **0x2ba** on the player with component index 9 (Actor) and the slot-20 data
`f0 0x40, f1 0, skill list`: the list replaces the skill of the same id, new stats included. Live: the swing sped up at
once. The Gateway now sends the 0x20 update instead (above), which also removes skills and updates the character panel.

**Inherent attack-gem speed (#191).** `GrantedEffectsPerLevel.AttackSpeedMultiplier` multiplies weapon attack
rate separately from the player's increased/more speed: Burning Arrow -30%, Heavy Strike -15%, Flicker Strike +20%.
The level-aware catalog accessor follows the same selected-level rule as other GEPL values.
[PoB CalcOffence](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/1afbc4cdcc4fe83273ce8e06a51c5cf986999250/src/Modules/CalcOffence.lua#L2299)
multiplies `source.AttackRate` by `1 + attackSpeedMultiplier / 100` before ordinary speed modifiers.
For the supported ordinary weapon-based path, `SkillAttackTiming` sends total base weapon ms as
`round(weapon base ms / (1 + inherent percent / 100))`, midpoint away from zero, with a minimum of one ms.
It retains the original weapon base ms (0x142/0x143) and final player speed percent (0x126/0x127).
The native total base fields (0x221a/0x221b) are integer ms, so the server uses that same rounded base, then player
speed, then its existing 30 Hz attack-duration floor; rounding the base changes the uncapped time by at most
0.5 ms divided by the player-speed factor.
Native action code `0x141c06c2a..0x141c06c8e` reads 0x221a and divides by 1000 and `1 + stat(0x126) / 100`.
The explicit 0x221a getter (`0x142706568..0x14270658f`) returns the stored value without another GEPL multiplier.
Gem pacing, attack contact/projectile release and channel intervals share that duration; selected APS is `100 / seconds`.
Ordinary attacks and non-attack skills retain their previous paths.
Native baseline before this fix: a 714 ms bow had stored speed 0%, base/total base 714 ms and APS 140 for both
ordinary attacks and Burning Arrow, with actual native clip duration 0.714 seconds in each case.
After the fix, actual native input verified Burning Arrow total base 1020 ms, clip duration 1.019999959 seconds
and stored APS 98; its original base stayed 714 ms and player speed stayed 0%.
The ordinary bow attack retained total base 714 ms, clip duration 0.7140000038 seconds and stored APS 140.
These checks verify native duration and stored APS, not a separate measurement of release/contact timing.
Local before/after captures are `logs/skill-timing-{before,after}-default-bow-real.json` and
`logs/skill-timing-{before,after}-burning-arrow-real.json` in the integration checkout.
Catalog, native-field, modifier-order, pacing/contact and spell-exclusion tests pass.
This excludes support/conditional/per-charge speed, skill-specific attack-time overrides and off-hand alternation rules.
Selected attack-gem damage ranges/DPS still inherit the existing weapon display; correcting those is separate work.
No generator output changes are needed: the multiplier is already extracted.

## 5. Combat timing (Gateway `Attack`)

1. C→S 0x24 arrives when the player clicks, before the client walks into range. The server walks its position estimate
   toward the monster and stops at the first cell within `MaxAttackDistance` (`WalkEstimator.Approach`).
2. On arrival it starts the swing: the 0x1ab echo (from the stop cell, aimed at the monster). Echoing at the click made
   the client swing on the spot, out of range.
3. At the swing's contact point it rolls the accuracy check and, on a hit, lowers the monster's life with 0x1b1. The
   contact point is the active weapon's `attack1_<weapon>` clip contact keyframe (`AnimationContacts.AttackContact`:
   its shield or dual-wield variant, then any attack1 of that weapon, else 0.4) as a fraction of the attack time, since
   the client stretches the clip to it, plus 150 ms for the client's walk into range lagging the server's estimate
   (`Game_HIT_OFFSET_MS`). It never lands later than the next held attack may start (a quarter early, less 50 ms:
   `AttackTiming.ContactDelay`), because that attack cancels a hit still waiting. The chance is PoB's `calcs.hitChance`: `accuracy / (accuracy + (evasion / 5)^0.9) × 125`,
   rounded and clamped to 5..100, with the monster's `DefaultMonsterStats.Evasion` at its level. The player's accuracy
   for this roll is the pools' rating raised by the Accuracy Mastery's target conditions under
   `TargetAccuracy.AgainstTarget` at the target's own rarity and position at that moment (§4); the gateway logs the
   value whenever a condition changes it. A miss is logged and
   sends no life update; spells do not roll accuracy and always hit.
4. Any later player action (0x25 move, another 0x24) cancels steps 2 and 3 (`ClientState.ActionCount`).
5. Pacing: one swing per attack time. While the button is held the client sends one 0x24 per swing; a swing may
   arrive up to a quarter of an attack early (jitter) and the schedule stays on the client's cadence. When the server
   thought the swing took 833 ms instead of 757 ms it fell behind and dropped every third or fourth swing, which then
   animated without damage.

**Critical strikes.** The server rolls a hit's critical chance after the damage roll: `base chance × (1 + increased
critical strike chance / 100)`, capped at 100%, then multiplies the damage by the final multiplier (150% plus
`base_critical_strike_multiplier_+`), as PoB's `CalcOffence` does. PoB's `unarmedWeaponData[class].CritChance = 0`
and `base_critical_strike_multiplier = 150` are the defaults used here. The base is the equipped main hand's
`WeaponTypes.dat` `Critical` permyriad (830 = 8.30%); unarmed is 0. Two pure base stats replace that base before the
global increases, the way PoB's `WeaponBaseCritChance` override replaces an attack pass's
[source.CritChance](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/16de4b82d57f1c0de6eb40f37143c32d4da36a02/src/Modules/CalcOffence.lua#L2003-L2012)
only when that hand's weapon type is not "None" (ModParser gets it from "Base Critical Strike Chance for Attacks
with Weapons is X%").
`override_weapon_base_critical_strike_chance` (Stats.dat row 9892, client key 9893, permyriad) is the armed main
hand's base: Slayer Overwhelm (graph id 17315) grants 800 = 8.00%, and the Blind Prophet position's
Sightless (graph id 24698) grants 900 = 9.00%.
`base_melee_critical_strike_chance_while_unarmed_%` (Stats.dat row 3906,
client key 3907, permyriad; the Behemoth position's Final Blow, graph id 10135, grants 1000 = 10.00%) fills the unarmed
melee attack that has no weapon base. Each is a base, not a fixed final chance: the generic
`critical_strike_chance_+%` increases still apply on top. A weapon hand keeps the unarmed base out, an unarmed
context keeps the weapon override out, and `SpellCriticalStrike`'s spell route reads neither.
The active weapon set determines the attack base and weapon-specific increases; an inactive weapon grants neither.
`global_cannot_crit`, `virtual_global_cannot_crit` and `cannot_critical_strike_with_attacks` suppress attack crits.
The pool builds the base once in `AttackBaseCriticalStrike`.
`/set_stat` replaces its source value and rebuilds the pools for critical-chance inputs and prohibitions.
The panel's final `main_hand_critical_strike_chance` and the rolls share that result.
Generic and spell increases use the same effective raw inputs as `CharacterPlayerStats`: a development value,
including zero, replaces that stat's complete passive/equipment/aura sum.
The attack and pooled spell reference report those effective increased percentages, so `/crit` agrees with the rolls.
The attack base feeds the shared `DamageModel.CriticalStrikeChancePerMyriad` used by spells, rolls and panels.
Nonnegative half permyriad values round up: a 650 base with +1% increased gives 657.
Generic and applicable typed inputs are summed as signed `long` values before the final cap;
large positive and negative inputs may cancel before clamping.
Native checks recorded below belong to the original PR head `8c4c62d`.
The shared half-up midpoint and resolved stack need a new native check; automated coverage establishes the contract.
Signed increases stay in 64 bits through the pool and player-plus-skill sum; only final native values are bounded.
Pool refresh uses exact supported stat names; unsupported conditional or minion stats do not rebuild pools.
Pool rebuilding runs outside the monster lock and retries if another rebuild changes the pools before assignment.

**Conditional critical policy (#242).**
The supplied 3.29.3b static evidence identifies `0x1427e5480` as the virtual critical-extra-roll getter.
It adds `extra_critical_rolls`, `extra_critical_rolls_while_on_low_life` when virtual stat 7751 holds,
`extra_critical_rolls_during_focus` while `has_focus` holds, and one for `keystone_sharp_and_brittle`.
Its final signed clamp at `0x1427e59f9` is -1..1, after the addition; two lucky sources still mean two total rolls.
There is no Attack or Melee restriction on the low-life term, so supported spell hits qualify too.
`CriticalStrikePolicy` and `Program.CriticalStrikes` consume those raw inputs independently of any class choice.
Low life is derived from current living owner Life and maximum Life with an inclusive 50% default threshold.
`low_life_threshold_%_override` takes precedence over `low_life_threshold_%` when positive.
The not-full-Life and Vulnerability count-as-low-life flags use current Life and current curse-state stats.
The Focus skill and curse application lifecycles are not implemented by this critical policy.

The existing positive-chance `Next(10000)` stays after the damage packet's original per-type draws.
A nonzero signed extra adds exactly one following `Next(10000)` even if the first draw decides the result.
Positive luck combines with OR, negative luck with AND, and zero luck uses only the original draw.
This ordering is LocalExile's local RNG policy, not an established official-server RNG sequence.
Cannot-crit flags take precedence and consume no critical draws, including under lucky conditions.
The default attack, supported attack/spell projectiles and area hits share this policy at accepted contact.
Ordinary bow arrows keep their existing released damage/chance/multiplier snapshot and sample conditional luck
and cannot-crit flags from the live owner at impact.
No additional packet, accuracy or impale draw is introduced by the policy.

`unarmed_melee_attack_critical_strike_multiplier_+` adds points to the ordinary multiplier only for an active
empty-hand melee Attack context without Spell; an inactive-set weapon cannot arm that context.
A selected ranged attack and every spell exclude it, including when their owner is unarmed.
The unarmed melee base also stays out of a selected ranged attack's chance.
Final Blow's raw graph node 10135 grants 1000 base permyriad, one low-life extra roll and 50 multiplier points.
The generic consumer uses the raw stats; it adds no ascendancy choice, class or node-allocation behavior.
Default and selected supported panel multipliers use the same context and add linked support bonuses once.
The panel retains the ordinary permyriad chance; `virtual_extra_critical_rolls` carries the separate lucky policy.
`virtual_on_low_life` and the selected skill's extra-roll virtual follow current owner state.
The player tick refreshes player/skill stats when Life, Focus or the effective extra-roll condition changes.
Combat samples contact state directly and does not wait for that panel refresh cadence.
Full player-stat and Actor refreshes use the shared `SendPlayerStatSnapshot` factory inside the connection's send gate.
The factory locks `Monsters`, rejects closed/leaving connections and samples current stats at send time.
A queued character or charge refresh cannot replay old low-life virtuals or an earlier active-weapon multiplier.
Held-gate wire regressions cover both directions across the 50% boundary and active-set swaps in both packet kinds.

Automated tests parse `0x24` into the actual object-use route and check Life losses and deterministic critical draws.
Controls cover default contact, ordinary bows, linked melee/ranged attacks, spell projectiles and spell areas,
the 50% boundary, signed cancellation/clamping, live state changes, active sets and cannot-crit precedence.
The existing module initializer supplies the checkout root once; production catalogs retain relative `data/` paths.
The tests consume node 10135's raw stats without allocating an ascendancy node or selecting a Phrecia class.
An unselected ascendancy refund is refused without spending points; removing the raw inputs clears the policy.
Upstream's separate ascendancy lifecycle and `AscendancyClientCompatibility` play gate remain unchanged.
A successful paid Final Blow refund is not established by these raw-source controls or the native checks below.
Fifteen native checks used a new unascended Marauder with raw inputs.
The active empty hand dealt a 200-damage critical hit; an active wand dealt 150, and an inactive wand did not suppress
its empty alternate hand's 200 multiplier.
Removing the raw bonus restored 150.
A 66-Life owner at 34 Life was above low life, at exactly 33 was low life, and remained low at 32.
Native received virtuals confirmed signed cancellation, the one-extra-roll clamp, unlucky state and condition clearing.
Cannot-crit forced an actual noncritical 100-damage hit despite a capped base chance and positive luck.
Healing, death and relog cleared the conditional state.
The spell multiplier stayed 150 while the unarmed melee multiplier was 200.
The hit checks exercise the default attack; supported spell/projectile contacts and exact RNG draw order are covered
by deterministic automated tests, not a native statistical-distribution claim.
The fifteen controls were also repeated on the resolved `effb797` composition.
The Fortification combination below has automated coverage; the native controls do not allocate Final Blow.
The merged Fortification path uses the same resolved critical multiplier as target damage.
Its source is the direct rolled packet before target mitigation and excludes stored Impale damage.
A parsed default-attack control with raw stats deals 200 damage to a 400-Life target and grants 14 Fortification.
Removing the unarmed bonus gives 150 damage and 12 stacks; cannot-crit gives 100 damage and 10 stacks.
These are automated checks of LocalExile's explicit Fortification generation model, not new native evidence.
Once Phrecia play is compatible, allocate/refund node 10135.
Verify that base, multiplier and conditional luck clear together.

**Existing base-chance native checks (historical).**
Twelve real-client checks used a fresh synthetic character and read the client's received stat values.
A Driftwood Wand's 830 became 800 and 900; 100% increased chance raised 900 to 1800, then cannot-crit set it to zero.
Switching to an empty second set gave zero despite the inactive wand; the unarmed base gave 1000.
Switching back restored 900, clearing the weapon override restored 830, and relog cleared the development overrides.
Automated regressions also cover generic/spell development increases, aura replacement and active-hand increases.
Five follow-up native getter checks verify the corrected development inputs on a new synthetic character.
With weapon base 900, generic increase 0 gives attack 900 and the pooled spell reference 500;
generic 100 gives 1800 and 1000, then spell-specific 50 changes only the spell reference to 1250.
Generic `int.MaxValue` with that spell modifier caps both chances at 10000; `int.MinValue` clears both.
The Gateway runs from its checkout root with repository-relative catalogs during these checks.
These checks validate the generic stat consumers; ascendancy allocation remains in its separate lifecycle change.
Spells use their own level's `GrantedEffectStatSetsPerLevel.SpellCritChance`:
Fireball 500 (5.00%), Freezing Pulse and Ice Nova 750, Arc 600, Scorching Ray 0.
`SkillCatalog.SpellCritChance` returns null for missing/invalid data, distinct from a known zero; both fail closed.
The unconditional passive stats are summed into `CharacterStats.Pools`: generic `critical_strike_chance_+%` and
`base_critical_strike_multiplier_+` for both attacks and spells, the attack/melee/weapon-shape chance and multiplier
stats for attacks, and `spell_critical_strike_chance_+%` / `base_spell_critical_strike_multiplier_+` for spells. The final
`main_hand_critical_strike_chance` / `_multiplier` and `spell_critical_strike_chance` / `_multiplier` stats go to the
client, so the Offence panel shows them.
`SpellCriticalStrike` shares the raw player-plus-skill increased-chance calculation between `GemSkills` and projectile/
area spell hits; the old final Fireball-based player display chance is not added as another input.
The overcapped lightning resistance's increase reaches that calculation through `CharacterPlayerStats`, which adds the
pools' stored derived value to the raw `critical_strike_chance_+%` once (§12); the attack and panel chances fold the
same value in `PoolsFor` instead.
Precision and Zealotry expose their existing crit bonuses through `AuraCatalog.RawStats` for that calculation.
Pool construction still reads passives/items and adds `AuraCatalog.Bonus` once; it does not read `RawStats` back.
The result rounds to integer permyriad, halves away from zero, then caps at 0–10000, matching
[PoB CalcOffence](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/16de4b82d57f1c0de6eb40f37143c32d4da36a02/src/Modules/CalcOffence.lua#L3022-L3027).
`global_cannot_crit` and `virtual_global_cannot_crit` disable the supported spell roll.
Ordinary increased chance cannot make a known zero-base spell crit.
The selected skill's spell rows also require `casting_spell = 1` (CharacterPanelStats `Spell_CritChance`);
the main-hand rows exclude that flag.
`EffectStats` supplies it only for an actual ActiveSkillType Spell without Attack, and sends zero otherwise.
Warcries are not classified as spells merely because they are not attacks.
Area and projectile hits use the same actual Attack/Spell types, with Attack taking precedence for a mixed type.
Bow attack projectiles retain their weapon damage, damage effectiveness, attack critical chance and multiplier;
the spell-specific calculation applies only to the spell route.
`SkillHitPlan` rejects unsupported types and spells without extracted hit damage before building a weapon packet or
rolling critical chance, so Scorching Ray, Righteous Fire and warcries cannot fall through to a wand hit.
This adds no beam or damage-over-time implementation.
The copied-save baseline recorded stored spell chance 500 for both Fireball and Freezing Pulse,
and the selected Freezing Pulse panel still showed main-hand rows (main-hand crit 830).
Native getters also read the stored 500; evidence is `logs/spell-crit-baseline.json` in the validation checkout.
Copied-save native panel/getter checks now show Fireball 5%, Freezing Pulse and Ice Nova 7.5%, and Arc 6%.
With 100% increased generic crit they show Fireball 10% and Freezing Pulse 15%; adding another 100% spell crit gives
15% and 22.5%, respectively, so the inputs add once.
`global_cannot_crit = 1` makes the native skill chance zero and hides its critical row.
After clearing those modifiers and relogging, Freezing Pulse still shows 7.5%.
Scorching Ray remains explicitly zero with 100000% increased generic crit; its actual cast caused no weapon hit or
critical roll against a nearby 86-life Ash Harvester.
On the same connection, Fireball with that increased chance dealt a logged 22-damage critical hit, leaving 64 life.
The final Arc/Ice Nova and hitless-spell checks used the reviewed type-routing fix.
Evidence: validation `logs/spell-crit-{after-default,after-generic100,after-combined100,after-disabled,after-relog,
final-arc-ice,final-scorching-ray}.json`, `spell-critical-live-summary.json` and `live-gateway.log`.
Native probes reported no errors.
Catalog, raw-source, aura, flag, rounding, hit routing and roll-boundary checks are also automated.
Timed, charge, enemy-state, lucky/more and added-base critical mechanics remain outside this slice.
`/crit` prints the attack values and the legacy pooled 5%-base spell reference, not the selected gem's chance.

**Impale.** A hit that rolls `impale_on_hit_%_chance` stores 10% of the swing's physical damage on the monster as a
stack: five at most, each lasting five physical hits. The stored amount is taken from the packet's physical **maximum**,
not the rolled damage, so it is steady for a given weapon. Every later physical hit on that monster first takes the
stored total as extra physical (a `DamageRange(stored, stored)` added to the packet before `RollTotal`, so it converts and
mitigates with the hit) and then spends one hit of every stack. Order, in `Gateway.Attack`: add the bonus, `AfterHit()`,
then roll `Apply(physical)` — a new stack applied first would lose one of its five hits on the swing that made it. The
stack count and stored total are read before the hit applies because a killing blow clears them, which is why the log
reports the real stacks on the killing 0x1b1. Only physical is stored. Live 2026-10-20, `/set_stat
attacks_impale_on_hit_%_chance 100` on one monster: `hit for 5, impaled (1 stack(s), 0.5 stored)`, then `impale +0.5 (2
stack(s))`, `impale +1 (3)`, `impale +1.5 (4)`, `impale +2 (5 stack(s), 2.6 stored)` on the killing critical, and the next
monster reopened at one stack.

Exits use the same approach (`ExitReach` = 6 cells) before the area change or the instance list.

**Life bar on death:** when life reaches 0 the client's Life tick (`0x141e6a190`) stops notifying its listeners, so the
mini life bar over the monster kept its last value. The bar entry (`0x140220ab0` computes its flags; `0x140221fd0`
decides visibility) also listens to stats (`0x140220c30`): `hide_mini_life_bar` (0x2a8b) or `virtual_is_hidden_monster`
(0x59d0) hide it. The Gateway sends 0x1b3 `hide_mini_life_bar = 1` on the monster with the killing 0x1b1. Live: the bar
disappears.

## 6. Monsters attack

Live 2026-10-17: Hungry Corpses walk up, swing (animated) and lower the player's life; walking away dodges a swing.

- **Skill.** A monster's Actor gets the same skill list as the player (slot 20, f0 0x40) with its MonsterVarieties
  `GrantedEffectsKeys[0]` (Hungry Corpse: 406 `Melee`). Monster effects have per-level rows only for some levels, so
  `SkillCatalog.FindByRow` takes the highest GEPL / GESSPL level at or below the monster's (Melee: level 1, GEPL 4394).
  Id 0x0102, like the player's (no ActionType masks to it).
- **Stats.** The final attack stats (`MonsterAttack.Stats`): attack time = MonsterVarieties `AttackSpeed` (2400 ms),
  reach = `MaximumAttackDistance` (8), damage = DefaultMonsterStats `Damage` at the level × `DamageMultiplier` / 100
  with a ±20% spread (the spread is assumed), then the monster's mods (its rarity's, its affixes: damage, attack time,
  extra fire / cold / lightning / chaos; [14](14-monster-packs.md) §5). They go in the monster's Stats component and
  in the skill.
- **`skill_is_non_pathing` (0x4f32) on the skill.** The Melee action's begin (vtable +0x38 `0x141938830`) is just that
  stat: when 0, the action runner (`0x141e8e060`) sends the action through the client's own walk-into-range step,
  which drops a server-driven monster's queued attack in the same frame (traced: created, queued, both checks passed,
  then neither current nor queued). The server walks the monster itself, so the skill is non-pathing. Only on the
  skill: on the monster's Stats component it also stops its Move action from pathing (the monsters stood still).
- **AI** (`RunMonsterAi`, 100 ms): a hostile, chasing monster within reach stops and swings: 0x1ab with its skill, flags
  0x0408 | 0x80, target = the player, sequence 0xffff. At half the attack time the hit lands if the monster lives and
  the player is still within reach + 2: 0x1b1 on the player.
- **Defence.** The monster's accuracy is `DefaultMonsterStats.Accuracy` at its level (raised by its mods); the server
  rolls the player's
  evasion with the same `AccuracyHitChance` formula and sends no life update on a miss. The roll uses PoE's shared
  entropy counter (0–99): each attack adds its hit chance, 100 or more means a hit and subtracts 100, and the counter is
  randomized after 3.33 s without an attack. A hit is a critical strike only when the monster's data gives it a
  chance: monster attacks have none (the monster Melee stat sets' `AttackCritChance` is 0, Monster.ot only sets the
  130% `base_critical_strike_multiplier`), so only the of Deadliness mod makes one crit, at 130% less the player's
  `base_self_critical_strike_multiplier_-%` (at least 100%; [14](14-monster-packs.md) §5). Live 2026-10-26: a normal
  Hungry Corpse never crit, one with of Deadliness every time. The hit then goes through the player's armour
  (`MitigatePhysical`) and block. `/defences` prints the reference values.
- **Player life** (`ClientState.PlayerLife`, from the level/class/passive pools). At 0 the client plays the death
  animation and blocks moving. `/heal` sends full life: 0x1b1 from 0 runs the client's revive. The overhead life bar is
  hidden at 0 (`hide_mini_life_bar`, as for monsters) and shown again on revive.

## 7. The death screen

The client's `death_screen` panel (constructor `0x14028fc80`, setup `0x140290820`) does not open by itself when the
local player dies: **S→C 0x55** opens it (instance group, class `0x1435dce40`, read `0x14219f820`, dispatcher case 0x14 →
listeners at instance +0xf78 → `0x140d5d6a0` → a task → HUD vtable +0x4d0 `0x140d86180`).

```
u16 0x0055, u8 flags: bit0, bit1 = hide, bit2, bit3, bit4
unless bit1: u32 BE (+0x18), u8 (+0x15) when that u32 is non-zero, u16 BE (+0x1c)     (meanings unknown, sent 0)
```

Title "You have died!" (ClientStrings ids are row + 1). Live 2026-10-17:

| Flags | Buttons |
|---|---|
| 0x0c (standard league) | "Resurrect in Town", "Resurrect at Checkpoint" |
| 0x00 | "Resurrect" |
| 0x01 (hardcore) | "Exit" ("Exit to the character selection screen": sends C→S 0x153, not implemented yet) |
| 0x02 | hides the screen |

bit3 turns the left button into "Resurrect in Town", bit2 adds the checkpoint button (not with bit0); a third
"Spectate" button belongs to PvP. bit4 and the trailing fields are not mapped (Sanctum and other modes probably use them).

The buttons (`0x140290bf0`) send **C→S 0x54**: `u16 0x0054, u8 flags`, bit0 = "Resurrect in Town" (1) or the
checkpoint / plain "Resurrect" (0). The Gateway (`Resurrect`): hides the screen (0x55 bit1); in town → area change to
the character's last town (`LastTown` in Auth's "resolve" answer, the town the next login would start in; the new player
object has full life); at the checkpoint → 0x1b1 full life first (a teleport sent while
dead was ignored), then 0x1b7 next to the area's waypoint if the character has activated it, else to the area's arrival point
(`AreaLayout.Checkpoint`). `Game_LEAGUE=hardcore` sends 0x01;
`/death_screen [flags]` previews any variant.

## 8. Stat keys

**Client stat key = Stats.dat row + 1** with the vendored schema: every key read during an attack matches
(0xf5 → row 244 `movement_velocity_+%`, 0x7d → 124 `base_maximum_life`, 0x144 → 323
`main_hand_minimum_attack_distance`, 0x35a → 857 `is_totem`, 0xce6 → 3301 `virtual_action_speed_+%`, …).
`scripts/re/frida/trace-skill-stats.js` logs the keys, values and callers of the stat getter `0x1424cfa40` while an
action is created (`0x141941c90`) or started (`0x141e8cf10`); `dump-stats-object.js` dumps the stored (key, value)
vector (stats +0xf0) of whichever stats object answers a key; `scripts/re/statkeys.py <key>…` lists the client code
that reads a key (`mov edx, key` + call). Stats like `main_hand_total_base_weapon_attack_duration_ms` are read by many
small calculators in `0x141b..`–`0x141d..`.

## 9. Gem skills: stats and casting

Live 2026-10-18 with the Witch's Fireball. Code: `SkillCatalog.EffectStats`, `GemSkills`, `CastOn`, `LaunchProjectile`
(Gateway).

- **Skill ids.** Gem skills take the ids after the default attack's that no ActionType `HASH16 & 0x3fff` uses
  (`SkillCatalog.SkillIds`): 0x0106, 0x010a and 0x0114 are basic actions in 3.29.3b, so plain counting from 0x0103 would
  have replaced one. Fireball is 0x0103.
- **Stats** (`SkillCatalog.EffectStats`): the effect's GrantedEffectStatSets row (ImplicitStats = 1, ConstantStats with
  ConstantStatsValues), its GESSPL row at the gem level (FloatStats with BaseResolvedValues, AdditionalStats with
  AdditionalStatsValues), the cast and the cost. Fireball level 1: `spell_minimum/maximum_base_fire_damage` 9–14,
  `active_skill_base_area_of_effect_radius` 10, `base_chance_to_ignite_%` 25, `base_is_projectile` 1, mana cost 5. The
  skill's list also carries the player's stats.
- **Cast time.** The client reads **`virtual_base_spell_cast_time_ms`** (0x24b9) when it starts the cast; without it
  the cast was instant (`trace-skill-stats.js`). The Gateway sends GrantedEffects `CastTime` (750 ms) there and in
  `base_spell_cast_time_ms`, and `hundred_times_casts_per_second`. `mana_cost` (0x2e72) is what the client spends.
- **Casting.** At the ground: C→S 0x25 with the gem skill's id. A **spell** stops where it stands and the server echoes
  0x1ab from there (no walk). An **attack** (ActiveSkillType Attack: Holy Strike, Ground Slam …) follows bit 0 of the
  message's flags, the client's **"Always Attack Without Moving"** setting (live 2026-10-25: 0x400 with it off, 0x401
  with it on; the setting itself also arrives as C→S 0x16 command 8):
  - bit 0 set: the player swings from where it stands; the server echoes 0x1ab there and pays at the swing.
  - bit 0 clear: the client is **attack-moving** — it walks to the aimed point itself and sends no C→S 0x25 Move for
    that walk. The server walks its estimate after it (`Walk.Order`) and answers 0x1ab from that estimate, like a
    move's echo. A point with a living monster on it within the skill's hit radius is a swing (the client aims at the
    cell, not at the object: Holy Strike on a monster sends 0x25, live 2026-10-25): the server walks into the
    weapon's reach and only then pays and hits. An empty point never swings, so running costs no mana.
  On a monster: C→S 0x24; an attack walks into the weapon's reach first (`Walk.Approach`, the swing is echoed on
  arrival, unlike a spell) and a spell casts from where it stands. An attack with "Always Attack Without Moving" on
  casts in place. On the player itself (Righteous Fire): C→S 0x24 with the player's id as the target; the server
  echoes 0x1ab in place.
  An **instant** skill (stat `skill_is_instant`, below) is not sent this way.
  C→S **0x191** carries an object target (8 bytes: u32 object id, u16 skill id).
  C→S **0x190** carries a ground aim (12 bytes: u32 x, u32 y, u16 skill id), without an action sequence.
  Sender `0x1401232e0` selects 0x190 for target mode 4 and 0x191 for object/self modes 5/6.
  The 0x190 class `0x1435c0718` writer `0x141f91f60` confirms network order and these widths.
  Actual Frostblink Q sent `0190 0000011A 000000DC 0103`; unframed, this discarded the rest of its receive chunk.
  Both messages now frame independently, including fragments and coalesced following messages.
  Only an equipped Instant gem may use either route; movement currently supports ordinary Frostblink only.
  The server applies the effect and answers with S→C **0x1ce** (`InstanceProtocol.InstantAction`:
  object header, u16 skill id, u16 sequence (0xffff), u16 flags (0x20 → u32 target object), compact target position;
  handler `0x142195880`): the client runs the skill's action without animation and consumes a use of the skill
  (actor vtable +0x198), which starts the skill bar's cooldown. Without it the tooltip showed the cooldown but the bar
  never ran it: the cooldown records had no entries (Frida, live 2026-10-21; not yet checked with 0x1ce).
  A location acceptance has flags 0, no object id, sequence 0xffff and the original ground aim.
  Ordinary Frostblink uses the existing mana and cooldown checks but skips the generic single-area hit approximation.
  Its server movement commits inside the 0x1ce send gate after repeating life, area, gem and placement checks.
  IceDash action `0x1412f1200` records its departure and aim; endpoint resolver `0x141bfe240` clamps distance
  to an integer maximum `42 * (100 + skill_maximum_travel_distance_+%) / 100` and a 32-cell minimum.
  Component rounding is `floor(component * factor + .5)` in `0x140671d00`, including negative components.
  The server uses the same emitted player/skill stat precedence, refuses overflow and unsupported minimum/maximum cases,
  and requires a captured grid, in-bounds aim/endpoint, clear straight terrain, quest barriers and endpoint clearance.
  It rejects cases needing the client's terrain endpoint search instead of guessing a destination.
  Manual native 0x1ce experiments confirmed near aim `(290,229) -> (300,220)` resolves to `(314,208)`;
  far aim `(311,220) -> (200,220)` resolves to `(269,220)`.
  While walking from `(279,221)`, aim `(310,220)` resolved to `(311,220)` and ended the old predicted Move.
  These manual protocol experiments establish resolver and walk-stop behavior separately from production validation.
  The server mirrors that walk stop with `Walk.Teleport`, preserving ActionCount, channel and pending release tokens.
  Hovering a monster sends Frostblink as **0x191**, rather than 0x190; the former nonmovement-only gate rejected it.
  The object confirmation uses flags 0x20 and the target id, with its raw cell as the coordinate fallback.
  Handler `0x142195ab1..0x142195b0e` binds the entity through `0x14193e0a0` and replaces the packet aim with its
  Positioned cell. IceDash `0x141bfe1e0` and `0x141bfe240` then route a bound object through `0x141bfe610` before
  applying the usual distance limits. Simply passing the monster's centre to the ground resolver is incorrect.
  The object resolver first extends a short direction to 32 cells, rounds its components, then normalizes that
  rounded vector again and extends it by player Positioned size + target Positioned size + 2.
  Positioned size is `object_size - 1`; the normal player has size 1, and Ash Harvester ObjectSize 3 gives size 2.
  `0x141c02050` traces those candidates in inclusive Bresenham order, retaining the last valid cell.
  Its first terrain-layer threshold can abort the trace; other terrain or target-footprint failures skip a cell.
  The target-footprint check is `0x141c02c30 -> 0x141c03c80`; the outer resolver may then search terrain again.
  The server accepts only a wholly clear candidate extension and the existing conservative final placement.
  Partial traces, coincident targets requiring seeded random direction, unknown sizes and other object types reject.
  Both preflight and the actual confirmation gate require a living monster whose current walk has ended.
  The object id makes the client resolve its target again on delivery, so moving-target parity is not claimed.
  At the gate the current equipped gem, player/area state, target, sizes, raw cell and placement are checked again;
  one game-time sample observes both walkers. No invalid preflight pays mana or reserves a cooldown.
  A start invalidated after payment retains the existing accepted-use cost policy, but sends no late confirmation.
  The object route skips generic area damage and preserves action/channel tokens, as the ground route does.
  Manual native object confirmations from `(280,220)` with player/target sizes 1/2 established these clear cases:
  target `(300,220)` traces `(312,220)..(317,220)` and lands `(317,220)`;
  target `(330,220)` traces `(330,220)..(335,220)` and lands at the 42-cell clamp `(322,220)`;
  target `(300,240)` traces `(303,243)..(307,247)` and lands `(307,247)`;
  target `(300,230)` traces `(309,234)..(314,236)` and lands `(314,236)`.
  The last case distinguishes normalizing the rounded minimum vector from reusing the original direction.
  Reflected target `(260,220)` computed `(248,220)..(243,220)`, but terrain retained `(248,220)` and the native
  fallback landed `(264,220)`; the server rejects such partial traces instead of guessing that fallback.
  Evidence is `logs/instant-location-object-manual-{near,far,diagonal,offaxis,reflected}.json` in integration.
  These are injected native protocol experiments, without server movement or production cost acceptance.
  Actual hovered Q before this extension produced 0x191, no confirmation, no cost and no movement.
  Production hovered Q now passed from `(280,220)` against stationary target `(300,220)`, with sizes 1/2.
  The native request hook recorded message 401 (`0x191`), skill 259 and target 12288.
  Exactly one 12-mana cost changed mana 175 to 163 and emitted one object-bound 0x1ce.
  Its flags were 0x20, target id 12288 and raw aim `(300,220)`.
  Native position and server `/pos` both became `(317,220)`.
  A framed ground 0x190 retry after 0.5 seconds produced no extra mana cost or confirmation.
  Combined #197 progress synchronization retained base 3000 ms, total 3 seconds and recovery rate 1.
  Near evidence: integration `logs/instant-location-object-live-near.json`.
  Final evidence: integration `logs/instant-location-object-live-near-final.json`.
  Framed production 0x191 controls from `(280,220)` matched native/server endpoints for two stationary targets.
  Off-axis target `(300,230)` reached `(314,236)`; far target `(330,220)` reached `(322,220)`.
  Each accepted control paid 12 mana once and emitted one object-bound confirmation.
  A framed request on a target walking from `(330,220)` toward `(283,220)` was rejected without cost or movement.
  Reflected partial terrain, missing target `0xdeadbeef` and noninstant Fireball 260 were also rejected.
  Those three framed invalid requests left mana 175, native cooldown entries empty and position `(280,220)`.
  They emitted no 0x1ce.
  These are production framed-request controls, distinct from actual hovered Q and injected native RE confirmations.
  Framed positives: integration `logs/instant-location-object-framed-{offaxis,far}.json`.
  Moving rejection: integration `logs/instant-location-object-framed-moving-reject.json`.
  Other invalid cases: integration `logs/instant-location-object-framed-invalid.json`.
  End-to-end stationary validation subsequently passed with actual Q input from `(280,220)`:
  raw aim `(308,221)` resolved to native/server `(312,221)`.
  Requests after 0.3 seconds, with noninstant Fireball, or with an unsigned-invalid x coordinate were rejected.
  Rejections sent no confirmation and changed no mana or position.
  A retry after 3.2 seconds aimed at `(300,220)` and reached native/server `(280,218)`.
  Exactly two accepted uses paid 12 mana each, generated two ground confirmations and consumed two native uses.
  The combined validation also included the separate #197 cooldown progress synchronization after each acceptance;
  its record retained base 3000 ms, total 3 seconds and rate 1.
  No 0x190 request remained unframed.
  Evidence: `logs/instant-location-live-default.json` in the integration validation checkout.
  Production ground Frostblink use during an active walk has not been measured;
  the walking evidence is manual native RE.
  Departure/arrival damage, chilled ground, nearby-enemy cooldown recovery, shared Blink cooldown groups,
  other location instant skills and complete movement restriction/terrain searches remain unimplemented.
- **Hit reaction (live 2026-10-25).** A monster that takes a hit plays its rig's **TakeHit** clip with S→C **0x1b2**
  (`InstanceProtocol.AnimationUpdate`, kind 0): object header, u8 kind, u32 clip index (+0x24), u32 float bits (speed,
  +0x28). The index is 0-based into the actor's animation list, which is the rig `.amd`'s animations in file order
  (Sanctum zombie: `flinch_unarmed` = 156 of 249 in `Art/Models/MONSTERS/skeleton/rig.amd`; `flinch_pitchfork` = 151,
  both played the flinch *with its SoundEvent* live). The handler (`0x141e58ef0`) calls `0x141dba530`, which ignores an
  index past the actor's animation count — a wrong index costs nothing. `0x1b1` alone only stores the pool and runs the
  death/revive slots, so a hit without 0x1b2 is silent and still. A killing blow gets no flinch (the client's death
  runs instead). The reaction is gated like a stun: the hit must take at least a tenth of the monster's max life, and a
  monster does not flinch again until its clip has played (its `.amd` length at speed 1, at least 0.4 s) — otherwise a
  fast attack or a volley restarts the clip and its sound and keeps cutting the monster's own attack and walk animations
  while the server still moves it. The flinch is checked again inside the connection's send gate, so a hit that kills
  the monster meanwhile (marked Dead before its own 0x1b1 goes out) drops it rather than playing it over the death.
  `data/monster-takehit.json` (`python -I scripts/re/monster_takehit.py`) holds each MonsterVarieties
  row's index(es) and clip lengths: its `.act` maps `TakeHit` to a clip, and the `.ao`'s `AnimationController` names
  the rig. A rig whose parsed animations do not add up to the `.amd`'s count line is skipped (a missed entry would shift
  every later index to a wrong but in-range clip). 26 `.act`
  files map a pool — `flinch_01, flinch_02, flinch_03`, or `flinch_small:max_duration=0.45, flinch_large:min_duration=0.45`
  (the condition picks by the hit's stun duration, which the server does not model) — and the server picks one at random
  per hit; every other variety has a single clip. An `@Name` alias is expanded to the whole list it names, and every
  `extends` parent is followed (not just the first), with "nothing" ending the chain. The client also has the directional
  types TakeHitForward/Backward/Left/Right, but the `.act` files that set them alias them all to `@TakeHit`, so they add
  no variants. 0x262 (a Life "changed by" event with a signed delta) reaches the Life listeners but plays no flinch,
  sound or blood.
- **Player hit recovery.** S→C **0x1d1** selects an `Animation.dat` type through the actor's current class/stance ACT.
  `InstanceProtocol.ActorAnimation` writes object header, BE float seconds, u8 animation type, u8 boolean.
  Type **5** is `TakeHit`; this is not a rig clip index.
  Reader `0x141fde9d0` widens the type to message `+0x24` and stores the boolean at `+0x28`.
  Actor handler `0x1421949c3` calls virtual `+0xe0`, `0x141e95e60`.
  It ends predictive action `+0x1b8` through `0x141e90740`, cancels queued work, and sets Actor `+0x219` bit 2.
  Action gate `0x141e8d900` rejects starts while that bit is set.
  Completion `0x141e934b0` clears the bit and resumes queued/idle processing.
  Ground bow attacks reserve their pacing and token under the same recovery gate; queued arrow creation checks it.
  Live on a captured Strand, a 0.5 s packet stopped predictive walking and a subsequent cast.
  The predicted/current action became null, movement stopped, and recovery cleared after about 0.508 s.
  A new cast worked afterward.
  Integrated copied-save tests used a level 1 Witch (57 life, no ES) and Ash Harvester row 717 at level 7.
  Actual 24/27 physical damage hits stopped client prediction and the server walk estimate at cell (283, 216).
  TakeHit selected Witch clip 177 and cleared Actor `+0x219` bit 2 after the requested 429 ms recovery.
  A slow Fireball cast was cancelled before release, with no projectile created.
  An already released Fireball continued through recovery to impact and removal.
  A bow-equipped copied character also lost a pending Burning Arrow before release after an actual monster hit.
  An ordinary ground arrow request during recovery produced no action echo or projectile; a fresh request worked afterward.
  Burning Arrow released before the hit continued to removal, while native recovery blocked the new arrow request.
  With `cannot_be_stunned = 1`, another actual 24 damage hit sent no 0x1d1 and did not set the recovery flag.
  Evidence: `logs/monster-hit-recovery-{walk,cast,flight,immune}.json` in the validation checkout.
  In contrast, 0x1b2 merely replaced the clip while movement or server cast release continued.
  Its Play ABI is `0x141dba530(controller, &resourcePair, index, speed, ...)`, with the index in **r8d**.
  Actor `+0x1a0` is the body controller; its main state is `+0x180`, clip index `+0x190`, elapsed `+0x1a4`.
  `trace-player-hit-animation.js` records these fields and Positioned movement without sending packets.
  The trailing 0x1d1 boolean enables a client avoid-interruption roll; the server sends false and decides eligibility.

  The supported slice is surviving, unblocked **physical monster melee**.
  [PoB's defence calculation](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/16de4b82d57f1c0de6eb40f37143c32d4da36a02/src/Modules/CalcDefence.lua#L2154-L2250)
  gives effective physical damage a 25% bonus, then rolls `min(100, 200 × effectiveDamage / stunThreshold)`.
  Base chances at or below 20% are discarded before applying unconditional stun avoidance.
  The life threshold includes unconditional `stun_threshold_+%`; known stun immunity is respected.
  Duration uses PoB's 350 ms base, the extracted Monster.ot's 20% more melee stun duration, self duration modifiers,
  and the player's final stun recovery, rounded upward to a 33 ms frame (normally 429 ms).
  Invalid stats yielding a nonpositive duration or more than 60 s disable recovery rather than locking input.
  `SendPlayerHitRecovery` commits cancellation inside the send gate after rechecking survival and active recovery.
  It increments `ActionCount`, clears channel hits, and stops `PlayerWalk` at its current cell.
  Pending melee, casts and area hits retain their original token across awaits; released projectiles keep flying.
  Projectile creation rechecks the token inside the send gate; cancellation resets ordinary attack pacing too.
  Buffered 0x24/0x25/0x197 inputs are rejected using socket-read game time through the recovery end.
  Object walk-ups reserve movement and the original token under the same recovery check.
  Their delayed continuations and queued action/teleport echoes recheck that token.
  Recovery does not restart while active; flasks and instant skills keep their existing paths.
  This does not implement block recovery, ES stun avoidance, CI/mana/ES threshold substitutions, conditional defences,
  attacker-specific stun modifiers or monster spell/ranged stuns.
  Known final or raw base ES, active equipped ES bases and local flat ES disable this slice until ES avoidance exists.
  This includes Discipline before a separate ES pool implementation computes `maximum_energy_shield`.
  Conditional stun-defence builds, flat/substituted thresholds and CI are also skipped conservatively.
  An equipped skill with `skill_cannot_be_stunned` or contact-phase immunity disables this slice for the build.
  Its precise active action phase is not tracked, so even an inactive equipped skill is conservatively excluded.
- **Hit effects (2026-10-25).** The target's blood and the weapon impact sound come from S→C **0x1af** "action hit"
  (`InstanceProtocol.ActionHit`; class `0x1435ddb38`, read `0x141fddba0` → `0x1419bd6f0`, Actor handler `0x1421940a0`
  → `0x141e90f50`): object header (the attacker), u16 action sequence (must match the action's), u8, u8 skill index
  (0 = the current action), u32 target id, u16 flags, u8 kind (bit 0: belongs to an action, bit 1: hit data follows).
  With bit 1: a u16 if `flags & 0x400` (`flags >> 10 & 1` in the reader), u8 code, u8 hit angle (0..255 a full turn,
  `atan2(dx, −dy)` from attacker to target), then a u16 for kind bit 2 and one for bit 3. The handler calls the
  attacker's `ProcessDamagePackageInternal` (vtable +0x230), which plays the effects; 0x1b1 stays authoritative on
  life. The server sends it before 0x1b1 on the default attack and on attack-gem hits (flags 0x4201, kind 3) and on a
  monster's hit on the player (flags 0x4601 with u16 450, kind 2, sequence 0xffff); spells and projectiles skip it.
- **Release.** A spell leaves at its cast animation's **contact** keyframe, which the client plays stretched to the cast
  time: the Witch's `spell_projectile_wand` in `Art/Models/CHARACTERS/Witch/rig.amd` is 1066 ms long with `contact` at
  599 ms, so Fireball leaves after 56% of the cast. `python -I scripts/re/anim_contacts.py` writes every class's
  contacts to data/animation-contacts.json (`AnimationContacts`); the Gateway waits cast time × that fraction.
- **Cast speed (#192).** Genuine Spell/non-Attack skills keep the catalog milliseconds in both
  `base_spell_cast_time_ms` and `virtual_base_spell_cast_time_ms`. The native spell action already divides that
  virtual base by `1 + cast_speed_+% / 100`; pre-dividing it on the server applied cast speed twice.
  `SpellCastTiming` adjusts only the server duration and final `hundred_times_casts_per_second`, using the same
  computed cast-speed total. A 750 ms Fireball at +100% therefore takes 375 ms, and at -80% takes 3750 ms;
  the panel rates are 2.67 and 0.27 casts/s. The outgoing virtual base remains 750 in both cases.
  Native 3.29.3b evidence: `0x1419e7660` reads key 0x24b9 at `0x1419e7683`, divides by 1000 at
  `0x1419e7698`, then divides by one plus getter +0x498 / 100 at `0x1419e76a0..0x1419e76bd`.
  Its speed getter `0x1419e7500` ordinarily reads `cast_speed_+%` (296) at `0x1419e763c`.
  The alternate action `0x1419eef50` uses the same formula with getter +0x788.
  In the earlier recovery capture (`logs/monster-hit-recovery-cast.json`), a 1.066-second clip at playback speed
  0.0568533279 took 18.7500015 seconds: the old 3750 virtual milliseconds were divided by 0.2 again.
  Real-client Q casts in the combined validation build now have native clip durations 3.750000157 seconds at
  -80%, 0.375 seconds at +100%, and 0.75 seconds at zero. The native virtual base was 750 in all three captures;
  emitted cast rates were 27, 267 and 133 hundredths. Duration was measured as clip length / playback speed.
  Captures: `logs/skill-timing-after-fireball-{minus80,plus100,default}-real.json` in the integration checkout.
  These checks establish native clip duration; projectile release/contact alignment was not separately measured.
  Server projectile release, area-spell delay and the Portal spell's cast wait use the adjusted duration once.
  Catalog instant skills retain their instant flag, zero server cast duration and no finite casts-per-second rate.
  A channelled spell's native base is also preserved; this does not implement beam damage or change the existing
  server channel-hit cadence. Attack and non-spell timing paths are unchanged.
  For non-instant spells, a nonpositive speed factor or a duration exceeding signed integer milliseconds has
  no server duration, no hit/effect or mana cost, and zero displayed cast rate. Positive server durations round
  to milliseconds with a 1 ms numeric floor; this bound is not an official cast-speed cap. Conditional action-speed
  rules, skill-specific speed substitutions and further native timing mechanics remain outside this fix.
- **Instant skills.** The client knows an instant skill by its stat **`skill_is_instant`** (0x2064), not by the
  ActiveSkillType `Instant`; the server derives it from the type and sends it with `base_skill_is_instant`
  (`SkillCatalog.EffectStats`). Without it Haste's and Righteous Fire's actions used the spell action class (vtable
  `0x1434adb70`, as Fireball's) and waited for a cast that never played: the action stayed the player's current
  action (+0x1b0, flag +0x218 bit 0x02), and the queued-action gate `0x141e8d900` refused every other skill (only
  Move passes it) until a move replaced it (Frida, live 2026-10-21). With the stat the client sends the skill as 0x191
  (above) instead. The 0x1ab handler skips a remote actor's action of a skill with 0x2064.
- **Cooldowns.** The client keeps a skill's cooldown itself (manager +0x170 records, cooldown group from the data, 36 =
  per skill; entries `{remaining s, total s, recovery rate, -NaN}` added by `0x141eebfe0` when the skill is used), but
  only when the skill's stats have **`virtual_total_cooldown_count`** (0x4806; `0x141eea440` returns no cooldown
  otherwise). The duration comes from the data (or `skill_cooldown_time_override_ms`) plus `virtual_cooldown_modifier_ms`.
  `SkillCatalog.EffectStats` sends `base_cooldown_count` and `virtual_total_cooldown_count` = GEPL StoredUses for
  skills with a GEPL Cooldown. The client creates the cooldown record when the skill is added (`0x141eea6a0`) and
  needs a cooldown time there: the skill's +0xc8, which comes from the **skill record** (the third u32 of the record's
  `u32, u32, u32, u8` → +0xc0, +0xc4, +0xc8, +0xcc in `0x141ee73d0`), not from the data; or the stats
  `skill_cooldown_time_override_ms` / `skill_cooldown_is_4_seconds`. With 0 there the client kept no cooldown and the
  bar showed none (Frida: no records, +0xc8 = 0; live 2026-10-21); `GrantedSkill.CooldownMs` now carries GEPL Cooldown.
  +0xd0 (the cooldown group) comes from the GEPL row's CooldownGroup (+0x30).
  For ordinary one-use spells/attacks, the native base remains GEPL Cooldown; the final recovery percentage is
  player-computed `virtual_cooldown_speed_+%` (3398) + the skill's own base (3397) and virtual (3398) inputs once.
  Dash quality 20 adds 10%; the player's base input is already included in its computed virtual value.
  `SingleUseCooldown` shares this resolution between granted skill stats and server readiness.
  The server retains remaining **base milliseconds**, subtracting elapsed game-clock time times the recovery rate;
  stat/skill refresh settles the old rate before installing the new one, and pause advances no game time.
  Its ordinary preflight atomically checks readiness, reserves one use, and changes action/movement under `Monsters`
  before an action echo or effect can run.
  A queued continuation keeps the original action/area ticket; cancellation consumes the accepted use without a refund.
  An attack approach only checks readiness, then reserves at arrival under its original action token.
  Instant 0x191 skills preserve the current action.
  A projectile's creation gate ends action cancellation; released flights continue with their existing area lifetime.
  Multi-use, extra-use, duplicate same-effect gem, flat/override and pause-while-active mechanics retain legacy
  `SkillReadyAt` raw deadlines.
  Shared cooldown groups, bypass, conditional/enemy recovery, typed/more recovery and official tick equivalence are
  outside this generic one-use implementation.
  A zero recovery rate stalls a spent use.
  Extreme input sums are bounded to avoid signed native `percent + 100` overflow.

  Native evidence: Actor +0x598 owns the manager; its +0x170/+0x178 range holds 0x48-byte records.
  A record's +0x10/+0x18 vector holds 0x10-byte entries: remaining base seconds, total base seconds, recovery rate,
  flags; record +0x30 is uses, +0x34 base ms, +0x3c skill id, +0x40 group.
  Constructor `0x141eebed0` reads skill +0xc8 unless an override applies.
  `0x141eebfe0` takes the cooldown object at record +0x10 in RCX, recovery percent in EDX and group propagation in R8B;
  it stores base ms / 1000 and `(100 + percent) / 100` separately.
  Tick `0x141eebdb0..0x141eebdc8` subtracts elapsed time times rate.
  `0x141eeb7aa..0x141eeb7e7` can refresh active rates, but ordinary skill replacement does not call it for a positive
  rate change: `0x141eead13..0x141eead4a` only checks base/count changes or an existing zero rate.
  Likewise, SkillStats' `0x14219465b..0x1421946d7` checks modifier/count/override changes, not recovery percentage.
  Therefore updated 3398 alone leaves an active native entry at its old positive rate.
  The cooldown-only Actor 0x40 trailer above fixes that without replacing a skill or consuming another use.
  `0x142197d24..0x142197e7b` reads its entries; `0x142197f90..0x142197fd8` replaces the matching cooldown's vector.
  `InstanceEntities.PlayerSingleUseCooldowns` sends remaining/total **base** seconds, recovery rate, flags `0xffffffff`.
  An empty vector clears a use that expired while the update waited; native base duration and group are untouched.
  Only eligible spent uses whose original action/instant acknowledgement reached its gate can request this refresh.
  The packet's factory samples game time and resolves the current skill id/effect map under `Monsters` at the actual
  send gate, after the skill-stat update; dead/old-area states and recycled skill ids cannot refresh the wrong entry.
  Unsynchronized progress stays dirty through rate round trips such as 1 -> 2 -> 1,
  including changes before acknowledgement.
  Rejected/unacknowledged uses send nothing.
  Instant acknowledgements request one progress snapshot even at an unchanged rate: native use consumption starts at
  0x1ce, so its fresh full budget must align with the original server reservation after time spent queued.
  Ordinary casts keep their native predicted initial timing and refresh only changed progress.
  A refresh replaces one entry, including zero-rate transitions, rather than appending or restarting it.
  Initial ordinary prediction/network alignment and official tick boundaries are not claimed exact.
  Pre-fix real-client Dash at +100% held base 2.5 s/rate 2 and completed natively within 1.4 s, but a raw 0x25 retry
  was echoed/moved before the server rejected it for 1132 ms remaining (`logs/cooldown-before-dash-plus100.json`).
  Automated checks cover 0/+100/-50%, source aggregation, quality, changing rates, pause,
  and atomic stale-use rejection.
  Real-client Dash checks pass for initial +100% (retry at 1.4 s), -50% (3 s refused, 5.2 s accepted), and an early
  0.3 s raw request with no echo, movement or mana charge.
  Pause held native remaining base time exactly through 1.2 s of wall time and refused the paused retry.
  Artifacts: `logs/cooldown-after-dash-{plus100,minus50,early,pause}.json` in the validation checkout.
  The initial rate-change check exposed the native positive-rate cache above: the server changed rate at 0.5 s,
  while the client retained rate 1 (`logs/cooldown-after-dash-rate-change.json`).
  A manual wire experiment replaced Frostblink's active entry with remaining 2.5/total 3/rate 2, retaining exactly one
  entry and native base 3000; this verified the reader, not the production refresh path
  (`logs/cooldown-manual-actor40-progress.json` in the validation checkout).
  The production Dash rate-change check now passes: an actual Q use retained native base 2500/total 2.5 seconds,
  and +100% recovery at 0.5 seconds changed the one existing entry from rate 1 to rate 2 without resetting progress.
  A raw retry at 1.3 seconds was refused without an echo or mana cost; at 1.7 seconds the native entry was empty and
  the next use succeeded. Exactly two accepted uses produced two action echoes and two mana costs.
  Artifact: `logs/cooldown-after-dash-rate-change-fixed.json` in the validation checkout.
  Layout, active/expired/zero/pause, rate-roundtrip and queued encrypted-send checks also pass.
  Frostblink's C→S 0x190 location-instant handling belongs to a separate contribution; it is not implemented here.
  [PoB](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/dev/src/Modules/CalcOffence.lua)
  also divides base duration by recovery, then rounds ordinary one-use durations to its server tick.
  This server keeps its existing continuous game clock and does not claim that tick boundary behavior.
- **Mana.** The server keeps the player's mana (`PlayerMana`): a cast pays the skill's `mana_cost` first (refused
  without enough), and `RunPlayerTick` regenerates 1.75% of maximum mana per second (Character.ot
  `mana_regeneration_rate_per_minute_%` 105) plus `base_mana_regeneration_rate_per_minute`, raised by
  `mana_regeneration_rate_+%`. Changes go out as 0x1b1 with pool 1. The same two formulas (`PanelStats.LifePerMinute`
  and `ManaPerMinute`) are sent per minute as `total_life_recovery_per_minute_from_regeneration` and
  `total_mana_recovery_per_minute_from_regeneration`, the Defence tab's Life / Mana Regeneration per Second rows,
  which the client divides by 60 — so the panel shows the regeneration the tick applies. An **attack** also waits for
  the shared attack timer (`AttackTiming`): the client re-aims a held attack every 50 ms, so before 2026-10-25 every
  use was a swing (Holy Strike: twelve a second, 6 mana each). Attack gem skills and the default attack share
  `ClientState.NextAttackAt`, a use may arrive a quarter of an attack early, and a use that arrives sooner is ignored:
  no cost, no swing, no 0x1b1. The client does not deduct the cost itself (a live trace of its Life pool showed the
  mana change at the server's 0x1b1), so no "cost back" update is needed.
- **Ground attack in dual wield crashes the client: the off hand's weapon stats were missing (issue #167, fixed
  2026-10-25).** A dual-wielded attack swings the off hand too, and the client sets that strike's weapon up from the
  hand's **own** final stats; it derives none (§4). The Gateway sent only `main_hand_*`, so the second weapon had no
  attack time, damage or reach on the client at all. A Holy Strike use on *empty ground* — an action the client starts by
  itself (`0x141e8cf10` called from `0x141e8d8c7`, its own click, not from the 0x1ab handler) and repeats one attack time
  later on its own — then animated the off-hand weapon (`holy_strike_animates_owners_main_hand_weapon`) with a null
  animation set: the weapon's `AnimationController` holds an animation set at +0x180 (Frida: rig animation id 493/494 in
  dual wield) and drops it when the animation ends, and the next action's first update (`0x141c98700`) found +0x180 = 0
  and faulted at `mov r10, [r11+0xc8]` (0x141c988c1, access violation at 0xc8). Frida 2026-10-25 while the client builds
  the action read `off_hand_maximum_attack_distance` = 0 where the main hand's was 11. The Gateway now sends both hands'
  weapon stats from `WeaponStats()` (`WeaponFor` + `OffhandWeaponFor`, `SkillCatalog.Unarmed.OffStats`):
  `off_hand_minimum/maximum_physical_damage` (0x119/0x11a), `off_hand_minimum/maximum_total_damage` (0x2a0/0x2a1),
  `off_hand_base_weapon_attack_duration_ms` (0x143), `off_hand_total_base_weapon_attack_duration_ms` (0x221b),
  `off_hand_attack_speed_+%` (0x127) and `off_hand_base_maximum/maximum_attack_distance` (0x147/0x4d4). Live: the set is
  installed on every action (493/494 alternating) and holding the button on empty ground no longer crashes. What was
  *not* the cause: the echo's skill id, sending no 0x1ab for ground uses, dropping
  `holy_strike_animates_owners_main_hand_weapon`, the animated-weapons limit (the client derives
  `holy_strike_maximum_number_of_animated_weapons` itself from `base_holy_strike_maximum_number_of_animated_weapons` and
  `double_holy_strike_animated_weapons_limit`: 0x1400c37d1 registers 0x14279ee30), and the Templar rig's missing
  mace/sceptre stances (`Art/Models/CHARACTERS/Templar/rig.amd`).
- **Damage** (`SpellHit.cs`, `SpellDamage.cs`). Projectile hits, area hits and the selected spell's panel
  share `SupportedHit`'s final typed packet: catalog base damage, effective added damage, applicable increases,
  aura/support multipliers, conversion and gain-as-extra, then the actual caster's spell-damage restriction.
  Selected spell and inherited main-hand ranges now use this same supported packet, including explicit zeros.
  Its existing player/skill stat merge and support conversion order are unchanged.
  `SpellHit.Resolve` retains its standalone separate-input contract for callers of that seam.
  See [20](20-damage.md) section 2f for key 5341, zero-hit callbacks, RF and native verification boundaries.
  Damage ranges are floored only for display/rolls; the packet keeps fractions through conversion.
  The displayed per-use average and DPS retain the unrounded packet average, before critical strikes and target
  mitigation. DPS uses the skill's emitted casts-per-second value, not the weapon's attacks per second.
  This is the current server convention, not an official DPS calculation: crit weighting, target defences,
  per-type integer-roll expectation, cooldown/downtime and special skill mechanics are excluded.
  Hitless spells do not borrow weapon hits or turn flat added spell damage into a hit; their hit ranges and
  inherited weapon DPS/per-use fields are explicitly zeroed. This does not implement a beam or DoT damage panel.
- **Damage effectiveness** (2026-10-24). An attack gem's hit is the weapon's damage × its `DamageEffectiveness` (the
  GESSPL row, permyriad: Flicker Strike 7730 at level 1, 30890 at 20, 69600 at 40 — the gem's "Deals 77.3% of base
  Attack Damage"), through `SkillCatalog.DamageEffectiveness` and `SpellDamage.WithEffectiveness`. A row of 0 (the
  default attack's) or a negative one (a few skills have them) scales nothing. A **spell**'s own base damage is already
  final: its effectiveness is the added-damage one (Fireball 200%), and the GESSPL `BaseMultiplier` is 0 for every spell,
  so that multiplier does not scale a spell's own base hit; its added damage is scaled separately.
- **Per-level stats** add to a ConstantStats value of the same stat instead of replacing it (Righteous Fire's radius:
  18 + 0 at level 1).

## 9a. Gem skills without a projectile (first version, 2026-10-23)

Only projectile skills (Fireball) and Righteous Fire dealt damage: every other gem skill logged "no projectile". Now
`AreaSkillHit` gives them one hit, at the attack's contact keyframe (attacks, as the default attack but without the
walk-in offset) or after the cast (spells), on every living monster
within the skill's `active_skill_base_area_of_effect_radius` (in cells, else 12, grown by area of effect as Righteous
Fire's: `SkillArea`; halved before 2026-10-23, which the units do not support) of the aimed
point, or of the player when a melee skill is aimed too far. Attacks use the default attack's hit (`WeaponFor`: the
weapon in the hand by its WeaponTypes row with its local physical damage, added physical damage and attack speed mods,
else the class's unarmed attack; the player's stats on top), spells `SkillCatalog.HitDamage` with the player's stats; both convert and crit.
The player object of every area (`SendPlayer`) carries the same weapon attack, so the character panel's DPS includes the
weapon's local mods from the start (until 2026-10-23 it started with the unarmed attack, and only an equipment change,
`SendSkills`, brought the weapon's numbers).
`base_deal_no_damage` skills (Portal) deal none. An attack's hit carries the skill's damage effectiveness (§9). Not
modelled: the skills' shapes (waves, cones, chains, leaps), stun, and the accuracy roll. Being tested live.

**Movement skills** (ActiveSkillType Movement: Leap Slam, Whirling Blades …): the server puts the player at the aimed
cell when the skill starts (`SkillCatalog.IsMovement`); it kept the starting cell before, and a later resync pulled the
player back there (live 2026-10-23).

**Channelled skills** (Cyclone): after the skill's 0x1ab start the client sends **C→S 0x197** (10 bytes: u32 BE x,
u32 BE y, the player's cell) every few frames while the button is held, and **C→S 0x199** (2 bytes) when it is let go
(read from live captures 2026-10-23). The Gateway only moves the player there: 0x197 does not come at the attack rate.
The hits run on the server's own timer (`RunChannel`, started with the channel): the first sweep lands as the channel
starts, then one sweep around the player's current cell every `SupportedAttackSeconds` (the skill's own speed, its
supports and the player's attack speed, as the tooltip shows), paying the cost each time, until 0x199, any other action,
running out of mana, death or travel, or when the 0x197 updates stop for a second (the client sends them all the
time while the button is held, so a lost 0x199 cannot keep the channel going: `LastChannelUpdateAt`;
`ClientState.ChannelSkill`, `NextChannelHitAt`). One sweep resolves its damage
and multiplier once and rolls them per monster; every monster gets its hit, life update and death packet before the
sweep's deaths run their loot, experience and quest work (`AreaSkillHit`). Before, a sweep recalculated the whole skill
per monster and handled each death inline, spreading one sweep over up to 1.4 s in a crowd.

## 10. Projectiles (experimental)

**Current implementation:** the [framework](../game/projectile-framework.md) owns gem/bow production routing and
the sole 30 Hz Core simulator. Without skill coverage, catalog-recognized projectiles use basic straight-flight
recipes; missing metadata is refused before costs. Coverage replaces recipes and presentation, never the engine.
The old callback flight, observed-motion estimator, fixed-gap spread and named return-policy helpers are removed.
The older native evidence below records packet discoveries and the replaced implementation: its 16 ms polling,
send-gate damage commits, return overshoot/debug flags and fixed-gap controls are not current runtime behavior.
Revalidate native gameplay against the current runtime; packet-layout evidence remains applicable.

The server creates and moves the projectile. The client's Projectile component (vtable `0x1435a0d10`, 0x20 bytes) does
nothing by itself: its slot 17 reads nothing and almost every other slot is a bare `ret`.

- **Object.** A 0x2b9 of the Projectiles.dat type named after the effect (`Metadata/Projectiles/Fireball`, hash of the
  path; the type register knows it from the table). `Metadata/Projectiles/Projectile` deserializes Positioned,
  Animated, BaseEvents and Projectile, so the blob is Positioned, one Animated byte and the tasks byte
  (`ProjectileObject`; the Animated component is index 1), then 0x1a9 (wake zone). The real link between a skill and
  its projectile is unknown; the Gateway matches the Id. Multiple projectiles are one object each, spread around the
  aim (`ProjectileSpread`: `number_of_additional_projectiles`, `projectile_spread_radius` or 10°,
  `Game_PROJECTILE_SPREAD`): the client fires none of its own. Kinetic Blast's effect is ClusterBurst, which names no
  Projectiles row, so it falls back to the wand attack's `Metadata/Projectiles/WandProjectile` (3260) and plays
  `Metadata/Effects/Spells/cluster_burst/big.ao` where it lands; a landing on bare ground runs one area hit of the
  skill's radius (its secondary explosions are not modelled).
- **Pierce.** Pierce Support's `projectile_base_number_of_targets_to_pierce` (2 at level 1, 4 at level 20) and the other
  pierce stats give a hit budget of 1 + pierce: the projectile passes through that many monsters, each hit landing as
  the glide reaches its cell; the support's more Projectile Damage is part of `SupportedHit`. `always_pierce` is
  unlimited. The return leg uses the same budget; Returning Projectiles Support's `returning_projectiles_always_pierce`
  hits all targets. Contacts are observed during the flight, not selected along the launch line: a target that
  leaves the line before contact is missed, one that walks into it is hit, and the budget is spent only at real
  contacts. The return leg is swept the same way as the projectile flies back.
- **Returning projectiles.** A projectile whose stats allow it (`projectiles_return`, `attack_projectiles_return`,
  `projectile_return_%_chance`; `skill_projectiles_cannot_return` forbids; `Game_PROJECTILE_RETURN` forces) returns
  from a target/terrain collision or from maximum distance (Freezing Pulse only from a collision), flies back to the
  caster and 15 m (150 cells) past them, and is then removed (`Game_PROJECTILE_RETURN_DISTANCE` in cells,
  `returned_projectile_speed_+%`). Only one return per projectile; Spark and the other self-moving/rain/tornado
  secondary projectiles cannot return; Fireball explodes after the return. The return leg damages every monster along
  it except the one it returned from, with Returning Projectiles Support's less Damage
  (`support_return_returning_projectiles_damage_+%_final`), and `returning_projectiles_always_pierce` lets it hit all
  of them (otherwise the first only). A simultaneous volley is shotgun-limited to one hit per target, out and back.
  The return's hits land as the returning projectile reaches each cell, not when the turn happens; ailments and
  pierce/fork/chain priority are not modelled.
- **Flight.** S→C **0x1ba** to the target cell at the row's `ProjectileSpeed` (world units per second, 1040 → ~96
  cells per second at 250/23 world units per cell). The u32 at +0x28 is a **height**, not padding: the handler
  (`0x141e30c90`) passes it to `0x141e34400`, which keeps the terrain height for 0x7fffffff and otherwise sets
  z = -marker * 7.8125, reading the packet height as the signed Positioned marker, not signed world-z steps.
  With 0 the fireball sat under the ground and did not seem to move; with 0x7fffffff it flies (slowed to 10 cells per
  second with `Game_PROJECTILE_SPEED` to watch it). Positioned's vtable +0xd8, which the move calls when the flag
  byte has bit 0, is a `ret`; the interpolation is `0x141e4d180`.
- **Rotation.** Positioned rotation (+0x29c) = **atan2(dx, −dy)** in grid cells (the player's own moves traced with
  `trace-move.js`: facing (+15, −18) read 0.668).
- **Ordinary speed modifiers (2026-10-25; automated and live Fireball checks).** `ProjectileSpeed` scales the
  table's world-units/second by `1 + (player final projectile_speed_+% + skill base_projectile_speed_+%)/100`,
  then converts to cells/second with 250/23 world units per cell. `Stats.dat` marks `base_projectile_speed_+%`
  (client key 178) as a scalable non-virtual input and `projectile_speed_+%` (1407) as virtual. The player's
  `CharacterPlayerStats`/`PanelStats` total already includes its base input, so that input is not added again.
  Skill inputs are read separately from `SkillCatalog.EffectStats` at release, including level/quality, rather
  than overlaid on the player's raw values. For example, Freezing Pulse's quality row grants 2000 permille per
  quality: +40% at quality 20 adds to a player's +30%, giving 1.7 times table speed.
  The additive formula follows PoB's [ProjectileSpeed calculation](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/16de4b82d57f1c0de6eb40f37143c32d4da36a02/src/Modules/CalcOffence.lua#L1152)
  and [modifier formula](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/16de4b82d57f1c0de6eb40f37143c32d4da36a02/src/Modules/CalcTools.lua#L16),
  restricted here to additive increases/reductions. One resolved speed drives collision prediction, 0x1ba's
  duration and the server's flight delay. A finite positive `Game_PROJECTILE_SPEED` still overrides the result
  in cells/second. Invalid overrides fall back to the modified table speed. Finite positive overrides, including
  0.001 cells/second, are preserved when their flight duration is representable. Only a nonpositive speed or a
  flight exceeding [.NET's maximum Task.Delay duration](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.delay?view=net-10.0)
  (4,294,967,294 ms) gets a distance-dependent lower speed bound, before collision prediction. The bound includes
  endpoint rounding and is a numeric guard, not a measured official minimum speed.
  More/less modifiers, conditional and per-charge speed, support-gem application and special projectile speed
  rules remain unimplemented.
  Live validation used a copied OfflineWitch database and three Fireball casts over the same 116-cell path.
  At 0/+100/-50% raw player speed, the logs reported 95.7/191.4/47.8 cells per second; the actual 0x1ba payloads
  carried durations 1.212375/0.606187/2.424749 seconds. Before the patch, both 0 and +100% used 95.7 cells per second.
  These checks cover travel timing, not moving-target collision or pixel-level matching with the official game.
- **Hit (reworked 2026-10-25).** `ProjectileFlight` advances from the
  connection's elapsed game clock, polled about every 16 ms. For each interval it solves the earliest intersection
  of the projectile segment and each living monster's observed movement segment in relative coordinates, with the
  existing 4-cell radius (`Game_PROJECTILE_HIT_RADIUS`; the row's CollisionRadius is 2). It keeps previous samples
  across movement orders, rather than choosing a future target at launch. A monster can walk out of a formerly
  predicted hit, walk into the path later, or intercept before a farther monster. Removed/dead monsters are omitted;
  new objects and replaced walk estimators are tested only at the current endpoint, without inventing past motion.
  The first contact stops the flight; range expiry without a contact removes it without an explosion.
  Live at 10 cells/s on The Twilight Strand: a monster ordered from (330, 220) to (330, 240) after release was
  missed, and the projectile reached the wall at (369, 220).
  Ordering the monster from (330, 240) into the ray after release produced a hit at (326, 220).
  These checks used actual monster movement orders; nearer interceptors and dead targets also have automated coverage.
- **Stopping at contact (static evidence and live verification, 2026-10-25).** The initial 0x1ba goes to the
  range/static-wall endpoint. At a discovered contact, S→C **0x1b7** relocates it to the rounded contact cell with the appropriate height,
  then the Animated swap below plays its impact. The 3.29.3b Positioned handler's switch (`0x141e30c90`) maps 0x1b7
  to `0x141e30da7`: it stores the height at +0x290 and trailing u32 at +0x2b4, then calls `0x141e31200`. That routine's
  tail (`0x141e3162f`) calls `0x141e35250`, which clears the movement durations at +0x240/+0x280 and movement flags.
  Thus the explosion does not continue along the old glide. `InstanceProtocol.Teleport` now accepts an optional
  fixed-height marker and float world z; existing calls retain their two zero fields.
  Unlike 0x1ba, 0x1b7 copies the fixed pair directly. For a Positioned marker `m`, send
  marker `m` and float world z `-m * 7.8125`, matching the creation pair and the glide endpoint.
  Terrain is not resampled at impact; consistent native interpolation still needs the fresh integration check below.
  The height getter (`0x141e35800`) compares +0x290 with 0x7fffffff first; that sentinel selects terrain sampling
  and ignores +0x2b4. Current flights use a fixed height, including the bare offset without a height grid.
  Every other marker immediately returns the float at +0x2b4; sending a non-sentinel marker with a zero float
  would snap the effect to world z zero.
  Live contact probes read zero native movement duration at both the monster and wall impacts.
  Separate raw 0x1b7 probes preserved marker/float pairs (-8, -62.5), (8, 62.5) and the terrain sentinel,
  with native movement duration zero in each case.
  These raw probes establish copy/stop semantics, independently of the marker/world-z relation in the
  creation blob. The moving-target and raw-stop live checks preceded the bow packet/height update; the merged
  bow/collision path needs a fresh live integration check. Broader surface-height accuracy remains open (#182).
  Impact damage is committed under the send gate and a short monster lock, rechecking connection/area/pause and
  target life. Packet writes and hit notifications await outside the monster lock. A transfer invalidates flights
  even after its pending ticket is consumed and removes outstanding projectile/impact objects before 0x2b2;
  a failed transfer permits new flights without reviving old ones or leaving their visuals behind.
  Cast release, flight and the explosion's 1.5-second lifetime use game time and wait through pause.
  Terminal model swaps and removals recheck pause inside the send gate and retry after resume;
  a transfer cancels the retry and takes ownership of visual cleanup.
  Live pause held the projectile at (289, 220) for 1.2 seconds, with no removal until the game resumed.
  Local validation captures: `logs/projectile-collision-live.json` and `logs/projectile-fixed-height-live.json`.
  These remain estimates: monster positions are integer `WalkEstimator` samples, movement between samples is a
  straight chord, the radius is shared across monsters, and the client has its own cell stepping/network delay.
  Static wall checks use a captured two-dimensional grid, not object/doodad colliders or three-dimensional geometry.
  Sharp turns between samples or long scheduling stalls can still differ from the client. This is not an official
  collision/tick-rate implementation; explosion area damage, pierce/chains and object-owned colliders are unchanged.
- **Walls (live 2026-10-18).** The client's walkability provider (walk-map hook `0x1410ddcfd`) returns a second grid
  of the same size at +0x20/+0x28: what stops projectiles. 0 blocks, 1..5 (a distance gradient like the walk map's) lets
  them through. On The Coast seed 1 every walkable cell is open in it, cliffs and rocks are 0 in both, and 1.44 M cells
  the walk map blocks (the sea) are open. `scripts/walkmaps` saves it as `<area>_seed<seed>.projectile.bin`
  (`ProjectileGrid`). Static routes use a supercover grid trace with exact rational boundary comparisons,
  including non-45-degree diagonal corner cells, clipped to
  an open cell before the first blocker; the moving-target sweep uses that same endpoint. Earlier live checks: the fireball
  explodes on cliffs and flies over the sea. Doodads and objects with their own colliders (not in the grid) do not stop
  it yet. Grids exist for Lioneye's Watch and The Coast seed 1.
- **Explosion (live 2026-10-18).** Projectiles.dat names the impact as an animation of the projectile's own .ao
  (Fireball: ImpactAnimationIds `fireball_explode` → `point_impact.pet`). No server message found yet plays an
  animation by name, so on a hit the server swaps the projectile's animated object instead: S→C 0x2ba to component 1
  (Animated, in the order Positioned, Animated, BaseEvents) with flags bit0 and the path
  `Metadata/Effects/Spells/fireball/explode_new2.ao`, whose default animation `animate` plays the `splode` particles
  and the End sound (reader `0x141e57490`: bit0 path, bit1 a 0x2c-table index, bit2 `0x141e5adb0` attachments; no
  bit3 there). Only Fireball gets this explosion; a bow arrow's impact is `none` (it sticks/disappears and its
  burning is the monster's debuff). The object is removed 1.5 s later. `Game_PROJECTILE_IMPACT_AO` overrides the path
  (`none` turns it off). `Metadata/Effects/Effect` (Positioned, BaseEvents, Animated, LimitedLifespan) would be the
  cleaner carrier but is not known to be in the client's type register; an unknown type throws in `0x142187480`.
- **Tilt.** `Metadata/Projectiles/Projectile.ot` gives the client's Render `tilt_to_target` and
  `maintain_tilt_after_movement` (type data +0x76, +0x77; parser `0x1415ef5e0`). Render (`0x1415f53c0`) pitches the
  model by −`0x141e35c00`(Positioned, maintain): atan(dz / distance) of the current move, or, when not moving and
  maintained, the angle at Positioned +0x2a0 (f1 0x10 in the blob; the client set it to π/2 on our fireball). Traced live:
  the tilt is 0 during the flight, and the yaw (1.497 to the east, 4.617 to the west) follows the flight on screen.
- **Height.** Heights are absolute, in steps of 7.8125 world units. The client's terrain height of a cell is
  `0x141d84b00`(terrain, int cell[2]): terrain +0x18 / +0x20 = size in tiles of 23 cells, +0x28 = 0x38-byte tile
  entries (tile data pointer, +0x36 rotation code, +0x30 i16 height offset), result = the tile's height at the
  tile-local cell (`0x141d7d100`) + terrain +0x104 × the tile's offset; `0x141e35800` interpolates four cells. With a
  height grid (`TerrainHeights`, data/walkability/<area>_seed<seed>.heights.bin, int16 per cell, −32768 = unknown) the
  Gateway uses terrain + `Game_PROJECTILE_HEIGHT` steps (default 5, the torso) as the Positioned marker `m`.
  Positioned f0 0x04 carries i32 `m` and f32 world z `-m * 7.8125`; 0x1ba carries the same marker `m`.
  Without a grid it uses the bare offset.
  The grid covers natural terrain, not doodads/platforms; the protocol carries no player z, so platforms and doodads
  need a server-side world/surface height model (issue #182).
- **Capturing heights from the client** (2026-10-18): direct dumps from Frida's thread crashed or hung the client
  (the terrain was still being built; the lookup runs with several terrain pointers). The read-only capture below
  (`capture-terrain-tiles.js` + `heights.py`, no calls into the client) works; an offline `.tdt` calculation is still
  possible from the same tile files.
- **Heights in the .tdt (decoded offline, 2026-10-18).** The binary parser is `0x1429f31a0`. After the header (§6 of
  08-area-transitions.md) come a u16 flags word, a hull-plane count byte (0 here), then a u16 subtile count (w × h)
  and one record per subtile, row by row: u16 flags, u8, [3 bytes if the high flag byte has bit 1, version ≥ 13],
  then, unless flags bit 2 is set, a **byte map** (`0x1429f59a0`): header byte b, kind = b & 7, n = (b >> 3) & 3 extra
  bytes, size = (b >> 5) | extra << (3 + 8k). Kind 0 = a constant (size is the value; 0 = all zero); kinds 1..4 = an
  LZ4 block of `size` bytes that expands to the 69 / 137 / 281 / 529-byte palette form `0x141d8fc40` reads (2 / 4 / 16
  int8 palette entries + 1 / 2 / 4-bit indices, LSB first, or 529 raw int8). Unless flags bits 0 and 1 are both set,
  a u16 length and that many bytes follow (a second per-cell map). Check: `beachencampment_v01_01.tdt` (15 × 18
  subtiles) placed at cell (69, 0) with the transposing code reproduces the Lioneye's Watch capture exactly (142 830
  cells, 0 differences, height offset 0).
- **Placed tiles of generated areas (live 2026-10-18).** `0x142041200` loops over the finished terrain grid
  (arg0 +0x28: the 0x38-byte entries `0x141d84b00` reads; entry +0 = subtile record whose heights vector is at +0/+8,
  +0x30 i16 height offset, +0x36 code; terrain +0x104 = multiplier, 1 in both areas). The read-only Frida hook
  `scripts/re/frida/capture-terrain-tiles.js` dumps them on entry (no calls into the client; the client stayed up);
  `scripts/walkmaps/heights.py` applies the client's lookup (code tables `0x142fa04b0`, `0x142fb7f98`). The town
  capture reproduces the known grid exactly; The Coast seed 1 is 1725 × 1725, −37..90.
- **Sign.** World z points down, but 0x1ba's signed height marker points up: `marker = -z / 7.8125`.
  `ProjectileObject.Height` takes world-z steps and writes the opposite marker plus float world z.
  The move endpoint takes that encoded marker, not the API argument: marker +5 means world z -39.0625.
  Static 3.29.3b evidence: `0x141e34921` passes the current marker and `0x141e3491a` the packet endpoint marker
  into the 3D stepper `0x141e4d180`; `0x141e3495b..0x141e3496b` converts the packet marker to float, multiplies
  by 7.8125 (`0x1436a36b8`) and flips its sign (`0x1436b3520`) before render interpolation `0x141e4d510`.
  Using the opposite marker made a creation at -39.0625 glide toward +39.0625 before the stop reset its height.
  A raw same-path A/B probe showed marker -5 drifting toward positive z, while overriding the endpoint to +5
  returned it toward -39.0625.
  Corrected normal launches then retained marker 5/world z -39.0625 at creation, after the glide started,
  during a paused mid-flight sample and at moving-monster and wall contact stops.
  The same run verified a moving-away miss, a later-entry hit, pause/resume and zero native glide step after contact.
- **Flight (live 2026-10-18).** S→C 0x1ba's float is the move's **duration in seconds**, not a speed: the handler
  passes distance / it as the speed, and the step walker sets Positioned +0x23c = that speed and +0x240 = the time per
  cell step (duration / the path's longest axis), advancing +0x244 by the frame time (`0x141e32710`). Sending the
  speed (95.7) made each step take ~1 s, so the fireball crawled (~10 world units/s, sampled with Frida); sending
  distance / speed it flies at ~1000 units/s. The fireball now starts `Game_PROJECTILE_AHEAD` cells (1.5) in front of
  the caster and flies on to `Game_PROJECTILE_RANGE` cells (120, clamped to the area), stopped by the first
  monster or wall contact described above. `/mob_move` and `Game_MOVE_MODE=move` still pass a speed in that field.
- **Ordinary bow attacks (#203).** Ground and object uses share `LaunchOrdinaryBow`:
  the action is confirmed, then the game clock waits for `attack1_bow` contact without the melee walk-in offset.
  The original action, life and area epoch must still be valid at creation. An already-paused use is rejected before
  changing action, walk or attack pacing; an accepted echo/creation queued behind a later pause waits for resume.
  Ground confirmation runs outside serial input dispatch, so resume and a cancelling move remain processable.
  After creation, later actions and player death do not cancel flight; pause freezes it and transfer/disconnect
  invalidate it. The clicked monster is an aim: another monster can intercept, and walls or a moved-away target
  can prevent damage. The object-use path returns before the old melee contact hit, so it cannot double-hit.
  Current player position, weapon/raw damage, added damage, player-first default-skill conversion, accuracy,
  attack crit chance/multiplier, impale chance and projectile speed are sampled after the contact wait, before
  enqueueing creation. Aim remains the original aim. This is the selected release snapshot policy; official-server
  snapshot equivalence has not been established.
  At the actual send gate, one game-time sample drives the target observation and swept contact test. Target
  evasion is checked there; on a hit, existing target impales are consumed, a new stack may be added, the packet
  is rolled and crit applied, and life is committed with the arrow's stop packet. Paused or obsolete queued
  samples consume no accuracy randomness or target state. A killing hit clears impales. Damage, accuracy,
  crit and impale formulas retain the previous ordinary attack's approximations (including melee physical
  increases on bows); this adds no new armour/resistance, entropy or support mechanics.
  An evaded object is ignored for the rest of this straight flight, and a farther contact in the same sweep may
  hit. This is a bounded policy supported by the current community
  [Projectile](https://www.poewiki.net/wiki/Projectile) and [Avoidance](https://www.poewiki.net/wiki/Avoidance) pages;
  older mirrored descriptions disagree, and no primary GGG statement or official-server trace was found.
  Official evasion-continuation parity remains unverified. Null contact predicates preserve existing gem behavior.
  Ordinary arrows send ranged life/flinch/death notifications without the melee `0x1af 0x4201` sound or Fireball
  explosion. Before-controls on 2026-10-25 showed object arrows created at bow-clip elapsed ~0.012 s and melee
  damage before flight impact, while a ground arrow crossed the same monster with no damage. On the combined
  runtime, actual right-click ground/object after-checks each caused one hit only at the native arrow stop:
  ground aim (363,224) hit at (296,221) for 13; object aim (300,220) hit at (296,220) for 6, without melee
  `0x1af 0x4201`. Creation was in `attack1_bow`, clip elapsed ~0.466/~0.448 rather than ~0.013;
  the expected clip contact
  is ~0.399, so frame/server scheduling delay remains visible and exact native timing equivalence is not claimed.
  A wall before the selected object stopped the arrow at (318,276) without damage; previously the selected
  monster still took damage. A nearer interceptor took one 7-damage hit while the selected target was untouched.
  A target ordered off-path after release was missed and the arrow continued to the wall. A framed Move before
  release cancelled creation/damage; after release it left the old arrow alive to deal one 13-damage hit.
  During flight pause, 35 native samples stayed at (286,220); that shot later missed and continued to the wall.
  A release paused before contact created nothing while paused, then created ~1.03 s after resume and hit once
  for 11. Paused raw ground/object requests were ignored and resume remained processable.
  A shot with 100 added cold captured at release still dealt 112 after those raw stats were cleared in flight:
  one killing hit and one 4-XP award. Loot was not measured. Live accuracy was not forced to 100%; the existing
  `/set_stat accuracy_rating` path does not refresh `conn.Pools.Accuracy` used by ordinary combat.
  Combined-only Frostblink checks: actual right-click then actual Q after release preserved one 13-damage hit.
  Actual right-click then a timed framed `0x190` before release moved the native player to (312,221); the arrow
  was created at (313,221) with the original aim (363,224), ending at the wall. This verifies local composition,
  not standalone Frostblink support or official-server mechanics. Combined-only hit-recovery controls showed
  native TakeHit (clip 163, 0.429 s) cancelling the pending bow without creating its arrow; input during recovery
  was denied and a fresh retry after expiry released. An arrow created before recovery still stopped/removed
  under its original id despite action cancellation. These recovery/Frostblink features are separate contributions.
  Evidence: integration-checkout `logs/ordinary-bow-after-{ground,object,wall-object,interceptor-object,
  moving-away-object,prerelease-cancel,released-move-hit,flight-pause,release-pause,paused-dispatch,
  release-snapshot-kill,released-native-frostblink,pending-bow-framed-frostblink}.json`.
  Recovery evidence: `logs/ordinary-bow-hit-recovery-{bow,flight}.json` in the same checkout.
  Automated sweep, release snapshot, actual send-gate lifecycle and paused-input dispatch checks also pass.
  Surface/platform height, wands, supports, pierce, fork and chain are outside this change.
- **Still wrong.** Without a surface-height model the projectile uses the captured terrain height; on a platform or
  doodad (which the terrain grid does not include) it spawns below the player (issue #182). The direction floats of
  Positioned's f1 0x04 block are not used; sending (dx, dy, 0, 0) put the fireball under the ground, so the Gateway no
  longer sends them.

## 10a. Weapon restrictions: why a skill is greyed out

The client decides itself whether a skill can be used (the skill bar's "[Skill can't be used]", ClientStrings 844
`SkillPopupSkillUnusable`): `FUN_141ee7fb0(skill, mainType, offType)` sets the skill's +0xb0 (usable) / +0xb1, and
`FUN_141ee8410` checks a hand's type against the ActiveSkills `WeaponRestriction_ItemClassesKeys` (setting +0xb2 main
hand / +0xb3 off hand fits; neither → unusable). The types live in the ActiveSkillManager at +0x14c / +0x150, set
through its vtable +8 (`FUN_142199200`) from `FUN_141e97190`, which reads them from the player's Inventories component
(`FUN_141e1f310(inventories, 0 / 1)`: the slot's record's first field). A type is the item's **ItemClasses row + 1**
(`FUN_1423b8b90`; `FUN_1424aac70` goes back): Two Hand Axe (row 16) = 17, no weapon = Unarmed 37, 0x68 = nothing.
That first field is the equipment record's first u8 (13-items.md "What the model wears"), which the Gateway sent as 1
for every item: every skill with a weapon restriction was greyed out (Ground Slam with a two-handed axe, live
2026-10-23, found with a Frida trace of `FUN_142199200`). The Gateway now sends ClassRow + 1 (`VisibleItem.Type`).

## 10b. Actor restriction: warcry skills disabled

`your_warcry_skills_are_disabled` is Stats row 5408, wire key 5409 in 3.29.3b.
Rampager, PassiveSkills graph node 49937, supplies value 1.
`SkillCatalog.IsWarcry` follows GrantedEffects.ActiveSkill and ActiveSkills.ActiveSkillTypes to the Warcry type.
The extracted ActiveSkillType row is 62; classification resolves its Id instead of hardcoding names or spell status.
Enduring Cry is a Warcry and is not a Spell in these tables.

`ActorSkillDisabled` reads the actual caster through `CharacterPlayerStats`, before any skill stat overlay.
Passive and active equipment sources use the existing raw stat assembly; a raw dev value replaces their total.
Any signed nonzero value disables new warcry uses.
Explicit zero or source removal restores admission on the same connection without rebuilding resource pools.
The restriction leaves the Actor skill list and ordinary attacks, spells and auras available.
The caster stat builder does not call `GemDisabled` or build `PlayerSkills`, avoiding recursive admission checks.

`GemDisabled` shares the restriction with ground 0x25 and the `CastGemSkill` backstop.
`TryBeginCooldownUse` also checks it before creating or spending cooldown state or invoking action side effects.
This covers monster/self object 0x24 and eligible instant paths before a new action echo, cost or effect.
Instant aliases retain their existing supported subset; this does not add instant warcry executors.

The existing `CastGemSkill` check remains the cast admission boundary.
A queued action echo can pass preflight and then fail that check before payment.
After cast admission, a source change behind a queued paid mana packet does not add a late cancellation or refund.
An accepted Enduring Cry still schedules its existing non-cancellable charge release in `UseChargeSkill`.
Later source or action changes do not cancel that release.
Enduring Cry remains partial: this change adds no healing engine or other warcry mechanics.

Automated coverage checks parsed 0x24/0x25 requests, payment, cooldown retry, source precedence and queued sends.
It also checks unsupported instant aliases and admitted instant Righteous Fire with the restriction present.
Seven native checks used a fresh unascended Witch, the real level-one gems and five normal nearby enemies.
Ground and self/object requests with positive or negative flags emitted no new action or cry effect and spent no Mana.
Clearing the source admitted a 13-Mana Enduring Cry, granting one actual native Endurance Charge from five power.
Reapplying the flag after payment did not cancel that accepted release; an ordinary Fireball still dealt 200 damage.
A refused request after the original eight-second cooldown spent no use; clearing the source allowed a paid retry.
The retry granted a second charge; an explicit warcry speed input kept it clear of the first charge's expiry.
Relog cleared the transient developer flag.
These controls verify the shared restriction, not Rampager allocation or complete Enduring Cry behavior.
Future composition with outgoing Spell or incoming armour changes must retain this caster-only admission check.

## 10c. Actor restriction: own aura skills disabled (#304)

Static inspection of the pinned 3.29.3b client establishes these virtuals (wire keys, not JSON row indices):

- `your_auras_are_disabled` (11075) is `base_your_auras_are_disabled` (21148) OR
  `keystone_herald_of_doom` (11881), with either signed nonzero input counting as true.
  Registration `0x1400dd340` selects callback `0x14289a420`.
  Rampager node 49937 supplies 21148; its spell, warcry, scale and Rampage effects are independent.
- `skill_is_aura_skill` (15801), callback `0x1428cf720`, is `base_skill_is_aura_skill` (3004) OR
  (`curse_apply_as_aura` (2730) AND `skill_is_curse` (2532)).
  Registration is `0x1400e4f60`.
  `skill_disabled_by_stat` (15798), callback `0x1428cf770`, tests 15801 and then caster 11075 in its aura branch.
  Registration is `0x1400e4fc0`; other branches remain independent restrictions.

Classification follows GrantedEffects.ActiveSkill to ActiveSkills.ActiveSkillTypes and ActiveSkillType.Id.
Aura type row 38 has FlagStat row 3003, giving wire key 3004.
All 17 supported ordinary self auras are typed Aura, as are unsupported Pride, Flesh and Stone, Vaal Haste and banners.
Reservation alone is insufficient: Heralds, Arctic Armour, Tempest Shield, Petrified Blood and Blood and Sand
are not Aura.
RF is not Aura even though one of its buffs is named `righteous_fire_aura`.
Neither BuffDefinitions.IsSkillBuff nor a buff's name or duration identifies an aura.
Accepted linked Blasphemy adds Aura and supplies 2730 to an AppliesCurse skill (flag 2532).
The metadata and native skill flags are published without adding a curse-aura executor.
Admission, preflight, cast and acknowledgement checks carry the actual skill ID and its accepted supports.
Two copies of Enfeeble can classify differently: the linked copy is an aura; an unlinked copy is not.
Remembered restoration checks the candidate gem's own requirements rather than another copy of the same effect.

The Gateway resolves the restriction from current caster passive/equipment sources before any skill overlay.
Developer values replace each complete raw input; an explicit final 11075 replacement also wins, including zero.
Actor 11075 and skill 3004/15801 are published coherently; an own-aura restriction sets skill 15798 to 1.
Publication preserves an independently supplied 15798 reason and never clears all skill flags.
Ground, object/self, eligible instant-object and the shared pre-cost/cooldown gate reuse the warcry admission seams.
The locked activation boundary rechecks the restriction; the unsupported instant-ground aura alias stays unsupported.
The skills and bindings stay granted while their uses are prohibited.
Ordinary ground/self/object acknowledgement gates recheck the current own-aura restriction under the state lock
before committing native use or cooldown publication; a refused acknowledgement cannot continue into a cast.
The original action/connection ownership predicates and independent accepted Warcry/RF behavior remain in force.

**LocalExile lifecycle policy:** a temporary restriction suspends active self-owned auras
and retains ActiveAuras intent.
Both source and received buff instances end once, reservation and bonuses cease, and current mana gains no refill.
Lifting the restriction restores available remembered ordinary self auras once, using fresh instances and a mana clamp.
Restoration is not a manual activation and does not recheck transient cast affordability.
Death still clears saved intent, including while runtime effects are suspended.
The inspected native admission virtuals do not prove this official-server lifecycle or automatic restoration policy.

Pool/raw-stat reads detach restricted self auras without recursing through CharacterPlayerStats or aura restoration.
At the send gate, reconciliation precedes player, Actor, mana and initial object snapshots.
Rage and Fortification full snapshots share this batch; unchanged Rage still publishes pending aura changes.
Presentation diffs against the instances actually sent on this connection, so queued adds cannot resurrect old buffs.
Loading may restore runtime intent before a player object exists; dirty/presented state remains pending until creation.
Initial 0x2b9 contains reconciled stats/pools, then entry replays each pair once on that object.
Closed/leaving connections cannot restore or publish aura pairs.
RF, flasks, Fortification, other temporary effects and foreign received buff instances are outside this owned list.

Metadata, real-dispatch, source, decrypted loopback, creation-ordering and queued/lifecycle regressions are automated.
Fifteen native controls on source `01e458d` used a fresh unascended Witch and level-one Clarity, Haste and Fireball.
Actual Buffs vectors contained both owned instances for each active aura.
At 700 maximum Mana, Clarity reserved 34 and Clarity plus Haste reserved 384; their regeneration/movement bonuses rose.
The positive restriction removed both pairs, bonuses and reservation, retaining 316 current Mana without a refill.
Ground, self/object and instant-object retries started no action; ordinary Fireball still dealt 200 actual damage.
The installed Fortification buff and count remained present.
Clearing the source restored fresh pairs once and clamped current Mana; repeated snapshots kept the same instances.
Signed negative inputs, the Herald input and explicit final zero/negative replacements passed independent controls.
Relog cleared the transient developer source and replayed the saved Haste pair in the combat area.
Its Mana reservation was sampled after a zero Strength-mana source rebuild, after observing the entry pair.
Death while suspended cleared intent, and resurrection did not restore a phantom aura.
Initial attempts exposed observer gaps for null-GEPL basic actions and creation-only Mana, not gameplay failures.
Evidence is in local `logs/phrecia-completion/own-aura304-20261027-111846` with exact artifact versions and hashes.
Native grey-icon prediction, persistent restricting equipment and another client's party effects remain unverified.
11881 is consumed only by this restriction; complete Lone Messenger, Rampager, Phrecia admission, party/minion auras,
general reservations, banners, enemy auras and Vaal systems remain separate work.
A foreign-source packet non-removal control establishes no party aura executor or gameplay behavior.

## 11. The skill bar

- **C→S 0x4e** (5 bytes, class `0x1435bfc58`): `u8 slot, u16 skill id` (big-endian). Slot 2 is the right mouse button.
  The skill bar UI (`0x140aecab0`) sends it when the player puts a skill in a slot. The client also does it by itself
  when a skill is **added** to an existing player (0x2ba): it took the first free slot after socketing Fireball. Skills in
  the creation blob, or added before the HUD exists, stay unbound.
- **S→C 0x53** (instance group, class `0x1435dc4c0`, read `0x14219f600`): `13 × u16` skill ids by slot (raw bytes, so
  little-endian; basic actions by their own id, Move = 0x2909), then u16 +0x2a and u16 +0x2c (sent 0). The bar (vtable `0x14319d870`) rebuilds from such an array in
  vtable +0x390 `0x140ae0fb0`: it looks each id up in the ActiveSkillManager and fills the slot; an unknown id (0)
  clears it. Without 0x53 the bar is empty after every area change and login.
- **The 0x20 skill update can take a bound skill off the bar**: live 2026-10-21, Haste's slot emptied when its aura went
  on (the aura's stats resend every skill record), and the area change's 0x53 brought it back. A gem taken out and
  socketed again did not get its slot back either. `SendSkills` therefore sends the saved bar (0x53) after every
  update, unless it adds a skill whose GrantedEffects Id the saved bar does not have: the client binds such a new
  skill to a free slot itself (0x4e), and a bar sent then would clear it.
- **The Gateway** saves each 0x4e (`CharacterSaves`, `data/saves.db`) as slot → a GrantedEffects Id for gems and the default
  attack (skill ids change with the socketed gems), `Action/<ActionTypes Id>` for a basic action (Move 0x2909 =
  "Move only"), or "" for a cleared slot. It sends 0x53 right after the player and area messages, no delay needed,
  with the default attack on slot 0 only when nothing was saved there. Live 2026-10-18: Fireball on the right mouse
  button and "Move only" on the left one came back after login and area changes, with the bar there at once.
- **New skills go into empty slots only** (`SkillBarLayout`, `FillSkillBar`). A new character's bar gets the default
  attack on the left mouse button (slot 0) and its gem skill on the right one (slot 2). `SendSkills` runs it before it sends the saved bar
  again (#108), so a gem skill that is nowhere on the bar takes the first empty slot in this order: the right mouse button, Q W E R T (3–7), the
  middle button, then the rest. An empty slot is one never set, cleared, or holding a skill whose gem left. Slots the
  player set stay as they are, and a cleared left button stays cleared. Live 2026-10-20: a new Templar got the sword on
  the left button and Holy Strike on the right.

## 12. Buffs and Righteous Fire

- **S→C 0x1c1** (world group, class `0x1435ddb78`, read `0x141fdf870`, world handler case 0x1c1 → Buffs component
  `0x141e11720`): object header (u32 id, u32, u16), u16 buff instance (+0x20), u16 BuffDefinitions row, u32 +0x64 (the
  source object), u16 +0x60 (the source skill; for a flask buff its flask-bar cell, under which the client draws the
  effect's timer bar, [13](13-items.md) §8a), u8 flags (bit0: u8 u32 u8 u16; bit1: +0x68, an f32 duration in seconds,
  default +infinity; bit2: u16 stacks,
  default 1; bit3: u16 u16; bit4: u16 BE BuffVisuals row override, default the row's, read after bit3's fields (a row past the table gives none); bit5: u8 +0x80, default 2; bit6 → +0x81;
  bit7: f32 +0x6c, default 1.0), a u16 when the row's bool at +0xff is set (`__col44` in the vendored schema), one u32
  per GrantedStats entry when the row's `IsRecovery` (+0x7c) is set, then a u32-counted list of u32. A row with either
  flag needs those fields, or the client misreads the message and the ones after it (the flask buffs, rows 5 and 9:
  [13](13-items.md) §8). The client reads the BuffDefinitions row in memory as the
  .dat row (BuffVisualsKey at +0x55). **S→C 0x1c2** (class `0x1435dd918`): object header, u16 buff instance: removes it.
  The Buffs component has no 0x2ba update reader; its creation blob holds the same entries ([04](04-player-entity.md)).
- **Righteous Fire** (`ActivateRighteousFire`, `RunPlayerTick`, `BurnAround`): cast on the player itself (§9), it puts
  two buffs on the player: `righteous_fire` (row 12, BuffVisuals 15: `ri_fire/bottom.epk`, the ring at the feet) and
  `righteous_fire_aura` (row 13, BuffVisuals 19: the ignite flames, and for a player model EPKFiles2
  `ri_fire/player.epk`, the burning hands). Every 250 ms the player loses 90% of maximum life per second
  (`base_nonlethal_fire_damage_%_of_maximum_life_taken_per_minute` 5400; nonlethal, it stops at 1) and every hostile
  monster within its radius plus its size loses 70% of the player's maximum life per second
  (`base_righteous_fire_%_of_max_life_to_deal_to_nearby_per_minute` 4200) and carries `righteous_fire_aura` while
  inside. It ends at death; casting it again does nothing. **The radius** is `active_skill_base_area_of_effect_radius`
  (18 at level 1; one unit is one cell) plus `active_skill_base_radius_+`, scaled as Path of Building's calcRadius:
  floor(base × floor(100 × √(area modifier)) / 100), where the modifier is (1 + `base_skill_area_of_effect_+%` +
  `skill_area_of_effect_+%` + `spell_area_of_effect_+%`) × each `…area_of_effect_+%_final`, then
  × (1 + `active_skill_area_of_effect_radius_+%_final`), from the player's stats under the skill's (`SkillArea`).
  Conditional area stats (per charge, per attribute, "recently") are not modelled.
- **A worn Righteous Fire effect** (its `skill_art_variation`, [23](23-microtransactions.md) §3) swaps both buffs'
  visuals through 0x1c1's BuffVisuals override and the ring's AO (`SkillArt`), also on the enemies' aura; NoRing
  draws no ring. A pick while the fire burns lights it again with the new art (casting again while it burns does
  nothing, so before 2026-10-23 a pick showed only after the fire ended).
- **The big burning circle** is not a buff visual: it is MiscAnimated `RighteousFireRing`
  (`Metadata/Effects/Spells/ri_fire/rifire.ao`), an effect object of its own. The client's effect script
  (`0x142087910`) makes MiscAnimated effects as `Metadata/Effects/Effect`, `ServerEffect`, `PermanentServerEffect` …
  objects; `PermanentServerEffect` is Positioned, BaseEvents, Animated (Render client-only), so the Gateway creates one
  (`EffectObject`: the projectile's Positioned, Animated bit0 + the .ao path) under the player and glides it with 0x1ba
  every 100 ms to where the player will be 100 ms later along its path (`FollowRighteousFire`, `RunRingFollow`;
  before 2026-10-23 it glided once per ground click to that walk's end, so a walk to a stash or an item, which comes as
  C→S 0x24, left it behind, and the straight glide cut the path's corners). It is created with Positioned flags0 0x80
  (`EffectObject.FixedRotation`): without it each glide turned the ring to face the move, so it spun as the player
  turned (live 2026-10-21). It is also scaled to the radius: Positioned flags0 0x20's uniform scale (+0x2b0, the
  x / y stretches left at 1, [04](04-player-entity.md) §4.0). The art is drawn for the radius in its MiscAnimated row's
  `__col4`, in world units (RighteousFireRing 400, RighteousFireRingWhite 200, the league avatars' blue ring 380, the
  Vaal rings 500; 0 for art not scaled to an area); the meshes agree (`rifire.sm` ±463 with its flames at 380,
  `rifire_white.sm` half that). Scale = radius × 250/23 / `__col4`: 0.49 for the default ring at 18 cells (1.8 m), 0.98
  for the white one. Verified against a screenshot of the real game at level 1 (2026-10-23): with the health bar as the
  ruler, the outer ring is about twice the inner flame ring (the `righteous_fire` buff's `bot_02.ao`). Before, all
  three scale floats carried the scale, so the ring was drawn at its square (0.24) and hid under the inner ring.
- **Units.** A radius stat is in cells (one cell = 250/23 world units), and the client's tooltip shows it as metres at
  10 per metre (Righteous Fire's 18: "1.8 metres"). A world unit is about a centimetre (the Marauder's mesh,
  `Warrior.sm`, is 190 tall), so 18 cells ≈ 196 world units ≈ 1.8–2 m. Live 2026-10-18: with only the
  buffs the player had just the small ring at its feet; with the effect object the big circle shows and follows the
  player, and monsters around burn to death.
- **`/god`** toggles 1000% increased attack and cast speed, 200% increased area of effect
  (`base_skill_area_of_effect_+%`), life and mana regeneration (below), +10 000
  maximum life and +1000 to each attribute (`GodStats`, `PoolsFor`); `/set_aoe <percent> | off` sets the character's
  increased area of effect in place of it (a burning Righteous Fire is lit again with the new radius); `RunPlayerTick` applies the regeneration. Life
  regenerates 100% of maximum life per second (`life_regeneration_rate_per_minute_%` 6000), not a flat 10 000: Righteous
  Fire burns 90% per second, which outran a flat 10 000 once passives raised life above 11 111.
- **Resistances** (`CharacterStats`, `DamageModel`): the passives' `base_resist_all_elements_%` and
  `base_fire/cold/lightning/chaos_damage_resistance_%` are summed from the character's 0% base into `Pools`; the client
  applies the two permanent Kitava quest penalties itself, and the Gateway mirrors them from the same flags
  (`a5q6-CompletedBoss`, `a10q3-SeenBoss`/`a10q3-UsedRewardBook`: -30% each, not an aura). The final value is
  `clamp(resistance, -200, min(75 + max-resistance modifiers, 90))`, taken from Path of Building commit `16de4b8`:
  `src/Modules/Data.lua` (`ResistFloor = -200`, `MaxResistCap = 90`), `src/Data/Misc.lua`
  (`base_maximum_all_resistances_% = 75`) and `src/Modules/CalcDefence.lua` (the clamp). The project is cloned locally
  into the git-ignored `Tools/PathOfBuilding` as the source of these formulas. Righteous Fire's self-burn is mitigated
  by fire resistance; `/resistances` prints the modifier, penalty and final values and `/hit <type> <damage>` applies
  the same rule. Armour reduction and player evasion as avoidance against monster attacks now exist (see §4);
  penetration and max-resistance modifiers are still not modelled, and monster evasion is used for the player's
  accuracy check (§5).
- **Overcapped-resistance modifiers** (`ResistanceOvercap`, `DamageModel.Overcap`): while one of the flag stats is
  set, the uncapped resistance above its maximum is a generic increased modifier — PoB `16de4b8`,
  CalcDefence.lua's `ResistOverCap = max(0, total - max)` with the maximum capped at 90, applied as
  `Evasion` (INC) for `EvasionRatingIncreasedByOvercappedColdRes` and, in CalcOffence.lua lines 745-748, as `CritChance`
  (INC) for `CritChanceIncreasedByOvercappedLightningRes`: +1% increased evasion per 1% of cold overcap and +1%
  increased critical chance per 1% of lightning overcap, additive with the other increases, for attacks and spells
  alike. The flags are ordinary stats (keys 5320/5321), so any source sets them: the Surfcaster's Sea Legs notable
  (PassiveSkills graph 61696, with its +40% `cold_and_lightning_damage_resistance_%`, which the passive/item aggregator
  adds to both elements' raw resistance), an item mod, or `/set_stat`. An explicit dev value replaces its own input —
  the per-type, pair and all-element resistance stats, and per-type or global maximum resistance — rather than
  adding to the aggregate, and `/set_stat` rebuilds the pools at once.
  The shared inputs are `base_resist_all_elements_%`, `additional_maximum_all_resistances_%` and
  `additional_maximum_all_elemental_resistances_%`.
  `PoolsFor` derives the increases once per pool build (with the
  Kitava penalty inside the raw resistance; a penalty change rebuilds the pools again) and stores them in `Pools`, so
  the evade rolls, the panel and the spell consumers read the same numbers; the uncapped values stay in `Pools`
  untouched.
  The resolved stack adds the derived critical increase once to the signed pool sum, before final half-up rounding.
  An armed 830 base with 45% overcap gives 1204; raw spell stats expose the same derived source once.
  Selected skills use the complete pool sum so a clipped native raw stat cannot erase negative cancellation.
  Per-key resistance replacements include the active Purity contribution already in the pool bonus.
  Equipment 80 plus Purity 40 stays 120 when the typed raw key is replaced with 120, rather than becoming 160.
  An explicit zero replaces the typed resistance or maximum completely, while other raw keys remain independent.
  Real aura/catalog/pool tests cover cold, lightning and all-element replacements; this fix needs native rechecking.
  New automated regressions cover those combinations with explicit zero flags and cannot-crit.
  These thirteen native checks belong to original head `39cbda2`; recheck the resolved combinations natively.
  A fresh Witch verified 120% cold/lightning resistance with each flag:
  evasion 15 → 22 (45% additional increased evasion), spell crit 500 → 725 (45% increased chance).
  Raising maxima to 90 reduced these to 19 and 650; a global +5 maximum gave 21 and 700.
  Adding then clearing 40% all-element resistance gave 28/925 then 22/725.
  Act 5 and Act 10 penalties reduced these to 17/575 then 15/500; clearing the penalties restored 22/725.
  Zero flags and relog removed the development bonuses.
  These validate the generic consumers; allocating Sea Legs belongs to the separate ascendancy lifecycle change.
- **Spell Suppression** (`CharacterStats`, `DamageModel`): the passives' `base_spell_suppression_chance_%` is summed into
  `Pools` (capped at 100%, PoB `SuppressionChanceCap`); a successful roll makes a spell hit take 40% less damage
  (`SuppressionEffect` in PoB `src/Modules/Data.lua`). The passives' unconditional
  `base_spell_damage_%_suppressed` is added to the 40% effect (e.g. Inveterate +3, Primal Instinct -10); the effect is
  not hard-capped, matching PoB. Conditional effect stats (`...while_on_full_energy_shield`,
  `...if_not_suppressed_recently`, per-hit/per-bark) are not modelled yet. Chance above 100 is overcap
  (`SpellSuppressionChanceOverCap`), but the effective chance is still 100. The passive
  `spell_suppression_chance_is_lucky` rolls twice and keeps the better roll (PoB `suppressRolls`, effective
  `1 - (1 - p)^2`); an unlucky roll (`p^2`) is modelled for the dev probe but has no passive stat yet.
  `/suppression` shows the chance, effective chance, effect and roll mode; `/suppression_luck <lucky|unlucky|off>` forces
  the roll for testing, and `/spellhit <type>` rolls it. There are no monster spell hits yet, so this is a dev probe and a
  preparation for monster spells; `spell_damage_%_suppressed` effect modifiers and suppression-adjacent effects are not
  modelled.
- **Block / Spell Block** (`CharacterStats`, `DamageModel`): passives' `additional_block_%` (attack) and
  `base_spell_block_%` (spells) are summed, with `maximum_block_%` / `base_maximum_spell_block_%` raising the default
  75% maximum up to PoB's `BlockChanceCap = 90` (`src/Modules/Data.lua`; `Misc.lua` has the two 75 bases). The totals
  follow `CalcDefence.lua`: `round((base + flat) × (1 + block_chance_+%) × more)` for attack and
  `round(flat spell block × (1 + block_chance_+%) × more)` for spells, then floored at 0 and capped at the
  per-type maximum. An equipped
  shield adds `ShieldTypes.Block` (22 for the Twig Spirit Shield) to attack block, and the shield-specific passives
  (`shield_block_%`, `dual_wield_or_shield_block_%`, `block_chance_%_while_holding_shield` and their spell equivalents)
  apply only while it is equipped. Staff passives (`staff_block_%`, `additional_staff_block_%`,
  `spell_block_with_staff_%`, `spell_block_chance_%_while_holding_staff_or_shield`) apply with a staff; dual wielding
  adds the inherent 20% attack block and the dual-wield block/spell-block passives; bows use their own
  `with_bow_additional_block_%` / `spell_block_with_bow_%`. A successful block prevents the hit (`BlockEffect` defaults to full prevention).
  Monster melee uses attack block; `/blockhit` tests attack/spell block and `/block` prints both chances. The character
  panel's attack row is `attack_block_%` with `uncapped_attack_block_%` in parentheses; a separate spell block row is
  drawn only when `display_spell_block_chance` is non-zero, so `ClassAttributes` sends both display stats. The Block
  Mastery effects (`PassiveSkillMasteryEffects.dat`) add +2 maximum attack block, +2 maximum spell block, 20 life and
  20 mana on a block, or +1% spell block per 5% attack block; the last uses the final capped attack block and is applied
  after the shield base. **Versatile Combatant** (`keystone_versatile_combatant`) reduces both maxima by 10% and adds
  2% spell block for each 1% of attack block above the reduced maximum (uncapped attack block minus maximum). Glancing
  Blows/`BlockEffect` modifiers, projectile block and other on-block triggers are not modelled. Lucky block
  (`base_block_luck`, `attack_block_luck`, `spell_block_luck`) uses `1 - (1 - p)²` (PoB CalcDefence); Svalinn's unique
  item modifiers themselves are not supported yet.

### Cyclone area updates (2026-10-25)

`/set_aoe` sends the increased-area input, but Cyclone's visual also needs the final
`active_skill_area_of_effect_radius` in its granted skill record. The Gateway now computes and sends
that radius together with its base for Cyclone/CycloneAltX. The hit-radius calculation uses the same
base selection and `SkillArea.Radius`, so visual scaling and hit selection agree on the next cast.
Turning the override off resends the character's own modifiers and the newly calculated radius.

The exported Cyclone stat set has no explicit base radius. Use 16 cells for these two variants,
matching [Path of Building's skill definitions](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/dev/src/Export/Skills/act_dex.txt);
an explicit positive base stat takes precedence. Other skills keep their existing data/fallback base.
Client evidence: `0x1414b13c0` reads final radius stat 0x4876 to scale its cyclone effect; the shared
cyclone path `0x141d434c0` also reads 0x4876 (or the base 0x4875 in its conditional-radius path).
Increased area is not increased radius: +300% area changes radius 16 → 32, +0% restores 16 without
other modifiers. Tests cover this calculation, spell-area exclusion, explicit bases and the fallback.
Additional melee-range/stage modifiers specific to Cyclone remain outside this initial area model.
Live visual verification after restarting Gateway is pending.

**Follow-up after live failure:** sending only 0x4875/0x4876 did not change the rendered channel.
The live hit radius did change (24 → 42 cells after `/set_aoe 300` on the test character), so that
was insufficient evidence for visual scaling. The conditional-radius helper `0x141939880` reads
`total_skill_area_of_effect_radius_+%_including_final` (0x1591) on its normal branch and the
aggregated `skill_area_of_effect_+%` (0xd7), rather than the base input 0x1b5, on its conditional
branch. Cyclone's skill record now carries both derived values as well. +300% increased area is
+100% radius before other modifiers. Compute all outputs from the original inputs before writing
the aggregated 0xd7, to avoid counting 0x1b5 twice. The server's hit calculation still uses the
original input stats. `Cyclone area:` log lines record the transmitted base, radius, area increase
and radius scale. Live verification of this follow-up is pending.
Checked against the disassembly (2026-10-25): the conditional branch of `0x141939880` reads only 0xd7, never
0x1b5, so the record's aggregated 0xd7 is not counted twice; its more multipliers are 0x1670, 0x16ee and 0x28d7.
Its caller `0x141939aa0` adds `active_skill_base_radius_+` (0x16c4) to the base itself, so the record carries the
base without it, as the server's `SkillArea.Radius` adds it to its own base.

## 13. Auras and reservation

**Persistent toggles (2026-10-25).** CharacterSaves.ActiveAuras stores stable GrantedEffects Ids,
updated transactionally on every successful manual toggle; a missing gem suspends runtime effects and retains intent.
On login/area connection, LoadProgress restores progress and evaluates usable gems first, then resolves
saved aura ids to fresh skill and source/recipient buff ids when town and caster restrictions allow them.
It recalculates pools with the restored bonuses and initializes the fresh connection's mana to the unreserved amount.
SendPlayer restores both buff records, so ground art, top-left icons and skill-bar indicators return.
An absent/unusable gem skips that aura without discarding saved intent; it can return after requirements are met.
Remembered restoration clamps reservation/current mana rather than rejecting saved intent for transient affordability.
Old saves without ActiveAuras start empty.

**Expanded activation (2026-10-25).** Determination (buff 142), Grace (143), Discipline (148),
Anger (149), Hatred (150), Wrath (151), Zealotry (1272), Malevolence (1274) and Envy (828)
now use the same toggle/reservation/source-and-recipient lifecycle as the original eight auras.
RequiredMana checks the uncapped total before adding an aura; ReservedMana remains capped for display.
Previously clamping before validation made the insufficient-reservation check unreachable.
Determination and Grace add their level/quality-based flat and more defences, including Iron Reflexes.
Anger/Envy supply added attack/spell damage, Hatred feeds gain-as-extra conversion, Wrath adds attack
lightning damage and more spell lightning damage, Zealotry supplies spell crit and more spell damage.
Spell added damage uses the gem's damage effectiveness. Malevolence's more DoT affects the implemented
Righteous Fire burn; its duration stat and Discipline's ES stats are published, but the server's ES pool,
recharge, general DoT skills and Zealotry's conditional consecrated ground remain unimplemented.

**Source and recipient pair (2026-10-25).** Sending only the source fixed ground art but the live
client stopped showing top-left aura icons. Each self aura now owns two buff instances of its
BuffDefinitions row: a source with bit0 art and explicit display state 0 (bit5), and an ordinary
received buff with default display state 2 and bit3 skill association. The source is sent first;
both ids are removed on toggle-off or gem removal and restored on area entry. The server retains
one Aura record, so reservation and bonuses are counted once. Top-left recovery awaits live confirmation.

**Caster visuals (2026-10-25, awaiting live verification).** BuffVisuals' EPKFiles1 is the common
`affected_player.epk`; EPKFiles2 holds each aura's own art (Clarity, Vitality, Precision, Haste and
the four Purities). `0x1416082d0` selects `m_AuraEffectPack` (row +0x20/+0x28) when Buff +0x30
is nonzero, otherwise `m_EffectPack` (+0x10/+0x18). AddBuff bit0's first byte (+0x76) becomes
that field via `0x141e07220` and `0x141e06290`. `auraSource: true` sends the complete bit0 group
`u8 1, u32 0, u8 0, u16 0` before other optional fields. All supported self auras use this on
activation and restoration; recipients and other buffs retain their existing default. These aura
rows have no MiscAnimated3 ring, so the nonzero marker only selects their EPK art; a radius-scaled
ring will require its actual radius rather than this marker. The skill-bar association is independent.

- **Reservation is in the data.** `GrantedEffectsPerLevel` has `ManaReservationFlat` and
  `ManaReservationPercent` (permyriad: `5000` = 50%); `SkillCatalog.Reservation` reads them. Standard
  auras reserve 50%, Purity of Fire/Ice/Lightning and Discipline 35%, Heralds 25%; Clarity, Vitality and
  Precision reserve flat amounts at level 1 (34, 28 and 22). No separate table is needed.
- **The server computes the reserved amount.** The client does not derive it: `PlayerObject` carries it in
  the Life component's mana pool (`current, reserved`), and every S→C 0x1b1 mana update must send it too,
  or the client clears it. The amount is PoB's `CalcPerform`: flat and percent reservations are divided by
  `1 + mana reservation efficiency / 100`, the percent is rounded to two decimals per skill, and the total
  is `ceil(maximum mana × total percent / 100) + flat`. The maximum mana is unchanged; current mana is
  capped at `maximum - reserved`.
- **Activation.** The client sends an aura as a normal self/ground gem cast (C→S 0x24/0x25); the Gateway
  toggles it instead of paying a mana cost. The buff row comes from `AuraCatalog` (Purity of Elements 144,
  Purity of Fire/Ice/Lightning 145/146/147, Clarity 155, Vitality 153, Precision 1293) and is sent with
  S→C 0x1c1 with the player as source and the skill id as the source skill. A second cast removes it with
  0x1c2. The buff visual is the row's `BuffVisuals` (the `player_auras/...` EPKs); the server also sends
   the final stats, because the client derives none of them. The skill-bar indicator uses the buff's separate
   skill association (0x1c1 flags bit3): **u16 BE ActionTypes HASH16, then u16 BE server skill id**.
   The Gateway sends it for activation and when restoring auras on area entry. Removing the buff removes
   the association. The full action hash comes from GrantedEffects.ActiveSkill → ActiveSkills.ActionType
   → ActionTypes.HASH16; it must not be masked to the basic-action id.
   Client `0x140ad1400` draws `AuraFrame` and the rotating `AuraCircle` when `0x141e11180` finds an
   IsSkillBuff whose +0x48/+0x4a match the skill's +0x40/+0x42. Reader `0x141fdf870` reads the action
   hash into message +0x74 and skill id into +0x72; `0x141e11720` copies them to buff +0x4a/+0x48.
   The mandatory source skill (+0x60) alone does not satisfy this check. Automated wire/lookup tests
   cover the change; live icon verification is pending.
   **Correction to the earlier notes:** 0x25f in `0x140ae1130`/`0x140ae4550` refers to Elemental
   Overload (BuffDefinitions row 606, returned one-based by `0x142454bd0`), not an aura UI event or a
   network opcode. Sending a standalone 0x25f packet is invalid and is not needed for aura indicators.
- **Implemented effects.** Purity of Elements/Fire/Ice/Lightning add their resistances; Clarity and Vitality
  add their flat regeneration; Precision adds flat accuracy and increased critical strike chance; Haste adds
  attack, cast and movement speed. Generic
  `non_curse_aura_effect_+%` / `aura_effect_+%` and the Reservation mastery's self-aura effect scale the
  values; reservation efficiency comes from `base_mana_reservation_efficiency_+%`,
  `base_reservation_efficiency_+%` and the mana/global efficiency stats. `AuraCatalog.Bonus` and `RawStats`
  apply these to `Pools` and `CharacterStats`.
- **Not implemented yet.** Casting one of these logs that its reservation is known but its effect is not implemented and does not
  reserve mana:
  - **Enemy auras:** Pride still needs a radius- and time-dependent enemy physical-damage-taken debuff.
  - **Heralds:** Ash, Ice and Thunder need on-kill/overkill/shatter/storm effects; Agony and Purity need minions.
  - **Banners:** Defiance, Dread and War need the planted banner, stages and their pulses.
  - **Curse auras:** Blasphemy-style skills need the curse system.
  - **Discipline:** activation, reservation and presentation work; the energy-shield pool/recharge mechanics remain open.
  - **Partial effects:** Purity of Elements' elemental-ailment immunity is not applied. Purity of Fire/Ice/Lightning
    max-resistance values are read at the gem's level (0 at level 1).
  - **Masteries:** Mana6, Reservation3 (life efficiency), Reservation4 (area) and Reservation5 (self aura effect) are mapped;
    Reservation1 (damage per aura), Reservation2 (max resistances with reserved life and mana) and Reservation6 (aura
    duration) are not. Skill-specific reservation efficiency/effect stats are not modelled.
  - **Systems:** supports, party/minion auras and life reservation.
- **Dev check.** `/auras` prints the active auras, flat/percent reservation, reserved and unreserved mana,
  reservation efficiency and aura effect. Area changes recreate the player object, so `SendPlayer` puts the
  running auras' buffs back on it.

## 14. Character panel sums

Whaletooth Bangle's owner duration is projected by `SkillDurationModifiers` in `CharacterPlayerStats`:
`skill_effect_duration_+%` gains the signed per-ten-Strength value (key 2817) for each complete group of ten final Strength.
It adds to ordinary raw duration once per rebuild, after attribute resolution; explicit final-stat overrides win.
The resulting stat reaches the normal player/GrantedSkill stat publication without changing message layouts.
Enduring Cry consumes that duration in its paid recovery interval, including compatible linked duration supports.
Its base 2500 ms becomes 3175 ms at 272 final Strength with Whaletooth, retaining the Power-based recovery rate.
Native paired recovery totals, expiry Life correction, transfer and cleanup are in
[Antiquarian](../game/antiquarian.md#strength-duration-and-stun).
Other skill/summon lifetime engines remain separate.
See [the ascendancy scope and lifecycle checks](../game/ascendancies.md#whaletooth-bangle-duration-stat).

The character panel (CharacterPanelStats.dat, 387 rows) reads mostly virtual stats that the server computes; the client
derives none of them (§4). Passives and item mods give the base form (`base_stun_recovery_+%`) while the Stun Recovery
row reads `stun_recovery_+%`. `PanelStats` (issue #75) lists, for about 150 display stats, what each one sums: the display
stat's own value (Haste gives `cast_speed_+%` itself), its base form and the unconditional combined stats that apply to
it:

| Display | Also counts |
|---|---|
| `avoid_ignite/chill/freeze/shock_%` | `avoid_all_elemental_status_%` (Avoid Elemental Ailments) |
| `self_*_duration_-%` (elemental ailments) | `self_elemental_status_duration_-%` |
| `virtual_ignite/chill/freeze/shock/scorch/frostburn/sap_duration_+%` | `base_elemental_status_ailment_duration_+%`, `base_all_ailment_duration_+%`, the per-element ailment duration, `chill_and_freeze_duration_+%` |
| `virtual_chill/shock/scorch/frostburn/sap_effect_+%` | `non_damaging_ailment_effect_+%`, `cold_` / `lightning_ailment_effect_+%` |
| `chance_to_freeze_%`, `chance_to_shock_%`, the ignite chances | `chance_to_freeze_shock_ignite_%` |
| `reduce_enemy_fire/cold/lightning_resistance_%` | `reduce_enemy_elemental_resistance_%`, `base_penetrate_elemental_resistances_%` |
| `cast_speed_+%` | `attack_and_cast_speed_+%` (also counted in attack speed) |
| `life/mana_leech_from_*_damage_permyriad` | the elemental leech for fire, cold, lightning; life also `horrible_ugly_life_leech_from_*_damage_perhundredthousand` ÷ 10 |
| `virtual_life/mana/energy_shield_leech_speed_+%` | `life_mana_es_leech_speed_+%` |
| `virtual_endurance/frenzy/power_charge_duration_+%` | `charge_duration_+%`, `base_charge_duration_+%` |
| `display_skill_increased_item_rarity/quantity_+%` | `base_item_found_rarity/quantity_+%` |
| `virtual_trap/mine_throwing_speed_+%` | `trap_and_mine_throwing_speed_+%` (mine: `mine_laying_speed_+%`) |
| `minimum/maximum_*_damage_to_return_to_melee_attacker` | `*_damage_to_return_to_melee_attacker` (both ends) |
| `extra_damage_taken_from_crit_+%` | − `base_self_critical_strike_multiplier_-%` |
| `impale_on_hit_%_chance` | `attacks_impale_on_hit_%_chance` (the attack reads this row's name) |
| `intermediary_combined_action_speed_+%` | − `action_speed_-%` |

**Main hand / off hand rows** (`main_hand_…` / `off_hand_…`, `virtual_total_main_hand_…`) add the global stats and that
hand's item's own local stats (`HandLocalStats`: the active item in the first weapon set, else the second):
`local_chance_to_bleed_on_hit_%`, `local_poison_on_hit_%`, `local_attacks_impale_on_hit_%_chance`,
`local_chance_to_blind_on_hit_%`, `local_fire/cold/lightning/chaos/elemental_penetration_%`,
`local_stun_threshold_reduction_+%`, `local_life/mana_gain_per_target`, `local_life/mana_leech_from_physical_damage_permyriad`
and `local_double_damage_to_chilled_enemies`. Spell and secondary rows use the global stats only (`spells_…` where the
game has a spell form).

**Damage rows from the hit the server swings with** (`PanelStats.HandDamage`, issue #75):
`main_hand_minimum_total_damage`, `main_hand_maximum_total_damage` and the per-type
`main_hand_minimum/maximum_{physical,fire,cold,lightning,chaos}_damage` are computed from
`Skills.UnarmedFor(ClassRow).HitDamage(stats)` after conversion, rounded down because the client shows whole numbers, and
sent with `skill_uses_main_hand = 1` — the client gates the rows on that flag, so the numbers alone leave them blank. Only
the main hand is filled, since the server attacks with it; an empty packet adds no rows.

**Selected attack-gem hit rows (#196)** (`AttackSkillHit`, `AttackPanelStats`): main-hand total and per-type
ranges now use the same packet constructor as projectile and area attack combat.
The constructor preserves the current order: the actual weapon's hit with character modifiers, flat added attack
damage from character inputs, the skill's level-aware effectiveness, then conversion and gain-as-extra.
Conversion retains `ConversionFor` precedence: player keys win and level-only skill keys fill gaps, without quality
conversion. Skill-owned damage modifiers are not newly applied; this panel change preserves current combat rules.
Burning Arrow level one uses effectiveness 18000 and 100% skill physical-to-fire conversion; Heavy Strike uses 12430.
All twelve main-hand range fields are explicit and floored, including zeros for absent types after conversion.
`CharacterPanelStats` reads whole-number total keys 670/671, physical 279/280, fire 267/268, cold 263/264,
lightning 271/272 and chaos 275/276. These rows retain the existing main-hand and spell/secondary gates.
Per-use average (2044) and DPS (679) are hundredths; packet fractions are retained through the average, and DPS
uses the skill's already-emitted `hundred_times_attacks_per_second` (678), rather than recalculating attack timing.
Consequently DPS uses that rounded displayed rate, not a new precise action-rate calculation.
Copied-save GUI baseline: a 5-13 physical bow with Strength 14 showed 5-13 physical/total, per-use 9.18 and DPS 12.86
for both ordinary attack and Burning Arrow, while Burning Arrow already showed its corrected APS 0.98.
The preserved raw packet is 5.1-13.26; Burning Arrow resolves 9.18-23.868 fire, average 16.524.
Actual native checks now show 9-23 fire/total, physical zero, per-use 16.52 and DPS 16.19 at emitted APS 0.98.
Adding 5 flat cold attack damage showed cold 9-9, fire 9-23, total 18-32, per-use 25.52 and DPS 25.01.
An actual noncritical server hit dealt 24 damage to an 86-life Ash Harvester.
Clearing the added cold stat returned every cold field to zero and restored the 9-23 fire/total display.
Relog retained Burning Arrow's 9-23, per-use 16.52 and DPS 16.19; ordinary attack stayed 5-13 physical,
per-use 9.18, DPS 12.86 and APS 1.40.
The fixture is also regression-tested.
Crit weighting, accuracy, target mitigation, integer-roll expectation and downtime are excluded.
Initial creation and refresh both pass raw `CharacterPlayerStats` plus the actual weapon into `GemSkills` separately
from inherited final display stats. Derived hand ranges cannot feed back into the packet.
Only attack gems receive this override; ordinary attacks, spells, non-attack actions and off-hand rows keep their
existing display paths. Supports, conditional mechanics, off-hand alternation and broader modifier-scope corrections
remain separate work. Existing character modifiers are preserved even where their weapon scope remains approximate.
Real catalog, packet/source-isolation, conversion, added-damage, flooring, absent-type and native-table tests pass.
Native skill selection, stat refresh/clearing and relog checks passed; equipment UI refresh was not separately exercised.
Local integration captures are `logs/attack-damage-burning-arrow-{default,added-cold,cleared,relog}.json`
and `logs/attack-damage-server-hit.json`; no extracted data changes are needed.

**Selected spell hit rows** (`SpellPanelStats`, issue #188): `spell_minimum/maximum_total_damage` and
`spell_minimum/maximum_{physical,fire,cold,lightning,chaos}_damage` come from `SpellHit.Resolve`, the same converted
packet used by projectile and area combat. Every spell range is written, including zeros for absent types and hitless
spells. CharacterPanelStats reads total keys 674/675, physical 261/262, fire 255/256, cold 253/254, lightning 257/259
and chaos 258/260 as whole integers, with `casting_spell` (438) required and
`display_skill_deals_secondary_damage` (1675) excluded. DPS (679), damage per use (2044) and casts per second (682)
are hundredths (the table's formatter 14), so range values must not be multiplied by 100.
For example, level-one Fireball's 9–14 with +12% spell damage gives a 10.08–15.68 packet: the range is 10–15,
per use is 12.88, and DPS at the emitted 1.33 casts/s is 17.13. These averages exclude crit and mitigation.
The same skill construction runs on initial player creation and subsequent refresh; character conversion inputs
are passed separately from the inherited final weapon/display stats. Attack and unsupported-action displays keep
their existing path.
Native panel/getter checks with a +12% spell-damage wand show Fireball 10-15 fire, 12.88 per use and 17.13 DPS,
and Freezing Pulse 10-15 cold, 12.88 per use and 19.84 DPS.
With 50% fire-to-chaos conversion and 20% fire gained as chaos, Fireball shows total 12-18, fire 5-7 and chaos 7-10,
15.46 per use and 20.56 DPS; an actual noncritical server hit dealt 14 damage.
Scorching Ray has twelve zero hit-range fields and zero DPS/per-use, also after adding 100 flat spell fire damage.
The same added-damage control gives Fireball 234-239 fire, confirming that hit-capable spells still receive it.
Automated packet, conversion, hitless, creation/refresh and table-format checks also pass.

**Rows that combine a pool with a stat** (`PanelStats.FromPools`): `projectile_attack_block_%` = attack block +
`additional_block_chance_against_projectiles_%`, `spell_projectile_block_%` = spell block + `base_spell_projectile_block_%`
(with `spell_block_differs_against_projectiles` 1, which shows the row), each capped at its maximum, and
`chaos_damage_over_time_resistance_%` (and its uncapped form) = chaos resistance +
`additional_chaos_resistance_against_damage_over_time_%`, capped at the chaos maximum. Each is sent only when its stat is set.

Conditional stats (while, if, per charge, against …) are not counted. Flags (`cannot_be_frozen`, `immune_to_ignite`
…) are 1 when any source is set; "Immune to Ignite and Shock" sets both. Only non-zero values are sent; a sum that drops
to zero is cleared with the other stale stats (`PlayerStatsWithCleared`). A stat set with `/set_stat` keeps its value.
`cast_speed_+%` is now the total, so `CastSpeedPct` reads only it. Which combined stats count is from the game's
documented behaviour, not a capture; the signs of the crit-taken and action speed rows are not confirmed. Live
2026-10-20: `/set_stat base_stun_recovery_+% 30`, `avoid_all_elemental_status_% 20` and `base_cannot_be_frozen 1` show
on the Defence tab as Stun Recovery 30%, the four Avoidance rows at 20% and "Cannot be Frozen".

## 15. Endurance, Frenzy and Power charges

Issue #237, part of #75's Charges tab. Code: `Charges.cs` (state and limits), `ChargeGains.cs` (what grants them),
`ChargeEffects.cs` (what they do), `ChargeSession.cs` (one connection's charges at work), `ChargeBuffs.cs` (the HUD
buffs), `Program.Charges.cs` (the Gateway glue) and `ChargePanelStats.cs` (the maxima).

- **Rules.** A charge lasts 10 seconds (poewiki's Charge page; Path of Building's CalcSetup `ChargeDuration` BASE 10;
  no game table holds it), × (1 + `charge_duration_+%` + `base_charge_duration_+%` + the type's own increase
  (`endurance_charge_duration_+%`, `base_frenzy_charge_duration_+%`, `power_charge_duration_+%`) +
  `frenzy_charge_duration_+%_per_frenzy_charge` × the Frenzy Charges after the gain) × (1 +
  `power_charge_duration_+%_final`), floored to milliseconds. Gaining a charge restarts the duration of every charge of
  its type, also at the maximum, where none is added, and all charges of a type expire together (poewiki). The maximum
  is `ChargePanelStats`' (3 from Character.ot plus `max_*_charges`, the combined power-and-endurance and
  power-and-frenzy mods and `maximum_frenzy_power_endurance_charges`; verified on the native Charges tab with all three
  baseline rows at `0 / 3`); the minimum is `base_minimum_*_charges` +
  `minimum_frenzy_power_endurance_charges`, at most the maximum, and is always held; a gain adds on top of what the
  character has (Path of Building: max(charges, min(maximum, minimum)); poewiki: the maximum takes priority). A lower
  maximum (an item taken off) removes the charges above it at the next step: the session observes the limits and the
  periodic stats whenever the character's stats are worked out (`ChargeSession.Observe` in `WithCharges`), and nothing
  polls them. "% chance that if you would gain … Charges, you instead gain up to maximum" is
  rolled on every gain. Time is the connection's game clock, which stops while the game is paused.
- **Death and area changes.** Death takes every gained charge at once (`PlayerDied`; BuffDefinitions `CancelOnDeath`
  on the three rows), so a pause or Resurrect in Town cannot keep them; the minimum stays. An area change (`Transfer`)
  hands the charges to the destination ticket with the time they had left;
  the new connection's ticker restores them and puts their buffs on the new player object, so loading does not count.
  A new login starts with none. Whether the official server keeps charges across areas is unverified: poewiki's Charge
  page says nothing about it. The rule is the one `CarryCharges` call in `Transfer`.
- **Gains** (`ChargeGains.Rules`): each source stat is its own roll of `rng.Next(100) < chance`; 100 and the "Gain a …
  Charge" flags gain without a roll, and a random type is drawn with `rng.Next(3)`. On kill:
  `endurance_charge_on_kill_%`, `add_frenzy_charge_on_kill_%_chance`, `add_power_charge_on_kill_%_chance`, the two kill
  flags, `power_frenzy_or_endurance_charge_on_kill_%`, the shield and dual-wield ones. On hit:
  `add_frenzy_charge_on_skill_hit_%` (the character's plus the used skill's), the rare-or-unique ones. On critical
  strike: power, endurance, melee endurance, frenzy, wand power, per enemy you crit. On non-critical strike: power,
  with claws or daggers. When hit: endurance, frenzy. On block: endurance, frenzy, attack-only frenzy, power,
  spell-only power, a random one. Over time (`ChargeGains.Periodics`): per second, every x ms (the body armour
  implicits), random every 6 seconds, each second if hit in the last 4 seconds, while stationary or moving (the walk
  estimate); not in towns. Skills: the Frenzy gem's hit gives a Frenzy Charge once per use (ActiveSkills `frenzy`;
  without a Frenzy projectile row it hits as an area, and only its first target counts), Power Siphon's
  killing hit a Power Charge, Flicker Strike's and Power Siphon's own on-hit chances count, and Enduring Cry grants an
  Endurance Charge per 5 power (`endurance_charge_granted_per_X_monster_power_during_endurance_warcry`) of the hostile
  monsters within 60 cells grown by area (Path of Building's radius; power 1 normal, 2 magic, 10 rare, 20 unique) when
  its cast ends on the game clock, sped up by the player's and the gem's `warcry_speed_+%`; a later action does not
  cancel it (`cannot_cancel_skill_before_contact_point`).
  The combat code only enqueues events (`ChargesOnHit` at the default attack, area and projectile hits, `ChargesOnKill`
  in `MonsterKilled`, `ChargesWhenHit` in `MonsterSwing` and `/blockhit`); `RunCharges` rolls them every 50 ms, with
  the character's stats worked out outside the Monsters lock and only while events wait.
- **Effects** (`ChargeEffects`). Every character has Character.ot's per-charge stats (`Inherent`): 4% increased attack
  and cast speed and 4% more damage per Frenzy Charge, 4% physical and 4% elemental damage reduction per Endurance
  Charge, 50% increased critical strike chance per Power Charge (Path of Building's CalcSetup uses the same constants).
  They and the "per … charge" stats of passives, items and gems become, × the charges held, the stats the server
  already calculates with (`ChargeEffects.PerCharge`, added to the character's stat sources by `WithCharges`): attack
  and cast speed, critical strike chance and multiplier, increased and added damage, gain as extra chaos, resistances,
  armour, evasion, block, spell suppression, movement speed, accuracy, area, projectile speed, energy shield, life and
  mana regeneration, and panel sums (stun, cooldown, life on kill). The "with no Frenzy Charges" and "while you have
  any" stats follow the count (`Conditional`). The more damage per Frenzy Charge scales every hit (`AttackHit`,
  `SpellDamage.More`) and Righteous Fire's burning (`ChargeDamageMore`). The physical damage reduction (with the
  passives' `base_additional_physical_damage_reduction_%`) adds to armour's and the elemental one applies after
  resistance, both capped at 90% (Path of Building's CalcDefence; `DamageModel.MitigatePhysical`, `DamageModel.Reduce`),
  for monster hits and the dev `/hit`, `/spellhit` and `/blockhit`.
- **Stats sent.** `current_*_charges` (the Charges tab's "x / y" rows, with `virtual_maximum_*_charges`),
  `actual_current_*_charges` (the buff rows' `Current_StatsKey`), `minimum_*_charges`, `current_removable_*_charges`,
  `additional_physical_damage_reduction_%`, `additional_elemental_damage_reduction_%` and the per-charge stats, which
  the per-charge rows read. A count change recomputes the pools and the walk speed and sends 0x1b3 and the skills'
  stats (the client swings and casts with the speed in its skill records), without the skill-bar work of
  `SendCharacterStats` (`SendChargeStats`).
- **The HUD** (live 2026-10-25). One buff per type: BuffDefinitions rows 28 `endurance_charge`, 27 `frenzy_charge` and
  29 `power_charge` (`ShowCount`; BuffVisuals 41, 40 and 42 with the BuffVisualOrbs `ChargeEndurance`, `ChargeFrenzy`,
  `ChargePower`), the player as its source. S→C 0x1c1 flags bit1 carries the time left (f32 seconds) and bit2 the
  count (u16, read after bit7's field and before bit3's). The icon at the top left shows the count from bit2 (a raw buff
  with 2 stacks and no charge stats showed "2") and a timer from bit1; without bit1 (minimum charges) it has no timer.
  The orbs circle the character. A change sends 0x1c2 for the old instance and a new 0x1c1. In towns the client draws
  no charge buff (Lioneye's Watch, the server's and a raw one alike); the server keeps counting the charges there.
- **`/charges`** shows each type's count, maximum, minimum, duration and time left; `/charges <type> <n>` sets a count,
  `/charges add <type> [n]` gains as a skill would (refresh, maximum chance) and `/charges clear` empties them. A stat
  set with `/set_stat` counts in the charge effects and limits too.
- **Not modelled.** The other charge types (Spirit, Siphoning, Blitz, Challenger, Brutal, Affliction, Absorption, Blood,
  Brine and the rest) and the stats that turn standard charges into them; skills and effects that consume or remove
  charges (Discharge, Flicker Strike's cooldown bypass, Immortal Call, "lose all … when", expiry triggers); gains that
  need stun, freeze, bleed, poison, curses, traps, mines, totems, Vaal souls, other warcries, allies, flask use (the
  utility flask PR #233) or support gems (Power Charge On Critical, Endurance Charge on Melee Stun); Power Siphon's "or
  soon after the hit"; Blood Rage (no self-buff skills); conditional maxima and minima (while affected by an aura, on
  low life, devotion, "count as having maximum", a maximum equal to another type's); per-charge stats whose target the
  server does not calculate (ailments, leech, damage taken, damage over time increases, wand-only or attack-only damage,
  mana cost, buff duration, Frenzy's and Flicker Strike's own per-charge attack speed and damage).

## 16. Rage

Generic Rage generation, loss, attack damage, transient ownership and native count/buff packets are implemented.
See [26-rage.md](26-rage.md) for inputs, primary evidence, versioned policies and the scoped native controls.
Automated parsed-dispatch coverage does not constitute native verification.

## Open

- Monster attacks still land at half the attack time (the player's use the contact keyframe); "more" multipliers; energy shield (Righteous Fire's ES parts are ignored).
- A 30 Hz server loop: `TickRate` only bounds the attack time; hits use timers and the monster AI runs every 500 ms.
- Monsters use only their first granted effect; no ranged or spell skills, no monster skill choice.
- A damage model: armour, evasion, resistances, crits; the player's real life from level and stats.
- The death screen's bit4 and trailing fields (Sanctum and other modes); exit to character selection (0x153).
- Skill record fields +0xc0/+0xc4 still have unproved semantic names.
- Projectiles: their height above the ground and model orientation (§10), collision, the explosion, the real
  skill → projectile link; damage for ground casts.
- The meaning of 0x53's two trailing u16.

### Aura lifecycle: town suppression and death

ActiveAuras records per-character enabled intent by GrantedEffects Id. LoadProgress restores every remembered aura whose gem is available, using fresh connection skill/buff ids. Restoration is not a new cast and does not drop saved auras when their combined reservation exceeds a transient mana pool; displayed reservation is clamped to maximum mana. New manual activations still validate the full reservation.

Campaign towns (WorldAreas.IsTown via WaypointCatalog) instantiate no aura runtime entries: no reservation, bonuses, source/recipient buffs, icons or floor effects. Saved intent stays untouched and restores automatically in hideouts, maps and campaign combat areas. Ground skill requests allow only Move in towns; gem/default attacks on objects and the shared gem cast entry point are refused there. NPC, stash and waypoint interactions remain available.

Skill removal suspends runtime effects without deleting enabled intent. A skill-list refresh resumes remembered effects when their gems become available again. Manual toggles update intent normally. Actual death paths clear saved intent and runtime auras before the death screen; the player tick also handles death cleanup. Resurrection resets the per-death cleanup guard but does not reactivate cleared auras. Switching characters never changes another character's intent. AuraSaveTests cover independent characters, town round trips, all enabled effects despite transient reservation limits, fresh runtime ids and death reset. Live lifecycle verification pending.

#### Aura restoration bootstrap correction

A real town/hideout round trip restored only Vitality despite saved Precision and Clarity. The log marked the body armour inactive because Precision needed 22 dexterity while initial pools still contained the class base 14. CharacterStatSources was checking socketed gems against stale/uninitialized pools while constructing those very pools, then disabling the entire item's global modifiers and all its gems.

Item activation now uses only the equipment's own requirements. Gem requirements remain enforced individually at socketing/casting against completed pools; an unavailable gem no longer suppresses its host armour, unrelated gems or their attributes. This removes the circular initialization gate on aura candidates. AuraEquipmentRestoreTests reproduces the fresh-connection equipment case and restores all 17 supported self auras, including 34 distinct source/recipient buff packets after town suppression. Restoration has no aura-count limit.

### Grace Period on combat-area entry

Each instance connection grants a fresh 60-second `grace_period` buff after creating the player and before
spawning monsters. WorldAreas.IsTown and owned hideout templates are excluded by this server's policy.
BuffDefinitions row 6 is visible, non-recovery, uses BuffVisuals row 8 (`invulnerability.dds`), and has
base_cannot_be_damaged / is_hidden_monster / in_grace_period and minion protection flags. Send ordinary
S→C 0x1c1 with a fresh buff instance and u68 holding IEEE-754 f32 **60.0 seconds** (0x42700000); remove once with 0x1c2.

GracePeriod tracks a per-entry deadline and initial cell using the pause-aware connection game clock.
The first actual grid step (including approach-to-object movement), expiry, skill/attack use or a successful
flask use ends protection. An idle/blocked move order alone does not remove it. Protection is checked at
hit time and ends immediately on movement/deadline even before the recovery loop sends the remove packet.
It cannot reactivate by returning to the starting cell. No state is persisted across connections.

While protected, the server grants immunity to every damage type, suppresses RF self-burning and outgoing
RF pulses, and prevents hostile AI targeting and pending monster hits. Functional summoned-minion combat
is not implemented; cosmetic pets have no incoming/outgoing damage to protect.
GGG's [3.25.0 patch notes](https://www.pathofexile.com/forum/view-thread/3531661) explicitly identify the
Grace Period buff and protection from enemy targeting. The native buff metadata supplies the icon/name/stats;
the requested 60-second duration and town/hideout exclusions are server policy. See also the
[community mechanics reference](https://www.poewiki.net/wiki/Grace_Period) for action cancellation rules.
Automated coverage includes exact expiry, all exclusion combinations, native metadata/packet, one-step
movement and action cancellation. Real-client icon/timing verification is pending.

Grace Period countdown correction: packet +0x68 is copied by 0x141e07220 to constructor parameters,
then by 0x141e06290 to Buff +0x18 (initial duration) and +0x1c (remaining duration).
0x141e10520 subtracts elapsed seconds multiplied by Buff +0x20 (packet +0x6c, rate default 1).
The omitted-duration default 0x14015a670 is float positive infinity (0x7f800000).
An integer 60000 was interpreted as a tiny subnormal float and immediately displayed 0:00.
The regression independently decodes the BE duration bits and checks float 60, rather than comparing two calls
to the same packet writer. Native icon was confirmed; corrected countdown awaits live verification.
