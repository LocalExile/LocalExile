using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using Rules = global::Game;

namespace GameServer.Modules.Game;

/// <summary>The game's rules: gives the framework its <see cref="IGameLogic"/>.</summary>
public sealed class GameModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        Rules.CombatLifetime = module.Lifetime;
        Program.SetGameLogic(new GameLogic(module.Lifetime));
        string id = module.Manifest.Id;
        GameEvents.Tick.Add(id, (conn, _) => global::DoedreSewers.Tick(conn));
        GameEvents.ObjectUse.Add(module.Manifest.Id, global::DoedreSewers.Use);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::DoedreSewers.PlayerStats);
        module.Lifetime.Own(global::DoedreSewers.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::DoedreSewers.Leaving(conn));
        GameEvents.Tick.Add(id, (conn, _) => global::BrineKing.Tick(conn));
        GameEvents.Tick.Add(id, (conn, _) => global::AtlasBosses.Tick(conn));
        GameEvents.Tick.Add(id, (conn, _) => global::PantheonBosses.Tick(conn));
        GameEvents.Tick.Add(id, (conn, _) => global::VaalOversoul.Tick(conn));
        GameEvents.Tick.Add(id, (conn, _) => global::DepravedTrinity.Tick(conn));        module.Lifetime.Own(global::DepravedTrinity.CleanupOnUnload());
        GameEvents.ObjectUse.Add(module.Manifest.Id, global::DepravedTrinity.Use);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::DepravedTrinity.PlayerStats);
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::DepravedTrinity.Leaving(conn));
        GameEvents.Tick.Add(id, (conn, _) => global::KitavaActFive.Tick(conn));
        module.Lifetime.Own(global::KitavaActFive.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::KitavaActFive.Leaving(conn));
        GameEvents.Tick.Add(id, (conn, _) => global::KitavaActTen.Tick(conn));
        module.Lifetime.Own(global::KitavaActTen.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::KitavaActTen.Leaving(conn));
        module.Lifetime.Own(global::VaalOversoul.CleanupOnUnload());
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::VaalOversoul.Leaving(conn));
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::BrineKing.PlayerStats);
        module.Lifetime.Own(global::BrineKing.CleanupOnUnload());
        module.Lifetime.Own(global::AtlasBosses.CleanupOnUnload());
        module.Lifetime.Own(global::PantheonBosses.CleanupOnUnload());
        GameEvents.Kill.Add(module.Manifest.Id, Rules.ReservationKill);
        GameEvents.Tick.Add(id, (conn, _) => global::Slayer.Tick(conn));
        GameEvents.Kill.Add(module.Manifest.Id, global::Slayer.Kill);
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, global::Slayer.PlayerStats);
        GameEvents.OutgoingDamage.Add(module.Manifest.Id, global::Slayer.OutgoingDamage);
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::Slayer.Leaving(conn));
        GameEvents.Tick.Add(id, (conn, _) => global::Hierophant.Tick(conn));
        module.Lifetime.Own(GameEvents.PlayerStatSources.Add("Game", global::Hierophant.PlayerStats));
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::Hierophant.Leaving(conn));
        GameEvents.PlayerLeaving.Add(id, (conn, _) => global::BrineKing.Leaving(conn));
    }
}

public sealed class GameLogic(IModuleLifetime? lifetime = null) : IGameLogic, IControlledPlayerRules, IControlledPlayerCommands, IWorldAdmissionRules
{
    public IModuleLifetime? Lifetime { get; } = lifetime ?? Rules.CombatLifetime;
    public bool WorldAdmissionActive => Lifetime is { IsActive: true } && ReferenceEquals(Lifetime, Rules.CombatLifetime);
    public double MaximumLeaderDistance => Rules.ControlledFollowerLeash;
    public void QueueControlledIntent(ClientState player, ControlledPlayerIntent intent) => Rules.QueueControlledIntent(player, intent);
    public void ClearControlledIntent(ClientState player) => Rules.ClearControlledIntent(player);
    public object ObserveControlledPlayer(ClientState player, ClientState leader) => Rules.ObserveControlledPlayer(player, leader);
    public void ResumePlayerUpdates(ClientState conn)
    {
        if (ReferenceEquals(Program.GameLogicOrNull, this) && ReferenceEquals(Lifetime, Rules.CombatLifetime))
            Rules.EnsurePlayerUpdates(conn);
    }
    public Task OnClientMessages(string remote, NetworkStream stream, ConnectionCiphers ciphers, IReadOnlyList<(ushort Id, byte[] Bytes)> messages, byte[] unknown, ClientState conn, long readAt) =>
        Rules.OnClientMessages(remote, stream, ciphers, messages, unknown, conn, readAt);

    public void Admitting(ClientState conn) => Rules.RegisterVitalsAdmission(conn);

    public int PrepareCharacter(string remote, ClientState conn, int level)
    {
        Rules.RefuseInterruptedHardcoreDeath(conn);
        level = Rules.PrepareCharacterForArea(remote, conn, level);
        global::KitavaActTen.ConfigurePortal(conn);
        // Restore mode vitals before any admitted-area send can fail and enter save cleanup.
        Rules.InitializeCharacterVitals(conn);
        return level;
    }

    public IReadOnlyDictionary<uint, int>? AreaLeagueStats(ClientState conn) => Rules.LeagueStatsFor(conn);

    public async Task Entered(string remote, ClientState conn, int level, int ticketLevel)
    {
        if (level != ticketLevel)
        {
            // The saved experience is the character's level: Auth's (the ticket's) was not updated at the last level-up.
            var fixedLevel = await Rules.SynchronizeAccountLevel(conn);
            Console.WriteLine($"[gateway] {remote} {Program.CharacterName(conn)}: level {level} from the saved experience (the account had {ticketLevel})" +
                (fixedLevel.Ok ? "" : $"; not saved to the account: {fixedLevel.Error}"));
        }
        Console.WriteLine($"[gateway] {remote} {Program.CharacterName(conn)}: {Rules.SkillsFor(conn).ClassFor(conn.ClassRow).ObjectType} (Characters row {conn.ClassRow}), level {conn.Pools.Level} (experience {conn.Experience}, {conn.Passives.Count} passive(s), life {conn.Pools.Life}, mana {conn.Pools.Mana}, attack speed +{conn.Pools.AttackSpeedPct}%), league {conn.League ?? "?"}");
    }

    public void Closing(ClientState conn)
    {
        foreach (var close in GameEvents.TransientClosing.For(conn)) close(conn);
        conn.PaidChannel = null;
        conn.NonChaosDotBypassCarry = conn.NonChaosDotTakenCarry = 0;
        conn.RetirePlayerUpdates();
        Rules.FortificationOf(conn).Clear();
        Rules.LeechOf(conn).Clear(conn.Clock.Elapsed.TotalSeconds);
        conn.Rage.Clear();
        conn.Rampage.Clear();
        conn.RetireOwnedCombat();
        conn.RetireResourceEffects();
        conn.VaalPreventionUntil = 0;
    }

    public void Save(ClientState conn, bool prepared)
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

    public void Left(ClientState conn)
    {
        Rules.ForgetVitalsAdmission(conn);
        // The instance keeps its monsters as they are now, for the next connection (#12); the player's minions go with him.
        if (conn.MonstersPlaced) Rules.KeepInstanceMonsters(conn);
    }

    public IDisposable ProjectileMutation(ClientState conn) => Rules.ProjectileMutation(conn, advance: false);
    public IConnectionPlayer PlayerFor(ClientState conn) => new Rules.ModPlayer(conn);
    public IPlayerWorld WorldFor(ClientState conn) => new Rules.ModuleWorld(conn);
    public IEnumerable<(string Value, string Detail)>? CompletionValues(string kind) => Rules.CompletionValues(kind);
    public double PlayerActionFactor(ClientState conn) => Rules.PlayerActionFactor(conn);
    public CharacterSaves.Save BuildControlled(GameServer.Core.Accounts.CharacterClass characterClass, int level) => Rules.BuildControlledPlayer(characterClass, level);
    public Task InitializeControlled(ClientState player, ClientState leader) => Rules.InitializeControlledPlayer(player, leader);
    public Task StepControlled(ClientState player, ClientState leader) => Rules.StepControlledPlayer(player, leader);
    public Task ControlledCompletion(ClientState player) => player.PlayerUpdatesState is PlayerUpdatesState updates ? updates.Completion : Task.CompletedTask;
}
