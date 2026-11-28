-- Which stats the effect rules handle. A stat an enabled rule reads is 'rule' (fx_rules lists it); fx_stat_notes holds
-- stats that are known and still handled elsewhere, and which step of the effect rules design takes them.
CREATE TABLE fx_stat_notes (
    stat TEXT PRIMARY KEY,
    handled TEXT NOT NULL CHECK (handled IN ('code', 'none')),
    ref TEXT,
    note TEXT
) STRICT;

CREATE VIEW fx_stats (stat, handled, ref, note) AS
SELECT s.key, 'rule', min(s.rule), NULL
FROM fx_rule_sources s JOIN fx_rules r ON r.id = s.rule
WHERE r.enabled = 1 AND s.kind = 'stat'
GROUP BY s.key
UNION ALL
SELECT n.stat, n.handled, n.ref, n.note FROM fx_stat_notes n
WHERE n.stat NOT IN (SELECT s.key FROM fx_rule_sources s JOIN fx_rules r ON r.id = s.rule WHERE r.enabled = 1);

INSERT INTO fx_stat_notes (stat, handled, ref, note) VALUES
('gain_X_endurance_charges_per_second', 'code', 'ChargeGains.Periodics', 'a task: step 5'),
('gain_X_frenzy_charges_per_second', 'code', 'ChargeGains.Periodics', 'a task: step 5'),
('gain_X_power_charges_per_second', 'code', 'ChargeGains.Periodics', 'a task: step 5'),
('body_armour_implicit_gain_endurance_charge_every_x_ms', 'code', 'ChargeGains.Periodics', 'a task: step 5'),
('body_armour_implicit_gain_frenzy_charge_every_x_ms', 'code', 'ChargeGains.Periodics', 'a task: step 5'),
('body_armour_implicit_gain_power_charge_every_x_ms', 'code', 'ChargeGains.Periodics', 'a task: step 5'),
('gain_X_random_charges_every_6_seconds', 'code', 'ChargeGains.Periodics', 'a task: step 5'),
('gain_endurance_charge_per_second_if_have_been_hit_recently', 'code', 'ChargeGains.Periodics', 'a task: step 5'),
('number_of_endurance_charges_to_gain_every_4_seconds_while_stationary', 'code', 'ChargeGains.Periodics', 'a task: step 5'),
('gain_a_endurance_charge_every_X_seconds_while_stationary', 'code', 'ChargeGains.Periodics', 'a task: step 5'),
('gain_random_charge_per_second_while_stationary', 'code', 'ChargeGains.Periodics', 'a task: step 5'),
('gain_a_frenzy_charge_every_X_seconds_while_moving', 'code', 'ChargeGains.Periodics', 'a task: step 5'),
('gain_power_charge_per_second_if_have_not_lost_power_charge_recently', 'code', 'ChargeGains.Periodics', 'a task: step 5'),
('lose_power_charge_each_second_if_not_detonated_mines_recently', 'code', 'ChargeGains.Periodics', 'a task: step 5'),
('gain_X_power_charges_on_casting_a_spell', 'code', 'MonsterCharges.MonsterChargesOnHitDealtLocked', 'a monster actor: step 4'),
('remove_charges_on_hit_%', 'code', 'MonsterCharges.MonsterChargesOnHitDealtLocked', 'charges taken from the player: step 2'),
('map_monsters_steal_charges', 'code', 'MonsterCharges.StealMapChargesLocked', 'charges taken from the player: step 2'),
('chance_to_grant_power_charge_on_death_%', 'code', 'Curses.CurseCharges', 'a cursed enemy grants the charge: step 2'),
('chance_to_grant_frenzy_charge_on_death_%', 'code', 'Curses.CurseCharges', 'a cursed enemy grants the charge: step 2'),
('chance_to_grant_endurance_charge_on_death_%', 'code', 'Curses.CurseCharges', 'a cursed enemy grants the charge: step 2'),
('grant_attacker_power_charge_when_hit_%_chance', 'code', 'Curses.CurseCharges', 'a cursed enemy grants the charge: step 2'),
('grant_attacker_frenzy_charge_when_hit_%_chance', 'code', 'Curses.CurseCharges', 'a cursed enemy grants the charge: step 2'),
('grant_attacker_endurance_charge_when_hit_%_chance', 'code', 'Curses.CurseCharges', 'a cursed enemy grants the charge: step 2'),
('chance_to_grant_endurance_charge_to_nearby_allies_on_hit_%', 'code', 'SpectreChargeRoars.PlayerGrantsNearbyChargesLocked', 'charges for nearby allies: step 2'),
('chance_to_grant_frenzy_charge_to_nearby_allies_on_hit_%', 'code', 'SpectreChargeRoars.PlayerGrantsNearbyChargesLocked', 'charges for nearby allies: step 2'),
('chance_to_grant_frenzy_charge_to_nearby_allies_on_kill_%', 'code', 'SpectreChargeRoars.PlayerGrantsNearbyChargesLocked', 'charges for nearby allies: step 2'),
('chance_to_grant_power_charge_to_nearby_allies_on_kill_%', 'code', 'SpectreChargeRoars.PlayerGrantsNearbyChargesLocked', 'charges for nearby allies: step 2');
