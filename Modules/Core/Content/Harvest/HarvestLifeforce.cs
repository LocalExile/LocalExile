using GameServer.Modding;

namespace GameServer.Modules.Harvest;

/// <summary>
/// What a grove monster gives when it dies: Crystallised Lifeforce of its seed's colour (HarvestSeedItems: 1 Wild,
/// 2 Vivid, 3 Primal) and its experience, both scaled by the grove's stats (<see cref="HarvestStats"/>, from the map's
/// composed area stats, so an Atlas passive, an Idol and a scarab that grant the same stat act the same). Both rules
/// run per kill and per experience gain: outside a grove instance they cost one table lookup.
/// </summary>
public static class HarvestLifeforce
{
    const string FaridunItem = "Metadata/Items/Currency/HarvestSeedFaridun";

    // The drop chance (percent) and the base amount range by seed tier 1–4 (ForgottenArbiter's measurements, 3.19–3.22;
    // the server tables hold no lifeforce amounts).
    static readonly int[] Chance = [0, 2, 10, 100, 100];
    static readonly (double Min, double Max)[] Base = [(0, 0), (4.5, 10), (12, 25), (37, 57), (190, 280)];

    // The lifeforce BaseItemTypes Ids by colour (HarvestSeedItems.BaseItem), resolved on the first grove kill.
    static string?[]? _items;

    static string?[] Items(ClientState conn)
    {
        if (_items is { } items) return items;
        items = new string?[4];
        foreach (var (colour, (baseItem, _)) in HarvestTables.Current.SeedItems)
            if ((uint)colour < 4) items[colour] = ItemsFor(conn).Row(baseItem)?.Id;
        return _items = items;
    }

    /// <summary>The lifeforce one kill of a seed of <paramref name="tier"/> drops, as stacks (0, 1 or 2 counts): the tier's
    /// chance, then a uniform base amount × 2 with the Doubling scarab × (1 + map item quantity / 200 + increased lifeforce
    /// / 100), rounded; Doubling Season's chance drops a second stack of the same size.</summary>
    public static int Roll(Random random, HarvestStats stats, int tier, out int second)
    {
        second = 0;
        tier = Math.Clamp(tier, 1, 4);
        if (random.Next(100) >= Chance[tier]) return 0;
        var (min, max) = Base[tier];
        double amount = min + random.NextDouble() * (max - min);
        if (stats.DoubleLifeforce) amount *= 2;
        amount *= Math.Max(0, 1 + stats.Quantity / 200.0 + stats.LifeforceIncrease / 100.0);
        int count = (int)Math.Round(amount);
        if (count <= 0) return 0;
        if (stats.DuplicateLifeforceChance > 0 && random.Next(100) < stats.DuplicateLifeforceChance) second = count;
        return count;
    }

    /// <summary>GameEvents.Drops: a grove monster's lifeforce.</summary>
    public static void Drops(DropRoll drop)
    {
        if (!SacredGrove.TryMonster(drop.Conn, drop.Monster.ObjectId, out var tag, out var stats)) return;
        // Refiner's Bargain grants a random crop instead; the server has no crop grant yet, so nothing drops.
        if (stats.RandomCropsInstead) return;
        // [G] A Faridun Corpse-Grown seed's monster drops one Crystallised Rancour in place of lifeforce.
        if (tag.Faridun)
        {
            drop.Stacks.Add((FaridunItem, 1));
            return;
        }
        if ((uint)tag.Colour >= 4 || Items(drop.Conn)[tag.Colour] is not { } item) return;
        int count = Roll(Random.Shared, stats, tag.Tier, out int second);
        if (count > 0) drop.Stacks.Add((item, count));
        if (second > 0) drop.Stacks.Add((item, second));
    }

    /// <summary>GameEvents.Experience: map_harvest_monster_granted_experience_+% on a grove monster's kill.</summary>
    public static void Experience(ExperienceGain gain)
    {
        if (!SacredGrove.TryMonster(gain.Conn, gain.Monster.ObjectId, out _, out var stats) || stats.ExperienceIncrease == 0) return;
        gain.Experience = (uint)Math.Clamp(gain.Experience * (1 + stats.ExperienceIncrease / 100.0), 0, uint.MaxValue);
    }
}
