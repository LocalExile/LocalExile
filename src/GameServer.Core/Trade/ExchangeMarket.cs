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
/// <param name="patientRange">How much of the way from the market's price to the other side's a patient order fills: 1 all of it, as a live market does, which
/// lets a player earn the spread; about 0.5 only up to the price itself, which does not; 0 none.</param>
/// <param name="patientSpeed">Divides how long a patient order waits.</param>
/// <param name="catchUpHours">How many missed hours an order catches up on.</param>
public sealed class ExchangeMarket(ExchangeRates rates, Dictionary<string, ExchangePrice> prices, Func<uint, string?> idOf, double spreadScale, double volumeShare,
    double patientRange = 1, double patientSpeed = 1, int catchUpHours = 168)
{
    /// <summary>How many missed hours an order catches up on; 0 is the current hour alone.</summary>
    public int CatchUpHours => catchUpHours;

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

    /// <summary>The steps of a market, as many as the window shows (<c>0x140b94550</c>): how far each is from the price, in spreads past the first, and
    /// its share of the hour's room. The digests hold no order book, so the shape is a judgement: more, further from the price.</summary>
    static readonly (double Steps, double Share)[] Ladder = [(0, .10), (.4, .12), (.9, .15), (1.6, .18), (2.4, .20), (3.5, .25)];

    /// <summary>What Faustus has for sale of an item against another at an hour (now, by default), best first, without what this hour's orders took of it.
    /// Each step has room for at least one of the dearer item, or a rare one would never trade. Same hour, same steps: the stock only looks alive.</summary>
    public IReadOnlyList<InstanceCurrencyExchange.MarketLevel> Levels(uint give, uint get, long? hour = null, Dictionary<uint, double>? used = null)
    {
        long at = hour ?? Hour;
        if (PriceOf(give) is not { } g || PriceOf(get) is not { } p || ChaosAt(give, at) is not { } giveChaos || ChaosAt(get, at) is not { } getChaos) return [];
        double spread = Math.Min(.5, Math.Max(SpreadFor(g.Volume), SpreadFor(p.Volume)) * spreadScale), step = Math.Max(spread, .005);
        double room = Math.Min(RoomOf(give), RoomOf(get)), dearer = Math.Max(giveChaos, getChaos);
        double taken = Math.Max(used?.GetValueOrDefault(give) ?? 0, used?.GetValueOrDefault(get) ?? 0);
        var levels = new List<InstanceCurrencyExchange.MarketLevel>();
        // Not HashCode: it changes with every run of the server, and the same hour must have the same steps.
        var rng = new Random((int)(((give * 31 + get) * 31 + (ulong)(at / 3600)) % int.MaxValue));
        foreach (var (steps, share) in Ladder)
        {
            double value = double.IsInfinity(room) ? Unlimited * giveChaos : Math.Max(dearer, room * share * (.7 + rng.NextDouble() * .6));
            double left = Math.Min(value, taken);
            taken -= left;
            ulong stock = (ulong)Math.Min(Unlimited, Math.Floor((value - left) / giveChaos + 1e-9));
            if (stock == 0) continue;
            if (ExchangeRatio.Below(getChaos / giveChaos * (1 - Math.Min(.6, spread + step * steps))) is not var (a, b)) continue;
            if (levels.Count > 0 && levels[^1] is var last && last.Give == a && last.Get == b) levels[^1] = last with { Stock = last.Stock + stock };
            else levels.Add(new(a, b, stock));
        }
        return levels;
    }

    /// <summary>How long someone takes to come to an order at the far end of the spread, by how much of the thinner item trades. A judgement: the digests do not
    /// say how long orders wait.</summary>
    public static double PatientHoursFor(double weekVolume) => weekVolume >= 100_000 ? 5.0 / 60 : weekVolume >= 10_000 ? .5 : weekVolume >= 1_000 ? 2 : 12;

    /// <summary>The seconds until an order asking more than the market gives, but no more than the other side asks, is taken as it stands: nothing at the market's
    /// price, the most at the other side's. Null for an order outside that, which waits for the price to move.</summary>
    public double? PatientWait(uint want, uint have, uint wantCount, uint haveCount, uint seed)
    {
        if (patientRange <= 0 || Trade(want, have) is not var (give, get) || Trade(have, want) is not var (otherGive, otherGet)) return null;
        if (PriceOf(want) is not { } w || PriceOf(have) is not { } h) return null;
        double market = (double)give / get, other = (double)otherGet / otherGive, asked = (double)wantCount / haveCount;
        if (asked <= market || asked > other) return null;
        double position = (asked - market) / (other - market);
        if (position > patientRange) return null;
        double jitter = .7 + new Random((int)(seed & int.MaxValue)).NextDouble() * .6;
        return PatientHoursFor(Math.Min(w.Volume, h.Volume)) * 3600 * position * jitter / Math.Max(patientSpeed, .01);
    }

    /// <summary>Takes from an hour's room what an order bought, in chaos.</summary>
    internal void Take(uint want, uint have, double chaos, Dictionary<uint, double> used)
    {
        if (!IsChaos(want)) used[want] = used.GetValueOrDefault(want) + chaos;
        if (!IsChaos(have)) used[have] = used.GetValueOrDefault(have) + chaos;
    }

    internal double ChaosOf(uint item, long hour) => ChaosAt(item, hour) ?? 0;
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

    /// <summary>When it was placed, unix seconds.</summary>
    public long PlacedAt { get; set; }

    /// <summary>Buys down an hour's steps, best first, as far as the order's ratio allows: "market orders can only ever trade at or above your chosen ratio".
    /// True when anything filled.</summary>
    internal bool Advance(ExchangeMarket market, long hour, Dictionary<uint, double> used)
    {
        if (State != InstanceCurrencyExchange.OrderState.Listed) return false;
        long fill = 0, wanted = WantCount - Bought;
        double chaos = market.ChaosOf(Want, hour);
        foreach (var level in market.Levels(Want, Have, hour, used))
        {
            if (wanted <= fill || (long)WantCount * level.Get > (long)HaveCount * level.Give) break;
            long bought = Math.Min(wanted - fill, (long)Math.Min(level.Stock, long.MaxValue));
            Paid += (double)bought * level.Get / level.Give;
            fill += bought;
        }
        if (fill <= 0) return false;
        market.Take(Want, Have, fill * chaos, used);
        Record(fill);
        return true;
    }

    /// <summary>Whether someone has come to this order by now, and the hour's market has room for it.</summary>
    internal bool PatientDue(ExchangeMarket market, long now, Dictionary<uint, double> used) =>
        State == InstanceCurrencyExchange.OrderState.Listed && PlacedAt > 0 && market.PatientWait(Want, Have, WantCount, HaveCount, Id) is { } wait && now - PlacedAt >= wait
        && market.Levels(Want, Have, used: used).Count > 0;

    /// <summary>Fills what the hour has room for at the order's own price: it is what the other side came to.</summary>
    internal bool AdvancePatient(ExchangeMarket market, long now, Dictionary<uint, double> used)
    {
        if (!PatientDue(market, now, used)) return false;
        ulong room = market.Levels(Want, Have, used: used).Aggregate(0UL, (sum, level) => sum + level.Stock);
        long fill = (long)Math.Min(room, WantCount - Bought);
        if (fill <= 0) return false;
        Paid += (double)fill * HaveCount / WantCount;
        market.Take(Want, Have, fill * market.ChaosOf(Want, market.Hour), used);
        Record(fill);
        return true;
    }

    void Record(long fill)
    {
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
    }
}

/// <summary>A player's orders in a league, and what they have used of this hour's market.</summary>
public sealed class ExchangeBook
{
    public uint NextId { get; set; } = 1;
    public List<ExchangeOrder> Orders { get; set; } = [];
    public long UsedHour { get; set; }
    public Dictionary<uint, double> Used { get; set; } = [];

    public static ExchangeBook Parse(string? json) => json is null ? new() : JsonSerializer.Deserialize<ExchangeBook>(json) ?? new();
    public string Serialize() => JsonSerializer.Serialize(this);

    /// <summary>What this hour's orders took from the market, for showing it.</summary>
    public Dictionary<uint, double>? UsedAt(long hour) => UsedHour == hour ? Used : null;

    /// <summary>Whether an order has anything waiting to be claimed.</summary>
    public bool HasClaims => Orders.Exists(o => o.State != InstanceCurrencyExchange.OrderState.Listed && o.ClaimWant + o.ClaimHave > 0);

    /// <summary>Whether <see cref="Settle"/> has anything to do: a new hour, or a patient order someone has come to.</summary>
    public bool SettleDue(ExchangeMarket market, long now) => Orders.Exists(o => o.State == InstanceCurrencyExchange.OrderState.Listed
        && (o.LastHour < market.Hour || o.PlacedAt == 0 || o.PatientDue(market, now, UsedAt(market.Hour) ?? [])));

    /// <summary>Runs every hour since each listed order last saw the market, as if the player had been there: only hours after it was placed, so an
    /// order cannot be placed at a peak already past. Then the patient orders someone has come to by <paramref name="now"/> (unix seconds). The orders that
    /// filled anything.</summary>
    public List<ExchangeOrder> Settle(ExchangeMarket market, long now)
    {
        var listed = Orders.Where(o => o.State == InstanceCurrencyExchange.OrderState.Listed).ToList();
        var changed = new List<ExchangeOrder>();
        if (listed.Count == 0) return changed;
        foreach (var order in listed)
        {
            if (order.LastHour == 0) order.LastHour = market.Hour - 3600;
            if (order.PlacedAt == 0) order.PlacedAt = now;
        }
        long first = Math.Max(listed.Min(o => o.LastHour) + 3600, market.Hour - Math.Max(market.CatchUpHours - 1, 0) * 3600L);
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
        if (UsedHour != market.Hour)
        {
            UsedHour = market.Hour;
            Used.Clear();
        }
        foreach (var order in listed)
            if (order.AdvancePatient(market, now, Used) && !changed.Contains(order)) changed.Add(order);
        return changed;
    }
}
