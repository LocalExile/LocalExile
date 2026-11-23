# Status: Skills, auras, stats and combat

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/skills-and-combat.md](../roadmap/skills-and-combat.md).

## Works (verified with the real client)

- Storm Call: each landing sets a marker that strikes when the duration ends and calls down the others.
  Docs: [11](../docs/protocol/11-skills.md) Â§9a.
- Holy Sweep's art: base `skill_art_variation` 2 plus the worn effect's value.
  Docs: [23](../docs/protocol/23-microtransactions.md) Â§3.
- Holy Sweep's area: its swings hit around the player. Docs: [11](../docs/protocol/11-skills.md) Â§9a.
- Flicker Strike: the server teleports the player to the target, with its charge speed and buff; with no monster in
  range the use is refused and costs nothing.
  Docs: [11](../docs/protocol/11-skills.md) Â§9a.
- Cooldown bypass: a use during the cooldown spends a charge; a cooldown skill pays its cost when its use is
  accepted. Docs: [11](../docs/protocol/11-skills.md) Â§9.
- Fortification from melee hits. Docs: [Fortification](../docs/game/fortification.md).
- Vigilant Strike consumes its direct 8-second melee-hit Fortify grant and refreshes the displayed Fortification timer on a later grant through the shared path; cooldown bypass and fresh supported-client icon/timer behavior are verified live.
- Shield skills beside a one-handed weapon (`is_dual_wielding`), and Shield Charge's charge and shield damage.
  Docs: [11](../docs/protocol/11-skills.md) Â§10a and Â§10k, [13](../docs/protocol/13-items.md).
- Antiquarian's nodes. Docs: [Antiquarian](../docs/game/antiquarian.md).
- Conditional critical policy (#242): signed clamp, luck, unarmed multiplier and one roll per gem use.
  Docs: [11](../docs/protocol/11-skills.md), conditional critical policy.
- Generic Rage (#281). Docs: [26](../docs/protocol/26-rage.md).
- Mana per final Strength (#287). Docs: [11](../docs/protocol/11-skills.md) Â§4.
- Player Actor scale (#299). Docs: [04](../docs/protocol/04-player-entity.md#41a-player-actor-scale-299).
- Armour against chaos hits (#288). Docs: [20](../docs/protocol/20-damage.md) Â§2e.
- Increased attributes (#285). Docs: [11](../docs/protocol/11-skills.md) Â§4, [13](../docs/protocol/13-items.md) Â§2d.
- Power of Purpose (#291). Docs: [Power of Purpose](../docs/game/power-of-purpose.md).
- The default attack, and weapons whose requirements are not met leaving the hands.
  Docs: [11](../docs/protocol/11-skills.md) Â§4 and Â§10a.
- Combat timing: stationary attacks, wand bolts, monster flinches and the monster planner.
  Docs: [11](../docs/protocol/11-skills.md) Â§4â€“6.
- Impale. Docs: [20](../docs/protocol/20-damage.md) Â§3a, [11](../docs/protocol/11-skills.md) Â§14.
- Offence panel damage rows from the hit the server swings with. Docs: [11](../docs/protocol/11-skills.md) Â§14.
- Whisperer "your mind is destroyed": spell increases also scale attacks.
  Docs: [20](../docs/protocol/20-damage.md) Â§2c.
- Behemoth Steel Willed: attack damage per 450 final armour. Docs: [20](../docs/protocol/20-damage.md) Â§2d.
- Steel Willed incoming-hit armour. Docs: [Steel Willed armour](../docs/game/steel-willed-armour.md).
- Accuracy, the Accuracy Mastery's target conditions and attack evasion.
  Docs: [11](../docs/protocol/11-skills.md) Â§4â€“5, [20](../docs/protocol/20-damage.md) Â§2h.
- Armour, Evasion and Energy Shield from passives and equipment, Iron Reflexes and Chaos Inoculation.
  Docs: [11](../docs/protocol/11-skills.md) Â§4, [13](../docs/protocol/13-items.md) Â§2.
- Gem skills, their projectiles and triggered releases, including linked summons and Desecrate.
  Docs: [11](../docs/protocol/11-skills.md) Â§9â€“10, [25](../docs/protocol/25-gem-supports.md).
- Resistances, with the Kitava penalties. Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Spell Suppression. Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Movement speed and action speed. Docs: [07](../docs/protocol/07-latency-visibility-movement.md) Â§3.
- Block and Spell Block. Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Endurance, Frenzy, Power, converted and Blood Charges, including Reap's life cost and mixed-target hits. Docs: [11](../docs/protocol/11-skills.md) Â§15.
- Discharge and Discharge of Misery. Docs: [11](../docs/protocol/11-skills.md) Â§15.
- Character panel sums. Docs: [11](../docs/protocol/11-skills.md) Â§14.
- Character-panel leech limits and hit leech. Docs: [20](../docs/protocol/20-damage.md) Â§5b.
- Life and Mana regeneration, and Zealot's Oath. Docs: [20](../docs/protocol/20-damage.md) Â§5.
- Determination and Grace. Docs: [11](../docs/protocol/11-skills.md) Â§13.
- Discipline. Docs: [11](../docs/protocol/11-skills.md) Â§13,
  [20](../docs/protocol/20-damage.md), "Player energy shield".
- Buffs. Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Warcry admission restriction (#297). Docs: [11](../docs/protocol/11-skills.md) Â§10b.
- Own-aura restriction (#304). Docs: [11](../docs/protocol/11-skills.md) Â§10c.
- Spell skills deal no damage (#293). Docs: [20](../docs/protocol/20-damage.md) Â§2g.
- Spells are released at the cast animation's contact keyframe. Docs: [11](../docs/protocol/11-skills.md) Â§9.
- Pride on enemies. Docs: [11](../docs/protocol/11-skills.md) Â§12.

## Partly working

- Cursed and ignited kill effects use the accepted hit and corpse state. Retained hexes support Asenath's corpse effects.
  Witchbane grants its timed immunity on player kills, and minion curse explosions use the killing minion's damage.
- Hex Master and Defiled Forces govern curse duration and ailment refresh. Firestorm schedules timed hits.
- Weapon-local Bloodseeker instant Life leech, Elder life gained against blinded enemies and culling against bleeding
  enemies, and Nametaker critical multiplier and instant leech follow the hand that makes the hit.
- Weapon-conditioned attack damage, bow bleeding, attack ignite and spell poison read their qualifying stats.
  Trap and mine damage, Battlemage, Perseverance, physical reduction bypass and elemental ailment swaps apply offline.
- Acrobatics, The Agnostic, Imbalanced Guard, Wicked Ward, Corrupted Soul and Transcendence resolve their defences.
  Two-handed melee and mace stun modifiers, and doubled stun duration, apply on qualifying hits.

- Summoned followers stop pathless walk orders and retry when a route becomes available.
- Spell-totem summons keep their minion identity instead of inheriting their summoning skill's totem flags.
- Righteous Fire's enemy burn with linked supports. Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Monster-hit bleeding and poison share timed damage, avoidance and paid flask cleansing; native QA pending.
- Scorch, Brittle and Sap from chance, crit (Secrets of Suffering) and block sources on monsters; Scorch and Brittle
  on the player; not checked live. Docs: [20](../docs/protocol/20-damage.md) "Scorch, Brittle and Sap".
- Selected worn skills and Cat/Avian/Spider/Crab owners (#504). Docs: [11](../docs/protocol/11-skills.md).
- Embrace Madness and self Vulnerability (#504). Docs: [11](../docs/protocol/11-skills.md).
- Incoming bleed/poison and qualified Elusive (#504). Docs: [20](../docs/protocol/20-damage.md).
- Divine Blessing (including March of the Legion) pays a one-time cost and grants one temporary Blessing;
  Vaal Haste has a soul-paid temporary aura. Smite has separate melee/lightning hits and a timed on-hit aura.
  Smite impacts and Vaal Haste's speed buff are verified live; complete build and party parity remain partial.
- Eternal Blessing suspends other skills' mana reservations while its aura is active and preserves life reservations.
- Guardian's Blessing needs a living minion that shares its support link. The minion takes physical damage over time,
  and the aura ends when the last qualifying minion is gone.

- Leap Slam: landing hits and full-life stuns; recovery cadence awaits a client check. Docs: [11](../docs/protocol/11-skills.md) Â§9a.

- Contagion and Contagion of Subsiding: a chaos damage over time with no hit; a monster that dies affected spreads
  it for the time it had left. Contagion of Transference spreads on hit. The debuff icon and some kill paths remain open.
- Bane and Bane of Condemnation: independent chaos damage over time and linked Hex application. Damage and duration
  use the Hexes applied to each target. The Bane-specific buff presentation remains open.
- Essence Drain: a surviving hit leaves a chaos damage over time that recovers life for its caster, and
  Contagion's spread carries it. Desperation drains the caster's Life and Energy Shield while its debuff deals damage.
  The debuff icon and Survivors support remain open.
- Ice Shot: its primary arrows (`IceArrow`) hit; the cone splash is open.
  Docs: [11](../docs/protocol/11-skills.md) Â§10.
- Multi-strike skills (Double Strike): each strike is its own hit, sharing one critical roll.
  Docs: [11](../docs/protocol/11-skills.md) Â§9a.
- Dual wielding: each attack use swings the hand the client picks from its sequence (main, off, mainâ€¦), with that
  weapon's time, damage, critical chance, accuracy and local mods; a hand the skill's weapon list excludes never
  swings, and `skill_double_hits_when_dual_wielding` skills hit with both at once. Not checked live.
  Docs: [11](../docs/protocol/11-skills.md) Â§4.
- Holy Sweep's hammers hit, with the client's draws: Hammerfalls' waves fall where the client draws them and follow
  attack speed; Holy Sweep's hammers fall on the server's landed hits, all at contact, where the client uses its own
  hit roll and drops them along the swing.
  Docs: [11](../docs/protocol/11-skills.md) Â§9a.
- Combat load: input-checked caches and snapshots; pathfinding and load spikes are still under investigation.
  Docs: [performance](../docs/dev/performance.md).
- Survivors league and its three optional modules. Docs: [Survivors mode](../docs/game/survivors-mode.md).
- Attack-speed admission and projectile speed for gem attacks. Docs: [11](../docs/protocol/11-skills.md) Â§10.
- Skill area and duration scaling: the player's increased area counted once at every area site, type-scoped area,
  supports' more or less area, Duration masteries and warcry and Vaal duration (not checked live).
  Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Lifetap Support and Blood Magic publish converted wire `life_cost` / `life_cost_per_minute` (zeroing wire mana cost)
  so the client spends Life only. Docs: [25](../docs/protocol/25-gem-supports.md).
- Movement, travel and stance skill cooldowns read their recovery stats. Spell and curse costs read their efficiency.
  Mark waivers, Cannibalistic Rite, the Covenant and Rathpith Globe use the server's resource admission.
- Energy Shield skill costs (Energy Blade, Wild Strike of Extremes, Replica Covenant) and Eldritch Battery: costs paid
  from Energy Shield first, hits past it to Life, its Mana share taken from it, 50% less recharge (not checked live).
  Eternal Youth redirects recharge to Life and reduces regeneration and total Life leech. Immortal Ambition
  prevents shield recharge and regeneration and redirects retained Life leech at full Life ([15](../docs/protocol/15-progression.md)).
  Docs: [20](../docs/protocol/20-damage.md).
- Bow arrow counts from bow and quiver modifiers. Docs: [11](../docs/protocol/11-skills.md) Â§10,
  [RE count inputs](../docs/re/projectiles/count-stat-inputs.md) Â§3.
- Explosive Arrow adds fuses on terminal contacts. The first fuse times the combined blast, and hitting the fuse cap
  detonates it early.
- Ensnaring Arrow tethers a surviving final target until it leaves the anchor's radius. Its projectile attack
  damage taken modifier does not multiply with each additional tether.
- Tornado Shot's terminal secondary arrows. Docs: [11](../docs/protocol/11-skills.md) Â§10,
  [RE terminal geometry](../docs/re/projectiles/tornado-geometry.md).
- Kinetic Bolt's zig-zags (including multi-projectile volley parity). Docs: [11](../docs/protocol/11-skills.md) Â§10.
- Mirage Archer spawns non-blocking on the client (`Positioned.blocking = false`, `flags1 0x08`) so it does not trap
  player movement onto diagonals. Docs: [11](../docs/protocol/11-skills.md) Â§10.
- Ice Nova of Frostbolts expands from up to two active player Frostbolt projectiles near the aimed point with its native
  more-damage multiplier (not checked live). Docs: [11](../docs/protocol/11-skills.md) Â§9a.
- Petrified Blood. Docs: [20](../docs/protocol/20-damage.md) Â§2i.
- Shared Mana/Recoup and Blind from Whisperer's nodes. Docs: [Whisperer](../docs/game/whisperer.md).
- Selected supported-spell panel. Docs: [11](../docs/protocol/11-skills.md) Â§9.
- Percentage attributes, attribute-scaled pools and defences, and inherent-bonus prohibitions.
  Docs: [11](../docs/protocol/11-skills.md) Â§4, [13](../docs/protocol/13-items.md) Â§2d,
  [20](../docs/protocol/20-damage.md#player-energy-shield-2026-10-25).
- Weapon-local damage and timing; wand/claw/dagger added ranges, Varunastra, Fan of Blades, Eclipse Solaris and the
  Ritual boots' accuracy lightning. Docs: [20](../docs/protocol/20-damage.md) Â§2a-2b.
  Attack coefficients, the typed weapon passives and weapon-scoped cold per Dexterity have shared consumers.
  Attack-only ignite chances and the bow skill's ailment multiplier act at contact;
  further skill-specific inputs and native checks remain open (#368).
- Projectile framework: one 30 Hz runtime for gem and bow projectiles.
  Docs: [framework](../docs/game/projectile-framework.md), [client research](../docs/re/012-projectile-execution.md).
- Arcane Might (#364). Docs: [Arcane Might](../docs/game/arcane-might.md), [20](../docs/protocol/20-damage.md) Â§2c.
- Ruthless and Hardcore rules per connection. Docs: [league modes](../docs/game/league-modes.md).
- Grace Period on area entry. Docs: [11](../docs/protocol/11-skills.md), "Grace Period on combat-area entry".
- Vaal souls (#308), Vaal Ground Slam, Vaal Fireball, Vaal Spark and Vaal Discipline. Docs: [27](../docs/protocol/27-vaal-souls.md),
  [Behemoth](../docs/game/behemoth.md).
- Ground Slam's forward wave gains more damage near the player. Volcanic Fissure hits along its path and launches
  landing projectiles at the end of the sweep. Gem melee swings keep their hit over a queued Move.
  Docs: [11](../docs/protocol/11-skills.md) Â§9a.
- Heavy Strike and Trarthus confirmed-hit particles/audio, with typed and critical melee feedback. Single-clip Heavy Strike stances use their ACT contact.
  Docs: [11](../docs/protocol/11-skills.md), "Hit effects".
- Warcry release and cooldowns. Intimidating Cry applies War Bringer's exertion count and carries the spent exertion
  to the attack's melee and projectile hits for Overexertion. Docs: [11](../docs/protocol/11-skills.md) Â§10i.
- Strike targeting: spirit strikes and the previous location. Docs: [11](../docs/protocol/11-skills.md) Â§10d.
- Passive enemy auras. Docs: [11](../docs/protocol/11-skills.md) Â§10e.
- Passive-triggered skills: Ancestral Commander's Spirit chieftains.
  Docs: [11](../docs/protocol/11-skills.md) Â§10f, [20](../docs/protocol/20-damage.md) Â§2e.
- Player minions: Conviction novas, Multistrike, Ruthless and Melee Splash. Docs: [11](../docs/protocol/11-skills.md) Â§10h.
  Infernal Legion causes self-burn and burning damage nearby, and Minion Instability explodes a living minion at Low Life.
- Life Recoup. Docs: [20](../docs/protocol/20-damage.md) Â§5.
- Ancestral Commander defences. Docs: [Ancestral Commander](../docs/game/ancestral-commander.md),
  [20](../docs/protocol/20-damage.md) Â§4.
- Aura restoration bootstrap fix. Docs: [11](../docs/protocol/11-skills.md), "Aura restoration bootstrap correction".
- Aura lifecycle: town suppression, restoration and death. Docs: [11](../docs/protocol/11-skills.md), "Aura lifecycle".
- Aura presentation: distinct art, and source and received buffs together.
  Docs: [11](../docs/protocol/11-skills.md) Â§13.
- Ordinary aura expansion: Determination, Grace, Anger, Hatred, Wrath, Zealotry, Malevolence and Envy.
  Docs: [11](../docs/protocol/11-skills.md) Â§13.
- Watcher's Eye modifiers for Pride combat, elemental and critical penetration, Grace blind, Haste debuff expiry,
  and Clarity mana recovery apply while the matching aura is active. The legacy Zealotry modifiers apply to
  owned Consecrated Ground and grant Arcane Surge when creating it.
- Flesh and Stone and Blood and Sand. Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Passive-granted skills and Summon Elemental Relic.
  Docs: [11](../docs/protocol/11-skills.md) §13, [Paladin](../docs/game/paladin.md).
- Equipment-granted Bear Trap, Wintertide Brand, Decoy Totem, Smite, Frostblink, Summon Stone Golem, Convocation,
  Enduring Cry and Intimidating Cry use their existing skill actions. A restored golem retains its equipment source.
- Doryani's Touch uses the gloves' unarmed lightning damage and requires both hands to have no weapon.
- Lightpoacher can earn Spirit Charges on kills with an equipped Abyss Jewel. An accepted skill use can spend a
  live charge to launch Spirit Burst.
- Approaching Flames from Annihilation's Approach toggles Flame-Touched, its Adrenaline and its fire damage.
- Owned minions: relics, chieftains and the 17 golems. Docs: [11](../docs/protocol/11-skills.md) §10h.
  Mindless Aggression and minion area mastery scale minions. Fresh Meat grants timed Adrenaline and Wakened Fury.
- Raised Host and Carnage Chieftains grant Power and Frenzy Charges with their native roars. Spectre charge visuals still need live verification.
- Link skills. Docs: [11](../docs/protocol/11-skills.md) §13, [Paladin](../docs/game/paladin.md#link-skills).
  Docs: [11](../docs/protocol/11-skills.md) Â§13, [Paladin](../docs/game/paladin.md).
- Owned minions: relics, chieftains and the 17 golems. Docs: [11](../docs/protocol/11-skills.md) Â§10h.
- Link skills. Docs: [11](../docs/protocol/11-skills.md) Â§13, [Paladin](../docs/game/paladin.md#link-skills).
- Saved aura toggles. Docs: [11](../docs/protocol/11-skills.md), [21](../docs/protocol/21-hideouts.md).
- Spells disabled (Devout of Gruthkul). Docs: [11](../docs/protocol/11-skills.md) Â§10j.
- Passive blind aura. Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Passive-granted footstep skill (Abberath's Fury). Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Charge gains on stun, skill use and target state, supports' own chances, "lose all" and "on reaching maximum"
  uniques, loss triggers, cannot-gain and maximum-replacing mods (not checked live).
  Docs: [11](../docs/protocol/11-skills.md) Â§15.
- Lightning Strike resolves its melee hit and secondary projectiles.
- Molten Strike and its Zenith and Vaal versions: a melee hit that launches magma balls from the struck enemies, and
  Zenith's fifth-attack buff. Docs: [11](../docs/protocol/11-skills.md) Â§10d.
- Rain of Arrows, Blast Rain and Toxic Rain resolve individual landings; Toxic Rain pods slow, deal chaos damage
  over time and burst; Concentrated Effect scaling and owner/proxy damage restrictions await a client check.
  The arrows fall before they land, and Toxic Rain's pods grow and burst, for every skill effect.
  The arrow the client flies up is stood in for by the falling art; Rain of Arrows and Blast Rain shoot nothing up.
- Caustic Arrow stops at the aimed point and leaves caustic ground: chaos damage over time that does not stack.
  The ground is the art's pieces laid over the radius, for the worn skill effect; the layout is the server's own.
  Only the default, Celestial and Tentacles grounds fade in and out.
- Projectile modifiers and named projectile skills. Docs: [framework](../docs/game/projectile-framework.md),
  [skills](../docs/game/projectile-skills.md), [20](../docs/protocol/20-damage.md) Â§3.
- Bow and wand attack gems without a projectile row (Frenzy, Puncture, Power Siphon) fire the held weapon's
  projectile (not checked live). Docs: [skills](../docs/game/projectile-skills.md).
  Frenzy gives one Frenzy Charge per use and its more attack damage and speed per charge.
  Puncture always bleeds, for its skill duration and with its more bleeding damage.
  Power Siphon fires one projectile at each of the nearest enemies ahead, with its per-Power-Charge criticals.
- Doomfletch's weapon physical gain reaches attacks as separate fire, cold and lightning damage.
- Equipment modifiers scale from final attributes, Oskarm uses final Accuracy for attack critical chance, and
  Mask of the Stitched Demon uses full maximum Energy Shield for Life regeneration.
- Blink Arrow and its variants leave clones at departure; clones use arrows, arrow rain or elemental shots.
  Lightning Warp queues teleports and hits both endpoints.
- Instant skills, shared Blink and Guard cooldowns (a guard skill cast from the bar as well as one a trigger releases),
  opt-in warcry sharing and cost admission, Bladefall of Trarthus and both Frostblink hit and chill areas, with clear
  landings across terrain gaps. A guard's bar entry is held at the skill's own cooldown with a zero recovery rate while
  its buff runs and counts at the character's recovery after it, as the official client shows. The held entry is published
  before the instant action so the client skips its press prediction, and a stat-only skill refresh (a guard or self buff
  starting/ending) no longer rebuilds the skill bar; a skill-list refresh during the buff (movement, conditional item stats)
  re-publishes the held entry, since the client's skill replacement would otherwise refresh its zero-rate entry: the ring
  does not sweep or replay on the first press or while moving (#1127).
  Docs: [11](../docs/protocol/11-skills.md) Â§9, Â§11.
- Vaal Immortal Call: 100 souls, its 60 s cooldown, 8 s of soul gain prevention, a duration from the Endurance Charges it
  discharges, and Life held at 1 while the buff runs. Docs: [11](../docs/protocol/11-skills.md) Â§11, Â§12.
- Steelskin, Immortal Call and Molten Shell cast by hand start the triggered guard buff; a running or recovering
  guard refuses the next before its cost. Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Phase Run: movement speed and Phasing for its duration, longer per Frenzy Charge consumed; the next skill use
  swaps it for its more melee physical damage (not checked live). Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Blood Rage: attack speed, physical attack life leech, Frenzy Charge chance on kill and its physical degeneration;
  a kill refreshes it (not checked live). Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Auras, reservation, Pride, heralds and permanent spell buffs. Docs: [11](../docs/protocol/11-skills.md) Â§13.
- Animated equipment, including Self Reflection. Docs: [11](../docs/protocol/11-skills.md) Â§10h.
  Animated Guardian keeps its equipment on death and is unavailable in that non-hideout instance.
- Summons: quality, source recovery, spell repeats, and enemy-seeking undamageable Skitterbots; mines and traps
  Revenants consume nearby allies, Smiting Guardians use Smite, and Slamming Zombies attack between slams.
  (including Fire Trap proxy stats, detonation aim, burning-enemy bonus, throw clamping, and lockstep timing).
  Locus Mine keeps close mines armed. Invention scales trap and mine damage by their age at the trigger.
  Docs: [11](../docs/protocol/11-skills.md) Â§10h.
- Summon Reaper of Eviscerating follows its owner, consumes allies and attacks on recast.
- Predator targeting, Feeding Frenzy buffs, aggressive Skeleton Warrior dashes, defensive targeting and Meat Shield proximity damage.
- Minion equipment applies Blind, Poison, Withered, and skeleton or zombie Ash on hit.
- Speaker's Wreath restricts minion hit kills to ignited enemies.
- Queen's Escape increases the skeleton limit.
- Foulborn Severed in Sleep adds minion base critical chance and Foulborn United in Dream raises Minion gem quality.
- Mutewind Seal and Redblade Band apply modifiers to golems socketed in their ring.
- Triad Grip converts minion physical damage from its socket contents.
- Alberon's Warpath summons one permanent following warrior per cast.
  Docs: [11](../docs/protocol/11-skills.md) Â§10h.
- Absolution and Dominating sentinels, Phantasms, direct Vaal Skeletons and Vaal Absolution/Domination ascensions.
  HUD minion counter icons (`PlayerMinionsChange`) now publish for all active player minion types (not checked live).
  Docs: [11](../docs/protocol/11-skills.md) Â§10h.
  Vaal minion totem, trap and mine casts are refused before spending souls or starting an action.
- Removable player buffs can be right-click cancelled via C→S `0x4b` (Grace Period, Shrines, guards, auras, Arcane
  Surge, phasing; non-removable debuffs rejected; not checked live). Docs: [11](../docs/protocol/11-skills.md) §12.
- Native knockback hit presentation (`0x1af` bit 11 + `u3c`/`u3e`), totem directional `HitAngle`/`HitCode`, `0x1b0`
  `ActionResultFlag` encoder, and chance-based instant cooldown reset sync (`0x50cd`/`0x52e2`; not checked live).
  Docs: [11](../docs/protocol/11-skills.md) §5, §9, §10g.
- Vaal Animate Weapon transforms eligible melee drops and landed Bladefall of Trarthus blades into temporary unique weapons.
- Conversion Trap temporarily changes the triggering enemy's allegiance. Converted enemies can attack hostiles,
  and unique enemies are immune.
- Bear Trap's single secondary hit immobilises the target, then applies a decaying slow and trap/mine hit vulnerability.
- Hexes and marks. Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Totems: Rejuvenation, Arc, projectile spells, supported summons, ballistas, Shockwave, Devouring and Decoy Totem
  (ordinary monster AI), with totem limit, range, duration and Lockstep placement timing.
- Holy Flame Totem places without channelling on its owner, creates Consecrated Ground, and fires piercing bolts.
  Docs: [11](../docs/protocol/11-skills.md) Â§10g.
- Passive self buffs. Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Onslaught on kill and from a Silver Flask. Docs: [13](../docs/protocol/13-items.md) Â§8a.
- Rampage and Soul Eater from item stats, with Soul Ascension's souls from hits on rare and unique enemies.
  Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Stats: the client's stat keys and the player's base pools, plus item-requirement stats (`main_hand_weapon_type`,
  `virtual_*_for_requirements`), dual-wield snapshot sync, and light radius virtual stats (`virtual_light_radius_+%`,
  `virtual_blind_does_not_affect_light_radius`; new virtuals not checked live).
  Docs: [11](../docs/protocol/11-skills.md) Â§4, Â§8, Â§14.
- Attack speed passives and attack timing. Docs: [11](../docs/protocol/11-skills.md) Â§4.
- Spell cast timing (#192). Docs: [11](../docs/protocol/11-skills.md) Â§9.
- Selected spell hit damage panel (#188). Docs: [11](../docs/protocol/11-skills.md) Â§9 and Â§14.
- Critical strikes. Docs: [11](../docs/protocol/11-skills.md) Â§5.
- Overcapped-resistance modifiers. Docs: [11](../docs/protocol/11-skills.md) Â§4, Â§5 and Â§12.
- Projectiles: release, contact, lockstep client shots and monster mortars.
  Docs: [framework](../docs/game/projectile-framework.md), [11](../docs/protocol/11-skills.md) Â§6 and Â§10.
- Combat: server-side damage, monster AI and player hit recovery. Real and synthetic enemy hits share recent-hit,
  attack/spell block, suppression, charge and automatic-flask event bookkeeping.
  Blocked monster hits retain flask charge gain from being hit.
  Real and synthetic blocks recover Life, Mana and Energy Shield before damage, with shared recovery caps.
  Docs: [11](../docs/protocol/11-skills.md) Â§5â€“6.
- Critical-strike elemental resistance rules (Inevitable Judgement). Docs: [20](../docs/protocol/20-damage.md) Â§4.
- Client Buff descriptor research (#370, #466, offline only).
  Docs: [report](../docs/re/client-buff-descriptor-consumers.md).
- Damage model: packets, conversion, ailments, penetration and player ES. Player ailments from ordinary hits,
  boss skills, ground and reflection share recent-freeze, automatic-flask and movement notifications.
  Native boss ailment chances and modifiers feed the ordinary hit application, with one avoidance roll per kind.
  Docs: [20](../docs/protocol/20-damage.md),
  [Gambler](../docs/game/gambler.md), [Polytheist](../docs/game/polytheist.md).
- Continuous life and mana recovery. Docs: [20](../docs/protocol/20-damage.md), "Continuous resource updates".
- Consecrated ground while stationary. Docs: [13](../docs/protocol/13-items.md) Â§8a,
  [Paladin](../docs/game/paladin.md).
- The player's debuffs on monsters: taunt, enemy auras, Intimidate, Unnerve and Wither.
  Docs: [11](../docs/protocol/11-skills.md) Â§12, [Paladin](../docs/game/paladin.md).
- Cyclone's area of effect. Docs: [11](../docs/protocol/11-skills.md), "Cyclone area updates".
- Self-targeted spells, including Convocation and corpse offerings. Docs: [11](../docs/protocol/11-skills.md) §9a.
- Winter Orb. Docs: [11](../docs/protocol/11-skills.md) §9a.
- Dark Bargain (self-cast only; Summon Skeletons does not work yet). Docs: [11](../docs/protocol/11-skills.md) §9a.
- Brand system: Storm Brand (+ Indecision), Armageddon Brand (+ Volatility, Recall), Penance Brand (+ Dissipation,
  Conduction), Wintertide Brand, Arcanist Brand, Brand Recall and Brandsurge with attachment, limits, Swiftbrand
  cadence, `ActiveSigil`/`magma_pustule`/`immolation_brand_burn` buffs and `SigilDisplay` counters.
  Base Penance Brand and Brand Recall were exercised against Amarissa in Predictive and Lockstep on the
  3.29.3d DX12 client. Conduction currently spreads only from its attached monster to two nearby targets.
  Arcanist Brand requires a supported trigger executor for each linked spell.
  Docs: [11](../docs/protocol/11-skills.md).
- Snipe (`channelled_snipe` gem and Assailum) stage channelling (`snipe_stacks` 1494) and staged bow attack release,
  and General's Cry (`generals_cry` + variants) corpse selection and non-blocking Mirage Warrior melee execution
  (not checked live). Docs: [11](../docs/protocol/11-skills.md).
- Self-targeted spells, including Convocation and corpse offerings. Docs: [11](../docs/protocol/11-skills.md) Â§9a.
- Winter Orb. Docs: [11](../docs/protocol/11-skills.md) Â§9a.
- Dark Bargain (self-cast only; Summon Skeletons does not work yet). Docs: [11](../docs/protocol/11-skills.md) Â§9a.

## Not working yet

- Projectile spawn height on platforms and doodads: the server has no surface-height model (#182).
  Docs: [11](../docs/protocol/11-skills.md) Â§10.
- Spirit of Utula's Leadership and Rakiata's Mark trigger need their own chieftain executors.
  Docs: [11](../docs/protocol/11-skills.md) Â§10f.
- Where Flicker Strike lands beside the monster is the server's guess, and its buff ignores increased buff effect.
  Docs: [11](../docs/protocol/11-skills.md) Â§9a.
- Effects attached to bones (Righteous Fire's orbs) are not sent; banners and combat auras are not implemented.
  Docs: [11](../docs/protocol/11-skills.md) Â§12.
