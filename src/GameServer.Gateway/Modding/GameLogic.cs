using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;

/// <summary>What the framework asks of the game. The <c>Game</c> module (Modules/Core/Game) gives it when it loads: the Gateway holds the
/// connections, the state and the saves, and the module the rules. Agents/docs/dev/modules.md.</summary>
public interface IGameLogic
{
    /// <summary>Validate an imported character's native progress against this game's rules, without saving it.</summary>
    void ValidateImportedBuild(GameServer.Core.Accounts.Character character, CharacterSaves.Save save) { }
    /// <summary>Claim this already active area's player updates after entry or game replacement.</summary>
    void ResumePlayerUpdates(Player player) { }
    /// <summary>A server frame of the connection ended (Program.RunWorldFrames); the next one starts at
    /// <paramref name="nextFrameStart"/>, on the connection's clock.</summary>
    Task WorldFrameEnded(Player player, double nextFrameStart) => Task.CompletedTask;
    /// <summary>A frame's shared world delta is about to be written to <paramref name="player"/> (before its frame window advances).</summary>
    void WritingWorldDelta(Player player, IReadOnlyList<AreaDelta.Record> delta) { }
    /// <summary>Prepare immutable item-value rules for this game generation, or null for the existing calculation.</summary>
    ItemModStats.ModifierValueRule? ItemModifierValues(Item item, ModCatalog mods) => null;
    /// <summary>An additive percentage to ignite duration resolved by the active game rules.</summary>
    int IgniteDurationIncrease(Func<uint, int> caster) => 0;
    /// <summary>One read's client messages, in order, on the connection's handler task.</summary>
    Task OnClientMessages(IReadOnlyList<(ushort Id, byte[] Bytes)> messages, byte[] unknown, Player player, long readAt);
    /// <summary>A connection is about to be prepared; it may fail after this, and <see cref="Left"/> still runs.</summary>
    void Admitting(Player player);
    /// <summary>Before <see cref="Admitting"/>: whether the framework goes on with this connection. A rule that lets one
    /// connection hold a character ends the one it replaces here (Program.EndAsync) and waits for that one's save, so this
    /// connection's load cannot race it. False refuses the connection: its thread still runs <see cref="Save"/> and
    /// <see cref="Left"/>, and its character was not prepared.</summary>
    Task<bool> AdmittingAsync(Player player) => Task.FromResult(true);
    /// <summary>Loads the character into the connection, before the area is sent; the character's level.</summary>
    int PrepareCharacter(Player player, int level);
    /// <summary>What the area's header says of the league (S→C 0x1d).</summary>
    IReadOnlyDictionary<uint, int>? AreaLeagueStats(Player player);
    /// <summary>The area was sent; level is what the character is, ticketLevel what Auth said.</summary>
    Task Entered(Player player, int level, int ticketLevel);
    /// <summary>The connection closes: stops what must not go on (Monsters lock held by the framework).</summary>
    void Closing(Player player);
    /// <summary>The connection ended: saves what waits in memory. prepared: its character was loaded.</summary>
    void Save(Player player, bool prepared);
    /// <summary>The connection ended, after <see cref="Save"/>.</summary>
    void Left(Player player);
    /// <summary>One projectile runtime's commit, as a scope; the framework's own state changes take it too.</summary>
    IDisposable ProjectileMutation(Player player);
    IGamePlayer PlayerFor(Player player);
    IPlayerWorld WorldFor(Player player);
    /// <summary>What an argument of this kind can be, for tools; null when there is no such kind.</summary>
    IEnumerable<(string Value, string Detail)>? CompletionValues(string kind);
    /// <summary>The character's action-speed factor (1 is normal), which scales attack, cast, movement and stun-recovery
    /// pacing: the game's own action_speed_-% bucket plus the ailments the framework tracks.</summary>
    double PlayerActionFactor(Player player);
    /// <summary>An extra multiplier applied by an active movement action (1 when none applies).</summary>
    double MovementSpeedMultiplier(Player player) => 1;
}

/// <summary>A player as mods see it, with its connection.</summary>
public interface IGamePlayer : IPlayer
{
    Player Player { get; }
}

/// <summary>A connection's projectile runtime, which belongs to the game module.</summary>
public interface IProjectileRuntime
{
    bool Active { get; }
    void Invalidate();
    /// <summary>The runtime's clock, in its ticks; a second is 120000.</summary>
    double NowTicks { get; }
}

/// <summary>Transient rule-owned work. Retirement retains only primitive cleanup receipts in the connection.</summary>
public interface ITransientCombatState
{
    void Clear();
    IReadOnlyCollection<uint> RetiredObjects => [];
    IReadOnlyCollection<(uint Actor, ushort Buff)> RetiredBuffs => [];
}
