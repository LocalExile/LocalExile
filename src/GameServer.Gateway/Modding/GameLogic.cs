using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;

/// <summary>What the framework asks of the game. The <c>Game</c> module (Modules/Core/Game) gives it when it loads: the Gateway holds the
/// connections, the state and the saves, and the module the rules. Agents/docs/dev/modules.md.</summary>
interface IGameLogic
{
    /// <summary>One read's client messages, in order, on the connection's handler task.</summary>
    Task OnClientMessages(string remote, NetworkStream stream, ConnectionCiphers ciphers, IReadOnlyList<(ushort Id, byte[] Bytes)> messages, byte[] unknown, ClientState conn, long readAt);
    /// <summary>A connection is about to be prepared; it may fail after this, and <see cref="Left"/> still runs.</summary>
    void Admitting(ClientState conn);
    /// <summary>Loads the character into the connection, before the area is sent; the character's level.</summary>
    int PrepareCharacter(string remote, ClientState conn, int level);
    /// <summary>What the area's header says of the league (S→C 0x1d).</summary>
    IReadOnlyDictionary<uint, int>? AreaLeagueStats(ClientState conn);
    /// <summary>The area was sent; level is what the character is, ticketLevel what Auth said.</summary>
    Task Entered(string remote, ClientState conn, int level, int ticketLevel);
    /// <summary>The connection closes: stops what must not go on (Monsters lock held by the framework).</summary>
    void Closing(ClientState conn);
    /// <summary>The connection ended: saves what waits in memory. prepared: its character was loaded.</summary>
    void Save(ClientState conn, bool prepared);
    /// <summary>The connection ended, after <see cref="Save"/>.</summary>
    void Left(ClientState conn);
    /// <summary>One projectile runtime's commit, as a scope; the framework's own state changes take it too.</summary>
    IDisposable ProjectileMutation(ClientState conn);
    IConnectionPlayer PlayerFor(ClientState conn);
    IPlayerWorld WorldFor(ClientState conn);
    /// <summary>What an argument of this kind can be, for tools; null when there is no such kind.</summary>
    IEnumerable<(string Value, string Detail)>? CompletionValues(string kind);
    /// <summary>The character's action-speed factor (1 is normal), which scales attack, cast, movement and stun-recovery
    /// pacing: the game's own action_speed_-% bucket plus the ailments the framework tracks.</summary>
    double PlayerActionFactor(ClientState conn);
}

/// <summary>A player as mods see it, with its connection.</summary>
interface IConnectionPlayer : IPlayer
{
    ClientState Conn { get; }
}

/// <summary>A connection's projectile runtime, which belongs to the game module.</summary>
interface IProjectileRuntime
{
    bool Active { get; }
    void Invalidate();
    /// <summary>The runtime's clock, in its ticks; a second is 120000.</summary>
    double NowTicks { get; }
}

/// <summary>Transient rule-owned work. Retirement retains only primitive cleanup receipts in the connection.</summary>
interface ITransientCombatState
{
    void Clear();
    IReadOnlyCollection<uint> RetiredObjects => [];
    IReadOnlyCollection<(uint Actor, ushort Buff)> RetiredBuffs => [];
}
