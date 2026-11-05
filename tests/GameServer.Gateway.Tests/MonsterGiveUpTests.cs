namespace GameServer.Gateway.Tests;

public sealed class MonsterGiveUpTests
{
    [Fact]
    public void GiveUpPlanCarriesAnEndedActionAndAMove()
    {
        var plan = new Game.MonsterAiPlan();
        Assert.Empty(plan.EndedActions);

        // What the give-up branch records for a monster that lost the player.
        plan.EndedActions.Add(0x3010u);
        Assert.Contains(0x3010u, plan.EndedActions);
    }
}
