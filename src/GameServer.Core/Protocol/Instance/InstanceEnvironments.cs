using System.Buffers.Binary;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// An area's environment overrides: S→C 0xdf carries the list. The message is
/// <c>u16 id, u8 count, then per environment (u16 Environments row, u8 slot, f32 seconds)</c>; the
/// packet REPLACES the area's whole override list, so the empty list clears it. Slot 0 only: slot 1 is
/// never sent — on a one-layer area it reads and writes outside the layer vector and the client falls over.
/// </summary>
public static class InstanceEnvironments
{
    public const ushort DataId = 0x00df;

    /// <summary>S→C 0xdf: the area's environment overrides, as (Environments row, seconds) pairs.</summary>
    public static byte[] Data(params (ushort Row, double Seconds)[] envs)
    {
        if (envs.Length > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(envs));
        var w = new LoginWriter().U16(DataId).U8((byte)envs.Length);
        Span<byte> seconds = stackalloc byte[4];
        foreach (var (row, secs) in envs)
        {
            BinaryPrimitives.WriteSingleBigEndian(seconds, (float)secs);
            w.U16(row).U8(0).Bytes(seconds);
        }
        return w.ToArray();
    }
}
