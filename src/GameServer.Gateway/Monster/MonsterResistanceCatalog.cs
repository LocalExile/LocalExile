using System.Text.Json;
using GameServer.Core.World;
using GameServer.Core;

/// <summary>Base monster resistance from MonsterTypes.Resistances → MonsterResistances. The level's
/// DefaultMonsterStats.Difficulty selects Normal/Cruel/Merciless columns; it is not guessed from area names.
/// Spawned monster modifiers, exposure and curses are additional sources, outside this base table.</summary>
public sealed class MonsterResistanceCatalog(string folder = "data")
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
        if (!ClientTables.Has("MonsterTypes") || !ClientTables.Has("DefaultMonsterStats") || !ClientTables.Has("MonsterResistances")) return null;
        var t = ClientTables.Table("MonsterTypes");
        var l = ClientTables.Table("DefaultMonsterStats");
        var r = ClientTables.Table("MonsterResistances");
        return new(
            [.. t.Select(row => row.IntOrNull("Resistances") ?? -1)],
            [.. l.Select(row => row.Int("Difficulty"))],
            [.. r.Select(row => (from difficulty in new[] { "Normal", "Cruel", "Merciless" }
                from element in new[] { "Fire", "Cold", "Lightning", "Chaos" }
                select row.Int(element + difficulty)).ToArray())]);
    }
}
