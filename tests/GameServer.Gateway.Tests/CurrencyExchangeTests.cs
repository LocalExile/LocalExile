using GameServer.Core.Protocol.Instance;
using GameServer.Core.Trade;

namespace GameServer.Gateway.Tests;

public sealed class CurrencyExchangeTests
{
    [Fact]
    public void AnOrderIsWrittenAsTheWindowReadsIt()
    {
        var order = new InstanceCurrencyExchange.Order(7, "Std", 0x11111111, 0x22222222, 300, 4, 5, 6, 3, 1, 8, 9,
            InstanceCurrencyExchange.OrderState.Completed);
        byte[] expected =
        [
            0x02, 0xf3,
            7, 0, 0, 0, 0, 0, 0, 0, // id, written from memory
            0, 0, 0, 7,
            0, 3, (byte)'S', (byte)'t', (byte)'d',
            0, 0, 0, 0,
            1, // in this league
            0x11, 0x11, 0x11, 0x11, 0x22, 0x22, 0x22, 0x22,
            0, 0, 1, 44, 0, 0, 0, 4, 0, 0, 0, 5, 0, 0, 0, 6,
            0, 3, 0, 1, 0, 8, 0, 9,
            1,
            0,
        ];
        Assert.Equal(expected, InstanceCurrencyExchange.OrderChanged(order));
    }

    [Fact]
    public void PricesComeFromVolumesAndARatioNeverPaysMoreThanTheRate()
    {
        const string X = "Metadata/Items/Currency/X", Thin = "Metadata/Items/Currency/Thin", ViaDivine = "Metadata/Items/Currency/ViaDivine",
            Rare = "Metadata/Items/Currency/Rare", Dear = "Metadata/Items/Currency/Dear", Stray = "Metadata/Items/Currency/Stray", Scam = "Metadata/Items/Currency/Scam";
        static string Market(string league, string a, string b, int va, int vb) =>
            $$$"""{"league":"{{{league}}}","market_pair":["{{{a}}}","{{{b}}}"],"volume_traded":{"{{{a}}}":{{{va}}},"{{{b}}}":{{{vb}}}}}""";
        static string Digest(int hour, params string[] markets) =>
            $$$"""{"next_change_id":{{{hour + 3600}}},"markets":[{{{string.Join(',', markets)}}}]}""";

        var rates = ExchangeRates.FromDigests(
        [
            Digest(100 * 3600, Market("Allflame", ExchangeRates.Divine, ExchangeRates.Chaos, 1, 500), Market("Private (PL1)", X, ExchangeRates.Chaos, 1, 900),
                Market("Allflame", Dear, ExchangeRates.Chaos, 9, 90), Market("Allflame", Dear, ExchangeRates.Divine, 10, 2),
                Market("Allflame", Stray, ExchangeRates.Chaos, 1, 5), Market("Allflame", Scam, ExchangeRates.Chaos, 9, 9)),
            Digest(99 * 3600, Market("Allflame", ExchangeRates.Divine, ExchangeRates.Chaos, 1, 400),
                Market("Allflame", X, ExchangeRates.Chaos, 100, 50), Market("Allflame", Thin, ExchangeRates.Chaos, 1, 4), Market("Allflame", Scam, ExchangeRates.Chaos, 1, 134),
                Market("Allflame", ViaDivine, ExchangeRates.Divine, 200, 1), Market("Standard", X, ExchangeRates.Chaos, 10, 30)),
            // Days ago: it prices what the last day cannot, and moves nothing that it can.
            Digest(50 * 3600, Market("Allflame", ExchangeRates.Divine, ExchangeRates.Chaos, 1, 900), Market("Allflame", Rare, ExchangeRates.Chaos, 2, 10),
                Market("Allflame", Stray, ExchangeRates.Chaos, 9, 995)),
        ]);

        Assert.Equal(100 * 3600, rates.SourceHour);
        Assert.Equal(5, rates.Prices(standard: false)![Rare].Chaos);
        Assert.Equal(80, rates.Prices(standard: false)![Dear].Chaos); // the middle card: its divine trades count, 2 divines for 10 at 400 each
        Assert.Equal(995.0 / 9, rates.Prices(standard: false)![Stray].Chaos); // one trade today does not move a week's market
        Assert.Equal(1, rates.Prices(standard: false)![Scam].Chaos); // nor does one dear trade beside nine cheap ones
        Assert.Equal("Allflame", rates.Current);
        Assert.DoesNotContain("Private (PL1)", rates.Leagues.Keys);
        var prices = rates.Prices(standard: false)!;
        Assert.Equal(400, prices[ExchangeRates.Divine].Chaos); // the last day's, not the old 900
        Assert.Equal(0.5, prices[X].Chaos);
        Assert.Equal(400.0 / 200, prices[ViaDivine].Chaos);
        Assert.False(prices.ContainsKey(Thin));
        Assert.Equal(3, rates.Prices(standard: true)![X].Chaos);

        foreach (double rate in new[] { 471.37, 1.5, 1, 0.73, 1.0 / 471.37, 64999.5 })
        {
            var (give, get) = ExchangeRatio.Below(rate)!.Value;
            Assert.True((double)give / get <= rate, $"{give}:{get} pays more than {rate}");
            Assert.True((double)give / get >= rate * 0.99, $"{give}:{get} is far below {rate}");
        }
        Assert.Null(ExchangeRatio.Below(70000));
        Assert.Null(ExchangeRatio.Below(0));

        // An order selling 10 of a card for at least 150 chaos each. The card was 100, reached 200 while the player was away, and is 100 again: it fills
        // in those hours and no others, one card an hour, at those hours' prices. Above all, the peak before it was placed does not count.
        const long Now = 500 * 3600;
        const uint Chaos = 1, Card = 2;
        var card = new ExchangePrice(100, 1000, [[490, 300], [496, 100], [497, 200], [499, 100]]);
        var market = new ExchangeMarket(new(Now, "L", [], Hours: 100), new() { [ExchangeRates.Chaos] = new(1, 1e9), ["card"] = card },
            hash => hash == Chaos ? ExchangeRates.Chaos : hash == Card ? "card" : null, spreadScale: 1, volumeShare: .1);
        var order = new ExchangeOrder { Id = 1, Want = Chaos, Have = Card, WantCount = 1500, HaveCount = 10, Remaining = 10, RatioWant = 150, RatioHave = 1,
            State = InstanceCurrencyExchange.OrderState.Listed, LastHour = 495 * 3600 };
        var book = new ExchangeBook { Orders = [order] };

        Assert.Equal([order], book.Settle(market));
        Assert.Equal(InstanceCurrencyExchange.OrderState.Listed, order.State);
        Assert.Equal(Now, order.LastHour);
        // Hours 497 and 498, 180 chaos a card (200 less the 10% of a thin item), and the room of one card's worth an hour.
        Assert.Equal((uint)(2 * 200), order.ClaimWant);
        Assert.Equal(10u - 3, order.Remaining); // 400 chaos at 180 is 2.2 cards, so a third is spoken for
        Assert.Empty(book.Settle(market)); // the same hour again changes nothing

        // A trade's spread is the thinner item's, and a busy pair's is tight.
        Assert.Equal(.01, ExchangeMarket.SpreadFor(100_000));
        Assert.Equal(.10, ExchangeMarket.SpreadFor(999));
    }
}
