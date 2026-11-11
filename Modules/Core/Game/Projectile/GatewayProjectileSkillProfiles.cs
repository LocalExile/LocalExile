using GameServer.Core.Projectiles;


/// <summary>Named behavior and catalog policies on the common projectile runtime.</summary>
public static class GatewayProjectileSkillProfiles
{
    // User-tuned feel values, not official timings or frame measurements. Keep fractional milliseconds.
    public const double ClusterDelaySeconds = 0;
    public const double ClusterIntervalSeconds = 1d / 30;
    public const double RollingMagmaBounceCells = 16;
    public static IReadOnlyList<string> Effects { get; } = Array.AsReadOnly(new[]
        { "Fireball", "KineticBlast", "KineticBlastAltX", "KineticBlastAltY",
            "KineticBolt", "KineticBoltAltX", "KineticBoltAltY" });

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
    public static bool IsKineticBolt(string effect) => effect is "KineticBolt" or "KineticBoltAltX" or "KineticBoltAltY";
    public static bool IsFireball(string metadata) => metadata.Contains("Fireball", StringComparison.Ordinal);
    public static bool IsLightningArrow(string effect) => effect is "LightningArrow" or "LightningArrowRoyale" or "VaalLightningArrow";

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

        // Rolling Magma lobs its orb onto the aim, where it explodes, then chains along the ground: each of its chains
        // (number_of_chains, 2 at level 1) is a bounce forward and another explosion, which may hit the same monsters.
        // The client's bounces go straight on without seeking an enemy (Frida 2026-11-01, trace-chain-decisions.js).
        // shortcut: 16 cells between impacts and the 14-cell base radius (SkillArea.BaseRadius) are not read from the
        // client; it takes 375 ms per bounce, slower than this flight at the orb's speed, so a bounce's hits land ~0.1 s early.
        else if (effect == "RollingMagma")
            p = p with
            {
                Lobbed = true, DirectDamage = false, GroundBounceDistance = RollingMagmaBounceCells,
                EndArea = new("rolling-magma", 0), EndAreaReasons = new HashSet<ProjectileEndReason> { ProjectileEndReason.Landing }
            };
        else if (IsLightningArrow(effect) && get("lightning_arrow_maximum_number_of_extra_targets") is > 0 and var extra)
            p = p with
            {
                ContactTargets = new("lightning-arrow-targets", extra, 18)
            };

        // Arrow Nova's payload and circular volley. The child count keeps this stage's base count; the shared modifier
        // pass below adds the skill's and the support's extra arrows to it.
        if (get("projectiles_rain") != 0 || get("skill_is_rain_skill") != 0) p = ArrowNovaChildren(p);

        p = GatewayProjectileModifiers.Resolve(p, get, attack, arrows);
        // Its chains bounce on the ground instead of seeking another monster.
        if (effect == "RollingMagma") p = p with { GroundChains = p.Chain, Chain = 0 };
        // Spectral Throw pierces every enemy and flies back to the thrower, hitting again on the way back.
        if (effect == "SpectralThrow") p = p with { Pierce = -1, Returns = true };
        if (IsKineticBolt(effect)) p = WithKineticBoltLegs(p);
        if (IsTornadoShot(effect)) p = TornadoShotChildren(p, get);
        // The endpoint cluster uses the same already-resolved quantity as each contact cluster.
        if (p.ContactArea is { } contact) p = p with { EndArea = contact };
        return ScaleAreas(p, effect, get, spell: !attack);
    }

    public static ProjectileProfile WithKineticBoltLegs(ProjectileProfile parent, ushort sequence = 0)
    {
        var secondary = KineticBoltSecondary(parent);
        return parent with
        {
            Legs = i => new KineticBoltLegs(sequence, parent.SequentialDelay > 0 ? i : 0, parent.ZigZags,
                parent.Speed, parent.Range, secondary),
            LegLeash = 0
        };
    }

    public static ProjectileProfile KineticBoltSecondary(ProjectileProfile parent) => parent with
    {
        Id = "Metadata/Projectiles/ZigZagSecondaryWandProjectile",
        Count = 1,
        NativeVolley = null,
        SpreadRadians = 0,
        AimSpreadDistance = 0,
        SequentialDelay = 0,
        CountConsumer = ProjectileCountConsumer.Primary,
        Returns = false,
        ReturnChancePercent = 0,
        ReturnRolls = null,
        CannotReturn = true,
        ZigZags = 0,
        Legs = null,
        LegLeash = 0,
        HidePredictedVisual = true
    };

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
            ForkExtraProjectileChancePercent = parent.ForkExtraProjectileChancePercent,
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

    // The client's terminal RNG (0x1419c73c0) seeds [sequence, mother-derived byte, owner, repeats left,
    // secondary-release byte, 0, 0, 0]. A fresh mother's derived byte is 2, and each arrow of a volley adds its index.
    // The radial emitter then spaces children around that sampled base ray with no extra phase.
    public static ProjectileVector TornadoShotBaseDirection(ushort sequence, uint derived, uint owner, uint repeats)
    {
        var random = new AnimationVariantSelector.NativeRandom([sequence, derived, owner, repeats, 0, 0, 0, 0]);
        uint sample = TornadoBounded(ref random, 7001);
        float phi = (float)sample / 7000f * 6.283180236816406f;
        return new(MathF.Sin(phi), -MathF.Cos(phi));
    }

    static ProjectileVector TornadoShotBaseDirection(ProjectileEntity parent) =>
        TornadoShotBaseDirection(parent.Use.Sequence, (uint)(2 + parent.VolleyIndex), parent.Use.Owner.Id,
            parent.Use.RepeatsLeft);

    // The native bounded draw (0x1404d0990): reject the incomplete top bucket, then take the remainder.
    static uint TornadoBounded(ref AnimationVariantSelector.NativeRandom random, uint exclusiveMax)
    {
        ulong limit = (1UL << 32) / exclusiveMax * exclusiveMax;
        while (true)
        {
            uint value = random.NextUInt();
            if (value < limit) return value % exclusiveMax;
        }
    }

    // Arrow Nova (support projectiles_rain + skill_is_rain_skill): the supported bow attack fires one payload arrow
    // that lands at the aim and releases the skill's whole volley in a circle from there. Frida 2026-11-04 (Split
    // Arrow, 18 arrows): all children started on one cell and the directions were exactly 20 degrees apart, the first
    // on the aim ray; the client drew both the payload and the children, and the payload dealt no damage.
    static ProjectileProfile ArrowNovaChildren(ProjectileProfile parent)
    {
        var child = parent with
        {
            // Keep the base count and the skill's own contact/terminal areas: these are its normal arrows.
            NativeVolley = null, SpreadRadians = 0, AimSpreadDistance = 0,
            // The client draws these itself (Frida 2026-11-04: the 18 SplitArrowDefault children were client objects),
            // so a server object would be a second, later volley at the landing cell.
            HideVisual = true,
            CountConsumer = ProjectileCountConsumer.Primary,
            DirectDamage = true, Lobbed = false,
            EndEmission = null, TravelEmission = null, Pulse = null,
            EndAreaOnReturnTurnaround = false, Legs = null, LegLeash = 0, GroundChains = 0,
            Returns = false, ReturnChancePercent = 0, ReturnRolls = null, CannotReturn = true,
            FormChange = null, SplitForm = null, SplitOnTerrain = false
        };
        return parent with
        {
            // One payload arrow: no hits, no contacts, and no server object (the official server sends none; the
            // parabola was ours). The rain rig and its attached arrow art carry the visual; the children stay hidden.
            Count = 1, CountConsumer = ProjectileCountConsumer.Children,
            DirectDamage = false, HideVisual = true, Lobbed = true, ArcHeight = 200,
            Pierce = 0, CannotPierce = true, Fork = 0, Chain = 0, Split = 0, CannotSplit = true,
            EndArea = null, EndAreaReasons = null, EndAreaOnReturnTurnaround = false,
            ContactArea = null, ContactTargets = null,
            EndEmission = new(child, Radial: true) { Delay = ProjectileRuntime.Time(0.221) }
        };
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
        if (IsLightningArrow(effect) && p.ContactTargets is { } targets)
            return p with { ContactTargets = targets with
                { Radius = SkillArea.Radius(Value("active_skill_base_area_of_effect_radius", 18), get, spell) } };
        if (p.EndArea is { } terminal)
            return p with { EndArea = terminal with
                { Radius = SkillArea.Radius(SkillArea.BaseRadius(effect, get), get, spell) } };
        return p;
    }
}
