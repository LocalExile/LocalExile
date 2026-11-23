INSERT INTO encounter_jewel_drop_overrides_schema (boss_id, item_id, chance)
SELECT boss_id, item_id, chance FROM encounter_jewel_drop_overrides;
DROP TABLE encounter_jewel_drop_overrides;
ALTER TABLE encounter_jewel_drop_overrides_schema RENAME TO encounter_jewel_drop_overrides;

INSERT INTO drop_boss_items (boss_id, item_id, item_kind, chance, confidence, source, notes)
VALUES ('Metadata/Monsters/AtlasBosses/TheElder/TheElderUber', 'Watcher''s Eye', 'unique', NULL, 'unknown',
        'PoEDB Watcher''s Eye; WorldAreas MapWorldsElderArenaUber', 'Three aura modifiers; chance unpublished.');
