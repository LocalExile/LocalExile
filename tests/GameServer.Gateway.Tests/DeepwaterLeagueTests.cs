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
    // A lantern placement (C->S 0x2ca) is framed, and a lantern ends its blob with its light task.
    [Fact]
    public void Allflame_objects_and_ruleset_actions_are_written()
    {
        var valerie = NpcCatalog.Load().Find("Metadata/NPC/League/Deepwater/ValerieTutorial");
        Assert.NotNull(valerie);
        Assert.True(valerie.Supported);
        byte[] blob = new NpcObject(236, 190, valerie.Components!, 0, valerie.StateMachine, valerie.TransitionState).Blob();
        Assert.Equal("0000" + "EC000000" + "BE000000" + "00000000" + "0000" + "00020001" + "00" + "00" + "01FF" + "00"
            + "00" + "00000000" + "00000000" + "00"
            + "0202" + "0000" + "0000" + "0000000000" + "0000000000" + "00", Convert.ToHexString(blob));

        // A lantern placement (C->S 0x2ca for ruleset 171, opcode 0 and the cell) is framed, so the tick after it survives.
        byte[] place = Convert.FromHexString("02CAAB09" + "00" + "EC000000" + "BE000000");
        var split = InstanceProtocol.SplitClientMessages([.. place, .. Tick()], out byte[] partial, out byte[] unknown);
        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Equal([(ushort)0x2ca, (ushort)0x1b], split.Select(m => m.Id));
        var (row, payload) = InstanceRulesets.ReadAction(split[0].Bytes);
        Assert.Equal(0xab, row);
        Assert.Equal(place[4..], payload);

        // The Lantern's components and its DeepwaterLantern task (GameObjectTasks HASH16 0xc4df, LE) close its blob.
        byte[] light = new byte[16];
        byte[] lantern = new QuestObject(236, 190, ["Positioned", "BaseEvents", "Animated", "Buffs", "StateMachine", "MinimapIcon", "Functions"])
        {
            StateMachine = new StateMachineData([0], 0),
            Tasks = [(0xc4df, light)],
        }.Blob();
        Assert.Equal("01DFC4" + Convert.ToHexString(light), Convert.ToHexString(lantern[^19..]));

        // An int script variable (type 9): this.RandomNumber = 3 in the base data.
        Assert.Equal(0xe70679ebu, QuestObject.VariableHash("RandomNumber"));
        byte[] numbered = new QuestObject(0, 0, ["Positioned"]) { IntVariables = new Dictionary<uint, int> { [0xe70679eb] = 3 } }.Blob();
        Assert.Equal("0001" + "EB7906E7" + "000109" + "03000000", Convert.ToHexString(numbered[..13]));

        // A chart base writes its DeepwaterChart part (u8 Area Level, u8, DeepwaterRooms row; a row past the end crashes the
        // client) last, and the header
        // flags (0x0006: a Dredged item, accepted by the capsule grid) follow the type hash.
        var chart = new ItemCatalog("data").Find("Metadata/Items/Deepwater/ChartSandy");
        Assert.NotNull(chart);
        Assert.True(chart.Supported);
        byte[] item = (ItemCatalog.Create(chart) with { HeaderFlags = 6, Chart = new(68, 0, 3) }).Payload();
        Assert.Equal("0600", Convert.ToHexString(item[4..6]));
        Assert.Equal("440003", Convert.ToHexString(item[^3..]));

        // Ctrl + click into the capsule (C->S 0x0112, live: item 7 from the main inventory) is framed and read.
        split = InstanceProtocol.SplitClientMessages([.. Convert.FromHexString("0112000000070001"), .. Tick()], out partial, out unknown);
        Assert.Empty(unknown);
        Assert.Equal([(ushort)0x112, (ushort)0x1b], split.Select(m => m.Id));
        Assert.Equal(new InstanceItems.LeagueWindowMove(7, false, true), InstanceItems.ReadLeagueWindowMove(split[0].Bytes));
    }

    static byte[] Tick()
    {
        byte[] tick = new byte[InstanceProtocol.KnownClientLength(0x1b) ?? throw new InvalidOperationException("0x1b has no fixed length")];
        tick[1] = 0x1b;
        return tick;
    }

    static IModuleContext Context(IModuleLifetime lifetime)
    {
        var context = DispatchProxy.Create<IModuleContext, SurvivorsLeagueTests.HookContext>();
        ((SurvivorsLeagueTests.HookContext)(object)context).Lifetime = lifetime;
        return context;
    }
}
