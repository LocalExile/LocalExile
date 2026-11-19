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
        global::MapScarabBosses.LoadGameData();
        global::MapScarabContent.LoadGameData();
        global::MapScarabPacks.LoadGameData();
        global::StrongboxCatalog.LoadGameData();
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        Rules.CombatLifetime = module.Lifetime;
        // The game's systems in each area's own world (GameArea).
        AreaSetup.Load(module);
        global::Game.LoadStats(module);
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.InArea(global::Sirus.Area, global::Sirus.Tick));
        // A map device's portal labels read "<area> (Completed)" once that run's boss is dead (World/Portals.cs).
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always((conn, _) => Rules.TickMapRunBoss(conn)));
        GameEvents.MonsterKilled.Add(module.Manifest.Id, kill => Rules.TickMapRunBoss(Program.ConnOf(kill.Player), force: true));
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, _) => global::Sirus.Leaving(conn));
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::Sirus.PlayerStats);
        InstanceEvents.AreaStats.Add(module.Manifest.Id, Rules.MapAreaStats);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapScarabContent.Describe);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapScarabPacks.Opening);
        GameEvents.Drops.Add(module.Manifest.Id, MapScarabPacks.Drops);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapStrongboxes.Opening);
        GameEvents.AreaGenerated.Add(module.Manifest.Id, MapStrongboxes.Enter);
        GameEvents.ObjectUse.Add(module.Manifest.Id, MapStrongboxes.Use);
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always((conn, _) => MapStrongboxes.Tick(conn)));
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, _) => { MapStrongboxes.Leave(conn); return Task.CompletedTask; });
        GameEvents.StateBuilding.Add(module.Manifest.Id, MapStrongboxes.Describe);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapEssences.Opening);
        GameEvents.MapOpening.Add(module.Manifest.Id, MapScarabBosses.Opening);
        GameEvents.AreaGenerated.Add(module.Manifest.Id, MapEssences.Enter);
        GameEvents.ObjectUse.Add(module.Manifest.Id, MapEssences.Use);
        GameEvents.Drops.Add(module.Manifest.Id, MapEssences.Drops);
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, _) => { MapEssences.Leave(conn); return Task.CompletedTask; });
        GameEvents.StateBuilding.Add(module.Manifest.Id, MapEssences.Describe);
        module.Lifetime.Own(MonsterModEffects.HandOverMore(Rules.AtlasMoreLife, Rules.AtlasMoreDamage));
        module.Lifetime.Own(global::Sirus.CleanupOnUnload());
        Rules.InitializeAccountHideouts(module.Settings<Rules.HideoutMigrationSettings>().LegacySourceCharacter);
        // Build the optional melee clock tables before combat can admit an action, rather than inside its send gate.
        Program.Skills.WarmMonsterMeleeClocks();
        WarmCatalogues();
        Program.SetGameLogic(new GameLogic(module.Lifetime));
        module.Lifetime.Own(Rules.InstallMirror());
        string id = module.Manifest.Id;
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::DoedreSewers.Area, global::DoedreSewers.Tick));
        GameEvents.ObjectUse.Add(module.Manifest.Id, global::DoedreSewers.Use);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::DoedreSewers.PlayerStats);
        module.Lifetime.Own(global::DoedreSewers.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::DoedreSewers.Leaving(conn));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::BrineKing.Area, global::BrineKing.Tick));
        GameEvents.AreaTick.Add(id, GameEvents.Always((conn, _) => global::AtlasBosses.Tick(conn)));
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
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::DepravedTrinity.Leaving(conn));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::KitavaActFive.Area, global::KitavaActFive.Tick));
        module.Lifetime.Own(global::KitavaActFive.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::KitavaActFive.Leaving(conn));
        GameEvents.AreaTick.Add(id, GameEvents.InArea(global::KitavaActTen.Area, global::KitavaActTen.Tick));
        module.Lifetime.Own(global::KitavaActTen.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::KitavaActTen.Leaving(conn));
        module.Lifetime.Own(global::VaalOversoul.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::VaalOversoul.Leaving(conn));
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::BrineKing.PlayerStats);
        // Curses, Withered, grounds and the other debuffs monsters put on the player (Game.PlayerDebuffs).
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, Rules.PlayerDebuffStats);
        module.Lifetime.Own(global::BrineKing.CleanupOnUnload());
        module.Lifetime.Own(global::AtlasBosses.CleanupOnUnload());
        GameEvents.Kill.Add(module.Manifest.Id, Rules.ReservationKill);
        GameEvents.AreaTick.Add(id, GameEvents.Always((conn, _) => global::Slayer.Tick(conn)));
        GameEvents.Kill.Add(module.Manifest.Id, global::Slayer.Kill);
        GameEvents.Kill.Add(module.Manifest.Id, Rules.SpreadPoisonOnKill);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::Slayer.PlayerStats);
        GameEvents.OutgoingDamage.Add(module.Manifest.Id, global::Slayer.OutgoingDamage);
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::Slayer.Leaving(conn));
        GameEvents.AreaTick.Add(id, GameEvents.Always((conn, _) => global::Hierophant.Tick(conn)));
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::Hierophant.PlayerStats);
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::Hierophant.Leaving(conn));
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::BrineKing.Leaving(conn));
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
    public void ResumePlayerUpdates(Player conn)
    {
        if (ReferenceEquals(Program.GameLogicOrNull, this) && ReferenceEquals(Lifetime, Rules.CombatLifetime))
            Rules.EnsurePlayerUpdates(conn);
    }
    public Task WorldFrameEnded(Player conn, double nextFrameStart) =>
        Rules.LockstepFrameEnded(conn, nextFrameStart);
    public Task OnClientMessages(IReadOnlyList<(ushort Id, byte[] Bytes)> messages, byte[] unknown, Player conn, long readAt) =>
        Rules.OnClientMessages(messages, unknown, conn, readAt);

    public void Admitting(Player conn) => Rules.RegisterVitalsAdmission(conn);

    /// <summary>GGG's login rule: one connection holds a character at a time. The newer login takes it and the older client
    /// is told and dropped, and this one's character is loaded only after that connection saved what it held, so the two
    /// never write the same character (or its items) from two states.</summary>
    public async Task<bool> AdmittingAsync(Player conn)
    {
        // A Ruthless league's data set is built for its first character, before that character is loaded.
        if (Program.LeagueFor(conn) is { HardMode: true } league) await Task.Run(() => Rules.LoadHardModeData(league));
        foreach (var previous in Rules.ClaimCharacter(conn))
        {
            // An area change: the old connection handed the character over (Travel.Transfer) and the client drops it once
            // this one is up (09 §6). It is not another login, and main's transfer path already orders its saves.
            if (previous.TransferAccepted) continue;
            Console.WriteLine($"[gateway] {Program.CharacterName(conn)}: another login took the character; ending the older connection");
            try
            {
                await Program.EndAsync(previous, "another login took this character",
                    "This character was entered from another client: you have been disconnected.");
            }
            catch (TimeoutException)
            {
                Console.WriteLine($"[gateway] {Program.CharacterName(conn)}: the older connection did not finish ending within "
                    + $"{Program.EndingTimeout.TotalSeconds:0} s; this login is refused");
                return false;
            }
        }
        return true;
    }

    public int PrepareCharacter(Player conn, int level)
    {
        Rules.RefuseInterruptedHardcoreDeath(conn);
        level = Rules.PrepareCharacterForArea(conn, level);
        global::KitavaActTen.ConfigurePortal(conn);
        // Restore mode vitals before any admitted-area send can fail and enter save cleanup.
        Rules.InitializeCharacterVitals(conn);
        return level;
    }

    public IReadOnlyDictionary<uint, int>? AreaLeagueStats(Player conn) => Rules.LeagueStatsFor(conn);

    public async Task Entered(Player conn, int level, int ticketLevel)
    {
        // The client generates the area from here (S→C 0x1d) until its 0x7d: the packs are planned meanwhile.
        Rules.PlanPopulationAhead(conn);
        if (level != ticketLevel)
        {
            // The saved experience is the character's level: Auth's (the ticket's) was not updated at the last level-up.
            var fixedLevel = await Rules.SynchronizeAccountLevel(conn);
            Console.WriteLine($"[gateway] {conn.Remote} {Program.CharacterName(conn)}: level {level} from the saved experience (the account had {ticketLevel})" +
                (fixedLevel.Ok ? "" : $"; not saved to the account: {fixedLevel.Error}"));
        }
        Console.WriteLine($"[gateway] {conn.Remote} {Program.CharacterName(conn)}: {Rules.SkillsFor(conn).ClassFor(conn.ClassRow).ObjectType} (Characters row {conn.ClassRow}), level {conn.Pools.Level} (experience {conn.Experience}, {conn.Passives.Count} passive(s), life {conn.Pools.Life}, mana {conn.Pools.Mana}, attack speed +{conn.Pools.AttackSpeedPct}%), league {conn.League ?? "?"}");
    }

    public void Closing(Player conn)
    {
        foreach (var close in GameEvents.TransientClosing.For(conn)) close(conn);
        Rules.EndEscapePause(conn);
        conn.PaidChannel = null;
        conn.NonChaosDotBypassCarry = conn.NonChaosDotTakenCarry = conn.ChaosDotBypassCarry = 0;
        conn.IncomingAilments.Clear();
        conn.RetirePlayerUpdates();
        Rules.FortificationOf(conn).Clear();
        Rules.LeechOf(conn).Clear(Rules.AreaTime(conn));
        conn.Rage.Clear();
        conn.Rampage.Clear();
        conn.RetireOwnedCombat();
        conn.RetireResourceEffects();
        conn.VaalPreventionUntil = 0;
    }

    public void Save(Player conn, bool prepared)
    {
        // A refused admission must not load/start inventories or flush a previous connection's cached copy.
        if (prepared)
        {
            CharacterSaves.SaveExperience(Program.CharacterName(conn));
            Rules.ItemsOf(conn).SaveUnsaved();
            Rules.PersistVitals(conn);
            Rules.SaveGolems(conn);
        }
        Rules.CommitHardcoreDeath(conn);
    }

    public void Left(Player conn)
    {
        Rules.ReleaseCharacter(conn);
        Rules.ForgetVitalsAdmission(conn);
        // The instance keeps its monsters as they are now, for the next connection (#12); the player's minions go with him.
        Rules.KeepInstanceMonsters(conn);
    }

    public IDisposable ProjectileMutation(Player conn) => Rules.ProjectileMutation(conn, advance: false);
    public IConnectionPlayer PlayerFor(Player conn) => new Rules.ModPlayer(conn);
    public IPlayerWorld WorldFor(Player conn) => new Rules.ModuleWorld(conn);
    public IEnumerable<(string Value, string Detail)>? CompletionValues(string kind) => Rules.CompletionValues(kind);
    public double PlayerActionFactor(Player conn) => Rules.PlayerActionFactor(conn);
    public CharacterSaves.Save BuildControlled(GameServer.Core.Accounts.CharacterClass characterClass, int level) => Rules.BuildControlledPlayer(characterClass, level);
    public Task InitializeControlled(Player player, Player leader) => Rules.InitializeControlledPlayer(player, leader);
    public Task StepControlled(Player player, Player leader) => Rules.StepControlledPlayer(player, leader);
    public Task ControlledCompletion(Player player) => player.PlayerUpdatesState is PlayerUpdatesState updates ? updates.Completion : Task.CompletedTask;
}
