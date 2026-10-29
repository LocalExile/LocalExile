# Paladin

Paladin is the Duelist's Phrecia Champion identity (`Champion`, ascendancy row 11, 3.29.3b), start node 24984.
The start is implicit and free and has no stats; its link to 50986 is the ordinary Duelist start, not a class node.
The class is not complete yet. The small nodes, Anointed Champion, the Paladin Aura options, Light of Divinity, Bring
the Battle and Elemental Valiance work; the other notables need link skills, which the engine does not have yet.

## Node checklist

Effect text and numbers are the wiki's (from the game's stat descriptions) and the 3.29.3b PassiveSkills values.

| Node | Effect | Gameplay consumer |
|---|---|---|
| 2134, 49271, 363, 29126, 54067, 43835, 2437, 38874, 11127 | Regenerate 0.5% of Life per second; 5% increased effect of Non-Curse Auras from your Skills | Done. `life_regeneration_rate_per_minute_%` 30 goes into the life regeneration the recovery loop applies (`PanelStats.LifePerMinute`); `non_curse_aura_effect_+%` 5 goes into `Pools.AuraEffectPct`, which scales every running aura's bonuses (`AuraCatalog.Bonus`, `RawStats`, `ResistanceStats`). |
| 52104 Anointed Champion | Consecrated Ground around you while stationary; its effects linger for 4 seconds; 50% increased effect of Consecrated Ground you create | Done; see below. Curse effect on self has no consumer: nothing in the engine curses the player. |
| 13763 Light of Divinity | Nearby (6 m) enemies take 25% increased Elemental Damage (BuffTemplate 672) | Done; see below. Augury of Penitence (2551) on the hostile monsters within 60 cells; their hits and burns read its `elemental_damage_taken_+%` (`MonsterTakesMitigated`, `MonsterBurn`). |
| 40299 Elemental Valiance | 25% chance to trigger level 20 Summon Elemental Relic on your or a nearby ally's kill, or on hitting a Rare or Unique enemy | Done; see below. The player's kills and hits roll the trigger (`TriggerElementalRelicLocked`); the relic is an owned minion (`PlayerMinions`, `PlayerMinionsTick`) whose aura goes into the player's stats and whose death explosion hits monsters. Party allies' kills have no consumer: there is no party play. |
| 46492 Bring the Battle | Warcries have infinite Power; enemies taunted by you take 20% increased damage and deal 20% less damage to other targets | Done; see below. Enduring Cry's power is 999999 (`UseWarcry`); the player's taunted monsters take 20% more of every type. The 20% less damage against other targets has no possible consumer: monsters only hit the player, who is the taunter. |
| 42396 Field Medicine | Magic utility flasks applied to you have 40% increased effect; non-unique utility flasks you use apply to linked targets | Partly: the 40% is read for magic flasks (`UtilityFlasks`); there are no link skills to carry flasks to a target. |
| 40171 Absolution | 40% increased effect of Non-Curse Auras while you have a linked target; curses go to you instead of linked targets | Not implemented yet: no link skills; no curse source exists for the redirect. |
| 34448 Deep Connections | +5 to level of Link Skill Gems; no experience lost when a linked target's death kills you; link skills can target damageable minions | Partly: the +5 reaches the link gems' effective level (`SkillGemLevel`), but link skills have no executor; the engine has no death experience penalty. |
| 44795 Paladin Aura | Choice shell: one paid point, one free option | Done: the allocation and refund rules (`AscendancyProgress`); no stats of its own. |
| 58846 Determination | Grants level 30 Determination (GrantedEffectsPerLevel 17772) | Done; see below. Flat armour and its more armour (`AuraCatalog.Bonus`). |
| 28569 Grace | Grants level 30 Grace (GrantedEffectsPerLevel 17460) | Done; see below. Flat evasion and its more evasion. |
| 39868 Discipline | Grants level 40 Discipline (GrantedEffectsPerLevel 17316) | Done; see below. 356 flat energy shield in the ES pool and 30% faster recharge. |

Three legal eight-point builds, with a swap of the Paladin Aura option to Discipline, reach every node:

```text
363 13763 29126 40299 2437 52104 43835 46492
38874 44795 58846 2134 40171 49271 34448 54067 42396
54067 42396 11127 34448 49271 40171 38874 44795 28569
```

No legal build holds all nine small nodes: three sit behind notables, so eight points reach at most seven
(`363 2437 38874 43835 54067 40171 49271`, 3.5% of Life per second and 35% aura effect).
Refunding a Paladin Aura option costs five refund points; the shell itself refunds for free.

## Anointed Champion

- **Stationary.** The walk has ended (`now >= Walk.ArrivesAt`, the same test as the charge gains), the player is alive
  and not in a town (hideouts are not towns).
  While stationary, one consecrated ground of the stationary radius (18 cells, 1.8 m) stays where he stands, with the
  Sulphur Flask's ground art; it is removed (0x2bb) when he moves or dies, and a new one is made where he stops.
- **Radius.** As the client derives `virtual_consecrated_ground_while_stationary_radius` (0x1428d43a0): the plain stat,
  the Strength variant when Strength is the highest attribute (ties count), the Crusader variant with two or more
  Crusader items; the largest wins. No area modifier applies.
- **Linger.** The ground's effect stays for `your_consecrated_ground_effect_lingers_for_ms_after_leaving_the_area` after
  the last moment any of the player's grounds covered him, so leaving the ground and the ground ending both linger.
  Every ground the engine makes is the player's own (the Sulphur Flask's too). A death ends the linger.
- **Effect.** `consecrated_ground_effect_+%` raises the ground's stats: 5% of life per second becomes 7.5% (300 to
  450 per minute) and the curse effect on self -50% becomes -75% (Path of Building CalcPerform: `5 * effect` and
  `-50 * effect`). The regeneration adds to the small nodes', Fortification's and every other source's.
- **Where it runs.** `FlaskGrounds.State` keeps the radius and linger from the stats last sent to the client
  (`PlayerStatsWithCleared`); `RunPlayerTick` starts the flask-ground ticker when a radius is present, and the ticker
  keeps running while it is. `FlaskGroundsTick` does one 100 ms step. A new area has a fresh state.

Client evidence (decompile dump, checked against the disassembly): the client derives the radius at 0x1428d43a0 and
the linger at 0x142890140 (`0x230d`, plus `0x2461` while affected by Zealotry); both are registered virtual-stat
derivations (0x1400e5c70, 0x1400db900) with no other direct reader in the dump. The client does not predict the
ground or the linger; it shows the server's effect object and the `ground_consecration` buff (208).
`consecrated_ground_effect_+%` (0x23f8) has no direct client reader.
BuffDefinitions row 1262 `consecrated_ground_while_stationary` is invisible and has no stats; it is not sent.

Unverified: whether the game makes the stationary ground in towns (the engine does not, as it runs no auras there),
and whether the real ground disappears at once when the player moves (the linger makes the gameplay the same).

## Paladin Aura

The option's skill is a passive-granted skill (`PassiveGrantedSkills`): PassiveSkills.dat `GrantedEffectsPerLevel` of the
allocated node (`GrantedEffectsPerLevelHardmode` in Ruthless) fixes the skill and its level.
It joins the player's skills after the socketed gems ([11](../protocol/11-skills.md) §13):

- **Skill id.** `GemSkillIds` keys it by a synthetic host (`0xFFFF0000 | node`, socket 0), so the id stays while the
  node stays allocated and the skill bar keeps it; a swapped option's new skill never takes the old one's id at once.
- **Level and stats.** The row's level (30, or 40 for Discipline) with quality 0 and no supports. Gem-level and gem-quality
  bonuses do not apply: it is not a gem. It has no level or attribute requirement (the row's 90 or 100 is a gem's).
- **Cast.** The client casts it as any aura (0x24 on the player): the gem cast path toggles it (`ToggleAura`), reserves
  the row's 50% (Discipline 35%), sends the source and received buffs with the skill-bar association, and runs its
  bonuses through `AuraCatalog`. The nodes' increased aura effect scales them, as for any of the player's auras.
- **Discipline.** Its `base_maximum_energy_shield` is flat energy shield in the pool before the increases (PoB: a BASE
  mod), scaled by aura effect; its `energy_shield_recharge_rate_+%` reaches the recharge (`EnergyShield.RechargePerMinute`).
- **Saves and removal.** The toggle is saved by aura family (`CharacterSaves.ActiveAuras`) and restored on the next
  connection. Refunding, swapping or resetting the option removes the skill: its aura ends and the mana comes back
  (`EndSkillEffects`, `ReconcileOwnAuras`); the saved intent stays, as for a gem that left.
- **One per family.** A Determination gem and the granted Determination cannot both run (the engine's rule for two gems).
  Whether the game lets both reserve is unverified.

Client evidence: the skill record (0x142196ce0) has no item field, so a granted skill's record is the same as a gem
skill's, which the client already lists and binds by id. The aura frame lookup (0x141e11180, checked in the disassembly)
matches only the buff's skill id (+0x48) and action hash (+0x4a). Not checked live yet: that the client shows the
granted skill in the skill picker and does not grey it out below level 90.

## Light of Divinity and Bring the Battle

- **Enemy aura.** Light of Divinity's template 672 (`CustomAscendancyNearbyEnemiesIncreasedElementalDamageTaken`,
  AuraRadius 60, targets `[3]`) puts BuffDefinitions 2551 Augury of Penitence (`elemental_damage_taken_+%` 25) on
  every hostile living monster within 60 cells (6 m) of the living player (`PassiveBuffAuras`, `EnemyDebuffsTick`
  every 100 ms of game time). 0x1c1 has the player as source; 0x1c2 takes it off when the monster leaves the radius or
  dies, the node is refunded or the player dies. Area of effect does not change the radius.
- **Taunt.** Every warcry taunts the hostile monsters within its radius when its cast ends (60 cells grown by area of
  effect, the wiki's 6 m); the Basalt Flask taunts within its 30 cells (`TauntLocked`). A taunt lasts 3 s (wiki Taunt),
  raised by `taunt_duration_+%`, replaces the player's older taunt on that monster and ends when the monster or the
  player dies. It shows BuffDefinitions 408 `taunted` with its time left.
- **Damage taken.** Each monster keeps the increased damage taken the player's debuffs give it
  (`SpawnedMonster.TakenFromPlayer`); its hits (`MonsterTakesMitigated`: the default attack, gem area hits,
  projectiles) and burns (`MonsterBurn`, Righteous Fire) read it. `taunted_enemies_damage_taken_+%` (20) counts on
  every type while the player's taunt lasts, `elemental_damage_taken_+%` on fire, cold and lightning. Both add to the
  monster's own `base_damage_taken_+%` (Path of Building: DamageTaken and ElementalDamageTaken are INC mods), so fire
  on a taunted monster near the player takes 145%. Spell Resistant's less spell damage stays a separate multiplier.
- **Infinite power.** `warcries_have_infinite_power` makes a warcry's power 999999 (PoB CalcPerform's override).
  Enduring Cry near one normal monster then grants the maximum Endurance Charges (the grant of 199999 is clamped) and
  the regeneration of its 25-power cap. The client works warcry power out itself for its display (0x141abd120 reads
  `0x302e`); the stat goes out with the player's stats.
- **No consumer.** `taunted_enemies_damage_+%_final_vs_non_taunt_target`: monsters only ever hit the player, who is
  the taunter, so there is no hit for it to reduce.
  Taunt immunity (`cannot_be_taunted`, map mods) is not modelled, and neither is the warcry-only
  `taunted_enemies_by_warcry_damage_taken_+%` that the client adds for warcry skills (0x1428b0450); nothing in the
  engine grants it.
- **Other warcries.** Infernal, Seismic, Intimidating, Rallying, Ancestral, Battlemage's and General's Cry pay, cool
  down and taunt; their own buffs, exerted attacks and summons are not implemented yet.

Unverified: what BuffTemplates `__col4` means (1 self, 2 allies, 3 enemies is read from the rows only); whether the
game measures "nearby" to the monster's centre, as here, or to its edge; that the client draws both buffs as sent.

## Elemental Valiance

- **Trigger.** 40299 gives BuffTemplates 546 `GuardianAlliesSpawnRelicsForYouOnKill` (AuraRadius 60, targets `[2]`,
  `can_trigger_summon_elemental_relic_on_nearby_ally_kill_or_on_hit_rare_or_unique`) and GrantedEffectsPerLevel 78412,
  Summon Elemental Relic at level 20 (Ruthless: its HardMode row). The skill is triggered and stays off the skill list.
  Each of these rolls the skill's 25% (`skill_triggered_by_nearby_allies_kill_or_hit_rare_unique_%_chance`): a kill by
  the player (default attack, gem area hits, projectiles, Righteous Fire), a kill by his relic within 60 cells of him,
  and a hit with damage on a Rare or Unique monster. A killing hit on a rare rolls twice.
  A success spends the skill's 300 ms cooldown, with cooldown recovery (`SingleUseCooldown`); while it recovers, no
  roll is made.
- **The relic.** A random element, a third each: MonsterVarieties 7185–7187 (`ElementalLivingRelicFire`, `Cold`,
  `Lightning`). Its level is the character's, up to 85 (Ruthless 80, `minion_actor_level_is_user_level_up_to_maximum`).
  Its life is the MinionLife column × 400 (`MonsterCatalog.BaseLife`, the client's rule for IsPlayerMinion types).
  It lives `base_skill_effect_duration` (5 s) with the player's skill duration modifiers. A trigger for an element
  that already has a relic refreshes its duration instead, so there is at most one relic of each element.
  It is created on the player's team (Positioned team 0) next to him and follows him (the monster AI's surround).
- **Its aura.** The Fire Relic casts Anger, the Cold Relic Hatred, the Lightning Relic Wrath (the variety's aura
  effect), at stats between the two gem levels whose PlayerLevelReq bracket the relic's level, in proportion, rounded
  down (`PlayerMinions.AuraStatsAt`). GGG's numbers for a level-85 relic (Anger 182–259, Hatred 43%, Wrath 26–415 and
  23%) are gem levels 27 and 28 halfway; Path of Building takes the lower level.
  The radius is an aura's base 40 cells plus the level's `active_skill_base_radius_+` (59 at gem level 20).
  While the living player is within it, he has the received buff (BuffDefinitions 149 Anger, 150 Hatred, 151 Wrath;
  0x1c1 with the relic as source) and its stats. His own aura effect does not scale it, because it is the relic's skill.
  Only the strongest of one aura applies: his own Anger replaces the relic's when its stats add up to at least as much.
- **Its end.** When its duration ends, the relic dies (0x1b1 life 0) and explodes for 100% of its maximum life
  (`guardian_relic_explode_on_death_for_%_life_as_element_damage`) as its element.
  The explosion hits every hostile living monster within 22 cells of it and is mitigated as any hit (resistances,
  Light of Divinity's and the taunt's increased damage taken, `MonsterTakesMitigated`).
  Its kills give the player their experience and items and count as a nearby ally's kills for the trigger; they give
  no on-kill charges, which are the player's own. Minions leave no corpse, so the body goes (0x2bb) a second later.
- **Cleanup.** The player's death, or losing the node, kills the relics without an explosion.
  An area change or a logout ends them with the connection. The instance never keeps them and nothing saves them.

Client evidence (decompile dump, checked against the disassembly): the client knows a monster as the player's minion
by its own stats `is_player_minion` (0x48a) and `is_semantic_minion` (0x3cf8); the link-target filter 0x141bf8490 reads
both. The Gateway sends them on the relic's Stats component.

Unverified: the explosion radius (the data has no explosion skill for the relic; 22 cells is Path of Building's
Minion Instability radius), the aura base radius (Path of Building's 40 for every aura, not in the data), the
one-second body removal, and that a death or a refund kills the relics without an explosion.
Not modelled: the relic's teleport to the player (RelicTeleport; it walks instead), the explosion's art, its aura's
circle around the relic (only the player's received buff is sent), minion life modifiers, and monsters attacking the
relic (none do, so it ends only by its duration). The client shows no cooldown for the skill, which it does not list.

## Admission

The default Phrecia gate stays closed.
`Game_VERIFIED_PALADIN=1` admits only this identity after the same read-only installed-graph check as Gambler
(`VerifiedGamblerGraph`, ordinary `82D82CD3…` and combined `F2766B5A…`). A client with the original Phrecia graph is
refused before any save. Nothing installs or patches the archive.

With the graph installed, start through `scripts/dev/live.ps1` and run:

```text
/ascendancy phrecia Paladin
/ascendancy_points all
/add_passive 363 13763 29126 40299 2437 52104 43835 46492
/ascendancy
```

Stand still: the consecrated ground appears around the player and the Defence tab shows 9.5% of Life regenerated per
second (four small nodes and 7.5% from the ground). Walk away: the regeneration stays for 4 seconds, then drops to 2%.
Walk up to monsters: within 6 m they show Augury of Penitence, and fire, cold and lightning hits deal 25% more (the
gateway log's hit damage). With an Enduring Cry gem, cry next to one monster: it shows Taunted for 3 seconds, the
player gets three Endurance Charges, and the monster takes 20% more damage until the taunt ends.

For Elemental Valiance, fight with the same build: about one kill in four makes a relic next to the player.
The gateway log shows `Fire Relic summoned at level …` with its aura's stats, and the player shows Anger, Hatred or
Wrath from it. Five seconds later the log shows `Relic … explodes`, and the monsters near it take its life as damage.

For the Paladin Aura, reset and take `/add_passive 38874 44795 58846`: Determination appears on an empty skill-bar slot.
Cast it: half the mana is reserved and the Defence tab's armour rises. Relog: it is still on. Refund 58846 (refund
points) and take 39868: Determination ends, Discipline appears, and casting it adds 374 flat energy shield (356, 5% more from 38874).

Coverage: `AscendancyGatewayIntegrationTests.Paladin`.
