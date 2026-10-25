# Status: Skills, auras, stats and combat

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/skills-and-combat.md](../roadmap/skills-and-combat.md).

## Works (verified with the real client)

- Skills and combat: the player has the default attack (PlayerMelee, Characters.dat StartingMeleeSkill) in the Actor
  skill list; it goes on the skill bar, walks into range, swings at the unarmed speed (757 ms with dual wielding's
  10%) or faster with `/set_stat attack_speed_+%` (server-computed final stats, 0x2ba skill update, panel's Attacks
  per Second), and the server lowers the monster's life 2–5 at the contact point; moving cancels;
  the dead monster's life bar hides.
  Code: `GrantedSkill`, `SkillCatalog.cs`, Gateway.
  Docs: [11](../docs/protocol/11-skills.md).
- Impale: a physical hit that rolls `impale_on_hit_%_chance` stores 10% of the hit's physical damage on the monster
  (five stacks, five hits each), and every later physical hit on it takes the stored damage again as extra physical
  before the roll; five stacks add half the impaling hit's damage — the mechanic behind the panel's Chance to Impale
  row, verified live 2026-10-20 (the bonus climbs 0.5 → 2 over five hits and a fresh target reopens at one stack).
  Code: `Impale.cs`, Gateway (`Attack`).
  Docs: [11](../docs/protocol/11-skills.md) §14.
- Offence panel damage rows: the hit the server swings with becomes `main_hand_minimum/maximum_total_damage` and the
  per-type minimum/maximum rows, with `skill_uses_main_hand` so the client shows them;
  whole numbers, main hand only, and an empty packet sends none.
  Code: `PanelStats.HandDamage`, Gateway (`CharacterPlayerStats`).
  Docs: [11](../docs/protocol/11-skills.md) §14.
- Accuracy: the player's final rating includes flat/increased passives, weapon-specific stats and the unconditional
  Accuracy Mastery effects; attacks roll PoB's hit chance against the monster's `DefaultMonsterStats.Evasion`, misses
  send no life update, spells always hit; the Offence panel gets `main_hand_accuracy_rating` /
  `off_hand_accuracy_rating` and the Chance to Hit rows at the character's level; `/accuracy [level]`.
  Code: `CharacterStats.cs`, `DamageModel.cs`, `MonsterCatalog.cs`, Gateway.
  Docs: [11](../docs/protocol/11-skills.md) §4–5.
- Armour and Evasion: flat armour/evasion from the passive tree (`base_physical_damage_reduction_rating`,
  `base_evasion_rating`) and the base body armour/helmet/gloves/boots (the `ArmourTypes.dat` minimum), with
  `physical_damage_reduction_rating_+%`, `evasion_rating_+%`, `evasion_and_physical_damage_reduction_rating_+%` (Cloth
  and Chain), `global_defences_+%` and the combined increase;
  the character panel gets the final `physical_damage_reduction_rating` / `evasion_rating`;
  physical `/hit` and monster melee apply PoB's armour reduction (90% cap), monster attacks use PoE's shared evasion
  entropy counter before damage, and `/defences` shows armour, physical reduction and the chance to evade a reference
  monster.
  Iron Reflexes converts all evasion to armour and drops dexterity's evasion bonus.
  Not yet: item mods/quality, shields or auras.
  Code: `CharacterStats.cs`, `DamageModel.cs`, `ItemCatalog.cs`, Gateway.
  Docs: [11](../docs/protocol/11-skills.md) §4, [13](../docs/protocol/13-items.md) §2.
- Gem skills and bow attacks: Fireball from the socketed gem with its level-1 stats (9–14 fire, 750 ms cast via
  `virtual_base_spell_cast_time_ms`, 5 mana), cast at the ground or at a monster; a bow's ordinary attack and attack
  gems create `Metadata/Projectiles/DefaultArrow` (or the gem's own projectile) at the attack animation's `attack1_bow`
  contact (0.374 of the attack time).
  Attack gems deal the weapon's damage × `DamageEffectiveness` with the attack's crit; a bow arrow has no Fireball
  impact.
  Projectiles spawn at the captured terrain height + `Game_PROJECTILE_HEIGHT` (5 steps = torso) on natural ground;
  without a capture they use the bare offset.
  Code: `SkillCatalog.cs`, Gateway (`CastOn`, `LaunchProjectile`, `AreaSkillHit`), `ProjectileObject`.
  Docs: [11](../docs/protocol/11-skills.md) §9–10.
- Resistances: the passive `base_resist_all_elements_%` / per-element `base_*_damage_resistance_%` are summed from the
  0% base, the two Kitava quest penalties (-30% each) are mirrored from quest flags, and the final value uses Path of
  Building's floor -200 / default max 75 / absolute cap 90 (`DamageModel`);
  used by Righteous Fire's self-burn and `/hit`; `/resistances` shows modifier, penalty and final.
  Code: `CharacterStats.cs`, `DamageModel.cs`, Gateway.
  Docs: [11](../docs/protocol/11-skills.md) §12.
- Spell Suppression: the passive `base_spell_suppression_chance_%` is capped at 100% and a successful suppress takes
  40% less spell damage plus `base_spell_damage_%_suppressed` (PoB `SuppressionChanceCap`/`SuppressionEffect`);
  `spell_suppression_chance_is_lucky` rolls twice and keeps the better roll (effective `1-(1-p)^2`), with an unlucky
  `p^2` dev mode; the conditional chance passives count while their condition holds:
  `spell_suppression_chance_%_while_on_full_life`, `_while_moving` (the walk estimate) and `_if_enemy_hit_recently`
  (an attack or spell hit in the last 4 s); `/suppression`, `/suppression_luck`, `/spellhit`.
  Not implemented: the full energy shield stats (no energy shield model), the other conditional effect stats, monster
  spell hits.
  The character panel gets the chance again within 250 ms of a condition changing (the player tick).
  Code: `CharacterStats.cs`, `DamageModel.cs`, Gateway.
  Docs: [11](../docs/protocol/11-skills.md) §12.
- Movement speed: the passive `base_movement_velocity_+%` is summed into `Pools`, sent as the client's
  `movement_velocity_+%` and used by the server's walk copy (37 × (1 + pct / 100) cells/s);
  applied on login, level, passive and area changes; `/set_speed` adds its percentage on top;
  only unconditional passive increases are modelled (no action speed, more multipliers, gear/buff movement or the 128
  cap yet).
  Code: `CharacterStats.cs`, Gateway (`ApplyMovementSpeed`).
  Docs: [07](../docs/protocol/07-latency-visibility-movement.md) §3.
- Block / Spell Block: passives' `additional_block_%` and `base_spell_block_%` are summed and capped at 75% / 90% (PoB
  `BlockChanceCap`), max-res passives `maximum_block_%` / `base_maximum_spell_block_%` raise the caps;
  a successful block prevents the hit, monster melee and `/blockhit` use it; `/block` shows both chances;
  an equipped shield adds its `ShieldTypes.Block` base and enables the shield-specific passives, staff passives apply
  with a staff, and dual wielding adds its inherent 20% attack block and dual-wield block/spell-block passives;
  Versatile Combatant reduces both maxima by 10% and converts overcapped attack block to spell block at 2% per 1%;
  lucky block uses 1-(1-p)^2 for attack/spell (dev `/block_luck`);
  the character panel gets `uncapped_attack_block_%` and `display_spell_block_chance` so it shows the uncapped value
  in parentheses and the spell block row.
  Code: `CharacterStats.cs`, `DamageModel.cs`, Gateway.
  Docs: [11](../docs/protocol/11-skills.md) §12.
- Character panel sums: about 150 panel stats that sum base forms are computed from passives, items and auras with
  their unconditional combined stats: stun, ailment avoidance, durations and effects (ignite, chill, freeze, shock,
  scorch, brittle, sap), ailment chances, penetration, leech, life/mana/ES on kill, rarity and quantity, cooldown,
  projectile speed, area, curse and flask effect, reflect, charge durations, cannot-be/immune flags (including the
  four damage immunities and physical immunity, from their base forms);
  the main hand / off hand rows add that weapon's local stats;
  projectile block and chaos resistance against damage over time combine the pools with their stat;
  the Defence tab's Life / Mana Regeneration per Second rows are the tick's own per-minute rates
  (`total_*_recovery_per_minute_from_regeneration`).
  The rest of the 387 rows is tracked in issue #75.
  Code: `PanelStats.cs`, Gateway (`CharacterPlayerStats`, `HandLocalStats`).
  Docs: [11](../docs/protocol/11-skills.md) §14.
- Buffs (S→C 0x1c1 add, 0x1c2 remove) and Righteous Fire: cast on oneself, its buffs and the big burning circle (an
  effect object, `Metadata/Effects/PermanentServerEffect` with `rifire.ao`, following the player), 90% of life per
  second nonlethal burn on the player, 70% of life per second to monsters within its radius (18 cells, grown by the
  player's area of effect stats, `SkillArea`; they burn to death), the circle scaled to that radius (MiscAnimated's
  reference radius, the uniform Positioned scale; matches the real game at level 1) and kept on the player every 100
  ms; server-side mana (cost, regeneration); `/god` (also 200% increased area of effect), `/set_aoe`.
  Code: Gateway (`ActivateRighteousFire`, `RunPlayerTick`), `SkillArea.cs`.
  Docs: [11](../docs/protocol/11-skills.md) §12.
- Spells are released at the cast animation's contact keyframe (Witch 599 of 1066 ms;
  every class's from its rig .amd), sped up by cast speed.
  Code: `AnimationContacts.cs`, `scripts/re/anim_contacts.py`.
  Docs: [11](../docs/protocol/11-skills.md) §9.

## Partly working

- Aura restoration bootstrap fix: socketed gem requirements no longer deactivate their entire host item during pool
  initialization.
  Equipment contributes attributes before individual gem validation;
  saved auras remain candidates on town/hideout round trips.
  Regression covers fresh-connection Precision/Clarity equipment and all 17 supported self auras.
  Live retest pending.
- Aura lifecycle: towns suppress all aura effects/reservation while retaining per-character enabled intent.
  Exit/relogin/character return restores available remembered auras without rechecking cast affordability;
  manual activation still validates mana.
  Death clears saved/runtime auras; skill refresh resumes temporarily unavailable effects.
  Automated lifecycle coverage; live verification pending.
  See protocol/11-skills.md.
- Aura presentation (2026-10-25): self auras select their distinct EPKFiles2 art.
  A live check found that source-only buffs hid the top-left icons;
  source and received buff instances now coexist, restore and end together.
  Icon restoration awaits client confirmation.
  See [11](../docs/protocol/11-skills.md) section 13.
- Ordinary aura expansion (2026-10-25, awaiting client confirmation): Determination, Grace, Discipline, Anger, Hatred,
  Wrath, Zealotry, Malevolence and Envy now activate with reservation and paired visual/HUD buffs.
  Armour/evasion, added hit damage, gain-as-cold, spell damage/crit and RF's more DoT are connected.
  Discipline's ES pool/recharge and conditional effects remain partial.
  Reservation overflow is refused.
  See [11](../docs/protocol/11-skills.md) section 13.
- Saved aura toggles (2026-10-25, awaiting client confirmation): active effects persist per character and restore on
  relog, area entry and server restart, subject to usable gems and available reservation.
  Helena's picker now receives all 103 exported personal hideouts, including MTX templates;
  switching templates now uses the native selection request and persists the chosen home.
  See [11](../docs/protocol/11-skills.md) and [21](../docs/protocol/21-hideouts.md).
- **Instant skills and cooldowns (verified live 2026-10-21).** Haste, the Purities and Righteous Fire carry
  `skill_is_instant`, so the client sends them as C→S 0x191 and keeps walking;
  the server applies them and answers S→C 0x1ce, which starts the skill bar's cooldown.
  Skill records carry the GEPL Cooldown and the stats the cooldown count, and the server refuses a skill until its
  cooldown is over.
  The skill bar keeps its slots when skills are re-sent or a gem is socketed again (the saved bar goes out as 0x53)
  ([11](../docs/protocol/11-skills.md) §9, §11).
- **Auras (the simple set is verified live).** Reservation is read from GrantedEffectsPerLevel (`ManaReservationFlat`
  / `ManaReservationPercent`), reduced by the passive/mastery reservation efficiency and sent as the Life pool's
  reserved mana (and in every 0x1b1 mana update).
  Aura skills toggle on cast; Purity of Elements/Fire/Ice/Lightning, Clarity, Vitality, Precision and Haste apply
  their gem's level and quality effects and buff icons; their 3D effects (`affected_player.epk`, `faster_attack.epk`
  …) do not render yet.
  The skill-bar active frame and rotating circle now receive the buff's skill id/action hash association (0x1c1 bit3),
  including area-entry restoration; automated checks pass, live icon verification pending.
  Not modelled: combat auras (Anger/Hatred/Wrath/Zealotry/Pride), Heralds, banners, curse auras,
  Determination/Grace/Discipline (the base armour/evasion model exists, but their buffs and energy shield are not
  applied), Purity ailment immunity, Reservation masteries 1/2/6, supports, party/minion auras and life reservation
  ([11](../docs/protocol/11-skills.md) 13).
- **Stats.** The client's stat key is the Stats.dat row + 1 with the vendored schema
  ([11](../docs/protocol/11-skills.md) §8); `/set_stat` takes stat names or keys.
  The player's life, mana, accuracy, armour and evasion come from its class and level, with the attributes' bonuses
  (energy shield, evasion and melee physical damage increases);
  accuracy also takes its flat/increased passives, weapon-specific stats and Accuracy Mastery, and the final value is
  sent as `main_hand_accuracy_rating` / `off_hand_accuracy_rating` for the Offence panel;
  armour and evasion also take their flat/increased passives and the base equipped armour pieces (body, helmet,
  gloves, boots), while life and mana still ignore passives and items (attack speed is handled below)
  ([11](../docs/protocol/11-skills.md) §4).
- **Attack speed passives.** Generic, melee, one-/two-handed, weapon-family, shield and dual-wield passive attack
  speed is summed into the client's `main_hand_attack_speed_+%`;
  dual wielding adds PoB's 10% more and the server's unarmed attack uses the same final value.
  The default attack also sends the panel's `hundred_times_damage_per_second` and
  `display_hundred_times_damage_per_skill_use`.
  Live verified: the dual-wield character's APS follows the passives (2.93 → 2.98 after a node).
  Minion, totem and timed/conditional stats are not modelled;
  the character panel's APS stays cached after a passive allocation (reopening the panel or switching its tab does not
  refresh it; only a relog does).
  ([11](../docs/protocol/11-skills.md) §4)
- **Critical strikes.** The server rolls unconditional crit passives: the equipped main hand's `WeaponTypes.dat`
  critical chance (permyriad) raised by generic/attack/melee/weapon-shape increased crit chance for attacks,
  each spell's extracted base chance, and 150% plus `base_critical_strike_multiplier_+`;
  critical damage is multiplied and logged.
  Unarmed has 0% base crit.
  The final main-hand and spell crit chance/multiplier stats go to the client for the Offence panel;
  selected spell chance and server projectile/area rolls now share raw player and skill inputs once.
  Precision and Zealotry retain their existing bonuses without counting them twice.
  Actual Spell skill types set the panel's casting flag; attacks and non-spell warcries keep it clear.
  Hitless spells and unsupported action types cannot fall through to weapon damage or attack critical rolls.
  Native panel/getter checks show Fireball 5%, Freezing Pulse and Ice Nova 7.5%, and Arc 6%, including after relog.
  Combined generic/spell bonuses add once; the no-crit flag clears the native chance and hides its row.
  A hitless Scorching Ray cast dealt no weapon damage, while the same connection's Fireball dealt a critical hit.
  Catalog, chance, panel gates, aura sources and hit routing also have automated checks.
  Timed, charge, enemy-state, lucky/more and added-base crit mechanics are not modelled.
  `/crit` shows the attack values and a pooled 5%-base spell reference ([11](../docs/protocol/11-skills.md) §5).
- **Projectiles.** Moving targets are checked throughout flight by sweeping observed relative movement.
  A monster can change course away from a hit, enter the path later, or intercept before a farther target.
  The client glide is stopped at the contact cell before impact with a height-preserving 0x1b7.
  Cast release, flight and explosion cleanup use game time and wait through pause.
  Area changes remove outstanding visuals before handoff and invalidate flights even if the transfer fails.
  Disconnects cancel outstanding flight tasks.
  Automated trajectory, wall, lifecycle and packet checks pass.
  Live checks verified a moving-away miss, a later-entry hit, first-wall contact, pause/resume and native glide stop.
  Raw fixed-height and terrain-sentinel stop packets preserved their fields and cleared native movement duration.
  These live collision and raw-stop checks preceded the bow packet/height update; merged bow/collision
  integration still needs a live recheck. Fixed-height stops preserve the new creation marker/world-z pair.
  Broader surface-height accuracy remains open (#182).
  Positions remain integer server walk estimates sampled about every 16 ms, with a shared collision radius
  and straight movement between samples.
  Sharp turns, scheduling stalls and client interpolation can still differ; official timing equivalence is unverified.
  Static walls use the captured two-dimensional projectile grid with diagonal-corner traversal and endpoint checks.
  Without a height grid, flight uses the bare hand offset; platforms and doodads need a surface-height model (#182).
  Height capture still uses `scripts/re/frida/capture-terrain-tiles.js` and `scripts/walkmaps/heights.py`.
  Doodad/object colliders, explosion area damage and pierce/chains remain unimplemented.
  Ordinary player and skill projectile-speed increases/reductions now share the flight and predicted-arrival speed.
  The player's computed total is counted once; the skill's own level/quality bonus is added separately.
  The development speed override retains priority.
  Live Fireball casts over the same 116-cell path took 1.212 seconds at baseline, 0.606 at +100% and 2.425 at -50%.
  Conditional/more/less speed and support-gem application remain open.
  Code: `ProjectileSpeed.cs`, `ProjectileFlight.cs`, `Program.Combat.cs` (`LaunchProjectile`, `FlyProjectile`), `ClientState.cs`,
  `InstanceProtocol.Teleport`.
  Docs: [11](../docs/protocol/11-skills.md) §10 and [22](../docs/protocol/22-pause.md).
- **Combat.** The player's default attack kills monsters with server-side damage (no monster armour or resistances
  yet; the player's hit lands at the attack animation's contact keyframe, a monster's still at half its attack time).
  Monsters use their first granted effect and swing back; their attacks roll player evasion and then go through armour
  and block.
  The player's life comes from the class/level/passive pools.
  The server has no simulation loop: hits use timers, attacks are bounded by a 30/s tick (`TickRate`), the AI runs
  every 500 ms.
  Item mods count through the global item stats; "emerge" monsters stay hidden;
  346 varieties with NPC / ControlZone / ... components are refused.
- **Damage model (foundation, with armour/evasion).** A hit is a per-type `DamagePacket`;
  `DamageConversion` handles the game's physical, lightning, cold, fire, chaos order, chained conversion, skill/global
  precedence, the 100% cap and gain-as-extra.
  DoT is a single type with fractional tick carry, and `Ailments` has the ignite/bleed/poison base formulas.
  The default attack, Fireball and monster melee build packets and read the player's/skill's conversion stats;
  packet mitigation applies each type's own resistance, and the physical part then goes through armour.
  Righteous Fire's burn uses the DoT tick model.
  A damage-type immunity (`*_immunity`, `elemental_damage_immunity`) zeroes that type on the player's hits, from the
  same sources the panel's Immune rows use (`DamageImmunities.cs`).
  Ordinary player energy shield derives from equipment, intelligence and passives.
  It absorbs non-chaos hits before life and recovers through regeneration and delayed, damage-interrupted recharge.
  The existing 16 ms elapsed-time loop supplies the client's native ES recovery prediction.
  Live checks covered absorption, regeneration, interrupted recharge and chaos bypass.
  Special ES keystones, leech/recoup and conditional substitutions remain unimplemented.
  See [ES scope](../docs/protocol/20-damage.md#player-energy-shield-2026-10-25).
  Not yet: penetration/exposure, added damage, increased/more support modifiers, ailment
  application/duration and monster resistances ([20](../docs/protocol/20-damage.md)).
- Continuous life/mana recovery (2026-10-24): regeneration, flask recovery and self RF now share a 16 ms
  elapsed-game-time loop, replacing regeneration's 250 ms jumps; pool fractions and caps are preserved.
  Implemented with regression tests; real-client visual verification after Gateway restart is pending.
  See [20](../docs/protocol/20-damage.md), "Continuous resource updates".
- **Cyclone area of effect (2026-10-25).** `/set_aoe` now resends Cyclone's calculated final radius in the skill
  record, using the same radius calculation for server hits.
  Cyclone/CycloneAltX use a 16-cell base when their exported stat set has none.
  Spell-only area does not affect the attack.
  Tested calculations; live visual check pending.
  The first live check found that direct radius alone did not scale the channelled visual;
  its conditional path now also receives aggregate area (0xd7) and radius percentage (0x1591).
  Cyclone's additional melee-range and stage modifiers remain partial.
  See protocol/11-skills.md.
- Standard maximum-charge rows now include unconditional maximum Endurance/Frenzy/Power charge bonuses.
  Current-charge mechanics, conditional bonuses and replacement rules remain unimplemented.
  See [skills](../docs/protocol/11-skills.md).

## Not working yet

- Projectile spawn height on platforms and doodads: the protocol carries no player z and the server has no
  surface-height model, so a projectile on the hideout waypoint platform spawns at the terrain height below it.
  Tracked in issue #182.
- Effects that should be attached to bones (Righteous Fire's orbs, MiscAnimated RighteousFireOrb) are not sent;
  an effect object follows the player only by 0x1ba glides.
  Combat auras, Heralds and banners are not implemented; only the simple auras above use buffs.
  Ruthless and SSF change nothing in game; towns are shared across leagues.
