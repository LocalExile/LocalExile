using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class QuestNpcInstanceTests
{
    const string Boss = "Metadata/Monsters/Frog/FrogGod/SilverFrog";
    const string Sin = "Metadata/NPC/Act8/SinYugul";
    const string Source = "test-instance-appearance";
    sealed class AppearanceAct : Quests.Act
    {
        public override int Number => 8;
        public override IReadOnlyList<Quests.Quest> Table => [new Quests.Quest("appearance-test")
        {
            new Quests.KillRule("follower", Boss, ["appearance-killed"]),
            new Quests.AppearsRule("follower", Boss, Sin, "appearance-reward"),
            Quests.On.Npc("follower", "Metadata/NPC/Act2/HelenaSins", "helena-gone", transitionedBy: "helena-freed", transitionedTo: 2),
        }];
    }

    [Fact]
    public async Task A_soul_NPC_belongs_to_the_kill_in_this_instance_and_survives_the_corpse()
    {
        using var rules = Quests.Register(Source, [new AppearanceAct()]);
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        using var returnWire = new PartyFollowerTests.Wire();
        using var freshWire = new PartyFollowerTests.Wire();
        var instance = new InstanceState();
        var player = runtime.PlayerOf(instance, wire, "SoulHost");
        var returning = runtime.PlayerOf(instance, returnWire, "SoulReturn");
        var fresh = runtime.PlayerOf(new(), freshWire, "SoulFresh");
        foreach (var c in new[] { player, returning, fresh })
        {
            c.LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false),
                new HashSet<string> { Source });
            c.Character.QuestFlags.Add("appearance-killed");
        }
        var variety = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find(Boss));
        SpawnedMonster Monster() => new("Yugul", new PlayerWalk(120, 130, 30), 50, 10, 1000)
            { Variety = variety, Level = 58, Hostile = true };
        var boss = Monster();
        player.Area.Monsters[1] = boss;
        fresh.Area.Monsters[1] = Monster();
        Assert.Empty(Game.AppearedBefore(player));
        Assert.Empty(Game.AppearedBefore(fresh));

        boss.Dead = true; boss.Life = 0;
        await Game.QuestNpcsAppear(player, variety, (120, 130));
        Assert.Equal(1, Assert.Single(player.Npcs).Value.State);
        Assert.Equal((120, 130), (Assert.Single(player.QuestNpcs).X, Assert.Single(player.QuestNpcs).Y));

        // The instance saves only living monsters; the appearance must not need its discarded corpse.
        player.Area.Monsters.Clear();
        returning.QuestNpcs.AddRange(Game.AppearedBefore(returning));
        await Game.UpdateQuestNpcs(returning, "return");
        var restored = Assert.Single(returning.QuestNpcs);
        Assert.Equal((120, 130), (restored.X, restored.Y));
        Assert.Equal(2, Assert.Single(returning.Npcs).Value.State);
        Assert.Empty(Game.AppearedBefore(fresh));

        returning.Character.QuestFlags.Add("appearance-reward");
        await Game.UpdateQuestNpcs(returning, "reward");
        Assert.Empty(returning.Npcs);

        // Helena has three asset states: 2 is rescued; the final state 3 hides her entirely.
        const string helenaType = "Metadata/NPC/Act2/HelenaSins";
        var helena = Assert.IsType<NpcCatalog.Npc>(Game.Npcs.Find(helenaType));
        Assert.Equal(3, helena.TransitionStates);
        fresh.QuestNpcs.Add(new(helena, 120, 130, 0, "follower", helenaType, null, roomState: true));
        await Game.UpdateQuestNpcs(fresh, "before rescue");
        Assert.Equal(1, Assert.Single(fresh.Npcs).Value.State);
        fresh.Character.QuestFlags.Add("helena-freed");
        await Game.UpdateQuestNpcs(fresh, "rescue");
        Assert.Equal(2, Assert.Single(fresh.Npcs).Value.State);
        // A later visit creates the same visible rescued state directly, without replaying state 1.
        fresh.Npcs.Clear(); fresh.QuestNpcs.Clear();
        fresh.QuestNpcs.Add(new(helena, 120, 130, 0, "follower", helenaType, null, roomState: true));
        await Game.UpdateQuestNpcs(fresh, "return");
        Assert.Equal(2, Assert.Single(fresh.Npcs).Value.State);
        fresh.Character.QuestFlags.Add("helena-gone");
        await Game.UpdateQuestNpcs(fresh, "gone");
        Assert.Empty(fresh.Npcs);
    }
}
