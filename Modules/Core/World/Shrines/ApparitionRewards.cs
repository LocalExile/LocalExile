using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Modules.Shrines;

/// <summary>The four Conqueror buffs' found-item chances, before the rare equipment's influence affixes roll.</summary>
public static class ApparitionRewards
{
    static readonly (string Shrine, uint Stat, ItemInfluence Influence)[] Chances =
    [
        ("Warlord", Stat.FoundItemsWarlordInfluenceChancePct, ItemInfluence.Warlord),
        ("Crusader", Stat.FoundItemsCrusaderInfluenceChancePct, ItemInfluence.Crusader),
        ("Redeemer", Stat.FoundItemsRedeemerInfluenceChancePct, ItemInfluence.Redeemer),
        ("Hunter", Stat.FoundItemsHunterInfluenceChancePct, ItemInfluence.Hunter),
    ];
    static ushort[] BuffRows = [];
    public static void Load() => BuffRows = [.. Chances.Select(c => ShrineCatalog.Find(c.Shrine)!.BuffRow)];

    public static void Drops(DropRoll roll)
    {
        bool active = false;
        foreach (ushort row in BuffRows)
            if (ShrineManager.HasBuff(roll.Player, row)) { active = true; break; }
        if (!active) return;
        {
            var stats = ShrineManager.AddStats(roll.Player, []);
            var rolls = RollsFor(roll.Player);
            roll.Changes.Add((drop, random) => [Apply(drop, stats.GetValueOrDefault, rolls, random)]);
        }
    }

    public static ItemDrops.Drop Apply(ItemDrops.Drop drop, Func<uint, int> stat, ItemRolls rolls, Random random)
    {
        var item = drop.Item;
        if (!drop.Type.CanBeMirrored || item.Rarity != ItemRarity.Rare || item.Influences != ItemInfluence.None || item.Synthesised
            || item.FracturedCount != 0 || item.Mirrored || item.Corrupted || item.Vestigial) return drop;
        List<ItemInfluence>? candidates = null;
        foreach (var c in Chances)
        {
            int chance = stat(c.Stat);
            if (chance <= 0 || rolls.TagsWith(drop.Type, c.Influence).Count == drop.Type.Tags.Count
                || !RandomRolls.Chance(chance, 100, random)) continue;
            (candidates ??= []).Add(c.Influence);
        }
        if (candidates is not { Count: > 0 }) return drop;
        var influence = candidates.Count == 1 ? candidates[0] : candidates[random.Next(candidates.Count)];
        return drop with { Item = rolls.Roll(item with { Influences = influence }, ItemRarity.Rare, random) };
    }
}
