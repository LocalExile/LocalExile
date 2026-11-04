using GameServer.Core.Protocol.Login;
using System.Buffers.Binary;

namespace GameServer.Core.Protocol.Instance;

public static class InstanceCurrencyExchange
{
    public const byte Window = 0x5e;
    public const ushort ListRequest = 0x2e6, PlaceRequest = 0x2e8, CancelRequest = 0x2e9,
        CollectRequest = 0x2eb, MarketRequest = 0x2ee, PopularRequest = 0x2ef, FeeRequest = 0x2f4;
    public sealed record Order(uint Id, string League, uint Give, uint Want, uint Offered, uint Remaining,
        uint Received, ushort WantRatio, ushort GiveRatio, ulong Created, byte Status);
    public sealed record Level(ushort Want, ushort Give, ulong Stock);
    public readonly record struct CollectionRequest(uint OrderId, bool Refund, bool ToInventory, bool SingleStack);

    public static CollectionRequest ReadCollection(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != 11 || BinaryPrimitives.ReadUInt16BigEndian(packet) != CollectRequest
            || packet[6] > 1 || packet[7] > 1 || packet[8] > 1)
            throw new ArgumentException("Invalid currency exchange collection request", nameof(packet));
        // Native mouse callbacks: side 0 = offered currency, 1 = proceeds;
        // byte 7 selects one stack, byte 8 selects the cursor (cleared by Ctrl).
        return new(BinaryPrimitives.ReadUInt32BigEndian(packet[2..]), packet[6] == 0, packet[8] == 0, packet[7] != 0);
    }

    static LoginWriter WriteOrder(LoginWriter w, Order o) => w.RawU64(o.Created).U32(o.Id).WStr(o.League)
        .RawU32(0).Bool(true).U32(o.Give).U32(o.Want).U32(o.Offered).U32(o.Remaining).U32(o.Received).U32(0)
        .U16(o.WantRatio).U16(o.GiveRatio).U16(0).U16(0).U8(o.Status);

    public static byte[] Orders(IReadOnlyList<Order> orders, byte limit)
    {
        var w = new LoginWriter().U16(0x2e7);
        Count(w, orders.Count);
        foreach (var o in orders) WriteOrder(w, o);
        return w.U8(limit).ToArray();
    }
    public static byte[] Update(Order order) => WriteOrder(new LoginWriter().U16(0x2f1), order).U8(0).ToArray();
    public static byte[] Fee(uint hash, uint amount, bool divisor) =>
        new LoginWriter().U16(0x2f5).U32(hash).U32(amount).Bool(divisor).ToArray();
    public static byte[] Popular(uint hash, IReadOnlyList<uint> currencies)
    {
        var w = new LoginWriter().U16(0x2f0).U32(hash);
        Count(w, currencies.Count);
        foreach (uint currency in currencies) w.RawU32(currency);
        return w.ToArray();
    }
    public static byte[] Market(uint first, uint second, IReadOnlyList<Level> buy, IReadOnlyList<Level> sell)
    {
        var w = new LoginWriter().U16(0x2f2).U32(first).U32(second);
        foreach (var levels in new[] { buy, sell })
        {
            Count(w, levels.Count);
            foreach (var l in levels) w.U16(l.Want).U16(l.Give).RawU64(l.Stock);
        }
        return w.ToArray();
    }
    static void Count(LoginWriter w, int count)
    {
        if (count < 0 || count >= 0x4000) throw new ArgumentOutOfRangeException(nameof(count));
        if (count < 0x80) w.U8((byte)count);
        else w.U16((ushort)(0x8000 | count));
    }
}
