using GameServer.Core.Protocol.Instance;

/// <summary>Immutable published poses; observers never lock another connection's simulation.</summary>
public sealed record InstancePlayer(uint Id, uint Type, PlayerObject Template, int X, int Y, uint Life,
    ClientState? Owner = null)
{
    // Only session liveness is read through the owner; all replicated values remain immutable snapshots.
    public bool Current => Owner is null || !Owner.Closed && !Owner.LeavingArea && Owner.PlayerSent;
    /// <summary>Where the player is walking to (its walk's last cell; its own cell when standing). Observers start a Move
    /// action toward it, which their client paths and animates, instead of gliding the body from pose to pose.</summary>
    public (int X, int Y)? Destination { get; init; }
    public IReadOnlyList<SharedAura> Auras { get; init; } = [];
    public double WalkSpeed { get; } = Owner?.Walk.Speed ?? Program.WalkSpeed;
    public (uint Source, int Movement) EnemySlow { get; init; }
}

public sealed record SharedAura(AuraCatalog.Aura Aura, int Radius, int Effect);

/// <summary>Player presence shared by one Auth instance, independently of its characters' private world state.</summary>
public sealed class InstancePresence
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<uint, InstancePlayer> _players = new();
    public void PublishPlayer(InstancePlayer player) => _players[player.Id] = player;
    public void RemovePlayer(uint id) => _players.TryRemove(id, out _);
    public InstancePlayer[] PlayersExcept(uint id) => [.. _players.Values.Where(p => p.Id != id)];
    public InstancePlayer? Player(uint id) => _players.TryGetValue(id, out var p) && p.Current ? p : null;
}

/// <summary>The instance's events (InstanceWorld): what a module can add to the shared instance.</summary>
public static class InstanceEvents
{
    /// <summary>An instance to join for a transfer without a chosen one (a party member's), 0 for the default choice.</summary>
    public static readonly GameEvent<Func<ClientState, PendingTransfer, ulong>> TransferInstance = new();

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
    /// none. Only a state for the area's own plugin, of a plugin the server's generator ports, is kept: the client would
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
        foreach (var plugin in states.Keys.ToList())
            if (own is not { } o || o.Id != plugin || !GameServer.Data.Terrain.Generation.LevelGenerator.PortsPlugin(o.Name))
            {
                Console.WriteLine($"[gateway] terrain plugin {plugin} state of 0x{request.Area:x4} dropped: not the area's own plugin, or not ported");
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
    // Snapshot from the shared origin last presented to this player. Each change to its stats replaces the list.
    public IReadOnlyList<(uint Key, int Value)>? OriginStats { get; init; }
    public bool CreatedAsCorpse { get; init; }
    public (ushort EmitterBuff, ushort ReceiverBuff, uint Source, int Rate) LifeAura { get; init; }
    public (ushort EmitterBuff, ushort ReceiverBuff, uint Source, int Rate) PhysicalDamageAura { get; init; }
    public (ushort EmitterBuff, ushort ReceiverBuff, uint Source, int Rate) EnemySlow { get; init; }
    public ((ushort EmitterBuff, ushort ReceiverBuff, uint Source, int Rate) Buff, int Cast, int Movement) SpeedAura { get; init; }
}
public sealed record SharedMonsterOrigin(uint Type, GameServer.Core.Protocol.Instance.MonsterObject Object, ushort Row, byte Level, MonsterRarity Rarity);
public sealed record SharedMonster(uint Id, SharedMonsterOrigin Origin, SpawnedMonster Source, int X, int Y, (int X, int Y) Destination,
    uint Life, uint EnergyShield = 0, uint MaximumEnergyShield = 0)
{
    public (uint Source, int Movement) EnemySlow { get; init; }
}

public partial class Program
{
    /// <summary>The connections in game now (entered an area, not yet gone), for what plays beside them without one of its
    /// own: server-driven players (the PlayerBots module) look up the towns players are in.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<ClientState, byte> InGame = new();

    /// <summary>Sets the player's party size; a change tells the client the players in the area (S→C 0x231: the party
    /// panel's bonus icons, which compute their percentages from the client's own constants, Game.PartyBonus) and the
    /// player the bonus in chat.</summary>
    public static async Task SetPartySize(ClientState conn, int size)
    {
        size = Math.Max(1, size);
        if (conn.PartySize == size) return;
        conn.PartySize = size;
        int more = size - 1;
        string text = more == 0 ? "Party bonus: none"
            : $"Party bonus ({size} players): monster life +{50 * more}% normal, +{60 * more}% magic, +{80 * more}% rare, +{100 * more}% unique; currency items +{23 * more}%";
        if (conn.Outbox is { } outbox)
        {
            await outbox(GameServer.Core.Protocol.Instance.InstanceChat.ChatLine(text), $"0x0017 chat line \"{text}\"");
            // The value the client scales by its constants: 100 per extra player (Game.PartyBonus).
            await outbox(GameServer.Core.Protocol.Instance.InstanceSocial.PartyModifiers((ushort)(100 + 100 * more), (byte)size, 0),
                $"0x0231 party modifiers: {size} players");
        }
    }
}

public sealed partial class InstanceState
{
    public InstanceState() : this(null) { }
    public InstanceState(InstancePresence? presence) => Presence = presence ?? new();

    public InstancePresence Presence { get; }
    public void PublishPlayer(InstancePlayer player) => Presence.PublishPlayer(player);
    public void RemovePlayer(uint id) => Presence.RemovePlayer(id);
    public InstancePlayer[] PlayersExcept(uint id) => Presence.PlayersExcept(id);
    public InstancePlayer? Player(uint id) => Presence.Player(id);
}

public sealed partial class ClientState
{
    static int _nextPeerId = 0x20000000;
    // Settable when the state is built, so a test of a rule seeded by it does not depend on how many states came before.
    public uint PeerId { get; init; } = unchecked((uint)Interlocked.Increment(ref _nextPeerId));
    public PlayerObject? PeerTemplate;
    public uint PeerType;
    public readonly Dictionary<uint, InstancePlayer> SeenPlayers = [];
    /// <summary>The instance's ground items this client has (SharedGround.cs).</summary>
    public readonly HashSet<uint> SeenGround = [];
    /// <summary>The instance's monsters as this client was last shown them (SharedMonsters.cs SyncInstanceMonsters).</summary>
    public readonly Dictionary<uint, MonsterView> MonsterViews = [];
    /// <summary>Instance scene snapshots already created on this connection, guarded by Monsters.</summary>
    public readonly HashSet<uint> SceneViews = [];
    /// <summary>The instance's monsters out of this client's wake zone (it was sent 0x1aa for them: Resync.KeepWakeZone).
    /// A monster is asleep (SpawnedMonster.Asleep: no AI, its walk stopped) only when it is out of every client's.</summary>
    public readonly HashSet<uint> AsleepMonsters = [];
    /// <summary>The instance's bosses whose life bar this client was shown (a boss's bar is each client's own).</summary>
    public readonly HashSet<uint> BossBarsSeen = [];
    /// <summary>This client's living monsters in its wake zone as of its last frame, sorted (SharedMonsters.cs).</summary>
    public volatile uint[] AwakeMonsters = [];
    /// <summary>The shared monsters this connection runs now (their AI, so their actions), sorted (SharedMonsters.cs).</summary>
    public volatile uint[] RunMonsters = [];
    /// <summary>Another player's projectile objects shown on this client, by (their PeerId, their object id), under ids of
    /// this client's own (SharedMonsters.cs MirrorProjectiles).</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<(uint Peer, uint Id), uint> MirroredObjects = new();
    public int NextMirroredObject = 0x0c000000; // below 0x40000000 too, as SharedMonsterIds
    /// <summary>This connection's projectile objects the other clients were sent.</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<uint, byte> MirroredProjectiles = new();
    /// <summary>This player's minions as created (SharedMonsters.cs), and as of its last frame, for the other clients.</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<uint, SharedMonsterOrigin> MinionOrigins = new();
    public volatile SharedMonster[] PublishedMinions = [];
    /// <summary>The last action of this player that the other clients were sent was a skill (not a walk): its end goes too.</summary>
    public bool PeerActing;
    /// <summary>Where Auth sent this connection's player (area, instance), from the transfer until the next connection:
    /// a party member who follows at once joins them there (Social's PartyInstance).</summary>
    public (ushort Area, ulong Instance)? TransferredTo;
    /// <summary>The buffs (object, instance) this client's additions of went to the others: only their removals follow.</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<(uint Id, ushort Instance), byte> MirroredBuffs = new();
    /// <summary>The other players' minions this client was shown, by (their PeerId, their id).</summary>
    public readonly Dictionary<(uint Peer, uint Id), SharedMonster> SeenMinions = [];
    /// <summary>This player's shares of the instance host's kills, for its own loop to apply (SharedKills.cs).</summary>
    public readonly System.Collections.Concurrent.ConcurrentQueue<Func<string, System.Net.Sockets.NetworkStream, GameServer.Core.Protocol.Login.ConnectionCiphers, Task>> KillShares = new();
    /// <summary>Sends to this connection's client, for another connection (Program.Social).</summary>
    public Func<byte[], string?, Task>? Outbox;
    /// <summary>The party this player is in, counting the player (1: none): Social's parties and PlayerBots' (SetPartySize).
    /// Each member beyond the first gives Game.PartyBonus.</summary>
    public int PartySize = 1;
    public readonly Dictionary<(uint Peer, ushort Skill), AuraCatalog.Aura> ReceivedAuras = [];
    /// <summary>The open player trade (Program.Social), and the player this one asked to trade with (its PeerId).</summary>
    public PlayerTrade? Trade;
    public uint TradeRequestTo;
}
