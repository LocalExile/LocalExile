# Status: Items, currency and crafting

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/items-and-crafting.md](../roadmap/items-and-crafting.md).

## Works (verified with the real client)

- Eldritch pinnacle-presence implicits on player equipment apply within 80 cells of a living hostile pinnacle boss.
- Ground items belong to the area instance and stay across logouts, disconnects and crashes; drops play native audio.
  Docs: [13](../docs/protocol/13-items.md) §5.
- Life and mana flasks: recovery over time, flask mods, queueing, full-pool refusal and the drinking sound.
  Docs: [13](../docs/protocol/13-items.md) §8.
- Flask charges from kills, crits, hits and time; towns and hideouts refill them.
  Docs: [13](../docs/protocol/13-items.md) §8b.
- Flask modifier composition (#75): charge gain, recovery rates, duration and effect.
  Docs: [13](../docs/protocol/13-items.md) §8.
- Utility flasks and the Iron Flask's Ward. Docs: [13](../docs/protocol/13-items.md) §8a,
  [Antiquarian](../docs/game/antiquarian.md).
- Items: the start kit, moving, equipping, sockets, dropping, stacks and `/item`.
  Docs: [13](../docs/protocol/13-items.md) §4a.
- Item mods: rarities, mod lists, magic and rare rolls, identification and their stats on the character.
  Docs: [13](../docs/protocol/13-items.md) §2a-§2c, [item crafter](../docs/dev/item-crafter.md).
- Item states: influences, split, synthesised, corrupted, mirrored, fractured, crafted and enchanted mods.
  Docs: [13](../docs/protocol/13-items.md) §2f.
- Game content in world.db (#734), republished on `/reload`.
  Docs: [editable game data](../docs/dev/editable-game-data.md),
  [saves](../docs/saves.md).
- Sacred Orb rerolls of the shared defence base. Docs: [13](../docs/protocol/13-items.md), "Sacred Orb".
- Special defence overrides and ES-to-Life conversion. Docs: [13](../docs/protocol/13-items.md) §2,
  [20](../docs/protocol/20-damage.md), "Player energy shield".
- Gold: per account and league, level/rarity-scaled monster drops picked up automatically in one saved transaction.
  Docs: [04](../docs/protocol/04-player-entity.md) §6.
- Loot: retail currency weights (34 enabled core orbs), gems, flasks, equipment, uniques, campaign and map divination
  cards by area tag, scarabs, stacked drops, and class/ilvl-bounded socket counts and one-socket implicits.
  Docs: [15](../docs/protocol/15-progression.md) §3, [drop data](../docs/dev/drop-data-schema.md).

## Partly working

- Voidbringer loses mana after a successful skill use, independently of the skill cost.
- Equipped jewel, weapon and flask modifiers affect qualifying hits, projectiles and recovery.
- Read-only inventories emit `0x00c2` permissions; Heist NPC equipment handles `0x00fd` and controller `0x002d` moves.
- Soul Ripper spends charges for Vaal souls when allowed, and Forbidden Taste applies its recovery and chaos damage.
- The Wise Oak, Taste of Hate and Soul Catcher apply their qualified damage modifiers during the flask effect.
- Map flask penalties apply to utility and recovery flask effects and to awarded charges.
- Alchemist's Genius: flask use rolls grant (10% effect, 20% charges, 4 s from ReminderText 189), rescaling flasks.

- Native skin-transfer bind/reclaim preserves complete donors across character and stash saves. Optional item-stat
  sources exclude socket/skill hosts; 3.29.3c picker binding and protocol reclaim pass, with the shift-click gesture
  and 3.29.3d picker untested. Docs: [23](../docs/protocol/23-microtransactions.md).

- Unique-flask mechanics; Oriath explosion parity and Replica Rumi's Petrified remain open.
  Docs: [13](../docs/protocol/13-items.md) §8a.

- NPC divination card exchange supports all 456 active 3.29.3d card definitions and keeps the 14 client-disabled
  cards, including all Grand Spectrum bases and corruption implicits; local weights are not official probabilities.
  Docs: [17](../docs/protocol/17-npcs.md).
- Heist enchantment magnitudes and implicit-modifier magnitudes apply through Core Game.
  Current native tooltip comparison is pending ([13 §2c](../docs/protocol/13-items.md)).
- Optional backpack mods can enable the native 20 slots; pickup stacks span both carried grids and quest rewards can enter the enabled backpack. Stored items remain removable after disabling the mod.

- Character-build imports validate native items, trees, tattoo targets and limits, preserving secondary ascendancy and module-owned progress. Runtime golem sessions are cleared.
  Docs: [modding](../docs/dev/modding.md).
- On-kill explosions and Eater of Worlds attribute damage reduction.
  Docs: [13](../docs/protocol/13-items.md), "Kill explosions and attribute damage reduction".
- Conditional item operators and qualified source lifetimes (#504). Docs: [13](../docs/protocol/13-items.md).
- Influenced item mods (Shaper, Elder, conquerors) apply, curses on hit and ground immunities included.
  Not checked live: ground immunities, bleeding removal on warcry.
  Not read yet: nearby-enemy auras, reflect, Unholy Might, Conflux, triggered marks, damage against ailing enemies,
  damage over time with attacks and the weapon-local ailment stats.
- Item modifier effect and affix-cap implicits. Docs: [13](../docs/protocol/13-items.md), "Item modifier effect".
- Utility-flask orbs: Enkindling/Instilling enchantments, incoming Bleed/Poison triggers, and automatic use of full utility flasks.
  Docs: [13](../docs/protocol/13-items.md), "Enkindling Orb and Instilling Orb".
- Tinctures (#564): flask-belt placement, drops, toggle activation/deactivation, Mana Burn / Weeping Wounds drain, live effect scaling, shared cooldown, weapon-scoped stats, unique tincture effects (except Grasping Vines), Nature's Concoction / Colloidal Mixture, and unequip/death/session cleanup; awaiting real-client verification.
- Sacred Orb rerolls: fresh checks of the merged branch, and Ward's character calculations.
  Docs: [13](../docs/protocol/13-items.md), "Sacred Orb".
- Special defence rules and local requirement percentages. Docs: [13](../docs/protocol/13-items.md) §2-§2d.
- Yriel and Beacon creation variants use world-data modifier bundles; their gameplay remains partial.
- Unique items, Mageblood, Headhunter and Replica Headhunter, Bino's Kitchen Knife, The Squire, abyss jewels,
  Paradoxica's double damage (main hand only), Yoke of Suffering's damage taken per ailment type its wearer inflicted
  (no scorch, brittle or sap) and Forbidden Shako's random supports; most uniques' own mechanics are missing.
  Bino's spreads the strongest poison and regenerates nearby players and allied monsters; fresh client checks are pending.
  Every client unique has a definition (from Path of Building, or the PoE wiki for those it lacks) but the maps,
  Impossible Escape, watchstones and the six unique Heist contracts. Docs: [13](../docs/protocol/13-items.md) §2e, §8a.
- Ruthless items, flasks, drops and vendors. Docs: [league modes](../docs/game/league-modes.md).
- Essence crafting. Docs: [13](../docs/protocol/13-items.md), "Essence crafting".
- Quiver eligibility: every hand sends its weapon type. Docs: [13](../docs/protocol/13-items.md),
  "Client weapon-type stats".
- **Jeweller's Orb** (verified live). Docs: [13](../docs/protocol/13-items.md), "Jeweller's Orb".
- **Socket colours** (3.29; verified live). Docs: [13](../docs/protocol/13-items.md), "Socket colours (3.29)".
- **Orb of Alchemy.**
  Docs: [13](../docs/protocol/13-items.md), "Orb of Alchemy", [evidence](../docs/re/alchemy-coe.md).
- **Chaos Orb.** Docs: [13](../docs/protocol/13-items.md), "Chaos Orb", [evidence](../docs/re/chaos-coe.md).
- **Divine Orb** (verified live). Docs: [13](../docs/protocol/13-items.md), "Divine Orb".
- **Orb of Scouring** (verified live). Docs: [13](../docs/protocol/13-items.md), "Orb of Scouring".
- **Orb of Transmutation.** Docs: [13](../docs/protocol/13-items.md), "Orb of Transmutation",
  [evidence](../docs/re/transmutation-coe.md).
- **Orb of Alteration.** Docs: [13](../docs/protocol/13-items.md), "Orb of Alteration",
  [evidence](../docs/re/alteration-coe.md).
- **Orb of Augmentation.** Docs: [13](../docs/protocol/13-items.md), "Orb of Augmentation",
  [evidence](../docs/re/augmentation-coe.md).
- **Regal Orb.** Docs: [13](../docs/protocol/13-items.md), "Regal Orb", [evidence](../docs/re/regal-coe.md).
- **Exalted Orb.** Docs: [13](../docs/protocol/13-items.md), "Exalted Orb", [evidence](../docs/re/exalted-coe.md).
- **Orb of Annulment.** Docs: [13](../docs/protocol/13-items.md), "Orb of Annulment",
  [evidence](../docs/re/annulment-coe.md).
- **Armourer's Scrap** (level-scaled quality not checked live).
  Docs: [13](../docs/protocol/13-items.md), "Armourer's Scrap".
- **Blacksmith's Whetstone** (level-scaled quality not checked live).
  Docs: [13](../docs/protocol/13-items.md), "Blacksmith's Whetstone".
- **Gemcutter's Prism** (not checked live). Docs: [13](../docs/protocol/13-items.md), "Gemcutter's Prism".
- **Ancient Orb** (not checked live). Docs: [13](../docs/protocol/13-items.md), "Ancient Orb, chisels and the Eldritch
  rerolls".
- **Orb of Chance** (local rule). Docs: [13](../docs/protocol/13-items.md), "Orb of Chance",
  [admission](../docs/re/chance-coe.md).
- **Vaal Orb and corruption** (fresh client check pending): full 3.29 retail outcomes across gems, equipment (normal
  through unique, 3.29 white-to-coloured socket corruption, `GenerationType = 5` implicits, Eldritch single-implicit
  replacement, 6-affix rare bricks with 1/36 max-link), maps/MapKeys (unidentified, 8-mod, +1 tier / T16 to Vaal
  Temple, unique maps), jewels (standard, Abyss, Cluster, corrupted-only uniques) and flasks (-10% to +10% quality);
  unidentified items and tinctures are refused, and Hinekora's Lock previews match commits.
  Docs: [13](../docs/protocol/13-items.md), "Vaal Orb and corruption", [notes](../docs/re/vaal-coe.md).
- **Eldritch Ichors.** Docs: [13](../docs/protocol/13-items.md), "Eldritch Ichors and Embers",
  [evidence](../docs/re/eldritch-ichor-coe.md).
- **Eldritch Embers.** Docs: [13](../docs/protocol/13-items.md), "Eldritch Ichors and Embers",
  [evidence](../docs/re/eldritch-ember-coe.md).
- **Cartographer's Chisel, Eldritch Chaos/Exalted Orb and Orb of Unmaking** (local rules, not checked live; the Orb grants one Atlas respec point).
  Docs: [13](../docs/protocol/13-items.md), "Ancient Orb, chisels and the Eldritch rerolls".
- **Orb of Binding, the Eldritch Orb of Annulment and the Tainted family** (local rules; the Tainted orbs live 2026-11-14).
  Docs: [13](../docs/protocol/13-items.md), "Ancient Orb, chisels and the Eldritch rerolls".
- **Enkindling Orb.** Docs: [13](../docs/protocol/13-items.md), "Enkindling Orb and Instilling Orb",
  [evidence](../docs/re/enkindling-coe.md).
- **Instilling Orb.** Docs: [13](../docs/protocol/13-items.md), "Enkindling Orb and Instilling Orb",
  [evidence](../docs/re/instilling-coe.md).
- **Currency results look right in the client** for every implemented currency's main use (fresh client check pending
  for the expanded 3.29 Vaal Orb targets). Currency cannot be used out of a public stash tab; currency other than quality and flask enchants is
  refused on a worn item ("Item is currently equipped."), and on mirrored or corrupted targets with the client's
  reason ("Item is mirrored.", "Target is corrupted.").
- **Glassblower's Bauble** (level-scaled quality not checked live).
  Docs: [13](../docs/protocol/13-items.md), "Glassblower's Bauble".
- **Catalysts and Tainted Catalyst** (not checked live): apply jewellery quality (+5/+2/+1 up to 20%, switching replaces
  type; Tainted rolls random type and 1–20% on corrupted jewellery), with Sinistral/Dextral prefix/suffix scaling.
  Docs: [13](../docs/protocol/13-items.md).
- **Fracturing Orb** (verified live). Docs: [13](../docs/protocol/13-items.md), "Fracturing Orb",
  [the game rules](../docs/re/fracturing-mirror-mechanics.md).
- Mirror of Kalandra copies non-unique equipment, flasks and T1–16 maps, from non-public stash tabs and onto stash
  tabs too. Docs: [13](../docs/protocol/13-items.md).
- **Hinekora's Lock** (server rules verified live; dual-item 0x29e and type 0x29f foresight wired, live check pending).
  Docs: [13](../docs/protocol/13-items.md), "Hinekora's Lock", [evidence](../docs/re/hinekora-lock.md).
- Labyrinth Divine Font crafting (0x246/0x2a2/0x245/0x247/0x2a3) and bench craft award toasts (0x1d6) are wired.
  Live client checks are pending; docs: [21](../docs/protocol/21-hideouts.md).
- **Orb of Fusing** (verified live). Docs: [13](../docs/protocol/13-items.md), "Orb of Fusing".
- Currency metamods: prefix/suffix locks and attack/caster restrictions.
  Docs: [13](../docs/protocol/13-items.md), "Currency metamod protection".
- Repeat currency use by type on passives (`C→S 0x78`) and world objects (`C→S 0x79`) routes to Tattoo/Scouring,
  Strongbox and Essence handlers (offline verified; live client check pending).
- Divine Vessels drop as the one-cell base used by the fragment stash. Both empty base types can capture
  Pantheon souls in matching Atlas maps and unlock upgrades at Sin (soul capture not checked live).
  Docs: [pantheon](../docs/game/campaign/pantheon.md).

- Effect rules: item triggers as rows in world.db ([effect rules](../docs/dev/effect-rules.md)). Onslaught and
  Adrenaline on kill, hit or crit; marks on hit; item-granted skills triggered on hit, crit, kill, when hit, on
  block, on a skill or attack use and on melee hits (Lightning Bolt, Fire Burst, Ignition Blast, Commandment of
  Inferno, Blazing Glare, Shield Shatter, Storm Cascade, Starfall and others, as area hits with an effect object for
  art); Raging Spirits, spiders and wolves on kill; Blood Rage on kill and Shade Form; and the always-on Death Aura,
  Abberath's Fury and Blinding Aura. Seen in the client 2026-11-22 and 2026-11-23. Every art, radius and storm
  number is a guess until its row has `checked = 1`. Mageblood's lines work from any worn item. "Nearby Enemies
  have/take ..." lines (the four resistances, all resistances, physical and elemental damage taken
  and stun recovery) give their rolled value to enemies within the reach their own aura template states (60 cells),
  shown with the template's buff, and "Nearby Enemies are Blinded / Intimidated" lines work as the auras do. 17 map
  modifiers about players ("Players have Point Blank", "Players and Monsters take #% increased Fire Damage") count as
  the player's own stat. 27 flask lines "during Effect" that no code read (leech, regeneration, accuracy, rarity,
  Vessel of Vinktar's lightning lines, Culling Strike, Cannot be Stunned) hold while that flask's effect runs, and
  "when you use a Flask" lines (recover life, chance for Onslaught, remove chill and freeze, ignite, shock or
  bleeding) run on every flask use. Ten stats count as another stat from the table ("increased Global Damage" on a
  weapon, the old leech and spell block names, "+# to Armour and Evasion Rating"). Offline tests only so far.
- Added damage "with Axe / Mace or Sceptre / Staff / Sword Attacks" counts for attacks with that weapon, Overwhelm
  takes its percent off the physical damage reduction a hit meets, and "Minions take #% increased Damage" reaches
  minions. A minion's hit is a hit event of its own actor: "Minions have #% chance to Blind on Hit with Attacks" and
  "... to Hinder Enemies on Hit with Spells" run from it, and minions' attacks Overwhelm. A minion's hit rolls Chill, Freeze and Shock from its cold and lightning
  damage on the minion's own chances, and "Minion Critical Strikes do not deal extra Damage" counts. Offline tests
  only so far.
- An Eldritch implicit "while a Unique Enemy is in your Presence" counts only while a unique enemy is within the
  presence radius, as the Pinnacle Boss ones do. "+# to Level of all Fire / Cold / Lightning / Chaos / Physical Spell
  Skill Gems" raise gems with both tags, and "... all Minion / Melee / Totem / Aura Skill Gems" theirs. Offline tests
  only so far.
- A mod that is an aura stays with its wearer only when its aura template says it reaches the wearer: "Nearby
  Allies ..." lines (Dying Breath, Kingmaker) no longer count for the wearer, and "You and nearby Allies ..." lines
  no longer go missing. Defences "from Equipped Helmet and Gloves / Helmet and Boots / Gloves and Boots" and the
  weapon tree's "more Cast Speed" count. Offline tests only so far.
- Single uniques' lines: "+# Strength / Dexterity / Intelligence Requirement" on the item; increased damage against
  Frozen, Ignited, Shocked or Hindered enemies and with hits and ailments against Bleeding, Chilled or Cursed ones;
  23 "per # Strength / Dexterity / Intelligence" lines from the table; 14 "per Red / Green / Blue / White Socket"
  lines and The Untouched Soul's "per Empty Socket" ones; leech, added damage and attack damage against enemies in
  a state (Shocked, Cursed, Frozen, Bleeding, Maimed, Chilled, Poisoned, Ignited); "at least # Strength / Dexterity /
  Intelligence" thresholds; Rain of Arrows and Toxic Rain on bow attack (Lioneye's Paws), each as one hit around the
  aim; poison chance against Cursed enemies and for attacks at maximum Frenzy Charges; critical strike multiplier
  per 1% block chance; The Tactician's threshold; at the character's own hit, critical strike chance against Chilled
  enemies, life leech on critical strike and "Cannot Leech Life from Critical Strikes"; Replica Windripper's 20%
  against Frozen enemies. Offline tests only so far.
- Fire, Cold, Lightning and Elemental Aegis (Invictus Solaris, Vix Lunaris, Perepiteia, Magna Eclipsis): the shield
  takes its types' share of a hit's elemental damage and is restored after its delay, with the client's own buffs,
  as Physical Aegis does. Not carried across an area change. Offline tests only so far.
- Lines that belong to a ring's slot or to the ring itself, from the `fx_item_local` table while the effect rules are
  on: Shavronne's Revelation's six left and right ring slot lines and Woespike's impale duration. Offline tests only.
- Ground effects from item skills: Consecrate on critical strike (Gifts from Above) and Shock Ground when hit
  (Ylfeban's Trickery, enemies on it Shocked at 15%), on the flask grounds; the radius is a guess. Not seen in the
  client yet.
- Voidfletcher: Void Charges (one every interval up to the maximum, counted in code, not shown on screen) and Void
  Shot for a charge on a bow attack, as one area hit with no art. Ylfeban's Trickery curses what hits you with a random
  hex beyond the curse limit (a rule with `ignore`: `curse_limit`). Offline tests only.
- Damage returned to attackers: "Reflects # Damage to Melee Attackers", "Reflects # Damage to Attackers on Block" and
  "#% of Melee Physical Damage taken reflected to Attacker" (Bramblejack, Wall of Brambles, The Anvil, The Blood
  Thorn, Veil of the Night). Rules of the `damage` action on when_hit and block; the monster
  mitigates it and it is not a hit of the character's. Queen of the Forest's movement speed per evasion and Razor of the Seventh Sun's melee physical damage
  against ignited enemies. Offline tests only.
- Keystones from items and cluster jewels: Inner Conviction (3% more Spell Damage per Power Charge, Power Charges
  instead of Frenzy Charges) and Kineticism (attack projectiles always bleed, maim and knock back; projectiles cannot
  pierce, fork or chain). Prospero's Protection's doubled shield armour; Maligaro's Virtuosity's 300% multiplier on
  hits. Not seen in the client yet.
  Code: `Modules/Core/Game/Effects/`. Not yet: see the doc's "Not yet".

## Not working yet

- Most uniques and special mods (#60) and chancing to a unique; currency beyond the orbs above; support gems and links.
  Docs: [13](../docs/protocol/13-items.md), "Open".
