/// <summary>
/// Izaro's Toggle Statues (the wiki's Conduits; LabyrinthSecrets <c>ToggleStatues&lt;tier&gt;1</c>, room boss_statues.arm):
/// in that arena three statues, fire, cold and lightning (LabyrinthSecretEffects <c>ToggleStatues&lt;tier&gt;_&lt;element&gt;</c>:
/// EmpowerStatue1-3), rise one at a time during the fight on the arena's small elevators (Elevator_Player, the wiki's
/// "three small holes"), each giving Izaro its element's buff while on (BuffDefinitions <c>izaro_*_buff_phase1</c>: chance
/// to ignite, freeze or shock, that element's damage taken, physical added as it). A click turns one off for a while.
/// The ones on (or not risen yet) when the trial ends stay on him for the run's later trials: there they stand in the
/// arena's alcove (BossObjectiveMarkers) as StatueDoodad1-3 and give the "…2" rows' buffs (the same, with more item
/// rarity and quantity), when the later Izaro room has the <c>ToggleStatues&lt;tier&gt;2</c> secret.
/// The original (live, the user): they rise one at a time; a click turns one off for about 20 s. The rise interval is
/// ours. The ignite, freeze and shock chances are given to the client's buff but not used by the server's monster hits.
/// </summary>
static class IzaroStatues
{
    /// <summary>A statue: its LabyrinthSecretEffects Id, element (0 fire, 1 cold, 2 lightning), object type, buff row and
    /// the buff's stats with the row's values.</summary>
    public sealed record Statue(string Id, int Element, string Type, ushort Buff, IReadOnlyDictionary<string, int> Stats);

    /// <summary>EmpowerStatueBase.ot's states: 1 lowered, 2 raising (lift_raise, 10.7 s), 3 raised and off, 4 on (targetable;
    /// back to 3 plays Deactivate), 5 lowering (lift_lower, 3.333 s), 6 lowered again.</summary>
    public const byte Lowered = 1, Raising = 2, Off = 3, On = 4, Lowering = 5, Down = 6;

    /// <summary>StatueDoodad's on state (2: Activated_idle and its glow).</summary>
    public const byte DoodadOn = 2;

    /// <summary>The Transitionable's index: EmpowerStatue (Positioned, BaseEvents, InteractionAction, Animated,
    /// Transitionable), StatueDoodad (Positioned, BaseEvents, Animated, Transitionable).</summary>
    public const byte StatueTransitionable = 4, DoodadTransitionable = 3;

    static readonly string[] StatueComponents = ["Positioned", "BaseEvents", "InteractionAction", "Animated", "Transitionable"];
    static readonly string[] DoodadComponents = ["Positioned", "BaseEvents", "Animated", "Transitionable"];

    /// <summary>The small elevators' (Elevator_Player: Izaro's elevator's states) and the statues' rise, and their lowering.</summary>
    public const string SmallElevator = "Metadata/Terrain/Labyrinth/Objects/Elevator_Player",
        Marker = "Metadata/Terrain/Labyrinth/Objects/BossObjectiveMarker";
    public const double RiseSeconds = 10.7, LowerSeconds = 3.333;

    /// <summary>The time between the statues' rises (ours), and how long a click turns one off (the user's memory).</summary>
    public const double RiseInterval = 12, OffSeconds = 20;

    /// <summary>Whether the trial's arena has the statues (its mechanic, the "…1" row).</summary>
    public static bool InArena(IzaroFights.Trial trial) =>
        trial.Mechanic is { } m && m.StartsWith("ToggleStatues", StringComparison.Ordinal) && m.EndsWith('1');

    /// <summary>Whether a later trial's room shows the statues an earlier one left on (its "…2" secret).</summary>
    public static bool Returns(IzaroFights.Trial trial) =>
        (trial.Secrets ?? []).Any(s => s.StartsWith("ToggleStatues", StringComparison.Ordinal) && s.EndsWith('2'));

    /// <summary>A tier's three statues (fire, cold, lightning), or their "…2" versions.</summary>
    public static IReadOnlyList<Statue> For(int tier, bool returning) =>
        [.. Statues.Value.Where(s => s.Id.StartsWith($"ToggleStatues{TierName(tier)}_", StringComparison.Ordinal) && s.Id.EndsWith('2') == returning)
            .OrderBy(s => s.Element)];

    /// <summary>The "…2" version of a statue (the carried one).</summary>
    public static Statue? Returning(string id) => Statues.Value.FirstOrDefault(s => s.Id == id + "2");

    static string TierName(int tier) => tier switch { 1 => "Normal", 2 => "Cruel", 3 => "Merciless", _ => "Endgame" };

    public static bool IsStatue(string type) => Statues.Value.Any(s => s.Type == type && !s.Id.EndsWith('2'));

    public static byte[] Blob(Statue statue, int x, int y, float angle, byte state) => statue.Id.EndsWith('2')
        ? LabyrinthTrials.Blob(DoodadComponents, "", x, y, angle, 1, state, null, blockage: false, blocked: false)
        : LabyrinthTrials.Blob(StatueComponents, "", x, y, angle, 1, state, null, blockage: false, blocked: false);

    /// <summary>The stats of the statues on, summed (for SpawnedMonster.EncounterStats).</summary>
    public static Dictionary<string, int> Sum(IEnumerable<Statue> on)
    {
        var sum = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in on)
            foreach (var (stat, value) in s.Stats) sum[stat] = sum.GetValueOrDefault(stat) + value;
        return sum;
    }

    // LabyrinthSecretEffects' ToggleStatues rows with their OTFile, buff and values; the buff's stats from BuffDefinitions
    // and Stats (data/tables).
    static readonly Lazy<List<Statue>> Statues = new(() =>
    {
        var statues = new List<Statue>();
        try
        {
            using var effects = GameTables.Open("data/tables/LabyrinthSecretEffects.json");
            using var buffs = GameTables.Open("data/tables/BuffDefinitions.json");
            using var stats = GameTables.Open("data/tables/Stats.json");
            var buffRows = buffs.RootElement.EnumerateArray().ToList();
            var statRows = stats.RootElement.EnumerateArray().ToList();
            foreach (var r in effects.RootElement.EnumerateArray())
            {
                string id = r.GetProperty("Id").GetString() ?? "";
                if (!id.StartsWith("ToggleStatues", StringComparison.Ordinal)) continue;
                string type = (r.GetProperty("OTFile").GetString() ?? "").Replace(".ot", "", StringComparison.Ordinal);
                if (r.GetProperty("Buff_BuffDefinitionsKey").ValueKind != System.Text.Json.JsonValueKind.Number || type.Length == 0) continue;
                int buff = r.GetProperty("Buff_BuffDefinitionsKey").GetInt32();
                int[] values = [.. r.GetProperty("Buff_StatValues").EnumerateArray().Select(v => v.GetInt32())];
                string[] keys = [.. buffRows[buff].GetProperty("StatsKeys").EnumerateArray()
                    .Select(k => statRows[k.GetInt32()].GetProperty("Id").GetString() ?? "")];
                var map = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < Math.Min(keys.Length, values.Length); i++) map[keys[i]] = values[i];
                string element = id[(id.IndexOf('_') + 1)..].TrimEnd('2');
                statues.Add(new Statue(id, element switch { "Fire" => 0, "Cold" => 1, _ => 2 }, type, (ushort)buff, map));
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Console.WriteLine($"[gateway] Izaro's statues: {ex.Message}");
        }
        return statues;
    });
}
