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
    // Kept refused: a gem (or the base gem of its variant) that is an area, channelled, rain, melee or Vaal skill, or a
    // Barrage volley (projectiles_barrage). A plain shot would be the wrong shape for them; they need their own recipes.
    static ProjectileSkillDefinition? WeaponProjectileDefinition(int effect)
    {
        if (!Skills.IsAttack(effect) || !Skills.IsProjectile(effect) || Skills.EffectId(effect) is not { } id) return null;
        var stats = Skills.EffectStats(effect, 1);
        bool Has(string name) => Stats.Key(name) is uint key && stats.Any(s => s.Item1 == key && s.Item2 != 0);
        bool arrows = Has("skill_can_fire_arrows"), wand = Has("skill_can_fire_wand_projectiles");
        if (!arrows && !wand || Has("projectiles_barrage")) return null;
        string root = WeaponProjectileBase(id);
        foreach (int row in new[] { effect, Skills.EffectRow(root) })
            if (row >= 0 && new[] { "Area", "Channel", "Rain", "Melee", "Vaal" }.Any(type => Skills.HasType(row, type))) return null;
        string Row(string fallback, params string[] suffixes) => suffixes.Select(s => $"Metadata/Projectiles/{root}{s}")
            .FirstOrDefault(candidate => Skills.Projectile(candidate) is not null) ?? fallback;
        string bow = Row(OrdinaryRangedAttack.Arrow, "Arrow"), wandRow = Row(OrdinaryRangedAttack.Wand, "Wand", "Projectile");
        var parameters = new ProjectileSkillParameters((get, speed, fromBow) => GatewayProjectileSkillProfiles.Resolve(id,
            arrows && (fromBow || !wand) ? bow : wandRow, get, speed, attack: true, fromBow));
        var definition = SkillProjectileDefinition(id, arrows ? bow : wandRow, parameters);
        if (!Has("power_siphon_base_fire_at_x_targets")) return definition;
        var specialize = definition.SpecializeRelease!;
        return definition with { SpecializeRelease = (context, use) => AtNearbyTargets(context, specialize(context, use)) };
    }

    // PowerShot takes one ordinary projectile contact per visible target. A crowded search samples the
    // spatial buckets with the action RNG before writing the individual target cells.
    static ProjectileUse AtNearbyTargets(ProjectileReleaseContext context, ProjectileUse use)
    {
        var source = use.LaunchSource;
        double now = context.Session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        var candidates = new List<ProjectileVector>();
        foreach (var monster in context.Conn.Monsters.Values)
        {
            if (!monster.Hostile || monster.Dead || monster.Life == 0 || monster.OwnerId is not null || monster.WorldHidden
                || ArenaHazards.Untargetable(context.Conn, monster)) continue;
            var at = ProjectileSession.Vector(monster.Walk.Position(now));
            double reach = 70 + LivingCollision.Size(monster);
            var delta = at - source;
            if (delta.X * delta.X + delta.Y * delta.Y <= reach * reach && context.Session.TraceTerrain(at, source) is null)
                candidates.Add(at);
        }
        // An empty contact still consumes the attack and emits nothing.
        if (candidates.Count == 0) return use with { CanEmit = static () => false };

        // Native 23-cell buckets are visited row first. Inside a bucket the Gateway has the instance's object order.
        var targets = candidates.OrderBy(at => Math.Floor(at.Y / 23)).ThenBy(at => Math.Floor(at.X / 23)).ToArray();
        int count = Math.Min(targets.Length, use.Profile.Count);
        if (targets.Length > count)
        {
            var rng = new AnimationVariantSelector.NativeRandom([use.Sequence, 0, use.Emitter.Id, use.RepeatsLeft, 0, 0, 0, 0]);
            for (int i = 0; i < count; i++)
            {
                int pick = i + rng.Uniform(0, targets.Length - i - 1);
                (targets[i], targets[pick]) = (targets[pick], targets[i]);
            }
        }
        double along = Math.Atan2(use.Aim.Y - use.Origin.Y, use.Aim.X - use.Origin.X);
        var angles = new double[count];
        for (int i = 0; i < count; i++)
            angles[i] = Math.Atan2(targets[i].Y - use.Origin.Y, targets[i].X - use.Origin.X) - along;
        return use with { Profile = use.Profile with { Count = count, NativeVolley = new() { Angles = angles } } };
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
                    && Stats.Key("virtual_kinectic_blast_number_of_clusters") is uint key)
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
                var areaStats = SkillAreaStats(context.Conn, SupportedSkill(context.Conn, context.SkillId).Stats, context.Gem.Effect);
                int Get(string key) => Stats.Key(key) is uint id ? areaStats.GetValueOrDefault(id) : 0;
                use = use with { Profile = GatewayProjectileSkillProfiles.ScaleAreas(use.Profile, effect, Get,
                    !Skills.IsAttack(context.Gem.Effect)) };
                var shot = (ProjectileOffense)use.Payload!;
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
