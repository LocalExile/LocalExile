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
  u8 c, c × (u32, u32)        → manager +0x188 (ActiveSkills row → Vaal souls: FUN_141eea5b0 compares the souls a
                                use needs with the entry's count)
  u8 d, d × (u32, u32, u32)
  ```
  After login the Gateway sends every skill change this way (`SendSkills`): all the player's skills as records and the
  ids that left. It remembers the ids the client has per connection (`ClientSkillIds`, from the player object and every
  update since). Live: a re-socketed gem's skill appears in the character panel again (with 0x40 it did not).

`0x142197b10`:
```
u8 n,  n × skill record (0x142196ce0, through 0x1421981d0: insert sorted by id, replacing an equal id, then slot 7)
u8 m,  m × (u32 ActiveSkills row (0x101-byte rows), u32, u32) → manager +0x188, the Vaal souls (not used)
u32 k, k × (u16 skill id, u32 c, c × (u32, u32, u32, u32))     (not used)
```

Skill record (`0x142196ce0`):
```
u16  skill id                                      → +0x40
GEPL reference (0x1424a0dc0):
  u8                                               → ref +0x10 (0 for us; a stat-set reference with its u8 set uses it + 1)
  u32  GrantedEffectsPerLevel row (0x78-byte rows)
  stat-set reference (0x1424a0990): u8 a, [u8 if a], u32 GrantedEffectStatSetsPerLevel row (0xb8-byte rows)
  one more stat-set reference per entry of the effect's additional stat sets (GrantedEffects row → its list count)
u8   flags: bit0 → u32 parent object id ("Skill parent object did not exist" if unknown) + u16; bit1, bit2 → instance flags
u8   n supports, n × (GEPL reference, u32)
u32, u32, u32 cooldown ms, u8                       → skill +0xc0, +0xc4, +0xc8, +0xcc (0x141ee73d0)
compact n × (compact stat key, compact value)      the skill's own stats
per additional stat set: the same stats list
```
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

Accuracy's flat and increased passives are summed from `accuracy_rating` and `accuracy_rating_+%`, the weapon-specific
stats according to the equipped weapon shape (`sword_accuracy_rating`, `wand_accuracy_rating_+%`, ...), and the Accuracy
Mastery (`accuracy_rating_per_level`, `dexterity_accuracy_bonus_grants_accuracy_rating_+3_per_dexterity_instead`,
`gain_accuracy_rating_equal_to_intelligence`). Conditional stats (nearby ally, marked enemy, low life, frenzy charges,
unique/close enemies) are not modelled. The final values go to the client as `accuracy_rating` and, for the Offence
panel's Main Hand / Off Hand Accuracy rows, `main_hand_accuracy_rating` / `off_hand_accuracy_rating`. Its Chance to
Hit rows are sent directly as `main_hand_chance_to_hit_%` / `off_hand_chance_to_hit_%` (and the evasive-monster
variants), against a reference monster of the character's level; `character_sheet_reference_evasion` and
`..._high_dex_evasion` carry that monster's evasion.

Armour and evasion are summed from the passive tree and the **base types** of the equipped body armour, helmet, gloves
and boots ([13](13-items.md) §1): `base_physical_damage_reduction_rating` / `base_evasion_rating` are flat, while
`physical_damage_reduction_rating_+%`, `evasion_rating_+%`, `evasion_and_physical_damage_reduction_rating_+%`
(Cloth and Chain and similar nodes), `global_defences_+%` and the combined armour/evasion increase are additive
increased modifiers. **Iron Reflexes** (`keystone_iron_reflexes`) converts all evasion rating, including the base 15 and
passive/item flat evasion, to armour and removes dexterity's 1% per 5 evasion; the converted part keeps the armour,
evasion and shared defence increases once each. Item mods, quality, local defence rolls, shields and auras are not counted
yet; the base value is the armour row's minimum, which is the client's default when the item's Armour component is zero.
The final `physical_damage_reduction_rating` and `evasion_rating` (with their `_+%`) go to the client. Armour uses
PoB's `armour / (armour + damage × 5) × 100`, rounded and capped at 90% (`DamageModel.ArmourReduction`); evasion uses
the same hit-chance formula as accuracy, so the chance to evade is `100 - AccuracyHitChance(evasion, attacker accuracy)`
(`DamageModel.EvadeChance`). Monster melee rolls the same formula against `DefaultMonsterStats.Accuracy` at the
monster's level and evades before damage; physical monster hits then go through armour and block. `/defences` prints
both against a reference monster of the character's level. Life and mana still ignore passives and items.

The default attack's damage (`Unarmed.HitDamage`) is a physical `DamagePacket`: MinDamage..MaxDamage × (1 +
(`damage_+%` + `physical_damage_+%` + `melee_damage_+%` + `melee_physical_damage_+%`) / 100). The packet goes through
the conversion and gain-as-extra stats, then rolls one number per damage type; the server uses the same packet for its
hits. `/set_stat` overrides any of these. The packet, conversion order and damage-over-time model are in
[20-damage.md](20-damage.md); the panel's older `Unarmed.Damage` tuple is only the physical display range.

**Changing a skill after entering:** S→C **0x2ba** on the player with component index 9 (Actor) and the slot-20 data
`f0 0x40, f1 0, skill list`: the list replaces the skill of the same id, new stats included. Live: the swing sped up at
once. The Gateway now sends the 0x20 update instead (above), which also removes skills and updates the character panel.

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
   rounded and clamped to 5..100, with the monster's `DefaultMonsterStats.Evasion` at its level. A miss is logged and
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
hand's base: Slayer Overwhelm (PassiveSkills row 17315) grants 800 = 8.00%, and the Blind Prophet position's
Sightless (row 24698) grants 900 = 9.00%. `base_melee_critical_strike_chance_while_unarmed_%` (Stats.dat row 3906,
client key 3907, permyriad; the Behemoth position's Final Blow, row 10135, grants 1000 = 10.00%) fills the unarmed
melee attack that has no weapon base. Each is a base, not a fixed final chance: the generic
`critical_strike_chance_+%` increases still apply on top. A weapon hand keeps the unarmed base out, an unarmed
context keeps the weapon override out, and `SpellCriticalStrike`'s spell route reads neither, so cannot-crit and
the spell panels are untouched. The pool builds the base once in `AttackBaseCriticalStrike`; `/set_stat` on either
raw stat rebuilds the pools in place, and the panel's final `main_hand_critical_strike_chance` and the rolls share it.
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
  with a ±20% spread (the spread is assumed). They go in the monster's Stats component and in the skill.
- **`skill_is_non_pathing` (0x4f32) on the skill.** The Melee action's begin (vtable +0x38 `0x141938830`) is just that
  stat: when 0, the action runner (`0x141e8e060`) sends the action through the client's own walk-into-range step,
  which drops a server-driven monster's queued attack in the same frame (traced: created, queued, both checks passed,
  then neither current nor queued). The server walks the monster itself, so the skill is non-pathing. Only on the
  skill: on the monster's Stats component it also stops its Move action from pathing (the monsters stood still).
- **AI** (`RunMonsterAi`, 500 ms): a hostile, chasing monster within reach stops and swings: 0x1ab with its skill, flags
  0x0408 | 0x80, target = the player, sequence 0xffff. At half the attack time the hit lands if the monster lives and
  the player is still within reach + 2: 0x1b1 on the player.
- **Defence.** The monster's accuracy is `DefaultMonsterStats.Accuracy` at its level; the server rolls the player's
  evasion with the same `AccuracyHitChance` formula and sends no life update on a miss. The roll uses PoE's shared
  entropy counter (0–99): each attack adds its hit chance, 100 or more means a hit and subtracts 100, and the counter is
  randomized after 3.33 s without an attack. A hit then goes through the player's armour (`MitigatePhysical`) and
  block. `/defences` prints the reference values.
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
  An **instant** skill (stat `skill_is_instant`, below) is not sent this way: C→S **0x191** (8 bytes: u32 target
  object id, u16 skill id; `InstanceProtocol.ReadUseInstantSkill`) uses it without an action or a sequence, so the
  client keeps walking. The server applies the effect and answers with S→C **0x1ce** (`InstanceProtocol.InstantAction`:
  object header, u16 skill id, u16 sequence (0xffff), u16 flags (0x20 → u32 target object), compact target position;
  handler `0x142195880`): the client runs the skill's action without animation and consumes a use of the skill
  (actor vtable +0x198), which starts the skill bar's cooldown. Without it the tooltip showed the cooldown but the bar
  never ran it: the cooldown records had no entries (Frida, live 2026-10-21; not yet checked with 0x1ce).
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
- **Cast speed.** `base_cast_speed_+%` and `cast_speed_+%` shorten `virtual_base_spell_cast_time_ms` and
  `hundred_times_casts_per_second` in the skill's stats and the server's release time.
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
  +0xd0 (the cooldown group) comes from the GEPL row's CooldownGroup (+0x30). The server also enforces it: `CastGemSkill` refuses a skill until its
  GEPL Cooldown is over (`ClientState.SkillReadyAt`; Haste and the Purities 1200 ms, Righteous Fire 300 ms).
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
- **Damage** (`SpellDamage.cs`). A spell's hit is, per damage type, its `spell_minimum/maximum_base_{type}_damage`
  × (1 + `damage_+%` + `spell_damage_+%` + `elemental_damage_+%` for fire / cold / lightning + `{type}_damage_+%` +
  `projectile_damage_+%` for `base_is_projectile` skills), from the player's stats (passives, item mods, auras) under
  the skill's own. The server's hits roll between the unrounded minimum and maximum and drop the fraction; the skill's
  tooltip gets `display_hundred_times_damage_per_skill_use` (the unrounded average × 100) and
  `hundred_times_damage_per_second` (× casts per second). Before 2026-10-20 a gem skill carried the default attack's
  DPS with the player's stats. Conversion is applied per type through `DamagePacket`; more multipliers and added
  damage are not modelled.
- **Damage effectiveness** (2026-10-24). An attack gem's hit is the weapon's damage × its `DamageEffectiveness` (the
  GESSPL row, permyriad: Flicker Strike 7730 at level 1, 30890 at 20, 69600 at 40 — the gem's "Deals 77.3% of base
  Attack Damage"), through `SkillCatalog.DamageEffectiveness` and `SpellDamage.WithEffectiveness`. A row of 0 (the
  default attack's) or a negative one (a few skills have them) scales nothing. A **spell**'s own base damage is already
  final: its effectiveness is the added-damage one (Fireball 200%), and the GESSPL `BaseMultiplier` is 0 for every spell,
  so spells are left alone until added damage is modelled.
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
(read from live captures 2026-10-23). The Gateway moves the player there and, once per attack time, runs
`AreaSkillHit` around it at once (`ClientState.ChannelSkill`, `NextChannelHitAt`).

## 10. Projectiles (experimental)

The server creates and moves the projectile. The client's Projectile component (vtable `0x1435a0d10`, 0x20 bytes) does
nothing by itself: its slot 17 reads nothing and almost every other slot is a bare `ret`.

- **Object.** A 0x2b9 of the Projectiles.dat type named after the effect (`Metadata/Projectiles/Fireball`, hash of the
  path; the type register knows it from the table). `Metadata/Projectiles/Projectile` deserializes Positioned,
  Animated, BaseEvents and Projectile, so the blob is Positioned, one Animated byte and the tasks byte
  (`ProjectileObject`; the Animated component is index 1), then 0x1a9 (wake zone). The real link between a skill and
  its projectile is unknown; the Gateway matches the Id.
- **Flight.** S→C **0x1ba** to the target cell at the row's `ProjectileSpeed` (world units per second, 1040 → ~96
  cells per second at 250/23 world units per cell). The u32 at +0x28 is a **height**, not padding: the handler
  (`0x141e30c90`) passes it to `0x141e34400`, which keeps the terrain height for 0x7fffffff and otherwise sets
  z = height × 7.8125, the height read as a signed world z (positive down; a negative value is above sea level).
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
  Unlike 0x1ba, 0x1b7 copies the fixed pair directly. For signed world-z steps `s`, send
  marker `-s` and float world z `s * 7.8125`, preserving the projectile creation pair and fixed flight height.
  Terrain is not resampled at impact, so sloping ground does not make the explosion jump vertically.
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
  Gateway sends the projectile at terrain + `Game_PROJECTILE_HEIGHT` steps (default 5, the torso) as the signed world
  z: Positioned f0 0x04's i32 is -z / 7.8125 and its f32 is z; 0x1ba carries the same signed z.
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
- **Sign.** World z points down and the protocol's height is a signed world z in 7.8125-unit steps, not a positive
  "up" value: 0x1ba's u32 is that signed value (a positive height is below sea level, a negative one above), and
  Positioned's f0 0x04 block is i32 -z / 7.8125 with f32 z. Sending the positive "height up" put the arrow at
  z = +117 (under the hideout platform); the same value negated (0xfffffff1) put it at z = -117, the torso. The live
  arrow's i32 -5 / f32 +39.0625 is z = +39.0625, five steps above a player at z = +78.125.
- **Flight (live 2026-10-18).** S→C 0x1ba's float is the move's **duration in seconds**, not a speed: the handler
  passes distance / it as the speed, and the step walker sets Positioned +0x23c = that speed and +0x240 = the time per
  cell step (duration / the path's longest axis), advancing +0x244 by the frame time (`0x141e32710`). Sending the
  speed (95.7) made each step take ~1 s, so the fireball crawled (~10 world units/s, sampled with Frida); sending
  distance / speed it flies at ~1000 units/s. The fireball now starts `Game_PROJECTILE_AHEAD` cells (1.5) in front of
  the caster and flies on to `Game_PROJECTILE_RANGE` cells (120, clamped to the area), stopped by the first
  monster or wall contact described above. Default-attack arrows are visual only: their damage retains the attack
  contact timing and does not use the flight callback. `/mob_move` and `Game_MOVE_MODE=move` still pass a speed in that field.
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
  source object), u16 +0x60 (the source skill), u8 flags (bit0: u8 u32 u8 u16; bit1: u32 +0x68; bit2: u16 stacks,
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
updated transactionally on every successful toggle and cleared for an active aura when its gem leaves.
On login/area connection, LoadProgress restores progress and evaluates usable gems first, then resolves
saved aura ids to fresh skill and source/recipient buff ids. It checks total reservation against current
mana, recalculates pools with the restored bonuses and initializes current mana to the unreserved amount.
SendPlayer restores both buff records, so ground art, top-left icons and skill-bar indicators return.
An absent/unusable gem or insufficient reservation skips that aura without discarding saved intent;
it can return on a later login after requirements are met. Old saves without ActiveAuras start empty.

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

## Open

- Monster attacks still land at half the attack time (the player's use the contact keyframe); "more" multipliers; energy shield (Righteous Fire's ES parts are ignored).
- A 30 Hz server loop: `TickRate` only bounds the attack time; hits use timers and the monster AI runs every 500 ms.
- Monsters use only their first granted effect; no ranged or spell skills, no monster skill choice.
- A damage model: armour, evasion, resistances, crits; the player's real life from level and stats.
- The death screen's bit4 and trailing fields (Sanctum and other modes); exit to character selection (0x153).
- f0 0x20 (skill update), the second and third lists of f0 0x40, supports (linked support gems), gem levels.
- Projectiles: their height above the ground and model orientation (§10), collision, the explosion, the real
  skill → projectile link; damage for ground casts.
- The meaning of 0x53's two trailing u16.

## Standard maximum-charge rows

`ChargePanelStats.Compute` derives the three standard maximum-charge display values from base three plus unconditional modifiers. The current values remain zero; conditional modifiers, replacement charges and gain/expiry mechanics are outside this change. Verified on the native Charges tab with all three baseline rows at `0 / 3`; eight focused modifier tests cover the formulas.
### Aura lifecycle: town suppression and death

ActiveAuras records per-character enabled intent by GrantedEffects Id. LoadProgress restores every remembered aura whose gem is available, using fresh connection skill/buff ids. Restoration is not a new cast and does not drop saved auras when their combined reservation exceeds a transient mana pool; displayed reservation is clamped to maximum mana. New manual activations still validate the full reservation.

Campaign towns (WorldAreas.IsTown via WaypointCatalog) instantiate no aura runtime entries: no reservation, bonuses, source/recipient buffs, icons or floor effects. Saved intent stays untouched and restores automatically in hideouts, maps and campaign combat areas. Ground skill requests allow only Move in towns; gem/default attacks on objects and the shared gem cast entry point are refused there. NPC, stash and waypoint interactions remain available.

Skill removal suspends runtime effects without deleting enabled intent. A skill-list refresh resumes remembered effects when their gems become available again. Manual toggles update intent normally. Actual death paths clear saved intent and runtime auras before the death screen; the player tick also handles death cleanup. Resurrection resets the per-death cleanup guard but does not reactivate cleared auras. Switching characters never changes another character's intent. AuraSaveTests cover independent characters, town round trips, all enabled effects despite transient reservation limits, fresh runtime ids and death reset. Live lifecycle verification pending.

#### Aura restoration bootstrap correction

A real town/hideout round trip restored only Vitality despite saved Precision and Clarity. The log marked the body armour inactive because Precision needed 22 dexterity while initial pools still contained the class base 14. CharacterStatSources was checking socketed gems against stale/uninitialized pools while constructing those very pools, then disabling the entire item's global modifiers and all its gems.

Item activation now uses only the equipment's own requirements. Gem requirements remain enforced individually at socketing/casting against completed pools; an unavailable gem no longer suppresses its host armour, unrelated gems or their attributes. This removes the circular initialization gate on aura candidates. AuraEquipmentRestoreTests reproduces the fresh-connection equipment case and restores all 17 supported self auras, including 34 distinct source/recipient buff packets after town suppression. Restoration has no aura-count limit.
