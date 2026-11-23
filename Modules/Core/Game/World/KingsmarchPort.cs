using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServer.Modding;

/// <summary>Kingsmarch's port: shipments of what the farm grows, sent to one of the Kalguuran ports and paid back in
/// barter. The client's own port window cannot be built in this client build (its S→C 0x7b kind 0x58 maps to an empty
/// stub), so the whole flow lives on <c>/port …</c>, the way the farm lives on <c>/village</c>.
/// Shipments are the town's own state: a ship leaves with the cargo the farm's store gave up, takes time proportional to
/// the port's distance, may be lost to the risks the port is known for, and pays barter when it comes home. A lost trip is
/// not a coin flip with no story: the client's own failure table says what went wrong and what it cost.</summary>
internal static class KingsmarchPort
{
    const string Area = "KalguuranSettlersLeague";
    const string PortsFile = "data/tables/VillageShippingPorts.json";
    const string ExportsFile = "data/tables/VillageExports.json";
    const string FailuresFile = "data/tables/VillageShippingFailures.json";
    const string ResourcesFile = "data/tables/VillageResources.json";
    const string DataFile = "data/kingsmarch-port.json";

    /// <summary>What a trip has to be worth before a port takes it. The tables' <c>ExpectedShipmentValue</c> is 150, but a
    /// young town's farm holds a handful of crops worth 12-24 each, so the skeleton asks for a cargo that fits one harvest
    /// and leaves the real quota for when shipments are the town's income and not a test.</summary>
    const int MinCargoValue = 40;

    /// <summary>Barter a successful shipment brings home, as a multiple of its value. Real Kingsmarch pays in barter
    /// currency whose exact table is not in the client's data files, so the skeleton keeps one number and says so.</summary>
    const double BarterMultiplier = 1.4;

    static readonly Regex Markup = new("<[^>]*>|[{}\"]", RegexOptions.Compiled);

    static readonly object _lock = new();
    static readonly List<Port> _ports = [];
    static readonly List<Loss> _losses = [];
    static readonly Dictionary<string, int> _values = new(StringComparer.OrdinalIgnoreCase);
    // The row a resource has in VillageResources.json: the failure table names resources by that row, not by id.
    static readonly Dictionary<string, int> _rows = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, SavedPort> _saved = [];
    static bool _loaded;

    sealed record Port(string Id, string Name, int Distance, double Risk);

    /// <summary>One way a shipment can go wrong, straight out of <c>VillageShippingFailures.json</c>:
    /// <c>Rows</c> = the resources it can hit (empty = the whole cargo), <c>Lost</c>/<c>Bonus</c> = percent of the cargo and
    /// of the return, <c>Delay</c> = percent of the travel time, <c>Crew</c> = crew members that do not come home,
    /// <c>Weight</c> = how often it happens relative to the rest.</summary>
    sealed record Loss(string Id, string Text, int[] Rows, int Lost, int Bonus, int Delay, int Crew, int Weight);

    /// <summary>A ship on its way: what it carries, where to and when it is due back.</summary>
    sealed class Ship
    {
        public string Port { get; set; } = "";
        public int Value { get; set; }
        public int Crew { get; set; }
        public double Sent { get; set; }
        public double Returns { get; set; }
        // How often this trip was pushed back by weather (a delay is a once-per-trip event, not a loop).
        public int Delays { get; set; }
        public Dictionary<string, int> Cargo { get; set; } = [];
    }

    /// <summary>One character's harbour: the crew it has, the barter it earned, and the ships at sea.</summary>
    sealed class SavedPort
    {
        public int Crew { get; set; } = 3;
        public int Barter { get; set; }
        /// <summary>The town's treasury: gold the player funded into Kingsmarch (Johan's Fund the Treasury).</summary>
        public long Gold { get; set; }
        public List<Ship> Ships { get; set; } = [];
    }

    /// <summary>Loads the ports, what each resource is worth, and the ways a trip can go wrong, once per module load.</summary>
    static void Load()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                using var ports = JsonDocument.Parse(File.ReadAllBytes(PortsFile));
                foreach (var row in ports.RootElement.EnumerateArray())
                {
                    string id = row.GetProperty("Id").GetString() ?? "";
                    _ports.Add(new Port(
                        id,
                        row.GetProperty("Name").GetString() ?? id,
                        row.GetProperty("DistanceFromPort").GetInt32(),
                        row.GetProperty("RiskMultiplier").GetDouble()));
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException)
            {
                Console.WriteLine($"[gateway] {PortsFile}: {ex.Message}");
            }
            try
            {
                using var exports = JsonDocument.Parse(File.ReadAllBytes(ExportsFile));
                foreach (var row in exports.RootElement.EnumerateArray())
                {
                    int value = row.GetProperty("ShippingValue").GetInt32();
                    if (value <= 0) continue;
                    _values[row.GetProperty("Id").GetString() ?? ""] = value;
                    // The farm's store is keyed by the crop's name ("Wheat"), the exports table by its id; keep both.
                    if (row.TryGetProperty("Name", out var name) && name.GetString() is { Length: > 0 } display)
                        _values[display] = value;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException)
            {
                Console.WriteLine($"[gateway] {ExportsFile}: {ex.Message}");
            }
            try
            {
                using var resources = JsonDocument.Parse(File.ReadAllBytes(ResourcesFile));
                int row = 0;
                foreach (var entry in resources.RootElement.EnumerateArray())
                {
                    string id = entry.ValueKind == JsonValueKind.Object
                        ? entry.TryGetProperty("Id", out var idField) ? idField.GetString() ?? "" : ""
                        : entry.GetString() ?? "";
                    if (id.Length > 0) _rows[id] = row;
                    row++;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
            {
                Console.WriteLine($"[gateway] {ResourcesFile}: {ex.Message}");
            }
            try
            {
                using var failures = JsonDocument.Parse(File.ReadAllBytes(FailuresFile));
                foreach (var row in failures.RootElement.EnumerateArray())
                {
                    string id = row.GetProperty("Id").GetString() ?? "";
                    // "NULL" is the table's empty slot and a commandeered ship waits for the player, so neither is rolled.
                    if (id.Length == 0 || id is "NULL" or "ShipCommandeered") continue;
                    int weight = row.TryGetProperty("__col9", out var w) ? w.GetInt32() : 0;
                    if (weight <= 0) continue;
                    var rows = new List<int>();
                    if (row.TryGetProperty("Resource", out var list))
                        foreach (var r in list.EnumerateArray()) rows.Add(r.GetInt32());
                    _losses.Add(new Loss(
                        id,
                        row.GetProperty("Text").GetString() ?? id,
                        [.. rows],
                        row.GetProperty("ResourceLostPercent").GetInt32(),
                        row.GetProperty("PortBonusLostPercent").GetInt32(),
                        row.TryGetProperty("__col5", out var delay) ? delay.GetInt32() : 0,
                        row.GetProperty("CrewMembersLost").GetInt32(),
                        weight));
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException)
            {
                Console.WriteLine($"[gateway] {FailuresFile}: {ex.Message}");
            }
        }
    }

    /// <summary>The character's harbour, read from disk once per module load.</summary>
    static SavedPort Harbour(string who)
    {
        lock (_saved)
        {
            if (_saved.TryGetValue(who, out var port)) return port;
            try
            {
                if (File.Exists(DataFile))
                {
                    var all = JsonSerializer.Deserialize<Dictionary<string, SavedPort>>(File.ReadAllBytes(DataFile));
                    if (all is not null) foreach (var (key, value) in all) _saved[key] = value;
                    if (_saved.TryGetValue(who, out var loaded)) return loaded;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Console.WriteLine($"[gateway] {DataFile} is unreadable, starting the harbour over: {ex.Message}");
            }
            return _saved[who] = new SavedPort();
        }
    }

    static void Save()
    {
        lock (_saved)
        {
            try { File.WriteAllBytes(DataFile, JsonSerializer.SerializeToUtf8Bytes(_saved, new JsonSerializerOptions { WriteIndented = true })); }
            catch (IOException ex) { Console.WriteLine($"[gateway] {DataFile}: {ex.Message}"); }
        }
    }

    /// <summary>The barter the character's harbour holds. The recruiter is paid from it, so the town's two businesses
    /// share one purse instead of inventing a second currency.</summary>
    public static int Barter(string who)
    {
        lock (_saved) return Harbour(who).Barter;
    }

    /// <summary>Pays <paramref name="cost"/> barter, or leaves the harbour alone and returns false when it cannot.</summary>
    public static bool PayBarter(string who, int cost)
    {
        lock (_saved)
        {
            var port = Harbour(who);
            if (cost <= 0) return true;
            if (port.Barter < cost) return false;
            port.Barter -= cost;
            Save();
            return true;
        }
    }

    /// <summary>The gold in the character's town treasury.</summary>
    public static long Gold(string who)
    {
        lock (_saved) return Harbour(who).Gold;
    }

    /// <summary>Puts gold into the treasury; the treasury after.</summary>
    public static long Fund(string who, long amount)
    {
        lock (_saved)
        {
            var port = Harbour(who);
            port.Gold += Math.Max(0, amount);
            Save();
            return port.Gold;
        }
    }

    /// <summary>Spends gold the treasury can afford, or nothing and false.</summary>
    public static bool Spend(string who, int amount)
    {
        lock (_saved)
        {
            var port = Harbour(who);
            if (amount <= 0) return true;
            if (port.Gold < amount) return false;
            port.Gold -= amount;
            Save();
            return true;
        }
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

    /// <summary>How many of the town's workers a shipment ties up: a small cargo needs one, a rich one a full complement.</summary>
    static int CrewRequired(int value) => Math.Clamp((int)Math.Ceiling(value / (double)MinCargoValue), 1, 5);

    /// <summary>A fixed number in [0,1) per shipment and purpose: the same trip always resolves the same way, so the player
    /// cannot re-roll a bad outcome by reloading the area.</summary>
    static double Die(Ship ship, double salt) => Math.Abs(Math.Sin(ship.Sent * salt + ship.Value * 78.233 + ship.Crew * 3.7)) % 1.0;

    /// <summary>The failure this shipment suffers: a weighted pick among the rows that can hit its cargo.</summary>
    static Loss? Pick(Ship ship)
    {
        var fits = _losses.Where(l => l.Rows.Length == 0
            || ship.Cargo.Keys.Any(c => _rows.TryGetValue(c, out int row) && l.Rows.Contains(row))).ToList();
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

    /// <summary>Every tick: ships that are due come home, or do not.</summary>
    public static Task Tick(IPlayer player, double now)
    {
        if (!string.Equals(player.AreaId, Area, StringComparison.Ordinal)) return Task.CompletedTask;
        Load();
        double clock = Environment.TickCount64 / 1000.0;
        List<Ship> due;
        var harbour = Harbour(player.Character ?? "");
        lock (_lock)
        {
            due = [.. harbour.Ships.Where(s => clock >= s.Returns)];
            foreach (var ship in due) harbour.Ships.Remove(ship);
        }
        if (due.Count == 0) return Task.CompletedTask;
        var lines = new List<string>();
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
                    lock (_lock) harbour.Ships.Add(ship);
                    lines.Add($"{Clean(loss.Text)} The shipment to {to} is {loss.Delay}% late.");
                    continue;
                }
                int home = Math.Max(0, ship.Crew - loss.Crew);
                harbour.Crew += home;
                double kept = (1 - loss.Lost / 100.0) * (1 - loss.Bonus / 100.0);
                int paid = (int)Math.Round(ship.Value * BarterMultiplier * kept);
                harbour.Barter += paid;
                lines.Add($"{Clean(loss.Text)} ({paid} barter for {ship.Value} value, {home} of {ship.Crew} crew home.)");
                continue;
            }
            // The crew is tied up while the ship is away and comes home with it.
            harbour.Crew += ship.Crew;
            int barter = (int)Math.Round(ship.Value * BarterMultiplier);
            harbour.Barter += barter;
            lines.Add($"A ship came back from {to}: {barter} barter ({ship.Value} value sent, {ship.Crew} crew home).");
        }
        Save();
        return lines.Count == 0 ? Task.CompletedTask : player.Tell(string.Join(" | ", lines));
    }

    /// <summary>The table's texts carry the client's own markup (<c>&lt;kalguurred&gt;{…}</c>); chat shows them plain.</summary>
    static string Clean(string text) => Markup.Replace(text, "").Trim();

    /// <summary>The harbour as one report: the ports with their distance and risk, the ships at sea and what is banked.</summary>
    public static string Report(IPlayer player)
    {
        Load();
        var harbour = Harbour(player.Character ?? "");
        double clock = Environment.TickCount64 / 1000.0;
        var lines = new List<string>();
        for (int i = 0; i < _ports.Count; i++)
            lines.Add($"{i + 1} {_ports[i].Name} (distance {_ports[i].Distance}, risk {Failure(_ports[i]) * 100:0}%)");
        lock (_lock)
        {
            if (harbour.Ships.Count == 0) lines.Add("ships at sea: none");
            foreach (var ship in harbour.Ships)
                lines.Add($"at sea: {ship.Cargo.Count} kind(s) worth {ship.Value} → {ship.Port}, back in {Math.Max(0, ship.Returns - clock):0} s");
        }
        lines.Add($"crew {harbour.Crew}, barter {harbour.Barter}");
        lines.Add("commands: /port send <port> <crop> <count>");
        return string.Join(" | ", lines);
    }

    /// <summary>Puts more hands on the docks: crew is what a shipment ties up, and it comes home with the ship.</summary>
    public static string Crew(IPlayer player, int add)
    {
        var harbour = Harbour(player.Character ?? "");
        lock (_lock) harbour.Crew += Math.Clamp(add, 1, 5);
        Save();
        return $"The harbour now has {harbour.Crew} crew.";
    }

    /// <summary>Sends a shipment: takes the cargo out of the farm's store, ties up crew and starts the clock.</summary>
    public static string Send(IPlayer player, int portNumber, string crop, int count)
    {
        Load();
        if (portNumber < 1 || portNumber > _ports.Count) return $"no port {portNumber}: /port lists them";
        var port = _ports[portNumber - 1];
        if (count <= 0) return "count must be positive";
        if (!_values.TryGetValue(crop, out int each)) return $"{crop} is not something the ports buy";
        int have = KingsmarchTown.Stored(player, crop);
        if (have < count) return $"the town has {have} {crop}, not {count}";
        int value = each * count;
        if (value < MinCargoValue) return $"{count} {crop} is worth {value}; a port wants at least {MinCargoValue} worth of cargo";
        var harbour = Harbour(player.Character ?? "");
        int crew = CrewRequired(value);
        lock (_lock) if (harbour.Crew < crew) return $"that cargo needs {crew} crew, the harbour has {harbour.Crew}";
        int taken = KingsmarchTown.TakeFromStore(player, crop, count);
        if (taken <= 0) return $"the town has no {crop} to send";
        double clock = Environment.TickCount64 / 1000.0;
        var ship = new Ship
        {
            Port = port.Id, Value = each * taken, Crew = crew, Sent = clock, Returns = clock + Travel(port),
            Cargo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [crop] = taken },
        };
        lock (_lock)
        {
            harbour.Crew -= crew;
            harbour.Ships.Add(ship);
        }
        Save();
        return $"Sent {taken} {crop} (value {ship.Value}, {crew} crew) to {port.Name}; back in {Travel(port):0} s.";
    }
}
