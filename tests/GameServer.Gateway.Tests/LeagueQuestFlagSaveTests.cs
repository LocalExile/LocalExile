using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public class LeagueQuestFlagSaveTests
{
    [Fact]
    public void League_quest_flags_are_saved_per_account_league_and_survive_a_reload()
    {
        Saves.Open(Path.Combine(Path.GetTempPath(), "LocalExile-league-flags-" + Guid.NewGuid().ToString("N") + ".db"));
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

        Assert.True(LeagueRules.Standard.DevelopmentUnlocks);
        var calibration = GameServer.Core.Accounts.LeagueCatalog.FromEnvironment().Find("Campaign Calibration")!;
        Assert.DoesNotContain("PlayerBots", calibration.Modules);
        var conn = new ClientState(new(1, "calibration", 100, 100, 10, 10, null, []), 1, 1);
        conn.RestoreQuestFlags([], [], developmentUnlocks: true);
        Assert.Contains("HideoutUnlocked", conn.QuestFlags);
        Assert.Contains(Quests.EndGameAvailable, conn.UnearnedQuestFlags);
        Assert.DoesNotContain(Quests.EndGameAvailable, conn.QuestFlags);
        conn.RestoreQuestFlags([], [], developmentUnlocks: false);
        Assert.Empty(conn.QuestFlags);
        Assert.Empty(conn.UnearnedQuestFlags);
        Assert.Empty(conn.LeagueQuestFlags);
        conn.RestoreQuestFlags(["a1q1-KilledHillock", "HideoutUnlocked", Quests.EndGameAvailable], [263], developmentUnlocks: false);
        Assert.True(conn.EarnedQuestFlag(Quests.EndGameAvailable));
        Assert.Contains("HideoutUnlocked", conn.QuestFlags);
        Assert.Contains("a1q1-KilledHillock", conn.QuestFlags);
        Assert.Equal([263], conn.LeagueQuestFlags);
    }
}
