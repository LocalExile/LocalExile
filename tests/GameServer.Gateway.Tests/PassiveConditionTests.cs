namespace GameServer.Gateway.Tests;

/// <summary>Issue #1102: the tree's conditional stats and the keystones that ride on the crit policy.</summary>
public sealed class PassiveConditionTests
{
    [Fact]
    public void A_conditional_passive_stat_counts_only_while_its_condition_holds()
    {
        uint conditional = Program.Stats.Key("physical_damage_while_dual_wielding_+%")!.Value;
        uint plain = Program.Stats.Key("physical_damage_+%")!.Value;
        var passives = new Dictionary<uint, int> { [conditional] = 25 };

        // Dual wielding: the stat moves onto the plain name every consumer already reads.
        var holding = Game.FoldPassiveConditions(passives, new StatConditions.Subject(DualWield: true));
        Assert.Equal(25, holding.GetValueOrDefault(plain));
        Assert.False(holding.ContainsKey(conditional));

        // One weapon only: the stat does not count at all.
        Assert.Empty(Game.FoldPassiveConditions(passives, new StatConditions.Subject(DualWield: false)));

        // A condition this build cannot answer keeps the stat as it was, never dropping or moving it.
        var unknown = Game.FoldPassiveConditions(passives, StatConditions.Subject.Unknown);
        Assert.Equal(25, unknown.GetValueOrDefault(conditional));
    }

    [Fact]
    public void Resolute_and_precise_technique_never_crit_and_resolute_cannot_be_evaded()
    {
        using var f = new MinionUtilityTests.Fixture();
        var resolute = new Dictionary<uint, int> { [Program.Stats.Key("resolute_technique")!.Value] = 1 };
        Assert.True(Game.CriticalProhibitedBy(resolute, attack: true, own: null));
        Assert.False(Game.CriticalProhibitedBy(resolute, attack: false, own: null));
        Assert.True(AttackAccuracyProfile.From(f.Conn.Pools, resolute, Program.Stats, own: null, local: null)
            .CannotBeEvaded);

        var precise = new Dictionary<uint, int> { [Program.Stats.Key("keystone_precise_technique")!.Value] = 1 };
        Assert.True(Game.CriticalProhibitedBy(precise, attack: true, own: null));

        foreach (string keystone in new[] { "keystone_elemental_overload", "keystone_ailment_crit" })
            Assert.True(Game.CriticalHitsDealNoExtraDamage(
                new Dictionary<uint, int> { [Program.Stats.Key(keystone)!.Value] = 1 }));
        Assert.False(Game.CriticalHitsDealNoExtraDamage(new Dictionary<uint, int>()));
    }
}
