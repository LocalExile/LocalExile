using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServer.Core;
using GameServer.Modding;

/// <summary>Kingsmarch's port: shipments of what the farm grows, loaded onto one of the town's three ships, sent to a
/// Kalguuran port and paid back in barter.
/// The client's port window draws (0x7b kind 0x58 opens it) and its three ship cards read the dock slots the server
/// pushes (0x2c9 kind 0x9b subtag 2), but its buttons (<c>貨物</c>, <c>選擇港口</c>, <c>啟航</c>, <c>遺棄</c>) never build a
/// village action in this client build: clicking them only moves the card's own local state. So the window is the
/// readout and <c>/port …</c> is the input, the way the town map and <c>/village</c> are split.
/// A dock slot holds a ship: cargo loaded from the farm's store, the port it is bound for, and the crew it needs. Sailing
/// it moves that ship to sea, where it takes time proportional to the port's distance, may be lost to the risks the port
/// is known for, and pays barter when it comes home. A lost trip is not a coin flip with no story: the client's own
/// failure table says what went wrong and what it cost.
/// Time is the wall clock in Unix seconds, so a ship saved yesterday is due today instead of waiting for the process to
/// have been up as long as its trip. The harbour itself lives in the character's own save
/// (CharacterSaves.ModuleData, one blob per character), so a trip survives a relog and two characters' harbours can
/// never write over one another.</summary>
public static class KingsmarchPort
{
    const string Area = "KalguuranSettlersLeague";
    // The client's own tables (data/client.db, read through GameServer.Core.ClientTables once at load): the ports the
    // ships sail to, what each export is worth, the ways a trip can go wrong, and the shipment's bounds. The data/
    // tables/*.json copies the first build read were exports of exactly these tables, and client.db is the source they
    // were exported from.
    const string PortsTable = "VillageShippingPorts";
    const string ExportsTable = "VillageExports";
    const string FailuresTable = "VillageShippingFailures";
    const string ConstantsTable = "VillageSharedConstants";

    /// <summary>The key this module's save lives under in the character's save (CharacterSaves.ModuleData): one blob
    /// per character, the <see cref="SavedPort"/> as JSON.</summary>
    const string SaveKey = "kingsmarch-port";

    /// <summary>What a trip has to be worth before a port takes it. The tables' <c>ExpectedShipmentValue</c> is 150, but a
    /// young town's farm holds a handful of crops worth 12-24 each, so the skeleton asks for a cargo that fits one harvest
    /// and leaves the real quota for when shipments are the town's income and not a test.</summary>
    const int MinCargoValue = 40;

    /// <summary>Barter a successful shipment brings home, as a multiple of its value. Real Kingsmarch pays in barter
    /// currency whose exact table is not in the client's data files, so the skeleton keeps one number and says so.</summary>
    const double BarterMultiplier = 1.4;

    /// <summary>How many u32 the wire's cargo array carries: one per VillageExports row, the same sixteen the panel's
    /// own send action packs (0x06: slot, port, failure, 16 × u32).</summary>
    const int WireCargoRows = 16;

    /// <summary>The bounds the client's own port panel puts on a shipment's value, read from
    /// <c>VillageSharedConstants</c> at load. The numbers here are that table's 3.29.3c values and are kept only for a
    /// table that cannot be read.</summary>
    static int _minShipmentValue = 50000;
    static int _maxShipmentValue = 50000000;

    /// <summary>What the tables expect one shipment to be worth, from <c>VillageSharedConstants.ExpectedShipmentValue</c>
    /// (150 in 3.29.3c): the divisor a trip's crew requirement is measured in, so a harbour's three hands can sail a
    /// cargo of about three hundred worth and a full hold wants more hands than the town starts with.</summary>
    static int _expectedShipmentValue = 150;

    /// <summary>What one quota reroll costs, from <c>VillageSharedConstants.QuotaRerollCost</c> (15000 gold in
    /// 3.29.3c). Read from the table at load; the number here is that row's own value, kept for a table that cannot
    /// be read. <see cref="QuotaRerollCost"/> is what callers use.</summary>
    static int _quotaRerollCost = 15000;

    /// <summary>What one quota reroll costs, by <c>VillageSharedConstants.QuotaRerollCost</c> (15000 gold in
    /// 3.29.3c): the panel's 選擇港口 reroll button's price, one number for every port.</summary>
    public static int QuotaRerollCost => _quotaRerollCost;

    static readonly Regex Markup = new("<[^>]*>|[{}\"]", RegexOptions.Compiled);

    // The once-only table load: a Load() call either fills the four lists below or returns, and no other method
    // writes them. Reads after Load() see them filled; reads before it (a command that skips Load) see them empty,
    // which is how the skeleton always behaved.
    static readonly object _lock = new();
    static readonly List<Port> _ports = [];
    static readonly List<Loss> _losses = [];
    static readonly Dictionary<string, int> _values = new(StringComparer.OrdinalIgnoreCase);
    // The row a resource has in VillageExports: the wire's cargo array has one u32 per row of that table, and
    // VillageShippingFailures' Resource column indexes that same table — not VillageResources, whose row 15 is the
    // iron bar while export row 15 is the Thaumaturgic Dust.
    static readonly Dictionary<string, int> _export = new(StringComparer.OrdinalIgnoreCase);
    // The reverse of _export: wire slot → the VillageExports row's Id, so a cargo array the panel sent can name its
    // cargo without scanning the dictionary — and without a crash when a slot has no row (a table with fewer rows
    // than the wire's sixteen u32 keeps the rest and skips the rest).
    static readonly string[] _exportByRow = new string[WireCargoRows];
    // Every change of a harbour goes through <see cref="Change"/> under this one lock: the change closure reads the
    // blob the save holds and writes the changed one back, so a read-modify-write of one character's harbour cannot
    // interleave with another one of this module's. The save database's own transactions keep the rest of the
    // character's save intact either way.
    static readonly object _saveLock = new();
    static bool _loaded;

    /// <summary>The town owns three ships, and the port window draws one card per dock slot.</summary>
    public const int Docks = 3;

    /// <summary>The clock every trip is timed by: Unix seconds on the wall clock, so a saved trip is due when the world
    /// says so and not when this process has been up for long enough.</summary>
    static double Clock() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    sealed record Port(string Id, string Name, int Distance, double Risk);

    /// <summary>One way a shipment can go wrong, straight out of the <c>VillageShippingFailures</c> table:
    /// <c>Rows</c> = the <c>VillageExports</c> rows it can hit (empty = the whole cargo), <c>Lost</c>/<c>Bonus</c> =
    /// percent of the cargo and of the return, <c>Delay</c> = percent of the travel time, <c>Crew</c> = crew members that
    /// do not come home, <c>Weight</c> = how often it happens relative to the rest.</summary>
    sealed record Loss(string Id, string Text, int[] Rows, int Lost, int Bonus, int Delay, int Crew, int Weight);

    /// <summary>A ship on its way: what it carries, where to and when it is due back.</summary>
    sealed class Ship
    {
        /// <summary>The dock slot the ship left from, so its card comes back to the same place (0 to 2).</summary>
        public int Slot { get; set; }
        public string Port { get; set; } = "";
        public int Value { get; set; }
        public int Crew { get; set; }
        public double Sent { get; set; }
        public double Returns { get; set; }
        // How often this trip was pushed back by weather (a delay is a once-per-trip event, not a loop).
        public int Delays { get; set; }
        /// <summary>The VillageShippingFailures row that hit this trip, empty while it is still at sea.</summary>
        public string Loss { get; set; } = "";
        public Dictionary<string, int> Cargo { get; set; } = [];
    }

    /// <summary>One ship at the dock: the cargo loaded onto it and the port it will sail for once the player says so.
    /// This is the "装货" half the port window draws in its 貨物 box.</summary>
    sealed class Dock
    {
        /// <summary>The VillageShippingPorts Id the player picked, empty until a port is chosen.</summary>
        public string Port { get; set; } = "";
        public Dictionary<string, int> Cargo { get; set; } = [];
    }

    /// <summary>One character's harbour: the crew it has, the barter it earned, the three docks, and the ships at sea.
    /// Saved as the character's own blob under <see cref="SaveKey"/>; a change goes through <see cref="Change"/>, and
    /// what the methods hand out is always a fresh deserialized copy, never a shared one.</summary>
    sealed class SavedPort
    {
        public int Crew { get; set; } = 3;
        public int Barter { get; set; }
        /// <summary>The town's treasury: gold the player funded into Kingsmarch (Johan's Fund the Treasury).</summary>
        public long Gold { get; set; }
        /// <summary>Wages the treasury could not pay yet; they are paid from the next funding.</summary>
        public int Wages { get; set; }
        public List<Ship> Ships { get; set; } = [];
        /// <summary>The three ships the town owns, each a dock slot the port window draws as a card. A slot is a ship
        /// being loaded until it sails, and the slot is empty again when that ship comes home.</summary>
        public List<Dock> Docks { get; set; } = [];
        /// <summary>What came home and has not been collected yet. Kept for the tables' collect rows; a trip's barter is
        /// banked the moment it is due, because the panel's collect button cannot be reached in this build.</summary>
        public List<Reward> Rewards { get; set; } = [];
        /// <summary>Each port's shipment quota, by <c>VillageShippingPorts</c> Id: six numbers the panel shows per port and
        /// the reroll button replaces. Absent until the port is first rerolled.</summary>
        public Dictionary<string, int[]> Quotas { get; set; } = [];
        /// <summary>Bumped whenever a card or the purse changes, so the tower tells the panel once and not once a frame.</summary>
        public int Version { get; set; }
    }

    /// <summary>One shipment that came home, waiting to be collected.</summary>
    sealed class Reward
    {
        public string Port { get; set; } = "";
        public int Barter { get; set; }
        public double At { get; set; }
    }

    /// <summary>Loads the ports, what each resource is worth, and the ways a trip can go wrong, once per module load.
    /// The four tables come from client.db through <see cref="ClientTables.Table"/>: VillageShippingPorts,
    /// VillageExports, VillageShippingFailures and VillageSharedConstants. A numeric cell arrives as the number its
    /// column declares, or as the text of the value the client's own stringified data kept — <see cref="TableInt"/>
    /// and <see cref="TableReal"/> take both, so a cell of either shape reads the same.</summary>
    static void Load()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                foreach (var row in ClientTables.Table(PortsTable))
                {
                    string id = row.TextOrNull("Id") ?? "";
                    _ports.Add(new Port(
                        id,
                        row.TextOrNull("Name") ?? id,
                        TableInt(row, "DistanceFromPort", 0),
                        TableReal(row, "RiskMultiplier", 1.0)));
                }
            }
            catch (Exception ex) when (TableFailure(ex))
            {
                Console.WriteLine($"[gateway] {PortsTable}: {ex.Message}");
            }
            try
            {
                foreach (var row in ClientTables.Table(ExportsTable))
                {
                    // Every row keeps its index, even the ones worth nothing: the cargo array is indexed by it.
                    string exportId = row.TextOrNull("Id") ?? "";
                    if (exportId.Length > 0)
                    {
                        _export[exportId] = row.Index;
                        if (row.Index < _exportByRow.Length) _exportByRow[row.Index] = exportId;
                    }
                    int value = TableInt(row, "ShippingValue", 0);
                    if (value <= 0) continue;
                    _values[exportId] = value;
                    // The farm's store is keyed by the crop's name ("Wheat"), the exports table by its id; keep both.
                    if (row.TextOrNull("Name") is { Length: > 0 } display)
                    {
                        _values[display] = value;
                        _export[display] = _export.GetValueOrDefault(exportId, 0);
                    }
                }
            }
            catch (Exception ex) when (TableFailure(ex))
            {
                Console.WriteLine($"[gateway] {ExportsTable}: {ex.Message}");
            }
            try
            {
                foreach (var row in ClientTables.Table(ConstantsTable))
                {
                    string id = row.TextOrNull("Id") ?? "";
                    int value = TableInt(row, "Value", 0);
                    if (id == "MinShipmentValue") _minShipmentValue = value;
                    else if (id == "MaxShipmentValue") _maxShipmentValue = value;
                    else if (id == "ExpectedShipmentValue") _expectedShipmentValue = Math.Max(1, value);
                    else if (id == "QuotaRerollCost") _quotaRerollCost = value;
                }
            }
            catch (Exception ex) when (TableFailure(ex))
            {
                // The fields above already hold that table's 3.29.3c numbers, so an unreadable table keeps the
                // client's bounds and the reroll's price.
                Console.WriteLine($"[gateway] {ConstantsTable}: {ex.Message}");
            }
            try
            {
                foreach (var row in ClientTables.Table(FailuresTable))
                {
                    string id = row.TextOrNull("Id") ?? "";
                    // "NULL" is the table's empty slot and a commandeered ship waits for the player, so neither is rolled.
                    if (id.Length == 0 || id is "NULL" or "ShipCommandeered") continue;
                    int weight = TableInt(row, "__col9", 0);
                    if (weight <= 0) continue;
                    _losses.Add(new Loss(
                        id,
                        row.TextOrNull("Text") ?? id,
                        // The VillageExports rows the failure can hit ([] = the whole cargo); a JSON list column reads
                        // as numbers whatever kind of items the extractor kept.
                        row.Ints("Resource"),
                        TableInt(row, "ResourceLostPercent", 0),
                        TableInt(row, "PortBonusLostPercent", 0),
                        TableInt(row, "__col5", 0), // the delay percent (ShipmentDelayed100% carries 100 there)
                        TableInt(row, "CrewMembersLost", 0),
                        weight));
                }
            }
            catch (Exception ex) when (TableFailure(ex))
            {
                Console.WriteLine($"[gateway] {FailuresTable}: {ex.Message}");
            }
        }
    }

    /// <summary>The failures a table read can hit: the database or the column is missing, the cell's type is not what
    /// the column declares, or a list holds something that is not the numbers it was expected to be. A table that
    /// fails this way logs and leaves the field defaults in place, as the JSON copies did.</summary>
    static bool TableFailure(Exception ex) =>
        ex is FileNotFoundException or IOException or KeyNotFoundException or InvalidCastException
            or InvalidOperationException or FormatException;

    /// <summary>A table number that may arrive as the number its column declares or as the text the client's own
    /// stringified data kept: parsed either way (the invariant culture, so "50000" never becomes 50,000 by a locale's
    /// thousands separator), with <paramref name="fallback"/> when the column is missing, null or neither.</summary>
    static int TableInt(ClientRow row, string column, int fallback)
    {
        try { return row.LongOrNull(column) is { } value ? (int)Math.Clamp(value, int.MinValue, int.MaxValue) : fallback; }
        catch (InvalidCastException)
        {
            return int.TryParse(row.TextOrNull(column), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
        }
    }

    /// <summary><see cref="TableInt"/> for a real column: the column's own number, else its text form, else the
    /// fallback.</summary>
    static double TableReal(ClientRow row, string column, double fallback)
    {
        try { return row.RealOrNull(column) ?? fallback; }
        catch (InvalidCastException)
        {
            return double.TryParse(row.TextOrNull(column), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;
        }
    }

    // ---- The harbour's save: one blob per character, in the character's own save ------------------

    /// <summary>The character's harbour, as the character's save holds it (CharacterSaves.ModuleData under
    /// <see cref="SaveKey"/>): a fresh deserialized copy on every call, so no caller holds a piece of a shared
    /// dictionary and no save write can enumerate what another thread is changing — the flat file kept both, a
    /// dictionary under one lock and the file write under another. A blob that cannot be read (an older build's shape,
    /// an unreadable save) starts the harbour over, as the flat file did.</summary>
    static SavedPort Harbour(string who)
    {
        string? json = null;
        try { json = CharacterSaves.ModuleData(who, SaveKey); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Console.WriteLine($"[gateway] the save of {who} could not be read for its harbour, starting it over: {ex.Message}");
        }
        return Ready(ReadBlob(json));
    }

    /// <summary>The saved blob as a harbour, or a fresh one when there is nothing saved or the blob is unreadable.</summary>
    static SavedPort ReadBlob(string? json)
    {
        try
        {
            return string.IsNullOrEmpty(json) ? new SavedPort() : JsonSerializer.Deserialize<SavedPort>(json) ?? new SavedPort();
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[gateway] the harbour's saved blob is unreadable, starting it over: {ex.Message}");
            return new SavedPort();
        }
    }

    /// <summary>Changes the character's harbour in one save: <paramref name="change"/> gets the harbour as the save
    /// holds it and returns it changed, or null when it changed nothing (a refused spend, an already-at-sea slot) and
    /// the save is left alone. The change runs inside CharacterSaves.ChangeModuleData, and every change this module
    /// makes goes through here under one lock, so a read-modify-write cannot interleave with another one of this
    /// module's: there is no shared dictionary left to change under one lock while a save enumerates it under
    /// another. Returns the harbour as it now stands. A save write that fails logs and the call still returns the
    /// changed harbour (the next change writes the whole blob again); nothing here writes files of its own.</summary>
    static SavedPort Change(string who, Func<SavedPort, SavedPort?> change)
    {
        var result = new SavedPort();
        lock (_saveLock)
        {
            try
            {
                CharacterSaves.ChangeModuleData(who, SaveKey, json =>
                {
                    var port = Ready(ReadBlob(json));
                    var changed = change(port);
                    if (changed is null)
                    {
                        result = port;
                        return null;
                    }
                    result = changed;
                    return JsonSerializer.Serialize(changed);
                });
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                Console.WriteLine($"[gateway] the harbour of {who} could not be saved: {ex.Message}");
            }
        }
        return Ready(result);
    }

    /// <summary>A harbour saved before the town owned docks has fewer than three of them: give it the rest.</summary>
    static SavedPort Ready(SavedPort port)
    {
        while (port.Docks.Count < Docks) port.Docks.Add(new Dock());
        return port;
    }

    /// <summary>The barter the character's harbour holds. The recruiter is paid from it, so the town's two businesses
    /// share one purse instead of inventing a second currency.</summary>
    public static int Barter(string who) => Harbour(who).Barter;

    /// <summary>Pays <paramref name="cost"/> barter, or leaves the harbour alone and returns false when it cannot.</summary>
    public static bool PayBarter(string who, int cost)
    {
        if (cost <= 0) return true;
        bool paid = false;
        Change(who, port =>
        {
            if (port.Barter < cost) return null;
            port.Barter -= cost;
            paid = true;
            return port;
        });
        return paid;
    }

    /// <summary>The gold in the character's town treasury, and the wages still owed to its workers.</summary>
    public static long Gold(string who) => Harbour(who).Gold;

    public static int Wages(string who) => Harbour(who).Wages;

    /// <summary>Puts gold into the treasury and settles any wages owed first; the treasury after.</summary>
    public static long Fund(string who, long amount)
    {
        var port = Change(who, harbour =>
        {
            harbour.Gold += Math.Max(0, amount);
            int owed = Math.Min(harbour.Wages, (int)Math.Min(harbour.Gold, int.MaxValue));
            harbour.Wages -= owed;
            harbour.Gold -= owed;
            return harbour;
        });
        return port.Gold;
    }

    /// <summary>Pays a worker's wage: false when the treasury is short, in which case the wage is owed and settled by the
    /// next funding, so the town keeps working instead of stalling on an empty treasury.</summary>
    public static bool PayWages(string who, int amount)
    {
        if (amount <= 0) return true;
        bool paid = false;
        Change(who, port =>
        {
            if (port.Gold < amount)
            {
                port.Wages += amount; // the wage is owed: the state changed, so the save happens
                return port;
            }
            port.Gold -= amount;
            paid = true;
            return port;
        });
        return paid;
    }

    /// <summary>Spends gold the treasury can afford, or nothing and false.</summary>
    public static bool Spend(string who, int amount)
    {
        if (amount <= 0) return true;
        bool spent = false;
        Change(who, port =>
        {
            if (port.Gold < amount) return null;
            port.Gold -= amount;
            spent = true;
            return port;
        });
        return spent;
    }

    /// <summary>Seconds a ship needs for a port: the home port is a short hop, Kalguur is minutes away.</summary>
    static double Travel(Port port) => 45 + port.Distance / 10.0;

    /// <summary>Chance a shipment is lost on the way: the port's risk, raised by its distance, capped so a trip is worth
    /// risking. A lost trip is then described by the failure table rather than being a bare 0-value return.
    /// <c>/port storm</c> overrides it, which is how the loss path is tested without waiting for bad luck.</summary>
    static double Failure(Port port) => _storm >= 0 ? Math.Clamp(_storm, 0, 1) : Math.Clamp((0.03 + port.Distance / 10000.0) * port.Risk, 0, 0.35);

    /// <summary>The loss chance <c>/port storm</c> forced, or -1 for the port's own.</summary>
    static double _storm = -1;

    /// <summary>Forces the loss chance of every trip (dev: /port storm 100), or gives the ports their own back (/port storm off).</summary>
    public static string Storm(double percent)
    {
        _storm = percent < 0 ? -1 : percent / 100.0;
        return _storm < 0 ? "The ports are back to their own risks." : $"Every trip now loses its cargo with probability {_storm * 100:0}%.";
    }

    /// <summary>How many of the town's crew a shipment ties up, in the tables' own units: one hand per
    /// <c>ExpectedShipmentValue</c> (150) a cargo is worth, at least one and at most six, which is what one ship's card
    /// can hold (船員 0/6). The harbour starts with three, so a cargo of about four hundred sails without a hire and a
    /// full hold is what the crew limit is for.</summary>
    static int CrewRequired(int value) => Math.Clamp((int)Math.Ceiling(value / (double)_expectedShipmentValue), 1, 6);

    /// <summary>The value a <see cref="Ship"/> carries: the cargo's value held inside the client's own shipment bounds
    /// (<c>VillageSharedConstants</c>). The ceiling is that table's <c>MaxShipmentValue</c>; the floor is the lower of
    /// its <c>MinShipmentValue</c> and the town's own <see cref="MinCargoValue"/>, because this town deliberately takes a
    /// cargo the client's minimum would not, and clamping such a cargo up would inflate the barter it pays.</summary>
    static int ShipmentValue(int value) => Math.Clamp(value, Math.Min(_minShipmentValue, MinCargoValue), _maxShipmentValue);

    /// <summary>A fixed number in [0,1) per shipment and purpose: the same trip always resolves the same way, so the player
    /// cannot re-roll a bad outcome by reloading the area.</summary>
    static double Die(Ship ship, double salt) => Math.Abs(Math.Sin(ship.Sent * salt + ship.Value * 78.233 + ship.Crew * 3.7)) % 1.0;

    /// <summary>The barter a trip pays: its value times <see cref="BarterMultiplier"/>, less the share the failure took,
    /// rounded and held inside an int. The multiplication is double arithmetic, and the clamp keeps a table that
    /// promised more than an int holds from wrapping the payout.</summary>
    static int Paid(int value, double kept) => (int)Math.Clamp(Math.Round(value * BarterMultiplier * kept), 0, int.MaxValue);

    /// <summary>The failure this shipment suffers: a weighted pick among the rows that can hit its cargo. A row's
    /// <c>Resource</c> column holds <c>VillageExports</c> row indices, the same index the cargo array carries, so a
    /// cargo key is matched through <c>_export</c> and never through a VillageResources row: export row 15 is the dust,
    /// while VillageResources row 15 is the iron bar. A store name the exports table does not know matches no such row, so
    /// it can only be hit by a failure that takes the whole cargo.</summary>
    static Loss? Pick(Ship ship)
    {
        var fits = _losses.Where(l => l.Rows.Length == 0
            || ship.Cargo.Keys.Any(c => _export.TryGetValue(c, out int row) && l.Rows.Contains(row))).ToList();
        if (fits.Count == 0) return null;
        int total = fits.Sum(l => l.Weight);
        double roll = Die(ship, 11.17) * total;
        foreach (var loss in fits)
        {
            roll -= loss.Weight;
            if (roll < 0) return loss;
        }
        return fits[^1];
    }

    /// <summary>Every tick: ships that are due come home, or do not. Nothing is written when no ship is due, so a town
    /// waiting on the sea does not save once a frame.</summary>
    public static Task Tick(IPlayer player, double now)
    {
        if (!string.Equals(player.AreaId, Area, StringComparison.Ordinal)) return Task.CompletedTask;
        Load();
        double clock = Clock();
        var waiting = Harbour(player.Character ?? "");
        if (!waiting.Ships.Any(s => clock >= s.Returns)) return Task.CompletedTask;
        var lines = new List<string>();
        Change(player.Character ?? "", harbour =>
        {
            // The ships that are due, taken out first so a weather delay can put its own back without meeting itself.
            var due = harbour.Ships.Where(s => clock >= s.Returns).ToList();
            if (due.Count == 0) return null;
            foreach (var ship in due) harbour.Ships.Remove(ship);
            foreach (var ship in due)
            {
                var port = _ports.FirstOrDefault(p => p.Id == ship.Port);
                string to = port?.Name ?? ship.Port;
                if (port is not null && Die(ship, 12.9898) < Failure(port))
                {
                    var loss = Pick(ship);
                    if (loss is null)
                    {
                        lines.Add($"The shipment to {to} was lost at sea ({ship.Value} value, {ship.Crew} crew).");
                        continue;
                    }
                    if (loss.Delay > 0 && ship.Delays == 0)
                    {
                        // Weather: the ship is still out there, and the trip takes longer rather than failing.
                        ship.Delays++;
                        ship.Returns = clock + Travel(port) * loss.Delay / 100.0;
                        harbour.Ships.Add(ship);
                        lines.Add($"{Clean(loss.Text)} The shipment to {to} is {loss.Delay}% late.");
                        continue;
                    }
                    ship.Loss = loss.Id;
                    int home = Math.Max(0, ship.Crew - loss.Crew);
                    harbour.Crew += home;
                    double kept = (1 - loss.Lost / 100.0) * (1 - loss.Bonus / 100.0);
                    int paid = Paid(ship.Value, kept);
                    harbour.Barter += paid;
                    lines.Add($"{Clean(loss.Text)} ({paid} barter for {ship.Value} value, {home} of {ship.Crew} crew home.)");
                    continue;
                }
                // The crew is tied up while the ship is away and comes home with it.
                harbour.Crew += ship.Crew;
                int barter = Paid(ship.Value, 1.0);
                // The barter is banked the moment the ship is due: the port panel's collect button is the only other way to
                // move it, and that button never reaches the server in this build (its clicks only move the card's own
                // local state), so a trip that paid into a reward row would be a trip that never paid at all.
                harbour.Barter += barter;
                lines.Add($"A ship came back from {to}: {barter} barter for {ship.Value} value ({ship.Crew} crew home, {harbour.Barter} in the purse).");
            }
            harbour.Version++;
            return harbour;
        });
        return lines.Count == 0 ? Task.CompletedTask : player.Tell(string.Join(" | ", lines));
    }

    /// <summary>The table's texts carry the client's own markup (<c>&lt;kalguurred&gt;{…}</c>); chat shows them plain.</summary>
    static string Clean(string text) => Markup.Replace(text, "").Trim();

    /// <summary>Whether the ships sail to this port. VillageShippingPorts row 0 is Kingsmarch itself — the home port,
    /// distance 1 and risk 1 in 3.29.3c against every real destination's 140 or more — and a shipment to it would be
    /// the town shipping itself for barter, so the exclusion goes by the distance the table declares: anything a hop
    /// of one or less is the town, not a destination. Row 0 stays in <see cref="_ports"/>, so the panel's row numbers
    /// stay the table's own <c>_row</c> (the wire's ship pairs carry the one-based row).</summary>
    static bool IsDestination(Port port) => port.Distance > 1;

    /// <summary>The first dock slot with no ship of its own, or -1 when all three are at sea.</summary>
    static int FreeSlot(SavedPort harbour) =>
        Enumerable.Range(0, Docks).FirstOrDefault(i => harbour.Ships.All(s => s.Slot != i), -1);

    /// <summary>The harbour as one report: the docks the port window draws as cards, the ports with their distance and risk,
    /// the ships at sea and what is banked.</summary>
    public static string Report(IPlayer player)
    {
        Load();
        var harbour = Harbour(player.Character ?? "");
        double clock = Clock();
        var lines = new List<string>();
        // The three cards, in the order the port window draws them: what each holds and where it is bound.
        for (int slot = 0; slot < Docks; slot++)
        {
            var atSea = harbour.Ships.FirstOrDefault(s => s.Slot == slot);
            if (atSea is not null)
            {
                string bound = _ports.FirstOrDefault(p => p.Id == atSea.Port)?.Name ?? atSea.Port;
                lines.Add($"dock {slot}: at sea for {bound}, {atSea.Value} worth, back in {Math.Max(0, atSea.Returns - clock):0} s");
                continue;
            }
            var dock = harbour.Docks[slot];
            if (dock.Cargo.Count == 0)
            {
                lines.Add($"dock {slot}: empty");
                continue;
            }
            string cargo = string.Join(", ", dock.Cargo.Select(c => $"{c.Value} {c.Key}"));
            string destination = dock.Port.Length == 0 ? "no port chosen" : (_ports.FirstOrDefault(p => p.Id == dock.Port)?.Name ?? dock.Port);
            lines.Add($"dock {slot}: {cargo} worth {Worth(dock.Cargo)}, {destination}");
        }
        for (int i = 0; i < _ports.Count; i++)
            lines.Add($"{i + 1} {_ports[i].Name} (distance {_ports[i].Distance}, risk {Failure(_ports[i]) * 100:0}%{(IsDestination(_ports[i]) ? "" : ", home")})");
        lines.Add($"crew {harbour.Crew}, barter {harbour.Barter}");
        lines.Add("commands: /port route <slot> <port> | /port load <slot> <crop> <count> | /port sail <slot> | /port unload <slot>");
        return string.Join(" | ", lines);
    }

    /// <summary>Puts more hands on the docks: crew is what a shipment ties up, and it comes home with the ship.</summary>
    public static string Crew(IPlayer player, int add)
    {
        var harbour = Change(player.Character ?? "", port =>
        {
            port.Crew += Math.Clamp(add, 1, 5);
            return port;
        });
        return $"The harbour now has {harbour.Crew} crew.";
    }

    /// <summary>Sends a shipment: takes the cargo out of the farm's store, ties up crew and starts the clock. Everything
    /// is checked before the store gives up anything — the port is a real row and one the ships sail to (Kingsmarch
    /// itself, the table's row 0, is not), the crop is priced, the count times its worth — long, so a big count cannot
    /// wrap the multiplication into a small or negative one — clears <see cref="MinCargoValue"/>, and the crew and a
    /// free dock slot are there. A check that fails leaves the store alone.</summary>
    public static string Send(IPlayer player, int portNumber, string crop, int count)
    {
        Load();
        if (portNumber < 1 || portNumber > _ports.Count) return $"no port {portNumber}: /port lists them";
        var port = _ports[portNumber - 1];
        if (!IsDestination(port)) return $"{port.Name} is the home port: the ships sail from here, not to it";
        if (count <= 0) return "count must be positive";
        if (!_values.TryGetValue(crop, out int each)) return $"{crop} is not something the ports buy";
        int have = KingsmarchTown.Stored(player, crop);
        if (have < count) return $"the town has {have} {crop}, not {count}";
        long planned = (long)each * count;
        if (planned < MinCargoValue) return $"{count} {crop} is worth {planned}; a port wants at least {MinCargoValue} worth of cargo";
        int value = ShipmentValue((int)Math.Min(planned, int.MaxValue));
        int crew = CrewRequired(value);
        var harbour = Harbour(player.Character ?? "");
        if (harbour.Crew < crew) return $"that cargo needs {crew} crew, the harbour has {harbour.Crew}";
        if (FreeSlot(harbour) < 0) return $"all {Docks} slots are at sea; a harbour this size sails {Docks} ships at once";
        int taken = KingsmarchTown.TakeFromStore(player, crop, count);
        if (taken <= 0) return $"the town has no {crop} to send";
        // The store may have shrunk between the look and this take: the trip carries what was actually taken, and its
        // value and crew are that cargo's — never more than the checks above passed for.
        long actual = (long)each * taken;
        value = ShipmentValue((int)Math.Min(actual, int.MaxValue));
        crew = CrewRequired(value);
        double clock = Clock();
        var ship = new Ship
        {
            Port = port.Id, Value = value, Crew = crew, Sent = clock, Returns = clock + Travel(port),
            Cargo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [crop] = taken },
        };
        string? refused = null;
        Change(player.Character ?? "", p =>
        {
            // The slot and the crew are re-checked inside the save: another connection's sail could have taken them
            // since the checks above, and a refused trip puts the cargo back rather than losing it.
            int slot = FreeSlot(p);
            if (slot < 0) { refused = $"all {Docks} slots are at sea; a harbour this size sails {Docks} ships at once"; return null; }
            if (p.Crew < crew) { refused = $"that cargo needs {crew} crew, the harbour has {p.Crew}"; return null; }
            ship.Slot = slot;
            p.Crew -= crew;
            p.Ships.Add(ship);
            p.Version++;
            return p;
        });
        if (refused is not null)
        {
            KingsmarchTown.AddToStore(player, crop, taken);
            return refused;
        }
        return $"Sent {taken} {crop} (value {value}, {crew} crew) to {port.Name}; back in {Travel(port):0} s.";
    }

    // ---- The two halves of a trip the port window draws: loading a dock slot, then sailing it ----

    /// <summary>Loads farm produce onto one of the town's three ships: the 貨物 box the port window draws from the dock
    /// slots. The cargo leaves the farm's store here and is only taken off the map when the ship comes back with barter.
    /// </summary>
    /// <param name="player">The player whose town it is.</param>
    /// <param name="slot">The dock slot (0 to 2), which is the ship card the port window draws.</param>
    /// <param name="crop">The store's own name for what is being loaded, as <c>/village store</c> lists it.</param>
    /// <param name="count">How many units to load.</param>
    public static string Load(IPlayer player, int slot, string crop, int count)
    {
        Load();
        if (slot < 0 || slot >= Docks) return $"no dock slot {slot}: the harbour has {Docks}";
        if (count <= 0) return "count must be positive";
        if (!_values.TryGetValue(crop, out int each)) return $"{crop} is not something the ports buy";
        var harbour = Harbour(player.Character ?? "");
        if (harbour.Ships.Any(s => s.Slot == slot))
            return $"slot {slot} is at sea; it has to come home before it takes cargo";
        int have = KingsmarchTown.Stored(player, crop);
        if (have < count) return $"the town has {have} {crop}, not {count}";
        int taken = KingsmarchTown.TakeFromStore(player, crop, count);
        if (taken <= 0) return $"the town has no {crop} to load";
        int value = 0;
        string? refused = null;
        Change(player.Character ?? "", p =>
        {
            if (p.Ships.Any(s => s.Slot == slot))
            {
                refused = $"slot {slot} is at sea; it has to come home before it takes cargo";
                return null;
            }
            var dock = p.Docks[slot];
            // A hold loaded twice with a near-full count cannot wrap its total into a negative one.
            dock.Cargo[crop] = (int)Math.Min((long)dock.Cargo.GetValueOrDefault(crop) + taken, int.MaxValue);
            value = Worth(dock.Cargo);
            p.Version++;
            return p;
        });
        if (refused is not null)
        {
            KingsmarchTown.AddToStore(player, crop, taken);
            return refused;
        }
        // Say what the hold is worth and, when it is not yet worth a trip, what a port asks for: the panel's own
        // MinShipmentValue gate gives the same answer, and a player who has loaded too little finds out here.
        return value < MinCargoValue
            ? $"Loaded {taken} {crop} into slot {slot}; its cargo is worth {value}, and a port wants at least {MinCargoValue}."
            : $"Loaded {taken} {crop} into slot {slot}; its cargo is worth {value}, enough for a trip.";
    }

    /// <summary>Sails the ship in a dock slot for one of the ports: the trip starts here, the crew is tied up until it
    /// comes home, and the slot is free again once it does. The checks (slot, cargo worth, crew, and a destination the
    /// ships actually sail to) all pass before the harbour changes; the save re-checks the slot and the crew, so two
    /// sails racing for one card cannot both win.</summary>
    /// <param name="player">The player whose town it is.</param>
    /// <param name="slot">The dock slot whose cargo sails (0 to 2).</param>
    /// <param name="portNumber">The port as <c>/port</c> numbers it, 1 for the first row of
    /// <c>VillageShippingPorts</c>; 0 sails for the port already chosen with <see cref="Route"/>, which is the
    /// order the client's own window does it in.</param>
    public static string Sail(IPlayer player, int slot, int portNumber)
    {
        Load();
        if (slot < 0 || slot >= Docks) return $"no dock slot {slot}: the harbour has {Docks}";
        var harbour = Harbour(player.Character ?? "");
        if (portNumber == 0)
        {
            // No port on the command line: the card's own choice is the destination, the way the panel's
            // 選擇港口 button sets it before 啟航 is offered.
            string chosen = harbour.Docks[slot].Port;
            int row = _ports.FindIndex(p => p.Id == chosen);
            if (row < 0) return $"no port chosen for slot {slot}: /port route {slot} <port>, or give one on this command";
            portNumber = row + 1;
        }
        if (portNumber < 1 || portNumber > _ports.Count) return $"no port {portNumber}: /port lists them 1 to {_ports.Count}";
        var port = _ports[portNumber - 1];
        if (!IsDestination(port)) return $"{port.Name} is the home port: the ships sail from here, not to it";
        if (harbour.Ships.Any(s => s.Slot == slot)) return $"slot {slot} is already at sea";
        var cargo = new Dictionary<string, int>(harbour.Docks[slot].Cargo, StringComparer.OrdinalIgnoreCase);
        int worth = Worth(cargo);
        if (worth < MinCargoValue)
            return cargo.Count == 0
                ? $"slot {slot} is empty: /port load {slot} <crop> <count> puts the cargo in it"
                : $"slot {slot} carries {worth} worth of cargo; a port wants at least {MinCargoValue}";
        int value = ShipmentValue(worth);
        int crew = CrewRequired(value);
        if (harbour.Crew < crew) return $"that cargo needs {crew} crew, the harbour has {harbour.Crew}";
        double clock = Clock();
        var ship = new Ship
        {
            Slot = slot, Port = port.Id, Value = value, Crew = crew, Sent = clock, Returns = clock + Travel(port), Cargo = cargo,
        };
        string? refused = null;
        Change(player.Character ?? "", p =>
        {
            if (p.Ships.Any(s => s.Slot == slot)) { refused = $"slot {slot} is already at sea"; return null; }
            if (p.Crew < crew) { refused = $"that cargo needs {crew} crew, the harbour has {p.Crew}"; return null; }
            p.Crew -= crew;
            p.Ships.Add(ship);
            p.Docks[slot] = new Dock();
            p.Version++;
            return p;
        });
        if (refused is not null) return refused;
        return $"Slot {slot} sails for {port.Name} with {value} worth of cargo ({crew} crew); back in {Travel(port):0} s.";
    }

    /// <summary>Picks the port a dock slot is bound for, the panel's <c>選擇港口</c> step: the card then draws the
    /// destination and its travel time before anything is loaded, and <c>/port sail &lt;slot&gt;</c> needs nothing else.
    /// Kingsmarch itself (the table's row 0) is not a destination, so the card can never be bound for the town.</summary>
    /// <param name="player">The player whose town it is.</param>
    /// <param name="slot">The dock slot to bind (0 to 2).</param>
    /// <param name="portNumber">The port as <c>/port</c> numbers it.</param>
    public static string Route(IPlayer player, int slot, int portNumber)
    {
        Load();
        if (slot < 0 || slot >= Docks) return $"no dock slot {slot}: the harbour has {Docks}";
        if (portNumber < 1 || portNumber > _ports.Count) return $"no port {portNumber}: /port lists them 1 to {_ports.Count}";
        var port = _ports[portNumber - 1];
        if (!IsDestination(port)) return $"{port.Name} is the home port: the ships sail from here, not to it";
        string? refused = null;
        Change(player.Character ?? "", p =>
        {
            if (p.Ships.Any(s => s.Slot == slot)) { refused = $"slot {slot} is at sea; its destination is already set"; return null; }
            p.Docks[slot].Port = port.Id;
            p.Version++;
            return p;
        });
        if (refused is not null) return refused;
        return $"Slot {slot} is bound for {port.Name} (distance {port.Distance}, risk {Failure(port) * 100:0}%, {Travel(port):0} s each way).";
    }

    /// <summary>Empties a dock slot back into the farm's store, the stand-in for the port window's drop button.</summary>
    /// <param name="player">The player whose town it is.</param>
    /// <param name="slot">The dock slot to empty (0 to 2).</param>
    public static string Unload(IPlayer player, int slot)
    {
        Load();
        if (slot < 0 || slot >= Docks) return $"no dock slot {slot}: the harbour has {Docks}";
        var harbour = Harbour(player.Character ?? "");
        if (harbour.Ships.Any(s => s.Slot == slot)) return $"slot {slot} is at sea; there is nothing to unload";
        Dictionary<string, int> cargo = [];
        string? refused = null;
        Change(player.Character ?? "", p =>
        {
            if (p.Ships.Any(s => s.Slot == slot)) { refused = $"slot {slot} is at sea; there is nothing to unload"; return null; }
            cargo = new Dictionary<string, int>(p.Docks[slot].Cargo, StringComparer.OrdinalIgnoreCase);
            if (cargo.Count == 0) return null; // nothing to move: the save stays as it is
            // Only the hold is emptied: the port the card is bound for is what the player picked with /port route, and
            // taking the cargo back out to load a different amount should not cost them that choice again.
            p.Docks[slot].Cargo.Clear();
            p.Version++;
            return p;
        });
        if (refused is not null) return refused;
        if (cargo.Count == 0) return $"slot {slot} is already empty";
        int back = 0;
        foreach (var (crop, count) in cargo) back += KingsmarchTown.AddToStore(player, crop, count);
        return $"Unloaded slot {slot}: {back} unit(s) back in the farm's store.";
    }

    /// <summary>What a pile of cargo is worth, by <c>VillageExports</c>' ShippingValue per row. The sum is long and
    /// clamped, so a hold full of expensive cargo cannot wrap its value into a negative one.</summary>
    static int Worth(Dictionary<string, int> cargo)
    {
        long value = 0;
        foreach (var (crop, count) in cargo) value += (long)_values.GetValueOrDefault(crop, 0) * count;
        return (int)Math.Min(value, int.MaxValue);
    }

    // ---- What the town window draws, as the client wants it on the wire (S→C 0x2c9) --------------

    /// <summary>One destination as the port panel lists it: <c>Row</c> is its row in
    /// <c>VillageShippingPorts</c>, which is what the panel refers to a port by (row 0 is Kingsmarch itself, the home
    /// port the ships sail from — it is listed so the row numbers stay the table's own).</summary>
    public sealed record PortView(int Row, string Id, string Name, int Distance, double Risk);

    /// <summary>One of the town's three ships as the port window draws it: <c>Cargo</c> is the 16-entry array indexed by
    /// <c>VillageExports</c> row, the same sixteen u32 the panel's own send action carries
    /// (<c>0x06</c>: slot, port, failure, 16 × u32). A dock that has not sailed yet carries the port chosen for it and
    /// what has been loaded; one at sea carries the trip. <c>FailureRow</c> is -1 until a trip goes wrong.</summary>
    public sealed record CardView(int Slot, int PortRow, int FailureRow, int Value, int Crew, double Returns, bool AtSea,
        int[] Cargo);

    /// <summary>Every port the panel can refer to, in table order.</summary>
    public static IReadOnlyList<PortView> PortList()
    {
        Load();
        var list = new List<PortView>(_ports.Count);
        for (int i = 0; i < _ports.Count; i++)
            list.Add(new PortView(i, _ports[i].Id, _ports[i].Name, _ports[i].Distance, _ports[i].Risk));
        return list;
    }

    /// <summary>The three ships, one per card the port window draws whether or not anything is loaded in it.</summary>
    public static IReadOnlyList<CardView> Cards(string who)
    {
        Load();
        var harbour = Harbour(who);
        var list = new List<CardView>(Docks);
        for (int slot = 0; slot < Docks; slot++)
        {
            var atSea = harbour.Ships.FirstOrDefault(s => s.Slot == slot);
            if (atSea is not null)
            {
                list.Add(new CardView(slot, _ports.FindIndex(p => p.Id == atSea.Port),
                    atSea.Loss.Length == 0 ? -1 : _losses.FindIndex(l => l.Id == atSea.Loss),
                    atSea.Value, atSea.Crew, atSea.Returns, true, Pack(atSea.Cargo)));
                continue;
            }
            var dock = harbour.Docks[slot];
            list.Add(new CardView(slot, _ports.FindIndex(p => p.Id == dock.Port), -1, Worth(dock.Cargo), 0, 0, false,
                Pack(dock.Cargo)));
        }
        return list;
    }

    /// <summary>How many versions of this harbour have been drawn: what tells a push that nothing has changed.</summary>
    public static int Version(string who) => Harbour(who).Version;

    /// <summary>A cargo pile as the wire wants it: 16 counts, one per <c>VillageExports</c> row.</summary>
    static int[] Pack(Dictionary<string, int> cargo)
    {
        var wire = new int[WireCargoRows];
        foreach (var (key, count) in cargo)
            if (_export.TryGetValue(key, out int row) && row >= 0 && row < WireCargoRows) wire[row] += count;
        return wire;
    }

    /// <summary>One shipment that came home, waiting to be collected, as the port panel lists it.</summary>
    public sealed record RewardView(int Index, string Port, int Barter);

    /// <summary>What came home and waits to be collected.</summary>
    public static IReadOnlyList<RewardView> Rewards(string who)
    {
        Load();
        var harbour = Harbour(who);
        return [.. harbour.Rewards.Select((r, i) => new RewardView(i, r.Port, r.Barter))];
    }

    /// <summary>The panel's collect button: <paramref name="index"/> &lt; 0 takes every reward, otherwise just that one.
    /// Returns the barter collected.</summary>
    public static int TakeRewards(string who, int index)
    {
        Load();
        int taken = 0;
        Change(who, harbour =>
        {
            if (index < 0)
            {
                foreach (var reward in harbour.Rewards) taken += reward.Barter;
                harbour.Rewards.Clear();
            }
            else if (index < harbour.Rewards.Count)
            {
                taken = harbour.Rewards[index].Barter;
                harbour.Rewards.RemoveAt(index);
            }
            if (taken == 0) return null; // nothing to collect: the save stays as it is
            harbour.Barter += taken;
            harbour.Version++;
            return harbour;
        });
        return taken;
    }

    /// <summary>A port's six-number shipment quota, as the panel's per-port row shows it. Every port starts at the six
    /// zeros the client draws — the table carries no quota numbers of its own — and a reroll writes the next set.</summary>
    public static int[] Quota(string who, int portRow)
    {
        Load();
        if (portRow < 0 || portRow >= _ports.Count) return new int[6];
        var harbour = Harbour(who);
        return harbour.Quotas.TryGetValue(_ports[portRow].Id, out int[]? quota) ? (int[])quota.Clone() : new int[6];
    }

    /// <summary>The panel's quota reroll: <see cref="QuotaRerollCost"/> gold (the
    /// <c>VillageSharedConstants.QuotaRerollCost</c> row) buys a new set of six numbers for that port. Returns what
    /// happened, and the new quota.</summary>
    public static string RerollQuota(string who, int portRow, int cost, out int[] quota)
    {
        Load();
        quota = Quota(who, portRow);
        if (portRow < 0 || portRow >= _ports.Count) return $"no port row {portRow}";
        var harbour = Harbour(who);
        if (!Spend(who, cost)) return $"a reroll costs {cost} gold and the treasury has {harbour.Gold}";
        var fresh = quota;
        Change(who, port =>
        {
            var next = new int[6];
            for (int i = 0; i < next.Length; i++) next[i] = Random.Shared.Next(1, 7) * 10;
            port.Quotas[_ports[portRow].Id] = next;
            fresh = next;
            port.Version++;
            return port;
        });
        quota = fresh;
        return $"{_ports[portRow].Name} quota rerolled for {cost} gold";
    }

    /// <summary>Drops the ship in a dock slot without paying for it: what the panel's <c>遺棄</c> button asks for, and the
    /// irreversible end of a trip. The slot is what the card is drawn under, so the ship is found by its own
    /// <see cref="Ship.Slot"/> and not by its place in the list.</summary>
    public static bool DropShip(string who, int slot)
    {
        bool dropped = false;
        Change(who, harbour =>
        {
            int at = harbour.Ships.FindIndex(s => s.Slot == slot);
            if (at < 0) return null;
            harbour.Ships.RemoveAt(at);
            harbour.Version++;
            dropped = true;
            return harbour;
        });
        return dropped;
    }

    /// <summary>The town's purse, as the town window shows it.</summary>
    public static (int Crew, int Barter, long Gold) Vault(string who)
    {
        var harbour = Harbour(who);
        return (harbour.Crew, harbour.Barter, harbour.Gold);
    }

    /// <summary>Sends a shipment the port panel described: 16 cargo counts indexed by VillageExports row. Everything is
    /// checked before the town gives up anything, so a refused shipment never destroys the cargo it wanted: the port
    /// row is a real row and one the ships sail to (row 0 is Kingsmarch itself, the home port), every cargo slot names
    /// a VillageExports row through <c>_exportByRow</c> (a slot with no row is skipped, not a crash), the total — long,
    /// so a full hold of expensive cargo cannot wrap the per-line multiplication or the sum — clears
    /// <see cref="MinCargoValue"/>, and the crew and a free dock slot are there. Only then does the store pay, and a
    /// trip the store can no longer cover whole is refused with its cargo back. Returns the message to tell the
    /// player, so the panel's click has the same story as <c>/port send</c>.</summary>
    public static string SendCargo(IPlayer player, int portRow, int[] cargo, out int value, out int crew)
    {
        Load();
        value = 0;
        crew = 0;
        if (cargo is null) return "the panel sent no cargo";
        if (portRow < 0 || portRow >= _ports.Count) return $"no port row {portRow}";
        var port = _ports[portRow];
        if (!IsDestination(port)) return $"{port.Name} is the home port: the ships sail from here, not to it";
        // What the panel asked for, reduced to what the store can pay and priced in long. A slot with no export row,
        // or one whose export is worth nothing to the ports (the dust), is skipped: taking it would be taking cargo
        // from the store for nothing.
        var wanted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        for (int row = 0; row < cargo.Length && row < WireCargoRows; row++)
        {
            if (cargo[row] <= 0) continue;
            string id = _exportByRow[row];
            if (string.IsNullOrEmpty(id)) continue;
            int each = _values.GetValueOrDefault(id, 0);
            if (each <= 0) continue;
            int have = KingsmarchTown.Stored(player, id);
            int take = Math.Min(have, cargo[row]);
            if (take <= 0) continue;
            wanted[id] = take;
            total += (long)each * take;
        }
        if (total <= 0) return "the town has none of that cargo";
        if (total < MinCargoValue) return $"that cargo is worth {total}; a port wants at least {MinCargoValue}";
        value = ShipmentValue((int)Math.Min(total, int.MaxValue));
        crew = CrewRequired(value);
        var harbour = Harbour(player.Character ?? "");
        if (harbour.Crew < crew) return $"that cargo needs {crew} crew, the harbour has {harbour.Crew}";
        if (FreeSlot(harbour) < 0) return $"all {Docks} slots are at sea; a harbour this size sails {Docks} ships at once";
        // Every check passed: the store pays now, and the trip carries what it actually got (the store may have
        // shrunk between the look and this take).
        var taken = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long actual = 0;
        foreach (var (id, want) in wanted)
        {
            int got = KingsmarchTown.TakeFromStore(player, id, want);
            if (got <= 0) continue;
            taken[id] = got;
            actual += (long)_values.GetValueOrDefault(id, 0) * got;
        }
        if (actual < MinCargoValue)
        {
            // The store gave less than the checks saw: what came out goes back, and the shipment is refused whole
            // rather than sailed at a value the player did not ask for.
            foreach (var (id, got) in taken) KingsmarchTown.AddToStore(player, id, got);
            return $"that cargo is worth {actual}; a port wants at least {MinCargoValue}";
        }
        value = ShipmentValue((int)Math.Min(actual, int.MaxValue));
        crew = CrewRequired(value);
        int needed = crew; // the closure below reads the number: an out parameter cannot be captured
        double clock = Clock();
        var ship = new Ship { Port = port.Id, Value = value, Crew = crew, Sent = clock, Returns = clock + Travel(port), Cargo = taken };
        string? refused = null;
        Change(player.Character ?? "", p =>
        {
            // A shipment the panel described has no slot of its own, so it takes the first free one: the card it is
            // drawn under is the slot, and two ships sharing one would hide the second. The slot and the crew are
            // re-checked inside the save, and a trip refused there is a trip whose cargo goes back.
            int slot = FreeSlot(p);
            if (slot < 0) { refused = $"all {Docks} slots are at sea; a harbour this size sails {Docks} ships at once"; return null; }
            if (p.Crew < needed) { refused = $"that cargo needs {needed} crew, the harbour has {p.Crew}"; return null; }
            ship.Slot = slot;
            p.Crew -= needed;
            p.Ships.Add(ship);
            p.Version++;
            return p;
        });
        if (refused is not null)
        {
            foreach (var (id, got) in taken) KingsmarchTown.AddToStore(player, id, got);
            value = 0;
            crew = 0;
            return refused;
        }
        return $"Sent {value} worth of cargo to {port.Name} ({crew} crew, back in {Travel(port):0} s).";
    }
}
