using GameServer.Core.Protocol.Instance;

// Positioned and 0x1ba use marker = -worldZ / 7.8125. ProjectileObject.Height instead takes worldZ / 7.8125.
// Keep the three packet forms together so a glide cannot interpolate toward the opposite height before its stop.
readonly record struct ProjectileHeight(int Marker)
{
    // Same flight contract as main: absent captures use zero as a provisional terrain estimate.
    // This is a fixed world height, not a client-sampled terrain height plus a relative offset.
    public static ProjectileHeight Flight(int? ground, string? setting) =>
        new((ground ?? 0) + (int.TryParse(setting, out int up) ? up : 5));

    // Native Positioned samples its terrain when this marker is present. Unknown ground is not world zero.
    public static ProjectileHeight Terrain => new(int.MaxValue);
    public int? ObjectHeight => Marker == int.MaxValue ? null : -Marker;
    public float WorldZ => ObjectHeight is int height ? height * 7.8125f : 0;

    public byte[] MoveTo(uint id, int x, int y, float seconds) =>
        InstanceProtocol.MoveTo(id, x, y, seconds, u28: unchecked((uint)Marker));

    public byte[] StopAt(uint id, int x, int y) =>
        InstanceProtocol.Teleport(id, x, y, unchecked((uint)Marker), WorldZ);
}
