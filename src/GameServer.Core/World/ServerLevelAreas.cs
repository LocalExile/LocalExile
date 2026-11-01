using System.Text.Json;

namespace GameServer.Core.World;

/// <summary>
/// The areas the Gateway generates itself (its <c>ServerLevels</c>, Agents/docs/re/011-level-generation.md), from the
/// level generator's gate (scripts/re/levelgen/gate-baseline.json): those whose seed-1 trace passes every check of the
/// gate, and those whose seed 1 the client cannot generate either whose trace at another seed does; every logged load
/// of the area must match too. Auth gives their new instances random seeds; every other area keeps seed 1, whose
/// captures the Gateway has, or the seed it built in place of a seed 1 it cannot build (<see cref="SeedsInPlaceOfOne"/>).
/// </summary>
public static class ServerLevelAreas
{
    public const string BaselineFile = "scripts/re/levelgen/gate-baseline.json";

    /// <summary>The supported areas of a gate baseline: each "capture &lt;area&gt;" (its seed-1 trace) whose tile hash,
    /// both grids, every draw the port makes, the doodads, the cell heights and the transitions match (a check without a
    /// capture does not count against it), and each area whose seed 1 the client and the port fail alike ("failure
    /// &lt;area&gt; 1" matching: no seed-1 trace can exist) with a "load &lt;area&gt; &lt;seed&gt;" (a trace at another
    /// seed) passing the same checks. An area is left out when any of its "oracle", "load" or "failure" entries does not
    /// match. Empty without the file.</summary>
    public static HashSet<string> Load(string file, Action<string>? log = null) => Read(file, log).Supported;

    /// <summary>The seed each area whose seed-1 load the client fails ("failure &lt;area&gt; 1") is played at wherever
    /// seed 1 would be used: the lowest seed of its fully checked traces ("load &lt;area&gt; &lt;seed&gt;" passing), else
    /// the lowest of its logged loads the port matches by tile hash ("oracle &lt;area&gt; &lt;seed&gt;" matching): a seed
    /// the client built. Areas with no such load are left out (they stay at seed 1). Empty without the file.</summary>
    public static Dictionary<string, uint> SeedsInPlaceOfOne(string file, Action<string>? log = null) => Read(file, log).InPlaceOfOne;

    static (HashSet<string> Supported, Dictionary<string, uint> InPlaceOfOne) Read(string file, Action<string>? log)
    {
        var supported = new HashSet<string>(StringComparer.Ordinal);
        var inPlaceOfOne = new Dictionary<string, uint>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(file)) return (supported, inPlaceOfOne);
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var failed = new HashSet<string>(StringComparer.Ordinal);
            var builtSeeds = new Dictionary<string, uint>(StringComparer.Ordinal);
            var checkedSeeds = new Dictionary<string, uint>(StringComparer.Ordinal);
            var seedOneFailures = new Dictionary<string, bool>(StringComparer.Ordinal);
            static void Lowest(Dictionary<string, uint> seeds, string area, uint seed)
            {
                if (seed > 1 && (!seeds.TryGetValue(area, out uint low) || seed < low)) seeds[area] = seed;
            }
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var r = p.Value;
                var parts = p.Name.Split(' ');
                if (parts.Length < 2) continue;
                string kind = parts[0], area = parts[1];
                uint seed = parts.Length > 2 && uint.TryParse(parts[2], out uint s) ? s : 0;
                bool hash = r.GetProperty("Hash").GetString() == "match";
                switch (kind)
                {
                    case "oracle":
                        if (!hash) failed.Add(area);
                        else Lowest(builtSeeds, area, seed);
                        break;
                    case "failure":
                        // The port must fail where the client does; a seed-1 failure leaves the area without a capture.
                        if (!hash) failed.Add(area);
                        if (seed == 1) seedOneFailures[area] = hash;
                        break;
                    case "load":
                        if (hash && FullMatch(r)) Lowest(checkedSeeds, area, seed);
                        else failed.Add(area);
                        break;
                    case "capture" when parts.Length == 2:
                        if (hash && FullMatch(r)) supported.Add(area);
                        break;
                }
            }
            foreach (var (area, portFailsAlike) in seedOneFailures)
            {
                if (checkedSeeds.TryGetValue(area, out uint seed))
                {
                    inPlaceOfOne[area] = seed;
                    if (portFailsAlike) supported.Add(area);
                }
                else if (builtSeeds.TryGetValue(area, out seed)) inPlaceOfOne[area] = seed;
            }
            supported.ExceptWith(failed);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            log?.Invoke($"{file}: {ex.Message}");
        }
        return (supported, inPlaceOfOne);
    }

    // Every check of a traced load: draws (or the port stopping after the last one it makes), both grids, the doodads
    // (unless the trace has none), the cell heights and the transitions (each unless no capture had them).
    static bool FullMatch(JsonElement r)
    {
        int Int(string name, int none) => r.TryGetProperty(name, out var v) ? v.GetInt32() : none;
        string? first = r.TryGetProperty("FirstDiff", out var f) ? f.GetString() : null;
        bool draws = first is null || first.EndsWith(" (port stopped)", StringComparison.Ordinal);
        bool grids = Int("Walk", -1) == 0 && Int("Projectile", -1) == 0;
        int captured = Int("Captured", 0);
        bool doodads = captured < 0 || (Int("Placed", 0) == captured && Int("Matched", 0) == captured);
        return draws && grids && doodads && Int("Heights", -1) <= 0 && Int("Objects", -1) <= 0;
    }

    /// <summary>The WorldAreas <c>HASH16</c>s of these area ids (data/tables/WorldAreas.json); empty when the file is
    /// missing.</summary>
    public static HashSet<ushort> Hashes(string worldAreasJson, IReadOnlySet<string> ids) =>
        ByHash(worldAreasJson, ids.ToDictionary(id => id, _ => 0u)).Keys.ToHashSet();

    /// <summary>These per-area values keyed by the areas' WorldAreas <c>HASH16</c>s (data/tables/WorldAreas.json); empty
    /// when the file is missing.</summary>
    public static Dictionary<ushort, T> ByHash<T>(string worldAreasJson, IReadOnlyDictionary<string, T> byId)
    {
        var result = new Dictionary<ushort, T>();
        if (!File.Exists(worldAreasJson)) return result;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(worldAreasJson));
        foreach (var r in doc.RootElement.EnumerateArray())
            if (byId.TryGetValue(r.GetProperty("Id").GetString() ?? "", out var value))
                result[(ushort)r.GetProperty("HASH16").GetInt32()] = value;
        return result;
    }
}
