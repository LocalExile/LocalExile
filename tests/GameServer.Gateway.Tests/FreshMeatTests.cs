namespace GameServer.Gateway.Tests;

public sealed class FreshMeatTests
{
    [Fact]
    public async Task RagingSpiritLosesWakenedFuryWhenItsNativeDurationEnds()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        fixture.Equip(("Summon Raging Spirit", 1), ("Fresh Meat Support", 1));
        uint id = await fixture.Summon("SummonRagingSpirit", 0);
        var spirit = player.Summons.Fighters[id];
        var attack = spirit.Abilities.First(ability => !ability.Attack.Spell && ability.Attack.Damage.MaxTotal > 0);
        double lifetime = player.Summons.Until[id] - spirit.SummonedAt;
        double ends = spirit.SummonedAt + lifetime * .1;
        var stats = Game.CharacterPlayerStats(player);
        var active = Game.GolemModifiers(player, spirit, stats, ends - .01);
        var expired = Game.GolemModifiers(player, spirit, stats, ends);

        Assert.Equal(ends, spirit.FreshMeatUntil, 6);
        Assert.True(Game.GolemCriticalChance(attack, active) > Game.GolemCriticalChance(attack, expired));
        Assert.Equal(50, active.Effects[Stat.BaseCriticalStrikeMultiplierPlus]
            - expired.Effects[Stat.BaseCriticalStrikeMultiplierPlus]);
    }
}
