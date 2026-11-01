# Status: Gems

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/gems.md](../roadmap/gems.md).

## Works (verified with the real client)

Nothing yet.

## Partly working

- Ruthless gems use native primary/companion effect alternatives, requirements and skill rows.
  Deterministic support rewards are disabled; eligible active gems use Siosa's Uncarved Gemstone exchange.
  Gem XP receives the configured Ruthless multiplier.
  Ruthless availability excludes the 13 original movement gems, Venom Gyre and Item Rarity Support.
  Drops, vendor stock and runtime use enforce those exclusions; Quickstep remains permitted.
  An alternate Frostblink hardmode row does not make that unavailable gem playable.
  Automated mode catalog, acquisition and runtime checks pass.
  Ruthless Nessa, Siosa and unavailable-gem cases have automated coverage.
  See [league modes](../docs/game/league-modes.md).
- **Gem quest rewards (verified live 2026-10-26).** Every `QuestRewardOffers` row whose items the server can build is
  offered by the NPC's QuestReward dialogue row, with the `Characters` class filter and the offer's taken flag;
  unbuildable rows (level-8 magic armours, magic belts) are skipped instead of an allow list excluding whole offers.
  This covers the act gem offers: a1q4's six class gems (offer 3), a1q5's support gem (offer 2) and each act's later
  gem choices.
  Claiming puts the item in the main inventory and sets the flag in one save; a second claim is refused
  (`QuestRewardCatalog`; [12](../docs/protocol/12-quests.md) §1, [19](../docs/protocol/19-gems.md) §5).
  Live: Tarkleigh's "Glyph Reward" after `/quest_flag a1q4-PlacedGlyphs` — six Witch gems, claim, repeat refused.

- **Gem drops (verified live 2026-10-26).** 4% of a monster's item drops are an obtainable gem whose `DropLevel` the
  monster meets, unlevelled and without a rarity roll (`ItemDrops`, `GemSources.DropPool`,
  [15](../docs/protocol/15-progression.md) §3).
  The pool is the quest reward gems, the starting gems and the drop-only exceptional supports; the data's Graft,
  item-granted/legacy (Envy, Death Aura, Quickstep) and placeholder ("...") rows never drop.
  `GemDropsTests` cover the pool, the level gate and the unlevelled gem. The rate is our own until the game's drop pools
  are known.
  Live: rare monsters dropped normal skill and support gems. The first attempt used the data's ShopTag gems, which
  include the item-granted/legacy rows, so the pool was narrowed to the quest/starting/exceptional sets.

- **Gem vendors (verified live 2026-10-26 on Nessa; Lilly/Siosa unit-tested).**
  In this local checkout Nessa additionally has the complete creatable gem assortment, independent of quests/class,
  with alternative variants, colour/kind tabs and numbered overflow pages; White Skills combines both white kinds.
  A separate Skills tab preserves quest/class-restricted ordinary stock as the future gameplay shop.
  Skills lists active gems before supports, grouped red/green/blue/white; supports begin on a new row.
  Colour tabs remain complete development tools regardless of progression; NpcShops.cs documents the distinction
  without adding development messages to the player's chat when the shop opens.
  Every Nessa gem is unlimited stock on every tab: paid copies get fresh IDs and the original offer remains available.
  Network purchase tests cover repeats, variant/quality persistence, insufficient funds and save rollback.
  Other vendors retain progression.
  Automated full-stock, variant persistence and pagination checks cover the override; updated native display is pending.
  See [19](../docs/protocol/19-gems.md) §5, "Local Nessa shop".
  The currency/flask/jewellery vendors that also sell gems give seven tabs of level-1 gems, priced by their level-1 requirement tier (1–7 Scroll of Wisdom,
  8–15 Orb of Transmutation, 16–27 Orb of Alteration, 28–37 Orb of Chance, 38+ Orb of Alchemy). The stock
  is the gems of every
  completed quest offer (reward shown or taken), filtered to the character's class and to the client's shop list;
  Siosa (after "A Fixture of Fate") and Lilly Roth (after `UnlockedLillyGemShop`) ignore class, and Lilly also the
  quest completion; Faster Attacks (Nessa, after a1q2) and Faster Casting (Yeena, after a2q4) are the 3.29 cross-class
  additions.
  Live: Nessa listed the Witch's a1q1 + a1q4 gems, then the a1q2 gems and Faster Attacks Support after the flag; their
  prices matched the requirement tiers (the a1q1/a1q4 gems 1 Scroll of Wisdom, the a1q2 gems and Faster Attacks 1 Orb of
  Transmutation).
  Not live-checked: Lilly (the Act 6 town is not captured and spawns the player in water) and Siosa (The Library is
  not reachable); gem drops are in (above).
  (`GemVendorStock`, `NpcShops.Tabs`; [17](../docs/protocol/17-npcs.md) §5, [19](../docs/protocol/19-gems.md) §5).

- **Linked support gems.** Native compatibility, link groups, duplicate families and skill-bar support records
  are implemented; Cyclone with Increased Area of Effect was read successfully by the real client.
  Ordinary hit, area, speed, critical, cost and reservation calculations use linked support stats.
  Item-granted supports join the same compatibility, duplicate resolution, calculations and native badge records
  for gems in their source item, independently of links; automated tests cover their addition and removal.
  Local socketed level/quality bonuses apply to physical gems, including support gems, without raising requirements.
  The Delve socketed attack/spell more-damage effects reach the ordinary hit calculation.
  A real-client Cyclone control accepted the item-support records and displayed blue s/c, red W and white d.
  The supported critical values appeared in its panel and tooltip; special execution still has the gaps below.
  Spell Echo repeats a supported spell's releases and area hits a cast time apart, with its 40% more cast speed
  (`SpellRepeats`; [11](../docs/protocol/11-skills.md) "Spell repeats"); tested, not yet checked with the real client.
  Special execution mechanics remain incomplete; see [25](../docs/protocol/25-gem-supports.md).

- **Stable gem skill ids (tested, not yet checked with the real client).** Each gem keeps its skill id while it stays
  socketed (a gem moved through the cursor keeps its own; a gem swapped in never takes the old gem's id), so skill bar
  keys no longer move when other gems come or go; a skill that goes away ends its aura or Righteous Fire;
  one aura of a kind runs at a time (`GemSkillIds.cs`; [19](../docs/protocol/19-gems.md) §3).
- **Gem levels (tested, level 1 → 2 checked with the real client).** A gem's level comes from its experience up to its
  experience type's cap; its skill uses that level's cost, damage, reservation and cooldown, and its quality stats;
  transfigured and Trarthan gems grant their own skill; Vaal gems grant their Vaal skill and base skill.
  Vaal soul collection/native budget payloads are implemented, pending native acceptance.
  Unsupported Vaal casts remain refused even with souls; see [27](../docs/protocol/27-vaal-souls.md).
  The variant is sent in the SkillGem data and saved.
  Item serialization also derives the native variant flag for standalone and socketed gems, preserving level-lock
  and saved flags. Wire regressions cover the flag; transfigured item names still need a native retest;
  `/gem <name> [level] [quality]` (`ItemCatalog.cs`, `SkillCatalog.cs`; [19](../docs/protocol/19-gems.md) §1, §3).
  Not yet: corruption.
- **Gem requirements (tested, checked with the real client).** A gem's level requirement is the character level of its
  level (GrantedEffectStatSetsPerLevel `PlayerLevelReq`; Haste 24 at gem level 1 . 70 at 20, the client's tooltips),
  and its attribute requirements follow the client's own formula (`0x14246fc40`: `trunc((3 * level_req + 11) *
  (percent/100)^0.9 * k + 0.5)`, `k` 0.7 for a skill and 0.5 for a support, none below 14;
  fitted against 570 poedb tooltip cells with no mismatch: Haste 20 is 155 Dex, Clarity 20 119 Int, Fireball 20 155).
  Socketing a gem the character is too low or too weak for is refused (0x30, `GemRequirementsUnmet`), and a gem its
  requirements above the character's makes its item inactive (the item's requirements are the highest of its own and its
  gems', `EquipmentStatSources`; lost in PR #175, restored 2026-10-29), as a worn item the character is too low for is: the
  client reds it ("Requires level 70"), the item's mods stop counting and its skills leave the Actor list, so there is
  nothing to cast; a use that arrives anyway is refused without the action (no 0x1ab, and the mana pool again so the
  client's predicted cost comes back; `GemDisabled`).
  An inactive main-hand weapon is ignored: the default attack falls back to the class's unarmed hit, as if it were not
  worn.
  Checked live 2026-10-23 with a level 20 Fireball wand at character level 10, and its attributes with `/set_stat
  intelligence 200` and then 130 (155 needed: the client reds the gem, the wand's mods stop counting, a use is
  refused).
  In play the gem never gets there: its experience stalls at the first level the character does not meet
  (`SkillCatalog.MaxLevelAt` in `GainGemExperience`, the game's rule;
  the client derives levels from experience alone and would not stall by itself), and the level change re-sends the
  skills.
  Not yet: verify every disabled-gem aura transition
  ([19](../docs/protocol/19-gems.md) 1, 3).
- **Gem experience from kills (one gem in the first weapon set checked with the real client).** Every gem socketed in
  an equipped item, in either weapon set, gains 10% of a kill's monster experience (support gems included;
  inactive items, gems at their last level and corrupted gems over their cap nothing;
  quest levels and `/add_xp` give the gems nothing), sent with the kill as 0x00bf item changes, each item once, so the
  XP bars follow, and saved with the character (`GainGemExperience`, `PlayerInventories.ChangeSocketedGems`;
  [19](../docs/protocol/19-gems.md) §1).
  A kill's share stops one point below the next threshold, where the client shows the "+": it does not level a gem by
  itself (checked live 2026-10-23).
  Not checked live yet: several gems in one item, the second weapon set.
- **Gem level-up and the lock (checked with the real client).** Clicking the "+" sends C→S 0x31 (inventory, item,
  socket; 14 bytes); the server writes the next threshold into the gem, replaces the item (0xbf) and re-sends the
  skills (0x02ba Actor), so the client derives the new level and its numbers at once, and refuses a gem at its last
  level, a locked one, or one whose next level's requirements the character does not meet (`LevelUpGem`;
  the client's "+" check does not test them, so the server answers with the missing level or attribute).
  A right-click sends C→S 0xd7 (the same three u32s and a u8 lock flag;
  15 bytes), kept in the SkillGem flags bit 2 (`Item.GemFlags`) and saved, so the lock survives a login;
  the client greys the "+" while it is set (`LockGem`).
  Checked live 2026-10-23: level 1 → 2, right-click, relogin, unlock, level again;
  a level-up refused at character level 31 (Fireball level 10 needs 32) and with 70 Int (needs 75);
  `/gem_xp <amount|near>` sets the experience.
  Not checked: a gem in the inventory (not socketed), which sent nothing in the captures.

## Not working yet

Nothing yet.
