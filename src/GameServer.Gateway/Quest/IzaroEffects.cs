using System.Text.RegularExpressions;
using GameServer.Core;

/// <summary>
/// The effects of Izaro's arena mechanics (LabyrinthSecretEffects: <c>&lt;Mechanic&gt;&lt;Tier&gt;_&lt;Element&gt;</c>, and
/// <c>…2</c> for the one an earlier trial left, which a later trial's room brings back): each row's monster (the
/// gargoyles') or object (the statues'), Izaro's buff and the buff's stats with the row's values (BuffDefinitions, Stats).
/// The ones the official data spells oddly (GargoylesMerciless_LIghtning2) are matched without case.
/// </summary>
public static partial class IzaroEffects
{
    /// <summary>A row: its Id, mechanic (ToggleStatues, Gargoyles, Portals …), tier (1 Normal … 4 Endgame; 0 every tier: the
    /// portals', the Chrono Statues' and the Lieutenants' <c>…&lt;n&gt;_&lt;element&gt;</c>), element (0 fire, 1 cold, 2 lightning), whether it is the "…2" one, its
    /// monster variety's Id or object type (empty when none), Izaro's buff row (a portal that has not returned has none:
    /// 0, with no stats) and the buff's stats.</summary>
    public sealed record Effect(string Id, string Mechanic, int Tier, int Element, bool Returning, string Variety, string Type,
        ushort Buff, IReadOnlyDictionary<string, int> Stats);

    /// <summary>A mechanic's three effects of a tier (fire, cold, lightning), or their "…2" ones.</summary>
    public static IReadOnlyList<Effect> Of(string mechanic, int tier, bool returning) =>
        [.. All.Value.Where(e => e.Mechanic == mechanic && (e.Tier == tier || e.Tier == 0) && e.Returning == returning).OrderBy(e => e.Element)];

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

    // The rows of every tier: PortalSummon<n>_<element>, ChronoStatues<n>_<1 fire, 2 cold, 3 lightning> (their varieties'
    // elements) and Lieutenants<n>_<Bull, Melee, Ranged> (taken in that order for the lifts and alcoves).
    [GeneratedRegex("^(?<m>PortalSummon|ChronoStatues|Lieutenants)(?<n>[12])_(?<e>Fire|Cold|Lightning|[123]|Bull|Melee|Ranged)$")]
    private static partial Regex UntieredPattern();

    static string UntieredMechanic(string name) => name switch
    {
        "PortalSummon" => IzaroPortals.Mechanic,
        "ChronoStatues" => IzaroChronos.Mechanic,
        _ => IzaroLieutenants.Mechanic,
    };

    static int UntieredElement(string name) => name switch { "Fire" or "1" or "Bull" => 0, "Cold" or "2" or "Melee" => 1, _ => 2 };

    static readonly Lazy<List<Effect>> All = new(() =>
    {
        var all = new List<Effect>();
        try
        {
            var effects = ClientTables.Table("LabyrinthSecretEffects");
            var buffs = ClientTables.Table("BuffDefinitions");
            var stats = ClientTables.Table("Stats");
            var varieties = ClientTables.Table("MonsterVarieties");
            var buffRows = buffs.ToList();
            var statRows = stats.ToList();
            var varietyRows = varieties.ToList();
            foreach (var r in effects)
            {
                string id = r.Text("Id") ?? "";
                var untiered = UntieredPattern().Match(id);
                var m = untiered.Success ? untiered : IdPattern().Match(id);
                if (!m.Success) continue;
                bool buffed = !r.IsNull("Buff_BuffDefinitionsKey");
                if (!buffed && !untiered.Success) continue;
                int buff = buffed ? r.Int("Buff_BuffDefinitionsKey") : 0;
                var map = new Dictionary<string, int>(StringComparer.Ordinal);
                if (buffed)
                {
                    int[] values = [.. r.Ints("Buff_StatValues")];
                    string[] keys = [.. buffRows[buff].Ints("StatsKeys").Select(k => statRows[k].Text("Id") ?? "")];
                    for (int i = 0; i < Math.Min(keys.Length, values.Length); i++) map[keys[i]] = values[i];
                }
                string type = (r.Text("OTFile") ?? "").Replace(".ot", "", StringComparison.Ordinal);
                string variety = r.IntOrNull("MonsterVarietiesKey") is int v
                    && v < varietyRows.Count ? varietyRows[v].Text("Id") ?? "" : "";
                int element = untiered.Success ? UntieredElement(m.Groups["e"].Value)
                    : m.Groups["e"].Value.ToLowerInvariant() switch { "fire" => 0, "cold" => 1, _ => 2 };
                all.Add(untiered.Success
                    ? new Effect(id, UntieredMechanic(m.Groups["m"].Value), 0, element, m.Groups["n"].Value == "2", variety, type, (ushort)buff, map)
                    : new Effect(id, m.Groups["m"].Value, m.Groups["t"].Value.ToLowerInvariant() switch { "normal" => 1, "cruel" => 2, "merciless" => 3, _ => 4 },
                        element, m.Groups["r"].Success, variety, type, (ushort)buff, map));
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Console.WriteLine($"[gateway] Izaro's mechanics: {ex.Message}");
        }
        return all;
    });
}
