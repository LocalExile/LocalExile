using System.Text.Json;
using GameServer.Core;

namespace GameServer.Modules.PhreciaAscendancies.Classes.Harbinger;

/// <summary>Fixed curse flags on the target variety's own mods, separate from rolled rarity/affix effects.</summary>
sealed class HarbingerCurseSources
{
    readonly ModCatalog mods;
    readonly string folder;

    public HarbingerCurseSources(ModCatalog mods, string folder = "data")
    {
        this.mods = mods;
        this.folder = folder;
        sources = Load(mods, folder);
    }

    static readonly HashSet<uint> Flags = [Stat.ReflectCurses, Stat.ImmuneToCurses, Stat.Hexproof, HarbingerActionSpeed.ActionFloor];
    readonly Dictionary<int, Dictionary<uint, int>> sources;

    public int Get(SpawnedMonster monster, uint name) => monster.Mods.Effects[name]
        + (monster.Variety is { } variety && sources.TryGetValue(variety.Row, out var stats)
            ? stats.GetValueOrDefault(name) : 0);

    static Dictionary<int, Dictionary<uint, int>> Load(ModCatalog mods, string folder)
    {
        var result = new Dictionary<int, Dictionary<uint, int>>();
        if (!ClientTables.Has("MonsterVarieties")) return result;
        foreach (var variety in ClientTables.Table("MonsterVarieties"))
        {
            var stats = new Dictionary<uint, int>();
            foreach (int key in variety.Ints("ModsKeys"))
                if (mods.Row(key) is { } mod)
                    foreach (var stat in mod.Stats.Where(s => Flags.Contains(s.Id) && s.Min == s.Max))
                        stats[stat.Id] = stats.GetValueOrDefault(stat.Id) + stat.Min;
            if (stats.Count > 0) result[variety.Index] = stats;
        }
        return result;
    }
}
