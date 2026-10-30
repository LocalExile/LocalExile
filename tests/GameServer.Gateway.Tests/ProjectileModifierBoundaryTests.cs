using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public class ProjectileModifierBoundaryTests
{
    [Fact]
    public void Common_baseline_allows_intrinsic_area_quantity_before_one_modifier_resolution()
    {
        var inputs = Stats(("number_of_additional_projectiles", 4),
            ("attacks_number_of_additional_projectiles", 2),
            ("modifiers_to_number_of_projectiles_instead_apply_to_splitting", 1), ("projectiles_return", 1));
        var baseline = GatewayProjectileDefaults.Create("synthetic", inputs, 100);
        var profile = GatewayProjectileModifiers.Resolve(baseline with
        {
            Count = 1, CannotSplit = true, CountConsumer = ProjectileCountConsumer.ContactAreas,
            ContactArea = new("intrinsic-area", 10, Count: 3)
        }, inputs, attack: true);
        var world = new ProjectileRuntimeProbe(); world.Target(1, 40, 10);
        world.Release(profile); world.Advance(.4);
        Assert.Equal(9, world.Events.Count(e => e.Kind == ProjectileEventKind.Area));
        Assert.Equal(0, profile.Split);
        Assert.True(profile.Returns);
        Assert.Equal(40, profile.AimSpreadDistance);
        Assert.Equal(150, profile.ReturnOvershoot);
        Assert.True(profile.ExcludeTurnaroundTarget);
        Assert.True(profile.SkipDeniedContacts); Assert.True(profile.SkipDeniedReturnContacts);
        Assert.Equal(ProjectileHitScope.Emission, profile.HitRule.Scope);
    }

    static ProjectileProfile Resolve(ProjectileProfile profile, Func<string, int> get, bool attack = false,
        bool chainInputConsumed = false, bool intrinsicSplitCount = false) =>
        GatewayProjectileModifiers.Resolve(profile, get, attack, chainInputConsumed, intrinsicSplitCount);

    static Func<string, int> Stats(params (string Name, int Value)[] values)
    {
        var stats = values.ToDictionary(v => v.Name, v => v.Value);
        return name => stats.GetValueOrDefault(name);
    }

}
