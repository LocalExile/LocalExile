# Status: Skills, auras, stats and combat

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/skills-and-combat.md](../roadmap/skills-and-combat.md).

## Works (verified with the real client)

- Storm Call: each landing sets a marker that strikes when the duration ends and calls down the others.
  Docs: [11](../docs/protocol/11-skills.md) §9a.
- Holy Sweep's art: base `skill_art_variation` 2 plus the worn effect's value.
  Docs: [23](../docs/protocol/23-microtransactions.md) §3.
- Holy Sweep's area: its swings hit around the player. Docs: [11](../docs/protocol/11-skills.md) §9a.
- Flicker Strike: the server teleports the player to the target, with its charge speed and buff.
  Docs: [11](../docs/protocol/11-skills.md) §9a.
- Cooldown bypass: a use during the cooldown spends a charge. Docs: [11](../docs/protocol/11-skills.md) §9.
- Fortification from melee hits. Docs: [Fortification](../docs/game/fortification.md).
- Shield skills beside a one-handed weapon (`is_dual_wielding`), and Shield Charge's charge and shield damage.
  Docs: [11](../docs/protocol/11-skills.md) §10a and §10k, [13](../docs/protocol/13-items.md).
- Antiquarian's nodes. Docs: [Antiquarian](../docs/game/antiquarian.md).
- Conditional critical policy (#242): signed clamp, luck, unarmed multiplier and one roll per gem use.
  Docs: [11](../docs/protocol/11-skills.md), conditional critical policy.
- Generic Rage (#281). Docs: [26](../docs/protocol/26-rage.md).
- Mana per final Strength (#287). Docs: [11](../docs/protocol/11-skills.md) §4.
- Player Actor scale (#299). Docs: [04](../docs/protocol/04-player-entity.md#41a-player-actor-scale-299).
- Armour against chaos hits (#288). Docs: [20](../docs/protocol/20-damage.md) §2e.
- Increased attributes (#285). Docs: [11](../docs/protocol/11-skills.md) §4, [13](../docs/protocol/13-items.md) §2d.
- Power of Purpose (#291). Docs: [Power of Purpose](../docs/game/power-of-purpose.md).
- The default attack, and weapons whose requirements are not met leaving the hands.
  Docs: [11](../docs/protocol/11-skills.md) §4 and §10a.
- Combat timing: stationary attacks, wand bolts, monster flinches and the monster planner.
  Docs: [11](../docs/protocol/11-skills.md) §4–6.
- Impale. Docs: [20](../docs/protocol/20-damage.md) §3a, [11](../docs/protocol/11-skills.md) §14.
- Offence panel damage rows from the hit the server swings with. Docs: [11](../docs/protocol/11-skills.md) §14.
- Whisperer "your mind is destroyed": spell increases also scale attacks.
- Storm Call (live on 3.29.3c): each landing (aim, Spell Cascade area, Spell Echo repeat) sets a marker that strikes
  its area when the skill duration ends, and that strike also calls down every other marker out; quality's chance
  strikes a new marker on cast. Marker and impact are server art from the worn skin (all six skins checked).
  Code: `StormCall.cs`, `StormCallMarkers.cs`.
- Holy Sweep's art: the skill sends its base `skill_art_variation` 2 and a worn effect's value adds to it, so hits no
  longer crash the client drawing the hammers (live 2026-11-08 in a map: none, Originator's, Awakener's, Abyssal).
  Code: `SkillArtBase.cs`. Docs: [23](../docs/protocol/23-microtransactions.md) §3.
- Holy Sweep's area: its swings (ActionTypes Sweep, SweepAltX) hit around the player, where the client draws them,
  instead of around the aimed monster (live 2026-11-08); other melee area attacks still land on the target.
  Code: `MeleeAreaCentre.cs`, `AreaSkillHit`. Docs: [11](../docs/protocol/11-skills.md) §9a.
- Flicker Strike (live 2026-11-09): the server teleports the player (S→C 0x1b7), which the client's action never does,
  to the clicked monster in range or else the one nearest the aim. With none in range the server refuses the use
  (no cost, cooldown use or charge) and gives the client its mana back, as in PoE; the client's action itself would
  strike in place, and sends such uses for monsters it has not yet seen die.
  Its 10% attack speed per Frenzy Charge counts, and each use gives its buff (20% movement speed for the skill's 3 s).
  Code: `FlickerStrike.cs`. Docs: [11](../docs/protocol/11-skills.md) §9a.
- Cooldown bypass (live 2026-11-09: Flicker Strike with Frenzy Charges): the skill record carries GEPL
  `CooldownBypassType`, and a use during the cooldown spends a charge above the minimum.
  A cooldown skill's use pays its cost when it is accepted, with its cooldown use or charge, not when its effect starts.
  Code: `CooldownBypass.cs`, `Cooldowns.cs`. Docs: [11](../docs/protocol/11-skills.md) §9.
- Fortification: qualifying melee hits build temporary protection through an explicit LocalExile generation model.
  Native checks confirmed the buff component, expiry, 20/30-stack limits, attack speed and actual hit protection.
  Elapsed Life regeneration, stopping at expiry, area carry, death and relog were also verified.
  The evidence uses a new unascended character with raw inputs; it does not establish Behemoth selection.
  See [Fortification](../docs/game/fortification.md) for the formula, source coverage and remaining extensions.
- Shield-only skills beside a one-handed weapon (verified live 2026-11-01: Shield Charge with a sceptre and a shield).
  The client greys out a skill when `is_dual_wielding` is set and the main hand does not fit it, and it never derives
  that stat from network stats: unsent, it read 1 for every character.
  The Gateway now sends `is_dual_wielding` with both hands' native weapon types (`WeaponTypeStats`), and the skill
  became usable.
  Shield Charge's charge (verified live 2026-11-01): it ends and plays its end effect (the shield's off-hand attack time
  from the gem's `AttackTime`), runs at its own speed (`movement_velocity_+%` 22 → 112 → 22 while charging), and hits
  along its path (within 8 cells about every 100 ms, each monster once) and where it ends (within 16 cells).
  Its hits and Offence tab use the shield's damage (the gem's added damage plus its damage per 15 shield armour and
  evasion, at the gem's attack time), up to 100% more by distance charged, the charge capped at 75 cells (verified live
  2026-11-01). The stun scaling and `shield_charge_extra_distance` are not applied.
  Code: `WeaponTypeStats.cs`, `Skills.cs` (`FinalPlayerStatsFrom`, `GemSkills`), `ShieldCharge.cs`, `SkillCatalog.cs`.
  Docs: [11](../docs/protocol/11-skills.md) §10a and §10k, [13](../docs/protocol/13-items.md) "Client weapon-type stats".
- Antiquarian's real selected nodes now drive Ward absorption/restoration and Runic Tablet's pausable Adrenaline.
  Goliath's zero Intelligence reaches requirements and pools; Whaletooth extends actual Enduring Cry recovery.
  Turtle Shell integrates 8% Life over one second after accepted melee stun; Bloodnotch restores a share of hit damage
  immediately on that stun. Melding of the Flesh shares the highest elemental resistance cap in pools and mitigation.
  Lioneye's Hair bypasses supported attack accuracy, Bow scales actual travelled projectiles, and Shield replaces
  usable-shield base block and reduces physical ranged hits after armour.
  Three native builds cover all nodes, combat controls, paid refunds and effect/save lifecycles.
  See [Antiquarian](../docs/game/antiquarian.md) for evidence, exact graph admission and remaining engine boundaries.
- Conditional critical policy (#242): ordinary, low-life, Focus and Sharp and Brittle sources share a signed clamp.
  Opposing luck cancels, and multiple lucky sources still add at most one critical draw.
  The unarmed multiplier applies only to the active empty-hand melee attack, with matching panel values.
  Fifteen native checks verified real 200/150 critical hits, active-set switching and bonus removal.
  A 66-Life Marauder verified above, exactly at and below the inclusive 50% boundary.
  Native virtuals confirmed cancellation, clamping, unlucky state, cannot-crit, healing, death and relog.
  Native evidence targets `ea24316`; the resolved `effb797` composition has automated coverage and awaits native reruns.
  The checks use raw inputs on an unascended character; spell/bow contact rules and RNG order have automated coverage.
  A parsed 200-damage critical hit against 400 Life generates 14 Fortification from the same resolved multiplier.
  Removing the unarmed bonus gives 150 damage and 12 stacks; cannot-crit gives 100 damage and 10 stacks.
  The issue #75 review now shares one critical roll across a gem use's area targets, volley, pierce/return hits
  and existing impact callbacks. Gem luck is captured at admitted use; each hit samples current chance/prohibition
  and confirms attack accuracy independently. Twenty-two new cases and four updated parsed use-time-luck controls
  cover the changed lifecycle. This correction is automated, not part of the older native captures.
  Ordinary melee/object-bow now capture luck at swing admission after approach, and ground bows at their input
  admission. Twenty added controls cover Life/Focus/raw changes before confirmation, plus walk-in timing;
  eight existing signed-source/released-prohibition cases now assert admission luck. The other ordinary-bow
  snapshots, special repeats and native reruns remain open.
  Successful paid ascendancy refunds and Focus/curse lifecycles remain separate integration work.
  See [11-skills.md](../docs/protocol/11-skills.md), conditional critical policy.

- Generic Rage (#281): source-qualified attack/melee/critical and incoming gains share a half-second interval.
  Maximum, effect, separate delay recovery/loss speed, prevention and final attack multipliers are consumed.
  Seventeen native checks verify raw-source generation, installed buff/count, actual damage and unchanged speed.
  Fixed 100 damage at 15 Rage displays and deals 115; prevention, decay, pause, travel, death and relog pass.
  The checks use an unascended Witch; Behemoth selection and Vaal consumers remain separate.
  Supported contacts, area-sweep deduplication, queued refreshes and the 140-damage/11-Fortification composition
  have automated coverage; further native composition checks remain in [26-rage.md](../docs/protocol/26-rage.md).
  Capped attempted gains and frozen destination-loading time use explicit LocalExile policy v1.

- Mana per final Strength (#287): `X_mana_per_4_strength` joins the ordinary flat mana bucket before increases.
  Thirteen native controls verify complete four-Strength groups, signed inputs, source removal and relog.
  A fresh Witch's 56 mana becomes 80 at 14/15 Strength, 88 at 16 Strength and 110 with 25% increased mana.
  Maximum-mana attack lightning follows the final pool; display-only Strength overrides cannot feed it back.
  Idle aura reservation and current-mana clamps have decrypted command-wire coverage, including the send gate.
  Historical native controls use raw inputs and synthetic gear; Phrecia selection remains gated.
  The full Mental Conditioning node now composes with Power of Purpose in automated tests; native reruns remain pending.
  See [11](../docs/protocol/11-skills.md) section 4.

- Player Actor scale (#299): passive, active item, aura, flask and cosmetic size inputs share final1973.
  Metadata limit100 adds before ordinary limits, then explicit development replacements, including zero.
  Eighteen native checks measured real Positioned and interpolated Render scale on a new unascended Witch.
  Item20 plus cosmetic20 plus cosmetic3 produces scale1.43; removing sources restores1.0.
  Area entry, relog, death/revival, the default cap and explicit zero overrides were verified.
  Raw720 alone leaves the native model unchanged; server-derived final1973 drives its scale observer.
  Native object size, type scale and stretches remain unchanged; geometry consumers have automated coverage.
  Current/late peer change, reset, creation and revival have loopback-wire coverage, not a second-client visual check.
  Phrecia admission, Rampage and the separate native melee-range flag9807 remain separate work.
  See [04](../docs/protocol/04-player-entity.md#41a-player-actor-scale-299) for formula and evidence limits.

- Armour against chaos hits (#288): the boolean `base_armour_applies_to_chaos_damage` enables the existing armour
  model after chaos resistance, with the ordinary 90% limit.
  Eleven native controls verify actual player Life loss, source toggles, resistance ordering and relog.
  Extra physical/elemental reductions stay with their own types; the native final Armour rating is unchanged.
  Mixed monster packets, block, suppression, ES, Fortification and DoT separation have automated coverage.
  Native controls use a raw source on an unascended Marauder; Incorruptible selection and partial armour
  percentages remain separate.
  Recent-hit doubling composes in automated tests and fresh ordered joint native controls at `c9cd1864`.
  See [20](../docs/protocol/20-damage.md) section 2e.

- Increased attributes (#285): typed and all-attribute percentages scale the complete class/passive/active-gear
  flat bucket once, using the documented PoB rounding model.
  Historical evidence at `60044ad` has thirteen native controls for 3% Strength, additive increases, derived pools and
  matching requirement values on an unascended Marauder.
  A Plate Vest stops contributing below its requirement and resumes when the final attributes recover.
  Calculated zero attributes stay zero; source clearing and relog restore the expected base values.
  Own-item percentage retention, cascading activation, passive refunds and weapon swaps have automated coverage.
  Current-main composition with mana per final Strength, inactive weapons and raw zero/off overrides has regressions.
  Eleven current-main native controls verify final Strength groups, additive increases, mana and equipment requirements.
  Maximum mana follows 47 to 111, then 127/145 as attributes rise; 100% mana increases it to 290 once.
  Source removal and the ordinary vest's requirement disable/restore pass at rebased source `00b246f2`.
  Ordered joint native validation at `c9cd1864` also verifies final attributes feeding mana-to-armour conversion.
  Odd final Intelligence keeps its half mana until converted pools round; 16 Strength / 7 Intelligence gives 15 mana.
  Inactive-weapon, cascading-equipment, passive-refund and active-aura composition remain automated-only.
  The evidence does not establish official-server fractional parity or enable Phrecia class selection.
  See [11](../docs/protocol/11-skills.md) section 4 and [13](../docs/protocol/13-items.md) section 2d.

- Power of Purpose (#291): raw `keystone_mental_conditioning` converts 80% of flat mana to twice as much armour.
  Mana and armour increases add for the converted contribution, under the documented PoB calculation model.
  Thirteen historical native controls at original head `7678d60` covered toggles, increases, rounding and relog.
  Those controls exercised actual Steel Willed attack damage and remaining-mana lightning on an unascended Witch.
  Current-main tests compose mana per final Strength, active equipment, increases, chaos-armour/CI and Mageblood.
  The rebase onto `939b44fe` adds increased-attribute and complete command-snapshot regressions.
  Final scaled Strength/Intelligence, odd half mana, independent zero/off overrides and main's bounds compose there.
  Reservation, clamps, zero/off/removal, Iron Reflexes, MORE and own-aura send-gate freshness have automated coverage.
  Historical joint validation at `c9cd1864` passed 28 native controls, including eight final-attribute conversion cases.
  B=127 gives 25 mana / 222 Armour; 100% mana gives 51 / 425; odd final Intelligence retains its half mana.
  Remaining native cases include reservation, Iron Reflexes, MORE and the full node's independent consumers.
  The candidate on `939b44fe` has no new native acceptance; the earlier controls remain historical evidence.
  Phrecia selection remains gated.
  See [the model and scope](../docs/game/power-of-purpose.md).

- Skills and combat: the player has the default attack (PlayerMelee, Characters.dat StartingMeleeSkill) in the Actor
  skill list; it goes on the skill bar, walks into range, swings at the unarmed speed (757 ms with dual wielding's
  10%) or faster with `/set_stat attack_speed_+%` (server-computed final stats, 0x2bc skill update, panel's Attacks
  per Second), and the server lowers the monster's life 2–5 at the contact point; moving cancels;
  the dead monster's life bar hides.
  A weapon whose requirements are not met leaves the hands: the model sends its slot empty, so the client greys out
  the skills its restriction needs and the default attack punches; the server refuses such a skill too and re-sends
  the model when qualification changes without an equipment move. Automated only; awaiting a native check.
  Code: `GrantedSkill`, `SkillCatalog.cs`, Gateway.
  Docs: [11](../docs/protocol/11-skills.md) §10a.
- Local combat package: stationary default melee attacks no longer take the 150 ms walk-in offset.
  Native evidence in this entry belongs to LocalExile; the standalone destination port has automated validation
  and still needs a native rerun, as recorded in the [port record](../docs/dev/ranged-monster-actions-port.md).
  An in-range unarmed attack had no approach and its authoritative hit arrived 312 ms after the action echo
  (757 ms attack, attack1 contact baseline); positive walk-ins retain compensation.
  Ordinary ground/object wand attacks create native WandProjectile bolts, apply damage at the first actual collision,
  and play the native 500 ms impact clip before removal.
  Class ACT pools and the native action-seeded selector choose the correct main-hand clip and release contact.
  Sixteen ground RMB inputs matched native Witch clips 31/36/39/42; object sequence 4113 matched clip 39.
  Release was 6.1 to 31.4 ms after the selected contact in 15 ground samples; the first cold sample was 55.6 ms late.
  Enemy flinches now cancel movement and ordinary pending swings, hold AI for the requested native TakeHit recovery,
  and invalidate old queued contacts permanently. Moving-Drowned native samples held one cell through recovery;
  AI movement resumed afterward. The original rig-only hurt animation did not stop locomotion; projectile hits
  (`CommitProjectileHit`) send the same native recovery instead of that clip.
  The faster monster planner issued its first chase after 38 to 50 ms in native controls, without duplicate orders.
  Turn tracking, full 2.4 s Drowned attack pacing and actual Escape-menu pause/resume were also verified.
  Full off-hand timing/damage and nonzero intrinsic attack repeats are not implemented.
  Code: `Program.Combat.cs`, `Program.RangedAttacks.cs`, `AnimationContacts.cs`, `AnimationVariantSelector.cs`,
  `Program.Monsters.cs`, `HitReaction.cs`.
  Evidence: `artifacts/combat-validation/`, `artifacts/wand-ai-validation/wand-native-summary.json`
  and `wand-after-object.json`; docs: [11](../docs/protocol/11-skills.md).
- Impale: ordinary melee/bow and supported projectile/area hits store the actual pre-mitigation physical roll,
  including the source critical multiplier and impale effect. Each instance lasts five later hits/eight seconds
  with captured modifiers; any accepted hit can trigger its separate reflected damage, without a second crit,
  spell-only or return multiplier. No artificial five-stack cap or clear-on-death remains. Target avoidance,
  local/attack/spell scope, real Impale Support and all three selected chance aliases have automated coverage.
  Historical 2026-10-20 native observations used the old maximum-roll implementation; current native checks,
  conditional sources and advanced application/consumption rules remain open.
  Code: `Impale.cs`, `ImpaleStats.cs`, `Program.Impale.cs`, `Program.Combat.cs`, `OrdinaryRangedShot.cs`.
  Docs: [20](../docs/protocol/20-damage.md) §3a and [11](../docs/protocol/11-skills.md) §14.
- Offence panel damage rows: the hit the server swings with becomes `main_hand_minimum/maximum_total_damage` and the
  per-type minimum/maximum rows, with `skill_uses_main_hand` so the client shows them;
  whole numbers, main hand only, and an empty packet sends none.
  The rows and the server's hit share `AttackHit`, so the maximum-mana added lightning (Whisperer "we know all")
  appears in the panel the same way.
  Fourteen real-client stat checks verified 56 mana → 3–3 lightning, 1000 → 60–60, 50% increased lightning → 90–90,
  997 → 59–59 at the panel's floor, removal and relog.
  Generic attack increases apply; melee increases follow the actual attack type and active weapon set.
  Native switching between wand and club verified ranged exclusion and melee inclusion of that increase.
  These native checks belong to original head `378ffde`.
  The resolved supported-skill path has automated context, effectiveness and spell-isolation checks.
  Recheck that composition natively.
  Code: `PanelStats.HandDamage`, Gateway (`CharacterPlayerStats`).
  Docs: [11](../docs/protocol/11-skills.md) §14.
- Whisperer "your mind is destroyed": resolved spell increases/reductions also scale attacks at 150% of their value.
  Weapon, added-damage and supported attack constructors share the fractional additive contribution.
  Native panel and target-life checks verify 100 to 250, additive 230, a half-percent endpoint of 203 and reductions.
  Removal and relog clear the effect; ordinary bows retain release snapshots and supported spells remain isolated.
  The native check uses development stat inputs; Phrecia node selection and the rest of Whisperer remain separate.
  These native results are historical raw-stat checks; expanded Arcane Might coverage is listed under Partly working.
  Code: `ArcaneMight`, `DamageStats`, `SkillCatalog.Unarmed.HitDamage`, `SupportedAttackDamage`.
  Docs: [20](../docs/protocol/20-damage.md) §2c.
- Behemoth Steel Willed: attack damage per 450 final armour. Docs: [20](../docs/protocol/20-damage.md) §2d.
- Steel Willed incoming-hit armour. Docs: [Steel Willed armour](../docs/game/steel-willed-armour.md).
- Accuracy, the Accuracy Mastery's target conditions and attack evasion.
  Docs: [11](../docs/protocol/11-skills.md) §4–5, [20](../docs/protocol/20-damage.md) §2h.
- Armour, Evasion and Energy Shield from passives and equipment, Iron Reflexes and Chaos Inoculation.
  Docs: [11](../docs/protocol/11-skills.md) §4, [13](../docs/protocol/13-items.md) §2.
- Gem skills, their projectiles and triggered releases.
  Docs: [11](../docs/protocol/11-skills.md) §9–10, [25](../docs/protocol/25-gem-supports.md).
- Resistances, with the Kitava penalties. Docs: [11](../docs/protocol/11-skills.md) §12.
- Spell Suppression. Docs: [11](../docs/protocol/11-skills.md) §12.
- Movement speed and action speed. Docs: [07](../docs/protocol/07-latency-visibility-movement.md) §3.
- Block and Spell Block. Docs: [11](../docs/protocol/11-skills.md) §12.
- Endurance, Frenzy, Power, converted and Blood Charges. Docs: [11](../docs/protocol/11-skills.md) §15.
- Discharge and Discharge of Misery. Docs: [11](../docs/protocol/11-skills.md) §15.
- Character panel sums. Docs: [11](../docs/protocol/11-skills.md) §14.
- Character-panel leech limits and hit leech. Docs: [20](../docs/protocol/20-damage.md) §5b.
- Life and Mana regeneration, and Zealot's Oath. Docs: [20](../docs/protocol/20-damage.md) §5.
- Determination and Grace. Docs: [11](../docs/protocol/11-skills.md) §13.
- Discipline. Docs: [11](../docs/protocol/11-skills.md) §13,
  [20](../docs/protocol/20-damage.md), "Player energy shield".
- Buffs and Righteous Fire. Docs: [11](../docs/protocol/11-skills.md) §12.
- Warcry admission restriction (#297). Docs: [11](../docs/protocol/11-skills.md) §10b.
- Own-aura restriction (#304). Docs: [11](../docs/protocol/11-skills.md) §10c.
- Spell skills deal no damage (#293). Docs: [20](../docs/protocol/20-damage.md) §2g.
- Spells are released at the cast animation's contact keyframe. Docs: [11](../docs/protocol/11-skills.md) §9.
- Pride on enemies. Docs: [11](../docs/protocol/11-skills.md) §12.

## Partly working

- Contagion and Contagion of Subsiding: a chaos damage over time with no hit; a monster that dies affected spreads
  it for the time it had left. Not yet: the debuff icon (BuffDefinitions 504 crashes the client), Contagion of
  Transference, a spread when a projectile or a minion makes the kill.
- Essence Drain: a surviving hit leaves a chaos damage over time that recovers life for its caster, and
  Contagion's spread carries it. Not yet: the debuff icon (503 crashes the client), Essence Drain of
  Desperation's life loss, the debuff in Survivors.
- Ice Shot: its primary arrows (`IceArrow`) hit; the cone splash is open.
  Docs: [11](../docs/protocol/11-skills.md) §10.
- Multi-strike skills (Double Strike): each strike is its own hit, sharing one critical roll.
  Docs: [11](../docs/protocol/11-skills.md) §9a.
- Combat load: input-checked caches and snapshots; pathfinding and load spikes are still under investigation.
  Docs: [performance](../docs/dev/performance.md).
- Survivors league and its three optional modules. Docs: [Survivors mode](../docs/game/survivors-mode.md).
- Attack-speed admission and projectile speed for gem attacks. Docs: [11](../docs/protocol/11-skills.md) §10.
- Lifetap Support. Docs: [25](../docs/protocol/25-gem-supports.md).
- Blood Magic. Docs: [25](../docs/protocol/25-gem-supports.md).
- Bow arrow counts from bow and quiver modifiers. Docs: [11](../docs/protocol/11-skills.md) §10,
  [RE count inputs](../docs/re/projectiles/count-stat-inputs.md) §3.
- Tornado Shot's terminal secondary arrows. Docs: [11](../docs/protocol/11-skills.md) §10,
  [RE terminal geometry](../docs/re/projectiles/tornado-geometry.md).
- Kinetic Bolt's zig-zags. Docs: [11](../docs/protocol/11-skills.md) §10.
- Petrified Blood. Docs: [20](../docs/protocol/20-damage.md) §2i.
- Shared Mana/Recoup and Blind from Whisperer's nodes. Docs: [Whisperer](../docs/game/whisperer.md).
- Selected supported-spell panel. Docs: [11](../docs/protocol/11-skills.md) §9.
- Percentage attributes, attribute-scaled pools and defences, and inherent-bonus prohibitions.
  Docs: [11](../docs/protocol/11-skills.md) §4, [13](../docs/protocol/13-items.md) §2d,
  [20](../docs/protocol/20-damage.md#player-energy-shield-2026-10-25).
- Weapon-local damage and timing. Docs: [20](../docs/protocol/20-damage.md) §2a.
- Projectile framework: one 30 Hz runtime for gem and bow projectiles.
  Docs: [framework](../docs/game/projectile-framework.md), [client research](../docs/re/012-projectile-execution.md).
- Arcane Might (#364). Docs: [Arcane Might](../docs/game/arcane-might.md), [20](../docs/protocol/20-damage.md) §2c.
- Ruthless and Hardcore rules per connection. Docs: [league modes](../docs/game/league-modes.md).
- Grace Period on area entry. Docs: [11](../docs/protocol/11-skills.md), "Grace Period on combat-area entry".
- Vaal souls (#308) and Vaal Ground Slam. Docs: [27](../docs/protocol/27-vaal-souls.md),
  [Behemoth](../docs/game/behemoth.md).
- Ground Slam's forward wave, and gem melee swings that keep their hit over a queued Move.
  Docs: [11](../docs/protocol/11-skills.md) §9a.
- Warcry release and cooldowns. Docs: [11](../docs/protocol/11-skills.md) §10i.
- Strike targeting: spirit strikes and the previous location. Docs: [11](../docs/protocol/11-skills.md) §10d.
- Passive enemy auras. Docs: [11](../docs/protocol/11-skills.md) §10e.
- Passive-triggered skills: Ancestral Commander's Spirit chieftains.
  Docs: [11](../docs/protocol/11-skills.md) §10f, [20](../docs/protocol/20-damage.md) §2e.
- Player minions that fight. Docs: [11](../docs/protocol/11-skills.md) §10h.
- Life Recoup. Docs: [20](../docs/protocol/20-damage.md) §5.
- Ancestral Commander defences. Docs: [Ancestral Commander](../docs/game/ancestral-commander.md),
  [20](../docs/protocol/20-damage.md) §4.
- Aura restoration bootstrap fix. Docs: [11](../docs/protocol/11-skills.md), "Aura restoration bootstrap correction".
- Aura lifecycle: town suppression, restoration and death. Docs: [11](../docs/protocol/11-skills.md), "Aura lifecycle".
- Aura presentation: distinct art, and source and received buffs together.
  Docs: [11](../docs/protocol/11-skills.md) §13.
- Ordinary aura expansion: Determination, Grace, Anger, Hatred, Wrath, Zealotry, Malevolence and Envy.
  Docs: [11](../docs/protocol/11-skills.md) §13.
- Flesh and Stone and Blood and Sand. Docs: [11](../docs/protocol/11-skills.md) §12.
- Passive-granted skills and Summon Elemental Relic.
  Docs: [11](../docs/protocol/11-skills.md) §13, [Paladin](../docs/game/paladin.md).
- Owned minions: relics, chieftains and the 17 golems. Docs: [11](../docs/protocol/11-skills.md) §10h.
- Link skills. Docs: [11](../docs/protocol/11-skills.md) §13, [Paladin](../docs/game/paladin.md#link-skills).
- Saved aura toggles. Docs: [11](../docs/protocol/11-skills.md), [21](../docs/protocol/21-hideouts.md).
- Spells disabled (Devout of Gruthkul). Docs: [11](../docs/protocol/11-skills.md) §10j.
- Passive blind aura. Docs: [11](../docs/protocol/11-skills.md) §12.
- Passive-granted footstep skill (Abberath's Fury). Docs: [11](../docs/protocol/11-skills.md) §12.
- Lightning Strike resolves its melee hit and secondary projectiles.
- Rain of Arrows, Blast Rain and Toxic Rain resolve individual landings; Toxic Rain pods slow, deal chaos damage over time and burst.
- Projectile modifiers and named projectile skills. Docs: [framework](../docs/game/projectile-framework.md),
  [skills](../docs/game/projectile-skills.md), [20](../docs/protocol/20-damage.md) §3.
- Instant skills, cooldowns, Bladefall of Trarthus and Frostblink. Docs: [11](../docs/protocol/11-skills.md) §9, §11.
- Auras, reservation, Pride, heralds and permanent spell buffs. Docs: [11](../docs/protocol/11-skills.md) §13.
- Animated equipment, summons (including source recovery and spell repeats), mines and traps. Docs: [11](../docs/protocol/11-skills.md) §10h.
- Hexes and marks. Docs: [11](../docs/protocol/11-skills.md) §12.
- Totems: Rejuvenation, projectile spells and ballistas; placement validates the linked source again at release. Docs: [11](../docs/protocol/11-skills.md) §10g.
- Passive self buffs. Docs: [11](../docs/protocol/11-skills.md) §12.
- Onslaught on kill and from a Silver Flask. Docs: [13](../docs/protocol/13-items.md) §8a.
- Rampage and Soul Eater from item stats, with Soul Ascension's souls from hits on rare and unique enemies.
  Docs: [11](../docs/protocol/11-skills.md) §12.
- Stats: the client's stat keys and the player's base pools. Docs: [11](../docs/protocol/11-skills.md) §4, §8, §14.
- Attack speed passives and attack timing. Docs: [11](../docs/protocol/11-skills.md) §4.
- Spell cast timing (#192). Docs: [11](../docs/protocol/11-skills.md) §9.
- Selected spell hit damage panel (#188). Docs: [11](../docs/protocol/11-skills.md) §9 and §14.
- Critical strikes. Docs: [11](../docs/protocol/11-skills.md) §5.
- Overcapped-resistance modifiers. Docs: [11](../docs/protocol/11-skills.md) §4, §5 and §12.
- Projectiles: release, contact, lockstep client shots and monster mortars.
  Docs: [framework](../docs/game/projectile-framework.md), [11](../docs/protocol/11-skills.md) §6 and §10.
- Combat: server-side damage, monster AI and player hit recovery. Docs: [11](../docs/protocol/11-skills.md) §5–6.
- Critical-strike elemental resistance rules (Inevitable Judgement). Docs: [20](../docs/protocol/20-damage.md) §4.
- Client Buff descriptor research (#370, #466, offline only).
  Docs: [report](../docs/re/client-buff-descriptor-consumers.md).
- Damage model: packets, conversion, ailments, penetration and player ES. Docs: [20](../docs/protocol/20-damage.md),
  [Gambler](../docs/game/gambler.md), [Polytheist](../docs/game/polytheist.md).
- Continuous life and mana recovery. Docs: [20](../docs/protocol/20-damage.md), "Continuous resource updates".
- Consecrated ground while stationary. Docs: [13](../docs/protocol/13-items.md) §8a,
  [Paladin](../docs/game/paladin.md).
- The player's debuffs on monsters: taunt, enemy auras, Intimidate, Unnerve and Wither.
  Docs: [11](../docs/protocol/11-skills.md) §12, [Paladin](../docs/game/paladin.md).
- Cyclone's area of effect. Docs: [11](../docs/protocol/11-skills.md), "Cyclone area updates".
- Self-targeted spells. Docs: [11](../docs/protocol/11-skills.md) §9a.
- Winter Orb. Docs: [11](../docs/protocol/11-skills.md) §9a.
- Dark Bargain (self-cast only; Summon Skeletons does not work yet). Docs: [11](../docs/protocol/11-skills.md) §9a.

## Not working yet

- Projectile spawn height on platforms and doodads: the server has no surface-height model (#182).
  Docs: [11](../docs/protocol/11-skills.md) §10.
- Spirit of Utula's Leadership and Rakiata's Mark trigger need their own chieftain executors.
  Docs: [11](../docs/protocol/11-skills.md) §10f.
- Where Flicker Strike lands beside the monster is the server's guess, and its buff ignores increased buff effect.
  Docs: [11](../docs/protocol/11-skills.md) §9a.
- Holy Sweep's hammers deal no damage: the server has no hammerfall hits.
  Docs: [11](../docs/protocol/11-skills.md) §9a.
- Effects attached to bones (Righteous Fire's orbs) are not sent; banners and combat auras are not implemented.
  Docs: [11](../docs/protocol/11-skills.md) §12.
