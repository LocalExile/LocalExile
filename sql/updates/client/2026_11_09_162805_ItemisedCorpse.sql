-- Client table ItemisedCorpse: all 114 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "ItemisedCorpse" ("_row", "BaseItem", "MonsterVariety", "MonsterAbilities", "MonsterCategory") VALUES
(0, 4968, 10181, 'Fires Barrage of Arrows' || char(13) || '
Fires Hail of Arrows', 0),
(1, 4969, 10182, 'Fires Barrage of Arrows' || char(13) || '
Fires Hail of Arrows' || char(13) || '
Fires Forking Arrow', 0),
(2, 4970, 10183, 'Fires Barrage of Arrows' || char(13) || '
Fires Hail of Arrows' || char(13) || '
Fires Forking Arrow' || char(13) || '
Casts Hatred', 0),
(3, 4971, 10184, 'Casts Glacial Cascade' || char(13) || '
Casts Arctic Armour' || char(13) || '
Uses Glacial Hammer' || char(13) || '
Becomes Empowered if Allied Fiery Cannibal nearby' || char(13) || '
Glacial Cascade Triggers Ice Nova on hit while Empowered', 5),
(4, 4972, 10185, 'Casts Glacial Cascade' || char(13) || '
Casts Arctic Armour' || char(13) || '
Uses Glacial Hammer' || char(13) || '
Becomes Empowered if Allied Fiery Cannibal nearby' || char(13) || '
Glacial Cascade Triggers Ice Nova on hit while Empowered' || char(13) || '
Glacial Hammer Triggers Ice Crash on hit while Empowered', 5),
(5, 4973, 10186, 'Casts Glacial Cascade' || char(13) || '
Casts Arctic Armour' || char(13) || '
Uses Glacial Hammer' || char(13) || '
Becomes Empowered if Allied Fiery Cannibal nearby' || char(13) || '
Glacial Cascade Triggers Ice Nova on hit while Empowered' || char(13) || '
Glacial Hammer Triggers Ice Crash on hit while Empowered' || char(13) || '
Casts Purity of Ice', 5),
(6, 4974, 10187, 'Casts Firestorm' || char(13) || '
Casts Flame Dash' || char(13) || '
Becomes Empowered if allied Frozen Cannibal nearby' || char(13) || '
Casts Cremation while Empowered', 5),
(7, 4975, 10188, 'Casts Firestorm' || char(13) || '
Casts Flame Dash' || char(13) || '
Becomes Empowered if allied Frozen Cannibal nearby' || char(13) || '
Casts Cremation while Empowered' || char(13) || '
Uses Molten Strike while Empowered', 5),
(8, 4976, 10189, 'Casts Firestorm' || char(13) || '
Casts Flame Dash' || char(13) || '
Becomes Empowered if allied Frozen Cannibal nearby' || char(13) || '
Casts Cremation while Empowered' || char(13) || '
Uses Molten Strike while Empowered' || char(13) || '
Casts Purity of Fire', 5),
(9, 4977, 10190, 'Reforms after being killed', 2),
(10, 4978, 10191, 'Reforms after being killed' || char(13) || '
Explodes on death for 5% of life as Physical, Fire, and Lightning Damage', 2),
(11, 4979, 10192, 'Reforms after being killed' || char(13) || '
Explodes on death for 5% of life as Physical, Fire, and Lightning Damage' || char(13) || '
Explosion Scorches', 2),
(12, 4980, 10270, 'Casts Cannon Barrage' || char(13) || '
Casts Tidal Wave' || char(13) || '
Summons Geysers', 5),
(13, 4981, 10271, 'Casts Cannon Barrage' || char(13) || '
Casts Tidal Wave' || char(13) || '
Summons Geysers' || char(13) || '
Precision', 5),
(14, 4982, 10272, 'Casts Cannon Barrage' || char(13) || '
Casts Tidal Wave' || char(13) || '
Summons Geysers' || char(13) || '
Precision' || char(13) || '
Nearby Allies take reduced Cold Damage', 5),
(15, 4983, 10280, 'Cyclones' || char(13) || '
Lacerates' || char(13) || '
Rains down blades', 2),
(16, 4984, 10281, 'Cyclones' || char(13) || '
Lacerates' || char(13) || '
Rains down blades' || char(13) || '
Nearby allies have 20% chance to Impale with Attacks', 2),
(17, 4985, 10282, 'Cyclones' || char(13) || '
Lacerates' || char(13) || '
Rains down blades' || char(13) || '
Nearby allies have 20% chance to Impale with Attacks' || char(13) || '
Nearby allies have 30% increased Impale Effect', 2),
(18, 4986, 10211, 'Throws Physical Projectiles', 1),
(19, 4987, 10212, 'Throws Physical Projectiles' || char(13) || '
100% increased Impale Effect', 1),
(20, 4988, 10213, 'Throws Physical Projectiles' || char(13) || '
100% increased Impale Effect' || char(13) || '
Owner gains 10% increased Impale Effect', 1),
(21, 4989, 10193, 'Attacks with swords when not Defensive' || char(13) || '
Fires Mortars when Defensive', 0),
(22, 4990, 10194, 'Attacks with swords when not Defensive' || char(13) || '
Fires Mortars when Defensive' || char(13) || '
Inflicts Withered for 2 seconds on Hit', 0),
(23, 4991, 10195, 'Attacks with swords when not Defensive' || char(13) || '
Fires Mortars when Defensive' || char(13) || '
Inflicts Withered for 2 seconds on Hit' || char(13) || '
Creates Aura of Chaos when hitting Poisoned enemies', 0),
(24, 4992, 10220, 'Spells have 1% base Critical Strike Chance' || char(13) || '
Has 400% base Critical Strike Multiplier', 1),
(25, 4993, 10221, 'Spells have 1% base Critical Strike Chance' || char(13) || '
Has 400% base Critical Strike Multiplier' || char(13) || '
Nearby allies have +30% Critical Strike Multiplier', 1),
(26, 4994, 10222, 'Spells have 1% base Critical Strike Chance' || char(13) || '
Has 400% base Critical Strike Multiplier' || char(13) || '
Nearby allies have +30% Critical Strike Multiplier' || char(13) || '
Casts Zealotry', 1),
(27, 4995, 10205, 'Cyclones' || char(13) || '
Cleaves', 1),
(28, 4996, 10206, 'Cyclones' || char(13) || '
Cleaves' || char(13) || '
Maims on hit', 1),
(29, 4997, 10207, 'Cyclones' || char(13) || '
Cleaves' || char(13) || '
Maims on hit' || char(13) || '
Owner gains 10% increased Effect of Maim', 1),
(30, 4998, 10249, '', 3),
(31, 4999, 10250, '', 3),
(32, 5000, 10251, '', 3),
(33, 5001, 10202, 'Vomits Blood' || char(13) || '
Applies Corrupting Blood', 1),
(34, 5002, 10203, 'Vomits Blood' || char(13) || '
Applies Corrupting Blood' || char(13) || '
Absorbs Corrupting Blood, exploding it and dealing Physical Damage based on the damage of the Corrupting Blood', 1),
(35, 5003, 10204, 'Vomits Blood' || char(13) || '
Applies Corrupting Blood' || char(13) || '
Absorbs Corrupting Blood, exploding it and dealing Physical Damage based on the damage of the Corrupting Blood' || char(13) || '
Casts Pride', 1),
(36, 5004, 10208, 'Attacks with Dualstrike' || char(13) || '
Periodically enrages', 1),
(37, 5005, 10209, 'Attacks with Dualstrike' || char(13) || '
Periodically enrages' || char(13) || '
Attack Damage is affected by Owner''s Rage', 1),
(38, 5006, 10210, 'Attacks with Dualstrike' || char(13) || '
Periodically enrages' || char(13) || '
Attack Damage is affected by Owner''s Rage' || char(13) || '
Owner gains 5% of Physical Damage as Extra Fire Damage', 1),
(39, 5007, 10274, '', 5),
(40, 5008, 10275, '', 5),
(41, 5009, 10276, '', 5),
(42, 5010, 10277, 'Throws Poisonous Concoction', 5),
(43, 5011, 10278, 'Throws Poisonous Concoction' || char(13) || '
200% increased effect of flasks', 5),
(44, 5012, 10279, 'Throws Poisonous Concoction' || char(13) || '
200% increased effect of flasks' || char(13) || '
Owner gains 1 life flask charge every 3 seconds', 5),
(45, 5013, 10228, '', 2),
(46, 5014, 10229, '', 2),
(47, 5015, 10230, '', 2),
(48, 5016, 10286, 'Creates moving Cursed Ground', 3),
(49, 5017, 10287, 'Creates moving Cursed Ground' || char(13) || '
Uses Smite', 3),
(50, 5018, 10288, 'Creates moving Cursed Ground' || char(13) || '
Uses Smite' || char(13) || '
Hexes can affect Hexproof Enemies', 3),
(51, 5019, 10240, 'Summons skeletons' || char(13) || '
Fires mortars' || char(13) || '
Creates Chaos Offering' || char(13) || '
Creates Fire Offering', 3),
(52, 5020, 10241, 'Summons skeletons' || char(13) || '
Fires mortars' || char(13) || '
Creates Chaos Offering' || char(13) || '
Creates Fire Offering' || char(13) || '
Ally skeletons deal additional fire and chaos damage with attacks', 3),
(53, 5021, 10242, 'Summons skeletons' || char(13) || '
Fires mortars' || char(13) || '
Creates Chaos Offering' || char(13) || '
Creates Fire Offering' || char(13) || '
Ally skeletons deal additional fire and chaos damage with attacks' || char(13) || '
Casts Discipline', 3),
(54, 5022, 10255, 'Casts Dash of Destruction' || char(13) || '
Casts Screech of Splendor', 4),
(55, 5023, 10256, 'Casts Dash of Destruction' || char(13) || '
Casts Screech of Splendor' || char(13) || '
Screech of Splendor inflicts Lightning Exposure', 4),
(56, 5024, 10257, 'Casts Dash of Destruction' || char(13) || '
Casts Screech of Splendor' || char(13) || '
Screech of Splendor inflicts Lightning Exposure' || char(13) || '
Casts Grace', 4),
(57, 5025, 10234, 'Casts Shock Nova' || char(13) || '
Casts Spark' || char(13) || '
Casts Lightning Warp', 2),
(58, 5026, 10235, 'Casts Shock Nova' || char(13) || '
Casts Spark' || char(13) || '
Casts Lightning Warp' || char(13) || '
Casts Wrath', 2),
(59, 5027, 10236, 'Casts Shock Nova' || char(13) || '
Casts Spark' || char(13) || '
Casts Lightning Warp' || char(13) || '
Casts Wrath' || char(13) || '
Lightning Damage of nearby allies is Lucky', 2),
(60, 5028, 10199, 'Casts Lightning Burst', 0),
(61, 5029, 10200, 'Casts Lightning Burst' || char(13) || '
Casts Clarity', 0),
(62, 5030, 10201, 'Casts Lightning Burst inflicting Sap on Hit' || char(13) || '
Casts Clarity', 0),
(63, 5031, 10246, '20% increased Physical Weapon Damage per 10 Strength', 3),
(64, 5032, 10247, '20% increased Physical Weapon Damage per 10 Strength' || char(13) || '
Crushes on Hit', 3),
(65, 5033, 10248, '20% increased Physical Weapon Damage per 10 Strength' || char(13) || '
Crushes on Hit' || char(13) || '
Owner gains 5% More Physical Attack Damage', 3),
(66, 5034, 10283, 'Uses Leap Slam' || char(13) || '
Uses Sweep' || char(13) || '
Uses Enduring Cry' || char(13) || '
Uses Immortal call', 5),
(67, 5035, 10284, 'Uses Leap Slam' || char(13) || '
Uses Sweep' || char(13) || '
Uses Enduring Cry' || char(13) || '
Uses Immortal call' || char(13) || '
Casts Vitality', 5),
(68, 5036, 10285, 'Uses Leap Slam' || char(13) || '
Uses Sweep' || char(13) || '
Uses Enduring Cry' || char(13) || '
Uses Immortal call' || char(13) || '
Casts Vitality' || char(13) || '
Shares Endurance Charges with allies', 5),
(69, 5037, 10243, 'Causes Bleeding', 3),
(70, 5038, 10244, 'Causes Bleeding' || char(13) || '
Nearby enemies have 50% reduced Life Regeneration rate', 3),
(71, 5039, 10245, 'Causes Bleeding' || char(13) || '
Nearby enemies have 50% reduced Life Regeneration rate' || char(13) || '
Can inflict Bleeding on an Enemy up to 16 times' || char(13) || '
Bleeding does not deal extra Damage while the enemy is moving', 3),
(72, 5040, 10237, 'Summons Constructs' || char(13) || '
5% increased Attack Damage per 450 Armour', 2),
(73, 5041, 10238, 'Summons Constructs' || char(13) || '
5% increased Attack Damage per 450 Armour' || char(13) || '
Buffs nearby Constructs with 100% increased Damage and 30% Attack and Cast Speed', 2),
(74, 5042, 10239, 'Summons Constructs' || char(13) || '
8% increased Attack Damage per 450 Armour' || char(13) || '
Buffs nearby Constructs with 100% increased Damage and 30% Attack and Cast Speed', 2),
(75, 5043, 10214, 'Loses a percentage of Life when casting spells' || char(13) || '
Upon reaching 20% Life, stops casting to recover to Full Life', 1),
(76, 5044, 10215, 'Loses a percentage of Life when casting spells' || char(13) || '
Upon reaching 20% Life, stops casting to recover to Full Life' || char(13) || '
Heals nearby allies for 10% of the Life spent', 1),
(77, 5045, 10216, 'Loses a percentage of Life when casting spells' || char(13) || '
Upon reaching 20% Life, stops casting to recover to Full Life' || char(13) || '
Heals nearby allies for 10% of the Life spent' || char(13) || '
While on Low Life nearby allies gain spell damage', 1),
(78, 5046, 10217, '', 1),
(79, 5047, 10218, '', 1),
(80, 5048, 10219, '', 1),
(81, 5049, 10258, 'Summons Spiders' || char(13) || '
', 4),
(82, 5050, 10259, 'Summons Spiders' || char(13) || '
Ally Spiders deal additional chaos damage with attacks', 4),
(83, 5051, 10260, 'Summons Spiders' || char(13) || '
Ally Spiders deal additional chaos damage with attacks' || char(13) || '
Owner gains 10% increased Effect of Withered', 4),
(84, 5052, 10231, 'Must be linked to be powered', 2),
(85, 5053, 10232, 'Must be Linked to be powered' || char(13) || '
50% effects of Links on this', 2),
(86, 5054, 10233, 'Must be Linked to be powered' || char(13) || '
100% effects of Links on this', 2),
(87, 5055, 10252, 'High Life', 3),
(88, 5056, 10253, 'Higher Life' || char(13) || '
Releases a caustic cloud when hit', 3),
(89, 5057, 10254, 'Highest Life' || char(13) || '
Releases a caustic cloud when hit' || char(13) || '
Taunts on Hit', 3),
(90, 5058, 10196, 'Casts channelled Lightning Beam' || char(13) || '
Cannot inflict Shock', 0),
(91, 5059, 10197, 'Casts channelled Lightning Beam' || char(13) || '
Cannot inflict Shock' || char(13) || '
Removes Shock on hit to trigger Lightning Explosion', 0),
(92, 5060, 10198, 'Casts channelled Lightning Beam' || char(13) || '
Cannot inflict Shock' || char(13) || '
Removes Shock on hit to trigger Lightning Explosion' || char(13) || '
Casts Purity of Lightning', 0),
(93, 5061, 10261, '5% increased Attack Damage per 450 Evasion Rating', 4),
(94, 5062, 10262, '5% increased Attack Damage per 450 Evasion Rating' || char(13) || '
5% chance to grant a Frenzy Charge to nearby Allies on Hit', 4),
(95, 5063, 10263, '5% increased Attack Damage per 450 Evasion Rating' || char(13) || '
5% chance to grant a Frenzy Charge to nearby Allies on Hit' || char(13) || '
Casts Haste', 4),
(96, 5064, 10264, 'Buffs Owner with Rongokurai''s Boon', 4),
(97, 5065, 10265, 'Buffs Owner with Rongokurai''s Boon' || char(13) || '
Owner gains 3% Physical Damage Reduction', 4),
(98, 5066, 10266, 'Buffs Owner with Rongokurai''s Boon' || char(13) || '
Owner gains 5% Physical Damage Reduction' || char(13) || '
Casts Determination', 4),
(99, 5067, 10225, 'Fires Lightning Beams' || char(13) || '
Fires Chilling Blasts' || char(13) || '
Rain of Boulders' || char(13) || '
Slams', 2),
(100, 5068, 10226, 'Fires Lightning Beams' || char(13) || '
Fires Chilling Blasts' || char(13) || '
Rain of Boulders' || char(13) || '
Slams' || char(13) || '
Slam deals 500% more Damage with Ignite', 2),
(101, 5069, 10227, 'Fires Lightning Beams' || char(13) || '
Fires Chilling Blasts' || char(13) || '
Rain of Boulders' || char(13) || '
Slams' || char(13) || '
Slam deals 500% more Damage with Ignite' || char(13) || '
Lightning Beam has 20% Maximum Effect of Shock', 2),
(102, 5070, 10267, 'Cyclones' || char(13) || '
Uses Molten Fissure', 5),
(103, 5071, 10268, 'Cyclones' || char(13) || '
Uses Molten Fissure' || char(13) || '
Culling Strike', 5),
(104, 5072, 10269, 'Cyclones' || char(13) || '
Uses Molten Fissure' || char(13) || '
Culling Strike' || char(13) || '
Nearby allies have Onslaught', 5),
(105, 5073, 10529, 'Fires Astral projectiles', 3),
(106, 5074, 10530, 'Fires Astral projectiles' || char(13) || '
Creates Astral Chain Mines', 3),
(107, 5075, 10531, 'Fires Astral projectiles' || char(13) || '
Creates Astral Chain Mines' || char(13) || '
Curses Enemies with Temporal Chains on Hit', 3),
(108, 5076, 10503, 'Creates Blood Pustules' || char(13) || '
Summons Ichorous Pests', 5),
(109, 5077, 10504, 'Creates Blood Pustules' || char(13) || '
Summons Ichorous Pests' || char(13) || '
Creates Caustic Ground', 5),
(110, 5078, 10505, 'Creates Blood Pustules' || char(13) || '
Summons Ichorous Pests' || char(13) || '
Creates Caustic Ground' || char(13) || '
Casts Malevolence', 5),
(111, 5079, 10560, 'Ground Slams', 5),
(112, 5080, 10561, 'Ground Slams' || char(13) || '
Uses Ground Slam Nova', 5),
(113, 5081, 10562, 'Ground Slams' || char(13) || '
Uses Ground Slam Nova' || char(13) || '
Casts Anger', 5);
