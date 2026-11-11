-- The corpse-consumption and Hinder tattoo rows (issue #372). Each row is the replacement passive's canonical stat
-- set from the client's PassiveSkillOverrides (Stats + StatValues).
-- Consumers: killing_blow_consumes_corpse_restore_10%_life_chance_% is rolled by the killing blow under the Monsters
-- lock (MonsterKilled -> ConsumeCorpseOnKillLocked: corpse taken from conn.Monsters and from summoning/explosions/
-- warcries, consumed_corpse recorded, 10% of maximum life recovered); spells_chance_to_hinder_on_hit_% is rolled by
-- HitDebuffsOnHitLocked on spell hits (4 seconds, 30% reduced movement speed, the client's hinder buff row is shown).
INSERT INTO catalog_entries (kind, key, payload, enabled) VALUES
('tattoo_effect', 'Tattoo of the Kitava Heart Eater', '{"Name":"Tattoo of the Kitava Heart Eater","Stats":[{"Stat":"killing_blow_consumes_corpse_restore_10%_life_chance_%","Value":4}]}', 1),
('tattoo_effect', 'Tattoo of the Tasalio Shaman', '{"Name":"Tattoo of the Tasalio Shaman","Stats":[{"Stat":"spells_chance_to_hinder_on_hit_%","Value":5}]}', 1);
