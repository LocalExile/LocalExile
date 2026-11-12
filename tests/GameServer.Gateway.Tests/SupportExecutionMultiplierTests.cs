namespace GameServer.Gateway.Tests;

public class SupportExecutionMultiplierTests
{
    [Fact]
    public void Greater_echo_applies_the_spell_stat_per_repeat()
    {
        int Own(string name) => name == "support_greater_spell_echo_spell_damage_+%_final_per_repeat" ? 10 : 0;
        Assert.Equal(1, Game.GreaterEchoHitMore(0, Own));
        Assert.Equal(1.1, Game.GreaterEchoHitMore(1, Own));
        Assert.Equal(1.2, Game.GreaterEchoHitMore(2, Own));
    }

    [Fact]
    public void Channel_damage_counts_completed_seconds_and_stops_at_sixty_percent()
    {
        Assert.Equal(1, Game.FocusedChannelMore(.99, 9));
        Assert.Equal(1.09, Game.FocusedChannelMore(1.99, 9));
        Assert.Equal(1.54, Game.FocusedChannelMore(6, 9));
        Assert.Equal(1.6, Game.FocusedChannelMore(7, 9));
    }
}
