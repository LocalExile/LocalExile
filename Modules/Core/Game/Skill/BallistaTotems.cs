public static partial class Game
{
    public static bool BallistaEffect(Player player, int effect) => SkillsFor(player).IsAttack(effect)
        && !SkillsFor(player).IsTravel(effect) && Supports.Of(effect)?.Types.Contains(47) != true
        && (BallistaRain(player, effect) || UsesProjectileRuntime(effect) && ResolveProjectileDefinition(effect) is not null);

    static bool BallistaRain(Player player, int effect) =>
        SkillsFor(player).EffectId(effect) is var id && (id == "BlastRain" || IsToxicRain(id) || IsArtilleryBallista(id));

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

    public static double TotemTargetRange(Player player, ushort skill)
    {
        var stats = SupportedStats(player, skill);
        int basis = GolemStat(stats, Stat.BaseTotemRange);
        return (basis > 0 ? basis : 60) * Math.Max(0, 1 + GolemStat(stats, Stat.TotemRangePlusPct) / 100d);
    }

    public static double TotemAttackSeconds(Player player, ushort skill) =>
        SupportedAttackSecondsFor(player, skill, null, proxy: true);
}
