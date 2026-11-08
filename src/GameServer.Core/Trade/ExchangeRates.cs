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

    /// <summary>An item below this much chaos traded is too thin to price.</summary>
    public const double MinChaosVolume = 5;

    /// <summary>How many of the newest hours make an item's price when it traded enough in them.</summary>
    public const int RecentHours = 24;

    /// <summary>What share of its volume over all the digests an item needs in those hours: less is a stray trade, not the market moving.</summary>
    public const double MinRecentShare = 0.05;

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>One digest's trades of an item for chaos or divines.</summary>
    readonly record struct Trade(double Count, double Paid, bool InDivines, bool Recent);

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    public static ExchangeRates? Parse(string json) => JsonSerializer.Deserialize<ExchangeRates>(json, Options);

    /// <summary>The prices every league of this server trades at: official Standard's, or the current challenge league's.</summary>
    public Dictionary<string, ExchangePrice>? Prices(bool standard) => Leagues.GetValueOrDefault(standard ? Standard : Current);

    /// <summary>Prices from GGG's digests (<c>/api/currency-exchange/&lt;hour&gt;</c>), <b>newest first</b>. An item traded enough in the last
    /// <see cref="RecentHours"/> has that price, which follows the market; a rarer one has its price over all the digests.</summary>
    public static ExchangeRates FromDigests(IEnumerable<string> digests)
    {
        var trades = new Dictionary<string, Dictionary<string, List<Trade>>>();
        long newest = 0;
        foreach (string digest in digests)
        {
            using var document = JsonDocument.Parse(digest);
            var root = document.RootElement;
            long hour = root.GetProperty("next_change_id").GetInt64() - 3600;
            newest = Math.Max(newest, hour);
            bool recent = hour > newest - RecentHours * 3600;
            foreach (var market in root.GetProperty("markets").EnumerateArray())
            {
                string? league = market.GetProperty("league").GetString();
                if (league is null || league.Contains("(PL", StringComparison.Ordinal)) continue;
                var pair = market.GetProperty("market_pair");
                string a = pair[0].GetString()!, b = pair[1].GetString()!;
                var traded = market.GetProperty("volume_traded");
                if (!traded.TryGetProperty(a, out var va) || !traded.TryGetProperty(b, out var vb) || va.GetDouble() <= 0 || vb.GetDouble() <= 0) continue;
                Add(a, b, va.GetDouble(), vb.GetDouble());
                Add(b, a, vb.GetDouble(), va.GetDouble());

                void Add(string item, string hub, double count, double paid)
                {
                    if (hub is not (Chaos or Divine)) return;
                    if (!trades.TryGetValue(league, out var items)) trades[league] = items = [];
                    if (!items.TryGetValue(item, out var list)) items[item] = list = [];
                    list.Add(new(count, paid, hub == Divine, recent));
                }
            }
        }
        var leagues = trades.ToDictionary(l => l.Key, l =>
        {
            var prices = PricesOver(l.Value, recentOnly: false);
            foreach (var (item, price) in PricesOver(l.Value, recentOnly: true))
                if (!prices.TryGetValue(item, out var week) || price.Volume >= week.Volume * MinRecentShare) prices[item] = price;
            return prices;
        });
        string current = leagues.Where(l => l.Key != Standard && !l.Key.StartsWith("Hardcore", StringComparison.Ordinal))
            .OrderByDescending(l => l.Value.Count).Select(l => l.Key).FirstOrDefault() ?? Standard;
        return new(newest, current, leagues);
    }

    static Dictionary<string, ExchangePrice> PricesOver(Dictionary<string, List<Trade>> items, bool recentOnly)
    {
        var prices = new Dictionary<string, ExchangePrice>();
        bool Counts(Trade t) => !recentOnly || t.Recent;
        double divine = items.TryGetValue(Divine, out var divines) && Median([.. divines.Where(t => Counts(t) && !t.InDivines).Select(t => (t.Paid / t.Count, t.Count))]) is { } value ? value : 0;
        double chaosVolume = 0;
        foreach (var (item, list) in items)
        {
            if (item == Chaos) continue;
            // In chaos, each hour's trades at their own price. A divine pair counts with the chaos pair: dear items trade for divines, and
            // their few chaos trades can be far off (The Doctor, in a day: 9 at 231 chaos, 96 at 665 in divines).
            var priced = list.Where(t => Counts(t) && (!t.InDivines || divine > 0)).Select(t => (Chaos: t.InDivines ? t.Paid * divine : t.Paid, t.Count)).ToList();
            double volume = priced.Sum(t => t.Chaos);
            chaosVolume += list.Where(t => Counts(t) && !t.InDivines).Sum(t => t.Paid);
            if (volume >= MinChaosVolume && Median([.. priced.Select(t => (t.Chaos / t.Count, t.Count))]) is { } price) prices[item] = new(price, volume);
        }
        if (prices.Count > 0) prices[Chaos] = new(1, chaosVolume);
        return prices;
    }

    /// <summary>The price of the middle item traded. An average by chaos follows the dear trades, which are what a mistake or a scam looks like
    /// (The Forward Gaze, in a week: 4 at 1 chaos beside 4 at about 133), and the median by items does not.</summary>
    static double? Median(IReadOnlyList<(double Price, double Count)> trades)
    {
        double half = trades.Sum(t => t.Count) / 2, passed = 0;
        foreach (var (price, count) in trades.OrderBy(t => t.Price))
            if ((passed += count) >= half) return price;
        return null;
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
