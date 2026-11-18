using System.Reflection;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
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

    // ValerieTutorial (Stats, Functions, StateMachine after the base six) is written: Stats as a monster's with no pairs
    // (compact count, u32, u32, u8), nothing for Functions, then her StateMachine (2 states, 2 timers).
    [Fact]
    public void Valerie_writes_stats_and_functions()
    {
        var valerie = NpcCatalog.Load().Find("Metadata/NPC/League/Deepwater/ValerieTutorial");
        Assert.NotNull(valerie);
        Assert.True(valerie.Supported);
        byte[] blob = new NpcObject(236, 190, valerie.Components!, 0, valerie.StateMachine, valerie.TransitionState).Blob();
        Assert.Equal("0000" + "EC000000" + "BE000000" + "00000000" + "0000" + "00020001" + "00" + "00" + "01FF" + "00"
            + "00" + "00000000" + "00000000" + "00"
            + "0202" + "0000" + "0000" + "0000000000" + "0000000000" + "00", Convert.ToHexString(blob));
    }

    static IModuleContext Context(IModuleLifetime lifetime)
    {
        var context = DispatchProxy.Create<IModuleContext, SurvivorsLeagueTests.HookContext>();
        ((SurvivorsLeagueTests.HookContext)(object)context).Lifetime = lifetime;
        return context;
    }
}
