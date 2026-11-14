using GameServer.Core;

namespace GameServer.Modules.Anarchy;

/// <summary>
/// The Rogue Exiles that roam the areas: RogueExiles.dat rows whose column 1 is set (31 rows; the "Supporter",
/// Descent, Daresso arena, shade, map boss and Faridun varieties are not). Column 2 is read as the lowest area level
/// the exile appears at (Orra Greengate 5, Igna Phoenix 6 … Ailentia Rac 88; Torr Olgosso and Jonah Unchained have
/// none): not confirmed against the client. Their more life by level is RogueExileLifeScalingPerLevel (from 68: 150%,
/// up to 1 860% at 87 and above).
/// </summary>
sealed class RogueExiles
{
    public static RogueExiles Current { get; } = new();

    sealed record Data(List<(MonsterCatalog.Variety Variety, int MinLevel)> Wild, Dictionary<int, int> LifeMore);

    readonly Lazy<Data> _data = new(Load);

    /// <summary>A wild Rogue Exile that can appear at this level, every one alike; null when none can.</summary>
    public MonsterCatalog.Variety? Pick(int level, Random rng)
    {
        var allowed = _data.Value.Wild.Where(e => e.MinLevel <= level).ToList();
        return allowed.Count == 0 ? null : allowed[rng.Next(allowed.Count)].Variety;
    }

    /// <summary>The exiles' more life at this level, in percent.</summary>
    public int LifeMore(int level) => _data.Value.LifeMore.GetValueOrDefault(Math.Clamp(level, 1, 100));

    static Data Load()
    {
        var wild = new List<(MonsterCatalog.Variety, int)>();
        if (ClientTables.TableOrNull("RogueExiles") is { } exiles && ClientTables.TableOrNull("MonsterVarieties") is { } varieties)
            foreach (var e in exiles)
            {
                if (!(e.BoolOrNull("__col1") ?? false) || e.IntOrNull("MonsterVarietiesKey") is not int row) continue;
                if (varieties.Row(row)?.Text("Id") is not { } id || Monsters.Find(id) is not { Supported: true } variety || variety.Id != id) continue;
                wild.Add((variety, e.IntOrNull("__col2") ?? 1));
            }
        var life = new Dictionary<int, int>();
        foreach (var r in ClientTables.TableOrEmpty("RogueExileLifeScalingPerLevel"))
            life[r.Int("Level")] = r.Int("MoreLife");
        return new Data(wild, life);
    }
}
