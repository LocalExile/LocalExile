using GameServer.Core.Protocol.Instance;
using Xunit;

namespace GameServer.Gateway.Tests;

// A Sacred Grove crop's HarvestWorldObject (reader 0x141eb0cb0) carries its seed list: u8 n, n × {u8 HarvestSeeds row,
// u8 count}, after the components before it and before the tasks byte.
public class HarvestWorldObjectBlobTests
{
    [Fact]
    public void Writes_the_seed_rows_and_counts_last()
    {
        var states = new StateMachineData([0, 0, 0], 0, ["hatched", "withered", "colour"]).Set("colour", 2);
        var crop = new QuestObject(10, 20, ["Positioned", "StateMachine", "HarvestWorldObject"])
        {
            StateMachine = states,
            HarvestSeeds = [(5, 3), (16, 20)],
        };
        var without = new QuestObject(10, 20, ["Positioned", "StateMachine"]) { StateMachine = states };

        byte[] blob = crop.Blob(), plain = without.Blob();

        Assert.True(QuestObject.CanSerialize(crop.Components, true));
        Assert.Equal(plain[..^1], blob[..(plain.Length - 1)]);
        Assert.Equal(new byte[] { 0x02, 0x05, 0x03, 0x10, 0x14, 0x00 }, blob[(plain.Length - 1)..]);

        // Oshabi's soul tree (soultree.ot's read components) is a quest object.
        Assert.True(QuestObject.CanSerialize(["Positioned", "BaseEvents", "Animated", "InteractionAction", "StateMachine", "MinimapIcon"], true));
        // Her Root Wall is a monster whose type reads TriggerableBlockage after its StateMachine: unblocked, one zero byte.
        var wallStates = new StateMachineData([0, 0], 0, ["active", "flinch"]);
        string[] wall = [.. MonsterObject.BaseComponents, "StateMachine", "TriggerableBlockage"];
        byte[] walled = new MonsterObject(1, 2, 100, wall, StateMachine: wallStates).Blob();
        byte[] bare = new MonsterObject(1, 2, 100, [.. MonsterObject.BaseComponents, "StateMachine"], StateMachine: wallStates).Blob();
        Assert.True(MonsterObject.CanSerialize(wall, true));
        Assert.Equal([.. bare[..^1], 0x00, 0x00], walled);
    }
}
