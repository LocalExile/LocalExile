using System.Runtime.CompilerServices;
using System.Text.Json;
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
