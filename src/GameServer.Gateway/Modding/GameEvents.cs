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
    ImmutableArray<(string Owner, int Rank, T Run)> _subscribers = [];

    public GameEvent() => GameEventOwners.Register(this);

    public IDisposable Add(string owner, T run)
    {
        int rank = GameEventOwners.RankOf(owner);
        ImmutableInterlocked.Update(ref _subscribers, s =>
        {
            int at = s.Length;
            while (at > 0 && s[at - 1].Rank > rank) at--;
            return s.Insert(at, (owner, rank, run));
        });
        return new Subscription(() => ImmutableInterlocked.Update(ref _subscribers, s => s.RemoveAll(e => ReferenceEquals(e.Run, run))));
    }

    public IEnumerable<T> All => _subscribers.Select(s => s.Run);

    /// <summary>Whether anything subscribes at all.</summary>
    public bool Any => !_subscribers.IsEmpty;

    /// <summary>The subscribers for this connection's league.</summary>
    public IEnumerable<T> For(ClientState conn)
    {
        foreach (var (_, run) in Subscribers(conn)) yield return run;
    }

    /// <summary>The subscribers for this connection's league, with their owners (for <see cref="GameEventOwners.Guard"/>).</summary>
    public IEnumerable<(string Owner, T Run)> Subscribers(ClientState conn)
    {
        var subscribers = _subscribers;
        if (subscribers.IsEmpty) yield break;
        var league = Program.LeagueFor(conn);
        foreach (var (owner, rank, run) in subscribers)
            if (rank == GameEventOwners.ModRank || league.Modules.Contains(owner)
                || (rank == GameEventOwners.LeagueRank && league.Id.Equals(owner, StringComparison.OrdinalIgnoreCase)))
                yield return (owner, run);
    }

    void GameEventOwners.IOwned.Release(string owner, int rank) =>
        ImmutableInterlocked.Update(ref _subscribers, s => s.RemoveAll(e => e.Owner == owner && e.Rank == rank));

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
        try { await run(); return true; }
        catch (Exception ex) { Failed(owner, name, ex); return false; }
    }

    static void Failed(string owner, string name, Exception ex) =>
        Console.WriteLine($"[events] {owner} {name} failed (skipped): {ex}");
}

/// <summary>A hostile monster died by the player's hand, inside the kill's commit. What sends messages goes in <see cref="Later"/>: it runs
/// after the commit, in order, whichever way the kill came (a melee hit, a projectile). An atomic kill with
/// CommittedOutput instead requires handlers to commit gameplay synchronously and append primitive output there.</summary>
public sealed record KillContext(ClientState Conn, SpawnedMonster Monster, string VarietyId, (int X, int Y) At)
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
    // ---- The player's connection ---------------------------------------------------------------------------------------

    /// <summary>Every player tick (about 4 a second while the player is in an area and not paused), with the game seconds since the last one.</summary>
    public static readonly GameEvent<Func<ClientState, double, Task>> Tick = new();

    /// <summary>The player's area is ready: the client sent "area generated", and the area's monsters and objects are placed.</summary>
    public static readonly GameEvent<Func<ClientState, Task>> AreaGenerated = new();

    /// <summary>The player has used an area transition to another room of this instance.</summary>
    public static readonly GameEvent<Func<ClientState, Task>> LocalGateUsed = new();

    /// <summary>The player is about to leave the area or the game, with why.</summary>
    public static readonly GameEvent<Func<ClientState, string, Task>> PlayerLeaving = new();

    /// <summary>The player's hideout is being furnished: place stations here.</summary>
    public static readonly GameEvent<Func<ClientState, Task>> HideoutReady = new();

    /// <summary>Messages sent as the player enters an area, before its objects: a log line and the message.</summary>
    public static readonly GameEvent<Func<ClientState, IEnumerable<(string Log, byte[] Message)>>> EnterMessages = new();

    /// <summary>What the area's first message says beyond the server's own; null for no opinion. The first answer wins.</summary>
    public static readonly GameEvent<Func<ClientState, AreaHeader?>> AreaHeader = new();

    /// <summary>A map device's checked input before any item is consumed.</summary>
    public static readonly GameEvent<Action<MapOpening>> MapOpening = new();

    public static void ResolveMapOpening(MapOpening request)
    {
        foreach (var (owner, run) in MapOpening.Subscribers(request.Conn))
            GameEventOwners.Guard(owner, nameof(MapOpening), () => run(request));
    }

    /// <summary>A hostile monster died by the player's hand, after its kill was committed (<see cref="Kill"/> runs inside the commit).</summary>
    public static readonly GameEvent<Func<MonsterKill, Task>> MonsterKilled = new();

    /// <summary>An option picked in an NPC's dialogue; what an option does is a module's. True: it was this subscriber's. An option nothing
    /// answers does nothing.</summary>
    public static readonly GameEvent<Func<NpcOption, Task<bool>>> NpcOption = new();

    /// <summary>A killed monster's drops, before they are rolled: scale them, add items, or cancel them.</summary>
    public static readonly GameEvent<Action<DropRoll>> Drops = new();

    /// <summary>The experience of a kill, after the level penalty, before the character and its gems gain it.</summary>
    public static readonly GameEvent<Action<ExperienceGain>> Experience = new();

    /// <summary>Stats added to the character's own, by <c>Stats.dat</c> id, whenever the server rebuilds its stats.</summary>
    public static readonly GameEvent<Action<StatSheet>> Stats = new();

    static readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, GameEvent<Func<ClientState, byte[], Task<bool>>>> Packets = new();

    /// <summary>A client message (C→S) with this opcode, the whole message with its opcode, before the Gateway handles it. True: it was
    /// taken, and the Gateway's own handler does not run.</summary>
    public static GameEvent<Func<ClientState, byte[], Task<bool>>> Packet(ushort opcode) => Packets.GetOrAdd(opcode, _ => new());

    /// <summary>Whether anything subscribes to client messages with this opcode.</summary>
    public static bool WantsPacket(ushort opcode) => Packets.TryGetValue(opcode, out var packet) && packet.Any;

    /// <summary>A chat command, <c>/name args…</c>, through <see cref="CommandsBuilding"/>. The Gateway's own commands, and those added
    /// before it, keep their name. One that throws tells the player it failed, and the error is in the Gateway's log.</summary>
    public static IDisposable Command(string owner, string name, string usage, Func<ClientState, string[], Task> run, params string[] arguments) =>
        CommandsBuilding.Add(owner, (commands, _, _, _, conn) => commands.TryAdd(name, usage, async args =>
        {
            if (!await GameEventOwners.Guard(owner, $"/{name}", () => run(conn, args)))
                await commands.Reply($"/{name} failed ({owner}); the error is in the Gateway's log. Usage: {usage}");
        }, arguments));

    public static async Task RunTick(ClientState conn, double seconds)
    {
        foreach (var (owner, run) in Tick.Subscribers(conn)) await GameEventOwners.Guard(owner, nameof(Tick), () => run(conn, seconds));
    }

    public static async Task RunAreaGenerated(ClientState conn)
    {
        foreach (var (owner, run) in AreaGenerated.Subscribers(conn)) await GameEventOwners.Guard(owner, nameof(AreaGenerated), () => run(conn));
    }

    public static async Task RunLocalGateUsed(ClientState conn)
    {
        foreach (var (owner, run) in LocalGateUsed.Subscribers(conn)) await GameEventOwners.Guard(owner, nameof(LocalGateUsed), () => run(conn));
    }

    public static async Task RunPlayerLeaving(ClientState conn, string why)
    {
        foreach (var (owner, run) in PlayerLeaving.Subscribers(conn)) await GameEventOwners.Guard(owner, nameof(PlayerLeaving), () => run(conn, why));
    }

    public static async Task RunHideoutReady(ClientState conn)
    {
        foreach (var (owner, run) in HideoutReady.Subscribers(conn)) await GameEventOwners.Guard(owner, nameof(HideoutReady), () => run(conn));
    }

    public static List<(string Log, byte[] Message)> RunEnterMessages(ClientState conn)
    {
        var messages = new List<(string, byte[])>();
        foreach (var (owner, run) in EnterMessages.Subscribers(conn)) GameEventOwners.Guard(owner, nameof(EnterMessages), () => messages.AddRange(run(conn)));
        return messages;
    }

    public static AreaHeader? RunAreaHeader(ClientState conn)
    {
        AreaHeader? header = null;
        foreach (var (owner, run) in AreaHeader.Subscribers(conn))
        {
            GameEventOwners.Guard(owner, nameof(AreaHeader), () => header = run(conn));
            if (header is not null) break;
        }
        return header;
    }

    public static async Task RunMonsterKilled(MonsterKill kill)
    {
        foreach (var (owner, run) in MonsterKilled.Subscribers(kill.Conn)) await GameEventOwners.Guard(owner, nameof(MonsterKilled), () => run(kill));
    }

    /// <summary>True when a subscriber answered the option.</summary>
    public static async Task<bool> AnyNpcOption(NpcOption option)
    {
        foreach (var (owner, run) in NpcOption.Subscribers(option.Conn))
        {
            bool taken = false;
            if (await GameEventOwners.Guard(owner, nameof(NpcOption), async () => taken = await run(option)) && taken) return true;
        }
        return false;
    }

    /// <summary>True when a subscriber took the message.</summary>
    public static async Task<bool> AnyPacket(ClientState conn, ushort opcode, byte[] bytes)
    {
        if (!Packets.TryGetValue(opcode, out var packet)) return false;
        foreach (var (owner, run) in packet.Subscribers(conn))
        {
            bool taken = false;
            if (await GameEventOwners.Guard(owner, $"Packet 0x{opcode:x4}", async () => taken = await run(conn, bytes)) && taken) return true;
        }
        return false;
    }

    /// <summary>The subscribers' changes to a kill's drops; null when none of this league's changes drops.</summary>
    public static DropRoll? RunDrops(ClientState conn, MonsterInfo monster)
    {
        if (!Drops.Any) return null;
        DropRoll? roll = null;
        foreach (var (owner, run) in Drops.Subscribers(conn))
        {
            roll ??= new DropRoll(conn, monster);
            GameEventOwners.Guard(owner, nameof(Drops), () => run(roll));
        }
        return roll;
    }

    public static uint RunExperience(ClientState conn, MonsterInfo monster, uint experience)
    {
        if (!Experience.Any) return experience;
        var gain = new ExperienceGain(conn, monster, experience);
        foreach (var (owner, run) in Experience.Subscribers(conn)) GameEventOwners.Guard(owner, nameof(Experience), () => run(gain));
        return gain.Experience;
    }

    public static IReadOnlyDictionary<string, int> RunStats(ClientState conn)
    {
        if (!Stats.Any) return ImmutableDictionary<string, int>.Empty;
        var sheet = new StatSheet(conn);
        foreach (var (owner, run) in Stats.Subscribers(conn)) GameEventOwners.Guard(owner, nameof(Stats), () => run(sheet));
        return sheet.Added;
    }

    // ---- The game's own hand-overs ---------------------------------------------------------------------------------------

    // Optional gameplay shares the existing minion schedule and current connection writer.
    public static readonly GameEvent<Func<string, NetworkStream, ConnectionCiphers, ClientState, double, Task>> CompanionTick = new();
    public static readonly GameEvent<Func<ClientState, string, bool>> GrantedSkillExecutable = new();
    public static readonly GameEvent<Action<ClientState>> TransientClosing = new();
    public static readonly GameEvent<Action<ClientState, bool>> PauseChanged = new();
    public static readonly GameEvent<Func<ClientState, string?, ushort, (int X, int Y), Func<bool>, bool?>> SkillCastAt = new();
    public static readonly GameEvent<Action<KillContext>> Kill = new();
    /// <summary>Optional league systems' skills and transient skill-bar bindings.</summary>
    public static readonly GameEvent<Func<ClientState, IReadOnlyList<GrantedSkill>>> GrantedSkills = new();
    public static readonly GameEvent<Action<ClientState, ushort[]>> SkillBarBuilding = new();
    /// <summary>Equipment granted by a league system that waives its requirements.</summary>
    public static readonly GameEvent<Func<ClientState, IReadOnlyCollection<uint>>> RequirementExemptions = new();
    public static readonly GameEvent<Action<ClientState, Dictionary<uint, int>>> FinalStatsBuilding = new();
    public static readonly GameEvent<Func<ClientState, int, int>> IncomingDamage = new();
    /// <summary>False suppresses drops, ground publication and automatic gold collection.</summary>
    public static readonly GameEvent<Func<ClientState, bool>> LootAllowed = new();
    /// <summary>True when an optional system handled closing the native reward dialogue.</summary>
    public static readonly GameEvent<Func<ClientState, Task<bool>>> RewardClosing = new();
    /// <summary>A league encounter may refuse unrelated travel while its gate is closed.</summary>
    public static readonly GameEvent<Func<ClientState, PendingTransfer, string?>> TravelRefusal = new();
    /// <summary>The Auth transfer answer, after failed-transfer recovery and before a successful ticket is sent.</summary>
    public static readonly GameEvent<Func<ClientState, PendingTransfer, ControlResponse, Task>> TransferResolved = new();
    /// <summary>The player came back at a checkpoint or in town: a league encounter may restart its run.</summary>
    public static readonly GameEvent<Func<ClientState, Task>> Resurrected = new();

    public static IReadOnlyList<GrantedSkill> ExtraSkills(ClientState conn) =>
        [.. GrantedSkills.For(conn).SelectMany(run => run(conn))];

    public static HashSet<uint> ExemptItems(ClientState conn) =>
        [.. RequirementExemptions.For(conn).SelectMany(run => run(conn))];

    public static int AdjustIncomingDamage(ClientState conn, int damage)
    {
        foreach (var run in IncomingDamage.For(conn)) damage = run(conn, damage);
        return damage;
    }

    public static bool AllowsLoot(ClientState conn) => LootAllowed.For(conn).All(run => run(conn));

    public static async Task<bool> AnyRewardClosing(ClientState conn)
    {
        foreach (var run in RewardClosing.For(conn)) if (await run(conn)) return true;
        return false;
    }

    public static async Task RunResurrected(ClientState conn)
    {
        foreach (var run in Resurrected.For(conn)) await run(conn);
    }
    /// <summary>Talking to an NPC starts; true when a module has its own dialogue for it.</summary>
    public static readonly GameEvent<Func<ClientState, uint, SpawnedNpc, Task<bool>>> NpcTalk = new();
    /// <summary>A connection's chat commands are made: a module adds to them.</summary>
    public static readonly GameEvent<Func<ChatCommands, string, NetworkStream, ConnectionCiphers, ClientState, ChatCommands>> CommandsBuilding = new();
    /// <summary>The campaign driver's /state is made (Agents/docs/dev/campaign-automation.md): a module adds the fields of
    /// what it runs (the Pantheon's "pantheon").</summary>
    public static readonly GameEvent<Action<ClientState, System.Text.Json.Nodes.JsonObject>> StateBuilding = new();
    public static readonly GameEvent<Func<ClientState, Dictionary<uint, int>, Dictionary<uint, int>>> PlayerStatSources = new();
    public static readonly GameEvent<Func<ClientState, IReadOnlyDictionary<uint, int>, SpawnedMonster, DamagePacket, DamagePacket>> OutgoingDamage = new();
    /// <summary>A click on an object; true when it was this module's.</summary>
    public static readonly GameEvent<Func<ClientState, InstanceTransfer.UseSkillOnObject, double, Task<bool>>> ObjectUse = new();
    /// <summary>A click on an item on the ground; true when it was this module's.</summary>
    public static readonly GameEvent<Func<ClientState, uint, WorldItemObject, InstanceTransfer.UseSkillOnObject, double, Task<bool>>> GroundItemUse = new();
    /// <summary>An NPC option was picked, before its action; false stops it.</summary>
    public static readonly GameEvent<Func<ClientState, NpcCatalog.Talk, Task<bool>>> NpcOptionPicked = new();
    /// <summary>A read of client messages was handled.</summary>
    public static readonly GameEvent<Func<ClientState, IReadOnlyList<ushort>, Task>> MessagesHandled = new();
    public static readonly GameEvent<Func<ClientState, InstanceItems.PickUpItem, Task<bool>>> InventoryPick = new();
    public static readonly GameEvent<Func<ClientState, InstanceItems.PlaceItem, Task<bool>>> InventoryPlace = new();
    public static readonly GameEvent<Func<ClientState, Task>> NpcDialogueClosing = new();
    /// <summary>A gem skill's cast: its effect id, skill id, where from, the action; true when the module took it, null for not its skill.</summary>
    public static readonly GameEvent<Func<ClientState, string?, ushort, (int X, int Y), Func<bool>, bool?>> SkillCast = new();
    /// <summary>A currency used; true when it was this module's.</summary>
    public static readonly GameEvent<Func<ClientState, byte, InventoryItem, ItemCatalog.BaseType, Task<bool>>> ItemUse = new();
    /// <summary>A warcry with open_nearby_chests_on_warcry went off: a module opens its chests within the radius (cells).</summary>
    public static readonly GameEvent<Func<ClientState, (int X, int Y), int, Task>> ChestsOpened = new();
    /// <summary>A party line ("%…"), cleaned: the party is a module's to know, the text is the Chat module's; true when
    /// the module delivered it to the party.</summary>
    public static readonly GameEvent<Func<ClientState, string, IReadOnlyList<ChatLineItem>?, Task<bool>>> PartyChat = new();

    public static async Task<bool> AnyNpcTalk(ClientState conn, uint id, SpawnedNpc npc)
    {
        foreach (var run in NpcTalk.For(conn)) if (await run(conn, id, npc)) return true;
        return false;
    }

    public static ChatCommands RunCommandsBuilding(ClientState conn, ChatCommands commands, string remote, NetworkStream stream, ConnectionCiphers ciphers)
    {
        // With no connection, for the list tools read: every module's commands.
        foreach (var run in conn is null ? CommandsBuilding.All : CommandsBuilding.For(conn)) commands = run(commands, remote, stream, ciphers, conn!);
        return commands;
    }

    public static void RunStateBuilding(ClientState conn, System.Text.Json.Nodes.JsonObject state)
    {
        foreach (var run in StateBuilding.For(conn)) run(conn, state);
    }

    public static async Task RunKill(KillContext kill)
    {
        foreach (var run in Kill.For(kill.Conn)) run(kill);
        foreach (var send in kill.Afterwards) await send();
    }

    public static Dictionary<uint, int> WithPlayerStatSources(ClientState conn, Dictionary<uint, int> sources)
    {
        foreach (var run in PlayerStatSources.For(conn)) sources = run(conn, sources);
        return sources;
    }

    public static DamagePacket WithOutgoingDamage(ClientState conn, IReadOnlyDictionary<uint, int> player, SpawnedMonster monster, DamagePacket rolled)
    {
        foreach (var run in OutgoingDamage.For(conn)) rolled = run(conn, player, monster, rolled);
        return rolled;
    }

    public static async Task<bool> AnyObjectUse(ClientState conn, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        foreach (var run in ObjectUse.For(conn)) if (await run(conn, use, requestedAt)) return true;
        return false;
    }

    public static async Task<bool> AnyGroundItemUse(ClientState conn, uint id, WorldItemObject ground, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        foreach (var run in GroundItemUse.For(conn)) if (await run(conn, id, ground, use, requestedAt)) return true;
        return false;
    }

    public static async Task<bool> AllowNpcOption(ClientState conn, NpcCatalog.Talk talk)
    {
        foreach (var run in NpcOptionPicked.For(conn)) if (!await run(conn, talk)) return false;
        return true;
    }

    public static async Task RunMessagesHandled(ClientState conn, IReadOnlyList<ushort> ids)
    {
        foreach (var run in MessagesHandled.For(conn)) await run(conn, ids);
    }

    public static async Task<bool> AnyInventoryPick(ClientState conn, InstanceItems.PickUpItem pick)
    {
        foreach (var run in InventoryPick.For(conn)) if (await run(conn, pick)) return true;
        return false;
    }

    public static async Task<bool> AnyInventoryPlace(ClientState conn, InstanceItems.PlaceItem place)
    {
        foreach (var run in InventoryPlace.For(conn)) if (await run(conn, place)) return true;
        return false;
    }

    public static async Task RunChestsOpened(ClientState conn, (int X, int Y) at, int radius)
    {
        foreach (var run in ChestsOpened.For(conn)) await run(conn, at, radius);
    }

    public static async Task RunNpcDialogueClosing(ClientState conn)
    {
        foreach (var run in NpcDialogueClosing.For(conn)) await run(conn);
    }

    public static bool? RunSkillCast(ClientState conn, string? effect, ushort skillId, (int X, int Y) from, Func<bool> current)
    {
        foreach (var run in SkillCast.For(conn)) if (run(conn, effect, skillId, from, current) is { } taken) return taken;
        return null;
    }

    public static async Task<bool> AnyItemUse(ClientState conn, byte row, InventoryItem item, ItemCatalog.BaseType type)
    {
        foreach (var run in ItemUse.For(conn)) if (await run(conn, row, item, type)) return true;
        return false;
    }

    public static async Task<bool> RunPartyChat(ClientState conn, string text, IReadOnlyList<ChatLineItem>? items)
    {
        foreach (var run in PartyChat.For(conn)) if (await run(conn, text, items)) return true;
        return false;
    }
}
