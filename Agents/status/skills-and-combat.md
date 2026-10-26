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
  The rows and the server's hit share `AttackHit`, so the maximum-mana added lightning (Whisperer "we know all")
  appears in the panel the same way.
  Fourteen real-client stat checks verified 56 mana → 3–3 lightning, 1000 → 60–60, 50% increased lightning → 90–90,
  997 → 59–59 at the panel's floor, removal and relog.
  Generic attack increases apply; melee increases follow the actual attack type and active weapon set.
  Native switching between wand and club verified ranged exclusion and melee inclusion of that increase.
  Code: `PanelStats.HandDamage`, Gateway (`CharacterPlayerStats`).
  Docs: [11](../docs/protocol/11-skills.md) §14.
- Accuracy: the player's final rating includes flat/increased passives, weapon-specific stats and the unconditional
  Accuracy Mastery effects; attacks roll PoB's hit chance against the monster's `DefaultMonsterStats.Evasion`, misses
  send no life update, spells always hit; the Offence panel gets `main_hand_accuracy_rating` /
  `off_hand_accuracy_rating` and the Chance to Hit rows at the character's level; `/accuracy [level]`.
  The Mastery's two target-conditional effects are final multipliers applied to the roll's own target
  (`TargetAccuracy`): 40% more accuracy against Unique enemies (`SpawnedMonster.Rarity`) and 50% more against enemies
  in the constant 20-unit close range (2 metres; 10 units per metre), multiplying when both hold and never folded into
  the panel's rating or `/accuracy`'s unconditional rating.
  Gems' attack hits do not roll accuracy yet, so the conditions apply where a roll exists (the default attack).
  Exhibit 122 (graph id 36978, the Chieftain position) grants `global_always_hit` (Stats row 686, key 687 = 1):
  the default attack cannot be evaded, so it skips the accuracy roll entirely and the panel's Chance to Hit
  reference rows (normal and high-dexterity monster) read 100;
  the rating itself, the mastery's target multipliers and the critical chance never change with the flag.
  A dev `/set_stat` global always-hit reaches the same pools' flag and both allocation and dev removals drop it.
  Ten native checks used connected ordinary paths and actual mastery selection requests.
  The panel stayed at 711 while target rolls used 995 against Unique, 1066 in close range and 1493 when both held.
  A far Unique kept only 995; target movement, relog and refund restored the expected conditions and baseline.
  Code: `CharacterStats.cs`, `TargetAccuracy.cs`, `DamageModel.cs`, `MonsterCatalog.cs`, Gateway.
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
  Attack gems scale weapon base and added damage by separate normalized GESSPL factors, with the attack's crit.
  Both raw fields are signed deltas from 10000; a bow arrow has no Fireball impact.
  Automated controls cover positive/negative deltas, unequal factors, missing identity and true zero.
  Integrated native selected Burning Arrow reads damage per use 25.20, APS 10.78/14.35 and DPS 271.66/361.62.
  These are received action stats; automated tests also check the shared combat packet.
  Ordinary bow ground/object attacks now share contact-time release and damage at the actual flight collision
  (#203, automated and native collision/release/pause checks pass).
  Object uses no longer also deal a melee contact hit.
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
  Not implemented: conditional energy-shield suppression modifiers, the other conditional effect stats, monster
  spell hits.
  The character panel gets the chance again within 250 ms of a condition changing (the player tick).
  Code: `CharacterStats.cs`, `DamageModel.cs`, Gateway.
  Docs: [11](../docs/protocol/11-skills.md) §12.
- Behemoth's Incorruptible applies full final armour to chaos hits after resistance and before suppression/Ward/ES.
  Physical-only additional reduction does not apply to chaos; damage over time keeps its existing resistance path.
  A real connected path and Plate Vest produced 616 armour and native 45 damage from an otherwise 100-chaos hit.
  Spell-hit, physical-reduction, paid-refund and relog controls passed.
  Percentage-of-armour variants remain separate work.
  Code: `Program.IncomingArmour.cs`, the monster-hit and development-hit paths.
  Docs: [chaos armour](../docs/protocol/20-damage.md#chaos-armour-from-incorruptible).
- Current Rage accumulates from paid mana costs and supported hit sources, with a shared hit-gain cooldown.
  It has an actual maximum, loss delay/rate and attack damage multiplier; pause suspends timers and death clears it.
  State and the spending remainder survive area connections, while refunded sources stop generating Rage.
  Native Whisperer allocation/casts produced 30 Rage, its native counted buff and a fixed attack's 100 → 130 damage.
  Area transfer, expiry, relog and paid refund were checked; spells retain their own damage.
  Other generation families, per-Rage stat bonuses, Rage costs and Soul Gain Prevention remain unfinished.
  Code: `RageState.cs`, `Program.Rage.cs` and the cost/hit hooks.
  Docs: [current Rage](../docs/protocol/20-damage.md#current-rage-and-attack-damage).
- Ward absorbs a single hit before energy shield, then restores in full after its delay.
  Body armour/evasion conversion, global defence increases and restoration-speed modifiers use the actual equipment.
  Antiquarian checks cover 10/20 Ward, broken/restored native buffs, paid refunds and damage absorption.
  Runic Tablet grants 20 seconds of the native Adrenaline template's damage/speed/physical-reduction modifiers.
  Its expiry stays unchanged across area transfers; active Adrenaline cannot refresh and clears on death.
  Code: `WardState.cs`, `TemporaryPassiveBuffs.cs`, `Program.Ward.cs`, `Program.TemporaryPassives.cs`.
  Docs: [Ward rules and evidence](../docs/protocol/20-damage.md#player-ward-and-timed-adrenaline).
- Movement speed: the passive `base_movement_velocity_+%` is summed into `Pools`, sent as the client's
  `movement_velocity_+%` and used by the server's walk copy (37 × (1 + pct / 100) cells/s);
  applied on login, level, passive and area changes; `/set_speed` adds its percentage on top;
  unconditional passive increases have native coverage.
  Action speed and floors have separate automated coverage below; other more multipliers and the 128 cap remain open.
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
  every class's from its rig .amd), with the server duration adjusted once for cast speed.
  Code: `AnimationContacts.cs`, `scripts/re/anim_contacts.py`.
  Docs: [11](../docs/protocol/11-skills.md) §9.

## Partly working

- Main ascendancy effects execute through the existing combat and skill paths.
  Initial support includes active-gem level/quality, attributes/requirements, ordinary-point grants,
  lucky/unlucky hit and block rolls, triple damage and hit resistance rules.
  The Phrecia family gem-level stats raise their families' gems too (Herald, Vaal, Link, Raise Zombie), classified
  from the client's own gem metadata by `SkillGemFamilies.cs`; twelve native level/refund checks pass.
  Gem changes refresh retained auras and Righteous Fire, including its life and energy-shield coefficients.
  Spell-use, spell-damage and aura restrictions have separate rules; aura suspension preserves enabled intent.
  Real-client checks cover an Aristocrat Fireball cast and native point grants/refunds.
  Controlled Gateway tests cover actual impact targets, aura reservation/effects, restrictions and refund/reload.
  Typed derived damage supports Whisperer's 150% spell-to-attack and Wildspeaker's 150% minion-to-owner transfer.
  Wildspeaker counts supported minion damage per five final Dexterity; generic damage reaches owner spells and RF.
  Steel Willed and Forest Tracking scale attack increases from complete groups of 450 final armour/evasion.
  Fractional transfers join base and added damage before conversion and Rage.
  Sixteen native checks cover real-node allocation, fixed hits reaching enemy Life and paid refunds for four sources.
  Multiplicative action speed, Tailwind effect and Toughened Skin's independent floors execute in attack/cast/walk.
  Timing reuses PR #194's inherent attack bases and PR #195's native spell base plus final cast-speed contract.
  Native Q Fireball keeps 750 ms base and changes from 107 to 133 casts per hundred seconds with the action floor.
  Connected Tailwind gives 12%, or 22% with a separate Harbinger 10%; effect 56 still rounds that total to 22%.
  Native allocation, fractional effect and paid refunds pass; animation duration was not measured by stopwatch.
  Shared eligibility refuses impossible casts before action replacement, movement, echo, costs or cooldown reservation.
  Ground, monster, self and instant handler regressions cover actual level/quality and unchanged prior action state.
  PR #198's shared raw attack packet now supplies selected attack-gem ranges and pre-crit DPS at the emitted APS.
  Creation/refresh, quality conversion, skill-owned damage and active-hand controls pass automated checks.
  Native selected Burning Arrow APS changes 10.78 → 14.35 and its DPS follows the emitted rate.
  That earlier build used the wrong 1.8 scalar; it verifies timing, not corrected 2.8 damage scaling.
  Corrected native damage and complete official DPS remain open.
  Most Phrecia signature mechanics remain absent; selecting a class does not implement its entire tree.
  Code: `PassiveGemModifiers.cs`, `SkillGemFamilies.cs`, `GamblerModifiers.cs`, `SkillRestrictions.cs`, Gateway skill/combat partials.
  Docs: [implemented effects by class](../docs/game/ascendancies.md#what-this-does-not-complete).

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
- Ordinary aura expansion: Determination, Grace, Discipline, Anger, Hatred,
  Wrath, Zealotry, Malevolence and Envy now activate with reservation and paired visual/HUD buffs.
  Armour/evasion, added hit damage, gain-as-cold, spell damage/crit and RF's more DoT are connected.
  Discipline contributes its flat ES to the shared pool/recharge model; conditional effects remain partial.
  Native Paladin Determination and Arakaali Envy checks cover paired buffs, relog and source-node refunds.
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
  Skill records retain GEPL Cooldown as the native base and carry the cooldown count.
  Ordinary one-use spells/attacks now resolve player and skill generic recovery once, and the server keeps remaining
  base-time budget through stat changes and pause.
  Preflight reserves before action/movement/echo side effects; queued effects retain the original action/area ticket.
  Real-client Dash initial +100%/-50% recovery, early raw rejection without movement/echo/mana, and pause checks pass.
  The native positive-rate cache required an explicit cooldown-state refresh after skill-stat changes;
  this preserves remaining/total base time at the send gate without consuming another use.
  Native Dash mid-cooldown recovery changes now pass live: one entry changes rate with base/total/progress preserved,
  early input has no echo/cost, and reuse succeeds after native expiry.
  Wire, active/expired/zero-rate and queued encrypted-send checks also pass.
  Multi-use, duplicate same-effect gems, shared groups, extra uses, bypass, typed/conditional/more recovery and official
  tick equivalence remain outside this slice.
  Bounded manual Enduring Cry separately supports one singleton-group use and warcry-specific recovery.
  Its new cooldown/timing path has automated coverage and awaits the parent native pass described below.
  Skill records carry the GEPL Cooldown and the stats the cooldown count, and the server refuses a skill until its
  cooldown is over.
  Ground-target C→S 0x190 now frames its 12 bytes and routes ordinary Frostblink through mana/cooldown acceptance.
  The ground 0x1ce uses the raw aim; the server mirrors native 32/42-cell distance limits and ends the old walk.
  Placement requires a captured grid and clear terrain, barriers and endpoint clearance.
  Action tokens and pending releases remain unchanged; other location instant movement is withheld.
  Actual Q input and a cooldown retry matched native/server positions, with two 12-mana costs and two confirmations.
  Early retry, noninstant Fireball and unsigned-invalid coordinate requests changed no position or mana.
  Manual native experiments separately verified near/far clamps and the old predicted walk ending after a blink.
  Hovered-monster Frostblink sends 0x191 and now shares movement acceptance, with an object-bound 0x1ce.
  Its size-aware object candidate trace precedes the distance clamp;
  it does not reuse the monster centre as a ground aim.
  Both preflight and the send gate require a living known-size monster with no active walk, and recheck placement.
  Invalid preflight changes no mana/cooldown; later invalidation withholds confirmation under the existing cost policy.
  Automated actual-send tests cover current target/player sampling, target death/walk, pause, transfer, barriers and unequip.
  Four manual native object confirmations verified clear near/far/diagonal/off-axis endpoints.
  A reflected terrain fallback is deliberately rejected.
  Actual hovered Q on a stationary monster emitted 0x191 and one object-bound 0x1ce with flags 0x20.
  From `(280,220)` against `(300,220)`, native/server position became `(317,220)` for one 12-mana cost.
  A framed ground retry at 0.5 seconds changed no mana or position and sent no extra confirmation.
  Combined #197 synchronization retained base 3000 ms, total 3 seconds and rate 1.
  Production framed off-axis/far object requests matched native/server endpoints with one 12-mana cost each.
  Framed moving-target, reflected-terrain, missing-target and noninstant requests changed no mana or position.
  They sent no confirmation, and the invalid-combination control kept native cooldown entries empty.
  Production movement during an active walk has not been measured.
  Frostblink damage, chilled ground, enemy-dependent recovery and shared Blink cooldown groups remain unsupported.
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
  Not modelled: Pride's enemy debuff, Heralds, banners, curse auras,
  Purity ailment immunity, Reservation masteries 1/2/6, special support execution and party/minion auras.
  Life reservation and passive-granted auras use the same pool/effect lifecycle
  ([11](../docs/protocol/11-skills.md) 13).
- **Stats.** The client's stat key is the Stats.dat row + 1 with the vendored schema
  ([11](../docs/protocol/11-skills.md) §8); `/set_stat` takes stat names or keys.
  The player's life, mana, accuracy, armour and evasion come from its class and level, with the attributes' bonuses
  (energy shield, evasion and melee physical damage increases);
  accuracy also takes its flat/increased passives, weapon-specific stats and Accuracy Mastery (rating for the panel,
  the two target-conditional effects at the roll), and the final rating value is
  sent as `main_hand_accuracy_rating` / `off_hand_accuracy_rating` for the Offence panel;
  armour and evasion also take their flat/increased passives and the base equipped armour pieces (body, helmet,
  gloves, boots), while life and mana still ignore passives and items (attack speed is handled below)
  ([11](../docs/protocol/11-skills.md) §4).
- **Attack speed passives.** Generic, melee, one-/two-handed, weapon-family, shield and dual-wield passive attack
  speed is summed into the client's `main_hand_attack_speed_+%`;
  dual wielding adds PoB's 10% more and the server's unarmed attack uses the same final value.
  Attack gems now apply their extracted inherent speed multiplier to total base duration separately from player speed.
  Pacing, contact/release timing, channel intervals and selected APS share the same rounded native base duration.
  Actual native bow input verified ordinary duration 0.714 s and Burning Arrow 1.02 s, with stored APS 1.40 and 0.98.
  Original weapon base 714 ms and player speed 0% stayed unchanged; release/contact timing was not separately measured.
  Catalog, modifier-order, native-field and spell-exclusion tests pass.
  Supports, conditional/per-charge speed, special time overrides and attack-gem damage/DPS remain separate work.
  Selected attack-gem main-hand ranges now share the converted combat packet, including skill effectiveness.
  All twelve ranges clear absent types; pre-crit averages retain fractions and DPS uses the emitted APS value.
  Creation and refresh pass raw character stats and the actual weapon separately from inherited display values.
  Native Burning Arrow showed 9-23 fire/total, physical zero, per-use 16.52 and DPS 16.19 at APS 0.98.
  Added cold damage showed 9-9 cold and 18-32 total; clearing it removed the cold rows.
  Relog retained the base result.
  A noncritical server hit dealt 24 damage, while ordinary attack retained its original ranges and averages.
  Packet, catalog, conversion, source-isolation and native row-format tests also pass.
  Ordinary attacks, spells and off-hand display paths are unchanged.
  Conditional effects, special support execution, crit weighting and broader weapon modifier scope remain separate work.
  The default attack also sends the panel's `hundred_times_damage_per_second` and
  `display_hundred_times_damage_per_skill_use`.
  Live verified: the dual-wield character's APS follows the passives (2.93 → 2.98 after a node).
  Minion, totem and timed/conditional stats are not modelled;
  the character panel's APS stays cached after a passive allocation (reopening the panel or switching its tab does not
  refresh it; only a relog does).
  ([11](../docs/protocol/11-skills.md) §4)
- **Spell cast timing (#192).** Catalog base/virtual cast milliseconds now stay unchanged for native spell actions,
  which apply cast speed themselves.
  Server waits and displayed casts per second share `SpellCastTiming`; attacks keep their existing path.
  The old -80% Fireball recovery capture lasted 18.75 seconds instead of 3.75 due to two divisions by 0.2.
  Automated 0/+100/-80 timing, instant/channel classification, invalid-rate and existing aura/sum checks pass.
  Real-client Q Fireball clips measured 3.750000157 s at -80%, 0.375 s at +100%, and 0.75 s at zero.
  Native virtual base stayed 750 ms; emitted cast rates were 0.27, 2.67 and 1.33 casts/s respectively.
  Projectile release/contact alignment was not separately measured.
  Instant casts remain instant; channel damage cadence and conditional action-speed mechanics are unchanged.
  Numeric server durations below 1 ms are floored, and invalid factors/unrepresentable waits produce no spell effect.
  Code: `SpellCastTiming.cs`, Gateway (`GemSkills`, `CastMs`, `CastGemSkill`, `LaunchProjectile`, `AreaSkillHit`).
  Docs: [11](../docs/protocol/11-skills.md) section 9.
- **Selected spell hit damage panel (#188).** Spell total/per-type ranges and pre-crit average damage/DPS
  now use the same converted packet as projectile and area combat.
  All range fields are explicit, including zeros for absent types; hitless spells clear inherited weapon DPS/per-use.
  Initial player creation and later skill refresh both pass separate character conversion inputs.
  Attack displays, modifier precedence and the existing level-only conversion rule are preserved.
  Automated mixed conversion/gain, added-damage effectiveness, hitless, attack and native row-format checks pass.
  Native selection/getter checks show Fireball 10-15 fire and Freezing Pulse 10-15 cold with a +12% spell-damage wand.
  A conversion/gain control split Fireball into fire and chaos rows and dealt a noncritical 14-damage server hit.
  Scorching Ray cleared inherited hit ranges and weapon DPS, including after added spell damage refreshed its record.
  Ranges use server floors; averages retain packet fractions and exclude crit weighting, target mitigation,
  per-type integer-roll expectation and cooldown/downtime.
  Beam/DoT displays, conditional damage rules and broader conversion semantics remain outside this slice.
  Code: `SpellHit.cs`, `SpellPanelStats.cs`, Gateway (`GemSkills`, `LaunchProjectile`, `AreaSkillHit`).
  Docs: [11](../docs/protocol/11-skills.md) sections 9 and 14.
- **Critical strikes.** The server rolls unconditional crit passives: the equipped main hand's `WeaponTypes.dat`
  critical chance (permyriad) raised by generic/attack/melee/weapon-shape increased crit chance for attacks,
  each spell's extracted base chance, and 150% plus `base_critical_strike_multiplier_+`;
  critical damage is multiplied and logged.
  Unarmed has 0% base crit.
  Two pure base stats replace that base before the increased chance, the way PoB's `WeaponBaseCritChance` replaces
  an attack pass's `source.CritChance` when that hand has a weapon:
  `override_weapon_base_critical_strike_chance` (Stats row 9892, permyriad) on the armed main hand — Slayer
  Overwhelm (graph id 17315) grants 800 = 8.00% and Blind Prophet's Sightless (graph id 24698) grants 900 =
  9.00% — and `base_melee_critical_strike_chance_while_unarmed_%` (Stats row 3906, permyriad; Behemoth Final Blow,
  graph id 10135, grants 1000 = 10.00%) on the unarmed melee attack.
  Each is a base, not a fixed final chance: generic increased crit still applies on top;
  the weapon override stays out of unarmed and spell rolls, and the unarmed base stays out of a weapon hand.
  Only the active weapon set supplies the attack base and its weapon-specific increases.
  Final Blow also grants `unarmed_melee_attack_critical_strike_multiplier_+` (key 21565, +50 points).
  The pools store it separately; only the attack's own melee context receives it.
  Unarmed non-melee attack gems and spells keep the plain multiplier.
  Its `extra_critical_rolls_while_on_low_life` (key 14290) adds a roll at or below the live Low Life threshold.
  The default is 50% inclusive; positive override key 17896 wins over ordinary threshold key 17901.
  Ordinary lucky/unlucky rolls compose with that conditional count.
  Actual native critical hits remove 200 enemy Life unarmed and 150 armed; empty-hand and paid-refund controls pass.
  The native checks do not estimate critical probability; deterministic threshold/roll tests cover it.
  Exhibit 122 (graph id 36978, Chieftain) grants `global_always_hit` (key 687).
  The default attack skips accuracy and the panel's Chance to Hit rows read 100, with rating and crit untouched.
  Native allocation/refund also preserves the node's Dexterity drawback, 14 → 7 → 14.
  Automated checks cover active/inactive/swap/refund, non-melee exclusion, low-life boundaries and always-hit rows.
  `/set_stat` replaces the complete input contribution and rebuilds pools outside the combat lock.
  Twelve native checks verified 830 → 800 → 900, increased chance → 1800, cannot-crit → zero,
  empty-set/unarmed behavior, weapon restoration and relog; spell chance stayed 500.
  Ascendancy allocation remains in the separate lifecycle change.
  Global, virtual-global and attack-specific cannot-crit flags suppress the attack chance.
  `SpellCriticalStrike` reads neither base override.
  The final main-hand and spell crit chance/multiplier stats go to the client for the Offence panel;
  selected spell chance and server projectile/area rolls now share raw player and skill inputs once.
  Precision and Zealotry retain their existing bonuses without counting them twice.
  Attack, pooled spell and actual spell chance use the same integer-permyriad calculation and half-up rounding.
  Complete player increases stay signed `long`; skill increases add before the final clamp, preserving cancellation.
  Lightning overcap contributes exactly once; clipped native raw stats are not reused as combat inputs.
  Both pooled and actual spell chances honor global/virtual cannot-crit flags.
  Five standalone maintainer follow-up native checks cover ordinary and extreme developer critical inputs.
  The combined long-sum, no-crit and overcap integration still needs a fresh native check.
  Actual Spell skill types set the panel's casting flag; attacks and non-spell warcries keep it clear.
  Hitless spells and unsupported action types cannot fall through to weapon damage or attack critical rolls.
  Native panel/getter checks show Fireball 5%, Freezing Pulse and Ice Nova 7.5%, and Arc 6%, including after relog.
  Combined generic/spell bonuses add once; the no-crit flag clears the native chance and hides its row.
  A hitless Scorching Ray cast dealt no weapon damage, while the same connection's Fireball dealt a critical hit.
  Catalog, chance, panel gates, aura sources and hit routing also have automated checks.
  Other timed, enemy-state and added-base crit mechanics are not modelled (low-life conditional crit rolls now
  are, in this entry above).
  `/crit` shows the attack values and a pooled 5%-base spell reference ([11](../docs/protocol/11-skills.md) §5).
- **Overcapped-resistance modifiers.** While the flag stat is set, the uncapped resistance above its maximum is a
  generic increased modifier, exactly PoB's `ResistOverCap` (the maximum capped at 90): +1% increased evasion per 1%
  of cold overcap, +1% increased critical chance per 1% of lightning overcap, additive with the other increases for
  attacks and spells alike.
  The flags are ordinary stats, so any source sets them: the Surfcaster's Sea Legs notable (graph 61696, with its
  +40% cold-and-lightning pair resistance the aggregator now sums into both elements), an item mod, or `/set_stat`.
  An explicit `/set_stat` value replaces its own input (per-type, pair and all-element resistance, and per-type or
  global maximum resistance) instead of adding to the aggregate, and rebuilds the pools; a Kitava change rebuilds them
  again.
  `PoolsFor` derives the increases once per pool build and stores them in the pools, so the monster-attack evade
  rolls, the panel rows and the spell crit consumers read the same numbers; the uncapped resistance values stay
  untouched, and Iron Reflexes' converted evasion keeps the overcapped increase once.
  Automated checks cover below/equal/above cap, the 90 maximum, both penalties, flag removal and override, additive
  behaviour, spell/attack isolation and the Iron Reflexes conversion.
  Thirteen real-client stat checks verified evasion 15 → 22 and spell crit 500 → 725 at 120% resistances,
  raised maxima, replacement of global inputs, both Kitava penalties, flag removal and relog.
  Combined Gateway checks retain the same increase after the active weapon's base override calculation:
  830 base with 45% overcap gives 1204, a 900 override gives 1305, and the inactive weapon supplies no armed base.
  Ascendancy allocation remains part of the separate lifecycle change.
  See [11](../docs/protocol/11-skills.md) §4, §5 and §12.
- **Projectiles.** Moving targets are checked throughout flight by sweeping observed relative movement.
  A monster can change course away from a hit, enter the path later, or intercept before a farther target.
  The client glide is stopped at the contact cell before impact with the creation marker/world-z pair in 0x1b7.
  Cast release, flight and explosion cleanup use game time and wait through pause.
  Area changes remove outstanding visuals before handoff and invalidate flights even if the transfer fails.
  Disconnects cancel outstanding flight tasks.
  Ordinary bows snapshot current position, weapon and offensive inputs after their release wait, retaining the
  original aim; target evasion and impales resolve at the accepted collision's actual send gate.
  An evaded target is skipped once per straight flight, including a later hit within the same swept interval.
  This is a current-wiki-supported policy; official-server evasion continuation and snapshot equivalence remain
  unverified. Already-paused uses are rejected; accepted queued confirmations run outside
  ground input dispatch and can wait for resume or original-token cancellation.
  Actual queued-send tests cover pause, action replacement, death and transfer, plus released arrows surviving
  later actions/player death. Ordinary hits use ranged notifications, preserve existing damage/crit/impale
  approximations and clear impales on death. Native before-controls confirmed the old visual-only ground arrow
  and independent melee contact hit on object use. On the combined runtime, native ground/object right-clicks
  each dealt one hit only at the arrow stop (13 at (296,221), 6 at (296,220)), without melee `0x1af 0x4201`.
  Creation now occurs in the bow clip near contact; frame/server scheduling delay remains visible.
  Native wall-before-object, nearer interceptor, moving-away miss, framed Move before/after release, flight pause,
  release pause and paused raw ground/object dispatch controls passed. Added cold removed after release stayed
  in the arrow's 112-damage killing hit, with one 4-XP award (loot unmeasured).
  Combined-only Frostblink checks passed: actual Q after release preserved the old hit; a timed framed `0x190`
  before release changed the sampled origin while retaining the original aim. Combined recovery controls
  cancelled a pending release, denied recovery-time input and accepted fresh input after expiry; an already
  released arrow still stopped/removed. These checks compose separate Frostblink/recovery contributions.
  Automated trajectory, wall, lifecycle and packet checks pass.
  Live checks verified a moving-away miss, a later-entry hit, first-wall contact, pause/resume and native glide stop.
  Raw fixed-height and terrain-sentinel stop packets preserved their fields and cleared native movement duration.
  Earlier Fireball/raw-stop checks preceded the bow packet/height update; the ordinary bow controls above cover
  its combined path on natural ground. Attack-gem height/collision composition still needs a live recheck.
  Creation, glide endpoint and stop now share one signed marker; packet tests cover positive, negative and zero height.
  Corrected normal launches retained marker 5/world z -39.0625 at creation, paused mid-flight and both contact stops.
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
  Conditional/more/less speed and special projectile support mechanics remain open.
  Code: `ProjectileSpeed.cs`, `ProjectileFlight.cs`, `OrdinaryBowShot.cs`, `Program.Bows.cs`,
  `Program.Combat.cs` (`LaunchProjectile`, `FlyProjectile`), `ClientState.cs`,
  `InstanceProtocol.Teleport`.
  Docs: [11](../docs/protocol/11-skills.md) §10 and [22](../docs/protocol/22-pause.md).
- **Combat.** The player's default attack kills monsters with server-side damage and table-backed monster resistances.
  Monster armour remains unimplemented.
  The player's hit lands at the attack animation's contact keyframe, a monster's still at half its attack time.
  Monsters use their first granted effect and swing back; their attacks roll player evasion and then go through armour
  and block.
  Ordinary physical melee hits can trigger native player TakeHit recovery through 0x1d1.
  Native proof stopped predictive walking and casting, blocked new actions, and cleared the recovery flag afterward.
  Server recovery cancels pending actions and channel hits and stops the walk estimate for the same interval.
  Actual physical monster hits stopped walking and cancelled a slow cast before release in copied-save live checks.
  Released Fireball flight continued, and a stun-immune control took damage without recovery.
  With a bow equipped, a hit cancelled pending Burning Arrow, blocked an ordinary arrow during recovery,
  and allowed a fresh arrow afterward; an already released Burning Arrow kept flying.
  Chance, avoidance, immunity, recovery and delayed object walk-ups have automated checks.
  ES builds, substituted thresholds and unsupported conditional stun defences are skipped conservatively.
  See [11](../docs/protocol/11-skills.md), player hit recovery, for scope and protocol evidence.
  The player's life comes from the class/level/passive pools.
  The server has no simulation loop: hits use timers, attacks are bounded by a 30/s tick (`TickRate`), the AI runs
  every 500 ms.
  Item mods count through the global item stats;
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
  Flat additions and maximum-mana attack lightning execute with explicit attack/melee scaling context.
  The allocated Whisperer path produced native 53/75-damage hits at 745 mana, free casts and restored costs after refund.
  General/attack penetration and critical elemental ignore compose with existing Gambler and nearby effects.
  Seven native damage checks and combined automated cases verify their ordering and source removal.
  Not yet: exposure, conditional added damage and more support modifiers, ailment
  application/duration and monster resistance modifiers ([20](../docs/protocol/20-damage.md)).
  A separate pure ailment calculator retains raw base/added damage, conversion ancestry and scoped modifiers.
  Its critical DoT multiplier and independent ailment range have oracle tests; runtime application is still absent.
  Monster base resistances now come from the target client's MonsterTypes/MonsterResistances tables.
- Continuous life/mana recovery (2026-10-24): regeneration, flask recovery and self RF now share a 16 ms
  elapsed-game-time loop, replacing regeneration's 250 ms jumps; pool fractions and caps are preserved.
  Implemented with regression tests; real-client visual verification after Gateway restart is pending.
  See [20](../docs/protocol/20-damage.md), "Continuous resource updates".
- **Cyclone area of effect (2026-10-25).** `/set_aoe` now resends Cyclone's calculated final radius in the skill
  record, using the same radius calculation for server hits.
  Cyclone/CycloneAltX use a 16-cell base when their exported stat set has none.
  The player's increased area counts once for every area skill (`AreaPlayerStats`); it counted twice before.
  Spell-only area does not affect the attack.
  Tested calculations; live visual check pending.
  The first live check found that direct radius alone did not scale the channelled visual;
  its conditional path now also receives aggregate area (0xd7) and radius percentage (0x1591).
  Cyclone's additional melee-range and stage modifiers remain partial.
  See protocol/11-skills.md.
- Standard Endurance/Frenzy/Power charges now execute accepted hit, kill, block and periodic gains, expiry,
  minimum/maximum limits, per-charge effects and native buff publication.
  Real Whisperer/Polytheist allocation, reload and refund paths have production integration tests.
  Complete damage/critical/mitigation paths, exact-ticket travel and queued-expiry publication have regression checks.
  Twenty-three real-client checks cover both Phrecia paths, native buffs, accepted hit/kill gains, combined critical
  and incoming damage, original transfer deadline, pause, death and reconnect.
  Normal manual Enduring Cry GE268 has bounded paid owner execution with automated and real-client coverage.
  It requires one actual player-native ID in its non-36 group, one stored use and bypass 4.
  Duplicate gems and group peers across default, socketed-item and passive player skills are refused.
  Separate item-mod skill granting remains unimplemented.
  Unsupported cooldown/timing/cost variants reject before echo, cost or cooldown spending.
  Completion reads current live hostile non-owned monsters in the 60-cell base area, using rarity power 1/2/10/20.
  Each whole five power grants one charge through the existing limits, including Ancestral Endurance's +2 maximum.
  Visible buff 1541 supplies owner regeneration and `affected_by_a_warcry` for 2.5/3.4 s at gem levels 1/20.
  Regeneration is 120% life/minute per five power, counted to 25; the ordinary 10 s charge duration is independent.
  Call of the Ancestors' +200% speed/-30% recovery and quality 20's +40% recovery apply once.
  Native base 800 ms remains raw; warcry speed and global action speed stay separate.
  Level 20 plus +200% warcry/+22% action gives raw nonspell rate 423 and a 194 ms server wait.
  The pausable buff ends on death, source removal, transfer invalidation or disconnect, without carry/relog.
  Once paid execution starts, later ordinary action tokens do not cancel it; native contact timing remains unverified.
  Buff/stat/life sends use lifecycle guards and current values at the cipher gate.
  Fourteen real-client checks verify raw base/rate, level 1/20, Call, quality, instant use and group-peer rejection.
  Actual local playback remains 4.225 while the separate global action factor changes 1→1.22.
  Quality 20 gives cooldown recovery 1.4, or 1.1 with Call; refund preserves the spent budget.
  Owner buff effect 100% gives native regeneration 960% life/minute and Life 600→761 in 0.834 s.
  Ally charge/buff sharing, taunt AI, Deathrattle corpses/chests, other warcries and charge consumption remain open.
  See [skills](../docs/protocol/11-skills.md).
  Upstream native checks verified counted/timed charge icons, refresh/expiry, death and sample damage/speed effects.
  Those captures establish the original protocol; the guarded root verification above targets source `a959224`.
  The pinned upstream merge also passes native linked-support, charge and repeated-transfer checks.

## Not working yet

- Projectile spawn height on platforms and doodads: the protocol carries no player z and the server has no
  surface-height model, so a projectile on the hideout waypoint platform spawns at the terrain height below it.
  Tracked in issue #182.
- Effects that should be attached to bones (Righteous Fire's orbs, MiscAnimated RighteousFireOrb) are not sent;
  an effect object follows the player only by 0x1ba glides.
  Combat auras, Heralds and banners are not implemented; only the simple auras above use buffs.
  Ruthless and SSF change nothing in game; towns are shared across leagues.
