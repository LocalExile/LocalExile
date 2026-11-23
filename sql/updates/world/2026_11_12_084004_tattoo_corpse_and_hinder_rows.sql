-- The corpse-consumption and Hinder tattoo rows (issue #372). Each row is the replacement passive's canonical stat
-- set from the client's PassiveSkillOverrides (Stats + StatValues).
-- Consumers: killing_blow_consumes_corpse_restore_10%_life_chance_% is rolled by the killing blow under the Monsters
-- lock (MonsterKilled -> ConsumeCorpseOnKillLocked: corpse taken from conn.Monsters and from summoning/explosions/
-- warcries, consumed_corpse recorded, 10% of maximum life recovered); spells_chance_to_hinder_on_hit_% is rolled by
-- HitDebuffsOnHitLocked on spell hits (4 seconds, 30% reduced movement speed, the client's hinder buff row is shown).

INSERT INTO tattoo_effects (name, enabled) VALUES
('Tattoo of the Kitava Heart Eater', 1),
('Tattoo of the Tasalio Shaman', 1);

INSERT INTO tattoo_effect_stats (tattoo, ord, stat, value) VALUES
('Tattoo of the Kitava Heart Eater', 0, 'killing_blow_consumes_corpse_restore_10%_life_chance_%', 4),
('Tattoo of the Tasalio Shaman', 0, 'spells_chance_to_hinder_on_hit_%', 5);
