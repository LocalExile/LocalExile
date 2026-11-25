using System.Globalization;
using System.Text.RegularExpressions;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain;

namespace GameServer.Modules.Ritual;

/// <summary>
/// Ritual's values: the client's own tables (RitualRuneTypes, RitualSpawnPatterns, RitualConstants, RitualBalancePerLevel,
/// DaemonSpawningData, MonsterPacks / MonsterPackEntries, MiscAnimated, BuffDefinitions, BaseItemTypes, WorldAreas, and
/// the rune .ao's locators) and the binary-verified 3.29.3c/d UI formulas.
/// </summary>
public static partial class RitualTables
{
    /// <summary>World units per cell.</summary>
    public const double Cell = 250.0 / 23.0;

    /// <summary>One capture frame in seconds.</summary>
    public const double Frame = 1.0 / 30;

    /// <summary>The altar's circle in cells: RitualConstants EncounterRadiusMaya (950) / Cell, floored = 87 [data].</summary>
    public static int RadiusCells => (int)(Constant("EncounterRadiusMaya", 950) / Cell);

    /// <summary>An altar is created when a player comes this near, in cells [capture].</summary>
    public const double RevealDistance = 250;

    /// <summary>Two altars of an area stand at least this far apart where the area has room [capture].</summary>
    public const double SpacingMin = 444;

    /// <summary>Guard packs per altar [chosen].</summary>
    public const int GuardPacksMin = 2, GuardPacksMax = 3;

    /// <summary>A guard pack stands this far from its altar, in cells, its monsters within <see cref="GuardScatter"/> of that spot.</summary>
    public const double GuardDistanceMin = 20, GuardDistanceMax = 70;
    public const int GuardScatter = 5;

    /// <summary>The revived monsters come in waves of 23, one every 2.5 frames, the first wave 60 frames after the start,
    /// the next when 25 % or less of the last one lives or 90 frames on [capture].</summary>
    public const int WaveSize = 23;
    public const double WaveSpacingFrames = 2.5, FirstWaveFrames = 60, WaveGapFrames = 90, NextWaveAlive = 0.25;

    /// <summary>A faded BloodPool is removed 57 frames after its 03 02 [capture].</summary>
    public const double PoolFadeFrames = 57;

    /// <summary>The light's scale: 1 + 8.46 x (dead / tied) [capture].</summary>
    public const double LightGain = 8.46;

    /// <summary>The area environment's end value, in seconds: raw f32 bits 0xb3d6bf95 (-1e-7).</summary>
    public const double EndEnvironmentSeconds = -1e-7;

    /// <summary>Maximum number of deferred items per account/league (verified in 0x14032049e: 50 - inv57.ItemCount).</summary>
    public const int MaxDeferredItems = 50;

    /// <summary>Minimum area level to allow deferring items (verified in 0x140321646: areas &lt;= 46 disable defer).</summary>
    public const int MinDeferAreaLevel = 47;

    public const string RitualSplinterId = "Metadata/Items/Currency/CurrencyRitualSplinter";
    public const string RitualVesselId = "Metadata/Items/Currency/CurrencyRitualStone";
    public const string BloodFilledVesselId = "Metadata/Items/MapFragments/RitualFragment";
    public const string AudienceWithTheKingId = "Metadata/Items/MapFragments/RitualBossFragment";
    public const string NamelessRingId = "Metadata/Items/Rings/RingRitual1";

    /// <summary>The tribute of a revived monster killed, by rarity: normal 10, magic 26, rare 135, unique 400 [capture]
    /// × (100 - MonsterFirstReviveLessPoints - MonsterRepeatReviveLessPoints × (revives - 1)) %, floor 0.</summary>
    public static int TributeOf(MonsterRarity rarity, int revives)
    {
        double value = rarity switch { MonsterRarity.Magic => 26, MonsterRarity.Rare => 135, MonsterRarity.Unique => 400, _ => 10 };
        int n = Math.Max(1, revives);
        double less = Constant("MonsterFirstReviveLessPoints", 0) + Constant("MonsterRepeatReviveLessPoints", 40) * (n - 1);
        return (int)(value * Math.Max(0, 100 - less) / 100);
    }

    /// <summary>Legacy static favours rows helper kept for capture-layout tests.</summary>
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

    /// <summary>The altars an area has at a level: RitualBalancePerLevel's __col1 and __col2 of the last row with MinLevel &lt;= level.</summary>
    public static (int Min, int Max) AltarRange(int level)
    {
        var b = BalanceOf(level);
        return (b.MinAltars, Math.Max(b.MinAltars, b.MaxAltars));
    }

    /// <summary>Maximum simultaneous monsters alive in a Ritual wave at <paramref name="level"/> (RitualBalancePerLevel.__col9).</summary>
    public static int MaxAlive(int level) => Math.Max(16, BalanceOf(level).MaxAlive);

    /// <summary>Base Tribute reroll cost at <paramref name="level"/> (RitualBalancePerLevel.__col13: 600..2000).</summary>
    public static int RerollBaseCost(int level) => Math.Max(0, BalanceOf(level).RerollCost);

    /// <summary>Exact binary-verified reroll cost formula (0x140615bd0).</summary>
    public static int RerollCost(int level, int rerollsUsed, int freeRerolls, int costPctFinal)
    {
        if (rerollsUsed < freeRerolls) return 0;
        int baseCost = RerollBaseCost(level);
        return Math.Max(0, (int)(baseCost * (1.0f + Math.Max(-100, costPctFinal) * 0.01f)));
    }

    /// <summary>Exact binary-verified maximum rerolls per area (0x14061d06a).</summary>
    public static int MaxRerolls(int additionalRerolls, bool cannotReroll) =>
        cannotReroll ? 0 : Math.Max(0, 1 + additionalRerolls);

    /// <summary>Exact binary-verified deferral fee formula (0x140320ce9 / 0x140321646):
    /// ceil(originalCost * (0.15 + 0.0015 * map_ritual_defer_reward_tribute_cost_+%)), clamped &gt;= 0.</summary>
    public static int DeferCost(int originalCost, int deferCostPct) =>
        Math.Max(0, (int)MathF.Ceiling(originalCost * (0.15f + 0.0015f * deferCostPct)));

    /// <summary>Discount applied to an item's buy cost when deferred (exact client formula 0x140321865: ceil(originalCost * 0.099), min 1).</summary>
    public static int DeferDiscount(int originalCost) =>
        Math.Max(1, (int)MathF.Ceiling(originalCost * 0.099f));

    /// <summary>Exact binary-verified deferral eligibility check (0x140321865).</summary>
    public static bool CanDefer(int areaLevel, ushort buyCost, ushort originalCost) =>
        areaLevel >= MinDeferAreaLevel
        && buyCost != ushort.MaxValue
        && originalCost != ushort.MaxValue
        && buyCost - (int)MathF.Ceiling(originalCost * 0.099f) > 0;

    static BalanceRow BalanceOf(int level)
    {
        var chosen = new BalanceRow(1, 3, 3, 24, 600);
        foreach (var row in Data.Balance)
            if (row.MinLevel <= level) chosen = row;
        return chosen;
    }

    public readonly record struct BalanceRow(int MinLevel, int MinAltars, int MaxAltars, int MaxAlive, int RerollCost);

    /// <summary>A RitualRuneTypes row.</summary>
    public sealed record Rune(int Row, string Id, int Weight, int LevelMin, int LevelMax, string Type, string? Ao,
        IReadOnlyList<string> Order, int? Environment, Daemon? Daemon, int BuffRow,
        IReadOnlyList<(uint Stat, int Value)> BuffStats, IReadOnlyList<int> ModRows, bool IsUber = false);

    /// <summary>A DaemonSpawningData row.</summary>
    public sealed record Daemon(string Id, IReadOnlyList<int> Varieties, int Max, int EveryMs);

    /// <summary>A Ritual guard pack.</summary>
    public sealed record GuardPack(string Id, int MinCount, int MaxCount, int MinLevel, int MaxLevel, int BossChance,
        IReadOnlyList<int> Bosses, IReadOnlyList<(int Variety, int Weight)> Entries);

    /// <summary>Whether the client's Ritual tables were read. False: no Ritual is placed.</summary>
    public static bool Loaded => Data.Runes.Count > 0;

    public static IReadOnlyList<Rune> Runes => Data.Runes;
    public static IReadOnlyList<string> RitualArmours => Data.RitualArmours;
    public static IReadOnlyList<string> RitualOmens => Data.RitualOmens;
    public static IReadOnlyList<string> ItemisedCorpses => Data.ItemisedCorpses;
    public static ushort WispBuffRow => Data.WispBuffRow;
    public static ushort AdrenalineBuffRow => Data.AdrenalineBuffRow;

    /// <summary>Campaign areas whose WorldAreas.ModsKeys carries <c>MapActsRitualSpawn</c> (retail Act 2 Northern Forest &amp; Dread Thicket)
    /// mapped to their forced altar count (3).</summary>
    public static int CampaignForcedAltars(string areaId) =>
        Data.CampaignForced.GetValueOrDefault(areaId, 0);

    /// <summary>The HiddenItem*Ritual BaseItemTypes Id matching an item's (width, height).</summary>
    public static string HiddenItemId(int width, int height)
    {
        if (Data.HiddenBySize.TryGetValue((width, height), out string? exact)) return exact;
        if (Data.HiddenBySize.TryGetValue((Math.Clamp(width, 1, 2), Math.Clamp(height, 1, 4)), out string? clamped)) return clamped;
        return "Metadata/Items/MicrotransactionCurrency/HiddenItem1x1Ritual";
    }

    /// <summary>Whether a daemon variety's object type is a real monster: its .ot descends from Metadata/Monsters/Monster
    /// with no daemon in its ancestry.</summary>
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

    /// <summary>RitualRuneTypes by SpawnWeight among LevelMin &lt;= level &lt;= LevelMax, boosting Uber runes by
    /// <paramref name="uberWeightPct"/> (<c>map_ritual_uber_rune_type_weighting_+% </c>) and restricting Nameless runes to maps &gt;= 81.</summary>
    public static Rune? PickRune(int level, Random rng, int uberWeightPct = 0, bool isMap = true)
    {
        var pool = new List<(Rune Rune, double Weight)>();
        double total = 0;
        foreach (var r in Data.Runes)
        {
            if (r.LevelMin > level || level > r.LevelMax || r.Weight <= 0) continue;
            bool nameless = r.Type.Contains("Nameless", StringComparison.OrdinalIgnoreCase)
                || r.Id.Contains("Nameless", StringComparison.OrdinalIgnoreCase);
            if (nameless && (!isMap || level < 81)) continue;
            double w = r.Weight;
            if (r.IsUber && uberWeightPct != 0) w = Math.Max(0, w * (100.0 + uberWeightPct) / 100.0);
            if (w <= 0) continue;
            pool.Add((r, w));
            total += w;
        }
        if (pool.Count == 0) return null;
        double x = rng.NextDouble() * total;
        foreach (var (r, w) in pool)
            if ((x -= w) < 0) return r;
        return pool[^1].Rune;
    }

    /// <summary>The guard packs that hold monsters at a level.</summary>
    public static List<GuardPack> GuardPacks(int level) =>
        [.. Data.Guards.Where(p => p.MinLevel <= level && level <= p.MaxLevel && p.Entries.Count > 0)];

    /// <summary>The rune's ModsKey rows as Explicit ItemMods.</summary>
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

    /// <summary>The mod set a ritual monster spawns with, for SpawnMonster's extraMods.</summary>
    public static MonsterModSet? RuneModSet(Rune rune, int level)
    {
        var mods = RuneMods(rune);
        return mods.Count == 0 ? null : MonsterModRolls.Create([.. mods], [], MonsterRarity.Normal, level);
    }

    /// <summary>One guard pack's monsters (MonsterVarieties rows).</summary>
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

    /// <summary>The rune .ao's locators in world units, in the altar's own frame.</summary>
    public static Locators LocatorsOf(string? ao) =>
        ao is not null && Data.Locators.TryGetValue(ao, out var found) ? found : Locators.None;

    public sealed record Locators(IReadOnlyList<(double X, double Y)> Inside, IReadOnlyList<(double X, double Y)> Outside,
        IReadOnlyList<(double X, double Y)> Daemon)
    {
        public static readonly Locators None = new([], [], []);

        public IReadOnlyList<(double X, double Y)> Part(string name) =>
            name == "Inside" ? Inside : name == "Outside" ? Outside : [];
    }

    /// <summary>A locator as a cell: turned by the altar's rotation, / Cell, rounded.</summary>
    public static (int X, int Y) LocatorCell((int X, int Y) centre, double rotation, (double X, double Y) at)
    {
        double c = Math.Cos(rotation), s = Math.Sin(rotation);
        return ((int)Math.Round(centre.X + (at.X * c - at.Y * s) / Cell), (int)Math.Round(centre.Y + (at.X * s + at.Y * c) / Cell));
    }

    // ---- loading ------------------------------------------------------------------------------------------------------

    sealed record Tables(
        List<Rune> Runes,
        Dictionary<string, double> Constants,
        List<BalanceRow> Balance,
        List<GuardPack> Guards,
        Dictionary<string, string[]> Chains,
        Dictionary<string, Locators> Locators,
        Dictionary<(int W, int H), string> HiddenBySize,
        List<string> RitualArmours,
        List<string> RitualOmens,
        List<string> ItemisedCorpses,
        Dictionary<string, int> CampaignForced,
        ushort WispBuffRow,
        ushort AdrenalineBuffRow);

    static Tables Data = new([], [], [], [], [], [], [], [], [], [], [], 2501, 1089);

    static double Constant(string id, double fallback) => Data.Constants.GetValueOrDefault(id, fallback);

    static ClientRow[] Rows(string table) => [.. ClientTables.TableOrEmpty(table)];

    static int Int(ClientRow row, string column, int fallback = 0) => row.IntOrNull(column) ?? fallback;

    static string Str(ClientRow row, string column) => row.TextOrNull(column) ?? "";

    static List<int> Ints(ClientRow row, string column) => [.. row.Ints(column)];

    /// <summary>Builds this class's game data at startup (the module's LoadData), not on first use.</summary>
    public static void LoadGameData()
    {
        var patterns = Rows("RitualSpawnPatterns").Select(r => (IReadOnlyList<string>)r.Texts("SpawnOrder")).ToList();
        var daemons = Rows("DaemonSpawningData")
            .Select(r => new Daemon(Str(r, "Id"), Ints(r, "MonsterVarieties"), Int(r, "__col2"), Int(r, "__col4")))
            .ToList();
        var animated = Rows("MiscAnimated");
        var buffs = Rows("BuffDefinitions");
        ushort wispBuff = 2501, adrenalineBuff = 1089;
        for (int i = 0; i < buffs.Length; i++)
        {
            string bid = Str(buffs[i], "Id");
            if (bid == "ritual_wisp_buff") wispBuff = (ushort)i;
            else if (bid == "adrenaline") adrenalineBuff = (ushort)i;
        }

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
            int buffRow = r.IntOrNull("BuffDefinitionsKey") ?? -1;
            var buffValues = Ints(r, "BuffStatValues");
            var buffKeys = buffRow >= 0 && buffRow < buffs.Length ? buffs[buffRow].Ints("StatsKeys") : [];
            var buffStats = buffKeys.Zip(buffValues, (k, v) => ((uint)(k + 1), v)).ToList();
            bool isUber = Int(r, "__col18") != 0;
            runes.Add(new Rune(i, Str(r, "Id"), Int(r, "SpawnWeight"), Int(r, "LevelMin"), Int(r, "LevelMax"), Str(r, "Type"),
                ao, pattern, r.IntOrNull("EnvironmentsKey"), daemon, buffRow, buffStats, Ints(r, "ModsKey"), isUber));
            if (ao is not null && !locators.ContainsKey(ao)) locators[ao] = ReadLocators(ao);
        }

        var constants = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var r in Rows("RitualConstants"))
            if (r.RealOrNull("Value") is double v) constants[Str(r, "Id")] = v;

        var balance = Rows("RitualBalancePerLevel")
            .Select(r => new BalanceRow(Int(r, "MinLevel"), Int(r, "__col1", 3), Int(r, "__col2", 3), Int(r, "__col9", 24), Int(r, "__col13", 600)))
            .ToList();

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

        // Load Ritual reward base pools from BaseItemTypes + ItemClasses.
        var hiddenBySize = new Dictionary<(int, int), string>();
        var ritualArmours = new List<string>();
        var ritualOmens = new List<string>();
        var corpses = new List<string>();
        int corpseClassRow = -1;
        var itemClasses = Rows("ItemClasses");
        for (int i = 0; i < itemClasses.Length; i++)
            if (Str(itemClasses[i], "Id") == "ItemisedCorpse") { corpseClassRow = i; break; }
        foreach (var r in Rows("BaseItemTypes"))
        {
            string id = Str(r, "Id");
            if (id.Contains("HiddenItem", StringComparison.Ordinal) && id.EndsWith("Ritual", StringComparison.Ordinal))
                hiddenBySize[(Math.Max(1, Int(r, "Width", 1)), Math.Max(1, Int(r, "Height", 1)))] = id;
            else if (id.StartsWith("Metadata/Items/Armours/", StringComparison.Ordinal) && id.Contains("Ritual", StringComparison.Ordinal))
                ritualArmours.Add(id);
            else if (id.StartsWith("Metadata/Items/Currency/AncestralOmen", StringComparison.Ordinal))
                ritualOmens.Add(id);
            else if (corpseClassRow >= 0 && r.IntOrNull("ItemClassesKey") == corpseClassRow)
                corpses.Add(id);
        }

        // Load retail campaign forced Ritual areas dynamically from WorldAreas.ModsKeys -> Mods("MapActsRitualSpawn").
        var campaignForced = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var modsRows = Rows("Mods");
        int actsRitualModRow = -1, actsRitualCount = 3;
        for (int i = 0; i < modsRows.Length; i++)
        {
            if (Str(modsRows[i], "Id") != "MapActsRitualSpawn") continue;
            actsRitualModRow = i;
            // Read forced spawn count from the mod's stat values if present (map_forced_ritual_spawns = 3).
            for (int s = 1; s <= 6; s++)
            {
                if (modsRows[i].IntOrNull($"StatsKey{s}") is int sk && (uint)(sk + 1) == Stat.MapForcedRitualSpawns)
                    actsRitualCount = Math.Max(1, Int(modsRows[i], $"Stat{s}Min", 3));
            }
            break;
        }
        if (actsRitualModRow >= 0)
        {
            foreach (var wa in Rows("WorldAreas"))
            {
                var areaMods = Ints(wa, "ModsKeys");
                if (areaMods.Contains(actsRitualModRow) && Str(wa, "Id") is { Length: > 0 } areaId)
                    campaignForced[areaId] = actsRitualCount;
            }
        }

        Data = new Tables(runes, constants, balance, guards, ReadChains(), locators,
            hiddenBySize, ritualArmours, ritualOmens, corpses, campaignForced, wispBuff, adrenalineBuff);
    }

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
