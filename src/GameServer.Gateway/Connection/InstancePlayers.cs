using GameServer.Core.Protocol.Instance;

/// <summary>Immutable published poses; observers never lock another connection's simulation.</summary>
sealed record InstancePlayer(uint Id, uint Type, PlayerObject Template, int X, int Y, uint Life,
    ClientState? Owner = null)
{
    // Only session liveness is read through the owner; all replicated values remain immutable snapshots.
    public bool Current => Owner is null || !Owner.Closed && !Owner.LeavingArea && Owner.PlayerSent;
    /// <summary>Where the player is walking to (its walk's last cell; its own cell when standing). Observers start a Move
    /// action toward it, which their client paths and animates, instead of gliding the body from pose to pose.</summary>
    public (int X, int Y)? Destination { get; init; }
}

/// <summary>Player presence shared by one Auth instance, independently of its characters' private world state.</summary>
sealed class InstancePresence
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<uint, InstancePlayer> _players = new();
    public void PublishPlayer(InstancePlayer player) => _players[player.Id] = player;
    public void RemovePlayer(uint id) => _players.TryRemove(id, out _);
    public InstancePlayer[] PlayersExcept(uint id) => [.. _players.Values.Where(p => p.Id != id)];
    public InstancePlayer? Player(uint id) => _players.TryGetValue(id, out var p) && p.Current ? p : null;
}

sealed record MonsterView((int X, int Y) At, (int X, int Y) To, uint Life);
sealed record SharedMonsterOrigin(uint Type, GameServer.Core.Protocol.Instance.MonsterObject Object, ushort Row, byte Level, MonsterRarity Rarity);
sealed record SharedMonster(uint Id, SharedMonsterOrigin Origin, SpawnedMonster Source, int X, int Y, (int X, int Y) Destination, uint Life);

partial class Program
{
    /// <summary>The connections in game now (entered an area, not yet gone), for what plays beside them without one of its
    /// own: server-driven players (the PlayerBots module) look up the towns players are in.</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<ClientState, byte> InGame = new();

    /// <summary>Sets the player's party size; a change tells the client the players in the area (S→C 0x231: the party
    /// panel's bonus icons, which compute their percentages from the client's own constants, Game.PartyBonus) and the
    /// player the bonus in chat.</summary>
    internal static async Task SetPartySize(ClientState conn, int size)
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

sealed partial class InstanceState
{
    public InstanceState() : this(null) { }
    internal InstanceState(InstancePresence? presence) => Presence = presence ?? new();

    internal InstancePresence Presence { get; }
    public void PublishPlayer(InstancePlayer player) => Presence.PublishPlayer(player);
    public void RemovePlayer(uint id) => Presence.RemovePlayer(id);
    public InstancePlayer[] PlayersExcept(uint id) => Presence.PlayersExcept(id);
    public InstancePlayer? Player(uint id) => Presence.Player(id);
}

sealed partial class ClientState
{
    static int _nextPeerId = 0x20000000;
    public readonly uint PeerId = unchecked((uint)Interlocked.Increment(ref _nextPeerId));
    public PlayerObject? PeerTemplate;
    public uint PeerType;
    public readonly Dictionary<uint, InstancePlayer> SeenPlayers = [];
    /// <summary>The instance's ground items this client has (SharedGround.cs).</summary>
    public readonly HashSet<uint> SeenGround = [];
    /// <summary>The instance's monsters as this client was last shown them (SharedMonsters.cs SyncInstanceMonsters).</summary>
    public readonly Dictionary<uint, MonsterView> MonsterViews = [];
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
    /// <summary>The open player trade (Program.Social), and the player this one asked to trade with (its PeerId).</summary>
    public PlayerTrade? Trade;
    public uint TradeRequestTo;
}
