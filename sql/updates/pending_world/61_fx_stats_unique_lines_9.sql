-- Loreweave's fixed maximum resistances, an old dagger implicit as an alias, and three lines that only act on allies.
UPDATE fx_stats SET handled = 'code', ref = 'Progression (maximum resistances)', why = NULL, fit = 'approximate'
WHERE stat = 'override_maximum_damage_resistance_%';
INSERT INTO fx_aliases (stat, ord, counts_as, scale, note) VALUES
('old_dagger_implicit_critical_strike_chance_+50%', 0, 'critical_strike_chance_+%', 1, '50% increased Global Critical Strike Chance (Replica Ungil''s Gauche); the stat carries the 50');
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_aliases', why = NULL, fit = NULL
WHERE stat = 'old_dagger_implicit_critical_strike_chance_+50%';
UPDATE fx_stats SET handled = 'display', ref = 'party only', why = NULL, fit = NULL
WHERE stat IN ('nearby_party_members_max_endurance_charges_is_equal_to_yours',
  'unique_nearby_allies_recover_permyriad_max_life_on_death', 'quicksilver_flasks_apply_to_nearby_allies');
