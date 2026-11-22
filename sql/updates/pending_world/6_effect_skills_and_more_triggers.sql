-- The skills the "area" executor releases, as data (they were a list in the code): the art of each is the nearest of
-- the client's effects until its row is checked against the game (checked = 1).
INSERT INTO effect_skills (effect, how, art, scale, seconds, radius, hits, every, spread, checked, note) VALUES
('LightningBolt', 'area', 'HeraldOfThunderStrike', 1, 2, NULL, 1, 0, 0, 0,
 'Choir of the Storm, Voice of the Storm. Art: a guess (the tables name none for the skill)'),
('FireBurstOnHit', 'area', 'PrismaticBurstFire', 1, 2, NULL, 1, 0, 0, 0,
 'Essence of Hysteria. Art: a guess'),
('TriggeredExplosion', 'area', 'FireNovaEnchantment', 1, 2, NULL, 1, 0, 0, 0,
 'Fire Burst on Kill. Art: a guess'),
('IgnitionBlast', 'area', 'IgnitionBlastExplode', 1, 2, NULL, 1, 0, 0, 0,
 'Art: the client''s own for the skill; size unchecked. Its "% of the monster''s maximum life as fire" part is not dealt yet'),
('LightningBoltOnHit', 'area', 'HeraldOfThunderStrike', 1, 2, NULL, 1, 0, 0, 0,
 'The mutated Mjolner''s Lightning Bolt. Art: a guess, as Lightning Bolt''s'),
('UniqueEnchantmentOfInfernoOnCrit', 'area', 'EnchantmentMeteorImpact', 1, 2, 10, 20, 0.1, 22, 0,
 'Commandment of Inferno on crit: a storm around the character, a meteor every fire_storm_fireball_delay_ms (100) for its 2 seconds. The meteor''s radius and the storm''s width are guesses (the old Firestorm''s 10 and about 25); art: the enchantment''s meteor impact, the falling meteor is not shown'),
('StalkingPustule', 'area', 'VolatileDeadExplode', 1, 2, 15, 1, 0, 0, 0,
 'An approximation: the real orb seeks an enemy before it bursts; here it bursts where the kill was. Art: Volatile Dead''s burst, a guess');

-- More triggers of item-granted skills ("carrier":"skill"). Which skills they fire depends on the executors and on
-- effect_skills above.
INSERT INTO effect_rules (stat, ord, rule, reviewed, note) VALUES
('trigger_on_ignited_enemy_death', 0,
 '{"carrier":"skill","when":"kill","checks":["ignited"],"cooldown":{"data":"cooldown"},"uses":{"data":"uses"},"do":[{"kind":"skill"}]}',
 1, 'Trigger this skill when an Ignited Enemy is Killed (Ignition Blast), where the enemy died'),
('chance_to_cast_on_kill_%', 0,
 '{"carrier":"skill","when":"kill","chance":{"from":"stat"},"cooldown":{"data":"cooldown"},"uses":{"data":"uses"},"do":[{"kind":"skill"}]}',
 1, 'Trigger this Spell on Kill, where the enemy died (Stalking Pustule)'),
('unique_mjolner_lightning_spells_triggered', 0,
 '{"carrier":"skill","when":"hit","checks":["attack"],"cooldown":{"data":"cooldown"},"uses":{"data":"uses"},"do":[{"kind":"skill"}]}',
 1, 'The mutated Mjolner''s Lightning Bolt on Hit. A guess from the mod''s name: every attack hit, within the skill''s cooldown');
