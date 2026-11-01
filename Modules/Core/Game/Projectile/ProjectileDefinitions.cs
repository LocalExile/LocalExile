using GameServer.Core.Projectiles;

static partial class Game
{
    // Parameter rules do not require native projectile metadata or an executable release path.
    internal sealed record ProjectileSkillParameters(
        Func<Func<string, int>, double, ProjectileProfile> Resolve)
    {
        public Action<ProjectileProfile, Dictionary<uint, int>>? PanelStats { get; init; }
    }

    // Executable definitions add resources and release/presentation policy to the shared parameter rules.
    internal sealed record ProjectileSkillDefinition(string Metadata, ProjectileSkillParameters Parameters)
    {
        public bool RequiresTerrain { get; init; }
        public bool CancelWindupOnNewAction { get; init; }
        public Func<ClientState, string?>? Unavailable { get; init; }
        public Func<ProjectileReleaseContext, ProjectileUse, ProjectileUse>? SpecializeRelease { get; init; }
        public Func<ProjectileEntity, ProjectilePresentation>? Presentation { get; init; }
    }

    internal sealed record ProjectileReleaseContext(ClientState Conn, ProjectileSession Session, ushort SkillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Gem,
        int Variation, Func<string, int> Get, Func<string, int> Player, Func<string, int> Own);

    internal sealed record ProjectilePresentation
    {
        public string? FlightVisual { get; init; }
        public uint? FlightClip { get; init; }
        /// <summary>The flight clip's playback speed: a once-only flight clip is stretched over the flight
        /// (the mortar's 1 s <c>proj</c> over its 0.3 s arc, as the client scales its own mortars).</summary>
        public float FlightClipSpeed { get; init; } = 1;
        public uint? SecondFormClip { get; init; }
        public Func<ProjectileEvent, string?>? EventEffect { get; init; }
        public long EffectLifetime { get; init; } = 60000;
        /// <summary>A clip the projectile's own rig plays where a contact stops it (an ordinary wand bolt's impact, with its
        /// sound and particles); the object then stays for <see cref="ImpactLifetime"/> quanta of game time.</summary>
        public uint? ImpactClip { get; init; }
        public long ImpactLifetime { get; init; } = 60000;
        /// <summary>Keep the stopped object for <see cref="ImpactLifetime"/> without a clip: an arrow left in its target
        /// or a wall (DefaultArrow has stuck/bounce art the client plays only for its own arrows).</summary>
        public bool StayOnImpact { get; init; }
        /// <summary>The release point's height above the ground in marker steps, from the releasing clip's contact
        /// keyframe (<see cref="ContactPoint.HeightSteps"/>: the Ranger's bow 16, the Sand Spitter's spit 9); null keeps
        /// <c>Game_PROJECTILE_HEIGHT</c> (<see cref="ProjectileHeight.DefaultSteps"/>).</summary>
        public int? LaunchHeight { get; init; }
    }

    static partial void RegisterProjectileSkillDefinitions(Dictionary<string, ProjectileSkillDefinition> definitions,
        ref bool requireExplicitProfiles);

    static partial void ConfigureCatalogProjectileDefinition(int effect, ref ProjectileSkillDefinition? definition);

    static partial void ConfigureCatalogProjectileParameters(int effect, string profileId,
        ref ProjectileSkillParameters parameters);

    static readonly (IReadOnlyDictionary<string, ProjectileSkillDefinition> Definitions, bool RequireExplicitProfiles) ProjectileDefinitions
        = BuildProjectileDefinitions();

    static (IReadOnlyDictionary<string, ProjectileSkillDefinition>, bool) BuildProjectileDefinitions()
    {
        var definitions = new Dictionary<string, ProjectileSkillDefinition>(StringComparer.Ordinal);
        bool requireExplicit = false;
        RegisterProjectileSkillDefinitions(definitions, ref requireExplicit);
        return (definitions, requireExplicit);
    }

    internal static ProjectileSkillDefinition? ProjectileDefinition(string effect) =>
        ProjectileDefinitions.Definitions.GetValueOrDefault(effect);

    // Catalog metadata permits only the basic approximation. Providers can require explicit recipes;
    // that is an admission policy, never a choice of simulator or production entry point.
    internal static ProjectileSkillDefinition? CatalogProjectileDefinition(int effect)
    {
        if (!Skills.IsProjectile(effect) || Skills.ProjectileFor(effect) is not { } projectile) return null;
        ProjectileSkillDefinition? definition = new(projectile.Id, CatalogProjectileParameters(effect, projectile.Id));
        ConfigureCatalogProjectileDefinition(effect, ref definition);
        return definition;
    }

    static ProjectileSkillDefinition? ResolveProjectileDefinition(int effect) =>
        ProjectileDefinition(Skills.EffectId(effect) ?? "")
        ?? (ProjectileDefinitions.RequireExplicitProfiles ? null : CatalogProjectileDefinition(effect));

    static ProjectileSkillParameters CatalogProjectileParameters(int effect, string profileId)
    {
        var parameters = new ProjectileSkillParameters((get, speed) =>
            GatewayProjectileDefaults.Resolve(profileId, get, speed, Skills.IsAttack(effect)));
        ConfigureCatalogProjectileParameters(effect, profileId, ref parameters);
        return parameters;
    }

    // Missing native art must not hide known stats or turn a panel calculation into an executable recipe.
    static ProjectileSkillParameters? ResolveProjectilePanelParameters(int effect) =>
        ResolveProjectileDefinition(effect)?.Parameters
        ?? (Skills.IsProjectile(effect) && Skills.EffectId(effect) is { } id
            ? CatalogProjectileParameters(effect, id) : null);

    static ProjectilePresentation PresentationFor(ProjectileEntity p) =>
        (p.Use.Payload as ProjectileOffense)?.Presentation
        ?? (p.Use.Payload as HostileProjectileOffense)?.Presentation
        ?? (p.Use.Payload is ProjectileOffense shot && Skills.EffectId(shot.Effect) is { } effect
            ? ProjectileDefinition(effect)?.Presentation?.Invoke(p) : null)
        ?? new();
}
