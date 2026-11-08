using System.Text.Json;

namespace GameServer.Core.Trade;

/// <summary>An item's value in chaos, and how much chaos of it traded.</summary>
public sealed record ExchangePrice(double Chaos, double Volume);

/// <summary>
/// The official leagues' Currency Exchange prices: what the rates service computes from GGG's hourly digests and the
/// Gateway's module trades at. Every price is in chaos, so that no path through several items pays more than it costs.
/// </summary>
/// <param name="SourceHour">The latest digest's hour, unix seconds.</param>
/// <param name="Current">The challenge league with the most markets.</param>
/// <param name="Leagues">League name, then the item's <c>BaseItemTypes</c> id.</param>
public sealed record ExchangeRates(long SourceHour, string Current, Dictionary<string, Dictionary<string, ExchangePrice>> Leagues)
{
    public const string Chaos = "Metadata/Items/Currency/CurrencyRerollRare", Divine = "Metadata/Items/Currency/CurrencyModValues";
    public const string Standard = "Standard";

    /// <summary>A pair below this much chaos in the digests is too thin to price: single trades there reach several times the value.</summary>
    public const double MinChaosVolume = 20;

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    public static ExchangeRates? Parse(string json) => JsonSerializer.Deserialize<ExchangeRates>(json, Options);

    /// <summary>The league whose prices a league of this server trades at: Standard at Standard's, every other at the current challenge league's.</summary>
    public Dictionary<string, ExchangePrice>? PricesFor(string league, string? choice = null) =>
        Leagues.GetValueOrDefault(string.IsNullOrEmpty(choice) ? league == Standard ? Standard : Current : choice);

    /// <summary>Prices from GGG's digests (<c>/api/currency-exchange/&lt;hour&gt;</c>): each pair's chaos traded over the items traded, over all the
    /// digests, so one outlier trade moves little. An item with no chaos pair is priced through its divine pair.</summary>
    public static ExchangeRates FromDigests(IEnumerable<string> digests)
    {
        var volumes = new Dictionary<string, Dictionary<(string A, string B), (double A, double B)>>();
        long hour = 0;
        foreach (string digest in digests)
        {
            using var document = JsonDocument.Parse(digest);
            var root = document.RootElement;
            hour = Math.Max(hour, root.GetProperty("next_change_id").GetInt64() - 3600);
            foreach (var market in root.GetProperty("markets").EnumerateArray())
            {
                string? league = market.GetProperty("league").GetString();
                if (league is null || league.Contains("(PL", StringComparison.Ordinal)) continue;
                var pair = market.GetProperty("market_pair");
                string a = pair[0].GetString()!, b = pair[1].GetString()!;
                var traded = market.GetProperty("volume_traded");
                if (!traded.TryGetProperty(a, out var va) || !traded.TryGetProperty(b, out var vb)) continue;
                if (!volumes.TryGetValue(league, out var pairs)) volumes[league] = pairs = [];
                var sum = pairs.GetValueOrDefault((a, b));
                pairs[(a, b)] = (sum.A + va.GetDouble(), sum.B + vb.GetDouble());
            }
        }
        var leagues = volumes.ToDictionary(l => l.Key, l => Prices(l.Value));
        string current = leagues.Where(l => l.Key != Standard && !l.Key.StartsWith("Hardcore", StringComparison.Ordinal))
            .OrderByDescending(l => l.Value.Count).Select(l => l.Key).FirstOrDefault() ?? Standard;
        return new(hour, current, leagues);
    }

    static Dictionary<string, ExchangePrice> Prices(Dictionary<(string A, string B), (double A, double B)> pairs)
    {
        // Against the hub, as (the other item's count, the hub's count).
        Dictionary<string, (double Count, double Hub)> Against(string hub)
        {
            var found = new Dictionary<string, (double, double)>();
            foreach (var ((a, b), (va, vb)) in pairs)
            {
                if (va <= 0 || vb <= 0) continue;
                if (a == hub) found[b] = (vb, va);
                else if (b == hub) found[a] = (va, vb);
            }
            return found;
        }

        var prices = new Dictionary<string, ExchangePrice>();
        var chaos = Against(Chaos);
        foreach (var (item, (count, paid)) in chaos)
            if (paid >= MinChaosVolume) prices[item] = new(paid / count, paid);
        if (prices.Count == 0) return prices;
        prices[Chaos] = new(1, chaos.Values.Sum(v => v.Hub));
        if (prices.GetValueOrDefault(Divine) is { } divine)
            foreach (var (item, (count, paid)) in Against(Divine))
                if (!prices.ContainsKey(item) && paid * divine.Chaos >= MinChaosVolume)
                    prices[item] = new(paid * divine.Chaos / count, paid * divine.Chaos);
        return prices;
    }
}

/// <summary>A rate as the whole numbers the Currency Exchange trades in.</summary>
public static class ExchangeRatio
{
    public const int Max = 65000;

    /// <summary>The numbers closest to <paramref name="rate"/> (what one item of the other side buys) that buy no more than it does:
    /// precise to a thousandth, or as near as <see cref="Max"/> allows. Null when the rate does not fit.</summary>
    public static (ushort Give, ushort Get)? Below(double rate)
    {
        if (!(rate > 0) || double.IsInfinity(rate)) return null;
        const int Precision = 1000;
        long give, get;
        if (rate >= 1)
        {
            get = Math.Max(1, Math.Min(Precision, (long)(Max / rate)));
            give = (long)Math.Floor(rate * get);
        }
        else
        {
            give = Math.Max(1, Math.Min(Precision, (long)(Max * rate)));
            get = (long)Math.Ceiling(give / rate);
        }
        if (give < 1 || give > Max || get > Max) return null;
        long divisor = System.Numerics.BigInteger.GreatestCommonDivisor(give, get) is var d ? (long)d : 1;
        return ((ushort)(give / divisor), (ushort)(get / divisor));
    }
}
