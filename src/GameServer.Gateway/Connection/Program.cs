using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using WalkEstimator = GameServer.Core.World.WalkEstimator;
using PlayerWalk = GameServer.Core.World.PlayerWalk;
using ClientPathfinder = GameServer.Core.World.ClientPathfinder;
using Progression = GameServer.Core.World.Progression;
using DamageModel = GameServer.Core.World.DamageModel;
using DamageType = GameServer.Core.World.DamageType;
using SaveFiles = GameServer.Core.Saves.SaveFiles;
using DamagePacket = GameServer.Core.World.DamagePacket;
using DamageRange = GameServer.Core.World.DamageRange;
using DamageOverTime = GameServer.Core.World.DamageOverTime;
using GameServer.Core.Diagnostics;

// GameServer.Gateway — the instance (game) server the client connects to after login 0x14, or after 0x27 on an area
// change (Agents/docs/protocol/02, 09).
// Reads the plaintext opening (0x0F ticket + 0x0E hello), asks GameServer.Auth which area/instance the ticket belongs to
// (InstanceControl "resolve"), answers 0x11 so both directions switch to Salsa20 keyed by the ticket's instance key,
// sends the area (0x1d), answers the client's 0x7d with the player and the area's area transitions, and handles ticks,
// movement and area transition clicks (0x24: change area, or with Ctrl the instance chooser; Agents/docs/protocol/09-area-change.md).
// Port: Game_GATEWAY_PORT (default 6112). Instance keys: data/instance-secret.bin (Game_INSTANCE_SECRET), shared with
// GameServer.Auth. Auth control: 127.0.0.1:Game_AUTH_CONTROL_PORT (20495); without it every ticket gets Lioneye's Watch.

// `commands`: one JSON line per chat command ({"name", "usage", "args"}) for tools such as the desktop launcher, then
// exit; nothing starts. `values <kind>`: what an argument of that kind can be ({"value", "detail"}), from the Gateway's
// own catalogs. `crafter`: the item crafter's bases, mods and stat descriptions (ItemCrafter.Catalog,
// Agents/docs/dev/item-crafter.md). Before ServerLog, whose timestamps would break the JSON lines.
if (args is ["crafter"])
{
    // The catalogs' own load lines would break the JSON.
    var stdout = Console.Out;
    Console.SetOut(TextWriter.Null);
    var lines = ItemCrafter.Catalog(Items, Rolls, Bench, StatDescriptions.Load()).Select(l => l.ToJsonString()).ToList();
    Console.SetOut(stdout);
    foreach (string line in lines) Console.WriteLine(line);
    return 0;
}
if (args is ["commands"] or ["values", _])
{
    // The modules give the commands and the values: their load lines would break the JSON.
    var stdout = Console.Out;
    Console.SetOut(TextWriter.Null);
    StartMods();
    Console.SetOut(stdout);
}
if (args is ["commands"])
{
    foreach (var (name, usage, arguments) in CommandUsages()) Console.WriteLine(JsonSerializer.Serialize(new { name, usage, args = arguments }));
    return 0;
}
if (args is ["values", var kind])
{
    if (GameLogicOrNull?.CompletionValues(kind) is not { } values) { Console.Error.WriteLine($"[gateway] no values of kind {kind}"); return 1; }
    foreach (var (value, detail) in values) Console.WriteLine(JsonSerializer.Serialize(new { value, detail }));
    return 0;
}

ServerLog.Install("gateway");

// dotnet run --project src/GameServer.Gateway -- seed-game-data [database]
// Fills the SQLite game-data overlay from data/tables without starting the servers. The first Gateway start does the
// same; this mode is for a fresh database, an editor or a check while the Gateway is stopped.
if (args.Length > 0 && args[0].Equals("seed-game-data", StringComparison.OrdinalIgnoreCase))
{
    string path = args.Length > 1 ? args[1] : Saves.DefaultPath;
    try
    {
        GameData.Open(path);
        var (stats, mods, modStats) = (GameData.Store
            ?? throw new InvalidOperationException("the game-data store did not open")).ReferenceCounts();
        if (stats == 0 || mods == 0 || modStats == 0)
        {
            Console.Error.WriteLine("[gateway] game data seed: Stats.json/Mods.json are missing; run scripts/dev/extract-data.ps1 first");
            return 1;
        }
        Console.WriteLine($"[gateway] game data seeded into {path}: {stats} stat(s), {mods} mod(s), " +
            $"{modStats} default mod stat row(s)");
        return 0;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"[gateway] game data seed failed: {e.Message}");
        return 1;
    }
}

int port = GatewayEndpoint.Port;
var keyring = InstanceKeyring.LoadOrCreate(Environment.GetEnvironmentVariable("Game_INSTANCE_SECRET") ?? Path.Combine("data", "instance-secret.bin"));

// The HNC side channel needs a 64-byte table from the client: extracted to data/, not shipped.
if (!File.Exists(GameServer.Data.Ggpk.ClientConstants.DefaultFile))
{
    Console.Error.WriteLine($"[gateway] {GameServer.Data.Ggpk.ClientConstants.DefaultFile} not found; run .\\scripts\\dev\\extract-data.ps1");
    return 1;
}
HncSender.UseMask(GameServer.Data.Ggpk.ClientConstants.Load().HncMaskBytes);
if (GameDataFiles.Warning() is { } missingData) Console.WriteLine(missingData);
if (GameDataFiles.OutdatedWarning() is { } outdatedData) Console.WriteLine(outdatedData);

// Characters and stashes: the save database (Saves). Saves from before it (data/saves, data/stashes) go into it once; one
// that can't be read stops the Gateway, so no character starts over.
try
{
    Saves.Open(Saves.DefaultPath);
    Saves.ImportJson(Saves.Db, "data", CharacterSaves.Opts);
    // The short-lived parsing thread releases its pooled buffers after startup.
    GameTables.Isolated(() => GameData.Open(Saves.Db.Path));
    Console.WriteLine($"[gateway] saves: {Saves.Db.Path}");
}
catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException)
{
    Console.Error.WriteLine($"[gateway] the save database can't be opened: {e.Message}");
    return 1;
}

// Every item sent carries its level requirement (13-items.md §2d): the client shows and checks the byte it receives.
// Gems have their own (GrantedEffectsPerLevel), which the client reads from the SkillGem component instead. Maps have
// none, as in the game: their drop level is where they drop, not who may run them.
Item.LevelRequirementOf = item => !item.Components.Contains("SkillGem") && Items.ByHash(item.TypeHash) is { Class: not (MapDevice.MapClass or MapDevice.MapKeyClass) } t
    ? ItemModStats.LevelRequirement(item, t.DropLevel, Items.Mods) : 0;

// Local play only by default; Game_BIND=0.0.0.0 to accept other machines (then also set Game_GATEWAY_HOST).
var bind = IPAddress.Parse(Environment.GetEnvironmentVariable("Game_BIND") ?? "127.0.0.1");
var listener = new TcpListener(bind, port);
// Before any client connects: Auth forgets the players of this Gateway's last run, which may have stopped without
// sending "leave" for them (their instances would never expire).
var started = await InstanceControl.CallAsync(new ControlRequest { Op = "gateway_started" });
if (!started.Ok) Console.WriteLine($"[gateway] Auth did not take gateway_started: {started.Error}");
// The mods (Mods/Binaries/<id>, Program.Mods.cs), before the first player can use one.
StartMods();
// Loading the tables above (Mods.json alone is 60 MB of JSON) leaves ~0.5 GB of free GC memory committed; give it back
// before serving, since several stacks often run side by side (Agents/docs/dev/performance.md).
GameTables.Release();
GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
listener.Start();
Console.WriteLine($"[gateway] listening on {bind}:{port}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
// What waits in memory to be saved (experience, flask charges from kills) is saved every few seconds, and at the end.
var saving = SaveUnsavedProgress(cts.Token);
AppDomain.CurrentDomain.ProcessExit += (_, _) => SaveUnsavedProgressNow("the Gateway stops");

while (!cts.IsCancellationRequested)
{
    TcpClient client;
    try
    {
        client = await listener.AcceptTcpClientAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }

    _ = HandleAsync(client, keyring);
}

listener.Stop();
await saving;
SaveUnsavedProgressNow("the Gateway stopped");
Console.WriteLine("[gateway] stopped");
return 0;

// Experience and flask charges from kills wait in memory (CharacterSaves.AddExperience, PlayerInventories.ChangeUnsaved)
// and are saved here at least every few seconds: a crash or a power cut loses at most those seconds of them.
static async Task SaveUnsavedProgress(CancellationToken stop)
{
    using var timer = new PeriodicTimer(CharacterSaves.ExperienceSaveInterval);
    try
    {
        while (await timer.WaitForNextTickAsync(stop)) SaveUnsavedProgressNow(null);
    }
    catch (OperationCanceledException) { }
}

sealed partial class Program
{
    internal const uint PlayerId = 0x1000;
    // The server's tick: 30 per second. Nothing the server times (an attack) is shorter than one tick.
    internal const int TickRate = 30;
    internal const double TickSeconds = 1.0 / TickRate;
    // Destination tickets of waypoint travel: that connection's player arrives next to the area's waypoint.
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, byte> WaypointArrivals = new();

    /// <summary>Tickets whose connection arrives at a given cell (back through a town portal).</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (int X, int Y)> SpotArrivals = new();
    // The skills granted to players (the default attack); loaded with the first player.
    internal static readonly SkillCatalog Skills = new();
    // Stat names ↔ client keys (Stats.dat row + 1).
    internal static readonly StatCatalog Stats = new();
    // Item types (BaseItemTypes + data/item-types.json); loaded with the first player.
    internal static readonly ItemCatalog Items = new();
    // What dying monsters drop (ItemDrops.cs).
    // Magic and rare items (ItemRolls.cs).
    internal static readonly ItemRolls Rolls = new(Items);
    internal static readonly BenchCrafting Bench = new(Items, Rolls);
    // Each live area instance's ground items, across connections (InstanceStore.cs). The lifetime is Auth's
    // InstanceRegistry.Lifetime: the Gateway wipes an instance when Auth would.
    internal static readonly InstanceStore Instances = new(TimeProvider.System, new GameServer.Core.World.InstanceRegistry().Lifetime);
    // The passive tree's nodes (PassiveSkills.dat by PassiveSkillGraphId).
    internal static readonly PassiveTree Passives = new();

    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ConnectionCiphers, SemaphoreSlim> SendGates = new();

    // The client's walking speed in grid cells per second at 100% (measured live: ~37 = Character.ot Pathfinding
    // base_speed). Game_WALK_SPEED overrides it.
    internal static readonly double WalkSpeed =
        double.TryParse(Environment.GetEnvironmentVariable("Game_WALK_SPEED"), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 37;

    // Game_TRACE=move (or the older Game_MOVE_TRACE=1) logs "[move-trace] qpc=…" for the client's 0x24/0x25/0x191 as read
    // and the server's 0x1ab/0x1b7/0x1ba as written, on the machine's QPC clock (scripts/re/frida/trace-movement.js uses
    // the same clock).
    internal static readonly bool MoveTrace = ServerTrace.Move;

    // The server frame length (ms, S→C 0x2b6): a lockstep client's world advances by these frames. Game_FRAME_MS overrides.
    internal static readonly byte FrameMs =
        byte.TryParse(Environment.GetEnvironmentVariable("Game_FRAME_MS"), out var fm) && fm > 0 ? fm : (byte)33;
}
