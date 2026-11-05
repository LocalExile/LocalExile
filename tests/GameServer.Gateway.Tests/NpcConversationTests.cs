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
}
