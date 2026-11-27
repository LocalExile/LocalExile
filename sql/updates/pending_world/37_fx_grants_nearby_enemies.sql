-- Grants: a rule can give OTHER actors a bundle of stats. The first step of "anything on anything": what an item,
-- a mod or a list of stats does becomes a bundle, and a rule gives the bundle to the enemies in the area, the enemies
-- within a radius, or the enemy of an event for a time. The monsters carry it as a layer of their own stats, with the
-- giver as its source, and lose it when the rule ends, when they leave the radius or when the giver leaves the area.
--
-- Also here: the source kind "item". Its stat belongs to the item (Stats IsLocal) and never reaches the character, so
-- the rule is turned on by each worn item that has the stat. That is what lets "Nearby Enemies ..." lines be rules.
--
-- Built with it, from rows alone (no code read these stats before): 7 "Nearby Enemies have/take ..." lines as grants,
-- each giving the line's own rolled value, and 2 "Nearby Enemies are Blinded / Intimidated" lines as the stat the
-- enemy-aura step already acts on. Nearby is 60 cells (6 metres), the reach the game's other enemy auras use; the
-- radius is each rule's own target_args and was not measured line by line.
INSERT INTO fx_actions (id, takes, built, origin, note) VALUES
('grant', 'bundle: what is given (unique: an item''s name; mod: a Mods Id or a list; stats: stat name to a number, to [min, max] or to {"from":"roll"} for the giving line''s own value; name); roll: where in its ranges the bundle sits, 0 to 100 (100 unless said); effect: a percent of the whole bundle (100 unless said). roll and effect are values, so either can be the line''s roll.', 1, 'Nearby Enemies have -9% to Fire Resistance',
 'Gives the target a bundle of stats as a layer of its own stats. To enemies_area or enemies_near it stands while the rule is active and follows who is there; to enemy (the trigger''s) it lasts for the duration timer. Stats that describe the item itself are left out of a bundle. A stat does on a monster what monster code reads it for; +# to maximum Life counts.')
ON CONFLICT(id) DO UPDATE SET takes = excluded.takes, built = 1, origin = excluded.origin, note = excluded.note;
UPDATE fx_targets SET note = note || ' For the action "grant": every living enemy in the area, the ones that arrive later too.' WHERE id = 'enemies_area';
UPDATE fx_targets SET note = note || ' For the action "grant": the enemies within "radius" cells of the actor (10 cells are 1 metre), checked a few times a second.' WHERE id = 'enemies_near';
UPDATE fx_actions SET takes = takes || '; value: a percent of the listed numbers (100 unless given), so the line''s roll can set them'
WHERE id = 'quantity';
INSERT INTO fx_rules (id, book, title, replaces, status, origin, note) VALUES
('nearby_enemies_fire_resistance', 'nearby', 'Nearby Enemies have +#% to Fire Resistance', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('nearby_enemies_cold_resistance', 'nearby', 'Nearby Enemies have +#% to Cold Resistance', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('nearby_enemies_lightning_resistance', 'nearby', 'Nearby Enemies have +#% to Lightning Resistance', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('nearby_enemies_chaos_resistance', 'nearby', 'Nearby Enemies have +#% to Chaos Resistance', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('nearby_enemies_all_resistances', 'nearby', 'Nearby Enemies have #% to all Resistances', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('nearby_enemies_physical_damage_taken', 'nearby', 'Nearby Enemies take #% increased Physical Damage', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('nearby_enemies_elemental_damage_taken', 'nearby', 'Nearby Enemies take #% increased Elemental Damage', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before.'),
('nearby_enemies_blinded', 'nearby', 'Nearby Enemies are Blinded', NULL, 'built', 'parts', 'Built from parts alone: the line turns on the stat the enemy-aura step acts on.'),
('nearby_enemies_intimidated', 'nearby', 'Nearby Enemies are Intimidated', NULL, 'built', 'parts', 'Built from parts alone: the line turns on the stat the enemy-aura step acts on.');
INSERT INTO fx_rule_sources (rule, ord, kind, key) VALUES
('nearby_enemies_fire_resistance', 0, 'item', 'local_display_nearby_enemy_fire_damage_resistance_%'),
('nearby_enemies_cold_resistance', 0, 'item', 'local_display_nearby_enemy_cold_damage_resistance_%'),
('nearby_enemies_lightning_resistance', 0, 'item', 'local_display_nearby_enemy_lightning_damage_resistance_%'),
('nearby_enemies_chaos_resistance', 0, 'item', 'local_display_nearby_enemy_chaos_damage_resistance_%'),
('nearby_enemies_all_resistances', 0, 'item', 'local_display_nearby_enemies_all_resistances_%'),
('nearby_enemies_physical_damage_taken', 0, 'item', 'local_display_nearby_enemy_physical_damage_taken_+%'),
('nearby_enemies_elemental_damage_taken', 0, 'item', 'local_display_nearby_enemy_elemental_damage_taken_+%'),
('nearby_enemies_blinded', 0, 'item', 'local_display_nearby_enemies_are_blinded'),
('nearby_enemies_intimidated', 0, 'item', 'local_display_nearby_enemies_are_intimidated');
INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES
('nearby_enemies_fire_resistance', 0, 'active', '{}'),
('nearby_enemies_cold_resistance', 0, 'active', '{}'),
('nearby_enemies_lightning_resistance', 0, 'active', '{}'),
('nearby_enemies_chaos_resistance', 0, 'active', '{}'),
('nearby_enemies_all_resistances', 0, 'active', '{}'),
('nearby_enemies_physical_damage_taken', 0, 'active', '{}'),
('nearby_enemies_elemental_damage_taken', 0, 'active', '{}'),
('nearby_enemies_blinded', 0, 'active', '{}'),
('nearby_enemies_intimidated', 0, 'active', '{}');
INSERT INTO fx_rule_do (rule, ord, action, args, target, target_args, value, timers) VALUES
('nearby_enemies_fire_resistance', 0, 'grant', '{"bundle":{"name":"nearby_enemies_fire_resistance","stats":{"base_fire_damage_resistance_%":{"from":"roll"}}}}', 'enemies_near', '{"radius":60}', '{}', '[{"timer":"source"}]'),
('nearby_enemies_cold_resistance', 0, 'grant', '{"bundle":{"name":"nearby_enemies_cold_resistance","stats":{"base_cold_damage_resistance_%":{"from":"roll"}}}}', 'enemies_near', '{"radius":60}', '{}', '[{"timer":"source"}]'),
('nearby_enemies_lightning_resistance', 0, 'grant', '{"bundle":{"name":"nearby_enemies_lightning_resistance","stats":{"base_lightning_damage_resistance_%":{"from":"roll"}}}}', 'enemies_near', '{"radius":60}', '{}', '[{"timer":"source"}]'),
('nearby_enemies_chaos_resistance', 0, 'grant', '{"bundle":{"name":"nearby_enemies_chaos_resistance","stats":{"base_chaos_damage_resistance_%":{"from":"roll"}}}}', 'enemies_near', '{"radius":60}', '{}', '[{"timer":"source"}]'),
('nearby_enemies_all_resistances', 0, 'grant', '{"bundle":{"name":"nearby_enemies_all_resistances","stats":{"base_resist_all_elements_%":{"from":"roll"},"base_chaos_damage_resistance_%":{"from":"roll"}}}}', 'enemies_near', '{"radius":60}', '{}', '[{"timer":"source"}]'),
('nearby_enemies_physical_damage_taken', 0, 'grant', '{"bundle":{"name":"nearby_enemies_physical_damage_taken","stats":{"physical_damage_taken_+%":{"from":"roll"}}}}', 'enemies_near', '{"radius":60}', '{}', '[{"timer":"source"}]'),
('nearby_enemies_elemental_damage_taken', 0, 'grant', '{"bundle":{"name":"nearby_enemies_elemental_damage_taken","stats":{"fire_damage_taken_+%":{"from":"roll"},"cold_damage_taken_+%":{"from":"roll"},"lightning_damage_taken_+%":{"from":"roll"}}}}', 'enemies_near', '{"radius":60}', '{}', '[{"timer":"source"}]'),
('nearby_enemies_blinded', 0, 'quantity', '{"stats":{"virtual_nearby_enemies_are_blinded":1}}', 'self', '{}', '{}', '[{"timer":"source"}]'),
('nearby_enemies_intimidated', 0, 'quantity', '{"stats":{"virtual_nearby_enemies_are_intimidated":1}}', 'self', '{}', '{}', '[{"timer":"source"}]');
UPDATE fx_stats SET handled = 'rule', ref = CASE stat
    WHEN 'local_display_nearby_enemy_fire_damage_resistance_%' THEN 'nearby_enemies_fire_resistance'
    WHEN 'local_display_nearby_enemy_cold_damage_resistance_%' THEN 'nearby_enemies_cold_resistance'
    WHEN 'local_display_nearby_enemy_lightning_damage_resistance_%' THEN 'nearby_enemies_lightning_resistance'
    WHEN 'local_display_nearby_enemy_chaos_damage_resistance_%' THEN 'nearby_enemies_chaos_resistance'
    WHEN 'local_display_nearby_enemies_all_resistances_%' THEN 'nearby_enemies_all_resistances'
    WHEN 'local_display_nearby_enemy_physical_damage_taken_+%' THEN 'nearby_enemies_physical_damage_taken'
    WHEN 'local_display_nearby_enemy_elemental_damage_taken_+%' THEN 'nearby_enemies_elemental_damage_taken'
    WHEN 'local_display_nearby_enemies_are_blinded' THEN 'nearby_enemies_blinded'
    WHEN 'local_display_nearby_enemies_are_intimidated' THEN 'nearby_enemies_intimidated'
    END, why = NULL, fit = NULL
WHERE stat IN ('local_display_nearby_enemy_fire_damage_resistance_%', 'local_display_nearby_enemy_cold_damage_resistance_%', 'local_display_nearby_enemy_lightning_damage_resistance_%', 'local_display_nearby_enemy_chaos_damage_resistance_%', 'local_display_nearby_enemies_all_resistances_%', 'local_display_nearby_enemy_physical_damage_taken_+%', 'local_display_nearby_enemy_elemental_damage_taken_+%', 'local_display_nearby_enemies_are_blinded', 'local_display_nearby_enemies_are_intimidated');
