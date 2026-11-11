using System.Text.Json;
using GameServer.Core;

namespace GameServer.Modules.PhreciaAscendancies.Classes.Harbinger;

/// <summary>Fixed curse flags on the target variety's own mods, separate from rolled rarity/affix effects.</summary>
sealed class HarbingerCurseSources(ModCatalog mods, string folder = "data")
{
    static readonly HashSet<string> Flags = ["reflect_curses", "immune_to_curses", "hexproof", HarbingerActionSpeed.ActionFloor];
    readonly Lazy<Dictionary<int, Dictionary<string, int>>> sources = new(() => Load(mods, folder));

    public int Get(SpawnedMonster monster, string name) => monster.Mods.Effects[name]
        + (monster.Variety is { } variety && sources.Value.TryGetValue(variety.Row, out var stats)
            ? stats.GetValueOrDefault(name) : 0);

    static Dictionary<int, Dictionary<string, int>> Load(ModCatalog mods, string folder)
    {
        var result = new Dictionary<int, Dictionary<string, int>>();
        if (!ClientTables.Has("MonsterVarieties")) return result;
        foreach (var variety in ClientTables.Table("MonsterVarieties"))
        {
            var stats = new Dictionary<string, int>();
            foreach (int key in variety.Ints("ModsKeys"))
                if (mods.Row(key) is { } mod)
                    foreach (var stat in mod.Stats.Where(s => Flags.Contains(s.Id) && s.Min == s.Max))
                        stats[stat.Id] = stats.GetValueOrDefault(stat.Id) + stat.Min;
            if (stats.Count > 0) result[variety.Index] = stats;
        }
        return result;
    }
}
