using System.Reflection;
using GameServer.Core.Accounts;
using GameServer.Core.World;
using GameServer.Modding;
using GameServer.Modules.Deepwater;

namespace GameServer.Gateway.Tests;

public sealed class DeepwaterLeagueTests
{
    // The Allflame leagues list the Deepwater module, which answers the league category the client's topology pick and
    // Deepwater pin read (LeagueCategoryToDisplayName's id for LeagueNames "Deepwater") and map_deepwater_league on every
    // area; every other league keeps category 60 and no league stat, whatever its name.
    [Fact]
    public void Allflame_leagues_play_in_the_Deepwater_category_with_the_league_stat()
    {
        var leagues = LeagueCatalog.FromFolder("Leagues");
        Assert.Empty(leagues.Errors);
        var allflame = leagues.All.Where(l => l.Base == "Allflame").ToList();
        Assert.Equal(8, allflame.Count);
        Assert.All(allflame, l => Assert.Contains("Core/Content/Deepwater", l.Modules));
        Assert.All(leagues.All.Where(l => l.Base != "Allflame"), l => Assert.DoesNotContain("Core/Content/Deepwater", l.Modules));

        var lifetime = new ModuleLifetime("Deepwater");
        try
        {
            new DeepwaterModule().Load(Context(lifetime));
            uint flag = Program.Stats.Key("map_deepwater_league")!.Value;
            var request = new AreaStatsRequest("Exile", null, 0x7ad2 /* 1_1_2 The Coast */, null);
            foreach (var league in allflame)
            {
                var runtime = new LeagueRuntime(league, ServerLeagues.ModulesOf(league));
                Assert.Equal(59, InstanceEvents.ComposeLeagueCategory(runtime, request with { League = league.Name }));
                Assert.Equal(1, InstanceEvents.ComposeAreaStats(runtime, request with { League = league.Name }).GetValueOrDefault(flag));
            }
            var standard = new LeagueRuntime(leagues.Find("Standard")!, ServerLeagues.ModulesOf(leagues.Find("Standard")!));
            var named = request with { League = "Deepwater Standard" };
            Assert.Equal(LeagueCategory.None, InstanceEvents.ComposeLeagueCategory(standard, named));
            Assert.False(InstanceEvents.ComposeAreaStats(standard, named).ContainsKey(flag));
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            GameEventOwners.Release("Deepwater", GameEventOwners.ModuleRank);
        }
    }

    static IModuleContext Context(IModuleLifetime lifetime)
    {
        var context = DispatchProxy.Create<IModuleContext, SurvivorsLeagueTests.HookContext>();
        ((SurvivorsLeagueTests.HookContext)(object)context).Lifetime = lifetime;
        return context;
    }
}
