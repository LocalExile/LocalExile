using System.Text.RegularExpressions;

/// <summary>
/// The effects of Izaro's arena mechanics (LabyrinthSecretEffects: <c>&lt;Mechanic&gt;&lt;Tier&gt;_&lt;Element&gt;</c>, and
/// <c>…2</c> for the one an earlier trial left, which a later trial's room brings back): each row's monster (the
/// gargoyles') or object (the statues'), Izaro's buff and the buff's stats with the row's values (BuffDefinitions, Stats).
/// The ones the official data spells oddly (GargoylesMerciless_LIghtning2) are matched without case.
/// </summary>
static partial class IzaroEffects
{
    /// <summary>A row: its Id, mechanic (ToggleStatues, Gargoyles …), tier (1 Normal … 4 Endgame), element (0 fire, 1 cold,
    /// 2 lightning), whether it is the "…2" one, its monster variety's Id or object type (empty when none), Izaro's buff row
    /// and the buff's stats.</summary>
    public sealed record Effect(string Id, string Mechanic, int Tier, int Element, bool Returning, string Variety, string Type,
        ushort Buff, IReadOnlyDictionary<string, int> Stats);

    /// <summary>A mechanic's three effects of a tier (fire, cold, lightning), or their "…2" ones.</summary>
    public static IReadOnlyList<Effect> Of(string mechanic, int tier, bool returning) =>
        [.. All.Value.Where(e => e.Mechanic == mechanic && e.Tier == tier && e.Returning == returning).OrderBy(e => e.Element)];

    public static Effect? Find(string id) => All.Value.FirstOrDefault(e => e.Id == id);

    /// <summary>The "…2" effect of an effect (the one a later trial brings back).</summary>
    public static Effect? ReturningOf(string id) => Find(id) is { } e
        ? All.Value.FirstOrDefault(r => r.Mechanic == e.Mechanic && r.Tier == e.Tier && r.Element == e.Element && r.Returning) : null;

    public static bool IsType(string type) => type.Length > 0 && All.Value.Any(e => e.Type == type && !e.Returning);

    /// <summary>Whether the trial's arena has the mechanic (its "…1" LabyrinthSecrets row).</summary>
    public static bool InArena(IzaroFights.Trial trial, string mechanic) =>
        trial.Mechanic is { } m && m.StartsWith(mechanic, StringComparison.Ordinal) && m.EndsWith('1');

    /// <summary>Whether a later trial's room brings back what an earlier one left of the mechanic (its "…2" secret).</summary>
    public static bool Returns(IzaroFights.Trial trial, string mechanic) =>
        (trial.Secrets ?? []).Any(s => s.StartsWith(mechanic, StringComparison.Ordinal) && s.EndsWith('2'));

    /// <summary>The effects' stats, summed (for SpawnedMonster.EncounterStats).</summary>
    public static Dictionary<string, int> Sum(IEnumerable<Effect> on)
    {
        var sum = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in on)
            foreach (var (stat, value) in e.Stats) sum[stat] = sum.GetValueOrDefault(stat) + value;
        return sum;
    }

    [GeneratedRegex("^(?<m>[A-Za-z]+?)(?<t>Normal|Cruel|Merciless|Endgame)_(?<e>Fire|Cold|Lightning)(?<r>2)?$", RegexOptions.IgnoreCase)]
    private static partial Regex IdPattern();

    static readonly Lazy<List<Effect>> All = new(() =>
    {
        var all = new List<Effect>();
        try
        {
            using var effects = GameTables.Open("data/tables/LabyrinthSecretEffects.json");
            using var buffs = GameTables.Open("data/tables/BuffDefinitions.json");
            using var stats = GameTables.Open("data/tables/Stats.json");
            using var varieties = GameTables.Open("data/tables/MonsterVarieties.json");
            var buffRows = buffs.RootElement.EnumerateArray().ToList();
            var statRows = stats.RootElement.EnumerateArray().ToList();
            var varietyRows = varieties.RootElement.EnumerateArray().ToList();
            foreach (var r in effects.RootElement.EnumerateArray())
            {
                string id = r.GetProperty("Id").GetString() ?? "";
                if (IdPattern().Match(id) is not { Success: true } m) continue;
                if (r.GetProperty("Buff_BuffDefinitionsKey").ValueKind != System.Text.Json.JsonValueKind.Number) continue;
                int buff = r.GetProperty("Buff_BuffDefinitionsKey").GetInt32();
                int[] values = [.. r.GetProperty("Buff_StatValues").EnumerateArray().Select(v => v.GetInt32())];
                string[] keys = [.. buffRows[buff].GetProperty("StatsKeys").EnumerateArray()
                    .Select(k => statRows[k.GetInt32()].GetProperty("Id").GetString() ?? "")];
                var map = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < Math.Min(keys.Length, values.Length); i++) map[keys[i]] = values[i];
                string type = (r.GetProperty("OTFile").GetString() ?? "").Replace(".ot", "", StringComparison.Ordinal);
                string variety = r.GetProperty("MonsterVarietiesKey") is { ValueKind: System.Text.Json.JsonValueKind.Number } v
                    && v.GetInt32() < varietyRows.Count ? varietyRows[v.GetInt32()].GetProperty("Id").GetString() ?? "" : "";
                int tier = m.Groups["t"].Value.ToLowerInvariant() switch { "normal" => 1, "cruel" => 2, "merciless" => 3, _ => 4 };
                int element = m.Groups["e"].Value.ToLowerInvariant() switch { "fire" => 0, "cold" => 1, _ => 2 };
                all.Add(new Effect(id, m.Groups["m"].Value, tier, element, m.Groups["r"].Success, variety, type, (ushort)buff, map));
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Console.WriteLine($"[gateway] Izaro's mechanics: {ex.Message}");
        }
        return all;
    });
}
