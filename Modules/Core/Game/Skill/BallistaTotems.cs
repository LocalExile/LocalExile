public static partial class Game
{
    public static bool BallistaEffect(Player conn, int effect) => SkillsFor(conn).IsAttack(effect)
        && !SkillsFor(conn).IsTravel(effect) && Supports.Of(effect)?.Types.Contains(47) != true
        && UsesProjectileRuntime(effect) && ResolveProjectileDefinition(effect) is not null
        // The artillery mortar needs a landing sequence, not a straight-arrow approximation.
        && SkillsFor(conn).EffectId(effect) is not ("ArtilleryBallista" or "ArtilleryBallistaAltX" or "ArtilleryBallistaAltY");

    public static bool BallistaPathAvailable(Player conn, ushort skill)
    {
        var gem = SupportedSkill(conn, skill);
        if (GolemStat(gem.Stats, Stat.IsRangedAttackTotem) == 0 || !BallistaEffect(conn, gem.Effect)
            || GolemStat(gem.Stats, Stat.RangedAttackTotemOnlyAttacksWhenOwnerAttacks) != 0) return false;
        var hand = WeaponProfileFor(conn, activeSet: true);
        return (hand.Bow || WeaponFor(conn).WieldingWand) && WeaponRestricted(conn, gem.Effect) is null;
    }

    public static int TotemLimit(Player conn, ushort skill)
    {
        var gem = SupportedSkill(conn, skill);
        var stats = SupportedStats(conn, skill);
        long limit = (long)BaseTotemsAllowed + GolemStat(stats, Stat.NumberOfAdditionalTotemsAllowed);
        if (GolemStat(stats, Stat.KeystoneAncestralBond) != 0) limit++;
        if (SkillsFor(conn).IsAttack(gem.Effect)) limit += GolemStat(stats, Stat.AttackSkillsAdditionalTotemsAllowed);
        if (GolemStat(gem.Stats, Stat.IsRangedAttackTotem) != 0)
            limit += GolemStat(stats, Stat.AttackSkillsAdditionalBallistaTotemsAllowed);
        return (int)Math.Clamp(limit, 0, int.MaxValue);
    }

    public static double TotemTargetRange(Player conn, ushort skill)
    {
        var stats = SupportedStats(conn, skill);
        int basis = GolemStat(stats, Stat.BaseTotemRange);
        return (basis > 0 ? basis : 60) * Math.Max(0, 1 + GolemStat(stats, Stat.TotemRangePlusPct) / 100d);
    }

    public static double TotemAttackSeconds(Player conn, ushort skill) =>
        SupportedAttackSecondsFor(conn, skill, null, proxy: true);
}
