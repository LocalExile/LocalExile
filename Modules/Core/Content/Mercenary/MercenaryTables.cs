using GameServer.Core;

namespace GameServer.Modules.Mercenary;

/// <summary>
/// What the client's own tables say about the mercenaries: the 13 MercenaryClasses (a house, an attribute, the hostile
/// variety a map mercenary is and the <c>*Allied</c> twin a hire becomes) and the MercenaryBuilds of each, which name the
/// build in the create's Mercenary component by their HASH16. Built once when the module loads.
/// </summary>
public static class MercenaryTables
{
    /// <summary>One MercenaryClasses row. <paramref name="Attribute"/> is the Id of its MercenaryAttributes row (Str, Dex, Int,
    /// StrDex, StrInt, DexInt, StrDexInt), which decides what its house wears.</summary>
    public sealed record Class(int Row, string Id, string Path, string AlliedPath, string House, string Attribute, bool Supported);

    /// <summary>One MercenaryBuilds row: the build a mercenary carries, its name, and the HASH16 the create names it by.</summary>
    public sealed record Build(int Row, string Id, int ClassRow, string Name, ushort Hash16, bool Infamous);

    static IReadOnlyList<Class> ClassesData = [];
    static IReadOnlyList<Build> BuildsData = [];

    /// <summary>The Rulesets rows of MercenaryEncounterRules (161) and MercenaryAreaRules (162), looked up by Id: a copy without
    /// the client rows then lists neither instead of a wrong row.</summary>
    public static byte? EncounterRulesRow { get; private set; }
    public static byte? AreaRulesRow { get; private set; }

    public static IReadOnlyList<Class> Classes => ClassesData;
    public static IReadOnlyList<Build> Builds => BuildsData;

    /// <summary>Builds this module's game data at startup (before the Gateway listens), not on first use.</summary>
    public static void LoadGameData()
    {
        ClassesData = LoadClasses();
        BuildsData = LoadBuilds();
        EncounterRulesRow = RulesetRow("MercenaryEncounterRules");
        AreaRulesRow = RulesetRow("MercenaryAreaRules");
    }

    static byte? RulesetRow(string id)
    {
        foreach (var r in ClientTables.TableOrEmpty("Rulesets"))
            if (string.Equals(r.TextOrNull("Id"), id, StringComparison.Ordinal) && r.Index is >= 0 and < 256)
                return (byte)r.Index;
        return null;
    }

    /// <summary>The classes this server can place: a variety it can serialise (the Mercenary component is written) and at
    /// least one build of its own.</summary>
    public static IReadOnlyList<Class> Eligible() =>
        [.. Classes.Where(c => c.Path.Length > 0 && c.Supported && BuildsOf(c.Row).Count > 0)];

    /// <summary>The builds one class draws from: its own rows, without the IsInfamous variants. How often the official server
    /// picks an infamous build is a spawn-chance GameStat of the class (HouseSpawnChanceStats), which nothing here reads, so
    /// only the plain builds are drawn.</summary>
    public static IReadOnlyList<Build> BuildsOf(int classRow) =>
        [.. Builds.Where(b => b.ClassRow == classRow && !b.Infamous)];

    /// <summary>One eligible class and one of its builds, drawn uniformly from <paramref name="rng"/>; null when no class is
    /// eligible.</summary>
    public static (Class Kind, Build Build)? Pick(Random rng)
    {
        var pool = Eligible();
        if (pool.Count == 0) return null;
        var kind = pool[rng.Next(pool.Count)];
        var builds = BuildsOf(kind.Row);
        return (kind, builds[rng.Next(builds.Count)]);
    }

    public static Class? ClassOf(int row) => Classes.FirstOrDefault(c => c.Row == row);

    public static Build? BuildOf(int row) => Builds.FirstOrDefault(b => b.Row == row);

    static List<Class> LoadClasses()
    {
        var all = global::Game.Monsters.All;
        string PathOf(int row) => row >= 0 && row < all.Count ? all[row].Id : "";
        bool Supported(int row) => row >= 0 && row < all.Count && all[row].Supported;
        var attributes = ClientTables.TableOrEmpty("MercenaryAttributes");
        var rows = new List<Class>();
        foreach (var r in ClientTables.TableOrEmpty("MercenaryClasses"))
        {
            int hostile = r.IntOrNull("MonsterVariety") ?? -1;
            int allied = r.IntOrNull("MonsterVarietyAllied") ?? -1;
            string attribute = r.IntOrNull("Attribute") is int a && attributes.Row(a) is { } row ? row.TextOrNull("Id") ?? "" : "";
            rows.Add(new Class(r.Index, r.TextOrNull("Id") ?? "", PathOf(hostile), PathOf(allied),
                r.TextOrNull("HouseName") ?? "", attribute, Supported(hostile)));
        }
        return rows;
    }

    static List<Build> LoadBuilds()
    {
        var rows = new List<Build>();
        foreach (var r in ClientTables.TableOrEmpty("MercenaryBuilds"))
            rows.Add(new Build(r.Index, r.TextOrNull("Id") ?? "", r.IntOrNull("Class") ?? -1, r.TextOrNull("Name") ?? "",
                (ushort)(r.IntOrNull("HASH16") ?? 0), r.IntOrNull("IsInfamous") is int infamous && infamous != 0));
        return rows;
    }
}
