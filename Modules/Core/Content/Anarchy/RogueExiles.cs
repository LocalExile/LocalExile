using GameServer.Core;

namespace GameServer.Modules.Anarchy;

/// <summary>
/// The Rogue Exiles that roam the areas: RogueExiles.dat rows whose column 1 is set (31 rows; the "Supporter",
/// Descent, Daresso arena, shade, map boss and Faridun varieties are not). Column 2 is read as the lowest area level
/// the exile appears at (Orra Greengate 5, Igna Phoenix 6 … Ailentia Rac 88; Torr Olgosso and Jonah Unchained have
/// none): not confirmed against the client. Their more life by level is RogueExileLifeScalingPerLevel (from 68: 150%,
/// up to 1 860% at 87 and above). RogueExileGiants lists the gigantified exiles: the "Supporter" variety of each, of the
/// same name. The three "of the Afarud" exiles (ExileRangerFaridun1 …) are the Faridun ones.
/// </summary>
sealed class RogueExiles
{
    public RogueExiles()
    {
        _data = Load();
    }

    public static RogueExiles Current { get; } = new();

    /// <summary>The gigantified exile's mod (Mods MonsterSupporterGigantism1): 80% larger, 100% more area, 100% life.</summary>
    public const string GiantMod = "MonsterSupporterGigantism1";

    sealed record Data(List<(MonsterCatalog.Variety Variety, int MinLevel)> Wild, Dictionary<string, MonsterCatalog.Variety> Giants,
        List<MonsterCatalog.Variety> Faridun, Dictionary<int, int> LifeMore);

    readonly Data _data;

    /// <summary>A wild Rogue Exile that can appear at this level, every one alike (any level's when
    /// <paramref name="anyLevel"/>: map_override_rogue_exile_min_level), other than those in <paramref name="besides"/>
    /// while another is left; null when none can.</summary>
    public MonsterCatalog.Variety? Pick(int level, Random rng, bool anyLevel = false, IReadOnlyCollection<string>? besides = null)
    {
        var allowed = _data.Wild.Where(e => anyLevel || e.MinLevel <= level).Select(e => e.Variety).ToList();
        if (besides is { Count: > 0 } && allowed.Where(v => !besides.Contains(v.Id)).ToList() is { Count: > 0 } fresh) allowed = fresh;
        return allowed.Count == 0 ? null : allowed[rng.Next(allowed.Count)];
    }

    /// <summary>As <see cref="Pick"/>, among the exiles that have a gigantified variety.</summary>
    public MonsterCatalog.Variety? PickWithGiant(int level, Random rng, bool anyLevel = false, IReadOnlyCollection<string>? besides = null)
    {
        var allowed = _data.Wild.Where(e => (anyLevel || e.MinLevel <= level) && _data.Giants.ContainsKey(e.Variety.Id))
            .Select(e => e.Variety).ToList();
        if (besides is { Count: > 0 } && allowed.Where(v => !besides.Contains(v.Id)).ToList() is { Count: > 0 } fresh) allowed = fresh;
        return allowed.Count == 0 ? Pick(level, rng, anyLevel, besides) : allowed[rng.Next(allowed.Count)];
    }

    /// <summary>One of the Faridun exiles; null when the tables have none the server can create.</summary>
    public MonsterCatalog.Variety? PickFaridun(Random rng) =>
        _data.Faridun.Count == 0 ? null : _data.Faridun[rng.Next(_data.Faridun.Count)];

    /// <summary>The gigantified variety of a wild exile (RogueExileGiants), if it has one.</summary>
    public MonsterCatalog.Variety? Giant(MonsterCatalog.Variety exile) => _data.Giants.GetValueOrDefault(exile.Id);

    /// <summary>Whether the variety is one of the Rogue Exiles this module places.</summary>
    public bool IsExile(string varietyId) =>
        _data.Wild.Any(e => e.Variety.Id == varietyId) || _data.Giants.Values.Any(v => v.Id == varietyId)
        || _data.Faridun.Any(v => v.Id == varietyId);

    /// <summary>The exiles' more life at this level, in percent.</summary>
    public int LifeMore(int level) => _data.LifeMore.GetValueOrDefault(Math.Clamp(level, 1, 100));

    static Data Load()
    {
        var wild = new List<(MonsterCatalog.Variety, int)>();
        var giants = new Dictionary<string, MonsterCatalog.Variety>(StringComparer.Ordinal);
        var faridun = new List<MonsterCatalog.Variety>();
        if (ClientTables.TableOrNull("RogueExiles") is not { } exiles || ClientTables.TableOrNull("MonsterVarieties") is not { } varieties)
            return new([], giants, faridun, []);
        MonsterCatalog.Variety? Variety(int row) =>
            varieties.Row(row)?.Text("Id") is { } id && Monsters.Find(id) is { Supported: true } variety && variety.Id == id ? variety : null;
        foreach (var e in exiles)
        {
            if (e.IntOrNull("MonsterVarietiesKey") is not int row || Variety(row) is not { } variety) continue;
            if (e.BoolOrNull("__col1") ?? false) wild.Add((variety, e.IntOrNull("__col2") ?? 1));
            else if (variety.Id.Contains("Faridun", StringComparison.Ordinal)) faridun.Add(variety);
        }
        foreach (var g in ClientTables.TableOrEmpty("RogueExileGiants"))
            if (g.IntOrNull("Id") is int row && Variety(row) is { } giant
                && wild.FirstOrDefault(w => w.Item1.Name == giant.Name).Item1 is { } of)
                giants.TryAdd(of.Id, giant);
        var life = new Dictionary<int, int>();
        foreach (var r in ClientTables.TableOrEmpty("RogueExileLifeScalingPerLevel"))
            life[r.Int("Level")] = r.Int("MoreLife");
        return new Data(wild, giants, faridun, life);
    }
}
