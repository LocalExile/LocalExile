using System.Globalization;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

/// <summary>
/// The town objects of Kingsmarch (<c>KalguuranSettlersLeague</c>): the buildings, platforms, wharves and props the server
/// must create — the client draws the room's own doodads, but the town's floor, docks and buildings are objects of the
/// <c>Metadata/Terrain/Leagues/Settlers/HubTown/Objects/*</c> types, which is why the authored furniture floats without them.
///
/// The list is <c>data/kingsmarch-town.json</c> (from the client's <c>room_doodads</c> rows of the <c>kingsmarch.arm</c>
/// room, index 1984): type, model, room-local cell and angle. A type whose components this blob cannot write is skipped and
/// logged, never sent half-written: a short blob makes the client report "Buffer Underflow" and quit.
///
/// <c>data/kingsmarch-limit.txt</c> is how many objects to create (0 = none). It is meant to be raised a step at a time —
/// 1, then 10, then all — because the first all-at-once attempt (2026-11-17 00:2x) made the client quit as the town loaded,
/// so at least one component's layout here is still wrong. The safest types (Positioned, BaseEvents, Animated,
/// StateMachine, InteractionAction, MinimapIcon) come first, so the lowest limits exercise the least uncertain blobs.
/// </summary>
public static class KingsmarchTown
{
    const string Area = "KalguuranSettlersLeague";
    const string DataFile = "data/kingsmarch-town.json";
    const string LimitFile = "data/kingsmarch-limit.txt";
    // Dev: only the object types whose path contains one of these words (comma separated; empty = all).
    const string FilterFile = "data/kingsmarch-types.txt";
    const string AnimatedFile = "data/kingsmarch-animated.txt";
    // Types this client does not register at runtime (found by the acceptance sweep): never sent.
    const string BlockedFile = "data/kingsmarch-blocked.txt";
    // Models the client does not know (found the same way): a row whose model contains one of these is skipped.
    const string BlockedModelsFile = "data/kingsmarch-blocked-models.txt";
    // Types the client refuses (found by the run loop): sent as the base type instead, model kept.
    const string FallbackFile = "data/kingsmarch-fallback.txt";
    // Per-type object state, e.g. "FarmingPlot plant=1" (the crop; the plot swaps its own model per plant value) or
    // "FarmingPlotInteractable visible=1". A value set here is sent with the StateMachine "server value wins" flag (0x02),
    // without which the client reverts the state to the type's default.
    const string StatesFile = "data/kingsmarch-states.txt";
    // The farm's crops and harvest, per character, so they survive a reload, a new area load and a server restart.
    const string FarmFile = "data/kingsmarch-farm.json";
    // The town's clickable boards (treasury/recruiter/farm) only spawn when this file exists; see Boards() for why they are
    // off by default.
    const string BoardsFile = "data/kingsmarch-boards.txt";
    // The town lines the module creates when no dev filter file exists: the six plots, which the client never builds
    // (its doodad pass drops them and it refuses a server-made type it does not know; see Agents/docs/game/settlers.md).
    static readonly string[] DefaultFilter = ["Metadata/Terrain/Leagues/Settlers/HubTown/Objects/FarmingPlot"];

    // The town's own panels are gated on the IsMapOwner quest flag (NPCTalk 667 "Manage Town", 670 "Town Recruitment",
    // 672 "Disenchanting", 674 "Shipping"): without it their options are in the list but the server refuses them. The town
    // instance belongs to the player, so they own it.
    //
    // The client hides the row for a second reason: NPCTalk column 16 is a list of league quest flag ids the row requires,
    // and the client's own availability test (0x141E7BB90, live 2026-11-20) reads that list at row+0xbe/+0xc6 and tests the
    // second flag set at player+0x1d0 (0x14012AE40(player+0x1d0, id-1)) before the row is listed at all. Row 3610 "Manage
    // Town" (column 16 [334]) was therefore never offered: a read-only probe of that function logged include id 334 with
    // bit 333 clear and a rejection, while the six plain rows of the same NPC returned 62 (offered). The id is fluid: row
    // 3611, the later variant of the same option, requires 1436 and refuses 334 instead.
    internal const int KingsmarchTownOwnerLeagueFlag = 334;

    // The King's Hand's other two village options are gated the same way (NPCTalk column 16 is a list of league quest flag
    // ids the row requires): row 3612 "Fund the Treasury" needs flag 1, row 3613 "Invest in Recruitment" needs flag 1434,
    // and Raulf's row 3556 "Town Recruitment" needs flag 95. Without them the client never lists the rows, so the options
    // exist in the client's own data but never appear in the dialogue.
    internal const int KingsmarchFundTreasuryLeagueFlag = 1;
    internal const int KingsmarchInvestRecruitmentLeagueFlag = 1434;
    internal const int KingsmarchRecruitmentLeagueFlag = 95;

    // The town panel is drawn from the records the client keeps on its ClientVillage (the ruleset list at +0xcfd8): wire
    // 0x1f with a record's kind builds one from the client's own factory table (kind 0x9b is the town record, 0x5a8 bytes,
    // 0x74 the farm record, 0x2e0 bytes) and the client skips a kind it already has. The value beside the kind is ignored
    // by the factories (client dispatcher 0x142150ae0 case 0x1f -> slot 8 0x1415a6800 -> allocator 0x141763b20).
    //
    // The kinds live in data/kingsmarch-records.txt (one hex kind per line, # comments). No file means no record: the
    // record is created empty, and the client crashed on entering the town with it (2026-11-21 22:37, fault
    // Game.patched.exe+0xdefc9a, seven seconds after the two 0x1f messages), so which kinds go out is a file, not code.
    const string RecordsFile = "data/kingsmarch-records.txt";

    // The village event names sent after each settlement record live in data/kingsmarch-events.txt (one name per line,
    // # comments). The record's event handler (client 0x14176a8e0) answers three names: "village_imgui",
    // "reroll_recruits" and "super_reroll_recruits" (0x14348e980, 0x14348e990, 0x14348e928). No file means no event.
    const string EventsFile = "data/kingsmarch-events.txt";

    // The probe ladder file: one "kind [event]" line per step, 15 seconds apart.
    const string ProbeFile = "data/kingsmarch-probe.txt";

    // The village UI events (wire 0x7b) the client's town panel listens for, in data/kingsmarch-ui.txt: one
    // "kind a b c d" line each, the four u32 fields of the message (the client's screen table 0x142fd89f0 names 0x54
    // Recombinator, 0x55 Recruitment, 0x56 TownMap, 0x57 Mine, 0x58 Port, 0x59 Farm, 0x5a BlackSmith, 0x5b
    // Disenchanting, 0x5c MapRunners, 0x5d Fishing, 0x5e CurrencyExchange; 0x35 carries a VillageJobs row). No file means
    // no UI event. The 0x7b is sent from the tick, not with the area: the client's town panel only exists once the area
    // has started and its window has been built.
    const string UiFile = "data/kingsmarch-ui.txt";

    static byte[] RecordKinds()
    {
        if (!File.Exists(RecordsFile)) return [];
        var kinds = new List<byte>();
        foreach (var line in File.ReadAllLines(RecordsFile))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            if (byte.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var kind)) kinds.Add(kind);
            else Console.WriteLine($"[gateway] kingsmarch record kind '{t}' is not a byte, skipped");
        }
        return kinds.ToArray();
    }

    /// <summary>Probes the client's village path one step at a time. Each line of <see cref="ProbeFile"/> is a record
    /// kind and an optional event name; every step sends the record (the client keeps one of each kind) and the event,
    /// and the steps are 15 seconds apart. The step before the client's disconnect is the one that killed it, so the
    /// file is the bisect: a file with no lines sends nothing.</summary>
    static async Task ProbeLadder(IPlayer player, Progress progress, double clock)
    {
        if (!File.Exists(ProbeFile)) return;
        if (progress.ProbeStep > 0 && clock < progress.ProbeAt) return;
        var lines = File.ReadAllLines(ProbeFile);
        var step = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            step++;
            if (step != progress.ProbeStep) continue;
            var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // A lone "-" is a control step: it sends nothing and only waits, so a crash without any message tells the
            // town apart from the client's own behaviour.
            if (parts[0] != "-")
            {
                if (!byte.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var kind)) continue;
                var name = parts.Length > 1 ? parts[1] : null;
                await player.Send(InstanceProtocol.AddRuleset(kind), $"probe step {step}: 0x001f kind 0x{kind:x}");
                if (name is not null)
                    await player.Send(InstanceProtocol.VillageEvent(kind, name), $"probe step {step}: 0x001e {name} for kind 0x{kind:x}");
                Console.WriteLine($"[gateway] kingsmarch probe step {step} sent: kind 0x{kind:x} event {name ?? "(none)"}");
            }
            else Console.WriteLine($"[gateway] kingsmarch probe step {step} sent: nothing (control step)");
            progress.ProbeStep++;
            progress.ProbeAt = clock + 15;
            return;
        }
    }

    /// <summary>The village UI events (wire 0x7b) sent after the area has started. Each line of <see cref="UiFile"/> is
    /// the kind and the message's four u32 fields.</summary>
    static (byte Kind, uint[] Args)[] UiMessages()
    {
        if (!File.Exists(UiFile)) return [];
        var messages = new List<(byte, uint[])>();
        foreach (var line in File.ReadAllLines(UiFile))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!byte.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var kind)) continue;
            var args = new uint[4];
            for (var i = 0; i < 4 && i + 1 < parts.Length; i++)
                if (!uint.TryParse(parts[i + 1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out args[i])) args[i] = 0;
            messages.Add((kind, args));
        }
        return messages.ToArray();
    }

    /// <summary>Sends the village UI events once per area load. The client's town panel is built with the area, so these
    /// go out from the tick, after the area has started.</summary>
    static async Task EnsureVillageUi(IPlayer player, Progress progress)
    {
        if (progress.Ui) return;
        progress.Ui = true;
        foreach (var (kind, args) in UiMessages())
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
            // three event names, so the names are read from a file: the wrong one leads the client into a UI path that
            // is not set up yet and it crashes on the way in (0x54ee9d8c, 2026-11-21 23:31).
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
        // They are on the wire with the area: the tick must not send them a second time.
        State(player).Records = true;
        return messages;
    }

    /// <summary>The village event names sent after a settlement record, one per line. No file means no event.</summary>
    static string[] EventNames()
    {
        if (!File.Exists(EventsFile)) return [];
        var names = new List<string>();
        foreach (var line in File.ReadAllLines(EventsFile))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            names.Add(t);
        }
        return names.ToArray();
    }

    static async Task EnsureOwnerFlag(IPlayer player)
    {
        // NPCTalk 3581 ("Shipping") is gated twice. The client's row test (0x141e7bb90) reads two flag sets: QuestFlag1..3
        // (16-byte entries) are looked up in the player's set (+0x1b8, 0x02bc bit1), while the unnamed raw-id columns are
        // looked up in the second set (+0x1d0, 0x02bc bit3, the league set) as id-1. Row 3581 wants IsMapOwner (2020) in the
        // first and row 1386 (a7q9-KilledBoss, its __col16) in the second. The server then repeats the check itself:
        // NpcCatalog.Available needs the character's own set to hold the row's QuestFlag1, and it fails silently.
        const int IsMapOwnerRow = 2020, ShippingGateRow = 1386;
        var mod = Program.PlayerOf(player);
        var character = mod.Character;
        // Once per connection, and again after an area load (Entered forgets the counter): the flag sets are projections, so
        // they replace the client's, and the dialogue list is rebuilt per area.
        if (_owner.GetValue(player, _ => new Counter()).Value++ != 0) return;
        // "IsMapOwner" is print-only in QuestFlags, so Quests.SetQuestFlags drops it in CommitQuestFlags and it never reaches
        // the connection's set; add it here, where both the server's dialogue gate and PlayerQuestFlags read it.
        bool added;
        lock (character.QuestFlags) added = character.QuestFlags.Add("IsMapOwner");
        if (added) Console.WriteLine($"[gateway] kingsmarch owner flag: IsMapOwner added to {character.Name}");
        await Game.SetLeagueQuestFlags(mod, [KingsmarchTownOwnerLeagueFlag, ShippingGateRow,
            KingsmarchFundTreasuryLeagueFlag, KingsmarchInvestRecruitmentLeagueFlag, KingsmarchRecruitmentLeagueFlag]);
        var flags = Game.PlayerQuestFlags(mod);
        await player.Send(InstanceEntities.PlayerQuestFlagsUpdate(mod.Id, flags),
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

    static List<TownObject>? _objects;
    static (int X, int Y) _origin = (138, 138);
    static Dictionary<string, (string[] Components, string[] States)>? _types;
    // Per connection: the next object row and id. A connection is one area load, so leaving and coming back starts over.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Progress> _progress = [];
    static readonly object _lock = new();
    // Spawning is opt-in per module load (see Tick): /village_spawn turns it on for this session.
    static bool _armed;

    /// <summary>Lets the next ticks spawn the town's objects (dev: /village_spawn).</summary>
    public static void Arm() => _armed = true;

    /// <summary>An area load: the client holds none of the town's objects yet, so it is built again from the saved farm.</summary>
    public static Task Entered(IPlayer player)
    {
        lock (_lock)
        {
            var progress = _progress.GetOrCreateValue(player);
            progress.Next = 0;
            progress.Plots.Clear();
            // A new area load reads the record kinds again, so a file change takes effect without a restart.
            progress.Records = false;
            progress.Ui = false;
            // The dialogs are listed again after an area load, and the flag projection above is sent once per connection:
            // forget it here so leaving the town and coming back re-sends it instead of needing a relog.
            _owner.Remove(player);
            Console.WriteLine($"[gateway] kingsmarch area load: {player.AreaId} character {player.Character}");
            // The client is still loading the area; objects sent into a loading area are dropped.
            progress.Ready = Environment.TickCount64 / 1000.0 + 3.0;
            Restore(progress, player.Character);
            // A real area load is the one case where sending the town again is safe, so the town needs no dev command.
            // The player's area is not updated yet while the area is being generated, so this cannot test for the town.
            _armed = true;
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
        // The probe ladder: which step is next, and when it may run (seconds, Environment.TickCount64 based).
        public int ProbeStep;
        public double ProbeAt;
        // Ids start at a random-ish base so that a module reload (which starts a new Progress) does not re-send the same
        // ids into a town the client already has: re-creating an id it still knows leaves the old object in place.
        public uint Id = 0x5A000000u + (uint)(Environment.TickCount64 & 0xFF) * 0x100;
        // The farm plots this connection created: a click on one of these ids plants or harvests.
        public readonly Dictionary<uint, Plot> Plots = [];
        // The character whose farm this is, the crops it had stored when the area loaded, and what it has harvested.
        public string Who = "";
        public List<SavedPlot> Saved = [];
        public readonly Dictionary<string, int> Store = [];
        // Before this (seconds, Environment.TickCount64 based) the area is still loading and nothing may be sent.
        public double Ready;
        // Plots removed for a replant, waiting for their re-create (the client's destroy is deferred).
        public readonly List<(double At, Plot Plot, int X, int Y)> Pending = [];
        // Which crop the next planting takes (the plots take turns at the five crops).
        public int Rotation;
        // The workers hired for this character's town; each tends one plot, or sits idle.
        public readonly List<Worker> Workers = [];
        // The town's clickable boards, since the client never builds its Kingsmarch panel in this build.
        public readonly Dictionary<uint, TownBoard> Boards = [];
        // Whether the settlement records (wire 0x1f) have already been sent on this connection.
        public bool Records;
        // Whether the village UI events (wire 0x7b) have already been sent on this connection.
        public bool Ui;
    }

    /// <summary>What a click on one of the town's boards does.</summary>
    enum BoardAction
    {
        Treasury,   // put gold from the character's stash into the town's treasury
        Recruit,    // hire the recruiter's first candidate
        Farm,       // report what the plots hold
    }

    /// <summary>A board standing in the town: the client's own Kingsmarch panel is never built in this build, so the
    /// town's buttons are objects — walk up to the board, click it and the action happens.</summary>
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
    }

    /// <summary>One of the town's six farm plots: what grows there and since when.</summary>
    sealed class Plot
    {
        public string Who = "";    // the character whose farm this plot belongs to
        public uint ObjectId;      // the id the client knows this plot by (it changes on every replant)
        public int Order;          // 0..5, the plot's index in the room
        public byte StateIndex;    // the StateMachine component's index, for its 0x2bc update
        public string[] States = [];   // the type's shared state names, in wire order
        public TownObject Row = null!; // the room row, so the plot can be re-created in another plant state
        public (string[] Components, string[] States) Type;
        public int Crop;           // 0 = bare, 1..5 = the plant state value
        public double PlantedAt;
        // When the client was last sent this plot. A plot is never removed again within a few seconds of being created: the
        // client's destroy is deferred and tearing down an object it has only just built is what corrupted its object map.
        public double CreatedAt;
        public int Harvests;
    }

    /// <summary>A plot as stored in <c>data/kingsmarch-farm.json</c>.</summary>
    sealed class SavedPlot
    {
        public int Crop { get; set; }
        public double Planted { get; set; }
        public int Harvests { get; set; }
    }

    /// <summary>One character's farm: its plots, its workers and what it has harvested.</summary>
    sealed class SavedFarm
    {
        public List<SavedPlot> Plots { get; set; } = [];
        public List<SavedWorker> Workers { get; set; } = [];
        public Dictionary<string, int> Store { get; set; } = [];
    }

    /// <summary>A worker as stored in <c>data/kingsmarch-farm.json</c>.</summary>
    sealed class SavedWorker
    {
        public int Plot { get; set; } = -1;
        public int Harvests { get; set; }
        public string Name { get; set; } = "";
    }

    static readonly Dictionary<string, SavedFarm> _farms = [];

    /// <summary>The character's saved farm, loaded from disk once per module load.</summary>
    static SavedFarm Farm(string who)
    {
        lock (_farms)
        {
            if (_farms.TryGetValue(who, out var farm)) return farm;
            if (File.Exists(FarmFile))
            {
                try
                {
                    var all = JsonSerializer.Deserialize<Dictionary<string, SavedFarm>>(File.ReadAllText(FarmFile));
                    if (all is not null)
                        foreach (var (key, value) in all) _farms[key] = value;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[gateway] kingsmarch: {FarmFile} is unreadable, starting the farm over: {e.Message}");
                }
            }
            if (!_farms.TryGetValue(who, out farm)) _farms[who] = farm = new SavedFarm();
            return farm;
        }
    }

    static void SaveFarm()
    {
        lock (_farms)
        {
            try
            {
                File.WriteAllText(FarmFile, JsonSerializer.Serialize(_farms, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e)
            {
                Console.WriteLine($"[gateway] kingsmarch: cannot write {FarmFile}: {e.Message}");
            }
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
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Counter> _owner = [];

    /// <summary>Creates the town's objects up to <c>data/kingsmarch-limit.txt</c>, a few per tick, on the live connection.</summary>
    public static async Task Tick(IPlayer player, double now)
    {
        // Diagnosis: the first two ticks of a connection say what the module sees.
        if (_diag.GetValue(player, _ => new Counter()).Value++ < 2)
            Console.WriteLine($"[gateway] kingsmarch tick: area '{player.AreaId}' character {player.Character} seed {player.Seed} limit {Limit()} objects {Ordered().Count} cwd {Directory.GetCurrentDirectory()} file {File.Exists(DataFile)} filter {Filter().Length}");
        if (!string.Equals(player.AreaId, Area, StringComparison.Ordinal)) return;
        await EnsureOwnerFlag(player);
        await EnsureSettlementRecords(player, State(player));
        await EnsureVillageUi(player, State(player));
        await ProbeLadder(player, State(player), Environment.TickCount64 / 1000.0);
        // Spawning waits for an explicit /village_spawn: a module reload starts with an empty Progress, and re-sending the
        // town's objects into an area where the client already has them is what killed the client on 2026-11-19 (the
        // client logged ObjectInWakeZone … already in the wake zone and dropped the connection).
        if (!_armed) return;
        _types ??= Types();

        Progress progress;
        lock (_lock)
        {
            progress = _progress.GetOrCreateValue(player);
            if (progress.Who.Length == 0) Restore(progress, player.Character);
        }
        // The tick's own time value is not the clock the rest of this file uses, so wait on Environment.TickCount64 here.
        double clock = Environment.TickCount64 / 1000.0;
        if (clock < progress.Ready) return;
        // The boards are the town's buttons and stand on their own: they are created whatever the object limit says, so a
        // limit of 0 (no town furniture) still leaves a way to fund the treasury, hire a worker and read the farm.
        await Boards(player, progress, clock);
        int limit = Limit();
        if (limit <= 0) return;

        var objects = Ordered();
        await Flush(player, progress, clock);
        await Work(player, progress, clock);

        int created = 0;
        while (progress.Next < objects.Count && progress.Next < limit && created < 20)
        {
            var row = objects[progress.Next++];
            var o = Substitute.TryGetValue(row.Type, out var stand) ? row with { Type = stand } : row;
            var fallback = Fallback();
            if (fallback.Length > 0 && fallback.Any(f => o.Type.EndsWith(f, StringComparison.OrdinalIgnoreCase)))
                o = o with { Type = "Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase" };
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
            await player.Send(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(o.Type),
                    Blob(x, y, o, type, plot ? saved?.Crop ?? 0 : null, plot ? hands : null)),
                $"0x02bb create Kingsmarch object 0x{id:x} {o.Type} at ({x}, {y}) plot {plot} workers {hands}");
            // No 0x1a9: the create blob's object flag (bit 1) already puts the object in the wake zone, and the explicit
            // enter is what the client answers with "ObjectInWakeZone was called … already in the wake zone".
            if (plot)
                lock (_lock) progress.Plots[id] = new Plot
                {
                    Who = progress.Who, ObjectId = id, Order = progress.Plots.Count, StateIndex = (byte)stateIndex, States = type.States,
                    Row = o, Type = type, Crop = saved?.Crop ?? 0, PlantedAt = saved?.Planted ?? 0, Harvests = saved?.Harvests ?? 0,
                    CreatedAt = clock,
                };
            created++;
        }
    }

    /// <summary>The town's clickable boards, on the plaza next to where the player arrives (590, 655): the treasury, the
    /// recruiter and the farm report. They are <c>NoticeboardInteractable</c> — the same components as the farm plots, which
    /// the client has been building and clicking without trouble.</summary>
    static readonly (BoardAction Action, int X, int Y, string Model)[] BoardRows =
    [
        (BoardAction.Treasury, 578, 668, "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/recruitment_noticeboard_01.ao"),
        (BoardAction.Recruit, 602, 668, "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/recruitment_noticeboard_02.ao"),
        (BoardAction.Farm, 590, 680, "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/farming_Interactable01_01.ao"),
    ];

    /// <summary>How much gold one click on the treasury board puts in (the character's stash pays, as with the NPC).</summary>
    const int TreasuryPick = 1000;

    /// <summary>Creates the town's boards — once per area load, and independent of the object limit, since they are the only
    /// way to fund the treasury, hire a worker or read the farm without a chat command. Off unless
    /// <c>data/kingsmarch-boards.txt</c> exists: the client answers a server-made <c>NoticeboardInteractable</c> with
    /// <c>Error executing GEAL … expected a number type</c> and then dies in its object map (fault RVA 0x148b38, 2026-11-19),
    /// so a board needs a type the client builds without its doodad-line data.</summary>
    static async Task Boards(IPlayer player, Progress progress, double clock)
    {
        if (!File.Exists(BoardsFile)) return;
        lock (_lock) if (progress.Boards.Count > 0) return;
        _types ??= Types();
        const string type = "Metadata/Terrain/Leagues/Settlers/HubTown/Objects/NoticeboardInteractable";
        if (!_types.TryGetValue(type, out var shape) || !shape.Components.All(Supported.Contains)) return;
        foreach (var (action, x0, y0, model) in BoardRows)
        {
            var row = new TownObject(type, model, x0, y0, 0f);
            uint id = progress.Id++;
            int x = _origin.X + row.X, y = _origin.Y + row.Y;
            await player.Send(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(type), Blob(x, y, row, shape)),
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

    /// <summary>Loads the character's saved farm into this area load: its crop on each plot and what it has harvested.</summary>
    static void Restore(Progress progress, string? character)
    {
        progress.Who = character ?? "";
        var farm = Farm(progress.Who);
        progress.Saved = farm.Plots;
        foreach (var (crop, count) in farm.Store) progress.Store[crop] = count;
        progress.Workers.Clear();
        foreach (var saved in farm.Workers)
            progress.Workers.Add(new Worker { Order = progress.Workers.Count, Plot = saved.Plot, Harvests = saved.Harvests, Name = saved.Name });
    }

    /// <summary>The objects with the surely-written types first, so a low limit only exercises those.</summary>
    static List<TownObject> Ordered()
    {
        var all = Objects();
        var sure = new List<TownObject>(all.Count);
        var rest = new List<TownObject>();
        foreach (var o in all)
        {
            if (_types is not null && _types.TryGetValue(o.Type, out var t) && t.Components.All(Sure.Contains)) sure.Add(o);
            else rest.Add(o);
        }
        sure.AddRange(rest);
        var badModels = BlockedModels();
        if (badModels.Length > 0) sure = [.. sure.Where(o => !badModels.Any(b => o.Model.Contains(b, StringComparison.OrdinalIgnoreCase)))];
        var blocked = Blocked();
        if (blocked.Length > 0) sure = [.. sure.Where(o => !blocked.Any(b => o.Type.EndsWith(b, StringComparison.OrdinalIgnoreCase)))];
        var filter = Filter();
        // A word with a slash in it is a whole type path (exact); a bare word matches any type path that contains it.
        if (filter.Length > 0)
            sure = [.. sure.Where(o => filter.Any(f => f.Contains('/')
                ? string.Equals(o.Type, f, StringComparison.OrdinalIgnoreCase)
                : o.Type.Contains(f, StringComparison.OrdinalIgnoreCase)))];
        return sure;
    }

    static string[] Fallback()
    {
        try
        {
            if (!File.Exists(FallbackFile)) return [];
            return File.ReadAllText(FallbackFile).Replace(',', ' ').Split(default(char[]), StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException) { return []; }
    }

    static string[] BlockedModels()
    {
        try
        {
            if (!File.Exists(BlockedModelsFile)) return [];
            return File.ReadAllText(BlockedModelsFile).Replace(',', ' ').Split(default(char[]), StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException) { return []; }
    }

    static string[] Blocked()
    {
        try
        {
            if (!File.Exists(BlockedFile)) return [];
            return File.ReadAllText(BlockedFile).Replace(',', ' ').Split(default(char[]), StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException) { return []; }
    }

    static string[] Filter()
    {
        try
        {
            // Without the dev file the town creates only what the client will not: the farm plots, so the farm works out of
            // the box. Every other line of the room is client-side furniture (its doodad pass drops the server_only types,
            // and the client refuses a server-made copy of a type it does not know; see Agents/docs/game/settlers.md).
            if (!File.Exists(FilterFile)) return DefaultFilter;
            var text = File.ReadAllText(FilterFile).Replace(',', ' ');
            var words = text.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries);
            return words;
        }
        catch (IOException) { return []; }
    }

    /// <summary>Dev: data/kingsmarch-animated.txt = "type" makes every Animated component 0 (the type own model).</summary>
    static bool UseTypeModel()
    {
        try
        {
            return File.Exists(AnimatedFile) && File.ReadAllText(AnimatedFile).Trim().Equals("type", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
    }

    static int Limit()
    {
        try
        {
            // No dev file: create every row the filter allows (the six plots).
            return File.Exists(LimitFile) && int.TryParse(File.ReadAllText(LimitFile).Trim(), out int n) ? Math.Clamp(n, 0, 10000) : 1000;
        }
        catch (IOException) { return 1000; }
    }

    static byte[] Blob(int x, int y, TownObject o, (string[] Components, string[] States) type, int? plant = null, int? workers = null)
    {
        var over = Overrides(o.Type);
        bool typeModel = over.TryGetValue("animated", out int animated) ? animated == 0 : UseTypeModel();
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
                    // animated_object is the one the object already is): those get 0, "use the type's own". Dev switch:
                    // data/kingsmarch-animated.txt = "type" forces 0 for every type.
                    if (typeModel) w.U8(0); else w.U8(1).WStr32(o.Model);
                    break;
                case "StateMachine":
                    w.U8((byte)type.States.Length).U8(0);
                    foreach (string s in type.States)
                    {
                        // A state sent with flags 0 is reverted by the client to the type's default; flags 0x02 keep what the
                        // server says. Only the states this file names get that flag, so nothing else changes behaviour.
                        bool said = over.TryGetValue(s, out int value);
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
                // shifts every component after it — InteractionAction, MinimapIcon — and the client quits.
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

    /// <summary>The state overrides for a type path, from <c>data/kingsmarch-states.txt</c> (<c>&lt;type&gt; state=value ...</c>,
    /// matched by the end of the type path). <c>animated=type</c> makes the Animated component 0 for that type.</summary>
    static Dictionary<string, int> Overrides(string type)
    {
        _states ??= States();
        if (_states.TryGetValue(type, out var map)) return map;
        foreach (var (key, value) in _states)
            if (type.EndsWith("/" + key, StringComparison.OrdinalIgnoreCase) || type.Equals(key, StringComparison.OrdinalIgnoreCase))
                return value;
        return [];
    }

    static Dictionary<string, Dictionary<string, int>>? _states;

    static Dictionary<string, Dictionary<string, int>> States()
    {
        var all = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(StatesFile)) return all;
            foreach (string line in File.ReadAllLines(StatesFile))
            {
                string text = line.Trim();
                if (text.Length == 0 || text[0] == '#') continue;
                var parts = text.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < parts.Length; i++)
                {
                    int eq = parts[i].IndexOf('=');
                    if (eq <= 0) continue;
                    string key = parts[i][..eq].Trim();
                    string value = parts[i][(eq + 1)..].Trim();
                    if (value.Equals("type", StringComparison.OrdinalIgnoreCase)) map[key] = 0;
                    else if (int.TryParse(value, out int n)) map[key] = n;
                }
                all[parts[0]] = map;
            }
        }
        catch (IOException) { }
        return all;
    }

    /// <summary>The town's farm crops, by the plot's <c>plant</c> state value (1..5).</summary>
    static readonly string[] Crops = ["Wheat", "Corn", "Pumpkin", "Globulora", "Orbisfera"];

    /// <summary>How long a crop takes to ripen, in seconds.</summary>
    const double GrowSeconds = 60;
    /// <summary>What the town pays a worker for one harvest, from the treasury (Johan's Fund the Treasury).</summary>
    const int Wage = 25;
    // Seconds a plot is left alone after the client was sent it: its destroy is deferred, so removing a fresh plot again is
    // what breaks the client's object map.
    const double Quiet = 6;

    /// <summary>A click on a farm plot (C→S 0x24, skill 0x266): plants a crop on a bare plot, harvests a ripe one and says
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
        double now = Environment.TickCount64 / 1000.0;
        if (plot.Crop == 0)
        {
            // The farm panel (client screen 0x59) cannot be opened in this build, so a click chooses the crop instead: the
            // plots take turns at the five crops, so one character can grow all of them.
            int crop;
            lock (_lock) crop = 1 + progress.Rotation++ % Crops.Length;
            plot.Crop = crop;
            plot.PlantedAt = now;
            await SetPlant(player, progress, objectId, plot);
            await player.Tell($"Plot {plot.Order + 1}: planted {Crops[crop - 1]} (ripe in {GrowSeconds:0} s).");
            Persist(progress);
            return true;
        }
        double ripe = plot.PlantedAt + GrowSeconds;
        if (now < ripe)
        {
            await player.Tell($"{Crops[plot.Crop - 1]} is still growing ({ripe - now:0} s).");
            return true;
        }
        if (now - plot.CreatedAt < Quiet)
        {
            await player.Tell($"Plot {plot.Order + 1} is still settling ({plot.CreatedAt + Quiet - now:0} s).");
            return true;
        }
        string harvested = Crops[plot.Crop - 1];
        plot.Crop = 0;
        plot.Harvests++;
        lock (progress.Store) progress.Store[harvested] = progress.Store.GetValueOrDefault(harvested) + 1;
        await SetPlant(player, progress, objectId, plot);
        await player.Tell($"Plot {plot.Order + 1}: harvested {harvested} ({InStore(progress, harvested)} in the town).");
        Persist(progress);
        return true;
    }

    /// <summary>Writes this connection's farm into data/kingsmarch-farm.json: its plots' crops and what it has harvested.</summary>
    static void Persist(Progress progress)
    {
        var farm = Farm(progress.Who);
        lock (_lock)
        {
            farm.Plots = [.. progress.Plots.Values.OrderBy(p => p.Order)
                .Select(p => new SavedPlot { Crop = p.Crop, Planted = p.PlantedAt, Harvests = p.Harvests })];
            progress.Saved = farm.Plots;
            farm.Workers = [.. progress.Workers.Select(w => new SavedWorker { Plot = w.Plot, Harvests = w.Harvests, Name = w.Name })];
        }
        lock (progress.Store) farm.Store = new(progress.Store);
        SaveFarm();
    }

    static int InStore(Progress progress, string crop)
    {
        lock (progress.Store) return progress.Store.GetValueOrDefault(crop);
    }

    /// <summary>How much of a crop the town is holding — what the port needs to know before it loads a ship.</summary>
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

    /// <summary>Puts a resource straight into the town's store and says how much is there now. The port needs cargo
    /// before it will load a ship, and the farm only makes it once workers exist, so this is the hand-over the
    /// developer commands use to test the harbour on its own.</summary>
    public static int Grant(IPlayer player, string resource, int count)
    {
        if (resource.Length == 0 || count <= 0) return 0;
        var progress = State(player);
        int now;
        lock (progress.Store) progress.Store[resource] = now = progress.Store.GetValueOrDefault(resource) + count;
        Persist(progress);
        return now;
    }

    /// <summary>The town's farm as one report: each plot's crop, how long it needs, who tends it, and what is stored.</summary>
    public static string StoreText(IPlayer player)
    {
        Progress progress;
        progress = State(player);
        double clock = Environment.TickCount64 / 1000.0;
        var lines = new List<string>();
        foreach (var plot in progress.Plots.Values.OrderBy(p => p.Order))
        {
            string what = plot.Crop == 0
                ? "bare"
                : plot.PlantedAt + GrowSeconds > clock ? $"{Crops[plot.Crop - 1]}, ripe in {plot.PlantedAt + GrowSeconds - clock:0} s" : $"{Crops[plot.Crop - 1]}, ready";
            string who = string.Join(",", progress.Workers.Where(w => w.Plot == plot.Order).Select(w => w.Order + 1));
            lines.Add($"plot {plot.Order + 1}: {what}{(who.Length > 0 ? $" (worker {who})" : "")}");
        }
        var idle = progress.Workers.Where(w => w.Plot < 0).Select(w => w.Order + 1).ToList();
        lines.Add(idle.Count == 0 ? "workers: none idle" : $"workers: idle {string.Join(",", idle)}");
        var store = progress.Store.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}").ToList();
        lines.Add("stored: " + (store.Count == 0 ? "nothing yet" : string.Join(", ", store)));
        int owed = KingsmarchPort.Wages(progress.Who);
        lines.Add($"treasury: {KingsmarchPort.Gold(progress.Who)} gold, {Wage} a harvest"
            + (owed > 0 ? $", {owed} owed in wages" : ""));
        lines.Add("commands: /village hire · /village assign <worker> <plot> · /treasury [gold]");
        return string.Join(" | ", lines);
    }

    /// <summary>The town's treasury: the gold it holds, what a harvest pays, and the wages it still owes.</summary>
    public static string TreasuryText(IPlayer player)
    {
        var progress = State(player);
        int owed = KingsmarchPort.Wages(progress.Who);
        return $"{KingsmarchPort.Gold(progress.Who)} gold in the town, {Wage} a harvest"
            + (owed > 0 ? $", {owed} owed in wages" : ", no wages owed")
            + " | /treasury <gold> puts more in (Johan's Fund the Treasury adds a thousand at a time)";
    }

    /// <summary>Johan's "Fund the Treasury": gold moves from the account's stash into the town's treasury, and the wages the
    /// town still owed are settled with it first. The S→C 0x2aa that carries the new total is what the client shows.</summary>
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
        int owed = KingsmarchPort.Wages(progress.Who);
        return $"Funded the treasury with {take} gold: {treasury} gold in the town{(owed > 0 ? $", {owed} still owed in wages" : ", no wages owed")}.";
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
            + $"{RecruitCost} a hire | /recruit hire <1-3>, then /village assign <worker> <plot 1-6>";
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
                    + "(ship crops with /port send <port> <crop> <count>)");
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
    /// then on — planting an empty plot and harvesting a ripe one while the player is elsewhere.</summary>
    public static async Task<string> Assign(IPlayer player, int worker, int plotOrder)
    {
        Progress progress;
        progress = State(player);
        Plot plot;
        lock (_lock)
        {
            if (worker < 1 || worker > progress.Workers.Count) return $"no worker {worker}: the town has {progress.Workers.Count} (hire with /village hire)";
            plot = progress.Plots.Values.FirstOrDefault(p => p.Order == plotOrder - 1)
                ?? throw new InvalidOperationException($"no plot {plotOrder}: the town has {progress.Plots.Count}");
            progress.Workers[worker - 1].Plot = plotOrder - 1;
            Persist(progress);
        }
        await SetWorkers(player, plot, progress.Workers.Count(w => w.Plot == plotOrder - 1));
        return $"Worker {worker} now tends plot {plotOrder}.";
    }

    static async Task SetWorkers(IPlayer player, Plot plot, int count)
    {
        var states = new StateMachineData(new int[plot.States.Length], 0, plot.States).Set("num_workers", count);
        await player.Send(InstanceEntities.StateMachineUpdate(plot.ObjectId, plot.StateIndex, states),
            $"0x02bc Kingsmarch plot {plot.Order + 1} num_workers {count}");
    }

    /// <summary>The assigned workers' round: a ripe plot is harvested into the town's store, an empty one is planted with the
    /// next crop of the rotation, so the farm keeps producing whether the player is standing there or not.</summary>
    static async Task Work(IPlayer player, Progress progress, double clock)
    {
        List<Plot> tended;
        lock (_lock) tended = [.. progress.Plots.Values.Where(p => progress.Workers.Any(w => w.Plot == p.Order)).OrderBy(p => p.Order)];
        foreach (var plot in tended)
        {
            // A plot the client has only just been sent is left alone for a while: removing it again right away is the
            // sequence that corrupts the client's object map (its 0x2bd destroy is deferred).
            if (clock - plot.CreatedAt < Quiet) continue;
            if (plot.Crop == 0)
            {
                int crop;
                lock (_lock) crop = 1 + progress.Rotation++ % Crops.Length;
                plot.Crop = crop;
                plot.PlantedAt = clock;
                await SetPlant(player, progress, plot.ObjectId, plot);
                Persist(progress);
                return;     // one plot a tick: the re-create waits for the client's deferred destroy anyway
            }
            if (clock < plot.PlantedAt + GrowSeconds) continue;
            string harvested = Crops[plot.Crop - 1];
            plot.Harvests++;
            lock (progress.Store) progress.Store[harvested] = progress.Store.GetValueOrDefault(harvested) + 1;
            // Harvested and sown with the next crop in one re-create: a bare step in between would mean two removals of the
            // same plot seconds apart, which is what killed the client on 2026-11-19 19:51 (fault 0x1ee74f3).
            int next;
            lock (_lock) next = 1 + progress.Rotation++ % Crops.Length;
            plot.Crop = next;
            plot.PlantedAt = clock;
            await SetPlant(player, progress, plot.ObjectId, plot);
            Persist(progress);
            // A harvest is what the town pays its worker for. An empty treasury does not stop the work: the wage is owed
            // and settled by the next funding (Johan's Fund the Treasury), so the farm never stalls on gold.
            bool paid = KingsmarchPort.PayWages(progress.Who, Wage);
            await player.Tell($"A worker harvested {harvested} on plot {plot.Order + 1} and sowed {Crops[next - 1]} ({InStore(progress, harvested)} in the town)."
                + (paid ? "" : $" ({Wage} gold of wages is owed: fund the treasury with Johan.)"));
            return;
        }
    }

    static async Task SetPlant(IPlayer player, Progress progress, uint objectId, Plot plot)
    {
        // The plot's crop is the model the client builds in its on_or_create_state_plant_<n> event, so a live plot changes
        // crop by being re-created in the new state (a 0x2bc update alone leaves the old model; tried 2026-11-19 17:38).
        // The new object needs a fresh id too: re-creating the same id leaves the old, bare instance in place (17:50).
        // Removing and creating in the same batch is what corrupted the client's object map (its 0x2bd destroy is
        // deferred, so the new object reusing the old block while that block is still queued breaks its tree lookup), so
        // the create waits for a later tick: see D:\大肥鱼工作区\scratch\object-lifecycle-report.md.
        var states = new StateMachineData(new int[plot.States.Length], 0, plot.States).Set("plant", plot.Crop);
        await player.Send(InstanceEntities.StateMachineUpdate(objectId, plot.StateIndex, states),
            $"0x02bc Kingsmarch plot 0x{objectId:x} plant {plot.Crop}");
        var row = plot.Row;
        int x = _origin.X + row.X, y = _origin.Y + row.Y;
        await player.Send(InstanceProtocol.RemoveObject(objectId), $"0x02bd Kingsmarch plot 0x{objectId:x} removed to replant");
        lock (_lock)
        {
            progress.Plots.Remove(objectId);
            progress.Pending.Add((Environment.TickCount64 / 1000.0 + 1.0, plot, x, y));
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
                plot.ObjectId = id;
                plot.CreatedAt = now;
                progress.Plots[id] = plot;
            }
            int hands = progress.Workers.Count(w => w.Plot == plot.Order);
            await player.Send(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(plot.Row.Type),
                    Blob(x, y, plot.Row, plot.Type, plot.Crop, hands)),
                $"0x02bb create Kingsmarch plot 0x{id:x} {plot.Row.Type} at ({x}, {y}) plant {plot.Crop} workers {hands}");
        }
    }

    static List<TownObject> Objects()
    {
        if (_objects is not null) return _objects;
        _objects = [];
        if (!File.Exists(DataFile)) return _objects;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(DataFile));
            var root = doc.RootElement;
            if (root.TryGetProperty("roomX", out var rx) && root.TryGetProperty("roomY", out var ry))
                _origin = (rx.GetInt32(), ry.GetInt32());
            foreach (var e in root.GetProperty("objects").EnumerateArray())
                _objects.Add(new TownObject(
                    e.GetProperty("type").GetString() ?? "",
                    e.GetProperty("model").GetString() ?? "",
                    e.GetProperty("x").GetInt32(),
                    e.GetProperty("y").GetInt32(),
                    (float)e.GetProperty("angle").GetDouble()));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Console.WriteLine($"[gateway] {DataFile}: {ex.Message}");
        }
        return _objects;
    }

    static Dictionary<string, (string[], string[])> Types()
    {
        var result = new Dictionary<string, (string[], string[])>(StringComparer.Ordinal);
        const string file = "data/object-types.json";
        if (!File.Exists(file)) return result;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            foreach (var t in doc.RootElement.GetProperty("types").EnumerateArray())
            {
                string type = t.GetProperty("type").GetString() ?? "";
                // The town also places world objects of other families (Stash, GuildStash, Waypoint, the Village NPCs);
                // they are real object types in the same table, so keep every one and let the component check decide.
                int read = t.GetProperty("read").GetInt32();
                result[type] =
                (
                    [.. t.GetProperty("components").EnumerateArray().Take(read).Select(c => c.GetString() ?? "")],
                    [.. t.GetProperty("shared_states").EnumerateArray().Select(s => s[0].GetString() ?? "")]
                );
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Console.WriteLine($"[gateway] {file}: {ex.Message}");
        }
        return result;
    }
}
