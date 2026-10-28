using GameServer.Core.Projectiles;

partial class Program
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
        public uint? SecondFormClip { get; init; }
        public Func<ProjectileEvent, string?>? EventEffect { get; init; }
        public long EffectLifetime { get; init; } = 60000;
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
        ?? (p.Use.Payload is ProjectileOffense shot && Skills.EffectId(shot.Effect) is { } effect
            ? ProjectileDefinition(effect)?.Presentation?.Invoke(p) : null)
        ?? new();
}
