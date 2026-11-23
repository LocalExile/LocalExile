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
        foreach (string effect in new[] { "HolyFlameTotem", "HolyFlameTotemAltX" })
            definitions[effect] = SkillProjectileDefinition(effect, holyFlameProjectile,
                SkillProjectileParameters(effect, holyFlameProjectile));
        const string frostBlade = "Metadata/Projectiles/IceStrikeProjectileNew";
        definitions["FrostBlades"] = SkillProjectileDefinition("FrostBlades", frostBlade,
            SkillProjectileParameters("FrostBlades", frostBlade));
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
                var shot = (ProjectileOffense)use.Payload!;
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
                if (!GatewayProjectileSkillProfiles.IsFireball(metadata)) return use;
                string ao = Art.AnimatedInPlaceOf(DefaultProjectileImpactAo, context.Variation);
                return use with { Payload = shot with { Presentation = new()
                {
                    EffectLifetime = ProjectileRuntime.Time(1.5),
                    EventEffect = value => value.Kind == ProjectileEventKind.Area ? ao : null
                } } };
            }
        };
}
