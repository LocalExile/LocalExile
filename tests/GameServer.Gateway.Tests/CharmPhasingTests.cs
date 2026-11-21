namespace GameServer.Gateway.Tests;

public sealed class CharmPhasingTests
{
    [Fact]
    public void Timed_phasing_answers_the_while_phasing_condition()
    {
        var player = new Player(new(1, "phasing", 100, 100, 100, 100, null, []), 1, 1)
            { Character = { Name = "Phasing" + Guid.NewGuid().ToString("N") } };
        player.PhasingUntil = player.Clock.Elapsed.TotalSeconds + 4;
        Assert.True(player.IsPhasing);
        Assert.True(Game.ItemConditionFlags(player, player.Clock.Elapsed.TotalSeconds)["_while_phasing"]);
    }
}
