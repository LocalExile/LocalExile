using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Village.Mapping;

/// <summary>
/// What a finished mapping run pays: the gold, the item drops and the bonus stats.
/// </summary>
public static class MappingRewards
{
    /// <summary>The client table that lists the bonus stats a finished map can grant.</summary>
    private const string BonusStatsFile = "data/tables/VillageMappingBonusStats.json";

    /// <summary>The item level cap of every item that has no cap of its own.</summary>
    private const int DefaultItemLevelCap = 84;

    /// <summary>The item level cap of the cluster jewels.</summary>
    private const int ClusterJewelItemLevelCap = 83;

    /// <summary>How far the gold of a map swings in both directions.</summary>
    private const double GoldSpread = 0.20;

    /// <summary>
    /// The item bases the fallback table can pay when the shared drop engine cannot be reached. The map's own loot:
    /// currency, divination cards and scarabs (the gold is paid as gold, not as an item).
    /// </summary>
    private static readonly string[] FallbackBases =
    [
        "Metadata/Items/Currency/CurrencyRerollRare",
        "Metadata/Items/Currency/CurrencyUpgradeToRare",
        "Metadata/Items/Currency/CurrencyIdentification",
        "Metadata/Items/DivinationCards/DivinationCardTheDoctor",
        "Metadata/Items/DivinationCards/DivinationCardTheHermit",
        "Metadata/Items/Scarabs/ScarabBreach1",
        "Metadata/Items/Scarabs/ScarabBreach2",
        "Metadata/Items/Currency/CurrencyStackedScarab",
    ];

    private static readonly object Gate = new();
    private static (int Stat, int Chance)[]? _bonusStats;

    /// <summary>
    /// Rolls what one finished map pays: the gold that goes into the town treasury, the item drops and the bonus stats.
    /// </summary>
    /// <param name="deviceId">The device the map ran on.</param>
    /// <param name="entry">The map that was run.</param>
    /// <param name="completion">How much of the map was cleared, in percent.</param>
    /// <param name="rng">The random source of the roll.</param>
    /// <returns>The reward of the run.</returns>
    public static MappingReward Roll(int deviceId, MappingQueueEntry entry, int completion, Random rng)
    {
        int done = Math.Clamp(completion, 0, 100);
        var reward = new MappingReward
        {
            DeviceId = deviceId,
            MapTypeHash = entry.MapTypeHash,
            Name = entry.Name,
            Tier = entry.Tier,
            Completion = done,
            Gold = Gold(entry.Tier, done, rng),
        };

        foreach (int stat in RollBonusStats(rng)) reward.BonusStats.Add(stat);
        foreach (MappingDrop drop in Drops(entry, done, rng)) reward.Drops.Add(drop);
        return reward;
    }

    /// <summary>
    /// The item level cap of an item base: the cluster jewels cap at 83, every other base has no cap of its own.
    /// </summary>
    /// <param name="typeHash">The base type hash of the item.</param>
    /// <returns>The cap of the base, or 0 when the base has no cap of its own.</returns>
    public static int ItemLevelCap(uint typeHash)
    {
        var type = Program.Items.ByHash(typeHash);
        if (type is null) return 0;
        return type.Cluster is not null ? ClusterJewelItemLevelCap : 0;
    }

    /// <summary>The gold of a finished map: the base gold of its tier, times how much was cleared, with a spread.</summary>
    private static long Gold(int tier, int completion, Random rng)
    {
        double baseGold = (100 + 150 * tier) * (completion / 100.0);
        double spread = 1.0 - GoldSpread + 2 * GoldSpread * rng.NextDouble();
        return (long)Math.Round(baseGold * spread);
    }

    /// <summary>The items a finished map drops.</summary>
    private static List<MappingDrop> Drops(MappingQueueEntry entry, int completion, Random rng)
    {
        var drops = new List<MappingDrop>();
        int level = MappingBalance.MapLevel(entry.Tier);
        double quantity = completion / 100.0;
        try
        {
            // The game's own drop engine: it rolls from the client tables and needs no player state, so a module can
            // roll with it. Only the map's level, its rarity and how much was cleared go in - no player bonus of any
            // kind (an atlas passive, a completion bonus, a voidstone or a character stat) is part of the roll.
            foreach (var drop in global::Game.Drops.Roll(level, MonsterRarity.Magic, rng, quantityMultiplier: quantity, mapTier: entry.Tier))
            {
                if (drop.Type.Id == ItemDrops.GoldId) continue;
                Add(drops, drop.Item.TypeHash, drop.Item.StackSize, drop.Item.ItemLevel, (byte)drop.Item.Rarity);
            }
        }
        catch (Exception)
        {
            // The shared engine reads the atlas data of the client; without it the small table of the map's own loot
            // stands in, so a finished map still pays something.
            drops.Clear();
            FallbackDrops(level, completion, rng, drops);
        }

        if (string.Equals(entry.Special, "Boss", StringComparison.OrdinalIgnoreCase)) AddBossFragment(entry, rng, drops);
        return drops;
    }

    /// <summary>The small table of loot that stands in when the shared drop engine cannot be reached.</summary>
    private static void FallbackDrops(int level, int completion, Random rng, List<MappingDrop> drops)
    {
        int count = Math.Max(1, (int)Math.Round(completion / 100.0 * 2));
        for (int i = 0; i < count; i++)
        {
            string id = FallbackBases[rng.Next(FallbackBases.Length)];
            var type = Program.Items.Find(id);
            if (type is null) continue;
            Add(drops, type.Hash, 1, level, (byte)ItemRarity.Normal);
        }
    }

    /// <summary>
    /// The boss fragment a boss map of a tier pays. The client links a boss to its fragment in no table the server can
    /// read, so the family comes from the client's own uber boss fragment bases, one family per two tiers; the tier 16
    /// atlas boss nodes (the Phoenix, the Minotaur, the Chimera and the Hydra) pay the Shaper's four fragments.
    /// </summary>
    private static string[]? BossFragmentFamily(int tier) => tier switch
    {
        1 or 2 => ["Metadata/Items/MapFragments/CurrencyUberBossKeyShaper"],
        3 or 4 => ["Metadata/Items/MapFragments/CurrencyUberBossKeyUberElder"],
        5 or 6 => ["Metadata/Items/MapFragments/CurrencyUberBossKeySirus"],
        7 or 8 => ["Metadata/Items/MapFragments/CurrencyUberBossKeyCortex"],
        9 or 10 => ["Metadata/Items/MapFragments/CurrencyUberBossKeyMaven"],
        11 or 12 => ["Metadata/Items/MapFragments/CurrencyUberBossKeyEater"],
        13 or 14 => ["Metadata/Items/MapFragments/CurrencyUberBossKeyExarch"],
        15 => ["Metadata/Items/MapFragments/CurrencyUberBossKeyAnger"],
        16 =>
        [
            "Metadata/Items/MapFragments/ShaperFragment1",
            "Metadata/Items/MapFragments/ShaperFragment2",
            "Metadata/Items/MapFragments/ShaperFragment3",
            "Metadata/Items/MapFragments/ShaperFragment4",
        ],
        17 => ["Metadata/Items/MapFragments/CrucibleUber"],
        _ => null,
    };

    /// <summary>Adds the boss fragment of a boss map to the drops of the run.</summary>
    private static void AddBossFragment(MappingQueueEntry entry, Random rng, List<MappingDrop> drops)
    {
        string[]? family = BossFragmentFamily(entry.Tier);
        if (family is null || family.Length == 0) return;
        var type = Program.Items.Find(family[rng.Next(family.Length)]);
        if (type is null) return;
        Add(drops, type.Hash, 1, MappingBalance.MapLevel(entry.Tier), (byte)ItemRarity.Normal);
    }

    /// <summary>Adds a drop to the list, stacking the counts of the same item.</summary>
    private static void Add(List<MappingDrop> drops, uint typeHash, int count, int itemLevel, byte rarity)
    {
        if (count <= 0) return;
        int level = CapLevel(typeHash, itemLevel);
        for (int i = 0; i < drops.Count; i++)
        {
            if (drops[i].TypeHash == typeHash && drops[i].ItemLevel == level && drops[i].Rarity == rarity)
            {
                drops[i] = drops[i] with { Count = drops[i].Count + count };
                return;
            }
        }
        drops.Add(new MappingDrop(typeHash, count, level, rarity));
    }

    /// <summary>The item level of a rolled item under its cap: the base's own cap wins over the default cap.</summary>
    private static int CapLevel(uint typeHash, int itemLevel)
    {
        int own = ItemLevelCap(typeHash);
        int cap = own > 0 ? own : DefaultItemLevelCap;
        return Math.Min(itemLevel, cap);
    }

    /// <summary>The bonus stats of a finished map, each rolled on its own chance.</summary>
    private static List<int> RollBonusStats(Random rng)
    {
        var stats = new List<int>();
        foreach ((int stat, int chance) in BonusStats())
        {
            if (chance <= 0) continue;
            if (rng.Next(100) < chance) stats.Add(stat);
        }
        return stats;
    }

    /// <summary>The bonus stats of the client table: a stat id and the percent chance it is granted.</summary>
    private static (int Stat, int Chance)[] BonusStats()
    {
        lock (Gate)
        {
            if (_bonusStats is not null) return _bonusStats;
            var rows = new List<(int, int)>();
            try
            {
                if (File.Exists(BonusStatsFile))
                {
                    using var stream = File.OpenRead(BonusStatsFile);
                    using var document = JsonDocument.Parse(stream);
                    foreach (var row in document.RootElement.EnumerateArray())
                    {
                        int stat = 0;
                        int chance = 0;
                        foreach (var field in row.EnumerateObject())
                        {
                            if (field.NameEquals("Stats") && field.Value.TryGetInt32(out int value)) stat = value;
                            else if (field.NameEquals("PercentChance") && field.Value.TryGetInt32(out int percent)) chance = percent;
                        }
                        if (stat != 0) rows.Add((stat, chance));
                    }
                }
            }
            catch (Exception)
            {
                // The client table is optional: a map without bonus stats still pays its gold and its drops.
            }
            _bonusStats = [.. rows];
            return _bonusStats;
        }
    }
}
