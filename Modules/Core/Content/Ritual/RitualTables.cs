using System.Globalization;
using System.Text.RegularExpressions;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain;

namespace GameServer.Modules.Ritual;

/// <summary>
/// Ritual's values: the client's own tables (RitualRuneTypes, RitualSpawnPatterns, RitualConstants, RitualBalancePerLevel,
/// DaemonSpawningData, MonsterPacks / MonsterPackEntries, MiscAnimated, BuffDefinitions and the rune .ao's locators) and the
/// numbers the official captures gave. A constant says [data] when it is read from the client, [capture] when it was
/// measured, and [chosen] when no source gives it and it is the server's own.
/// </summary>
public static partial class RitualTables
{
    /// <summary>World units per cell.</summary>
    public const double Cell = 250.0 / 23.0;

    /// <summary>One capture frame in seconds.</summary>
    public const double Frame = 1.0 / 30;

    /// <summary>The altar's circle in cells: RitualConstants EncounterRadiusMaya (950) / Cell, floored = 87 [data; the
    /// RitualBlocker's scale 87/23 and the 0x57 in the Interactable body agree, capture].</summary>
    public static int RadiusCells => (int)(Constant("EncounterRadiusMaya", 950) / Cell);

    /// <summary>An altar is created when a player comes this near, in cells [capture: the two areas with altars (levels 72 and
    /// 77) created them 214 to 306 cells from the player's last known cell, median 254, 11 creates; the same objects were
    /// removed at 234 to 332 once the player walked off (the area's ordinary object range, as the Abyss pieces)].</summary>
    public const double RevealDistance = 250;

    /// <summary>Two altars of an area stand at least this far apart where the area has room [capture: 444 to 1130 cells
    /// between the three altars of the level 72 area, 626 between the two of the level 77 area]; a small area falls back
    /// to two circles apart.</summary>
    public const double SpacingMin = 444;

    /// <summary>Guard packs per altar [chosen].</summary>
    public const int GuardPacksMin = 2, GuardPacksMax = 3;

    /// <summary>A guard pack stands this far from its altar, in cells, its monsters within <see cref="GuardScatter"/> of that
    /// spot [chosen: the circle is 87].</summary>
    public const double GuardDistanceMin = 20, GuardDistanceMax = 70;
    public const int GuardScatter = 5;

    /// <summary>The revived monsters come in waves of 23, one every 2.5 frames, the first wave 60 frames after the start,
    /// the next when 25 % or less of the last one lives or 90 frames on [capture: waves 22..26, 2..3 frames apart, 50..97
    /// frames between, the first at 60 / 60].</summary>
    public const int WaveSize = 23;
    public const double WaveSpacingFrames = 2.5, FirstWaveFrames = 60, WaveGapFrames = 90, NextWaveAlive = 0.25;

    /// <summary>A faded BloodPool is removed 57 frames after its 03 02 [capture].</summary>
    public const double PoolFadeFrames = 57;

    /// <summary>The light's scale: 1 + 8.46 x (dead / tied) [capture].</summary>
    public const double LightGain = 8.46;

    /// <summary>The area environment's end value, in seconds: the capture's raw f32 bits are 0xb3d6bf95, which is -1e-7.</summary>
    public const double EndEnvironmentSeconds = -1e-7;

    /// <summary>The tribute of a revived monster killed, by rarity: normal 10, magic 26, rare 135, unique 400 [capture:
    /// steps 10 / 26 / 400, 135 from the wiki] x (100 - MonsterFirstReviveLessPoints - MonsterRepeatReviveLessPoints x
    /// (revives - 1)) %, floor 0 [data: 0 and 40; capture 6 / 15..16 / 240 on the second revive].</summary>
    public static int TributeOf(MonsterRarity rarity, int revives)
    {
        double value = rarity switch { MonsterRarity.Magic => 26, MonsterRarity.Rare => 135, MonsterRarity.Unique => 400, _ => 10 };
        int n = Math.Max(1, revives);
        double less = Constant("MonsterFirstReviveLessPoints", 0) + Constant("MonsterRepeatReviveLessPoints", 40) * (n - 1);
        return (int)(value * Math.Max(0, 100 - less) / 100);
    }

    /// <summary>The favours window's rows with a number of altars done, as the one capture measured them (level 77): wave 1
    /// (ids 0x0c..0x15) always, wave 2 (ids 0x17, 0x19, 0x1b, 0x1d, 0x1f) once two altars are done, the rest cost-0xffff
    /// placeholders, ids ascending, both costs equal, flag 0. Other levels' costs are not measured, so every level gets
    /// these.</summary>
    public static IReadOnlyList<(uint Id, ushort Cost, ushort Cost2, byte Flag)> FavoursRows(int done)
    {
        ushort[] wave1 = [816, 704, 568, 416, 402, 378, 357, 329, 294, 273];
        ushort[] wave2 = [840, 960, 976, 1185, 1290];
        var rows = new List<(uint Id, ushort Cost, ushort Cost2, byte Flag)>();
        for (uint id = 2; id < (done >= 2 ? 7u : 12u); id++) rows.Add((id, 0xffff, 0xffff, 0));
        for (int i = 0; i < wave1.Length; i++) rows.Add(((uint)(0x0c + i), wave1[i], wave1[i], 0));
        if (done >= 2)
            for (int i = 0; i < wave2.Length; i++) rows.Add(((uint)(0x17 + 2 * i), wave2[i], wave2[i], 0));
        return rows;
    }

    /// <summary>The altars an area has at a level: RitualBalancePerLevel's first two i32 columns of the last row with
    /// MinLevel &lt;= level [data: 3..3 below level 46, 3..4 from it; capture: 3 altars in two areas].</summary>
    public static (int Min, int Max) AltarRange(int level)
    {
        int lo = 3, hi = 3;
        foreach (var (minLevel, a, b) in Data.Balance)
            if (minLevel <= level) (lo, hi) = (a, b);
        return (lo, Math.Max(lo, hi));
    }

    /// <summary>A RitualRuneTypes row: its weight and level range, the rune's .ao (MiscAnimatedKey1's AOFile), its
    /// RitualSpawnPatterns order, its Environments row and its DaemonSpawningData row. The rune's buff: BuffRow is its
    /// BuffDefinitionsKey, BuffStats that row's StatsKeys (as stat ids) paired in order with BuffStatValues, and ModRows
    /// its ModsKey rows (the mods the rune's monsters carry).</summary>
    public sealed record Rune(int Row, string Id, int Weight, int LevelMin, int LevelMax, string Type, string? Ao,
        IReadOnlyList<string> Order, int? Environment, Daemon? Daemon, int BuffRow,
        IReadOnlyList<(uint Stat, int Value)> BuffStats, IReadOnlyList<int> ModRows)
    {
        /// <summary>The rune's ModsKey rows resolved once, in <see cref="LoadGameData"/>: the Explicit ItemMods its
        /// monsters carry. Empty for a rune built without loading.</summary>
        public IReadOnlyList<ItemMod> Mods { get; init; } = [];
    }

    /// <summary>A DaemonSpawningData row: its MonsterVarieties rows, at most <see cref="Max"/> of them, one every
    /// <see cref="EveryMs"/> [data; capture: GoldenCoinDaemon at 0 / 121 / 244 frames = 4000 ms].</summary>
    public sealed record Daemon(string Id, IReadOnlyList<int> Varieties, int Max, int EveryMs);

    /// <summary>A Ritual guard pack (MonsterPacks azmeri-standalone-* / ritual-azmeri-*): its count, its level range, its
    /// boss chance and bosses, its entries by weight [data].</summary>
    public sealed record GuardPack(string Id, int MinCount, int MaxCount, int MinLevel, int MaxLevel, int BossChance,
        IReadOnlyList<int> Bosses, IReadOnlyList<(int Variety, int Weight)> Entries);

    /// <summary>Whether the client's Ritual tables were read. False: no Ritual is placed.</summary>
    public static bool Loaded => Data.Runes.Count > 0;

    public static IReadOnlyList<Rune> Runes => Data.Runes;

    /// <summary>Whether a daemon variety's object type is a real monster: its .ot (the variety's Id, else its BaseType)
    /// descends from Metadata/Monsters/Monster with no daemon in its ancestry (the PhysicalBleedTotem's chain is Monster
    /// -> Totems/TotemBase, the GoldenCoinDaemon's Monster -> Daemon/AbstractDaemon) [data: object-types.json "chain"].
    /// A type the table does not know has no chain and stays an object.</summary>
    public static bool IsMonsterType(MonsterCatalog.Variety variety)
    {
        var chains = Data.Chains;
        if (!chains.TryGetValue(variety.Id, out string[]? chain) && !chains.TryGetValue(variety.BaseType, out chain))
            return false;
        bool monster = false;
        foreach (string ancestor in chain)
        {
            if (ancestor == "Metadata/Monsters/Monster") monster = true;
            if (ancestor[(ancestor.LastIndexOf('/') + 1)..].EndsWith("Daemon", StringComparison.Ordinal)) return false;
        }
        return monster;
    }

    /// <summary>RitualRuneTypes by SpawnWeight among LevelMin &lt;= level &lt;= LevelMax [data].</summary>
    public static Rune? PickRune(int level, Random rng)
    {
        var pool = Data.Runes.Where(r => r.LevelMin <= level && level <= r.LevelMax && r.Weight > 0).ToList();
        if (pool.Count == 0) return null;
        double x = rng.NextDouble() * pool.Sum(r => r.Weight);
        foreach (var r in pool)
            if ((x -= r.Weight) < 0) return r;
        return pool[^1];
    }

    /// <summary>The guard packs that hold monsters at a level.</summary>
    public static List<GuardPack> GuardPacks(int level) =>
        [.. Data.Guards.Where(p => p.MinLevel <= level && level <= p.MaxLevel && p.Entries.Count > 0)];

    /// <summary>The rune's ModsKey rows as Explicit ItemMods (a fixed-value mod reads its StatNMin, a row the Mods table
    /// does not know is skipped).</summary>
    public static IReadOnlyList<ItemMod> RuneMods(Rune rune)
    {
        var table = ClientTables.TableOrEmpty("Mods");
        var mods = new List<ItemMod>();
        foreach (int row in rune.ModRows)
        {
            if (row < 0 || row >= table.Count) continue;
            var r = table[row];
            if (r.IntOrNull("HASH16") is not int hash) continue;
            var values = new List<int>();
            for (int i = 1; i <= 8; i++)
                if (r.IntOrNull($"StatsKey{i}") is int)
                    values.Add(r.IntOrNull($"Stat{i}Min") ?? 0);
            mods.Add(new ItemMod(ModSection.Explicit, (ushort)hash, values));
        }
        return mods;
    }

    /// <summary>The mod set a ritual monster spawns with, for SpawnMonster's extraMods: the rune's ModsKey rows resolved
    /// through the mods catalog (their stats only, so the rarity here is always Normal). Null when the rune carries no mods.
    /// Uses the rune's <see cref="Rune.Mods"/>, resolved once in <see cref="LoadGameData"/>; a rune built without loading
    /// falls back to resolving its rows here.</summary>
    public static MonsterModSet? RuneModSet(Rune rune, int level)
    {
        var mods = rune.Mods.Count > 0 ? rune.Mods : RuneMods(rune);
        return mods.Count == 0 ? null : MonsterModRolls.Create([.. mods], [], MonsterRarity.Normal, level);
    }

    /// <summary>One guard pack's monsters (MonsterVarieties rows): MinCount..MaxCount entries by Weight, and the boss at
    /// BossMonsterSpawnChance % [data].</summary>
    public static List<int> RollPack(GuardPack pack, Random rng)
    {
        var rows = new List<int>();
        int total = pack.Entries.Sum(e => e.Weight);
        int n = rng.Next(pack.MinCount, Math.Max(pack.MinCount, pack.MaxCount) + 1);
        for (int k = 0; k < n && total > 0; k++)
        {
            double x = rng.NextDouble() * total;
            foreach (var (variety, weight) in pack.Entries)
                if ((x -= weight) < 0) { rows.Add(variety); break; }
        }
        if (pack.Bosses.Count > 0 && rng.NextDouble() * 100 < pack.BossChance) rows.Add(pack.Bosses[rng.Next(pack.Bosses.Count)]);
        return rows;
    }

    /// <summary>The rune .ao's locators in world units, in the altar's own frame: "Inside_N", "Outside_N" and the "_daemon"
    /// ones, each list in N order [data]. Empty lists for a rune without an .ao or one that cannot be read.</summary>
    public static Locators LocatorsOf(string? ao) =>
        ao is not null && Data.Locators.TryGetValue(ao, out var found) ? found : Locators.None;

    public sealed record Locators(IReadOnlyList<(double X, double Y)> Inside, IReadOnlyList<(double X, double Y)> Outside,
        IReadOnlyList<(double X, double Y)> Daemon)
    {
        public static readonly Locators None = new([], [], []);

        public IReadOnlyList<(double X, double Y)> Part(string name) =>
            name == "Inside" ? Inside : name == "Outside" ? Outside : [];
    }

    /// <summary>A locator as a cell: turned by the altar's rotation, / Cell, rounded [capture: 21/22 spots of one altar within
    /// 2 cells].</summary>
    public static (int X, int Y) LocatorCell((int X, int Y) centre, double rotation, (double X, double Y) at)
    {
        double c = Math.Cos(rotation), s = Math.Sin(rotation);
        return ((int)Math.Round(centre.X + (at.X * c - at.Y * s) / Cell), (int)Math.Round(centre.Y + (at.X * s + at.Y * c) / Cell));
    }

    // ---- loading ------------------------------------------------------------------------------------------------------

    sealed record Tables(List<Rune> Runes, Dictionary<string, double> Constants, List<(int MinLevel, int Min, int Max)> Balance,
        List<GuardPack> Guards, Dictionary<string, string[]> Chains, Dictionary<string, Locators> Locators);

    static Tables Data = new([], [], [], [], [], []);

    static double Constant(string id, double fallback) => Data.Constants.GetValueOrDefault(id, fallback);

    static ClientRow[] Rows(string table) => [.. ClientTables.TableOrEmpty(table)];

    static int Int(ClientRow row, string column, int fallback = 0) => row.IntOrNull(column) ?? fallback;

    static string Str(ClientRow row, string column) => row.TextOrNull(column) ?? "";

    static List<int> Ints(ClientRow row, string column) => [.. row.Ints(column)];

    /// <summary>Builds this class's game data at startup (the module's LoadData), not on first use.</summary>
    public static void LoadGameData()
    {
        var patterns = Rows("RitualSpawnPatterns").Select(r => (IReadOnlyList<string>)r.Texts("SpawnOrder")).ToList();
        // DaemonSpawningData: Id, MonsterVarieties, then __col2 (the most at once) and __col4 (every so many ms).
        var daemons = Rows("DaemonSpawningData")
            .Select(r => new Daemon(Str(r, "Id"), Ints(r, "MonsterVarieties"), Int(r, "__col2"), Int(r, "__col4")))
            .ToList();
        var animated = Rows("MiscAnimated");
        var buffs = Rows("BuffDefinitions");
        var runes = new List<Rune>();
        var runeRows = Rows("RitualRuneTypes");
        var locators = new Dictionary<string, Locators>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < runeRows.Length; i++)
        {
            var r = runeRows[i];
            string? ao = r.IntOrNull("MiscAnimatedKey1") is int a && a >= 0 && a < animated.Length ? Str(animated[a], "AOFile") : null;
            if (string.IsNullOrEmpty(ao)) ao = null;
            var pattern = Ints(r, "SpawnPatterns") is { Count: > 0 } p && p[0] >= 0 && p[0] < patterns.Count ? patterns[p[0]] : ["Outside"];
            var daemon = r.IntOrNull("DaemonSpawningDataKey") is int d && d >= 0 && d < daemons.Count ? daemons[d] : null;
            // The buff: BuffDefinitionsKey is the buff's row, BuffStatValues its values in the order of that row's StatsKeys;
            // a stat id is the Stats row + 1. A buff with no stats (the charges / necromantic runes) carries its effect in
            // ModsKey instead.
            int buffRow = r.IntOrNull("BuffDefinitionsKey") ?? -1;
            var buffValues = Ints(r, "BuffStatValues");
            var buffKeys = buffRow >= 0 && buffRow < buffs.Length ? buffs[buffRow].Ints("StatsKeys") : [];
            var buffStats = buffKeys.Zip(buffValues, (k, v) => ((uint)(k + 1), v)).ToList();
            var rune = new Rune(i, Str(r, "Id"), Int(r, "SpawnWeight"), Int(r, "LevelMin"), Int(r, "LevelMax"), Str(r, "Type"),
                ao, pattern, r.IntOrNull("EnvironmentsKey"), daemon, buffRow, buffStats, Ints(r, "ModsKey"));
            runes.Add(rune with { Mods = RuneMods(rune) });
            if (ao is not null && !locators.ContainsKey(ao)) locators[ao] = ReadLocators(ao);
        }
        var constants = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var r in Rows("RitualConstants"))
            if (r.RealOrNull("Value") is double v) constants[Str(r, "Id")] = v;
        // RitualBalancePerLevel: MinLevel, then __col1 and __col2: the altar range.
        var balance = Rows("RitualBalancePerLevel").Select(r => (Int(r, "MinLevel"), Int(r, "__col1"), Int(r, "__col2"))).ToList();
        var packs = Rows("MonsterPacks");
        var byPack = new Dictionary<int, List<(int, int)>>();
        foreach (var e in Rows("MonsterPackEntries"))
            if (e.IntOrNull("MonsterPacksKey") is int pk && e.IntOrNull("MonsterVarietiesKey") is int mv)
            {
                if (!byPack.TryGetValue(pk, out var list)) byPack[pk] = list = [];
                list.Add((mv, Int(e, "Weight")));
            }
        var guards = new List<GuardPack>();
        for (int i = 0; i < packs.Length; i++)
        {
            string id = Str(packs[i], "Id");
            if (!id.StartsWith("azmeri-standalone-", StringComparison.Ordinal) && !id.StartsWith("ritual-azmeri-", StringComparison.Ordinal)) continue;
            guards.Add(new GuardPack(id, Math.Max(1, Int(packs[i], "MinCount", 1)), Math.Max(1, Int(packs[i], "MaxCount", 1)),
                Int(packs[i], "MinLevel"), Int(packs[i], "MaxLevel", 100), Int(packs[i], "BossMonsterSpawnChance"),
                Ints(packs[i], "BossMonster_MonsterVarietiesKeys"), byPack.GetValueOrDefault(i) ?? []));
        }
        Data = new Tables(runes, constants, balance, guards, ReadChains(), locators);
    }

    // Every object type's ancestry, root first (object-types.json "chain"). A table without the column gives no chain.
    static Dictionary<string, string[]> ReadChains()
    {
        var byType = new Dictionary<string, string[]>(StringComparer.Ordinal);
        try
        {
            foreach (var (type, row) in ObjectTypes.All())
                byType[type] = row.Texts("chain");
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
        {
            Console.WriteLine($"[ritual] object types' chains not read ({ex.GetType().Name}); daemon varieties stay objects");
            return [];
        }
        return byType;
    }

    static Locators ReadLocators(string ao)
    {
        var found = new Dictionary<string, List<(string Name, (double X, double Y) At)>>
        {
            ["Inside"] = [], ["Outside"] = [], ["daemon"] = [],
        };
        string text = "";
        try
        {
            if (GamePack.Default.TryRead(ao.ToLowerInvariant()) is { } bytes) text = TerrainText.Decode(bytes);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.WriteLine($"[ritual] {ao}: not read ({ex.GetType().Name}); the revived monsters stand at the altar");
        }
        foreach (Match m in LocatorLine().Matches(text))
        {
            string name = m.Groups[1].Value;
            var at = (double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
            if (name.EndsWith("_daemon", StringComparison.Ordinal)) found["daemon"].Add((name, at));
            else if (name.StartsWith("Inside", StringComparison.Ordinal)) found["Inside"].Add((name, at));
            else if (name.StartsWith("Outside", StringComparison.Ordinal)) found["Outside"].Add((name, at));
        }
        List<(double X, double Y)> Sorted(string key) =>
            [.. found[key].OrderBy(t => t.Name.Split('_')[0], StringComparer.Ordinal).ThenBy(t => LocatorNumber(t.Name)).Select(t => t.At)];
        return new Locators(Sorted("Inside"), Sorted("Outside"), Sorted("daemon"));
    }

    static int LocatorNumber(string name)
    {
        var m = Regex.Match(name, @"_(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    [GeneratedRegex(@"locator = ""(\S+) ([-\d.]+) ([-\d.]+)")]
    private static partial Regex LocatorLine();
}
