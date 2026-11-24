public static partial class Game
{
    public static bool BallistaEffect(Player player, int effect) => SkillsFor(player).IsAttack(effect)
        && !SkillsFor(player).IsTravel(effect) && Supports.Of(effect)?.Types.Contains(47) != true
        && (BallistaRain(player, effect) || UsesProjectileRuntime(effect) && ResolveProjectileDefinition(effect) is not null)
        // The artillery mortar needs a landing sequence, not a straight-arrow approximation.
        && SkillsFor(player).EffectId(effect) is not ("ArtilleryBallista" or "ArtilleryBallistaAltX" or "ArtilleryBallistaAltY");

    static bool BallistaRain(Player player, int effect) => SkillsFor(player).EffectId(effect) == "BlastRain";

    public static bool BallistaPathAvailable(Player player, ushort skill)
    {
        var gem = SupportedSkill(player, skill);
        if (GolemStat(gem.Stats, Stat.IsRangedAttackTotem) == 0 || !BallistaEffect(player, gem.Effect)
            || GolemStat(gem.Stats, Stat.RangedAttackTotemOnlyAttacksWhenOwnerAttacks) != 0) return false;
        var hand = WeaponProfileFor(player, activeSet: true);
        return (hand.Bow || WeaponFor(player).WieldingWand) && WeaponRestricted(player, gem.Effect) is null;
    }

    public static int TotemLimit(Player player, ushort skill)
    {
        var gem = SupportedSkill(player, skill);
        var stats = SupportedStats(player, skill);
        int limit = TotemAllowance(player, gem.Effect, gem.Stats, stats);
        return GolemStat(stats, Stat.Maximum1TotemAtATime) == 0 ? limit : Math.Min(1, limit);
    }

    // Conjoined Spire uses the normal maximum to scale its bonuses and separately caps simultaneous totems.
    static int TotemAllowance(Player player, int effect, IReadOnlyDictionary<uint, int> own, IReadOnlyDictionary<uint, int> stats)
    {
        long limit = (long)BaseTotemsAllowed + GolemStat(stats, Stat.NumberOfAdditionalTotemsAllowed)
            + Math.Max(0, GolemStat(own, Stat.BaseNumberOfTotemsAllowed));
        if (GolemStat(stats, Stat.KeystoneAncestralBond) != 0) limit++;
        if (SkillsFor(player).IsAttack(effect)) limit += GolemStat(stats, Stat.AttackSkillsAdditionalTotemsAllowed);
        if (GolemStat(own, Stat.IsRangedAttackTotem) != 0)
            limit += GolemStat(stats, Stat.AttackSkillsAdditionalBallistaTotemsAllowed);
        return (int)Math.Clamp(limit, 0, int.MaxValue);
    }

    /// <summary>The distance a totem plants from its caster: 20 cells before totem_placement_range_+%. The observed
    /// 25 cells on the official servers were one character's, whose own stats carry +25% (19 of 19 readable captures),
    /// so 25 is the distance WITH the modifier and the base is 20; until now the measured 25 was multiplied by the
    /// same +25% again and planted at 31.2. The corpus cannot tell "base 20, stat applied" from "base 25, stat
    /// ignored"; this keeps the stat meaningful.</summary>
    public const double BaseTotemPlacementRange = 20;

    public static double TotemPlacementRange(Player player, ushort skill) => BaseTotemPlacementRange
        * Math.Max(0, 1 + GolemStat(SupportedStats(player, skill), Stat.TotemPlacementRangePlusPct) / 100d);

    /// <summary>The cell a totem plants at for an aim: the aim itself inside the placement range, else the point on
    /// the way to it at the range's edge (the clamp runs before the collision shove, which can still move the totem a
    /// cell or two off a body).</summary>
    public static (int X, int Y) TotemPlacementPoint(Player player, ushort skill, (int X, int Y) from, (int X, int Y) aim)
    {
        double range = TotemPlacementRange(player, skill);
        double dx = aim.X - from.X, dy = aim.Y - from.Y;
        double distance = double.Hypot(dx, dy);
        if (distance <= range) return aim;
        double scale = range / distance;
        return ((int)Math.Round(from.X + dx * scale), (int)Math.Round(from.Y + dy * scale));
    }

    public static double TotemTargetRange(Player player, ushort skill)
    {
        var stats = SupportedStats(player, skill);
        int basis = GolemStat(stats, Stat.BaseTotemRange);
        return (basis > 0 ? basis : 60) * Math.Max(0, 1 + GolemStat(stats, Stat.TotemRangePlusPct) / 100d);
    }

    public static double TotemAttackSeconds(Player player, ushort skill) =>
        SupportedAttackSecondsFor(player, skill, null, proxy: true);
}
