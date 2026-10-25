# Status: Gems

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/gems.md](../roadmap/gems.md).

## Works (verified with the real client)

Nothing yet.

## Partly working

- **Stable gem skill ids (tested, not yet checked with the real client).** Each gem keeps its skill id while it stays
  socketed (a gem moved through the cursor keeps its own; a gem swapped in never takes the old gem's id), so skill bar
  keys no longer move when other gems come or go; a skill that goes away ends its aura or Righteous Fire;
  one aura of a kind runs at a time (`GemSkillIds.cs`; [19](../docs/protocol/19-gems.md) §3).
- **Gem levels (tested, level 1 → 2 checked with the real client).** A gem's level comes from its experience up to its
  experience type's cap; its skill uses that level's cost, damage, reservation and cooldown, and its quality stats;
  transfigured and Trarthan gems grant their own skill, Vaal gems their Vaal skill and their base skill (refused
  without Vaal souls, which are not gathered yet); the variant is sent in the SkillGem data and saved;
  `/gem <name> [level] [quality]` (`ItemCatalog.cs`, `SkillCatalog.cs`; [19](../docs/protocol/19-gems.md) §1, §3).
  Not yet: supports, corruption.
- **Gem requirements (tested, checked with the real client).** A gem's level requirement is the character level of its
  level (GrantedEffectStatSetsPerLevel `PlayerLevelReq`; Haste 24 at gem level 1 . 70 at 20, the client's tooltips),
  and its attribute requirements follow the client's own formula (`0x14246fc40`: `trunc((3 * level_req + 11) *
  (percent/100)^0.9 * k + 0.5)`, `k` 0.7 for a skill and 0.5 for a support, none below 14;
  fitted against 570 poedb tooltip cells with no mismatch: Haste 20 is 155 Dex, Clarity 20 119 Int, Fireball 20 155).
  Socketing a gem the character is too low or too weak for is refused (0x30, `GemRequirementsUnmet`), and a gem its
  requirements above the character's makes its item inactive, as a worn item the character is too low for is: the
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
  Not yet: supports don't stop applying when a gem is disabled, and a disabled gem's aura keeps running
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
