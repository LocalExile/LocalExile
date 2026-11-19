using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Deepwater;

/// <summary>Allflame Charts as items (BaseItemTypes 5413-5415; ChartThermalVents 5416 never shipped). A chart's
/// DeepwaterChart component holds its Area Level, its rotation and its DeepwaterRooms row (13-items §2); its item level is
/// its area level. Rarity follows world.db <c>deepwater_chart_*</c> (G), and magic and rare affixes are the ordinary item
/// rolls over Mods domain 39 (prefixes gt 1, suffixes gt 2, tag <c>chart</c>, tier by Level ≤ item level), so ordinary
/// currency works on a chart as on any item and keeps its component. A charted chart gets one Adjacent or Voyage implicit
/// (gt 37) in place of MapDeepwaterChartImplicit ("revealed once Charted").</summary>
internal static class DeepwaterCharts
{
    /// <summary>The shipped chart bases, by Id.</summary>
    public static readonly string[] Bases =
        ["Metadata/Items/Deepwater/ChartSandy", "Metadata/Items/Deepwater/ChartCoralForest", "Metadata/Items/Deepwater/ChartCoralReef"];
    public const string Sandy = "Metadata/Items/Deepwater/ChartSandy";
    /// <summary>QuestRewardOffers Id of Valerie's tutorial chart (row 130).</summary>
    public const string TutorialOffer = "DeepwaterTutorialReward";
    /// <summary>Valerie's chart: an area level 2 magic Sandy Seabed Chart (the wiki's Maiden Voyage).</summary>
    public const int TutorialLevel = 2;
    /// <summary>QuestFlags 3560 (the tracker's "found a Chart", QuestStates 1981).</summary>
    public const string ChartFound = "DeepwaterChartFound";
    /// <summary>Mods 40149 MapDeepwaterChartImplicit, every chart base's implicit: "Voyage Modifier will be revealed once Charted".</summary>
    const string Placeholder = "MapDeepwaterChartImplicit";
    const int ChartDomain = 39, ChartImplicit = 37;

    public static bool IsChart(ItemCatalog.BaseType type) => Array.IndexOf(Bases, type.Id) >= 0;
    public static bool IsChart(ItemCatalog catalog, Item item) => catalog.ByHash(item.TypeHash) is { } type && IsChart(type);

    static int Setting(string key) => DeepwaterLoot.Setting(key);

    /// <summary>A new chart of this base: item level and Area Level <paramref name="areaLevel"/>, rotation 0, a room of
    /// <see cref="Room"/>, and the given rarity or one rolled from world.db (G: normal 60 %, magic 30 %, rare 10 %). Magic and
    /// rare charts are unidentified while <c>deepwater_chart_unidentified</c> is 1 (G: as maps).</summary>
    public static Item Create(ClientState conn, ItemCatalog.BaseType type, int areaLevel, ItemRarity? rarity, Random random)
    {
        byte level = (byte)Math.Clamp(areaLevel, 1, 100);
        var item = ItemCatalog.Create(type, random: random) with { ItemLevel = level, Chart = new ChartData(level, 0, Room(type.Row, random, areaLevel: level)) };
        var rolled = rarity ?? RollRarity(random);
        if (rolled is not (ItemRarity.Magic or ItemRarity.Rare)) return item;
        return RollsFor(conn).Roll(item, rolled, random) with { Identified = Setting("deepwater_chart_unidentified") == 0 };
    }

    static ItemRarity RollRarity(Random random)
    {
        int magic = Setting("deepwater_chart_magic_percent"), rare = Setting("deepwater_chart_rare_percent");
        int roll = random.Next(100);
        return roll < rare ? ItemRarity.Rare : roll < rare + magic ? ItemRarity.Magic : ItemRarity.Normal;
    }

    /// <summary>GameEvents.RewardItem: Valerie's tutorial chart (offer <see cref="TutorialOffer"/>) is an Area Level 2 magic,
    /// identified Sandy chart with a generic Sandy room (G, the room is unsourced); "Descend" refuses an unidentified chart
    /// (ClientStrings 8466). Another offer's chart gets its component at the offer's item level.</summary>
    public static Item RewardItem(ClientState conn, int offer, Item item, ItemCatalog.BaseType type)
    {
        if (!IsChart(type)) return item;
        if (offer == TutorialOfferRow.Value)
        {
            var random = Random.Shared;
            var chart = ItemCatalog.Create(type, random: random) with
            {
                ItemLevel = TutorialLevel,
                Chart = new ChartData(TutorialLevel, 0, Room(type.Row, random, generic: true)),
            };
            return RollsFor(conn).Roll(chart, ItemRarity.Magic, random) with { Identified = true };
        }
        if (item.Chart is not null) return item;
        byte level = (byte)Math.Clamp((int)item.ItemLevel, 1, 100);
        return item with { ItemLevel = level, Chart = new ChartData(level, 0, Room(type.Row, Random.Shared, areaLevel: level)) };
    }

    internal static readonly Lazy<int> TutorialOfferRow = new(() =>
        ClientTables.TableOrEmpty("QuestRewardOffers").FirstOrDefault(r => r.Text("Id") == TutorialOffer) is { Table: not null } row ? row.Index : -1);

    // ---- the charted implicit ----

    /// <summary>The chart once explored: one implicit from the gt 37 pool (43 Adjacent and 19 Voyage mods as one pool, G),
    /// drawn by the <c>chart</c> tag weights among those of Level ≤ the chart's Area Level, in place of
    /// MapDeepwaterChartImplicit (G: its text says it is a placeholder). A chart that already has one keeps it. Null when the
    /// pool is empty or the base is unknown.</summary>
    public static Item? Charted(ItemCatalog catalog, Item item, Random random)
    {
        if (catalog.ByHash(item.TypeHash) is not { } type || !IsChart(type)) return null;
        var mods = catalog.Mods;
        if (item.Mods.Any(m => m.Section == ModSection.Implicit && mods.ByHash(m.Hash) is { Domain: ChartDomain, GenerationType: ChartImplicit })) return item;
        int level = item.Chart?.AreaLevel is byte l and > 0 ? l : item.ItemLevel;
        var pool = mods.All.Where(m => m.Domain == ChartDomain && m.GenerationType == ChartImplicit && m.Level <= level)
            .Select(m => (Mod: m, Weight: m.WeightFor(type.Tags))).Where(p => p.Weight > 0).ToList();
        if (pool.Count == 0) return null;
        int pick = random.Next(pool.Sum(p => p.Weight));
        var chosen = pool[^1].Mod;
        foreach (var (mod, weight) in pool)
        {
            if (pick < weight) { chosen = mod; break; }
            pick -= weight;
        }
        var implicitMod = chosen.Roll(ModSection.Implicit, random);
        ushort? placeholder = mods.ById(Placeholder)?.Hash;
        var list = item.Mods.ToList();
        int at = list.FindIndex(m => m.Section == ModSection.Implicit && m.Hash == placeholder);
        if (at >= 0) list[at] = implicitMod;
        else list.Insert(0, implicitMod);
        return item with { Mods = list };
    }

    // ---- the room ----

    sealed record RoomRow(byte Row, long Biome, int Mask, bool Special, string Name, string Id);

    // The rooms a chart may get: those of the shipped charts' biomes, including Kishara's Rest (#29 SandyCrashedShip1,
    // eligible at area level >= 81) while excluding its boss arena (#30, __col17 = true), [DNT] rows, Lost Shipment and
    // Runes of the Deep (nobody reports them, rules §3.2).
    static readonly Lazy<(Dictionary<int, long> BiomeOf, RoomRow[] Rooms)> Rooms = new(() =>
    {
        var biomeOf = new Dictionary<int, long>();
        var bases = ClientTables.TableOrEmpty("BaseItemTypes");
        foreach (var c in ClientTables.TableOrEmpty("DeepwaterCharts"))
            if (c.IntOrNull("BaseItemType") is { } item && c.LongOrNull("Biome") is { } biome
                && item < bases.Count && Array.IndexOf(Bases, bases[item].Text("Id")) >= 0)
                biomeOf[item] = biome;
        var shipped = biomeOf.Values.ToHashSet();
        var rooms = ClientTables.TableOrEmpty("DeepwaterRooms")
            .Where(r => r.Index < 0xff && r.LongOrNull("Biome") is { } b && shipped.Contains(b)
                && (r.IsNull("__col14") || r.Index == DeepwaterVelka.BarrierRoom) && r.BoolOrNull("__col17") != true
                && r.Text("Name") is { } name && !name.StartsWith("[DNT]", StringComparison.Ordinal)
                && r.Text("Id") is { } id && !id.Contains("LostShipment", StringComparison.Ordinal) && !id.Contains("StoneCircle", StringComparison.Ordinal))
            .Select(r => new RoomRow((byte)r.Index, r.Long("Biome"), r.Int("__col2"), r.BoolOrNull("Special") == true, r.Text("Name")!, r.Text("Id")!))
            .ToArray();
        return (biomeOf, rooms);
    });

    /// <summary>A room for a new chart of this base (rules §3.2, every weight G: the real ones are server-side and unknown):
    /// the chart's biome 90 %, another shipped biome 10 %; then a generic room 95 % or a special area 5 % (Anchorfield,
    /// Infested Bathyspheres and Hazardous Depths twice as likely as the others, the wiki's "more common"; Kishara's Rest
    /// #29 only at <paramref name="areaLevel"/> ≥ 81); a generic room by shape, ends and straights 0.6 against 1 (3.29.0b
    /// "somewhat less common"). <paramref name="generic"/> keeps the chart's own biome and a generic room (Valerie's chart).
    /// The client crashes drawing a chart whose room names no row, so row 0 is the fallback.</summary>
    public static byte Room(int baseRow, Random random, bool generic = false, int areaLevel = 1)
    {
        var (biomeOf, rooms) = Rooms.Value;
        if (!biomeOf.TryGetValue(baseRow, out long own) || rooms.Length == 0) return 0;
        long biome = own;
        if (!generic && random.Next(100) >= 90)
        {
            var others = biomeOf.Values.Distinct().Where(b => b != own).ToArray();
            if (others.Length > 0) biome = others[random.Next(others.Length)];
        }
        var inBiome = rooms.Where(r => r.Biome == biome).ToArray();
        var specials = inBiome.Where(r => r.Special
            && (r.Row != DeepwaterVelka.BarrierRoom || areaLevel >= DeepwaterVelka.MinChartRoomLevel)).ToArray();
        bool special = !generic && specials.Length > 0 && random.Next(100) < 5;
        if (special)
        {
            var areas = specials.GroupBy(r => r.Name).Select(g => (Rows: g.ToArray(), Weight: Common(g.First().Id) ? 2 : 1)).ToArray();
            int pick = random.Next(areas.Sum(a => a.Weight));
            foreach (var (rows, weight) in areas)
            {
                if (pick < weight) return rows[random.Next(rows.Length)].Row;
                pick -= weight;
            }
        }
        var plain = inBiome.Where(r => !r.Special).ToArray();
        if (plain.Length == 0) return inBiome.Length > 0 ? inBiome[random.Next(inBiome.Length)].Row : (byte)0;
        // Per row: 3 for an end or a straight, 5 for the rest (0.6 : 1).
        int total = plain.Sum(r => ShapeWeight(r.Mask));
        int at = random.Next(total);
        foreach (var r in plain)
        {
            int w = ShapeWeight(r.Mask);
            if (at < w) return r.Row;
            at -= w;
        }
        return plain[^1].Row;
    }

    static bool Common(string id) =>
        id.Contains("AnchorField", StringComparison.Ordinal) || id.Contains("Bathyspheres", StringComparison.Ordinal) || id.Contains("HazardBoat", StringComparison.Ordinal);

    static int ShapeWeight(int mask) => System.Numerics.BitOperations.PopCount((uint)mask) switch
    {
        1 => 3,
        2 when mask is 5 or 10 => 3,
        _ => 5,
    };
}
