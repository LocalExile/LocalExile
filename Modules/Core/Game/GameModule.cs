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
        Program.SetGameLogic(new GameLogic());
    }
}

sealed class GameLogic : IGameLogic
{
    public Task OnClientMessages(string remote, NetworkStream stream, ConnectionCiphers ciphers, IReadOnlyList<(ushort Id, byte[] Bytes)> messages, byte[] unknown, ClientState conn, long readAt) =>
        Rules.OnClientMessages(remote, stream, ciphers, messages, unknown, conn, readAt);

    public void Admitting(ClientState conn) => Rules.RegisterVitalsAdmission(conn);

    public int PrepareCharacter(string remote, ClientState conn, int level)
    {
        Rules.RefuseInterruptedHardcoreDeath(conn);
        level = Rules.PrepareCharacterForArea(remote, conn, level);
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
        Rules.FortificationOf(conn).Clear();
        Rules.LeechOf(conn).Clear(conn.Clock.Elapsed.TotalSeconds);
        conn.Rage.Clear();
        conn.Rampage.Clear();
        conn.RetireOwnedCombat();
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
}
