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

    [Fact]
    public void Default_volley_skips_denied_contacts_so_a_sibling_can_reach_the_far_target()
    {
        var profile = GatewayProjectileDefaults.Resolve("synthetic", Stats(("number_of_additional_projectiles", 1)),
            100, attack: true) with { SpreadRadians = 0, Radius = 1 };
        var world = new ProjectileRuntimeProbe(); world.Target(2, 20, 0); world.Target(3, 60, 0);
        var runtime = new ProjectileRuntime(world);
        runtime.Release(new(profile, new(1), new(1), 1, new(0, 0), new(100, 0)));
        runtime.AdvanceTo(120000);
        Assert.Equal(new uint[] { 2, 3 }, world.Attempts.Select(h => h.Target.Id));
    }

    [Fact]
    public void Default_return_excludes_the_turnaround_victim_and_passes_the_caster()
    {
        var profile = GatewayProjectileDefaults.Resolve("synthetic", Stats(("projectiles_return", 1)),
            100, attack: true) with { Radius = 1 };
        var world = new ProjectileRuntimeProbe(); world.Target(2, 30, 0);
        world.Targets.Add(new(new(1), 1, _ => new(0, 0), 0));
        var runtime = new ProjectileRuntime(world);
        runtime.Release(new(profile, new(1), new(1), 1, new(0, 0), new(100, 0)) { ReturnAnchor = new(1) });
        runtime.AdvanceTo(300000);
        Assert.Single(world.Attempts);
        Assert.Equal(-150, Assert.Single(world.Events, e => e.Kind == ProjectileEventKind.End).Position.X, 6);
    }

    [Fact]
    public void Boolean_fork_sources_establish_one_generation_and_only_additional_counts_extend_it()
    {
        foreach (bool attack in new[] { false, true })
        {
            var inputs = Stats(("projectiles_fork", 1), ("virtual_projectiles_fork", 3),
                ("attack_projectiles_fork", 1), ("projectiles_from_spells_fork", 1), ("arrows_fork", 1));
            Assert.Equal(1, Resolve(new("synthetic"), inputs, attack).Fork);
            Assert.Equal(3, Resolve(new("synthetic"), Stats(("projectiles_fork", 1),
                ("number_of_additional_forks_base", 2)), attack).Fork);
            Assert.Equal(4, Resolve(new("synthetic"), Stats(("projectiles_fork", 1),
                ("number_of_additional_forks_base", 2), ("virtual_number_of_forks_for_projectiles_final", 4)), attack).Fork);
        }
    }

    [Fact]
    public void Raw_bow_always_pierce_grant_applies_to_attacks_only()
    {
        var inputs = Stats(("base_arrows_always_pierce", 1));
        Assert.Equal(-1, Resolve(new("synthetic"), inputs, attack: true).Pierce);
        Assert.Equal(0, Resolve(new("synthetic"), inputs).Pierce);
    }

    [Fact]
    public void Generic_binding_resolves_a_synthetic_profile_without_a_skill_recipe()
    {
        var profile = Resolve(new("synthetic") { Count = 2 }, Stats(("number_of_additional_projectiles", 4)));
        Assert.Equal(6, profile.Count);
    }

    [Theory]
    [InlineData(false, 8, 4, 3)]
    [InlineData(true, 7, 9, 4)]
    public void Raw_and_virtual_aliases_use_the_larger_grant_and_attack_inputs_stay_scoped(
        bool attack, int count, int pierce, int forks)
    {
        var profile = Resolve(new("synthetic") { Count = 2, Pierce = 1, Fork = 1, Chain = 1, Split = 1 }, Stats(
            ("number_of_additional_projectiles", 2), ("attacks_number_of_additional_projectiles", 3),
            ("spells_number_of_additional_projectiles", 4), ("projectile_number_of_targets_to_pierce", 2),
            ("projectile_base_number_of_targets_to_pierce", 3), ("arrow_number_of_targets_to_pierce", 4),
            ("arrow_base_number_of_targets_to_pierce", 5), ("projectiles_fork", 1), ("virtual_projectiles_fork", 2),
            ("number_of_additional_forks_base", 1), ("attack_projectiles_fork", 2), ("arrows_fork", 3),
            ("attack_projectiles_fork_additional_times", 1), ("projectiles_from_spells_fork", 4),
            ("number_of_chains", 2), ("virtual_number_of_chains_for_projectiles", 3),
            ("projectile_number_to_split", 2), ("projectile_number_of_targets_to_split_to", 4),
            ("intermediary_projectile_number_of_targets_to_split_to", 3)), attack);
        Assert.Equal(count, profile.Count);
        Assert.Equal(pierce, profile.Pierce);
        Assert.Equal(forks, profile.Fork);
        Assert.Equal(4, profile.Chain);
        Assert.Equal(5, profile.Split);
    }

    [Theory]
    [InlineData("always_pierce", false)]
    [InlineData("virtual_always_pierce", false)]
    [InlineData("arrows_always_pierce", true)]
    public void Unlimited_pierce_is_preserved(string stat, bool attack)
    {
        Assert.Equal(-1, Resolve(new("synthetic") { Pierce = 2 }, Stats((stat, 1)), attack).Pierce);
        Assert.Equal(-1, Resolve(new("synthetic") { Pierce = -1 }, Stats()).Pierce);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prohibitions_survive_together_with_grants_for_runtime_precedence(bool attack)
    {
        var profile = Resolve(new("synthetic") { Pierce = 2, Fork = 2, Chain = 2, Split = 2, Returns = true }, Stats(
            ("cannot_pierce", 1), ("virtual_projectiles_cannot_fork", 1), ("virtual_projectiles_cannot_chain", 1),
            ("projectiles_cannot_split", 1), ("skill_projectiles_cannot_return", 1)), attack);
        Assert.True(profile.CannotPierce);
        Assert.True(profile.CannotFork);
        Assert.True(profile.CannotChain);
        Assert.True(profile.CannotSplit);
        Assert.True(profile.CannotReturn);
        Assert.Equal(2, profile.Pierce);
        Assert.Equal(2, profile.Fork);
        Assert.Equal(2, profile.Chain);
        Assert.Equal(2, profile.Split);
        Assert.True(profile.Returns);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Spell_prohibitions_do_not_restrict_attacks(bool attack, bool prohibited)
    {
        var profile = Resolve(new("synthetic"), Stats(("projectiles_from_spells_cannot_pierce", 1),
            ("projectiles_from_spells_cannot_fork", 1), ("projectiles_from_spells_cannot_chain", 1)), attack);
        Assert.Equal(prohibited, profile.CannotPierce);
        Assert.Equal(prohibited, profile.CannotFork);
        Assert.Equal(prohibited, profile.CannotChain);
    }

    [Theory]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(150, true)]
    public void Return_chance_grants_only_guaranteed_returns(int chance, bool returns)
    {
        Assert.Equal(returns, Resolve(new("synthetic"), Stats(("projectile_return_%_chance", chance))).Returns);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Attack_return_grants_stay_scoped(bool attack, bool returns)
    {
        Assert.Equal(returns, Resolve(new("synthetic"), Stats(("attack_projectiles_return", 1)), attack).Returns);
        Assert.True(Resolve(new("synthetic"), Stats(("projectiles_return", 1)), attack).Returns);
    }

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 2)]
    public void Consumed_chain_input_is_not_applied_again(bool consumed, int chains)
    {
        var profile = Resolve(new("synthetic") { Chain = 2, GroundChains = 4 },
            Stats(("number_of_chains", 3)), chainInputConsumed: consumed);
        Assert.Equal(chains, profile.Chain);
        Assert.Equal(4, profile.GroundChains);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generic_split_conversion_adds_extra_count_once_and_keeps_existing_split_grants(bool cannotSplit)
    {
        var profile = Resolve(new("synthetic") { Count = 3, Split = 2, CannotSplit = cannotSplit }, Stats(
            ("number_of_additional_projectiles", 4), ("projectile_number_to_split", 3),
            ("modifiers_to_number_of_projectiles_instead_apply_to_splitting", 1)));
        Assert.Equal(1, profile.Count);
        Assert.Equal(9, profile.Split);
        Assert.Equal(cannotSplit, profile.CannotSplit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Intrinsic_split_count_consumes_extra_once_even_when_generic_conversion_is_enabled(int conversion)
    {
        var profile = Resolve(new("synthetic") { Split = 3 }, Stats(("number_of_additional_projectiles", 4),
            ("projectile_number_to_split", 3),
            ("modifiers_to_number_of_projectiles_instead_apply_to_splitting", conversion)), intrinsicSplitCount: true);
        Assert.Equal(1, profile.Count);
        Assert.Equal(7, profile.Split);
    }

    [Theory]
    [InlineData(ProjectileCountConsumer.ContactAreas)]
    [InlineData(ProjectileCountConsumer.GroundChains)]
    [InlineData(ProjectileCountConsumer.Children)]
    public void Competing_count_conversions_are_rejected(ProjectileCountConsumer consumer)
    {
        Assert.Throws<InvalidOperationException>(() => Resolve(new("synthetic") { CountConsumer = consumer },
            Stats(("modifiers_to_number_of_projectiles_instead_apply_to_splitting", 1))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Intrinsic_area_count_keeps_extra_projectiles_when_competing_split_is_prohibited(bool profileProhibition)
    {
        var profile = Resolve(new("synthetic")
        {
            CountConsumer = ProjectileCountConsumer.ContactAreas,
            ContactArea = new("clusters", 5, Count: 3),
            CannotSplit = profileProhibition
        }, Stats(("number_of_additional_projectiles", 4),
            ("projectiles_cannot_split", profileProhibition ? 0 : 1),
            ("modifiers_to_number_of_projectiles_instead_apply_to_splitting", 1)));
        Assert.Equal(1, profile.Count);
        Assert.Equal(7, profile.ContactArea!.Count);
        Assert.Equal(0, profile.Split);
        Assert.True(profile.CannotSplit);
    }

    [Fact]
    public void Children_inherit_continuation_modifiers_without_expanding_their_count_twice()
    {
        var profile = Resolve(new("synthetic")
        {
            CountConsumer = ProjectileCountConsumer.Children,
            EndEmission = new(new("end-child") { Count = 3 }),
            TravelEmission = new(new("travel-child") { Count = 1 })
        }, Stats(("number_of_additional_projectiles", 4), ("projectile_number_of_targets_to_pierce", 2),
            ("number_of_chains", 3), ("projectiles_return", 1), ("skill_projectiles_cannot_return", 1)));
        Assert.Equal(1, profile.Count);
        Assert.Equal(7, profile.EndEmission!.Child.Count);
        Assert.Equal(1, profile.TravelEmission!.Child.Count);
        foreach (var child in new[] { profile.EndEmission.Child, profile.TravelEmission.Child })
        {
            Assert.Equal(2, child.Pierce);
            Assert.Equal(3, child.Chain);
            Assert.True(child.Returns);
            Assert.True(child.CannotReturn);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Distance_override_rejects_unadopted_early_end_transitions(bool lobbed)
    {
        var recipe = new ProjectileProfile("synthetic")
            { Lobbed = lobbed, EndEmission = lobbed ? null : new(new("child")) };
        Assert.Throws<InvalidOperationException>(() => Resolve(recipe, Stats(("projectile_distance_override", 40))));
    }

    [Fact]
    public void Distance_override_updates_an_ordinary_profile()
    {
        Assert.Equal(40, Resolve(new("synthetic"), Stats(("projectile_distance_override", 40))).Range);
    }
}
