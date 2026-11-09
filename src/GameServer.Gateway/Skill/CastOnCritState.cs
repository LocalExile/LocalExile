/// <summary>Cast On Critical Strike Support's trigger state on one connection, under the Monsters lock.
/// A crit with a supported attack triggers one supported spell of its link group: the triggers cycle through the
/// group's spells in socket order, and a spell on its cooldown is skipped. Each spell has its own cooldown; copies
/// of the same spell share it (poewiki "Cast On Critical Strike Support", Supporting multiple spells).</summary>
sealed class CastOnCritState
{
    public const string AttackStat = "cast_linked_spells_on_attack_crit_%";
    public const string SpellStat = "cast_spell_on_linked_attack_crit";

    readonly Dictionary<int, double> readyAt = [];
    readonly Dictionary<(uint Host, int Group), int> next = [];

    /// <summary>The index in <paramref name="spells"/> (GrantedEffects rows, socket order) of the spell this crit
    /// triggers, or null when all are on cooldown. The chosen spell's cooldown starts at <paramref name="now"/>.</summary>
    public int? Pick((uint Host, int Group) link, IReadOnlyList<int> spells, double now, Func<int, double> cooldownSeconds)
    {
        if (spells.Count == 0) return null;
        int start = next.GetValueOrDefault(link) % spells.Count;
        for (int i = 0; i < spells.Count; i++)
        {
            int index = (start + i) % spells.Count;
            if (now < readyAt.GetValueOrDefault(spells[index], double.NegativeInfinity)) continue;
            readyAt[spells[index]] = now + cooldownSeconds(spells[index]);
            next[link] = index + 1;
            return index;
        }
        return null;
    }

    /// <summary>The cooldown in seconds: the base time over the recovery rate (100 + recovery %); a rate of zero or
    /// less never recovers.</summary>
    public static double CooldownSeconds(int baseMilliseconds, long recoveryPct) =>
        recoveryPct <= -100 ? double.PositiveInfinity : Math.Max(0, baseMilliseconds) / 1000.0 / (1 + recoveryPct / 100.0);
}
