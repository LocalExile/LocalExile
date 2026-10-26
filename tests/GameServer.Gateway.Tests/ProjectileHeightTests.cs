using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ProjectileHeightTests
{
    [Theory]
    [InlineData(5, -39.0625f)]
    [InlineData(-8, 62.5f)]
    [InlineData(0, 0f)]
    public void Gateway_creation_glide_and_stop_keep_the_same_signed_native_height(int marker, float expectedZ)
    {
        var height = new ProjectileHeight(marker);
        byte[] blob = new ProjectileObject("Metadata/Projectiles/Fireball", 1, 2, Height: height.ObjectHeight).Blob();
        byte[] move = height.MoveTo(0x3001, 80, 2, 2.5f);
        byte[] stop = height.StopAt(0x3001, 40, 2);
        int createdMarker = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(18));
        float createdZ = BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(22));
        int endpointMarker = BinaryPrimitives.ReadInt32BigEndian(move.AsSpan(20));
        float endpointZ = -endpointMarker * 7.8125f; // Native 0x141e3495b..0x141e3496b.
        Assert.Equal(marker, createdMarker);
        Assert.Equal(expectedZ, createdZ);
        Assert.Equal(createdMarker, endpointMarker);
        Assert.Equal(createdZ, endpointZ);
        Assert.Equal(createdMarker, BinaryPrimitives.ReadInt32BigEndian(stop.AsSpan(20)));
        Assert.Equal(createdZ, BinaryPrimitives.ReadSingleBigEndian(stop.AsSpan(24)));
        Assert.Equal(2.5f, BinaryPrimitives.ReadSingleBigEndian(move.AsSpan(24)));
        // The client's render interpolator stores endpointZ - createdZ. It must stay zero in either sign convention.
        Assert.Equal(0f, endpointZ - createdZ);
        Assert.Equal(28, stop.Length);
    }
}
