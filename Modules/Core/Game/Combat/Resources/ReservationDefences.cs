using GameServer.Core.World;

public static partial class Game
{
    public static double ReservationHitTakenFactor(Player player, SpawnedMonster source, double now)
    {
        var flesh = player.Auras.Values.FirstOrDefault(a => a.EffectId == "FleshAndStone" && a.Stance == "Sand" && a.AffectsSelf);
        if (flesh is null) return 1;
        double radius = AuraRadius.Of(flesh, player.Pools.AuraAreaPct, SkillsFor(player), Stats);
        double reduction = AuraCatalog.FleshAndStoneDamageReduction(flesh, 0, SkillsFor(player), Stats);
        return Math.Max(0, 1 + reduction * AuraCatalog.DistanceEffect(
            Dist(source.Walk.Position(now), player.Walk.Position(now)), radius) / 100.0);
    }

    public static double ReservationTypedHitTakenFactor(Player player, DamageType type, double now)
    {
        var arctic = player.Auras.Values.FirstOrDefault(a => a.EffectId == "ArcticArmour");
        if (arctic is null || player.Walk.Moving(now) || type is not (DamageType.Physical or DamageType.Fire)) return 1;
        uint name = type == DamageType.Physical ? Stat.NewArcticArmourPhysicalDamageTakenWhenHitPlusPctFinal
            : Stat.NewArcticArmourFireDamageTakenWhenHitPlusPctFinal;
        uint? key = (name);
        int value = SkillsFor(player).EffectStats(arctic.Effect, arctic.Level, arctic.Quality).FirstOrDefault(s => s.Item1 == key).Item2;
        return Math.Max(0, 1 + value / 100.0);
    }
}
