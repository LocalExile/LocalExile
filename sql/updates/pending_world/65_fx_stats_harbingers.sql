-- "Grants Summon Harbinger of ... Skill": the Harbinger's own skill is used for the wearer on its cooldown while the
-- item is worn (Harbingers.cs). The minion is not made.
UPDATE fx_stats SET handled = 'code', ref = 'Harbingers.cs', why = NULL, fit = 'approximate'
WHERE stat = 'local_display_summon_harbinger_x_on_equip';
