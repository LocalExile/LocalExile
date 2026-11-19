using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Harvest;

/// <summary>
/// The Sacred Grove's client.db tables, read once per process: HarvestSeeds (the seed species by colour and tier),
/// HarvestPerLevelValues and HarvestLifeScalingPerLevel (server-only tables: the client has no Data/*.dat string for
/// them), HarvestSeedItems (lifeforce per colour) and the grove's and Oshabi's object types (components and StateMachine
/// defaults).
/// </summary>
public sealed class HarvestTables
{
    public const int Red = 1, Green = 2, Blue = 3;

    /// <summary>A HarvestSeeds row: its MonsterVarieties row, tier 1–4, colour (SeedType), and the seed's own .ao and
    /// the .ao it plays when it hatches.</summary>
    public sealed record Seed(int Row, string Id, int Monster, int Tier, int Colour, string? Model, string? Hatch, bool Faridun);

    /// <summary>A HarvestPerLevelValues row: the per-crop Tier 4 chance, the Tier 3 and Tier 2 per-roll chances and roll
    /// counts, and Crop Rotation's upgrade chances (T1→T2, T2→T3, T3→T4); chances in 1/10000.</summary>
    public sealed record LevelValues(int Level, int Tier4, int Tier3Chance, int Tier3Rolls, int Tier2Chance, int Tier2Rolls,
        int Upgrade1, int Upgrade2, int Upgrade3);

    /// <summary>A grove object type: the components the client reads, its StateMachine defaults and its Positioned size.</summary>
    public sealed record ObjectType(string Type, IReadOnlyList<string> Components, StateMachineData? States, int Size);

    public IReadOnlyList<Seed> Seeds { get; }
    /// <summary>The non-Faridun seed rows by colour (1–3) and tier (1–4): <c>ByColourTier[colour][tier]</c>.</summary>
    public int[][][] ByColourTier { get; }
    /// <summary>The Faridun Corpse-Grown seeds (rows 48–50), kept apart from the grove's roll.</summary>
    public IReadOnlyList<Seed> Faridun { get; }
    readonly LevelValues?[] _levels;
    readonly int[] _moreLife;
    /// <summary>HarvestSeedItems: colour → (BaseItemTypes row, the drop stat's Stats id).</summary>
    public IReadOnlyDictionary<int, (int BaseItem, int DropStat)> SeedItems { get; }
    public IReadOnlyDictionary<string, ObjectType> Objects { get; }

    static readonly Lazy<HarvestTables> Loaded = new(() => new HarvestTables());
    public static HarvestTables Current => Loaded.Value;

    public HarvestTables(IReadOnlyList<Seed> seeds, IReadOnlyList<LevelValues> levels, IReadOnlyDictionary<int, int> moreLife)
    {
        Seeds = seeds;
        Faridun = [.. seeds.Where(s => s.Faridun)];
        ByColourTier = new int[4][][];
        for (int c = 0; c < 4; c++)
        {
            ByColourTier[c] = new int[5][];
            for (int t = 0; t < 5; t++)
                ByColourTier[c][t] = [.. seeds.Where(s => !s.Faridun && s.Colour == c && s.Tier == t).Select(s => s.Row)];
        }
        int top = Math.Max(levels.Count == 0 ? 0 : levels.Max(l => l.Level), moreLife.Count == 0 ? 0 : moreLife.Keys.Max());
        _levels = new LevelValues?[top + 1];
        foreach (var l in levels) _levels[l.Level] = l;
        _moreLife = new int[top + 1];
        foreach (var (level, more) in moreLife) _moreLife[level] = more;
        SeedItems = new Dictionary<int, (int, int)>();
        Objects = new Dictionary<string, ObjectType>();
    }

    HarvestTables() : this(LoadSeeds(), LoadLevels(), LoadMoreLife())
    {
        var items = new Dictionary<int, (int, int)>();
        foreach (var row in ClientTables.TableOrEmpty("HarvestSeedItems"))
            items[row.Int("Id")] = (row.Int("BaseItem"), row.Int("DropStat") + 1);
        SeedItems = items;
        var shared = ObjectTypes.SharedStates();
        Objects = ObjectTypes.All().Where(t => t.Type.StartsWith(SacredGrove.ObjectRoot, StringComparison.Ordinal) || OshabiEncounter.Types.Contains(t.Type))
            .ToDictionary(t => t.Type, t => new ObjectType(t.Type, ObjectTypes.Components(t.Row), ObjectTypes.StateMachine(t.Row, shared),
                int.TryParse(t.Row.Text("positioned_object_size"), out int size) ? size : 1));
    }

    static List<Seed> LoadSeeds() => [.. ClientTables.TableOrEmpty("HarvestSeeds").Select(r => new Seed(r.Index, r.Text("Id") ?? "",
        r.Int("Monster"), r.Int("Tier"), r.Int("SeedType"), r.Text("SeedAnimation"), r.Text("HatchAnimation"), r.BoolOrNull("__col10") == true))];

    static List<LevelValues> LoadLevels() => [.. ClientTables.TableOrEmpty("HarvestPerLevelValues").Select(r => new LevelValues(r.Int("Level"),
        r.Int("__col1"), r.Int("__col2"), r.Int("__col3"), r.Int("__col4"), r.Int("__col5"), r.Int("__col9"), r.Int("__col10"), r.Int("__col11")))];

    static Dictionary<int, int> LoadMoreLife() =>
        ClientTables.TableOrEmpty("HarvestLifeScalingPerLevel").ToDictionary(r => r.Int("Level"), r => r.Int("MoreLife"));

    /// <summary>The values for an area level, clamped to the table's levels; null without the table.</summary>
    public LevelValues? Level(int level)
    {
        for (int l = Math.Clamp(level, 0, _levels.Length - 1); l >= 0; l--)
            if (_levels[l] is { } values) return values;
        return _levels.FirstOrDefault(v => v is not null);
    }

    /// <summary>HarvestLifeScalingPerLevel's MoreLife for an area level (percent more life), clamped to the table.</summary>
    public int MoreLife(int level) => _moreLife.Length == 0 ? 0 : _moreLife[Math.Clamp(level, 0, _moreLife.Length - 1)];

    public Seed? SeedRow(int row) => (uint)row < (uint)Seeds.Count ? Seeds[row] : null;
}
