using System.Text.Json;

namespace GameServer.Core.World;

/// <summary>
/// The areas the Gateway generates itself (its <c>ServerLevels</c>, Agents/docs/re/011-level-generation.md): those whose
/// seed-1 trace passes every check of the level generator's gate (scripts/re/levelgen/gate-baseline.json) and whose
/// logged loads all match. Auth gives their new instances random seeds; every other area keeps seed 1, whose captures
/// the Gateway has.
/// </summary>
public static class ServerLevelAreas
{
    public const string BaselineFile = "scripts/re/levelgen/gate-baseline.json";

    /// <summary>The baseline for the servers: a tracked file, while they run in the runtime directory, so it resolves
    /// against Game_SOURCE_ROOT when the launcher sets one.</summary>
    public static string BaselinePath => Path.Combine(Environment.GetEnvironmentVariable("Game_SOURCE_ROOT") ?? ".", BaselineFile);

    /// <summary>The supported areas of a gate baseline: each "capture &lt;area&gt;" whose tile hash, both grids, every
    /// draw the port makes, the doodads, the cell heights and the transitions match (a check without a capture does not
    /// count against it), with every "oracle &lt;area&gt; &lt;seed&gt;" of the area matching too. Empty without the file.</summary>
    public static HashSet<string> Load(string file, Action<string>? log = null)
    {
        var supported = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(file)) return supported;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var failedOracle = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var r = p.Value;
                string hash = r.GetProperty("Hash").GetString() ?? "";
                if (p.Name.StartsWith("oracle ", StringComparison.Ordinal))
                {
                    if (hash != "match") failedOracle.Add(p.Name.Split(' ')[1]);
                    continue;
                }
                if (!p.Name.StartsWith("capture ", StringComparison.Ordinal) || hash != "match") continue;
                int Int(string name, int none) => r.TryGetProperty(name, out var v) ? v.GetInt32() : none;
                string? first = r.TryGetProperty("FirstDiff", out var f) ? f.GetString() : null;
                bool draws = first is null || first.EndsWith(" (port stopped)", StringComparison.Ordinal);
                bool grids = Int("Walk", -1) == 0 && Int("Projectile", -1) == 0;
                int captured = Int("Captured", 0);
                bool doodads = captured < 0 || (Int("Placed", 0) == captured && Int("Matched", 0) == captured);
                if (draws && grids && doodads && Int("Heights", -1) <= 0 && Int("Objects", -1) <= 0)
                    supported.Add(p.Name["capture ".Length..]);
            }
            supported.ExceptWith(failedOracle);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            log?.Invoke($"{file}: {ex.Message}");
        }
        return supported;
    }

    /// <summary>The WorldAreas <c>HASH16</c>s of these area ids (data/tables/WorldAreas.json); empty when the file is
    /// missing.</summary>
    public static HashSet<ushort> Hashes(string worldAreasJson, IReadOnlySet<string> ids)
    {
        if (!File.Exists(worldAreasJson)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllBytes(worldAreasJson));
        return doc.RootElement.EnumerateArray()
            .Where(r => ids.Contains(r.GetProperty("Id").GetString() ?? ""))
            .Select(r => (ushort)r.GetProperty("HASH16").GetInt32())
            .ToHashSet();
    }
}
