-- Two more single unique lines in code, and two that have nothing to do on the server.
UPDATE fx_stats SET handled = 'code', ref = 'CriticalStrikes.CriticalHitMultiplier', why = NULL, fit = 'approximate'
WHERE stat = 'critical_strikes_deal_no_damage';
UPDATE fx_stats SET handled = 'code', ref = 'MonsterAilments.AilmentsOnHitLocked', why = NULL, fit = NULL
WHERE stat = 'critical_strikes_do_not_always_apply_non_damaging_ailments';
-- There is no fishing here, and the monster category is drawn by the client from the stat itself.
UPDATE fx_stats SET handled = 'display', ref = 'no fishing on this server', why = NULL, fit = NULL
WHERE stat = 'can_catch_exotic_fish';
UPDATE fx_stats SET handled = 'display', ref = 'shown by the client', why = NULL, fit = NULL
WHERE stat = 'can_see_corpse_types';
