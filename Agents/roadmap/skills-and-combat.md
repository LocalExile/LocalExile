# Roadmap: Skills, auras, stats and combat

Part of the [Roadmap](../Roadmap.md); what already works in this area is in
[status/skills-and-combat.md](../status/skills-and-combat.md).
Items keep the Roadmap's priority groups, and are in rough priority order within each group.

## 1. Quality of life

Aura intent persistence, town suppression and death reset are implemented;
validate town/combat/character-switch round trips in the real client (protocol/11-skills.md).

## 3. Gameplay

- **Ordinary aura activation:** nine previously rejected self auras now share reservation, presentation and the
  supported stat/damage paths.
  Remaining work: Discipline's ES pool, Pride's enemy debuff, conditional consecrated ground, herald/minion/banner
  mechanics.
  See protocol/11-skills.md section 13.
- **Aura caster art:** the distinct EPKFiles2 visuals are selected on activation and area restoration;
  the live source-only regression is addressed with a separate received buff for the top-left icon, awaiting
  confirmation.
  Radius-scaled aura rings, if added, still need their actual radius.
- **Completed area fix:** Cyclone's `/set_aoe` final radius is now sent to the client and shared with hit selection;
  see Status and protocol/11-skills.md (live check pending).

### The player's real stats

- **Known:** client stat key = Stats.dat row + 1 ([11](../docs/protocol/11-skills.md) §8); `/set_stat` takes names.
  The server must send final values (the client derives none): the attack's are computed in
  `SkillCatalog.Unarmed.Stats`.
- **Done when:** life, mana, attributes, resistances and their derived values come from the server's own calculation
  (Characters.dat base values, level, items, passives) and are sent in the Stats component.

### Auras: reservation and effects

- **Known:** `GrantedEffectsPerLevel` carries the reservation; the Life pool's reserved field and S→C 0x1b1 update it;
  Purity of Elements/Fire/Ice/Lightning, Clarity, Vitality, Precision and Haste are implemented with their buff
  visuals and `/auras`, awaiting live verification ([11](../docs/protocol/11-skills.md) 13,
  [Status](../status/skills-and-combat.md)).
- **Start:** the auras' 3D effects (BuffVisuals EPKs of the `IsSkillBuff` rows do not render, unlike Righteous
  Fire's); the combat auras (Anger, Hatred, Wrath, Zealotry, Pride), Heralds, banners, curse auras and
  Determination/Grace/Discipline's armour/evasion/ES effects.
  The skill-bar indicator's buff association is implemented (0x1c1 bit3; live check pending, see Status).
  Support gems come with the gem system ("Gems" below); party and minion auras and life reservation with their own
  systems.
- **Done when:** the standard auras reserve correctly, their effects match PoB, and the character panel and HUD show
  the same reserved and unreserved values.

### Damage model (foundation done, deeper defence next)

- **Done:** hits are per-type `DamagePacket`s; conversion follows physical, lightning, cold, fire, chaos, including
  chained conversion, the 100% cap and gain-as-extra.
  DoT/ailment formulas and fractional ticks are in the same model, and per-type resistance mitigation works on
  packets.
  The default attack, Fireball and monster melee use packets and read the player's/skill's conversion stats
  ([20](../docs/protocol/20-damage.md)).
  The player's real life, resistances, armour and evasion are computed from level, passives and base equipment, and
  armour/evasion mitigate monster hits (Status: Armour and Evasion).
- **Known:** both sides still have flat damage ranges ([11](../docs/protocol/11-skills.md) 5, 6);
  ordinary player ES absorption, regeneration and interrupted recharge are implemented and live-checked.
  Special ES mechanics, penetration, exposure, ailment application, added damage and support modifiers remain open.
- **Start:** special ES mechanics, penetration/exposure and the remaining conditional critical strike stats
  from the stats both sides already carry; monster skills beyond the first granted effect (ranged, spells).
  Spell base critical chance now comes from each skill's level and shares its panel/combat calculation.
  Native skill switching, combined bonuses, no-crit, relog and hitless-spell controls are verified.
  Added-base, lucky/more and conditional crit remain separate work.
  Add support effects through `DamagePacket.Add`, `Scale`, `Convert` and `AddGain`.
- **Done when:** damage numbers match the character panel and the monsters' levels.

### Death screen variants

- **Known:** S→C 0x55 flags bit0/2/3 are mapped ([11](../docs/protocol/11-skills.md) §7);
  bit4 and the trailing u32 / u8 / u16 are not.
- **Done when:** Sanctum and other modes show their own options; hardcore's "Exit" works (with Esc → Exit, C→S 0x153).

### Skill projectiles

- **Known:** Fireball and a bow's `DefaultArrow`/attack-gem projectiles are server-made objects
  ([11](../docs/protocol/11-skills.md) §9–10); attack gems use the `attack1_bow` contact and the weapon's
  damage; on natural ground the spawn height is the captured terrain height + 5 steps.
  Ordinary player and skill projectile-speed bonuses affect flight and predicted arrival together; baseline/+100%/-50%
  Fireball casts were checked live, while conditional/more/less speed and support application remain open.
  Swept relative-motion checks run throughout flight to the first monster or wall contact, with pause, transfer
  and disconnect handling through the game clock and flight lifetime. Default-attack arrows are visual only.
  Automated checks pass, including nearer interceptors, dead targets and lifetime invalidation.
  Live moving-away misses, later-entry hits, wall impact, native glide stop and pause/resume passed before the
  bow packet/height update; merged bow/collision integration needs a live recheck. Integer monster walk estimates,
  a shared collision radius and straight movement between samples still limit accuracy.
- **Start:** a server-side surface-height/world model for platforms and doodads (#182), height grids for more areas
  (or in the walk-map recorder), and collision with doodads and objects that have their own colliders.
- **Done when:** Fireball and bow arrows look like the real ones in every area: at the caster's torso on terrain and
  platforms, stopping at walls and objects.
