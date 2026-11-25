using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class OasisCheckpointTests
{
    [Fact]
    public void Oasis_checkpoint_tracks_the_last_local_gate_for_each_player()
    {
        var instance = new InstanceState();
        var layout = new AreaLayout(1, ShakariEncounter.Area, 300, 300, 20, 20, (_, _) => true, []);
        var player = new Player(layout, 1, 1) { Instance = instance };
        var other = new Player(layout, 2, 2) { Instance = instance };
        var gate = new AreaTransitionObject(70, 80, layout.Hash, null) { TeleportTo = (150, 150) };
        Assert.Null(ShakariEncounter.CheckpointOf(player));

        ShakariEncounter.RememberGateCheckpoint(player, gate, (152, 155));
        Assert.Equal((152, 155), ShakariEncounter.CheckpointOf(player));
        Assert.Null(ShakariEncounter.CheckpointOf(other));

        var nextGate = gate with { GridX = 210, GridY = 215, TeleportTo = (240, 242) };
        ShakariEncounter.RememberGateCheckpoint(player, nextGate, (242, 244));
        Assert.Equal((242, 244), ShakariEncounter.CheckpointOf(player));

        ShakariEncounter.RememberGateCheckpoint(player, gate with { DestinationHash16 = 2 }, (90, 100));
        Assert.Equal((242, 244), ShakariEncounter.CheckpointOf(player));
        Assert.Null(ShakariEncounter.CheckpointOf(new Player(layout, 1, 1) { Instance = new() }));
    }
}
