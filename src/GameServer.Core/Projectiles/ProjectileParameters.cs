namespace GameServer.Core.Projectiles;

public enum ProjectileCountConsumer { Primary, ContactAreas, GroundChains, Children }

/// <summary>Typed modifier inputs, composed before emission. Prohibitions override grants independently.</summary>
public sealed record ProjectileModifiers
{
    public int ExtraProjectiles { get; init; }
    public int Pierce { get; init; }
    public int Fork { get; init; }
    public int Chain { get; init; }
    public int Split { get; init; }
    public bool Returns { get; init; }
    public bool CannotPierce { get; init; }
    public bool CannotFork { get; init; }
    public bool CannotChain { get; init; }
    public bool CannotSplit { get; init; }
    public bool CannotReturn { get; init; }
    public IReadOnlyList<string> ExclusiveConversions { get; init; } = [];
}

public static class ProjectileParameters
{
    public static ProjectileProfile Resolve(ProjectileProfile profile, ProjectileModifiers modifiers)
    {
        if (modifiers.ExclusiveConversions.Distinct(StringComparer.Ordinal).Skip(1).Any())
            throw new InvalidOperationException("Conflicting projectile conversions: "
                + string.Join(", ", modifiers.ExclusiveConversions.Order(StringComparer.Ordinal)));
        var p = profile with
        {
            Pierce = profile.Pierce < 0 || modifiers.Pierce < 0 ? -1 : profile.Pierce + modifiers.Pierce,
            Fork = Math.Max(0, profile.Fork + modifiers.Fork), Chain = Math.Max(0, profile.Chain + modifiers.Chain),
            Split = Math.Max(0, profile.Split + modifiers.Split), Returns = profile.Returns || modifiers.Returns,
            CannotPierce = profile.CannotPierce || modifiers.CannotPierce,
            CannotFork = profile.CannotFork || modifiers.CannotFork,
            CannotChain = profile.CannotChain || modifiers.CannotChain,
            CannotSplit = profile.CannotSplit || modifiers.CannotSplit,
            CannotReturn = profile.CannotReturn || modifiers.CannotReturn
        };
        int Count(int n) => Math.Clamp(n + modifiers.ExtraProjectiles, 1, 256);
        return profile.CountConsumer switch
        {
            ProjectileCountConsumer.Primary => p with { Count = Count(p.Count) },
            ProjectileCountConsumer.ContactAreas when p.ContactArea is { } area =>
                p with { ContactArea = area with { Count = Count(area.Count) } },
            ProjectileCountConsumer.GroundChains => p with { GroundChains = Math.Max(0,
                p.GroundChains + modifiers.ExtraProjectiles + modifiers.Chain), Chain = 0 },
            ProjectileCountConsumer.Children when p.EndEmission is { } emission =>
                p with { EndEmission = emission with { Child = emission.Child with { Count = Count(emission.Child.Count) } } },
            _ => throw new InvalidOperationException("Count consumer has no matching stage: " + profile.Id)
        };
    }
}
