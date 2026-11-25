using GameServer.Core.World;

public static partial class Game
{
    const int ImpalerRadius = 30;

    public static ImpaleStats.Settings PlayerImpaleSettings(IReadOnlyDictionary<uint, int> stats, StatCatalog catalog,
        bool attack, IReadOnlyDictionary<uint, int>? own = null, IReadOnlyDictionary<uint, int>? weaponLocal = null,
        bool critical = false)
    {
        var settings = ImpaleStats.From(stats, catalog, attack, own, weaponLocal, critical);
        return stats.GetValueOrDefault(Stat.KeystoneImpale) == 0 ? settings : settings with
        {
            Applications = 6,
            ReapplicationDelay = 5,
        };
    }

    // Reflected physical damage receives target physical reduction and generic damage taken once. It is neither
    // attack nor spell damage and cannot crit, penetrate, or separately grant on-hit effects/Fortification/leech.
    public static int MonsterImpaleDamage(SpawnedMonster target, double stored, double now = double.NaN) =>
        MonsterTakesMitigated(target, DamagePacket.Of(DamageType.Physical, stored, stored), spell: false, now);

    public static (int Damage, double Reflected, bool Applied) WithImpaleHitLocked(Player player, SpawnedMonster target,
        int directDamage, DamagePacket rolled, bool critical, int sourceCriticalMultiplier,
        ImpaleStats.Settings settings, double hitMore = 1)
    {
        if (target.Invulnerable) return (directDamage, 0, false);
        double physical = rolled[DamageType.Physical].Min
            * (critical ? Math.Max(0, sourceCriticalMultiplier) / 100.0 : 1) * Math.Max(0, hitMore);
        return ApplyPlayerImpaleContact(player, target, directDamage, physical, AreaTime(player), ProjectileWorldTime(player),
            settings, AreaRng(player));
    }

    static (int Damage, double Reflected, bool Applied) ApplyPlayerImpaleContact(Player player, SpawnedMonster target,
        int directDamage, double physical, double at, double mitigationAt, ImpaleStats.Settings settings, Random random)
    {
        var result = ApplyImpaleContact(target, directDamage, physical, at, mitigationAt, settings, random);
        if (result.Applied && settings.ReapplicationDelay > 0)
        {
            var centre = target.Walk.Position(at);
            foreach (var (_, enemy) in player.Area.MonstersNear(centre, ImpalerRadius))
                if (!ReferenceEquals(enemy, target) && enemy is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false }
                    && Dist(centre, enemy.Walk.Position(at)) <= ImpalerRadius)
                    enemy.Impale.Spread(physical, at, settings, random, enemy.Effects[Stat.AvoidImpalePct]);
        }
        return result;
    }

    static (int Damage, double Reflected, bool Applied) ApplyImpaleContact(SpawnedMonster target, int directDamage,
        double physical, double at, double mitigationAt, ImpaleStats.Settings settings, Random random)
    {
        var (stored, applied) = target.Impale.OnHit(physical, at, settings, random, target.Effects[Stat.AvoidImpalePct]);
        return (ImpaleStats.AddReflected(directDamage, MonsterImpaleDamage(target, stored, mitigationAt)), stored, applied);
    }

    readonly record struct ImpaleContact(double Physical, double At, ImpaleStats.Settings Settings, Random Random);
}
