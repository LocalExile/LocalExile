-- Kineticism (The Siege): attack projectiles always bleed, maim and knock back; projectiles cannot pierce, fork or chain.
UPDATE fx_stats SET handled = 'code', ref = 'GatewayProjectileModifiers, MonsterBleeds.BleedSettings, AilmentsOnHitLocked', why = NULL, fit = NULL
WHERE stat = 'keystone_barbed_tips';
UPDATE fx_stats SET handled = 'item', ref = 'ClusterJewelStats.Keystone', why = NULL, fit = NULL
WHERE stat = 'local_jewel_expansion_keystone_kineticism';
