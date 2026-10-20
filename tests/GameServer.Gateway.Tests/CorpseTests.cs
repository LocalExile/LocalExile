using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class CorpseTests
{
    static readonly MonsterCatalog.Variety Crab =
        new(7, "Metadata/Monsters/Crab/ShoreCrab", "Shore Crab", "base", null, null);

    static CorpseBook.Corpse Dead(uint id = 0x3001, int x = 10, int y = 10, double at = 0, byte level = 2) =>
        new(id, Crab, level, MonsterRarity.Magic, $"Shore Crab@{level}", x, y, at);

    [Fact]
    public void A_corpse_remembers_what_died_where_and_when()
    {
        var book = new CorpseBook();
        book.Add(Dead());

        var corpse = book.Get(0x3001, now: 1);

        Assert.NotNull(corpse);
        Assert.Equal("Shore Crab", corpse!.Variety.Name);
        Assert.Equal(2, corpse.Level);
        Assert.Equal(MonsterRarity.Magic, corpse.Rarity);
        Assert.Equal((10, 10), (corpse.X, corpse.Y));
    }

    [Fact]
    public void A_corpse_is_gone_once_its_time_ran_out()
    {
        var book = new CorpseBook();
        book.Add(Dead(at: 0));

        Assert.NotNull(book.Get(0x3001, CorpseBook.LifetimeSeconds - 1));
        Assert.Null(book.Get(0x3001, CorpseBook.LifetimeSeconds));
        Assert.Equal(0, book.Count);
    }

    [Fact]
    public void A_raise_consumes_the_corpse_once()
    {
        var book = new CorpseBook();
        book.Add(Dead());

        Assert.NotNull(book.Take(0x3001, now: 1));
        Assert.Null(book.Take(0x3001, now: 1));
        Assert.Null(book.Get(0x3001, now: 1));
    }

    [Fact]
    public void An_expired_corpse_cannot_be_raised()
    {
        var book = new CorpseBook();
        book.Add(Dead(at: 0));

        Assert.Null(book.Take(0x3001, CorpseBook.LifetimeSeconds + 1));
    }

    [Fact]
    public void The_nearest_corpse_is_the_one_a_raise_with_no_id_takes()
    {
        var book = new CorpseBook();
        book.Add(Dead(0x3001, x: 100, y: 100));
        book.Add(Dead(0x3002, x: 12, y: 9));
        book.Add(Dead(0x3003, x: -50, y: -50));

        Assert.Equal(0x3002u, book.Nearest(10, 10, now: 1)!.Id);
    }

    [Fact]
    public void No_corpse_means_no_raise()
    {
        Assert.Null(new CorpseBook().Nearest(10, 10, now: 0));
    }

    [Fact]
    public void The_gem_allows_one_spectre_and_the_passives_add_to_it()
    {
        Assert.Equal(1, Spectres.Limit(0));
        Assert.Equal(2, Spectres.Limit(1));
        Assert.Equal(3, Spectres.Limit(2));
        // A negative source (a curse, a wrong stat) never takes the gem's own one away.
        Assert.Equal(1, Spectres.Limit(-1));
    }
}
