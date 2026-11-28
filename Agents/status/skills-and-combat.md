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
- Cooldown bypass: the accepted skill use spends the required charge together with its cost before the charge changes.
  A cooldown skill otherwise pays its cost when accepted. Docs: [11](../docs/protocol/11-skills.md) Â§9.
- Fortification from melee hits. Docs: [Fortification](../docs/game/fortification.md).
- Vigilant Strike consumes its direct 8-second melee-hit Fortify grant and refreshes the displayed Fortification timer on a later grant through the shared path; cooldown bypass and fresh supported-client icon/timer behavior are verified live.
- Shield skills beside a one-handed weapon (`is_dual_wielding`), and Shield Charge's path hits, arrival bash and forward wave. The charge passes through Actors and Chests and stops on solid scenery or its targeted monster. Shield attacks use shield-side accuracy.
  Docs: [11](../docs/protocol/11-skills.md) Â§10a and Â§10k, [13](../docs/protocol/13-items.md).
- Antiquarian's nodes. Docs: [Antiquarian](../docs/game/antiquarian.md).
- Conditional critical policy (#242): signed clamp, luck, unarmed multiplier and one roll per gem use.
  Docs: [11](../docs/protocol/11-skills.md), conditional critical policy.
- Generic Rage (#281), including effective-Rage scaling (`rage_effect_+%`) for per-Rage attack speed (Crave the Slaughter), armour, movement speed and stun threshold. Docs: [26](../docs/protocol/26-rage.md).
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
- Shattering Steel erupts on impact or after a 140 ms flight. Call of Steel removes Impales and reloads Steel Shards,
  and the Shattering Steel of Ammunition ward expires on the area clock.
- Splitting Steel bursts at the aim, a target or terrain, then its shards burst at their own endpoints. The Ammunition
  variant consumes up to two Steel Shards and fires an already-split shard when empty.
- Shrapnel Ballista of Steel consumes an owner Steel Shard at each volley and fires a single native shrapnel
  projectile when empty.
- Steelworm spends an extra Steel Shard on player projectile attacks. Spending eight while equipped grants additional
  projectiles for four seconds, ending early when the quiver leaves the active set.
- Accuracy, the Accuracy Mastery's target conditions, Deadeye's Avidity and attack evasion.
  Docs: [11](../docs/protocol/11-skills.md) Â§4â€“5, [20](../docs/protocol/20-damage.md) Â§2h.
- Armour, Evasion and Energy Shield from passives and equipment, Iron Reflexes and Chaos Inoculation.
  Docs: [11](../docs/protocol/11-skills.md) Â§4, [13](../docs/protocol/13-items.md) Â§2.
- Gem skills, their projectiles and triggered releases, including linked summons and Desecrate's corpses and chaos
  ground.
  Docs: [11](../docs/protocol/11-skills.md) Â§9â€“10, [25](../docs/protocol/25-gem-supports.md).
- Resistances, with the Kitava penalties. Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Spell Suppression. Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Movement speed and action speed. Docs: [07](../docs/protocol/07-latency-visibility-movement.md) Â§3.
- Block and Spell Block. Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Endurance, Frenzy, Power, converted and Blood Charges, including Reap's life cost and mixed-target hits. Docs: [11](../docs/protocol/11-skills.md) Â§15.
- Discharge and Discharge of Misery. Docs: [11](../docs/protocol/11-skills.md) Â§15.
- Character panel sums. Docs: [11](../docs/protocol/11-skills.md) Â§14.
- Character-panel leech limits and hit leech. Docs: [20](../docs/protocol/20-damage.md) Â§5b.
- Life and Mana regeneration, and Zealot's Oath; plain regeneration skips per-frame Life writes (client-predicted).
  Docs: [20](../docs/protocol/20-damage.md) Â§5.
- Determination and Grace. Docs: [11](../docs/protocol/11-skills.md) Â§13.
- Discipline. Docs: [11](../docs/protocol/11-skills.md) Â§13,
  [20](../docs/protocol/20-damage.md), "Player energy shield".
- Buffs. Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Warcry admission restriction (#297). Docs: [11](../docs/protocol/11-skills.md) Â§10b.
- Own-aura restriction (#304). Docs: [11](../docs/protocol/11-skills.md) Â§10c.
- Spell skills deal no damage (#293). Docs: [20](../docs/protocol/20-damage.md) Â§2g.
- Spells are released at the cast animation's contact keyframe. Docs: [11](../docs/protocol/11-skills.md) Â§9.
- Pride on enemies. Docs: [11](../docs/protocol/11-skills.md) Â§12.

- Bladestorm swings around the player and creates timed Blood or Sand storms with their respective buffs.
- Chain Hook pulls and hits at beam contact at range, and uses the weapon stance's swing at close range.
- Chain Hook of Trarthus attaches, grants Rage on attack hits and deals area damage when a chain breaks.

## Partly working

- Vaal Immortal Call protects the last point of Life from on-kill Life loss while active.
- Aura-dependent recovery, mana-spent charges and the wearer's Withered hit modifiers resolve on the server.
- The equipped weapon and the target's pre-hit ailments govern qualified hit recovery and debuffs.
- Frost Shield and Sigil of Power run staged areas. Docs: [11](../docs/protocol/11-skills.md).
- Supreme Ego changes the caster's aura reservation, effect and recipients. Docs: [11](../docs/protocol/11-skills.md).
- Native keystone damage shifts, charge replacement and Ward rules have server consumers (live combat checks pending).

- Cursed and ignited kill effects use the accepted hit and corpse state. Retained hexes support Asenath's corpse effects.
  Witchbane grants its timed immunity on player kills, and minion curse explosions use the killing minion's damage.
- Hit-kill corpse destruction (`enemies_explode_on_kill` from Axe Mastery / Kitava's Feast, Unhinged, and ignited corpse
  destruction from Pyre / Cremator) removes corpses on lethal hits before death skills or corpse consumers run.
- Hex Master and Defiled Forces govern curse duration and ailment refresh. Firestorm schedules timed hits.
- Player curses (monster, strongbox, Harbinger, Doedre pillar, and map hexes) deduplicate per curse type so only the
  strongest active source applies; Temporal Chains enforces the -75% action-speed cap and unaffected-by-Temporal-Chains
  protections, and walk speed follows stat publication on application and removal.
- Linked spells resolve offline. See [linked spells](../docs/protocol/25-gem-supports.md#native-linked-spell-payloads).
- Mark Mastery blinds around the live marked enemy in offline tests, using the server's 35-cell nearby blind reach.
- Weapon-local Bloodseeker instant Life leech, Elder life gained against blinded enemies and culling against bleeding
  enemies, and Nametaker critical multiplier and instant leech follow the hand that makes the hit.
- Replica Innsbury Edge supplies its local Withered chance to accepted weapon hits on the server. Live checks pending.
- Weapon-conditioned attack damage, bow bleeding, attack ignite and spell poison read their qualifying stats.
  Trap and mine damage, Battlemage, Perseverance, physical reduction bypass and elemental ailment swaps apply offline.
- Acrobatics, The Agnostic, Imbalanced Guard, Wicked Ward, Corrupted Soul and Transcendence resolve their defences.
  Two-handed melee and mace stun modifiers, and doubled stun duration, apply on qualifying hits.

- Summoned followers stop pathless walk orders and retry when a route becomes available.
- Spell-totem summons keep their minion identity instead of inheriting their summoning skill's totem flags.
- Held summon casts each raise their minion: the next cast or a walk does not cancel a cast whose mana was paid.
- Righteous Fire's enemy burn with linked supports. Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Increased and reduced damage over time taken on enemies combine with other damage-taken modifiers for persistent
  damage.
- Monster-hit bleeding and poison share timed damage, avoidance and paid flask cleansing; native QA pending.
- Scorch, Brittle and Sap from chance, crit (Secrets of Suffering) and block sources on monsters; Scorch and Brittle
  on the player; not checked live. Docs: [20](../docs/protocol/20-damage.md) "Scorch, Brittle and Sap".
- Selected worn skills and Cat/Avian/Spider/Crab owners (#504). Docs: [11](../docs/protocol/11-skills.md).
- Embrace Madness and self Vulnerability (#504). Docs: [11](../docs/protocol/11-skills.md).
- Incoming bleed/poison and qualified Elusive (#504). Docs: [20](../docs/protocol/20-damage.md).
- Divine Blessing (including March of the Legion) pays a one-time cost and grants one temporary Blessing;
  Vaal Haste has a soul-paid temporary aura. Smite has separate melee/lightning hits and a timed on-hit aura.
  Smite impacts and Vaal Haste's speed buff are verified live; complete build and party parity remain partial.
- Elemental Hit (melee): chooses one element per swing and filters damage after conversion, then hits the area around
  a successful strike. A pre-existing ailment of the chosen element increases the radius. The selected element agrees
  with the supported client's predicted action.
- Static Strike (ordinary): melee hit grants `static_strike_buff` bound to the client's `StaticStrikeNew` action and
  runs data-driven periodic chained beam hits (`Modules/Core/Game/Skill/StaticStrike.cs`, live-verified).
  Gathering Lightning gains a timed stack on melee contact and emits a beam from that contact. Its chain allowance
  and hit and ailment damage grow with the stacks already held. Docs [11](../docs/protocol/11-skills.md).
- Boneshatter (Regular, Complex Trauma, Carnage) and Trauma Support resolve stun pulses, independent Trauma stack
  lifetimes, added physical attack damage and stun duration per Trauma, and physical self-hits. Docs: [11](../docs/protocol/11-skills.md#boneshatter).
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
- Ice Shot damages enemies in a cone behind the impact. Ice Shot of Penetration applies two sideways cones.
- Lightning Arrow of Electrocution applies its timed strikes to the enemy where the arrow stops.
  Docs: [11](../docs/protocol/11-skills.md) Â§10.
- Dual Strike applies its full-life damage and critical chance to each target at contact.
- Multi-strike skills (Double Strike): each strike is its own hit, sharing one critical roll. Either Impaling strike
  can reflect stored physical damage to nearby enemies on a kill.
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
- Survivors league, the mod `survivors` in LocalExile-Mods. Docs: [Survivors league](../docs/game/survivors-mode.md).
- Attack-speed admission and projectile speed for gem attacks. Docs: [11](../docs/protocol/11-skills.md) Â§10.
- Skill area and duration scaling: the player's increased area counted once at every area site, type-scoped area,
  supports' more or less area, Duration masteries and warcry and Vaal duration (not checked live).
  Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Lifetap Support and Blood Magic publish converted wire `life_cost` / `life_cost_per_minute` (zeroing wire mana cost)
  so the client spends Life only; Blood Magic also removes all Mana, gives 10% more maximum Life and reserves Life
  instead of Mana. Docs: [25](../docs/protocol/25-gem-supports.md).
- Movement, travel and stance skill cooldowns read their recovery stats. Spell and curse costs read their efficiency.
  Mark waivers, Cannibalistic Rite, the Covenant and Rathpith Globe use the server's resource admission.
- Vile Reinvigoration grants Energy Shield regeneration during the recent-kill window (checked offline).
- Energy Shield skill costs (Energy Blade, Wild Strike of Extremes, Replica Covenant) and Eldritch Battery: costs paid
  from Energy Shield first, hits past it to Life, its Mana share taken from it, 50% less recharge (not checked live).
  Eternal Youth redirects recharge to Life and reduces regeneration and total Life leech. Immortal Ambition
  prevents shield recharge and regeneration and redirects retained Life leech at full Life ([15](../docs/protocol/15-progression.md)).
  Docs: [20](../docs/protocol/20-damage.md).
- Area skills include their dedicated knockback chance on accepted hits (checked offline).
- Bow arrow counts from bow and quiver modifiers. Docs: [11](../docs/protocol/11-skills.md) Â§10,
  [RE count inputs](../docs/re/projectiles/count-stat-inputs.md) Â§3.
- Explosive Arrow adds fuses on terminal contacts. The first fuse times the combined blast, and hitting the fuse cap
  detonates it early.
- Forbidden Rite and Soul Sacrifice lob chaos explosions and apply their caster-specific backlash (checked offline).
- Ensnaring Arrow tethers a surviving final target until it leaves the anchor's radius. Its projectile attack
  damage taken modifier does not multiply with each additional tether.
- Tornado Shot's terminal secondary arrows and Cloudburst's landing volley.
  Docs: [11](../docs/protocol/11-skills.md) Â§10,
  [RE terminal geometry](../docs/re/projectiles/tornado-geometry.md).
- Kinetic Bolt's zig-zags (including multi-projectile volley parity). Docs: [11](../docs/protocol/11-skills.md) Â§10.
- Mirage Archer spawns non-blocking on the client (`Positioned.blocking = false`, `flags1 0x08`) so it does not trap
  player movement onto diagonals. Docs: [11](../docs/protocol/11-skills.md) Â§10.
- Ice Nova of Frostbolts expands from up to two active player Frostbolt projectiles near the aimed point with its native
  more-damage multiplier (not checked live). Docs: [11](../docs/protocol/11-skills.md) Â§9a.
- Double Strike of Momentum gains stages per started pair of swings, including Multistrike repeats. The stage counter,
  attack cadence and skill panel follow each gain and reset on movement.
- Glacial Hammer's third strike amplifies Freeze duration from hit damage, and strikes shatter frozen non-unique
  enemies already below one third life. Shattering guarantees the critical roll of every third consecutive strike.
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
- Ice Spear uses a distance-based second form, and Splitting's children enter it at contact. Trarthus Spectral Throw
  returns to its trap site.
- Spectral Helix uses the client's stepped spiral and reverses at terrain. Trarthus starts each spiral at its
  triggering trap.
- Elemental Hit of the Spectrum bursts around a successful projectile hit and counts ailments on each victim.
  Prismatic clones burst with their own radius and rolled element.
- Venom Gyre catches returning blades, releases them with Whirling Blades and replenishes them during the Vaal buff.
- Vaal souls (#308), Vaal Ground Slam's radial wave, Vaal Fireball, Vaal Spark and Vaal Discipline (allied recharge).
  Docs: [27](../docs/protocol/27-vaal-souls.md),
  [Behemoth](../docs/game/behemoth.md).
- Cold Snap leaves chilled ground with cold damage over time and grants Frenzy Charges for kills in it.
  Vaal Cold Snap casts; its ground follows the caster and grants a Frenzy Charge each second an enemy is in it.
  Both land at the last visible cell when aimed past a wall, and the ground uses the worn skin's art.
- Tectonic Slam uses a forward fissure with seeded branches.
  Contacts advance along each branch and one use can contact each target once.
- Sunder and its variants time their wave segments and victim shockwaves.
  Earthbreaking keeps overlapping uses, and Trarthus can redirect at enemies and terrain.
- Ice Crash lands in three timed circles with one contact per enemy.
  Cadence uses the swinging hand's radius and requires two compatible weapons.
- Consecrated Path and its Endurance variant select a target near the aim, land at contact, and create timed ground.
- Earthshatter and both variants resolve travelling fissures and spikes triggered by slams, warcries or the timer.
- Bonespire Support raises bone spires on supported self-used warcries.
  The player's slams detonate the spires and inflict bleeding.
- Perforate resolves repeated Blood spikes and single-hit Sand rays.
  Duality adds spikes for four seconds after a stance change.
  Bloodshed always uses its slower, wider Blood sequence.
- Ground Slam's forward wave scales with area and gains more damage near the player.
  Area attacks publish their final radius.
  Volcanic Fissure hits along its path and launches landing projectiles at the end.
  Snaking hits along each leg and erupts, seeking a reachable target outside the previous eruption's hit set.
  Gem melee swings keep their hit over a queued Move.
  Docs: [11](../docs/protocol/11-skills.md) Â§9a.
- Lacerate and its variants resolve two forward waves at their animation contacts. The overlap can take both hits,
  and stance changes update the native angle and Blood Stance bleeding modifiers.
- Cleave hits in a forward semicircle. Quality counts nearby enemies up to the radius cap, and Cleave of Rage
  needs an axe and sword pair and adds reach at its Rage thresholds.
- Shield Crush and Shield Crush of the Chieftain use their three ShieldBash wave events. Each enemy can be selected by
  the centre wave and one side wave. Docs: [11](../docs/protocol/11-skills.md) §9a.
- Purifying Flame variants hit along their path and at the impact, create Consecrated Ground and only hit fresh targets
  on Consecrated Ground with the shockwave. Self, triggered, totem, trap and mine casts share the same targeting rules.
- Wave of Conviction resolves a travelling cone with the strongest elemental Exposure. Trarthus contacts once on each
  leg of the ring. Each caster's new wave replaces its previous one.
- Crackling Lance resolves its beam geometry, native Intensity, proxy casts, and Disintegration Shock cap.
- Manabond resolves its mana-based cross and base Lightning Damage on direct, triggered, and placed casts.
- Heavy Strike and Trarthus confirmed-hit particles/audio, with typed and critical melee feedback. Single-clip Heavy Strike stances use their ACT contact.
  Docs: [11](../docs/protocol/11-skills.md), "Hit effects".
- Seismic, Ancestral, Infernal and Battlemage's Cry grant their timed buffs to the caster and nearby allies.
- Infernal Cry exerts six melee attacks and triggers Combust on the first melee hit of each attack.
- Infernal Cry marks enemies for a timed fire explosion that consumes their corpses on death.
- Battlemage's Cry triggers one linked spell on each of five exerted attacks' first melee hits, with the spell's cost.
- Shockwave supports spend a stored use on a melee hit, and Void Shockwave repeats from enemies hit by its first wave.
- Pyre Support triggers successive flame pillars on supported melee hits against already ignited enemies.
- Limbsplit and The Cauteriser trigger their own Gore Shockwave on a melee hit with at least 150 Strength.
- Poison hits and spread use increased Poison Duration. Viper Strike of the Mamba applies one poison per hit
  to a target with no active poisons. Unnatural Strength grants minions conversion and Wither on hit.
- Intimidating Cry applies War Bringer's exertion count and carries the spent exertion
  to the attack's melee and projectile hits for Overexertion. Docs: [11](../docs/protocol/11-skills.md) Â§10i.
- Seismic Cry gives six melee slams more area after release, independently of the timed armour buff (predictive and lockstep).
- Ancestral Cry gives eight melee strikes two additional targets after release, independently of the resistance buff (predictive and lockstep).
- Exerted attacks publish the client's `display_num_empowered_attacks` counter per warcry and apply passive/item exerted-attack modifiers across slams and strikes; Automation and Autoexertion publish linked counts, active toggle buffs, reservations, area carry, and cooldown triggers.
- Strike targeting: spirit strikes and the previous location. Frost Blades and Lightning Strike resolve their extra
  melee contacts and projectile volleys. Docs: [11](../docs/protocol/11-skills.md) Â§10d.
- Passive enemy auras. Docs: [11](../docs/protocol/11-skills.md) Â§10e.
- Passive-triggered skills: Ancestral Commander's Spirit chieftains.
  Docs: [11](../docs/protocol/11-skills.md) Â§10f, [20](../docs/protocol/20-damage.md) Â§2e.
- Player minions: Conviction novas, Multistrike, Ruthless and Melee Splash. Docs: [11](../docs/protocol/11-skills.md) Â§10h.
  Infernal Legion causes self-burn and burning damage nearby, and Minion Instability explodes a living minion at Low Life.
- Life Recoup. Docs: [20](../docs/protocol/20-damage.md) Â§5.
- Master Toxicist, Call to Arms, chaos pair resistances, chaos Energy Shield opt-out; Mana Recoup before Ward.
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
- Bloodbond's Blood Offering consumes up to four corpses and grants minion damage and life regeneration
  based on life sacrificed.
  Spell Totem uses its own life, and support traps and mines can apply the offering.
- A self-cast Bodyswap of Sacrifice moves to a living target at contact and resolves spell damage at both endpoints.
  A selected damageable minion is destroyed and contributes a separate secondary explosion.
  Totems, traps and mines use their own departure point, and linked triggers move the player.
- Doryani's Touch uses the gloves' unarmed lightning damage and requires both hands to have no weapon.
- Lightpoacher can earn Spirit Charges on kills with an equipped Abyss Jewel. An accepted skill use can spend a
  live charge to launch Spirit Burst.
- Approaching Flames from Annihilation's Approach toggles Flame-Touched, its Adrenaline and its fire damage.
- Owned minions: relics, chieftains and the 17 golems. Docs: [11](../docs/protocol/11-skills.md) §10h.
  Mindless Aggression and minion area mastery scale minions. Fresh Meat grants timed Adrenaline and Wakened Fury.
  Ice Cyclone leaves chilled ground along the minion's path.
  Flame Golem of the Meteor lands on clear terrain within its summoner's skill range.
- Straight minion projectiles need a clear shot. The Wretched Defiler spectre fires its timed spiral and supports Spell Echo and Returning Projectiles.
- Spectre roars grant native charges ([minions](../docs/protocol/11-skills.md#10h-player-minions-that-fight)).
- Itemised corpses raise native spectres ([minions](../docs/protocol/11-skills.md#10h-player-minions-that-fight)).
- Wraithlord counts socketed Ghastly Eyes ([minions](../docs/protocol/11-skills.md#10h-player-minions-that-fight)).
- Holy Strike summons weapon copies ([minions](../docs/protocol/11-skills.md#10h-player-minions-that-fight)).
- Holy Relic nova reaches allies ([minions](../docs/protocol/11-skills.md#10h-player-minions-that-fight)).
- Phantasmal Might follows active Phantasms ([minions](../docs/protocol/11-skills.md#10h-player-minions-that-fight)).
- Equipped summons use active sources ([minions](../docs/protocol/11-skills.md#10h-player-minions-that-fight)).
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
- Frost Blades of Katabasis checks its primary strike reach and selects one enemy before supported extra strikes.
- Lightning Strike resolves its melee hit and secondary projectiles.
- Infernal Blow charges detonate on expiry or the sixth charge, and marked corpses explode in a separate radius;
  Immolation charges the attacker.
- Pestilent Strike spreads a marked corpse's remaining poison damage as a separate chaos debuff.
- Lightning Strike of Arcing resolves the melee strike and the bolt's escalating chain damage.
  Docs: [11](../docs/protocol/11-skills.md).
- Molten Strike and its Zenith and Vaal versions: a melee hit that launches magma balls from the struck enemies, and
  Zenith's fifth-attack buff. Docs: [11](../docs/protocol/11-skills.md) Â§10d.
- Wild Strike and Extremes resolve contact and follow-ups. See [projectile skills](../docs/game/projectile-skills.md).
- Rain of Arrows, Blast Rain and Toxic Rain resolve individual landings, including their Royale gems.
  Toxic Rain pods slow, deal chaos damage over time and burst. Concentrated Effect scaling and owner/proxy
  damage restrictions await a client check.
  The arrows fall before they land, and Toxic Rain's pods grow and burst, for every skill effect.
  The arrow the client flies up is stood in for by the falling art; Rain of Arrows and Blast Rain shoot nothing up.
- Rain of Arrows of Artillery lands along the shot direction; Saturation lands in expanding pairs around the aim.
- Blast Rain of Trarthus applies a burn on the single arrow's landing hit and on targets its forward fire wave reaches.
- Storm Rain and its transfigured gems leave arrows that fire periodic area hits between their endpoints.
- Conflagration leaves arrows that detonate near the caster, setting off adjacent arrows and applying a burning debuff.
- Kinetic Rain variants land delayed area hits. See [projectile skills](../docs/game/projectile-skills.md).
- Explosive and Poisonous Concoction land area hits and spend charges for their attack bonuses. Bouncing chains along
  the ground.
- Thunderstorm blinds inside the storm, grows while occupied, and releases Thunderburst on a timely full-stage exit.
- Voidstorm from supported rain landings grows and blinds enemies until its owner enters and detonates it.
- Windburst triggers after the owner walks the required distance and hits with a supported attack,
  releasing piercing projectiles that burst at expiry.
- Hiveborn Support summons temporary cold-attacking minions after the player's supported offering succeeds.
- Kinetic Fusillade and Detonation refresh their capped hover queues and discharge in sequence.
  See [projectile skills](../docs/game/projectile-skills.md).
- Kinetic Instability supports place Kinetic Flux on the linked wand attack's killing hit or, for Greater,
  its critical hit.
- Scourge Arrow and its Royale variant plant pods on release based on channel stages; Scourge Arrow of Menace plants
  a pod per arrow. See [projectile skills](../docs/game/projectile-skills.md).
- Somatic Shell absorbs other hits and transfers on depletion. Its own skill damage bypasses existing shells.
  See [projectile skills](../docs/game/projectile-skills.md).
- Caustic Arrow stops at the aimed point and leaves caustic ground: chaos damage over time that does not stack.
  The ground is the art's pieces laid over the radius, for the worn skill effect; the layout is the server's own.
  Only the default, Celestial and Tentacles grounds fade in and out.
- Projectile modifiers and named projectile skills. Docs: [framework](../docs/game/projectile-framework.md),
  [skills](../docs/game/projectile-skills.md), [20](../docs/protocol/20-damage.md) Â§3.
- Soulrend and its Spiral variant turn toward nearby enemies ahead and renew a non-stacking chaos debuff along each
  flight. Spiral releases each projectile on its own timed angle.
- Pact of Ghorr empowers eligible self-cast damage-over-time spells. Casting a different empowered spell raises
  Hunger's trigger rate. Fleshrend flies periodically and stacks physical damage over time and increased damage over
  time taken.
- Pact casts grant timed Afflictions. Unholy Accomplice grants Boons in their place and improves Pact casting and
  recovery speed. K'Tash empowers the next damaging Vaal spell, reducing its soul gain prevention and refunding its
  spent souls. Lycia empowers the next three hand-cast channelling spells and triggers Heaven's Scourge during the
  channel and again on release.
- Beidat empowers eligible non-Vaal, non-Brand hand-cast spells with radial projectiles, spiral areas or additional
  beam chains and increases the corresponding hit and ailment damage.
- Bow and wand attack gems without a projectile row (Frenzy, Puncture, Power Siphon) fire the held weapon's
  projectile (not checked live). Docs: [skills](../docs/game/projectile-skills.md).
  Frenzy gives one Frenzy Charge per use and its more attack damage and speed per charge.
  Puncture always bleeds, for its skill duration and with its more bleeding damage.
  Power Siphon fires one projectile at each of the nearest enemies ahead, with its per-Power-Charge criticals.
- Doomfletch's weapon physical gain reaches attacks as separate fire, cold and lightning damage.
- Equipment modifiers scale from final attributes, Oskarm uses final Accuracy for attack critical chance and
  its item-local Assassin's Mark level for qualifying hits by the wearer, and
  Mask of the Stitched Demon uses full maximum Energy Shield for Life regeneration.
- Blink Arrow and Mirror Arrow create their clones at arrow contact, with the native attack and projectile timing.
  Blink Arrow, including the Royale gem in an existing save, moves the player in the same update and leaves its clone
  at departure.
  Clones use arrows, arrow rain or elemental shots.
  Lightning Warp queues teleports and hits both endpoints.
- Instant skills, shared Blink and Guard cooldowns (a guard skill cast from the bar as well as one a trigger releases),
  opt-in warcry sharing and cost admission, Bladefall of Trarthus and both Frostblink hit and chill areas, with clear
  landings across terrain gaps, walk-only door crossing (prison grates via layer-1 sight barriers) while stopping before
  solid walls and projectile-blocking doors with the 5-cell minimum displacement check, Flame Dash 25/60-cell sight-line
  and fallback clamping, pre-cooldown admission, and burning-ground segment DoT, sight-aware movement landing fallback,
  monster-blocker landing slide, and same-frame `0x02bc` cooldown refreshes queued behind `0x01ce` so predictive clients
  execute the blink (verified live). `Frostblink of Wintry Blast` (`FrostblinkAltX`) teleports at cast contact with
  32/42-cell IceDash clamping, hits both ends at 20 base radius without chilled ground, scales hit damage per 5% target
  chill and removes chill on hit (verified live). A guard's bar entry is held at the
  skill's own cooldown with a zero recovery rate while its buff runs and counts at the character's recovery after it, as
  the official client shows. The held entry is published before the instant action so the client skips its press
  prediction, and a stat-only skill refresh (a guard or self buff starting/ending) no longer rebuilds the skill bar; a
  skill-list refresh during the buff (movement, conditional item stats) re-publishes the held entry, since the client's
  skill replacement would otherwise refresh its zero-rate entry: the ring does not sweep or replay on the first press or
  while moving (#1127).
  A stat-value-only skill refresh (flask, charge, aura, Rage) sends one `0x01b4` per changed stat set, not the whole
  `0x02bc`; every other case sends the whole list.
  Blink cooldown refreshes use the same gem copy as the shared debit.
  Docs: [11](../docs/protocol/11-skills.md) §9, §11.
- Vaal Immortal Call: 100 souls, its 60 s cooldown, 8 s of soul gain prevention, a duration from the Endurance Charges it
  discharges, and Life held at 1 while the buff runs. Docs: [11](../docs/protocol/11-skills.md) Â§11, Â§12.
- Vaal Molten Shell: soul-paid armour and hit absorption, fire reflection each second and on removal, and a single
  active guard buff with separate Vaal and regular cooldowns.
- Vaal Sweep repeats its attack and knocks back eligible enemies. Vaal Lightning Warp damages and moves nearby
  enemies without moving its caster. Vaal Ancestral Warchief slams, leaps and grants its owner more melee damage
  while active. Offline checks cover the server's timing, damage and movement.
- Steelskin, Immortal Call and Molten Shell cast by hand start the triggered guard buff; a running or recovering
  guard refuses the next before its cost. Immortal Call also sends its cast art, whose sound events are the
  activation sound an instant skill's own 0x1ce does not play. Docs: [11](../docs/protocol/11-skills.md) §12.
- Phase Run: movement speed and Phasing for its duration, longer per Frenzy Charge consumed; the next skill use
  swaps it for its more melee physical damage (not checked live). Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Crushing Fist, Glacial Shield Swipe, Swordstorm, Eviscerate and Shield of Light open a use window on block.
  Swordstorm deals eight timed dual-weapon hits. Glacial Shield Swipe advances a forward cone.
  Eviscerate advances two waves that can each hit a target.
  Shield of Light sends a forward cone and waves from nearby allies in tile entry order on its first contact.
  Crushing Fist fixes its ground position on the first animation contact and hits on the following slam contact.
- Bloodscent applies its Rage grant to the weapon that landed the hit (checked offline).
- Blood Rage: attack speed, physical attack life leech, Frenzy Charge chance on kill and its physical degeneration;
  a kill refreshes it (not checked live). Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Auras, reservation, Pride, heralds (Herald of Thunder's bolts drawn in the worn skin; walls block them) and
  permanent spell buffs. Docs: [11](../docs/protocol/11-skills.md) Â§13.
- Animated equipment, including Self Reflection. Docs: [11](../docs/protocol/11-skills.md) Â§10h.
  Animated Guardian keeps its equipment on death and is unavailable in that non-hideout instance.
- Summons: quality, source recovery, spell repeats, and enemy-seeking undamageable Skitterbots; mines and traps
  Revenants consume nearby allies, Smiting Guardians use Smite, and Slamming Zombies attack between slams.
  (including Fire Trap proxy stats, detonation aim, burning-enemy bonus, throw clamping, and lockstep timing).
  Locus Mine keeps close mines armed and aims detonated volleys at the player.
  Invention scales trap and mine damage by their age at the trigger.
  Docs: [11](../docs/protocol/11-skills.md) Â§10h.
- Bladefall places timed volleys and eligible lingering blades for supported mine, totem and triggered casts.
- Blade Blast chains through up to 50 landed blades across direct, triggered and proxy casts, honoring blade retention.
- Blade Blast of Unloading consumes up to ten orbiting blades in a sequence of expanding blasts.
- Blade Blast of Dagger Detonation adds eligible weapon damage to its nova, including on spell totems.
- Blade Vortex stacks individually timed blades on the caster, scales its pulse with the blade count, and supports
  repeats, triggered casts, spell totems and a held key; one blade buff draws the ring with the blade count and a
  counter icon, and its pulse reaches monster bodies and stops at walls.
- Vaal Blade Vortex pursues nearby enemies and hits periodically from direct casts and paid spell totems.
- Ball Lightning of Static places a finite stationary orb for self-casts, damage triggers and mines.
- Ball Lightning applies repeated bolts with one hit interval per target and release. Orbiting steers one orb along
  a widening spiral, checks clearance at each grid step and shares the client's seeded launch speed for hand casts.
  Static places a finite orb for self-casts, damage triggers and mines and uses the same hit interval.
- Frost Bomb uses its crystal duration and pulse or detonation behavior for supported triggers and totems.
- Detonate Dead and Volatile Dead use eligible corpses and their timed effects across supported cast and proxy paths.
- Cremation variants place finite geysers and resolve their corpse and impact effects.
- Unearth pierces along its flight and creates a usable Bone Archer corpse at the landing.
- Corpse Walk creates ordinary or eligible Spectral Corpses with elapsed walking while its item grant is equipped.
- Damage and stun triggers aim at the hit source when known. Arc of Surging keeps an eligible target through its
  delayed split.
- Shock Nova releases a ring and a larger nova, including when cast by a totem, trap or mine. The ring applies the
  skill's shock chance, and the maximum shock modifier applies to both contacts. Triggered, it draws both in the
  worn skin.
- Flame Surge hits forward and uses an existing ignite for burning ground. Combusting uses the ignite it inflicts,
  with its own creation interval.
- Glacial Cascade uses the same burst pattern for hand casts, spell totems, traps and mines. Its normal and Royale
  final bursts are larger and cannot knock back, and earlier bursts limit knockback against unique enemies.
- Dark Bargain of Trarthus tracks Ruin on the caster. The seventh stack consumes it, sacrifices life and adds base
  chaos damage to that release.
- Arc cannot chain straight back. Arc of Oscillating can, while Arc of Surging turns chain modifiers into more split
  targets. Spell totems, traps and mines use their linked Arc variant. Vaal Arc branches twice without reusing targets
  and grants a timed Lucky Arc buff.
- Summon Reaper of Eviscerating follows its owner, consumes allies and attacks on recast.
- Predator targeting, Feeding Frenzy buffs, aggressive Skeleton Warrior dashes, defensive targeting and Meat Shield proximity damage.
  Docs: [11](../docs/protocol/11-skills.md) Â§10h.
- Minion equipment rules: limits, restrictions, Triad Grip, Speaker's Wreath, Alberon's Warpath, and on-hit ailments.
- Absolution and Dominating sentinels, Phantasms, direct Vaal Skeletons and Vaal Absolution/Domination ascensions.
  HUD minion counter icons (`PlayerMinionsChange`) now publish for all active player minion types, and a placed totem
  is listed under `TotemMinion` until it dies (not checked live). The active and permanent minion counts
  (`number_of_active_minions`, `number_of_active_permanent_minions`) count the same minion roster, with a permanent
  minion being one without a duration.
  Owned minions are created as the official servers send them; a raise removes its corpse, and a raise with no corpse
  in reach is refused before its cost is paid (not checked live).
  A summon that ends in play dies where it stands, and its body is removed 21 frames later (not checked live).
  Docs: [11](../docs/protocol/11-skills.md) Â§10h.
  Vaal minion casts linked to a totem, trap or mine support are refused before spending souls or starting an action.
- Removable player buffs can be right-click cancelled via C→S `0x4b` (Grace Period, Shrines, guards, auras, Arcane
  Surge, phasing; non-removable debuffs rejected; not checked live). Docs: [11](../docs/protocol/11-skills.md) §12.
- Native knockback hit presentation (`0x1af` bit 11 + `u3c`/`u3e`), totem directional `HitAngle`/`HitCode`, `0x1b0`
  `ActionResultFlag` encoder, and chance-based instant cooldown reset sync (`0x50cd`/`0x52e2`; not checked live).
  Docs: [11](../docs/protocol/11-skills.md) §5, §9, §10g.
- Predictive/monster `0x1ae` channel end, Divine Ire `0x233f` stages, Flame Dash `0x2c8` segment (not checked live).
- Incremental `0x00c6` flask charges, `0x0174` item-use results and 0x00bb/0x00bc/0x01c9/0x01ca/0x02d9 encoders.
- Vaal Animate Weapon transforms eligible melee drops and landed Bladefall of Trarthus blades into temporary unique weapons.
- Conversion Trap temporarily changes the triggering enemy's allegiance. Converted enemies can attack hostiles,
  and unique enemies are immune.
- Bear Trap's single secondary hit immobilises the target, then applies a decaying slow and trap/mine hit vulnerability.
  Skewers impales with its secondary hit and shares the killing-blow reflection.
- Blade Trap and its variants sweep with two blades, with their own rotation count or duration and a shared
  per-target hit interval.
- Hexes and marks. Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Totems: Rejuvenation, Arc, projectile spells, supported summons, ballistas, Shockwave, Devouring and Decoy Totem
  (ordinary monster AI), with totem limit, range, duration and Lockstep placement timing. Eligible placements can summon
  two totems. Skill bars without a saved attack-in-place mask use each bound skill's default.
- Retained Royale Artillery Ballista gems place a bow totem whose mortar volley uses the gem's own arrow count and
  range.
- Siege Ballista of Trarthus places a formation within the active totem limit. Each totem fires once at the placement
  aim and then expires.
- Shockwave Totem of Authority centres each nova on its owner's position when the cast releases.
  A lockstep non-spell totem placement's hold takes the client's own Move, as GGG's servers do.
  two totems.
  A Focused Ballista totem fires only when due with an owner attack it has not spent.
  Skill bars without a saved attack-in-place mask use each bound skill's default.
- Searing Bond totems burn enemies along their links, and Searing Bond of Detonation leaves a timed area burn at cap.
- Artillery Ballista's mortar volleys, including Cross Strafe and Focus Fire (checked offline).
  Docs: [11](../docs/protocol/11-skills.md) §10g.
- Ballista and Spell Totem Support totems take the client's model for the skill, Rain of Arrows places as a ballista,
  and players walk through their own totems and minions, which still block enemies.
- Minions inherit spell suppression chance and the minion damage over time multiplier from their owner.
- Holy Flame Totem and its Ire variant use their native projectiles. Leaving the area retires totem objects and their
  ground effects.
  Docs: [11](../docs/protocol/11-skills.md) Â§10g.
- Passive self buffs. Docs: [11](../docs/protocol/11-skills.md) Â§12.
- [World effect rules](../docs/dev/combat-effect-rules.md) drive Onslaught and basic percentage-based charge gains on kill and their shield, dual-wield, frozen-enemy and poison-count variants, item-granted marks on hit and Svalinn's socketed spell on block. Player and hostile monster skill hits share the Frenzy and Power charge rules. The separate Power-on-hit roll is player-only. Ice Bite uses the same frozen-kill rule for a player and a supported minion; the killer gains the Frenzy Charge.
- Onslaught on kill and from a Silver Flask. Docs: [13](../docs/protocol/13-items.md) Â§8a.
- Rampage and Soul Eater from item stats, with Soul Ascension's souls from hits on rare and unique enemies.
  Docs: [11](../docs/protocol/11-skills.md) Â§12.
- Stats: the client's stat keys and the player's base pools, plus item-requirement stats (`main_hand_weapon_type`,
  `virtual_*_for_requirements`), dual-wield snapshot sync, and light radius virtual stats (`virtual_light_radius_+%`,
  `virtual_blind_does_not_affect_light_radius`; new virtuals not checked live).
  Docs: [11](../docs/protocol/11-skills.md) Â§4, Â§8, Â§14.
- Attack speed passives and attack timing.
  Lockstep: an action during a strike's walk-up or swing starts from the client's cell; cost pays before the 0x1ab.
  Docs: [11](../docs/protocol/11-skills.md) Â§4.
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
- Cyclone and Cyclone of Tumult convert extra melee strike range into increased area.
  Tumult gains and loses stages on the area clock, with per-stage radius, attack speed and movement speed.
  Docs: [11](../docs/protocol/11-skills.md), "Cyclone area updates".
- Player-cast Blade Flurry and Blade Flurry of Incision with aimed acquisition, paid stages, and release strikes.
  Docs: [11](../docs/protocol/11-skills.md#blade-flurry).
- Both Charged Dash gems channel in place, hit along the steered path and release a final wave. Only the base gem teleports.
  The normal gem moves to a legal landing; the Projection variant stays in place.
  Docs: [11](../docs/protocol/11-skills.md#charged-dash-projection-and-release).
- Player-cast Flameblast gains paid stages and explodes at its aimed location on release. Celerity also explodes at
  maximum stages, and Contraction loses radius as it stages.
- Player-cast Lightning Tendrils follows the channel aim, with gem-specific strong pulses and Escalation radius growth.
- Player-cast Blight applies independently expiring damage layers and Hinder in front of the player.
  Atrophy reduces life regeneration during Hinder, and Contagion carries active Blight of Contagion layers.
- Player-cast Scorching Ray steers while held and refreshes burn stages on targets it touches. The ordinary ray applies
  Fire Exposure at its stage limit.
- Player-cast Incinerate builds paid stages, widens and lengthens while held, and releases an igniting wave.
  Expanse releases a wider-ranging, slower wave. Venting sustains a longer cone without a release wave.
- Player-cast Storm Burst emits independently timed orbs that jump with the aim while held. Repulsion jumps away.
  Releasing the channel detonates the active orbs with their remaining-jump multiplier.
- Wither pulses follow the channel aim. Players and spell totems apply Hinder and separately expiring Withered stacks.
- Channel-dependent item modifiers follow the player's active channel through release, interruption and skill removal.
- Self-targeted spells, including Convocation and corpse offerings. Docs: [11](../docs/protocol/11-skills.md) §9a.
- Winter Orb. Docs: [11](../docs/protocol/11-skills.md) §9a.
- Dark Bargain (self-cast only; Summon Skeletons does not work yet). Docs: [11](../docs/protocol/11-skills.md) §9a.
- War, Dread and Defiance Banner spend melee Valour to plant a local aura with a finite radius and duration.
- Brand system: Storm Brand (+ Indecision), Armageddon Brand (+ Volatility, Recall), Penance Brand (+ Dissipation,
  Conduction), Wintertide Brand, Arcanist Brand, Brand Recall and Brandsurge with attachment, limits, Swiftbrand
  cadence, `ActiveSigil`/`magma_pustule`/`immolation_brand_burn` buffs and `SigilDisplay` counters. A brand whose
  enemy dies returns to its cast or recall position.
  Base Penance Brand and Brand Recall were exercised against Amarissa in Predictive and Lockstep on the
  3.29.3d DX12 client. Conduction currently spreads only from its attached monster to two nearby targets.
  Arcanist Brand requires a supported trigger executor for each linked spell.
  Recalling a brand supported by Foulgrasp creates hands that detonate once when an enemy enters range.
  Docs: [11](../docs/protocol/11-skills.md).
- Prismatic Burst triggers on a supported player attack hit and rolls one element using squared attributes and Combat Focus.
- Enemy hits trigger Avenging Flame on supported totems, scaling fire damage with the totem's maximum life.
- Sacred Wisps take their lifetime from the linked summon skill and preserve their shared limit when switching attacks.
- Snipe (`channelled_snipe` gem and Assailum) charges stages and fires on release. Interrupting the channel clears the
  stages.
  General's Cry (`generals_cry` + variants) selects corpses for non-blocking Mirage Warrior melee execution
  (not checked live). Docs: [11](../docs/protocol/11-skills.md).
- Self-targeted spells, including Convocation and corpse offerings. Docs: [11](../docs/protocol/11-skills.md) Â§9a.
- Winter Orb. Docs: [11](../docs/protocol/11-skills.md) Â§9a.
- Dark Bargain (self-cast only; Summon Skeletons does not work yet). Docs: [11](../docs/protocol/11-skills.md) Â§9a.

- Frozen Legion and Rallying spend stored uses per placed statue; each statue steps inward and attacks once with
  Frozen Sweep.
- Whirling Blades selects targets along its resolved slide and hits them in travel order.

## Not working yet

- Projectile spawn height on platforms and doodads: the server has no surface-height model (#182).
  Docs: [11](../docs/protocol/11-skills.md) Â§10.
- Spirit of Utula's Leadership and Rakiata's Mark trigger need their own chieftain executors.
  Docs: [11](../docs/protocol/11-skills.md) Â§10f.
- Where Flicker Strike lands beside the monster is the server's guess, and its buff ignores increased buff effect.
  Docs: [11](../docs/protocol/11-skills.md) Â§9a.
- Effects attached to bones (Righteous Fire's orbs) are not sent.
  Docs: [11](../docs/protocol/11-skills.md) Â§12.
