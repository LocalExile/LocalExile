namespace GameServer.Core.Protocol.Instance;

/// <summary>A native, permanently powered Delve path lamp. The client builds its cable to the previous light.
/// ClientDelveLight 0x1415d88d0 reads radius, link count, powered/follow-robot flags and object ids;
/// 0x1415d77c0 resolves the links and 0x1415d7b70 creates the native connector beam.</summary>
public sealed record DelveTrailLightObject(int X, int Y, uint Previous = 0)
{
    public const string MetadataPath = "Metadata/Terrain/Leagues/Delve/Objects/DelveLight";
    public byte[] Blob()
    {
        var w = new BlobWriter().U8(0).U8(0).I32(X).I32(Y).F32(0).U16(0).U8(0).U8(0)
            .U8(0) // Animated: the original lamp model, no override or attachment.
            .U8(1).Stats([]).U32(0).U32(0).U8(0).U16(0);
        Pool(w, 1); Pool(w, 0); Pool(w, 0); w.U8(0);
        w.F32(25).U32(Previous == 0 ? 0u : 1u).U8(1).U8(0);
        if (Previous != 0) w.U32(Previous);
        return w.U8(0).ToArray();
    }
    static void Pool(BlobWriter w, uint value) => w.U32(value).U32(0).U16(0).U32(0).U32(0);
}
