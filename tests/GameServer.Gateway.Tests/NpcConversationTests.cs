namespace GameServer.Gateway.Tests;

public sealed class NpcConversationTests
{
    [Fact]
    public void Introductions_use_catalog_flags_and_are_not_replayed_after_being_heard()
    {
        var catalog = NpcCatalog.Load();
        Assert.Null(catalog.Error);
        foreach (string name in new[] { "Nessa", "Bestel", "Tarkleigh" })
        {
            var npc = catalog.Find(name)!;
            Assert.NotNull(npc);
            var flags = new HashSet<string>(StringComparer.Ordinal);
            var first = catalog.IntroductionOf(npc, flags);
            Assert.NotNull(first);
            Assert.Equal("Gossip", first.Category);
            Assert.True(first.Introduction && first.Automatic);
            Assert.NotNull(first.StartFlag);
            flags.Add(first.StartFlag);
            Assert.Null(catalog.IntroductionOf(npc, flags));
            flags.Clear();
            foreach (string excluded in first.ExcludedFlags) flags.Add(excluded);
            if (first.ExcludedFlags.Count > 0) Assert.Null(catalog.IntroductionOf(npc, flags));
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
            var intro = catalog.IntroductionOf(npc, flags)!;
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
    }
}
