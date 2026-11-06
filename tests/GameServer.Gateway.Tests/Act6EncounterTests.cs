using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class Act6EncounterTests
{
    [Fact]
    public void EscortAndBossProgressionCannotSkipPendingStages()
    {
        var first = new RailEscort([(0, 0), (13, 0), (13, 26)], 13);
        var second = new RailEscort([(100, 0), (100, 13)], 13);
        first.Advance(0.5, true);
        Assert.Equal((6.5, 0d), first.Position);
        first.Advance(100, false);
        Assert.Equal((6.5, 0d), first.Position);
        first.Advance(1, true);
        Assert.Equal((13d, 6.5), first.Position);
        Assert.Equal(2, first.Target);
        Assert.Equal(1.5, first.RemainingSeconds);
        Assert.False(second.Complete);
        first.Restore((14, 20));
        Assert.Equal((13d, 20d), first.Position);
        first.Advance(100, true);
        first.Advance(100, true);
        Assert.True(first.Complete);
        Assert.Equal((13d, 26d), first.Position);
        Assert.False(second.Complete);
        second.Advance(1, true);
        Assert.True(second.Complete);

        var gate = new LifePhaseGate([75, 50, 25]);
        uint life = 1000;
        Assert.False(gate.Begin(life, 1000));
        foreach (uint floor in new uint[] { 750, 500, 250 })
        {
            life = gate.Damage(life, 1000, int.MaxValue);
            Assert.Equal(floor, life);
            Assert.Equal(floor, gate.Damage(life, 1000, int.MaxValue));
            Assert.True(gate.Begin(life, 1000));
            Assert.False(gate.Begin(life, 1000));
            Assert.Equal(life, gate.Damage(life, 1000, int.MaxValue));
            gate.Finish();
            int completed = gate.Completed;
            gate.Finish();
            Assert.Equal(completed, gate.Completed);
        }
        gate.DamageTakenPercent = 20;
        Assert.Equal(230u, gate.Damage(life, 1000, 100));
        gate.DamageTakenPercent = 100;
        Assert.Equal(150u, gate.Damage(life, 1000, 100));
        Assert.Equal(0u, gate.Damage(life, 1000, int.MaxValue));
        Assert.False(gate.Begin(0, 1000));
        Assert.Equal(1u, new LifePhaseGate([75]).Damage(1, 1, int.MaxValue));
    }

    [Fact]
    public void CartAndTidePacketsPreserveFollowingComponentAndPacketBoundaries()
    {
        string[] components = ["Positioned", "Animated", "BaseEvents", "Stats", "Movement", "Transitionable", "MinimapIcon"];
        var blob = new QuestObject(471, 448, components, Team: 4).Blob();
        Assert.Equal(4, BitConverter.ToUInt16(blob, 14));
        // Positioned 18, Animated 1, Stats 10; Movement: presence byte, XY, four u32s, two flags.
        int movement = 2 + 18 + 1 + 10;
        Assert.Equal(0, blob[movement]);
        Assert.Equal(471, BitConverter.ToInt32(blob, movement + 1));
        Assert.Equal(448, BitConverter.ToInt32(blob, movement + 5));
        Assert.All(blob[(movement + 9)..(movement + 27)], b => Assert.Equal(0, b));
        Assert.Equal(new byte[] { 1, 0, 0 }, blob[(movement + 27)..]);
        Assert.Equal(5, QuestObject.TransitionableIndex(components));

        using var metadata = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes("data/tables/BuffDefinitions.json"));
        var row = metadata.RootElement[856];
        Assert.Equal("brine_king_tide", row.GetProperty("Id").GetString());
        Assert.False(row.GetProperty("IsRecovery").GetBoolean());
        Assert.False(row.GetProperty("__col44").GetBoolean());
        byte[] buff = BrineKing.TideBuff(0x1000, 7);
        Assert.Equal(27, buff.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, buff[23..]);
        byte[] next = GameServer.Core.Protocol.Instance.InstanceProtocol.LifeUpdate(0x1000, 1000);
        byte[] stream = [.. buff, .. next];
        Assert.Equal(next, stream[27..]);
    }
}
