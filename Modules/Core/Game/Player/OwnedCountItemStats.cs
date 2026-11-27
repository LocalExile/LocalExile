/// <summary>Qualified item coefficients whose inputs are this area's living owned entities.</summary>
public static class OwnedCountItemStats
{
    public const string Totems = "Item.ActiveTotems", Skeletons = "Item.ActiveSkeletons",
        RagingSpirits = "Item.ActiveRagingSpirits", Zombies = "Item.ActiveZombies", Spectres = "Item.ActiveSpectres";
    public static readonly (uint Input, string Count, uint[] Outputs)[] Rules =
    [
        (Stat.XArmourPerActiveTotem, Totems, [Stat.BasePhysicalDamageReductionRating]),
        (Stat.AdditionalAttackBlockPctPerSummonedSkeleton, Skeletons, [Stat.AdditionalBlockPct]),
        (Stat.AttackAndCastSpeedPlusPctPerSummonedRagingSpirit, RagingSpirits, [Stat.AttackSpeedPlusPct, Stat.BaseCastSpeedPlusPct]),
        (Stat.LifeRegenerationRatePerMinutePctPerRaisedZombie, Zombies, [Stat.LifeRegenerationRatePerMinutePct]),
        (Stat.ManaRegenerationRatePlusPctPerRaisedSpectre, Spectres, [Stat.ManaRegenerationRatePlusPct]),
        (Stat.SpellDamagePlusPctPerSummonedSkeleton, Skeletons, [Stat.SpellDamagePlusPct]),
        (Stat.AttackDamagePlusPctPerRaisedZombie, Zombies, [Stat.AttackDamagePlusPct]),
    ];

    public static bool IsCoefficient(uint stat) => Rules.Any(r => r.Input == stat);

    // Captured beside the other condition inputs; consumers reuse this view instead of counting again mid-build.
    public static void Capture(Player player, double totemNow, double minionNow, Dictionary<string, long> measured)
    {
        foreach (var key in Rules.Select(r => r.Count)) measured[key] = 0;
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return;
        measured[Totems] = player.Totems.Living(totemNow).Count();
        foreach (var minion in player.Minions.All)
        {
            if (minion.Monster.Dead || minion.Monster.Life == 0 || minionNow >= minion.Until
                || !player.Area.Monsters.TryGetValue(minion.Id, out var held) || !ReferenceEquals(held, minion.Monster)) continue;
            string? key = minion.Kind switch
            {
                "Skeleton" => Skeletons, "RagingSpirit" => RagingSpirits, "Zombie" => Zombies, "Spectre" => Spectres,
                _ => null,
            };
            if (key is not null) measured[key]++;
        }
    }

    public static void Apply(Dictionary<uint, int> stats, StatCatalog catalog, IReadOnlyDictionary<string, long>? counts)
    {
        var additions = new Dictionary<uint, long>();
        foreach (var (input, count, outputs) in Rules)
        {
            // The count is read only for a coefficient the character has: an unread count cannot change the result
            // (Game.ReadAmounts).
            long coefficient = input is var key ? stats.GetValueOrDefault(key) : 0;
            if (coefficient == 0) continue;
            long amount = coefficient * Math.Max(0, counts?.GetValueOrDefault(count) ?? 0);
            if (amount == 0) continue;
            foreach (uint target in outputs)
                    additions[target] = additions.GetValueOrDefault(target) + amount;
        }
        foreach (var (key, amount) in additions)
            stats[key] = ActorScale.Saturate(stats.GetValueOrDefault(key) + amount);
    }
}
