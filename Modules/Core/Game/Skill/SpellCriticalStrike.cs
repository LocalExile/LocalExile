

/// <summary>The ordinary spell hit's critical chance, shared by the selected skill's panel and damage rolls.
/// Read raw player and raw skill increases once, not the player's final Fireball-based display value.
/// Attacks and spells share half-up integer permyriad rounding in DamageModel.</summary>
public static class SpellCriticalStrike
{
    public const int RollSize = 10_000;

    public static int Chance(int? basePerMyriad, Func<uint, int> player, Func<uint, int>? skill = null,
        long? playerIncrease = null)
    {
        int Own(uint name) => skill?.Invoke(name) ?? 0;
        if (basePerMyriad is null or < 0 || player(Stat.GlobalCannotCrit) != 0 || Own(Stat.GlobalCannotCrit) != 0
            || player(Stat.VirtualGlobalCannotCrit) != 0 || Own(Stat.VirtualGlobalCannotCrit) != 0) return 0;
        long increased = (playerIncrease ?? ((long)player(Stat.CriticalStrikeChancePlusPct)
            + player(Stat.SpellCriticalStrikeChancePlusPct)))
            + Own(Stat.CriticalStrikeChancePlusPct) + Own(Stat.SpellCriticalStrikeChancePlusPct);
        return AddedBaseCriticalStrike.Chance(basePerMyriad.Value, increased, spell: true, player, skill);
    }

    public static int Chance(int? basePerMyriad, StatCatalog stats, IReadOnlyDictionary<uint, int> player,
        IReadOnlyDictionary<uint, int> skill, long? playerIncrease = null) => Chance(basePerMyriad,
            name => name is var k ? player.GetValueOrDefault(k) : 0,
            name => name is var k ? skill.GetValueOrDefault(k) : 0, playerIncrease);

    public static bool Hits(int chance, int roll) => roll is >= 0 and < RollSize && roll < Math.Clamp(chance, 0, RollSize);
}
