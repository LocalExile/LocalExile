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
            "MoltenStrike", "MoltenStrikeAltX", "MoltenStrikeAltY", "VaalMoltenStrike", "HolyFlameTotem", "HolyFlameTotemAltX", "SpiritBurst" });

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
    internal static bool UnearthEffect(string? effect) => effect is "Unearth" or "UnearthRoyale";
    public static bool IsFireball(string metadata) => metadata.Contains("Fireball", StringComparison.Ordinal);
    public static bool IsLightningArrow(string effect) => effect is "LightningArrow" or "LightningArrowRoyale" or "VaalLightningArrow";
    internal static bool IsShatteringSteel(string? effect) => effect is "ShatteringSteel" or "ShatteringSteelAltX" or "ShatteringSteelRoyale";
    internal static bool IsHolyFlameTotem(string? effect) => effect is "HolyFlameTotem" or "HolyFlameTotemAltX";
    internal static bool IsForbiddenRite(string? effect) => effect is "ForbiddenRite" or "ForbiddenRiteAltX";
    public static bool IsMoltenStrike(string? effect) =>
        effect is "MoltenStrike" or "MoltenStrikeAltX" or "MoltenStrikeAltY" or "VaalMoltenStrike";
    public static bool IsCausticArrow(string? effect) =>
        effect is "CausticArrow" or "CausticArrowAltX" or "CausticArrowAltY" or "VaalCausticArrow";

    public static ProjectileProfile Resolve(string effect, string metadata, Func<uint, int> get, double speed, bool attack,
        bool arrows = false)
    {
        var p = GatewayProjectileDefaults.Create(metadata, get, speed, arrows) with
        {
            CannotReturn = CannotReturn.Contains(effect), ReturnOnDistance = effect != "FreezingPulse"
        };
        if (IsKineticBlast(effect))
        {
            bool clustering = effect == "KineticBlastAltX";
            int count = get(Stat.ClusterBurstSpawnAmount) is > 0 and var amount ? amount : clustering ? 3 : 4;
            int areaFinal = get(Stat.ActiveSkillAreaDamagePlusPctFinal);
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
                        ExtraCountChancePercent = get(Stat.KineticBlastPctChanceForAdditionalBlast) },
                EndAreaReasons = NonContactStops, EndAreaOnReturnTurnaround = true
            };
        }
        else if (IsFireball(metadata))
            p = p with { EndArea = new("fireball-explosion", 0), EndAreaReasons = TerminalStops };
        else if (Game.IsCreepingFrost(effect))
            p = p with { EndArea = new("creeping-frost-burst", 0), EndAreaReasons = TerminalStops };
        else if (effect == "BlazingSalvo")
            p = p with
            {
                Lobbed = true,
                LobbedEnemyContacts = true,
                // An angular fan grows in world size with distance instead of rescaling to a fixed target width.
                AimSpreadDistance = 0,
                EndArea = new("blazing-salvo", 16),
                EndAreaReasons = new HashSet<ProjectileEndReason>
                    { ProjectileEndReason.Enemy, ProjectileEndReason.Terrain, ProjectileEndReason.Landing },
            };
        else if (IsForbiddenRite(effect))
            p = p with
            {
                Lobbed = true, DirectDamage = false,
                CannotPierce = true, CannotFork = true, CannotSplit = true, CannotChain = true, CannotReturn = true,
                EndArea = new("forbidden-rite", 0),
                EndAreaReasons = new HashSet<ProjectileEndReason> { ProjectileEndReason.Landing, ProjectileEndReason.Terrain }
            };
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
        else if (effect is "RollingMagma" or "RollingMagmaAltX" or "RollingMagmaAltY" or "RollingMagmaRoyale")
            p = p with
            {
                Lobbed = true, DirectDamage = false, GroundBounceDistance = RollingMagmaBounceCells,
                EndArea = new("rolling-magma", 0), EndAreaReasons = new HashSet<ProjectileEndReason> { ProjectileEndReason.Landing }
            };
        else if (IsLightningArrow(effect) && get(Stat.LightningArrowMaximumNumberOfExtraTargets) is > 0 and var extra)
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
                CannotChain = get(Stat.MoltenStrikeProjectilesChainWhenImpactingGround) == 0,
                EndArea = new("molten-strike", 0),
                EndAreaReasons = new HashSet<ProjectileEndReason> { ProjectileEndReason.Landing, ProjectileEndReason.Terrain }
            };

        else if (Game.IsPyroclastMine(effect))
            p = p with
            {
                Lobbed = true, DirectDamage = false,
                CannotPierce = true, CannotFork = true, CannotSplit = true, CannotChain = true, CannotReturn = true,
                EndArea = new("pyroclast-mine", 0),
                EndAreaReasons = new HashSet<ProjectileEndReason> { ProjectileEndReason.Landing },
            };
        else if (effect == "VolcanicFissure")
            p = p with
            {
                Count = p.Count + Math.Max(0, get(Stat.VolcanicFissureNumberOfAdditionalProjectiles)),
                Lobbed = true, DirectDamage = false,
                CannotPierce = true, CannotFork = true, CannotSplit = true, CannotChain = true, CannotReturn = true,
                EndArea = new("volcanic-fissure", 0),
                EndAreaReasons = new HashSet<ProjectileEndReason> { ProjectileEndReason.Landing }
            };
        else if (UnearthEffect(effect))
            p = p with { Lobbed = true, LobbedEnemyContacts = true, CannotReturn = true };
        else if (IsHolyFlameTotem(effect))
            p = p with { Count = Math.Max(1, get(Stat.BaseNumberOfProjectiles)), Pierce = -1, CannotReturn = true };
        else if (IsCausticArrow(effect))
            p = p with
            {
                ContactArea = new("caustic-arrow-explosion", 0) { ExcludeContactTarget = true },
                EndAreaReasons = NonContactStops,
            };

        else if (IsShatteringSteel(effect))
            p = p with
            {
                EndArea = new("shattering-steel-cone", 0)
                {
                    ConeHalfAngle = Math.PI / 2,
                    ReplacesDirectContact = false,
                    ExcludeContactTarget = true,
                },
                EndAreaReasons = TerminalStops,
            };
        else if (effect is "LancingSteel" or "LancingSteelAltX")
            p = p with
            {
                SequentialDelay = ProjectileRuntime.Time(2d / 30),
                AimSpreadDistance = 0,
                SpreadRadians = 0,
            };

        // Arrow Nova's payload and circular volley. The child count keeps this stage's base count; the shared modifier
        // pass below adds the skill's and the support's extra arrows to it.
        if (get(Stat.ProjectilesRain) != 0 || get(Stat.SkillIsRainSkill) != 0) p = ArrowNovaChildren(p);

        // These player stats belong to a particular active skill, including its transfigured variants.
        int extraProjectiles = effect switch
        {
            "FreezingPulse" or "FreezingPulseAltX" or "FreezingPulseAltY" or "FreezingPulseRoyale"
                => get(Stat.FreezingPulseNumberOfAdditionalProjectiles),
            "FrostBolt" or "FrostBoltAltX" or "FrostBoltAltY" or "FrostBoltRoyale"
                => get(Stat.FrostboltNumberOfAdditionalProjectiles),
            "BlazingSalvo" => get(Stat.BlazingSalvoNumberOfAdditionalProjectiles),
            "ForbiddenRite" or "ForbiddenRiteAltX" => get(Stat.ForbiddenRiteNumberOfAdditionalProjectiles),
            _ => 0
        };
        int pierce = effect is "IceShot" or "IceShotAltX" or "IceShotAltY" or "IceShotRoyale" or "VaalIceShot"
            ? get(Stat.IceShotPiercePlus) : 0;
        bool nova = effect switch
        {
            "Spark" or "SparkAltX" or "SparkAltY" or "VaalSpark" => get(Stat.SparkProjectilesNova) != 0,
            "EtherealKnives" or "EtherealKnivesAltX" or "EtherealKnivesAltY" or "EtherealKnivesRoyale"
                => get(Stat.EtherealKnivesProjectilesNova) != 0,
            _ => false
        };
        if (nova && get(Stat.SupportParallelProjectileNumberOfPointsPerSide) == 0)
            p = p with { NativeVolley = new() { Nova = true } };
        p = GatewayProjectileModifiers.Resolve(p, get, attack, arrows, skillExtraProjectiles: extraProjectiles, skillPierce: pierce);
        if (IsShatteringSteel(effect) && p.ParallelOrigins is null
            && get(Stat.ActiveSkillAdditionalProjectilesFireParallelXDist) is > 0 and var parallelGap)
            p = p with
            {
                AimSpreadDistance = 0,
                SpreadRadians = 0,
                ParallelOrigins = new(p.Count / 2, parallelGap / WorldUnits.PerCell, 0),
            };
        if (IsMoltenStrike(effect))
        {
            bool groundChains = get(Stat.MoltenStrikeProjectilesChainWhenImpactingGround) != 0;
            int groundChainCount = groundChains ? Math.Max(0, p.Chain + get(Stat.MoltenStrikeChainCountPlus)) : 0;
            p = p with
            {
                CannotChain = !groundChains,
                Chain = 0,
                GroundChains = groundChainCount,
                GroundBounceKeepsRange = groundChainCount > 0
            };
            int countFinal = get(Stat.ThresholdJewelMoltenStrikeDamageProjectileCountPlusPctFinal);
            if (countFinal != 0)
                p = p with { Count = Math.Max(1, (int)Math.Floor(p.Count * Math.Max(0, 1 + countFinal / 100.0))) };
        }
        if (Game.IsCreepingFrost(effect) && get(Stat.ModifiersToProjectileCountDoNotApply) != 0)
            p = p with { Count = Math.Max(1, get(Stat.BaseNumberOfProjectiles)) };
        // Its chains bounce on the ground instead of seeking another monster.
        if (effect == "RollingMagma") p = p with { GroundChains = p.Chain, Chain = 0 };
        if (effect == "HolyFlameTotemAltX" && get(Stat.ModifiersToProjectileCountDoNotApply) != 0)
            p = p with { Count = Math.Max(1, get(Stat.BaseNumberOfProjectiles)) };
        // Spectral Throw pierces every enemy and flies back to the thrower, hitting again on the way back.
        if (effect == "SpectralThrow") p = p with { Pierce = -1, Returns = true };
        if (effect == "SpectralThrowAltX")
        {
            int repeatMs = get(Stat.SpectralThrowForgetHitListTimeOverride);
            p = p with
            {
                Returns = true,
                PierceAllOnReturn = true,
                OutboundHitLimit = 0,
                ReturnHitRule = repeatMs > 0
                    ? new(ProjectileHitScope.Projectile, ProjectileRepeat.Window, ProjectileRuntime.Time(repeatMs / 1000d)) : null
            };
        }
        if (effect == "Twister") p = p with { Pierce = -1 };
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
            // Native ZigZagProjectile (0x141a8ce97..0x141a8ced8) adds the volley index for all multi-projectile fans
            // (LMP/GMP/Kinetic Bolt of Fragmentation), not only sequential Barrage volleys.
            Legs = i => new KineticBoltLegs(sequence, i, parent.ZigZags,
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

    static bool IsTornadoShot(string effect) => effect is "TornadoShot" or "TornadoShotAltY";

    // The client predicts the primary arrow and its terminal group itself (Frida: seven local and seven server
    // TornadoShotArrow objects for one use), so the server sends no object for either and only simulates the damage.
    // The group is three TornadoShotSecondaryArrow flights around the client's terminal RNG base ray, so both sides
    // fan out the same way (TornadoShotBaseDirection).
    static ProjectileProfile TornadoShotChildren(ProjectileProfile parent, Func<uint, int> get)
    {
        int count = Math.Clamp(get(Stat.TornadoShotNumOfSecondaryProjectiles) is > 0 and var n ? n : 3, 1, 256);
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

    public static ProjectileProfile ScaleAreas(ProjectileProfile p, string effect, Func<uint, int> get, bool spell)
    {
        int Value(uint key, int fallback) => get(key) is > 0 and var value ? value : fallback;
        if (IsKineticBlast(effect) && p.ContactArea is { } contact)
        {
            bool clustering = effect == "KineticBlastAltX";
            int blast = SkillArea.Radius(Value(Stat.ActiveSkillBaseAreaOfEffectRadius, clustering ? 19 : 14), get, spell);
            int placement = SkillArea.Radius(Value(Stat.ActiveSkillBaseSecondaryAreaOfEffectRadius, clustering ? 28 : 20), get, spell);
            // Centers lie on the scaled placement circle; only the angle is sampled within each sector.
            var area = contact with { Radius = blast, Offset = placement, MinimumOffset = placement };
            return p with { ContactArea = area, EndArea = area };
        }
        if (IsLightningArrow(effect) && p.ContactTargets is { } targets)
            return p with { ContactTargets = targets with
                { Radius = SkillArea.Radius(Value(Stat.ActiveSkillBaseAreaOfEffectRadius, 18), get, spell) } };
        if (IsMoltenStrike(effect) && p.EndArea is { } moltenArea)
        {
            int AreaStat(uint name) => name == SkillArea.BaseIncreased
                ? get(name) + get(Stat.MoltenStrikeRadiusPlusPct) : get(name);
            int baseRadius = Value(Stat.ActiveSkillBaseAreaOfEffectRadius, 9);
            return p with { EndArea = moltenArea with { Radius = SkillArea.Radius(baseRadius, AreaStat, spell) } };
        }
        if (IsForbiddenRite(effect) && p.EndArea is { } riteArea)
            return p with { EndArea = riteArea with
                { Radius = SkillArea.Radius(Value(Stat.ActiveSkillBaseAreaOfEffectRadius, 8), get, spell) } };
        if (IsCausticArrow(effect) && p.ContactArea is { } causticArea)
        {
            int AreaStat(uint name) => name == SkillArea.BaseIncreased
                ? get(name) + get(Stat.CausticArrowRadiusPlusPct) : get(name);
            int baseRadius = Value(Stat.CausticArrowExplodeOnHitBaseAreaOfEffectRadius, 12);
            var scaled = causticArea with { Radius = SkillArea.Radius(baseRadius, AreaStat, spell) };
            return p with { ContactArea = scaled, EndArea = scaled };
        }
        if (Game.IsPyroclastMine(effect) && p.EndArea is { } pyroclast)
            return p with { EndArea = pyroclast with { Radius = SkillArea.Radius(12, get, spell) } };
        if (p.EndArea is { } terminal)
            return p with { EndArea = terminal with
                { Radius = SkillArea.Radius(SkillArea.BaseRadius(effect, get), get, spell) } };
        return p;
    }
}
