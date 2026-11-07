using GameServer.Core.World;

static partial class Game
{
    internal static double ReservationHitTakenFactor(ClientState conn, SpawnedMonster source, double now)
    {
        var flesh = conn.Auras.Values.FirstOrDefault(a => a.EffectId == "FleshAndStone" && a.Stance == "Sand" && a.AffectsSelf);
        if (flesh is null) return 1;
        double radius = AuraRadius.Of(flesh, conn.Pools.AuraAreaPct, SkillsFor(conn), Stats);
        double reduction = AuraCatalog.FleshAndStoneDamageReduction(flesh, 0, SkillsFor(conn), Stats);
        return Math.Max(0, 1 + reduction * AuraCatalog.DistanceEffect(
            Dist(source.Walk.Position(now), conn.Walk.Position(now)), radius) / 100.0);
    }

    internal static double ReservationTypedHitTakenFactor(ClientState conn, DamageType type, double now)
    {
        var arctic = conn.Auras.Values.FirstOrDefault(a => a.EffectId == "ArcticArmour");
        if (arctic is null || conn.Walk.Moving(now) || type is not (DamageType.Physical or DamageType.Fire)) return 1;
        string name = type == DamageType.Physical ? "new_arctic_armour_physical_damage_taken_when_hit_+%_final"
            : "new_arctic_armour_fire_damage_taken_when_hit_+%_final";
        uint? key = Stats.Key(name);
        int value = SkillsFor(conn).EffectStats(arctic.Effect, arctic.Level, arctic.Quality).FirstOrDefault(s => s.Item1 == key).Item2;
        return Math.Max(0, 1 + value / 100.0);
    }
}
