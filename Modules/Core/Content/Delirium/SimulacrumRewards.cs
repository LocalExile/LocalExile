using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Delirium;

public sealed partial class DeliriumModule
{
    static readonly string[] EncounterUniques = ["Voices", "Megalomaniac", "Split Personality", "Assailum", "Algor Mortis", "Perfidy",
        "The Front Line", "The Interrogation", "Natural Affinity"];

    List<Item> RollRewards(ClientState conn, Run run)
    {
        if (Environment.GetEnvironmentVariable("Game_DROPS") == "0") return [];
        var random = Random.Shared;
        var catalog = ItemsFor(conn);
        var drops = DropsFor(conn);
        var uniques = UniquesFor(conn);
        var rolls = RollsFor(conn);
        byte level = (byte)SimulacrumRules.RewardLevel(run.Wave);
        var all = catalog.All.Where(t => t.Supported && t.DropLevel <= level && !t.Components.Contains("Quest")).ToArray();
        var gemRows = GemSources.DropPool(catalog);
        // The server's reward-tier rate is tunable; its population/clear-speed formula is not published by GGG.
        int tier = Math.Clamp(2 + run.Kills / 40 + (conn.Clock.Elapsed.TotalSeconds - run.Started < 90 ? 1 : 0), 2, 7);
        var result = new List<Item>();
        UniqueCatalog.Definition[]? uniquePool = null;
        foreach (int kind in run.Rewards)
        {
            ItemCatalog.BaseType[] pool = [.. all.Where(t => Eligible(kind, t, gemRows))];
            for (int i = 0; i < tier; i++)
            {
                Item? item;
                if (kind == 0) item = Currency();
                else if (kind == 14) item = drops.Scarab(random, level)?.Item;
                else if (kind == 17) item = drops.Card(random, level)?.Item;
                else if (kind == 3) item = drops.Map(level, MonsterRarity.Rare, Math.Clamp(level - 67, 1, 16), random)?.Item;
                else if (kind == 1)
                {
                    uniquePool ??= uniques.All.Where(u => !EncounterUniques.Contains(u.Name)
                        && catalog.Find(u.BaseTypeId) is { } t && t.DropLevel <= level && ItemDrops.Droppable(t)).ToArray();
                    item = drops.PickUnique(uniquePool, random) is { } unique ? uniques.Create(unique, random) : null;
                }
                else
                {
                    var type = pool.Length == 0 ? null : pool[random.Next(pool.Length)];
                    item = type is null ? null : ItemCatalog.Create(type);
                    if (item is not null && type is not null && (ItemDrops.EquipmentClasses.Contains(type.Class) || type.Class.Contains("Jewel", StringComparison.Ordinal)))
                        item = rolls.Roll(item with { ItemLevel = level }, random.Next(100) < 70 ? ItemRarity.Rare : ItemRarity.Magic, random) with { Identified = false };
                    if (item is not null && kind == 19) item = item with { Corrupted = true };
                }
                if (item is not null) result.Add(item with { ItemLevel = level });
            }
        }
        int bosses = (run.OmniThisWave ? 1 : 0) + (run.KosisThisWave ? 1 : 0);
        var exclusive = EncounterUniques.Select(uniques.Find).OfType<UniqueCatalog.Definition>().ToArray();
        for (int i = 0; i < bosses; i++)
            if (exclusive.Length > 0 && (run.KosisThisWave && run.KosisCount == 1 || random.NextDouble() < Math.Clamp(Setting("simulacrum_unique_chance_pct", 25) / 100, 0, 1))
                && drops.PickUnique(exclusive, random) is { } definition && uniques.Create(definition, random) is { } reward)
                result.Add(reward with { ItemLevel = level });
        // A cluster jewel is part of each wave's Delirium rewards, with its own native enchantments and rolled affixes.
        var clusters = all.Where(t => t.Cluster is not null).ToArray();
        if (clusters.Length > 0)
            result.Add(rolls.Roll(ItemCatalog.Create(clusters[random.Next(clusters.Length)]) with { ItemLevel = level }, ItemRarity.Rare, random) with { Identified = false });
        return result;

        Item? Currency()
        {
            var pool = ItemDrops.Currency.Select(c => (Type: catalog.Find(c.Id), c.Weight))
                .Where(c => c.Type is { Supported: true } && c.Type.DropLevel <= level).ToArray();
            double choice = random.NextDouble() * pool.Sum(c => c.Weight);
            foreach (var (type, weight) in pool)
            {
                choice -= weight;
                if (choice <= 0 && type is not null)
                    return ItemCatalog.Create(type) with { StackSize = (ushort)Math.Min(type.MaxStack, random.Next(1, 4)) };
            }
            return null;
        }
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
