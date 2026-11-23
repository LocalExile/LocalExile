using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using Rules = global::Game;

namespace GameServer.Modules.Game;

/// <summary>The game's rules: gives the framework its <see cref="IGameLogic"/>.</summary>
public sealed class GameModule : IModule, IDisposable
{
    /// <summary>
    /// Builds, when the module loads, the catalogues every first login and first arrival reads (the stash rules, gems,
    /// monster contacts, life tables, golem scaling, the room index, the hideouts). Each is built on first use otherwise,
    /// which was while the first player after a start waited: about 3 s of server time on the first login (2026-11-17).
    /// A catalogue that cannot be built here is left for its first use, which reports why as it always did.
    /// </summary>
    static void WarmCatalogues()
    {
        try
        {
            Program.Items.Warm();
            Program.Skills.Warm();
            Rules.Monsters.Warm();
            Rules.GolemData.Warm();
            AreaPopulation.Warm();
            _ = HideoutCatalog.Hideouts;
            _ = CharacterAudioCatalog.Default;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException or KeyNotFoundException or InvalidOperationException)
        {
            Console.WriteLine($"[gateway] catalogues not built at startup, left for their first use: {e.Message}");
        }
    }

    /// <summary>The end of an unload: the host disposes a module's entry after it stopped the module's lifetime, so the
    /// clean-ups that lifetime owns (the fights' CleanupOnUnload) have read the rule tables by now.</summary>
    public void Dispose()
    {
        Rules.ForgetClaimedCharacters();
        Rules.ResetAchievementCache();
        RuleTables.Empty();
    }

    /// <summary>This module's game data, built in a fixed order when it loads: before the Gateway listens, as the
    /// Gateway's own (WorldLoader). Tests that use the module call it once.</summary>
    public static void LoadData()
    {
        // The catalogs the others read first. The HardMode data set waits for its first player (Rules.LoadHardModeData).
        Rules.LoadSettingsData();
        // The catalogs the rules share (Settings.cs and the systems' own files), each after the ones it reads.
        Rules.Monsters = new();
        Rules.Packs = new();
        Rules.Cosmetics = new();
        Rules.Mtx = new();
        Rules.MtxEffects = new();
        Rules.Art = new();
        Rules.Uniques = new(Program.Items);
        Rules.Essences = new(Program.Items, Program.Rolls);
        Rules.Drops = new(Program.Items, Program.Rolls, maps: Rules.MapDevices.Atlas, uniques: Rules.Uniques);
        Rules.AtlasPassiveNodes = new();
        Rules.TimelessJewels = new();
        Rules.OrdinaryPassives = new();
        Rules.Ascendancies = new();
        Rules.Descendancies = new();
        Rules.AscendancyRules = new(Rules.Ascendancies);
        Rules.Animations = new();
        Rules.Emerges = new();
        Rules.GolemData = new();
        Rules.SummonData = new();
        Rules.RampageData = new();
        Rules.Supports = new();
        Rules.LoadMinionSupportsData();
        Rules.LoadWornGrantedSkillsData();
        Rules.LoadPhasingData();
        Rules.LoadBuffCancelData();
        Rules.LoadDeathPenaltyData();
        Rules.LoadModuleWorldData();
        Rules.LoadMonsterModsData();
        Rules.LoadPassiveGrantedSkillsRulesData();
        // Combat, skills and projectiles.
        Rules.LoadOwnAurasData();
        global::StationaryCollision.LoadStationaryCollisionData();
        Rules.LoadProjectilesData();
        Rules.LoadPlacedSkillsData();
        Rules.LoadTotemsRulesData();
        // Flasks.
        global::AntiquarianEffects.LoadAntiquarianEffectsData();
        Rules.LoadFlaskGroundsData();
        // Monsters.
        global::BossKits.LoadBossKitsData();
        global::BossMods.LoadBossModsData();
        global::IzaroSkills.LoadIzaroSkillsData();
        Rules.LoadMonsterContagionsData();
        Rules.LoadMonsterMinionsData();
        Rules.LoadHostileGroundsData();
        global::MonsterSkills.LoadMonsterSkillsData();
        // Passives and the player.
        global::ClusterJewelLayout.LoadClusterJewelLayoutData();
        global::JewelPathEffects.LoadJewelPathEffectsData();
        global::ForbiddenTattoo.LoadForbiddenTattooData();
        global::JourneyMakanuiCatalogue.LoadJourneyMakanuiCatalogueData();
        global::TattooCatalogue.LoadTattooCatalogueData();
        // The world.
        global::MapSulphite.LoadMapSulphiteData();
        Rules.LoadQuestEngineData();
        global::TourRecords.LoadTourRecordsData();
        global::MapEssenceCatalog.LoadGameData();
        global::MapEssences.LoadGameData();
        global::MapScarabBosses.LoadGameData();
        global::MapScarabContent.LoadGameData();
        global::MapScarabPacks.LoadGameData();
        global::StrongboxCatalog.LoadGameData();
        global::StrongboxMods.LoadGameData();
        global::MapStrongboxes.LoadGameData();
        global::CampaignDoors.LoadGameData();
        global::CampaignChests.LoadGameData();
        global::WeaponIdentity.LoadGameData();
        global::GroundArt.LoadGameData();
        global::BeyondCatalog.LoadGameData();
        global::TormentCatalog.LoadGameData();
        global::HarbingerCatalog.LoadGameData();
        Rules.LoadEssenceAurasData();
        Rules.LoadEssenceSummonsData();
        Rules.LoadEssenceProjectiles();
        Rules.LoadRemnantAuraData();
        Rules.LoadSelfBuffSkillsData();
        Rules.LoadMapAreaStats();
        Rules.LoadAchievementsData();
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        Rules.CombatLifetime = module.Lifetime;
        // The game's systems in each area's own world (GameArea).
        AreaSetup.Load(module);
        global::Game.LoadStats(module);
        module.Lifetime.Own(Rules.CleanupTemporaryAuras());
        module.Lifetime.Own(Rules.CleanupEssenceAuras());
        module.Lifetime.Own(Rules.CleanupMonsterSummons());
        module.Lifetime.Own(Rules.CleanupRemnantAuras());
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.InArea(global::Sirus.Area, global::Sirus.Tick));
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always((player, _) => Rules.CloseInteractionsIfMovingAway(player, null, Rules.AreaTime(player))));
        // A map device's portal labels read "<area> (Completed)" once that run's boss is dead (World/Portals.cs).
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always((player, _) => Rules.TickMapRunBoss(player)));
        GameEvents.MonsterKilled.Add(module.Manifest.Id, kill => Rules.TickMapRunBoss(kill.Player, force: true));
        // A dead monster's debuffs come off: each player's step for it.
        GameEvents.MonsterKilled.Add(module.Manifest.Id, kill =>
        {
            // A Misery proximity shield dies with its monster: the counter is a hot-path guard, not a state that
            // outlives the monster (nothing would ever decrement it for a monster that leaves the area).
            MapEssences.ClearProximityShield(kill.Player, kill.Monster.ObjectId);
            if (kill.Player.Area.Monsters.TryGetValue(kill.Monster.ObjectId, out var killedMonster))
                Rules.CleanupDeadMonsterLocked(kill.Player.Area, kill.Monster.ObjectId, killedMonster);
            foreach (var p in kill.Player.Frames.Players()) global::Game.MonsterDebuffsChanged(p, kill.Monster.ObjectId);
            return Task.CompletedTask;
        });
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (player, _) => Rules.ClearPlayerMonsterAurasAsync(player));
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (player, _) => global::Sirus.Leaving(player));
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::Sirus.PlayerStats);
        InstanceEvents.AreaStats.Add(module.Manifest.Id, Rules.MapAreaStats);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapScarabContent.Describe);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapScarabPacks.Opening);
        GameEvents.Drops.Add(module.Manifest.Id, MapScarabPacks.Drops);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapStrongboxes.Opening);
        GameEvents.PlayerEntered.Add(module.Manifest.Id, MapStrongboxes.Enter);
        GameEvents.ObjectUse.Add(module.Manifest.Id, MapStrongboxes.Use);
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always((player, _) => MapStrongboxes.Tick(player)));
        GameEvents.ChestsOpened.Add(module.Manifest.Id, MapStrongboxes.OpenNear);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, MapStrongboxes.CurseStats);
        GameEvents.Packet(GameServer.Core.Protocol.Instance.InstanceItems.UseItemOnObjectId).Add(module.Manifest.Id, MapStrongboxes.UseItemOn);
        GameEvents.Packet(GameServer.Core.Protocol.Instance.InstanceItems.UseItemTypeOnObjectId).Add(module.Manifest.Id, MapStrongboxes.UseItemTypeOn);
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (player, _) => { MapStrongboxes.Leave(player); return Task.CompletedTask; });
        GameEvents.StateBuilding.Add(module.Manifest.Id, MapStrongboxes.Describe);
        GameEvents.AreaGenerated.Add(module.Manifest.Id, CampaignDoors.OnAreaGenerated);
        GameEvents.PlayerEntered.Add(module.Manifest.Id, CampaignDoors.OnPlayerEntered);
        GameEvents.ObjectUse.Add(module.Manifest.Id, CampaignDoors.Use);
        GameEvents.SkillCast.Add(module.Manifest.Id, (player, _, _, from, _) => { _ = CampaignDoors.BreakSmashablesNear(player, from); return null; });
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (player, _) => { CampaignDoors.Leave(player); return Task.CompletedTask; });
        GameEvents.AreaGenerated.Add(module.Manifest.Id, CampaignChests.OnAreaGenerated);
        GameEvents.PlayerEntered.Add(module.Manifest.Id, CampaignChests.OnPlayerEntered);
        GameEvents.ObjectUse.Add(module.Manifest.Id, CampaignChests.Use);
        GameEvents.ChestsOpened.Add(module.Manifest.Id, CampaignChests.OpenNear);
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (player, _) => { CampaignChests.Leave(player); return Task.CompletedTask; });
        GameEvents.MapOpening.Add(module.Manifest.Id, MapEssences.Opening);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapScarabBosses.Opening);
        GameEvents.PlayerEntered.Add(module.Manifest.Id, MapEssences.Enter);
        GameEvents.ObjectUse.Add(module.Manifest.Id, MapEssences.Use);
        GameEvents.Packet(GameServer.Core.Protocol.Instance.InstanceItems.UseItemOnObjectId).Add(module.Manifest.Id, MapEssences.UseItemOn);
        GameEvents.Packet(GameServer.Core.Protocol.Instance.InstanceItems.UseItemTypeOnObjectId).Add(module.Manifest.Id, MapEssences.UseItemTypeOn);
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always(MapEssences.Tick));
        GameEvents.Drops.Add(module.Manifest.Id, MapEssences.Drops);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, Rules.RemnantAuraStats);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, MapEssences.EssenceDebuffStats);
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (player, _) => { MapEssences.Leave(player); return Task.CompletedTask; });
        GameEvents.StateBuilding.Add(module.Manifest.Id, MapEssences.Describe);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapTorment.Opening);
        GameEvents.AreaGenerated.Add(module.Manifest.Id, MapTorment.Enter);
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always(MapTorment.Tick));
        GameEvents.Drops.Add(module.Manifest.Id, MapTorment.Drops);
        GameEvents.Kill.Add(module.Manifest.Id, MapTorment.Kill);
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, _) => { MapTorment.Leave(conn); return Task.CompletedTask; });
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, MapTorment.PlayerStats);
        GameEvents.BuffCancel.Add(module.Manifest.Id, MapTorment.CancelBuff);
        GameEvents.BuffCancel.Add(module.Manifest.Id, Rules.CancelRemovableBuff);
        GameEvents.StateBuilding.Add(module.Manifest.Id, MapTorment.Describe);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapHarbinger.Opening);
        GameEvents.AreaGenerated.Add(module.Manifest.Id, MapHarbinger.Enter);
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always(MapHarbinger.Tick));
        GameEvents.Kill.Add(module.Manifest.Id, MapHarbinger.Kill);
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, _) => { MapHarbinger.Leave(conn); return Task.CompletedTask; });
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, MapHarbinger.PlayerStats);
        GameEvents.StateBuilding.Add(module.Manifest.Id, MapHarbinger.Describe);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapBeyond.Opening);
        GameEvents.AreaGenerated.Add(module.Manifest.Id, MapBeyond.Enter);
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always(MapBeyond.Tick));
        GameEvents.Kill.Add(module.Manifest.Id, MapBeyond.Kill);
        GameEvents.Drops.Add(module.Manifest.Id, MapBeyond.Drops);
        GameEvents.Experience.Add(module.Manifest.Id, MapBeyond.Experience);
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, _) => { MapBeyond.Leave(conn); return Task.CompletedTask; });
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, MapBeyond.PlayerStats);
        GameEvents.StateBuilding.Add(module.Manifest.Id, MapBeyond.Describe);
        module.Lifetime.Own(IncomingResourceDefences.HandOverFinalTaken([MapBeyond.BossLessDamageStat]));
        module.Lifetime.Own(MonsterModEffects.HandOverMore(Rules.AtlasMoreLife, Rules.AtlasMoreDamage));
        module.Lifetime.Own(MonsterModEffects.HandOverMore([MapTorment.LifeStat, MapBeyond.LifeStat], [], [MapTorment.TaperStat]));
        // Secrets of Suffering's "Cannot Ignite, Chill, Freeze or Shock" and Stormblood's Sap and Brittle
        // (MonsterAlternateAilments).
        module.Lifetime.Own(MonsterElementalAilments.HandOverInflictProhibitions(Rules.SecretsOfSufferingProhibitions));
        module.Lifetime.Own(UtilityFlasks.HandOverDuringEffectFlags(Rules.AlternateAilmentFlaskFlags));
        module.Lifetime.Own(global::Sirus.CleanupOnUnload());
        Rules.InitializeAccountHideouts(module.Settings<Rules.HideoutMigrationSettings>().LegacySourceCharacter);
        // Build the optional melee clock tables before combat can admit an action, rather than inside its send gate.
        Program.Skills.WarmMonsterMeleeClocks();
        WarmCatalogues();
        Program.SetGameLogic(new GameLogic(module.Lifetime));
        module.Lifetime.Own(Rules.InstallJoining());
        string id = module.Manifest.Id;
        GameEvents.PlayerLeaving.Add(id, (player, _) => { Rules.LeaveArea(player); return Task.CompletedTask; });
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::DoedreSewers.Area, global::DoedreSewers.Tick));
        GameEvents.ObjectUse.Add(module.Manifest.Id, global::DoedreSewers.Use);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::DoedreSewers.PlayerStats);
        module.Lifetime.Own(global::DoedreSewers.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::DoedreSewers.Leaving(player));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::BrineKing.Area, global::BrineKing.Tick));
        GameEvents.AreaTick.Add(id, GameEvents.Always((player, _) => global::AtlasBosses.Tick(player)));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::VaalOversoul.Area, global::VaalOversoul.Tick));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::DepravedTrinity.Area, global::DepravedTrinity.Tick));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::Arakaali.Area, global::Arakaali.Tick));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::Arakaali.Area, global::ArakaaliLasers.Tick));
        module.Lifetime.Own(global::ArakaaliLasers.CleanupOnUnload());
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::Yugul.Area, global::Yugul.Tick));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::SolarisLunaris.Area, global::SolarisLunaris.Tick));
        module.Lifetime.Own(global::SolarisLunaris.CleanupOnUnload());
        module.Lifetime.Own(global::Yugul.CleanupOnUnload());
        module.Lifetime.Own(global::Arakaali.CleanupOnUnload());
        module.Lifetime.Own(global::DepravedTrinity.CleanupOnUnload());
        GameEvents.ObjectUse.Add(module.Manifest.Id, global::DepravedTrinity.Use);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::DepravedTrinity.PlayerStats);
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::DepravedTrinity.Leaving(player));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::KitavaActFive.Area, global::KitavaActFive.Tick));
        module.Lifetime.Own(global::KitavaActFive.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::KitavaActFive.Leaving(player));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::KitavaActTen.Area, global::KitavaActTen.Tick));
        module.Lifetime.Own(global::KitavaActTen.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::KitavaActTen.Leaving(player));
        module.Lifetime.Own(global::VaalOversoul.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::VaalOversoul.Leaving(player));
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::BrineKing.PlayerStats);
        // Curses, Withered, grounds and the other debuffs monsters put on the player (Game.PlayerDebuffs).
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, Rules.PlayerDebuffStats);
        module.Lifetime.Own(global::BrineKing.CleanupOnUnload());
        module.Lifetime.Own(global::AtlasBosses.CleanupOnUnload());
        GameEvents.Kill.Add(module.Manifest.Id, Rules.ReservationKill);
        GameEvents.AreaTick.Add(id, GameEvents.Always((player, _) => global::Slayer.Tick(player)));
        GameEvents.Kill.Add(module.Manifest.Id, global::Slayer.Kill);
        GameEvents.Kill.Add(module.Manifest.Id, Rules.SpreadPoisonOnKill);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::Slayer.PlayerStats);
        GameEvents.OutgoingDamage.Add(module.Manifest.Id, global::Slayer.OutgoingDamage);
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::Slayer.Leaving(player));
        GameEvents.AreaTick.Add(id, GameEvents.Always((player, _) => global::Game.VaalImmortalCallTick(player)));
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::Game.LeavingVaalImmortalCall(player));
        GameEvents.AreaTick.Add(id, GameEvents.Always((player, _) => global::Hierophant.Tick(player)));
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::Hierophant.PlayerStats);
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::Hierophant.Leaving(player));
        GameEvents.Packet(InstanceEntities.ResetAllPassivesId).Add(module.Manifest.Id, async (player, _) =>
        {
            await Rules.ResetPassivesFree(player);
            return true;
        });
        GameEvents.PlayerLeaving.Add(id, (player, _) => global::BrineKing.Leaving(player));
        GameEvents.CompanionTick.Add(id, Rules.TickBrands);
        GameEvents.PlayerStatSources.Add(id, Rules.BrandPlayerStats);
        GameEvents.PlayerLeaving.Add(id, (player, _) => { Rules.ClearBrands(player); return Task.CompletedTask; });
        GameEvents.TransientClosing.Add(id, Rules.ClearBrands);
        GameEvents.GrantedSkillExecutable.Add(id, (_, effectId) => Rules.IsBrandsurgeEffect(effectId));
        GameEvents.Kill.Add(id, Rules.BrandOnMonsterKill);
        module.Lifetime.Own(Rules.CleanupBrandsOnUnload());
        GameEvents.AreaGenerated.Add(id, CharacterVoiceLines.OnAreaGenerated);
        GameEvents.Kill.Add(id, CharacterVoiceLines.OnKill);
        GameEvents.AreaHeader.Add(id, _ => new AreaHeader(null, [InstanceAchievements.PermanentAchievementSetId]));
        GameEvents.EnterMessages.Add(id, Rules.AchievementEnterMessages);
        GameEvents.PlayerEntered.Add(id, Rules.CheckAreaEntryAchievements);
        GameEvents.Kill.Add(id, Rules.OnMonsterKillAchievements);
    }
}

public sealed class GameLogic(IModuleLifetime? lifetime = null) : IGameLogic, IControlledPlayerRules, IControlledPlayerCommands, IWorldAdmissionRules
{
    public IModuleLifetime? Lifetime { get; } = lifetime ?? Rules.CombatLifetime;
    public bool WorldAdmissionActive => Lifetime is { IsActive: true } && ReferenceEquals(Lifetime, Rules.CombatLifetime);
    public double MaximumLeaderDistance => Rules.ControlledFollowerLeash;
    public ItemModStats.ModifierValueRule? ItemModifierValues(Item item, ModCatalog mods) =>
        HeistModifierMagnitudes.For(item, mods);
    public void ValidateImportedBuild(GameServer.Core.Accounts.Character character, CharacterSaves.Save save) =>
        Rules.ValidateImportedBuild(character, save);
    public void QueueControlledIntent(Player player, ControlledPlayerIntent intent) => Rules.QueueControlledIntent(player, intent);
    public void ClearControlledIntent(Player player) => Rules.ClearControlledIntent(player);
    public object ObserveControlledPlayer(Player player, Player leader) => Rules.ObserveControlledPlayer(player, leader);
    public void ResumePlayerUpdates(Player player)
    {
        if (ReferenceEquals(Program.GameLogicOrNull, this) && ReferenceEquals(Lifetime, Rules.CombatLifetime))
            Rules.EnsurePlayerUpdates(player);
    }
    public Task WorldFrameEnded(Player player, double nextFrameStart) =>
        Rules.LockstepFrameEnded(player, nextFrameStart);
    public Task OnClientMessages(IReadOnlyList<(ushort Id, byte[] Bytes)> messages, byte[] unknown, Player player, long readAt) =>
        Rules.OnClientMessages(messages, unknown, player, readAt);

    public void Admitting(Player player) => Rules.RegisterVitalsAdmission(player);

    /// <summary>GGG's login rule: one connection holds a character at a time. The newer login takes it and the older client
    /// is told and dropped, and this one's character is loaded only after that connection saved what it held, so the two
    /// never write the same character (or its items) from two states.</summary>
    public async Task<bool> AdmittingAsync(Player player)
    {
        // A Ruthless league's data set is built for its first character, before that character is loaded.
        if (Program.LeagueFor(player) is { HardMode: true } league) await Task.Run(() => Rules.LoadHardModeData(league));
        foreach (var previous in Rules.ClaimCharacter(player))
        {
            // An area change: the old connection handed the character over (Travel.Transfer) and the client drops it once
            // this one is up (09 §6). It is not another login, and main's transfer path already orders its saves.
            if (previous.TransferAccepted) continue;
            Console.WriteLine($"[gateway] {Program.CharacterName(player)}: another login took the character; ending the older connection");
            try
            {
                await Program.EndAsync(previous, "another login took this character",
                    "This character was entered from another client: you have been disconnected.");
            }
            catch (TimeoutException)
            {
                Console.WriteLine($"[gateway] {Program.CharacterName(player)}: the older connection did not finish ending within "
                    + $"{Program.EndingTimeout.TotalSeconds:0} s; this login is refused");
                return false;
            }
        }
        return true;
    }

    public int PrepareCharacter(Player player, int level)
    {
        Rules.RefuseInterruptedHardcoreDeath(player);
        level = Rules.PrepareCharacterForArea(player, level);
        global::KitavaActTen.ConfigurePortal(player);
        // Restore mode vitals before any admitted-area send can fail and enter save cleanup.
        Rules.InitializeCharacterVitals(player);
        return level;
    }

    public IReadOnlyDictionary<uint, int>? AreaLeagueStats(Player player) => Rules.LeagueStatsFor(player);

    public async Task Entered(Player player, int level, int ticketLevel)
    {
        // The client generates the area from here (S→C 0x1d) until its 0x7d: the packs are planned meanwhile.
        Rules.PlanPopulationAhead(player);
        if (level != ticketLevel)
        {
            // The saved experience is the character's level: Auth's (the ticket's) was not updated at the last level-up.
            var fixedLevel = await Rules.SynchronizeAccountLevel(player);
            Console.WriteLine($"[gateway] {player.Connection.Remote} {Program.CharacterName(player)}: level {level} from the saved experience (the account had {ticketLevel})" +
                (fixedLevel.Ok ? "" : $"; not saved to the account: {fixedLevel.Error}"));
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} {Program.CharacterName(player)}: {Rules.SkillsFor(player).ClassFor(player.Character.ClassRow).ObjectType} (Characters row {player.Character.ClassRow}), level {player.Pools.Level} (experience {player.Character.Experience}, {player.Character.Passives.Count} passive(s), life {player.Pools.Life}, mana {player.Pools.Mana}, attack speed +{player.Pools.AttackSpeedPct}%), league {player.Character.League ?? "?"}");
    }

    public void Closing(Player player)
    {
        foreach (var close in GameEvents.TransientClosing.For(player)) close(player);
        Rules.EndEscapePause(player);
        player.PaidChannel = null;
        player.NonChaosDotBypassCarry = player.NonChaosDotTakenCarry = player.ChaosDotBypassCarry = 0;
        player.IncomingAilments.Clear();
        Rules.ClearPlayerDecay(player);
        Rules.ClearCorruptedBlood(player);
        Rules.ClearEssenceAuraExposure(player);
        player.RetirePlayerUpdates();
        Rules.FortificationOf(player).Clear();
        Rules.LeechOf(player).Clear(Rules.AreaTime(player));
        player.Rage.Clear();
        player.Rampage.Clear();
        player.RetireOwnedCombat();
        player.RetireResourceEffects();
        player.VaalPreventionUntil = 0;
    }

    public void Save(Player player, bool prepared)
    {
        // A refused admission must not load/start inventories or flush a previous connection's cached copy.
        if (prepared)
        {
            CharacterSaves.SaveExperience(Program.CharacterName(player));
            Rules.ItemsOf(player).SaveUnsaved();
            Rules.PersistVitals(player);
            Rules.SaveGolems(player);
        }
        Rules.CommitHardcoreDeath(player);
    }

    public void Left(Player player)
    {
        Rules.ReleaseCharacter(player);
        Rules.ForgetVitalsAdmission(player);
        Rules.ForgetSourceValues(player);
        // The instance keeps its monsters as they are now, for the next connection (#12); the player's minions go with him.
        Rules.KeepInstanceMonsters(player);
    }

    public IDisposable ProjectileMutation(Player player) => Rules.ProjectileMutation(player, advance: false);
    public IGamePlayer PlayerFor(Player player) => new Rules.ModPlayer(player);
    public IPlayerWorld WorldFor(Player player) => new Rules.ModuleWorld(player);
    public IEnumerable<(string Value, string Detail)>? CompletionValues(string kind) => Rules.CompletionValues(kind);
    public double PlayerActionFactor(Player player) => Rules.PlayerActionFactor(player);
    public CharacterSaves.Save BuildControlled(GameServer.Core.Accounts.CharacterClass characterClass, int level) => Rules.BuildControlledPlayer(characterClass, level);
    public Task InitializeControlled(Player player, Player leader) => Rules.InitializeControlledPlayer(player, leader);
    public Task StepControlled(Player player, Player leader) => Rules.StepControlledPlayer(player, leader);
    public Task ControlledCompletion(Player player) => player.PlayerUpdatesState is PlayerUpdatesState updates ? updates.Completion : Task.CompletedTask;
}
