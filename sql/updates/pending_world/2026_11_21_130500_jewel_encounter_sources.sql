-- Keep unverified retail rates unset. Server owners can opt in with encounter_jewel_drop_overrides.
UPDATE unique_items SET encounter_only = 1
WHERE (name = 'Forbidden Flame' AND base_type = 'Metadata/Items/Jewels/JewelStr')
   OR (name = 'Forbidden Flesh' AND base_type = 'Metadata/Items/Jewels/JewelInt');

INSERT INTO drop_boss_items (boss_id, item_id, item_kind, chance, confidence, source, notes)
VALUES
    ('Metadata/Monsters/AtlasMemory/BenevolenceBoss', 'Bound By Destiny', 'unique', NULL, 'unknown',
     'PoEDB Incarnation of Dread', 'Native encounter source; no verified exact rate.'),
    ('Metadata/Monsters/AtlasMemory/BenevolenceBossUBER', 'Bound By Destiny', 'unique', NULL, 'unknown',
     'PoEDB Incarnation of Dread', 'Native encounter source; no verified exact rate.'),
    ('Metadata/Monsters/AtlasMemory/AngerBoss', 'Bound By Destiny', 'unique', NULL, 'unknown',
     'PoEDB Incarnation of Fear', 'Native encounter source; no verified exact rate.'),
    ('Metadata/Monsters/AtlasMemory/AngerBossUBER', 'Bound By Destiny', 'unique', NULL, 'unknown',
     'PoEDB Incarnation of Fear', 'Native encounter source; no verified exact rate.'),
    ('Metadata/Monsters/AtlasMemory/IgnoranceBoss/IgnoranceBoss', 'Bound By Destiny', 'unique', NULL, 'unknown',
     'PoEDB Incarnation of Neglect', 'Native encounter source; no verified exact rate.'),
    ('Metadata/Monsters/AtlasMemory/IgnoranceBoss/IgnoranceBossUBER', 'Bound By Destiny', 'unique', NULL, 'unknown',
     'PoEDB Incarnation of Neglect', 'Native encounter source; no verified exact rate.')
ON CONFLICT(boss_id, item_id) DO NOTHING;
