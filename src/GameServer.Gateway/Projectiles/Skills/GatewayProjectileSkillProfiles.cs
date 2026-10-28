using GameServer.Core.Projectiles;

/// <summary>Named behavior and catalog policies on the common projectile runtime.</summary>
static class GatewayProjectileSkillProfiles
{
    // User-tuned feel values, not official timings or frame measurements. Keep fractional milliseconds.
    public const double ClusterDelaySeconds = 0;
    public const double ClusterIntervalSeconds = 1d / 30;
    public static IReadOnlyList<string> Effects { get; } = Array.AsReadOnly(new[]
        { "Fireball", "KineticBlast", "KineticBlastAltX", "KineticBlastAltY" });

    static readonly HashSet<string> CannotReturn = new(StringComparer.Ordinal)
    {
        "Spark", "SparkAltX", "SparkAltY", "RainOfArrows", "BlastRain", "SplittingSteel", "VenomGyre",
        "WinterOrb", "ForbiddenRite", "Cremation", "RollingMagma", "ToxicRain", "PoisonousConcoction"
    };
    static readonly IReadOnlySet<ProjectileEndReason> TerminalStops = new HashSet<ProjectileEndReason>
        { ProjectileEndReason.Enemy, ProjectileEndReason.Terrain, ProjectileEndReason.Distance, ProjectileEndReason.Returned };
    static readonly IReadOnlySet<ProjectileEndReason> NonContactStops = new HashSet<ProjectileEndReason>
        { ProjectileEndReason.Distance, ProjectileEndReason.Returned };

    public static bool IsKineticBlast(string effect) => effect is "KineticBlast" or "KineticBlastAltX" or "KineticBlastAltY";
    public static bool IsFireball(string metadata) => metadata.Contains("Fireball", StringComparison.Ordinal);

    public static ProjectileProfile Resolve(string effect, string metadata, Func<string, int> get, double speed, bool attack)
    {
        var p = GatewayProjectileDefaults.Create(metadata, get, speed) with
        {
            CannotReturn = CannotReturn.Contains(effect), ReturnOnDistance = effect != "FreezingPulse"
        };
        if (IsKineticBlast(effect))
        {
            bool clustering = effect == "KineticBlastAltX";
            int count = get("cluster_burst_spawn_amount") is > 0 and var amount ? amount : clustering ? 3 : 4;
            int areaFinal = get("active_skill_area_damage_+%_final");
            // Exported normal/compatibility profiles carry -40; the default also supports synthetic inputs.
            double multiplier = 1 + (areaFinal != 0 ? areaFinal : clustering ? 0 : -40) / 100d;
            p = p with
            {
                Count = clustering ? 1 : p.Count,
                CannotSplit = clustering,
                CountConsumer = clustering ? ProjectileCountConsumer.ContactAreas : ProjectileCountConsumer.Primary,
                ContactArea = new("kinetic-blast", 0, count, Multiplier: Math.Max(0, multiplier))
                    { SectorPlacement = true, RespectTerrain = true, ExcludeContactTarget = true,
                        Delay = ProjectileRuntime.Time(ClusterDelaySeconds),
                        Interval = ProjectileRuntime.Time(ClusterIntervalSeconds) },
                EndAreaReasons = NonContactStops, EndAreaOnReturnTurnaround = true
            };
        }
        else if (IsFireball(metadata))
            p = p with { EndArea = new("fireball-explosion", 0), EndAreaReasons = TerminalStops };

        p = GatewayProjectileModifiers.Resolve(p, get, attack);
        // The endpoint cluster uses the same already-resolved quantity as each contact cluster.
        if (p.ContactArea is { } contact) p = p with { EndArea = contact };
        return ScaleAreas(p, effect, get, spell: !attack);
    }

    public static ProjectileProfile ScaleAreas(ProjectileProfile p, string effect, Func<string, int> get, bool spell)
    {
        int Value(string key, int fallback) => get(key) is > 0 and var value ? value : fallback;
        if (IsKineticBlast(effect) && p.ContactArea is { } contact)
        {
            bool clustering = effect == "KineticBlastAltX";
            int blast = SkillArea.Radius(Value("active_skill_base_area_of_effect_radius", clustering ? 19 : 14), get, spell);
            int placement = SkillArea.Radius(Value("active_skill_base_secondary_area_of_effect_radius", clustering ? 28 : 20), get, spell);
            // Centers lie on the scaled placement circle; only the angle is sampled within each sector.
            var area = contact with { Radius = blast, Offset = placement, MinimumOffset = placement };
            return p with { ContactArea = area, EndArea = area };
        }
        if (p.EndArea is { } terminal)
            return p with { EndArea = terminal with
                { Radius = SkillArea.Radius(SkillArea.BaseRadius(effect, get), get, spell) } };
        return p;
    }
}
