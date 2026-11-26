# Client data findings from a modded play copy

Findings from running the 3.29.3c client against a fork with patched client tables and mods (2026-11-20 to
2026-11-26). Each says how it was seen. None changes code in this repository.

## Patching the client's archive

- **Raw Oodle blocks.** The client's own `OodleLZ_Decompress` (0x142c9d520) accepts a bundle chunk that is one Kraken
  stream with a single uncompressed block: the two bytes `CC 06`, then the raw bytes. Seen for 1, 255, 65,536 and
  262,144 bytes (a full chunk), in the client's code run under an emulator and then live. `8C 06` is refused, and so is
  a chunk whose compressed size equals its size with no header. So a patched bundle needs no compressor.
- **The client's own bundles need ooz.** OozSharp (`ManagedOodleDecoder`) reads raw-block chunks, but not GGG's 3.29.3c
  bundles: `_.index.bin` says encoder 12 and its chunks hold Selkie blocks ("Decoder type Selkie not supported").
  Reading them needs the extractor's ooz-wasm worker (`Tools/ooz/ooz_worker.mjs`), as `GameFileSystem` with a decoder
  does.
- **Bundle header.** A rewritten bundle must keep the header word at offset 16 as 1, as every bundle of the client has.
  With another value the client did not load the patched archive; with 1 it did.
- **New rows need their own protocol ids.** Mods, WorldAreas and ItemVisualIdentity rows are named on the wire by
  `HASH16`, BaseItemTypes and Words by `HASH32`. A copied row that keeps its template's value is shown as the template.
  A base type's `HASH32` is MurmurHash2-32 (seed 0) of its `Id` in UTF-8, in every row of the client's table; the
  `HASH16` columns are not derived from the `Id`, so a new one only has to be unique.
- **The servers never read the client.** They read `data/client.db`, built from `sql/`. A content change has to go
  into both.

## What the client accepts in its tables

Seen live on the play copy, each row added through a content pack (both the archive and `client.db`):

| Table | New rows | Result |
|---|---|---|
| `Stats` | 948 | The client logs "Stats enum is out of sync" and closes at character select. Its stat list is fixed. |
| `Mods`, `ModType`, `BuffDefinitions` | hundreds | Loaded: the client reaches the game with them. |
| `BaseItemTypes` (new currencies with their own art from PNG) | a few | Loaded; they drop, stack and show their art. |

So new item modifiers have to reuse existing stats. A common way: one existing stat carries an index and a second
carries the value, with one stat description block over the pair.

- A loot filter may name a new enchantment, and `HasEnchantment "<name>"` matches it on unidentified drops too (beam,
  colours, minimap icon, sound). The client lists filters from `Documents\My Games\Path of Exile` itself, even when its
  `Client\config.json` points its settings at a subfolder.
- `InstanceEntities.WorldPopupIcon` (the number shown over the player) shows whole numbers only.

## Stats and descriptions

- **"Allocates X".** `mod_granted_passive_hash` and `_2` to `_5` are described as `"Allocates {0}" passive_hash`: the
  value is a node's `PassiveSkillGraphId`. The client has five generic mods for them, `GrantedPassive` to
  `GrantedPassive5` (Mods rows 21489 to 21493: Domain 1, GenerationType 3, range 0 to 0), so an item mod's own value
  names the node. Nothing on the server reads these stats yet.
- **Arcane Might.** The six `additive_spell_damage_modifiers_apply_to_*` stats are described as "Attacks have N%
  Arcane Might" (100%, 150%, N%, N% while wielding a Wand, retaliation, active skill), with `ReminderText` row 452:
  "Arcane Might causes Increases and Reductions to Spell Damage to also apply to Attack Damage at that percentage of
  their value".
- **Who an aura reaches.** `BuffTemplates`' fourth column (`__col4` in the schema) lists who a template's aura reaches:
  1 its owner, 2 allies, 3 enemies, 10 minions. A mod's `BuffTemplate1` therefore tells whether a line is for the
  wearer ("You and nearby Allies") or for others only ("Nearby Allies").
- **Keystone rules.** The client's `ReminderText` has rule text for Inner Conviction, Hollow Palm Technique, Shared
  Suffering and Conduit only. Lone Messenger, Kineticism, Disciple of Kitava, Nature's Patience and Voracious Flame have
  none in the data (their rules are on poewiki and poedb); no full text was found for Veteran's Awareness.

## Passive tree and ascendancies

- **Phrecia nodes.** The Phrecia ascendancies' nodes are their own `PassiveSkills` rows (`Id` starting
  `AscendancyCustom`), but they point at the same `Ascendancy` rows (0 to 18) as the normal classes. The Phrecia name of
  each is that row's `LeagueName` column. In the 3.29.3c tables: 390 such nodes, beside 379 normal ascendancy nodes.
- **Choice nodes.** A multiple-choice node's options (`IsMultipleChoiceOption`) do not follow it in the table (Knife in
  the Back is row 1617, its parent Assassination Style 1628). They are tied to their parent only by the passive graph's
  links (`passive_graphs`, graphs `normal` and `phrecia`).
- **Other ascendancies in the data.** Reliquarian (36 nodes) and Luminary (23) have `Ascendancy` rows and nodes; the
  Phrecia Harbinger's nodes include eight with placeholder names (`<<HRB32>>` and the like). 37 ascendancy nodes grant
  a skill (`GrantedEffectsPerLevel`).

## Achievements

- Achievement sets are numbered 1 to 80 (`Achievements.SetId`); `AchievementSetsDisplay` has 73 rows with ids in that
  range. Many old event sets hold a single achievement (set 21: `Flashback2ReachLevel35`).

## An open crash: Harbinger of Time

Seen twice on the play copy (main's `ItemHarbingers.cs`, with no other Harbinger code): the client closes about one
second after the Harbinger minion's **second** use of `HarbingerActionSpeedAura` (20 s cooldown). Around it the server
sends 0x02bc, 0x01ab, the skill stat refresh and 0x01c1 for buff row 921 with a new instance id (0x4E, after 0x4D was
removed by 0x01c2 eight seconds earlier). The first use sends the same and is fine. The cause was not found by reading.

## For mod authors

- `ModHost` reloads a mod when any file in its folder changes. A mod that saves data or writes a file (a sound, a
  cache) into its own folder reloads itself on every write: one unlock that saved three times reloaded the mod three
  times. Keep written files outside `Mods/` (under `data/`).
