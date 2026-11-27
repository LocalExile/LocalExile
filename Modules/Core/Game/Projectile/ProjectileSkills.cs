using GameServer.Core.Projectiles;

public static partial class Game
{
    static partial void RegisterProjectileSkillDefinitions(Dictionary<string, ProjectileSkillDefinition> definitions,
        ref bool requireExplicitProfiles)
    {
        foreach (string effect in GatewayProjectileSkillProfiles.Effects)
            if (Skills.ProjectileFor(Skills.EffectRow(effect)) is { } projectile)
                definitions.Add(effect, SkillProjectileDefinition(effect, projectile.Id,
                    SkillProjectileParameters(effect, projectile.Id)));
        foreach (string effect in new[] { "ExplosiveArrow", "ExplosiveArrowAltX", "ExplosiveArrowAltY", "ExplosiveArrowRoyale" })
            definitions[effect] = SkillProjectileDefinition(effect, FuseArrowProjectile,
                SkillProjectileParameters(effect, FuseArrowProjectile));
        const string tetheringArrow = "Metadata/Projectiles/TetheringArrow";
        foreach (string effect in new[] { "EnsnaringArrow", "EnsnaringArrowAltX", "EnsnaringArrowAltY", "EnsnaringArrowRoyale" })
            definitions[effect] = SkillProjectileDefinition(effect, tetheringArrow, SkillProjectileParameters(effect, tetheringArrow));
        const string holyFlameProjectile = "Metadata/Projectiles/TotemHolyFireSpray";
        foreach (string effect in new[] { "HolyFlameTotem", "HolyFlameTotemAltX", "HolyFlameTotemRoyale" })
            definitions[effect] = SkillProjectileDefinition(effect, holyFlameProjectile,
                SkillProjectileParameters(effect, holyFlameProjectile));
        const string shrapnelBallista = "Metadata/Projectiles/CrossbowShotgunProjectile";
        definitions[SteelShrapnelBallistaEffect] = SkillProjectileDefinition(SteelShrapnelBallistaEffect, shrapnelBallista,
            SkillProjectileParameters(SteelShrapnelBallistaEffect, shrapnelBallista));
        const string frostBlade = "Metadata/Projectiles/IceStrikeProjectileNew";
        definitions["FrostBlades"] = SkillProjectileDefinition("FrostBlades", frostBlade,
            SkillProjectileParameters("FrostBlades", frostBlade));
        const string galvanicArrow = "Metadata/Projectiles/ShrapnelShot";
        foreach (string effect in new[] { "GalvanicArrow", "GalvanicArrowAltX", "GalvanicArrowAltY" })
            definitions[effect] = SkillProjectileDefinition(effect, galvanicArrow, SkillProjectileParameters(effect, galvanicArrow));
        const string ember = "Metadata/Projectiles/Ember";
        foreach (string effect in new[] { "BlazingSalvo", "BlazingSalvoRoyale" })
            definitions[effect] = SkillProjectileDefinition(effect, ember, SkillProjectileParameters(effect, ember));
        foreach (string effect in new[] { "BallLightning", "BallLightningAltX", "BallLightningRoyale" })
            definitions[effect] = SkillProjectileDefinition(effect, PlayerBallLightningProjectile,
                SkillProjectileParameters(effect, PlayerBallLightningProjectile));
        foreach (string effect in new[] { "ForbiddenRite", "ForbiddenRiteAltX" })
            definitions[effect] = SkillProjectileDefinition(effect, ForbiddenRitePrimary,
                SkillProjectileParameters(effect, ForbiddenRitePrimary));
        definitions[AvengingFlameEffect] = SkillProjectileDefinition(AvengingFlameEffect, InfernalBoltProjectile,
            SkillProjectileParameters(AvengingFlameEffect, InfernalBoltProjectile));
        RegisterSpectralShieldThrows(definitions);
        foreach (string effect in new[] { "VenomGyre", "VenomGyreAltX", "VenomGyreAltY", "VenomGyreRoyale",
                     "VenomGyreMercenary", VaalVenomGyreEffect })
            definitions[effect] = SkillProjectileDefinition(effect, VenomGyreProjectile,
                SkillProjectileParameters(effect, VenomGyreProjectile));
        const string spectralWeaponProjectile = "Metadata/Projectiles/ThrownWeapon";
        foreach (string effect in new[] { SpectralHelixEffect, SpectralHelixTrapEffect })
            definitions[effect] = SkillProjectileDefinition(effect, spectralWeaponProjectile,
                SkillProjectileParameters(effect, spectralWeaponProjectile));
        RegisterEyeOfWinter(definitions);
        const string boneLance = "Metadata/Projectiles/BoneLance";
        foreach (string effect in new[] { "Unearth", "UnearthRoyale" })
            definitions[effect] = SkillProjectileDefinition(effect, boneLance, SkillProjectileParameters(effect, boneLance));
        const string creepingFrost = "Metadata/Projectiles/ArcticBreath";
        foreach (string effect in new[] { "CreepingFrost", "CreepingFrostAltX", "CreepingFrostAltY", "CreepingFrostRoyale" })
            definitions[effect] = SkillProjectileDefinition(effect, creepingFrost, SkillProjectileParameters(effect, creepingFrost));
        const string coldMine = "Metadata/Projectiles/ColdProjectileMine";
        foreach (string effect in new[] { "IcicleMine", "IcicleMineAltX", "IcicleMineAltY" })
            definitions[effect] = SkillProjectileDefinition(effect, coldMine, SkillProjectileParameters(effect, coldMine));
        const string pyroclast = "Metadata/Projectiles/MortarBarrageMine";
        foreach (string effect in new[] { "PyroclastMine", "PyroclastMineAltX", "PyroclastMineAltY" })
            definitions[effect] = SkillProjectileDefinition(effect, pyroclast, SkillProjectileParameters(effect, pyroclast));
        const string fissureProjectile = "Metadata/Projectiles/VolcanicSlamMortar";
        definitions["VolcanicFissure"] = SkillProjectileDefinition("VolcanicFissure", fissureProjectile,
            SkillProjectileParameters("VolcanicFissure", fissureProjectile));
    }

    static partial void ConfigureCatalogProjectileDefinition(int effect, ref ProjectileSkillDefinition? definition)
    {
        if (definition is not null && Skills.EffectId(effect) is { } id)
            definition = SkillProjectileDefinition(id, definition.Metadata, definition.Parameters);
    }

    static partial void ConfigureCatalogProjectileParameters(int effect, string profileId,
        ref ProjectileSkillParameters parameters)
    {
        if (Skills.EffectId(effect) is { } id)
            parameters = SkillProjectileParameters(id, profileId);
    }

    // A bow or wand attack gem (skill_can_fire_arrows / skill_can_fire_wand_projectiles) whose effect names no projectile
    // row fires the held weapon's: the skill's own art where Projectiles.dat has it by weapon (FrenzyArrow / FrenzyWand,
    // PunctureArrow, PowerSiphonProjectile), else the default attack's DefaultArrow / WandProjectile. The weapon is the
    // one in hand at release (ArrowsFor); the gem's weapon restriction already refused any other.
    // Area, channelled, rain, melee and Vaal skills need their own shapes. Barrage shares the weapon projectile art
    // with ordinary shots and schedules its separate releases through the Barrage path.
    static ProjectileSkillDefinition? WeaponProjectileDefinition(int effect)
    {
        if (!Skills.IsAttack(effect) || !Skills.IsProjectile(effect) || Skills.EffectId(effect) is not { } id) return null;
        var stats = Skills.EffectStats(effect, 1);
        bool Has(string name) => Stats.Key(name) is uint key && stats.Any(s => s.Item1 == key && s.Item2 != 0);
        bool arrows = Has("skill_can_fire_arrows"), wand = Has("skill_can_fire_wand_projectiles");
        bool barrage = IsBarrageGem(id);
        if (!arrows && !wand || Has("projectiles_barrage") && !barrage) return null;
        string root = WeaponProjectileBase(id);
        foreach (int row in new[] { effect, Skills.EffectRow(root) })
            if (row >= 0 && new[] { "Area", "Channel", "Rain", "Melee", "Vaal" }.Any(type => Skills.HasType(row, type))) return null;
        string Row(string fallback, params string[] suffixes) => suffixes.Select(s => $"Metadata/Projectiles/{root}{s}")
            .FirstOrDefault(candidate => Skills.Projectile(candidate) is not null) ?? fallback;
        string bow = Row(OrdinaryRangedAttack.Arrow, "Arrow"), wandRow = Row(OrdinaryRangedAttack.Wand, "Wand", "Projectile");
        var parameters = new ProjectileSkillParameters((get, speed, fromBow) => GatewayProjectileSkillProfiles.Resolve(id,
            arrows && (fromBow || !wand) ? bow : wandRow, get, speed, attack: true, fromBow));
        var definition = SkillProjectileDefinition(id, arrows ? bow : wandRow, parameters);
        var specialize = definition.SpecializeRelease!;
        return definition with
        {
            CancelWindupOnNewAction = barrage || definition.CancelWindupOnNewAction,
            SpecializeRelease = (context, use) => AtNearbyTargets(context, specialize(context, use))
        };
    }

    // Power Siphon (power_siphon_base_fire_at_x_targets, projectiles_are_not_fired, number_of_projectiles_override 1):
    // one projectile at each of the nearest enemies in front of or beside the caster, up to the stat's count; with
    // none in range, one along the aim. Each aims at its enemy's cell at release.
    // shortcut: "nearby" is the projectile's flight range and "in front" the half-plane of the aim; neither is read
    // from the client.
    static ProjectileUse AtNearbyTargets(ProjectileReleaseContext context, ProjectileUse use)
    {
        int most = context.Get(Stat.PowerSiphonBaseFireAtXTargets);
        if (most <= 0) return use;
        var player = context.Player;
        double now = context.Session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        var caster = ProjectileSession.Vector(player.Walk.Position(now));
        var aim = use.Aim - caster;
        var targets = player.Area.Monsters.Values
            .Where(m => m.Hostile && !m.Dead && m.Life > 0 && m.OwnerId is null && !m.WorldHidden)
            .Select(m => ProjectileSession.Vector(m.Walk.Position(now)))
            .Where(at => (at - caster).Length <= use.Profile.Range && (at - caster).X * aim.X + (at - caster).Y * aim.Y >= 0)
            .OrderBy(at => (at - caster).Length).Take(most).Select(at => at - use.Origin).ToArray();
        if (targets.Length == 0) return use with { Profile = use.Profile with { Count = 1, NativeVolley = new() { Angles = [0] } } };
        double along = Math.Atan2(use.Aim.Y - use.Origin.Y, use.Aim.X - use.Origin.X);
        return use with { Profile = use.Profile with { Count = targets.Length,
            NativeVolley = new() { Angles = [.. targets.Select(to => Math.Atan2(to.Y, to.X) - along)] } } };
    }

    // A variant's base gem: Royale and transfigured effects share the base effect's skill shape and art.
    static string WeaponProjectileBase(string id)
    {
        foreach (string suffix in new[] { "Royale", "AltX", "AltY", "AltZ" })
            if (id.Length > suffix.Length && id.EndsWith(suffix, StringComparison.Ordinal)) return id[..^suffix.Length];
        return id;
    }

    static ProjectileSkillParameters SkillProjectileParameters(string effect, string profileId) =>
        new((get, speed, arrows) => GatewayProjectileSkillProfiles.Resolve(effect, profileId, get, speed,
            Skills.IsAttack(Skills.EffectRow(effect)), arrows))
        {
            PanelStats = (profile, stats) =>
            {
                if (GatewayProjectileSkillProfiles.IsKineticBlast(effect) && profile.ContactArea is { } area
                    && Stat.VirtualKinecticBlastNumberOfClusters is var key)
                    stats[key] = area.Count;
                if (IsSpectralHelix(effect)) stats[Stat.SpectralSpiralWeaponNumberOfBounces] = profile.TerrainBounces;
            }
        };

    static ProjectileSkillDefinition SkillProjectileDefinition(string effect, string metadata,
        ProjectileSkillParameters parameters) => new(metadata, parameters)
        {
            SpecializeRelease = (context, use) =>
            {
                // Player base increased area is already included in the panel total. Lay the skill's own
                // supported stats over the area-specific player snapshot, then scale each base radius once.
                var areaStats = SkillAreaStats(context.Player, SupportedSkill(context.Player, context.SkillId).Stats, context.Gem.Effect);
                int Get(uint id) => areaStats.GetValueOrDefault(id);
                use = use with { Profile = GatewayProjectileSkillProfiles.ScaleAreas(use.Profile, effect, Get,
                    !Skills.IsAttack(context.Gem.Effect)) };
                if (effect is "LancingSteel" or "LancingSteelAltX" or "ShatteringSteelAltX" or "SplittingSteelAltX"
                    or SteelShrapnelBallistaEffect)
                    return PrepareSteelRelease(context, use, effect);
                if (IsGalvanicArrow(effect)) return GalvanicArrowRelease(context, use, effect, areaStats);
                if (IsIceSpear(effect)) return IceSpearRelease(context, use);
                if (effect == "BallLightningAltX") return ReleaseOrbitingBallLightning(context, use);
                if (IsSpectralHelix(effect)) return ReleaseSpectralHelix(context, use);
                if (GatewayProjectileSkillProfiles.IsBlazingSalvo(effect)) return BlazingSalvoRelease(use, Get);
                if (effect == "SpectralShieldThrowAltY") return StationaryShieldRelease(context, use);
                if (effect is "IcicleMine" or "IcicleMineAltX" or "IcicleMineAltY")
                {
                    // Use the native shard rig's contact marker as the current flight expiry estimate.
                    var profile = NativeVolley(use.Profile, context.Get) with
                    {
                        Lifetime = ProjectileRuntime.Time(.32),
                    };
                    return use with { Profile = profile };
                }
                var shot = (ProjectileOffense)use.Payload!;
                if (effect is "Soulrend" or "SoulrendAltY") return SoulrendRelease(context, use, shot, Get);
                if (GatewayProjectileSkillProfiles.UnearthEffect(effect))
                {
                    int level = Math.Clamp(context.Own(Stat.UnearthBaseCorpseLevel)
                        + context.Get(Stat.UnearthAdditionalCorpseLevel), 1, 100);
                    var corpse = new UnearthCorpse((byte)level, context.Get(Stat.DesecrateMaximumNumberOfCorpses));
                    return use with { Payload = shot with { Unearth = corpse } };
                }
                if (effect is "RollingMagma" or "RollingMagmaAltX" or "RollingMagmaAltY" or "RollingMagmaRoyale"
                    && use.Profile.EndArea is { } magma)
                {
                    var profile = use.Profile;
                    if (Get(Stat.MagmaOrbSkillAreaOfEffectPlusPctPerBounce) is int area and not 0)
                    {
                        int baseRadius = SkillArea.BaseRadius(effect, Get);
                        double BounceRadius(int count) => SkillArea.Radius(baseRadius, key => key == SkillArea.Increase
                            ? ActorScale.Saturate((long)Get(key) + (long)count * area) : Get(key), spell: true);
                        profile = profile with { EndArea = magma with { RadiusForGroundBounce = BounceRadius } };
                    }
                    return use with { Profile = profile, Payload = shot with
                        { DamageMorePerGroundBounce = context.Get(Stat.ThresholdJewelMagmaOrbDamagePlusPctFinalPerChain) } };
                }
                if (GatewayProjectileSkillProfiles.IsCausticArrow(effect) && effect != "CausticArrowAltX")
                {
                    int radius = (int)(use.Profile.ContactArea?.Radius ?? 12);
                    double duration = SkillDurationModifiers.Seconds(context.Own(Stat.BaseSkillEffectDuration),
                        context.PlayerStat, k => k == Stat.SkillEffectDurationPlusPct
                            ? context.Own(k) + context.Get(Stat.CausticArrowDurationPlusPct) : context.Own(k));
                    var damageContext = new SkillDamageContext(SkillHitKind.Attack, shot.ActualCaster ?? CharacterPlayerStats(context.Player));
                    double dps = damageContext.Suppressed ? 0 : CausticGroundDps(context.Get, context.Own);
                    if (radius > 0 && duration > 0 && dps > 0)
                        shot = shot with { CausticCloud = new(radius, duration, dps, context.Get(Stat.GroundCausticArtVariation)) };
                    return use with { Payload = shot };
                }
                if (GatewayProjectileSkillProfiles.IsKineticBlast(effect))
                    return use with { Payload = shot with { AreaVisual = Art.AnimatedInPlaceOf(KineticBlastExplosionAo, context.Variation),
                        Presentation = new() { EffectLifetime = ProjectileRuntime.Time(.35) } } };
                if (IsEnsnaringArrow(effect)) return use with { Payload = shot with { Ensnare = CaptureEnsnare(context, Get) } };
                if (context.Own(Stat.EtherealKnivesBladeLeftInGroundForEveryXProjectiles) is > 0 and var every
                    && context.Own(Stat.MaximumNumberOfBladesLeftInGround) is > 0 and var maximum)
                {
                    if (context.NotHandCast || context.Get(Stat.IsNotHandCast) != 0)
                    {
                        // The native interval grows inversely with the number of blades left by proxies (0x1428affa0).
                        long factor = 100L + context.Get(Stat.BladesLeftInGroundPlusPctFinalIfNotHandCast);
                        every = factor > 0 ? (int)Math.Min(int.MaxValue, 100L * every / factor) : 0;
                    }
                    double duration = SkillDurationModifiers.Seconds(context.Own(Stat.BaseSkillEffectDuration),
                        context.PlayerStat, context.Own);
                    if (every > 0 && duration > 0)
                        return use with { Payload = shot with { GroundBlade = new(every, maximum, ProjectileRuntime.Time(duration)) } };
                }
                if (!GatewayProjectileSkillProfiles.IsFireball(metadata)) return use;
                string ao = Art.AnimatedInPlaceOf(DefaultProjectileImpactAo, context.Variation);
                return use with { Payload = shot with { Presentation = new()
                {
                    EffectLifetime = ProjectileRuntime.Time(1.5),
                    EventEffect = value => value.Kind == ProjectileEventKind.Area ? ao : null
                } } };
            }
        };

    static ProjectileUse BlazingSalvoRelease(ProjectileUse use, Func<uint, int> get)
    {
        var profile = use.Profile;
        if (profile.EndArea is not { } impact) return use;
        // The patch notes give the close and maximum-distance blast radii. Interpolate between those endpoints.
        double distance = (use.Aim - use.Origin).Length;
        int basis = (int)Math.Round(16 + 6 * Math.Clamp(distance / profile.Range, 0, 1));
        int radius = SkillArea.Radius(basis, get, spell: true);
        double spread = Math.Min(Math.Tau, profile.SpreadRadians
            * SkillArea.RadiusMultiplier(get(Stat.EmberProjectileSpreadAreaPlusPct)));
        return use with { Profile = profile with { EndArea = impact with { Radius = radius }, SpreadRadians = spread } };
    }
}
