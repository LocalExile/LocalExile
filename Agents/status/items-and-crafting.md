# Status: Items, currency and crafting

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/items-and-crafting.md](../roadmap/items-and-crafting.md).

## Works (verified with the real client)

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
- Gold: per account and league, dropped by monsters and picked up by itself.
  Docs: [04](../docs/protocol/04-player-entity.md) §6.
- Loot: currency, gems, flasks, equipment and uniques by weight, cards and scarabs in maps, stacked drops.
  Docs: [15](../docs/protocol/15-progression.md) §3, [drop data](../docs/dev/drop-data-schema.md).

## Partly working

- The mod SDK validates native character-build previews and commits confirmed imports after session drain; the optional POB mod and converter are external.

- **On-kill explosions and attribute damage reduction (implemented 2026-11-04; native behavior checked with the
  official client 2026-11-05, server live check pending):** "Enemies you Kill Explode, dealing X% of their Life as
  Physical Damage" (the Crusader body-armour prefix and the synthesis implicit) deals X% of the killed monster's
  maximum life as physical damage to every hostile monster within 25 cells, mitigated by each target's resistances,
  and a kill by the explosion can trigger another one. The radius is a fixed 25 cells: measured with the official
  3.29.3b client, one explosion killed at 22.85 and 24.35 cells, and increased area of effect does not extend it.
  The explosion consumes the corpse (0x2bd at once) and plays the native `HolyExplosion` art (MiscAnimated 2877,
  `Metadata/Effects/Spells/holy_explode/holy_explode.ao`) for two seconds; its corpse was removed 0-6 ms after the
  death. The Eater of Worlds body-armour implicits "1% less Damage Taken per X
- Conditional item gates, attribute/level added damage, Phasing and Avatar of Fire reach live pools and hits.
  Weapon/aura, tracked-event, strict thresholds and qualified equipment/charge-state gates have coverage;
  Arborix's worn Dash, travel lock and four recent consumers have deterministic transaction, lifetime and boundary
  coverage in the Gateway; the native check remains pending. Yriel's Fostering's actual worn Ursa grant now owns one
  level-70 friendly beast minion through the shared spawn, movement and attack path, including source, area, owner and
  module cleanup plus the real `_if_you_have_beast_minion` physical-damage consumer. Ursa/Rhoa are native accepted;
  Snake's 30% physical-to-chaos path is automated; native factory, projectile and source cleanup are verified.
  Scorpion's Call now uses the real shared Blood/Sand
  stance state for all five rolls: projectile damage and Life recovery in Blood, Evasion and area in Sand, and the
  four-second changed-stance attack speed, with actual hit, recovery, radius and pool consumers covered. Its
  normal-control and terrain results remain native-unverified.
  Weapon/aura, tracked-event, numeric-threshold and supported runtime-state gates have automated coverage.
  The item mods' "when on low/full life or mana" spelling shares the "while" state flags and folds onto the plain stat.
  Untracked, counted-recent and compound operators remain partial ([13](../docs/protocol/13-items.md)).

- **On-kill explosions and attribute damage reduction (implemented 2026-11-04, automated verification; live client
  pending):** "Enemies you Kill Explode, dealing X% of their Life as Physical Damage" (the Crusader body-armour prefix
  and the synthesis implicit) deals X% of the killed monster's maximum life as physical damage to every hostile monster
  within 20 cells, mitigated by each target's resistances; increased area of effect scales the radius, and a kill by the
  explosion can trigger another one. The Eater of Worlds body-armour implicits "1% less Damage Taken per X
  Strength/Dexterity/Intelligence" reduce hits, ignite and the other damage over time taken as a final multiplier.
  Not modelled: the chance-based and conditional (ignited, shocked, bleeding) explode variants and secondary damage's
  scaling by global physical/area damage.
  Code: `KillExplosions.cs`, `IncomingResourceDefences.cs`, `IncomingDamage.cs`, `PlayerAilments.cs`, `Player.cs`.

- **Item modifier effect (implemented 2026-11-03, automated verification; live client pending):** the item-local
  increased-effect stats scale the item's own explicit modifiers — every explicit one
  (`local_explicit_mod_effect_+%`: Simplex Amulet 100%, Focused Amulet 100%, Subsume the Source 120–240%) or one side
  only (`local_prefix_mod_effect_+%` / `local_suffix_mod_effect_+%`: the Cogwork, Geodesic, Composite, Manifold,
  Ratcheting and Helical Rings, 25% or 50%). Implicits, enchants and the effect stats themselves stay raw, and values
  round down like The Adorned's jewel effect. The same implicits' `local_maximum_prefixes_allowed_+` and
  `local_maximum_suffixes_allowed_+` move the rare affix caps for currency and bench crafting
  (Composite 6/0, Manifold 4/1, Simplex 1/2, Helical 1/4 …).
  Not modelled: the weapon-tree damage-category effect stats (Mods.dat has no damage-type tags), unveiled/tincture
  effect, and the per-socketed-abyss-jewel variant.
  Code: `ItemModStats.cs`, `ItemRolls.cs`, `BenchCrafting.cs`, `ItemWeaponStats.cs`.

- Utility-flask orbs: Enkindling and Instilling use native weighted enchant pools, replacing previous enchantments.
  Enkindling coefficients and the active-effect charge-gain lock are wired; Instilling uses paid flask-use events.
  Automated checks pass. Native checks confirmed both currency transactions, enchant replacement, maximum-charge
  clamping, the active-effect charge lock and automatic use after hitting a rare monster.
  Incoming Bleed/Poison sources remain absent, although their enchantment conditions are represented.
  See [13-items](../docs/protocol/13-items.md), utility flask orb enchantments.


- Sacred Orb rerolls: ordinary and Shift-repeat currency routes replace only the shared defence base roll (0..31).
  Requires Armour/Mods components and a known variable native range.
  Corrupted, mirrored and fixed-range targets are refused.
  The same roll can recur and still costs one orb; affixes, quality, sockets, gems and other properties stay intact.
  Automated coverage checks eligibility, persistence, save rollback and worn-item pool refresh.
  Fourteen native captures recorded at `63c1039` verify ordinary/repeat use, refusals, inventory/stash combinations,
  unchanged outcomes and restart persistence; fresh checks of the merged branch remain open.
  Ward ranges count for eligibility, but Ward character calculations and recovery remain unimplemented.
  Code: `CurrencyEffects.Sacred`, Gateway (`Program.Items.cs`).
- Character-build previews and imports through the mod SDK; the POB mod and converter are external.
  Docs: [modding](../docs/dev/modding.md).
- On-kill explosions and Eater of Worlds attribute damage reduction.
  Docs: [13](../docs/protocol/13-items.md), "Kill explosions and attribute damage reduction".
- Conditional item gates feed live pools and stat publication; untracked, counted-recent and compound operators are
  partial. Docs: [13](../docs/protocol/13-items.md).
- Item modifier effect and affix-cap implicits. Docs: [13](../docs/protocol/13-items.md), "Item modifier effect".
- Utility-flask orbs: Enkindling and Instilling enchantments; incoming Bleed and Poison sources are absent.
  Docs: [13](../docs/protocol/13-items.md), "Enkindling Orb and Instilling Orb".
- Sacred Orb rerolls: fresh checks of the merged branch, and Ward's character calculations.
  Docs: [13](../docs/protocol/13-items.md), "Sacred Orb".
- Special defence rules and local requirement percentages. Docs: [13](../docs/protocol/13-items.md) §2-§2d.
- Unique items, Mageblood, Headhunter and abyss jewels; most uniques' own mechanics are missing.
  Docs: [13](../docs/protocol/13-items.md) §2e, §8a.
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
- **Vaal Orb and corruption** (local rule). Docs: [13](../docs/protocol/13-items.md), "Vaal Orb and corruption",
  [admission](../docs/re/vaal-coe.md).
- **Eldritch Ichors.** Docs: [13](../docs/protocol/13-items.md), "Eldritch Ichors and Embers",
  [evidence](../docs/re/eldritch-ichor-coe.md).
- **Eldritch Embers.** Docs: [13](../docs/protocol/13-items.md), "Eldritch Ichors and Embers",
  [evidence](../docs/re/eldritch-ember-coe.md).
- **Cartographer's Chisel, Eldritch Chaos/Exalted Orb and Orb of Unmaking** (local rules, not checked live).
  Docs: [13](../docs/protocol/13-items.md), "Ancient Orb, chisels and the Eldritch rerolls".
- **Orb of Binding, the Eldritch Orb of Annulment and the Tainted family** (local rules, not checked live).
  Docs: [13](../docs/protocol/13-items.md), "Ancient Orb, chisels and the Eldritch rerolls".
- **Enkindling Orb.** Docs: [13](../docs/protocol/13-items.md), "Enkindling Orb and Instilling Orb",
  [evidence](../docs/re/enkindling-coe.md).
- **Instilling Orb.** Docs: [13](../docs/protocol/13-items.md), "Enkindling Orb and Instilling Orb",
  [evidence](../docs/re/instilling-coe.md).
- **Currency results look right in the client** for every implemented currency's main use; Vaal on jewels is outside
  the local rule.
- **Glassblower's Bauble** (level-scaled quality not checked live).
  Docs: [13](../docs/protocol/13-items.md), "Glassblower's Bauble".
- **Fracturing Orb** (verified live). Docs: [13](../docs/protocol/13-items.md), "Fracturing Orb",
  [the game rules](../docs/re/fracturing-mirror-mechanics.md).
- Mirror of Kalandra copies non-unique equipment, flasks and T1–16 maps. Docs: [13](../docs/protocol/13-items.md).
- **Hinekora's Lock** (server rules verified live). Docs: [13](../docs/protocol/13-items.md), "Hinekora's Lock",
  [evidence](../docs/re/hinekora-lock.md).
- **Orb of Fusing** (verified live). Docs: [13](../docs/protocol/13-items.md), "Orb of Fusing".
- Currency metamods: prefix/suffix locks and attack/caster restrictions.
  Docs: [13](../docs/protocol/13-items.md), "Currency metamod protection".

## Not working yet

- Most uniques and special mods (#60) and chancing to a unique; currency beyond the orbs above; support gems and links.
  Docs: [13](../docs/protocol/13-items.md), "Open".
