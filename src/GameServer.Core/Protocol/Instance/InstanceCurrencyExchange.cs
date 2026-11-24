using System.Buffers.Binary;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>Faustus's Currency Exchange window (Agents/docs/protocol/29-currency-exchange.md).</summary>
public static class InstanceCurrencyExchange
{
    /// <summary>S→C 0x7b kind: CurrencyExchangeWindow in the screen table at 0x142fd80f0.</summary>
    public const byte WindowUi = 0x5e;

    public const ushort RequestOrdersId = 0x2e8, PlaceOrderId = 0x2ea, CloseOrderId = 0x2eb, ClaimId = 0x2ed, RequestMarketId = 0x2f0,
        RequestPartnersId = 0x2f1, RequestFeeId = 0x2f6;
    public const int RequestOrdersLength = 2, PlaceOrderLength = 18, CloseOrderLength = 6, ClaimLength = 11, RequestMarketLength = 10,
        RequestPartnersLength = 6, RequestFeeLength = 6;

    public const ushort OrdersId = 0x2e9, QueryOrderResultId = 0x2ef, PartnersId = 0x2f2, OrderChangedId = 0x2f3, MarketId = 0x2f4, FeeId = 0x2f7,
        OrderCompletedId = 0x2f8, ErrorId = 0x2f9;

    /// <summary>BackendErrors.dat rows used by S→C 0x2f9 (handler 0x140b90be0).</summary>
    public const ushort ErrorInvalidRequest = 604, ErrorMarketDisabled = 608, ErrorTooManyOrders = 612,
        ErrorGenericOrderFailure = 615, ErrorCouldNotFindSellItems = 616;

    /// <summary>S→C 0x2f9 (class 0x1435ca200, read 0x141f991c0): u16 BE BackendErrors row shown in the Currency Exchange error popup.</summary>
    public static byte[] Error(ushort backendError) => new LoginWriter().U16(ErrorId).U16(backendError).ToArray();

    /// <summary>S→C 0x2f8, empty: "Faustus has completed one of your Currency Exchange orders", as a notification with its sound (0x140d96cf0).</summary>
    public static byte[] OrderCompleted() => new LoginWriter().U16(OrderCompletedId).ToArray();

    public const ushort OrdersWaitingId = 0x2fa;

    /// <summary>S→C 0x2fa, empty: sets the flag 0x2f8 sets, with no notification.</summary>
    public static byte[] OrdersWaiting() => new LoginWriter().U16(OrdersWaitingId).ToArray();

    /// <summary>What an order's row shows; <see cref="Removed"/> with nothing left to claim takes the row away.</summary>
    public enum OrderState : byte { Listed = 0, Completed = 1, Cancelled = 3, Removed = 4 }

    /// <summary>An order as the window holds it. The counts of a side are in its item; a ratio is want : have.</summary>
    public sealed record Order(uint Id, string League, uint Have, uint Want, uint HaveTotal, uint ClaimHave, uint ClaimWant, uint Remaining,
        ushort RatioHave, ushort RatioWant, ushort MarketHave, ushort MarketWant, OrderState State);

    /// <summary>A step of a market: <paramref name="Give"/> of what its side sells for <paramref name="Get"/> of the other item.</summary>
    public sealed record MarketLevel(ushort Give, ushort Get, ulong Stock);

    public readonly record struct PlaceOrder(uint Want, uint Have, uint WantCount, uint HaveCount);
    /// <summary>C→S 0x2ed (11 bytes, writer 0x141f9f660, caller 0x140beccd0): Order u32 BE, Side u8 (1 bought / 0 sold),
    /// PressKind u8 (1 left click / 0 Ctrl+right click), ToCursor u8 (1 plain click onto cursor / 0 Ctrl+click to inventory),
    /// CellX and CellY u8 (clicked slot coordinates).</summary>
    public readonly record struct Claim(uint Order, byte Side, byte PressKind = 1, bool ToCursor = false, byte CellX = 0, byte CellY = 0);

    static uint U32(ReadOnlySpan<byte> message, int at) => BinaryPrimitives.ReadUInt32BigEndian(message[at..]);

    public static PlaceOrder ReadPlaceOrder(ReadOnlySpan<byte> m) => new(U32(m, 2), U32(m, 6), U32(m, 10), U32(m, 14));

    /// <summary>C→S 0x2eb, 0x2f1 and 0x2f6: their one u32.</summary>
    public static uint ReadU32(ReadOnlySpan<byte> m) => U32(m, 2);

    /// <summary>C→S 0x2f0: the two items of a market, the lower hash first.</summary>
    public static (uint Low, uint High) ReadRequestMarket(ReadOnlySpan<byte> m) => (U32(m, 2), U32(m, 6));

    /// <summary>C→S 0x2ed: the order, side (1 bought, 0 sold), press kind (1 left, 0 Ctrl+right), to-cursor flag, and clicked cell.</summary>
    public static Claim ReadClaim(ReadOnlySpan<byte> m) => new(U32(m, 2), m[6], m[7], m[8] != 0, m[9], m[10]);

    /// <summary>S→C 0x2e9: every order of the league and how many the window allows.</summary>
    public static byte[] Orders(IReadOnlyCollection<Order> orders, byte slots)
    {
        var w = new LoginWriter().U16(OrdersId);
        InstanceProtocol.CompactUInt(w, (uint)orders.Count);
        foreach (var order in orders) Write(w, order);
        return w.U8(slots).ToArray();
    }

    /// <summary>S→C 0x2f3: one order added or changed.</summary>
    /// <param name="order">The order.</param>
    /// <param name="placed">Just placed: Faustus says FaustusSettlersPlaceOrder_Random (0x141628180).</param>
    public static byte[] OrderChanged(Order order, bool placed = false)
    {
        var w = new LoginWriter().U16(OrderChangedId);
        Write(w, order);
        return w.Bool(placed).ToArray();
    }

    /// <summary>S→C 0x2f2: the items that trade against this one. The list is written from memory, not byte-swapped.</summary>
    public static byte[] Partners(uint item, IReadOnlyCollection<uint> partners)
    {
        var w = new LoginWriter().U16(PartnersId).U32(item);
        InstanceProtocol.CompactUInt(w, (uint)partners.Count);
        foreach (uint partner in partners) w.RawU32(partner);
        return w.ToArray();
    }

    /// <summary>S→C 0x2f4: a market, best level first. <paramref name="sellingLow"/> sell the lower hash, <paramref name="sellingHigh"/> the higher.</summary>
    public static byte[] Market(uint low, uint high, IReadOnlyCollection<MarketLevel> sellingLow, IReadOnlyCollection<MarketLevel> sellingHigh)
    {
        var w = new LoginWriter().U16(MarketId).U32(low).U32(high);
        Write(w, sellingLow);
        Write(w, sellingHigh);
        return w.ToArray();
    }

    /// <summary>S→C 0x2f7: the gold an order pays per item bought, or with <paramref name="itemsPerGold"/> how many items one gold pays for.</summary>
    public static byte[] Fee(uint item, uint fee, bool itemsPerGold) => new LoginWriter().U16(FeeId).U32(item).U32(fee).Bool(itemsPerGold).ToArray();

    /// <summary>S→C 0x2ef (16 bytes, class 0x1435e6888, read 0x1421b80e0 / write 0x1421b8020, dispatcher case 0xf3 at
    /// 0x14215157a): Currency Exchange query order result (<c>u32 BE queryId</c>, <c>u16 BE status</c>,
    /// <c>u32 BE bought</c>, <c>u32 BE sold</c>).</summary>
    public static byte[] QueryOrderResult(uint queryId, ushort status, uint bought, uint sold) =>
        new LoginWriter().U16(QueryOrderResultId).U32(queryId).U16(status).U32(bought).U32(sold).ToArray();

    static void Write(LoginWriter w, IReadOnlyCollection<MarketLevel> levels)
    {
        InstanceProtocol.CompactUInt(w, (uint)levels.Count);
        foreach (var level in levels) w.U16(level.Give).U16(level.Get).RawU64(level.Stock);
    }

    // Read by 0x1429adaf0. The first 8 bytes and the raw u32 after the league have no reader in the window; the id goes in
    // the first so that it stays unique.
    static void Write(LoginWriter w, Order o) => w.RawU64(o.Id).U32(o.Id).WStr(o.League).RawU32(0).Bool(true)
        .U32(o.Have).U32(o.Want).U32(o.HaveTotal).U32(o.ClaimHave).U32(o.ClaimWant).U32(o.Remaining)
        .U16(o.RatioHave).U16(o.RatioWant).U16(o.MarketHave).U16(o.MarketWant).U8((byte)o.State);
}
