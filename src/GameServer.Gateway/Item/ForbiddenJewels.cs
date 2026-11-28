using GameServer.Core;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Forbidden Flame (<c>PuzzlePieceCleansingFireUnique__1</c>, <c>unique_jewel_grants_notable_hash_part_1</c>) and
/// Forbidden Flesh (<c>PuzzlePieceGreatTangleUnique__1</c>, <c>…_part_2</c>): the stat is the PassiveSkillGraphId of the
/// ascendancy notable the pair allocates ("Allocates {0} if you have the matching modifier on …", passive_hash), and the
/// client derives "Requires Class" from that notable. The Mods row's value (1) names no node, so a new jewel rolls one
/// (poewiki, 3.29): any notable of the class's ascendancies, the Scion's from the Ascendant (its "Path of the …" notables
/// and the ascendancy-named options); not Reliquarian or Luminary, not starting nodes, not the unused ones, not Tawhoa,
/// Forest's Strength (removed in 3.25), and not the hidden notables only the Uber Exarch and Uber Eater drop
/// (AscendancySpecialEldritch*, Harness the Void).
/// </summary>
public static class ForbiddenJewels
{
    public const string Flame = "Forbidden Flame", Flesh = "Forbidden Flesh";

    /// <summary>The character classes by Characters.dat row (Ascendancy.Character), as the variants name them.</summary>
    static readonly string[] Classes = ["Marauder", "Witch", "Scion", "Ranger", "Duelist", "Shadow", "Templar"];

    static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    {
        "AscendancyChieftan12", "AscendancyTrickster14",
    };

    static IReadOnlyDictionary<string, IReadOnlyList<int>>? byClass;

    public static bool Is(string uniqueName) => uniqueName is Flame or Flesh;

    public static bool IsClass(string name) => Classes.Contains(name, StringComparer.OrdinalIgnoreCase);

    static IReadOnlyDictionary<string, IReadOnlyList<int>> Notables()
    {
        if (byClass is { } loaded) return loaded;
        var classOf = new Dictionary<int, string>();
        foreach (var row in ClientTables.TableOrEmpty("Ascendancy"))
            if (row.Text("Id") is { } id && id is not ("Reliquarian" or "Luminary")
                && row.IntOrNull("Character") is int character && character >= 0 && character < Classes.Length)
                classOf[row.Index] = Classes[character];
        var result = Classes.ToDictionary(c => c, _ => new List<int>(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in ClientTables.TableOrEmpty("PassiveSkills"))
        {
            if (row.IntOrNull("AscendancyKey") is not int ascendancy || !classOf.TryGetValue(ascendancy, out var cls)) continue;
            string id = row.Text("Id") ?? "", name = row.Text("Name") ?? "";
            if (row.Bool("IsAscendancyStartingNode") || Excluded.Contains(id) || name.Length == 0 || name.StartsWith('[')
                || id.StartsWith("AscendancySpecialEldritch", StringComparison.Ordinal)
                || id.StartsWith("AscendancyCustom", StringComparison.Ordinal)) continue;
            bool pick = cls == "Scion"
                ? row.Bool("IsMultipleChoiceOption") || row.Bool("IsNotable") && !row.Bool("IsMultipleChoice")
                : row.Bool("IsNotable") && !row.Bool("IsMultipleChoice");
            if (pick && row.IntOrNull("PassiveSkillGraphId") is int graph and > 0) result[cls].Add(graph);
        }
        return byClass = result.ToDictionary(p => p.Key, p => (IReadOnlyList<int>)p.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A notable for a new jewel: of <paramref name="className"/> when given, else of a random class (each
    /// class equally likely, then each of its notables).</summary>
    public static int? Roll(string? className, Random random)
    {
        var notables = Notables();
        var classes = className is not null && notables.ContainsKey(className) ? [className]
            : notables.Where(p => p.Value.Count > 0).Select(p => p.Key).ToArray();
        if (classes.Length == 0) return null;
        var pool = notables[classes[random.Next(classes.Length)]];
        return pool.Count == 0 ? null : pool[random.Next(pool.Count)];
    }

    static IReadOnlyDictionary<int, int>? classRowOf;

    /// <summary>The Characters.dat row of the class whose ascendancy holds <paramref name="graph"/>; null for a node no
    /// Forbidden jewel can grant.</summary>
    public static int? ClassRowOf(int graph)
    {
        classRowOf ??= Notables().SelectMany(p => p.Value.Select(g => (Graph: g, Row: Array.FindIndex(Classes,
                c => c.Equals(p.Key, StringComparison.OrdinalIgnoreCase)))))
            .GroupBy(x => x.Graph).ToDictionary(g => g.Key, g => g.First().Row);
        return classRowOf.TryGetValue(graph, out int row) ? row : null;
    }

    /// <summary>A socketed jewel's half of the pair: Flame (part 1) or Flesh (part 2) and the notable's graph id, or
    /// null when the item carries neither stat (or only the Mods row's unrolled placeholder).</summary>
    public static (bool Flame, int Graph)? Read(Item item, ModCatalog catalog)
    {
        if (!item.Identified) return null;
        foreach (var mod in item.Mods)
            if (catalog.ByHash(mod.Hash) is { Stats.Count: > 0 } row && mod.Values.Count > 0 && mod.Values[0] > 1)
            {
                if (row.Stats[0].Id == Stat.UniqueJewelGrantsNotableHashPart1) return (true, mod.Values[0]);
                if (row.Stats[0].Id == Stat.UniqueJewelGrantsNotableHashPart2) return (false, mod.Values[0]);
            }
        return null;
    }

    /// <summary>The notable a character's socketed Forbidden pair allocates: a Flame and a Flesh with the same notable,
    /// of the character's class (<paramref name="classRow"/>, Characters.dat), not already allocated. Null otherwise.</summary>
    public static ushort? Granted(IEnumerable<Item> socketed, ModCatalog catalog, int classRow, IReadOnlySet<ushort> allocated)
    {
        var halves = socketed.Select(i => Read(i, catalog)).OfType<(bool Flame, int Graph)>().ToList();
        foreach (var flame in halves.Where(h => h.Flame))
            if (halves.Any(h => !h.Flame && h.Graph == flame.Graph) && flame.Graph is > 0 and <= ushort.MaxValue
                && ClassRowOf(flame.Graph) == classRow && !allocated.Contains((ushort)flame.Graph))
                return (ushort)flame.Graph;
        return null;
    }

    /// <summary>The jewel's mods with its notable rolled in place of the Mods row's placeholder.</summary>
    public static void Apply(List<ItemMod> mods, ModCatalog catalog, string? className, Random random)
    {
        for (int i = 0; i < mods.Count; i++)
        {
            if (catalog.ByHash(mods[i].Hash) is not { } mod || mod.Stats.Count == 0
                || mod.Stats[0].Id is not (Stat.UniqueJewelGrantsNotableHashPart1 or Stat.UniqueJewelGrantsNotableHashPart2)) continue;
            if (Roll(className, random) is int graph)
                mods[i] = mods[i] with { Values = [graph, .. mods[i].Values.Skip(1)] };
        }
    }
}
