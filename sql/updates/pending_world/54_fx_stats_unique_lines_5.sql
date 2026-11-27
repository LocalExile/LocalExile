-- Three more single unique lines.
UPDATE fx_stats SET handled = 'code', ref = 'Progression (SlotArmourBonus)', why = NULL, fit = NULL
WHERE stat = 'armour_from_shield_doubled';
UPDATE fx_stats SET handled = 'code', ref = 'CriticalStrikes.CriticalHitMultiplier', why = NULL, fit = 'approximate'
WHERE stat = 'critical_strike_multiplier_is_300';
-- No monster leeches mana from a player on this server, so there is nothing for the line to stop.
UPDATE fx_stats SET handled = 'display', ref = 'nothing leeches mana from players', why = NULL, fit = NULL
WHERE stat = 'cannot_have_mana_leeched_from';
