using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class GemVariantWireTests
{
    [Theory]
    [InlineData(false, 1, 0x00, 0x20)]
    public void Native_variant_flag_matches_the_variant_in_standalone_and_socketed_item_payloads(
        bool socketed, int variant, byte storedFlags, byte wireFlags)
    {
        var gem = new Item(7, ["SkillGem"], 0)
        { GemExperience = 378, GemVariant = variant, GemFlags = storedFlags };
        var host = new Item(8, ["Sockets"], 0) { Sockets = [new(SocketColour.White, gem)] };
        byte[] payload = socketed ? host.Payload() : gem.Payload();
        int gemOffset = socketed ? 8 : 0; // Host item header, socket header and socket colour.
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(gemOffset)));
        Assert.Equal(378, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(gemOffset + 6)));
        Assert.Equal(variant, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(gemOffset + 10)));
        Assert.Equal(wireFlags, payload[gemOffset + 14]);
        Assert.Equal(storedFlags, gem.GemFlags);
    }
}
