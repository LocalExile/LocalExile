using GameServer.Core.Projectiles;

public static partial class Game
{
    static readonly IReadOnlySet<ProjectileEndReason> ShieldImpact = new HashSet<ProjectileEndReason>
        { ProjectileEndReason.Enemy, ProjectileEndReason.Terrain };
    static readonly IReadOnlySet<ProjectileEndReason> ShieldAtTarget = new HashSet<ProjectileEndReason>
        { ProjectileEndReason.Distance, ProjectileEndReason.Terrain };

    static void RegisterSpectralShieldThrows(Dictionary<string, ProjectileSkillDefinition> definitions)
    {
        foreach (var (effect, stationary) in new[]
        {
            ("SpectralShieldThrow", false), ("SpectralShieldThrowAltX", false), ("SpectralShieldThrowAltY", true),
        })
        {
            string primary = stationary ? "Metadata/Projectiles/TrarthanShieldThrow" : "Metadata/Projectiles/ShieldThrow";
            string secondary = stationary ? "Metadata/Projectiles/TrarthanShieldThrowSecondary" : "Metadata/Projectiles/ShieldThrowSecondary";
            definitions[effect] = SkillProjectileDefinition(effect, primary, new((get, speed, _) =>
            {
                var shard = GatewayProjectileDefaults.Create(secondary, get, speed);
                double more = Math.Max(0, 1 + get(Stat.ThrownShieldSecondaryProjectileDamagePlusPctFinal) / 100d);
                var burst = new ProjectileEmissionRecipe(shard, Radial: true,
                    RandomDirections: get(Stat.ThrownShieldProjectileFireRandomDirections) != 0)
                {
                    ChildPayload = payload => payload is ProjectileOffense offense ? offense.ScaleDamage(more) : payload,
                    ExcludeImpactTarget = true,
                    EmitOnContinueContact = !stationary,
                    EndReasons = stationary ? ShieldAtTarget : ShieldImpact,
                };
                var profile = GatewayProjectileDefaults.Create(primary, get, speed) with
                {
                    Count = 1, CountConsumer = ProjectileCountConsumer.Children, EndEmission = burst,
                    CannotPierce = !stationary, Pierce = stationary ? -1 : 0,
                    Chain = Math.Max(0, get(Stat.PrimaryProjectileChainsPlus)),
                    CannotReturn = stationary,
                };
                return GatewayProjectileModifiers.Resolve(profile, get, attack: true);
            }));
        }
    }

    static ProjectileUse StationaryShieldRelease(ProjectileReleaseContext context, ProjectileUse use)
    {
        var profile = use.Profile;
        if (profile.EndEmission is not { } burst) return use;
        int size = Math.Max(1, context.Own(Stat.StationaryShieldThrowProjectilesPerInterval));
        double interval = Math.Max(1, context.Own(Stat.StationaryShieldThrowFiringIntervalMs)) / 1000d;
        double acceleration = Math.Max(0, context.Own(Stat.StationaryShieldThrowIntervalAccelerationPctPerInterval)) / 100d;
        var waves = new List<(long Delay, int Count)>();
        double delay = 0;
        for (int left = burst.Child.Count; left > 0; left -= size)
        {
            delay += interval / (1 + acceleration * waves.Count);
            waves.Add((ProjectileRuntime.Time(delay), Math.Min(left, size)));
        }
        var shot = (ProjectileOffense)use.Payload!;
        return use with
        {
            Profile = profile with
            {
                Range = Math.Max(.001, Math.Min(profile.Range, (use.Aim - use.Origin).Length)),
                EndEmission = burst with { Waves = waves },
            },
            Payload = shot with { Presentation = new()
                { StayAtEndOfRange = true, ImpactLifetime = ProjectileRuntime.Time(delay + .1) } },
        };
    }
}
