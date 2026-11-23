using System.Text.Json;

namespace GameServer.Core.Trade;

/// <summary>An item's value in chaos, and how much chaos of it traded over all the digests.</summary>
/// <param name="Chaos">Its value now.</param>
/// <param name="Volume">The chaos traded.</param>
/// <param name="History">Its value through the digests' hours, when it changed: [hour number (unix seconds / 3600), chaos], 0 for no price then.</param>
public sealed record ExchangePrice(double Chaos, double Volume, double[][]? History = null);

/// <summary>
/// The official leagues' Currency Exchange prices, as a mod publishes them (<c>ExchangeRateStore.Publish</c>) and the module trades at them. Every price is
/// in chaos, so that no path through several items pays more than it costs.
/// </summary>
/// <param name="SourceHour">The latest digest's hour, unix seconds.</param>
/// <param name="Current">The challenge league with the most markets.</param>
/// <param name="Leagues">League name, then the item's <c>BaseItemTypes</c> id.</param>
/// <param name="Hours">How many hours of digests the volumes cover.</param>
public sealed record ExchangeRates(long SourceHour, string Current, Dictionary<string, Dictionary<string, ExchangePrice>> Leagues, int Hours = 1)
{
    public const string Chaos = "Metadata/Items/Currency/CurrencyRerollRare", Divine = "Metadata/Items/Currency/CurrencyModValues";
    public const string Standard = "Standard";

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static ExchangeRates? Parse(string json) => JsonSerializer.Deserialize<ExchangeRates>(json, Options);

    /// <summary>The prices every league of this server trades at: official Standard's, or the current challenge league's.</summary>
    public Dictionary<string, ExchangePrice>? Prices(bool standard) => Leagues.GetValueOrDefault(standard ? Standard : Current);
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
