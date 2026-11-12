using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    static readonly (string Local, string Target)[] ItemUtilityFlaskSources =
    [
        ("local_unique_flask_physical_damage_%_to_add_as_chaos_while_healing", "physical_damage_%_to_add_as_chaos"),
        ("local_unique_flask_elemental_damage_%_to_add_as_chaos_while_healing", "elemental_damage_%_to_add_as_chaos"),
        ("local_unique_flask_life_leech_from_chaos_damage_permyriad_while_healing", "base_life_leech_from_chaos_damage_permyriad"),
        ("local_unique_flask_block_%_while_healing", "additional_block_%"),
        ("local_unique_flask_spell_block_%_while_healing", "base_spell_block_%"),
        ("local_unique_flask_physical_damage_%_to_add_as_cold_while_healing", "physical_damage_%_to_add_as_cold"),
        ("local_unique_flask_avoid_chill_%_while_healing", "base_avoid_chill_%"),
        ("local_unique_flask_avoid_freeze_%_while_healing", "base_avoid_freeze_%"),
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
        var ordinary = UtilityFlasks.RawStats(snapshot, catalog).Select(row => row.Key).ToHashSet();
        UtilityFlasks.AddRawStats(stats, snapshot, catalog);
        foreach (var (name, value) in snapshot)
            // Block chances are already published from the pools. Avoidance is already in the generic raw view.
            if (name is not ("additional_block_%" or "base_spell_block_%")
                && ItemUtilityFlaskSources.Any(row => row.Target == name) && catalog.Key(name) is uint key
                && !ordinary.Contains(key))
                stats[key] = ActorScale.Add(key, stats.GetValueOrDefault(key), value);
    }

    public static CharacterStats.Bonus ApplyItemUtilityFlaskBonuses(CharacterStats.Bonus bonus,
        IEnumerable<(string Stat, int Value)> running)
    {
        var snapshot = running as IReadOnlyCollection<(string Stat, int Value)> ?? running.ToArray();
        var result = UtilityFlasks.Apply(bonus, snapshot);
        return result with
        {
            BlockChance = ActorScale.Saturate((long)result.BlockChance
                + snapshot.Where(row => row.Stat == "additional_block_%").Sum(row => (long)row.Value)),
            SpellBlockChance = ActorScale.Saturate((long)result.SpellBlockChance
                + snapshot.Where(row => row.Stat == "base_spell_block_%").Sum(row => (long)row.Value)),
        };
    }
}
