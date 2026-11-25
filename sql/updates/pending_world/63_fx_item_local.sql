-- fx_item_local: a stat that belongs to its item (Stats.dat IsLocal) and so never reaches the character's sum, counted
-- as a character stat while the item is worn in a slot. slot: any, left_ring, right_ring.
CREATE TABLE IF NOT EXISTS fx_item_local (
  stat      TEXT NOT NULL,
  ord       INTEGER NOT NULL DEFAULT 0,
  slot      TEXT NOT NULL DEFAULT 'any',
  counts_as TEXT NOT NULL,
  scale     REAL NOT NULL DEFAULT 1,
  enabled   INTEGER NOT NULL DEFAULT 1,
  checked   INTEGER NOT NULL DEFAULT 0,
  note      TEXT,
  PRIMARY KEY (stat, ord)
);
INSERT INTO fx_item_local (stat, ord, slot, counts_as, scale, note) VALUES
('local_left_ring_slot_energy_shield', 0, 'left_ring', 'base_maximum_energy_shield', 1, 'Shavronne''s Revelation. Left ring slot: +# to maximum Energy Shield'),
('local_left_ring_slot_mana_regeneration_rate_per_minute', 0, 'left_ring', 'base_mana_regeneration_rate_per_minute', 1, 'Left ring slot: Regenerate # Mana per Second'),
('local_left_ring_slot_no_energy_shield_recharge_or_regeneration', 0, 'left_ring', 'no_energy_shield_recharge_or_regeneration', 1, 'Left ring slot: You cannot Recharge or Regenerate Energy Shield'),
('local_right_ring_slot_base_energy_shield_regeneration_rate_per_minute_%', 0, 'right_ring', 'base_energy_shield_regeneration_rate_per_minute_%', 1, 'Right ring slot: Regenerate #% of Energy Shield per second'),
('local_right_ring_slot_maximum_mana', 0, 'right_ring', 'base_maximum_mana', 1, 'Right ring slot: +# to maximum Mana'),
('local_right_ring_slot_no_mana_regeneration', 0, 'right_ring', 'no_mana_regeneration', 1, 'Right ring slot: You cannot Regenerate Mana'),
('local_ring_impaled_debuff_duration_+%_final', 0, 'any', 'impaled_debuff_duration_+%', 1, 'Woespike. A "less" counted with the increases: the same number unless another line changes Impale duration');
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_item_local', why = NULL, fit = NULL
WHERE stat IN ('local_left_ring_slot_energy_shield', 'local_left_ring_slot_mana_regeneration_rate_per_minute',
  'local_left_ring_slot_no_energy_shield_recharge_or_regeneration',
  'local_right_ring_slot_base_energy_shield_regeneration_rate_per_minute_%', 'local_right_ring_slot_maximum_mana',
  'local_right_ring_slot_no_mana_regeneration');
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_item_local', why = NULL, fit = 'approximate'
WHERE stat = 'local_ring_impaled_debuff_duration_+%_final';
-- A cluster jewel's "1 Added Passive Skill is <notable>" lines (Megalomaniac's too) are carried out by the cluster
-- jewel layout (ClusterJewelLayout, ClusterJewelStats): the notable is added and grants its own row's stats.
UPDATE fx_stats SET handled = 'item', ref = 'ClusterJewelStats', why = NULL, fit = NULL
WHERE handled = 'none' AND stat LIKE 'local_affliction_notable_%';
