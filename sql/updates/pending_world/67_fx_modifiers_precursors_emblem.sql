-- Precursor's Emblem's lines that are a plain stat while at maximum charges: conditional rows with counts_as.
INSERT INTO fx_modifiers (stat, ord, quantity, how, scale, conditions, target, note, counts_as) VALUES
('cannot_be_stunned_while_at_max_endurance_charges', 0, 'cannot_be_stunned', 'flat', 1, '[{"if":"state","is":"while_at_maximum_endurance_charges"}]', 'self', 'You cannot be Stunned while at maximum Endurance Charges', 'base_cannot_be_stunned'),
('gain_vaal_pact_while_at_maximum_endurance_charges', 0, 'keystone_vaal_pact', 'flag', 1, '[{"if":"state","is":"while_at_maximum_endurance_charges"}]', 'self', 'You have Vaal Pact while at maximum Endurance Charges', 'keystone_vaal_pact'),
('gain_mind_over_matter_while_at_maximum_power_charges', 0, 'keystone_mana_shield', 'flag', 1, '[{"if":"state","is":"while_at_maximum_power_charges"}]', 'self', 'You have Mind over Matter while at maximum Power Charges', 'keystone_mana_shield');
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_modifiers', why = NULL, fit = NULL
WHERE stat IN ('cannot_be_stunned_while_at_max_endurance_charges', 'gain_vaal_pact_while_at_maximum_endurance_charges',
  'gain_mind_over_matter_while_at_maximum_power_charges');
