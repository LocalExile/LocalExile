using GameServer.Core.World;

public static partial class Game
{
    public static double ReservationHitTakenFactor(Player conn, SpawnedMonster source, double now)
    {
        var flesh = conn.Auras.Values.FirstOrDefault(a => a.EffectId == "FleshAndStone" && a.Stance == "Sand" && a.AffectsSelf);
        if (flesh is null) return 1;
        double radius = AuraRadius.Of(flesh, conn.Pools.AuraAreaPct, SkillsFor(conn), Stats);
        double reduction = AuraCatalog.FleshAndStoneDamageReduction(flesh, 0, SkillsFor(conn), Stats);
        return Math.Max(0, 1 + reduction * AuraCatalog.DistanceEffect(
            Dist(source.Walk.Position(now), conn.Walk.Position(now)), radius) / 100.0);
    }

    public static double ReservationTypedHitTakenFactor(Player conn, DamageType type, double now)
    {
        var arctic = conn.Auras.Values.FirstOrDefault(a => a.EffectId == "ArcticArmour");
        if (arctic is null || conn.Walk.Moving(now) || type is not (DamageType.Physical or DamageType.Fire)) return 1;
        uint name = type == DamageType.Physical ? Stat.NewArcticArmourPhysicalDamageTakenWhenHitPlusPctFinal
            : Stat.NewArcticArmourFireDamageTakenWhenHitPlusPctFinal;
        uint? key = (name);
        int value = SkillsFor(conn).EffectStats(arctic.Effect, arctic.Level, arctic.Quality).FirstOrDefault(s => s.Item1 == key).Item2;
        return Math.Max(0, 1 + value / 100.0);
    }
}
