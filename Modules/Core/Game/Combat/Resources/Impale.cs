using GameServer.Core.World;

public static partial class Game
{
    // Reflected physical damage receives target physical reduction and generic damage taken once. It is neither
    // attack nor spell damage and cannot crit, penetrate, or separately grant on-hit effects/Fortification/leech.
    public static int MonsterImpaleDamage(SpawnedMonster target, double stored) =>
        MonsterTakesMitigated(target, DamagePacket.Of(DamageType.Physical, stored, stored), spell: false);

    public static (int Damage, double Reflected, bool Applied) WithImpaleHitLocked(ClientState conn, SpawnedMonster target,
        int directDamage, DamagePacket rolled, bool critical, int sourceCriticalMultiplier,
        ImpaleStats.Settings settings, double hitMore = 1)
    {
        if (target.Invulnerable) return (directDamage, 0, false);
        double physical = rolled[DamageType.Physical].Min
            * (critical ? Math.Max(0, sourceCriticalMultiplier) / 100.0 : 1) * Math.Max(0, hitMore);
        var (stored, applied) = target.Impale.OnHit(physical, conn.Clock.Elapsed.TotalSeconds, settings, Random.Shared,
            target.Mods.Effects["avoid_impale_%"]);
        return (ImpaleStats.AddReflected(directDamage, MonsterImpaleDamage(target, stored)), stored, applied);
    }
}
