using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class MeleeHitFeedbackTests
{
    [Fact]
    public void An_authoritative_heavy_strike_keeps_typed_critical_feedback_without_replaying_a_root_action()
    {
        var chaos = DamagePacket.Of(DamageType.Chaos, 100, 100);
        foreach (string effect in new[] { "HeavyStrike", "HeavyStrikeAltX", "HeavyStrikeAltY" })
        {
            string art = Assert.IsType<string>(Game.MeleeImpactArt(effect));
            var packet = Game.MeleeHitFeedback(0x1000, 0x1234, 0x8000012, Game.HitCode(chaos), 0x40,
                critical: true, impactArt: art, orderKey: 0x1000, checkpoint: 0x7000);
            Assert.Equal(InstanceProtocol.ActionHitId, BinaryPrimitives.ReadUInt16BigEndian(packet));
            Assert.Equal(0x1000u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(6)));
            Assert.Equal(0x7000, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10)));
            Assert.Equal(0xffff, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(12)));
            Assert.Equal(0x8000012u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(16)));
            Assert.Equal(0x4221, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(20)));
            Assert.Equal(3, packet[22]); // Immediate hit data, never an unconsumable queued result.
            Assert.Equal(4, packet[23]); // Actual Chaos hit, not the old physical default.
            Assert.Equal(0x40, packet[24]);
        }
        // A different skill keeps its real sequence and its existing native hit presentation.
        Assert.Null(Game.MeleeImpactArt("Sweep"));
        Assert.Equal(InstanceProtocol.ActionHit(0x1000, 0x1234, 0x8000012, flags: 0x4201, kind: 3, code: 2),
            Game.MeleeHitFeedback(0x1000, 0x1234, 0x8000012, 2, 0, false, null));
    }
}
