-- Retail 3.29 Acts 6-10 loot parity: unique drop pool hygiene, restored core unique weights,
-- currency weight recalibration, and retunable Part 2 campaign map / Divine Vessel settings.

-- 1. Enforce encounter_only = 1 on all boss/pinnacle uniques listed in drop_boss_items.
UPDATE unique_items
SET encounter_only = 1
WHERE encounter_only = 0
  AND name IN (SELECT item_id FROM drop_boss_items WHERE item_kind = 'unique');

-- 2. Enforce encounter_only = 1 on all Heist Replica uniques (Grand Heist Curio Display exclusives).
UPDATE unique_items
SET encounter_only = 1
WHERE encounter_only = 0
  AND name LIKE 'Replica %';

-- 3. Enforce encounter_only = 1 on Breach, Incursion, Delve boss/city, Delirium/Simulacrum,
-- Synthesis boss, vendor-recipe, Labyrinth, and legacy/race-reward exclusive uniques.
UPDATE unique_items
SET encounter_only = 1
WHERE encounter_only = 0
  AND name IN (
    -- Breach & Blessing-upgraded Breach exclusives
    'Skin of the Loyal', 'Skin of the Lords',
    'Xoph''s Inception', 'Xoph''s Nurture', 'Xoph''s Heart', 'Xoph''s Blood',
    'The Formless Flame', 'The Formless Inferno',
    'Tulfall', 'The Snowblind Grace', 'The Perfect Form', 'The Halcyon', 'The Pandemonius',
    'Esh''s Mirror', 'Esh''s Visage', 'Hand of Thought and Motion', 'Hand of Wisdom and Action',
    'Voice of the Storm', 'Choir of the Storm',
    'Uul-Netol''s Kiss', 'Uul-Netol''s Embrace', 'The Infinite Pursuit', 'The Red Trail',
    'The Anticipation', 'The Surrender', 'Uul-Netol''s Vow',
    'Severed in Sleep', 'United in Dream',
    'The Green Dream', 'The Green Nightmare', 'The Red Dream', 'The Red Nightmare',
    'The Blue Dream', 'The Blue Nightmare', 'Presence of Chayula',
    -- Incursion / Temple of Atzoatl & Vial-upgraded exclusives
    'Apep''s Slumber', 'Apep''s Supremacy',
    'Architect''s Hand', 'Slavedriver''s Hand',
    'Coward''s Chains', 'Coward''s Legacy',
    'Dance of the Offered', 'Omeyocan',
    'Mask of the Spirit Drinker', 'Mask of the Stitched Demon',
    'Sacrificial Heart', 'Zerphi''s Heart',
    'Soul Catcher', 'Soul Ripper',
    'Story of the Vaal', 'Fate of the Vaal',
    'String of Servitude', 'Tempered Flesh', 'Tempered Mind', 'Tempered Spirit',
    'Transcendent Flesh', 'Transcendent Mind', 'Transcendent Spirit',
    'Shadowstitch',
    -- Delve boss & Azurite Mine city exclusives
    'Aul''s Uprising', 'Crown of the Tyrant', 'Command of the Pit', 'Hale Negator',
    'Putembo''s Meadow', 'Putembo''s Mountain', 'Putembo''s Valley',
    'Uzaza''s Meadow', 'Uzaza''s Mountain', 'Uzaza''s Valley',
    'Ahkeli''s Meadow', 'Ahkeli''s Mountain', 'Ahkeli''s Valley',
    'Precursor''s Emblem',
    -- Delirium / Simulacrum exclusives
    'Voices', 'Megalomaniac', 'Split Personality', 'The Interrogation', 'Kitava''s Teachings',
    'One With Nothing', 'Calamitous Visions',
    -- Synthesis boss exclusives
    'Bottled Faith', 'Circle of Ambition', 'Circle of Anguish', 'Circle of Fear',
    'Circle of Guilt', 'Circle of Nostalgia', 'Circle of Regret',
    'Garb of the Ephemeral', 'Mask of the Tribunal', 'Nebulis',
    'Offering to the Serpent', 'Perepiteia', 'Storm''s Gift',
    -- Boss, vendor-recipe, Labyrinth, Blight, Vaal, and race-reward exclusives
    'The Broken Elegy', 'The Retch', 'The Goddess Scorned', 'The Goddess Unleashed', 'The Anima Stone',
    'Kingmaker', 'The Taming', 'Loreweave', 'Star of Wraeclast', 'Arborix', 'Duskdawn',
    'The Flow Untethered', 'The Adorned', 'Grand Spectrum',
    'Leadership''s Price', 'Heroic Tragedy',
    'Demigod''s Beacon', 'Demigod''s Bounty', 'Demigod''s Dominance', 'Demigod''s Eye',
    'Demigod''s Immortality', 'Demigod''s Presence', 'Demigod''s Stride',
    'Demigod''s Touch', 'Demigod''s Triumph'
  );

-- 4. Restore canonical ASCII-named retail-tier unique_weights rows for the 8 missing core uniques
-- (replacing curly-apostrophe / truncated duplicates where present, and seeding Rigwald's Charge).
DELETE FROM unique_weights
WHERE name IN (
    'Emperor’s Vigilance',
    'Tasalio’s Sign',
    'Ngamahu’s Sign',
    'Veruso’s Battering Rams',
    'Mercenary’s Lot',
    'Broken Elegy',
    'Witchhunter’s Judgment'
);

INSERT OR REPLACE INTO unique_weights (name, weight, ruthless_weight) VALUES
('Emperor''s Vigilance', 0.044072277499465164, 0.2457002353372873),
('Rigwald''s Charge', 0.25, 0.54),
('Tasalio''s Sign', 0.45662099263558775, 0.704225373395827),
('Ngamahu''s Sign', 0.3496503624754485, 0.6249999906867744),
('Veruso''s Battering Rams', 0.13404825668719367, 0.4048582949056321),
('Mercenary''s Lot', 0.13003901073590482, 0.4),
('The Broken Elegy', 0.10131712376847261, 0.35714286322496386),
('Witchhunter''s Judgment', 0.11074197448187126, 0.371747203988273);

-- 5. Recalibrate Cartographer's Chisel and Orb of Unmaking drop weights to retail 3.29 parity.
UPDATE drop_currency_weights
SET weight = 10.0, notes = 'Cartographer''s Chisel, 3.29 retail parity weight'
WHERE currency_id = 'Metadata/Items/Currency/CurrencyMapQuality';

UPDATE drop_currency_weights
SET weight = 16.0, notes = 'Orb of Unmaking, matched to Orb of Regret 3.29 retail parity weight'
WHERE currency_id = 'Metadata/Items/Currency/CurrencyAtlasPassiveRefund';

-- 6. Retunable Part 2 campaign map pacing and Divine Vessel drop settings (reloaded via /reload).
INSERT INTO system_settings (key, value) VALUES
-- Multiplier applied to map drop chance in campaign areas (mapTier == 0) for retail ~1-3 T1 maps across Acts 8-10 (legacy: 1.0).
('drop_campaign_map_chance_multiplier', 0.18),
-- Base per-kill Divine Vessel drop chance (%) for monsters at level 68+ or inside Atlas maps (0 disables).
('drop_divine_vessel_chance_pct', 0.08);
