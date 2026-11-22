using GameServer.Core.Projectiles;


/// <summary>Named behavior and catalog policies on the common projectile runtime.</summary>
public static class GatewayProjectileSkillProfiles
{
    // User-tuned feel values, not official timings or frame measurements. Keep fractional milliseconds.
    // Client cluster timer (3.29.3c 0x141ac99c0): first area on the first update, then 0.1 s per area.
    public const double ClusterDelaySeconds = 0;
    public const double ClusterIntervalSeconds = 0.1;
    // Client placement (3.29.3c 0x141ac9630): three candidate angles per sector before it is omitted.
    public const int ClusterPlacementAttempts = 3;
    public const double RollingMagmaBounceCells = 16;
    public static IReadOnlyList<string> Effects { get; } = Array.AsReadOnly(new[]
        { "Fireball", "KineticBlast", "KineticBlastAltX", "KineticBlastAltY",
            "KineticBolt", "KineticBoltAltX", "KineticBoltAltY",
            "MoltenStrike", "MoltenStrikeAltX", "MoltenStrikeAltY", "VaalMoltenStrike", "HolyFlameTotem" });

    static readonly HashSet<string> CannotReturn = new(StringComparer.Ordinal)
    {
        "Spark", "SparkAltX", "SparkAltY", "Twister", "RainOfArrows", "BlastRain", "SplittingSteel", "VenomGyre",
        "WinterOrb", "ForbiddenRite", "Cremation", "RollingMagma", "ToxicRain", "PoisonousConcoction"
    };
    static readonly IReadOnlySet<ProjectileEndReason> TerminalStops = new HashSet<ProjectileEndReason>
        { ProjectileEndReason.Enemy, ProjectileEndReason.Terrain, ProjectileEndReason.Distance, ProjectileEndReason.Returned };
    static readonly IReadOnlySet<ProjectileEndReason> NonContactStops = new HashSet<ProjectileEndReason>
        { ProjectileEndReason.Distance, ProjectileEndReason.Returned };

    public static bool IsKineticBlast(string effect) => effect is "KineticBlast" or "KineticBlastAltX" or "KineticBlastAltY";
    public static bool IsKineticBolt(string effect) => effect is "KineticBolt" or "KineticBoltAltX" or "KineticBoltAltY";
    public static bool IsBallLightningOrb(string? effect) => effect is "BallLightning" or "BallLightningAltX" or "BallLightningAltY";
    internal const double BallLightningLifetimeSeconds = 2;

    public static bool IsFireball(string metadata) => metadata.Contains("Fireball", StringComparison.Ordinal);
    public static bool IsLightningArrow(string effect) => effect is "LightningArrow" or "LightningArrowRoyale" or "VaalLightningArrow";
    public static bool IsMoltenStrike(string? effect) =>
        effect is "MoltenStrike" or "MoltenStrikeAltX" or "MoltenStrikeAltY" or "VaalMoltenStrike";

    public static ProjectileProfile Resolve(string effect, string metadata, Func<string, int> get, double speed, bool attack,
        bool arrows = false)
    {
        var p = GatewayProjectileDefaults.Create(metadata, get, speed, arrows) with
        {
            CannotReturn = CannotReturn.Contains(effect), ReturnOnDistance = effect != "FreezingPulse"
        };
        if (effect.StartsWith("PowerSiphon", StringComparison.Ordinal)
            && get("power_siphon_base_fire_at_x_targets") is > 0 and var siphonBase)
        {
            // virtual_total_number_of_projectiles starts with the gem's targets plus base projectiles.
            // The action's one-projectile override applies separately to each acquired enemy.
            p = p with { Count = Math.Max(1, siphonBase + get("base_number_of_projectiles")) };
        }
        if (IsBallLightningOrb(effect))
        {
            const int basePulseMilliseconds = 200;
            int rate = 100 + get(ProjectileSpeed.BallLightningRate);
            int pulseMilliseconds = basePulseMilliseconds * 100 / rate;
            p = p with
            {
                Lifetime = ProjectileRuntime.Time(BallLightningLifetimeSeconds),
                Legs = effect == "BallLightningAltX" ? _ => new OrbitingBallLightningLegs(speed) : null,
                DirectDamage = false, CannotFork = true, CannotChain = true, CannotSplit = true,
                Pulse = new("ball-lightning", 0, ProjectileRuntime.Time(pulseMilliseconds / 1000d))
                    { HitOnSpawn = false, AreaDamage = false, ShareIntervalAcrossUse = true }
            };
            if (effect == "BallLightningAltY")
                return p with { Stationary = true, Speed = 0, Range = 0, Count = 1, CannotReturn = true };
        }
        else if (IsKineticBlast(effect))
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
                        Interval = ProjectileRuntime.Time(ClusterIntervalSeconds),
                        UpdateTimer = true, PlacementAttempts = ClusterPlacementAttempts,
                        ExtraCountChancePercent = get("kinetic_blast_%_chance_for_additional_blast") },
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
        // Spark behaviour variation 4 selects tornado art and forgets contacts every 100 ms (native action).
        // An official server contact trace remains separate from this client-side repeat policy.
        else if (effect == "Twister")
            p = p with { HitRule = new(ProjectileHitScope.Projectile, ProjectileRepeat.Window, ProjectileRuntime.Time(.1)) };

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
        // Molten Strike's balls are lobbed onto sampled landing cells and deal area damage there without direct contacts,
        // piercing, forking, splitting or monster chaining (3.29.3c RE: 0x141b6ade0 / 0x141b6baa0, base radius 9).
        // Vaal Molten Strike / Wildfire enable ground-chaining along the sampled trajectory (0x141b6b710 / 0x141b6b790).
        else if (IsMoltenStrike(effect))
            p = p with
            {
                Lobbed = true, DirectDamage = false,
                CannotPierce = true, CannotFork = true, CannotSplit = true,
                CannotChain = get("molten_strike_projectiles_chain_when_impacting_ground") == 0,
                EndArea = new("molten-strike", 0),
                EndAreaReasons = new HashSet<ProjectileEndReason> { ProjectileEndReason.Landing, ProjectileEndReason.Terrain }
            };

        else if (effect == "VolcanicFissure")
            p = p with
            {
                Count = p.Count + Math.Max(0, get("volcanic_fissure_number_of_additional_projectiles")),
                Lobbed = true, DirectDamage = false,
                CannotPierce = true, CannotFork = true, CannotSplit = true, CannotChain = true, CannotReturn = true,
                EndArea = new("volcanic-fissure", 0),
                EndAreaReasons = new HashSet<ProjectileEndReason> { ProjectileEndReason.Landing }
            };
        else if (effect == "HolyFlameTotem")
            p = p with { Count = Math.Max(1, get("base_number_of_projectiles")), Pierce = -1, CannotReturn = true };

        // Cloudburst starts in Tornado Shot's secondary action. Its rain payload fires the skill's full arrow
        // count from the landing cell. Only ordinary Tornado Shot creates a separate group at each arrow's end.
        var payload = p;
        if (effect == "TornadoShotAltX")
            p = p with { Id = TornadoSecondaryArrow, Lifetime = TornadoSecondaryLifetime };
        if (get("projectiles_rain") != 0 || get("skill_is_rain_skill") != 0)
            p = ArrowNovaChildren(payload, p, effect, get);

        // These player stats belong to a particular active skill, including its transfigured variants.
        int extraProjectiles = effect switch
        {
            "Barrage" or "BarrageAltX" => get("barrage_num_of_additional_projectiles"),
            "SplitArrow" or "SplitArrowAltX" or "SplitArrowAltY" or "VaalSplitArrow"
                => get("split_arrow_num_of_additional_projectiles"),
            "Spark" or "SparkAltX" or "SparkAltY" or "VaalSpark"
                => get("spark_num_of_additional_projectiles") + get("spark_number_of_additional_projectiles"),
            "FreezingPulse" or "FreezingPulseAltX" or "FreezingPulseAltY" or "FreezingPulseRoyale"
                => get("freezing_pulse_number_of_additional_projectiles"),
            "FrostBolt" or "FrostBoltAltX" or "FrostBoltAltY" or "FrostBoltRoyale"
                => get("frostbolt_number_of_additional_projectiles"),
            _ => 0
        };
        int pierce = effect is "IceShot" or "IceShotAltX" or "IceShotAltY" or "IceShotRoyale" or "VaalIceShot"
            ? get("ice_shot_pierce_+") : 0;
        bool nova = effect switch
        {
            "Spark" or "SparkAltX" or "SparkAltY" or "VaalSpark" => get("spark_projectiles_nova") != 0,
            "EtherealKnives" or "EtherealKnivesAltX" or "EtherealKnivesAltY" or "EtherealKnivesRoyale"
                => get("ethereal_knives_projectiles_nova") != 0,
            _ => false
        };
        if (nova && get("support_parallel_projectile_number_of_points_per_side") == 0)
            p = p with { NativeVolley = new() { Nova = true } };
        p = GatewayProjectileModifiers.Resolve(p, get, attack, arrows, skillExtraProjectiles: extraProjectiles, skillPierce: pierce);
        // The Barrage action moves its endpoint bonus into the split result once when count modifiers apply to splitting.
        if (effect is ("Barrage" or "BarrageAltX") && get("modifiers_to_number_of_projectiles_instead_apply_to_splitting") != 0)
            p = p with { Split = p.Split + get("barrage_final_volley_fires_x_additional_projectiles_simultaneously") };
        if (IsMoltenStrike(effect))
        {
            bool groundChains = get("molten_strike_projectiles_chain_when_impacting_ground") != 0;
            int groundChainCount = groundChains ? Math.Max(0, p.Chain + get("molten_strike_chain_count_+")) : 0;
            p = p with
            {
                CannotChain = !groundChains,
                Chain = 0,
                GroundChains = groundChainCount,
                GroundBounceKeepsRange = groundChainCount > 0
            };
            int countFinal = get("threshold_jewel_molten_strike_damage_projectile_count_+%_final");
            if (countFinal != 0)
                p = p with { Count = Math.Max(1, (int)Math.Floor(p.Count * Math.Max(0, 1 + countFinal / 100.0))) };
        }
        // Its chains bounce on the ground instead of seeking another monster.
        if (effect == "RollingMagma") p = p with { GroundChains = p.Chain, Chain = 0 };
        // Spectral Throw pierces every enemy and flies back to the thrower, hitting again on the way back.
        if (effect == "SpectralThrow") p = p with { Pierce = -1, Returns = true };
        if (effect == "Twister") p = p with { Pierce = -1 };
        if (IsKineticBolt(effect)) p = WithKineticBoltLegs(p);
        if (effect is "TornadoShot" or "TornadoShotAltY")
        {
            // Arrow Nova releases the resolved primary arrows at the landing cell. Each of those arrows
            // still pierces and releases its own secondary group at the end of its flight.
            if (p.EndEmission is { } rain)
            {
                var primary = TornadoShotChildren(rain.Child, get);
                p = p with { EndEmission = rain with { Child = primary } };
            }
            else p = TornadoShotChildren(p, get);
        }
        // The endpoint cluster uses the same already-resolved quantity as each contact cluster.
        if (p.ContactArea is { } contact) p = p with { EndArea = contact };
        return ScaleAreas(p, effect, get, spell: !attack);
    }

    public static ProjectileProfile WithKineticBoltLegs(ProjectileProfile parent, ushort sequence = 0)
    {
        var secondary = KineticBoltSecondary(parent);
        return parent with
        {
            // Simultaneous fans use the volley index. Sequential Barrage contacts start at offset one.
            Legs = i => new KineticBoltLegs(sequence, i + (parent.SequentialDelay > 0 ? 1 : 0), parent.ZigZags,
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

    const string TornadoSecondaryArrow = "Metadata/Projectiles/TornadoShotSecondaryArrow";
    static readonly long TornadoSecondaryLifetime = ProjectileRuntime.Time(.3);

    // The client predicts the primary arrow and its terminal group itself (Frida: seven local and seven server
    // TornadoShotArrow objects for one use), so the server sends no object for either and only simulates the damage.
    // The group is three TornadoShotSecondaryArrow flights around the client's terminal RNG base ray, so both sides
    // fan out the same way (TornadoShotBaseDirection).
    static ProjectileProfile TornadoShotChildren(ProjectileProfile parent, Func<string, int> get)
    {
        int count = Math.Clamp(get("tornado_shot_num_of_secondary_projectiles") is > 0 and var n ? n : 3, 1, 256);
        var child = parent with
        {
            Id = TornadoSecondaryArrow,
            Count = count,
            NativeVolley = null,
            SpreadRadians = 0,
            AimSpreadDistance = 0,
            Range = SystemSettings.Default.ProjectileMaximumRangeCells,
            // Client trace: the secondary action's final callback caps at 300 ms, so projectile speed sets the range.
            Lifetime = TornadoSecondaryLifetime,
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
            {
                InheritForkOrChain = true,
                InheritReturnPhase = true
            }
        };
    }

    // The client's terminal RNG (0x1419c73c0) seeds [sequence, mother-derived byte, owner, repeats left,
    // secondary-release byte, 0, 0, 0]. The shared allocator assigns 2..255 in creation order, then restarts at 2.
    // The radial emitter then spaces children around that sampled base ray with no extra phase.
    public static ProjectileVector TornadoShotBaseDirection(ushort sequence, uint derived, uint owner, uint repeats)
    {
        var random = new AnimationVariantSelector.NativeRandom([sequence, derived, owner, repeats, 0, 0, 0, 0]);
        uint sample = TornadoBounded(ref random, 7001);
        float phi = (float)sample / 7000f * 6.283180236816406f;
        return new(MathF.Sin(phi), -MathF.Cos(phi));
    }

    static ProjectileVector TornadoShotBaseDirection(ProjectileEntity parent) =>
        TornadoShotBaseDirection(parent.Use.Sequence, DerivedByte(parent.SpawnOrdinal), parent.Use.Owner.Id,
            parent.Use.RepeatsLeft);

    internal static uint DerivedByte(int ordinal) => 2u + (uint)ordinal % 254;

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
    static ProjectileProfile ArrowNovaChildren(ProjectileProfile payload, ProjectileProfile arrows, string effect, Func<string, int> get)
    {
        bool nova = get("projectiles_nova") != 0;
        var sweep = nova && get("projectiles_barrage") != 0 ? PlayerArrowNovaSweep : null;
        // The Barrage actor issues one shot per contact. Cloudburst's secondary action always selects zero side points.
        var points = nova && sweep is null && effect != "TornadoShotAltX" ? Game.ParallelFiringPoints.From(get) : null;
        bool random = nova && get("random_projectile_direction") != 0;
        bool tornado = effect is "TornadoShot" or "TornadoShotAltY";
        var child = arrows with
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
            OrdinalReturnRoll = get("projectile_return_%_chance") is > 0 and < 100 ? Game.EmittedReturnRoll : null,
            ReturnOvershoot = Game.ClientProjectileRange(get),
            FormChange = null, SplitForm = null, SplitOnTerrain = false
        };
        return payload with
        {
            // One payload arrow: no hits, no contacts, and no server object (the official server sends none; the
            // parabola was ours). The rain rig and its attached arrow art carry the visual; the children stay hidden.
            Count = 1, CountConsumer = ProjectileCountConsumer.Children,
            NativeVolley = AimedRainPayload,
            DirectDamage = false, HideVisual = true, Lobbed = true, ArcHeight = 200,
            LandingDelay = ArrowNovaLayout.LandingDelay(0), CannotReturn = true,
            Pierce = 0, CannotPierce = true, Fork = 0, Chain = 0, Split = 0, CannotSplit = true,
            EndArea = null, EndAreaReasons = null, EndAreaOnReturnTurnaround = false,
            ContactArea = null, ContactTargets = null,
            EndEmission = new(child, Radial: true)
            {
                Delay = ProjectileRuntime.Time(.221),
                Sweep = sweep,
                FlightLayout = tornado || points is not null || random
                    ? ArrowNovaLayout.Create(get, sweep, points, tornado, random, tornado || Program.Skills.ProjectileUsesTargetLocation(arrows.Id))
                    : null
            }
        };
    }

    // Random directions affect the damaging arrows released from the selected rain cell.
    static readonly ProjectileNativeVolley AimedRainPayload = new() { Angles = Array.AsReadOnly<double>([0]) };

    // 3.29.3c: the Barrage rain action starts at the aim relative to the landing cell. At the aimed
    // cell this is +X. The player Positioned flags select the negative turn over 400 ms.
    static readonly ProjectileSweep PlayerArrowNovaSweep = new(ProjectileRuntime.Time(.4), -Math.Tau, payload =>
    {
        var landing = new ProjectileVector(Math.Floor(payload.Position.X + .5), Math.Floor(payload.Position.Y + .5));
        return (payload.Use.Aim - landing).Unit;
    });

    public static ProjectileProfile ScaleAreas(ProjectileProfile p, string effect, Func<string, int> get, bool spell)
    {
        int Value(string key, int fallback) => get(key) is > 0 and var value ? value : fallback;
        if (IsBallLightningOrb(effect) && p.Pulse is { } pulse)
            return p with { Pulse = pulse with
                { Radius = SkillArea.Radius(get("active_skill_base_area_of_effect_radius"), get, spell) } };
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
        if (IsMoltenStrike(effect) && p.EndArea is { } moltenArea)
        {
            int AreaStat(string name) => name == SkillArea.BaseIncreased
                ? get(name) + get("molten_strike_radius_+%") : get(name);
            int baseRadius = Value("active_skill_base_area_of_effect_radius", 9);
            return p with { EndArea = moltenArea with { Radius = SkillArea.Radius(baseRadius, AreaStat, spell) } };
        }
        if (p.EndArea is { } terminal)
            return p with { EndArea = terminal with
                { Radius = SkillArea.Radius(SkillArea.BaseRadius(effect, get), get, spell) } };
        return p;
    }
}
