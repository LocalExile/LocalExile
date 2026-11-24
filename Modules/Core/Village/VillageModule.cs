using System.Runtime.CompilerServices;
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
    /// The farm record is 0x74.</summary>
    public const byte TownRecordKind = 0x9b, FarmRecordKind = 0x74;

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
        /// <summary>What the town has bought on top of <c>VillageBaseStats</c>, by stat row (none yet: the upgrade table
        /// is not in the client's data).</summary>
        public readonly Dictionary<int, int> Stats = [];
        /// <summary>The work each <c>VillageProduction</c> row has done on the unit it is making, 0 to 1. The line's own
        /// <c>BaseProductionTime</c> turns it into the production counter the panel reads.</summary>
        public readonly double[] Progress = new double[15];
        /// <summary>Unix seconds the town was last advanced to. The town's works run on the wall clock, not the area's
        /// clock: they keep going while the character is in a map, or offline (up to <see cref="VillageProduction.MaxStep"/>),
        /// which is the whole point of hiring miners.</summary>
        public long WallAt;
        /// <summary>The wall clock when the state was last pushed: the panels are told once a second, not once a frame.</summary>
        public long StateAt = long.MinValue;
        /// <summary>Gold the mined ore has earned since the last push, waiting to be paid into the treasury: the harbour
        /// writes its save on every change, so a unit-by-unit payment would write it dozens of times a minute.</summary>
        public uint OreGold;
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

    /// <summary>The village of a player in the league, restored from the character's stored town the first time it is asked
    /// for.</summary>
    public Settlement Of(IPlayer player) => _settlements.GetValue(player, _ => Restore(player));

    /// <summary>
    /// The settlement's stored half: what the town holds and who works where. The treasury and the harbour have their own
    /// file (<see cref="KingsmarchPort"/>); this one keeps the ore, the bars, the work in progress and the workers, so a
    /// town does not forget its miners at a relog or a restart.
    /// </summary>
    public sealed class SavedVillage
    {
        public Dictionary<byte, byte> Assignments { get; set; } = [];
        public Dictionary<byte, byte> Ranks { get; set; } = [];
        public Dictionary<int, uint> Resources { get; set; } = [];
        public double[] Progress { get; set; } = new double[VillageProduction.Counters];
        public List<int> Upgrades { get; set; } = [];
        /// <summary>Unix seconds when the town was last written: the town keeps working while nobody is in it (up to
        /// <see cref="VillageProduction.MaxStep"/>), so the restore advances it by the gap this stamp leaves behind.</summary>
        public long LastSeen { get; set; }
    }

    /// <summary>Where the settlement is kept, relative to the repository root (the mapping line keeps its own the same way).
    /// </summary>
    public const string SaveFile = "data/kingsmarch-village.json";

    static readonly object _saveLock = new();
    static readonly Dictionary<string, SavedVillage> _saved = new(StringComparer.Ordinal);
    static bool _loaded;

    static Settlement Restore(IPlayer player)
    {
        var settlement = new Settlement();
        string who = player.Character ?? "";
        if (who.Length == 0) return settlement;
        var saved = Stored(who);
        foreach (var (worker, job) in saved.Assignments) if (job != 0) settlement.Assignments[worker] = job;
        foreach (var (worker, rank) in saved.Ranks) settlement.Ranks[worker] = rank;
        foreach (var (row, count) in saved.Resources) settlement.Resources[row] = count;
        for (int i = 0; i < settlement.Progress.Length && i < saved.Progress.Length; i++)
            settlement.Progress[i] = saved.Progress[i];
        foreach (int row in saved.Upgrades) settlement.Upgrades.Add(row);
        settlement.Workers = (byte)settlement.Assignments.Count;
        // The first tick advances the town from this stamp, so time spent offline is worked (bounded by MaxStep in
        // Advance) instead of being lost with the session.
        settlement.WallAt = saved.LastSeen;
        // The treasure is the harbour's own number: read it so the panel's gold and the town's spending agree at once.
        settlement.Gold = (uint)Math.Clamp(KingsmarchPort.Gold(who), 0, uint.MaxValue);
        return settlement;
    }

    /// <summary>The character's stored town, read from disk once per process; a file that cannot be read leaves the town
    /// empty rather than failing the load.</summary>
    static SavedVillage Stored(string who)
    {
        lock (_saveLock)
        {
            LoadSaves();
            if (_saved.TryGetValue(who, out var saved)) return saved;
            return _saved[who] = new SavedVillage();
        }
    }

    static void LoadSaves()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(SaveFile)) return;
            var all = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, SavedVillage>>(File.ReadAllBytes(SaveFile));
            if (all is not null) foreach (var (key, value) in all) _saved[key] = value;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[gateway] {SaveFile}: {ex.Message}");
        }
    }

    /// <summary>Writes the character's town back: to a temporary file first, so a crash mid-write cannot leave a
    /// half-written save behind.</summary>
    static void SaveVillage(string who, Settlement settlement)
    {
        if (who.Length == 0) return;
        lock (_saveLock)
        {
            var saved = new SavedVillage
            {
                Assignments = new(settlement.Assignments),
                Ranks = new(settlement.Ranks),
                Resources = new(settlement.Resources),
                Progress = (double[])settlement.Progress.Clone(),
                Upgrades = [.. settlement.Upgrades],
                LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            _saved[who] = saved;
            try
            {
                string? folder = Path.GetDirectoryName(SaveFile);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                string temp = SaveFile + ".tmp";
                File.WriteAllBytes(temp, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_saved,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, SaveFile, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[gateway] {SaveFile}: {ex.Message}");
            }
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

    /// <summary>What hiring one of the waiting recruits costs. The client weighs the button against the treasury's gold
    /// (its own <c>VillageRecruitBalancePerTier</c> holds 2,000 gold a tier), so the price is paid out of the
    /// treasury; the client never moves gold itself, the server does.</summary>
    public const uint RecruitPrice = 2000;

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
        if (settlement.Recruits.Count == 0 && settlement.RecruitVersion == 0 && File.Exists(RecruitsFile)) RollRecruits(settlement);
        if (settlement.RecruitsPushed == settlement.RecruitVersion) return;
        settlement.RecruitsPushed = settlement.RecruitVersion;
        // the count is read twice: once by the handler (0x141767fc1) and once by the reader (0x141773338)
        var payload = new List<byte> { (byte)settlement.Recruits.Count, (byte)settlement.Recruits.Count };
        for (int i = 0; i < settlement.Recruits.Count; i++)
            payload.AddRange(VillageWorkers.Recruit(settlement.Recruits[i].Row, i, settlement.Recruits[i].Skill, RecruitPrice));
        await player.Send(StateFrame(TownRecordKind, 1, payload.ToArray()),
            $"0x02c9 village recruits: {settlement.Recruits.Count}");
    }

    /// <summary>Tells the panel the settlement's numbers: the treasury, the resource and production counters and the
    /// village clock (S→C 0x2c9, kind 0x9b, subtag 4). The client's reader 0x1424c8190 takes, in this order: 21 u32 (the
    /// resource counters), 15 u32 (the production counters), the gold, one more u32, six flag bytes, three u64, four
    /// entries (a u64, three bytes and the name they carry), a u64, two bytes and the village's clock; the handler then
    /// reads one more u64 and adds (its now − that value) to the clock, so both have to be the client's own time in whole
    /// seconds — zero or milliseconds push the clock into the future and the client kills itself with its own "advancing
    /// village state to a time in the past" complaint. A frame one byte short aborts the client with 0xc000041d, so the
    /// layout has to be exact.</summary>
    async Task PushState(IPlayer player)
    {
        if (player.AreaId != "KalguuranSettlersLeague") return;
        var settlement = Of(player);
        if (settlement.StatePushed == settlement.Version) return;
        settlement.StatePushed = settlement.Version;
        long clock = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // The block the client reads, in the order its reader 0x1424c8190 takes it: 21 resource counters, 15 production
        // counters, the gold, one more number, six farm-adjacency markers, three pairs, three named blocks (a u64, the
        // length of the name, two bytes, the name), a u64, two packed bytes, the village's clock and four packed bytes.
        var payload = new List<byte>(245);
        for (int i = 0; i < 21; i++)
            payload.AddRange(BitConverter.GetBytes(settlement.Resources.GetValueOrDefault(i)));
        // The production counters: the work the town's lines have done on the unit they are making, in hundredths of
        // that unit's own BaseProductionTime (the client's 0x1424c5b80 divides the remaining work by the crew's
        // efficiency). A panel that reads them draws its "Time" and "Production Rate"; zeros mean nothing is running.
        for (int j = 0; j < 15; j++)
            payload.AddRange(BitConverter.GetBytes(VillageProduction.Counter(settlement, j)));
        payload.AddRange(BitConverter.GetBytes(settlement.Gold));
        payload.AddRange(BitConverter.GetBytes(0u));
        payload.AddRange(new byte[6]);
        for (int k = 0; k < 3; k++)
        {
            payload.AddRange(BitConverter.GetBytes(0u));
            payload.AddRange(BitConverter.GetBytes(0u));
        }
        for (int e = 0; e < 3; e++)
        {
            payload.AddRange(BitConverter.GetBytes((ulong)0));
            payload.Add(0);
            payload.Add(0);
            payload.Add(0);
        }
        payload.AddRange(BitConverter.GetBytes((ulong)0));
        payload.Add(0);
        payload.Add(0);
        payload.AddRange(BitConverter.GetBytes((ulong)clock));
        payload.AddRange(new byte[4]);
        // The handler reads one more u64 after the block: the time the frame was made, which it subtracts from its own
        // clock and adds to the village's. Both have to be the client's own time in whole seconds.
        payload.AddRange(BitConverter.GetBytes((ulong)clock));
        await player.Send(StateFrame(TownRecordKind, 4, payload.ToArray()),
            $"0x02c9 village state: gold {settlement.Gold}, 21 resources, 15 production counters");
    }

    /// <summary>
    /// Runs the town's mining and smelting lines for the game time since the last tick, and keeps the treasury's gold in
    /// step (<c>village_gold_per_ore_mined</c> a mined unit). The counters the panels draw are the record's own, so the
    /// settlement is marked changed while a line moves — but the panels are told once a second rather than once a frame:
    /// the client re-simulates its own copy between pushes anyway (<c>0x1424c4550</c>), and a frame a frame would be one
    /// state message every 17 ms.
    /// </summary>
    /// <param name="player">A player in the town.</param>
    /// <param name="now">The area time of this tick, in game seconds.</param>
    async Task RunProduction(IPlayer player, double now)
    {
        if (player.AreaId != "KalguuranSettlersLeague") return;
        var settlement = Of(player);
        // The town's works run on the wall clock, and on the area's own frame delta they would stop the moment the
        // character walked into a map. Advance() bounds one step, so a week offline stops at MaxStep.
        long stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        double elapsed = settlement.WallAt <= 0 ? 0 : stamp - settlement.WallAt;
        settlement.WallAt = stamp;
        if (elapsed <= 0) return;
        var moved = VillageProduction.Advance(settlement, elapsed);
        settlement.OreGold += moved.Gold;
        // Once a second is enough for both the save and the panel: the client re-simulates its own copy between pushes
        // (F-108), and a save per frame would write the file every 17 ms.
        if (stamp - settlement.StateAt < 1) return;
        settlement.StateAt = stamp;
        SaveVillage(player.Character ?? "", settlement);
        if (settlement.OreGold > 0)
        {
            // The ore's own gold (village_gold_per_ore_mined a unit): it goes into the town's treasury, which is the same
            // purse the wages, the shipments and the recruiting draw on, so the panel and the payouts cannot drift apart.
            long treasury = KingsmarchPort.Fund(player.Character ?? "", settlement.OreGold);
            settlement.OreGold = 0;
            settlement.Gold = (uint)Math.Clamp(treasury, 0, uint.MaxValue);
        }
        if (moved.Units > 0 || moved.Progressed) settlement.Version++;
    }

    /// <summary>Puts claimed ore into the town's "Ore Marked" counters and tells the panel: what a map's ore deposit does
    /// once the player has finished its encounter (<see cref="KingsmarchOre"/>).</summary>
    /// <param name="player">The player whose town it is.</param>
    /// <param name="rank">The ore's rank, 1 (Crimson Iron) to 5 (Verisium).</param>
    /// <param name="amount">How many units the deposit was worth.</param>
    public async Task MarkOre(IPlayer player, int rank, int amount)
    {
        var settlement = Of(player);
        uint marked = VillageProduction.AddPendingOre(settlement, rank, amount);
        settlement.Version++;
        SaveVillage(player.Character ?? "", settlement);
        if (player.AreaId == "KalguuranSettlersLeague") await PushState(player);
        Console.WriteLine($"[gateway] kingsmarch: rank {rank} ore +{amount} marked ({marked} waiting)");
    }

    /// <summary>Puts a worker in the town and tells the panel about it: the dev stand-in for the recruitment panel's hire
    /// button, which the panel itself sends as action 0x00 and 0x03.</summary>
    /// <param name="player">The player whose town it is.</param>
    /// <param name="jobRow">The <c>VillageJobs</c> row the worker takes (1 is "Mining Crimson Iron").</param>
    public async Task<string> Hire(IPlayer player, int jobRow, int rank = 1)
    {
        var settlement = Of(player);
        byte worker = (byte)(settlement.Assignments.Count + 1);
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
        VillageProduction.LoadGameData();
        KingsmarchOre.Install(module.Manifest.Id, MarkOre);
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
                if (args.Length >= 2 && string.Equals(args[0], "ore", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(args[1], out int oreRank))
                {
                    int amount = args.Length >= 3 && int.TryParse(args[2], out int given) ? given : 100;
                    var settlement = Of(player);
                    uint marked = VillageProduction.AddPendingOre(settlement, oreRank, amount);
                    settlement.Version++;
                    await PushState(player);
                    await player.Tell($"Kingsmarch: {amount} ore of rank {oreRank} marked ({marked} waiting)");
                    return;
                }
                await player.Tell("usage: /villagepanel hire <VillageJobs row> [rank] | /villagepanel ore <rank 1-5> [amount] | /villagepanel gold <amount> | /villagepanel purse <amount>");
            });
        });
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always(async (connection, now) =>
        {
            var player = Program.PlayerOf(connection);
            await OpenPort(player);
            await StatePoll(player, now);
            await RunProduction(player, now);
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
                0x03 => Assign(settlement, args),
                0x02 => Release(settlement, args),
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
        // The treasury is the harbour object's own gold (the same purse the wages, the shipments and the rerolls draw on),
        // so the panel, the payout and the spending all see one number instead of two that drift apart.
        long treasury = KingsmarchPort.Fund(player.Character ?? "", take);
        settlement.Gold = (uint)Math.Clamp(treasury, 0, uint.MaxValue);
        settlement.Version++;
        // Johan's "Quest Complete" row waits for this flag (NPCTalk __col16 1436).
        await global::Game.SetLeagueQuestFlags(Program.PlayerOf(player), [KingsmarchDialogue.GoldAdded]);
        await player.Send(InstanceEntities.Gold(left.Value), $"0x02aa gold: {left} (put {take} into the treasury)");
        return $"treasury +{take} (gold now {settlement.Gold})";
    }

    /// <summary>The panel's REMOVE: the player takes gold out of the treasury, and it goes back into the account.</summary>
    static async Task<string> Take(Settlement settlement, uint amount, IPlayer player)
    {
        uint taken = (uint)Math.Min(amount, settlement.Gold);
        if (taken == 0) return "the treasury is empty";
        if (!KingsmarchPort.Spend(player.Character ?? "", (int)Math.Min(taken, int.MaxValue)))
            return $"the treasury holds {KingsmarchPort.Gold(player.Character ?? "")} gold, less than the {taken} asked for";
        settlement.Gold = (uint)Math.Clamp(KingsmarchPort.Gold(player.Character ?? ""), 0, uint.MaxValue);
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
        int lowest = high ? 3 : 1;              // a high quality reroll never brings a recruit below rank 3
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
        if (index < 0 || index >= settlement.Recruits.Count)
            return $"no recruit in slot {index} ({settlement.Recruits.Count} waiting)";
        if (settlement.Gold < RecruitPrice)
            return $"hiring needs {RecruitPrice} gold in the treasury ({settlement.Gold} there)";
        if (!KingsmarchPort.Spend(player.Character ?? "", (int)RecruitPrice))
            return $"hiring needs {RecruitPrice} gold in the treasury ({KingsmarchPort.Gold(player.Character ?? "")} there)";

        var (row, skill) = settlement.Recruits[index];
        settlement.Recruits.RemoveAt(index);
        settlement.RecruitVersion++;
        settlement.Gold = (uint)Math.Clamp(KingsmarchPort.Gold(player.Character ?? ""), 0, uint.MaxValue);
        byte worker = (byte)(settlement.Assignments.Count + 1);
        settlement.Assignments[worker] = row;
        settlement.Ranks[worker] = skill;
        settlement.Workers = (byte)settlement.Assignments.Count;
        settlement.Version++;
        // Johan's "Invest in Recruitment" row waits for this flag (NPCTalk __col16 1434).
        await global::Game.SetLeagueQuestFlags(Program.PlayerOf(player), [KingsmarchDialogue.RecruitHired]);
        await PushWorkers(player);
        await PushRecruits(player);
        await PushState(player);
        return $"hired recruit {index}: worker {worker} takes VillageJobs row {row} at rank " +
            $"{VillageWages.Rank(skill)} for {RecruitPrice} gold (treasury {settlement.Gold})";
    }

    /// <summary>Picks the two recruits waiting to be hired: a job that has a name to show and a skill level.</summary>
    /// <param name="settlement">The settlement the recruits belong to.</param>
    static void RollRecruits(Settlement settlement, int lowest = 1)
    {
        settlement.Recruits.Clear();
        // the jobs that have a name to show: the five mining and the five smelting ones, plus disenchanting and mapping
        int[] jobs = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 17, 18];
        int top = Math.Max(VillageWages.Ranks, lowest) + 1;
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
        string said = KingsmarchTown.Assign(player, worker, plot).GetAwaiter().GetResult();
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
    static string Assign(Settlement settlement, ReadOnlySpan<byte> args)
    {
        if (args.Length < 2) return "assign worker (no arguments)";
        byte worker = args[0], row = args[1];
        // The panel numbers the workers of the column it shows: the "Current Workers" column holds them all, in the
        // order they were pushed, and the "Available Workers" column holds the ones without a job.
        var order = settlement.Assignments.OrderBy(pair => pair.Key).ToList();
        if (row == 0)
        {
            // The panel's REMOVE button: the worker leaves its job and goes back to the available ones.
            if (order.Count <= worker) return $"remove worker {worker}: no worker to remove";
            settlement.Assignments[order[worker].Key] = 0;
            settlement.Workers = (byte)settlement.Assignments.Count;
            settlement.Version++;
            return $"worker {order[worker].Key} back to idle ({settlement.Workers}/{settlement.Capacity})";
        }
        // The panel's ASSIGN button: one of the available workers takes the job. The town's own limits come first: an
        // ore rank the town cannot work yet leaves the row locked (the client's "Job is locked", 7578), and a job type's
        // crew is capped by its VillageJobTypes MaxWorkersStat (12 miners, 6 smiths).
        int kind = VillageProduction.KindOfJob(row);
        if (!VillageProduction.Unlocked(settlement, row))
        {
            int reach = VillageProduction.Stat(settlement,
                kind == VillageProduction.MiningKind ? VillageProduction.MineRank : VillageProduction.SmithRank);
            return $"assign worker {worker}: VillageJobs row {row} is locked (the town works ores up to rank {reach})";
        }
        int cap = kind switch
        {
            VillageProduction.MiningKind => VillageProduction.Stat(settlement, VillageProduction.MaxMiners),
            VillageProduction.SmeltingKind => VillageProduction.Stat(settlement, VillageProduction.MaxSmiths),
            _ => int.MaxValue,
        };
        if (cap > 0 && VillageProduction.CrewOf(settlement, kind) >= cap)
            return $"assign worker {worker}: the town already has {cap} on that job";
        var idle = order.Where(pair => pair.Value == 0).ToList();
        if (idle.Count <= worker) return $"assign worker {worker}: no available worker to assign";
        settlement.Assignments[idle[worker].Key] = row;
        settlement.Workers = (byte)settlement.Assignments.Count;
        settlement.Version++;
        return $"worker {idle[worker].Key} on VillageJobs row {row} ({settlement.Workers}/{settlement.Capacity})";
    }

    /// <summary>The panel's release button (the small icon on a worker): the worker leaves the town for good, so the
    /// worker list shrinks and the panel is told about it.</summary>
    static string Release(Settlement settlement, ReadOnlySpan<byte> args)
    {
        byte index = args.Length > 0 ? args[0] : (byte)0;
        var order = settlement.Assignments.OrderBy(pair => pair.Key).ToList();
        if (order.Count <= index) return $"release worker {index}: no worker to release";
        byte key = order[index].Key;
        settlement.Assignments.Remove(key);
        settlement.Ranks.Remove(key);
        settlement.Workers = (byte)settlement.Assignments.Count;
        settlement.Version++;
        return $"worker {key} released ({settlement.Workers}/{settlement.Capacity})";
    }
}
