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

    public static ProjectileProfile Resolve(string effect, string metadata, Func<string, int> get, double speed, bool attack,
        bool arrows = false)
    {
        var p = GatewayProjectileDefaults.Create(metadata, get, speed, arrows) with
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
        // Wiki Spark: a set of sparks forgets the enemies it hit every 0.66 seconds, so one cast can hit a target again.
        // The transfigured and Vaal versions share the rule.
        // Their wander is the client's own legs (SparkLegs, set at release from the use's sequence).
        else if (effect is "Spark" or "SparkAltX" or "SparkAltY" or "VaalSpark")
            p = p with { HitRule = new(ProjectileHitScope.Emission, ProjectileRepeat.Window, ProjectileRuntime.Time(0.66)) };

        p = GatewayProjectileModifiers.Resolve(p, get, attack, arrows);
        if (IsTornadoShot(effect)) p = TornadoShotChildren(p, get);
        // The endpoint cluster uses the same already-resolved quantity as each contact cluster.
        if (p.ContactArea is { } contact) p = p with { EndArea = contact };
        return ScaleAreas(p, effect, get, spell: !attack);
    }

    static bool IsTornadoShot(string effect) => effect is "TornadoShot" or "TornadoShotAltX" or "TornadoShotAltY";

    // The client predicts the primary arrow and its terminal group itself (Frida: seven local and seven server
    // TornadoShotArrow objects for one use), so the server sends no object for either and only simulates the damage.
    // The group is three TornadoShotSecondaryArrow flights around the client's terminal RNG base ray, so both sides
    // fan out the same way (TornadoShotBaseDirection).
    static ProjectileProfile TornadoShotChildren(ProjectileProfile parent, Func<string, int> get)
    {
        int count = Math.Clamp(get("tornado_shot_num_of_secondary_projectiles") is > 0 and var n ? n : 3, 1, 256);
        var child = parent with
        {
            Id = "Metadata/Projectiles/TornadoShotSecondaryArrow",
            Count = count,
            NativeVolley = null,
            SpreadRadians = 0,
            AimSpreadDistance = 0,
            Range = SystemSettings.Default.ProjectileMaximumRangeCells,
            // Client trace: the secondary action's final callback caps at 300 ms, so projectile speed sets the range.
            Lifetime = ProjectileRuntime.Time(.3),
            SequentialDelay = 0,
            CountConsumer = ProjectileCountConsumer.Primary,
            Pierce = parent.Pierce, Fork = parent.Fork, Chain = parent.Chain, Split = 0,
            CannotPierce = parent.CannotPierce, CannotFork = parent.CannotFork, CannotChain = parent.CannotChain,
            CannotSplit = true,
            Returns = false, ReturnChancePercent = 0, ReturnRolls = null, CannotReturn = true,
            EndEmission = null, TravelEmission = null, Pulse = null,
            EndArea = null, EndAreaReasons = null, EndAreaOnReturnTurnaround = false, ContactArea = null,
            ContactTargets = null, Lobbed = false, Legs = null, LegLeash = 0, GroundChains = 0,
            FormChange = null, SplitForm = null, SplitOnTerrain = false,
            HideVisual = true
        };
        return parent with
        {
            // Client +0x330 bit 0x4: the ordinary primary arrow pierces before its terminal child group. The negative
            // sentinel is the runtime's never-decrementing pierce and the panel's virtual_always_pierce.
            Pierce = -1, CannotPierce = false,
            // The client draws its own primary arrow; a server object would be a second arrow from the bow contact.
            HideVisual = true,
            EndEmission = new(child, Radial: true, BaseDirection: TornadoShotBaseDirection)
        };
    }

    // The client's terminal RNG (0x1419c29b0) seeds [sequence, mother-derived byte, owner, repeats left,
    // secondary-release byte, 0, 0, 0]. A fresh mother's derived byte is 2, and each arrow of a volley adds its index.
    // The radial emitter then spaces children around that sampled base ray with no extra phase.
    internal static ProjectileVector TornadoShotBaseDirection(ushort sequence, uint derived, uint owner, uint repeats)
    {
        var random = new AnimationVariantSelector.NativeRandom([sequence, derived, owner, repeats, 0, 0, 0, 0]);
        uint sample = TornadoBounded(ref random, 7001);
        float phi = (float)sample / 7000f * 6.283180236816406f;
        return new(MathF.Sin(phi), -MathF.Cos(phi));
    }

    static ProjectileVector TornadoShotBaseDirection(ProjectileEntity parent) =>
        TornadoShotBaseDirection(parent.Use.Sequence, (uint)(2 + parent.VolleyIndex), parent.Use.Owner.Id,
            parent.Use.RepeatsLeft);

    // The native bounded draw (0x1404d03d0): reject the incomplete top bucket, then take the remainder.
    static uint TornadoBounded(ref AnimationVariantSelector.NativeRandom random, uint exclusiveMax)
    {
        ulong limit = (1UL << 32) / exclusiveMax * exclusiveMax;
        while (true)
        {
            uint value = random.NextUInt();
            if (value < limit) return value % exclusiveMax;
        }
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
