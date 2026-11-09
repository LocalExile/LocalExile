-- Client table MicrotransactionObjectEffects: all 83 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "MicrotransactionObjectEffects" ("_row", "Id", "__col1", "Script1", "Script2", "EffectPack", "MiscObject", "CorpseSinkVariation", "MiscAnimated", "__col8", "__col9", "__col10", "__col11", "__col12", "__col13", "__col14", "__col15", "__col16", "__col17", "__col18") VALUES
(0, 'MicrotransactionBreachHandOnKill', '', 'Delay( 1.75,' || char(13) || '
{' || char(13) || '
    AddEffectPack( "Metadata/Effects/Microtransactions/Misc/rare_unique_kill/breach/epks/corpse_dragged_down.epk" );' || char(13) || '
} );', 'Delay( 1.0,' || char(13) || '
{' || char(13) || '
  PlayEffect( "Metadata/Effects/Microtransactions/Misc/rare_unique_kill/breach/breachhole.ao", scale:0.65 );' || char(13) || '
} );', '[]', '[]', 4, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(1, 'MicrotransactionSkySwordOnKill', '', '', '', '[585]', '[1232]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(2, 'MicrotransactionBirdsOnRareKill', '', '', '', '[586]', '[1233]', 1, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(3, 'MicrotransactionMoltenLiquifyOnKill', '', 'SetCurrentAnimationSpeed(0.02);', '', '[592]', '[]', 5, '[7113]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(4, 'MicrotransactionBloodAndGoreOnKill', '', '', '', '[595]', '[]', 6, '[7112]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(5, 'RaijuScourgeLightningOnKill', '', '', '', '[]', '[]', NULL, '[7056,7056,7056,7056,7056]', 0, 0, 0, 1, 7, 0, '[]', 0, 0, 0, 0),
(6, 'MicrotransactionCordycepBowOnKill', '', '', '', '[603,604]', '[]', NULL, '[]', 0, 0, 0, 0, 0, 2, '[]', 0, 0, 0, 0),
(7, 'MicrotransactionStatueOnKill', '', '', '', '[]', '[]', NULL, '[7114]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(8, 'MicrotransactionAnchorOnKill', '', '', '', '[615]', '[]', NULL, '[7290]', 0, 0, 0, 0, 0, 0, '["frameupper"]', 0.2800000011920929, 0, 0, 0),
(9, 'MicrotransactionSharkAttackOnKill', '', 'Delay( 1.13,' || char(13) || '
{' || char(13) || '
    AddEffectPack( "Metadata/Effects/Microtransactions/Misc/rare_unique_kill/shark/epk/chomped_down.epk", 1.5 );' || char(13) || '
} );', '', '[]', '[]', NULL, '[7291]', 0, 0, 0, 0, 0, 0, '["frameupper"]', 1, 0, 0, 0),
(10, 'MicrotransactionTentacleConsumeOnKill', '', 'Delay( 1.9,' || char(13) || '
{' || char(13) || '
    AddEffectPack( "Metadata/Effects/Microtransactions/Misc/rare_unique_kill/tentacle/epk/chomped_down.epk", 1.5 );' || char(13) || '
} );', '', '[]', '[]', NULL, '[7292,7293,7294,7295,7296]', 0, 0, 0, 0, 0, 0, '["root"]', 1, 1, 0, 0),
(11, 'MicrotransactionDragonFireOnKill', '', 'Delay( 2.15,' || char(13) || '
{' || char(13) || '
    AddEffectPack( "Metadata/Effects/Microtransactions/on_kill/Dragon_Fire/epk/disintegrate_down.epk", 1.5 );' || char(13) || '
} );', '', '[]', '[1312]', NULL, '[]', 0, 0, 0, 0, 0, 0, '["frameupper"]', 2, 0, 0, 0),
(12, 'MicrotransactionBlackHoleOnKill', '', 'Delay(0.1, {this.black_hole = FindClosestObject( "Metadata/Effects/Microtransactions/Misc/uniques/BlackHole/blackhole" ); this.black_hole_position = black_hole.GetPositionValues(  );' || char(13) || '
		this.position = this.GetPositionValues(  );' || char(13) || '
		this.sopx = black_hole_position[0] - position[0];' || char(13) || '
		this.on_kill_black_hole_position += sopx;' || char(13) || '
		this.sopy = black_hole_position[1] - position[1];' || char(13) || '
		this.on_kill_black_hole_position += sopy;' || char(13) || '
		this.sopz = black_hole_position[2] - position[2];' || char(13) || '
		this.on_kill_black_hole_position += sopz; });', '', '[614]', '[1311]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(13, 'MicrotransactionVaalPalmHoleOnKill', '', '', '', '[629]', '[1334]', NULL, '[]', 0, 0, 0, 0, 0, 0, '["frameupper"]', 0.10000000149011612, 0, 0, 0),
(14, 'MicrotransactionCelestialEmperorOnKill', '', '', '', '[631]', '[1361]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 1750, 0),
(15, 'MicrotransactionBlackstarMeteorOnKill', '', '', '', '[641]', '[1348]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(16, 'MicrotransactionNecroticFlamesOnKill', '', '', '', '[633]', '[1347]', NULL, '[]', 0, 0, 0, 0, 0, 0, '["frameupper"]', 1, 0, 500, 0),
(17, 'MicrotransactionAesirZeusLightningOnKill', '', '', '', '[635]', '[1349]', NULL, '[]', 0, 0, 0, 0, 0, 0, '["frameupper"]', 2, 0, 1600, 0),
(18, 'MicrotransactionSulphiteShatterOnKill', '', '', '', '[647]', '[1362]', NULL, '[]', 0, 0, 0, 0, 0, 0, '["frameupper"]', 1, 0, 0, 0),
(19, 'MicrotransactionVoodooKingGlovesOnKillNormal', '', '', '', '[]', '[1409]', NULL, '[7491]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(20, 'MicrotransactionVoodooKingGlovesOnKillMagic', '', '', '', '[]', '[1409]', NULL, '[7491]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(21, 'MicrotransactionVoodooKingGlovesOnKillRare', '', '', '', '[]', '[1409,1409,1409,1409,1409,1409]', NULL, '[7491]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(22, 'MicrotransactionVoodooKingGlovesOnKillUnique', '', '', '', '[]', '[1409,1409,1409,1409,1409,1409,1409,1409,1409,1409,1409,1409]', NULL, '[7491]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(23, 'MicrotransactionSandwraithMastodon', '', '', '', '[]', '[1393]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(24, 'MicrotransactionSandwraithPlayerGEAL', '', 'this.bone_storm = GetAttachedAOByType( "Metadata/Effects/Microtransactions/BodyArmours/CoreMarakethAssassin/bone_tornado.ao" );' || char(13) || '
this.bone_storm.controller.SetStateTo( bones, 0 );', '', '[]', '[]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(25, 'NightLotusRareUniqueOnKill', '', '', '', '[664,669]', '[]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(26, 'LocustOnKillEffect', '', '', '', '[672]', '[]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(27, 'MicrotransactionChaosOrbFinisher', '', '', '', '[]', '[]', NULL, '[7523]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(28, 'MicrotransactionExaltedOrbFinisher', '', '', '', '[]', '[]', NULL, '[7524]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(29, 'MicrotransactionVaalOrbFinisher', '', '', '', '[]', '[]', NULL, '[7525]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(30, 'MicrotransactionMirrorFinisher', '', '', '', '[]', '[]', NULL, '[7526]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(31, 'MicrotransactionShaperSlamFinisher', '', '', '', '[]', '[1363]', NULL, '[]', 0, 0, 0, 0, 0, 0, '["frameupper"]', 1, 0, 0, 0),
(32, 'HeadhunterOnRareKill', '', '', 'IfExists( this.mtx_headhunter_controller, (){this.mtx_headhunter_controller.Call(rare_flare);}, ()' || char(13) || '
		{' || char(13) || '
			this.mtx_headhunter_controller = SpawnObject( "Metadata/Effects/Microtransactions/Character_FX/Headhunter/SkullController" );' || char(13) || '
this.mtx_headhunter_controller.AddToPlayerWakeGroup();' || char(13) || '
		globals.always_serialised += this.mtx_headhunter_controller;' || char(13) || '
			this.mtx_headhunter_controller.AttachToGameObject( this, root );' || char(13) || '
		} );' || char(13) || '
		this.mtx_headhunter_controller.skulls_to_spawn += other_party;', '[]', '[]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(33, 'DivineArtilleryStrikeRareOnKill', '', '', '', '[675]', '[]', NULL, '[7530]', 0, 0, 0, 0, 0, 0, '["frameupper"]', 3, 0, 0, 0),
(34, 'MicrotransactionIceShatterFinisher', '', '', '', '[679]', '[]', NULL, '[7546]', 0, 0, 0, 0, 0, 0, '["frameupper"]', 2, 0, 0, 0),
(35, 'MicrotransactionPillarOfFlameOnKill', '', 'Delay( 2.00,' || char(13) || '
{' || char(13) || '
    AddEffectPack( "Metadata/Effects/Microtransactions/on_kill/PillarOfFlame/epk/body_burnt_burst.epk", 4.0 );' || char(13) || '
} );' || char(13) || '
Delay( 4.00,' || char(13) || '
{' || char(13) || '
    AddEffectPack( "Metadata/Effects/Microtransactions/on_kill/PillarOfFlame/epk/disintegrate_down.epk");' || char(13) || '
    AddEffectPack( "Metadata/Effects/Microtransactions/on_kill/PillarOfFlame/epk/disintegrate_down_idle.epk");' || char(13) || '
} );', '', '[680]', '[1364]', NULL, '[]', 0, 0, 0, 0, 0, 0, '["frameupper"]', 1, 0, 1000, 0),
(36, 'MTXCrabArmourCrabsOnKill', '', '', '', '[]', '[1410,1411,1412,1413]', NULL, '[7554]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(37, 'MtxMultislashOnKill', '', '', '', '[705]', '[]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(38, 'MtxIonCannonOnKill', '', '', '', '[]', '[]', NULL, '[7556]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(39, 'MtxBloodCycloneOnKill', '', '', '', '[706]', '[]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(40, 'MtxKaruiElbowDrop', '', '', '', '[]', '[]', NULL, '[7557]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(41, 'MtxKaomGroundnPound', '', '', '', '[]', '[]', NULL, '[7558]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(42, 'MtxVerisiumKnight', '', '', '', '[]', '[1469]', NULL, '[]', 0, 0, 0, 0, 0, 0, '["root"]', 1.5, 0, 0, 0),
(43, 'MtxExplodingHeart', '', '', '', '[]', '[]', NULL, '[7562]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(44, 'MicrotransactionHokoRareFinisher', '', '', '', '[]', '[]', NULL, '[7663]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(45, 'MicrotransactionBreachClap', '', '', '', '[729]', '[]', NULL, '[7676]', 0, 0, 0, 0, 0, 0, '["<root>"]', 1.100000023841858, 0, 1100, 0),
(46, 'MicrotransactionBrambleGhastTentacle', '', '', '', '[734]', '[1579]', NULL, '[]', 0, 0, 0, 0, 0, 0, '["grp_chest"]', 0.25, 0, 1450, 0),
(47, 'MicrotransactionGuillotineExecutioner', '', '', '', '[]', '[]', NULL, '[7697]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(48, 'MicrotransactionSoulCoaster', '', '', '', '[733]', '[]', NULL, '[7699]', 0, 0, 0, 0, 0, 0, '["grp_chest"]', 1.25, 0, 1000, 0),
(49, 'MicrotransactionHusbandWife', '', '', '', '[731]', '[1577]', NULL, '[]', 0, 0, 0, 0, 0, 0, '["frameupper"]', 2.5, 0, 2100, 0),
(50, 'MicrotransactionQueenofFilth', '', 'RemoveEffectPack( ''Metadata/Effects/Spells/hit_and_blood/EPKs/corpseState_Blood.epk'' );', '', '[732]', '[1578]', NULL, '[]', 0, 0, 0, 0, 0, 0, '["frameupper"]', 1.5, 0, 1500, 0),
(51, 'MicrotransactionDeathSoulstealerFinisher', '', '', '', '[735]', '[1581]', NULL, '[]', 0, 0, 0, 0, 0, 0, '["frameupper"]', 0.8999999761581421, 0, 2950, 0),
(52, 'MicrotransactionZodiacDragon', '', '', '', '[]', '[]', NULL, '[7720]', 0, 0, 0, 0, 0, 0, '["frameupper"]', 2.5, 0, 0, 0),
(53, 'MicrotransactionPhrecianMagistrate', '', '', '', '[736]', '[]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(54, 'MicrotransactionCryptkeeper', '', '', '', '[743]', '[]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(55, 'MicrotransactionJusticeUniqueFinisher', '', 'Delay( 0.1, ' || char(13) || '
    {' || char(13) || '
        this.finisher = FindClosestObject( ''Metadata/Effects/Microtransactions/on_kill/GoddessOfJustice/GoddessJustice'' );' || char(13) || '
        If( this.finisher.CurrentAnimationIndex() == 0,' || char(13) || '
        {' || char(13) || '
			Delay( 4.075,' || char(13) || '
				{' || char(13) || '
					RemoveEffectPack( ''Metadata/Effects/Spells/hit_and_blood/EPKs/corpseState_Blood.epk'' );' || char(13) || '
					AddEffectPack( ''Metadata/Effects/Microtransactions/on_kill/GoddessOfJustice/epk/disintegrate_down.epk'' );' || char(13) || '
				} );' || char(13) || '
        },' || char(13) || '
        {' || char(13) || '
			Delay( 3.9,' || char(13) || '
				{' || char(13) || '
					AddEffectPack( ''Metadata/Effects/Microtransactions/on_kill/GoddessOfJustice/epk/corpse_soul.epk'' );' || char(13) || '
				} );' || char(13) || '
        } );' || char(13) || '
    } );', '', '[]', '[1616]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(56, 'MicrotransactionCrueltyFinisher', '', '', '', '[]', '[]', 8, '[7812]', 0, 0, 0, 0, 0, 0, '["frameupper"]', 0.75, 0, 0, 0),
(57, 'MicrotransactionKetzuli', '', '', '', '[748]', '[]', NULL, '[7817]', 0, 0, 0, 0, 0, 0, '["frameupper"]', 2, 0, 1650, 0),
(58, 'MicrotransactionDoryaniClampFinisher', '', 'Delay( 2.5, ' || char(13) || '
    {' || char(13) || '
		RemoveEffectPack( ''Metadata/Effects/Spells/hit_and_blood/EPKs/corpseState_Blood.epk'' );' || char(13) || '
    } );', '', '[749]', '[]', NULL, '[7818]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(59, 'MicrotransactionDoryaniDeviceFinisher', '', 'Delay( 1.985, ' || char(13) || '
    {' || char(13) || '
		RemoveEffectPack( ''Metadata/Effects/Spells/hit_and_blood/EPKs/corpseState_Blood.epk'' );' || char(13) || '
    } );', '', '[750]', '[]', NULL, '[7819]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 1500, 0),
(60, 'MicrotransactionDoryaniFlamethrowerFinisher', '', 'Delay( 0.5, ' || char(13) || '
    {' || char(13) || '
		RemoveEffectPack( ''Metadata/Effects/Spells/hit_and_blood/EPKs/corpseState_Blood.epk'' );' || char(13) || '
    } );', '', '[751]', '[]', NULL, '[7820]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 500, 0),
(61, 'MicrotransactionDoryaniLaserFinisher', '', '', '', '[752]', '[]', NULL, '[7821]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(62, 'MonarchOfTrathusLionFinisher', '', '', '', '[]', '[]', NULL, '[7894]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(63, 'HandOfXeshtFinisher', '', '', '', '[]', '[]', NULL, '[7933]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(64, 'MicrotransactionGhostlyCannons', '', '', '', '[]', '[]', NULL, '[7964]', 0, 0, 0, 0, 0, 0, '["grp_chest"]', 1, 0, 0, 0),
(65, 'PlummetingChandelierFinisher', '', '', '', '[]', '[]', NULL, '[7975]', 0, 0, 0, 0, 0, 0, '["frameupper"]', 0.5, 0, 0, 0),
(66, 'PhoenixFireFinisher', '', '', '', '[787]', '[]', NULL, '[7979]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 2100, 0),
(67, 'GiantCentipedeFinisher', '', '', '', '[]', '[]', NULL, '[7980]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(68, 'TheEndFinisherEffect', '', '', '', '[]', '[1711]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(69, 'BackttheScrollFinisher', '', '', '', '[]', '[1710]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(70, 'StygianFinisher', '', '', '', '[803]', '[]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(71, 'BlackholeFreezeFinisher', '', '', '', '[]', '[1713]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(72, 'NightLotusFinisherEffect', '', '', '', '[815]', '[1761]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(73, 'AbyssalCrystalFinisherEffect', '', '', '', '[833]', '[]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(74, 'SwordSlashFinisherEffect', '', '', '', '[]', '[1802]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(75, 'TimeRewindFinisherEffect', '', '', '', '[834]', '[1826]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(76, 'SharkAttackFinisherEffect', '', '', '', '[]', '[]', NULL, '[8225]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 210, 0),
(77, 'KaruiDeepwaterFinisherEffect', '', '', '', '[]', '[]', NULL, '[8258]', 0, 0, 0, 0, 0, 0, '["grp_chest"]', 0, 0, 0, 1000),
(78, 'PustuleExplodeFinisherEffect', '', 'this.AddEffectPack( ''Metadata/Effects/Microtransactions/on_kill/PustuleExplode/epk/corpse_fx.epk'' );', '', '[837]', '[]', NULL, '[8239]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(79, 'BugSwarmFinisherEffect', '', '', '', '[849]', '[]', NULL, '[]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 500),
(80, 'EdictOfAshFinisherEffect', '', '', '', '[851]', '[1830]', NULL, '[]', 0, 0, 0, 0, 0, 0, '["grp_chest"]', 0, 0, 2150, 800),
(81, 'CollossalTentacleFinisherEffect', '', '', '', '[]', '[]', NULL, '[8270]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0),
(82, 'EdictOfTheSpearFinisherEffect', '', '', '', '[]', '[]', NULL, '[8272]', 0, 0, 0, 0, 0, 0, '[]', 0, 0, 0, 0);
