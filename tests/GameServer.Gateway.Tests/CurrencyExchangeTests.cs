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
            Rare = "Metadata/Items/Currency/Rare", Dear = "Metadata/Items/Currency/Dear", Stray = "Metadata/Items/Currency/Stray";
        static string Market(string league, string a, string b, int va, int vb) =>
            $$$"""{"league":"{{{league}}}","market_pair":["{{{a}}}","{{{b}}}"],"volume_traded":{"{{{a}}}":{{{va}}},"{{{b}}}":{{{vb}}}}}""";
        static string Digest(int hour, params string[] markets) =>
            $$$"""{"next_change_id":{{{hour + 3600}}},"markets":[{{{string.Join(',', markets)}}}]}""";

        var rates = ExchangeRates.FromDigests(
        [
            Digest(100 * 3600, Market("Allflame", ExchangeRates.Divine, ExchangeRates.Chaos, 1, 500), Market("Private (PL1)", X, ExchangeRates.Chaos, 1, 900),
                Market("Allflame", Dear, ExchangeRates.Chaos, 9, 90), Market("Allflame", Dear, ExchangeRates.Divine, 10, 2),
                Market("Allflame", Stray, ExchangeRates.Chaos, 1, 5)),
            Digest(99 * 3600, Market("Allflame", ExchangeRates.Divine, ExchangeRates.Chaos, 1, 400),
                Market("Allflame", X, ExchangeRates.Chaos, 100, 50), Market("Allflame", Thin, ExchangeRates.Chaos, 1, 4),
                Market("Allflame", ViaDivine, ExchangeRates.Divine, 200, 1), Market("Standard", X, ExchangeRates.Chaos, 10, 30)),
            // Days ago: it prices what the last day cannot, and moves nothing that it can.
            Digest(50 * 3600, Market("Allflame", ExchangeRates.Divine, ExchangeRates.Chaos, 1, 900), Market("Allflame", Rare, ExchangeRates.Chaos, 2, 10),
                Market("Allflame", Stray, ExchangeRates.Chaos, 9, 995)),
        ]);

        Assert.Equal(100 * 3600, rates.SourceHour);
        Assert.Equal(5, rates.Prices(standard: false)![Rare].Chaos);
        Assert.Equal(90, rates.Prices(standard: false)![Dear].Chaos); // its busiest pair, 2 divines for 10, not its 9 chaos trades
        Assert.Equal(100, rates.Prices(standard: false)![Stray].Chaos); // one trade today does not move a week's market
        Assert.Equal("Allflame", rates.Current);
        Assert.DoesNotContain("Private (PL1)", rates.Leagues.Keys);
        var prices = rates.Prices(standard: false)!;
        Assert.Equal(450, prices[ExchangeRates.Divine].Chaos); // the volumes of both hours, not one trade's price
        Assert.Equal(0.5, prices[X].Chaos);
        Assert.Equal(450.0 / 200, prices[ViaDivine].Chaos);
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
    }
}
