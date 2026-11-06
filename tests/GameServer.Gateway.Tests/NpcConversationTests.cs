namespace GameServer.Gateway.Tests;

public sealed class NpcConversationTests
{
    [Fact]
    public void Automatic_conversations_use_catalog_priority_flags_and_visited_rows()
    {
        // Hot reload adds state to existing connections without rerunning their constructors.
        var existing = (ClientState)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ClientState));
        Assert.Empty(existing.AutomaticNpcTalkRows);
        existing.AutomaticNpcTalkRows.Add(7);
        Assert.Contains(7, existing.AutomaticNpcTalkRows);
        var catalog = NpcCatalog.Load();
        Assert.Null(catalog.Error);
        foreach (string name in new[] { "Nessa", "Bestel", "Tarkleigh" })
        {
            var npc = catalog.Find(name)!;
            Assert.NotNull(npc);
            var flags = new HashSet<string>(StringComparer.Ordinal);
            var first = catalog.NextConversationOf(npc, flags, new HashSet<int>());
            Assert.NotNull(first);
            Assert.Equal("Gossip", first.Category);
            Assert.True(first.Introduction && first.Automatic);
            Assert.NotNull(first.StartFlag);
            flags.Add(first.StartFlag);
            Assert.NotEqual(first.Row, catalog.NextConversationOf(npc, flags, new HashSet<int>())?.Row);
            flags.Clear();
            foreach (string excluded in first.ExcludedFlags) flags.Add(excluded);
            if (first.ExcludedFlags.Count > 0)
                Assert.NotEqual(first.Row, catalog.NextConversationOf(npc, flags, new HashSet<int>())?.Row);
        }
    }
    [Fact]
    public void Quest_continuations_revalidate_flags_and_respect_visited_rows()
    {
        var catalog = NpcCatalog.Load();
        foreach (var (name, expected) in new[] { ("Nessa", "Lioneye's Watch"), ("Bestel", "The Medicine Chest") })
        {
            var npc = catalog.Find(name)!;
            var flags = new HashSet<string>();
            var intro = catalog.NextConversationOf(npc, flags, new HashSet<int>())!;
            flags.Add(intro.StartFlag!);
            var visited = new HashSet<int> { intro.Row };
            if (name == "Bestel")
            {
                Assert.Null(catalog.NextConversationOf(npc, flags, visited));
                flags.Add("a1q5-ReceivedQuest");
            }
            var next = catalog.NextConversationOf(npc, flags, visited);
            Assert.NotNull(next);
            Assert.Equal(expected, next.Option);
            visited.Add(next.Row);
            Assert.NotEqual(next.Row, catalog.NextConversationOf(npc, flags, visited)?.Row);
            visited.Clear();
            flags.Add(next.StartFlag!);
            Assert.NotEqual(next.Row, catalog.NextConversationOf(npc, flags, visited)?.Row);
        }
        var tarkleigh = catalog.Find("Tarkleigh")!;
        var rewardFlags = new HashSet<string> { "TarkleighIntroduced" };
        Assert.Null(catalog.NextConversationOf(tarkleigh, rewardFlags, new HashSet<int>()));
        rewardFlags.Add("a1q1-KilledHillock");
        var reward = catalog.NextConversationOf(tarkleigh, rewardFlags, new HashSet<int>());
        Assert.NotNull(reward);
        Assert.Equal("QuestReward", reward.Category);
        Assert.Equal(0, reward.RewardOffer);
        rewardFlags.Add("a1q1-QuestRewardTaken");
        Assert.Null(catalog.NextConversationOf(tarkleigh, rewardFlags, new HashSet<int>()));
    }
}
