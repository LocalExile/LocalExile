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
        const string holyFlameProjectile = "Metadata/Projectiles/TotemHolyFireSpray";
        definitions["HolyFlameTotem"] = SkillProjectileDefinition("HolyFlameTotem", holyFlameProjectile,
            SkillProjectileParameters("HolyFlameTotem", holyFlameProjectile));
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
                var areaStats = AreaSkillStats(context.Player, context.SkillId, SupportedSkill(context.Player, context.SkillId));
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
