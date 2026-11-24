using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using GameServer.Modules.Village.Mapping;

namespace GameServer.Modules.Village;

/// <summary>
/// The Kingsmarch village (Settlers of Kalguur): the settlement the panels are drawn from, and the action channel those
/// panels send.
///
/// The client keeps one record per ruleset kind on its <c>ClientVillage</c> (list at <c>+0xcfd8</c>); kind <c>0x9b</c> is the
/// town record (<c>0x5a8</c> bytes) and is built by S→C <c>0x1f</c> (u8 kind, u32 value) from the client's factory table. The
/// panels are HUD member windows opened by S→C <c>0x7b</c> kinds (screen table <c>0x142fd89f0</c>: <c>0x54</c> recombinator,
/// <c>0x55</c> recruitment, <c>0x56</c> town map, <c>0x57</c> mine, <c>0x58</c> port, <c>0x59</c> farm, <c>0x5a</c> blacksmith,
/// <c>0x5b</c> disenchanting, <c>0x5c</c> map runners, <c>0x5d</c> fishing, <c>0x5e</c> currency exchange; each is a HUD member
/// window, so the client always has it and <c>0x142169dd0</c> just Shows it),
/// and every button they show is sent as C→S <c>0x2ca</c>: <c>u16 id | u8 kind | compact-int length | blob</c>, where the blob
/// is the action byte and its arguments.
/// </summary>
public sealed class VillageModule : IModule
{
    /// <summary>C→S 0x2ca: what a Kingsmarch panel sends when a button is pressed.</summary>
    public const ushort ActionId = InstanceRulesets.ActionId;

    /// <summary>The ruleset record kind of the town (the client's <c>ClientVillage</c>): 0x5a8 bytes, created by S→C 0x1f.
    /// Kind <c>0x74</c> was tried as "the farm record" and is not one: the client answers it with the
    /// "ExpeditionPlugin not found!" dialog, so nothing sends it.</summary>
    public const byte TownRecordKind = 0x9b;

    /// <summary>The village's own state: what the treasury holds and who works where. The panel draws these numbers from
    /// the settlement record the client keeps; the server owns them.</summary>
    public sealed class Settlement
    {
        /// <summary>The treasury (the panel's "Treasury Gold").</summary>
        public uint Gold;
        /// <summary>How many of the population have a job (the panel's "Population n/90").</summary>
        public byte Workers;
        /// <summary>How many the village can house (the client's VillageBaseStats row 20728: 90).</summary>
        public byte Capacity = 90;
        /// <summary>Worker index → VillageJobs row.</summary>
        public readonly Dictionary<byte, byte> Assignments = [];
        /// <summary>Worker index → skill level (the client's job level, the worker's rank).</summary>
        public readonly Dictionary<byte, byte> Ranks = [];
        /// <summary>The recruits waiting to be hired (the recruitment panel): the <c>VillageJobs</c> row they would take and
        /// their skill level.</summary>
        public readonly List<(byte Row, byte Skill)> Recruits = [];
        /// <summary>What the panel has been told about the recruits; the list is pushed again when it changes.</summary>
        public int RecruitVersion;
        /// <summary>What the panel has been told about the recruits already.</summary>
        public int RecruitsPushed = -1;
        /// <summary>The <c>VillageUpgradeCategories</c> rows this settlement has bought.</summary>
        public readonly HashSet<int> Upgrades = [];
        /// <summary>Workers told to stop mapping after the map they are in (the town map's pause button).</summary>
        public readonly HashSet<byte> Paused = [];
        /// <summary>What the panel has been told, as a counter: the rows are pushed again when it changes.</summary>
        public int Version;
        /// <summary>What the panel has been told already.</summary>
        public int Pushed;
        /// <summary>What the panel has been told about the settlement's numbers. -1 so the first tick pushes them.</summary>
        public int StatePushed = -1;
        /// <summary>The VillageResources rows the town holds (the panel's resource counters).</summary>
        public readonly Dictionary<int, uint> Resources = [];
        /// <summary>One "VillageResources" row per farm plot, six of them, in plot order: 10..14 for the five crops and
        /// 0xff for a bare plot. The client's label resolver reads these bytes directly and faults on row 0, so a bare
        /// plot must name a row that does not exist rather than row zero.</summary>
        public readonly byte[] Slots = [0xff, 0xff, 0xff, 0xff, 0xff, 0xff];
        /// <summary>The fifteen production counters the state frame carries: <c>VillageProduction</c> rows Mining1..5,
        /// Smelting1..5, Farming1..5 in that order. The panel's site numbers come from here, so a worker is counted into
        /// the counter of the job it holds (the farm's sixth plot folds into Farming5).</summary>
        public readonly byte[] Counters = new byte[15];
    }

    readonly ConditionalWeakTable<IPlayer, Settlement> _settlements = new();

    /// <summary>What the panel's action byte means. The names come from the client's own action lambdas; the ones marked
    /// "live" were read off a real click.</summary>
    static readonly Dictionary<byte, string> Actions = new()
    {
        [0x00] = "reroll recruits (u8 tier)",
        [0x03] = "assign worker (u8 worker, u8 VillageJobs row)",
        [0x04] = "assign farm (u8 worker, u8 VillageResources row)",
        [0x05] = "perform upgrade (u8, VillageUpgrades row)",
        [0x06] = "send shipment (u8 slot, u8 port, u8 failure, 16 u32)",
        [0x07] = "abandon shipment (u8 slot)",
        [0x0a] = "abandon captured shipment",
        [0x0b] = "reroll port quota (u8 VillageShippingPorts row)",
        [0x12] = "pause mapping after this map (u8, u8)",
        [0x13] = "take reward (3 u32)",
        [0x14] = "take reward to location (6 u32)",
        [0x15] = "take reward to socket (5 u32)",
        [0x16] = "take all rewards (u32)",
        [0x18] = "give treasury gold (u32)",
        [0x19] = "take treasury gold (u32)",
        // The client also builds these, from other serializer clusters that share the 0x2ca helper (0x1421a1960): the
        // action byte on its own is ambiguous, so the dispatch key is (record kind, action). The names are the client's
        // own shapes; what each one does belongs to the panel that sent it.
        [0x01] = "hire worker (u8 candidate index)",
        [0x02] = "release worker (u8 row index)",
        [0x0c] = "set option (u8)",
        [0x0d] = "set option (u8)",
        [0x0e] = "set option (u8, u8)",
        [0x0f] = "confirm",
        [0x10] = "confirm",
        [0x11] = "confirm",
        [0x17] = "confirm",
        [0x1b] = "set option (u8)",
        [0x1c] = "set option (u8)",
        [0x1d] = "set option (u8)",
    };

    /// <summary>The village of a player in the league.</summary>
    public Settlement Of(IPlayer player) => _settlements.GetValue(player, _ => new Settlement());

    /// <summary>Where the farm module writes the town's numbers (its gold, its workers and their jobs, its crops). The
    /// farm lives in the Game module and the panel is pushed from this one, so the numbers cross in a small file: a file
    /// rather than a reference, so neither module has to load the other's assembly.</summary>
    public const string FarmFile = "data/kingsmarch-village.json";

    /// <summary>What the farm file said last, per connection: the settlement is only pushed again when it changes.</summary>
    static readonly ConditionalWeakTable<IPlayer, string> _farm = new();

    /// <summary>Copies the farm's numbers into the settlement the panel is drawn from.</summary>
    /// <param name="player">The player whose panel is open.</param>
    void SyncFarm(IPlayer player)
    {
        var file = new FileInfo(FarmFile);
        if (!file.Exists) return;
        string text;
        try
        {
            text = File.ReadAllText(FarmFile);
        }
        catch (IOException)
        {
            // A writer in the middle of replacing it: the next tick reads it again.
            return;
        }
        if (_farm.TryGetValue(player, out string? seen) && seen == text) return;
        _farm.AddOrUpdate(player, text);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException e)
        {
            Console.WriteLine($"[gateway] kingsmarch village file is not JSON: {e.Message}");
            return;
        }
        using (doc)
        {
            var root = doc.RootElement;
            var settlement = Of(player);
            int version = settlement.Version;
            if (root.TryGetProperty("gold", out var gold)) settlement.Gold = (uint)Math.Max(0, gold.GetInt64());
            if (root.TryGetProperty("capacity", out var capacity)) settlement.Capacity = (byte)capacity.GetInt32();
            settlement.Assignments.Clear();
            settlement.Ranks.Clear();
            if (root.TryGetProperty("workers", out var workers))
            {
                foreach (var worker in workers.EnumerateArray())
                {
                    int row = worker.GetProperty("row").GetInt32();
                    // A worker with no job is VillageJobs row 0 (its JobType is 0, idling): the panel lists it under
                    // the idle column rather than dropping it, so the town's population is everyone who was hired.
                    byte order = (byte)worker.GetProperty("order").GetInt32();
                    settlement.Assignments[order] = (byte)row;
                    settlement.Ranks[order] = (byte)Math.Clamp(worker.GetProperty("rank").GetInt32() - 1, 0, 4);
                }
            }
            settlement.Workers = (byte)settlement.Assignments.Count;
            // The site numbers on the map icons: a worker counts into the counter of the job it holds.
            Array.Clear(settlement.Counters);
            foreach (var job in settlement.Assignments.Values)
            {
                if (job is >= 1 and <= 10) settlement.Counters[job - 1]++;
                else if (job is >= 11) settlement.Counters[10 + Math.Min(4, job - 11)]++;
            }
            // The panel's crop counters are VillageResources rows 10..14, in the order the farm grows them.
            string[] crops = ["Wheat", "Corn", "Pumpkin", "Globulora", "Orbisfera"];
            if (root.TryGetProperty("crops", out var store))
            {
                for (int i = 0; i < crops.Length; i++)
                    settlement.Resources[10 + i] = store.TryGetProperty(crops[i], out var have) ? (uint)Math.Max(0, have.GetInt32()) : 0;
            }
            // The mine and smeltery lines read VillageResources rows 5..9 (the ores) and 15..19 (the bars); the store
            // file carries both under the same bag as the crops.
            string[] ores = ["IronOre", "CopperOre", "Amber", "BismuthOre", "VerisiumOre"];
            string[] bars = ["RefinedIron", "RefinedCopper", "RefinedAmber", "RefinedBismuth", "RefinedVerisium"];
            if (root.TryGetProperty("crops", out var mined))
            {
                for (int i = 0; i < ores.Length; i++)
                {
                    settlement.Resources[5 + i] = mined.TryGetProperty(ores[i], out var ore) ? (uint)Math.Max(0, ore.GetInt32()) : 0;
                    settlement.Resources[15 + i] = mined.TryGetProperty(bars[i], out var bar) ? (uint)Math.Max(0, bar.GetInt32()) : 0;
                }
                settlement.Resources[20] = mined.TryGetProperty("MagicDust", out var dust) ? (uint)Math.Max(0, dust.GetInt32()) : 0;
            }
            // The six plot slots the client names on the map icons: a plot growing crop n is row 9 + n, a bare one is
            // 0xff (out of range, which the client treats as "no row" instead of dereferencing row 0).
            Array.Fill(settlement.Slots, (byte)0xff);
            if (root.TryGetProperty("plots", out var plots))
            {
                foreach (var plot in plots.EnumerateArray())
                {
                    int order = plot.GetProperty("order").GetInt32();
                    int crop = plot.GetProperty("crop").GetInt32();
                    if (order is < 0 or > 5) continue;
                    if (crop is < 1 or > 5) continue;
                    settlement.Slots[order] = (byte)(9 + crop);
                }
            }
            // The pushes compare this against what the panel already has.
            settlement.Version = version + 1;
            Console.WriteLine($"[gateway] village sync: {settlement.Assignments.Count} worker(s), gold {settlement.Gold}, "
                + $"slots [{string.Join(' ', settlement.Slots)}], version {settlement.Version}");
        }
    }

    /// <summary>The states the client has already been sent, per connection: the file's stamp and what went out.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IPlayer, string> _sent = new();

    /// <summary>Where the candidate state frames are written for a live test: one <c>kind subtag hex</c> line per frame,
    /// built from the settlement the server actually holds. Copy a line into <see cref="StateFile"/> to send just that one
    /// frame: the client's reader does not resynchronise, so a frame it cannot parse takes the connection with it.</summary>
    public const string FramesFile = "data/kingsmarch-frames.txt";

    /// <summary>Where the state frames to push are written, one per line: <c>kind subtag hex</c>. Rewriting the file
    /// re-sends them on the next tick, so a payload can be tried against the live client without a rebuild.</summary>
    public const string StateFile = "data/kingsmarch-state.txt";

    /// <summary>The file that tells the town there are recruits waiting (the recruitment panel's slots stay grey
    /// without it).</summary>
    public const string RecruitsFile = "data/kingsmarch-recruits.txt";

    /// <summary>What hiring a waiting recruit costs: one hour of the wage the recruit would earn, which is the number
    /// the card shows (its u32 at <c>+0xa8</c>). The panel weighs the button against the treasury's gold and the client
    /// never moves gold itself, so the server charges it. A flat 2,000 sat far above the gold a town actually runs on,
    /// which is why every hire was refused.</summary>
    public static uint Price(byte row, byte skill) => VillageWages.Wage(VillageWorkers.JobType(row), skill);

    /// <summary>Put a file here and the server opens the port window itself, the stand-in for clicking Tujen's Shipping
    /// line: a test client reaches the port panel without a hand on the mouse. Once per connection, in the town.</summary>
    public const string OpenFile = "data/kingsmarch-open-port.txt";

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IPlayer, string> _opened = new();


    static async Task OpenPort(IPlayer player)
    {
        if (player.AreaId != "KalguuranSettlersLeague") return;
        var file = new FileInfo(OpenFile);
        if (!file.Exists)
        {
            _opened.Remove(player);
            return;
        }

        // The file's text is the screen kind to open (0x56 the town map, 0x58 the port); editing or saving it again
        // opens the window once more, so a window that caches its numbers when it is built can be rebuilt on demand.
        string stamp = $"{file.LastWriteTimeUtc.Ticks}/{file.Length}";
        if (_opened.TryGetValue(player, out string? opened) && opened == stamp) return;
        _opened.AddOrUpdate(player, stamp);
        byte kind = 0x58;
        string text = (await File.ReadAllTextAsync(OpenFile)).Trim();
        if (text.Length > 0 && byte.TryParse(text.StartsWith("0x") ? text[2..] : text,
                System.Globalization.NumberStyles.HexNumber, null, out byte asked)) kind = asked;
        await player.Send(InstanceWaypoints.OpenUi(kind), $"0x007b 0x{kind:x2} open (dev: {OpenFile})");
    }

    /// <param name="player">A player in the league.</param>
    /// <param name="now">The tick's time.</param>
    static async Task StatePoll(IPlayer player, double now)
    {
        if (player.AreaId != "KalguuranSettlersLeague") return;
        var file = new FileInfo(StateFile);
        if (!file.Exists) return;
        string stamp = $"{file.LastWriteTimeUtc.Ticks}/{file.Length}";
        if (_sent.TryGetValue(player, out string? sent) && sent == stamp) return;
        _sent.AddOrUpdate(player, stamp);
        foreach (string raw in await File.ReadAllLinesAsync(StateFile))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            byte kind = Convert.ToByte(parts[0], 16), subtag = Convert.ToByte(parts[1], 16);
            byte[] payload = Convert.FromHexString(parts[2]);
            await player.Send(StateFrame(kind, subtag, payload),
                $"0x02c9 village state kind 0x{kind:x2} subtag {subtag}: {Convert.ToHexString(payload)}");
        }
        if (!File.Exists(FramesFile)) await WriteFrames(player);
    }

    /// <summary>Tells the panel who works in the town: the whole list at once (S→C 0x2c9, kind 0x9b, subtag 0), which is
    /// the client's <c>0x141767f60</c> switch: subtag 0 takes a count and then that many row payloads and REPLACES the
    /// record's row list (it grows, shrinks or overwrites it at <c>+0x160</c>), while subtag 5 only appends one row
    /// (so pushing a row per worker again and again would pile up copies of the same worker). The rows go out again
    /// whenever the settlement changes, so the panel's population and job list follow the server's own town.</summary>
    /// <param name="player">The player whose panel is open.</param>
    async Task PushWorkers(IPlayer player)
    {
        if (player.AreaId != "KalguuranSettlersLeague") return;
        var settlement = Of(player);
        if (settlement.Version == 0 || settlement.Pushed == settlement.Version) return;
        settlement.Pushed = settlement.Version;
        var rows = settlement.Assignments.OrderBy(pair => pair.Key)
            .Select(pair => VillageWorkers.Row(pair.Value, pair.Key, settlement.Ranks.GetValueOrDefault(pair.Key)))
            .ToList();
        var payload = new List<byte> { (byte)rows.Count };
        foreach (var row in rows) payload.AddRange(row);
        await player.Send(StateFrame(TownRecordKind, 0, payload.ToArray()),
            $"0x02c9 village workers: {rows.Count} row(s), subtag 0 replaces the list");
    }

    /// <summary>Tells the panel who is waiting to be hired (S→C 0x2c9, kind 0x9b, subtag 1). The client's reader
    /// 0x141773300 takes a count and then that many recruits; each recruit is the worker's row plus a u32, so the
    /// candidate cards get the name, the job, the wage and the skill level the server chose.</summary>
    /// <param name="player">The player whose recruits are shown.</param>
    async Task PushRecruits(IPlayer player)
    {
        if (player.AreaId != "KalguuranSettlersLeague") return;
        var settlement = Of(player);
        // A town always has somebody waiting: the roll cannot be gated on the switch file, or the panel's cards
        // stay empty and every hire click answers "no recruit in slot" (the file only gates a reroll).
        if (settlement.Recruits.Count == 0 && settlement.RecruitVersion == 0) RollRecruits(settlement);
        if (settlement.RecruitsPushed == settlement.RecruitVersion) return;
        settlement.RecruitsPushed = settlement.RecruitVersion;
        // the count is read twice: once by the handler (0x141767fc1) and once by the reader (0x141773338)
        var payload = new List<byte> { (byte)settlement.Recruits.Count, (byte)settlement.Recruits.Count };
        for (int i = 0; i < settlement.Recruits.Count; i++)
            payload.AddRange(VillageWorkers.Recruit(settlement.Recruits[i].Row, i, settlement.Recruits[i].Skill,
                Price(settlement.Recruits[i].Row, settlement.Recruits[i].Skill)));
        await player.Send(StateFrame(TownRecordKind, 1, payload.ToArray()),
            $"0x02c9 village recruits: {settlement.Recruits.Count}");
    }

    /// <summary>Tells the panel the settlement's numbers: the treasury, the resource and production counters, the six
    /// plot slots and the village clock (S→C 0x2c9, kind 0x9b, subtag 4). Its reader 0x1424c8190 takes exactly 0xa6
    /// (166) bytes and returns: 21 u32 (resource counters) → village+0x1a0, 15 u32 (production counters) → +0x1f4, a u32
    /// → +0x198, a u32 → +0x19c, six u8 (one VillageResources row per farm plot) → +0x230, then a u64 that is consumed
    /// but not stored — the time the frame was made in Unix seconds, which the caller subtracts from its own clock
    /// (0x1417683e1) and adds to the village's. Anything after 0xa6 is invisible, and a wrong time there pushes the
    /// clock into the past.</summary>
    async Task PushState(IPlayer player)
    {
        if (player.AreaId != "KalguuranSettlersLeague") return;
        var settlement = Of(player);
        if (settlement.StatePushed == settlement.Version) return;
        settlement.StatePushed = settlement.Version;
        long clock = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = new List<byte>(166);
        for (int i = 0; i < 21; i++)
            payload.AddRange(BitConverter.GetBytes(settlement.Resources.GetValueOrDefault(i)));
        for (int j = 0; j < 15; j++) payload.AddRange(BitConverter.GetBytes((uint)settlement.Counters[j]));
        payload.AddRange(BitConverter.GetBytes(settlement.Gold));
        // The u32 after the treasury sits at +0x198 and its meaning is unknown. Writing the wage total in it crashed the
        // client (fault 0x162869f, inside the shipping failure roll), so it stays 0.
        payload.AddRange(BitConverter.GetBytes(0u));
        // The six plot slots the map icons name: a plot growing crop n is VillageResources row 9 + n, a bare plot is
        // 0xff. Row 0 has no name object and the client's label resolver does not check, so a zero slot faults at 0x8.
        payload.AddRange(settlement.Slots);
        payload.AddRange(BitConverter.GetBytes((ulong)clock));
        // The reader stops at 0xa6, but the frame's declared length has to cover what the client's other readers walk
        // over: a 166-byte frame made it raise "Buffer Underflow", so the unused tail stays as zeros.
        payload.AddRange(new byte[245 - payload.Count]);
        await player.Send(StateFrame(TownRecordKind, 4, payload.ToArray()),
            $"0x02c9 village state: gold {settlement.Gold}, slots [{string.Join(' ', settlement.Slots)}]");
    }

    /// <summary>Puts a worker in the town and tells the panel about it: the dev stand-in for the recruitment panel's hire
    /// button, which the panel itself sends as action 0x00 and 0x03.</summary>
    /// <param name="player">The player whose town it is.</param>
    /// <param name="jobRow">The <c>VillageJobs</c> row the worker takes (1 is "Mining Crimson Iron").</param>
    public async Task<string> Hire(IPlayer player, int jobRow, int rank = 1)
    {
        var settlement = Of(player);
        int hired = KingsmarchTown.AddWorker(player);
        if (hired < 1) return "the town could not take another worker";
        byte worker = (byte)hired;
        settlement.Assignments[worker] = (byte)jobRow;
        settlement.Ranks[worker] = VillageWages.Skill(rank);
        settlement.Workers = (byte)settlement.Assignments.Count;
        settlement.Version++;
        await PushWorkers(player);
        return $"worker {worker} takes VillageJobs row {jobRow} at rank {rank} ({settlement.Workers}/{settlement.Capacity})";
    }


    /// <summary>S→C 0x2c9, the village's state channel (the counterpart of 0x2ca): the client's reader 0x141fa7ca0 takes
    /// <c>u16 id | u8 kind | compact-int length | blob</c>, keeps the kind at +0x90 and hands the blob to ClientVillage
    /// (vtable slot 8, 0x141767f60), which reads one subtag byte and its payload. One frame carries one subtag.</summary>
    static byte[] StateFrame(byte kind, byte subtag, ReadOnlySpan<byte> payload)
    {
        var blob = new List<byte>(payload.Length + 1) { subtag };
        blob.AddRange(payload.ToArray());
        var frame = new List<byte>(blob.Count + 5) { 0x02, 0xc9, kind };
        int v = blob.Count;
        if (v < 0x80) frame.Add((byte)v);
        else if (v < 0x4000) { frame.Add((byte)(0x80 | (v >> 8))); frame.Add((byte)v); }
        else { frame.Add((byte)(0xc0 | (v >> 16))); frame.Add((byte)(v >> 8)); frame.Add((byte)v); }
        frame.AddRange(blob);
        return frame.ToArray();
    }

    /// <summary>The frames the town window would need, built from the settlement the server holds. The ship frames are
    /// exact (subtag 2: slot, port row, failure row, then the 16 cargo counts the panel's own send action carries); the
    /// port frames (subtag 9) are the same six numbers the pin's quota update takes, whose meaning is still unknown, so
    /// they are written out for a one-frame-at-a-time live test rather than pushed.</summary>
    static async Task WriteFrames(IPlayer player)
    {
        var lines = new List<string>
        {
            "# kind subtag hex — one frame per line; copy ONE line into " + StateFile + " and save",
        };
        var ports = KingsmarchPort.PortList();
        lines.Add($"# {ports.Count} ports");
        lines.Add($"9b 01 {ports.Count:x2}");
        foreach (var port in ports)
        {
            // Zeroes first, on purpose. The frame is only safe once the client has built the port window's pin board
            // ([ClientVillage+0x5c8], filled when KalguurPortWindow is opened): pushing a quota before that walks into a
            // NULL and takes the client down with c0000005 at 0x1402D1029 (WER event 1001, 2026-11-22 11:49:49).
            var six = new List<byte> { (byte)port.Row, 6 };
            for (int i = 0; i < 6; i++) six.AddRange(BitConverter.GetBytes(0u)[..4]);
            lines.Add($"9b 09 {Convert.ToHexString(six.ToArray())}");
        }
        var who = player.Character ?? "";
        foreach (var ship in KingsmarchPort.ShipList(who))
        {
            var frame = new List<byte> { (byte)ship.Slot, (byte)(ship.PortRow < 0 ? 0xff : ship.PortRow),
                (byte)(ship.FailureRow < 0 ? 0xff : ship.FailureRow) };
            foreach (int count in ship.Cargo) frame.AddRange(BitConverter.GetBytes((uint)count)[..4]);
            lines.Add($"9b 02 {Convert.ToHexString(frame.ToArray())}");
        }
        var vault = KingsmarchPort.Vault(who);
        lines.Add($"# crew {vault.Crew}, barter {vault.Barter}, gold {vault.Gold}");
        try { await File.WriteAllLinesAsync(FramesFile, lines); }
        catch (IOException ex) { Console.WriteLine($"[gateway] {FramesFile}: {ex.Message}"); }
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        VillageWorkers.LoadGameData();
        MappingCommands.Register(module.Manifest.Id);
        GameEvents.CommandsBuilding.Add(module.Manifest.Id, (commands, connection) =>
        {
            var player = Program.PlayerOf(connection);
            return commands.Add("villagepanel", "/villagepanel hire <jobRow> [rank] (the town panel's workers)", async args =>
            {
                if (args.Length >= 2 && string.Equals(args[0], "hire", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(args[1], out int row))
                {
                    int rank = args.Length >= 3 && int.TryParse(args[2], out int given) ? given : 1;
                    await player.Tell("Kingsmarch: " + await Hire(player, row, rank));
                    return;
                }
                if (args.Length >= 2 && string.Equals(args[0], "hirepanel", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(args[1], out int slot))
                {
                    // the recruitment panel's own hire button, without a hand on the mouse: same handler the 0x2ca
                    // action 0x01 reaches
                    await player.Tell("Kingsmarch: " + await HireRecruit(player, slot));
                    return;
                }
                if (args.Length >= 1 && string.Equals(args[0], "resend", StringComparison.OrdinalIgnoreCase))
                {
                    // the tester's way of making the panel take the file again: forget what was sent, read the farm
                    // file afresh and push every frame of the settlement
                    _farm.Remove(player);
                    var fresh = Of(player);
                    fresh.Pushed = -1;
                    fresh.RecruitsPushed = -1;
                    fresh.StatePushed = -1;
                    SyncFarm(player);
                    fresh = Of(player);
                    fresh.Pushed = -1;
                    fresh.RecruitsPushed = -1;
                    fresh.StatePushed = -1;
                    await PushWorkers(player);
                    await PushRecruits(player);
                    await PushState(player);
                    await player.Tell($"Kingsmarch: resent the panel (population {fresh.Workers}, gold {fresh.Gold}, "
                        + $"slots [{string.Join(' ', fresh.Slots)}])");
                    return;
                }
                if (args.Length >= 2 && string.Equals(args[0], "gold", StringComparison.OrdinalIgnoreCase)
                    && uint.TryParse(args[1], out uint gold))
                {
                    var settlement = Of(player);
                    settlement.Gold = gold;
                    settlement.Version++;
                    await PushState(player);
                    await player.Tell("Kingsmarch: the treasury holds " + gold);
                    return;
                }
                if (args.Length >= 2 && string.Equals(args[0], "purse", StringComparison.OrdinalIgnoreCase)
                    && uint.TryParse(args[1], out uint purse))
                {
                    // the tester's way of getting gold to try the panel's buttons with
                    var stash = global::Game.PersonalStashOf(Program.PlayerOf(player));
                    uint total = stash.AddGold(purse);
                    await player.Send(InstanceEntities.Gold(total), $"0x02aa gold: {total}");
                    await player.Tell($"Kingsmarch: the purse holds {total}");
                    return;
                }
                await player.Tell("usage: /villagepanel hire <VillageJobs row> [rank 1-5] | hirepanel <slot> | gold <amount> | purse <amount>");
            });
        });
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always(async (connection, now) =>
        {
            var player = Program.PlayerOf(connection);
            SyncFarm(player);
            await OpenPort(player);
            await StatePoll(player, now);
            await PushWorkers(player);
            await PushRecruits(player);
            await PushState(player);
            MappingEngine.Advance(player.Character ?? "", now);
        }));
        GameEvents.Packet(ActionId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            // The wire is u16 id | u8 ruleset row | compact-int length | blob (the client's writer 0x141fa7c10); the row
            // is the record the panel belongs to (0x9b the town, 0x74 the farm) and the blob's first byte is the action.
            byte kind = 0;
            byte[] blob = [];
            try
            {
                (kind, blob) = InstanceRulesets.ReadAction(bytes);
            }
            catch (InvalidDataException)
            {
                Console.WriteLine($"[gateway] 0x02ca village action: a cut-off message ({bytes.Length} bytes)");
                return true;
            }
            var action = blob.Length > 0 ? blob[0] : (byte)0;
            var named = Actions.GetValueOrDefault(action, "unknown action");
            var settlement = Of(Program.PlayerOf(conn));
            var args = blob.Length > 1 ? blob[1..] : [];

            // The panel's buttons are the settlement's own: the treasury buttons change the gold, the job list assigns a
            // worker. Each is applied to the village the server owns.
            var player = Program.PlayerOf(conn);
            var who = player.Character ?? "";
            var note = action switch
            {
                0x18 => await Give(settlement, Amount(args), player),
                0x19 => await Take(settlement, Amount(args), player),
                0x03 => Assign(settlement, args, player),
                0x02 => Unassign(settlement, args, player),
                0x06 => Sail(player, args),
                0x07 => args.Length == 0 ? "abandon (no slot)" : KingsmarchPort.DropShip(who, args[0]) ? $"ship {args[0]} abandoned" : $"no ship in slot {args[0]}",
                0x00 => await RerollRecruits(player, args.Length > 0 ? args[0] : 0),
                0x01 => await HireRecruit(player, args.Length > 0 ? args[0] : 0),
                0x04 => Farm(player, args),
                0x05 => Upgrade(settlement, args),
                0x0a => "abandon captured shipment: none is captured here",
                0x0b => Reroll(who, args),
                0x12 => Pause(settlement, args),
                0x13 or 0x14 or 0x15 => Collect(who, action, args),
                0x16 => Collect(who, action, args),
                _ => null,
            };

            Console.WriteLine($"[gateway] 0x02ca village action 0x{action:x2} {named}, kind 0x{kind:x2}, " +
                $"blob {Convert.ToHexString(blob)}{(note is null ? "" : ", " + note)}");
            // A refused button has to say why: the panel only shows the numbers it is given, so a hire that the treasury
            // cannot pay for would otherwise look like a button that does nothing at all.
            if (note is not null) await player.Tell($"Kingsmarch: {note}");
            await PushWorkers(player);
            await PushRecruits(player);
            await PushState(player);
            return true;
        });
    }

    /// <summary><c>VillageSharedConstants.json</c>: what one quota reroll costs.</summary>
    const int QuotaRerollCost = 15000;

    /// <summary>The gold an action carries. The panels write little-endian, like every other number on the wire.</summary>
    static uint Amount(ReadOnlySpan<byte> args) => args.Length switch
    {
        >= 4 => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(args),
        >= 2 => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(args),
        1 => args[0],
        _ => 0,
    };

    /// <summary>The port panel's send button: <c>u8 slot, u8 port row, u8 failure row, 16 × u32</c> cargo counts indexed
    /// by <c>VillageExports.json</c> row, exactly the array a dock slot is filled with on the way back.</summary>
    static string Sail(IPlayer player, ReadOnlySpan<byte> args)
    {
        if (args.Length < 3 + 16 * 4) return $"send shipment (short: {args.Length} bytes)";
        var cargo = new int[16];
        for (int i = 0; i < 16; i++)
            cargo[i] = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(args[(3 + i * 4)..]);
        string said = KingsmarchPort.SendCargo(player, args[1], cargo, out int value, out int crew);
        return $"send shipment slot {args[0]} port row {args[1]}: {said} (value {value}, crew {crew})";
    }

    /// <summary>The panel's ADD: the player puts gold into the treasury. The gold leaves the account's stash, so the
    /// treasury cannot be filled out of nothing; the client is told the new total with the same 0x02aa the account's
    /// gold is announced with.</summary>
    static async Task<string> Give(Settlement settlement, uint amount, IPlayer player)
    {
        var stash = global::Game.PersonalStashOf(Program.PlayerOf(player));
        uint take = (uint)Math.Min(amount, stash.Gold);
        if (take == 0) return $"the account has no gold to put into the treasury ({stash.Gold} held)";
        uint? left = stash.TrySpendGold(take);
        if (left is null) return $"the account has {stash.Gold} gold, less than the {take} asked for";
        settlement.Gold += take;
        settlement.Version++;
        await player.Send(InstanceEntities.Gold(left.Value), $"0x02aa gold: {left} (put {take} into the treasury)");
        return $"treasury +{take} (gold now {settlement.Gold})";
    }

    /// <summary>The panel's REMOVE: the player takes gold out of the treasury, and it goes back into the account.</summary>
    static async Task<string> Take(Settlement settlement, uint amount, IPlayer player)
    {
        uint taken = (uint)Math.Min(amount, settlement.Gold);
        if (taken == 0) return "the treasury is empty";
        settlement.Gold -= taken;
        settlement.Version++;
        var stash = global::Game.PersonalStashOf(Program.PlayerOf(player));
        uint gold = stash.AddGold(taken);
        await player.Send(InstanceEntities.Gold(gold), $"0x02aa gold: {gold} (took {taken} out of the treasury)");
        return $"treasury -{taken} (gold now {settlement.Gold})";
    }

    /// <summary>The recruitment panel's hire button: the tier of the slot the player picked.</summary>
    /// <summary>The recruitment panel's reroll button: a new set of recruits, paid for out of the player's purse. The
    /// client shows the price itself (its <c>VillageRecruitBalancePerTier</c> holds 2,000 gold a reroll), and the button
    /// does nothing on its own: the server owns who is waiting to be hired.</summary>
    /// <param name="player">The player who is rerolling.</param>
    async Task<string> RerollRecruits(IPlayer player, int tier = 0)
    {
        var settlement = Of(player);
        var stash = global::Game.PersonalStashOf(Program.PlayerOf(player));
        // the panel has two buttons: the plain reroll and the "high quality" one, which costs more and brings better
        // recruits (the client's tier 1 sender is 0x140510400)
        bool high = tier != 0;
        int cost = high ? 16000 : 2000;
        int lowest = high ? 2 : 0;              // a high quality reroll never brings a recruit below rank 3
        if (stash.Gold < cost) return $"reroll: {cost} gold needed, {stash.Gold} on hand";
        uint? left = stash.TrySpendGold(cost);
        RollRecruits(settlement, lowest);
        settlement.RecruitVersion++;
        await PushRecruits(player);
        await player.Send(InstanceEntities.Gold(left!.Value), $"0x02aa gold: {left.Value} (paid {cost} for a reroll)");
        return $"reroll: {settlement.Recruits.Count} new recruits for {cost} gold";
    }

    /// <summary>The recruitment panel's hire button: the client sends the candidate's own index (the card's
    /// <c>+0x440</c>), and the town takes the recruit into service. The price is paid out of the treasury, because
    /// that is the gold the client weighs the button against (<c>0x14050fd8f</c> reads the treasury's
    /// <c>+0x198</c>); the client never moves gold itself, so the server takes it and tells the panel the new
    /// numbers.</summary>
    /// <param name="player">The player whose town it is.</param>
    /// <param name="index">The candidate's slot in the waiting list.</param>
    async Task<string> HireRecruit(IPlayer player, int index)
    {
        var settlement = Of(player);
        // The waiting list is normally rolled by the tick that pushes it to the panel; a click that arrives before that
        // tick has to see somebody waiting, or the panel refuses a hire it was showing cards for.
        if (settlement.Recruits.Count == 0) RollRecruits(settlement);
        if (index < 0 || index >= settlement.Recruits.Count)
            return $"no recruit in slot {index} ({settlement.Recruits.Count} waiting)";
        uint price = Price(settlement.Recruits[index].Row, settlement.Recruits[index].Skill);
        // The treasury the town really holds lives in the farm, and this settlement's copy is rewritten from it every
        // tick, so the charge has to go through the farm. When the town's purse is short the player's own gold covers the
        // rest: the panel offers the hire, so pressing it has to hire.
        var (paid, paidFrom) = await KingsmarchTown.PayTownPurchase(player, (int)price);
        if (!paid) return $"hiring needs {price} gold: {paidFrom}";

        var (row, skill) = settlement.Recruits[index];
        settlement.Recruits.RemoveAt(index);
        settlement.RecruitVersion++;
        settlement.Gold = (uint)Math.Max(0, KingsmarchTown.Treasury(player));
        // The worker has to join the farm's own list as well: the panel is rewritten from it every tick, so a worker
        // that only exists in this settlement's copy vanishes on the next tick.
        int hired = KingsmarchTown.AddWorker(player, skill);
        if (hired < 1) return "hiring failed: the town could not take another worker";
        byte worker = (byte)hired;
        // A new hire joins the idle list, not a job: the recruitment panel hires, the farm panel assigns. VillageJobs row
        // 0 is the idling row, which is the column the "available workers" list reads.
        await KingsmarchTown.MoveTo(player, worker, 0);
        settlement.Assignments[worker] = 0;
        settlement.Ranks[worker] = skill;
        settlement.Workers = (byte)settlement.Assignments.Count;
        settlement.Version++;
        await PushWorkers(player);
        await PushRecruits(player);
        await PushState(player);
        return $"hired recruit {index} as worker {worker} at rank " +
            $"{VillageWages.Rank(skill)} for {price} gold ({paidFrom}); assign {worker} on the farm panel";
    }

    /// <summary>Picks the two recruits waiting to be hired: a job that has a name to show and a skill level.</summary>
    /// <param name="settlement">The settlement the recruits belong to.</param>
    static void RollRecruits(Settlement settlement, int lowest = 0)
    {
        settlement.Recruits.Clear();
        // the jobs that have a name to show: the five mining and the five smelting ones, plus disenchanting and mapping
        int[] jobs = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18];
        int top = Math.Max(VillageWages.Ranks, lowest + 1);
        for (int i = 0; i < 2; i++)
            settlement.Recruits.Add(((byte)jobs[Random.Shared.Next(jobs.Length)],
                (byte)Random.Shared.Next(lowest, top)));
    }

    static string Recruit(IPlayer player, ReadOnlySpan<byte> args)
    {
        int index = args.Length > 0 ? args[0] + 1 : 1;
        string said = KingsmarchTown.Recruit(player, index).GetAwaiter().GetResult();
        return $"recruit (slot {index}): {said}";
    }

    /// <summary>The farm panel's assign button: which worker tends which plot. The panel names the plot by its row in the
    /// crop table, and the town's plots are numbered from 1, so the row is used as the plot's order.</summary>
    static string Farm(IPlayer player, ReadOnlySpan<byte> args)
    {
        if (args.Length < 2) return "assign farm (no arguments)";
        int worker = args[0] + 1, plot = args[1] + 1;
        string said = KingsmarchTown.AssignIdle(player, args[0], plot).GetAwaiter().GetResult();
        return $"assign farm (worker {worker} → plot {plot}): {said}";
    }

    /// <summary>The town map's upgrade button: which <c>VillageUpgradeCategories</c> row was bought.</summary>
    static string Upgrade(Settlement settlement, ReadOnlySpan<byte> args)
    {
        if (args.Length < 2) return "perform upgrade (no arguments)";
        int row = args[1];
        bool first = settlement.Upgrades.Add(row);
        return first ? $"upgrade row {row} bought" : $"upgrade row {row} was already bought";
    }

    /// <summary>The port panel's quota reroll: one port, paid out of the treasury.</summary>
    static string Reroll(string who, ReadOnlySpan<byte> args)
    {
        if (args.Length < 1) return "reroll quota (no port)";
        string said = KingsmarchPort.RerollQuota(who, args[0], QuotaRerollCost, out int[] quota);
        return $"{said} (quota {string.Join('/', quota)})";
    }

    /// <summary>The town map's pause button: stop mapping after the current map.</summary>
    static string Pause(Settlement settlement, ReadOnlySpan<byte> args)
    {
        if (args.Length < 2) return "pause mapping (no arguments)";
        byte worker = args[0];
        bool paused = args[1] != 0;
        if (paused) settlement.Paused.Add(worker);
        else settlement.Paused.Remove(worker);
        return $"worker {worker} {(paused ? "pauses" : "resumes")} mapping after this map";
    }

    /// <summary>The port panel's collect button: one shipment by index, or every one of them for <c>0x16</c>.</summary>
    static string Collect(string who, byte action, ReadOnlySpan<byte> args)
    {
        if (action == 0x16) return $"collected every shipment: {KingsmarchPort.TakeRewards(who, -1)} barter";
        if (args.Length < 4) return "collect reward (no index)";
        int index = (int)Amount(args);
        int taken = KingsmarchPort.TakeRewards(who, index);
        return taken == 0 ? $"no shipment waiting at index {index}" : $"collected shipment {index}: {taken} barter";
    }

    /// <summary>A worker takes a job (the panel's job list) or leaves it. The panel's two buttons are one action: the
    /// job row it names, and the worker it means. Row 0 is "Idling", so it is a worker leaving its job; any other row is
    /// a worker taking it. The worker is one of the town's own, so the town does not grow: the worker changes the job
    /// and the panel's worker list is pushed again.</summary>
    static string Assign(Settlement settlement, ReadOnlySpan<byte> args, IPlayer player)
    {
        if (args.Length < 2) return "assign worker (no arguments)";
        byte worker = args[0], row = args[1];
        // The panel numbers the workers of the column it shows: the "Current Workers" column holds them all, in the
        // order they were pushed, and the "Available Workers" column holds the ones without a job.
        var order = settlement.Assignments.OrderBy(pair => pair.Key).ToList();
        if (row == 0)
        {
            // The panel's REMOVE button: the worker leaves its job and goes back to the available ones.
        if (order.Count == 0) return "remove worker: the town has no workers";
        byte at = (byte)Math.Min((int)worker, order.Count - 1);
            settlement.Assignments[order[at].Key] = 0;
            _ = KingsmarchTown.MoveTo(player, order[at].Key, 0);
            settlement.Workers = (byte)settlement.Assignments.Count;
            settlement.Version++;
            return $"worker {order[at].Key} back to idle ({settlement.Workers}/{settlement.Capacity})";
        }
        // The panel's ASSIGN button: one of the available workers takes the job.
        var idle = order.Where(pair => pair.Value == 0).ToList();
        // The index is a place in the available column, and that column shrinks as workers are placed, so it is
        // clamped rather than rejecting whoever is last -that was the worker nobody could ever place.
        if (idle.Count == 0) return "assign worker: every worker already has a job";
        var pick = idle[Math.Min((int)worker, idle.Count - 1)];
        settlement.Assignments[pick.Key] = row;
        settlement.Workers = (byte)settlement.Assignments.Count;
        settlement.Version++;
        KingsmarchTown.MoveTo(player, pick.Key, row).GetAwaiter().GetResult();
        return $"worker {pick.Key} on VillageJobs row {row} ({settlement.Workers}/{settlement.Capacity})";
    }

    /// <summary>The panel's release button (the small icon on a worker): the worker leaves the town for good, so the
    /// worker list shrinks and the panel is told about it.</summary>
    /// <summary>The red button under a worker in the farm window: the worker leaves its job and goes back to the
    /// available list. It is not a dismissal -the town keeps the worker and so does the farm, so only the job is cleared.
    /// Treating it as a dismissal made the panel and the farm drift apart by one worker every time.</summary>
    static string Unassign(Settlement settlement, ReadOnlySpan<byte> args, IPlayer player)
    {
        byte index = args.Length > 0 ? args[0] : (byte)0;
        var order = settlement.Assignments.OrderBy(pair => pair.Key).ToList();
        if (order.Count == 0) return "remove worker: the town has no workers";
        var pick = order[Math.Min((int)index, order.Count - 1)];
        settlement.Assignments[pick.Key] = 0;
        settlement.Version++;
        KingsmarchTown.MoveTo(player, pick.Key, 0).GetAwaiter().GetResult();
        return $"worker {pick.Key} back to idle ({settlement.Assignments.Count(pair => pair.Value == 0)} available)";
    }
    static string Release(Settlement settlement, ReadOnlySpan<byte> args, IPlayer player)
    {
        byte index = args.Length > 0 ? args[0] : (byte)0;
        var order = settlement.Assignments.OrderBy(pair => pair.Key).ToList();
        if (order.Count <= index) return $"release worker {index}: no worker to release";
        byte key = order[index].Key;
        settlement.Assignments.Remove(key);
        KingsmarchTown.Fire(player, index + 1);
        settlement.Ranks.Remove(key);
        settlement.Workers = (byte)settlement.Assignments.Count;
        settlement.Version++;
        return $"worker {key} released ({settlement.Workers}/{settlement.Capacity})";
    }
}
