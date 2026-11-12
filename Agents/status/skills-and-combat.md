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
- Projectile modifiers and named projectile skills. Docs: [framework](../docs/game/projectile-framework.md),
  [skills](../docs/game/projectile-skills.md), [20](../docs/protocol/20-damage.md) §3.
- Instant skills, cooldowns, Bladefall of Trarthus and Frostblink. Docs: [11](../docs/protocol/11-skills.md) §9, §11.
- Auras, reservation, Pride, heralds and permanent spell buffs. Docs: [11](../docs/protocol/11-skills.md) §13.
- Animated equipment, summons (including source recovery and spell repeats), mines and traps. Docs: [11](../docs/protocol/11-skills.md) §10h.
- Hexes and marks. Docs: [11](../docs/protocol/11-skills.md) §12.
- Totems: Rejuvenation, projectile spells and ballistas. Docs: [11](../docs/protocol/11-skills.md) §10g.
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
