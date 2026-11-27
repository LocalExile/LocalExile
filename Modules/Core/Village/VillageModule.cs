using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

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

    /// <summary>Johan's "Quest Complete" row waits for this league flag (NPCTalk __col16 1436): gold reached the town.</summary>
    const int GoldAddedFlag = 1436;

    /// <summary>Johan's "Invest in Recruitment" row waits for this league flag (NPCTalk __col16 1434): a recruit was hired.</summary>
    const int RecruitHiredFlag = 1434;

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

    /// <summary>The key the settlement's panel-side state is stored under, in the character's own save
    /// (<see cref="CharacterSaves.ModuleData"/>): one blob per character, written atomically, next to the farm's own
    /// "kingsmarch-town" blob that the town's other half keeps.</summary>
    public const string SaveKey = "kingsmarch-village";

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

    /// <summary>The character's stored town, out of the character's own save; a missing or unreadable blob leaves the
    /// town empty rather than failing the load.</summary>
    static SavedVillage Stored(string who)
    {
        try
        {
            string? json = CharacterSaves.ModuleData(who, SaveKey);
            if (string.IsNullOrEmpty(json)) return new SavedVillage();
            return System.Text.Json.JsonSerializer.Deserialize<SavedVillage>(json) ?? new SavedVillage();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[gateway] kingsmarch village save ({who}): {ex.Message}");
            return new SavedVillage();
        }
    }

    /// <summary>Writes the character's town back into the character's own save, atomically.</summary>
    static void SaveVillage(string who, Settlement settlement)
    {
        if (who.Length == 0) return;
        var saved = new SavedVillage
        {
            Assignments = new(settlement.Assignments),
            Ranks = new(settlement.Ranks),
            Resources = new(settlement.Resources),
            Progress = (double[])settlement.Progress.Clone(),
            Upgrades = [.. settlement.Upgrades],
            LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        string json = System.Text.Json.JsonSerializer.Serialize(saved,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        if (!CharacterSaves.ChangeModuleData(who, SaveKey, _ => json))
            Console.WriteLine($"[gateway] kingsmarch village save ({who}): the write was refused");
    }

    /// <summary>What the farm's mirror said last, per connection: the settlement is only rebuilt when the farm's own
    /// numbers moved.</summary>
    static readonly ConditionalWeakTable<IPlayer, object> _farmStamp = new();

    /// <summary>The farm's crop names, in VillageResources rows 10..14's order. The store bag carries the crops only:
    /// the ores and bars are the village module's own production counters, which the mirror never touches.</summary>
    static readonly string[] Crops = ["Wheat", "Corn", "Pumpkin", "Globulora", "Orbisfera"];

    /// <summary>Copies the farm engine's numbers into the settlement the panel is drawn from. The farm
    /// (KingsmarchTown) stays the single engine that owns the workers, the plots and the store; this is the panel's
    /// read-only mirror of them, taken in process through <see cref="KingsmarchTown.View"/>, so no file and no second
    /// engine can disagree with the farm again.</summary>
    /// <param name="player">The player whose panel is open.</param>
    void MirrorFarm(IPlayer player)
    {
        var view = KingsmarchTown.View(player);
        if (view is null) return;
        // A cheap content stamp: the workers' slots and ranks, the store's counts and the plots' crops. Rebuilding the
        // mirror every tick would bump the settlement's Version every tick, and the panel would be pushed every frame.
        string stamp = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{string.Join(';', view.Workers.Select(w => $"{w.Plot}:{w.Rank}"))}"
            + $"|{string.Join(';', view.Store.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))}"
            + $"|{string.Join(';', view.Plots.Select(p => $"{p.Order}:{p.Crop}"))}");
        if (_farmStamp.TryGetValue(player, out object? seen) && seen as string == stamp) return;
        _farmStamp.AddOrUpdate(player, stamp);

        var settlement = Of(player);
        // The workers: the farm's list is the town's population, in the order the town keeps them (their Order is their
        // place from one). A worker on a farm plot holds the plot's VillageJobs row (11 + the plot); a worker on the
        // mine, the smeltery or a ship keeps the row the settlement already gave it - which vein (1-5) or which ore
        // (6-10) a worker works is the settlement's own choice, not the farm's - so the previous rows are captured
        // before the list is rebuilt.
        var previous = new Dictionary<byte, byte>(settlement.Assignments);
        settlement.Assignments.Clear();
        settlement.Ranks.Clear();
        for (int i = 0; i < view.Workers.Count; i++)
        {
            var (plot, rank, _) = view.Workers[i];
            byte key = (byte)(i + 1);
            settlement.Ranks[key] = (byte)Math.Clamp(rank, 1, 5);
            settlement.Assignments[key] = plot switch
            {
                KingsmarchTown.MineJob => (byte)Math.Clamp((int)previous.GetValueOrDefault(key), 1, 5),
                KingsmarchTown.SmelterJob => (byte)Math.Clamp((int)previous.GetValueOrDefault(key), 6, 10),
                >= 0 and <= 5 => (byte)(11 + plot),
                >= KingsmarchTown.ShipJob - 2 => (byte)(21 + (KingsmarchTown.ShipJob - plot)),
                _ => (byte)0,
            };
        }
        settlement.Workers = (byte)settlement.Assignments.Count;
        // The site numbers on the map icons: a worker counts into the counter of the job it holds.
        Array.Clear(settlement.Counters);
        foreach (var job in settlement.Assignments.Values)
        {
            if (job is >= 1 and <= 10) settlement.Counters[job - 1]++;
            else if (job is >= 11) settlement.Counters[10 + Math.Min(4, job - 11)]++;
        }
        // The store bag: the crops are rows 10..14 (and the dust 20, when the town has made any). The ore and bar rows
        // (5..9, 15..19) are the production lines' own counters, produced by VillageProduction.Advance — the mirror
        // never writes them, or it would wipe what the lines have made since the last farm change.
        for (int i = 0; i < Crops.Length; i++)
            settlement.Resources[10 + i] = (uint)Math.Max(0, view.Store.GetValueOrDefault(Crops[i]));
        settlement.Resources[20] = (uint)Math.Max(0, view.Store.GetValueOrDefault("MagicDust"));
        // The six plot slots the client names on the map icons: a plot growing crop n is row 9 + n, a bare one is
        // 0xff (out of range, which the client treats as "no row" instead of dereferencing row 0).
        Array.Fill(settlement.Slots, (byte)0xff);
        foreach (var (plotOrder, crop) in view.Plots)
        {
            if (plotOrder is < 0 or > 5 || crop is < 1 or > 5) continue;
            settlement.Slots[plotOrder] = (byte)(9 + crop);
        }
        settlement.Version++;
    }

    /// <summary>What hiring a waiting recruit costs: one hour of the wage the recruit would earn, which is the number
    /// the card shows (its u32 at <c>+0xa8</c>). The panel weighs the button against the treasury's gold and the client
    /// never moves gold itself, so the server charges it. A flat 2,000 sat far above the gold a town actually runs on,
    /// which is why every hire was refused.</summary>
    public static uint Price(byte row, byte skill) => VillageWages.Wage(VillageWorkers.JobType(row), skill);

    /// <summary>The harbour version each player has already been told about, so the dock cards go out on change only.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IPlayer, Stamp> _portStamps = new();

    sealed class Stamp { public int Version = -1; }


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
        var payload = new List<byte>(245);
        for (int i = 0; i < 21; i++)
            payload.AddRange(BitConverter.GetBytes(settlement.Resources.GetValueOrDefault(i)));
        // The production counters: the work the town's lines have done on the unit they are making, in hundredths of
        // that unit's own BaseProductionTime (the client's 0x1424c5b80 divides the remaining work by the crew's
        // efficiency). A panel that reads them draws its "Time" and "Production Rate"; zeros mean nothing is running.
        for (int j = 0; j < 15; j++)
            payload.AddRange(BitConverter.GetBytes(VillageProduction.Counter(settlement, j)));
        payload.AddRange(BitConverter.GetBytes(settlement.Gold));
        // The u32 after the treasury sits at +0x198 and its meaning is unknown. Writing the wage total in it crashed the
        // client (fault 0x162869f, inside the shipping failure roll), so it stays 0.
        payload.AddRange(BitConverter.GetBytes(0u));
        // The six plot slots the map icons name: a plot growing crop n is VillageResources row 9 + n, a bare plot is
        // 0xff. Row 0 has no name object and the client's label resolver does not check, so a zero slot faults at 0x8.
        payload.AddRange(settlement.Slots);
        // Bytes 158..181 are three (u32, u32) ship pairs, one per dock slot (the client's reader 0x1424c8190 walks
        // three of them): the port the ship is bound for as its one-based VillageShippingPorts row, and the cargo's
        // worth. An empty slot is (0, 0).
        var cards = KingsmarchPort.Cards(player.Character ?? "");
        foreach (var card in cards.Take(3))
        {
            payload.AddRange(BitConverter.GetBytes(card.PortRow >= 0 ? (uint)(card.PortRow + 1) : 0u));
            payload.AddRange(BitConverter.GetBytes((uint)Math.Clamp(card.Value, 0, int.MaxValue)));
        }
        for (int s = cards.Count; s < 3; s++) payload.AddRange(new byte[8]);
        // The village clock the client re-simulates from sits at 225..232 (u64); bytes 182..224 stay zero, and the
        // frame ends at 237 (the reader stops there, its caller 0x1417683cc then takes the u64 timestamp that
        // follows), so the declared length covers 245 bytes: a shorter frame raised "Buffer Underflow".
        payload.AddRange(new byte[225 - payload.Count]);
        payload.AddRange(BitConverter.GetBytes((ulong)clock));
        payload.AddRange(new byte[237 - payload.Count]);
        payload.AddRange(BitConverter.GetBytes((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await player.Send(StateFrame(TownRecordKind, 4, payload.ToArray()),
            $"0x02c9 village state: gold {settlement.Gold}, slots [{string.Join(' ', settlement.Slots)}]");
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

    /// <summary>Tells the port window what is in the town's three dock slots (S→C 0x2c9, kind 0x9b, subtag 2): a slot that
    /// is still loading draws the cargo in its 貨物 box and the port already picked for it, and a slot that is at sea
    /// draws the trip. The panel's own buttons never build a village action in this client build (they only move the
    /// card's own local state), so <c>/port load</c> and <c>/port sail</c> are the input and this push is what makes the
    /// window tell the truth about them.</summary>
    /// <param name="player">The player whose port window is open.</param>
    async Task PushPorts(IPlayer player)
    {
        if (player.AreaId != "KalguuranSettlersLeague") return;
        string who = player.Character ?? "";
        int version = KingsmarchPort.Version(who);
        var stamp = _portStamps.GetOrCreateValue(player);
        if (stamp.Version == version) return;
        stamp.Version = version;
        foreach (var card in KingsmarchPort.Cards(who))
        {
            var frame = new List<byte> { (byte)card.Slot, (byte)(card.PortRow < 0 ? 0xff : card.PortRow),
                (byte)(card.FailureRow < 0 ? 0xff : card.FailureRow) };
            foreach (int count in card.Cargo) frame.AddRange(BitConverter.GetBytes((uint)count)[..4]);
            await player.Send(StateFrame(TownRecordKind, 2, frame.ToArray()),
                $"0x02c9 village ship slot {card.Slot}: {(card.AtSea ? "at sea" : "loading")}, port {card.PortRow}, {card.Value} worth");
        }
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        VillageWorkers.LoadGameData();
        VillageProduction.LoadGameData();
        KingsmarchOre.Install(module.Manifest.Id, MarkOre);
        GameEvents.CommandsBuilding.Add(module.Manifest.Id, (commands, connection) =>
        {
            var player = Program.PlayerOf(connection);
            commands.Add("villagepanel", "/villagepanel hire <jobRow> [rank] (the town panel's workers)", async args =>
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
                    // forget what the panel has been told and push every frame of the settlement again
                    var fresh = Of(player);
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
                await player.Tell("usage: /villagepanel hire <VillageJobs row> [rank] | /villagepanel hirepanel <slot> | /villagepanel resend");
            });
            // The farm window's and the port window's own buttons never build a village action in this client build
            // (clicking them only moves the card's local state), so these two commands are the feature's input, the way
            // the panels' 0x2ca actions are the town's.
            commands.Add("village", "/village [hire | assign <worker> <plot 1-6 | mine | smelter | ship 1-3> | crop <plot> <crop>] (the Kingsmarch farm, mine, smeltery and ships)",
                async args =>
                {
                    if (args.Length > 0 && args[0] == "hire")
                    { await player.Tell("Kingsmarch: " + await KingsmarchTown.Hire(player)); return; }
                    if (args.Length >= 3 && args[0] == "crop")
                    {
                        if (!int.TryParse(args[1], out int which)) { await player.Tell("usage: /village crop <plot 1-6> <wheat | corn | pumpkin | globulora | orbisfera>"); return; }
                        await player.Tell("Kingsmarch: " + await KingsmarchTown.Choose(player, which, args[2]));
                        return;
                    }
                    if (args.Length >= 2 && args[0] == "assign")
                    {
                        if (!int.TryParse(args[1], out int worker)) { await player.Tell("usage: /village assign <worker> <plot 1-6 | mine | smelter | ship 1-3>"); return; }
                        if (args.Length >= 3 && args[2] is "ship" or "shipping")
                        {
                            int ship = 1;
                            if (args.Length >= 4 && (!int.TryParse(args[3], out ship) || ship < 1 || ship > 3))
                            { await player.Tell("usage: /village assign <worker> ship <1-3>"); return; }
                            await player.Tell("Kingsmarch: " + KingsmarchTown.AssignJob(player, worker, "ship", ship));
                            return;
                        }
                        if (args.Length >= 3 && args[2] is "mine" or "smelter")
                        { await player.Tell("Kingsmarch: " + KingsmarchTown.AssignJob(player, worker, args[2])); return; }
                        if (args.Length >= 3 && int.TryParse(args[2], out int plot))
                        { await player.Tell("Kingsmarch: " + await KingsmarchTown.Assign(player, worker, plot)); return; }
                        await player.Tell("usage: /village assign <worker> <plot 1-6 | mine | smelter | ship 1-3>"); return;
                    }
                    await player.Tell($"Kingsmarch farm: {KingsmarchTown.StoreText(player)}");
                });
            commands.Add("port", "/port [route <slot> <port> | load <slot> <crop> <count> | sail <slot> [port] | unload <slot> | send <port> <crop> <count>] (Kingsmarch shipping)",
                async args =>
                {
                    // The port window's 貨物 box: produce goes into a dock slot, where the card draws it until it sails.
                    if (args.Length >= 4 && args[0] == "load" && int.TryParse(args[1], out int slot) && int.TryParse(args[3], out int units))
                    { await player.Tell("Kingsmarch: " + KingsmarchPort.Load(player, slot, args[2], units)); return; }
                    // The port window's 選擇港口 button: the card is bound for a port before it is loaded.
                    if (args.Length >= 3 && args[0] == "route" && int.TryParse(args[1], out int routeSlot) && int.TryParse(args[2], out int routePort))
                    { await player.Tell("Kingsmarch: " + KingsmarchPort.Route(player, routeSlot, routePort)); return; }
                    // The port window's 啟航 button: the loaded slot becomes a ship at sea.
                    if (args.Length >= 2 && args[0] == "sail" && int.TryParse(args[1], out int sailSlot))
                    { await player.Tell("Kingsmarch: " + KingsmarchPort.Sail(player, sailSlot, args.Length >= 3 && int.TryParse(args[2], out int sailPort) ? sailPort : 0)); return; }
                    if (args.Length >= 2 && args[0] == "unload" && int.TryParse(args[1], out int unloadSlot))
                    { await player.Tell("Kingsmarch: " + KingsmarchPort.Unload(player, unloadSlot)); return; }
                    if (args.Length >= 4 && args[0] == "send" && int.TryParse(args[1], out int port) && int.TryParse(args[3], out int count))
                    { await player.Tell("Kingsmarch: " + KingsmarchPort.Send(player, port, args[2], count)); return; }
                    await player.Tell($"Kingsmarch port: {KingsmarchPort.Report(player)}");
                });
            return commands;
        });
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always(async (connection, now) =>
        {
            var player = Program.PlayerOf(connection);
            MirrorFarm(player);
            await RunProduction(player, now);
            await PushWorkers(player);
            await PushRecruits(player);
            await PushState(player);
            await PushPorts(player);
        }));
        GameEvents.Packet(ActionId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            // The wire is u16 id | u8 ruleset row | compact-int length | blob (the client's writer 0x141fa7c10); the row
            // is the record the panel belongs to (0x9b the town) and the blob's first byte is the action. Only the town's
            // actions are ours: returning false hands every other ruleset's 0x2ca on to its own handler.
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
            if (kind != TownRecordKind) return false;
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
                0x03 => await Assign(settlement, args, player),
                0x02 => Release(settlement, args, player),
                0x06 => Sail(player, args),
                0x07 => args.Length == 0 ? "abandon (no slot)" : KingsmarchPort.DropShip(who, args[0]) ? $"ship {args[0]} abandoned" : $"no ship in slot {args[0]}",
                0x00 => await RerollRecruits(player, args.Length > 0 ? args[0] : 0),
                0x01 => await HireRecruit(player, args.Length > 0 ? args[0] : 0),
                0x04 => await Farm(player, args),
                0x05 => Upgrade(settlement, args),
                0x0a => "abandon captured shipment: none is captured here",
                0x0b => Reroll(who, args),
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
        await global::Game.SetLeagueQuestFlags(Program.PlayerOf(player), [GoldAddedFlag]);
        await player.Send(InstanceEntities.Gold(left.Value), $"0x02aa gold: {left} (put {take} into the treasury)");
        return $"treasury +{take} (gold now {settlement.Gold})";
    }

    /// <summary>The panel's REMOVE: the player takes gold out of the treasury, and it goes back into the account.
    /// The take is capped at int.MaxValue so the debit (the harbour's int arithmetic) and the credit are always the
    /// same number: an uncapped uint against an int debit minted the difference out of nothing.</summary>
    static async Task<string> Take(Settlement settlement, uint amount, IPlayer player)
    {
        uint taken = Math.Min(Math.Min(amount, settlement.Gold), int.MaxValue);
        if (taken == 0) return "the treasury is empty";
        if (!KingsmarchPort.Spend(player.Character ?? "", (int)taken))
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
        // Johan's "Invest in Recruitment" row waits for this flag (NPCTalk __col16 1434).
        await global::Game.SetLeagueQuestFlags(Program.PlayerOf(player), [RecruitHiredFlag]);
        await PushWorkers(player);
        await PushRecruits(player);
        await PushState(player);
        return $"hired recruit {index} as worker {worker} at rank " +
            $"{VillageWages.Rank(skill)} for {price} gold ({paidFrom}); assign {worker} on the farm panel";
    }

    /// <summary>Picks the two recruits waiting to be hired: a job that has a name to show and a skill level.</summary>
    /// <param name="settlement">The settlement the recruits belong to.</param>
    static void RollRecruits(Settlement settlement, int lowest = 1)
    {
        settlement.Recruits.Clear();
        // the jobs that have a name to show: the five mining and the five smelting ones, plus disenchanting and mapping
        int[] jobs = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18];
        // Next's upper bound is exclusive: the top is one past the highest rank, so the best rank can actually roll.
        int top = Math.Max(VillageWages.Ranks + 1, lowest + 1);
        for (int i = 0; i < 2; i++)
            settlement.Recruits.Add(((byte)jobs[Random.Shared.Next(jobs.Length)],
                (byte)Random.Shared.Next(lowest, top)));
    }

    /// <summary>The farm panel's crop list: which crop the plot named in the action grows. That list is the five
    /// <c>VillageResources</c> rows the farm draws from (10..14, in the town's own crop order) with "none" as 0xff, so a
    /// second byte of 10..14 or 0xff is a crop; the assign button names a plot instead (0..5) and the ranges do not
    /// overlap, which is how one action byte tells the two apart. Live 2026-11-26: picking Corn on the panel arrived as
    /// <c>04 00 0B</c> - the first plot, resource row 11.</summary>
    static async Task<string> Farm(IPlayer player, byte[] args)
    {
        if (args.Length < 2) return "assign farm (no arguments)";
        int plot = args[0] + 1, row = args[1];
        if (row == 0xff)
            return $"farm (plot {plot} left bare): {await KingsmarchTown.Fallow(player, plot)}";
        if (row is >= 10 and <= 14)
        {
            string crop = KingsmarchTown.Crops[row - 10];
            return $"farm (plot {plot} growing {crop}): {await KingsmarchTown.Choose(player, plot, crop)}";
        }
        int worker = args[0] + 1, target = row + 1;
        string said = await KingsmarchTown.AssignIdle(player, args[0], target);
        return $"assign farm (worker {worker} → plot {target}): {said}";
    }

    /// <summary>The town map's upgrade button: which <c>VillageUpgradeCategories</c> row was bought. The client's
    /// writer (0x141778020..0x1417780c0) sends the action byte and the row, and no more (args.Length == 1 is the row
    /// alone after the dispatcher strips the action), so a longer payload is refused.</summary>
    static string Upgrade(Settlement settlement, ReadOnlySpan<byte> args)
    {
        if (args.Length < 1) return "perform upgrade (no arguments)";
        int row = args[0];
        bool first = settlement.Upgrades.Add(row);
        return first ? $"upgrade row {row} bought" : $"upgrade row {row} was already bought";
    }

    /// <summary>The port panel's quota reroll: one port, paid out of the treasury.</summary>
    static string Reroll(string who, ReadOnlySpan<byte> args)
    {
        if (args.Length < 1) return "reroll quota (no port)";
        string said = KingsmarchPort.RerollQuota(who, args[0], KingsmarchPort.QuotaRerollCost, out int[] quota);
        return $"{said} (quota {string.Join('/', quota)})";
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
    static async Task<string> Assign(Settlement settlement, byte[] args, IPlayer player)
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
            await KingsmarchTown.MoveTo(player, order[at].Key, 0);
            settlement.Workers = (byte)settlement.Assignments.Count;
            settlement.Version++;
            return $"worker {order[at].Key} back to idle ({settlement.Workers}/{settlement.Capacity})";
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
        // A ship's crew is capped per ship rather than per job type: the harbour has three cards and each holds six
        // (village_max_crew_per_ship), and the row it names is the card it belongs to (21, 22, 23 = ships 1, 2, 3).
        if (kind == VillageProduction.ShippingKind)
        {
            int perShip = Math.Max(1, VillageProduction.Stat(settlement, VillageProduction.MaxCrewPerShip));
            int aboard = VillageProduction.WorkersOn(settlement, row);
            if (aboard >= perShip)
                return $"assign worker {worker}: ship {row - 20} already has {aboard} crew";
        }
        var idle = order.Where(pair => pair.Value == 0).ToList();
        // The index is a place in the available column, and that column shrinks as workers are placed, so it is
        // clamped rather than rejecting whoever is last -that was the worker nobody could ever place.
        if (idle.Count == 0) return "assign worker: every worker already has a job";
        var pick = idle[Math.Min((int)worker, idle.Count - 1)];
        settlement.Assignments[pick.Key] = row;
        settlement.Workers = (byte)settlement.Assignments.Count;
        settlement.Version++;
        await KingsmarchTown.MoveTo(player, pick.Key, row);
        return $"worker {pick.Key} on VillageJobs row {row} ({settlement.Workers}/{settlement.Capacity})";
    }

    /// <summary>The panel's release button (action 0x02, the client's own "release worker"): the worker leaves the
    /// town for good, so the worker list shrinks and the panel is told about it. The town's REMOVE button is action
    /// 0x03 with row 0 (a plain unassign), not this.</summary>
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
