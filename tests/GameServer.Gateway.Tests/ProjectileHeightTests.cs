using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ProjectileHeightTests
{
    [Theory]
    [InlineData(null, null, 5)]
    [InlineData(12, null, 17)]
    [InlineData(-8, null, -3)]
    [InlineData(null, "9", 9)]
    [InlineData(12, "9", 21)]
    [InlineData(12, "0", 12)]
    [InlineData(12, "-2", 10)]
    [InlineData(null, "invalid", 5)]
    [InlineData(null, "", 5)]
    public void Flight_height_preserves_main_ground_fallback_and_configured_offset(int? ground, string? setting, int expected)
    {
        Assert.Equal(expected, ProjectileHeight.Flight(ground, setting).Marker);
    }

    [Fact]
    public async Task Missing_grid_flight_keeps_configured_lift_at_native_creation_glide_and_stop()
    {
        await using var f = new ProjectileRuntimeFixture();
        f.Release();
        uint id = Assert.Single(f.Conn.ProjectileObjects);
        byte[] start = await f.Read();
        int offset = int.TryParse(Environment.GetEnvironmentVariable("Game_PROJECTILE_HEIGHT"), out int value) ? value : 5;
        var height = new ProjectileHeight(offset);
        var expected = new ProjectileObject("Metadata/Projectiles/DefaultArrow", 10, 10,
            (float)(Math.PI / 2), Height: height.ObjectHeight);
        Assert.True(start.AsSpan().IndexOf(expected.Blob()) >= 0);
        Assert.True(start.AsSpan().IndexOf(height.MoveTo(id, 110, 10, 10)) >= 0);
        Assert.True(start.AsSpan().IndexOf(height.StopAt(id, 10, 10)) >= 0);
        f.Advance(10.01);
        Assert.Empty(f.Session.Runtime.Projectiles);
        byte[] stop = await f.Read();
        Assert.True(stop.AsSpan().IndexOf(height.StopAt(id, 110, 10)) >= 0);
    }

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
