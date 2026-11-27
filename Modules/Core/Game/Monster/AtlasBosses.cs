using System.Runtime.CompilerServices;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;

/// <summary>
/// The Atlas pinnacle and memory bosses: Shaper, Elder, Sirus, Eldritch minibosses &amp; pinnacles (Infinite Hunger,
/// Black Star, Eater of Worlds, Searing Exarch), Maven &amp; Nucleus, and Secrets of the Atlas Originator/Memory
/// pinnacles &amp; minibosses (Incarnations of Neglect/Fear/Dread and their three Fragment minibosses).
/// <para>Skill ids: disjoint 0x4000+ ranges per boss family (never the player's gem range 0x0103..0x0142) so
/// multi-boss arenas (Uber Elder, Maven + Nucleus) never collide on skill ids.</para>
/// <para>Art: every .ao path below was verified against the client's metadata (data/game-files.zip).</para>
/// </summary>
static class AtlasBosses
{
    internal const string Shaper = "Metadata/Monsters/AtlasBosses/TheShaperBoss";
    // The Shaper as the Uber Elder encounter brings him (MapWorldsElderArenaUber): standing beside TheElderUber.
    internal const string ShaperUberElder = "Metadata/Monsters/AtlasBosses/TheShaperBossUberElder";
    internal const string Elder = "Metadata/Monsters/AtlasBosses/TheElder/TheElder";
    internal const string ElderUber = "Metadata/Monsters/AtlasBosses/TheElder/TheElderUber";
    internal const string Sirus = "Metadata/Monsters/AtlasExiles/AtlasExile5";
    internal const string SirusThrone = "Metadata/Monsters/AtlasExiles/AtlasExile5Throne";
    internal const string SirusGauntlet = "Metadata/Monsters/AtlasExiles/AtlasExile5Gauntlet";
    internal const string SirusStandalone = "Metadata/Monsters/AtlasExiles/AtlasExile5Standalone";
    internal const string SirusStandaloneMirage = "Metadata/Monsters/AtlasExiles/AtlasExile5StandaloneMirage";

    // Eldritch minibosses & pinnacles (MapWorldsPrimordialBoss1..4).
    internal const string InfiniteHunger = "Metadata/Monsters/AtlasInvaders/DoomMonsters/DoomBoss_";
    internal const string BlackStar = "Metadata/Monsters/AtlasInvaders/BlackStarMonsters/BlackStarBoss";
    internal const string BlackStarCasterClone = "Metadata/Monsters/AtlasInvaders/BlackStarMonsters/BlackStarBossCasterClone";
    internal const string BlackStarMeleeClone = "Metadata/Monsters/AtlasInvaders/BlackStarMonsters/BlackStarBossMeleeClone";
    internal const string EaterOfWorlds = "Metadata/Monsters/AtlasInvaders/ConsumeMonsters/ConsumeBoss";
    internal const string EaterOfWorldsGauntlet = "Metadata/Monsters/AtlasInvaders/ConsumeMonsters/ConsumeBossGauntlet";
    internal const string SearingExarch = "Metadata/Monsters/AtlasInvaders/CleansingMonsters/CleansingBoss";
    internal const string SearingExarchGauntlet = "Metadata/Monsters/AtlasInvaders/CleansingMonsters/CleansingBossGauntlet";

    // The Maven & Nucleus (MavenBoss).
    internal const string Maven = "Metadata/Monsters/MavenBoss/TheMaven";
    internal const string MavenEnraged = "Metadata/Monsters/MavenBoss/TheMavenEnraged_";
    internal const string MavenEnragedGauntlet = "Metadata/Monsters/MavenBoss/TheMavenEnragedGauntlet";
    internal const string MavenBrain = "Metadata/Monsters/MavenBoss/MavenBrainBoss";
    internal const string MavenBrainGauntlet = "Metadata/Monsters/MavenBoss/MavenBrainBossGauntlet_";

    // Originator / Memory Pinnacles & Memory Thread Minibosses (Secrets of the Atlas 3.26-3.29).
    internal const string NeglectBoss = "Metadata/Monsters/AtlasMemory/IgnoranceBoss/IgnoranceBoss";
    internal const string NeglectBossUber = "Metadata/Monsters/AtlasMemory/IgnoranceBoss/IgnoranceBossUBER";
    internal const string NeglectBossGauntlet = "Metadata/Monsters/AtlasMemory/IgnoranceBoss/IgnoranceBossGAUNTLET_";
    internal const string NeglectBossUberGauntlet = "Metadata/Monsters/AtlasMemory/IgnoranceBoss/IgnoranceBossUBERGAUNTLET";
    internal const string FearBoss = "Metadata/Monsters/AtlasMemory/AngerBoss";
    internal const string FearBossUber = "Metadata/Monsters/AtlasMemory/AngerBossUBER";
    internal const string FearBossGauntlet = "Metadata/Monsters/AtlasMemory/AngerBossGAUNTLET";
    internal const string FearBossUberGauntlet = "Metadata/Monsters/AtlasMemory/AngerBossUBERGAUNTLET_";
    internal const string DreadBoss = "Metadata/Monsters/AtlasMemory/BenevolenceBoss";
    internal const string DreadBossUber = "Metadata/Monsters/AtlasMemory/BenevolenceBossUBER";
    internal const string DreadBossGauntlet = "Metadata/Monsters/AtlasMemory/BenevolenceBossGAUNTLET";
    internal const string DreadBossUberGauntlet = "Metadata/Monsters/AtlasMemory/BenevolenceBossGAUNTLETUBER";
    internal const string FragmentOfNeglect = "Metadata/Monsters/AtlasMemory/FragmentOfIgnorance/FragmentOfIgnorance__";
    internal const string FragmentOfNeglectStandalone = "Metadata/Monsters/AtlasMemory/FragmentOfIgnorance/FragmentOfIgnoranceSTANDALONE";
    internal const string FragmentOfFear = "Metadata/Monsters/AtlasMemory/FragmentOfAnger";
    internal const string FragmentOfFearStandalone = "Metadata/Monsters/AtlasMemory/FragmentOfAngerSTANDALONE";
    internal const string FragmentOfDread = "Metadata/Monsters/AtlasMemory/FragmentOfBenevolence";
    internal const string FragmentOfDreadStandalone = "Metadata/Monsters/AtlasMemory/FragmentOfBenevolenceSTANDALONE_";

    // Zana arena ally varieties (Shaper / Elder / Uber Elder).
    internal const string ZanaShaperVariety = "Metadata/Monsters/AtlasBosses/Zana";
    internal const string ZanaElderVariety = "Metadata/Monsters/AtlasBosses/ZanaElder";
    internal const string ZanaUberElderVariety = "Metadata/Monsters/AtlasBosses/ZanaUberElder";

    // GrantedEffects rows (verified against MonsterVarieties.GrantedEffectsKeys in data/client.db).
    internal const int ShaperMeleeEffect = 481;
    internal const int CelestialBeamEffect = 6093;
    internal const int AcceleratingProjectilesEffect = 6106;
    internal const int FlickerSlamEffect = 6091;
    internal const int RetreatEffect = 6089;
    internal const int BulletHellEffect = 6111;
    internal const int ElderMeleeEffect = 406;
    internal const int SiphonEffect = 9058;
    internal const int SlammingTentaclesEffect = 9062;
    internal const int ExpandingNovaEffect = 9066;
    internal const int DoomProjectileEffect = 9351;
    internal const int ProjectileSpellcastEffect = 9330;
    internal const int SirusBeamBlastEffect = 7008;
    internal const int SirusCorridorBlastEffect = 10948;
    internal const int SirusCircleMazeEffect = 10949;
    internal const int SirusCircleMaze2Effect = 10950;
    internal const int SirusCircleMaze3Effect = 10951;
    internal const int SirusCircleMaze4Effect = 10952;
    internal const int SirusCircleMaze5Effect = 10953;
    internal const int SirusMultiBeamEffect = 8098;
    internal const int SirusMultiBeam2Effect = 8099;
    internal const int SirusCloneBeamEffect = 7031;
    internal const int SirusMeteorEffect = 10141;
    internal const int SirusMeteorGroundEffect = 10945;
    internal const int SirusBeamBombEffect = 10973;

    // Eldritch GrantedEffects.
    internal const int UniqueMeleeEffect = 420;
    internal const int FireUniqueMeleeEffect = 422;
    internal const int BossMeleeEffect = 419;

    internal const int HungerSlamCastEffect = 8252;
    internal const int HungerSlamEffect = 7373;
    internal const int HungerVomitChannelEffect = 7374;
    internal const int HungerVomitMortarEffect = 4564;
    internal const int HungerDrillChargeEffect = 4353;
    internal const int HungerStompEffect = 7386;
    internal const int HungerSumoSlamEffect = 7388;
    internal const int HungerGooEruptionEffect = 7393;

    internal const int BlackStarSpawnCasterEffect = 8248;
    internal const int BlackStarSpawnMeleeEffect = 8249;
    internal const int BlackStarFireLeapEffect = 10549;
    internal const int BlackStarFireLeapImpactEffect = 7364;
    internal const int BlackStarColdCascadeEffect = 7365;
    internal const int BlackStarCometBarrageEffect = 12185;
    internal const int BlackStarCometImpactEffect = 7379;
    internal const int BlackStarLaserSpearsEffect = 12186;

    internal const int EaterDashEffect = 10554;
    internal const int EaterDashSlamCastEffect = 10555;
    internal const int EaterDisintegrateBeamEffect = 7381;
    internal const int EaterOmegaTentaclesEffect = 8269;
    internal const int EaterBonkedEffect = 8257;
    internal const int EaterDashSlamEffect = 7383;
    internal const int EaterSlamEffect = 7392;
    internal const int EaterDoomLaserEffect = 12193;
    internal const int EaterTentacleSlamEffect = 7400;
    internal const int EaterUltimateNukeEffect = 7402;

    internal const int ExarchBugBarrageCastEffect = 8250;
    internal const int ExarchMurderLanternCastEffect = 8251;
    internal const int ExarchDashSlashCastEffect = 10553;
    internal const int ExarchFireWallEffect = 12177;
    internal const int ExarchSummonFlameWallEffect = 12182;
    internal const int ExarchPushFlameWallEffect = 12183;
    internal const int ExarchBugMortarEffect = 4563;
    internal const int ExarchLanternExplosionEffect = 7396;
    internal const int ExarchDashSlashEffect = 7372;

    // Maven & Nucleus GrantedEffects.
    internal const int MavenProjectileEffect = 11748;
    internal const int MavenLaserBarrageEffect = 11739;
    internal const int MavenMemoryGameEffect = 11736;
    internal const int MavenCannonCastEffect = 11740;
    internal const int MavenCircleSlamCastEffect = 11741;
    internal const int MavenCircleSlamEffect = 11725;
    internal const int MavenColdCannonEffect = 11749;
    internal const int MavenSuperFireEffect = 11750;
    internal const int MavenSuperFireImpactEffect = 11751;
    internal const int MavenCollapseVolatilesEffect = 11761;

    internal const int BrainChargeBeamEffect = 11723;
    internal const int BrainIonCannonEffect = 11724;
    internal const int BrainBeamBlastEffect = 11726;
    internal const int BrainProjBlastCastEffect = 11727;
    internal const int BrainLargeBarrageCastEffect = 11728;
    internal const int BrainProjBlastEffect = 11730;
    internal const int BrainLargeBarrageEffect = 11731;
    internal const int BrainSmallBarrageEffect = 11732;
    internal const int BrainDecelProjEffect = 11752;

    // Originator / Memory Pinnacles & Minibosses GrantedEffects.
    internal const int NeglectSpawnLasersEffect = 8349;
    internal const int NeglectAccelProjEffect = 12572;
    internal const int NeglectIceNovaSlamEffect = 7635;
    internal const int NeglectBigBlastEffect = 10469;
    internal const int NeglectBeamDamageEffect = 7634;
    internal const int NeglectLaserBeamEffect = 7636;
    internal const int NeglectLaserBeam2Effect = 7637;

    internal const int FearGroundSlamEffect = 7628;
    internal const int FearBurningSlamEffect = 7629;
    internal const int FearStompEffect = 7630;
    internal const int FearSolarNovaEffect = 7631;
    internal const int FearFissureProjEffect = 12647;
    internal const int FearFissureSlamEffect = 12648;
    internal const int FearLeapEffect = 10577;
    internal const int FearLeapAttackEffect = 7644;
    internal const int FearGigaBeamEffect = 12663;
    internal const int FearBeamBarrageEffect = 4587;
    internal const int FearOrbShatterEffect = 7662;
    internal const int FearBigStompEffect = 7663;

    internal const int DreadCascadeSlamEffect = 7627;
    internal const int DreadBulletHellStartEffect = 8348;
    internal const int DreadFusionBlastEffect = 7632;
    internal const int DreadCentipedeEffect = 3909;
    internal const int DreadDoubleFrontSlamEffect = 7638;
    internal const int DreadEyebeamEffect = 7640;
    internal const int DreadLightningProjEffect = 3904;
    internal const int DreadCelestialBeamEffect = 12649;
    internal const int DreadTentacleWaveEffect = 7643;
    internal const int DreadFireProjEffect = 3916;
    internal const int DreadColdProjEffect = 3918;
    internal const int DreadChaosProjEffect = 3920;
    internal const int DreadShrinkingNovaEffect = 7649;
    internal const int DreadEyebeamIceEffect = 7650;
    internal const int DreadTeleSlamEffect = 7661;

    internal const int FragNeglectBeamBlastEffect = 12591;
    internal const int FragNeglectCorridorEffect = 12593;
    internal const int FragNeglectCircleMazeEffect = 12594;
    internal const int FragNeglectCloneBeamEffect = 12610;
    internal const int FragNeglectBeamBombEffect = 12618;
    internal const int FragNeglectMeteorEffect = 12620;
    internal const int FragNeglectRainOfStarsEffect = 12625;
    internal const int FragNeglectDeathProjEffect = 3911;

    internal const int FragFearShieldShardsEffect = 12627;
    internal const int FragFearHolyBeamEffect = 12629;
    internal const int FragFearShatterBeamEffect = 12630;
    internal const int FragFearDecelProjEffect = 12633;
    internal const int FragFearMassBeamEffect = 12634;
    internal const int FragFearDashEffect = 12636;
    internal const int FragFearSlamEffect = 12637;
    internal const int FragFearNetMortarEffect = 12642;
    internal const int FragFearUltimateEffect = 7659;
    internal const int FragFearExplosiveArrowEffect = 12694;

    internal const int FragDreadSuperProjEffect = 6296;
    internal const int FragDreadGreaterBlastEffect = 12590;
    internal const int FragDreadDelayedBlastEffect = 12579;
    internal const int FragDreadDivineChargeEffect = 12584;
    internal const int FragDreadBallLightningEffect = 12585;
    internal const int FragDreadOffsetProjEffect = 12586;
    internal const int FragDreadDivineCallEffect = 12589;
    internal const int FragDreadDivineBeamEffect = 12626;
    internal const int FragDreadKillZoneEffect = 7639;

    // ActionTypes HASH16 & 0x3fff: the basic Move.
    internal const ushort MoveSkill = 0x2909;

    // Verified BuffDefinitions rows (data/client.db).
    internal const ushort ShaperImmunityRow = 1028, ElderImmunityRow = 1027;
    internal const ushort SirusImmunityRow = 1464;
    internal const ushort ZanaBubbleBuffRow = 1219; // zana_player_bubble ("Zana's Protection")
    internal const ushort MavenStasisBuffRow = 1696; // maven_boss_stasis
    internal const ushort MavenMemoryGameDegenRow = 1784; // maven_memory_game_degen
    internal const ushort MavenPunishmentRow = 1785; // maven_rotating_beam_debuff ("Maven's Punishment")
    internal const ushort BlackStarSunlightRow = 1989; // blackstar_sunlight ("Annihilating Light")
    internal const ushort BlackStarMoonlightRow = 1990; // blackstar_moonlight ("Crushing Darkness")
    internal const ushort HungerDrowningRow = 2028; // infinite_hunger_drowning_debuff ("Drowning")
    internal const ushort EaterLivewireRow = 2037; // tangle_tentacle_orb_zapped ("Livewire")
    internal const ushort EaterDrowningRow = 2043; // tangle_drowning ("Drowning")
    internal const ushort ExarchSearingRuneRow = 2056; // cleansing_fire_snake_debuff ("Searing Rune")
    internal const ushort ExarchCauterisedRow = 2063; // cleansing_fire_snake_secondary_debuff ("Cauterised Flesh")
    internal const ushort EaterLaserRow = 2067; // eater_of_worlds_laser ("Inescapable Doom")
    internal const ushort DreadTraumaRow = 2555; // benevolenceboss_collectable1_debuff
    internal const ushort NeglectBeamDebuffRow = 2615; // ignorance_beam_debuff
    internal const ushort FearBeamDebuffRow = 2620; // anger_boss_beam ("Anger's Intensity")

    const string ShaperArt = "Metadata/Effects/Spells/monsters_effects/atlasofworldsbosses/theshaper/";
    const string ElderArt = "Metadata/Effects/Spells/monsters_effects/atlasofworldsbosses/elder/";
    internal const string CelestialBeamArt = ShaperArt + "celestialbeam/rig.ao";
    internal const string ShaperShieldArt = ShaperArt + "immune_shield/immune_shield.ao";
    internal const string ShaperShieldOffArt = ShaperArt + "immune_shield/immune_shield_off.ao";
    internal const string FlickerSlamAppearArt = ShaperArt + "teleport_slam/teleport_appear.ao";
    internal const string FlickerSlamDisappearArt = ShaperArt + "teleport_slam/teleport_disappear.ao";
    internal const string FlickerSlamArt = ShaperArt + "teleport_slam/teleport_slam.ao";
    internal const string ShaperTeleportArt = ShaperArt + "instant_teleport/instant_teleport.ao";
    internal const string BulletHellCastArt = ShaperArt + "bullet_hell/cast/bullet_hell_cast.ao";
    internal const string ElderShieldArt = ElderArt + "immune_shield/immune_shield.ao";
    internal const string ElderShieldOffArt = ElderArt + "immune_shield/immune_shield_off.ao";
    internal const string ElderSiphonArt = ElderArt + "siphon/siphon_cone.ao";
    internal const string ElderNovaArt = ElderArt + "nova/nova.ao";
    internal const string ElderDoomArt = ElderArt + "doom/on_act/on_act.ao";
    internal const string ElderDoomHitArt = ElderArt + "doom/doom_hit/doomhit.ao";
    internal const string ElderTentaclePortalArt = ElderArt + "tentacle_portal/tentacle_portal.ao";
    internal const string ElderTentacleSlamArt = ElderArt + "tentacle_portal/slam_impact.ao";
    internal const string ElderTeleportPlayerArt = ElderArt + "teleport/player/warpslam.ao";
    internal const string ZanaBubbleArt = "Metadata/Effects/Spells/masters/strdexint/zanasupershield.ao";
    internal const string ZanaElderChannelArt = "Metadata/Effects/Environment/AOW/Elder/ZanaChannel/rig.ao";

    const string SirusArt = "Metadata/Effects/Spells/monsters_effects/atlasexiles/orion/";
    internal const string SirusBeamBlastArt = SirusArt + "spam_beam.ao";
    internal const string SirusCorridorArt = SirusArt + "corridor_nuke.ao";
    internal const string SirusNukeBeamArt = SirusArt + "nuke_beam_blast.ao";
    internal const string SirusCarpetArt = SirusArt + "death_carpeting.ao";
    internal const string SirusTripleBeamArt = SirusArt + "triplebeam/triple_beam_01.ao";
    internal const string SirusTripleBeam2Art = SirusArt + "triplebeam/triple_beam_02.ao";
    internal const string SirusMeteorArt = SirusArt + "meteor/meteor_150.ao";
    internal const string SirusMeteorImpactArt = SirusArt + "destruction_assets/meteors/impact_medium.ao";
    internal const string SirusMeteorGroundArt = SirusArt + "destruction_assets/meteors/meteor_ground.ao";
    internal const string SirusShieldArt = SirusArt + "immunity_shield.ao";
    internal const string SirusShieldOffArt = SirusArt + "warp_down.ao";
    internal const string SirusTeleportArt = SirusArt + "teleporting/orion_teleport.ao";
    internal const string SirusDoomGeyserArt = SirusArt + "doom_geyser.ao";

    // Projectiles.dat rows (verified against data/client.db Projectiles table).
    internal static SkillCatalog.MonsterProjectileInfo BulletHellShot { get; } =
        new("Metadata/Projectiles/ShaperBulletHellProjectile", 375, false, 3) { Count = 12, SpreadRadians = Math.Tau };
    internal static SkillCatalog.MonsterProjectileInfo DoomShot { get; } =
        new("Metadata/Projectiles/ElderProjectileDoom", 600, false, 2);
    internal static SkillCatalog.MonsterProjectileInfo BlackStarCometShot { get; } =
        new("Metadata/Projectiles/BlackStarBossComet", 650, false, 4) { Count = 3, SpreadRadians = 0.55 };
    internal static SkillCatalog.MonsterProjectileInfo ExarchFireWallShot { get; } =
        new("Metadata/Projectiles/CleansingFireWall", 400, false, 4) { Count = 5, SpreadRadians = 0.7 };
    internal static SkillCatalog.MonsterProjectileInfo MavenBasicShot { get; } =
        new("Metadata/Projectiles/MavenProjectile", 2250, false, 3) { Count = 3, SpreadRadians = 0.35 };
    internal static SkillCatalog.MonsterProjectileInfo MavenColdShot { get; } =
        new("Metadata/Projectiles/MavenCannonColdProjectile", 1500, false, 4);
    internal static SkillCatalog.MonsterProjectileInfo MavenSuperFireShot { get; } =
        new("Metadata/Projectiles/MavenSuperFireProjectile", 750, false, 4);
    internal static SkillCatalog.MonsterProjectileInfo BrainBlastShot { get; } =
        new("Metadata/Projectiles/MavenBrainProjectileBlast", 750, false, 2) { Count = 5, SpreadRadians = 0.7 };
    internal static SkillCatalog.MonsterProjectileInfo BrainLargeShot { get; } =
        new("Metadata/Projectiles/MavenBrainLargeProjectileBarrage", 300, false, 3) { Count = 8, SpreadRadians = Math.Tau };
    internal static SkillCatalog.MonsterProjectileInfo BrainSmallShot { get; } =
        new("Metadata/Projectiles/MavenBrainSmallProjectileBarrage", 750, false, 3) { Count = 6, SpreadRadians = 1.1 };
    internal static SkillCatalog.MonsterProjectileInfo BrainDecelShot { get; } =
        new("Metadata/Projectiles/MavenBrainDeceleratingProjectile", 750, false, 3) { Count = 4, SpreadRadians = 0.8 };
    internal static SkillCatalog.MonsterProjectileInfo NeglectReflectedShot { get; } =
        new("Metadata/Projectiles/IgnoranceUberReflectedLaserProjectileCold", 2000, false, 2) { Count = 5, SpreadRadians = 0.65 };
    internal static SkillCatalog.MonsterProjectileInfo FearSpearShot { get; } =
        new("Metadata/Projectiles/ZAngerOrbSpear", 1800, false, 4) { Count = 3, SpreadRadians = 0.45 };
    internal static SkillCatalog.MonsterProjectileInfo DreadCentipedeShot { get; } =
        new("Metadata/Projectiles/BenevolenceBossCentipedeProjectile", 750, false, 2) { Count = 6, SpreadRadians = 1.0 };
    internal static SkillCatalog.MonsterProjectileInfo DreadLightningShot { get; } =
        new("Metadata/Projectiles/BenevolenceBossLightningProjectile", 600, false, 2) { Count = 5, SpreadRadians = 0.8 };
    internal static SkillCatalog.MonsterProjectileInfo DreadFireShot { get; } =
        new("Metadata/Projectiles/BenevolenceBossFireBullet", 210, false, 2) { Count = 8, SpreadRadians = Math.Tau };
    internal static SkillCatalog.MonsterProjectileInfo DreadColdShot { get; } =
        new("Metadata/Projectiles/BenevolenceBossColdProjectile", 600, false, 3) { Count = 5, SpreadRadians = 0.8 };
    internal static SkillCatalog.MonsterProjectileInfo DreadChaosShot { get; } =
        new("Metadata/Projectiles/BenevolenceBossChaosProjectile", 200, false, 4) { Count = 6, SpreadRadians = Math.Tau };
    internal static SkillCatalog.MonsterProjectileInfo FragNeglectDeathShot { get; } =
        new("Metadata/Projectiles/FragmentOfIgnoranceDeathProjectile", 800, false, 2) { Count = 5, SpreadRadians = 0.75 };
    internal static SkillCatalog.MonsterProjectileInfo FragFearShardsShot { get; } =
        new("Metadata/Projectiles/FragmentOfAngerShards", 800, false, 2) { Count = 5, SpreadRadians = 0.7 };
    internal static SkillCatalog.MonsterProjectileInfo FragFearArrowShot { get; } =
        new("Metadata/Projectiles/FragmentOfAngerExplosiveArrow", 1000, false, 2) { Count = 3, SpreadRadians = 0.4 };
    internal static SkillCatalog.MonsterProjectileInfo FragDreadProjShot { get; } =
        new("Metadata/Projectiles/FragmentOfBenevolenceProj", 800, false, 2) { Count = 6, SpreadRadians = 0.9 };
    internal static SkillCatalog.MonsterProjectileInfo FragDreadSuperShot { get; } =
        new("Metadata/Projectiles/BenevolenceSuperProjectile", 180, false, 2) { Count = 10, SpreadRadians = Math.Tau };

    internal static bool IsShaper(MonsterCatalog.Variety? variety) => variety?.Id is Shaper or ShaperUberElder;
    internal static bool IsElder(MonsterCatalog.Variety? variety) => variety?.Id is Elder or ElderUber;
    internal static bool IsSirus(MonsterCatalog.Variety? variety) => variety?.Id is Sirus or SirusThrone or SirusGauntlet
        or SirusStandalone or SirusStandaloneMirage;
    internal static bool IsSirusSeated(MonsterCatalog.Variety? variety) => variety?.Id is SirusThrone;
    internal static bool IsInfiniteHunger(MonsterCatalog.Variety? variety) => variety?.Id is InfiniteHunger;
    internal static bool IsBlackStar(MonsterCatalog.Variety? variety) => variety?.Id is BlackStar;
    internal static bool IsEaterOfWorlds(MonsterCatalog.Variety? variety) => variety?.Id is EaterOfWorlds or EaterOfWorldsGauntlet;
    internal static bool IsSearingExarch(MonsterCatalog.Variety? variety) => variety?.Id is SearingExarch or SearingExarchGauntlet;
    internal static bool IsMaven(MonsterCatalog.Variety? variety) => variety?.Id is Maven or MavenEnraged or MavenEnragedGauntlet;
    internal static bool IsMavenBrain(MonsterCatalog.Variety? variety) => variety?.Id is MavenBrain or MavenBrainGauntlet;
    internal static bool IsNeglect(MonsterCatalog.Variety? variety) => variety?.Id is NeglectBoss or NeglectBossUber or NeglectBossGauntlet or NeglectBossUberGauntlet;
    internal static bool IsFear(MonsterCatalog.Variety? variety) => variety?.Id is FearBoss or FearBossUber or FearBossGauntlet or FearBossUberGauntlet;
    internal static bool IsDread(MonsterCatalog.Variety? variety) => variety?.Id is DreadBoss or DreadBossUber or DreadBossGauntlet or DreadBossUberGauntlet;
    internal static bool IsFragmentOfNeglect(MonsterCatalog.Variety? variety) => variety?.Id is FragmentOfNeglect or FragmentOfNeglectStandalone;
    internal static bool IsFragmentOfFear(MonsterCatalog.Variety? variety) => variety?.Id is FragmentOfFear or FragmentOfFearStandalone;
    internal static bool IsFragmentOfDread(MonsterCatalog.Variety? variety) => variety?.Id is FragmentOfDread or FragmentOfDreadStandalone;

    internal static bool IsBoss(MonsterCatalog.Variety? variety) =>
        IsShaper(variety) || IsElder(variety) || IsSirus(variety)
        || IsInfiniteHunger(variety) || IsBlackStar(variety) || IsEaterOfWorlds(variety) || IsSearingExarch(variety)
        || IsMaven(variety) || IsMavenBrain(variety)
        || IsNeglect(variety) || IsFear(variety) || IsDread(variety)
        || IsFragmentOfNeglect(variety) || IsFragmentOfFear(variety) || IsFragmentOfDread(variety);

    internal static LifePhaseGate? Phases(MonsterCatalog.Variety variety) =>
        IsInfiniteHunger(variety) || IsBlackStar(variety) ? new([66, 33])
        : IsMavenBrain(variety) || IsFragmentOfNeglect(variety) || IsFragmentOfFear(variety) || IsFragmentOfDread(variety) ? new([50])
        : new([75, 50, 25]);

    internal static int BasicEffect(MonsterCatalog.Variety variety) =>
        IsSirus(variety) ? SirusBeamBlastEffect
        : IsElder(variety) ? ElderMeleeEffect
        : IsShaper(variety) ? ShaperMeleeEffect
        : IsSearingExarch(variety) || IsFragmentOfFear(variety) ? FireUniqueMeleeEffect
        : IsMaven(variety) ? MavenProjectileEffect
        : IsMavenBrain(variety) ? BrainProjBlastEffect
        : IsNeglect(variety) || IsFragmentOfNeglect(variety) ? BossMeleeEffect
        : UniqueMeleeEffect;

    internal static bool MeleeLike(MonsterCatalog.Variety variety) =>
        !(IsSirus(variety) || IsMaven(variety) || IsMavenBrain(variety));

    // Disjoint 0x4000+ skill ID ranges per boss family.
    internal static readonly int[] ShaperEffects = [ShaperMeleeEffect, CelestialBeamEffect, AcceleratingProjectilesEffect,
        FlickerSlamEffect, RetreatEffect, BulletHellEffect];
    internal static readonly int[] ElderEffects = [ElderMeleeEffect, SiphonEffect, SlammingTentaclesEffect,
        ExpandingNovaEffect, DoomProjectileEffect, ProjectileSpellcastEffect];
    internal static readonly int[] SirusEffects = [SirusBeamBlastEffect, SirusCorridorBlastEffect, SirusCircleMazeEffect,
        SirusCircleMaze2Effect, SirusCircleMaze3Effect, SirusCircleMaze4Effect, SirusCircleMaze5Effect, SirusMultiBeamEffect,
        SirusMultiBeam2Effect, SirusCloneBeamEffect, SirusMeteorEffect, SirusMeteorGroundEffect, SirusBeamBombEffect];
    internal static readonly int[] HungerEffects = [UniqueMeleeEffect, HungerSlamCastEffect, HungerSlamEffect,
        HungerVomitChannelEffect, HungerVomitMortarEffect, HungerDrillChargeEffect, HungerStompEffect, HungerSumoSlamEffect, HungerGooEruptionEffect];
    internal static readonly int[] BlackStarEffects = [UniqueMeleeEffect, BlackStarSpawnCasterEffect, BlackStarSpawnMeleeEffect,
        BlackStarFireLeapEffect, BlackStarFireLeapImpactEffect, BlackStarColdCascadeEffect, BlackStarCometBarrageEffect,
        BlackStarCometImpactEffect, BlackStarLaserSpearsEffect];
    internal static readonly int[] EaterEffects = [UniqueMeleeEffect, EaterDashEffect, EaterDashSlamCastEffect,
        EaterDisintegrateBeamEffect, EaterOmegaTentaclesEffect, EaterBonkedEffect, EaterDashSlamEffect, EaterSlamEffect,
        EaterDoomLaserEffect, EaterTentacleSlamEffect, EaterUltimateNukeEffect];
    internal static readonly int[] ExarchEffects = [FireUniqueMeleeEffect, ExarchBugBarrageCastEffect, ExarchMurderLanternCastEffect,
        ExarchDashSlashCastEffect, ExarchFireWallEffect, ExarchSummonFlameWallEffect, ExarchPushFlameWallEffect,
        ExarchBugMortarEffect, ExarchLanternExplosionEffect, ExarchDashSlashEffect];
    internal static readonly int[] MavenEffects = [MavenProjectileEffect, MavenLaserBarrageEffect, MavenMemoryGameEffect,
        MavenCannonCastEffect, MavenCircleSlamCastEffect, MavenCircleSlamEffect, MavenColdCannonEffect,
        MavenSuperFireEffect, MavenSuperFireImpactEffect, MavenCollapseVolatilesEffect];
    internal static readonly int[] MavenBrainEffects = [BrainChargeBeamEffect, BrainIonCannonEffect, BrainBeamBlastEffect,
        BrainProjBlastCastEffect, BrainLargeBarrageCastEffect, BrainProjBlastEffect, BrainLargeBarrageEffect,
        BrainSmallBarrageEffect, BrainDecelProjEffect];
    internal static readonly int[] NeglectEffects = [BossMeleeEffect, NeglectSpawnLasersEffect, NeglectAccelProjEffect,
        NeglectIceNovaSlamEffect, NeglectBigBlastEffect, NeglectBeamDamageEffect, NeglectLaserBeamEffect, NeglectLaserBeam2Effect];
    internal static readonly int[] FearEffects = [UniqueMeleeEffect, FearGroundSlamEffect, FearBurningSlamEffect,
        FearStompEffect, FearSolarNovaEffect, FearFissureProjEffect, FearFissureSlamEffect, FearLeapEffect,
        FearLeapAttackEffect, FearGigaBeamEffect, FearBeamBarrageEffect, FearOrbShatterEffect, FearBigStompEffect];
    internal static readonly int[] DreadEffects = [UniqueMeleeEffect, DreadCascadeSlamEffect, DreadBulletHellStartEffect,
        DreadFusionBlastEffect, DreadCentipedeEffect, DreadDoubleFrontSlamEffect, DreadEyebeamEffect, DreadLightningProjEffect,
        DreadCelestialBeamEffect, DreadTentacleWaveEffect, DreadFireProjEffect, DreadColdProjEffect, DreadChaosProjEffect,
        DreadShrinkingNovaEffect, DreadEyebeamIceEffect, DreadTeleSlamEffect];
    internal static readonly int[] FragNeglectEffects = [BossMeleeEffect, FragNeglectBeamBlastEffect, FragNeglectCorridorEffect,
        FragNeglectCircleMazeEffect, FragNeglectCloneBeamEffect, FragNeglectBeamBombEffect, FragNeglectMeteorEffect,
        FragNeglectRainOfStarsEffect, FragNeglectDeathProjEffect];
    internal static readonly int[] FragFearEffects = [FireUniqueMeleeEffect, FragFearShieldShardsEffect, FragFearHolyBeamEffect,
        FragFearShatterBeamEffect, FragFearDecelProjEffect, FragFearMassBeamEffect, FragFearDashEffect,
        FragFearSlamEffect, FragFearNetMortarEffect, FragFearUltimateEffect, FragFearExplosiveArrowEffect];
    internal static readonly int[] FragDreadEffects = [UniqueMeleeEffect, FragDreadSuperProjEffect, FragDreadGreaterBlastEffect,
        FragDreadDelayedBlastEffect, FragDreadDivineChargeEffect, FragDreadBallLightningEffect, FragDreadOffsetProjEffect,
        FragDreadDivineCallEffect, FragDreadDivineBeamEffect, FragDreadKillZoneEffect];

    internal static int[] EffectsFor(MonsterCatalog.Variety variety) =>
        IsSirus(variety) ? SirusEffects
        : IsElder(variety) ? ElderEffects
        : IsShaper(variety) ? ShaperEffects
        : IsInfiniteHunger(variety) ? HungerEffects
        : IsBlackStar(variety) ? BlackStarEffects
        : IsEaterOfWorlds(variety) ? EaterEffects
        : IsSearingExarch(variety) ? ExarchEffects
        : IsMaven(variety) ? MavenEffects
        : IsMavenBrain(variety) ? MavenBrainEffects
        : IsNeglect(variety) ? NeglectEffects
        : IsFear(variety) ? FearEffects
        : IsDread(variety) ? DreadEffects
        : IsFragmentOfNeglect(variety) ? FragNeglectEffects
        : IsFragmentOfFear(variety) ? FragFearEffects
        : IsFragmentOfDread(variety) ? FragDreadEffects
        : ShaperEffects;

    internal static ushort SkillBaseFor(MonsterCatalog.Variety variety) =>
        IsShaper(variety) ? (ushort)0x4000
        : IsElder(variety) ? (ushort)0x4010
        : IsSirus(variety) ? (ushort)0x4020
        : IsInfiniteHunger(variety) ? (ushort)0x4040
        : IsBlackStar(variety) ? (ushort)0x4050
        : IsEaterOfWorlds(variety) ? (ushort)0x4060
        : IsSearingExarch(variety) ? (ushort)0x4070
        : IsMaven(variety) ? (ushort)0x4080
        : IsMavenBrain(variety) ? (ushort)0x4090
        : IsNeglect(variety) ? (ushort)0x40A0
        : IsFear(variety) ? (ushort)0x40B0
        : IsDread(variety) ? (ushort)0x40C0
        : IsFragmentOfNeglect(variety) ? (ushort)0x40D0
        : IsFragmentOfFear(variety) ? (ushort)0x40E0
        : IsFragmentOfDread(variety) ? (ushort)0x40F0
        : (ushort)0x4000;

    /// <summary>The skill id the client's actor uses for one of the boss's effects.</summary>
    internal static ushort Skill(MonsterCatalog.Variety variety, int effect)
    {
        var effects = EffectsFor(variety);
        int index = Array.IndexOf(effects, effect);
        return (ushort)(SkillBaseFor(variety) + (index >= 0 ? index : effects.Length + 1));
    }

    /// <summary>The actor's skill list: every effect the controller can play, so that the client owns the clip a 0x1ab
    /// action names.</summary>
    internal static IReadOnlyList<GrantedSkill>? SkillsFor(Player player, MonsterCatalog.Variety variety, int level,
        MonsterAttack attack)
    {
        if (!IsBoss(variety)) return null;
        var catalog = Game.SkillsFor(player);
        var effects = EffectsFor(variety);
        return [.. effects.Select(effect =>
        {
            var use = Attack(player, variety, level, effect, attack);
            return catalog.FindByRow(Skill(variety, effect), effect, level,
                WithBehaviourVariation(player, effect, use.SkillStats()));
        }).OfType<GrantedSkill>()];
    }

    static readonly Dictionary<int, (double Min, double Max)> SirusFractions = new()
    {
        [SirusBeamBlastEffect] = (0.8, 1.2),
        [SirusCorridorBlastEffect] = (1.6, 2.4),
        [SirusCircleMazeEffect] = (1.2, 1.8),
        [SirusCircleMaze2Effect] = (1.2, 1.8),
        [SirusCircleMaze3Effect] = (1.2, 1.8),
        [SirusCircleMaze4Effect] = (1.2, 1.8),
        [SirusCircleMaze5Effect] = (1.2, 1.8),
        [SirusCloneBeamEffect] = (1.6, 2.4),
        [SirusMultiBeamEffect] = (1.6, 2.4),
        [SirusMultiBeam2Effect] = (1.6, 2.4),
        [SirusMeteorEffect] = (1.6, 2.4),
        [SirusMeteorGroundEffect] = (0.4, 0.6),
        [SirusBeamBombEffect] = (0.8, 1.2),
    };

    internal static DamagePacket SirusDamage(int effect, int min, int max)
    {
        var (low, high) = SirusFractions.GetValueOrDefault(effect, (1.0, 1.0));
        return DamagePacket.Of(DamageType.Physical,
            new DamageRange((int)Math.Floor(min * low), (int)Math.Ceiling(max * high)));
    }

    internal static MonsterAttack Attack(Player player, MonsterCatalog.Variety variety, int level, int effect,
        MonsterAttack? swing = null)
    {
        var catalog = Game.SkillsFor(player);
        var (min, max) = Monsters.BaseDamage(variety, (byte)Math.Clamp(level, 1, 100)) ?? (1, 1);
        bool melee = effect == BasicEffect(variety) && MeleeLike(variety);
        var damage = catalog.HitDamage(effect, level);
        if (IsSirus(variety)) damage = SirusDamage(effect, min, max);
        else if (damage.MaxTotal <= 0 && effect is not (CelestialBeamEffect or SiphonEffect or FearGigaBeamEffect))
            damage = DamagePacket.Of(DamageType.Physical, min, max);
        double seconds = melee ? Math.Max(0.3, variety.AttackMs / 1000.0) : Math.Max(0.3, catalog.CastTimeMs(effect) / 1000.0);
        double reach = melee ? variety.AttackRange : 45;
        if (melee && swing is not null) return swing with { SkillId = Skill(variety, effect), Scripted = true };
        var projectile = ProjectileFor(player, effect);
        return new MonsterAttack(Skill(variety, effect), seconds, reach, damage)
        {
            Spell = catalog.IsSpell(effect),
            Scripted = true,
            Ranged = projectile is not null,
            Projectile = projectile,
        };
    }

    internal static SkillCatalog.MonsterProjectileInfo? ProjectileFor(Player player, int effect) => effect switch
    {
        BulletHellEffect => BulletHellShot,
        DoomProjectileEffect => DoomShot,
        BlackStarCometBarrageEffect => BlackStarCometShot,
        ExarchFireWallEffect => ExarchFireWallShot,
        MavenProjectileEffect => MavenBasicShot,
        MavenColdCannonEffect => MavenColdShot,
        MavenSuperFireEffect => MavenSuperFireShot,
        BrainProjBlastEffect => BrainBlastShot,
        BrainLargeBarrageEffect => BrainLargeShot,
        BrainSmallBarrageEffect => BrainSmallShot,
        BrainDecelProjEffect => BrainDecelShot,
        NeglectAccelProjEffect => NeglectReflectedShot,
        FearFissureProjEffect => FearSpearShot,
        DreadCentipedeEffect => DreadCentipedeShot,
        DreadLightningProjEffect => DreadLightningShot,
        DreadFireProjEffect => DreadFireShot,
        DreadColdProjEffect => DreadColdShot,
        DreadChaosProjEffect => DreadChaosShot,
        FragNeglectDeathProjEffect => FragNeglectDeathShot,
        FragFearShieldShardsEffect => FragFearShardsShot,
        FragFearExplosiveArrowEffect => FragFearArrowShot,
        FragDreadOffsetProjEffect => FragDreadProjShot,
        FragDreadSuperProjEffect => FragDreadSuperShot,
        _ => Game.SkillsFor(player).MonsterProjectile(effect),
    };

    internal static DamagePacket DamageOverTime(Player player, int effect, int level)
    {
        var stats = Game.SkillsFor(player).EffectStats(effect, level);
        int Get(uint name)
        {
            foreach (var (id, value) in stats) if (id == name) return value;
            return 0;
        }
        var packet = DamagePacket.Empty;
        foreach (var (type, name) in new[]
        {
            (DamageType.Physical, Stat.BasePhysicalDamageToDealPerMinute),
            (DamageType.Fire, Stat.BaseFireDamageToDealPerMinute),
            (DamageType.Cold, Stat.BaseColdDamageToDealPerMinute),
            (DamageType.Lightning, Stat.BaseLightningDamageToDealPerMinute),
            (DamageType.Chaos, Stat.BaseChaosDamageToDealPerMinute),
        })
        {
            double perSecond = Get(name) / 60.0;
            if (perSecond < 1) continue;
            packet = packet.Add(type, new DamageRange(Math.Floor(perSecond), Math.Ceiling(perSecond)));
        }
        return packet;
    }

    // Per-player active pinnacle boss debuffs and Zana protection bubble (O(1) on PlayerStats hot path).
    internal sealed class PlayerEffectState
    {
        public bool ZanaBubble;
        public int HungerDrowningStacks;
        public int SolarFlareStacks;
        public int AstralAvalancheStacks;
        public bool MavenNoRegenActive;
        public double MavenNoRegenUntil;
        public int SearingRuneStacks;
        public double SearingRuneUntil;
        public bool EaterDrowning;
    }

    static readonly Store<Player, PlayerEffectState> PlayerEffects = new();

    internal static PlayerEffectState EffectsOf(Player player) => PlayerEffects.GetValue(player, _ => new());

    internal static Dictionary<uint, int> PlayerStats(Player player, Dictionary<uint, int> sources)
    {
        if (!PlayerEffects.TryGetValue(player, out var state)) return sources;
        double now = AreaTime(player);
        if (state.ZanaBubble)
            sources[Stat.BaseCannotBeDamaged] = sources.GetValueOrDefault(Stat.BaseCannotBeDamaged) + 1;
        if (state.HungerDrowningStacks > 0)
        {
            int stacks = Math.Min(state.HungerDrowningStacks, 20);
            sources[Stat.ActionSpeedMinusPct] = sources.GetValueOrDefault(Stat.ActionSpeedMinusPct) + stacks * 2;
            sources[Stat.MovementVelocityPlusPct] = sources.GetValueOrDefault(Stat.MovementVelocityPlusPct) - stacks * 2;
        }
        if (state.SolarFlareStacks > 0)
            sources[Stat.BaseFireDamageResistancePct] = sources.GetValueOrDefault(Stat.BaseFireDamageResistancePct) - Math.Min(state.SolarFlareStacks, 15) * 3;
        if (state.AstralAvalancheStacks > 0)
            sources[Stat.BaseColdDamageResistancePct] = sources.GetValueOrDefault(Stat.BaseColdDamageResistancePct) - Math.Min(state.AstralAvalancheStacks, 15) * 3;
        if (state.MavenNoRegenUntil > now)
        {
            sources[Stat.LifeRegenerationRatePlusPct] = sources.GetValueOrDefault(Stat.LifeRegenerationRatePlusPct) - 100;
            sources[Stat.EnergyShieldRegenerationRatePlusPct] = sources.GetValueOrDefault(Stat.EnergyShieldRegenerationRatePlusPct) - 100;
        }
        if (state.SearingRuneStacks > 0 && state.SearingRuneUntil > now)
            sources[Stat.BaseFireDamageResistancePct] = sources.GetValueOrDefault(Stat.BaseFireDamageResistancePct) - Math.Min(state.SearingRuneStacks, 10) * 2;
        if (state.EaterDrowning)
            sources[Stat.MovementVelocityPlusPct] = sources.GetValueOrDefault(Stat.MovementVelocityPlusPct) - 35;
        return sources;
    }

    internal static Task Leaving(Player player)
    {
        ClearPlayerEffects(player);
        return Task.CompletedTask;
    }

    internal static void ClearPlayerEffects(Player player)
    {
        if (!PlayerEffects.TryGetValue(player, out var state)) return;
        state.ZanaBubble = false;
        state.HungerDrowningStacks = 0;
        state.SolarFlareStacks = 0;
        state.AstralAvalancheStacks = 0;
        state.MavenNoRegenActive = false;
        state.MavenNoRegenUntil = 0;
        state.SearingRuneStacks = 0;
        state.SearingRuneUntil = 0;
        state.EaterDrowning = false;
        if (player.Closed) return;
        foreach (ushort row in new[] { ZanaBubbleBuffRow, HungerDrowningRow, BlackStarSunlightRow, BlackStarMoonlightRow,
            MavenPunishmentRow, MavenMemoryGameDegenRow, ExarchSearingRuneRow, ExarchCauterisedRow, EaterDrowningRow,
            EaterLivewireRow, EaterLaserRow, DreadTraumaRow, NeglectBeamDebuffRow, FearBeamDebuffRow })
            player.Area.Buffs.Clear(player.Id, row);
    }

    /// <summary>Runs the fight of every pinnacle and memory boss in this connection's instance.</summary>
    internal static async Task Tick(Player player)
    {
        if (!player.Connection.PlayerSent || player.Closed || player.LeavingArea) return;
        List<(uint Id, SpawnedMonster Monster)> found = [];
        foreach (var (id, monster) in player.Area.Monsters)
            if (IsBoss(monster.Variety)) found.Add((id, monster));
        if (found.Count == 0 && !Fights.TryGetValue(player.Instance, out _)) return;
        foreach (var (id, monster) in found)
        {
            if (!Fights.TryGetValue(player.Instance, out var byMonster))
                Fights.Add(player.Instance, byMonster = new Dictionary<uint, AtlasBossFight>());
            if (!byMonster.TryGetValue(id, out var fight))
            {
                var variety = monster.Variety!;
                fight = CreateFight(id, monster, player, variety);
                byMonster[id] = fight;
                Console.WriteLine($"[gateway] {player.Connection.Remote} 0x{id:x} {variety.Name ?? variety.Id} "
                    + $"({variety.Id}, level {monster.Level}, life {monster.MaxLife}): fight started, "
                    + $"{(IsMonsterRunner(player) ? "its steps run here" : "another connection runs its steps")}");
            }
            await fight.Gate.WaitAsync();
            try
            {
                await fight.Bind(player);
                if (IsMonsterRunner(player)) await fight.Step(player, AreaTime(player));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} {monster.Variety!.Id} 0x{id:x} step failed: {ServerLog.Describe(ex)}");
            }
            finally { fight.Gate.Release(); }
        }
    }

    static AtlasBossFight CreateFight(uint id, SpawnedMonster monster, Player player, MonsterCatalog.Variety variety) =>
        IsSirus(variety) ? new SirusFight(id, monster, player)
        : IsShaper(variety) ? new ShaperFight(id, monster, player)
        : IsElder(variety) ? new ElderFight(id, monster, player)
        : IsInfiniteHunger(variety) ? new InfiniteHungerFight(id, monster, player)
        : IsBlackStar(variety) ? new BlackStarFight(id, monster, player)
        : IsEaterOfWorlds(variety) ? new EaterOfWorldsFight(id, monster, player)
        : IsSearingExarch(variety) ? new SearingExarchFight(id, monster, player)
        : IsMaven(variety) ? new MavenFight(id, monster, player)
        : IsMavenBrain(variety) ? new MavenBrainFight(id, monster, player)
        : IsNeglect(variety) ? new NeglectFight(id, monster, player)
        : IsFear(variety) ? new FearFight(id, monster, player)
        : IsDread(variety) ? new DreadFight(id, monster, player)
        : IsFragmentOfNeglect(variety) ? new FragmentOfNeglectFight(id, monster, player)
        : IsFragmentOfFear(variety) ? new FragmentOfFearFight(id, monster, player)
        : new FragmentOfDreadFight(id, monster, player);

    static readonly Store<InstanceState, Dictionary<uint, AtlasBossFight>> Fights = new();

    internal static string? EffectName(int effect)
    {
        lock (EffectNames)
            if (EffectNames.TryGetValue(effect, out string? known)) return known;
        var table = GameServer.Core.ClientTables.TableOrNull("GrantedEffects");
        if (table is null) return null;
        string? name = table.Row(effect)?.TextOrNull("Id");
        lock (EffectNames) EffectNames[effect] = name;
        return name;
    }

    static readonly Dictionary<int, string?> EffectNames = [];

    internal static IReadOnlyList<string> ActorSkillNames(Player player, uint id) =>
        player.Area.Monsters.TryGetValue(id, out var monster)
            ? [.. monster.SkillIds.Select(s => EffectName(s.Effect)).OfType<string>()]
            : [];

    internal static int EffectByName(SpawnedMonster monster, string action)
    {
        foreach (var (effect, _) in monster.SkillIds)
            if (string.Equals(EffectName(effect), action, StringComparison.OrdinalIgnoreCase)) return effect;
        if (int.TryParse(action, out int row) && monster.SkillIds.Any(s => s.Effect == row)) return row;
        return -1;
    }

    internal static async Task<bool> ActorAction(Player player, uint id, string action)
    {
        if (!player.Area.Monsters.TryGetValue(id, out var monster)) return false;
        if (monster.Dead || monster.Life == 0 || !CurrentGameWork || InGracePeriod(player)) return false;
        int effect = EffectByName(monster, action);
        if (effect < 0) return false;
        ushort skillId = 0;
        foreach (var (e, s) in monster.SkillIds)
            if (e == effect) { skillId = s; break; }
        if (skillId == 0) return false;
        var attack = new MonsterAttack(skillId, 1, Math.Max(20, monster.Attack?.Range ?? 20), DamagePacket.Empty)
            { Spell = Game.SkillsFor(player).IsSpell(effect), Scripted = true, NoContact = true };
        int generation = Interlocked.Increment(ref monster.ActionGeneration);
        bool played = await MonsterSwingCore(player, id, monster, generation, attack);
        if (played)
            Console.WriteLine($"[gateway] {player.Connection.Remote} monster 0x{id:x} ({monster.Label}) plays {action} (skill 0x{skillId:x4})");
        return played;
    }

    internal static IDisposable CleanupOnUnload() => new Cleanup();

    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights)
                foreach (var fight in pair.Value.Values) fight.Stop();
            foreach (var pair in PlayerEffects)
                ClearPlayerEffects(pair.Key);
        }
    }
}
