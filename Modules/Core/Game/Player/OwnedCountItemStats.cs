/// <summary>Qualified item coefficients whose inputs are this area's living owned entities.</summary>
public static class OwnedCountItemStats
{
    public const string Totems = "Item.ActiveTotems", Skeletons = "Item.ActiveSkeletons",
        RagingSpirits = "Item.ActiveRagingSpirits", Zombies = "Item.ActiveZombies", Spectres = "Item.ActiveSpectres";
    public static readonly (string Input, string Count, string[] Outputs)[] Rules =
    [
        ("X_armour_per_active_totem", Totems, ["base_physical_damage_reduction_rating"]),
        ("additional_attack_block_%_per_summoned_skeleton", Skeletons, ["additional_block_%"]),
        ("attack_and_cast_speed_+%_per_summoned_raging_spirit", RagingSpirits, ["attack_speed_+%", "base_cast_speed_+%"]),
        ("life_regeneration_rate_per_minute_%_per_raised_zombie", Zombies, ["life_regeneration_rate_per_minute_%"]),
        ("mana_regeneration_rate_+%_per_raised_spectre", Spectres, ["mana_regeneration_rate_+%"]),
    ];

    public static bool IsCoefficient(string stat) => Rules.Any(r => r.Input == stat);

    // Captured beside the other condition inputs; consumers reuse this view instead of counting again mid-build.
    public static void Capture(ClientState conn, double totemNow, double minionNow, Dictionary<string, long> measured)
    {
        foreach (var key in Rules.Select(r => r.Count)) measured[key] = 0;
        if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return;
        measured[Totems] = conn.Totems.Living(totemNow).Count();
        foreach (var minion in conn.Minions.All)
        {
            if (minion.Monster.Dead || minion.Monster.Life == 0 || minionNow >= minion.Until
                || !conn.Monsters.TryGetValue(minion.Id, out var held) || !ReferenceEquals(held, minion.Monster)) continue;
            string? key = minion.Kind switch
            {
                "Skeleton" => Skeletons, "RagingSpirit" => RagingSpirits, "Zombie" => Zombies, "Spectre" => Spectres,
                _ => null,
            };
            if (key is not null) measured[key]++;
        }
    }

    public static void Apply(Dictionary<uint, int> stats, StatCatalog catalog, IReadOnlyDictionary<string, long>? counts,
        IReadOnlyDictionary<uint, int> overrides)
    {
        var additions = new Dictionary<uint, long>();
        foreach (var (input, count, outputs) in Rules)
        {
            long amount = (catalog.Key(input) is uint key
                ? overrides.GetValueOrDefault(key, stats.GetValueOrDefault(key)) : 0)
                * Math.Max(0, counts?.GetValueOrDefault(count) ?? 0);
            if (amount == 0) continue;
            foreach (string output in outputs)
                if (catalog.Key(output) is uint target && !overrides.ContainsKey(target))
                    additions[target] = additions.GetValueOrDefault(target) + amount;
        }
        foreach (var (key, amount) in additions)
            stats[key] = ActorScale.Saturate(stats.GetValueOrDefault(key) + amount);
    }
}
