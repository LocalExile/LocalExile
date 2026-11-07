using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.NpcDialogue;

public sealed partial class NpcDialogueModule
{
    const string ExchangeWindow = "currency-exchange";
    sealed class ExchangeSession
    {
        public readonly ConcurrentDictionary<(uint First, uint Second), byte> Pairs = new();
    }
    readonly object _marketCycle = new();
    readonly Dictionary<string, long> _marketVersions = new(StringComparer.OrdinalIgnoreCase);
    readonly ConditionalWeakTable<IPlayer, ExchangeSession> _exchange = new();
    IDisposable? _exchangeTick;
    bool _exchangeHandlersRegistered;
    static string League(ClientState conn) => conn.League ?? "Standard";
    static InstanceCurrencyExchange.Order Wire(CurrencyExchange.Order o)
    {
        int gcd = CurrencyExchange.Gcd(o.Wanted, o.Offered);
        return new(o.Id, o.League, o.Give, o.Want, (uint)o.Offered, (uint)o.RemainingGive, (uint)o.Claim,
            (ushort)(o.Wanted / gcd), (ushort)(o.Offered / gcd), (ulong)o.Created,
            o.Empty ? (byte)4 : o.Active ? (byte)0 : o.Closed ? (byte)3 : (byte)1);
    }
    async Task OpenExchange(IModuleContext module, IPlayer player)
    {
        var conn = ConnOf(player);
        if (conn.Ssf || conn.Account is null)
        {
            await player.Tell("Currency Exchange is unavailable in Solo Self-Found.");
            return;
        }
        EnsureExchangeHandlers(module);
        var world = module.World(player);
        await world.NpcClose("0x0084 close NPC dialogue for currency exchange");
        await world.OpenWindow(ExchangeWindow, InstanceCurrencyExchange.Window, "0x007b open Currency Exchange");
        TrackExchange(module, player);
        await RefreshExchangeMarket(module, player);
        await SendOrders(player);
    }
    static Task SendOrders(IPlayer player)
    {
        var conn = ConnOf(player);
        return player.Send(InstanceCurrencyExchange.Orders(CurrencyExchange.Orders(League(conn), conn.Account)
            .Select(Wire).ToArray(), CurrencyExchange.OrderLimit), "0x02e7 currency exchange orders");
    }
    void TrackExchange(IModuleContext module, IPlayer player)
    {
        lock (_marketCycle)
        {
            _exchange.GetOrCreateValue(player);
            // No exchange tick hook exists until somebody opens the exchange.
            _exchangeTick ??= module.OnTick(async (current, seconds) =>
            {
                if (!_exchange.TryGetValue(current, out _)) return;
                var conn = ConnOf(current);
                if (conn.Closed || conn.LeavingArea || module.World(current).OpenWindowOwner != ExchangeWindow)
                {
                    ForgetExchange(current);
                    return;
                }
                await RefreshExchangeMarket(module, current);
            });
        }
    }
    void ForgetExchange(IPlayer player)
    {
        lock (_marketCycle)
        {
            _exchange.Remove(player);
            if (_exchange.Any()) return;
            _exchangeTick?.Dispose();
            _exchangeTick = null;
        }
    }
    async Task RefreshExchangeMarket(IModuleContext module, IPlayer player)
    {
        var conn = ConnOf(player);
        if (conn.Ssf || conn.Account is null) return;
        string league = League(conn);
        IReadOnlyList<CurrencyExchange.Order> changed;
        lock (_marketCycle)
        {
            long version = ExchangeMarkets.Version;
            if (_marketVersions.TryGetValue(league, out long applied) && applied == version) return;
            changed = CurrencyExchange.SyncMarkets(league, ItemsFor(conn));
            _marketVersions[league] = version;
        }
        await Notify(module, changed, league);
    }
    void EnsureExchangeHandlers(IModuleContext module)
    {
        lock (_marketCycle)
        {
            if (_exchangeHandlersRegistered) return;
            RegisterExchangeHandlers(module);
            _exchangeHandlersRegistered = true;
        }
    }
    void RegisterExchangeHandlers(IModuleContext module)
    {
        module.OnPlayerLeaving((player, _) =>
        {
            ForgetExchange(player);
            return Task.CompletedTask;
        });
        foreach (ushort id in new[] { InstanceCurrencyExchange.ListRequest, InstanceCurrencyExchange.PlaceRequest,
            InstanceCurrencyExchange.CancelRequest, InstanceCurrencyExchange.CollectRequest, InstanceCurrencyExchange.MarketRequest,
            InstanceCurrencyExchange.PopularRequest, InstanceCurrencyExchange.FeeRequest })
            module.OnPacket(id, async packet =>
            {
                var player = packet.Player;
                var conn = ConnOf(player);
                if (conn.Ssf || conn.Account is null || module.World(player).OpenWindowOwner != ExchangeWindow) return true;
                TrackExchange(module, player);
                await RefreshExchangeMarket(module, player);
                byte[] bytes = packet.Bytes.ToArray();
                uint Read(int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
                var catalog = ItemsFor(conn);
                var stash = PersonalStashOf(conn);
                var items = ItemsOf(conn);
                switch (id)
                {
                    case InstanceCurrencyExchange.ListRequest:
                        await SendOrders(player);
                        break;
                    case InstanceCurrencyExchange.FeeRequest:
                    {
                        uint hash = Read(2);
                        var c = CurrencyExchange.Find(hash, catalog);
                        await player.Send(InstanceCurrencyExchange.Fee(hash, (uint)(c?.Fee ?? 0), c?.Divisor ?? false),
                            "0x02f5 exchange gold fee");
                        break;
                    }
                    case InstanceCurrencyExchange.PopularRequest:
                    {
                        uint hash = Read(2);
                        var popular = CurrencyExchange.Orders(League(conn)).Where(o => o.Active && o.Give == hash)
                            .Select(o => o.Want).Distinct().Take(100).ToArray();
                        await player.Send(InstanceCurrencyExchange.Popular(hash, popular), "0x02f0 exchange currencies");
                        break;
                    }
                    case InstanceCurrencyExchange.MarketRequest:
                    {
                        uint first = Read(2), second = Read(6);
                        _exchange.GetOrCreateValue(player).Pairs.TryAdd((first, second), 0);
                        await SendMarket(player, first, second);
                        break;
                    }
                    case InstanceCurrencyExchange.PlaceRequest:
                    {
                        uint want = Read(2), give = Read(6), wanted = Read(10), offered = Read(14);
                        var before = items.Items(PlayerInventories.MainInventory).Select(i => i.Id).ToArray();
                        var placed = CurrencyExchange.Place(conn.Account, League(conn), conn.Ssf, items, stash, catalog,
                            want, give, unchecked((int)wanted), unchecked((int)offered));
                        if (placed is null)
                        {
                            await player.Tell("Order refused: check currency, gold, amounts and available order slots.");
                            await SendOrders(player);
                            break;
                        }
                        await SendStashPayment(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, stash, placed.Payment.Stash);
                        await player.Send(InstanceItems.InventoryChange(1, items.NextRevision(PlayerInventories.MainInventory), before,
                            items.Items(PlayerInventories.MainInventory)), "0x00bf exchange escrow payment");
                        await player.Send(InstanceEntities.Gold(stash.Gold), "0x02a9 exchange gold payment");
                        await Notify(module, placed.Changed);
                        break;
                    }
                    case InstanceCurrencyExchange.CancelRequest:
                        if (CurrencyExchange.Cancel(League(conn), conn.Account, Read(2)) is { } cancelled)
                            await Notify(module, [cancelled]);
                        break;
                    case InstanceCurrencyExchange.CollectRequest:
                    {
                        var request = InstanceCurrencyExchange.ReadCollection(bytes);
                        var collected = CurrencyExchange.Collect(League(conn), conn.Account, request.OrderId, request.Refund,
                            request.ToInventory, items, catalog, ShapeOf, request.SingleStack);
                        if (collected is null) { await player.Tell("Nothing collectable, or no inventory space."); break; }
                        await player.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(collected.Row),
                            items.NextRevision(collected.Row), collected.Items.Select(i => i.Id).ToArray(), collected.Items),
                            "0x00bf exchange collection");
                        await player.Send(InstanceCurrencyExchange.Update(Wire(collected.Order)), "0x02f1 exchange collected");
                        break;
                    }
                }
                return true;
            });
    }
    static Task SendMarket(IPlayer player, uint first, uint second)
    {
        var orders = CurrencyExchange.Orders(League(ConnOf(player)));
        InstanceCurrencyExchange.Level[] Levels(uint want, uint give) => orders
            .Where(o => o.Active && o.Want == want && o.Give == give)
            .GroupBy(o => (Want: o.Wanted / CurrencyExchange.Gcd(o.Wanted, o.Offered),
                Give: o.Offered / CurrencyExchange.Gcd(o.Wanted, o.Offered)))
            .OrderBy(g => (decimal)g.Key.Want / g.Key.Give).Take(20)
            .Select(g => new InstanceCurrencyExchange.Level((ushort)g.Key.Give, (ushort)g.Key.Want,
                (ulong)g.Sum(o => (long)o.RemainingGive))).ToArray();
        return player.Send(InstanceCurrencyExchange.Market(first, second, Levels(second, first), Levels(first, second)),
            "0x02f2 exchange market depth");
    }
    async Task Notify(IModuleContext module, IReadOnlyList<CurrencyExchange.Order> changed, string? refreshedLeague = null)
    {
        foreach (var entry in _exchange)
        {
            var conn = ConnOf(entry.Key);
            if (conn.Closed || conn.LeavingArea || module.World(entry.Key).OpenWindowOwner != ExchangeWindow) continue;
            foreach (var pair in entry.Value.Pairs.Keys)
            {
                if (!string.Equals(refreshedLeague, League(conn), StringComparison.OrdinalIgnoreCase)
                    && !changed.Any(o => o.League.Equals(League(conn), StringComparison.OrdinalIgnoreCase)
                        && ((o.Want == pair.First && o.Give == pair.Second) || (o.Give == pair.First && o.Want == pair.Second)))) continue;
                try { await SendMarket(entry.Key, pair.First, pair.Second); }
                catch (IOException) { /* The next query reads the committed book. */ }
            }
            foreach (var order in changed.Where(o => o.League.Equals(League(conn), StringComparison.OrdinalIgnoreCase)
                && o.Account.Equals(conn.Account, StringComparison.OrdinalIgnoreCase)))
            {
                try { await entry.Key.Send(InstanceCurrencyExchange.Update(Wire(order)), "0x02f1 exchange order updated"); }
                catch (IOException) { /* The committed order is restored on the recipient's next entry. */ }
            }
        }
    }
}
