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
- Gold: per account and league, dropped by monsters and picked up by itself. Gold and ground stacks share the saved pickup transaction.
  Docs: [04](../docs/protocol/04-player-entity.md) §6.
- Loot: currency, gems, flasks, equipment and uniques by weight, equipment drops at area level (+1 magic monster, +2 rare or unique), one-socket base implicits on ordinary jewellery and quivers, ordinary equipment socket counts bounded by class and item level, cards and scarabs in maps, stacked drops.
  Docs: [15](../docs/protocol/15-progression.md) §3, [drop data](../docs/dev/drop-data-schema.md).

## Partly working

- Heist enchantment magnitudes apply through Core Game. Current native tooltip comparison is pending ([13 §2c](../docs/protocol/13-items.md)).
- Optional backpack mods can enable the native 20 slots; stored items remain removable after disabling the mod.

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
- Unique items, Mageblood, Headhunter and abyss jewels; most uniques' own mechanics are missing. Every client unique
  has a definition (from Path of Building, or the PoE wiki for those it lacks) but the maps, Impossible Escape,
  watchstones and the six unique Heist contracts. Docs: [13](../docs/protocol/13-items.md) §2e, §8a.
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
