using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    static readonly (string Local, string Target)[] ItemUtilityFlaskSources =
    [
        ("local_unique_flask_physical_damage_%_to_add_as_chaos_while_healing", "physical_damage_%_to_add_as_chaos"),
        ("local_unique_flask_elemental_damage_%_to_add_as_chaos_while_healing", "elemental_damage_%_to_add_as_chaos"),
        ("local_unique_flask_life_leech_from_chaos_damage_permyriad_while_healing", "base_life_leech_from_chaos_damage_permyriad"),
    ];

    public static (IReadOnlyList<UtilityFlasks.Source> Stats, double Seconds) ItemUtilityFlaskFor(
        UtilityFlasks.Utility utility, Item flask, IReadOnlyDictionary<string, int> local,
        Func<string, int> character, int playerLevel = 0)
    {
        var ordinary = UtilityFlasks.For(utility, flask, local, character, playerLevel);
        if (!ItemUtilityFlaskSources.Any(row => local.GetValueOrDefault(row.Local) != 0)) return ordinary;
        double effect = FlaskEffectScaling.Multiplier(character, playerLevel, local.GetValueOrDefault("local_flask_effect_+%"),
            flask.Rarity == ItemRarity.Magic ? character("magic_utility_flask_effect_+%") : 0);
        var sources = ordinary.Stats.ToList();
        foreach (var (input, target) in ItemUtilityFlaskSources)
            if (local.GetValueOrDefault(input) is int value and not 0)
                sources.Add(new("mod " + target, target, UtilityFlasks.Scale(value, effect)));
        return (sources, ordinary.Seconds);
    }

    public static void AddItemUtilityFlaskRawStats(Dictionary<uint, int> stats,
        IEnumerable<(string Stat, int Value)> running, StatCatalog catalog)
    {
        var snapshot = running as IReadOnlyCollection<(string Stat, int Value)> ?? running.ToArray();
        UtilityFlasks.AddRawStats(stats, snapshot, catalog);
        foreach (var (name, value) in snapshot)
            if (ItemUtilityFlaskSources.Any(row => row.Target == name) && catalog.Key(name) is uint key)
                stats[key] = ActorScale.Add(key, stats.GetValueOrDefault(key), value);
    }
}
