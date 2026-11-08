using System.Numerics;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Trade;
using GameServer.Modding;
using Wire = GameServer.Core.Protocol.Instance.InstanceCurrencyExchange;

namespace GameServer.Modules.CurrencyExchange;

/// <summary>Faustus's Currency Exchange at the official leagues' prices (Agents/docs/protocol/29-currency-exchange.md): the module only
/// reads the prices from <c>data/</c>, and never connects to anything itself.</summary>
public sealed class CurrencyExchangeModule : IModule
{
    const string SaveKey = "currency-exchange", Window = "currency-exchange", RatesFile = ExchangeRateFeed.File;
    const double SettleEverySeconds = 30;

    sealed record Choices(double SpreadScale, double VolumeShare, int OrderSlots, double MaxRateAgeDays);

    /// <summary>Starts full: the first tick in an area settles, so what filled while the player was away shows as they arrive.</summary>
    sealed class Timer { public double Seconds = SettleEverySeconds; }

    readonly ConditionalWeakTable<IPlayer, Timer> _timers = new();
    readonly object _ratesLock = new();
    ExchangeRates? _rates;
    DateTime _ratesWritten;
    Choices _choices = new(1, 0.1, 10, 7);
    IModuleContext _module = null!;

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        _module = module;
        _choices = module.Settings<Choices>();
        module.OnNpcOption(async talk =>
        {
            if (!talk.Script.Contains("CurrencyExchangeWindow", StringComparison.Ordinal)) return false;
            await module.World(talk.Player).NpcClose("0x0084 close NPC dialogue for the Currency Exchange");
            await Open(talk.Player);
            return true;
        });
        module.OnChatCommand("exchange", "/exchange (opens the Currency Exchange)", command => Open(command.Player));
        Handle(Wire.RequestOrdersId, (player, _) => SendOrders(player));
        Handle(Wire.RequestFeeId, (player, m) => SendFee(player, Wire.ReadU32(m.Span)));
        Handle(Wire.RequestPartnersId, (player, m) => SendPartners(player, Wire.ReadU32(m.Span)));
        Handle(Wire.RequestMarketId, (player, m) => SendMarket(player, Wire.ReadRequestMarket(m.Span)));
        Handle(Wire.PlaceOrderId, (player, m) => Place(player, Wire.ReadPlaceOrder(m.Span)));
        Handle(Wire.CloseOrderId, (player, m) => Close(player, Wire.ReadU32(m.Span)));
        Handle(Wire.ClaimId, (player, m) => Claim(player, Wire.ReadClaim(m.Span)));
        module.OnTick(async (player, seconds) =>
        {
            var timer = _timers.GetOrCreateValue(player);
            if ((timer.Seconds += seconds) < SettleEverySeconds) return;
            timer.Seconds = 0;
            await Settle(player);
        });
    }

    void Handle(ushort opcode, Func<IPlayer, ReadOnlyMemory<byte>, Task> run) => _module.OnPacket(opcode, async packet =>
    {
        await run(packet.Player, packet.Bytes);
        return true;
    });

    Task Open(IPlayer player) => _module.World(player).OpenWindow(Window, Wire.WindowUi, "0x007b open the Currency Exchange");

    ExchangeItems ItemsOf(IPlayer player) => ExchangeItems.For(_module.World(player).Crafting);

    ExchangeRates? Rates()
    {
        lock (_ratesLock)
        {
            var file = new FileInfo(RatesFile);
            if (!file.Exists || file.LastWriteTimeUtc == _ratesWritten) return _rates;
            try
            {
                _rates = ExchangeRates.Parse(File.ReadAllText(RatesFile));
                _ratesWritten = file.LastWriteTimeUtc;
                _module.Log($"rates of {DateTimeOffset.FromUnixTimeSeconds(_rates?.SourceHour ?? 0):u}, current league {_rates?.Current}");
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                _module.Log($"{RatesFile} cannot be read, the rates stay as they were: {e.Message}");
            }
            return _rates;
        }
    }

    /// <summary>The market this player trades on; null when there are no rates, or they are too old to trade at.</summary>
    ExchangeMarket? MarketOf(IPlayer player)
    {
        if (Rates() is not { } rates) return null;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - rates.SourceHour > _choices.MaxRateAgeDays * 86400) return null;
        var items = ItemsOf(player);
        return rates.Prices(ExchangeRateFeed.League == ExchangeRatesLeague.Standard) is { } prices
            ? new(rates, prices, hash => items.ByHash.TryGetValue(hash, out var row) ? row.Id : null, _choices.SpreadScale, _choices.VolumeShare) : null;
    }

    ExchangeBook BookOf(IPlayer player) => ExchangeBook.Parse(_module.World(player).AccountData.Get(SaveKey));

    bool Save(IPlayer player, Action<ExchangeBook> change) => _module.World(player).AccountData.Change(SaveKey, json =>
    {
        var book = ExchangeBook.Parse(json);
        change(book);
        return book.Serialize();
    });

    static Wire.Order ToWire(ExchangeOrder o, string league, ExchangeMarket? market)
    {
        // The row's market ratio is the cheapest competing order: Faustus selling what this order sells.
        var (give, get) = market?.Trade(o.Have, o.Want) ?? ((ushort)0, (ushort)0);
        return new(o.Id, league, o.Have, o.Want, o.HaveCount, o.ClaimHave, o.ClaimWant, o.Remaining, o.RatioHave, o.RatioWant, give, get, o.State);
    }

    Task SendOrder(IPlayer player, ExchangeOrder order, string why) =>
        player.Send(Wire.OrderChanged(ToWire(order, player.League, MarketOf(player))), $"0x02f3 order {order.Id} {order.State}: {why}");

    Task SendOrders(IPlayer player)
    {
        var market = MarketOf(player);
        var orders = BookOf(player).Orders;
        return player.Send(Wire.Orders([.. orders.Select(o => ToWire(o, player.League, market))], (byte)_choices.OrderSlots),
            $"0x02e9 {orders.Count} Currency Exchange orders");
    }

    Task SendFee(IPlayer player, uint item) => ItemsOf(player).ByHash.TryGetValue(item, out var row)
        ? player.Send(Wire.Fee(item, row.Fee, row.ItemsPerGold), $"0x02f7 gold fee of {row.Id}: {row.Fee}") : Task.CompletedTask;

    Task SendPartners(IPlayer player, uint item)
    {
        var market = MarketOf(player);
        uint[] partners = market is null ? [] : [.. ItemsOf(player).ByHash.Keys.Where(hash => hash != item && market.Trades(hash))];
        return player.Send(Wire.Partners(item, partners), "0x02f2 what trades against an item");
    }

    Task SendMarket(IPlayer player, (uint Low, uint High) pair)
    {
        var market = MarketOf(player);
        return player.Send(Wire.Market(pair.Low, pair.High, market?.Levels(pair.Low, pair.High) ?? [], market?.Levels(pair.High, pair.Low) ?? []),
            $"0x02f4 market {pair.Low:x8} / {pair.High:x8}");
    }

    async Task Place(IPlayer player, Wire.PlaceOrder request)
    {
        var items = ItemsOf(player);
        if (request.Want == request.Have || request.WantCount is 0 or >= 1_000_000 || request.HaveCount is 0 or >= 1_000_000
            || !items.ByHash.TryGetValue(request.Want, out var want) || !items.ByHash.ContainsKey(request.Have)) return;
        if (MarketOf(player) is not { } market || market.Trade(request.Want, request.Have) is null)
        {
            await player.Tell("Currency Exchange: there is no market for these two items.");
            return;
        }
        uint divisor = (uint)BigInteger.GreatestCommonDivisor(request.WantCount, request.HaveCount);
        uint ratioWant = request.WantCount / divisor, ratioHave = request.HaveCount / divisor;
        if (ratioWant > ExchangeRatio.Max || ratioHave > ExchangeRatio.Max) return;
        if (BookOf(player).Orders.Count >= _choices.OrderSlots) return;
        ulong gold = want.ItemsPerGold ? (request.WantCount + (ulong)want.Fee - 1) / Math.Max(want.Fee, 1) : request.WantCount * (ulong)want.Fee;
        if (gold > uint.MaxValue) return;

        ExchangeOrder? placed = null;
        var filled = new List<ExchangeOrder>();
        bool paid = await _module.World(player).PayCurrency(request.Have, (int)request.HaveCount, (uint)gold, () => Save(player, book =>
        {
            // It meets this hour's market at once, with what the book's other orders left of it.
            placed = new ExchangeOrder
            {
                Id = book.NextId++, Want = request.Want, Have = request.Have, WantCount = request.WantCount, HaveCount = request.HaveCount,
                Remaining = request.HaveCount, RatioWant = (ushort)ratioWant, RatioHave = (ushort)ratioHave, State = Wire.OrderState.Listed,
                LastHour = market.Hour - 3600,
            };
            book.Orders.Add(placed);
            filled = book.Settle(market);
        }), $"Currency Exchange order, {gold} gold");
        if (!paid)
        {
            await player.Tell("Currency Exchange: not enough currency or gold.");
            return;
        }
        await SendOrder(player, placed!, "placed");
        await SendFilled(player, filled.Where(o => o != placed));
    }

    async Task SendFilled(IPlayer player, IEnumerable<ExchangeOrder> filled)
    {
        foreach (var order in filled)
        {
            await SendOrder(player, order, "filled");
            if (order.State == Wire.OrderState.Completed) await player.Tell("Faustus has completed one of your Currency Exchange orders.");
        }
    }

    /// <summary>The player's X on an order: what is left of it can be claimed. The gold stays paid, as the window warns.</summary>
    async Task Close(IPlayer player, uint id)
    {
        ExchangeOrder? closed = null;
        Save(player, book =>
        {
            if (book.Orders.Find(o => o.Id == id) is not { } order) return;
            closed = order;
            if (order.State == Wire.OrderState.Listed)
            {
                order.ClaimHave = order.Remaining;
                order.Remaining = 0;
                order.State = Wire.OrderState.Cancelled;
            }
            Forget(book, order);
        });
        if (closed is not null) await SendOrder(player, closed, "closed");
    }

    /// <summary>An order with nothing left to claim leaves the book and the window.</summary>
    static void Forget(ExchangeBook book, ExchangeOrder order)
    {
        if (order.State == Wire.OrderState.Listed || order.ClaimWant + order.ClaimHave + order.Remaining > 0) return;
        order.State = Wire.OrderState.Removed;
        book.Orders.Remove(order);
    }

    async Task Claim(IPlayer player, Wire.Claim claim)
    {
        // As the window says, nothing is claimed until the order is completed or closed.
        if (BookOf(player).Orders.Find(o => o.Id == claim.Order) is not { } order || order.State == Wire.OrderState.Listed) return;
        bool bought = claim.Side == 1;
        uint item = bought ? order.Want : order.Have, amount = bought ? order.ClaimWant : order.ClaimHave;
        if (amount == 0) return;
        ExchangeOrder? changed = null;
        int placed = await _module.World(player).ReceiveCurrency(item, (int)amount, into => Save(player, book =>
        {
            if (book.Orders.Find(o => o.Id == claim.Order) is not { } saved) return;
            if (bought) saved.ClaimWant -= (uint)into;
            else saved.ClaimHave -= (uint)into;
            changed = saved;
            Forget(book, saved);
        }), "Currency Exchange claim");
        if (placed < amount) await player.Tell("Currency Exchange: not enough room in the inventory.");
        if (changed is not null) await SendOrder(player, changed, "claimed");
    }

    /// <summary>A listed order fills when the prices come to it, wherever the player is, and through the hours they were away.</summary>
    async Task Settle(IPlayer player)
    {
        if (!_module.World(player).PlayerReady || MarketOf(player) is not { } market) return;
        var book = BookOf(player);
        if (!book.Orders.Exists(o => o.State == Wire.OrderState.Listed && o.LastHour < market.Hour)) return;
        var filled = new List<ExchangeOrder>();
        Save(player, saved => filled = saved.Settle(market));
        await SendFilled(player, filled);
    }
}
