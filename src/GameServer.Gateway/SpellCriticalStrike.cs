/// <summary>The ordinary spell hit's critical chance, shared by the selected skill's panel and damage rolls.
/// Read raw player and raw skill increases once, not the player's final Fireball-based display value.
/// PoB CalcOffence.lua (16de4b8, line 3023) rounds to integer permyriad before capping the chance.</summary>
static class SpellCriticalStrike
{
    public const int RollSize = 10_000;

    public static int Chance(int? basePerMyriad, Func<string, int> player, Func<string, int>? skill = null)
    {
        int Own(string name) => skill?.Invoke(name) ?? 0;
        if (basePerMyriad is not > 0 || player("global_cannot_crit") != 0 || Own("global_cannot_crit") != 0
            || player("virtual_global_cannot_crit") != 0 || Own("virtual_global_cannot_crit") != 0) return 0;
        long increased = (long)player("critical_strike_chance_+%") + player("spell_critical_strike_chance_+%")
            + Own("critical_strike_chance_+%") + Own("spell_critical_strike_chance_+%");
        decimal chance = decimal.Round(basePerMyriad.Value * (100m + increased) / 100m, 0,
            MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(chance, 0m, RollSize);
    }

    public static int Chance(int? basePerMyriad, StatCatalog stats, IReadOnlyDictionary<uint, int> player,
        IReadOnlyDictionary<uint, int> skill) => Chance(basePerMyriad,
            name => stats.Key(name) is uint k ? player.GetValueOrDefault(k) : 0,
            name => stats.Key(name) is uint k ? skill.GetValueOrDefault(k) : 0);

    public static bool Hits(int chance, int roll) => roll is >= 0 and < RollSize && roll < Math.Clamp(chance, 0, RollSize);
}
