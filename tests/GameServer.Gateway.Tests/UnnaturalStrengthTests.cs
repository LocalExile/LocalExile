namespace GameServer.Gateway.Tests;

// Unnatural Strength: the node's minion_has_unholy_might maps to the minion's own has_unholy_might, and the
// Unholy Might buff row's grants (full physical-to-chaos conversion, 25% to Wither on hit for 2 seconds) join
// the minion's mods here, so GolemAbilities' conversion and the minion hit path read them.
public sealed class UnnaturalStrengthTests
{
    [Fact]
    public void Minion_with_minion_has_unholy_might_gains_full_conversion_and_the_wither_chance()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        var golem = new GolemState.Golem(0xB0A1, null!, Game.GolemData.Find("SummonFlameGolem")!, 1);
        var plain = Game.GolemModifiers(player, golem, Game.CharacterPlayerStats(player));
        var unholy = Game.GolemModifiers(player, golem,
            new Dictionary<uint, int>(Game.CharacterPlayerStats(player)) { [Stat.MinionHasUnholyMight] = 1 });
        Assert.Equal(100, unholy.Effects[Stat.PhysicalDamagePctToConvertToChaos]
            - plain.Effects[Stat.PhysicalDamagePctToConvertToChaos]);
        Assert.Equal(25, unholy.Effects[Stat.WitheredOnHitFor2SecondsPctChance]
            - plain.Effects[Stat.WitheredOnHitFor2SecondsPctChance]);
    }
}
