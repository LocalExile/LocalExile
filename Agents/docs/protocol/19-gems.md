# 19. Gems

State of 2026-10-23: gem items serialize, socket and save ([13](13-items.md)). Every socketed skill gem grants its
variant's skill at the gem's level and quality (§3), skill changes after login go out as the Actor's 0x20 update
([11](11-skills.md) §3), and a gem gains experience, levels up when the player clicks its "+", and can have its level
locked (§4). Requirements are in §2; supports are still planned (Roadmap "Gems 3", #64).

## 1. The gem in the item data (SkillGem component)

Family gem-level stats now raise effective active-skill levels in the common supported-skill calculation.
The Herald/Vaal/Link families come from GemTags stat columns; Raise Zombie uses SkillGems' minion-level stat.
Both skills of a Vaal gem share the gem's family bonus; support gems remain outside these active-only bonuses.
Supported level bonuses and family sources sum before clamping to at least one.
Actor skill records and skill stats use that same effective level without changing SkillGem experience or quality.
Intrinsic requirements remain unchanged; see [scope and checks](../game/ascendancies.md#family-gem-levels).

The SkillGem component's reader `0x14246eb00` (writer `0x14246ea40`) takes, in order:

| Data | Stored at | Use |
|---|---|---|
| i32 experience | +0x28 | -1 when the gem is one level over the cap (the writer sends -1 when its level is above the type's) |
| u32 | +0x3c | the variant: an index into the SkillGems row's `GemVariants` (0 the normal gem) |
| u8 | +0x10 | flags (read through `0x140e28cf0`); bit 2 = the level is locked (right-click, §4); bit 5 is set by the client when the variant is not 0, bit 7 when it builds the component empty |
| u16 | +0x40 | an IndexableSupportGems row's `Index` (0: none); not used for gems yet |

The reader zeroes the level (+0x2c) and two experience thresholds (+0x30, +0x34) first. Then:

- **Experience -1:** experience 0 and level = the gem type's +0x28 + 1. The gem type's +0x28 is the maximum level
  (below), so this is one level over the cap; the writer produces it for a level above the cap (a corrupted level 21
  gem is the likely case).
- **Any other experience:** `0x14246f900` derives the level. From level 0, while the level is under the maximum (gem
  type +0x28) and the next threshold (+0x34) is not above the experience, the level goes up by one, +0x30 takes the old
  threshold and +0x34 the next level's from the gem's experience table (ItemExperiencePerLevel, the row's +0x14).

So the client derives the level from the experience alone; the server sends experience, not a level. Because the level
is derived on every read, a gem whose experience reached a threshold levels at once: the server stops the experience one
point below the next threshold, which is also the state the client draws a full bar and the "+" for (§4), and only the
player's click sends the experience to the threshold. The other stall is the character's level requirement: the game
keeps the gem at the last level the character meets, and the server stops the experience just below the next level's
threshold (`SkillCatalog.MaxLevelAt`, `GainGemExperience`; §"Requirements" below) — the client does not stall it, it
derives whatever the experience says (checked live 2026-10-23).

The server gives the kills' experience to the gems (`GainGemExperience`, from `MonsterKilled` only: a quest's level and
`/add_xp` give the gems nothing): each gem socketed in an equipped item, in either weapon set (rows 1–10, 14, 15),
gains 10% of the player's monster experience, up to the threshold of the first level whose requirement the character
does not meet and the last level's threshold (a support gem too; a gem in an
inactive item, at its last level, or at experience -1, a corrupted gem over its cap, nothing). All the gems change in
one step under the inventory's lock (`PlayerInventories.ChangeSocketedGems`), written into the gem inside the socket
list, and are sent as the socket's equipment row 0x00bf item change, each item once with all its gems — the same item
id removed and added again, the replace idiom: an addition of a known id alone is dropped as a duplicate and the client
keeps the stale gem (live 2026-10-23). One item change per row per kill; the client's XP bar and tooltip follow. A
level's costs, damage and reservation ride the skills, so a level-up also re-sends the skills (the 0x20 Actor update,
`SendSkills`), the same thing an equipment change does.

The variant: the copy `0x14246fa50` checks +0x3c against the `GemVariants` count (the SkillGems row's +0x77, the list's
count in the 3.29 layout) and keeps 0 when it is outside the list. The u16: `0x14246fae3` and `0x1424732c3` look it up
in `Data/IndexableSupportGems.dat` when it is not 0.

The server writes the component as `i32 experience, u32 variant, u8 flags, u16 0` (`Item.Payload`, `GemVariant`).
Serialization derives flag bit 5 (`0x20`) from a positive variant and clears a stale bit 5 for variant 0, including
gems nested inside sockets. Level-lock and other flags are preserved; saves retain the original `GemFlags`.
The network reader copies the raw flags byte into +0x10 (`0x14246eb46..0x14246eb5b`), without applying the native
copy's bit-5 normalization (`0x14246fab8..0x14246fae3`). A variant index alone therefore leaves the native variant
marker unset even though the server grants the correct variant skill. Wire regression tests cover both item paths;
the transfigured item-name display still needs a native retest.

## 2. The game data

- **SkillGems:** 847 rows, 534 skill gems and 313 support gems (`IsSupport`). Each row has the gem's base item type,
  its Str/Dex/Int requirement percentages, its experience type (`ItemExperienceType`), its variants (`GemVariants`, rows
  of GemEffects), and links to its Vaal and awakened versions (`VaalVariant_BaseItemTypesKey`, `IsVaalVariant`,
  `AwakenedVariant`, `RegularVariant`).
- **Variants (GemEffects):** the first variant is the normal gem. The 445 further variants are 297 named ones
  (transfigured "of X", the 12 "of Trarthus" Trarthan gems, and the Vaal gems' variants) and 148 placeholders named
  "[DNT] …AltX/AltY" that don't exist in the game. A transfigured variant has its own GrantedEffect (Ice Nova of
  Frostbolts is IceNovaAltX) and its own gem tags.
- **Vaal gems** are their own 59 SkillGems rows. Each variant's GrantedEffect is the regular Vaal skill and its
  GrantedEffect2 the base skill: Vaal Fireball's three variants are Vaal Fireball with Fireball, FireballAltX or
  FireballAltY. Vaal Sweep has no base skill.
- **Level caps** come from the experience type (ItemExperienceTypes, ItemExperiencePerLevel):

  | Experience types | Cap | Gems |
  |---|---|---|
  | GemProgression1 … GemProgression38 | 20 | most gems |
  | GemProgressionExceptional | 3 | the 52 exceptional supports (Empower, Enhance, Enlighten, Greater Spell Cascade, Eclipse, …) |
  | GemProgressionExceptionalPlus | 4 | Awakened Empower, Enhance and Enlighten |
  | GemProgressionPlus1 … Plus7 | 5 | the 35 awakened supports |
  | GemProgressionSingleLevel | 1 | 21 rows: Portal, Detonate Mines, Convocation, Capture Monster, …, and some named "[UNUSED] …" or "WIP …" (never created, like the "[DNT]" variants) |
  | GemProgressionBrandRecall, BloodAndSand, PlayTest | 6 | three special cases |
  | GemProgressionRoyale1, Royale4 | 10 | Royale gems |
  | GemProgressionGraftSkill | 40 | graft skills |

- **Levels:** GrantedEffectsPerLevel (per GrantedEffect and level: the player level requirement, costs, the support
  cost multiplier `CostMultiplier`, cooldown, mana and life reservation) and GrantedEffectStatSetsPerLevel (the stats)
  go up to level 40 for most skills, for the levels gained over the cap.
- **Quality:** GrantedEffectQualityStats, per GrantedEffect: stats and their values per 1% quality, in thousandths
  (`StatsValuesPermille`). The wiki: non-integer results usually round down.
- **Requirements:** the level requirement is GrantedEffectStatSetsPerLevel `PlayerLevelReq` of the gem's level (the
  same values as GrantedEffectsPerLevel's). The client checks it itself (0x14246fff0): a gem the character is too low
  for is drawn red, "Requires level 70", and the item it is socketed in with it (checked live 2026-10-23: a level 20
  Fireball in an equipped wand, then `/set_level 10`). It still sends uses of the skill, though — it plays the cast
  animation and takes the mana off its bar before the server hears of it — so the server is the one that refuses. The
  server treats the item as inactive, as it does a worn item the character is too low for: `EquipmentStatSources`
  raises each worn item's level and attribute requirements to the highest of its socketed gems' before resolving the
  inactive items (`SocketedGemRequirements`, from catalog data and the resolver's own attribute totals, never the
  pools, which may not exist yet at login; a `/set_stat virtual_*_for_requirements` replaces the attribute there as
  it does for the client), and `InactiveItems` keeps the result. PR #175 had dropped this rule, so the
  gems stayed on the skill bar and every use was refused (restored 2026-10-29). The official game does the same
  (checked 2026-10-29 on live Path of Exile 1: boots whose tooltip lists "Requires Level 72 (gem), 142 Str (gem), 114
  Dex (gem), 62 Int (gem)" read "You cannot use this item. Its stats will be ignored" once Dexterity fell under 114,
  and became usable again when that gem was taken out). Its mods and its gems do nothing and
  its skills leave the Actor skill list, so the client has
  nothing to cast (`GemSkillEffects`); a use that arrives anyway is refused before the action is started — no 0x1ab so
  the cast does not run, and the mana pool goes out again so the cost comes back (`GemDisabled`, logged "(disabled,
  action not started)"). Socketing a gem the character is too low for is refused (0x30, `GemRequirementsUnmet`), and a
  gem's level stalls at the first one the character cannot meet by stopping its experience (`SkillCatalog.MaxLevelAt`, §1).
  Anchors from the client's
  tooltips (poedb, 3.26 data, matching GrantedEffectStatSetsPerLevel): Haste level 1 needs character level 24 and
  58 Dex, level 20 needs 70 and 155; Clarity level 1 needs 10 and 22 Int, level 20 needs 70 and 119; Fireball level 20
  needs 70 and 155 Int.
- **Attribute requirements** are the client's computation in `0x14246fc40` (per attribute; `0x14246fff0` is the level
  requirement and clamps the level the same way): for one attribute at the gem's level, with `g` the level requirement
  of that level (`PlayerLevelReq`) and `p` the gem's SkillGems share in percent
  (Strength/Dexterity/IntelligenceRequirementPercent),

  ```
  value = (3 g + 11) * (p / 100) ^ 0.9 * k + 0.5     (truncated; k = 0.5 for a support, 0.7 for a skill)
  value < 14 ? 0 : value
  ```

  The client then scales by (100 + the requirement modifiers)/100 (none on gems today). The level is the gem's, clamped
  to its cap (a corrupted level 21 gem keeps level 20's requirement). Checked against poedb over 570 tooltip cells
  (Haste, Fireball, Arc, Wrath, Grace, Discipline, Determination, Clarity, Anger, Hatred, Enfeeble, Flammability, the
  transforms' 60/40 and 33/33/34 splits, and five supports) with no mismatch: 100% at 70 is 155, 75% 119, 60%/40%
  98/68, a support at 70 is 111 and at 8 is 18, and low-level gems fall under 14 and show none. The server checks the
  same values at socketing and skill use (`GemAttributeRequirements`, `GemRequirementsUnmet`), against
  `virtual_strength/dexterity/intelligence_for_requirements` as the client does. Checked live 2026-10-23: a level 20
  Fireball socketed at `/set_stat intelligence 200`, then 130 — at 130 the client reds the gem, the wand's mods stop
  counting, and a use is refused. Ordinary percentage attributes feed the final pool and requirement fields used
  here. The issue #75 port (PR #175) had kept a qualified host active while only its gem was refused, with controls
  at Fireball 9's 67-Intelligence threshold (ordinary Q input starting at Intelligence 68, refused at 62, starting
  again after recovery, the Plate Vest keeping its 19 Armour throughout); that rule is superseded by the restored
  host deactivation above, and those controls describe the port's behaviour, not the current one. Separate aura
  transitions still need their own lifecycle evidence.
  These captures precede this port; the ordinary-mode integration checks and fresh-native limitation are recorded
  in [the port record](../dev/issue75-port.md).
- **Supports:** GrantedEffects holds what a support applies to (`AllowedActiveSkillTypes`, `ExcludedActiveSkillTypes`,
  `AddedActiveSkillTypes`, `SupportsGemsOnly`) and its skill-bar letter (`SupportGemLetter`); a skill's types come from
  its ActiveSkills row.

## 3. Skills from gems today

- Each socketed skill gem's skill at the gem's level, sent in the Actor skill list (`GemSkills`, `GemSkillEffects`):
  - **Level** from the experience as the client derives it (§1; `ItemCatalog.GemType.Level`), capped by the experience
    type (§2), with its experience stalled by the character's level requirement (§2 "Requirements"). Experience -1 is
    the cap + 1.
  - **Skill:** the variant's GemEffects `GrantedEffect`; a transfigured or Trarthan gem is its own skill (Ice Nova of
    Frostbolts grants IceNovaAltX). A Vaal gem grants two skills, its Vaal skill and its base skill (`GrantedEffect2`),
    each with its own skill id. A support gem grants none.
  - **Numbers** from the level's rows: the GrantedEffectsPerLevel row (cost, reservation, cooldown, Vaal souls) and the
    GrantedEffectStatSetsPerLevel row (damage and the other stats), the highest level at or below the gem's when a
    skill lacks that level. Fireball deals 9–14 for 5 mana at level 1 and 1883–2825 for 20 mana at level 20; Clarity
    reserves 34 mana at level 1 and 279 at level 20.
  - **Quality:** GrantedEffectQualityStats per 1% in thousandths × the quality, rounded toward zero (Fireball at 20%:
    150 × 20 / 1000 = +3 radius). Whether the 16 negative quality values round toward zero or down is unverified.
  - **Cooldown:** the cooldown of the gem's level (GrantedEffectsPerLevel `Cooldown`, the auras' 1.2 s) is the one
    the server enforces and sends with the skill ([11](11-skills.md)); the server allows one use per cooldown even when
    `StoredUses` is higher.
  - **Vaal skills** have a soul collector and native budget payloads; unsupported Vaal executors remain refused.
    See [27-vaal-souls.md](27-vaal-souls.md); collection does not enable casting.
- **Not created** (`ItemCatalog.FindGem`, `/gem`): the "[DNT]" placeholder variants, the "[UNUSED] …", "[DNT] …" and
  "WIP …" gem rows, the awakened supports other than Awakened Empower, Enhance and Enlighten, and the Vaal variants
  whose base skill is only a placeholder's (Vaal Fireball has only its normal variant). A Vaal variant's name is "Vaal "
  and its base variant's ("Vaal Ice Nova of Frostbolts"); the table names them all "Vaal Ice Nova". Whether the game
  names them so is unverified.
- `/gem <name> [level] [quality]` puts a gem into the inventory (any variant by its name, levels 1 to the cap,
  quality 0 to 20).
- **Skill ids stay with their gem** (`GemSkillIds`): a gem keeps its skill id while it stays socketed. A gem moved to
  another socket keeps it too (matched by its skill): a move goes through the cursor, where the gem is out of the
  equipment for an update or more, so the id of a gem that left waits for a gem of its skill to come back. A new gem
  takes the lowest gem skill id no gem had, and only when none is left the id of the gem that left longest ago. The gem
  skill ids are the ids below `FirstSkillId + 64` that no basic action uses (`SkillCatalog.IsGemSkill`): 55 in 3.29.3b.
  The client binds skill bar keys by skill id: with ids given in socket order, taking Haste out of a wand gave Righteous
  Fire Haste's id, and Haste's keys then cast Righteous Fire (live 2026-10-20).
- An id a gem gives up never goes to another skill's gem in the same update. A gem swapped into a socket (the old gem
  goes to the cursor) or an item equipped over another changes the gems in one update; the old skill must go out as
  removed, so its aura or Righteous Fire ends and its skill bar key does not cast the new gem.
- A skill that goes away (its gem left the equipment) ends its aura or Righteous Fire, and the reserved mana comes back.
  One aura of a kind runs at a time: a second gem of the same aura can't start it.
- Support gems are not granted: one as a skill crashed the client ([13](13-items.md) §6). The skill record has a
  support list ([11](11-skills.md) §3), not used yet.
- Skill changes after login (equipment, levels, passives, auras) go out as the 0x20 update, which removes skills and
  tells the character panel ([11](11-skills.md) §3).

## 4. Levelling up and the level lock

The client draws the "+" on a gem whose experience is one point below the next level's threshold (the server's cap,
§1). Clicking it sends C→S 0x31 ([13](13-items.md) §6): u32 inventory id, u32 item id, u32 socket index (14 bytes,
live 2026-10-23). The server writes the next level's threshold into the gem and replaces the item (0xbf, the remove and
add idiom), so the client derives the new level and its skill; the skills go out again as the 0x20 Actor update. The
new level's requirements are the server's check (`GemRequirementsUnmet` at that level); a locked gem is refused. The
client's own check does not look at the requirements — only the lock and the remaining experience — so a gem whose next
level the character does not meet still shows the "+", and the click is answered with what is missing (checked live
2026-10-23: Fireball at level 9 with the experience one below level 10 on a character set to level 31, level 10 needing
32; then with 70 Int, level 10 needing 75). A gem lying in the inventory, not in a socket, sent nothing in the captures.

The lock: a right-click on a socketed gem sends C→S 0xd7 (15 bytes): the same three u32s and a u8, 1 to lock and 0 to
unlock. The client keeps the lock in the SkillGem flags +0x10 bit 2 — its level-up check `0x142472db0` returns false
when the bit is set, so the "+" goes away — and the server writes the bit into the item (`GemFlags`) and saves it, so
the lock is back after a login. Checked live 2026-10-23: the "+" appears with `/gem_xp near`, the click levels the gem
(level 1 → 2, 9–14 → 10–16 damage), right-click locks it, the lock survives a relogin, unlocking brings the "+" back.

The client's level-up check `0x142472db0` (reached from `0x1402169c0`, a vtable slot at `0x143036de8`) does not only
test the threshold: with `remaining = next − experience` it wants `remaining <= 10 × v` and
`remaining <= (next − current) / 4`, `v` being a field of the next level's stat set. Only the last condition was
exercised live (experience one below the threshold); whether the client also draws the "+" for the last quarter of a
level's bar is not checked, and the server only ever leaves a gem one point below the threshold.

## 5. Getting gems (quest rewards and vendors, 2026-10-26)

Quest rewards are the first acquisition path (`QuestRewardCatalog`, [12](12-quests.md) §1). `QuestRewardOffers` has
140 offers; the gem ones give level-1 gems filtered by the `Characters` class rows (Witch's a1q4 offer 3 is Frost
Bomb, Orb of Storms, Summon Raging Spirit, Contagion, Detonate Dead and Flame Wall). Talking to the NPC opens the
window the client's NPCTalk row names (`QuestRewardOffersKey`); taking a choice puts the item in the main inventory and
sets the offer's `QuestFlag` (the "taken" flag) in one save. The Gateway now offers every table row whose items
`Create` can build (level-1 items and normal flasks), instead of the hardcoded offers 0/1/2/8; unbuildable rows
(level-8 magic armours, magic belts) are skipped, as are offers with no buildable choice. Checked live 2026-10-26 on
offer 3 after `/quest_flag a1q4-PlacedGlyphs`: the six Witch gems, claim, flag and repeat refusal.

**Local Nessa shop.** This checkout restores Nessa's full testing assortment independently of character class and
quest completion (`NpcShops.Tabs`, `fullGemStock`). Every writable gem with a positive drop level and a creatable
variant is included, including transfigured, Trarthan and creatable Vaal variants. Red/green/blue skill and support
tabs stay separate; White Skills contains both white active and support gems. Full 12-by-12 pages continue as
numbered tabs instead of dropping overflow. These colour tabs are permanent development tools: they must always
contain the complete creatable assortment without quest or class gates, so command and mechanic development
does not depend on completing the campaign.
The separate Skills tab is the future gameplay shop: only ordinary gems unlocked by the character's quest flags
and class through GemVendorStock.Stock, at the existing vendor prices. Without progression input it is empty;
alternative variants are not unlocked implicitly. Active gems precede supports; each kind groups red, green, blue
and white gems, then orders by drop level and name within each colour. Supports start at column zero of the next
row, leaving the active row's remaining cells empty; if necessary they continue on the next page. It also paginates
if needed. The distinction is documented in NpcShops.cs; opening the shop sends no development explanation to
the player's chat. Keep progression restrictions confined to Skills when changing this shop.
Gems on every Nessa tab, including Skills and Goods, have unlimited stock. Each successful purchase pays the normal
price and receives a fresh player inventory ID; the shop template retains its ID, cell, price, quality and variant.
A manager-1 remove/add refresh keeps it visible and available for another purchase without reopening the shop.
An unpaid or failed purchase leaves the template available and grants nothing; payment and delivery remain atomic.
Other vendors and Nessa's non-gem goods remain finite.
Variant indices survive purchase and save/reload. Existing catalog exclusions still apply to placeholders and
unsupported awakened rows. The client data does not provide the former Anomalous/Divergent/Phantasmal quality
variants; those names are not fabricated. Automated stock/serialization tests cover this local assortment;
its updated client display still needs a live check. Other vendors retain quest progression below.

**Vendors.** The currency/flask/jewellery vendors that also sell gems (`GemVendorStock.Sells`: Nessa, Yeena,
Clarissa, Petarus and Vanja, Lani, Lilly Roth, Siosa) give seven tabs - Red/Green/Blue Skills and Support, White
Skills - of level-1 gems, each at the price of its level-1 requirement tier: 1–7 Scroll of Wisdom, 8–15 Orb of
Transmutation, 16–27 Orb of Alteration, 28–37 Orb of Chance, 38+ Orb of Alchemy ([17](17-npcs.md) §5,
`GemVendorStock.Price`). A quest unlocks its gems once its reward is
shown or was taken; act vendors filter that set to the character's class and to `ItemCatalog.ShopGems` (creatable,
dropping, not Vaal or awakened). Siosa (after `a3q12-DeliveredPages`) and Lilly Roth (after
`UnlockedLillyGemShop`) ignore the class restriction, and Lilly also the quest completion; the 3.29 cross-class
additions put Faster Attacks on Nessa after a1q2 and Faster Casting on Yeena after a2q4 (`GemVendorStock`).
Checked live 2026-10-26 on Nessa: the Witch's stock was the six a1q1 gems plus the six a1q4 gems, then the six a1q2
gems and Faster Attacks Support after `/quest_flag a1q2-KilledBrutus`; the a1q1/a1q4 gems cost 1 Scroll of Wisdom and
the a1q2 ones plus Faster Attacks 1 Orb of Transmutation, as their requirement tiers say. Lilly is
unit-tested only (the Act 6 town has no walk-map capture and spawns the player in water) and Siosa is not placed (The
Library is not reachable).
**Drops.** A monster's item drops include a gem 4% of the time (`ItemDrops`, our rate until the game's drop pools are
known): a gem from `GemSources.DropPool` whose `DropLevel` is at most the monster's level, at gem level 1 and with no
rarity roll. The pool is the obtainable gems only: every class's quest reward gems
(`QuestRewardOffers`), the starting gems (Characters `StartSkillGem`/`StartSupportGem`) and the drop-only exceptional
supports (Empower, Enhance, Enlighten). The data's Graft skills (the Chayula gems), item-granted and legacy skills
(Envy, Death Aura, Quickstep, the single-level rows) and placeholders such as "..." never drop. Because of the level
gate a low-level monster only drops low-drop-level gems. Checked by `GemDropsTests` and live 2026-10-26 (rare monsters
dropped normal skill and support gems). The first live attempt dropped the data's item-granted/legacy rows (Envy,
Death Aura, "...") at monster level 1 and led to this pool.

Still placeholder: every gem costs 1 Scroll of Wisdom at the vendors.

## Open

- What IndexableSupportGems (+0x40) is for; the u8's other bits.
- Not checked with the real client yet: a gem of a higher level or with quality, a transfigured gem's tooltip and skill,
  a Vaal gem's two skills, and how the client shows a Vaal skill without souls and a skill on cooldown. Checked
  2026-10-23: the level requirement (a level 20 gem red at character level 10; the client still sends its use, the
  server refusing the action) and the socketing refusal; the attribute requirements were fitted and checked live on
  2026-10-23 (`/set_stat intelligence 200`, then 130, under a level 20 Fireball); the "+", the level-up and the lock
  (§4).
- A gem in the inventory (not socketed): its "+" and lock messages were not seen; the client may level it locally.
- Verify disabled-gem aura transitions with changing attributes and links.
- Linked support resolution and the complete skill-record layout are documented in [25](25-gem-supports.md).
