using System.Collections.Immutable;
using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

/// <summary>A point where the framework hands over to the league's code, the modules and the mods, in the Gateway's own types: the one
/// hook system. The owner is the subscriber's id. A league's code runs for its own league's connections, a module's for the connections
/// whose league lists it, a mod's in every league while it is loaded. They run in that order: the league's code, then the modules', then the
/// mods'. When an owner unloads, its host removes every subscription it made (<see cref="GameEventOwners.Release"/>), so nobody has to keep
/// the handles.</summary>
public sealed class GameEvent<T> : GameEventOwners.IOwned where T : Delegate
{
    internal readonly record struct Subscriber(string Owner, int Rank, T Run, long Id)
    {
        public bool AppliesTo(LeagueRuntime league) => Rank == GameEventOwners.ModRank || league.Modules.Contains(Owner)
            || (Rank == GameEventOwners.LeagueRank && league.Id.Equals(Owner, StringComparison.OrdinalIgnoreCase));
    }

    ImmutableArray<Subscriber> _subscribers = [];
    internal ImmutableArray<Subscriber> Snapshot => _subscribers;
    long _nextSubscriberId, _revision;
    /// <summary>Changes when a provider is added or removed, for consumers that retain derived state.</summary>
    public long Revision => Volatile.Read(ref _revision);

    public GameEvent() => GameEventOwners.Register(this);

    public IDisposable Add(string owner, T run)
    {
        int rank = GameEventOwners.RankOf(owner);
        var subscriber = new Subscriber(owner, rank, run, Interlocked.Increment(ref _nextSubscriberId));
        ImmutableInterlocked.Update(ref _subscribers, s =>
        {
            int at = s.Length;
            while (at > 0 && s[at - 1].Rank > rank) at--;
            return s.Insert(at, subscriber);
        });
        Interlocked.Increment(ref _revision);
        return new Subscription(() =>
        {
            ImmutableInterlocked.Update(ref _subscribers, s => s.RemoveAll(e => ReferenceEquals(e.Run, run)));
            Interlocked.Increment(ref _revision);
        });
    }

    public IEnumerable<T> All => _subscribers.Select(s => s.Run);

    /// <summary>Whether anything subscribes at all.</summary>
    public bool Any => !_subscribers.IsEmpty;

    /// <summary>The subscribers for this connection's league.</summary>
    public IEnumerable<T> For(Player player)
    {
        foreach (var (_, run) in Subscribers(player)) yield return run;
    }

    /// <summary>The subscribers for this connection's league, with their owners (for <see cref="GameEventOwners.Guard"/>).</summary>
    public IEnumerable<(string Owner, T Run)> Subscribers(Player player) => Subscribers(Program.LeagueFor(player));

    /// <summary>The subscribers for a league, before a connection exists (an area's inputs are decided first).</summary>
    public IEnumerable<T> For(LeagueRuntime league)
    {
        foreach (var (_, run) in Subscribers(league)) yield return run;
    }

    IEnumerable<(string Owner, T Run)> Subscribers(LeagueRuntime league)
    {
        var subscribers = _subscribers;
        if (subscribers.IsEmpty) yield break;
        foreach (var subscriber in subscribers)
            if (subscriber.AppliesTo(league)) yield return (subscriber.Owner, subscriber.Run);
    }

    void GameEventOwners.IOwned.Release(string owner, int rank)
    {
        if (ImmutableInterlocked.Update(ref _subscribers, s => s.RemoveAll(e => e.Owner == owner && e.Rank == rank)))
            Interlocked.Increment(ref _revision);
    }

    int GameEventOwners.IOwned.Count(string owner) => _subscribers.Count(e => e.Owner == owner);

    sealed class Subscription(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }
}

/// <summary>Every <see cref="GameEvent{T}"/>, and what an owner (a league's code, a module or a mod, by id) holds beyond its subscriptions:
/// the server switches it set (<see cref="ChatRoutes"/>, <see cref="ExchangeRateStore"/>). The hosts release an owner when it unloads.</summary>
public static class GameEventOwners
{
    public interface IOwned
    {
        void Release(string owner, int rank);
        int Count(string owner);
    }

    static ImmutableArray<IOwned> _events = [];
    static ImmutableDictionary<(int Rank, string Owner), ImmutableList<IDisposable>> _held = ImmutableDictionary<(int, string), ImmutableList<IDisposable>>.Empty;

    /// <summary>The ranks: a league's code, a module, a mod. A host releases only its own rank, so a league and a module of the same
    /// name (Ruthless) do not release each other's.</summary>
    public const int LeagueRank = 0, ModuleRank = 1, ModRank = 2;

    public static void Register(IOwned gameEvent) => ImmutableInterlocked.Update(ref _events, e => e.Add(gameEvent));

    /// <summary>Keeps <paramref name="held"/> until the owner unloads, then disposes it.</summary>
    public static T Own<T>(string owner, T held) where T : IDisposable
    {
        ImmutableInterlocked.AddOrUpdate(ref _held, (RankOf(owner), owner), [held], (_, list) => list.Add(held));
        return held;
    }

    /// <summary>Removes every subscription the owner made and disposes what it holds. Its host calls this when it unloads.</summary>
    public static void Release(string owner, int rank)
    {
        foreach (var gameEvent in _events) gameEvent.Release(owner, rank);
        if (ImmutableInterlocked.TryRemove(ref _held, (rank, owner), out var held))
            foreach (var item in held)
                try { item.Dispose(); }
                catch (Exception ex) { Console.WriteLine($"[events] {owner}: releasing {item.GetType().Name} failed: {ex.Message}"); }
    }

    /// <summary>How many subscriptions the owner has (its "hooks" in /mod and /module).</summary>
    public static int Count(string owner) => _events.Sum(e => e.Count(owner));

    // A league's code first, then the modules, then the mods, by the kind of host that runs the owner. An owner no host runs (a test, a
    // helper) counts as a module, and so does a name that is both a module's and a league's.
    internal static int RankOf(string owner) => ModHost.RankOf(owner) ?? ModuleRank;

    /// <summary>Runs one subscriber; one that throws is logged and skipped, and the others still run.</summary>
    public static bool Guard(string owner, string name, Action run)
    {
        try { run(); return true; }
        catch (Exception ex) { Failed(owner, name, ex); return false; }
    }

    /// <inheritdoc cref="Guard(string, string, Action)"/>
    public static async Task<bool> Guard(string owner, string name, Func<Task> run)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { await run(); return true; }
        catch (Exception ex) { Failed(owner, name, ex); return false; }
        finally
        {
            // The events a message's handler awaits hold that client's next messages: a slow one names its owner.
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (ms >= SlowMilliseconds) Console.WriteLine($"[events] {owner} {name} took {ms:0} ms");
        }
    }

    const double SlowMilliseconds = 30;

    static void Failed(string owner, string name, Exception ex) =>
        Console.WriteLine($"[events] {owner} {name} failed (skipped): {ex}");
}

/// <summary>A hostile monster died by the player's hand, inside the kill's commit. What sends messages goes in <see cref="Later"/>: it runs
/// after the commit, in order, whichever way the kill came (a melee hit, a projectile). An atomic kill with
/// CommittedOutput instead requires handlers to commit gameplay synchronously and append primitive output there.</summary>
public sealed record KillContext(Player Player, SpawnedMonster Monster, string VarietyId, (int X, int Y) At)
{
    // Attributed owned kills still dispatch league content; only owner-specific triggers filter this flag.
    public bool PlayerKill { get; init; } = true;
    // An atomic kill supplies a primitive packet sink: handlers commit their drops now, rather than
    // retaining a module's deferred mutation across a network await. Ordinary async kills keep Later.
    public List<(byte[] Packet, string? Log)>? CommittedOutput { get; init; }
    public List<Func<Task>> Afterwards { get; } = [];
    public void Later(Func<Task> send) => Afterwards.Add(send);
}

/// <summary>Where the framework hands over to the league's code, the modules and the mods.</summary>
public static class GameEvents
{
    /// <summary>Adjust an entity's walking speed (cells/second) before its movement is resolved and presented.</summary>
    public static readonly GameEvent<Func<Player, uint, double, double>> MovementSpeed = new();

    public static double ResolveMovementSpeed(Player player, uint entity, double speed)
    {
        foreach (var (owner, run) in MovementSpeed.Subscribers(player))
        {
            double next = speed;
            if (GameEventOwners.Guard(owner, nameof(MovementSpeed), () => next = run(player, entity, speed))
                && double.IsFinite(next) && next > 0) speed = next;
        }
        return speed;
    }

    // ---- The player's connection ---------------------------------------------------------------------------------------

    /// <summary>The player's frames started in an area (each entry): return the content's work for every frame of this area
    /// (with the game seconds since the last), or null when it has none there. <see cref="InArea"/> and <see cref="Always"/> build one.</summary>
    public static readonly GameEvent<Func<Player, Func<Player, double, Task>?>> AreaTick = new();

    /// <summary>An <see cref="AreaTick"/> whose work runs only in the area <paramref name="areaId"/>.</summary>
    public static Func<Player, Func<Player, double, Task>?> InArea(string areaId, Func<Player, Task> run) =>
        player => player.Layout.Id == areaId ? (c, _) => run(c) : null;

    /// <summary>An <see cref="AreaTick"/> whose work runs in every area.</summary>
    public static Func<Player, Func<Player, double, Task>?> Always(Func<Player, double, Task> run) => _ => run;

    /// <summary>An area's own world (GameArea) was made, or a module loaded while it runs: add the rules' systems, stores,
    /// events and timers to it, with the module's lifetime as their source (GameArea.Drop removes them when it unloads). The
    /// connection is the area's first player's, for its league and the area's data; keep nothing of it but the area's.</summary>
    public static readonly GameEvent<Action<GameArea, Player>> AreaStarted = new();

    /// <summary>The area was generated: once, when its first player's client sent "area generated" and its monsters are
    /// placed. Place the area's objects here (everyone sees them; a later player is given them as they are). The player is
    /// the one whose entry generated it.</summary>
    public static readonly GameEvent<Func<Player, Task>> AreaGenerated = new();

    /// <summary>A player entered the area (its client sent "area generated"), after the area was generated: the player's own
    /// things and their quest view (the NPCs and quest blockers their quest state shows them).</summary>
    public static readonly GameEvent<Func<Player, Task>> PlayerEntered = new();

    /// <summary>A new instance's monsters are placed (not a kept instance's, whose monsters come back as they were): place
    /// a league encounter's monsters and objects here (<see cref="LeagueChances"/> rolls how many).</summary>
    public static readonly GameEvent<Func<AreaPopulated, Task>> AreaPopulated = new();

    /// <summary>The player has used an area transition to another room of this instance.</summary>
    public static readonly GameEvent<Func<Player, Task>> LocalGateUsed = new();

    /// <summary>The player is about to leave the area or the game, with why.</summary>
    public static readonly GameEvent<Func<Player, string, Task>> PlayerLeaving = new();

    /// <summary>The character has left the game (a logout, a disconnect; not an area change) and its save is written:
    /// nothing of that connection writes it again, and a login of the character waits for the subscribers before it
    /// reads it. The place to replace a character's save. Not run when the last save failed, or for a connection that never
    /// loaded the character (a refused login).</summary>
    public static readonly GameEvent<Func<Player, Task>> CharacterAtRest = new();

    /// <summary>The player's hideout is being furnished: place stations here.</summary>
    public static readonly GameEvent<Func<Player, Task>> HideoutReady = new();

    /// <summary>Messages sent as the player enters an area, before its objects: a log line and the message.</summary>
    public static readonly GameEvent<Func<Player, IEnumerable<(string Log, byte[] Message)>>> EnterMessages = new();

    /// <summary>Messages sent with the player's own, before 0x21 activates the area: what the client's UI reads once as
    /// it attaches to the area (a ruleset some of its windows look up then). A log line and the message.</summary>
    public static readonly GameEvent<Func<Player, IEnumerable<(string Log, byte[] Message)>>> BeforeEnterMessages = new();

    /// <summary>What the area's first message says beyond the server's own; null for no opinion. The first answer wins.</summary>
    public static readonly GameEvent<Func<Player, AreaHeader?>> AreaHeader = new();

    /// <summary>A map device's checked input before any item is consumed.</summary>
    public static readonly GameEvent<Action<MapOpening>> MapOpening = new();

    public static void ResolveMapOpening(MapOpening request)
    {
        foreach (var (owner, run) in MapOpening.Subscribers(request.Player))
            GameEventOwners.Guard(owner, nameof(MapOpening), () => run(request));
        if (request.Opening is not null)
            foreach (string message in request.UnhandledContent)
                Console.WriteLine($"[map-content] {message}");
    }

    /// <summary>A hostile monster died by the player's hand, after its kill was committed (<see cref="Kill"/> runs inside the commit).</summary>
    public static readonly GameEvent<Func<MonsterKill, Task>> MonsterKilled = new();

    /// <summary>An option picked in an NPC's dialogue; what an option does is a module's. True: it was this subscriber's. An option nothing
    /// answers does nothing.</summary>
    public static readonly GameEvent<Func<NpcOption, Task<bool>>> NpcOption = new();

    /// <summary>The player right-clicked a removable buff icon to cancel it (C→S 0x004b, buff instance id).
    /// True: this subscriber owned and removed that buff.</summary>
    public static readonly GameEvent<Func<Player, ushort, Task<bool>>> BuffCancel = new();

    /// <summary>Whether this subscriber fills the sub-area of an ExtraTerrainFeatures row (by its Id). A gate the layout links
    /// to such a sub-area (<see cref="AreaTransitionObject.Feature"/>) stands only where a subscriber answers true.</summary>
    public static readonly GameEvent<Func<Player, string, bool>> FeatureFilled = new();

    /// <summary>Whether a subscriber of the connection's league fills the row's sub-area (<see cref="FeatureFilled"/>).</summary>
    public static bool FillsFeature(Player player, string row) => FeatureFilled.For(player).Any(run => run(player, row));

    /// <summary>A killed monster's drops, before they are rolled: scale them, add items, or cancel them.</summary>
    public static readonly GameEvent<Action<DropRoll>> Drops = new();

    /// <summary>A gamble window as it opens (an NPC whose shop is the black market, window kind 10): add, remove or reprice
    /// its offers.</summary>
    public static readonly GameEvent<Action<GambleWindow>> Gambles = new();

    /// <summary>The experience of a kill, after the level penalty, before the character and its gems gain it.</summary>
    public static readonly GameEvent<Action<ExperienceGain>> Experience = new();

    /// <summary>Stats added to the character's own, by <c>Stats.dat</c> id, whenever the server rebuilds its stats.</summary>
    public static readonly GameEvent<Action<StatSheet>> Stats = new();

    /// <summary>Extra worn items counted as stat inputs only: their mods join the character's stat sources and conditions,
    /// their local defences the armour slots' defences. They host no gems or granted skills, and checks that look for one
    /// worn item (weapon hit stats, gem requirements, unique mechanics such as Her Embrace) still read the real inventory.
    /// Rows outside the equipment rows are dropped; ids must be stable and distinct from real items.</summary>
    public static readonly GameEvent<Func<Player, IReadOnlyList<(byte Row, InventoryItem Item)>,
        IReadOnlyList<(byte Row, InventoryItem Item)>>> ItemStatSources = new();

    static readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, GameEvent<Func<Player, byte[], Task<bool>>>> Packets = new();

    /// <summary>A client message (C→S) with this opcode, the whole message with its opcode, before the Gateway handles it. True: it was
    /// taken, and the Gateway's own handler does not run.</summary>
    public static GameEvent<Func<Player, byte[], Task<bool>>> Packet(ushort opcode) => Packets.GetOrAdd(opcode, _ => new());

    /// <summary>Whether anything subscribes to client messages with this opcode.</summary>
    public static bool WantsPacket(ushort opcode) => Packets.TryGetValue(opcode, out var packet) && packet.Any;

    /// <summary>A chat command, <c>/name args…</c>, through <see cref="CommandsBuilding"/>. The Gateway's own commands, and those added
    /// before it, keep their name. One that throws tells the player it failed, and the error is in the Gateway's log.</summary>
    public static IDisposable Command(string owner, string name, string usage, Func<Player, string[], Task> run, params string[] arguments) =>
        CommandsBuilding.Add(owner, (commands, player) => commands.TryAdd(name, usage, async args =>
        {
            if (!await GameEventOwners.Guard(owner, $"/{name}", () => run(player, args)))
                await commands.Reply($"/{name} failed ({owner}); the error is in the Gateway's log. Usage: {usage}");
        }, arguments));

    /// <summary>The content's work for the area the player just entered (<see cref="AreaTick"/>); empty when none has any.</summary>
    public static List<(string Owner, Func<Player, double, Task> Run)> AreaTickWork(Player player)
    {
        List<(string, Func<Player, double, Task>)> work = [];
        foreach (var (owner, start) in AreaTick.Subscribers(player))
            if (start(player) is { } run) work.Add((owner, run));
        return work;
    }

    public static async Task RunAreaTick(List<(string Owner, Func<Player, double, Task> Run)> work, Player player, double seconds)
    {
        foreach (var (owner, run) in work) await GameEventOwners.Guard(owner, nameof(AreaTick), () => run(player, seconds));
    }

    /// <summary>One player's area work, refreshed only when its subscriptions change.</summary>
    public sealed class AreaTickRunner(Player player)
    {
        long _revision = -1;
        Dictionary<long, Func<Player, double, Task>?> _bindings = [];
        List<(string Owner, Func<Player, double, Task> Run)> _work = [];

        public Task Run(double seconds)
        {
            long revision = AreaTick.Revision;
            if (revision != _revision) Refresh(revision);
            return _work.Count == 0 ? Task.CompletedTask : RunAreaTick(_work, player, seconds);
        }

        void Refresh(long revision)
        {
            var league = Program.LeagueFor(player);
            Dictionary<long, Func<Player, double, Task>?> bindings = [];
            List<(string Owner, Func<Player, double, Task> Run)> work = [];
            foreach (var subscriber in AreaTick.Snapshot)
            {
                if (!subscriber.AppliesTo(league)) continue;
                // Other subscribers keep the state they already bound, including ones that opted out of the area.
                if (!_bindings.TryGetValue(subscriber.Id, out var run))
                    GameEventOwners.Guard(subscriber.Owner, nameof(AreaTick), () => { run = subscriber.Run(player); });
                bindings.Add(subscriber.Id, run);
                if (run is not null) work.Add((subscriber.Owner, run));
            }
            _bindings = bindings;
            _work = work;
            // Record the revision read before enumeration, so a concurrent change is seen on the next frame.
            _revision = revision;
        }
    }

    public static async Task RunAreaGenerated(Player player)
    {
        foreach (var (owner, run) in AreaGenerated.Subscribers(player)) await GameEventOwners.Guard(owner, nameof(AreaGenerated), () => run(player));
    }

    public static async Task RunPlayerEntered(Player player)
    {
        foreach (var (owner, run) in PlayerEntered.Subscribers(player)) await GameEventOwners.Guard(owner, nameof(PlayerEntered), () => run(player));
    }

    public static async Task RunCharacterAtRest(Player player)
    {
        foreach (var (owner, run) in CharacterAtRest.Subscribers(player)) await GameEventOwners.Guard(owner, nameof(CharacterAtRest), () => run(player));
    }

    public static async Task RunAreaPopulated(AreaPopulated area)
    {
        foreach (var (owner, run) in AreaPopulated.Subscribers(area.Player)) await GameEventOwners.Guard(owner, nameof(AreaPopulated), () => run(area));
    }

    public static async Task RunLocalGateUsed(Player player)
    {
        foreach (var (owner, run) in LocalGateUsed.Subscribers(player)) await GameEventOwners.Guard(owner, nameof(LocalGateUsed), () => run(player));
    }

    public static async Task RunPlayerLeaving(Player player, string why)
    {
        foreach (var (owner, run) in PlayerLeaving.Subscribers(player)) await GameEventOwners.Guard(owner, nameof(PlayerLeaving), () => run(player, why));
    }

    public static async Task RunHideoutReady(Player player)
    {
        foreach (var (owner, run) in HideoutReady.Subscribers(player)) await GameEventOwners.Guard(owner, nameof(HideoutReady), () => run(player));
    }

    public static List<(string Log, byte[] Message)> RunEnterMessages(Player player)
    {
        var messages = new List<(string, byte[])>();
        foreach (var (owner, run) in EnterMessages.Subscribers(player)) GameEventOwners.Guard(owner, nameof(EnterMessages), () => messages.AddRange(run(player)));
        return messages;
    }

    public static List<(string Log, byte[] Message)> RunBeforeEnterMessages(Player player)
    {
        var messages = new List<(string, byte[])>();
        foreach (var (owner, run) in BeforeEnterMessages.Subscribers(player))
            GameEventOwners.Guard(owner, nameof(BeforeEnterMessages), () => messages.AddRange(run(player)));
        return messages;
    }

    public static AreaHeader? RunAreaHeader(Player player)
    {
        AreaHeader? header = null;
        foreach (var (owner, run) in AreaHeader.Subscribers(player))
        {
            GameEventOwners.Guard(owner, nameof(AreaHeader), () => header = run(player));
            if (header is not null) break;
        }
        return header;
    }

    public static async Task RunMonsterKilled(MonsterKill kill)
    {
        foreach (var (owner, run) in MonsterKilled.Subscribers(kill.Player)) await GameEventOwners.Guard(owner, nameof(MonsterKilled), () => run(kill));
    }

    /// <summary>True when a subscriber answered the option.</summary>
    public static async Task<bool> AnyNpcOption(NpcOption option)
    {
        foreach (var (owner, run) in NpcOption.Subscribers(option.Player))
        {
            bool taken = false;
            if (await GameEventOwners.Guard(owner, nameof(NpcOption), async () => taken = await run(option)) && taken) return true;
        }
        return false;
    }

    /// <summary>True when a subscriber owned and removed the buff.</summary>
    public static async Task<bool> RunBuffCancel(Player player, ushort buffInstance)
    {
        if (!BuffCancel.Any) return false;
        foreach (var (owner, run) in BuffCancel.Subscribers(player))
        {
            bool taken = false;
            if (await GameEventOwners.Guard(owner, nameof(BuffCancel), async () => taken = await run(player, buffInstance)) && taken) return true;
        }
        return false;
    }

    /// <summary>True when a subscriber took the message.</summary>
    public static async Task<bool> AnyPacket(Player player, ushort opcode, byte[] bytes)
    {
        if (!Packets.TryGetValue(opcode, out var packet)) return false;
        foreach (var (owner, run) in packet.Subscribers(player))
        {
            bool taken = false;
            if (await GameEventOwners.Guard(owner, $"Packet 0x{opcode:x4}", async () => taken = await run(player, bytes)) && taken) return true;
        }
        return false;
    }

    /// <summary>Every subscriber of this league sees the window's offers in turn.</summary>
    public static void RunGambles(GambleWindow window)
    {
        foreach (var (owner, run) in Gambles.Subscribers(window.Player))
            GameEventOwners.Guard(owner, nameof(Gambles), () => run(window));
    }

    /// <summary>The subscribers' changes to a kill's drops; null when none of this league's changes drops.</summary>
    public static DropRoll? RunDrops(Player player, MonsterInfo monster)
    {
        if (!Drops.Any) return null;
        DropRoll? roll = null;
        foreach (var (owner, run) in Drops.Subscribers(player))
        {
            roll ??= new DropRoll(player, monster);
            GameEventOwners.Guard(owner, nameof(Drops), () => run(roll));
        }
        return roll;
    }

    public static uint RunExperience(Player player, MonsterInfo monster, uint experience)
    {
        if (!Experience.Any) return experience;
        var gain = new ExperienceGain(player, monster, experience);
        foreach (var (owner, run) in Experience.Subscribers(player)) GameEventOwners.Guard(owner, nameof(Experience), () => run(gain));
        return gain.Experience;
    }

    public static IReadOnlyDictionary<uint, int> RunStats(Player player)
    {
        if (!Stats.Any) return ImmutableDictionary<uint, int>.Empty;
        var sheet = new StatSheet(player);
        foreach (var (owner, run) in Stats.Subscribers(player)) GameEventOwners.Guard(owner, nameof(Stats), () => run(sheet));
        return sheet.Added;
    }

    // ---- The game's own hand-overs ---------------------------------------------------------------------------------------

    // Optional gameplay shares the existing minion schedule and current connection writer.
    public static readonly GameEvent<Func<Player, double, Task>> CompanionTick = new();
    public static readonly GameEvent<Func<Player, string, bool>> GrantedSkillExecutable = new();
    public static readonly GameEvent<Action<Player>> TransientClosing = new();
    public static readonly GameEvent<Action<Player, bool>> PauseChanged = new();
    public static readonly GameEvent<Func<Player, string?, ushort, (int X, int Y), Func<bool>, bool?>> SkillCastAt = new();
    public static readonly GameEvent<Action<KillContext>> Kill = new();
    /// <summary>Optional league systems' skills and transient skill-bar bindings.</summary>
    public static readonly GameEvent<Func<Player, IReadOnlyList<GrantedSkill>>> GrantedSkills = new();
    public static readonly GameEvent<Action<Player, ushort[]>> SkillBarBuilding = new();
    /// <summary>Equipment granted by a league system that waives its requirements.</summary>
    public static readonly GameEvent<Func<Player, IReadOnlyCollection<uint>>> RequirementExemptions = new();
    public static readonly GameEvent<Action<Player, Dictionary<uint, int>>> FinalStatsBuilding = new();
    public static readonly GameEvent<Func<Player, int, int>> IncomingDamage = new();
    /// <summary>False suppresses drops, ground publication and automatic gold collection.</summary>
    public static readonly GameEvent<Func<Player, bool>> LootAllowed = new();
    /// <summary>True when an optional system handled closing the native reward dialogue.</summary>
    public static readonly GameEvent<Func<Player, Task<bool>>> RewardClosing = new();
    /// <summary>A quest reward choice the server built for this QuestRewardOffers row; a module completes its league's
    /// item parts. Return the item unchanged for no opinion; the first answer that changes it wins.</summary>
    public static readonly GameEvent<Func<Player, int, Item, ItemCatalog.BaseType, Item>> RewardItem = new();
    /// <summary>A league encounter may refuse unrelated travel while its gate is closed.</summary>
    public static readonly GameEvent<Func<Player, PendingTransfer, string?>> TravelRefusal = new();
    /// <summary>The Auth transfer answer, after failed-transfer recovery and before a successful ticket is sent.</summary>
    public static readonly GameEvent<Func<Player, PendingTransfer, ControlResponse, Task>> TransferResolved = new();
    /// <summary>The player came back at a checkpoint or in town: a league encounter may restart its run.</summary>
    public static readonly GameEvent<Func<Player, Task>> Resurrected = new();

    public static IReadOnlyList<GrantedSkill> ExtraSkills(Player player) =>
        [.. GrantedSkills.For(player).SelectMany(run => run(player))];

    public static HashSet<uint> ExemptItems(Player player) =>
        [.. RequirementExemptions.For(player).SelectMany(run => run(player))];

    public static int AdjustIncomingDamage(Player player, int damage)
    {
        foreach (var run in IncomingDamage.For(player)) damage = run(player, damage);
        return damage;
    }

    public static bool AllowsLoot(Player player) => LootAllowed.For(player).All(run => run(player));

    public static async Task<bool> AnyRewardClosing(Player player)
    {
        foreach (var run in RewardClosing.For(player)) if (await run(player)) return true;
        return false;
    }

    public static Item ResolveRewardItem(Player player, int offer, Item item, ItemCatalog.BaseType type)
    {
        foreach (var (owner, run) in RewardItem.Subscribers(player))
        {
            Item answer = item;
            GameEventOwners.Guard(owner, nameof(RewardItem), () => answer = run(player, offer, item, type));
            if (answer != item) return answer;
        }
        return item;
    }

    public static async Task RunResurrected(Player player)
    {
        foreach (var run in Resurrected.For(player)) await run(player);
    }
    /// <summary>Talking to an NPC starts; true when a module has its own dialogue for it.</summary>
    public static readonly GameEvent<Func<Player, uint, SpawnedNpc, Task<bool>>> NpcTalk = new();
    /// <summary>A connection's chat commands are made: a module adds to them.</summary>
    public static readonly GameEvent<Func<ChatCommands, Player, ChatCommands>> CommandsBuilding = new();
    /// <summary>The campaign driver's /state is made (Agents/docs/dev/campaign-automation.md): a module adds the fields of
    /// what it runs (the Pantheon's "pantheon").</summary>
    public static readonly GameEvent<Action<Player, System.Text.Json.Nodes.JsonObject>> StateBuilding = new();
    public static readonly GameEvent<Func<Player, Dictionary<uint, int>, Dictionary<uint, int>>> PlayerStatSources = new();
    public static readonly GameEvent<Func<Player, IReadOnlyDictionary<uint, int>, SpawnedMonster, DamagePacket, DamagePacket>> OutgoingDamage = new();
    /// <summary>A click on an object; true when it was this module's.</summary>
    public static readonly GameEvent<Func<Player, InstanceTransfer.UseSkillOnObject, double, Task<bool>>> ObjectUse = new();
    /// <summary>A click on an item on the ground; true when it was this module's.</summary>
    public static readonly GameEvent<Func<Player, uint, WorldItemObject, InstanceTransfer.UseSkillOnObject, double, Task<bool>>> GroundItemUse = new();
    /// <summary>An NPC option was picked, before its action; false stops it.</summary>
    public static readonly GameEvent<Func<Player, NpcCatalog.Talk, Task<bool>>> NpcOptionPicked = new();
    /// <summary>Whether an NPC opts into NPCTalk league quest flag filtering, automatic conversation matching, and
    /// league-flag updates (<c>__col15/__col16/__col17/__col18</c>); false leaves other NPCs untouched.</summary>
    public static readonly GameEvent<Func<Player, NpcCatalog.Npc, bool>> NpcUsesLeagueFlags = new();
    /// <summary>A read of client messages was handled.</summary>
    public static readonly GameEvent<Func<Player, IReadOnlyList<ushort>, Task>> MessagesHandled = new();
    public static readonly GameEvent<Func<Player, InstanceItems.PickUpItem, Task<bool>>> InventoryPick = new();
    public static readonly GameEvent<Func<Player, InstanceItems.PlaceItem, Task<bool>>> InventoryPlace = new();
    /// <summary>How one of the player's inventories is registered with the client on entering an area (S→C 0xc0): the
    /// Inventories row's own slot, or another kind or flags for it. The client's panels bind to the first registration
    /// of a row, so this is where a row's kind is chosen. The first answer wins; null for no opinion. The size the
    /// server places items by stays the row's own.</summary>
    public static readonly GameEvent<Func<Player, InstanceInventories.Slot, InstanceInventories.Slot?>> InventorySlot = new();
    /// <summary>Why the held item may not go into this inventory row (C→S 0x2a, 0x4a), for the log; null for no
    /// objection. The first objection wins.</summary>
    public static readonly GameEvent<Func<Player, byte, Item, string?>> InventoryRefusal = new();
    /// <summary>Why a placement (possibly swapping out an existing item) may not go into this inventory row (C→S 0x2a,
    /// 0x4a), for the log; null for no objection. The first objection wins.</summary>
    public static readonly GameEvent<Func<InventoryPlacement, string?>> InventoryPlacementRefusal = new();
    public static readonly GameEvent<Func<Player, Task>> NpcDialogueClosing = new();
    /// <summary>A gem skill's cast: its effect id, skill id, where from, the action; true when the module took it, null for not its skill.</summary>
    public static readonly GameEvent<Func<Player, string?, ushort, (int X, int Y), Func<bool>, bool?>> SkillCast = new();
    /// <summary>A currency used; true when it was this module's.</summary>
    public static readonly GameEvent<Func<Player, byte, InventoryItem, ItemCatalog.BaseType, Task<bool>>> ItemUse = new();
    /// <summary>A warcry with open_nearby_chests_on_warcry went off: a module opens its chests within the radius (cells).</summary>
    public static readonly GameEvent<Func<Player, (int X, int Y), int, Task>> ChestsOpened = new();
    /// <summary>A party line ("%…"), cleaned: the party is a module's to know, the text is the Chat module's; true when
    /// the module delivered it to the party.</summary>
    public static readonly GameEvent<Func<Player, string, IReadOnlyList<ChatLineItem>?, Task<bool>>> PartyChat = new();

    public static async Task<bool> AnyNpcTalk(Player player, uint id, SpawnedNpc npc)
    {
        foreach (var run in NpcTalk.For(player)) if (await run(player, id, npc)) return true;
        return false;
    }

    public static ChatCommands RunCommandsBuilding(Player player, ChatCommands commands)
    {
        // With no connection, for the list tools read: every module's commands.
        foreach (var run in player is null ? CommandsBuilding.All : CommandsBuilding.For(player)) commands = run(commands, player!);
        return commands;
    }

    public static void RunStateBuilding(Player player, System.Text.Json.Nodes.JsonObject state)
    {
        foreach (var run in StateBuilding.For(player)) run(player, state);
    }

    public static async Task RunKill(KillContext kill)
    {
        foreach (var run in Kill.For(kill.Player)) run(kill);
        foreach (var send in kill.Afterwards) await send();
    }

    public static Dictionary<uint, int> WithPlayerStatSources(Player player, Dictionary<uint, int> sources)
    {
        foreach (var run in PlayerStatSources.For(player)) sources = run(player, sources);
        return sources;
    }

    public static DamagePacket WithOutgoingDamage(Player player, IReadOnlyDictionary<uint, int> playerStats, SpawnedMonster monster, DamagePacket rolled)
    {
        foreach (var run in OutgoingDamage.For(player)) rolled = run(player, playerStats, monster, rolled);
        return rolled;
    }

    public static async Task<bool> AnyObjectUse(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        foreach (var run in ObjectUse.For(player)) if (await run(player, use, requestedAt)) return true;
        return false;
    }

    public static async Task<bool> AnyGroundItemUse(Player player, uint id, WorldItemObject ground, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        foreach (var run in GroundItemUse.For(player)) if (await run(player, id, ground, use, requestedAt)) return true;
        return false;
    }

    public static async Task<bool> AllowNpcOption(Player player, NpcCatalog.Talk talk)
    {
        foreach (var run in NpcOptionPicked.For(player)) if (!await run(player, talk)) return false;
        return true;
    }

    public static bool UsesNpcLeagueFlags(Player player, NpcCatalog.Npc npc)
    {
        foreach (var (owner, run) in NpcUsesLeagueFlags.Subscribers(player))
        {
            bool answer = false;
            GameEventOwners.Guard(owner, nameof(NpcUsesLeagueFlags), () => answer = run(player, npc));
            if (answer) return true;
        }
        return false;
    }

    public static async Task RunMessagesHandled(Player player, IReadOnlyList<ushort> ids)
    {
        foreach (var run in MessagesHandled.For(player)) await run(player, ids);
    }

    public static async Task<bool> AnyInventoryPick(Player player, InstanceItems.PickUpItem pick)
    {
        foreach (var run in InventoryPick.For(player)) if (await run(player, pick)) return true;
        return false;
    }

    public static async Task<bool> AnyInventoryPlace(Player player, InstanceItems.PlaceItem place)
    {
        foreach (var run in InventoryPlace.For(player)) if (await run(player, place)) return true;
        return false;
    }

    public static InstanceInventories.Slot ResolveInventorySlot(Player player, InstanceInventories.Slot slot)
    {
        foreach (var (owner, run) in InventorySlot.Subscribers(player))
        {
            InstanceInventories.Slot? answer = null;
            GameEventOwners.Guard(owner, nameof(InventorySlot), () => answer = run(player, slot));
            if (answer is { } changed) return changed;
        }
        return slot;
    }

    public static string? FirstInventoryRefusal(Player player, byte row, Item item) =>
        FirstInventoryRefusal(player, row, item, null);

    public static string? FirstInventoryRefusal(Player player, byte row, Item item, InventoryItem? replaced)
    {
        foreach (var (owner, run) in InventoryRefusal.Subscribers(player))
        {
            string? answer = null;
            GameEventOwners.Guard(owner, nameof(InventoryRefusal), () => answer = run(player, row, item));
            if (answer is not null) return answer;
        }
        if (!InventoryPlacementRefusal.Any) return null;
        var placement = new InventoryPlacement(player, row, item, replaced);
        foreach (var (owner, run) in InventoryPlacementRefusal.Subscribers(player))
        {
            string? answer = null;
            GameEventOwners.Guard(owner, nameof(InventoryPlacementRefusal), () => answer = run(placement));
            if (answer is not null) return answer;
        }
        return null;
    }

    public static async Task RunChestsOpened(Player player, (int X, int Y) at, int radius)
    {
        foreach (var run in ChestsOpened.For(player)) await run(player, at, radius);
    }

    public static async Task RunNpcDialogueClosing(Player player)
    {
        foreach (var run in NpcDialogueClosing.For(player)) await run(player);
    }

    public static bool? RunSkillCast(Player player, string? effect, ushort skillId, (int X, int Y) from, Func<bool> current)
    {
        foreach (var run in SkillCast.For(player)) if (run(player, effect, skillId, from, current) is { } taken) return taken;
        return null;
    }

    public static async Task<bool> AnyItemUse(Player player, byte row, InventoryItem item, ItemCatalog.BaseType type)
    {
        foreach (var run in ItemUse.For(player)) if (await run(player, row, item, type)) return true;
        return false;
    }

    public static async Task<bool> RunPartyChat(Player player, string text, IReadOnlyList<ChatLineItem>? items)
    {
        foreach (var run in PartyChat.For(player)) if (await run(player, text, items)) return true;
        return false;
    }
}
