namespace GameServer.Gateway.Tests;

public class QuestEncounterRestoreTests
{
    [Fact]
    public void Restoring_checks_the_encounters_own_area_and_completion_rules()
    {
        var quest = new Quests.Quest("test")
        {
            new Quests.ThresholdRule("roof", "kitava", 50, ["roof-done"]),
            new Quests.KilledAllRule("bridge", ["sun", "moon"], ["bridge-done"]),
            new Quests.KillRule("pyramid", "oversoul", ["pyramid-done"]),
        };
        var rules = new Quests.RuleSet([quest], quest.ToDictionary(r => r, _ => quest.Id));
        var flags = new HashSet<string> { "delivered" };
        Assert.False(rules.SpawnEncounterFinished("roof", "kitava", flags.Contains));
        flags.UnionWith(["roof-done", "bridge-done", "pyramid-done"]);
        Assert.True(rules.SpawnEncounterFinished("roof", "kitava", flags.Contains));
        Assert.True(rules.SpawnEncounterFinished("bridge", "moon", flags.Contains));
        Assert.True(rules.SpawnEncounterFinished("bridge", "sun", flags.Contains));
        Assert.True(rules.SpawnEncounterFinished("pyramid", "oversoul", flags.Contains));
        Assert.False(rules.SpawnEncounterFinished("another-area", "kitava", flags.Contains));
    }

    sealed class AppearanceAct : Quests.Act
    {
        public override int Number => 5;
        public override IReadOnlyList<Quests.Quest> Table => [new Quests.Quest("appearance-test")
        {
            new Quests.AppearsRule("follower", "Metadata/Monsters/AvariusCasticus/AvariusCasticusDivine",
                "Metadata/NPC/Act5/SinInnocence", "a5q6-QuestReceived"),
        }];
    }

    [Fact]
    public async Task A_kill_shows_Sin_once_to_the_eligible_peer_using_its_own_quest_state()
    {
        const string source = "test-quest-appearance";
        using var rules = Quests.Register(source, [new AppearanceAct()]);
        using var runtime = new PartyFollowerTests.Runtime();
        using var hostWire = new PartyFollowerTests.Wire();
        using var peerWire = new PartyFollowerTests.Wire();
        var instance = new InstanceState();
        var host = runtime.Connection(instance, hostWire, "AppearanceHost");
        var peer = runtime.Connection(instance, peerWire, "AppearancePeer");
        foreach (var player in new[] { host, peer })
        {
            player.LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false), new HashSet<string> { source });
            var template = new GameServer.Core.Protocol.Instance.PlayerObject(player.Character!, 0, 100, 100, 1000, 1000);
            instance.PublishPlayer(new(player.PeerId, 1, template, 100, 100, 1000, player));
        }
        host.QuestFlags.Add("a5q6-QuestReceived");
        var boss = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find("Metadata/Monsters/AvariusCasticus/AvariusCasticusDivine"));
        for (int i = 0; i < 2; i++)
        {
            await Game.QuestNpcsAppear("host", hostWire.Stream, hostWire.Ciphers, host, boss, (100, 100));
            await Game.ApplyKillShares("peer", peerWire.Stream, peerWire.Ciphers, peer);
        }
        Assert.Empty(host.Npcs);
        Assert.Equal("Metadata/NPC/Act5/SinInnocence", Assert.Single(peer.Npcs).Value.Npc.Id);
        Assert.NotEqual(0u, Assert.Single(peer.QuestNpcs).Id);
    }
}
