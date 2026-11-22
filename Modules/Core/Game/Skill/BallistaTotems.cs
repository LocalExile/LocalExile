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
        if (totem.Ballista && GolemStat(source.Stats, "ranged_attack_totem_only_attacks_when_owner_attacks") != 0)
            FocusedBallistas.AddOrUpdate(totem, new());
        else FocusedBallistas.Remove(totem);
    }

    // Called only for emitted projectiles, including predicted ones before the visual is skipped. A simultaneous
    // volley gives each ready ballista one order. Forks, chains and proxy releases are not the owner's attack.
    static void FocusBallistasOnOwnerShot(ClientState conn, ProjectileEntity shot)
    {
        var use = shot.Use;
        if (conn.Totems.All.Count == 0 || shot.ParentId != 0
            || shot.VolleyIndex != 0 && shot.Profile.SequentialDelay == 0
            || use.Owner.Id != PlayerId || use.Emitter.Id != PlayerId
            || use.Payload is not ProjectileOffense { Spell: false, FromTotem: false, FromTrapOrMine: false }) return;
        double at = shot.CreatedAt / (double)ProjectileRuntime.QuantaPerSecond;
        var aim = ((int)Math.Round(use.Aim.X), (int)Math.Round(use.Aim.Y));
        foreach (var totem in conn.Totems.All)
            if (totem.Ballista && totem.Alive(at) && at >= totem.NextCast
                && FocusedBallistas.TryGetValue(totem, out var focus))
                focus.Aim = aim;
    }

    public static bool BallistaEffect(ClientState conn, int effect) => SkillsFor(conn).IsAttack(effect)
        && !SkillsFor(conn).IsTravel(effect) && Supports.Of(effect)?.Types.Contains(47) != true
        && UsesProjectileRuntime(effect) && ResolveProjectileDefinition(effect) is not null
        // The artillery mortar needs a landing sequence, not a straight-arrow approximation.
        && SkillsFor(conn).EffectId(effect) is not ("ArtilleryBallista" or "ArtilleryBallistaAltX" or "ArtilleryBallistaAltY");

    public static bool BallistaPathAvailable(ClientState conn, ushort skill)
    {
        var gem = SupportedSkill(conn, skill);
        if (GolemStat(gem.Stats, "is_ranged_attack_totem") == 0 || !BallistaEffect(conn, gem.Effect)) return false;
        var hand = WeaponProfileFor(conn, activeSet: true);
        return (hand.Bow || WeaponFor(conn).WieldingWand) && WeaponRestricted(conn, gem.Effect) is null;
    }

    public static int TotemLimit(ClientState conn, ushort skill)
    {
        var gem = SupportedSkill(conn, skill);
        var stats = SupportedStats(conn, skill);
        long limit = (long)BaseTotemsAllowed + GolemStat(stats, "number_of_additional_totems_allowed");
        if (GolemStat(stats, "keystone_ancestral_bond") != 0) limit++;
        if (SkillsFor(conn).IsAttack(gem.Effect)) limit += GolemStat(stats, "attack_skills_additional_totems_allowed");
        if (GolemStat(gem.Stats, "is_ranged_attack_totem") != 0)
            limit += GolemStat(stats, "attack_skills_additional_ballista_totems_allowed");
        return (int)Math.Clamp(limit, 0, int.MaxValue);
    }

    public static double TotemTargetRange(ClientState conn, ushort skill)
    {
        var stats = SupportedStats(conn, skill);
        int basis = GolemStat(stats, "base_totem_range");
        return (basis > 0 ? basis : 60) * Math.Max(0, 1 + GolemStat(stats, "totem_range_+%") / 100d);
    }

    public static double TotemAttackSeconds(ClientState conn, ushort skill) =>
        SupportedAttackSecondsFor(conn, skill, null, proxy: true);
}
