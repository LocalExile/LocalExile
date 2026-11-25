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

    /// <summary>One MercenarySkills row: the effect the skill is, the one it is in the encounter where the row names one, and
    /// the level a mercenary must reach to use it.</summary>
    public sealed record SkillRow(int GrantedEffect, int EncounterGrantedEffect, int RequiredLevel);

    /// <summary>What a MercenaryBuilds row lists as its skills (MercenarySkills rows): all of Skills1, <c>Skills2Count</c> of
    /// Skills2 and <c>Skills3Count</c> of Skills3.</summary>
    sealed record BuildSkills(int[] Skills1, int[] Skills2, int Skills2Count, int[] Skills3, int Skills3Count);

    static IReadOnlyList<Class> ClassesData = [];
    static IReadOnlyList<Build> BuildsData = [];
    static IReadOnlyDictionary<int, SkillRow> SkillRowsData = new Dictionary<int, SkillRow>();
    static IReadOnlyDictionary<int, BuildSkills> BuildSkillsData = new Dictionary<int, BuildSkills>();

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
        (SkillRowsData, BuildSkillsData) = LoadSkills();
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

    /// <summary>The GrantedEffects rows a mercenary of this build fights with: its whole Skills1, Skills2Count rows drawn from
    /// Skills2 and Skills3Count from Skills3 (uniformly: the table has no weights), among the rows the mercenary's level
    /// reaches (RequiredLevel), so a low-level mercenary still gets as many as the rows it can use allow; the client tables
    /// say nothing about whether the official server draws first and filters after. A row that names an EncounterGrantedEffect gives that one while
    /// <paramref name="encounter"/> (the duel) and its GrantedEffect otherwise. The draw comes from
    /// <paramref name="rng"/>, so the same seed gives the same mercenary the same skills.</summary>
    public static IReadOnlyList<int> SkillsOf(Build build, int level, Random rng, bool encounter = true)
    {
        if (!BuildSkillsData.TryGetValue(build.Row, out var listed)) return [];
        List<int> effects = [];
        void Add(int row)
        {
            if (!SkillRowsData.TryGetValue(row, out var skill) || skill.RequiredLevel > level) return;
            int effect = encounter && skill.EncounterGrantedEffect >= 0 ? skill.EncounterGrantedEffect : skill.GrantedEffect;
            if (effect >= 0 && !effects.Contains(effect)) effects.Add(effect);
        }
        foreach (int row in listed.Skills1) Add(row);
        foreach (int row in Draw(listed.Skills2, listed.Skills2Count, rng)) Add(row);
        foreach (int row in Draw(listed.Skills3, listed.Skills3Count, rng)) Add(row);
        return effects;

        bool Reaches(int row) => SkillRowsData.TryGetValue(row, out var skill) && skill.RequiredLevel <= level;

        IEnumerable<int> Draw(int[] rows, int count, Random rng)
        {
            if (count <= 0 || rows.Length == 0) return [];
            var pool = rows.Where(Reaches).ToArray();
            if (pool.Length == 0) return [];
            for (int i = pool.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }
            return pool.Take(Math.Min(count, pool.Length));
        }
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

    static (Dictionary<int, SkillRow>, Dictionary<int, BuildSkills>) LoadSkills()
    {
        Dictionary<int, SkillRow> skills = [];
        foreach (var r in ClientTables.TableOrEmpty("MercenarySkills"))
            skills[r.Index] = new SkillRow(r.IntOrNull("GrantedEffect") ?? -1, r.IntOrNull("EncounterGrantedEffect") ?? -1,
                r.IntOrNull("RequiredLevel") ?? 0);
        Dictionary<int, BuildSkills> builds = [];
        foreach (var r in ClientTables.TableOrEmpty("MercenaryBuilds"))
            builds[r.Index] = new BuildSkills(r.Ints("Skills1"), r.Ints("Skills2"), r.IntOrNull("Skills2Count") ?? 0,
                r.Ints("Skills3"), r.IntOrNull("Skills3Count") ?? 0);
        return (skills, builds);
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
