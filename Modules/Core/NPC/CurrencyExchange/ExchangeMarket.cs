using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Modding;
using GameServer.Core;

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
        var types = ClientTables.Table("BaseItemTypes");
        var rows = ClientTables.Table("CurrencyExchange");
        var list = new List<(string, uint, bool)>();
        foreach (var row in rows)
        {
            if (row.IsNull("Item")) continue;
            if (!row.Bool("EnabledInStandardLeague") && !row.Bool("EnabledInChallengeLeague")) continue;
            int item = row.Int("Item");
            if (types[item].Text("Id") is { } id)
                list.Add((id, row.UInt("GoldPurchaseFee"), row.Bool("__col6")));
        }
        return list;
    }
}
