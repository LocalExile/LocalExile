using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using Xunit;

namespace GameServer.Core.Tests;

public class InstanceHeistTests
{
    [Fact]
    public void HeistState_encodes_150_bytes_with_verified_trailing_revealer_order()
    {
        var xp = new uint[InstanceHeist.RogueCount * InstanceHeist.JobSlotsPerRogue];
        xp[0] = 0x00001f40; // Karst slot 0 (8000)
        xp[35] = 0x00000320; // Isla slot 3 (800)

        byte[] packet = InstanceHeist.HeistState(true, xp, whakanoReveals: 5, giannaReveals: 2, nilesReveals: 7);

        Assert.Equal(InstanceHeist.HeistStateLength, packet.Length);
        Assert.Equal(InstanceHeist.HeistStateId, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(0, 2)));
        Assert.Equal(1, packet[2]);
        Assert.Equal(0x00001f40u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(3, 4)));
        Assert.Equal(0x00000320u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(3 + 35 * 4, 4)));
        // Verified at 0x1421b4f03..0x1421b4f65: wire order is whakano (+0x01), niles (+0x03), gianna (+0x02).
        Assert.Equal(5, packet[147]);
        Assert.Equal(7, packet[148]);
        Assert.Equal(2, packet[149]);
    }

    [Fact]
    public void RevealResult_and_AlertLevel_encode_verified_wire_layouts()
    {
        byte[] reveal = InstanceHeist.RevealResult(wingIndex: 2, roomTarget: InstanceHeist.RevealTargetWing, revealerRow: 1);
        Assert.Equal([0x02, 0x3b, 0x02, 0xff, 0x01], reveal);

        byte[] alert = InstanceHeist.AlertLevel(0.75f);
        Assert.Equal(InstanceHeist.AlertLevelLength, alert.Length);
        Assert.Equal(InstanceHeist.AlertLevelId, BinaryPrimitives.ReadUInt16BigEndian(alert.AsSpan(0, 2)));
        Assert.Equal(BitConverter.SingleToUInt32Bits(0.75f), BinaryPrimitives.ReadUInt32BigEndian(alert.AsSpan(2, 4)));
        Assert.Equal(InstanceHeist.AlertBarKind, alert[6]);

        byte[] clamped = InstanceHeist.AlertLevel(2.5f);
        Assert.Equal(BitConverter.SingleToUInt32Bits(1.0f), BinaryPrimitives.ReadUInt32BigEndian(clamped.AsSpan(2, 4)));
    }

    [Fact]
    public void Heist_c2s_packets_frame_and_parse_correctly()
    {
        byte[] sign = [0x02, 0x79, 0x02, 0x00, 0x05];
        byte[] reveal = [0x02, 0x7d, 0x01, 0x03, 0x02];
        byte[] confirm = [0x02, 0x7e];
        byte[] crew = [0x02, 0x7f, 0x01, 0x03, 0x00, 0x03, 0xff];

        byte[] stream = [.. sign, .. reveal, .. confirm, .. crew];
        var framed = InstanceProtocol.SplitClientMessages(stream, out var partial, out var unknown);
        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Equal(new ushort[]
        {
            InstanceHeist.SignContractId, InstanceHeist.RevealBlueprintId,
            InstanceHeist.ConfirmBlueprintId, InstanceHeist.SetBlueprintCrewId,
        }, framed.Select(m => m.Id));

        Assert.True(InstanceHeist.TryReadSignContract(framed[0].Bytes, out var rogues));
        Assert.Equal([0x00, 0x05], rogues);

        Assert.True(InstanceHeist.TryReadRevealBlueprint(framed[1].Bytes, out var revReq));
        Assert.Equal(new InstanceHeist.RevealBlueprintRequest(1, 3, 2), revReq);

        Assert.True(InstanceHeist.TryReadSetBlueprintCrew(framed[3].Bytes, out var crewReq));
        Assert.Equal(1, crewReq.WingIndex);
        Assert.Equal([0x00, 0x03, 0xff], crewReq.Crew);
    }
}
