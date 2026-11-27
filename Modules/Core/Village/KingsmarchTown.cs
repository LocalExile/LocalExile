using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

/// <summary>
/// The town objects of Kingsmarch (<c>KalguuranSettlersLeague</c>): the buildings, platforms, wharves and props the server
/// must create -the client draws the room's own doodads, but the town's floor, docks and buildings are objects of the
/// <c>Metadata/Terrain/Leagues/Settlers/HubTown/Objects/*</c> types, which is why the authored furniture floats without them.
///
/// The list is embedded (<see cref="TownLayout"/>): the client's <c>room_doodads</c> rows of the <c>kingsmarch.arm</c>
/// room, index 1984 - type, model, room-local cell and angle (the bytes the old <c>data/kingsmarch-town.json</c> carried).
/// A type whose components this blob cannot write is skipped and logged, never sent half-written: a short blob makes the
/// client report "Buffer Underflow" and quit.
///
/// <see cref="TownObjectLimit"/> is how many objects to create. The safest types (Positioned, BaseEvents, Animated,
/// StateMachine, InteractionAction, MinimapIcon) come first, so the lowest limits exercise the least uncertain blobs.
/// </summary>
public static class KingsmarchTown
{
    const string Area = "KalguuranSettlersLeague";

    /// <summary>The per-character save key in <see cref="CharacterSaves"/>' ModuleData: the town's farm plots, its workers
    /// and what it has produced, one record per character (the old data/kingsmarch-farm.json was a single file shared by
    /// every character, so a character's save clobbered another's in a co-op write race).</summary>
    const string TownSaveKey = "kingsmarch-town";

    /// <summary>How many town objects one area load creates. VillageBaseStats row 20728
    /// (<c>village_max_number_of_workers</c>, the town's population cap) is 90 - room enough for every object this build
    /// places, and the constant the old data/kingsmarch-limit.txt stood in for (its missing-file default was 0, which
    /// placed no buildings at all).</summary>
    const int TownObjectLimit = 90;

    // The town's own panels are gated on the IsMapOwner quest flag (NPCTalk 667 "Manage Town", 670 "Town Recruitment",
    // 672 "Disenchanting", 674 "Shipping"): without it their options are in the list but the server refuses them. The town
    // instance belongs to the player, so they own it.
    //
    // The client hides a row for a second reason: NPCTalk column 16 is a list of league quest flag ids the row requires,
    // and the client's own availability test (0x141E7BB90) reads that list at row+0xbe/+0xc6 and tests the second flag set
    // at player+0x1d0 before the row is listed at all. Row 3610 "Manage Town" (column 16 [334]) was therefore never
    // offered. The id is fluid: row 3611, the later variant of the same option, requires 1436 instead.
    internal const int KingsmarchTownOwnerLeagueFlag = 334;

    // The King's Hand's other two village options are gated the same way: row 3612 "Fund the Treasury" needs flag 1,
    // row 3613 "Invest in Recruitment" needs flag 1434, and Raulf's row 3556 "Town Recruitment" needs flag 95.
    internal const int KingsmarchFundTreasuryLeagueFlag = 1;
    internal const int KingsmarchInvestRecruitmentLeagueFlag = 1434;
    internal const int KingsmarchRecruitmentLeagueFlag = 95;

    // The town panel is drawn from the records the client keeps on its ClientVillage (the ruleset list at +0xcfd8): wire
    // 0x1f with a record's kind builds one from the client's own factory table (kind 0x9b is the town record, 0x5a8 bytes,
    // 0x74 the farm record, 0x2e0 bytes) and the client skips a kind it already has. The value beside the kind is ignored
    // by the factories (client dispatcher 0x142150ae0 case 0x1f -> slot 8 0x1415a6800 -> allocator 0x141763b20).
    //
    // The kinds and the event names below are the ones the old dev files (data/kingsmarch-records.txt,
    // data/kingsmarch-events.txt) carried; they are code now, because a missing file meant no record at all - and an
    // empty record is what crashed the client on entering the town (2026-11-21 22:37, fault Game.patched.exe+0xdefc9a).

    /// <summary>What the client's town panel is told about the town goes out over the 0x2c9 state channel the Village
    /// module pushes; this module writes no panel file of its own any more (the old data/kingsmarch-panel.json was a
    /// global file every connection overwrote, so two towns in one server overwrote each other's numbers every second).</summary>

    /// <summary>The settlement record kinds the town sends: 0x9b is the town record (the client's ClientVillage), the one
    /// kind this build's panels read.</summary>
    static byte[] RecordKinds() => [0x9b];

    /// <summary>The village event names sent after each settlement record: the record's event handler (client
    /// 0x14176a8e0) answers these three (its strings 0x14348e980, 0x14348e990, 0x14348e928).</summary>
    static string[] EventNames() => ["village_imgui", "reroll_recruits", "super_reroll_recruits"];

    /// <summary>The village UI events (wire 0x7b) sent once per area load. Empty on purpose: the town's panels are opened
    /// and filled by the Village module's 0x2c9 state channel now, so there is no 0x7b probe left to send (the old
    /// data/kingsmarch-ui.txt drove the same dev walk through the client's screen table - 0x54 Recombinator through
    /// 0x5e Currency Exchange - that this empty list replaces).</summary>
    static readonly (byte Kind, uint[] Args)[] VillageUiMessages = [];

    /// <summary>Sends the village UI events once per area load. The client's town panel is built with the area, so these
    /// go out from the tick, after the area has started.</summary>
    static async Task EnsureVillageUi(IPlayer player, Progress progress)
    {
        if (progress.Ui) return;
        progress.Ui = true;
        foreach (var (kind, args) in VillageUiMessages)
            await player.Send(InstanceWaypoints.OpenUi(kind, args[0], args[1], args[2], args[3]),
                $"0x007b village ui kind 0x{kind:x} ({string.Join(' ', args.Select(a => a.ToString("x")))})");
    }

    /// <summary>Reads the settlement records the client's town panel reads. The client keeps one of each kind, so this is
    /// sent once per area load and repeats are ignored by the client itself.</summary>
    static async Task EnsureSettlementRecords(IPlayer player, Progress progress)
    {
        if (progress.Records) return;
        progress.Records = true;
        var events = EventNames();
        foreach (var kind in RecordKinds())
        {
            await player.Send(InstanceProtocol.AddRuleset(kind), $"0x001f settlement record kind 0x{kind:x}");
            // The record on its own is empty: the client crashed seven seconds after it with fault
            // Game.patched.exe+0xdefc9a (2026-11-21 22:37). The record's event handler (client 0x14176a8e0) answers
            // three event names; the wrong one leads the client into a UI path that is not set up yet and it crashes on
            // the way in (0x54ee9d8c, 2026-11-21 23:31).
            foreach (var name in events)
                await player.Send(InstanceProtocol.VillageEvent(kind, name), $"0x001e village event {name} for kind 0x{kind:x}");
        }
    }

    /// <summary>The settlement records the client's town panel reads, sent before the area starts (with the player's own
    /// messages, ahead of 0x21). The client builds the town's window when the area starts and reads the village's records
    /// once, there (client 0x140521d00, the widget's constructor, scans village+0xcfd8 and skips it when the list is
    /// empty), so a record that arrives later is never read: the panel's gates stay shut and the town's button does
    /// nothing.</summary>
    public static IEnumerable<(string Log, byte[] Message)> BeforeEnterMessages(IPlayer player)
    {
        if (!string.Equals(player.AreaId, Area, StringComparison.Ordinal)) return [];
        var messages = new List<(string, byte[])>();
        var events = EventNames();
        foreach (var kind in RecordKinds())
        {
            messages.Add(($"0x001f settlement record kind 0x{kind:x}", InstanceProtocol.AddRuleset(kind)));
            foreach (var name in events)
                messages.Add(($"0x001e village event {name} for kind 0x{kind:x}", InstanceProtocol.VillageEvent(kind, name)));
        }
        // They are on the wire with the area. The tick repeats them a few seconds later on purpose: the client's village
        // is built by the area-load function, so a record sent before it has no village to go into, and the client skips
        // a kind it already has, which makes the repeat harmless when the first one did land.
        return messages;
    }

    static async Task EnsureOwnerFlag(IPlayer player)
    {
        // NPCTalk 3581 ("Shipping") is gated twice. The client's row test (0x141e7bb90) reads two flag sets: QuestFlag1..3
        // are looked up in the player's set, while the unnamed raw-id columns are looked up in the second set (the league
        // set) as id-1. The server then repeats the check itself: NpcCatalog.Available needs the character's own set to
        // hold the row's QuestFlag1, and it fails silently.
        const int IsMapOwnerRow = 2020, ShippingGateRow = 1386;
        var mod = Program.PlayerOf(player);
        var character = mod.Character;
        // Once per connection, and again after an area load (Entered forgets the counter): the flag sets are projections, so
        // they replace the client's, and the dialogue list is rebuilt per area.
        if (_owner.GetValue(player, _ => new Counter()).Value++ != 0) return;
        // "IsMapOwner" is print-only in QuestFlags, so Quests.SetQuestFlags drops it in CommitQuestFlags and it never
        // reaches the connection's set; add it here, where both the server's dialogue gate and PlayerQuestFlags read it.
        bool added;
        lock (character.QuestFlags) added = character.QuestFlags.Add("IsMapOwner");
        if (added) Console.WriteLine($"[gateway] kingsmarch owner flag: IsMapOwner added to {character.Name}");
        await Game.SetLeagueQuestFlags(mod, [KingsmarchTownOwnerLeagueFlag, ShippingGateRow,
            KingsmarchFundTreasuryLeagueFlag, KingsmarchInvestRecruitmentLeagueFlag, KingsmarchRecruitmentLeagueFlag]);
        var flags = Game.PlayerQuestFlags(mod);
        await Game.SendWorldRecord(player, mod.Id, InstanceEntities.PlayerQuestFlagsUpdate(mod.Id, flags),
            $"0x02bc village quest flags: {flags.Count} flag(s), IsMapOwner {(flags.Contains(IsMapOwnerRow) ? "in" : "MISSING")}");
    }

    // Brackets, Functions, Render, Preload and Targetable write nothing: their client readers are the ret-family (0x1400f1180)
    // or they are client-block only, so a type that lists them is still writable.
    static readonly HashSet<string> Supported =
        ["Positioned", "BaseEvents", "Animated", "StateMachine", "TriggerableBlockage", "InteractionAction", "MinimapIcon",
         "Functions", "Brackets", "Render", "Preload", "Targetable"];

    // Components whose wire layout this file is sure of (InstanceEntities/QuestObject/HideoutObject).
    static readonly HashSet<string> Sure =
        ["Positioned", "BaseEvents", "Animated", "StateMachine", "InteractionAction", "MinimapIcon"];

    sealed record TownObject(string Type, string Model, int X, int Y, float Angle);

    // The room's origin (kingsmarch.arm roomX/roomY): every layout row's room-local cell is offset by it.
    static readonly (int X, int Y) _origin = (138, 138);
    static Dictionary<string, (string[] Components, string[] States)>? _types;
    // Per connection: the next object row and id. A connection is one area load, so leaving and coming back starts over.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Progress> _progress = [];
    static readonly object _lock = new();
    // Placement is opt-in per Progress: a reload must not re-send objects the client still holds (that corrupts its
    // object map — crash 0x148859 on 2026-11-23 19:44), so only a real area load or /village_spawn turns it on, and the
    // flag lives on the Progress (one per connection) rather than in a single global. Arm() has no player context, so it
    // only seeds the flag a Progress starts with; Entered and the tick's new-instance branch arm their own Progress.
    static volatile bool _armPending;

    /// <summary>Lets the next ticks place the town's objects (dev: /village_spawn).</summary>
    public static void Arm() => _armPending = true;

    /// <summary>An area load: the client holds none of the town's objects yet, so it is built again from the saved farm.</summary>
    public static Task Entered(IPlayer player)
    {
        lock (_lock)
        {
            var progress = _progress.GetOrCreateValue(player);
            progress.Next = 0;
            progress.Plots.Clear();
            // The client reads the settlement records once per area load, so a new load has to send them again.
            progress.Records = false;
            progress.Ui = false;
            Console.WriteLine($"[gateway] kingsmarch area load: {player.AreaId} character {player.Character}");
            // The client is still loading the area; objects sent into a loading area are dropped.
            progress.Ready = UtcNow() + 3.0;
            Restore(progress, player.Character);
            // The area generation does not always fire (a cached layout re-entered from a town does not generate again),
            // so the tick also notices a new instance; either way a fresh area is what lets the town be placed again.
            progress.Armed = true;
        }
        return Task.CompletedTask;
    }

    /// <summary>The connection's town state, restored from the saved farm the first time anything asks for it. The store has
    /// to be readable in a load where the town's objects were not placed too: the port asks what the town is holding before
    /// any plot exists in the client.</summary>
    static Progress State(IPlayer player)
    {
        Progress progress;
        lock (_lock)
        {
            progress = _progress.GetOrCreateValue(player);
            if (progress.Who.Length == 0) Restore(progress, player.Character);
        }
        return progress;
    }

    sealed class Progress
    {
        public int Next;
        // The instance these objects were placed in: a new one means the client holds nothing and the town is built again.
        public object? Instance;
        // Ids start at a random-ish base so that a module reload (which starts a new Progress) does not re-send the same
        // ids into a town the client already has: re-creating an id it still knows leaves the old object in place.
        public uint Id = 0x5A000000u + (uint)(Environment.TickCount64 & 0xFF) * 0x100;
        // The farm plots this connection created: a click on one of these ids chooses its crop.
        public readonly Dictionary<uint, Plot> Plots = [];
        // The character whose farm this is, the crops it had stored when the area loaded, and what it has produced.
        public string Who = "";
        public List<SavedPlot> Saved = [];
        public readonly Dictionary<string, int> Store = [];
        // Before this (Unix seconds) the area is still loading and nothing may be sent.
        public double Ready;
        // Plots removed for a crop change, waiting for their re-create (the client's destroy is deferred).
        public readonly List<(double At, Plot Plot, int X, int Y)> Pending = [];
        // The workers hired for this character's town; each tends one plot, works the mine or the smeltery, or sits idle.
        public readonly List<Worker> Workers = [];
        // Which crop a bare plot takes when it is next clicked (the plots take turns at the five crops, since the farm
        // panel that would let a player choose one cannot be opened in this build).
        public int CropNext;
        // The town's clickable boards, since the client never builds its Kingsmarch panel in this build.
        public readonly Dictionary<uint, TownBoard> Boards = [];
        // Wall-clock seconds the town's wages were last settled at; wages are charged only for time the town is running,
        // so the time a character was away is settled once, by CatchUp, when the town is loaded again (RE C.3).
        public double WageSettledAt;
        // Wall-clock seconds the previous session last settled the town at: the start of the gap CatchUp fills in
        // (the town produces while its owner is away, until its treasury can no longer pay the wages). 0 = nothing owed.
        public double AwayFrom;
        // Wages earned but not yet a whole gold piece, so a slow trickle of work still costs what it owes.
        public double WageDebt;
        // Wall-clock seconds the farm was last written to the character's town save.
        public double PersistedAt;
        // Set when the treasury ran out: every job in the town stops producing until it is funded again. The town keeps
        // its state, so a single fill of the treasury restarts it (ClientStrings 7486/7684).
        public bool Halted;
        // True once this session placed the town's plots. A save made before that (a module reload while the character
        // stands in the town) has no plots in memory, and writing it would wipe the crops a character left growing.
        public bool Placed;
        // The reload-time pick-up has been tried for this record (it runs once, not on every tick).
        public bool Adopted;
        // The settlement records (0x1f) and the village UI events (0x7b) were sent for this area load.
        public bool Records;
        public bool Ui;
        // Whether this connection's ticks may place the town's objects: set by a real area load or a new instance
        // (Entered/the tick), or seeded by Arm() (/village_spawn) at creation. One flag per connection, not a
        // module-wide one - one player's reload must not re-place another's town.
        public bool Armed = _armPending;
    }

    /// <summary>What a click on one of the town's boards does.</summary>
    enum BoardAction
    {
        Treasury,   // put gold from the character's stash into the town's treasury
        Recruit,    // hire the recruiter's first candidate
        Farm,       // report what the plots hold
    }

    /// <summary>A board standing in the town: the client's own Kingsmarch panel is never built in this build, so the
    /// town's buttons are objects -walk up to the board, click it and the action happens.</summary>
    sealed class TownBoard
    {
        public BoardAction Action;
    }

    /// <summary>A hired worker: the plot it tends (-1 = idle) and what it has brought in.</summary>
    sealed class Worker    {
        public int Order;
        public int Plot = -1;
        public int Harvests;
        public string Name = "";
        // The worker's farming rank (1..5, the stat VillageBaseStats' 20773 caps at 5). It is the multiplier the rate
        // formula applies to that worker's contribution (RE C.1); the per-rank curve itself lives on the server and has
        // to be sampled, so this is the linear stand-in until it is measured.
        public int Rank = 1;
    }

    /// <summary>One of the town's six farm plots: the crop it is growing and how much of it has accumulated.
    ///
    /// Farming is continuous (RE 0.1): there is no planting, no growing, no maturing and no harvesting, and a plot with
    /// a crop left to itself turns time into crops. <see cref="Carry"/> is the fractional remainder the formula keeps on
    /// the plot between settlements, so the whole numbers land in the town's store one at a time.</summary>
    sealed class Plot
    {
        public string Who = "";    // the character whose farm this plot belongs to
        public uint ObjectId;      // the id the client knows this plot by (it changes on every crop change)
        public int Order;          // 0..5, the plot's index in the room
        public byte StateIndex;    // the StateMachine component's index, for its 0x2bc update
        public string[] States = [];   // the type's shared state names, in wire order
        public TownObject Row = null!; // the room row, so the plot can be re-created in another plant state
        public (string[] Components, string[] States) Type;
        public int Crop;           // 0 = nothing growing, 1..5 = the plant state value
        public double Carry;       // the fraction of a crop grown but not yet reached a whole one
        // Wall-clock seconds (Unix epoch) of the last settlement. A wall clock, not the process tick counter, because the
        // town has to settle for the time the character was away.
        public double SettledAt;
        // When the client was last sent this plot. A plot is never removed again within a few seconds of being created: the
        // client's destroy is deferred and tearing down an object it has only just built is what corrupted its object map.
        public double CreatedAt;
        // The plot is between two objects: its crop was changed, the old object was destroyed and the new one is waiting
        // for the next tick. Its id is a dead one, so nothing may be written to it and a click only says to wait.
        public bool Replanting;
        public int Produced;       // whole crops this plot has handed to the town's store
    }

    /// <summary>A plot as stored in the character's town save (CharacterSaves' ModuleData "kingsmarch-town").</summary>
    sealed class SavedPlot
    {
        // The object id the client knows this plot by. It is saved so a module reload, which starts a fresh record while
        // the client still holds the town's objects, can pick the plots back up under the ids they were created with
        // instead of sending new ones into a map the client already has.
        public uint Id { get; set; }
        public int Crop { get; set; }
        public double Carry { get; set; }
        public double Settled { get; set; }
        public int Produced { get; set; }
    }

    /// <summary>One character's farm: its plots, its workers and what it has produced. MineStarted and SmeltStarted hold
    /// the running job cycles so a reload does not hand out free ore (0 = none running, restored to now).</summary>
    sealed class SavedFarm
    {
        public List<SavedPlot> Plots { get; set; } = [];
        public List<SavedWorker> Workers { get; set; } = [];
        public Dictionary<string, int> Store { get; set; } = [];
        public double MineStarted { get; set; }
        public double SmeltStarted { get; set; }
        // Wall-clock seconds the town's wages were last settled at, so the offline time a character was away is not paid
        // for twice (the treasury is charged only for time the town is running, RE C.3).
        public double WageSettled { get; set; }
    }

    /// <summary>A worker as stored in the character's town save (CharacterSaves' ModuleData "kingsmarch-town").</summary>
    sealed class SavedWorker
    {
        public int Plot { get; set; } = -1;
        public int Harvests { get; set; }
        public string Name { get; set; } = "";
        public int Rank { get; set; } = 1;
    }

    // The towns this process has touched, by character. The save itself lives in the character's own save row, in
    // CharacterSaves' ModuleData under <see cref="TownSaveKey"/>, so one character's town is never written by another's.
    static readonly Dictionary<string, SavedFarm> _farms = [];

    /// <summary>The character's saved farm: read from the character's own save row the first time it is asked for, and
    /// kept in memory afterwards.</summary>
    static SavedFarm Farm(string who)
    {
        lock (_farms)
        {
            if (_farms.TryGetValue(who, out var farm)) return farm;
            if (who.Length > 0)
            {
                try
                {
                    string? json = CharacterSaves.ModuleData(who, TownSaveKey);
                    if (json is { Length: > 0 } text && JsonSerializer.Deserialize<SavedFarm>(text) is { } saved)
                        _farms[who] = farm = saved;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[gateway] kingsmarch: {who}'s town save is unreadable, starting it over: {e.Message}");
                }
            }
            if (!_farms.TryGetValue(who, out farm)) _farms[who] = farm = new SavedFarm();
            return farm;
        }
    }

    /// <summary>Writes the character's farm back into its own save row (an atomic per-character change), so a crash
    /// mid-write cannot leave a half-written town behind and a co-op neighbour's save is never touched.</summary>
    static void SaveFarm(string who)
    {
        if (who.Length == 0) return;
        SavedFarm? farm;
        lock (_farms) _farms.TryGetValue(who, out farm);
        if (farm is null) return;
        try
        {
            CharacterSaves.ChangeModuleData(who, TownSaveKey,
                _ => JsonSerializer.Serialize(farm, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Console.WriteLine($"[gateway] kingsmarch: cannot write {who}'s town save: {e.Message}");
        }
    }

    // The client registers object types only from its own dat tables at startup (Metadata/GameObjectRegister: MonsterVarieties,
    // Chests, NPCs, Projectiles, Characters, Pet, MiscObjects, HideoutDoodads) and nothing a server sends can add one. Six
    // HubTown types are not in it, so each stands as the accepted type with the same components and keeps its own model
    // (the model is what the client draws; only the type key must be one it knows).
    static readonly Dictionary<string, string> Substitute = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeAnimated"] = "Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase",
        ["Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeLarge"] = "Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase",
        ["Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPCIdle"] = "Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase",
        ["Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPC"] = "Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase",
        ["Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Seagull"] = "Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle",
        ["Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken"] = "Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Sheep",
        ["Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Peacock"] = "Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Sheep",
    };

    sealed class Counter { public int Value; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Counter> _diag = [];
    // How often the owner flag was granted for this connection: once per connection, and again after an area load.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Counter> _owner = [];

    /// <summary>Creates the town's objects up to <see cref="TownObjectLimit"/>, a few per tick, on the live connection.</summary>
    public static async Task Tick(IPlayer player, double now)
    {
        // Diagnosis: the first two ticks of a connection say what the module sees.
        if (_diag.GetValue(player, _ => new Counter()).Value++ < 2)
            Console.WriteLine($"[gateway] kingsmarch tick: area '{player.AreaId}' character {player.Character} seed {player.Seed} objects {Ordered().Count}");
        if (!string.Equals(player.AreaId, Area, StringComparison.Ordinal)) return;
        await EnsureOwnerFlag(player);

        Progress progress;
        object instance = Program.PlayerOf(player).Instance;
        // One clock for the whole farm: the wall clock in whole seconds, the same base the plots, the store's catch-up and
        // the harbour's trips are written on, so a town saved by one process is still in the right place in the next.
        double clock = UtcNow();
        lock (_lock)
        {
            progress = _progress.GetOrCreateValue(player);
            if (progress.Who.Length == 0) Restore(progress, player.Character);
            if (progress.Instance is null)
                // The first tick of a module load: adopt the instance the character is already in without placing the town
                // again, since the client holds objects this Progress knows nothing about.
                progress.Instance = instance;
            else if (!ReferenceEquals(progress.Instance, instance))
            {
                // A real area load: a new instance, so the client holds none of the town's objects and they are placed
                // again. This is the one case where re-sending the town is safe.
                progress.Instance = instance;
                progress.Next = 0;
                progress.Plots.Clear();
                progress.Ready = clock + 3.0;
                progress.Armed = true;
            }
        }
        _types ??= Types();
        // A module load that did not come from an area load: the client still holds the town's objects, so the farm is
        // picked back up from the save rather than placed again (see Adopt).
        if (!progress.Armed && !progress.Adopted)
        {
            progress.Adopted = true;
            Adopt(progress);
        }
        // The town panel is drawn from the settlement records (0x1f), the village events (0x1e) and the UI events (0x7b),
        // all sent from the constants above: a wrong kind crashes the client, so nothing is guessed at runtime.
        await EnsureSettlementRecords(player, progress);
        await EnsureVillageUi(player, progress);
        // What the town *places* is opt-in per connection (see Progress.Armed); what it *does* is not. A reload picks the
        // plots back up in Adopt, and from then on the farm, the mine and the smeltery keep running: gating those too is
        // what left a reloaded town standing still -no ore, no crops, and a replant whose new object was never created.
        bool placing = progress.Armed && clock >= progress.Ready;
        // The boards are the town's buttons and stand on their own: they are created whatever the object limit says, so a
        // low limit still leaves a way to fund the treasury, hire a worker and read the farm.
        if (placing) await Boards(player, progress, clock);

        await Flush(player, progress, clock);
        if (Work(progress) is { } news) await player.Tell(news);
        // The mine and the smeltery are the village module's own production lines (VillageProduction, the counters the
        // mine and blacksmith panels draw); this side runs the plots and the wages only, so the two engines that used
        // to produce the same ore twice are one.
        // The farm is a running rate, so it is written to the character's save on a timer rather than on every tick and
        // every crop. A plot waiting for its new object is never saved: the id it holds is one the client has just been
        // told to destroy, and a reload has to find ids it can pick up again.
        if (progress.Pending.Count == 0 && (progress.PersistedAt == 0 || UtcNow() - progress.PersistedAt >= 60))
        {
            progress.PersistedAt = UtcNow();
            Persist(progress);
        }
        if (!placing || TownObjectLimit <= 0) return;

        var objects = Ordered();
        int created = 0;
        while (progress.Next < objects.Count && progress.Next < TownObjectLimit && created < 20)
        {
            var row = objects[progress.Next++];
            var o = Substitute.TryGetValue(row.Type, out var stand) ? row with { Type = stand } : row;
            if (!_types.TryGetValue(o.Type, out var type))
            {
                Console.WriteLine($"[gateway] kingsmarch: {o.Type} is not a HubTown object type, not created");
                continue;
            }
            if (!type.Components.All(Supported.Contains))
            {
                Console.WriteLine($"[gateway] kingsmarch: {o.Type} ({string.Join(", ", type.Components)}) cannot be written yet, not created");
                continue;
            }
            // A plot is created in the state of what grows on it, since that state is what the client builds its model from.
            int stateIndex = Array.IndexOf(type.Components, "StateMachine");
            bool plot = stateIndex >= 0 && o.Type.EndsWith("/FarmingPlot", StringComparison.OrdinalIgnoreCase);
            SavedPlot? saved = plot && progress.Plots.Count < progress.Saved.Count ? progress.Saved[progress.Plots.Count] : null;
            // A plot the character's workers were left on comes back with them on it.
            int order = progress.Plots.Count;
            int hands = plot ? progress.Workers.Count(w => w.Plot == order) : 0;
            uint id = progress.Id++;
            int x = _origin.X + o.X, y = _origin.Y + o.Y;
            await Game.SendWorldCreate(player, id,
                InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(o.Type),
                    Blob(x, y, o, type, plot ? saved?.Crop ?? 0 : null, plot ? hands : null)),
                $"0x02bb create Kingsmarch object 0x{id:x} {o.Type} at ({x}, {y}) plot {plot} workers {hands}");
            // No 0x1a9: the create blob's object flag (bit 1) already puts the object in the wake zone, and the explicit
            // enter is what the client answers with "ObjectInWakeZone was called -already in the wake zone".
            if (plot)
                lock (_lock) progress.Plots[id] = new Plot
                {
                    Who = progress.Who, ObjectId = id, Order = order, StateIndex = (byte)stateIndex, States = type.States,
                    Row = o, Type = type, Crop = saved?.Crop ?? 0, Carry = saved?.Carry ?? 0, Produced = saved?.Produced ?? 0,
                    // A plot comes back from the timestamp it was left at, so the town keeps producing while its owner is
                    // in a map: CatchUp settles that gap against the treasury before the ordinary tick takes over.
                    SettledAt = saved is { Settled: > 0 } kept ? Math.Max(kept.Settled, UtcNow() - AwayCap) : UtcNow(),
                    CreatedAt = UtcNow(),
                };
            if (plot) progress.Placed = true;
            created++;
        }
        // The plots are back, so the gap since the character left can be settled (once per load).
        if (progress.Placed) CatchUp(progress);
    }

    /// <summary>The town's clickable boards, on the plaza next to where the player arrives (590, 655): the treasury, the
    /// recruiter and the farm report. They are <c>NoticeboardInteractable</c> -the same components as the farm plots, which
    /// the client has been building and clicking without trouble.</summary>
    static readonly (BoardAction Action, int X, int Y, string Model)[] BoardRows =
    [
        (BoardAction.Treasury, 578, 668, "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/recruitment_noticeboard_01.ao"),
        (BoardAction.Recruit, 602, 668, "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/recruitment_noticeboard_02.ao"),
        (BoardAction.Farm, 590, 680, "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/farming_Interactable01_01.ao"),
    ];

    /// <summary>How much gold one click on the treasury board puts in (the character's stash pays, as with the NPC).</summary>
    const int TreasuryPick = 1000;

    /// <summary>Creates the town's boards -once per area load, and independent of the object limit. Off: the client
    /// answers a server-made <c>NoticeboardInteractable</c> with <c>Error executing GEAL -expected a number type</c> and
    /// then dies in its object map (fault RVA 0x148b38, 2026-11-19), and the treasury/recruiter/farm boards were the
    /// stand-in for the town panel, which the Village module now pushes over the 0x2c9 state channel. The old
    /// <c>data/kingsmarch-boards.txt</c> existence gate was this switch's dev form.</summary>
    static readonly bool SpawnBoards = false;   // the client still dies on a server-made NoticeboardInteractable

    static async Task Boards(IPlayer player, Progress progress, double clock)
    {
        if (!SpawnBoards) return;
        lock (_lock) if (progress.Boards.Count > 0) return;
        _types ??= Types();
        const string type = "Metadata/Terrain/Leagues/Settlers/HubTown/Objects/NoticeboardInteractable";
        if (!_types.TryGetValue(type, out var shape) || !shape.Components.All(Supported.Contains)) return;
        foreach (var (action, x0, y0, model) in BoardRows)
        {
            var row = new TownObject(type, model, x0, y0, 0f);
            uint id = progress.Id++;
            int x = _origin.X + row.X, y = _origin.Y + row.Y;
            await Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(type), Blob(x, y, row, shape)),
                $"0x02bb create Kingsmarch board 0x{id:x} {action} at ({x}, {y})");
            lock (_lock) progress.Boards[id] = new TownBoard { Action = action };
        }
    }

    /// <summary>A click on one of the town's boards.</summary>
    static async Task<bool> Pushed(IPlayer player, BoardAction action)
    {
        switch (action)
        {
            case BoardAction.Treasury:
                await player.Tell("Kingsmarch: " + await FundTreasury(player, TreasuryPick));
                return true;
            case BoardAction.Recruit:
                await player.Tell("Kingsmarch: " + await Recruit(player, 1));
                return true;
            default:
                await player.Tell(StoreText(player));
                return true;
        }
    }

    /// <summary>Dev: a stash on the player's own cell. A <c>0x2bb</c> object never stamps the area's walk grid, so this is the
    /// reference for "does the client block a server-made object in this area at all" -in Lioneye's Watch the same object
    /// blocks walkers. Its type is <c>blocking = true</c> in the .ot and its hull comes from the model's AO walk shapes.</summary>
    public static async Task<string> Box(IPlayer player)
    {
        var progress = State(player);
        const string type = "Metadata/MiscellaneousObjects/Stash";
        _types ??= Types();
        if (!_types.TryGetValue(type, out var shape))
            return $"no type {type} in the client's object_types table";
        if (!shape.Components.All(Supported.Contains))
            return $"{type} needs components no writer has: {string.Join(", ", shape.Components)}";
        var (px, py) = player.Position;
        // The type owns its model: writing a made-up path makes the client throw "-.ao is non-virtual" (2026-11-20).
        var row = new TownObject(type, "", px, py, 0f);
        uint id = progress.Id++;
        await Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(type), Blob(px, py, row, shape)),
            $"0x02bb create test stash 0x{id:x} at ({px}, {py})");
        return $"a stash object 0x{id:x} on your cell ({px}, {py})";
    }

    /// <summary>Dev: one object of any type the client has registered, carrying an arbitrary model, next to the player. The town's
    /// own <c>HubTown/Objects/*</c> types are not in the client's register (so a 0x2bb with one is refused); the types in the
    /// dat tables' eight object registers -chests, miscellaneous objects, NPCs -are, and their <c>Positioned</c> carries
    /// <c>blocking</c> plus a walk hull from the model's AO, which is what makes a server-made object stop a walker.</summary>
    public static async Task<string> Prop(IPlayer player, string type, string model, int dx, int dy)
    {
        var progress = State(player);
        _types ??= Types();
        if (!_types.TryGetValue(type, out var shape))
            return $"no type {type} in the client's object_types table";
        if (!shape.Components.All(Supported.Contains))
            return $"{type} needs components no writer has: {string.Join(", ", shape.Components)}";
        var (px, py) = player.Position;
        int x = px + dx, y = py + dy;
        var row = new TownObject(type, model, x, y, 0f);
        uint id = progress.Id++;
        await Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(type),
                Blob(x, y, row, shape, modelFromWire: true)),
            $"0x02bb create prop 0x{id:x} {type} model '{model}' at ({x}, {y})");
        return $"prop 0x{id:x} of {type} at ({x}, {y}) with model {model}";
    }

    /// <summary>Dev: server-side blockers over a rectangle, to see whether the client's walker honours one at all.</summary>
    public static string Block(IPlayer player, int x0, int y0, int x1, int y1)
    {
        var world = Program.WorldOf(player);
        int n = 0;
        for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
            for (int y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++)
            {
                world.SetBlocker(0x5B000000u + (uint)n, x, y, 4);
                n++;
            }
        return $"blocked {n} cell(s) from ({x0}, {y0}) to ({x1}, {y1})";
    }

    /// <summary>Dev: a small ASCII picture of the walk grid around the player, one line per row, '#' blocked, '.' open,
    /// 'P' the player. Answers "is the stamp where I think it is" without walking anywhere.</summary>
    public static string Cells(IPlayer player, int halfX, int halfY)
    {
        var layout = Program.PlayerOf(player).Layout;
        if (layout.Walkable is not { } walk) return "no walk grid in this area";
        var (px, py) = player.Position;
        halfX = Math.Clamp(halfX, 1, 60);
        halfY = Math.Clamp(halfY, 1, 30);
        var text = new System.Text.StringBuilder();
        for (int y = py + halfY; y >= py - halfY; y--)
        {
            for (int x = px - halfX; x <= px + halfX; x++)
                text.Append(x == px && y == py ? 'P' : walk(x, y) ? '.' : '#');
            text.Append(' ').Append(y).Append('\n');
        }
        return $"walk grid {px - halfX}..{px + halfX} x {py - halfY}..{py + halfY} (row ends carry y):\n{text}";
    }

    /// <summary>The furthest cell the walk grid allows along the straight line from (x0, y0) to (x1, y1), stepping one
    /// cell at a time. The town's walls are straight runs of cells, so clipping the line is enough to stop a click that
    /// lands on the far side of one: the player walks up to the wall instead of through it.</summary>
    internal static (int X, int Y) Clip(Func<int, int, bool> walk, int x0, int y0, int x1, int y1)
    {
        int dx = x1 - x0, dy = y1 - y0;
        int steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (steps == 0) return (x1, y1);
        int lastX = x0, lastY = y0;
        for (int i = 1; i <= steps; i++)
        {
            int x = x0 + (int)Math.Round(dx * (double)i / steps);
            int y = y0 + (int)Math.Round(dy * (double)i / steps);
            if (x == lastX && y == lastY) continue;
            if (!walk(x, y)) return (lastX, lastY);
            lastX = x;
            lastY = y;
        }
        return (x1, y1);
    }

    /// <summary>Where a click should send the player when the town's walls are in the way. Live 2026-11-21 clipping the
    /// straight line alone trapped the player: standing beside a wall or a barrel, the first cell of any click towards the
    /// open ground could be blocked, so the move was refused again and again and the character twitched in place while
    /// refusing to leave. So the straight line is only the first candidate -the line is also tried bent to either side,
    /// and the bent line that travels furthest wins, which walks the player along the obstacle instead of pinning them.
    /// Returns the player's own cell only when no direction moves at all; the caller lets such a click through untouched
    /// rather than answering it, so a corner can never trap anyone.</summary>
    internal static (int X, int Y) Slide(Func<int, int, bool> walk, int x0, int y0, int x1, int y1)
    {
        var best = Clip(walk, x0, y0, x1, y1);
        double bestLength = Distance(x0, y0, best);
        double full = Distance(x0, y0, (x1, y1));
        if (bestLength >= full) return best;
        double dx = x1 - x0, dy = y1 - y0;
        foreach (double degrees in new[] { 30.0, -30.0, 55.0, -55.0, 80.0, -80.0 })
        {
            double radians = degrees * Math.PI / 180.0;
            double cos = Math.Cos(radians), sin = Math.Sin(radians);
            int bx = x0 + (int)Math.Round(dx * cos - dy * sin);
            int by = y0 + (int)Math.Round(dx * sin + dy * cos);
            var bent = Clip(walk, x0, y0, bx, by);
            double length = Distance(x0, y0, bent);
            if (length > bestLength)
            {
                best = bent;
                bestLength = length;
            }
            if (bestLength >= full * 0.75) break;
        }
        return best;
    }

    static double Distance(int x0, int y0, (int X, int Y) cell)
        => Math.Sqrt((double)(cell.X - x0) * (cell.X - x0) + (double)(cell.Y - y0) * (cell.Y - y0));

    /// <summary>Whether the town's wall handling is on. The client's own walker ignores the server's walk grid -live
    /// 2026-11-20 a click walked straight through a wall the server had stamped as blocked -so the module clips such a
    /// move to the wall face instead. The old <c>data/kingsmarch-bounce.txt</c> dev switch is gone; the handling is
    /// always on, which is the state the dev file's presence selected.</summary>
    internal static bool BounceOn() => true;

    /// <summary>Whether the area is the town, so the bounce only ever applies here.</summary>
    internal static bool IsTown(string areaId) => string.Equals(areaId, Area, StringComparison.Ordinal);

    /// <summary>Loads the character's saved farm into this area load: its crop on each plot and what it has harvested,
    /// the workers (a Plot of MineJob/SmelterJob works the mine or the smeltery) and the running job cycles.</summary>
    static void Restore(Progress progress, string? character)
    {
        progress.Who = character ?? "";
        var farm = Farm(progress.Who);
        progress.Saved = farm.Plots;
        foreach (var (crop, count) in farm.Store) progress.Store[crop] = count;
        // The town ran while the character was away; CatchUp settles that gap against the treasury once the plots are
        // back, and ordinary per-tick billing starts from this load.
        progress.AwayFrom = farm.WageSettled > 0 ? farm.WageSettled : 0;
        progress.WageSettledAt = UtcNow();
        progress.Halted = false;
        progress.Workers.Clear();
        foreach (var saved in farm.Workers)
            progress.Workers.Add(new Worker
            {
                Order = progress.Workers.Count, Plot = saved.Plot, Harvests = saved.Harvests, Name = saved.Name,
                Rank = saved.Rank < 1 ? 1 : saved.Rank,
            });
    }

    /// <summary>Picks the farm back up after a module reload: the client still holds the town's objects, so the plots are
    /// rebuilt from the save under the ids they were created with and nothing is sent. A reload starts a fresh record, and
    /// without this the plots only came back when the character left the town and returned, which meant a rebuild during
    /// play left every plot unclickable and the panel saying the plots were not loaded.
    ///
    /// The one case it cannot be sure of is a reload inside the second a replanted plot waits for its new object: the save
    /// then names the id the client has just been told to destroy, so that plot is handled as part of the store until the
    /// next area load. No object is ever created here, which is what keeps the client's object map out of harm.</summary>
    static void Adopt(Progress progress)
    {
        if (progress.Placed || progress.Plots.Count > 0 || progress.Saved.Count == 0 || _types is null) return;
        // A save written before the ids were stored in it names no plot the client knows: there is nothing to pick up.
        if (progress.Saved.Any(p => p.Id == 0)) return;
        var rows = Ordered().Where(o => o.Type.EndsWith("/FarmingPlot", StringComparison.OrdinalIgnoreCase)).ToList();
        if (rows.Count == 0) return;
        double now = UtcNow();
        lock (_lock)
        {
            int order = 0;
            foreach (var keep in progress.Saved.Take(rows.Count))
            {
                var o = rows[order];
                if (!_types.TryGetValue(o.Type, out var type)) continue;
                int stateIndex = Array.IndexOf(type.Components, "StateMachine");
                if (stateIndex < 0) continue;
                progress.Plots[keep.Id] = new Plot
                {
                    Who = progress.Who, ObjectId = keep.Id, Order = order, StateIndex = (byte)stateIndex,
                    States = type.States, Row = o, Type = type,
                    Crop = keep.Crop, Carry = keep.Carry, Produced = keep.Produced,
                    // The gap since the last save is settled against the treasury the same way an area load does it, and
                    // CreatedAt stays 0: these objects are old, so the guard against touching a fresh one does not apply.
                    SettledAt = keep.Settled > 0 ? Math.Max(keep.Settled, now - AwayCap) : now,
                    CreatedAt = 0,
                };
                order++;
            }
            progress.Placed = progress.Plots.Count > 0;
        }
        if (progress.Placed)
            Console.WriteLine($"[gateway] kingsmarch: {progress.Plots.Count} plot(s) picked back up after a module reload");
    }

    /// <summary>The objects with the surely-written types first, so a low limit only exercises those.</summary>
    static List<TownObject> Ordered()
    {
        var sure = new List<TownObject>(TownLayout.Count);
        var rest = new List<TownObject>();
        foreach (var o in TownLayout)
        {
            if (_types is not null && _types.TryGetValue(o.Type, out var t) && t.Components.All(Sure.Contains)) sure.Add(o);
            else rest.Add(o);
        }
        sure.AddRange(rest);
        return sure;
    }

    static byte[] Blob(int x, int y, TownObject o, (string[] Components, string[] States) type, int? plant = null, int? workers = null,
        bool? modelFromWire = null)
    {
        // The model comes from the row (the client draws what the model is), except a dev prop that names one on the wire.
        bool typeModel = modelFromWire is { } send ? !send : false;
        var w = new BlobWriter();
        w.U8(0);      // object flags
        w.U8(0);      // base data entry count
        foreach (string c in type.Components)
        {
            switch (c)
            {
                case "Positioned":
                    w.I32(x).I32(y).F32(o.Angle).U16(0).U8(0).U8(0x02).U8(0).U8(1);
                    break;
                case "Animated":
                    // Writing the model path is what a variant doodad needs, but some town types own their model (their
                    // animated_object is the one the object already is): those get 0, "use the type's own".
                    if (typeModel) w.U8(0); else w.U8(1).WStr32(o.Model);
                    break;
                case "StateMachine":
                    w.U8((byte)type.States.Length).U8(0);
                    foreach (string s in type.States)
                    {
                        // A state sent with flags 0 is reverted by the client to the type's default; flags 0x02 keep what
                        // the server says. Only the states the server actually sets get that flag.
                        bool said = false;
                        int value = 0;
                        if (plant is { } crop && s == "plant") { value = crop; said = true; }
                        if (workers is { } hands && s == "num_workers") { value = hands; said = true; }
                        if (!said) value = s is "visible" or "active" or "can_talk" ? 1 : 0;
                        w.CompactI(value).U8((byte)(said ? 0x02 : 0));
                    }
                    break;
                case "TriggerableBlockage":
                    w.U8(0);
                    break;
                // Functions writes nothing: its read is `ret 0` (0x1400f1180, verified in the client). Writing a count there
                // shifts every component after it -InteractionAction, MinimapIcon -and the client quits.
                case "Functions":
                    break;
                case "Brackets":     // 0 bytes, same ret-family reader as Functions
                case "Render":       // client block only
                case "Preload":      // client block only
                case "Targetable":   // client block only
                    break;
                case "InteractionAction":
                case "MinimapIcon":
                    w.U8(0);
                    break;
                // BaseEvents: server-only, nothing on the wire.
            }
        }
        w.U8(0);      // tasks
        return w.ToArray();
    }

    /// <summary>The town's farm crops, by the plot's <c>plant</c> state value (1..5). The client's own crop list is the
    /// <c>VillageResources</c> rows 10..14 in the same order, which is what the farm panel's action names a crop by.</summary>
    public static readonly string[] Crops = ["Wheat", "Corn", "Pumpkin", "Globulora", "Orbisfera"];

    /// <summary>One crop as the farming formula needs it: the <c>BaseProductionTime</c> of its VillageProduction row in
    /// seconds (how long one unit takes) and the <c>ShippingValue</c> of its VillageExports row (what the port charges for
    /// it, and the value of one unit of it). All five crops are worth the same per worker-hour: 12/360 = 1/30, so the rate
    /// the formula computes is the same for every crop and only the two bonuses and the rank can move it (RE C侫.1).</summary>
    sealed record Crop(string Name, int BaseTimeSec, int ExportValue);
    static readonly Crop[] CropTable =
    [
        new("Wheat", 360, 12),
        new("Corn", 450, 15),
        new("Pumpkin", 540, 18),
        new("Globulora", 630, 21),
        new("Orbisfera", 720, 24),
    ];

    /// <summary>The town's farming attributes, by their Stats row, read from VillageBaseStats (the town's base attribute
    /// sheet - the upgrades it has not bought leave every row at its base value). At the town's base level the two yield
    /// bonuses and the speed bonus are all 0, so the rate is the plain time-based one.</summary>
    const int StatFarmingSpeed = 20698;         // village_farming_speed_+% (20699 is village_mapping_speed_+%; this one has no VillageBaseStats row, so it reads 0 at base)
    const int StatYieldPerDifferentCrop = 20735;  // village_farming_yield_+%_per_different_crop_farmed (20736 is the adjacent-plot one)
    const int StatYieldPerAdjacentPlot = 20736;   // village_farming_yield_+%_per_adjacent_plot_farmed (20737 is non_lethal_life_loss_%_per_minute)
    const int StatMaxFarmersPerPlot = 20720;   // village_max_farmers_per_plot (3 at base; 20721 is the crop-rank cap)
    const int StatCropRankMax = 20721;         // village_can_farm_crops_up_to_rank (5 at base; 20722 is village_number_of_ships)

    /// <summary>How many workers one plot takes (MAX_FARMERS_PER_PLOT, 3 at base level).</summary>
    static int MaxFarmersPerPlot =>
        FarmStat(StatMaxFarmersPerPlot) > 0 ? (int)FarmStat(StatMaxFarmersPerPlot) : 3;

    /// <summary>The six plots' neighbours (VillageFarmAdjacency, indexed by the plot's room order 0..5). Only the yield
    /// bonus reads them.</summary>
    static readonly int[][] Adjacency = [[1, 2], [0, 3], [0, 3, 4], [1, 2, 5], [2, 5], [3, 4]];

    /// <summary>How a worker's farming rank multiplies its contribution to the rate. The per-rank curve is a server-side
    /// coefficient the client never sees (RE C.1), so this is the linear stand-in -rank 1 = 1, rank 5 = 2 - clamped to the
    /// crop-rank cap the town's own stats allow (the <see cref="StatCropRankMax"/> row's VALUE, not its id: the old clamp
    /// capped ranks at 20721, the stat's row number, instead of the 5 the row holds).</summary>
    static double RankMult(int rank)
    {
        int at = Math.Clamp(rank, 1, Math.Max(1, (int)FarmStat(StatCropRankMax)));
        return at <= RankCurve.Length ? RankCurve[at - 1] : 1.0 + 0.25 * (at - 1);
    }

    /// <summary>The per-rank multipliers, sampled live on 2026-11-24 (a lone worker of each rank on its own wheat plot
    /// reported 10 / 15 / 17.5 / 20 an hour for ranks 1 / 3 / 4 / 5, so the curve reads as linear). Sample it again by
    /// putting one worker of a known rank alone on a plot and reading the rate <c>/village</c> reports: the rate is
    /// 3600 x rank_mult / BaseTimeSec, so rank_mult is rate x BaseTimeSec / 3600.</summary>
    static readonly double[] RankCurve = [1.0, 1.25, 1.5, 1.75, 2.0];

    /// <summary>What the town pays a worker every hour, by job type and rank (RE C.3). The panel's "Wage: n/h" line is the
    /// sum of the wage field of the worker rows the server sends, so the treasury has to charge the same numbers or the
    /// town's "runs dry in" line would lie; the rows are built by <c>Modules/Core/Village/VillageWages.cs</c>, which this
    /// module cannot reference (they are separate assemblies), hence the copy. Idling and fishing cost nothing; the other
    /// jobs are rank 1 to 5.</summary>
    static readonly ushort[][] WageTable =
    [
        [0, 0, 0, 0, 0],                       // idling
        [80, 120, 160, 220, 280],              // mining
        [80, 120, 160, 220, 280],              // smelting
        [190, 285, 391, 517, 665],             // farming
        [170, 255, 349, 463, 595],             // disenchanting
        [375, 560, 750, 1100, 1875],           // mapping
        [110, 165, 225, 290, 300],             // shipping
        [0, 0, 0, 0, 0],                       // fishing
    ];

    /// <summary>The job type a worker's place in town stands for (a VillageJobTypes row).</summary>
    static byte JobTypeOf(Worker worker) => worker.Plot switch
    {
        MineJob => 1,
        SmelterJob => 2,
        >= 0 => 3,
        >= ShipJob - 2 and <= ShipJob => 6,
        _ => 0,
    };

    /// <summary>What a single worker costs an hour, or 0 when it is not working.</summary>
    static int WageOf(Worker worker)
    {
        byte job = JobTypeOf(worker);
        if (job == 0 || job >= WageTable.Length) return 0;
        var row = WageTable[job];
        return row[Math.Clamp(worker.Rank - 1, 0, row.Length - 1)];
    }

    /// <summary>The town's whole wage bill for an hour: only the workers that hold a job are paid (RE C.3).</summary>
    static int WagesPerHour(Progress progress) => progress.Workers.Sum(WageOf);

    /// <summary>The mine's ores and the bar each refines into, in VillageResources order (the ore row's
    /// <c>Result</c> in VillageExports names the same pairing). The real mine digs the ore its level reaches
    /// (VillageProduction Mining1..5, one ore a level); with the town's buildings still at their base level the
    /// mine follows the farm's rotation instead, so one character can dig and ship all five.</summary>
    static readonly string[] Ores = ["IronOre", "CopperOre", "Amber", "BismuthOre", "VerisiumOre"];
    static readonly string[] Bars = ["RefinedIron", "RefinedCopper", "RefinedAmber", "RefinedBismuth", "RefinedVerisium"];

    /// <summary>One worker-cycle of the mine or the smeltery (VillageProduction Mining1/Smelting1
    /// <c>BaseProductionTime</c>; the deeper tiers are 145/180/280/480 and arrive with the building upgrades).</summary>
    const double MineSeconds = 120;
    const double SmeltSeconds = 120;
    // Seconds a plot is left alone after the client was sent it: its destroy is deferred, so removing a fresh plot again is
    // what breaks the client's object map.
    const double Quiet = 6;
    // The two town jobs a worker can hold beyond a farm plot, stored in the worker's Plot field (the saved farm keeps a
    // plot order there; these never collide with one). The mine and the smeltery have no client-side state to re-state,
    // so unlike a plot an assignment to them is only the saved worker's field. The village panel's mirror maps them
    // back to the panel's VillageJobs rows, so they are public here.
    public const int MineJob = -2;
    public const int SmelterJob = -3;
    // The harbour's three ships, stored the same way: ShipJob minus the ship's own place (0, 1, 2 -the cards the port
    // window lists as 大海勇氣, KTS 東方之星 and 卡迪根之怒). The client's crew panel opens from a card's "+" badge and
    // sends the VillageJobs row that belongs to that card (21, 22, 23), so a worker placed on a ship has to land in a
    // slot this file can name or the very next tick rewrites the worker back to idle and the crew is thrown off again.
    public const int ShipJob = -4;

    /// <summary>Whether a worker's slot is one of the harbour's ships.</summary>
    static bool OnShip(int plot) => plot is >= ShipJob - 2 and <= ShipJob;

    /// <summary>Which ship a slot stands for, counting from zero, or -1 when the slot is not a ship.</summary>
    static int ShipOf(int plot) => OnShip(plot) ? ShipJob - plot : -1;

    /// <summary>Whether a worker holds any job at all (a plot, the mine, the smeltery or a ship), as against idling.
    /// Every place that asks "who is idle" has to ask this instead of <c>Plot &lt; 0</c>, or a ship's crew is offered
    /// as available and can be placed a second time.</summary>
    static bool Working(Worker worker) => worker.Plot >= 0 || worker.Plot == MineJob || worker.Plot == SmelterJob
        || OnShip(worker.Plot);

    /// <summary>The farm's live state as the village panel mirrors it: the workers (their slot and rank), the store
    /// bag and the plots' crops. The farm stays the single engine that owns these numbers; the panel's settlement
    /// copies them in (<see cref="VillageModule.MirrorFarm"/>) so the two can never disagree about a file again.</summary>
    public sealed record TownView(IReadOnlyList<(int Plot, int Rank, string Name)> Workers,
        IReadOnlyDictionary<string, int> Store, IReadOnlyList<(int Order, int Crop)> Plots);

    /// <summary>The connection's farm state as a read-only view, taken under the town's own lock. Null before the
    /// town exists for this connection (no area load yet), which the mirror treats as "nothing to copy".</summary>
    public static TownView? View(IPlayer player)
    {
        Progress progress;
        lock (_lock)
        {
            progress = State(player);
            if (progress.Workers.Count == 0 && progress.Store.Count == 0 && progress.Plots.Count == 0) return null;
            return new TownView(
                [.. progress.Workers.Select(w => (w.Plot, (int)w.Rank, w.Name))],
                new Dictionary<string, int>(progress.Store),
                [.. progress.Plots.Values.Select(p => (p.Order, p.Crop))]);
        }
    }

    /// <summary>Unix epoch seconds, the clock the town's continuous production settles on. The town has to settle the
    /// time a character was away, which a process-local tick counter cannot do.</summary>
    static double UtcNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>How much of a character's absence the town settles at once. The town runs on its own while its owner is
    /// away, but a month of crops in one load is not what a town of this size holds; a day is the stand-in until the
    /// village's own storage cap is known.</summary>
    const double AwayCap = 24 * 3600;

    // The town's base attribute sheet, from VillageBaseStats in client.db (row.Stat -> row.StatValue), read once and kept.
    static Dictionary<int, double>? _baseStats;

    /// <summary>The town's farming attributes as the formula reads them (RE C.1/C.2): the VillageBaseStats row the stat
    /// id names, or 0 when the table has no row for it (the farming-speed stat has none, so it reads 0 at base level).</summary>
    static double FarmStat(int id)
    {
        if (_baseStats is null)
        {
            var all = new Dictionary<int, double>();
            try
            {
                foreach (var row in ClientTables.TableOrEmpty("VillageBaseStats"))
                    if (row.IntOrNull("Stat") is { } stat && row.RealOrNull("StatValue") is { } statValue)
                        all[stat] = statValue;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[gateway] kingsmarch: VillageBaseStats is unreadable, the town reads its base stats as 0: {e.Message}");
            }
            _baseStats = all;
        }
        return _baseStats.TryGetValue(id, out double value) ? value : 0;
    }

    /// <summary>A click on a farm plot (C->S 0x24, skill 0x266): plants a crop on a bare plot, harvests a ripe one and says
    /// what happened. The crop is the plot object's own <c>plant</c> StateMachine state (the client swaps its model to
    /// CropsWheat01/Corn/Pumpkins/Globulora/Orbisfera for 1..5), sent as a 0x2bc component update. False = not ours.</summary>
    public static async Task<bool> Use(IPlayer player, uint objectId)
    {
        if (!string.Equals(player.AreaId, Area, StringComparison.Ordinal)) return false;
        Plot? plot;
        Progress? progress;
        BoardAction? board = null;
        lock (_lock)
        {
            _progress.TryGetValue(player, out progress);
            if (progress is not null && progress.Boards.TryGetValue(objectId, out var found)) board = found.Action;
        }
        if (board is { } pushed) return await Pushed(player, pushed);
        lock (_lock)
        {
            _progress.TryGetValue(player, out progress);
            if (progress is null || !progress.Plots.TryGetValue(objectId, out plot)) return false;
        }
        double now = UtcNow();
        // A plot the client has only just been sent is left alone for a while: its 0x2bd destroy is deferred, and removing
        // a fresh plot again right away is what corrupted the client's object map.
        if (now - plot.CreatedAt < Quiet || plot.Replanting)
        {
            await player.Tell($"Plot {plot.Order + 1} is still settling ({plot.CreatedAt + Quiet - now:0} s).");
            return true;
        }
        if (plot.Crop == 0)
        {
            // The farm panel (client screen 0x59) cannot be opened in this build, so a click chooses the crop instead: the
            // plots take turns at the five crops, so one character can grow all of them. There is no planting cost and no
            // growing time to wait for -what a crop produces is a rate from that moment on (RE 0.1).
            int crop;
            lock (_lock) crop = 1 + progress.CropNext++ % Crops.Length;
            plot.Crop = crop;
            plot.SettledAt = now;
            await SetPlant(player, progress, objectId, plot);
            await player.Tell(progress.Workers.Any(w => w.Plot == plot.Order)
                ? $"Plot {plot.Order + 1}: now growing {Crops[crop - 1]} ({CropTable[crop - 1].ExportValue} a unit at the port)."
                : $"Plot {plot.Order + 1}: now growing {Crops[crop - 1]}, but no worker tends it. /village assign <worker> {plot.Order + 1}");
            Persist(progress);
            return true;
        }
        // A growing plot has nothing to harvest: a click reads it out instead.
        Settle(progress, plot, now);
        int hands = progress.Workers.Count(w => w.Plot == plot.Order);
        double rate = PlotRate(progress, plot);
        await player.Tell(hands == 0
            ? $"Plot {plot.Order + 1}: {Crops[plot.Crop - 1]}, {InStore(progress, Crops[plot.Crop - 1])} of it in the town -nobody is tending it."
            : progress.Halted
                ? $"Plot {plot.Order + 1}: {Crops[plot.Crop - 1]} -the town has halted, the treasury is empty (fund it with Johan)."
                : $"Plot {plot.Order + 1}: {Crops[plot.Crop - 1]}, {rate * 3600:0.#} an hour from {hands} worker(s), {plot.Produced} grown so far, {InStore(progress, Crops[plot.Crop - 1])} in the town.");
        return true;
    }

    /// <summary>Puts a named crop on a plot, replacing whatever it was growing. The farm panel that would let a player pick
    /// one cannot be opened in this build, so this is the planting half of the farm: a click on a bare plot takes the next
    /// crop in turn, and this picks a particular one. Crops cost nothing and start producing at their rate immediately
    /// (RE 0.1), so changing one only loses the fraction it had accumulated - what is already grown stays in the town.</summary>
    /// <param name="plotOrder">The plot, 1..6, as the report and the panel number them.</param>
    /// <param name="crop">The crop's name, any case ("wheat", "corn", "pumpkin", "globulora", "orbisfera").</param>
    public static async Task<string> Choose(IPlayer player, int plotOrder, string crop)
    {
        int chosen = -1;
        for (int i = 0; i < Crops.Length; i++)
            if (string.Equals(Crops[i], crop, StringComparison.OrdinalIgnoreCase)) chosen = i + 1;
        if (chosen < 0) return $"no crop \"{crop}\": the town grows {string.Join(", ", Crops)}";
        var progress = State(player);
        Plot? plot;
        lock (_lock) plot = progress.Plots.Values.FirstOrDefault(p => p.Order == plotOrder - 1);
        if (plot is null)
            return progress.Plots.Count == 0
                ? $"the town's plots are not loaded yet: walk into Kingsmarch (/goto {Area}) and try again"
                : $"no plot {plotOrder}: the town has {progress.Plots.Count} (1-{progress.Plots.Count})";
        double now = UtcNow();
        if (now - plot.CreatedAt < Quiet || plot.Replanting)
            return $"plot {plotOrder} is still settling ({plot.CreatedAt + Quiet - now:0} s)";
        // A replant leaves the plot in the record, so it can only be touched once its new object exists.
        if (plot.Crop == chosen)
            return $"plot {plotOrder} is already growing {Crops[chosen - 1]}";
        // What it has grown so far belongs to the town before the crop is swapped out.
        Settle(progress, plot, now);
        // Saved before the crop changes and the object is destroyed: from here until the new object exists, the plot holds
        // an id the client no longer knows, so the save must still describe the plot as it stands right now. A reload in
        // that second then comes back to this plot under its live id with its old crop, which only loses the one change.
        Persist(progress);
        uint id = plot.ObjectId;
        bool bare = plot.Crop == 0;
        plot.Crop = chosen;
        plot.SettledAt = now;
        await SetPlant(player, progress, id, plot);
        bool someone = progress.Workers.Any(w => w.Plot == plot.Order);
        return $"plot {plotOrder}: {(bare ? "now growing" : "switched to")} {Crops[chosen - 1]} "
            + $"({CropTable[chosen - 1].ExportValue} a unit at the port)"
            + (someone ? ", a worker is on it." : $", but nobody tends it (/village assign <worker> {plotOrder}).");
    }

    /// <summary>Stops a plot growing anything, the farm panel's "none" choice in its crop list: what it has grown so far goes
    /// into the town's store and the plot is left bare, ready for another crop.</summary>
    /// <param name="plotOrder">The plot, 1..6, as the report and the panel number them.</param>
    public static async Task<string> Fallow(IPlayer player, int plotOrder)
    {
        var progress = State(player);
        Plot? plot;
        lock (_lock) plot = progress.Plots.Values.FirstOrDefault(p => p.Order == plotOrder - 1);
        if (plot is null)
            return progress.Plots.Count == 0
                ? $"the town's plots are not loaded yet: walk into Kingsmarch (/goto {Area}) and try again"
                : $"no plot {plotOrder}: the town has {progress.Plots.Count} (1-{progress.Plots.Count})";
        if (plot.Crop == 0) return $"plot {plotOrder} is already bare";
        double now = UtcNow();
        if (now - plot.CreatedAt < Quiet || plot.Replanting)
            return $"plot {plotOrder} is still settling ({plot.CreatedAt + Quiet - now:0} s)";
        // Nothing is taken from a plot while it settles, so the last fraction of a crop is banked first.
        Settle(progress, plot, now);
        // As in Choose: the save stands at the plot's live id until the new object exists.
        Persist(progress);
        uint id = plot.ObjectId;
        string was = Crops[plot.Crop - 1];
        plot.Crop = 0;
        plot.SettledAt = now;
        await SetPlant(player, progress, id, plot);
        return $"plot {plotOrder}: {was} taken off, the plot is bare.";
    }

    /// <summary>How fast a plot turns time into crops, in crops a second (RE C.1/C.2):
    /// <c>(1/T) * 鍗?rank_mult(worker) * (1 + speed_bonus) * (1 + yield_bonus)</c>, where the yield bonus counts the crops
    /// the town is growing and the neighbours of this plot that are growing. 0 while the plot is bare, untended or the
    /// town has halted.</summary>
    static double PlotRate(Progress progress, Plot plot)
    {
        if (plot.Crop == 0 || progress.Halted) return 0;
        double rank = 0;
        foreach (var worker in progress.Workers) if (worker.Plot == plot.Order) rank += RankMult(worker.Rank);
        if (rank <= 0) return 0;
        double speed = FarmStat(StatFarmingSpeed) / 100.0;
        int distinct = progress.Plots.Values.Where(p => p.Crop != 0).Select(p => p.Crop).Distinct().Count();
        int adjacent = Adjacency[plot.Order].Count(o => progress.Plots.Values.Any(p => p.Order == o && p.Crop != 0));
        double bonus = (FarmStat(StatYieldPerDifferentCrop) * distinct + FarmStat(StatYieldPerAdjacentPlot) * adjacent) / 100.0;
        return rank * (1.0 + speed) * (1.0 + bonus) / CropTable[plot.Crop - 1].BaseTimeSec;
    }

    /// <summary>Settles one plot up to <paramref name="now"/>: the fraction it has grown stays on the plot, the whole crops
    /// go into the town's store. Called from the tick for every plot, so a plot nobody tends simply keeps its timestamp up
    /// to date instead of banking free time.</summary>
    static void Settle(Progress progress, Plot plot, double now)
    {
        double dt = now - plot.SettledAt;
        plot.SettledAt = now;
        if (dt <= 0 || plot.Crop == 0) return;
        double gain = PlotRate(progress, plot) * dt;
        if (gain <= 0) return;
        plot.Carry += gain;
        int whole = (int)Math.Floor(plot.Carry);
        if (whole <= 0) return;
        plot.Carry -= whole;
        plot.Produced += whole;
        string crop = Crops[plot.Crop - 1];
        lock (progress.Store) progress.Store[crop] = progress.Store.GetValueOrDefault(crop) + whole;
    }

    /// <summary>Writes this connection's farm into the character's town save: its plots' crops and what it has produced.</summary>
    static void Persist(Progress progress)
    {
        var farm = Farm(progress.Who);
        lock (_lock)
        {
            // A session that never placed the plots (a module reload while the character stands in the town) has an empty
            // plot list, and writing that would erase the crops the character had growing, so only a placed town saves them.
            if (progress.Placed)
            {
                farm.Plots = [.. progress.Plots.Values.OrderBy(p => p.Order)
                    .Select(p => new SavedPlot { Id = p.ObjectId, Crop = p.Crop, Carry = p.Carry, Settled = p.SettledAt, Produced = p.Produced })];
                progress.Saved = farm.Plots;
            }
            farm.Workers = [.. progress.Workers.Select(w => new SavedWorker { Plot = w.Plot, Harvests = w.Harvests, Name = w.Name, Rank = w.Rank })];
            farm.WageSettled = progress.WageSettledAt;
        }
        lock (progress.Store) farm.Store = new(progress.Store);
        SaveFarm(progress.Who);
    }

    static int InStore(Progress progress, string crop)
    {
        lock (progress.Store) return progress.Store.GetValueOrDefault(crop);
    }

    /// <summary>How much of a crop the town is holding -what the port needs to know before it loads a ship.</summary>
    public static int Stored(IPlayer player, string crop)
    {
        Progress progress;
        progress = State(player);
        return InStore(progress, crop);
    }

    /// <summary>Takes up to <paramref name="count"/> of a crop out of the town's store and says how many it could take.</summary>
    public static int TakeFromStore(IPlayer player, string crop, int count)
    {
        Progress progress;
        progress = State(player);
        int taken;
        lock (progress.Store)
        {
            int have = progress.Store.GetValueOrDefault(crop);
            taken = Math.Min(have, Math.Max(0, count));
            if (taken <= 0) return 0;
            if (have == taken) progress.Store.Remove(crop);
            else progress.Store[crop] = have - taken;
        }
        Persist(progress);
        return taken;
    }

    /// <summary>Puts produce back in the town's store, the way the port does when a loaded ship is unloaded again. Returns
    /// how many units the store took.</summary>
    public static int AddToStore(IPlayer player, string crop, int count)
    {
        if (count <= 0) return 0;
        var progress = State(player);
        lock (progress.Store) progress.Store[crop] = progress.Store.GetValueOrDefault(crop) + count;
        Persist(progress);
        return count;
    }

    /// <summary>The town's farm as one report: each plot's crop and rate, who tends it, and what is stored.</summary>
    public static string StoreText(IPlayer player)
    {
        Progress progress;
        progress = State(player);
        double clock = UtcNow();
        var lines = new List<string>();
        if (progress.Halted) lines.Add("HALTED: the treasury is empty, nothing is being produced");
        foreach (var plot in progress.Plots.Values.OrderBy(p => p.Order))
        {
            string what = plot.Crop == 0 ? "bare, click it to choose a crop" : Crops[plot.Crop - 1];
            double rate = PlotRate(progress, plot);
            string who = string.Join(",", progress.Workers.Where(w => w.Plot == plot.Order).Select(w => w.Order + 1));
            lines.Add($"plot {plot.Order + 1}: {what}"
                + (plot.Crop == 0 ? "" : $", {rate * 3600:0.#}/h, {plot.Produced} grown")
                + (who.Length > 0 ? $" (worker {who})" : " (no one)"));
        }
        var idle = progress.Workers.Where(w => !Working(w))
            .Select(w => w.Order + 1).ToList();
        lines.Add(idle.Count == 0 ? "workers: none idle" : $"workers: idle {string.Join(",", idle)}");
        var crews = Enumerable.Range(0, 3)
            .Select(ship => progress.Workers.Where(w => w.Plot == ShipJob - ship).Select(w => w.Order + 1).ToList())
            .ToList();
        lines.Add("ships: " + string.Join(", ", crews.Select((crew, ship) =>
            $"ship {ship + 1} " + (crew.Count == 0 ? "no one" : $"worker {string.Join(",", crew)}"))));
        var onMine = progress.Workers.Where(w => w.Plot == MineJob).Select(w => w.Order + 1).ToList();
        lines.Add("mine: " + (onMine.Count == 0 ? "no one" : $"worker {string.Join(",", onMine)}"));
        var atSmelter = progress.Workers.Where(w => w.Plot == SmelterJob).Select(w => w.Order + 1).ToList();
        lines.Add("smeltery: " + (atSmelter.Count == 0 ? "no one" : $"worker {string.Join(",", atSmelter)}"));
        var store = progress.Store.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}").ToList();
        lines.Add("stored: " + (store.Count == 0 ? "nothing yet" : string.Join(", ", store)));
        int worked = progress.Workers.Count(Working);
        lines.Add($"treasury: {KingsmarchPort.Gold(progress.Who)} gold, {WagesPerHour(progress)} an hour in wages"
            + (progress.WageDebt >= 1 ? $", {progress.WageDebt:0.#} due" : ""));
        lines.Add("commands: /recruit ; /village assign <worker> <plot 1-6 | mine | smelter | ship 1-3> ; /treasury [gold]");
        return string.Join(" | ", lines);
    }

    /// <summary>The town's treasury: the gold it holds, what its wages cost an hour, and whether it has halted.</summary>
    public static string TreasuryText(IPlayer player)
    {
        var progress = State(player);
        return $"{KingsmarchPort.Gold(progress.Who)} gold in the town, {WagesPerHour(progress)} an hour in wages"
            + (progress.Halted ? ", the town is halted (the treasury is empty)" : ", no wages owed")
            + " | /treasury <gold> puts more in (Johan's Fund the Treasury adds a thousand at a time)";
    }

    /// <summary>Johan's "Fund the Treasury": gold moves from the account's stash into the town's treasury, and the wages the
    /// town still owed are settled with it first. The S->C 0x2aa that carries the new total is what the client shows.</summary>
    public static async Task<string> FundTreasury(IPlayer player, int amount)
    {
        var progress = State(player);
        var stash = Game.PersonalStashOf(Program.PlayerOf(player));
        int take = (int)Math.Min(Math.Max(amount, 0), stash.Gold);
        if (take <= 0) return "the account has no gold to put into the treasury";
        uint? left = stash.TrySpendGold(take);
        if (left is null) return $"the account has {stash.Gold} gold, less than the {take} asked for";
        long treasury = KingsmarchPort.Fund(progress.Who, take);
        await player.Send(InstanceEntities.Gold(left.Value), $"0x02aa gold: {left} (funded the Kingsmarch treasury with {take})");
        // Funding is also what puts a halted town back to work, which the next tick notices and reports.
        return $"Funded the treasury with {take} gold: {treasury} gold in the town"
            + (progress.Halted ? ", which restarts the town's jobs." : ", no wages owed.");
    }

    /// <summary>The recruiter's pool: the Kalguur names Raulf offers, and what one hire costs in barter. The harbour and
    /// the farm share one purse, so a hire is paid for by shipping (KingsmarchPort) rather than with a second currency.</summary>
    static readonly string[] WorkerNames =
        ["Ainar", "Brynja", "Dala", "Eirik", "Fjola", "Gorm", "Haki", "Ivar", "Jorunn", "Kalf",
         "Leif", "Mira", "Njal", "Olaf", "Runa", "Sigrun", "Torfi", "Ulf", "Viggo", "Yrsa"];
    public const int RecruitCost = 40;

    /// <summary>One of the three names Raulf would offer: the same three until one is hired, so re-reading the list and
    /// then hiring the one that was read agree with each other.</summary>
    static string CandidateName(string who, int index)
    {
        int seed = index;
        foreach (char c in who) seed = (seed * 31 + c) & 0x3FFFFFFF;
        return WorkerNames[(seed + index * 7) % WorkerNames.Length];
    }

    /// <summary>The recruiter's list: three candidates, the harbour's barter and the price of a hire.</summary>
    public static string RecruitText(IPlayer player)
    {
        var progress = State(player);
        int hired;
        lock (_lock) hired = progress.Workers.Count;
        var names = Enumerable.Range(1, 3).Select(i => $"{i}) {CandidateName(progress.Who, hired + i)}");
        return $"{string.Join(", ", names)} | {KingsmarchPort.Barter(progress.Who)} barter in the harbour, "
            + $"{RecruitCost} a hire | /recruit hire <1-3>, then /village assign <worker> <plot 1-6 | mine | smelter>";
    }

    /// <summary>Hires one of the three candidates for <see cref="RecruitCost"/> barter.</summary>
    public static Task<string> Recruit(IPlayer player, int index)
    {
        var progress = State(player);
        if (index < 1 || index > 3) return Task.FromResult("usage: /recruit hire <1-3>");
        int number;
        string name;
        lock (_lock)
        {
            name = CandidateName(progress.Who, progress.Workers.Count + index);
            if (!KingsmarchPort.PayBarter(progress.Who, RecruitCost))
                return Task.FromResult($"a hire costs {RecruitCost} barter and the harbour has {KingsmarchPort.Barter(progress.Who)} "
                    + "(ship crops with /port load <slot> <crop> <count>, then /port sail <slot>)");
            progress.Workers.Add(new Worker { Order = progress.Workers.Count, Name = name });
            number = progress.Workers.Count;
            Persist(progress);
        }
        return Task.FromResult($"Hired {name} (worker {number}, {RecruitCost} barter paid): "
            + $"/village assign {number} <plot 1-6> puts it on a plot.");
    }

    /// <summary>Hires without a name for the candidate list ("/village hire"): the first of the three, paid the same.</summary>
    public static Task<string> Hire(IPlayer player) => Recruit(player, 1);

    /// <summary>Puts a worker on a plot: the plot is re-stated with its <c>num_workers</c> count and the worker tends it from
    /// then on -planting an empty plot and harvesting a ripe one while the player is elsewhere.</summary>
    public static async Task<string> Assign(IPlayer player, int worker, int plotOrder)
    {
        Progress progress;
        progress = State(player);
        Plot? plot;
        lock (_lock)
        {
            if (worker < 1 || worker > progress.Workers.Count) return $"no worker {worker}: the town has {progress.Workers.Count} (hire with /village hire)";
            plot = progress.Plots.Values.FirstOrDefault(p => p.Order == plotOrder - 1);
            // A module reload starts a fresh progress record, so the plots are only in memory once the town has been
            // loaded again. Saying so beats throwing: an exception in a chat handler drops the player's connection.
            if (plot is null)
                return progress.Plots.Count == 0
                    ? $"the town's plots are not loaded yet: walk into Kingsmarch (/goto {Area}) and try again"
                    : $"no plot {plotOrder}: the town has {progress.Plots.Count} (1-{progress.Plots.Count})";
            // A plot takes up to MAX_FARMERS_PER_PLOT (3) workers, and each one adds its own rate to the plot (RE C.1).
            int onPlot = progress.Workers.Count(w => w.Plot == plotOrder - 1 && progress.Workers.IndexOf(w) != worker - 1);
            if (onPlot >= MaxFarmersPerPlot)
                return $"plot {plotOrder} already has {onPlot} of its {MaxFarmersPerPlot} farmers";
            progress.Workers[worker - 1].Plot = plotOrder - 1;
            Persist(progress);
        }
        await SetWorkers(player, plot, progress.Workers.Count(w => w.Plot == plotOrder - 1));
        return $"Worker {worker} now tends plot {plotOrder}.";
    }

    /// <summary>The gold the town's treasury holds, the number the panels show.</summary>
    /// <param name="player">The player whose town it is.</param>
    public static long Treasury(IPlayer player) => KingsmarchPort.Gold(State(player).Who);

    /// <summary>Takes gold out of the town's treasury the way the town's own purchases do (a recruit's price, a
    /// shipment). Returns false and leaves the treasury alone when it holds too little.</summary>
    /// <param name="player">The player whose town it is.</param>
    /// <param name="cost">The gold to take.</param>
    public static bool PayTreasury(IPlayer player, int cost) => KingsmarchPort.Spend(State(player).Who, cost);

    /// <summary>Pays one of the town's purchases (a recruit's price). The treasury is the town's own purse, so it pays
    /// first; when it is short the rest comes out of the player's gold, the same gold they would have carried over with
    /// the King's Hand's "Fund the Treasury" - a hire the panel offered should not silently do nothing just because the
    /// town's purse happens to be empty.</summary>
    /// <param name="player">The player whose town it is.</param>
    /// <param name="cost">The gold the purchase costs.</param>
    /// <returns>True when the gold was taken, and a line saying which purse paid it.</returns>
    public static async Task<(bool Paid, string From)> PayTownPurchase(IPlayer player, int cost)
    {
        if (cost <= 0) return (true, "for nothing");
        var progress = State(player);
        long treasury = Treasury(player);
        int fromTreasury = (int)Math.Min(cost, treasury);
        int fromStash = cost - fromTreasury;
        var stash = Game.PersonalStashOf(Program.PlayerOf(player));
        // The player's purse is checked BEFORE the treasury is touched: the old order spent the treasury first and then
        // returned without refunding it when the purse could not cover the rest - a refused purchase emptied the town's
        // gold just the same.
        if (stash.Gold < (uint)fromStash)
            return (false, $"{cost} gold needed: the treasury holds {treasury} and your purse {stash.Gold}");
        if (fromTreasury > 0 && !PayTreasury(player, fromTreasury))
        {
            // The treasury was drained between the check and the spend: the whole purchase falls back on the purse.
            fromTreasury = 0;
            fromStash = cost;
            if (stash.Gold < (uint)fromStash)
                return (false, $"{cost} gold needed: the treasury holds {KingsmarchPort.Gold(progress.Who)} and your purse {stash.Gold}");
        }
        uint? left = stash.TrySpendGold(fromStash);
        if (left is null)
        {
            // The purse was drained between the check and the spend: put the treasury's share back before refusing.
            if (fromTreasury > 0) KingsmarchPort.Fund(progress.Who, fromTreasury);
            return (false, $"{cost} gold needed: the treasury holds {Treasury(player)}");
        }
        await player.Send(InstanceEntities.Gold(left.Value),
            $"0x02aa gold: {left} (paid {fromStash} of a {cost} Kingsmarch purchase out of the player's purse)");
        return (true, fromTreasury == 0
            ? $"{cost} gold out of your own purse ({left} left)"
            : $"{cost} gold ({fromTreasury} from the treasury, {fromStash} from your purse, {left} left)");
    }

    /// <summary>Hires one more worker for the town, the way the recruitment panel's hire button does: the worker joins
    /// the farm's own list, which is what the panel is rewritten from. Returns the worker's number, or 0 when the town is
    /// already at its limit.</summary>
    /// <param name="player">The player whose farm it is.</param>
    /// <param name="skill">The recruit's skill byte: the VillageJobSkillLevels JobLevel the client looks the rank up by,
    /// 1 to 5 (0 = no rank, hired as the first rank).</param>
    public static int AddWorker(IPlayer player, byte skill = 0)
    {
        Progress progress = State(player);
        int number;
        lock (_lock)
        {
            if (progress.Workers.Count >= 90) return 0;
            number = progress.Workers.Count + 1;
            progress.Workers.Add(new Worker
            {
                Order = progress.Workers.Count,
                Name = CandidateName(progress.Who, progress.Workers.Count),
                // The skill byte IS the rank (JobLevel is 1-based, 1..5): the recruit rolls rolled 1..5 and the panel's
                // worker rows carry the same byte, so no offset goes on it here (the old +1 turned every rank-1 hire
                // into a rank-2 worker and pushed a rank-5 hire to the cap).
                Rank = Math.Clamp((int)skill, 1, 5),
            });
            Persist(progress);
        }
        return number;
    }

    /// <summary>Moves one worker the way the town panel's own buttons do: a <c>VillageJobs</c> row names the job, 11..16
    /// being the six farm plots and 0 idling. The panel's worker list is rewritten from this state every tick, so a button
    /// the panel sends has to land here as well, or the next tick puts the worker straight back where it was.</summary>
    /// <param name="player">The player whose farm it is.</param>
    /// <param name="worker">The worker's number in the town, counting from one.</param>
    /// <param name="jobRow">The <c>VillageJobs</c> row the panel's button named.</param>
    public static async Task MoveTo(IPlayer player, int worker, int jobRow)
    {
        Progress progress = State(player);
        // The panel names a job by its VillageJobs row: 1 to 5 are the mine's veins, 6 to 10 the smeltery, 11 to 16 the
        // farm's six plots and 21 to 23 the harbour's three ships. Without the mine, smeltery and ship rows a recruited
        // miner or a ship's crew would arrive idle and the panel would rewrite the job back to "idle" on the next tick.
        int plot = jobRow switch
        {
            >= 1 and <= 5 => MineJob,
            >= 6 and <= 10 => SmelterJob,
            >= 11 and <= 16 => jobRow - 11,
            >= 21 and <= 23 => ShipJob - (jobRow - 21),
            _ => -1,
        };
        lock (_lock)
        {
            if (progress.Workers.Count == 0) return;
            worker = Math.Clamp(worker, 1, progress.Workers.Count);
            progress.Workers[worker - 1].Plot = plot;
            Persist(progress);
        }
        foreach (var row in progress.Plots.Values.Where(p => p.Order == plot || progress.Workers.Any(w => w.Plot == p.Order)))
            await SetWorkers(player, row, progress.Workers.Count(w => w.Plot == row.Order));
    }

    /// <summary>Puts an idle worker on a plot the way the farm panel's assign button does: the button names the worker's
    /// place among the idle ones, not its place in the town, so the index is resolved here first. Removing workers renumbers
    /// that list, which is why one worker could never be placed again after the others were removed.</summary>
    /// <param name="player">The player whose farm it is.</param>
    /// <param name="index">The idle worker's place in the available list, counting from zero.</param>
    /// <param name="plotOrder">The plot's number, counting from one.</param>
    public static Task<string> AssignIdle(IPlayer player, int index, int plotOrder)
    {
        Progress progress = State(player);
        int[] idle = [.. progress.Workers.Select((w, i) => (Worker: w, Place: i + 1)).Where(pair => !Working(pair.Worker)).Select(pair => pair.Place)];
        if (idle.Length == 0) return Task.FromResult("every worker already has a job");
        int worker = index >= 0 && index < idle.Length ? idle[index] : idle[^1];
        return Assign(player, worker, plotOrder);
    }
    /// <summary>Takes a worker out of the town for good, as the panel's release button does. The panel keeps its own list,
    /// but the farm is what the panel is re-written from, so the worker has to leave here too -otherwise the two lists
    /// drift apart by one and the last worker can never be placed again.</summary>
    /// <param name="player">The player whose farm it is.</param>
    /// <param name="worker">The worker's number in the town, counting from one.</param>
    public static void Fire(IPlayer player, int worker)
    {
        Progress progress = State(player);
        lock (_lock)
        {
            if (progress.Workers.Count == 0) return;
            worker = Math.Clamp(worker, 1, progress.Workers.Count);
            progress.Workers.RemoveAt(worker - 1);
            Persist(progress);
        }
    }
    static async Task SetWorkers(IPlayer player, Plot plot, int count)    {
        var states = new StateMachineData(new int[plot.States.Length], 0, plot.States).Set("num_workers", count);
        await Game.SendWorldRecord(player, plot.ObjectId, InstanceEntities.StateMachineUpdate(plot.ObjectId, plot.StateIndex, states),
            $"0x02bc Kingsmarch plot {plot.Order + 1} num_workers {count}");
    }

    /// <summary>Puts a worker on one of the town's jobs -the mine (VillageJobTypes 1), the smeltery (2) or one of the
    /// harbour's three ships (6) -instead of a farm plot. None of these has a client-side plot state to re-state (the
    /// mine hut and the smeltery stand as plain town furniture, and a ship's crew is one of the numbers the port card
    /// counts), so unlike <see cref="Assign"/> this only moves the worker and saves the farm.</summary>
    /// <param name="player">The player whose town it is.</param>
    /// <param name="worker">The worker's number in the town, counting from one.</param>
    /// <param name="job">"mine", "smelter" or "ship".</param>
    /// <param name="ship">Which ship, counting from one, when <paramref name="job"/> is "ship".</param>
    public static string AssignJob(IPlayer player, int worker, string job, int ship = 1)
    {
        Progress progress = State(player);
        int slot;
        string what;
        if (job is "mine" or "mining") { slot = MineJob; what = "the mine"; }
        else if (job is "smelter" or "smelting" or "smelt" or "smeltery") { slot = SmelterJob; what = "the smeltery"; }
        else if (job is "ship" or "shipping")
        {
            if (ship < 1 || ship > 3) return $"no ship {ship}: the harbour has three (1-3)";
            slot = ShipJob - (ship - 1);
            what = $"ship {ship}";
        }
        else return "the town's jobs are the mine, the smeltery and the three ships (a number 1-6 is a farm plot)";
        lock (_lock)
        {
            if (worker < 1 || worker > progress.Workers.Count)
                return $"no worker {worker}: the town has {progress.Workers.Count} (hire with /village hire)";
            progress.Workers[worker - 1].Plot = slot;
            Persist(progress);
        }
        return $"Worker {worker} now works {what}.";
    }

    /// <summary>The mine and the smeltery cycles used to run here, one ore or bar a worker every MineSeconds/
    /// SmeltSeconds into the town's store; they are the village module's production lines now (VillageProduction, whose
    /// counters the mine and blacksmith panels draw and whose rate is the client's own curve), so a worker on the mine
    /// or the smeltery is counted there through the settlement's mirror and produces nothing twice.</summary>

    /// <summary>The farm's round: every plot is settled up to now, so its crop lands in the town's store as it grows. A
    /// plot produces whether the player is standing there or not, as long as a worker tends it and the town is not
    /// halted.</summary>
    /// <summary>Settles every plot up to now and charges the town's wages. A plot turns time into crops while a worker
    /// tends it; the wage is charged per hour worked, and the moment the treasury cannot cover it the whole town halts
    /// until it is funded again (RE C.3). Returns a line for the player when the town started or stopped working.</summary>
    static string? Work(Progress progress)
    {
        bool halted = progress.Halted;
        List<Plot> plots;
        lock (_lock) plots = [.. progress.Plots.Values];
        double now = UtcNow();
        foreach (var plot in plots) Settle(progress, plot, now);
        SettleWages(progress, now);
        if (progress.Halted == halted) return null;
        return progress.Halted
            ? "Kingsmarch: the treasury is empty, so every job in the town has stopped. Fund it with Johan (or /treasury)."
            : "Kingsmarch: the treasury is funded again, so the town is working.";
    }

    /// <summary>Settles the time the character was away, once per area load and before the ordinary tick: the plots keep
    /// growing and the working workers keep being paid for that gap, up to the moment the treasury can no longer cover
    /// the wages -an empty treasury stops every job in the town (RE C.3). So a character who leaves the town running and
    /// comes back to a funded treasury finds the crops it grew and the gold the wages cost, rather than a frozen town.</summary>
    /// <param name="progress">This area load's record; <see cref="Progress.AwayFrom"/> is the last settled moment of the
    /// previous session, and is cleared here so the catch-up happens once.</param>
    static void CatchUp(Progress progress)
    {
        double from = progress.AwayFrom;
        progress.AwayFrom = 0;
        if (from <= 0 || progress.Plots.Count == 0) return;
        double now = UtcNow();
        double away = Math.Min(now - from, AwayCap);
        if (away <= 1) return;
        int working = progress.Workers.Count(Working);
        double worked = away;
        int perHour = WagesPerHour(progress);
        if (working > 0 && perHour > 0)
        {
            // The wages the gap owes, and how much of the gap the treasury could actually pay for.
            double afford = KingsmarchPort.Gold(progress.Who) / perHour * 3600.0;
            if (afford < worked)
            {
                worked = Math.Max(0, afford);
                progress.Halted = true;
            }
            int pay = (int)Math.Floor(perHour * worked / 3600.0);
            if (pay > 0) KingsmarchPort.Spend(progress.Who, pay);
        }
        double start = now - worked;
        foreach (var plot in progress.Plots.Values)
        {
            // A plot produces only for the part of the gap the town was paid for; the rest is time it stood halted.
            if (plot.SettledAt < start) plot.SettledAt = start;
            Settle(progress, plot, now);
        }
        progress.WageSettledAt = now;
        progress.WageDebt = 0;
        Persist(progress);
        Console.WriteLine($"[gateway] kingsmarch catch-up: {progress.Who} was away {away / 60:0.#} min, "
            + $"paid {perHour:0} gold an hour for {worked / 60:0.#} min{(progress.Halted ? " (the treasury ran out)" : "")}");
    }

    /// <summary>Charges the town's wages for the time since they were last settled: every worker that holds a job costs
    /// <see cref="WageOf"/> an hour out of the treasury, and an idle one costs nothing (RE C.3). When the treasury
    /// cannot pay, the town halts -the plots keep their crops but stop growing, and one fill of the treasury restarts
    /// everything (ClientStrings 7486/7684). Time the character was away is not billed and not produced for: the town is
    /// only running while its character is in it.</summary>
    static void SettleWages(Progress progress, double now)
    {
        double dt = now - progress.WageSettledAt;
        progress.WageSettledAt = now;
        if (progress.Halted)
        {
            // Nothing was worked while halted, so nothing is owed; a funded treasury puts the town back to work.
            progress.WageDebt = 0;
            if (KingsmarchPort.Gold(progress.Who) > 0) progress.Halted = false;
            return;
        }
        if (dt <= 0) return;
        // A missing tick (a reload, a lagging area load) must not bill an hour of wages at once.
        if (dt > 60) dt = 0;
        int perHour = WagesPerHour(progress);
        if (perHour <= 0)
        {
            progress.WageDebt = 0;
            return;
        }
        progress.WageDebt += perHour * dt / 3600.0;
        int due = (int)Math.Floor(progress.WageDebt);
        if (due <= 0) return;
        int pay = (int)Math.Min(due, KingsmarchPort.Gold(progress.Who));
        if (pay > 0) KingsmarchPort.Spend(progress.Who, pay);
        progress.WageDebt -= pay;
        if (pay < due) progress.Halted = true;
    }

    static async Task SetPlant(IPlayer player, Progress progress, uint objectId, Plot plot)
    {
        // The plot's crop is the model the client builds in its on_or_create_state_plant_<n> event, so a live plot changes
        // crop by being re-created in the new state (a 0x2bc update alone leaves the old model; tried 2026-11-19 17:38).
        // The new object needs a fresh id too: re-creating the same id leaves the old, bare instance in place (17:50).
        // Removing and creating in the same batch is what corrupted the client's object map (its 0x2bd destroy is
        // deferred, so the new object reusing the old block while that block is still queued breaks its tree lookup), so
        // the create waits for a later tick: see D:\婢堆嗗亯妤哥厧浼愭担婊冨隘\scratch\object-lifecycle-report.md.
        var states = new StateMachineData(new int[plot.States.Length], 0, plot.States).Set("plant", plot.Crop);
        await Game.SendWorldRecord(player, objectId, InstanceEntities.StateMachineUpdate(objectId, plot.StateIndex, states),
            $"0x02bc Kingsmarch plot 0x{objectId:x} plant {plot.Crop}");
        var row = plot.Row;
        int x = _origin.X + row.X, y = _origin.Y + row.Y;
        await Game.SendWorldRemoval(player, objectId, $"0x02bd Kingsmarch plot 0x{objectId:x} removed to replant");
        lock (_lock)
        {
            // The plot keeps its place in the record while its new object waits: one missing from the record for that
            // second would drop out of the report and out of the save. The id it holds is the one the client was just
            // told to destroy, so its timestamp says "not settled" until the new object exists, and a click waits.
            plot.CreatedAt = UtcNow();
            plot.Replanting = true;
            progress.Pending.Add((UtcNow() + 1.0, plot, x, y));
        }
    }

    /// <summary>Creates the plots that were removed for a replant, one second after their removal.</summary>
    static async Task Flush(IPlayer player, Progress progress, double now)
    {
        lock (_lock) if (progress.Pending.Count == 0) return;
        List<(double At, Plot Plot, int X, int Y)> due;
        lock (_lock)
        {
            due = [.. progress.Pending.Where(p => now >= p.At)];
            foreach (var p in due) progress.Pending.Remove(p);
        }
        foreach (var (_, plot, x, y) in due)
        {
            uint id;
            lock (_lock)
            {
                id = progress.Id++;
                progress.Plots.Remove(plot.ObjectId);
                plot.ObjectId = id;
                plot.Replanting = false;
                // UtcNow, the clock the click's Quiet guard reads; the queue's At is the area tick's own counter.
                plot.CreatedAt = UtcNow();
                progress.Plots[id] = plot;
            }
            int hands = progress.Workers.Count(w => w.Plot == plot.Order);
            await Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(plot.Row.Type),
                    Blob(x, y, plot.Row, plot.Type, plot.Crop, hands)),
                $"0x02bb create Kingsmarch plot 0x{id:x} {plot.Row.Type} at ({x}, {y}) plant {plot.Crop} workers {hands}");
            // The plot has a new id, and a reload has to pick it up under that one: the save is brought up to date here
            // rather than on the minute, so the id a save names is the id the client was last sent.
            Persist(progress);
        }
    }

    // The town's objects, from the client's room_doodads rows of the kingsmarch.arm room (index 1984) - the bytes the old
    // data/kingsmarch-town.json carried: type, model, room-local cell and angle, in the room's own order. Embedded now so
    // a fresh checkout places the same town without a data file beside the server; the room's own origin (roomX/roomY,
    // 138/138) is the _origin above.
    static readonly List<TownObject> TownLayout =
    [
        new("Metadata/NPC/League/Kalguur/VillageFaustus", "Metadata/NPC/League/Heist/Faustus/Faustus.ao", 389, 625, 1.17173f),
        new("Metadata/NPC/League/Kalguur/VillageIsla", "Metadata/NPC/League/Heist/Isla/Isla.ao", 213, 547, -2.46387f),
        new("Metadata/NPC/League/Kalguur/VillageTujen", "Metadata/NPC/League/Expedition/Tujen/Tujen.ao", 283, 429, -0.7837f),
        new("Metadata/NPC/League/Kalguur/VillageRog", "Metadata/NPC/League/Expedition/Rog/Rog.ao", 316, 523, -1.34867f),
        new("Metadata/NPC/League/Kalguur/VillageDannig", "Metadata/NPC/League/Expedition/Dannig/Dannig.ao", 493, 478, -0.41545f),
        new("Metadata/NPC/League/Kalguur/VillageFarmer", "Metadata/NPC/League/Kalguur/KalguuranFarmer/KalguuranFarmer.ao", 367, 794, -1.6406f),
        new("Metadata/NPC/League/Kalguur/VillageRecruiter", "Metadata/NPC/League/Kalguur/KalguuranRecruiterAl/KalguuranRecruiterAl.ao", 422, 664, -0.68752f),
        new("Metadata/NPC/League/Kalguur/VillageMayor", "Metadata/NPC/League/Kalguur/KalguuranCityManager/KalguuranCityManager.ao", 429, 624, -1.5708f),
        new("Metadata/MiscellaneousObjects/Waypoint", "Metadata/Terrain/Missions/Hideouts/Objects/WaypointVariants/waypointcore_church.ao", 452, 516, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDevicePortal", "Metadata/Effects/Spells/portal/portal2.ao", 109, 603, -1.55615f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDevicePortal", "Metadata/Effects/Spells/portal/portal2.ao", 114, 591, -1.56413f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDevicePortal", "Metadata/Effects/Spells/portal/portal2.ao", 154, 591, 1.59226f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDevicePortal", "Metadata/Effects/Spells/portal/portal2.ao", 159, 603, 1.5434f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDevicePortal", "Metadata/Effects/Spells/portal/portal2.ao", 153, 615, 1.58862f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDevicePortal", "Metadata/Effects/Spells/portal/portal2.ao", 154, 509, 1.54965f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDevicePortal", "Metadata/Effects/Spells/portal/portal2.ao", 109, 521, -1.55617f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDevicePortal", "Metadata/Effects/Spells/portal/portal2.ao", 115, 508, -1.58434f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDevicePortal", "Metadata/Effects/Spells/portal/portal2.ao", 154, 532, 1.61114f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDevicePortal", "Metadata/Effects/Spells/portal/portal2.ao", 159, 521, 1.58817f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDevicePortal", "Metadata/Effects/Spells/portal/portal2.ao", 115, 532, -1.63862f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDevicePortal", "Metadata/Effects/Spells/portal/portal2.ao", 242, 615, 1.54799f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDevicePortal", "Metadata/Effects/Spells/portal/portal2.ao", 247, 603, 1.59282f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDevicePortal", "Metadata/Effects/Spells/portal/portal2.ao", 242, 591, 1.59583f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDevicePortal", "Metadata/Effects/Spells/portal/portal2.ao", 202, 615, -1.55132f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDevicePortal", "Metadata/Effects/Spells/portal/portal2.ao", 197, 603, -1.56963f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDevicePortal", "Metadata/Effects/Spells/portal/portal2.ao", 202, 591, -1.55426f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDevicePortal", "Metadata/Effects/Spells/portal/portal2.ao", 114, 615, -1.6144f),
        new("Metadata/MiscellaneousObjects/Stash", "Metadata/Terrain/Doodads/Encampment/chest_v01_01.ao", 420, 611, -1.5708f),
        new("Metadata/MiscellaneousObjects/GuildStash", "Metadata/Terrain/Doodads/Encampment/guildstash_v01_01.ao", 420, 641, -1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Sheep", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/WhiteSheepGrazing.ao", 329, 837, -0.53414f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Sheep", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/WhiteSheepGrazing.ao", 334, 884, -1.58296f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Sheep", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/WhiteSheepGrazing.ao", 353, 876, 1.98838f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Sheep", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/WhiteSheepGrazing.ao", 356, 848, 0.69193f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Sheep", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/WhiteSheepGrazing.ao", 361, 934, 0.30731f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Sheep", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/WhiteSheepGrazing.ao", 412, 887, -2.49548f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/FarmingPlot", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/CropsOrbisfera01.ao", 217, 762, 1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/FarmingPlot", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/CropsOrbisfera01.ao", 290, 762, -1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/FarmingPlot", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/CropsOrbisfera01.ao", 219, 848, 1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/FarmingPlot", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/CropsOrbisfera01.ao", 291, 851, -1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/FarmingPlot", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/CropsOrbisfera01.ao", 217, 946, -1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/FarmingPlot", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/CropsOrbisfera01.ao", 290, 946, -1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Seagull", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 268, 446, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Seagull", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 348, 467, -2.57716f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Seagull", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 351, 455, -0.74714f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Seagull", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 356, 471, 1.58127f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Seagull", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 341, 462, -0.98441f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Seagull", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 364, 452, 0.75027f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Seagull", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 363, 464, -1.575f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Seagull", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 373, 458, -1.57168f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Seagull", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 71, 570, 0.55787f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Seagull", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 82, 567, -1.4922f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Seagull", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 84, 556, -2.92664f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Seagull", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 73, 557, -1.94452f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Seagull", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 78, 562, -2.83958f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Seagull", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 79, 556, -0.18307f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Seagull", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 89, 547, -2.62051f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Seagull", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 92, 559, 1.09401f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/SettlerPeasants/Farming/RandomFarmer.ao", 321, 780, -1.11893f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/SettlerPeasants/Farming/RandomForager.ao", 277, 755, 1.89924f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/EzomyteCivilian/forkman.ao", 281, 768, -2.47853f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/SettlerPeasants/Farming/RandomFarmer.ao", 213, 800, 0.25756f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/EzomyteCivilian/forkman.ao", 225, 740, 1.19027f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/SettlerPeasants/Farming/RandomForager.ao", 212, 772, 2.36159f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/SettlerPeasants/Farming/RandomFarmer.ao", 206, 885, 0.33463f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/EzomyteCivilian/forkman.ao", 224, 870, 2.37112f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/SettlerPeasants/Farming/RandomForager.ao", 225, 849, -1.29144f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/SettlerPeasants/Farming/RandomFarmer.ao", 287, 886, -0.67355f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/EzomyteCivilian/forkman.ao", 288, 828, 1.06578f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/SettlerPeasants/Farming/RandomForager.ao", 292, 870, 1.65435f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/SettlerPeasants/Farming/RandomFarmer.ao", 299, 909, -2.54494f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/EzomyteCivilian/forkman.ao", 298, 926, 0.28528f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/SettlerPeasants/Farming/RandomForager.ao", 291, 962, 1.27781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/EzomyteCivilian/forkman.ao", 214, 965, -2.47853f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/SettlerPeasants/Farming/RandomForager.ao", 224, 940, 1.06072f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/SettlerPeasants/Farming/RandomFarmer.ao", 246, 930, -1.65798f),
        new("Metadata/Terrain/Tools/AudioTools/Leagues/SettlersLeague/ShopIndoorAudioAO", "Metadata/Terrain/Tools/AudioTools/Leagues/SettlersLeague/ShopIndoorAudioAO.ao", 286, 512, 0f),
        new("Metadata/Terrain/Tools/AudioTools/Leagues/SettlersLeague/TavernIndoorAudioAO", "Metadata/Terrain/Tools/AudioTools/Leagues/SettlersLeague/TavernIndoorAudioAO.ao", 401, 643, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 294, 389, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 250, 389, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 250, 355, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 295, 355, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 295, 322, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 250, 322, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 295, 281, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 250, 281, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 295, 254, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 250, 211, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 294, 213, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeAnimalController", "Metadata/Terrain/Doodads/Tools/Marker_Pin_Important_01.ao", 270, 415, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 141, 388, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 97, 388, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 97, 354, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 142, 354, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 140, 322, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 96, 322, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 141, 281, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 96, 281, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 96, 253, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 142, 253, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 141, 212, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 96, 211, -1.23781f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeAnimalController", "Metadata/Terrain/Doodads/Tools/Marker_Pin_Important_01.ao", 117, 415, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 412, 468, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 408, 477, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 425, 469, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 419, 477, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 416, 489, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 221, 461, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 212, 467, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 218, 469, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 304, 625, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 295, 634, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 292, 623, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 301, 618, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 313, 623, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 308, 633, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 281, 713, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 275, 705, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 285, 704, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 296, 709, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 288, 713, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 276, 691, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 487, 561, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 482, 564, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 486, 570, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 493, 572, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken", "Metadata/Critters/Chicken/Chicken.ao", 483, 576, 0f),
        new("Metadata/NPC/League/Kalguur/TavernDialogueController", "Metadata/Terrain/Doodads/Tools/Marker_Pin_Important_01.ao", 402, 640, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/WorkerController", "Metadata/Terrain/Doodads/Tools/Marker_Pin_Important_01.ao", 265, 635, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DisenchantingWorker", "Metadata/NPC/League/Kalguur/ExpeditionHireling/ExpeditionHirelingDisenchanting.ao", 276, 526, -3.04434f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DisenchantingWorker", "Metadata/NPC/League/Kalguur/ExpeditionHireling/ExpeditionHirelingDisenchanting.ao", 268, 506, -1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/Thugs/RandomThug.ao", 254, 622, 0.30077f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/Thugs/RandomThug.ao", 251, 612, 1.74926f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/Thugs/RandomThug.ao", 265, 621, -0.44978f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/Thugs/RandomThug.ao", 272, 614, -1.18214f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/Thugs/RandomThug.ao", 257, 602, 2.67414f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/Thugs/RandomThug.ao", 267, 603, -2.3323f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/Thugs/RandomThug.ao", 78, 583, 2.29574f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/Thugs/RandomThug.ao", 92, 585, -2.3323f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/Thugs/RandomThug.ao", 96, 598, -1.18214f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/Thugs/RandomThug.ao", 89, 609, -0.44978f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/Thugs/RandomThug.ao", 77, 609, 0.9282f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/Thugs/RandomThug.ao", 76, 596, 1.74926f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/Thugs/RandomThug.ao", 112, 476, 2.67414f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/Thugs/RandomThug.ao", 122, 470, 2.98679f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/Thugs/RandomThug.ao", 106, 503, 0.21929f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/Thugs/RandomThug.ao", 117, 497, -0.03803f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/Thugs/RandomThug.ao", 100, 494, 0.68449f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/Thugs/RandomThug.ao", 104, 483, 1.74926f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/SettlerPeasants/Farming/RandomForager.ao", 209, 926, 1.35413f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/SettlerPeasants/Farming/RandomForager.ao", 283, 947, 1.69077f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/SettlerPeasants/Farming/RandomForager.ao", 303, 836, 2.12197f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/SettlerPeasants/Farming/RandomForager.ao", 227, 826, -0.902f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/SettlerPeasants/Farming/RandomForager.ao", 286, 782, 2.84397f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/TemporaryNPCWorker", "Metadata/NPC/League/Kalguur/SettlerPeasants/Farming/RandomForager.ao", 226, 780, -2.03534f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DisenchantingItem", "Metadata/Terrain/Doodads/Leagues/Settlers/RandomDisenchantingItem.ao", 259, 506, 1.53963f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DisenchantingItem", "Metadata/Terrain/Doodads/Leagues/Settlers/RandomDisenchantingItem.ao", 275, 536, -3.14159f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Peacock", "Metadata/Critters/PeacockMale/PeacockMale.ao", 207, 666, 0f),
        new("Metadata/NPC/League/Kalguur/Letters/LetterController", "Metadata/NPC/League/Kalguur/Letters/LetterToCaptain.ao", 407, 669, 0f),
        new("Metadata/MiscellaneousObjects/Stash", "Metadata/Terrain/Doodads/Encampment/chest_v01_01.ao", 214, 533, -1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPCIdle", "Metadata/NPC/League/Kalguur/KalguuranRecruiterAnn/KalguuranRecruiterAnn.ao", 431, 659, -1.60313f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPCIdle", "Metadata/NPC/League/Kalguur/Tavern/RandomTarvernSittingParkBench.ao", 318, 590, 1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPCIdle", "Metadata/NPC/League/Kalguur/Tavern/RandomTarvernSittingParkBench.ao", 279, 592, -1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPCIdle", "Metadata/NPC/League/Kalguur/Tavern/RandomTarvernSittingParkBench.ao", 287, 642, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPCIdle", "Metadata/NPC/League/Kalguur/Tavern/RandomTarvernSittingParkBench.ao", 190, 467, 1.09956f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPCIdle", "Metadata/NPC/League/Kalguur/Tavern/RandomTarvernSittingParkBench.ao", 503, 571, -1.6057f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Hideouts/VillageDoodads/LumbermillRedWagonOpenIdle01.ao", 476, 669, 2.30383f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Canary.ao", 183, 715, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeLarge", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/fenec01_01.ao", 574, 391, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPC", "Metadata/NPC/League/Kalguur/EzomyteCivilian/EzomyteCiviliansMaleLeaningPickaxe.ao", 165, 659, 1.53786f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPC", "Metadata/NPC/League/Kalguur/ExpeditionHireling/ExpeditionHirelingHoldingPickaxe.ao", 167, 712, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/coalpile01.ao", 525, 429, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/coalpile02.ao", 525, 445, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Expedition/CampProps/ExpeditionArmourRacks01_01.ao", 490, 418, -1.71042f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/TavernLantern01_01.ao", 443, 632, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeAnimated", "Metadata/Effects/Environment/League_Settlers/SmeltingMachine/ao/SmeltingMachine.ao", 515, 498, -1.58825f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/TavernLantern01_01.ao", 408, 593, -1.55334f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/TavernLantern01_01.ao", 392, 593, -1.55334f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/TavernLantern01_01.ao", 361, 622, -3.14159f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/TavernLantern01_01.ao", 361, 669, -3.14159f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/TavernLantern01_01.ao", 412, 688, 1.58825f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/TavernLantern01_01.ao", 271, 539, 1.6057f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/TavernLantern01_01.ao", 324, 527, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/TavernLantern01_01.ao", 324, 502, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/cart_bismutht_01.ao", 113, 736, -0.13384f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/cart_copper_01.ao", 138, 757, 2.65811f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/cart_iron_01.ao", 119, 772, 2.99514f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/cart_petrifiedtree_01.ao", 133, 727, 2.94071f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/cart_verisium_01.ao", 222, 710, -0.45378f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPC", "Metadata/NPC/League/Kalguur/KalguuranTreasureHunters/RandomTreasureHunterStanding.ao", 257, 257, -0.8589f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeAnimated", "Metadata/NPC/League/Kalguur/DogTraderDobermann/DogTraderDobermann.ao", 378, 758, -1.65807f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Expedition/CampProps/ExpeditionCratesFieldTools01_01.ao", 220, 913, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Woods/Village/wheelbarrow04.ao", 307, 894, 0.65174f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeLarge", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/TavernRoof01_01.ao", 400, 645, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/CropPlanterBox01_01.ao", 290, 763, 1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/CropPlanterBox01_01.ao", 217, 763, 1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/CropPlanterBox01_01.ao", 291, 851, -1.55335f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/CropPlanterBox01_01.ao", 219, 850, 1.55335f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/CropPlanterBox01_01.ao", 218, 946, -1.55335f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/CropPlanterBox01_01.ao", 291, 946, -1.55335f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage3_planterbox_01.ao", 423, 582, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage3_planterbox_01.ao", 449, 587, 0.7854f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage1_supplies_01.ao", 439, 576, -0.53976f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage3_planterbox_01.ao", 354, 586, -0.7854f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage3_planterbox_01.ao", 348, 677, -1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage1_supplies_01.ao", 350, 702, 0.81436f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage2_lamppost_01.ao", 313, 650, 2.3562f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage2_lamppost_01.ao", 329, 479, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage2_lamppost_01.ao", 239, 479, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage2_lamppost_01.ao", 219, 553, -2.3562f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage2_lamppost_01.ao", 421, 450, -2.618f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage2_lamppost_01.ao", 503, 547, -1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage2_lamppost_01.ao", 508, 672, -1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage2_lamppost_01.ao", 459, 615, 1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage2_lamppost_01.ao", 85, 489, 1.39626f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage3_planterbox_01.ao", 252, 480, -0.00035f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage3_planterbox_01.ao", 299, 480, -0.00035f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage1_supplies_01.ao", 325, 439, 3.07722f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/NoticeboardInteractable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/recruitment_noticeboard_01.ao", 371, 582, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeLargeAnimate", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Wharf01_01.ao", 117, 433, 1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/shipping_goods_crate.ao", 258, 395, 1.60071f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/fishBarrel02.ao", 257, 404, 1.0472f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/fishBarrel02.ao", 250, 438, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/fishBarrel01.ao", 241, 440, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/shipping_goods_barrel.ao", 288, 364, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/shipping_goods_crate.ao", 286, 356, 2.59895f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/fishBarrel02.ao", 138, 435, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/shipping_goods_crate.ao", 148, 437, 0.08962f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/shipping_goods_barrel.ao", 135, 333, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/shipping_goods_crate.ao", 133, 326, -0.52608f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/shipping_goods_crate.ao", 91, 440, -0.52495f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/fishBarrel03.ao", 100, 435, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeLarge", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop01_01.ao", 294, 511, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeLarge", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Tavern01_01.ao", 358, 646, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeLarge", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/MineHut01_01.ao", 176, 742, -0.12217f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeLarge", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Smelting01_01.ao", 532, 492, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeLarge", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Blacksmith01_01.ao", 527, 431, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeLargeAnimate", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Wharf01_01.ao", 271, 433, 1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/smithing_tool_02.ao", 515, 446, 0.55108f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/smithing_tool_01.ao", 482, 406, 0.01745f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_mining_crane_01.ao", 82, 725, 1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_mining_crane_02.ao", 103, 759, 0.97738f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_mining_crusher.ao", 184, 690, -1.62315f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDevice", "Metadata/Terrain/Doodads/Leagues/Settlers/KalguuranMapDevice/settler_map_device_tier_3.ao", 134, 599, -3.14159f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDevice", "Metadata/Terrain/Doodads/Leagues/Settlers/KalguuranMapDevice/settler_map_device_tier_3.ao", 134, 517, -3.14159f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDevice", "Metadata/Terrain/Doodads/Leagues/Settlers/KalguuranMapDevice/settler_map_device_tier_3.ao", 222, 599, -3.14159f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDeviceButton", "Metadata/Terrain/Doodads/Leagues/Settlers/KalguuranMapDevice/KalguuranMapDevicePedestal.ao", 222, 583, -3.14159f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDeviceButton", "Metadata/Terrain/Doodads/Leagues/Settlers/KalguuranMapDevice/KalguuranMapDevicePedestal.ao", 134, 501, -3.14159f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/MapDeviceButton", "Metadata/Terrain/Doodads/Leagues/Settlers/KalguuranMapDevice/KalguuranMapDevicePedestal.ao", 134, 583, -3.14159f),
        new("Metadata/MiscellaneousObjects/Settlers/RecombinatingBench", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RecombinationMachine01_01.ao", 215, 513, -1.51843f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeShip", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/ShippingFullShip03.ao", 49, 308, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeShip", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/ShippingFullShip01.ao", 341, 256, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeShip", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/ShippingFullShip02.ao", 202, 259, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/FarmingPlotInteractable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/farming_Interactable02_01.ao", 240, 764, 1.59448f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/FarmingPlotInteractable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/farming_Interactable01_01.ao", 265, 764, -1.53377f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/FarmingPlotInteractable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/farming_Interactable03_01.ao", 270, 820, -0.6215f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/FarmingPlotInteractable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/farming_Interactable04_01.ao", 240, 815, 0.59588f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/FarmingPlotInteractable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/farming_Interactable05_01.ao", 240, 915, 0.63719f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/FarmingPlotInteractable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/farming_Interactable06_01.ao", 269, 915, -0.70865f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeLarge", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShopRoof01_01.ao", 294, 511, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPCIdle", "Metadata/NPC/League/Kalguur/SettlerPeasants/Standing/RandomStanding.ao", 344, 584, 0.52452f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPCIdle", "Metadata/NPC/League/Kalguur/SettlerPeasants/Standing/RandomStanding.ao", 355, 576, -1.81833f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeLargeAnimate", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/WindmillBlade.ao", 402, 811, 1.12814f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/KalguuranShovel01_01.ao", 231, 816, -1.5359f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/KalguuranShovel01_01.ao", 269, 730, -2.23684f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/KalguuranShovel01_01.ao", 310, 912, 2.96826f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/KalguuranFork01_01.ao", 285, 816, -1.44659f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/KalguuranHoe01_01.ao", 294, 818, -1.5875f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/KalguuranShovelScythe01_01.ao", 314, 819, -1.69227f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/KalguuranHoe01_01.ao", 339, 762, -2.99147f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/KalguuranShovelScythe01_01.ao", 339, 754, -1.06373f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/KalguuranFork01_01.ao", 337, 764, 1e-05f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/KalguuranFork01_01.ao", 359, 814, -1.42245f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/KalguuranFork01_01.ao", 211, 984, -1.42245f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/KalguuranShovelScythe01_01.ao", 202, 819, 0.56053f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/KalguuranShovelScythe01_01.ao", 235, 726, 0.41669f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/KalguuranHoe01_01.ao", 238, 732, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullHideOnUpgrade", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 436, 526, 1.13177f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullHideOnUpgrade", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 442, 461, -0.57636f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullHideOnUpgrade", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 439, 489, 2.9647f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullHideOnUpgrade", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 429, 497, 2.06447f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullHideOnUpgrade", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 471, 523, 2.06447f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullHideOnUpgrade", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 424, 558, 1.0138f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullHideOnUpgrade", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 421, 550, 1.62236f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullHideOnUpgrade", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Seagull.ao", 428, 554, 1.62236f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPC", "Metadata/NPC/League/Kalguur/EzomyteCivilian/EzomyteCivilianFemaleFarmerLeaning.ao", 346, 816, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SheepUpgradeable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/WhiteSheepGrazing.ao", 340, 860, -1.71644f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SheepUpgradeable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/WhiteSheepGrazing.ao", 347, 914, -1.71644f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SheepUpgradeable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/WhiteSheepGrazing.ao", 389, 877, -1.71644f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SheepUpgradeable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/WhiteSheepGrazing.ao", 393, 909, -1.71644f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SheepUpgradeable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/WhiteSheepGrazing.ao", 337, 952, -1.71644f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SheepUpgradeable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/WhiteSheepGrazing.ao", 336, 928, -1.71644f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SheepUpgradeable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/WhiteSheepGrazing.ao", 369, 893, -1.71644f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SheepUpgradeable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/WhiteSheepGrazing.ao", 370, 863, -1.71644f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SheepUpgradeable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/WhiteSheepGrazing.ao", 351, 831, -1.71644f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SheepUpgradeable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/WhiteSheepGrazing.ao", 332, 900, -1.71644f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SheepUpgradeable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/WhiteSheepGrazing.ao", 341, 979, -1.71644f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SheepUpgradeable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/WhiteSheepGrazing.ao", 308, 1006, -1.71644f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SheepUpgradeable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/WhiteSheepGrazing.ao", 267, 1013, -1.71644f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SheepUpgradeable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/WhiteSheepGrazing.ao", 382, 942, -1.71644f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SheepUpgradeable", "Metadata/Critters/Sheep/BlackSheep.ao", 387, 865, -1.71644f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeAnimated", "Metadata/Terrain/Doodads/Hideouts/KalguuranWorkshopCraftingBench/attachments/SimpleHireling_Shovelling.ao", 511, 429, 1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeAnimated", "Metadata/Terrain/Doodads/Hideouts/KalguuranWorkshopCraftingBench/attachments/SimpleHireling_WeaponRack.ao", 509, 404, 1.14598f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeAnimated", "Metadata/Terrain/Doodads/Hideouts/KalguuranWorkshopCraftingBench/attachments/SimpleHireling_Anvil.ao", 510, 446, 1.75753f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeAnimated", "Metadata/NPC/League/Kalguur/Blacksmith/EzomyteCiviliansMaleChiseling.ao", 483, 417, 1.62075f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPCIdle", "Metadata/NPC/League/Kalguur/Smithing/RandomWallLeaning.ao", 501, 547, -1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPCIdle", "Metadata/NPC/League/Kalguur/Smithing/RandomBarrelLeaning.ao", 508, 590, -1.61507f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPC", "Metadata/NPC/League/Kalguur/EzomyteCivilian/EzomyteCiviliansMaleLeaningShovel.ao", 94, 675, -1.49252f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Act5/Theopolis/SlaveDens/SlavePen_chair.ao", 511, 407, -2.05949f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Hideouts/VillageDoodads/AlcoholCrate_01.ao", 257, 384, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Hideouts/VillageDoodads/AlcoholCrate_02.ao", 284, 421, -0.72672f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Hideouts/VillageDoodads/AlcoholCrate_03.ao", 385, 579, -0.36934f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Hideouts/WellObjects/WellObjects05_1.ao", 350, 894, 1.11701f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Expedition/plunger.ao", 156, 723, -0.94248f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Expedition/plunger.ao", 177, 717, -0.15708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Expedition/explosive.ao", 210, 723, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Expedition/explosive.ao", 209, 715, 1.13446f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Expedition/explosive.ao", 158, 729, 1.13446f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop/Carpet01.ao", 271, 511, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop/ChestStack01.ao", 271, 511, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop/CounterProps01.ao", 271, 502, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop/CounterProps02.ao", 271, 511, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop/Drawer01.ao", 271, 511, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop/Drawer02.ao", 271, 511, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop/GroundProps01.ao", 271, 511, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop/GroundProps02.ao", 271, 511, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop/Shed01.ao", 271, 515, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop/ShelfProps01.ao", 271, 511, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop/SideTable01.ao", 271, 511, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop/SmallTable01.ao", 295, 511, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop/SmallTable02.ao", 271, 511, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop/SteelBarrel01.ao", 271, 511, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop/SteelChest01.ao", 271, 511, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop/Storage01.ao", 247, 511, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop/WaterFountain01.ao", 271, 515, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/TavernStage2Table01.ao", 376, 612, 1.6057f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/TavernStage2Bench01.ao", 386, 612, 1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/TavernStage2Bench01.ao", 366, 612, 1.64061f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Delve/Contraption02.ao", 229, 536, -0.31416f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Delve/Contraption04.ao", 219, 498, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Delve/Contraption03.ao", 229, 505, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeLarge", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Stage2Road04_01.ao", 91, 437, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeLarge", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Stage2Road03_01.ao", 91, 437, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeLarge", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Stage2Road01_01.ao", 91, 437, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeLarge", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Stage2Road02_01.ao", 91, 437, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Tree01.ao", 113, 668, -1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage3_planterbox_04.ao", 175, 475, -1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/chair01.ao", 185, 473, 1.10308f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/chair01.ao", 290, 649, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/chair01.ao", 509, 568, -1.6009f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage3_planterbox_01.ao", 423, 582, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage3_planterbox_01.ao", 449, 587, 0.78539f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage3_planterbox_01.ao", 354, 586, -0.78539f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage3_planterbox_01.ao", 348, 677, -1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage3_planterbox_01.ao", 252, 480, -0.00035f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage3_planterbox_01.ao", 299, 480, -0.00035f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Pot02.ao", 287, 480, -1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Pot02.ao", 349, 622, -1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Pot02.ao", 454, 599, -1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Pot02.ao", 369, 479, -1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Pot02.ao", 184, 487, 2.13767f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Pot02.ao", 92, 665, -1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Pot02.ao", 347, 730, -1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Pot02.ao", 318, 606, -0.37284f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Pot02.ao", 263, 646, -1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Pot02.ao", 179, 461, 2.13767f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Pot01.ao", 312, 480, -1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Pot01.ao", 375, 482, -1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Pot01.ao", 347, 762, -1.07526f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPC", "Metadata/NPC/League/Kalguur/KalguuranTreasureHunters/RandomTreasureHunterSitting_03.ao", 224, 623, 3.08552f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPC", "Metadata/NPC/League/Kalguur/KalguuranTreasureHunters/RandomTreasureHunterSitting_01.ao", 74, 625, 1.52021f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/CanaryCage01.ao", 158, 706, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/CanaryCage03.ao", 156, 747, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/CanaryCage01.ao", 150, 782, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Canary.ao", 158, 706, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Animals/Canary.ao", 150, 782, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage1_supplies_01.ao", 158, 770, -1.6057f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Pickaxe01.ao", 187, 657, -1.17948f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Pickaxe02.ao", 157, 714, -1.01229f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/PickAxeWheelbarrow01.ao", 141, 707, 0.39899f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Pickaxe02.ao", 159, 736, 1.35721f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Wheelbarrow01.ao", 83, 709, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Wheelbarrow02.ao", 176, 657, 0.76795f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Wheelbarrow03.ao", 212, 689, 0.69813f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Wheelbarrow04.ao", 111, 715, 1.31965f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Wheelbarrow05.ao", 140, 675, -2.78553f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop/ChestStack01.ao", 562, 522, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop/SteelBarrel01.ao", 499, 550, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop/SteelChest01.ao", 529, 502, -2.09439f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/RogShop/WaterFountain01.ao", 554, 545, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage2_lamppost_01.ao", 178, 645, -0.00035f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage2_lamppost_01.ao", 258, 644, -0.00035f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Park01.ao", 292, 664, -1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage2_lamppost_01.ao", 316, 693, 1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage2_lamppost_01.ao", 69, 576, 1.569f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/BaggageCart01.ao", 114, 247, -1.8143f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/fishBarrel01.ao", 131, 254, 0.00035f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/fishBarrel01.ao", 258, 240, 0.00035f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/fishBarrel02.ao", 266, 247, 0.00035f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/BaggageCart01.ao", 282, 238, -0.27854f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/planks01.ao", 259, 306, -1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/planks01.ao", 288, 393, -1.57081f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/planks01.ao", 167, 422, 0.00035f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/fishBarrel03.ao", 197, 423, 0.00035f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/BaggageCart01.ao", 94, 470, -2.55385f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Bollard01.ao", 222, 434, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Bollard01.ao", 177, 433, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/MarketShops/Crate_Bread01.ao", 409, 431, 0.41888f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/MarketShops/Crate_Bread02.ao", 429, 432, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/MarketShops/Crate_Fish01.ao", 405, 421, -1.58825f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/MarketShops/Crate_Fish02.ao", 449, 430, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/MarketShops/Crate_Fish03.ao", 385, 430, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/MarketShops/MarketStand01_01.ao", 381, 503, 1.53589f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/MarketShops/MarketStand02_01.ao", 397, 411, 3.10672f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/MarketShops/MarketStandBread01_01.ao", 458, 417, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/MarketShops/MarketStandFish01_01.ao", 366, 418, 3.07179f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/MarketShops/MarketStandFish02_01.ao", 424, 416, -1.6057f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/MarketShops/MarketStandMeat01_01.ao", 385, 527, 1.53589f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/MarketShops/TableSlanted_Bread_3x_01.ao", 383, 489, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/MarketShops/TableSlanted_Deli_2x_01.ao", 350, 417, -1.6057f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/MarketShops/TableSlanted_Deli_2x_02.ao", 460, 430, 2.9845f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/MarketShops/TableSlanted_Deli_3x_01.ao", 389, 501, 1.55334f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Park03.ao", 298, 591, 1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/chair01.ao", 311, 592, 1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/chair01.ao", 286, 590, -1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPC", "Metadata/NPC/League/Kalguur/Tavern/RandomTarvernSittingBench.ao", 383, 612, -1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPC", "Metadata/NPC/League/Kalguur/Tavern/RandomTarvernSittingBench.ao", 369, 610, 1.5708f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/NoticeboardInteractable", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/recruitment_noticeboard_02.ao", 157, 701, -0.16626f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/NoticeboardInteractableSmelting", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/recruitment_noticeboard_02.ao", 507, 520, -1.38686f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeLarge", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Stage2Road05_01.ao", 91, 437, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeLarge", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/Stage2Road06_01.ao", 91, 437, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/town_stage1_supplies_01.ao", 347, 607, -1.37937f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/chair01.ao", 79, 661, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPC", "Metadata/NPC/League/Kalguur/Tavern/RandomTarvernSittingParkBench.ao", 77, 654, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase", "Metadata/Terrain/Doodads/Tools/invisible_blocker.ao", 211, 715, 0f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeAnimated", "Metadata/Effects/Microtransactions/Hideout/Villagers/KalguuranCiviliansMale02.ao", 477, 663, 0.73304f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPCIdle", "Metadata/NPC/League/Kalguur/SettlerPeasants/Standing/RandomStanding.ao", 232, 481, 0.82869f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPCIdle", "Metadata/NPC/League/Kalguur/SettlerPeasants/Standing/RandomStanding.ao", 246, 474, -1.81833f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPCIdle", "Metadata/NPC/League/Kalguur/SettlerPeasants/Standing/RandomStanding.ao", 402, 522, -1.81833f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPCIdle", "Metadata/NPC/League/Kalguur/SettlerPeasants/Standing/RandomStanding.ao", 495, 661, -1.54868f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPCIdle", "Metadata/NPC/League/Kalguur/SettlerPeasants/Standing/RandomStanding.ao", 397, 425, 0.66273f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPCIdle", "Metadata/NPC/League/Kalguur/SettlerPeasants/Standing/RandomStanding.ao", 88, 452, -0.95954f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPCIdle", "Metadata/NPC/League/Kalguur/SettlerPeasants/Standing/RandomStanding.ao", 129, 409, 0.17592f),
        new("Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPCIdle", "Metadata/NPC/League/Kalguur/SettlerPeasants/Standing/RandomStanding.ao", 124, 267, -0.15273f),
    ];

    /// <summary>The object types the client registers, from client.db: the generated <c>object_types</c> table carries
    /// each type's component list (the first <c>read</c> of them are what the wire blob writes) and the
    /// <c>object_type_shared_states</c> table carries a row's shared states in order - the bytes the old
    /// data/object-types.json held.</summary>
    static Dictionary<string, (string[], string[])> Types()
    {
        var result = new Dictionary<string, (string[], string[])>(StringComparer.Ordinal);
        try
        {
            // The shared states, keyed by the object_types row they belong to (the table's <c>type</c> column is that
            // row's <c>_row</c>, which <see cref="ClientTable"/>'s row Index is).
            var shared = new Dictionary<int, List<(int Ord, string Name)>>();
            foreach (var s in ClientTables.TableOrEmpty("object_type_shared_states"))
            {
                int type = s.IntOrNull("type") ?? -1;
                if (type < 0 || s.TextOrNull("name") is not { } name) continue;
                if (!shared.TryGetValue(type, out var list)) shared[type] = list = [];
                list.Add((s.IntOrNull("ord") ?? 0, name));
            }
            foreach (var t in ClientTables.TableOrEmpty("object_types"))
            {
                string type = t.TextOrNull("type") ?? "";
                if (type.Length == 0) continue;
                // The town also places world objects of other families (Stash, GuildStash, Waypoint, the Village NPCs);
                // they are real object types in the same table, so keep every one and let the component check decide.
                int read = t.IntOrNull("read") ?? 0;
                var components = t.Texts("components");
                string[] states = shared.TryGetValue(t.Index, out var list)
                    ? [.. list.OrderBy(e => e.Ord).Select(e => e.Name)] : [];
                result[type] = ([.. components.Take(read)], states);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Console.WriteLine($"[gateway] kingsmarch: the client's object_types tables: {ex.Message}");
        }
        return result;
    }
}
