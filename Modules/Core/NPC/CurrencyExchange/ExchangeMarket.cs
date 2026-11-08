using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Trade;
using GameServer.Modding;

namespace GameServer.Modules.CurrencyExchange;

/// <summary>What the game's <c>CurrencyExchange</c> table lists, by the type hash the window sends.</summary>
sealed class ExchangeItems
{
    public sealed record Row(string Id, uint Hash, uint Fee, bool ItemsPerGold);

    static readonly Lazy<IReadOnlyList<(string Id, uint Fee, bool ItemsPerGold)>> Table = new(Load);
    static readonly ConditionalWeakTable<IItemCrafting, ExchangeItems> PerDataSet = new();

    public IReadOnlyDictionary<uint, Row> ByHash { get; }

    ExchangeItems(IItemCrafting items) => ByHash = Table.Value
        .Select(r => items.TypeHash(r.Id) is { } hash ? new Row(r.Id, hash, r.Fee, r.ItemsPerGold) : null).OfType<Row>()
        .GroupBy(r => r.Hash).ToDictionary(g => g.Key, g => g.First());

    public static ExchangeItems For(IItemCrafting items) => PerDataSet.GetValue(items, i => new ExchangeItems(i));

    static IReadOnlyList<(string, uint, bool)> Load()
    {
        using var types = JsonDocument.Parse(File.ReadAllBytes("data/tables/BaseItemTypes.json"));
        using var rows = JsonDocument.Parse(File.ReadAllBytes("data/tables/CurrencyExchange.json"));
        var list = new List<(string, uint, bool)>();
        foreach (var row in rows.RootElement.EnumerateArray())
        {
            if (row.GetProperty("Item").ValueKind != JsonValueKind.Number) continue;
            if (!row.GetProperty("EnabledInStandardLeague").GetBoolean() && !row.GetProperty("EnabledInChallengeLeague").GetBoolean()) continue;
            int item = row.GetProperty("Item").GetInt32();
            if (types.RootElement[item].GetProperty("Id").GetString() is { } id)
                list.Add((id, row.GetProperty("GoldPurchaseFee").GetUInt32(), row.GetProperty("__col6").GetBoolean()));
        }
        return list;
    }
}

/// <summary>The prices a league trades at. Faustus takes the other side of every order, at the official price less the spread.</summary>
sealed class ExchangeMarket(IReadOnlyDictionary<string, ExchangePrice> prices, ExchangeItems items, double spread)
{
    public const ulong Stock = 1_000_000;

    ExchangePrice? PriceOf(uint hash) => items.ByHash.TryGetValue(hash, out var row) ? prices.GetValueOrDefault(row.Id) : null;

    /// <summary>What Faustus gives of an item for what he gets of another: he never pays more than the price.</summary>
    public (ushort Give, ushort Get)? Trade(uint give, uint get) => PriceOf(give) is { } g && PriceOf(get) is { } p
        ? ExchangeRatio.Below(p.Chaos / g.Chaos * (1 - spread)) : null;

    public IReadOnlyList<InstanceCurrencyExchange.MarketLevel> Levels(uint give, uint get) => Trade(give, get) is var (a, b)
        ? [new(a, b, Stock)] : [];

    public IReadOnlyList<uint> Partners(uint item) =>
        [.. items.ByHash.Values.Where(r => r.Hash != item && prices.ContainsKey(r.Id)).Select(r => r.Hash)];
}

/// <summary>An order as it is saved, per account and league.</summary>
sealed class SavedOrder
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

    /// <summary>Fills the order when the market pays what it asks: never more than wanted, and what it did not need to pay comes back.</summary>
    public bool TryFill(ExchangeMarket market)
    {
        if (State != InstanceCurrencyExchange.OrderState.Listed || market.Trade(Want, Have) is not var (give, get)
            || (ulong)WantCount * get > (ulong)HaveCount * give) return false;
        uint paid = (uint)Math.Min(HaveCount, ((ulong)WantCount * get + give - 1) / give);
        ClaimWant = WantCount;
        ClaimHave = HaveCount - paid;
        Remaining = 0;
        State = InstanceCurrencyExchange.OrderState.Completed;
        return true;
    }
}

/// <summary>A player's orders and the next order's id.</summary>
sealed class SavedBook
{
    public uint NextId { get; set; } = 1;
    public List<SavedOrder> Orders { get; set; } = [];

    public static SavedBook Parse(string? json) => json is null ? new() : JsonSerializer.Deserialize<SavedBook>(json) ?? new();
    public string Serialize() => JsonSerializer.Serialize(this);
}
