-- Main built these itself since they were marked; the branch's own versions were taken out at the merge.
UPDATE fx_stats SET ref = 'Progression (QuietMightKeystoneSpellDamagePlusPctFinalPerPowerCharge), ChargeSession.Apply'
WHERE stat = 'keystone_quiet_might';
UPDATE fx_stats SET ref = 'AilmentPanelStats'
WHERE stat = 'poison_damage_+%_with_over_300_dexterity';
