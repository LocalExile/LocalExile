using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

public static class InstanceSkinTransfers
{
    public const uint MtxTypeHash = 3551001385u; // MicrotransactionApplyVisualIdentity
    // Native 3.29.3b confirmation 0x1402fc2e0, writer 0x141f9c650: five network-order u32 fields.
    public const ushort BindId = 0x207;
    public const int BindLength = 22;
    // Shift-click reclaim confirmation callback 0x14032abe0, writer 0x141f926a0.
    public const ushort ReclaimId = 0x122;
    public const int ReclaimLength = 10;
    public sealed record Bind(uint RecordId, uint SourceId, uint TargetId, uint SourceInventoryId, uint TargetInventoryId);
    public sealed record Reclaim(uint InventoryId, uint ItemId);

    public static Bind ReadBind(ReadOnlySpan<byte> bytes)
    {
        var r = new LoginReader(bytes[2..]);
        return new(r.U32(), r.U32(), r.U32(), r.U32(), r.U32());
    }

    public static Reclaim ReadReclaim(ReadOnlySpan<byte> bytes)
    {
        var r = new LoginReader(bytes[2..]);
        return new(r.U32(), r.U32());
    }
}
