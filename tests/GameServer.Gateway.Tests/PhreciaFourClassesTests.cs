using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public sealed class PhreciaFourClassesTests
{
    // Ancestral Commander, Paladin, Polytheist and Surfcaster: each is a Phrecia class of the catalog, played only with its
    // own opt-in, and its nodes' stats reach the player once the PhreciaAscendancies module owns the selection.
    // The family's choices are its module's own: the catalog has them only while a provider is registered.
    internal static AscendancyTree PhreciaTree() => ((IAscendancyFamilyPolicy)Activator.CreateInstance(
        typeof(GameServer.Modules.PhreciaAscendancies.PhreciaAscendanciesModule).Assembly
            .GetType("GameServer.Modules.PhreciaAscendancies.PhreciaFamilyPolicy", throwOnError: true)!, nonPublic: true)!).Tree;

    [Theory]
    [InlineData("Ancestral Commander", "Juggernaut", "Game_VERIFIED_ANCESTRAL_COMMANDER")]
    [InlineData("Paladin", "Champion", "Game_VERIFIED_PALADIN")]
    [InlineData("Polytheist", "Guardian", "Game_VERIFIED_POLYTHEIST")]
    [InlineData("Surfcaster", "Saboteur", "Game_VERIFIED_SURFCASTER")]
    public void Each_class_is_admitted_with_its_own_opt_in(string name, string id, string optIn)
    {
        var definition = Assert.Single(PhreciaFourClassesTests.PhreciaTree().Definitions,
            candidate => candidate.Name == name);
        Assert.Equal(id, definition.Identity.Id);
        var policy = (IAscendancyFamilyPolicy)Activator.CreateInstance(
            typeof(GameServer.Modules.PhreciaAscendancies.PhreciaAscendanciesModule).Assembly
                .GetType("GameServer.Modules.PhreciaAscendancies.PhreciaFamilyPolicy", throwOnError: true)!, nonPublic: true)!;
        string? previous = Environment.GetEnvironmentVariable(optIn);
        try
        {
            Environment.SetEnvironmentVariable(optIn, "0");
            Assert.False(policy.Supports(definition.Identity));
            Environment.SetEnvironmentVariable(optIn, "1");
            Assert.True(policy.Supports(definition.Identity));
            var node = definition.Nodes.Values.First(n => n.Stats.Count > 0);
            Assert.Equal(node.Stats, policy.NodeStats(definition, node.Id, node.Stats));
        }
        finally { Environment.SetEnvironmentVariable(optIn, previous); }
    }

    // "+# to Maximum Power and Frenzy Charges" is only this family's: its module gives the game the two maximums, and
    // the charge maximums come out as they did when the rules read the combined stat themselves.
    [Fact]
    public void The_Whisperers_combined_charge_maximum_reaches_the_rules_as_the_two_maximums()
    {
        const string optIn = "Game_VERIFIED_WHISPERER";
        var definition = Assert.Single(PhreciaTree().Definitions, candidate => candidate.Name == "Whisperer");
        var policy = (IAscendancyFamilyPolicy)Activator.CreateInstance(
            typeof(GameServer.Modules.PhreciaAscendancies.PhreciaAscendanciesModule).Assembly
                .GetType("GameServer.Modules.PhreciaAscendancies.PhreciaFamilyPolicy", throwOnError: true)!, nonPublic: true)!;
        string? previous = Environment.GetEnvironmentVariable(optIn);
        try
        {
            Environment.SetEnvironmentVariable(optIn, "1");
            var node = Assert.Single(definition.Nodes.Values, n => n.Stats.Any(s => s.Key == Stat.MaximumPowerAndFrenzyChargesPlus));
            int combined = node.Stats.Single(s => s.Key == Stat.MaximumPowerAndFrenzyChargesPlus).Value;
            Assert.True(combined > 0);
            var given = policy.NodeStats(definition, node.Id, node.Stats);
            Assert.DoesNotContain(given, s => s.Key == Stat.MaximumPowerAndFrenzyChargesPlus);
            Assert.Contains((Stat.MaxFrenzyCharges, combined), given);
            Assert.Contains((Stat.MaxPowerCharges, combined), given);
            // Every other stat of the node passes as it is, in its place.
            Assert.Equal(node.Stats.Where(s => s.Key != Stat.MaximumPowerAndFrenzyChargesPlus),
                given.Where(s => s.Key != Stat.MaxFrenzyCharges && s.Key != Stat.MaxPowerCharges));
            var sums = given.GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.Sum(s => s.Value));
            var maximums = ChargePanelStats.Compute(key => sums.GetValueOrDefault(key)).ToDictionary(r => r.Stat, r => r.Value);
            Assert.Equal(ChargePanelStats.BaseMaximumCharges + combined, maximums[Stat.VirtualMaximumFrenzyCharges]);
            Assert.Equal(ChargePanelStats.BaseMaximumCharges + combined, maximums[Stat.VirtualMaximumPowerCharges]);
            Assert.Equal(ChargePanelStats.BaseMaximumCharges, maximums[Stat.VirtualMaximumEnduranceCharges]);
        }
        finally { Environment.SetEnvironmentVariable(optIn, previous); }
    }

    // One action-speed bucket (main) with Changing Seasons and the base-speed ceiling (this PR): a reversed 20% chill
    // speeds the player up, the character's own action_speed_-% 10 still slows, and Juggernaut's
    // not_affected_by_action_speed_modifiers caps the result at base speed.
    [Fact]
    public void A_reversed_chill_and_the_characters_action_speed_share_one_bucket()
    {
        Func<uint, int> Stats(params (string Name, int Value)[] stats) =>
            name => stats.FirstOrDefault(s => Stat.Of(s.Name) == name).Value;
        double Factor(params (string, int)[] stats)
        {
            var ailments = new PlayerElementalAilments();
            var get = Stats(stats);
            ailments.Reconcile(get);
            ailments.Inflict(PlayerElementalAilments.Kind.Chill, 20, 2, 0, get, new Random(1));
            return ailments.TotalActionFactor(1, 10);
        }
        Assert.Equal(.70, Factor(), 6);
        Assert.Equal(1.10, Factor(("chill_effect_is_reversed", 1)), 6);
        Assert.Equal(1, Factor(("chill_effect_is_reversed", 1), ("not_affected_by_action_speed_modifiers", 1)), 6);
    }
}
