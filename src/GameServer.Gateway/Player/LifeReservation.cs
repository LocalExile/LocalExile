/// <summary>
/// The shared owner of reserved Life. Full maximum Life remains <see cref="CharacterStats.Pools.Life"/>; this class
/// derives the reservation from current equipment and active skills, and exposes the usable Life cap. Item reservation
/// (<c>life_reserved_by_stat_%</c>) is permanent while its item qualifies; life reservation efficiency applies only to
/// skill reservations, per skill before summation. Current Life is clamped by callers when sources change and is never
/// refilled when a reservation ends.
/// </summary>
static class LifeReservation
{
    public const string ItemPercentStat = "life_reserved_by_stat_%";
    public const string EnergyShieldPerReservedStat = "maximum_energy_shield_+_per_100_life_reserved";

    public readonly record struct Value(int Maximum, int ItemReserved, int SkillRequired, int Reserved, int Unreserved)
    {
        /// <summary>The unclamped amount demanded by current items and skills.</summary>
        public int Required => (int)Math.Min(int.MaxValue, (long)ItemReserved + SkillRequired);
    }

    public static Value Of(CharacterStats.Pools pools, int itemPercent, IEnumerable<AuraCatalog.Aura> auras,
        int additionalSkillReserved = 0)
    {
        int maximum = (int)Math.Min(int.MaxValue, pools.Life);
        int itemReserved = (int)Math.Clamp(Math.Floor(maximum * (long)Math.Max(0, itemPercent) / 100.0),
            0, maximum);
        int skillRequired = RequiredSkillLife(pools, auras, additionalSkillReserved);
        int required = (int)Math.Min(int.MaxValue, (long)itemReserved + skillRequired);
        int reserved = Math.Min(maximum, required);
        return new(maximum, itemReserved, skillRequired, reserved, Math.Max(0, maximum - reserved));
    }

    public static Value FromInputs(int maximumLife, int itemPercent, int lifeReservationEfficiencyPct,
        IEnumerable<AuraCatalog.Aura> auras, int additionalSkillReserved = 0)
    {
        int maximum = Math.Max(0, maximumLife);
        int itemReserved = (int)Math.Clamp(Math.Floor(maximum * (long)Math.Max(0, itemPercent) / 100.0),
            0, maximum);
        var pools = new CharacterStats.Pools(1, (uint)maximum, 0, 0, 0,
            LifeReservationEfficiencyPct: lifeReservationEfficiencyPct);
        int skillRequired = RequiredSkillLife(pools, auras, additionalSkillReserved);
        int required = (int)Math.Min(int.MaxValue, (long)itemReserved + skillRequired);
        int reserved = Math.Min(maximum, required);
        return new(maximum, itemReserved, skillRequired, reserved, Math.Max(0, maximum - reserved));
    }

    /// <summary>Skill Life reservation, already transferred from Mana by Arrogance/Blood Magic and repriced by the
    /// current supports, reduced by Life reservation efficiency. The rounding matches the existing Mana owner: each
    /// flat amount and each skill's percent is independent before their sums.</summary>
    public static int RequiredSkillLife(CharacterStats.Pools pools, IEnumerable<AuraCatalog.Aura> auras,
        int additionalSkillReserved = 0)
    {
        return (int)Math.Clamp((long)AuraCatalog.RequiredLife(pools, auras) + additionalSkillReserved,
            0, int.MaxValue);
    }

    public static int EnergyShield(int reserved, int perHundred)
    {
        return perHundred == 0 ? 0 : (int)Math.Clamp((long)reserved * perHundred / 100, int.MinValue, int.MaxValue);
    }

    public static uint Clamp(uint current, Value value) =>
        current == 0 ? 0 : Math.Min(current, (uint)Math.Max(0, value.Unreserved));
}
