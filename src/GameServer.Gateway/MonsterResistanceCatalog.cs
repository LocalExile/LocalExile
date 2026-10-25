using System.Text.Json;
using GameServer.Core.World;

/// <summary>Base monster resistance from MonsterTypes.Resistances → MonsterResistances. The level's
/// DefaultMonsterStats.Difficulty selects Normal/Cruel/Merciless columns; it is not guessed from area names.
/// Spawned monster modifiers, exposure and curses are additional sources, outside this base table.</summary>
sealed class MonsterResistanceCatalog(string folder = "data")
{
    private sealed record Tables(int[] TypeResistance, int[] Difficulty, int[][] Resistances);
    private readonly Lazy<Tables?> _tables = new(() => Load(folder));

    public int? BaseResistance(int monsterType, int level, DamageType type)
    {
        if (type == DamageType.Physical) return 0;
        if (_tables.Value is not { } t || monsterType < 0 || monsterType >= t.TypeResistance.Length
            || level < 1 || level > t.Difficulty.Length) return null;
        int row = t.TypeResistance[monsterType], difficulty = t.Difficulty[level - 1];
        if (row < 0 || row >= t.Resistances.Length || difficulty is < 1 or > 3) return null;
        int element = type switch { DamageType.Fire => 0, DamageType.Cold => 1, DamageType.Lightning => 2, DamageType.Chaos => 3, _ => -1 };
        return element < 0 ? 0 : t.Resistances[row][(difficulty - 1) * 4 + element];
    }

    private static Tables? Load(string folder)
    {
        string types = Path.Combine(folder, "tables", "MonsterTypes.json"),
            levels = Path.Combine(folder, "tables", "DefaultMonsterStats.json"),
            resistance = Path.Combine(folder, "tables", "MonsterResistances.json");
        if (!File.Exists(types) || !File.Exists(levels) || !File.Exists(resistance)) return null;
        using var t = JsonDocument.Parse(File.ReadAllBytes(types));
        using var l = JsonDocument.Parse(File.ReadAllBytes(levels));
        using var r = JsonDocument.Parse(File.ReadAllBytes(resistance));
        return new(
            [.. t.RootElement.EnumerateArray().Select(row => row.TryGetProperty("Resistances", out var value)
                && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : -1)],
            [.. l.RootElement.EnumerateArray().Select(row => row.GetProperty("Difficulty").GetInt32())],
            [.. r.RootElement.EnumerateArray().Select(row => (from difficulty in new[] { "Normal", "Cruel", "Merciless" }
                from element in new[] { "Fire", "Cold", "Lightning", "Chaos" }
                select row.GetProperty(element + difficulty).GetInt32()).ToArray())]);
    }
}
