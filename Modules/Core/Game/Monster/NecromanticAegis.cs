using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    static (Item Item, ItemCatalog.BaseType Type)? NecromanticShield(Player player, IReadOnlyDictionary<uint, int> owner)
        => GolemStat(owner, Stat.MinionsGetShieldStatsInsteadOfYou) != 0
            && ActiveOffHand(player) is { Type: { Class: "Shield" } type } shield ? (shield.Item, type) : null;

    static void AddNecromanticShieldStats(Player player, IReadOnlyDictionary<uint, int> owner, Dictionary<uint, int> recipient)
    {
        if (NecromanticShield(player, owner) is not { } equipped) return;
        var (shield, type) = equipped;
        var catalog = ItemsFor(player);
        var stats = ItemModStats.Global([shield], catalog.Mods, Stats);
        foreach (var (key, value) in stats)
            Add(key == Stat.CombinedBaseBlockPct ? Stat.MonsterBaseBlockPct : key, value);
        Add(Stat.MonsterBaseBlockPct, ItemModStats.ShieldBlock(catalog, shield, type));
        if (catalog.Defences(type) is { } defences)
        {
            var (armour, evasion, shieldPool) = ItemDefences.Calculate(shield, defences, catalog.Mods);
            Add(Stat.BasePhysicalDamageReductionRating, (int)Math.Truncate(armour));
            Add(Stat.BaseEvasionRating, (int)Math.Truncate(evasion));
            Add(Stat.BaseMaximumEnergyShield, shieldPool);
        }
        return;

        void Add(uint key, int value)
        {
            if (value != 0) recipient[key] = ActorScale.Add(key, recipient.GetValueOrDefault(key), value);
        }
    }
}
