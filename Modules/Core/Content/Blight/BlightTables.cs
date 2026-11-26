using GameServer.Core;

namespace GameServer.Modules.Blight;

/// <summary>
/// The client tables a defence reads, built once when the module loads (<see cref="BlightModule.LoadData"/>): the towers
/// (BlightTowers, BlightTowersPerLevel, BlightTowerAuras), lane trees (BlightTopologies and BlightTopologyNodes),
/// encounter types and boss scaling (BlightEncounterTypes, BlightBalancePerLevel, BlightBossLifeScalingPerLevel),
/// reward chests and oils (BlightChestTypes, BlightRewardTypes, BlightCraftingItems) and Sister Cassia's voice lines.
/// </summary>
public static class BlightTables
{
    /// <summary>One BlightTowers row; <see cref="Row"/> is what C→S 0x265 names. Row 0 is the empty foundation.</summary>
    public sealed record Tower(int Row, string Id, string Name, string Tier, int Radius, IReadOnlyList<int> Next, int Cost, int Variety);

    /// <summary>One BlightEncounterTypes row: lane monster theme, generic flag, roll weight and linked boss-only row.</summary>
    public sealed record EncounterType(int Row, string Id, bool IsGeneric, int Weight, int BossTypeRow);

    /// <summary>One BlightTowerAuras row: buff definition and visual MiscAnimated row.</summary>
    public sealed record TowerAura(int Row, int BuffRow, int Kind, int MiscAnimated);

    /// <summary>One Blight chest from BlightChestTypes joined with Chests.</summary>
    public sealed record BlightChest(int ChestsRow, string Id, string Name, string InheritsFrom);

    /// <summary>One BlightRewardTypes row.</summary>
    public sealed record RewardType(int Row, string Id);

    /// <summary>One Oil from BlightCraftingItems joined with BaseItemTypes.</summary>
    public sealed record OilItem(int Row, int BaseItemTypesRow, string Id, string Name, int Tier, int UseType);

    public const ushort CassiaNpcRow = 396;
    public const string CassiaWildVariety = "Metadata/Monsters/Masters/BlightBuilderWild";
    public const string BlightChestBaseType = "Metadata/Chests/BlightChest";

    public static IReadOnlyList<Tower> Towers { get; private set; } = [];

    /// <summary>The resource a defeated enemy gives by rarity (normal, magic, rare, unique): the first BlightBalancePerLevel row's list.</summary>
    public static IReadOnlyList<int> KillResource { get; private set; } = [];

    public static IReadOnlyList<BlightLayout.Topology> Topologies { get; private set; } = [];
    public static IReadOnlyList<BlightLayout.Node> Nodes { get; private set; } = [];
    public static IReadOnlyList<EncounterType> EncounterTypes { get; private set; } = [];
    public static IReadOnlyList<TowerAura> TowerAuras { get; private set; } = [];
    public static IReadOnlyList<BlightChest> Chests { get; private set; } = [];
    public static IReadOnlyList<RewardType> RewardTypes { get; private set; } = [];
    public static IReadOnlyList<OilItem> Oils { get; private set; } = [];
    public static IReadOnlyDictionary<string, ushort[]> CassiaVoices { get; private set; } = new Dictionary<string, ushort[]>();
    public static ushort InfectedMapModHash { get; private set; }

    static (int Level, int MoreLife)[] _bossLifeScaling = [];

    /// <summary>Boss life scaling percent from BlightBossLifeScalingPerLevel for the given area level.</summary>
    public static int BossMoreLifePct(int level)
    {
        if (_bossLifeScaling.Length == 0) return 0;
        int best = _bossLifeScaling[0].MoreLife;
        foreach (var (lvl, more) in _bossLifeScaling)
        {
            if (lvl <= level) best = more;
            else break;
        }
        return best;
    }

    public static void Load()
    {
        var perLevel = new Dictionary<int, (int Cost, int Variety)>();
        foreach (var r in ClientTables.TableOrEmpty("BlightTowersPerLevel"))
            perLevel[r.Int("BlightTowersKey")] = (r.Int("Cost"), r.Int("MonsterVarietiesKey"));
        Towers = [.. ClientTables.TableOrEmpty("BlightTowers").Select(r =>
        {
            var (cost, variety) = perLevel.GetValueOrDefault(r.Index, (0, -1));
            return new Tower(r.Index, r.Text("Id") ?? "", r.Text("Name") ?? "", r.Text("Tier") ?? "", r.Int("Radius"), r.Ints("NextUpgradeOptions"), cost, variety);
        })];
        var balance = ClientTables.TableOrEmpty("BlightBalancePerLevel");
        KillResource = balance.Any() ? balance.First().Ints("__col2") : [];
        var topologies = ClientTables.TableOrEmpty("BlightTopologies");
        Nodes = BlightLayout.ReadNodes(ClientTables.TableOrEmpty("BlightTopologyNodes"), [.. topologies.Select(r => r.Int("BlightTopologyNodesKey"))]);
        Topologies = [.. topologies.Select(r => new BlightLayout.Topology(r.Index, r.Text("Id") ?? "", r.Int("BlightTopologyNodesKey"),
            r.Int("__col2"), r.Int("__col3"), r.Int("__col4"), r.Int("__col6"))).Where(t => t.Root >= 0 && t.Root < Nodes.Count)];

        EncounterTypes = [.. ClientTables.TableOrEmpty("BlightEncounterTypes").Select(r =>
            new EncounterType(r.Index, r.Text("Id") ?? "", r.Int("IsGeneric") != 0, r.Int("Weight"), r.IntOrNull("__col4") ?? -1))];

        _bossLifeScaling = [.. ClientTables.TableOrEmpty("BlightBossLifeScalingPerLevel")
            .Select(r => (r.Int("Level"), r.Int("MoreLife")))
            .OrderBy(x => x.Item1)];

        TowerAuras = [.. ClientTables.TableOrEmpty("BlightTowerAuras").Select(r =>
            new TowerAura(r.Index, r.Int("BuffDefinitionsKey"), r.Int("__col2"), r.Int("MiscAnimatedKey")))];

        var chestRows = new Dictionary<int, (string Id, string Name, string InheritsFrom)>();
        foreach (var r in ClientTables.TableOrEmpty("Chests"))
            chestRows[r.Index] = (r.Text("Id") ?? "", r.Text("Name") ?? "", r.Text("InheritsFrom") ?? "");
        Chests = [.. ClientTables.TableOrEmpty("BlightChestTypes")
            .Select(r => r.Int("ChestsKey"))
            .Where(chestRows.ContainsKey)
            .Select(k => new BlightChest(k, chestRows[k].Id, chestRows[k].Name, chestRows[k].InheritsFrom))];

        RewardTypes = [.. ClientTables.TableOrEmpty("BlightRewardTypes").Select(r =>
            new RewardType(r.Index, r.Text("Id") ?? ""))];

        var baseItems = new Dictionary<int, (string Id, string Name)>();
        foreach (var r in ClientTables.TableOrEmpty("BaseItemTypes"))
            baseItems[r.Index] = (r.Text("Id") ?? "", r.Text("Name") ?? "");
        Oils = [.. ClientTables.TableOrEmpty("BlightCraftingItems")
            .Select(r =>
            {
                int oilRow = r.Int("Oil");
                var (id, name) = baseItems.GetValueOrDefault(oilRow, ("", ""));
                return new OilItem(r.Index, oilRow, id, name, r.Int("Tier"), r.Int("UseType"));
            })
            .Where(o => o.Id.Length > 0)];

        ushort infectedHash = 0;
        foreach (var r in ClientTables.TableOrEmpty("Mods"))
        {
            if (r.Text("Id") == "InfectedMap" && r.IntOrNull("HASH16") is int h and > 0)
            {
                infectedHash = (ushort)h;
                break;
            }
        }
        InfectedMapModHash = infectedHash;

        var voices = new Dictionary<string, List<ushort>>(StringComparer.Ordinal);
        foreach (var r in ClientTables.TableOrEmpty("NPCTextAudio"))
        {
            string id = r.Text("Id") ?? "";
            if (!id.StartsWith("BlightBuilder", StringComparison.Ordinal)) continue;
            if (r.Index < 0 || r.Index > ushort.MaxValue) continue;
            if (!voices.TryGetValue(id, out var list)) voices[id] = list = [];
            list.Add((ushort)r.Index);
        }
        CassiaVoices = voices.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
    }
}
