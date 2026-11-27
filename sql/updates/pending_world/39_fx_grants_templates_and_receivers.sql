-- Grants, second part.
--
-- 1. A bundle can be one of the game's own aura templates (BuffTemplates), and "template": "line" takes the template
--    of the item line that gives the bundle. The game's data already says, for every "Nearby Enemies ..." line, what
--    its aura gives (the buff's stat), how far it reaches (AuraRadius: 60 cells for the stat auras, 6 metres) and
--    what it shows as. The 7 lines of update 37 now read all of that from their own line instead of from numbers
--    written here, and the monsters in reach are shown the aura's own buff. One more line comes with it: "Nearby
--    Enemies have #% increased Stun and Block Recovery".
-- 2. fx_reads_as: what an actor of another kind reads for a stat a bundle gives it. A player's "elemental damage
--    taken" is the three elements' on a monster; Unwavering Stance's stat is "cannot be stunned" on a monster.
-- 3. A grant can go to the actor itself (standing, or for a time from an event) and to every player in the area.
CREATE TABLE fx_reads_as (
    actor TEXT NOT NULL CHECK (actor IN ('monster', 'player')),
    stat TEXT NOT NULL,
    ord INTEGER NOT NULL DEFAULT 0,
    reads TEXT NOT NULL,
    scale REAL NOT NULL DEFAULT 1,
    note TEXT,
    PRIMARY KEY (actor, stat, ord)
) STRICT;
INSERT INTO fx_reads_as (actor, stat, ord, reads, scale, note) VALUES
('monster', 'elemental_damage_taken_+%', 0, 'fire_damage_taken_+%', 1, 'Monsters take each element''s own.'),
('monster', 'elemental_damage_taken_+%', 1, 'cold_damage_taken_+%', 1, NULL),
('monster', 'elemental_damage_taken_+%', 2, 'lightning_damage_taken_+%', 1, NULL),
('monster', 'resist_all_%', 0, 'base_resist_all_elements_%', 1, '"to all Resistances": the three elements and chaos.'),
('monster', 'resist_all_%', 1, 'base_chaos_damage_resistance_%', 1, NULL),
('monster', 'keystone_unwavering_stance', 0, 'base_cannot_be_stunned', 1, 'The part of Unwavering Stance a monster has code for; "cannot evade" has none yet.');
UPDATE fx_rule_do SET args = '{"bundle":{"template":"line"}}', target_args = '{}'
WHERE action = 'grant' AND rule IN ('nearby_enemies_fire_resistance', 'nearby_enemies_cold_resistance', 'nearby_enemies_lightning_resistance',
    'nearby_enemies_chaos_resistance', 'nearby_enemies_all_resistances', 'nearby_enemies_physical_damage_taken', 'nearby_enemies_elemental_damage_taken');
INSERT INTO fx_rules (id, book, title, replaces, status, origin, note) VALUES
('nearby_enemies_stun_recovery', 'nearby', 'Nearby Enemies have #% increased Stun and Block Recovery', NULL, 'built', 'parts', 'Built from parts alone: no code handled this before. Its aura is the line''s own template.');
INSERT INTO fx_rule_sources (rule, ord, kind, key) VALUES
('nearby_enemies_stun_recovery', 0, 'item', 'local_display_nearby_enemies_stun_and_block_recovery_+%');
INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES
('nearby_enemies_stun_recovery', 0, 'active', '{}');
INSERT INTO fx_rule_do (rule, ord, action, args, target, target_args, value, timers) VALUES
('nearby_enemies_stun_recovery', 0, 'grant', '{"bundle":{"template":"line"}}', 'enemies_near', '{}', '{}', '[{"timer":"source"}]');
UPDATE fx_stats SET handled = 'rule', ref = 'nearby_enemies_stun_recovery', why = NULL, fit = NULL
WHERE stat = 'local_display_nearby_enemies_stun_and_block_recovery_+%';
UPDATE fx_actions SET takes = takes || ' A bundle can also be template: a BuffTemplates Id, or "line" for the giving line''s own (its stats, its reach and the buff it shows as come from the game''s data), and shows: a BuffDefinitions Id its carrier is shown with.',
    note = note || ' To self it stands while the rule is active, or lasts for the duration timer from an event; to players_area every player in the area has it while the rule is active. Given to a monster, a stat that fx_reads_as names another for is given as that one.'
WHERE id = 'grant';
UPDATE fx_targets SET built = 1, note = note || ' Built for the action "grant": every player in the area, the actor included, while the rule is active.' WHERE id = 'players_area';
UPDATE fx_targets SET note = note || ' For the action "grant" the radius may be left out when the bundle is a template: its own reach is used.' WHERE id = 'enemies_near';

-- 4. An area as a giver. Modifier rows with the target players_area give the area's players a stat they already act
--    on, as enemies_area rows do for its monsters: 17 map modifiers no code read ("Players have Point Blank",
--    "Players cannot Block Attack Damage", "Players and Monsters take #% increased Fire Damage" and others). And a
--    rule whose source is the area (kind "area", key: a stat the area has) can give its monsters or its players a
--    whole bundle; none is written here yet, the part is there for map modifiers that are made up.
INSERT INTO fx_modifiers (stat, ord, quantity, how, scale, target, note) VALUES
('map_player_attack_cast_and_movement_speed_+%_during_onslaught', 0, 'attack_cast_and_movement_speed_+%_during_onslaught', 'flat', 1, 'players_area', NULL),
('map_player_base_chaos_damage_taken_per_minute', 0, 'base_chaos_damage_taken_per_minute', 'flat', 1, 'players_area', NULL),
('map_player_cannot_block_attacks', 0, 'cannot_block_attacks', 'flat', 1, 'players_area', NULL),
('map_player_chance_to_gain_vaal_soul_on_kill_%', 0, 'chance_to_gain_vaal_soul_on_kill_%', 'flat', 1, 'players_area', NULL),
('map_player_cannot_recharge_energy_shield', 0, 'cannot_recharge_energy_shield', 'flat', 1, 'players_area', NULL),
('map_player_movement_velocity_+%', 0, 'base_movement_velocity_+%', 'flat', 1, 'players_area', NULL),
('map_players_movement_skills_cooldown_speed_+%', 0, 'movement_skills_cooldown_speed_+%', 'flat', 1, 'players_area', NULL),
('map_players_resist_all_%', 0, 'base_resist_all_elements_%', 'flat', 1, 'players_area', NULL),
('map_players_resist_all_%', 1, 'base_chaos_damage_resistance_%', 'flat', 1, 'players_area', NULL),
('map_player_no_regeneration', 0, 'no_life_regeneration', 'flat', 1, 'players_area', NULL),
('map_player_no_regeneration', 1, 'no_mana_regeneration', 'flat', 1, 'players_area', NULL),
('map_players_have_point_blank', 0, 'keystone_point_blank', 'flat', 1, 'players_area', NULL),
('map_player_has_blood_magic_keystone', 0, 'keystone_blood_magic', 'flat', 1, 'players_area', NULL),
('map_player_cannot_expose', 0, 'cannot_inflict_exposure', 'flat', 1, 'players_area', NULL),
('map_players_and_monsters_fire_damage_taken_+%', 0, 'fire_damage_taken_+%', 'flat', 1, 'players_area', NULL),
('map_players_and_monsters_fire_damage_taken_+%', 1, 'fire_damage_taken_+%', 'flat', 1, 'enemies_area', NULL),
('map_players_and_monsters_cold_damage_taken_+%', 0, 'cold_damage_taken_+%', 'flat', 1, 'players_area', NULL),
('map_players_and_monsters_cold_damage_taken_+%', 1, 'cold_damage_taken_+%', 'flat', 1, 'enemies_area', NULL),
('map_players_and_monsters_lightning_damage_taken_+%', 0, 'lightning_damage_taken_+%', 'flat', 1, 'players_area', NULL),
('map_players_and_monsters_lightning_damage_taken_+%', 1, 'lightning_damage_taken_+%', 'flat', 1, 'enemies_area', NULL),
('map_players_and_monsters_physical_damage_taken_+%', 0, 'physical_damage_taken_+%', 'flat', 1, 'players_area', NULL),
('map_players_and_monsters_physical_damage_taken_+%', 1, 'physical_damage_taken_+%', 'flat', 1, 'enemies_area', NULL),
('map_players_and_monsters_chaos_damage_taken_+%', 0, 'chaos_damage_taken_+%', 'flat', 1, 'players_area', 'Players only: monster code reads no chaos damage taken.')
ON CONFLICT(stat, ord) DO NOTHING;
INSERT INTO fx_stats (stat, kind, handled, ref) VALUES
('map_player_attack_cast_and_movement_speed_+%_during_onslaught', 'number', 'modifier', 'attack_cast_and_movement_speed_+%_during_onslaught on the areas players'),
('map_player_base_chaos_damage_taken_per_minute', 'number', 'modifier', 'base_chaos_damage_taken_per_minute on the areas players'),
('map_player_cannot_block_attacks', 'number', 'modifier', 'cannot_block_attacks on the areas players'),
('map_player_chance_to_gain_vaal_soul_on_kill_%', 'number', 'modifier', 'chance_to_gain_vaal_soul_on_kill_% on the areas players'),
('map_player_cannot_recharge_energy_shield', 'number', 'modifier', 'cannot_recharge_energy_shield on the areas players'),
('map_player_movement_velocity_+%', 'number', 'modifier', 'base_movement_velocity_+% on the areas players'),
('map_players_movement_skills_cooldown_speed_+%', 'number', 'modifier', 'movement_skills_cooldown_speed_+% on the areas players'),
('map_players_resist_all_%', 'number', 'modifier', 'base_resist_all_elements_% and base_chaos_damage_resistance_% on the areas players'),
('map_player_no_regeneration', 'number', 'modifier', 'no_life_regeneration and no_mana_regeneration on the areas players'),
('map_players_have_point_blank', 'number', 'modifier', 'keystone_point_blank on the areas players'),
('map_player_has_blood_magic_keystone', 'number', 'modifier', 'keystone_blood_magic on the areas players'),
('map_player_cannot_expose', 'number', 'modifier', 'cannot_inflict_exposure on the areas players'),
('map_players_and_monsters_fire_damage_taken_+%', 'number', 'modifier', 'fire_damage_taken_+% on the areas players and monsters'),
('map_players_and_monsters_cold_damage_taken_+%', 'number', 'modifier', 'cold_damage_taken_+% on the areas players and monsters'),
('map_players_and_monsters_lightning_damage_taken_+%', 'number', 'modifier', 'lightning_damage_taken_+% on the areas players and monsters'),
('map_players_and_monsters_physical_damage_taken_+%', 'number', 'modifier', 'physical_damage_taken_+% on the areas players and monsters'),
('map_players_and_monsters_chaos_damage_taken_+%', 'number', 'modifier', 'chaos_damage_taken_+% on the areas players')
ON CONFLICT(stat) DO UPDATE SET handled = 'modifier', ref = excluded.ref, why = NULL, fit = NULL;
