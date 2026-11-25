using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Delirium;

public sealed partial class DeliriumModule
{
    static readonly string[] EncounterUniques = ["Voices", "Megalomaniac", "Split Personality", "Assailum", "Algor Mortis", "Perfidy",
        "The Front Line", "The Interrogation", "Natural Affinity"];

    /// <summary>The pools a reward roll draws from, built once for a batch of payouts (the item catalog is large).</summary>
    sealed class RewardRolls(Player player, byte level)
    {
        public readonly Random Random = AreaRng(player);
        public readonly ItemCatalog Catalog = ItemsFor(player);
        public readonly ItemDrops Drops = DropsFor(player);
        public readonly UniqueCatalog Uniques = UniquesFor(player);
        public readonly ItemRolls Rolls = RollsFor(player);
        public readonly byte Level = level;
        public readonly ItemCatalog.BaseType[] All =
            [.. ItemsFor(player).All.Where(t => t.Supported && t.DropLevel <= level
                && !t.Components.Contains("Quest"))];
        public readonly IReadOnlySet<int> Gems = GemSources.DropPool(ItemsFor(player));
        UniqueCatalog.Definition[]? _uniquePool;

        UniqueCatalog.Definition[] GetUniquePool()
        {
            if (_uniquePool is null)
                _uniquePool = [.. Uniques.All.Where(u => !EncounterUniques.Contains(u.Name) && Catalog.Find(u.BaseTypeId) is { } t
                    && t.DropLevel <= Level && ItemDrops.Droppable(t))];
            return _uniquePool;
        }

        /// <summary>One item of one AfflictionRewardTypes kind, or null when its pool has nothing at this level.</summary>
        public Item? Kind(int kind)
        {
            Item? item;
            if (kind == 0) item = Currency();
            else if (kind == 14) item = Drops.Scarab(Random, Level)?.Item;
            else if (kind == 17) item = Drops.Card(Random, Level)?.Item;
            else if (kind == 3) item = Drops.Map(Level, MonsterRarity.Rare, Math.Clamp(Level - 67, 1, 16), Random)?.Item;
            else if (kind == 1) item = Drops.PickUnique(GetUniquePool(), Random) is { } unique ? Uniques.Create(unique, Random) : null;
            else
            {
                var pool = All.Where(t => Eligible(kind, t, Gems)).ToArray();
                var type = pool.Length == 0 ? null : pool[Random.Next(pool.Length)];
                item = type is null ? null : ItemCatalog.Create(type);
                if (item is not null && type is not null && (ItemDrops.EquipmentClasses.Contains(type.Class) || type.Class.Contains("Jewel", StringComparison.Ordinal)))
                    item = Rolls.Roll(item with { ItemLevel = Level }, Random.Next(100) < 70 ? ItemRarity.Rare : ItemRarity.Magic, Random) with { Identified = false };
                if (item is not null && kind == 19) item = item with { Corrupted = true };
            }
            return item is null ? null : item with { ItemLevel = Level };
        }

        Item? Currency()
        {
            var pool = ItemDrops.Currency.Select(c => (Type: Catalog.Find(c.Id), c.Weight))
                .Where(c => c.Type is { Supported: true } && c.Type.DropLevel <= Level).ToArray();
            double choice = Random.NextDouble() * pool.Sum(c => c.Weight);
            foreach (var (type, weight) in pool)
            {
                choice -= weight;
                if (choice <= 0 && type is not null)
                    return ItemCatalog.Create(type) with { StackSize = (ushort)Math.Min(type.MaxStack, Random.Next(1, 4)) };
            }
            return null;
        }
    }

    /// <summary>
    /// Everything the wave pays, rolled once when it is over: one item of a type for each cycle its bar completed
    /// (<see cref="Run.RewardCounts"/>), then the bosses' exclusive uniques and one cluster jewel. Nothing drops while
    /// the wave runs — the bars only count.
    /// </summary>
    List<Item> RollRewards(Player player, Run run)
    {
        if (Environment.GetEnvironmentVariable("Game_DROPS") == "0") return [];
        var rolls = new RewardRolls(player, (byte)SimulacrumRules.RewardLevel(run.Wave));
        var random = rolls.Random;
        var result = new List<Item>();
        for (int i = 0; i < run.Rewards.Length; i++)
        {
            int count = i < run.RewardCounts.Length ? run.RewardCounts[i] : 0;
            for (int n = 0; n < count; n++)
                if (rolls.Kind(run.Rewards[i]) is { } earned) result.Add(earned);
        }
        int bosses = (run.OmniThisWave ? 1 : 0) + (run.KosisThisWave ? 1 : 0);
        var exclusive = EncounterUniques.Select(rolls.Uniques.Find).OfType<UniqueCatalog.Definition>().ToArray();
        for (int i = 0; i < bosses; i++)
            if (exclusive.Length > 0 && (run.KosisThisWave && run.KosisCount == 1 || random.NextDouble() < Math.Clamp(Setting("simulacrum_unique_chance_pct", 25) / 100, 0, 1))
                && rolls.Drops.PickUnique(exclusive, random) is { } definition && rolls.Uniques.Create(definition, random) is { } reward)
                result.Add(reward with { ItemLevel = rolls.Level });
        // A cluster jewel is part of each wave's Delirium rewards, with its own native enchantments and rolled affixes.
        var clusters = rolls.All.Where(t => t.Cluster is not null).ToArray();
        if (clusters.Length > 0)
            result.Add(rolls.Rolls.Roll(ItemCatalog.Create(clusters[random.Next(clusters.Length)]) with { ItemLevel = rolls.Level }, ItemRarity.Rare, random) with { Identified = false });
        return result;
    }

    internal static List<Item> RollMirrorRewards(Player player, byte level, IReadOnlyList<int> rewards, IReadOnlyList<int> counts, int splinters)
    {
        if (Environment.GetEnvironmentVariable("Game_DROPS") == "0" || !GameEvents.AllowsLoot(player)) return [];
        var rolls = new RewardRolls(player, level);
        var result = new List<Item>();
        for (int i = 0; i < rewards.Count; i++)
        {
            int count = i < counts.Count ? counts[i] : 0;
            for (int n = 0; n < count; n++)
                if (rolls.Kind(rewards[i]) is { } earned) result.Add(earned);
        }
        if (splinters > 0 && rolls.Catalog.Find("Metadata/Items/Currency/CurrencyAfflictionShard") is { Supported: true } shard)
        {
            int remaining = splinters;
            while (remaining > 0)
            {
                ushort stack = (ushort)Math.Clamp(remaining, 1, Math.Max(1, (int)shard.MaxStack));
                result.Add(ItemCatalog.Create(shard) with { StackSize = stack, ItemLevel = level });
                remaining -= stack;
            }
        }
        return result;
    }

    static bool Eligible(int kind, ItemCatalog.BaseType t, IReadOnlySet<int> gems) => kind switch
    {
        2 => gems.Contains(t.Row),
        4 => ItemDrops.Droppable(t) && t.Class is "Ring" or "Amulet" or "Belt",
        5 => ItemDrops.Droppable(t) && t.Class is "Body Armour" or "Helmet" or "Gloves" or "Boots" or "Shield",
        6 => ItemDrops.Droppable(t) && ItemDrops.EquipmentClasses.Contains(t.Class) && t.Components.Contains("Weapon"),
        7 => t.Class == "AbyssJewel" || t.Id.Contains("/JewelAbyss", StringComparison.Ordinal),
        8 => t.Class is "ExpeditionCurrency" or "ExpeditionLogbook" || t.Id.Contains("/CurrencyRefresh", StringComparison.Ordinal),
        10 => t.Id.Contains("/CurrencyBreach", StringComparison.Ordinal) || t.Id.Contains("/Breach", StringComparison.Ordinal) && t.Class == "MapFragment",
        11 => t.Class == "StackableCurrency" && t.Id.Contains("Essence", StringComparison.Ordinal) && !t.Id.Contains("Corrupt", StringComparison.Ordinal),
        13 => t.Class == "MapFragment" && !t.Id.Contains("CurrencyAffliction", StringComparison.Ordinal) && !t.Id.Contains("/Scarabs/", StringComparison.Ordinal),
        15 => t.Id.Contains("/CurrencyDelveCrafting", StringComparison.Ordinal),
        19 => t.Class == "Amulet" && t.Id.Contains("Talisman", StringComparison.Ordinal),
        20 => t.Id.Contains("/CurrencyOfferingToTheGoddess", StringComparison.Ordinal),
        22 => t.Class is "Incubator" or "IncubatorStackable" || t.Id.Contains("CurrencyLegion", StringComparison.Ordinal),
        23 => t.Class == "StackableCurrency" && t.Id.Contains("Oil", StringComparison.Ordinal),
        24 => t.Class == "StackableCurrency" && t.Id.Contains("/CurrencyJewelleryQuality", StringComparison.Ordinal),
        25 => t.Id.Contains("/CurrencyAfflictionOrb", StringComparison.Ordinal) || t.Cluster is not null,
        _ => false,
    };
}
