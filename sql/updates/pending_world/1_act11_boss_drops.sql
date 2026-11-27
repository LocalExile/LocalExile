-- Mark Secrets of the Atlas Incarnation boss-exclusive uniques (plus Maven's Doppelgänger Guise) as encounter_only so
-- they never leak into the core drop pool.
UPDATE unique_items
SET encounter_only = 1
WHERE name IN (
    'Doppelgänger Guise',
    'Betrayal''s Sting',
    'Legacy of the Rose',
    'Servant of Decay',
    'The Unseen Hue',
    'Coiling Whisper',
    'Enmity''s Embrace',
    'Starcaller',
    'Whispers of Infinity',
    'Bonemeld',
    'The Hallowed Monarch',
    'Wine of the Prophet',
    'The Dark Monarch'
);

-- Endgame boss fragments, crests, memory echoes, Uber pinnacle fragments, boss-only currency, and Incarnation unique pools.
-- Unique pool rows keep chance = NULL where GGG does not publish per-item drop rates (drop-data-schema.md);
-- ItemDrops.BossDrops selects one guaranteed unique from a boss's NULL-chance unique pool while independent
-- fragment/currency rows roll by their explicit chance.

INSERT INTO drop_boss_items (boss_id, item_id, item_kind, chance, confidence, source, notes) VALUES
-- Shaper Guardians -> Shaper Fragments (guaranteed)
('Metadata/Monsters/AtlasBosses/PhoenixBoss', 'Metadata/Items/MapFragments/CurrencyShaperFragment1', 'other', 1.0, 'verified', 'PoE 3.29 Guardian guaranteed fragment', 'Fragment of the Phoenix'),
('Metadata/Monsters/AtlasBosses/MinotaurBoss', 'Metadata/Items/MapFragments/CurrencyShaperFragment2', 'other', 1.0, 'verified', 'PoE 3.29 Guardian guaranteed fragment', 'Fragment of the Minotaur'),
('Metadata/Monsters/AtlasBosses/ChimeraBoss', 'Metadata/Items/MapFragments/CurrencyShaperFragment3', 'other', 1.0, 'verified', 'PoE 3.29 Guardian guaranteed fragment', 'Fragment of the Chimera'),
('Metadata/Monsters/AtlasBosses/HydraBoss', 'Metadata/Items/MapFragments/CurrencyShaperFragment4', 'other', 1.0, 'verified', 'PoE 3.29 Guardian guaranteed fragment', 'Fragment of the Hydra'),

-- Elder Guardians -> Elder Fragments (guaranteed)
('Metadata/Monsters/AtlasBosses/TheElder/ElderGuardian1', 'Metadata/Items/MapFragments/CurrencyElderFragment1', 'other', 1.0, 'verified', 'PoE 3.29 Guardian guaranteed fragment', 'Fragment of Enslavement'),
('Metadata/Monsters/AtlasBosses/TheElder/ElderGuardian2', 'Metadata/Items/MapFragments/CurrencyElderFragment2', 'other', 1.0, 'verified', 'PoE 3.29 Guardian guaranteed fragment', 'Fragment of Eradication'),
('Metadata/Monsters/AtlasBosses/TheElder/ElderGuardian3', 'Metadata/Items/MapFragments/CurrencyElderFragment3', 'other', 1.0, 'verified', 'PoE 3.29 Guardian guaranteed fragment', 'Fragment of Constriction'),
('Metadata/Monsters/AtlasBosses/TheElder/ElderGuardian4_', 'Metadata/Items/MapFragments/CurrencyElderFragment4', 'other', 1.0, 'verified', 'PoE 3.29 Guardian guaranteed fragment', 'Fragment of Purification'),

-- Shaper & Elder -> Uber Elder Fragments + Orb of Dominance + Uber Elder unique pool
('Metadata/Monsters/AtlasBosses/TheShaperBoss', 'Metadata/Items/MapFragments/CurrencyUberElderFragment3', 'other', 0.50, 'verified', 'PoE 3.29 Shaper Uber Elder fragment drop', 'Fragment of Shape'),
('Metadata/Monsters/AtlasBosses/TheShaperBoss', 'Metadata/Items/MapFragments/CurrencyUberElderFragment4', 'other', 0.50, 'verified', 'PoE 3.29 Shaper Uber Elder fragment drop', 'Fragment of Knowledge'),
('Metadata/Monsters/AtlasBosses/TheShaperBoss', 'Metadata/Items/Currency/CurrencyUpgradeInfluenceMod', 'currency', 0.10, 'estimated', 'PoE 3.29 Shaper boss currency', 'Orb of Dominance'),
('Metadata/Monsters/AtlasBosses/TheElder/TheElder', 'Metadata/Items/MapFragments/CurrencyUberElderFragment1', 'other', 0.50, 'verified', 'PoE 3.29 Elder Uber Elder fragment drop', 'Fragment of Terror'),
('Metadata/Monsters/AtlasBosses/TheElder/TheElder', 'Metadata/Items/MapFragments/CurrencyUberElderFragment2', 'other', 0.50, 'verified', 'PoE 3.29 Elder Uber Elder fragment drop', 'Fragment of Emptiness'),
('Metadata/Monsters/AtlasBosses/TheElder/TheElderUber', 'Metadata/Items/Currency/CurrencyUpgradeInfluenceMod', 'currency', 0.20, 'estimated', 'PoE 3.29 Uber Elder boss currency', 'Orb of Dominance'),
('Metadata/Monsters/AtlasBosses/TheElder/TheElderUber', 'Call of the Void', 'unique', NULL, 'unknown', 'PoEDB The Elder / Uber Elder unique pool', NULL),
('Metadata/Monsters/AtlasBosses/TheElder/TheElderUber', 'Disintegrator', 'unique', NULL, 'unknown', 'PoEDB The Elder / Uber Elder unique pool', NULL),
('Metadata/Monsters/AtlasBosses/TheElder/TheElderUber', 'Impresence', 'unique', NULL, 'unknown', 'PoEDB The Elder / Uber Elder unique pool', NULL),
('Metadata/Monsters/AtlasBosses/TheElder/TheElderUber', 'Indigon', 'unique', NULL, 'unknown', 'PoEDB The Elder / Uber Elder unique pool', NULL),
('Metadata/Monsters/AtlasBosses/TheElder/TheElderUber', 'Mark of the Elder', 'unique', NULL, 'unknown', 'PoEDB The Elder / Uber Elder unique pool', NULL),
('Metadata/Monsters/AtlasBosses/TheElder/TheElderUber', 'Mark of the Shaper', 'unique', NULL, 'unknown', 'PoEDB The Elder / Uber Elder unique pool', NULL),
('Metadata/Monsters/AtlasBosses/TheElder/TheElderUber', 'Sublime Vision', 'unique', NULL, 'unknown', 'PoEDB Uber Elder unique pool', NULL),
('Metadata/Monsters/AtlasBosses/TheElder/TheElderUber', 'The Eternity Shroud', 'unique', NULL, 'unknown', 'PoEDB The Elder / Uber Elder unique pool', NULL),
('Metadata/Monsters/AtlasBosses/TheElder/TheElderUber', 'Voidfletcher', 'unique', NULL, 'unknown', 'PoEDB The Elder / Uber Elder unique pool', NULL),
('Metadata/Monsters/AtlasBosses/TheElder/TheElderUber', 'Voidforge', 'unique', NULL, 'unknown', 'PoEDB The Elder / Uber Elder unique pool', NULL),

-- Conquerors -> Sirus Crests (guaranteed) + Orb of Dominance
('Metadata/Monsters/AtlasExiles/AtlasExile1', 'Metadata/Items/MapFragments/CurrencySirusFragment2', 'other', 1.0, 'verified', 'PoE 3.29 Conqueror guaranteed crest', 'Baran''s Crest'),
('Metadata/Monsters/AtlasExiles/AtlasExile1Apparition', 'Metadata/Items/MapFragments/CurrencySirusFragment2', 'other', 1.0, 'verified', 'PoE 3.29 Conqueror guaranteed crest', 'Baran''s Crest'),
('Metadata/Monsters/AtlasExiles/AtlasExile1ApparitionUber', 'Metadata/Items/MapFragments/CurrencySirusFragment2', 'other', 1.0, 'verified', 'PoE 3.29 Conqueror guaranteed crest', 'Baran''s Crest'),
('Metadata/Monsters/AtlasExiles/AtlasExile1ApparitionUber', 'Metadata/Items/Currency/CurrencyUpgradeInfluenceMod', 'currency', 0.08, 'estimated', 'PoE 3.29 Conqueror boss currency', 'Orb of Dominance'),
('Metadata/Monsters/AtlasExiles/AtlasExile2_', 'Metadata/Items/MapFragments/CurrencySirusFragment4', 'other', 1.0, 'verified', 'PoE 3.29 Conqueror guaranteed crest', 'Veritania''s Crest'),
('Metadata/Monsters/AtlasExiles/AtlasExile2Apparition', 'Metadata/Items/MapFragments/CurrencySirusFragment4', 'other', 1.0, 'verified', 'PoE 3.29 Conqueror guaranteed crest', 'Veritania''s Crest'),
('Metadata/Monsters/AtlasExiles/AtlasExile2ApparitionUber', 'Metadata/Items/MapFragments/CurrencySirusFragment4', 'other', 1.0, 'verified', 'PoE 3.29 Conqueror guaranteed crest', 'Veritania''s Crest'),
('Metadata/Monsters/AtlasExiles/AtlasExile2ApparitionUber', 'Metadata/Items/Currency/CurrencyUpgradeInfluenceMod', 'currency', 0.08, 'estimated', 'PoE 3.29 Conqueror boss currency', 'Orb of Dominance'),
('Metadata/Monsters/AtlasExiles/AtlasExile3', 'Metadata/Items/MapFragments/CurrencySirusFragment1', 'other', 1.0, 'verified', 'PoE 3.29 Conqueror guaranteed crest', 'Al-Hezmin''s Crest'),
('Metadata/Monsters/AtlasExiles/AtlasExile3Apparition', 'Metadata/Items/MapFragments/CurrencySirusFragment1', 'other', 1.0, 'verified', 'PoE 3.29 Conqueror guaranteed crest', 'Al-Hezmin''s Crest'),
('Metadata/Monsters/AtlasExiles/AtlasExile3ApparitionUber', 'Metadata/Items/MapFragments/CurrencySirusFragment1', 'other', 1.0, 'verified', 'PoE 3.29 Conqueror guaranteed crest', 'Al-Hezmin''s Crest'),
('Metadata/Monsters/AtlasExiles/AtlasExile3ApparitionUber', 'Metadata/Items/Currency/CurrencyUpgradeInfluenceMod', 'currency', 0.08, 'estimated', 'PoE 3.29 Conqueror boss currency', 'Orb of Dominance'),
('Metadata/Monsters/AtlasExiles/AtlasExile4', 'Metadata/Items/MapFragments/CurrencySirusFragment3', 'other', 1.0, 'verified', 'PoE 3.29 Conqueror guaranteed crest', 'Drox''s Crest'),
('Metadata/Monsters/AtlasExiles/AtlasExile4Apparition', 'Metadata/Items/MapFragments/CurrencySirusFragment3', 'other', 1.0, 'verified', 'PoE 3.29 Conqueror guaranteed crest', 'Drox''s Crest'),
('Metadata/Monsters/AtlasExiles/AtlasExile4ApparitionUber', 'Metadata/Items/MapFragments/CurrencySirusFragment3', 'other', 1.0, 'verified', 'PoE 3.29 Conqueror guaranteed crest', 'Drox''s Crest'),
('Metadata/Monsters/AtlasExiles/AtlasExile4ApparitionUber', 'Metadata/Items/Currency/CurrencyUpgradeInfluenceMod', 'currency', 0.08, 'estimated', 'PoE 3.29 Conqueror boss currency', 'Orb of Dominance'),

-- Sirus -> Awakener's Orb & Orb of Dominance
('Metadata/Monsters/AtlasExiles/AtlasExile5', 'Metadata/Items/AtlasExiles/ApplyInfluence', 'currency', 0.25, 'estimated', 'PoEDB Sirus / community drop estimates', 'Awakener''s Orb'),
('Metadata/Monsters/AtlasExiles/AtlasExile5', 'Metadata/Items/Currency/CurrencyUpgradeInfluenceMod', 'currency', 0.15, 'estimated', 'PoE 3.29 Sirus boss currency', 'Orb of Dominance'),

-- The Maven & Nucleus of the Maven -> Orb of Dominance, Orb of Conflict, and Maven unique pool on Nucleus
('Metadata/Monsters/MavenBoss/TheMaven', 'Metadata/Items/Currency/CurrencyUpgradeInfluenceMod', 'currency', 0.25, 'estimated', 'Community 400-run sample (boss-drops.md)', 'Orb of Dominance'),
('Metadata/Monsters/MavenBoss/TheMaven', 'Metadata/Items/Currency/CurrencyConflictOrb', 'currency', 0.20, 'estimated', 'PoE 3.29 Maven boss currency', 'Orb of Conflict'),
('Metadata/Monsters/MavenBoss/MavenBrainBoss', 'Metadata/Items/Currency/CurrencyUpgradeInfluenceMod', 'currency', 0.25, 'estimated', 'Community 400-run sample (boss-drops.md)', 'Orb of Dominance'),
('Metadata/Monsters/MavenBoss/MavenBrainBoss', 'Metadata/Items/Currency/CurrencyConflictOrb', 'currency', 0.20, 'estimated', 'PoE 3.29 Maven boss currency', 'Orb of Conflict'),
('Metadata/Monsters/MavenBoss/MavenBrainBoss', 'Arn''s Anguish', 'unique', NULL, 'unknown', 'PoEDB The Maven unique pool (final killable Nucleus)', NULL),
('Metadata/Monsters/MavenBoss/MavenBrainBoss', 'Doppelgänger Guise', 'unique', NULL, 'unknown', 'PoEDB The Maven unique pool (final killable Nucleus)', NULL),
('Metadata/Monsters/MavenBoss/MavenBrainBoss', 'Echoforge', 'unique', NULL, 'unknown', 'PoEDB The Maven unique pool (final killable Nucleus)', NULL),
('Metadata/Monsters/MavenBoss/MavenBrainBoss', 'Grace of the Goddess', 'unique', NULL, 'unknown', 'PoEDB The Maven unique pool (final killable Nucleus)', NULL),
('Metadata/Monsters/MavenBoss/MavenBrainBoss', 'Graven''s Secret', 'unique', NULL, 'unknown', 'PoEDB The Maven unique pool (final killable Nucleus)', NULL),
('Metadata/Monsters/MavenBoss/MavenBrainBoss', 'Impossible Escape', 'unique', NULL, 'unknown', 'PoEDB The Maven unique pool (final killable Nucleus)', NULL),
('Metadata/Monsters/MavenBoss/MavenBrainBoss', 'Legacy of Fury', 'unique', NULL, 'unknown', 'PoEDB The Maven unique pool (final killable Nucleus)', NULL),
('Metadata/Monsters/MavenBoss/MavenBrainBoss', 'Olesya''s Delight', 'unique', NULL, 'unknown', 'PoEDB The Maven unique pool (final killable Nucleus)', NULL),
('Metadata/Monsters/MavenBoss/MavenBrainBoss', 'Progenesis', 'unique', NULL, 'unknown', 'PoEDB The Maven unique pool (final killable Nucleus)', NULL),
('Metadata/Monsters/MavenBoss/MavenBrainBoss', 'Viridi''s Veil', 'unique', NULL, 'unknown', 'PoEDB The Maven unique pool (final killable Nucleus)', NULL),

-- Eldritch Pinnacles & Minibosses -> Orb of Conflict, Eldritch Chaos/Exalted/Annulment, Eldritch Embers/Ichors
('Metadata/Monsters/AtlasInvaders/CleansingMonsters/CleansingBoss', 'Metadata/Items/Currency/CurrencyConflictOrb', 'currency', 0.20, 'estimated', 'PoE 3.29 Searing Exarch boss currency', 'Orb of Conflict'),
('Metadata/Monsters/AtlasInvaders/CleansingMonsters/CleansingBoss', 'Metadata/Items/Currency/CurrencyEldritchRerollRare', 'currency', 0.25, 'estimated', 'PoE 3.29 Searing Exarch Eldritch currency', 'Eldritch Chaos Orb'),
('Metadata/Monsters/AtlasInvaders/CleansingMonsters/CleansingBoss', 'Metadata/Items/Currency/CurrencyEldritchAddModToRare', 'currency', 0.25, 'estimated', 'PoE 3.29 Searing Exarch Eldritch currency', 'Eldritch Exalted Orb'),
('Metadata/Monsters/AtlasInvaders/CleansingMonsters/CleansingBoss', 'Metadata/Items/Currency/CurrencyEldritchRemoveMod', 'currency', 0.25, 'estimated', 'PoE 3.29 Searing Exarch Eldritch currency', 'Eldritch Orb of Annulment'),
('Metadata/Monsters/AtlasInvaders/CleansingMonsters/CleansingBoss', 'Metadata/Items/Currency/CurrencyEldritchEmber3', 'currency', 0.60, 'estimated', 'PoE 3.29 Searing Exarch Eldritch Ember', 'Grand Eldritch Ember'),
('Metadata/Monsters/AtlasInvaders/CleansingMonsters/CleansingBoss', 'Metadata/Items/Currency/CurrencyEldritchEmber4', 'currency', 0.35, 'estimated', 'PoE 3.29 Searing Exarch Eldritch Ember', 'Exceptional Eldritch Ember'),

('Metadata/Monsters/AtlasInvaders/ConsumeMonsters/ConsumeBoss', 'Metadata/Items/Currency/CurrencyConflictOrb', 'currency', 0.20, 'estimated', 'PoE 3.29 Eater of Worlds boss currency', 'Orb of Conflict'),
('Metadata/Monsters/AtlasInvaders/ConsumeMonsters/ConsumeBoss', 'Metadata/Items/Currency/CurrencyEldritchRerollRare', 'currency', 0.25, 'estimated', 'PoE 3.29 Eater of Worlds Eldritch currency', 'Eldritch Chaos Orb'),
('Metadata/Monsters/AtlasInvaders/ConsumeMonsters/ConsumeBoss', 'Metadata/Items/Currency/CurrencyEldritchAddModToRare', 'currency', 0.25, 'estimated', 'PoE 3.29 Eater of Worlds Eldritch currency', 'Eldritch Exalted Orb'),
('Metadata/Monsters/AtlasInvaders/ConsumeMonsters/ConsumeBoss', 'Metadata/Items/Currency/CurrencyEldritchRemoveMod', 'currency', 0.25, 'estimated', 'PoE 3.29 Eater of Worlds Eldritch currency', 'Eldritch Orb of Annulment'),
('Metadata/Monsters/AtlasInvaders/ConsumeMonsters/ConsumeBoss', 'Metadata/Items/Currency/CurrencyEldritchIchor3', 'currency', 0.60, 'estimated', 'PoE 3.29 Eater of Worlds Eldritch Ichor', 'Grand Eldritch Ichor'),
('Metadata/Monsters/AtlasInvaders/ConsumeMonsters/ConsumeBoss', 'Metadata/Items/Currency/CurrencyEldritchIchor4', 'currency', 0.35, 'estimated', 'PoE 3.29 Eater of Worlds Eldritch Ichor', 'Exceptional Eldritch Ichor'),

('Metadata/Monsters/AtlasInvaders/DoomMonsters/DoomBoss_', 'Metadata/Items/Currency/CurrencyEldritchIchor1', 'currency', 0.80, 'estimated', 'PoE 3.29 Infinite Hunger Eldritch currency', 'Lesser Eldritch Ichor'),
('Metadata/Monsters/AtlasInvaders/DoomMonsters/DoomBoss_', 'Metadata/Items/Currency/CurrencyEldritchIchor2', 'currency', 0.50, 'estimated', 'PoE 3.29 Infinite Hunger Eldritch currency', 'Greater Eldritch Ichor'),
('Metadata/Monsters/AtlasInvaders/DoomMonsters/DoomBoss_', 'Metadata/Items/Currency/CurrencyEldritchIchor3', 'currency', 0.25, 'estimated', 'PoE 3.29 Infinite Hunger Eldritch currency', 'Grand Eldritch Ichor'),
('Metadata/Monsters/AtlasInvaders/DoomMonsters/DoomBoss_', 'Metadata/Items/Currency/CurrencyEldritchRerollRare', 'currency', 0.10, 'estimated', 'PoE 3.29 Infinite Hunger Eldritch currency', 'Eldritch Chaos Orb'),
('Metadata/Monsters/AtlasInvaders/DoomMonsters/DoomBoss_', 'Metadata/Items/Currency/CurrencyEldritchAddModToRare', 'currency', 0.10, 'estimated', 'PoE 3.29 Infinite Hunger Eldritch currency', 'Eldritch Exalted Orb'),
('Metadata/Monsters/AtlasInvaders/DoomMonsters/DoomBoss_', 'Metadata/Items/Currency/CurrencyEldritchRemoveMod', 'currency', 0.10, 'estimated', 'PoE 3.29 Infinite Hunger Eldritch currency', 'Eldritch Orb of Annulment'),

('Metadata/Monsters/AtlasInvaders/BlackStarMonsters/BlackStarBoss', 'Metadata/Items/Currency/CurrencyEldritchEmber1', 'currency', 0.80, 'estimated', 'PoE 3.29 Black Star Eldritch currency', 'Lesser Eldritch Ember'),
('Metadata/Monsters/AtlasInvaders/BlackStarMonsters/BlackStarBoss', 'Metadata/Items/Currency/CurrencyEldritchEmber2', 'currency', 0.50, 'estimated', 'PoE 3.29 Black Star Eldritch currency', 'Greater Eldritch Ember'),
('Metadata/Monsters/AtlasInvaders/BlackStarMonsters/BlackStarBoss', 'Metadata/Items/Currency/CurrencyEldritchEmber3', 'currency', 0.25, 'estimated', 'PoE 3.29 Black Star Eldritch currency', 'Grand Eldritch Ember'),
('Metadata/Monsters/AtlasInvaders/BlackStarMonsters/BlackStarBoss', 'Metadata/Items/Currency/CurrencyEldritchRerollRare', 'currency', 0.10, 'estimated', 'PoE 3.29 Black Star Eldritch currency', 'Eldritch Chaos Orb'),
('Metadata/Monsters/AtlasInvaders/BlackStarMonsters/BlackStarBoss', 'Metadata/Items/Currency/CurrencyEldritchAddModToRare', 'currency', 0.10, 'estimated', 'PoE 3.29 Black Star Eldritch currency', 'Eldritch Exalted Orb'),
('Metadata/Monsters/AtlasInvaders/BlackStarMonsters/BlackStarBoss', 'Metadata/Items/Currency/CurrencyEldritchRemoveMod', 'currency', 0.10, 'estimated', 'PoE 3.29 Black Star Eldritch currency', 'Eldritch Orb of Annulment'),

-- Memory Thread Minibosses -> Guaranteed Memory Boss Echoes
('Metadata/Monsters/AtlasMemory/FragmentOfAnger', 'Metadata/Items/MapFragments/AtlasMemory/CurrencyFearKey', 'other', 1.0, 'verified', 'PoE 3.26-3.29 Memory Thread miniboss', 'Echo of Trauma'),
('Metadata/Monsters/AtlasMemory/FragmentOfIgnorance/FragmentOfIgnorance__', 'Metadata/Items/MapFragments/AtlasMemory/CurrencyIgnoranceKey', 'other', 1.0, 'verified', 'PoE 3.26-3.29 Memory Thread miniboss', 'Echo of Loneliness'),
('Metadata/Monsters/AtlasMemory/FragmentOfBenevolence', 'Metadata/Items/MapFragments/AtlasMemory/CurrencyBenevolenceKey', 'other', 1.0, 'verified', 'PoE 3.26-3.29 Memory Thread miniboss', 'Echo of Reverence'),

-- Originator / Memory Pinnacles (Incarnation of Neglect / Fear / Dread + Uber)
('Metadata/Monsters/AtlasMemory/IgnoranceBoss/IgnoranceBoss', 'Metadata/Items/Currency/CurrencyConflictOrb', 'currency', 0.15, 'estimated', 'PoE 3.26-3.29 Incarnation boss currency', 'Orb of Conflict'),
('Metadata/Monsters/AtlasMemory/IgnoranceBoss/IgnoranceBoss', 'Betrayal''s Sting', 'unique', NULL, 'unknown', 'PoEDB Incarnation of Neglect unique pool', NULL),
('Metadata/Monsters/AtlasMemory/IgnoranceBoss/IgnoranceBoss', 'Venarius'' Astrolabe', 'unique', NULL, 'unknown', 'PoEDB Incarnation of Neglect unique pool', NULL),
('Metadata/Monsters/AtlasMemory/IgnoranceBoss/IgnoranceBoss', 'Legacy of the Rose', 'unique', NULL, 'unknown', 'PoEDB Incarnation of Neglect unique pool', NULL),
('Metadata/Monsters/AtlasMemory/IgnoranceBoss/IgnoranceBossUBER', 'Metadata/Items/Currency/CurrencyConflictOrb', 'currency', 0.25, 'estimated', 'PoE 3.26-3.29 Uber Incarnation boss currency', 'Orb of Conflict'),
('Metadata/Monsters/AtlasMemory/IgnoranceBoss/IgnoranceBossUBER', 'Betrayal''s Sting', 'unique', NULL, 'unknown', 'PoEDB Uber Incarnation of Neglect unique pool', NULL),
('Metadata/Monsters/AtlasMemory/IgnoranceBoss/IgnoranceBossUBER', 'Venarius'' Astrolabe', 'unique', NULL, 'unknown', 'PoEDB Uber Incarnation of Neglect unique pool', NULL),
('Metadata/Monsters/AtlasMemory/IgnoranceBoss/IgnoranceBossUBER', 'Legacy of the Rose', 'unique', NULL, 'unknown', 'PoEDB Uber Incarnation of Neglect unique pool', NULL),

('Metadata/Monsters/AtlasMemory/AngerBoss', 'Metadata/Items/Currency/CurrencyConflictOrb', 'currency', 0.15, 'estimated', 'PoE 3.26-3.29 Incarnation boss currency', 'Orb of Conflict'),
('Metadata/Monsters/AtlasMemory/AngerBoss', 'Servant of Decay', 'unique', NULL, 'unknown', 'PoEDB Incarnation of Fear unique pool', NULL),
('Metadata/Monsters/AtlasMemory/AngerBoss', 'The Unseen Hue', 'unique', NULL, 'unknown', 'PoEDB Incarnation of Fear unique pool', NULL),
('Metadata/Monsters/AtlasMemory/AngerBoss', 'Coiling Whisper', 'unique', NULL, 'unknown', 'PoEDB Incarnation of Fear unique pool', NULL),
('Metadata/Monsters/AtlasMemory/AngerBoss', 'Enmity''s Embrace', 'unique', NULL, 'unknown', 'PoEDB Incarnation of Fear unique pool', NULL),
('Metadata/Monsters/AtlasMemory/AngerBoss', 'Starcaller', 'unique', NULL, 'unknown', 'PoEDB Incarnation of Fear unique pool', NULL),
('Metadata/Monsters/AtlasMemory/AngerBossUBER', 'Metadata/Items/Currency/CurrencyConflictOrb', 'currency', 0.25, 'estimated', 'PoE 3.26-3.29 Uber Incarnation boss currency', 'Orb of Conflict'),
('Metadata/Monsters/AtlasMemory/AngerBossUBER', 'Servant of Decay', 'unique', NULL, 'unknown', 'PoEDB Uber Incarnation of Fear unique pool', NULL),
('Metadata/Monsters/AtlasMemory/AngerBossUBER', 'The Unseen Hue', 'unique', NULL, 'unknown', 'PoEDB Uber Incarnation of Fear unique pool', NULL),
('Metadata/Monsters/AtlasMemory/AngerBossUBER', 'Coiling Whisper', 'unique', NULL, 'unknown', 'PoEDB Uber Incarnation of Fear unique pool', NULL),
('Metadata/Monsters/AtlasMemory/AngerBossUBER', 'Enmity''s Embrace', 'unique', NULL, 'unknown', 'PoEDB Uber Incarnation of Fear unique pool', NULL),
('Metadata/Monsters/AtlasMemory/AngerBossUBER', 'Starcaller', 'unique', NULL, 'unknown', 'PoEDB Uber Incarnation of Fear unique pool', NULL),
('Metadata/Monsters/AtlasMemory/AngerBossUBER', 'Whispers of Infinity', 'unique', NULL, 'unknown', 'PoEDB Uber Incarnation of Fear unique pool', NULL),

('Metadata/Monsters/AtlasMemory/BenevolenceBoss', 'Metadata/Items/Currency/CurrencyConflictOrb', 'currency', 0.15, 'estimated', 'PoE 3.26-3.29 Incarnation boss currency', 'Orb of Conflict'),
('Metadata/Monsters/AtlasMemory/BenevolenceBoss', 'Bonemeld', 'unique', NULL, 'unknown', 'PoEDB Incarnation of Dread unique pool', NULL),
('Metadata/Monsters/AtlasMemory/BenevolenceBoss', 'The Hallowed Monarch', 'unique', NULL, 'unknown', 'PoEDB Incarnation of Dread unique pool', NULL),
('Metadata/Monsters/AtlasMemory/BenevolenceBoss', 'Wine of the Prophet', 'unique', NULL, 'unknown', 'PoEDB Incarnation of Dread unique pool', NULL),
('Metadata/Monsters/AtlasMemory/BenevolenceBoss', 'The Dark Monarch', 'unique', NULL, 'unknown', 'PoEDB Incarnation of Dread unique pool', NULL),
('Metadata/Monsters/AtlasMemory/BenevolenceBoss', 'Bound By Destiny', 'unique', NULL, 'unknown', 'PoEDB Incarnation of Dread unique pool', NULL),
('Metadata/Monsters/AtlasMemory/BenevolenceBossUBER', 'Metadata/Items/Currency/CurrencyConflictOrb', 'currency', 0.25, 'estimated', 'PoE 3.26-3.29 Uber Incarnation boss currency', 'Orb of Conflict'),
('Metadata/Monsters/AtlasMemory/BenevolenceBossUBER', 'Bonemeld', 'unique', NULL, 'unknown', 'PoEDB Uber Incarnation of Dread unique pool', NULL),
('Metadata/Monsters/AtlasMemory/BenevolenceBossUBER', 'The Hallowed Monarch', 'unique', NULL, 'unknown', 'PoEDB Uber Incarnation of Dread unique pool', NULL),
('Metadata/Monsters/AtlasMemory/BenevolenceBossUBER', 'Wine of the Prophet', 'unique', NULL, 'unknown', 'PoEDB Uber Incarnation of Dread unique pool', NULL),
('Metadata/Monsters/AtlasMemory/BenevolenceBossUBER', 'The Dark Monarch', 'unique', NULL, 'unknown', 'PoEDB Uber Incarnation of Dread unique pool', NULL),
('Metadata/Monsters/AtlasMemory/BenevolenceBossUBER', 'Bound By Destiny', 'unique', NULL, 'unknown', 'PoEDB Uber Incarnation of Dread unique pool', NULL),

-- T17 / Nightmare Map Bosses -> Uber Pinnacle Fragments
('Metadata/Monsters/UnholyTrio/UnholyTrioMapUber', 'Metadata/Items/MapFragments/CurrencyUberBossKeySirus', 'other', 0.50, 'verified', 'PoE 3.29 Abomination T17 Uber fragment', 'Awakening Fragment'),
('Metadata/Monsters/UnholyTrio/UnholyTrioMapUber', 'Metadata/Items/MapFragments/CurrencyUberBossKeyMaven', 'other', 0.50, 'verified', 'PoE 3.29 Abomination T17 Uber fragment', 'Reality Fragment'),
('Metadata/Monsters/LeagueExpedition/ApparitionBoss/ApparitionExpeditionMap', 'Metadata/Items/MapFragments/CurrencyUberBossKeyShaper', 'other', 0.50, 'verified', 'PoE 3.29 Citadel T17 Uber fragment', 'Cosmic Fragment'),
('Metadata/Monsters/LeagueExpedition/ApparitionBoss/ApparitionExpeditionMap', 'Metadata/Items/MapFragments/CurrencyUberBossKeyCortex', 'other', 0.50, 'verified', 'PoE 3.29 Citadel T17 Uber fragment', 'Synthesising Fragment'),
('Metadata/Monsters/LeagueHeist/Boss/UnbreakableMk1Map', 'Metadata/Items/MapFragments/CurrencyUberBossKeyUberElder', 'other', 0.50, 'verified', 'PoE 3.29 Fortress T17 Uber fragment', 'Decaying Fragment'),
('Metadata/Monsters/LeagueHeist/Boss/UnbreakableMk1Map', 'Metadata/Items/MapFragments/CurrencyUberBossKeyCortex', 'other', 0.50, 'verified', 'PoE 3.29 Fortress T17 Uber fragment', 'Synthesising Fragment'),
('Metadata/Monsters/LeagueSanctum/Boss/Lycia2BossMap', 'Metadata/Items/MapFragments/CurrencyUberBossKeyIgnorance', 'other', 0.34, 'verified', 'PoE 3.29 Sanctuary T17 Uber fragment', 'Lonely Fragment'),
('Metadata/Monsters/LeagueSanctum/Boss/Lycia2BossMap', 'Metadata/Items/MapFragments/CurrencyUberBossKeyAnger', 'other', 0.33, 'verified', 'PoE 3.29 Sanctuary T17 Uber fragment', 'Traumatic Fragment'),
('Metadata/Monsters/LeagueSanctum/Boss/Lycia2BossMap', 'Metadata/Items/MapFragments/CurrencyUberBossKeyBenevolence', 'other', 0.33, 'verified', 'PoE 3.29 Sanctuary T17 Uber fragment', 'Reverent Fragment'),
('Metadata/Monsters/LeagueBetrayal/BetrayalCatarinaMapBoss', 'Metadata/Items/MapFragments/CurrencyUberBossKeyExarch', 'other', 0.50, 'verified', 'PoE 3.29 Ziggurat T17 Uber fragment', 'Blazing Fragment'),
('Metadata/Monsters/LeagueBetrayal/BetrayalCatarinaMapBoss', 'Metadata/Items/MapFragments/CurrencyUberBossKeyEater', 'other', 0.50, 'verified', 'PoE 3.29 Ziggurat T17 Uber fragment', 'Devouring Fragment')
ON CONFLICT (boss_id, item_id) DO UPDATE SET
    item_kind = excluded.item_kind,
    chance = excluded.chance,
    confidence = excluded.confidence,
    source = excluded.source,
    notes = excluded.notes;
