-- Client table KeywordPopups: all 46 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "KeywordPopups" ("_row", "Id", "Term", "Definition", "__col3", "__col4", "ItemReference", "ModReference") VALUES
(0, 'Test', 'Test', 'This test case is designed to be overwitten by other content', '', '', NULL, NULL),
(1, 'Intangibility', 'Intangibility', 'When Allflame crafting, many crafts will apply "Intangibility" to the resultant item, at a certain percentage. ' || char(13) || '
' || char(13) || '
When then performing further Allflame crafts, there is a chance that Vesper will only be able to create one Ghostly Copy of the item, with that chance being equal to the Intangibility on the item being crafted upon.', '', '', NULL, NULL),
(2, 'Phasing', 'Phasing', 'While you have Phasing, your movement is not blocked by Enemies.', '', '', NULL, NULL),
(3, 'ValourFunction', 'Valour', 'Valour is gained while you have a Banner skill and augments the power of those skills.', '', '', NULL, NULL),
(4, 'ValourGain', 'Valour', 'By default you gain 1 Valour on Melee Kill, or on Melee Hit against Rare or Unique enemies, no more than once every 0.5 seconds, up to a maximum of 50.', '', '', NULL, NULL),
(5, 'Enshroud', 'Enshrouded Item', 'Enshrouded items can be transformed in the Domain of Timeless Conflict to another Unique of the same item class, with a Vestigial implicit modifier derived from the Enshrouded item.' || char(13) || '
' || char(13) || '
To transform an Enshrouded item place it in a Crystal of Permutation within the Domain of Timeless Conflict. Killing monsters in the Domain will charge the Enshrouded Item. When the Enshrouded item is fully charged it gains one [VariantRarity|Variant Rarity]. You can interact with the Crystal to drop the new item, or continue charging the item to gain more Variant Rarity, resulting in potentially rarer dropped items.', '', '', NULL, NULL),
(6, 'VariantRarity', 'Variant Rarity', 'The higher the Variant Rarity on an Enshrouded item, the more likely the Vestigial item will be a rarer Unique item. Variant Rarity is capped at 3.', '', '', NULL, NULL),
(7, 'Scrying', 'Map Scrying', 'Scrying causes the Divination Cards found in a Map to instead be the Divination Cards found in the Map area listed on the Scrying Orb.' || char(13) || '
' || char(13) || '
You can only Scry to or from non-Unique Maps with bonus objectives.', '', '', NULL, NULL),
(8, 'ScryingOrb', '', '', '', '', 0, NULL),
(9, 'PactAffliction', 'Pact Afflictions', 'Afflictions granted from Pact skills are a type of debuff. By default Afflictions last for 10 seconds and you can have a maximum of 5 Afflictions on you at a time. You cannot have more than one of the same type of Affliction.' || char(13) || '
' || char(13) || '
Afflictions from Pact skills do not count as Afflictions from the Forbidden Sanctum.', '', '', NULL, NULL),
(10, 'PactBoon', 'Pact Boons', 'Boons granted from Pact skills are a type of buff. By default Boons last for 10 seconds and you can have a maximum of 5 Boons on you at a time. You cannot have more than one of the same type of Boon.' || char(13) || '
' || char(13) || '
Boons from Pact skills do not count as Boons from the Forbidden Sanctum.', '', '', NULL, NULL),
(11, 'BasicCurrency', 'Basic Currency', 'Basic currency includes any currency that drops from non-league content. Basic currency does not include Portal Scrolls or Scrolls of Wisdom.', '', '', NULL, NULL),
(12, 'SoulEater', 'Soul Eater', 'While you have Soul Eater, you consume the souls of anything killed nearby, gaining 5% increased Attack and Cast speed for each. You lose 1 soul every 0.5 seconds if you have not eaten any souls Recently. Maximum of 45 souls consumed', '', '', NULL, NULL),
(13, 'ContainsAbyss', 'Abyss', 'An Abyss is a pit which leads deep underground with multiple fissures branching from it. Any monsters spawned near an Abyss will be weakened by it.' || char(13) || '
Defeating these weakened monsters will cause the fissures to close.' || char(13) || '
Once the fissures have closed all the way to the pit, the pit will activate and spawn a large number of Abyssal monsters. Defeating these monsters will close the pit. ' || char(13) || '
' || char(13) || '
Each closed pit has a chance to spawn an Abyssal Trove. The final Abyss in the area has a chance to instead open an underground dungeon called an [AbyssalDepths|Abyssal Depths].' || char(13) || '
' || char(13) || '
There are three separate groups of Abyssal Monsters: the Lightless, the Blackblooded and the Legion of the Pit, each area generally contains one of these groups.' || char(13) || '
The rarity of the monsters spawned depends on the rarity of the slain weakened monsters and the emerging Abyssal will steal some or all of the modifiers from them.' || char(13) || '
' || char(13) || '
If an area already has an Abyss, modifiers that cause areas to contain additional Abysses will instead add two additional pits.', '', '', NULL, NULL),
(14, 'AbyssalCrystal', 'Abyssal Crystal', 'Each Abyssal Crystal spawned increases the difficulty and reward of all subsequent Abyssal Monsters spawned from Pits.', '', '', NULL, NULL),
(15, 'AbyssalConsort', 'Abyssal Consort', 'Abyssal Consorts gain additional modifiers, difficulty and reward for each prior Abyss pit that has opened.', '', '', NULL, NULL),
(16, 'AbyssalSkitterlock', 'Abyssal Skitterlock', 'Abyssal Skitterlocks are chests filled with Scarabs.', '', '', NULL, NULL),
(17, 'AbyssFactionUlaman', 'Ulaman Faction', 'If monsters are loyal to Ulaman, Abyssal Troves drop Jewellery, [MurderousEyeJewel|Murderous Eye Jewels] and [SearchingEyeJewel|Searching Eye Jewels].', '', '', NULL, NULL),
(18, 'AbyssFactionAmanamu', 'Amanamu Faction', 'If monsters are loyal to Amanamu, Abyssal Troves drop Armour items and [GhastlyEyeJewel|Ghastly Eye Jewels].', '', '', NULL, NULL),
(19, 'AbyssFactionKurgal', 'Kurgal Faction', 'If monsters are loyal to Kurgal, Abyssal Troves drop Weapon items and [HypnoticEyeJewel|Hypnotic Eye Jewels].', '', '', NULL, NULL),
(20, 'AbyssFedSoul', 'Abyssal Souls', 'Killing monsters near [ContainsAbyss|Abyss] chasms cause a soul to be released towards the chasm. Souls fed to chasms spawn Abyssal monsters.', '', '', NULL, NULL),
(21, 'AbyssalDepths', 'Abyssal Depths', 'Abyssal Depths are underground dungeons that contain a powerful Abyssal Lich.', '', '', NULL, NULL),
(22, 'MurderousEyeJewel', '', '', '', '', 2, NULL),
(23, 'SearchingEyeJewel', '', '', '', '', 3, NULL),
(24, 'HypnoticEyeJewel', '', '', '', '', 4, NULL),
(25, 'GhastlyEyeJewel', '', '', '', '', 5, NULL),
(26, 'SpiritInfusion', 'Spirit Infusion', 'Each stack of Spirit Infusion grants 15% faster start of Energy Shield Recharge, Channelling Spells deal 5% more Damage and Channelling Spells have 10% more Cost. Maximum of 10 stacks. You lose one stack every 0.5 seconds if you have not gained a stack in the past 4 seconds.', '', '', NULL, NULL),
(27, 'BrineCharges', 'Brine Charges', 'You gain 4% of Physical Damage as extra Cold Damage and as extra Lightning Damage per Brine Charge.' || char(13) || '
' || char(13) || '
Maximum Brine Charges is equal to Maximum Endurance Charges', '', '', NULL, NULL),
(28, 'BifurcatedCrit', 'Bifurcated Critical Strikes', 'Critical Strike chance is rolled twice when determining if a Critical Strike should occur:' || char(13) || '
If either roll succeeds the hit will be a regular Critical Strike, and if both rolls succeed the amount of extra damage dealt by the Critical Strike will be doubled. This is equivalent to doubling the amount by which the Critical Strike Multiplier exceeds 100 for that Hit.' || char(13) || '
' || char(13) || '
If a modifier makes your Critical Strike Chance Lucky or Unlucky, that luck will apply individually to each of the critical strike chance rolls.' || char(13) || '
' || char(13) || '
Effects which cause Hits to Always be Critical Strikes prevent Bifurcation - those hits do not roll the chance, so cannot roll it twice.', '', '', NULL, NULL),
(29, 'EffectiveChance', 'Effective Chance', 'Some effects manipulate how random chances are rolled in ways that affect the result without changing the stated chance for the effect, such as a chance being Lucky causing it to be rolled twice and use the better result, or an effect skipping the roll entirely to force something to succeed or fail. The Effective chance is the real chance to get a specific result, accounting for all such roll-manipulating effects.' || char(13) || '
' || char(13) || '
For example, if you had a Critical Strike Chance of 20%, and your Critical Hit chance was Lucky, your Effective chance to get a Critical Strike would be 36%, which is the chance that at least one of the two 20% rolls succeeds.', '', '', NULL, NULL),
(30, 'EmpoweredSpell', 'Empowered Spells', 'Pacts can Empower certain Spells causing them to gain additional effects. Empowerments expire if not used within 15 seconds. Instant Spells, Vaal Spells and Brand Spells cannot be Empowered by default.', '', '', NULL, NULL),
(31, 'HungerOfGhorr', 'Hunger of Ghorr', 'Causes Fleshrend to trigger periodically. Gains a Stage every time you cast a different [EmpoweredSpell|Empowered Spell] with Pact of Ghorr, up to a maximum of 3 Stages. Stages are reset back to 0 when you recast Pact of Ghorr.', '', '', NULL, NULL),
(32, 'GoldenLanterns', 'Golden Lanterns', 'Golden Lanterns grant a significant bonus to Rarity of items found when collected, lasting for the remainder of the instance.', '', '', NULL, NULL),
(33, 'MonsterFracture', 'Monster Fracturing', 'Monsters that Fracture on death will split into multiple copies of that Monster at one Rarity tier lower.', '', '', NULL, NULL),
(34, 'DeadMansSulphur', '', '', '', '', 1, NULL),
(35, 'KeystoneFirePurist', 'Voracious Flame', 'You can inflict an additional Ignite on each Enemy' || char(13) || '
Base Ignite Duration is 1 second' || char(13) || '
25% less Damage with Ignite' || char(13) || '
Cannot deal non-Fire Damage', '', '', NULL, NULL),
(36, 'KeystoneColdPurist', 'Bitter Frost', 'Enemies Chilled by your Hits have Cold Damage taken increased by Chill Effect' || char(13) || '
Enemies in your Chilling Areas have Cold Damage taken increased by Chill Effect' || char(13) || '
Cannot deal non-Cold Damage', '', '', NULL, NULL),
(37, 'KeystoneLightningPurist', 'Roiling Tempest', '25% more Maximum Lightning Damage' || char(13) || '
50% less Minimum Lightning Damage' || char(13) || '
Cannot deal non-Lightning Damage', '', '', NULL, NULL),
(38, 'DirectFlight', 'Direct Flight', 'Direct Flight is the distance a projectile can travel in a single straight line before it''s flight ends. At that point, by default, it will either Return if able, or otherwise dissipate.' || char(13) || '
' || char(13) || '
Projectile behaviours that cause the projectile to change directions such as Splitting, Forking, Chaining and Returning start a new Direct Flight in the new direction.', '', '', NULL, NULL),
(39, 'GhostlyCopy', 'Ghostly Copies', 'Ghostly Copies of items that are not chosen will vanish. Items created as a result of Allflame Crafting cannot be returned to a previous state via Imprinting.', '', '', NULL, NULL),
(40, 'PhysicalThorns', 'Physical Thorns', 'Physical Thorns activates when Hit by Physical Damage and lasts for 5 seconds.' || char(13) || '
While active, it reflects a fixed amount of Physical Damage each time the monster with Physical Thorns receives a Physical Hit.' || char(13) || '
Monsters cannot gain Physical Thorns more than once every 10 seconds.' || char(13) || '
You can only take Physical Thorns damage once every 0.1 seconds.', '', '', NULL, NULL),
(41, 'ElementalThorns', 'Elemental Thorns', 'Elemental Thorns activates when Hit by Elemental Damage and lasts for 5 seconds.' || char(13) || '
While active, it reflects a fixed amount of Elemental Damage each time the monster with Elemental Thorns receives an Elemental Hit, dealing Damage of the highest Elemental Damage type in the received Hit.' || char(13) || '
Monsters cannot gain Elemental Thorns more than once every 10 seconds.' || char(13) || '
You can only take Elemental Thorns damage once every 0.1 seconds.', '', '', NULL, NULL),
(42, 'CrabTotem', 'Crab Totem', 'Supports Spell skills. Instead of casting that Spell, you will summon a Crab that casts the Spell for you. Crabs created this way are not Minions, but are considered to be Totems.' || char(13) || '
' || char(13) || '
Cannot Support Channelling, Triggered, Cooldown or Vaal Spells, nor Skills used by things other than you, such as Traps, Mines or other proxies.', '', '', NULL, NULL),
(43, 'Draugurflame', 'Draugurflame', 'Draugurflames burn enemies within 3 metres of you, dealing Fire Damage equal to 15% of the Damage per second of the strongest Ignite you''ve inflicted in the past 8 seconds. This damage cannot be modified.', '', '', NULL, NULL),
(44, 'BoneFeeders', 'Bone Feeders', 'Summon two Bone Feeders which follow you around. Bone Feeders consume corpses they walk over, creating a temporary Aura after consuming 10 corpses. ' || char(13) || '
One Bone Feeder creates a Curse Aura of either Temporal Chains, Enfeeble or Punishment that ignores Curse Limits.' || char(13) || '
The other Bone Feeder creates an Aura that causes Enemy Damage with Hits to be Unlucky.', '', '', NULL, NULL),
(45, 'Toughness', 'Toughness', 'The higher toughness a monster has, the less damage it takes. A monster with 100% increased toughness takes 50% less damage, a monster with 200% increased toughness takes 67% less damage, and so on. ' || char(13) || '
' || char(13) || '
Negative toughness instead causes the monster to take more damage. A monster with 100% reduced toughness takes 100% more damage, a monster with 200% reduced toughness takes 200% more damage, and so on. ' || char(13) || '
' || char(13) || '
Effectively, each 100% of toughness halves the damage a monster takes, and each -100% toughness doubles the damage a monster takes.', '', '', NULL, NULL);
