using System.Text.Json;

namespace GameServer.Modules.Village.Mapping;

/// <summary>
/// Everything the town owns about its Atlas Runner mapping line, for one player (one account), saved in
/// <c>data/kingsmarch-mapping.json</c>.
/// </summary>
public sealed class MappingSave
{
    /// <summary>The map devices the town owns: each one has a queue, a team, and what it is running.</summary>
    public List<MappingDeviceState> Devices { get; set; } = [];

    /// <summary>The Atlas Runners the town has hired, alive and dead alike.</summary>
    public List<MappingRunner> Runners { get; set; } = [];

    /// <summary>The candidates waiting to be hired (the recruitment panel).</summary>
    public List<MappingRecruit> Recruits { get; set; } = [];

    /// <summary>The town's treasury: the gold the mapping line earns and the other Kingsmarch lines share.</summary>
    public long TreasuryGold { get; set; }

    /// <summary>When the mapping line last moved forward, in unix seconds.</summary>
    public double LastAdvance { get; set; }

    /// <summary>The id the next hired runner gets.</summary>
    public int NextRunnerId { get; set; } = 1;

    /// <summary>The id the next map device gets.</summary>
    public int NextDeviceId { get; set; } = 1;
}

/// <summary>
/// One map device: a queue of maps, the runners working it, the run in flight, and what came back.
/// </summary>
public sealed class MappingDeviceState
{
    /// <summary>The device's own id.</summary>
    public int Id { get; set; }

    /// <summary>The device's level, 1..4: the client's four bands (it may run T5, T10, T16 or T17). It decides the
    /// tiers the device may run and adds a little power.</summary>
    public int Level { get; set; } = 1;

    /// <summary>The maps waiting on the device, at most <see cref="MappingBalance.MaxQueue"/> of them.</summary>
    public List<MappingQueueEntry> Queue { get; set; } = [];

    /// <summary>The ids of the runners working this device, at most <see cref="MappingBalance.MaxWorkers"/> of them.</summary>
    public List<int> Workers { get; set; } = [];

    /// <summary>Whether the device stops after the map it is in (the panel's "Suspend Mapping" button).</summary>
    public bool SuspendAfterCurrent { get; set; }

    /// <summary>The run in flight, or nothing while the device is idle.</summary>
    public MappingRun? Current { get; set; }

    /// <summary>What came back from finished maps and has not been collected yet.</summary>
    public List<MappingReward> Rewards { get; set; } = [];

    /// <summary>Whether a runner of this device has been taken hostage by the bandits.</summary>
    public bool Kidnapped { get; set; }

    /// <summary>The runner the bandits hold, when there is one.</summary>
    public int KidnappedRunnerId { get; set; }

    /// <summary>When the hostage must be answered by, in unix seconds.</summary>
    public double KidnapDeadline { get; set; }
}

/// <summary>
/// One map in a device's queue: what it is, how hard it is, and what makes it special.
/// </summary>
public sealed class MappingQueueEntry
{
    /// <summary>The map type's own hash, as the client's map tables carry it.</summary>
    public uint MapTypeHash { get; set; }

    /// <summary>The map's name, as the panel shows it.</summary>
    public string Name { get; set; } = "";

    /// <summary>The map's tier, 1..17.</summary>
    public int Tier { get; set; }

    /// <summary>The map's rarity: 0 white, 1 blue, 2 yellow, 3 gold.</summary>
    public byte Rarity { get; set; }

    /// <summary>How many modifiers the map carries.</summary>
    public int AffixCount { get; set; }

    /// <summary>What makes the map special: "" (nothing), "Boss", "Guardian" or "Season".</summary>
    public string Special { get; set; } = "";

    /// <summary>How many scarabs the map holds.</summary>
    public int Scarabs { get; set; }
}

/// <summary>
/// One map a device is running: what it is, when it started and ends, and what it is expected to yield.
/// </summary>
public sealed class MappingRun
{
    /// <summary>The map being run.</summary>
    public MappingQueueEntry Entry { get; set; } = new();

    /// <summary>When the run started, in unix seconds.</summary>
    public double StartedAt { get; set; }

    /// <summary>When the run ends, in unix seconds.</summary>
    public double EndsAt { get; set; }

    /// <summary>How much of the map the team is expected to clear, in percent (0..100).</summary>
    public int Completion { get; set; }

    /// <summary>How long the map is expected to take, in minutes.</summary>
    public double Minutes { get; set; }

    /// <summary>How likely the run is to kill an Atlas Runner, in percent.</summary>
    public double DeathChance { get; set; }
}

/// <summary>
/// One Atlas Runner the town has hired: the worker's job, the specialty that makes a runner worth its wage, and
/// the device it works.
/// </summary>
public sealed class MappingRunner
{
    /// <summary>The runner's own id.</summary>
    public int Id { get; set; }

    /// <summary>The runner's name, as the panel shows it.</summary>
    public string Name { get; set; } = "";

    /// <summary>The runner's job class.</summary>
    public string Class { get; set; } = "";

    /// <summary>The runner's job level, 1..5: it sets the wage and how much skill relief the runner gives.</summary>
    public int Level { get; set; } = 1;

    /// <summary>The Atlas Runner specialty rank: 0 (no specialty at all) or 1..9 as the client has it. Each rank counts
    /// as <c>Rank / 5</c> of a team's power, and the mapping power scale tops out at 5.</summary>
    public int Rank { get; set; }

    /// <summary>The runner's wage, in gold per hour.</summary>
    public int Wage { get; set; }

    /// <summary>Whether the runner died in the Atlas (a dead runner is never used again).</summary>
    public bool Dead { get; set; }

    /// <summary>The device the runner works; 0 while the runner is not on one.</summary>
    public int DeviceId { get; set; }
}

/// <summary>
/// One candidate waiting to be hired: what the town would pay to hire it and what it would cost to keep.
/// </summary>
public sealed class MappingRecruit
{
    /// <summary>The candidate's name, as the panel shows it.</summary>
    public string Name { get; set; } = "";

    /// <summary>The candidate's job class.</summary>
    public string Class { get; set; } = "";

    /// <summary>The candidate's job level, 1..5.</summary>
    public int Level { get; set; } = 1;

    /// <summary>The Atlas Runner specialty rank: 0 (no specialty at all) or 1..9 as the client has it; the mapping power
    /// scale counts it up to 5.</summary>
    public int Rank { get; set; }

    /// <summary>The candidate's wage, in gold per hour.</summary>
    public int Wage { get; set; }

    /// <summary>What hiring this candidate costs, in gold.</summary>
    public int Cost { get; set; }
}

/// <summary>
/// One finished map waiting to be collected: what it paid, what it dropped, and which device it came from.
/// </summary>
public sealed class MappingReward
{
    /// <summary>The device the map was run on.</summary>
    public int DeviceId { get; set; }

    /// <summary>The map type's own hash, as the client's map tables carry it.</summary>
    public uint MapTypeHash { get; set; }

    /// <summary>The map's name, as the panel shows it.</summary>
    public string Name { get; set; } = "";

    /// <summary>The map's tier, 1..17.</summary>
    public int Tier { get; set; }

    /// <summary>How much of the map was cleared, in percent (0..100).</summary>
    public int Completion { get; set; }

    /// <summary>The gold the map paid into the treasury.</summary>
    public long Gold { get; set; }

    /// <summary>What the map dropped.</summary>
    public List<MappingDrop> Drops { get; set; } = [];

    /// <summary>The ids of the bonus stats the map granted.</summary>
    public List<int> BonusStats { get; set; } = [];

    /// <summary>When the map came back, in unix seconds.</summary>
    public double At { get; set; }

    /// <summary>Whether the reward has been collected.</summary>
    public bool Taken { get; set; }
}

/// <summary>
/// One item a finished map dropped: what it is, how many, and at what item level and rarity.
/// </summary>
public sealed record MappingDrop(uint TypeHash, int Count, int ItemLevel, byte Rarity);

/// <summary>
/// The mapping line's save file: one record per player, read once and written back after every change.
/// The path is relative to the repository root, like every other game data path.
/// </summary>
public static class MappingStore
{
    /// <summary>Where the mapping line's state is kept, relative to the repository root.</summary>
    public const string SaveFile = "data/kingsmarch-mapping.json";

    static readonly object _lock = new();
    static readonly Dictionary<string, MappingSave> _saved = new(StringComparer.Ordinal);
    static bool _loaded;

    /// <summary>
    /// The player's mapping record, read from disk on first use and empty until the town owns something.
    /// The record is the live one: change it, then call <see cref="Save"/> to write it back.
    /// </summary>
    /// <param name="who">The player's account name (the character's name, as the other Kingsmarch lines use it).</param>
    public static MappingSave Of(string who)
    {
        lock (_lock)
        {
            Load();
            if (_saved.TryGetValue(who, out var save)) return save;
            return _saved[who] = new MappingSave();
        }
    }

    /// <summary>
    /// Writes every player's mapping record back to disk: to a temporary file first, so a crash mid-write cannot
    /// leave a half-written save behind.
    /// </summary>
    public static void Save()
    {
        lock (_lock)
        {
            try
            {
                string? folder = Path.GetDirectoryName(SaveFile);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                string temp = SaveFile + ".tmp";
                File.WriteAllBytes(temp,
                    JsonSerializer.SerializeToUtf8Bytes(_saved, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, SaveFile, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[gateway] {SaveFile}: {ex.Message}");
            }
        }
    }

    /// <summary>Reads the save file once per process; a file that cannot be read leaves the towns empty.</summary>
    static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(SaveFile)) return;
            var all = JsonSerializer.Deserialize<Dictionary<string, MappingSave>>(File.ReadAllBytes(SaveFile));
            if (all is null) return;
            foreach (var (key, value) in all) _saved[key] = value;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Console.WriteLine($"[gateway] {SaveFile} is unreadable, starting the mapping over: {ex.Message}");
        }
    }
}
