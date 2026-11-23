-- Client table MicrotransactionLevelUpEffects: all 23 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "MicrotransactionLevelUpEffects" ("_row", "Id", "Animation", "EffectPack", "Stat", "MiscObject", "__col5", "Script", "__col7", "SoundEffect") VALUES
(0, 'MTXBlackStarRing', 6582, NULL, 16481, NULL, '', '', '', 1),
(1, 'MTXFieryLevelUp', 6543, NULL, 16375, NULL, '', '', '', NULL),
(2, 'MTXSkeletonLevelUp', NULL, 678, 19994, 1422, '', 'Delay( 1.5, {PlayEffect(''Metadata/Effects/Microtransactions/char_level_up/Supernova/supernova.ao'');});', '', NULL),
(3, 'MTXFlyingParadeTwilightLevelUp', NULL, NULL, 20796, NULL, '', 'Delay( 1.0, { DoRandom(' || char(13) || '
			{' || char(13) || '
				local parade = PlayEffect( ''Metadata/Effects/Microtransactions/char_level_up/FlyingParade/TwilightOrderHarpy1.ao'' );' || char(13) || '
			},' || char(13) || '
			{' || char(13) || '
				local parade = PlayEffect( ''Metadata/Effects/Microtransactions/char_level_up/FlyingParade/TwilightOrderHarpy2.ao'' );' || char(13) || '
			},' || char(13) || '
			{' || char(13) || '
				local parade = PlayEffect( ''Metadata/Effects/Microtransactions/char_level_up/FlyingParade/TwilightOrderHarpy3.ao'' );' || char(13) || '
			},' || char(13) || '
			{' || char(13) || '
				local parade = PlayEffect( ''Metadata/Effects/Microtransactions/char_level_up/FlyingParade/TwilightOrderHarpy4.ao'' );' || char(13) || '
			} );' || char(13) || '
} );			' || char(13) || '
PlayEffectAttached( ''Metadata/Effects/Microtransactions/char_level_up/FlyingParade/fx/ao/fireworks.ao'' );', '', NULL),
(4, 'MTXDeshretsBlessingLevelUp', NULL, 727, 21137, NULL, '', '', '', NULL),
(5, 'MTXValkyrieWingsLevelUp', NULL, 730, 21144, NULL, '', '', '', 1),
(6, 'MTXBloodyLevelUp', 7822, NULL, 21726, NULL, '', 'AddEffectPack( ''MicrotransactionBloodyLevelup'' );', '', NULL),
(7, 'MTXGildedLevelUp', 7823, NULL, 21727, NULL, '', 'Delay( 0.9, { ' || char(13) || '
				RemoveEffectPack( ''Metadata/Effects/Microtransactions/char_level_up/MidasTouch/gold/epk/level_up.epk'' ); ' || char(13) || '
				AddEffectPack( ''Metadata/Effects/Microtransactions/char_level_up/MidasTouch/gold/epk/level_up.epk'' ); ' || char(13) || '
			} );', '', NULL),
(8, 'MTXCrystalLevelUp', 7824, NULL, 21728, NULL, '', 'Delay( 0.5, { ' || char(13) || '
				RemoveEffectPack( ''Metadata/Effects/Microtransactions/char_level_up/MidasTouch/crystal/epk/crystal_lvlup.epk'' ); ' || char(13) || '
				AddEffectPack( ''Metadata/Effects/Microtransactions/char_level_up/MidasTouch/crystal/epk/crystal_lvlup.epk'' ); ' || char(13) || '
			} );', '', NULL),
(9, 'MTXMagicNatureLevelUp', NULL, NULL, 22180, NULL, '', 'PlayEffect( ''Metadata/Effects/Microtransactions/char_level_up/MagicNature/magicnature_lvlup.ao'');', '', NULL),
(10, 'MTXVerminSwarmLevelUp', NULL, 764, 22296, NULL, '', '', '', NULL),
(11, 'MTXTimeFreezeLevelUp', 7959, NULL, 22355, NULL, '', '', '', NULL),
(12, 'MTXBloodHowlLevelUp', NULL, NULL, 22411, NULL, '', 'PlayEffect( ''Metadata/Effects/Microtransactions/char_level_up/blood_howl/blood_howl_lvlup.ao'' );', '', 1),
(13, 'MTXPhoenixRebirthLevelUp', NULL, 788, 22499, NULL, '', 'DetachAllOfType( ''Metadata/Effects/Microtransactions/char_level_up/Phoenix/PhoenixWings.ao'' );' || char(13) || '
AddAttached( ''Metadata/Effects/Microtransactions/char_level_up/Phoenix/PhoenixWings.ao'', wings_attachment, 1.0, 0, false, true );', '', 1),
(14, 'MTXGhostLevelUp', 7988, 791, 22546, NULL, '', '', '', 1),
(15, 'MTXChronoLevelUp', NULL, 828, 23064, NULL, '', 'Delay( 1.4, { PlayTextAudio( ZarokhBoss_Godvoice_Sekhema_Random, ''Metadata/Monsters/FaridunLeague/Zarokh/Zarokh'' ); } );', '', 1),
(16, 'MTXKaruiAncestralLevelUp', NULL, 830, 23103, NULL, '', '', '', 1),
(17, 'MTXSacredTreeLevelUp', NULL, NULL, 23110, NULL, '', 'PlayEffect( ''Metadata/Effects/Microtransactions/char_level_up/SpiritWalker/spiritwalker_lvl_up.ao'' );', '', 1),
(18, 'MTXKingintheMistsLevelUp', NULL, 847, 23333, NULL, '', 'Delay( 1.6, { PlayEffect( ''Metadata/Effects/Microtransactions/char_level_up/KingInTheMists/kinginthemist_levelup.ao'' ); } );', '', 1),
(19, 'MTXVerisiumLevelUp', NULL, 850, 23345, 1829, '', '', '', 1),
(20, 'MTXNightfowlLevelUp', 8265, NULL, 23346, NULL, '', '', '', 1),
(21, 'MTXEdictoftheSpearLevelUp', NULL, 852, 23353, NULL, '', '', '', 1),
(22, 'MTXEdictofCorruptionLevelUp', 8271, 853, 23355, NULL, '', '', '', NULL);
