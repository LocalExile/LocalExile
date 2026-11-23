using GameServer.Core.Protocol.Instance;

/// <summary>An aura a player shares with the allies in its radius, at the caster's effect.</summary>
public sealed record SharedAura(AuraCatalog.Aura Aura, int Radius, int Effect);

/// <summary>The players of one Auth instance: the area's, and in an SSF town the other solo worlds' sharing it (chat,
/// party and friends see them; their world state stays apart). A player is in it from entering until leaving.</summary>
public sealed class InstancePresence
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<uint, Player> _players = new();
    public void AddPlayer(Player player) => _players[player.Id] = player;
    public void RemovePlayer(uint id) => _players.TryRemove(id, out _);
    /// <summary>The players in game here now, but <paramref name="id"/>.</summary>
    public Player[] PlayersExcept(uint id) =>
        _players.IsEmpty || (_players.Count == 1 && _players.ContainsKey(id))
            ? []
            : [.. _players.Values.Where(p => p.Id != id && InGame(p))];
    public Player? Player(uint id) => _players.TryGetValue(id, out var p) && InGame(p) ? p : null;
    static bool InGame(Player p) => !p.Closed && !p.LeavingArea && p.Connection.PlayerSent;
}

/// <summary>The instance's events (InstanceWorld): what a module can add to the shared instance.</summary>
public static class InstanceEvents
{
    /// <summary>An instance to join for a transfer without a chosen one (a party member's), 0 for the default choice.</summary>
    public static readonly GameEvent<Func<Player, PendingTransfer, ulong>> TransferInstance = new();

    /// <summary>The area stats a new instance is played with (0x1d's first stat list, 1-based Stats ids): the client's
    /// level generator reads them, so the Gateway generates the level with the same list. Asked once per instance, by
    /// its first connection, before that connection's state exists; the answers are summed.</summary>
    public static readonly GameEvent<Func<AreaStatsRequest, IReadOnlyDictionary<uint, int>?>> AreaStats = new();

    /// <summary>The league's answers to <see cref="AreaStats"/>, summed; a failing answer counts as none.</summary>
    public static IReadOnlyDictionary<uint, int> ComposeAreaStats(LeagueRuntime league, AreaStatsRequest request)
    {
        var sum = new Dictionary<uint, int>();
        foreach (var compose in AreaStats.For(league))
        {
            IReadOnlyDictionary<uint, int>? part;
            try { part = compose(request); }
            catch (Exception ex)
            {
                Console.WriteLine($"[gateway] area stats of 0x{request.Area:x4} for {request.Character}: {ex.GetType().Name}: {ex.Message}");
                continue;
            }
            foreach (var (key, value) in part ?? new Dictionary<uint, int>()) sum[key] = sum.GetValueOrDefault(key) + value;
        }
        return sum;
    }

    /// <summary>The league content category the instance is played in (S→C 0x1d +0x28, client world +0xd0;
    /// <see cref="GameServer.Core.World.LeagueCategory"/>): the client's topology pick, SpecialMapPins <c>__col5</c> and the
    /// object types' <c>league_category</c> read it, so the Gateway generates the level with it too. The first answer of
    /// the league's modules wins; none is 60.</summary>
    public static readonly GameEvent<Func<AreaStatsRequest, int?>> LeagueCategory = new();

    /// <summary>The league's first answer to <see cref="LeagueCategory"/>, or 60; a failing answer counts as none.</summary>
    public static int ComposeLeagueCategory(LeagueRuntime league, AreaStatsRequest request)
    {
        foreach (var compose in LeagueCategory.For(league))
        {
            int? value;
            try { value = compose(request); }
            catch (Exception ex)
            {
                Console.WriteLine($"[gateway] league category of 0x{request.Area:x4} for {request.Character}: {ex.GetType().Name}: {ex.Message}");
                continue;
            }
            if (value is not null) return GameServer.Core.World.LeagueCategory.Clamp(value);
        }
        return GameServer.Core.World.LeagueCategory.None;
    }

    /// <summary>The terrain plugin states a new instance is played with (S→C 0x1d extra data, second flag byte bit 0: per
    /// plugin its TerrainPlugins <c>__col1</c> id and the plugin's bytes). The client's plugin builds its part of the level
    /// from them, so the Gateway generates the level with the same. Asked once per instance
    /// by its first connection, with the area stats; per plugin the last answer wins (a mod after the modules).</summary>
    public static readonly GameEvent<Func<AreaStatsRequest, IReadOnlyList<(ushort Plugin, byte[] State)>?>> TerrainPluginStates = new();

    /// <summary>The league's answers to <see cref="TerrainPluginStates"/>, the last per plugin, a failing answer counting as
    /// none. Only a state the server's generator ports is kept: one for the area's own plugin when its generation is ported,
    /// or one for another plugin whose extra terrain feature row is (LevelGenerator.LeaguePluginFeature). The client would
    /// build any other plugin's part of the level and the server could not.</summary>
    public static IReadOnlyList<(ushort Plugin, byte[] State)> ComposeTerrainPluginStates(LeagueRuntime league, AreaStatsRequest request)
    {
        var states = new Dictionary<ushort, byte[]>();
        foreach (var compose in TerrainPluginStates.For(league))
        {
            IReadOnlyList<(ushort Plugin, byte[] State)>? part;
            try { part = compose(request); }
            catch (Exception ex)
            {
                Console.WriteLine($"[gateway] terrain plugin states of 0x{request.Area:x4} for {request.Character}: {ex.GetType().Name}: {ex.Message}");
                continue;
            }
            foreach (var (plugin, state) in part ?? [])
            {
                if (states.ContainsKey(plugin)) Console.WriteLine($"[gateway] terrain plugin {plugin} state of 0x{request.Area:x4} replaced by a later answer");
                states[plugin] = state;
            }
        }
        var own = AreaTerrainPlugins.Of(request.Area);
        foreach (var (plugin, state) in states.ToList())
        {
            bool ported = own is { } o && o.Id == plugin
                ? GameServer.Data.Terrain.Generation.LevelGenerator.PortsPlugin(o.Name)
                : GameServer.Data.Terrain.Generation.LevelGenerator.LeaguePluginFeature(AreaTerrainPlugins.NameOf(plugin), state) is not null;
            if (ported) continue;
            Console.WriteLine($"[gateway] terrain plugin {plugin} state of 0x{request.Area:x4} dropped: its generation is not ported");
            states.Remove(plugin);
        }
        return [.. states.Select(p => (p.Key, p.Value))];
    }

    /// <summary>Two stat lists summed (the area stats and the league's own, both sent in 0x1d's first list).</summary>
    public static IReadOnlyDictionary<uint, int>? Merge(IReadOnlyDictionary<uint, int>? a, IReadOnlyDictionary<uint, int>? b)
    {
        if (a is not { Count: > 0 }) return b;
        if (b is not { Count: > 0 }) return a;
        var merged = new Dictionary<uint, int>(a);
        foreach (var (key, value) in b) merged[key] = merged.GetValueOrDefault(key) + value;
        return merged;
    }
}

/// <summary>What an instance's area stats are composed from: the character whose connection opens it, its league, the
/// area (WorldAreas HASH16) and the map it was opened with (<see cref="InstanceState.Map"/>), null outside a map.</summary>
public sealed record AreaStatsRequest(string Character, string? League, ushort Area, MapDevice.Run? Map);

public sealed record MonsterView((int X, int Y) At, (int X, int Y) To, uint Life)
{
    public byte ActorStance { get; init; }
    public bool CreatedAsCorpse { get; init; }
}
public sealed record SharedMonsterOrigin(uint Type, GameServer.Core.Protocol.Instance.MonsterObject Object, ushort Row, byte Level, MonsterRarity Rarity);
public sealed record SharedMonster(uint Id, SharedMonsterOrigin Origin, SpawnedMonster Source, int X, int Y, (int X, int Y) Destination,
    uint Life, uint EnergyShield = 0, uint MaximumEnergyShield = 0);

public partial class Program
{
    /// <summary>The connections in game now (entered an area, not yet gone), for what plays beside them without one of its
    /// own: server-driven players (the PlayerBots module) look up the towns players are in.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<Player, byte> InGame = new();

    /// <summary>Sets the player's party size; a change tells the client the players in the area (S→C 0x231: the party
    /// panel's bonus icons, which compute their percentages from the client's own constants, Game.PartyBonus) and the
    /// player the bonus in chat.</summary>
    public static async Task SetPartySize(Player player, int size)
    {
        size = Math.Max(1, size);
        if (player.PartySize == size) return;
        player.PartySize = size;
        int more = size - 1;
        string text = more == 0 ? "Party bonus: none"
            : $"Party bonus ({size} players): monster life +{50 * more}% normal, +{60 * more}% magic, +{80 * more}% rare, +{100 * more}% unique; currency items +{23 * more}%";
        if (player.Connection.Outbox is { } outbox)
        {
            await outbox(GameServer.Core.Protocol.Instance.InstanceChat.ChatLine(text), $"0x0017 chat line \"{text}\"");
            // The value the client scales by its constants: 100 per extra player (Game.PartyBonus).
            await outbox(GameServer.Core.Protocol.Instance.InstanceSocial.PartyModifiers((ushort)(100 + 100 * more), (byte)size, 0),
                $"0x0231 party modifiers: {size} players");
        }
    }
}

public sealed partial class InstanceState : IHasComponents
{
    /// <summary>What modules keep on this area (their stores).</summary>
    public Components Components { get; } = new();

    public InstanceState() : this(null) { }
    public InstanceState(InstancePresence? presence) => Presence = presence ?? new();

    public InstancePresence Presence { get; }
    public void AddPlayer(Player player) => Presence.AddPlayer(player);
    public void RemovePlayer(uint id) => Presence.RemovePlayer(id);
    public Player[] PlayersExcept(uint id) => Presence.PlayersExcept(id);
    public Player? Player(uint id) => Presence.Player(id);
}

public sealed partial class Player
{
    static int _nextPeerId = 0x20000000;
    // Settable when the state is built, so a test of a rule seeded by it does not depend on how many states came before.
    public uint Id { get; init; } = unchecked((uint)Interlocked.Increment(ref _nextPeerId));
    public PlayerObject? PeerTemplate;
    /// <summary>The auras this player shares with its allies, set when its stats change (RefreshPeerScale).</summary>
    public IReadOnlyList<SharedAura> SharedAuras = [];
    public uint PeerType;
    /// <summary>This player's minions as created (SharedMonsters.cs), and as of its last frame, for the other clients.</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<uint, SharedMonsterOrigin> MinionOrigins = new();
    sealed record TransferTarget(ushort Area, ulong Instance);
    TransferTarget? _transferredTo;

    /// <summary>Where Auth sent this connection's player (area, instance), from the transfer until the next connection:
    /// a party member who follows at once joins them there (Social's PartyInstance).</summary>
    public (ushort Area, ulong Instance)? TransferredTo
    {
        get => Volatile.Read(ref _transferredTo) is { } target ? (target.Area, target.Instance) : null;
        set => Volatile.Write(ref _transferredTo, value is { } target ? new TransferTarget(target.Area, target.Instance) : null);
    }
    /// <summary>This player's shares of the instance host's kills, for its own loop to apply (SharedKills.cs).</summary>
    public readonly System.Collections.Concurrent.ConcurrentQueue<Func<Player, Task>> KillShares = new();
    /// <summary>The party this player is in, counting the player (1: none): Social's parties and PlayerBots' (SetPartySize).
    /// Each member beyond the first gives Game.PartyBonus.</summary>
    public int PartySize = 1;
    public readonly Dictionary<(uint Peer, ushort Skill), AuraCatalog.Aura> ReceivedAuras = [];
    /// <summary>The open player trade (Program.Social), and the player this one asked to trade with (its PeerId).</summary>
    public PlayerTrade? Trade;
    public uint TradeRequestTo;
}
