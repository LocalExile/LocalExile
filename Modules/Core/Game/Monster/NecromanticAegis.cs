using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    sealed record NecromanticShieldBonuses(ItemCatalog Catalog, IReadOnlyDictionary<uint, int> Stats);
    static readonly RuleTable<Item, NecromanticShieldBonuses> NecromanticShieldCache = new();

    static (Item Item, ItemCatalog.BaseType Type)? NecromanticShield(Player player, IReadOnlyDictionary<uint, int> owner)
        => GolemStat(owner, Stat.MinionsGetShieldStatsInsteadOfYou) != 0
            && ActiveOffHand(player) is { Type: { Class: "Shield" } type } shield ? (shield.Item, type) : null;

    static void AddNecromanticShieldStats(Player player, IReadOnlyDictionary<uint, int> owner, Dictionary<uint, int> recipient)
    {
        if (NecromanticShield(player, owner) is not { } equipped) return;
        var (shield, type) = equipped;
        var catalog = ItemsFor(player);
        if (!NecromanticShieldCache.TryGetValue(shield, out var bonuses) || !ReferenceEquals(bonuses.Catalog, catalog))
        {
            var stats = ItemModStats.Global([shield], catalog.Mods, Stats);
            var projected = new Dictionary<uint, int>(stats.Count + 4);
            foreach (var (key, value) in stats)
                Add(projected, key switch
                {
                    Stat.CombinedBaseBlockPct or Stat.AdditionalBlockPct or Stat.AttackBlockPct or Stat.ShieldBlockPct
                        => Stat.MonsterBaseBlockPct,
                    Stat.AdditionalSpellBlockPct => Stat.BaseSpellBlockPct,
                    _ => key,
                }, value);
            Add(projected, Stat.MonsterBaseBlockPct, ItemModStats.ShieldBlock(catalog, shield, type));
            if (catalog.Defences(type) is { } defences)
            {
                var (armour, evasion, shieldPool) = ItemDefences.Calculate(shield, defences, catalog.Mods);
                Add(projected, Stat.BasePhysicalDamageReductionRating, armour);
                Add(projected, Stat.BaseEvasionRating, evasion);
                Add(projected, Stat.BaseMaximumEnergyShield, shieldPool);
            }
            bonuses = new(catalog, projected);
            NecromanticShieldCache.AddOrUpdate(shield, bonuses);
        }
        foreach (var (key, value) in bonuses.Stats) Add(recipient, key, value);

        static void Add(Dictionary<uint, int> to, uint key, int value)
        {
            if (value != 0) to[key] = ActorScale.Add(key, to.GetValueOrDefault(key), value);
        }
    }
}
