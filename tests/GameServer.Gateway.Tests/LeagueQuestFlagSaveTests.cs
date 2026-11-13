namespace GameServer.Gateway.Tests;

public class LeagueQuestFlagSaveTests : IDisposable
{
    public void Dispose() => TestEnvironment.ReleaseOwnSaves();

    [Fact]
    public void League_quest_flags_are_saved_per_account_league_and_survive_a_reload()
    {
        TestEnvironment.OpenOwnSaves("league-flags");
        var catalog = new ItemCatalog("data");
        string account = "LeagueFlags" + Guid.NewGuid().ToString("N");
        string standard = AccountStash.Key(account, "Standard"), league = AccountStash.Key(account, "Mercenaries");

        // A Trial of Ascendancy's league flag (LabyrinthTrials column 1) set by one character of the league.
        Assert.True(AccountStash.For(league, catalog).ChangeLeagueQuestFlags([263]));
        Assert.False(AccountStash.For(league, catalog).ChangeLeagueQuestFlags([263]));
        AccountStash.Forget(league);
        Assert.Equal([263], AccountStash.For(league, catalog).LeagueQuestFlags);
        Assert.Empty(AccountStash.For(standard, catalog).LeagueQuestFlags);

        Assert.True(AccountStash.For(league, catalog).ChangeLeagueQuestFlags([263], on: false));
        AccountStash.Forget(league);
        Assert.Empty(AccountStash.For(league, catalog).LeagueQuestFlags);
    }
}
