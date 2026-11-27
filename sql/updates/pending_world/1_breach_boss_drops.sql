-- Mark Hive Colony (It That Was Tul & It That Was Esh) boss-exclusive uniques as encounter_only so they never leak into
-- the core monster drop pool. Uul-Netol's Vow is already encounter_only = 1.
UPDATE unique_items
SET encounter_only = 1
WHERE name IN (
    'The Will of Esh',
    'The Will of Tul',
    'The Will of Xoph',
    'The Will of Uul-Netol',
    'The Sundered Will',
    'Hand of the Lords',
    'The Grey Wind'
);

-- Wild Breach Boss: Vruun, Marshal of Xesht (spawns in stabilised Unstable Breaches / guaranteed with Breach Scarab of the Marshal).
INSERT INTO drop_bosses (boss_id, boss_name, area_id, category, enabled) VALUES
('Metadata/Monsters/Breach/WildBreachBoss', 'Vruun, Marshal of Xesht', NULL, 'map', 1)
ON CONFLICT(boss_id) DO NOTHING;

-- Hive Colony dual bosses (It That Was Tul & It That Was Esh) and Wild Breach Boss (Vruun, Marshal of Xesht) retail drop
-- tables (PoE 3.28-3.29 poewiki community samples: Tul/Esh n=330, Vruun n=471).
-- For Tul and Esh, the Breach module reads these rows on dual-boss completion: 'unique' rows are shares of the guaranteed
-- exclusive unique (summing to 1.00) and other rows roll independently (same pattern as Oshabi).
-- For Vruun, the five 'other' Breach ring rows sum to 1.00 so ItemDrops.BossDrops selects one guaranteed ring base per kill
-- (upgraded to rare by BreachBosses) while Hivebrain Gland, support gems and card roll independently.
INSERT INTO drop_boss_items (boss_id, item_id, item_kind, chance, confidence, source, notes) VALUES
-- It That Was Tul (ChayulaLeagueTowerBoss / Hive Colony)
('Metadata/Monsters/BreachBossFallenKnight/FallenKnight', 'The Will of Esh', 'unique', 0.17, 'estimated', 'poewiki It That Was Tul (3.28, n=330)', 'share of the one guaranteed unique'),
('Metadata/Monsters/BreachBossFallenKnight/FallenKnight', 'The Will of Tul', 'unique', 0.17, 'estimated', 'poewiki It That Was Tul (3.28, n=330)', 'share of the one guaranteed unique'),
('Metadata/Monsters/BreachBossFallenKnight/FallenKnight', 'The Will of Xoph', 'unique', 0.17, 'estimated', 'poewiki It That Was Tul (3.28, n=330)', 'share of the one guaranteed unique'),
('Metadata/Monsters/BreachBossFallenKnight/FallenKnight', 'The Will of Uul-Netol', 'unique', 0.12, 'estimated', 'poewiki It That Was Tul (3.28, n=330)', 'share of the one guaranteed unique'),
('Metadata/Monsters/BreachBossFallenKnight/FallenKnight', 'The Sundered Will', 'unique', 0.04, 'estimated', 'poewiki It That Was Tul (3.28, n=330)', 'share of the one guaranteed unique'),
('Metadata/Monsters/BreachBossFallenKnight/FallenKnight', 'Hand of the Lords', 'unique', 0.29, 'estimated', 'poewiki It That Was Tul (3.28, n=330)', 'share of the one guaranteed unique'),
('Metadata/Monsters/BreachBossFallenKnight/FallenKnight', 'The Grey Wind', 'unique', 0.03, 'estimated', 'poewiki It That Was Tul (3.28, n=330)', 'share of the one guaranteed unique'),
('Metadata/Monsters/BreachBossFallenKnight/FallenKnight', 'Uul-Netol''s Vow', 'unique', 0.01, 'estimated', 'poewiki It That Was Tul (3.28, n=330)', 'share of the one guaranteed unique'),
('Metadata/Monsters/BreachBossFallenKnight/FallenKnight', 'Metadata/Items/Currency/CurrencyBreachUpgradeUniqueGeneral', 'currency', 0.20, 'estimated', 'poewiki It That Was Tul (3.28, n=330)', 'Flesh of Xesht'),
('Metadata/Monsters/BreachBossFallenKnight/FallenKnight', 'Metadata/Items/Gems/SupportGemFoulgrasp', 'gem', 0.10, 'estimated', 'poewiki It That Was Tul (3.28, n=330)', 'Foulgrasp Support'),
('Metadata/Monsters/BreachBossFallenKnight/FallenKnight', 'Metadata/Items/Gems/SupportGemHiveborn', 'gem', 0.05, 'estimated', 'poewiki It That Was Tul (3.28, n=330)', 'Hiveborn Support'),
('Metadata/Monsters/BreachBossFallenKnight/FallenKnight', 'Metadata/Items/DivinationCards/DivinationCardSomethingDark', 'card', 0.10, 'estimated', 'poewiki It That Was Tul (3.28, n=330)', 'Something Dark'),
('Metadata/Monsters/BreachBossFallenKnight/FallenKnight', 'Metadata/Items/DivinationCards/DivinationCardTheEscape', 'card', 0.09, 'estimated', 'poewiki It That Was Tul (3.28, n=330)', 'The Escape'),

-- It That Was Esh (ChayulaLeagueTowerBoss / Hive Colony; shared loot table with It That Was Tul)
('Metadata/Monsters/BreachBossHandMage/HandMage', 'The Will of Esh', 'unique', 0.17, 'estimated', 'poewiki It That Was Esh (3.28, n=330)', 'share of the one guaranteed unique'),
('Metadata/Monsters/BreachBossHandMage/HandMage', 'The Will of Tul', 'unique', 0.17, 'estimated', 'poewiki It That Was Esh (3.28, n=330)', 'share of the one guaranteed unique'),
('Metadata/Monsters/BreachBossHandMage/HandMage', 'The Will of Xoph', 'unique', 0.17, 'estimated', 'poewiki It That Was Esh (3.28, n=330)', 'share of the one guaranteed unique'),
('Metadata/Monsters/BreachBossHandMage/HandMage', 'The Will of Uul-Netol', 'unique', 0.12, 'estimated', 'poewiki It That Was Esh (3.28, n=330)', 'share of the one guaranteed unique'),
('Metadata/Monsters/BreachBossHandMage/HandMage', 'The Sundered Will', 'unique', 0.04, 'estimated', 'poewiki It That Was Esh (3.28, n=330)', 'share of the one guaranteed unique'),
('Metadata/Monsters/BreachBossHandMage/HandMage', 'Hand of the Lords', 'unique', 0.29, 'estimated', 'poewiki It That Was Esh (3.28, n=330)', 'share of the one guaranteed unique'),
('Metadata/Monsters/BreachBossHandMage/HandMage', 'The Grey Wind', 'unique', 0.03, 'estimated', 'poewiki It That Was Esh (3.28, n=330)', 'share of the one guaranteed unique'),
('Metadata/Monsters/BreachBossHandMage/HandMage', 'Uul-Netol''s Vow', 'unique', 0.01, 'estimated', 'poewiki It That Was Esh (3.28, n=330)', 'share of the one guaranteed unique'),
('Metadata/Monsters/BreachBossHandMage/HandMage', 'Metadata/Items/Currency/CurrencyBreachUpgradeUniqueGeneral', 'currency', 0.20, 'estimated', 'poewiki It That Was Esh (3.28, n=330)', 'Flesh of Xesht'),
('Metadata/Monsters/BreachBossHandMage/HandMage', 'Metadata/Items/Gems/SupportGemFoulgrasp', 'gem', 0.10, 'estimated', 'poewiki It That Was Esh (3.28, n=330)', 'Foulgrasp Support'),
('Metadata/Monsters/BreachBossHandMage/HandMage', 'Metadata/Items/Gems/SupportGemHiveborn', 'gem', 0.05, 'estimated', 'poewiki It That Was Esh (3.28, n=330)', 'Hiveborn Support'),
('Metadata/Monsters/BreachBossHandMage/HandMage', 'Metadata/Items/DivinationCards/DivinationCardSomethingDark', 'card', 0.10, 'estimated', 'poewiki It That Was Esh (3.28, n=330)', 'Something Dark'),
('Metadata/Monsters/BreachBossHandMage/HandMage', 'Metadata/Items/DivinationCards/DivinationCardTheEscape', 'card', 0.09, 'estimated', 'poewiki It That Was Esh (3.28, n=330)', 'The Escape'),

-- Vruun, Marshal of Xesht (Unstable Breach wild boss)
('Metadata/Monsters/Breach/WildBreachBoss', 'Metadata/Items/Rings/RingB2', 'other', 0.22, 'estimated', 'poewiki Vruun, Marshal of Xesht (3.27-3.28, n=471)', 'Enthalpic Ring (guaranteed rare Breach ring pool)'),
('Metadata/Monsters/Breach/WildBreachBoss', 'Metadata/Items/Rings/RingB1', 'other', 0.22, 'estimated', 'poewiki Vruun, Marshal of Xesht (3.27-3.28, n=471)', 'Cryonic Ring (guaranteed rare Breach ring pool)'),
('Metadata/Monsters/Breach/WildBreachBoss', 'Metadata/Items/Rings/RingB3', 'other', 0.22, 'estimated', 'poewiki Vruun, Marshal of Xesht (3.27-3.28, n=471)', 'Synaptic Ring (guaranteed rare Breach ring pool)'),
('Metadata/Monsters/Breach/WildBreachBoss', 'Metadata/Items/Rings/RingB4', 'other', 0.28, 'estimated', 'poewiki Vruun, Marshal of Xesht (3.27-3.28, n=471)', 'Organic Ring (guaranteed rare Breach ring pool)'),
('Metadata/Monsters/Breach/WildBreachBoss', 'Metadata/Items/Rings/RingB5', 'other', 0.06, 'estimated', 'poewiki Vruun, Marshal of Xesht (3.27-3.28, n=471)', 'Fugitive Ring (guaranteed rare Breach ring pool)'),
('Metadata/Monsters/Breach/WildBreachBoss', 'Metadata/Items/MapFragments/CurrencyBrequelBossKey', 'currency', 0.12, 'estimated', 'poewiki Vruun, Marshal of Xesht (3.27-3.28, n=471)', 'Hivebrain Gland'),
('Metadata/Monsters/Breach/WildBreachBoss', 'Metadata/Items/Gems/SupportGemPyre', 'gem', 0.02, 'estimated', 'poewiki Vruun, Marshal of Xesht (3.27-3.28, n=471)', 'Pyre Support'),
('Metadata/Monsters/Breach/WildBreachBoss', 'Metadata/Items/Gems/SupportGemBonespire', 'gem', 0.02, 'estimated', 'poewiki Vruun, Marshal of Xesht (3.27-3.28, n=471)', 'Bonespire Support'),
('Metadata/Monsters/Breach/WildBreachBoss', 'Metadata/Items/DivinationCards/DivinationCardTheUndisputed', 'card', 0.02, 'estimated', 'poewiki Vruun, Marshal of Xesht (3.27-3.28, n=471)', 'The Undisputed')
ON CONFLICT(boss_id, item_id) DO UPDATE SET
    item_kind = excluded.item_kind,
    chance = excluded.chance,
    confidence = excluded.confidence,
    source = excluded.source,
    notes = excluded.notes;

INSERT INTO drop_card_sources (card_id, source_kind, source_id, min_map_tier, drop_level, confidence, source, notes) VALUES
('Metadata/Items/DivinationCards/DivinationCardSomethingDark', 'boss', 'Metadata/Monsters/BreachBossFallenKnight/FallenKnight', NULL, 0, 'estimated', 'poewiki It That Was Tul (3.28, n=330)', NULL),
('Metadata/Items/DivinationCards/DivinationCardSomethingDark', 'boss', 'Metadata/Monsters/BreachBossHandMage/HandMage', NULL, 0, 'estimated', 'poewiki It That Was Esh (3.28, n=330)', NULL),
('Metadata/Items/DivinationCards/DivinationCardTheEscape', 'boss', 'Metadata/Monsters/BreachBossFallenKnight/FallenKnight', NULL, 0, 'estimated', 'poewiki It That Was Tul (3.28, n=330)', NULL),
('Metadata/Items/DivinationCards/DivinationCardTheEscape', 'boss', 'Metadata/Monsters/BreachBossHandMage/HandMage', NULL, 0, 'estimated', 'poewiki It That Was Esh (3.28, n=330)', NULL),
('Metadata/Items/DivinationCards/DivinationCardTheUndisputed', 'boss', 'Metadata/Monsters/Breach/WildBreachBoss', NULL, 0, 'estimated', 'poewiki Vruun, Marshal of Xesht (3.27-3.28, n=471)', NULL)
ON CONFLICT(card_id, source_kind, source_id) DO NOTHING;
