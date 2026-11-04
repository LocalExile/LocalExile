using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class CurrencyExchangeTests : IDisposable
{
    string? _savePath;
    public void Dispose()
    {
        if (_savePath is not null) TestEnvironment.ReleaseSaves(_savePath);
    }
    [Fact]
    public void Orders_escrow_match_other_accounts_and_collect_once_across_reload()
    {
        string path = Path.Combine(Path.GetTempPath(), "LocalExile-exchange-" + Guid.NewGuid().ToString("N") + ".db");
        _savePath = path;
        Saves.Open(path);
        var catalog = new ItemCatalog("data");
        var wisdom = catalog.Find("Scroll of Wisdom")!;
        var portal = catalog.Find("Portal Scroll")!;
        string a = "ExchangeA" + Guid.NewGuid().ToString("N"), b = "ExchangeB" + Guid.NewGuid().ToString("N");
        var ai = PlayerInventories.For(a, catalog); var bi = PlayerInventories.For(b, catalog);
        var ast = AccountStash.For(a, catalog); var bst = AccountStash.For(b, catalog);
        ast.AddGold(100); bst.AddGold(100);
        ai.Add(0, 0, 0, ItemCatalog.Create(wisdom) with { StackSize = 12 });
        bi.Add(0, 0, 0, ItemCatalog.Create(portal) with { StackSize = 3 });
        PlayerInventories.Shape? Shape(Item item) => catalog.ByHash(item.TypeHash) is { } t
            ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;
        var first = CurrencyExchange.Place(a, "Standard", false, ai, ast, catalog, portal.Hash, wisdom.Hash, 6, 12)!;
        Assert.Equal(0, ai.Count(0, wisdom.Hash));
        Assert.Equal(94u, ast.Gold);
        Assert.True(first.Order.Active);
        Assert.Null(CurrencyExchange.Place(a, "Standard", true, ai, ast, catalog, portal.Hash, wisdom.Hash, 1, 1));
        Assert.Null(CurrencyExchange.Cancel("Standard", b, first.Order.Id));
        var second = CurrencyExchange.Place(b, "Standard", false, bi, bst, catalog, wisdom.Hash, portal.Hash, 6, 3)!;
        Assert.False(second.Order.Active);
        Assert.Equal(6, second.Order.Claim);
        Assert.Equal(6, CurrencyExchange.Orders("Standard", a).Single().RemainingGive);
        Assert.Empty(CurrencyExchange.Orders("Hardcore"));
        Saves.Open(path);
        var cancel = CurrencyExchange.Cancel("Standard", a, first.Order.Id)!;
        Assert.Equal(3, cancel.Claim);
        // Native refund icon: side zero; normal click puts one stack on the cursor.
        var click = InstanceCurrencyExchange.ReadCollection(Convert.FromHexString("02EB00000001000101FEFF"));
        Assert.Equal(new InstanceCurrencyExchange.CollectionRequest(cancel.Id, true, false, true), click);
        var refund = CurrencyExchange.Collect("Standard", a, click.OrderId, click.Refund, click.ToInventory,
            ai, catalog, Shape, click.SingleStack);
        Assert.NotNull(refund);
        Assert.Equal(PlayerInventories.Cursor, refund.Row);
        Assert.Equal(6, ai.Count(PlayerInventories.Cursor, wisdom.Hash));
        Assert.NotNull(CurrencyExchange.Collect("Standard", a, cancel.Id, false, true, ai, catalog, Shape));
        Assert.NotNull(CurrencyExchange.Collect("Standard", b, second.Order.Id, false, true, bi, catalog, Shape));
        Assert.Null(CurrencyExchange.Collect("Standard", b, second.Order.Id, false, true, bi, catalog, Shape));
        Assert.Empty(CurrencyExchange.Orders("Standard"));
        Assert.Equal(12, ai.Count(PlayerInventories.Cursor, wisdom.Hash) + bi.Count(0, wisdom.Hash));
        Assert.Equal(3, ai.Count(0, portal.Hash) + bi.Count(0, portal.Hash));
    }

    [Fact]
    public void Refusals_self_orders_and_failed_commits_cannot_create_or_lose_currency()
    {
        _savePath = Path.Combine(Path.GetTempPath(), "LocalExile-exchange-" + Guid.NewGuid().ToString("N") + ".db");
        Saves.Open(_savePath);
        var catalog = new ItemCatalog("data");
        var wisdom = catalog.Find("Scroll of Wisdom")!; var portal = catalog.Find("Portal Scroll")!;
        string account = "Exchange" + Guid.NewGuid().ToString("N");
        var items = PlayerInventories.For(account, catalog); var stash = AccountStash.For(account, catalog);
        items.Add(0, 0, 0, ItemCatalog.Create(wisdom) with { StackSize = 10 });
        items.Add(0, 1, 0, ItemCatalog.Create(portal) with { StackSize = 10 });
        Assert.Null(CurrencyExchange.Place(account, "Standard", false, items, stash, catalog, portal.Hash, wisdom.Hash, 1, 1));
        stash.AddGold(100);
        Assert.Null(CurrencyExchange.Place(account, "Standard", false, items, stash, catalog, portal.Hash, wisdom.Hash, 1, -1));
        Assert.Throws<IOException>(() => Saves.Atomically(() =>
        {
            Assert.NotNull(CurrencyExchange.Place(account, "Standard", false, items, stash, catalog, portal.Hash, wisdom.Hash, 2, 2));
            throw new IOException("simulated commit failure");
        }));
        PlayerInventories.Forget(account); AccountStash.Forget(account);
        items = PlayerInventories.For(account, catalog); stash = AccountStash.For(account, catalog);
        Assert.Empty(CurrencyExchange.Orders("Standard"));
        Assert.Equal(10, items.Count(0, wisdom.Hash));
        Assert.Equal(100u, stash.Gold);
        var a = CurrencyExchange.Place(account, "Standard", false, items, stash, catalog, portal.Hash, wisdom.Hash, 2, 2)!;
        var b = CurrencyExchange.Place(account.ToUpperInvariant(), "Standard", false, items, stash, catalog, wisdom.Hash, portal.Hash, 2, 2)!;
        Assert.True(a.Order.Active); Assert.True(b.Order.Active);
        Assert.All(CurrencyExchange.Orders("Standard"), o => Assert.Equal(0, o.Received));
        // The optional market fills existing orders, persists finite stock and withdraws on unload.
        using (var market = ExchangeMarkets.Register("test-market"))
        {
            market.Publish("snapshot-1", [new("Scroll of Wisdom", "Portal Scroll", 4, 4)], ["Standard"]);
            Assert.Contains("test-market", ExchangeMarkets.ActiveOwners("Standard"));
            Assert.Empty(CurrencyExchange.SyncMarkets("Hardcore", catalog));
            var changed = CurrencyExchange.SyncMarkets("Standard", catalog);
            Assert.Contains(changed, o => o.Id == a.Order.Id && o.Claim == 2);
            Assert.Equal(2, CurrencyExchange.Orders("Standard").Single(o => o.MarketOwner != null).RemainingGive);
            Assert.Empty(CurrencyExchange.SyncMarkets("Standard", catalog));
            Assert.Equal(2, CurrencyExchange.Orders("Standard").Single(o => o.MarketOwner != null).RemainingGive);
            var obsolete = ExchangeMarkets.Current("Standard").Single();
            market.Publish("rollback", [new("Scroll of Wisdom", "Portal Scroll", 100, 100)], ["Standard"]);
            Assert.False(ExchangeMarkets.IsCurrent(obsolete));
            Assert.Throws<IOException>(() => Saves.Atomically(() =>
            {
                CurrencyExchange.SyncMarkets("Standard", catalog);
                throw new IOException("rollback market replacement");
            }));
            Assert.Equal(2, CurrencyExchange.Orders("Standard").Single(o => o.MarketOwner != null).RemainingGive);
            market.Publish("snapshot-1", [new("Scroll of Wisdom", "Portal Scroll", 4, 4)], ["Standard"]);
            Assert.Empty(CurrencyExchange.SyncMarkets("Standard", catalog));
            Assert.Throws<ArgumentException>(() => market.Publish("invalid", [new("", "Portal Scroll", 1, 1)], ["Standard"]));
        }
        Assert.DoesNotContain(CurrencyExchange.Orders("Standard"), o => o.MarketOwner != null);
        Assert.Equal(2, CurrencyExchange.Orders("Standard", account).Single(o => o.Id == a.Order.Id).Claim);
        using (var market = ExchangeMarkets.Register("test-market"))
        {
            market.Publish("snapshot-1", [new("Scroll of Wisdom", "Portal Scroll", 4, 4)], ["Standard"]);
            Assert.Empty(CurrencyExchange.SyncMarkets("Standard", catalog));
            Assert.Equal(2, CurrencyExchange.Orders("Standard").Single(o => o.MarketOwner != null).RemainingGive);
            var consumed = CurrencyExchange.Place(account, "Standard", false, items, stash, catalog, portal.Hash, wisdom.Hash, 2, 2)!;
            Assert.Equal(2, consumed.Order.Claim);
            Assert.DoesNotContain(CurrencyExchange.Orders("Standard"), o => o.MarketOwner != null && o.Active);
            Assert.Empty(CurrencyExchange.SyncMarkets("Standard", catalog));
            Assert.DoesNotContain(CurrencyExchange.Orders("Standard"), o => o.MarketOwner != null && o.Active);
            market.Publish("snapshot-1", [new("Scroll of Wisdom", "Portal Scroll", 4, 4)], ["Standard"]);
            Assert.Empty(CurrencyExchange.SyncMarkets("Standard", catalog));
            Assert.DoesNotContain(CurrencyExchange.Orders("Standard"), o => o.MarketOwner != null && o.Active);
            market.Publish("snapshot-2", [new("Portal Scroll", "Scroll of Wisdom", 4, 4)], ["Standard"]);
            CurrencyExchange.SyncMarkets("Standard", catalog);
            Assert.Equal(2, CurrencyExchange.Orders("Standard", account).Single(o => o.Id == b.Order.Id).Claim);
        }
        var settings = new CurrencyMarket.Settings();
        Assert.False(settings.FetchPrices);
        var quotes = CurrencyMarket.Quotes([new("Divine Orb", 100, 1000)], settings);
        Assert.Equal(12, quotes.Length);
        var bid = quotes.First(q => q.Want == "Divine Orb");
        var ask = quotes.First(q => q.Give == "Divine Orb");
        Assert.Equal(95m, (decimal)bid.Offered / bid.Wanted);
        Assert.Equal(105m, (decimal)ask.Wanted / ask.Offered);
        Assert.Equal(50, bid.Wanted); Assert.Equal(50, ask.Offered);
        Assert.Equal(new decimal[] { 95, 93, 91, 89, 87, 85 }, quotes.Where(q => q.Want == "Divine Orb").Select(q => (decimal)q.Offered / q.Wanted));
        Assert.Equal(new decimal[] { 105, 107, 109, 111, 113, 115 }, quotes.Where(q => q.Give == "Divine Orb").Select(q => (decimal)q.Wanted / q.Offered));
        Assert.Equal(new[] { 50, 62, 78, 97, 122, 152 }, quotes.Where(q => q.Want == "Divine Orb").Select(q => q.Wanted));
        foreach (decimal price in new[] { 0.003m, 0.5m, 1m, 9.96m })
        {
            var ladder = CurrencyMarket.Quotes([new("Test Currency", price, 100000)], settings);
            Assert.Equal(12, ladder.Length);
            foreach (bool buy in new[] { true, false })
            {
                var rates = ladder.Where(q => (q.Want == "Test Currency") == buy)
                    .Select(q => buy ? (decimal)q.Offered / q.Wanted : (decimal)q.Wanted / q.Offered).ToArray();
                Assert.Equal(6, rates.Distinct().Count());
                Assert.True(rates.Zip(rates.Skip(1)).All(p => buy ? p.First > p.Second : p.First < p.Second));
            }
        }
        var custom = CurrencyMarket.Quotes([new("Divine Orb", 100, 1000)], settings with { LevelGapPercent = 3, LevelVolumeMultiplier = 1 });
        Assert.Equal(80m, (decimal)custom[5].Offered / custom[5].Wanted);
        Assert.All(custom, q => Assert.Equal(50, q.Want == "Divine Orb" ? q.Wanted : q.Offered));
        Assert.Empty(CurrencyMarket.Quotes([new("Divine Orb", 100, 1000)], settings with { VolumeFactor = 0 }));
        const string feed = """
            {"core":{"primary":"chaos","items":[{"id":"divine","name":"Divine Orb"}]},"items":[],
             "lines":[{"id":"divine","primaryValue":100,"volumePrimaryValue":100000}]}
            """;
        var parsed = CurrencyMarket.Parse(new("Standard", DateTimeOffset.UnixEpoch, [new("Currency", null, feed)]));
        Assert.Equal(new CurrencyMarket.Price("Divine Orb", 100, 1000), Assert.Single(parsed));
        Assert.Empty(CurrencyMarket.Quotes([new("Divine Orb", 100, 0)], settings));
        Assert.Empty(CurrencyMarket.Quotes([new("Cheap Currency", 0.001m, 1)], settings));
        var checkedAt = DateTimeOffset.UnixEpoch;
        var cached = new CurrencyMarket.Cache("Standard", checkedAt,
            [new("Currency", "etag", feed, checkedAt.AddHours(2))]);
        Assert.Equal(checkedAt.AddHours(2), CurrencyMarket.NextRefresh(cached, settings));
        Assert.Equal(checkedAt.AddHours(3), CurrencyMarket.NextRefresh(cached, settings with { RefreshMinutes = 180 }));
        var bundled = System.Text.Json.JsonSerializer.Deserialize<CurrencyMarket.Cache>(
            File.ReadAllText("Mods/Source/CurrencyMarket/prices.default.json"))!;
        Assert.Equal("Standard", bundled.League);
        Assert.Equal(new[] { "Currency", "Fragment" }, bundled.Feeds.Select(f => f.Category));
        var bundledPrices = CurrencyMarket.Parse(bundled);
        Assert.Contains(bundledPrices, p => p.Name == "Divine Orb");
        Assert.NotEmpty(CurrencyMarket.Quotes(bundledPrices, settings));
        Assert.Equal(new byte[] { 2, 0xe7, 0, 10 }, InstanceCurrencyExchange.Orders([], 10));
        Assert.Equal(18, InstanceProtocol.KnownClientLength(InstanceCurrencyExchange.PlaceRequest));
        Assert.Equal(11, InstanceProtocol.KnownClientLength(InstanceCurrencyExchange.CollectRequest));
        Assert.Equal(new InstanceCurrencyExchange.CollectionRequest(1, true, true, true),
            InstanceCurrencyExchange.ReadCollection(Convert.FromHexString("02EB00000001000100FEFF")));
        Assert.Equal(new InstanceCurrencyExchange.CollectionRequest(2, false, true, false),
            InstanceCurrencyExchange.ReadCollection(Convert.FromHexString("02EB00000002010000FEFF")));
    }
}
