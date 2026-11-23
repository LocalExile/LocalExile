using GameServer.Core.Projectiles;

public static partial class Game
{
    sealed class FocusedBallista
    {
        public (int X, int Y)? Aim;
    }

    static readonly RuleTable<TotemState.Totem, FocusedBallista> FocusedBallistas = new();

    static void BindFocusedBallista(TotemState.Totem totem, SupportedGem source)
    {
        if (totem.Ballista && GolemStat(source.Stats, Stat.RangedAttackTotemOnlyAttacksWhenOwnerAttacks) != 0)
            FocusedBallistas.AddOrUpdate(totem, new());
        else FocusedBallistas.Remove(totem);
    }

    // Called only for emitted projectiles, including predicted ones before the visual is skipped. A simultaneous
    // volley gives each ready ballista one order. Forks, chains and proxy releases are not the owner's attack.
    static void FocusBallistasOnOwnerShot(Player player, ProjectileEntity shot)
    {
        var use = shot.Use;
        if (player.Totems.All.Count == 0 || shot.ParentId != 0
            || shot.VolleyIndex != 0 && shot.Profile.SequentialDelay == 0
            || use.Owner.Id != player.Id || use.Emitter.Id != player.Id
            || use.Payload is not ProjectileOffense { Spell: false, FromTotem: false, FromTrapOrMine: false }) return;
        double at = shot.CreatedAt / (double)ProjectileRuntime.QuantaPerSecond;
        var aim = ((int)Math.Round(use.Aim.X), (int)Math.Round(use.Aim.Y));
        foreach (var totem in player.Totems.All)
            if (totem.Ballista && totem.Alive(at) && at >= totem.NextCast
                && FocusedBallistas.TryGetValue(totem, out var focus))
                focus.Aim = aim;
    }

    public static bool BallistaEffect(Player player, int effect) => SkillsFor(player).IsAttack(effect)
        && !SkillsFor(player).IsTravel(effect) && Supports.Of(effect)?.Types.Contains(47) != true
        && UsesProjectileRuntime(effect) && ResolveProjectileDefinition(effect) is not null
        // The artillery mortar needs a landing sequence, not a straight-arrow approximation.
        && SkillsFor(player).EffectId(effect) is not ("ArtilleryBallista" or "ArtilleryBallistaAltX" or "ArtilleryBallistaAltY");

    public static bool BallistaPathAvailable(Player player, ushort skill)
    {
        var gem = SupportedSkill(player, skill);
        if (GolemStat(gem.Stats, Stat.IsRangedAttackTotem) == 0 || !BallistaEffect(player, gem.Effect)) return false;
        var hand = WeaponProfileFor(player, activeSet: true);
        return (hand.Bow || WeaponFor(player).WieldingWand) && WeaponRestricted(player, gem.Effect) is null;
    }

    public static int TotemLimit(Player player, ushort skill)
    {
        var gem = SupportedSkill(player, skill);
        var stats = SupportedStats(player, skill);
        long limit = (long)BaseTotemsAllowed + GolemStat(stats, Stat.NumberOfAdditionalTotemsAllowed);
        if (GolemStat(stats, Stat.KeystoneAncestralBond) != 0) limit++;
        if (SkillsFor(player).IsAttack(gem.Effect)) limit += GolemStat(stats, Stat.AttackSkillsAdditionalTotemsAllowed);
        if (GolemStat(gem.Stats, Stat.IsRangedAttackTotem) != 0)
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
