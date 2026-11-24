using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modules.Legion;

namespace GameServer.Gateway.Tests;

public sealed class LegionTests
{
    [Fact]
    public async Task In_the_areas_frame_a_record_goes_once_to_every_client_and_a_joiners_to_it_alone()
    {
        var layout = new AreaLayout(0x1e9, "MapWorldsReef", 600, 600, 300, 300, (_, _) => true, []);
        var instance = new InstanceState();
        Player Make()
        {
            var player = new Player(layout, 1, 1) { Instance = instance, Connection = { PlayerSent = true } };
            instance.AddPlayer(player);
            player.Connection.WorldOutput.Begin(1);
            return player;
        }
        Player first = Make(), second = Make();
        Assert.Same(first.Area, second.Area);
        const uint id = 0x500;
        var owner = GameArea.Own(first.Area);
        try
        {
            // A per-player loop would put one copy per player in the frame that every client is written.
            await LegionOutput.ToArea(first, p => Program.Send(p, InstanceProtocol.EnterWakeZone(id), null));
            Assert.Null(Assert.Single(first.Area.Delta.Take()).Only);
            await LegionOutput.ToOne(second, () => Program.Send(second, InstanceProtocol.EnterWakeZone(id), null));
            Assert.Same(second, Assert.Single(first.Area.Delta.Take()).Only);
        }
        finally { GameArea.Own(owner); }
    }

    [Fact]
    public void An_actors_current_action_and_its_action_data_are_written_as_the_client_reads_them()
    {
        const uint id = 0x08000123;
        var action = new ActorAction(0x18fe, 7) { Animation = new AnimationControllerState(3, 0f), Payload = [0, 100, 0, 0, 0] };
        // 0x2bc to the Actor (index 9): f0 0x04, f1 0; then little-endian u16 skill, the action record (u16 sequence, u32 target
        // id, u32 x, u32 y, u16 flags), u8, the animation block (u32 path length, i32 clip, f32 time, f32 speed, f32, i32 queued)
        // and the class's payload as is.
        byte[] action4 =
        [
            0x04, 0x00, 0xfe, 0x18,
            0x07, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x00, 0x00, 0x00,
            0, 0, 0, 0, 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 0xff, 0xff,
            0, 100, 0, 0, 0,
        ];
        Assert.Equal((byte[])[0x02, 0xbc, 0x08, 0x00, 0x01, 0x23, 0, 0, 0, 0, 0, 0, 0x00, (byte)(1 + action4.Length), 0x09, .. action4],
            InstanceEntities.ActorCurrentAction(id, 9, action));
        // A monster created with it carries the same bytes after its Actor's flags (here no skill list), from its first frame.
        var monster = new MonsterObject(10, 10, 100, MonsterObject.BaseComponents) { CurrentAction = action };
        var blob = monster.Blob();
        Assert.True(blob.AsSpan().IndexOf(action4) > 0);
        Assert.Equal(-1, new MonsterObject(10, 10, 100, MonsterObject.BaseComponents).Blob().AsSpan().IndexOf(action4.AsSpan(0, 4)));
        // S→C 0x2c8: big-endian object header, action type, skill id, sequence, u8 sub-id, u16 length, the payload as is.
        Assert.Equal((byte[])[0x02, 0xc8, 0x08, 0x00, 0x01, 0x23, 0, 0, 0, 0, 0, 0, 0xd8, 0xfe, 0x18, 0xfe, 0x00, 0x07, 0x00, 0x00, 0x05, 2, 100, 0, 0, 0],
            InstanceProtocol.ActionData(id, 0xd8fe, 0x18fe, 7, 0, [2, 100, 0, 0, 0]));
    }
}
