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
- Loot: currency, gems, flasks, equipment and uniques by weight, ordinary equipment socket counts bounded by class and item level, cards and scarabs in maps, stacked drops.
  Docs: [15](../docs/protocol/15-progression.md) §3, [drop data](../docs/dev/drop-data-schema.md).

## Partly working

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
- Special defence rules: local zero-ES modifiers override this item's base, mods and quality only for ES.
  Supported equipment ES-to-Life conversion truncates each item's contribution, removes all equipment ES and adds
  base Life before Life increases; independent global/aura ES remains ES and excluded shields grant no converted Life.
  The corrupted-item conditional ES increase enters the global ES bucket, leaving tooltips and converted Life alone.
  Hidden no-attribute-requirement modifiers retain the level gate.
  Local requirement percentages (`local_attribute_requirements_+%`, `local_strength_requirement_+%` and the dexterity
  and intelligence ones) change an item's attribute requirements, counting a row that is both implicit and explicit once;
  automated, no native requirement check yet.
  Automated coverage passes; historical native conversion and corrupted-item ES controls are recorded at `0bae37a`
  and `c9060e4` (see the protocol reference for values).
  Fresh real-client checks of the merged branch and other unique/conditional mechanics remain open.
  Code: `ItemDefences.cs`, `ItemModStats.cs`, Gateway (`Program.Equipment.cs`).
  Docs: [13](../docs/protocol/13-items.md) §2-§2d.
- **Unique items and Mageblood (verified live 2026-10-26).** The client data links a unique's name (`Words` wordlist 6)
  and artwork (`UniqueStashLayout` → `ItemVisualIdentity`), but not its mods, so `UniqueCatalog` holds each definition:
  1,299 generated from Path of Building with their current base, implicits and rolls (`scripts/re/unique_items.py`),
  compared with poedb's item cards, with the differences that remain on the roadmap. Divine and Blessed Orbs reroll
  within a unique's own rolls, and a range in world.db's `mod_stat_overrides` wins over them.
  Ordinary uniques get default sockets; Tabula gets six linked whites and hidden overrides (tests; live check pending).
  Skin of the Loyal gets its six linked random-colour sockets, and the 3.29 hidden no-white-socket property is on it and
  Dialla's Malefaction (automated; live check pending).
  Fixed "Has X Sockets" counts (Loreweave, Voidfletcher, The Squire, crests and hats) and Malachai's all-white Unset Ring
  socket are generated from those rows too; link groups there use the Jeweller's approximation (automated).
  Every definition but the five tinctures creates, writes and reloads by test (the server cannot write the Tincture
  component). Seen in the client 2026-10-30, with their name, art, base, mods and item level: Tulfall, Shimmeron,
  Replica Atziri's Foible, Kalandra's Touch, Emperor's Vigilance, The Broken Elegy, Gorgon's Gaze, Grand Spectrum and
  Atziri's Reign.
  Abyss jewels fit allocated passive sockets and equipment Abyss sockets. Active hosts contribute socketed jewel stats
  and eye counts. Stygian Vises and unique armour create the Abyss sockets granted by their modifiers. Individual Gaze and Historic effects need their own consumers.
  `/item <name> <base name>` reaches one base of a unique with several (`/item Grand Spectrum Cobalt Jewel`).
  `/item Mageblood` creates the Heavy Belt unique with its name, artwork and fixed mods (the Strength is the base
  implicit); the client shows the unique tooltip.
  While Mageblood is worn, the leftmost 2–4 magic utility flasks in the flask bar apply their effects permanently without
  charges or a timer, and using them is refused (client mod, server backstop); the stats refresh when the belt or the
  flask bar changes. Its flasks keep their buffs on the client without a timer (not seen live yet).
  Inspired Learning grants one eligible rare affix for 20 s when its ordinary socket has four allocated notables in
  range. Duplicate stolen effects retain the longer lifetime.
  Bound By Destiny checks its 2/4/6 thresholds against active influenced equipment; dual-influenced items count
  once toward each matching influence. The Hunter modifier regenerates life for one second every four seconds.
  The Redeemer and Warlord modifiers blind and intimidate nearby enemies while in range.
  Headhunter: killing a rare gives each of its rolled affixes as a stack of character stats for 60 s (the same one resets
  its time), with one Headhunter buff showing the count and the longest time, and
  its rolled increased damage against rares (not seen live yet; they end on an area change, where the game keeps them).
  Not yet: other uniques' mechanics, chancing, the "effects cannot be removed" backstop, the Foulborn rightmost variant,
  Replica Headhunter and Enkindling (`UniqueCatalog`, `Program.Mageblood`, `StolenMods`, `Program.UniqueBelts`;
  [13](../docs/protocol/13-items.md) §2e, §8a).

- Ruthless uses native mod/flask alternatives, reduced tunable drops and vendor returns, and refuses bench crafting.
  Entering town or a hideout does not refill its pools/flasks; supported vendors refill them instead.
  Active utility flasks cannot gain charges, and native Ruthless durations/Onslaught values apply.
  Automated pool-restoration and session-ownership checks cover area changes, reconnects and vendor refill persistence.
  Drop, recipe, bench and flask-rule calculations have automated coverage; full retail balance remains partial.
  See [league modes](../docs/game/league-modes.md).
- Essence crafting (2026-10-25, automated verification; real-client use pending): all 105 craftable essences resolve
  their exact class-specific guaranteed mod from Essences.dat, including tier-8 and Desolation.
  Normal/rare eligibility, random-mod level caps, fractures, metamod refusal and repeated consumption from the Essence
  tab are covered.
  Uses the existing currency path for ordinary and Shift application.
  See [13](../docs/protocol/13-items.md).
- Quiver eligibility fix (2026-10-25, awaiting client confirmation): active bows publish `main_hand_weapon_type = 9`,
  required by the client's inventory gate; every hand now sends its weapon type (see "Client weapon-type stats" in
  [13](../docs/protocol/13-items.md)).
- **Jeweller's Orb: verified live (2026-10-21).** Normal use and Shift share the currency handler; rerolls counts
  1/2/3/4/6 and link groups, limited by class, dimensions and item level, and consumes one orb.
  3.29: sockets the reroll keeps retain their colours and only the added sockets are white.
  Socketed gems, unsupported targets and maximum base capacity are refused without spending an orb.
  The live-found refusal of level-1 boots/gloves with two sockets is fixed and verified live.
  Count and link probabilities are approximate; special socket overrides are not modelled.
  See [13](../docs/protocol/13-items.md), "Jeweller's Orb".
- **Socket colours (3.29; verified live 2026-10-26).** Item sockets generate white and may roll a
  colour in proportion to the base's attribute requirements, with the chance rising with item level; a gem in a
  matching red/green/blue socket gets +10% quality (the stat path is in [25](../docs/protocol/25-gem-supports.md)).
  Chromatic Orbs force one socket non-white and roll the rest, with item quality raising their chance; the bench's
  "At Least Two/Three/Four Non-White Sockets" crafts (`NN`/`NNN`/`NNNN`) reroll until they guarantee that many; Vaal
  corruption colours one white socket. `/item`, drops and shops use the same generator, and the start weapon's two
  linked sockets are white.
  The probabilities are an approximation (5% non-white at item level 1, +0.25 points per level up to 30% at 100, and
  +0.25 points per 1% quality for Chromatic/bench); `SocketColours`, [13](../docs/protocol/13-items.md),
  "Socket colours (3.29)".
  Not modelled: Tainted Chromatic/Jeweller's Orbs, Orb of Binding, the Locus of Corruption, the 25-roll bench reward,
  special item overrides.
- **Orb of Alchemy:** a modifiable normal item becomes an identified rare with 4-6 weighted affixes (CoE's 2/3, 1/4,
  1/12; jewels 3-4 with two per side) and a rare name. Flasks are magic-only; unknown/unsupported/unmodifiable,
  non-normal, corrupted/mirrored and empty-pool targets are refused before spending.
  CoE count/jewel checks, ordinary/Shift inventory/stash dispatch and save reloads have automated coverage.
  The old live check (2026-10-21) predates these rules; native re-acceptance is pending.
  See [13](../docs/protocol/13-items.md), "Orb of Alchemy", and [evidence](../docs/re/alchemy-coe.md).
- **Chaos Orb:** a modifiable rare item's unprotected affixes are rerolled (CoE 4/5/6 counts, jewels 3-4 with two per
  side; new tiers, new rare name); prefix/suffix metamod locks keep their side. Flasks are magic-only; non-rare,
  unmodifiable/unsupported and empty-pool targets are refused before spending.
  CoE count/metamod checks, ordinary/Shift inventory/stash dispatch and save reloads have automated coverage.
  The old live check (2026-10-21) predates these rules; native re-acceptance is pending.
  See [13](../docs/protocol/13-items.md), "Chaos Orb", and [evidence](../docs/re/chaos-coe.md).
- **Divine Orb (verified live, 2026-10-21):** keeps the affixes, tiers and order and rerolls only values.
  Shift works. See [13](../docs/protocol/13-items.md), "Divine Orb".
- **Orb of Scouring (verified live, 2026-10-21):** a magic / rare / unique item becomes normal, only base mods stay.
  See [13](../docs/protocol/13-items.md), "Orb of Scouring".
- **Orb of Transmutation:** normal to identified magic, targeting 1-2 weighted affixes at equal probability.
  Unknown, unsupported, unmodifiable and empty-pool bases are refused before spending.
  CoE count/pool comparisons, ordinary/Shift inventory/stash dispatch and save reloads have automated coverage.
  Framed native wand/flask upgrades, repeat/unmodifiable refusal and relog passed on 2026-10-29.
  Fresh native Shift/stash and manual currency UI checks remain separate.
  See [13](../docs/protocol/13-items.md), "Orb of Transmutation", and [evidence](../docs/re/transmutation-coe.md).
- **Orb of Alteration:** a modifiable magic item gets 1-2 new weighted affixes (one prefix, one suffix at most),
  with the same admission as the other affix currencies: unknown/unsupported/`Unmodifiable` bases, corrupted/mirrored
  items and empty pools are refused before spending.
  CoE count/flask checks, ordinary/Shift inventory/stash dispatch and save reloads have automated coverage.
  The old live check (2026-10-21) predates these admission rules; native re-acceptance is pending.
  See [13](../docs/protocol/13-items.md), "Orb of Alteration", and [evidence](../docs/re/alteration-coe.md).
- **Orb of Augmentation:** +1 weighted affix on a magic item, up to one prefix and one suffix, with the same admission
  checks as Transmutation: a known, serializable, non-`Unmodifiable` base with Mods; corrupted/mirrored, full items
  and open sides without ordinary affixes are refused before spending.
  CoE beta pool and simulated-probability checks, ordinary/Shift inventory/stash dispatch and save reloads have
  automated coverage. The old live check (2026-10-21) predates these admission checks; native re-acceptance is pending.
  See [13](../docs/protocol/13-items.md), "Orb of Augmentation", and
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
