using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Trade;

/// <summary>The market a player's orders trade on. Faustus takes the other side of each one at the official price less a spread, and only as much in
/// an hour as a share of what the official market trades.</summary>
/// <param name="rates">The prices and how many hours they cover.</param>
/// <param name="prices">The official league's prices.</param>
/// <param name="idOf">An item's <c>BaseItemTypes</c> id by its type hash.</param>
/// <param name="spreadScale">Multiplies <see cref="SpreadFor"/>.</param>
/// <param name="volumeShare">The share of an item's hourly volume an hour can trade; 0 for no limit.</param>
public sealed class ExchangeMarket(ExchangeRates rates, Dictionary<string, ExchangePrice> prices, Func<uint, string?> idOf, double spreadScale, double volumeShare)
{
    /// <summary>What the window shows as stock when nothing limits it.</summary>
    public const ulong Unlimited = 1_000_000;

    /// <summary>The hour the prices are of, unix seconds.</summary>
    public long Hour => rates.SourceHour;

    /// <summary>How far from the price a side trades. The digests have no real bid and ask, so this is a judgement: a busy market is tight, and a thin
    /// one's price is less sure.</summary>
    public static double SpreadFor(double weekVolume) => weekVolume >= 100_000 ? .01 : weekVolume >= 10_000 ? .02 : weekVolume >= 1_000 ? .05 : .10;

    ExchangePrice? PriceOf(uint item) => idOf(item) is { } id ? prices.GetValueOrDefault(id) : null;

    bool IsChaos(uint item) => idOf(item) == ExchangeRates.Chaos;

    /// <summary>An item's price at an hour: the current one, or its history's. Null when it had none then.</summary>
    double? ChaosAt(uint item, long hour)
    {
        if (PriceOf(item) is not { } price) return null;
        if (hour >= Hour || IsChaos(item)) return price.Chaos;
        var history = price.History;
        if (history is null) return null;
        double hourNumber = hour / 3600, found = 0;
        foreach (var change in history)
        {
            if (change[0] > hourNumber) break;
            found = change[1];
        }
        return found > 0 ? found : null;
    }

    public bool Trades(uint item) => PriceOf(item) is not null;

    /// <summary>What Faustus gives of an item for what he gets of another, at an hour (now, by default): never more than the two prices less the spread
    /// of the thinner one.</summary>
    public (ushort Give, ushort Get)? Trade(uint give, uint get, long? hour = null)
    {
        long at = hour ?? Hour;
        if (PriceOf(give) is not { } g || PriceOf(get) is not { } p || ChaosAt(give, at) is not { } giveChaos || ChaosAt(get, at) is not { } getChaos) return null;
        double spread = Math.Min(.5, Math.Max(SpreadFor(g.Volume), SpreadFor(p.Volume)) * spreadScale);
        return ExchangeRatio.Below(getChaos / giveChaos * (1 - spread));
    }

    /// <summary>How much chaos of an item an hour can trade.</summary>
    double RoomOf(uint item) => IsChaos(item) || volumeShare <= 0 || PriceOf(item) is not { } price
        ? double.PositiveInfinity : volumeShare * price.Volume / Math.Max(rates.Hours, 1);

    public IReadOnlyList<InstanceCurrencyExchange.MarketLevel> Levels(uint give, uint get)
    {
        if (Trade(give, get) is not var (a, b)) return [];
        double room = Math.Min(RoomOf(give), RoomOf(get));
        ulong stock = double.IsInfinity(room) || ChaosAt(give, Hour) is not { } chaos ? Unlimited : (ulong)Math.Clamp(room / chaos, 1, Unlimited);
        return [new(a, b, stock)];
    }

    /// <summary>How many of what an order wants this hour can fill, and that much of its room used. An hour always has room for one of the dearer item,
    /// or a rare one would never trade.</summary>
    internal long Fill(uint want, uint have, long wanted, long hour, Dictionary<uint, double> used)
    {
        if (ChaosAt(want, hour) is not { } wantChaos || ChaosAt(have, hour) is not { } haveChaos) return 0;
        if (volumeShare <= 0) return wanted;
        double usedWant = used.GetValueOrDefault(want), usedHave = used.GetValueOrDefault(have);
        double room = Math.Min(RoomOf(want) - usedWant, RoomOf(have) - usedHave);
        if (usedWant == 0 && usedHave == 0) room = Math.Max(room, Math.Max(wantChaos, haveChaos));
        long fill = Math.Min(wanted, (long)Math.Floor(Math.Min(room / wantChaos, wanted)));
        if (fill <= 0) return 0;
        if (!IsChaos(want)) used[want] = usedWant + fill * wantChaos;
        if (!IsChaos(have)) used[have] = usedHave + fill * wantChaos;
        return fill;
    }
}

/// <summary>An order as it is saved. What it pays comes off <see cref="Remaining"/> as it fills, and what it bought waits in <see cref="ClaimWant"/>.</summary>
public sealed class ExchangeOrder
{
    public uint Id { get; set; }
    public uint Want { get; set; }
    public uint Have { get; set; }
    public uint WantCount { get; set; }
    public uint HaveCount { get; set; }
    public uint ClaimWant { get; set; }
    public uint ClaimHave { get; set; }
    public uint Remaining { get; set; }
    public ushort RatioWant { get; set; }
    public ushort RatioHave { get; set; }
    public InstanceCurrencyExchange.OrderState State { get; set; }

    /// <summary>How many it has bought, claimed or not.</summary>
    public uint Bought { get; set; }

    /// <summary>What it has paid, unrounded: small fills at a fraction each must not each cost a whole item.</summary>
    public double Paid { get; set; }

    /// <summary>The last hour it was offered to the market, unix seconds.</summary>
    public long LastHour { get; set; }

    /// <summary>Fills what it can at an hour's prices, when they pay what the order asks for the rest. True when anything filled.</summary>
    internal bool Advance(ExchangeMarket market, long hour, Dictionary<uint, double> used)
    {
        if (State != InstanceCurrencyExchange.OrderState.Listed || market.Trade(Want, Have, hour) is not var (give, get)) return false;
        long wanted = WantCount - Bought;
        if (wanted <= 0 || wanted * get > (long)Remaining * give) return false;
        long fill = market.Fill(Want, Have, wanted, hour, used);
        if (fill <= 0) return false;
        Paid += (double)fill * get / give;
        // A whole item is held back as soon as a fraction of it is spent; what the order did not spend comes back when it ends.
        Remaining = HaveCount - (uint)Math.Min(HaveCount, (long)Math.Ceiling(Paid - 1e-9));
        Bought += (uint)fill;
        ClaimWant += (uint)fill;
        if (Bought == WantCount || Remaining == 0)
        {
            ClaimHave += Remaining;
            Remaining = 0;
            State = InstanceCurrencyExchange.OrderState.Completed;
        }
        return true;
    }
}

/// <summary>A player's orders in a league, and what they have used of this hour's market.</summary>
public sealed class ExchangeBook
{
    /// <summary>How far back an order catches up: the history the rates have.</summary>
    const int MaxCatchUpHours = 168;

    public uint NextId { get; set; } = 1;
    public List<ExchangeOrder> Orders { get; set; } = [];
    public long UsedHour { get; set; }
    public Dictionary<uint, double> Used { get; set; } = [];

    public static ExchangeBook Parse(string? json) => json is null ? new() : JsonSerializer.Deserialize<ExchangeBook>(json) ?? new();
    public string Serialize() => JsonSerializer.Serialize(this);

    /// <summary>Runs every hour since each listed order last saw the market, as if the player had been there: only hours after it was placed, so an
    /// order cannot be placed at a peak already past. The orders that filled anything.</summary>
    public List<ExchangeOrder> Settle(ExchangeMarket market)
    {
        var listed = Orders.Where(o => o.State == InstanceCurrencyExchange.OrderState.Listed).ToList();
        var changed = new List<ExchangeOrder>();
        if (listed.Count == 0) return changed;
        foreach (var order in listed)
            if (order.LastHour == 0) order.LastHour = market.Hour - 3600;
        long first = Math.Max(listed.Min(o => o.LastHour) + 3600, market.Hour - (MaxCatchUpHours - 1) * 3600L);
        for (long hour = first; hour <= market.Hour; hour += 3600)
        {
            if (UsedHour != hour)
            {
                UsedHour = hour;
                Used.Clear();
            }
            foreach (var order in listed.Where(o => o.LastHour < hour))
            {
                if (order.Advance(market, hour, Used) && !changed.Contains(order)) changed.Add(order);
                order.LastHour = hour;
            }
        }
        return changed;
    }
}
