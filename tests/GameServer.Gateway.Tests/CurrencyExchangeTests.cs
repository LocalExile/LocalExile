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
        Assert.Equal(1, InstanceCurrencyExchange.OrderChanged(order, placed: true)[^1]); // Faustus speaks of it
    }

    [Fact]
    public void RatesAreReadAndOrdersFillOnlyAtPricesTheMarketReaches()
    {
        // As a mod publishes them: the field names matter, as the mod has its own copy of the shape.
        var rates = ExchangeRates.Parse("""{"sourceHour":7200,"current":"Allflame","hours":24,"leagues":{"Allflame":{"x":{"chaos":0.5,"volume":50,"history":[[1,0.4]]}},"Standard":{"x":{"chaos":3,"volume":30}}}}""")!;
        Assert.Equal((7200, "Allflame", 24), (rates.SourceHour, rates.Current, rates.Hours));
        Assert.Equal(0.5, rates.Prices(standard: false)!["x"].Chaos);
        Assert.Equal(0.4, rates.Prices(standard: false)!["x"].History![0][1]);
        Assert.Equal(3, rates.Prices(standard: true)!["x"].Chaos);

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
        var card = new ExchangePrice(100, 999, [[490, 300], [496, 100], [497, 200], [499, 100]]);
        var market = new ExchangeMarket(new(Now, "L", [], Hours: 100), new() { [ExchangeRates.Chaos] = new(1, 1e9), ["card"] = card },
            hash => hash == Chaos ? ExchangeRates.Chaos : hash == Card ? "card" : null, spreadScale: 1, volumeShare: .1);
        var order = new ExchangeOrder { Id = 1, Want = Chaos, Have = Card, WantCount = 1500, HaveCount = 10, Remaining = 10, RatioWant = 150, RatioHave = 1,
            State = InstanceCurrencyExchange.OrderState.Listed, LastHour = 495 * 3600 };
        var book = new ExchangeBook { Orders = [order] };

        Assert.Equal([order], book.Settle(market, Now));
        Assert.Equal(InstanceCurrencyExchange.OrderState.Listed, order.State);
        Assert.Equal(Now, order.LastHour);
        // Hours 497 and 498, down the steps a card's worth each from 180 chaos a card (200 less the 10% of a thin item), as far as the order's 150 allows: three
        // of them, to 162. Each hour that is 600 chaos, for 200/180 + 200/172 + 200/162 = 3.5 cards.
        Assert.Equal((uint)(2 * 600), order.ClaimWant);
        Assert.Equal(10u - 8, order.Remaining);
        Assert.Empty(book.Settle(market, Now)); // the same hour again changes nothing
        Assert.Equal(6, market.Levels(Card, Chaos).Count);
        Assert.Empty(market.Levels(Chaos, Card, used: new() { [Card] = 1e6 })); // nothing left of the hour: no stock until the next

        // Between the market's 111 chaos and the other side's 90, an order offering 100 waits, and then is taken at its own price.
        var patient = new ExchangeOrder { Id = 2, Want = Card, Have = Chaos, WantCount = 1, HaveCount = 100, Remaining = 100, RatioWant = 1, RatioHave = 100,
            State = InstanceCurrencyExchange.OrderState.Listed, LastHour = Now, PlacedAt = Now };
        var waiting = new ExchangeBook { Orders = [patient], UsedHour = Now };
        Assert.False(waiting.SettleDue(market, Now));
        Assert.Empty(waiting.Settle(market, Now));
        Assert.True(waiting.SettleDue(market, Now + 12 * 3600));
        Assert.Equal([patient], waiting.Settle(market, Now + 12 * 3600));
        Assert.Equal(InstanceCurrencyExchange.OrderState.Completed, patient.State);
        Assert.Equal((1u, 0u), (patient.ClaimWant, patient.ClaimHave));
        var low = new ExchangeOrder { Id = 3, Want = Card, Have = Chaos, WantCount = 1, HaveCount = 80, Remaining = 80, RatioWant = 1, RatioHave = 80,
            State = InstanceCurrencyExchange.OrderState.Listed, LastHour = Now, PlacedAt = Now };
        Assert.Empty(new ExchangeBook { Orders = [low], UsedHour = Now }.Settle(market, Now + 999 * 3600)); // below the other side: only the price moving fills it
        Assert.Null(new ExchangeMarket(new(Now, "L", [], Hours: 100), new() { [ExchangeRates.Chaos] = new(1, 1e9), ["card"] = card },
            hash => hash == Chaos ? ExchangeRates.Chaos : "card", 1, .1, patientRange: 0).PatientWait(Card, Chaos, 1, 100, 2));

        // A trade's spread is the thinner item's, and a busy pair's is tight.
        Assert.Equal(.01, ExchangeMarket.SpreadFor(100_000));
        Assert.Equal(.10, ExchangeMarket.SpreadFor(999));
    }
}
