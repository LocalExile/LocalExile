using System.Buffers.Binary;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Async-trade merchant stash protocol (Path of Exile 3.29.3c): sale history request/response (<c>C→S 0x02ac</c> /
/// <c>S→C 0x02ad</c>), earnings balance list (<c>S→C 0x02ae</c>), take-earnings currency request (<c>C→S 0x02b0</c>),
/// and target item highlight/warning (<c>S→C 0x008e</c>).
/// </summary>
public static class InstanceAsyncTrade
{
    /// <summary>S→C 0x7b kind that opens the async-trade merchant stash window (<c>0x142169ebf</c>).</summary>
    public const byte OpenUiKind = 0x63;

    /// <summary>C→S 0x02ac (2 bytes, id only): request the full async-trade sale history snapshot.</summary>
    public const ushort RequestHistoryId = 0x02ac;
    public const int RequestHistoryLength = 2;

    /// <summary>S→C 0x02ad (reader <c>0x1421b9520</c>, entry <c>0x1421b9640</c>, dispatcher case <c>0xe4</c> at
    /// <c>0x14215b009</c>): sale history snapshot (<c>isIncremental = false</c>) or incremental sale push
    /// (<c>isIncremental = true</c>).</summary>
    public const ushort HistoryId = 0x02ad;

    /// <summary>S→C 0x02ae (reader <c>0x1421b9820</c>, dispatcher case <c>0xe5</c> at <c>0x14215b086</c>):
    /// async-trade earnings currency balances (<c>CompactUInt count</c>, <c>count × (u32 BE typeHash, u32 BE amount)</c>).</summary>
    public const ushort EarningsId = 0x02ae;

    /// <summary>C→S 0x02b0 (12 bytes, writer <c>0x141f9c880</c>, sender <c>0x140b3eb00</c>): withdraw currency from
    /// async-trade earnings (<c>u32 BE currencyTypeHash</c>, <c>u32 BE amount</c>, <c>u8 flagA</c>, <c>u8 flagB</c>).</summary>
    public const ushort TakeEarningsId = 0x02b0;
    public const int TakeEarningsLength = 12;

    /// <summary>S→C 0x008e (reader <c>0x141fa8ef0</c>, list <c>0x141fa8db0</c>, dispatcher case <c>0x20</c> at
    /// <c>0x1421538e5</c>): async-trade target item highlight/warning.</summary>
    public const ushort TargetHighlightId = 0x008e;

    /// <summary>One sale history entry in <see cref="HistoryId"/>.</summary>
    public sealed record SaleEntry(ulong Timestamp, uint CurrencyTypeHash, uint CurrencyAmount, bool Sold, byte[] ItemWire);

    /// <summary>One currency balance pair in <see cref="EarningsId"/>.</summary>
    public readonly record struct EarningBalance(uint CurrencyTypeHash, uint Amount);

    /// <summary>Parsed <see cref="TakeEarningsId"/> request.</summary>
    public readonly record struct TakeEarningsRequest(uint CurrencyTypeHash, uint Amount, byte FlagA, byte FlagB);

    /// <summary>
    /// Encodes S→C 0x02ad. Trailing <paramref name="isIncremental"/> MUST be <c>false</c> (byte <c>0</c>) for full
    /// history snapshots replying to <c>C→S 0x02ac</c> so the client marks history loaded (<c>0x14215b05e</c>).
    /// </summary>
    public static byte[] EncodeHistory(IReadOnlyList<SaleEntry> entries, bool isIncremental = false)
    {
        var w = new LoginWriter().U16(HistoryId);
        InstanceProtocol.CompactUInt(w, (uint)entries.Count);
        foreach (var e in entries)
        {
            if (e.ItemWire.Length > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(entries), "0x02ad item wire length exceeds u16");
            w.RawU64(e.Timestamp)
                .U32(e.CurrencyTypeHash)
                .U32(e.CurrencyAmount)
                .U8((byte)(e.Sold ? 1 : 0))
                .U16((ushort)e.ItemWire.Length)
                .Bytes(e.ItemWire);
        }
        return w.U8((byte)(isIncremental ? 1 : 0)).ToArray();
    }

    /// <summary>Encodes S→C 0x02ae: current async-trade earnings currency balances.</summary>
    public static byte[] EncodeEarnings(IReadOnlyList<EarningBalance> balances)
    {
        var w = new LoginWriter().U16(EarningsId);
        InstanceProtocol.CompactUInt(w, (uint)balances.Count);
        foreach (var b in balances) w.U32(b.CurrencyTypeHash).U32(b.Amount);
        return w.ToArray();
    }

    /// <summary>Parses C→S 0x02b0 (12 bytes).</summary>
    public static TakeEarningsRequest ReadTakeEarnings(ReadOnlySpan<byte> m)
    {
        if (m.Length < TakeEarningsLength)
            throw new InvalidDataException($"0x02b0 is {m.Length} bytes, not {TakeEarningsLength}");
        return new(
            BinaryPrimitives.ReadUInt32BigEndian(m[2..]),
            BinaryPrimitives.ReadUInt32BigEndian(m[6..]),
            m[10],
            m[11]);
    }

    /// <summary>
    /// Encodes S→C 0x008e (async-trade target highlight/warning). Note that <paramref name="rawItemClass"/> is written
    /// in little-endian wire order (matching <c>0x141fa8f6f..0x141fa8f80</c>, which reads the u32 without byte-swap).
    /// </summary>
    public static byte[] EncodeTargetHighlight(ushort errorCode, uint inventoryId, uint itemId, uint rawItemClass,
        IReadOnlyList<(uint ItemTypeHash, uint Amount)>? costs = null)
    {
        costs ??= [];
        if (costs.Count > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(costs));
        var w = new LoginWriter()
            .U16(TargetHighlightId)
            .U16(errorCode)
            .U32(inventoryId)
            .U32(itemId)
            .RawU32(rawItemClass)
            .U8((byte)costs.Count);
        foreach (var (typeHash, amount) in costs)
        {
            w.U32(typeHash);
            InstanceProtocol.CompactUInt(w, amount);
        }
        return w.ToArray();
    }
}
