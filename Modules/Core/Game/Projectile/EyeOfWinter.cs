using GameServer.Core.Projectiles;

public static partial class Game
{
    const string EyeProjectile = "Metadata/Projectiles/FrozenSphere";
    const string EyeShardProjectile = "Metadata/Projectiles/FrozenSphereShard";

    static void RegisterEyeOfWinter(Dictionary<string, ProjectileSkillDefinition> definitions)
    {
        foreach (string effect in new[] { "EyeOfWinter", "EyeOfWinterAltX", "EyeOfWinterAltY" })
            definitions[effect] = new(EyeProjectile, new((get, speed, _) => EyeOfWinterProfile(effect, get, speed)))
            {
                SpecializeRelease = (context, use) =>
                {
                    ProjectileEmissionRecipe? ArtFor(ProjectileEmissionRecipe? recipe) => recipe is null ? null
                        : recipe with { Child = recipe.Child with { Id = Art.Projectile(EyeShardProjectile, context.Variation) } };
                    return use with { Profile = use.Profile with
                    {
                        TravelEmission = ArtFor(use.Profile.TravelEmission),
                        EndEmission = ArtFor(use.Profile.EndEmission),
                    } };
                },
            };
    }

    static ProjectileProfile EyeOfWinterProfile(string effect, Func<uint, int> get, double speed)
    {
        bool finalOnly = effect == "EyeOfWinterAltX";
        bool travelOnly = get(Stat.EyeOfWinterCountShardsWhileFlyingInstead) != 0;
        int nativeCount = Math.Max(1, get(Stat.EyeOfWinterBaseExplosionShards));
        int ChildStat(uint stat) => stat == Stat.BaseNumberOfProjectiles ? nativeCount : get(stat);
        // Projectiles.dat gives 300 world units/s for the eye and 800 for each shard.
        double shardSpeed = speed * (800d / 300);
        long shardLife = ProjectileRuntime.Time(.8);
        var child = GatewayProjectileDefaults.Resolve(EyeShardProjectile, ChildStat, shardSpeed, attack: false) with
        {
            Lifetime = shardLife,
            HitRule = new(ProjectileHitScope.Projectile),
            LifetimeDamageGainPercent = get(Stat.FreezingPulseDamagePlusPctFinalAtLongRange),
        };
        int count = get(Stat.MaximumNumberOfProjectilesToFireIs1) != 0 ? 1 : child.Count;
        child = child with { Count = 1 };

        // The durations and emission cadence are local estimates. The count sinks and variant stages
        // come from the active skill: bonus shards belong to the last spiral, or to Transience's flight.
        long interval = ProjectileRuntime.Time(.1 / Math.Max(.01, 1 + get(Stat.EyeOfWinterSpiralFireFrequencyPlusPct) / 100d));
        long fast = ProjectileRuntime.Time(.016);
        return GatewayProjectileDefaults.Create(EyeProjectile, get, speed) with
        {
            Count = 1,
            DirectDamage = false,
            CannotPierce = true,
            CannotChain = true,
            CannotSplit = true,
            CannotFork = true,
            CannotReturn = true,
            Lifetime = travelOnly ? interval * count + 1 : ProjectileRuntime.Time(2),
            TravelEmission = finalOnly ? null : new(child, Radial: true, Interval: interval)
                { TravelGroups = travelOnly ? count : null },
            EndEmission = travelOnly ? null : new(child, Radial: true)
            {
                EndReasons = new HashSet<ProjectileEndReason>
                    { ProjectileEndReason.Distance, ProjectileEndReason.Lifetime, ProjectileEndReason.Terrain },
                Waves = [.. Enumerable.Range(0, count).Select(i => (i * fast, 1))],
            },
        };
    }
}
