-- Retail 3.29 campaign loot, vendor, monster balance, and area pacing settings.
-- All settings can be retuned live in world.db and reloaded via /reload.
-- Comments note the legacy generous preset values where they differ from retail 3.29.
INSERT INTO system_settings (key, value) VALUES
-- Monster drop baseline: retail 16% normal monster drop chance (legacy generous: 35).
('drop_normal_chance_pct', 16.0),
-- Drop category weights: retail 3.28+ 40/4/8/48 (legacy: 35/4/8/53).
('drop_weight_currency', 40.0),
('drop_weight_gems', 4.0),
('drop_weight_flasks', 8.0),
('drop_weight_equipment', 48.0),
-- Natural 1-20% quality roll chance on monster drops (0 disables).
('drop_natural_quality_chance_pct', 10.0),
-- Multiplier applied to retail socket-link curve (raise above 1.0 for generous links).
('drop_socket_link_scale', 1.0),
-- Multiplier applied to base unique drop chance (1.0 = retail parity).
('drop_unique_chance_multiplier', 1.0),
-- Over-levelled character drop penalty for currency and divination cards (0 disables).
('drop_penalty_enabled', 1.0),
('drop_penalty_level_offset', 2.0),
('drop_penalty_per_level_pct', 2.5),
('drop_penalty_character_level_cap', 68.0),
-- Base per-kill divination card chance in campaign areas with drop_card_areas rows.
('drop_campaign_card_chance_pct', 0.5),
-- Guaranteed rares and extra drops on first kill of campaign quest/act bosses (0 disables).
('drop_first_kill_boss_bonus', 1.0),
-- Levels between vendor offer refreshes: 1 = retail 100% every level-up (legacy staggered: 3).
('vendor_refresh_levels', 1.0),
-- Monster species mods, energy shield, armour, base crit (5% = 500 permyriad), and ailment threshold scaling.
('monster_species_mods_enabled', 1.0),
('monster_es_enabled', 1.0),
('monster_armour_enabled', 1.0),
('monster_base_crit_chance_permyriad', 500.0),
('monster_ailment_threshold_scaling', 1.0),
-- Campaign area chest density, doors, and magic/rare pack chances.
('campaign_chest_density_multiplier', 1.0),
('campaign_doors_enabled', 1.0),
('campaign_pack_magic_chance_pct', 12.0),
('campaign_pack_rare_chance_pct', 6.0);

-- Recalibrate drop_currency_weights for 3.28/3.29 retail parity:
-- higher early scroll share, lower Chromatic/Regret weights, ~0.055% Exalted/Divine parity,
-- and ~0.000033% Mirror of Kalandra / Hinekora's Lock parity.
UPDATE drop_currency_weights SET weight = 1150.0, notes = 'Scroll of Wisdom; 3.29 campaign scroll share adjustment' WHERE currency_id = 'Metadata/Items/Currency/CurrencyIdentification';
UPDATE drop_currency_weights SET weight = 360.0, notes = 'Portal Scroll; 3.29 campaign scroll share adjustment' WHERE currency_id = 'Metadata/Items/Currency/CurrencyPortal';
UPDATE drop_currency_weights SET weight = 110.0, notes = 'Chromatic Orb; 3.28/3.29 retail parity weight' WHERE currency_id = 'Metadata/Items/Currency/CurrencyRerollSocketColours';
UPDATE drop_currency_weights SET weight = 16.0, notes = 'Orb of Regret; 3.28/3.29 retail parity weight' WHERE currency_id = 'Metadata/Items/Currency/CurrencyPassiveRefund';
UPDATE drop_currency_weights SET weight = 2.2, notes = 'Exalted Orb; 3.28/3.29 ~0.055% parity weight' WHERE currency_id = 'Metadata/Items/Currency/CurrencyAddModToRare';
UPDATE drop_currency_weights SET weight = 2.2, notes = 'Divine Orb; 3.28/3.29 ~0.055% parity with Exalted Orb' WHERE currency_id = 'Metadata/Items/Currency/CurrencyModValues';
UPDATE drop_currency_weights SET weight = 0.0014, notes = 'Mirror of Kalandra; 3.28/3.29 ~0.000033% retail parity weight' WHERE currency_id = 'Metadata/Items/Currency/CurrencyDuplicate';
UPDATE drop_currency_weights SET weight = 0.0014, notes = 'Hinekora''s Lock; matched to Mirror of Kalandra 3.28/3.29 parity weight' WHERE currency_id = 'Metadata/Items/Currency/CurrencyHinekorasLock';

-- Oni-Goroshi only drops from Uber Hillock in The Twilight Strand, never from the general drop pool.
UPDATE unique_items SET encounter_only = 1 WHERE name = 'Oni-Goroshi';
