using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class GroundDropSoundTests
{
    [Fact]
    public async Task New_drops_sound_once_for_existing_observers_but_replays_restores_and_stack_remainders_are_quiet()
    {
        var item = new Item(0x11223344, [], 0);
        var old = new WorldItemObject(110, 100, item);
        byte[] replay = old.Blob(), drop = old.Blob(justDropped: true);
        Assert.Equal(replay.Length, drop.Length);
        Assert.Equal(0, replay[23]); Assert.Equal(1, drop[23]);
        Assert.Equal(replay.Where((_, i) => i != 23), drop.Where((_, i) => i != 23));
        var ground = new InstanceGround();
        uint earlier = ground.Add(old);
        object observer = new(), other = new();
        ground.Observe(observer); ground.Observe(other);
        Assert.False(ground.IsNewFor(observer, earlier));
        uint fresh = ground.Add(old);
        Assert.True(ground.IsNewFor(observer, fresh));
        Assert.True(ground.IsNewFor(other, fresh));
        ground.Observe(observer); // Reconciliation/reload does not reset an admitted observer's baseline.
        Assert.True(ground.IsNewFor(observer, fresh));
        object later = new(); ground.Observe(later);
        Assert.False(ground.IsNewFor(later, fresh));
        ground.Take(fresh); ground.Restore(fresh, old);
        Assert.False(ground.IsNewFor(observer, fresh));
        uint remainder = ground.Add(old, null, shared: true, justDropped: false);
        Assert.False(ground.IsNewFor(observer, remainder));

        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        using var lateWire = new PartyFollowerTests.Wire();
        var instance = new InstanceState();
        var viewer = runtime.Connection(instance, wire);
        var joining = runtime.Connection(instance, lateWire);
        instance.Ground.Observe(viewer);
        var freshObject = new WorldItemObject(111, 100, item);
        uint id = instance.Ground.Add(freshObject);
        string sound = Convert.ToHexString(InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), freshObject.Blob(justDropped: true)));
        int Count(PartyFollowerTests.Wire w, string hex) => Program.HistoryFor(w.Ciphers).Lines()
            .Count(line => line.Contains(hex[..Math.Min(hex.Length, PacketHistory.KeptBytes * 2)], StringComparison.Ordinal));
        await Game.SyncSharedGround("sound-observer", wire.Stream, wire.Ciphers, viewer);
        Assert.Equal(1, Count(wire, sound));
        await Game.SyncSharedGround("sound-observer-repeat", wire.Stream, wire.Ciphers, viewer);
        Assert.Equal(1, Count(wire, sound));
        await Game.SendGroundItems("sound-reentry", lateWire.Stream, lateWire.Ciphers, joining);
        Assert.Equal(0, Count(lateWire, sound));
        Assert.Equal(1, Count(lateWire, Convert.ToHexString(InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), freshObject.Blob()))));
        uint privateId = instance.Ground.Add(old, joining, shared: false);
        await Game.SyncSharedGround("sound-private", wire.Stream, wire.Ciphers, viewer);
        Assert.DoesNotContain(privateId, viewer.SeenGround);

        // A tutorial gem: its first recipient gets one create and its wake, as world records of the new object. The
        // shared ground knows that recipient, and a connection that comes later sees the item as an old one.
        string path = Path.Combine(Path.GetTempPath(), $"localexile-tutorial-gem-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        var gifted = new ClientState(new(1, "tutorial-gem", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = $"TutorialGem{Guid.NewGuid():N}", ClassRow = 0, Instance = new(),
            LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            Game.ValidateSavedProgress(gifted, null);
            Game.PrepareCharacterForArea("tutorial-gem", gifted, 1);
            foreach (bool support in new[] { false, true })
            {
                var existingObserver = new object();
                gifted.Instance.Ground.Observe(existingObserver);
                var packets = QuestobjectsRules.CommitTutorialGem(gifted, support, 100, 100);
                Assert.Equal(2, packets.Count);
                Assert.Equal(InstanceEntities.CreateObjectId, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packets[0].Packet));
                uint gem = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packets[0].Packet.AsSpan(2));
                Assert.Equal(InstanceProtocol.EnterWakeZone(gem), packets[1].Packet);
                Assert.True(packets[0].CreatesObject);
                Assert.Equal(gem, packets[0].WorldId);
                Assert.Equal(gem, packets[1].WorldId);
                var origin = gifted.Instance.Ground.Origin(gem);
                Assert.Same(gifted, origin.By);
                Assert.True(origin.Shared);
                var placed = Assert.IsType<WorldItemObject>(gifted.Instance.Ground.Get(gem));
                Assert.Equal(InstanceEntities.CreateObject(gem, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath),
                    placed.Blob(justDropped: true)), packets[0].Packet);
                Assert.True(gifted.Instance.Ground.IsNewFor(existingObserver, gem));
                var laterObserver = new object();
                gifted.Instance.Ground.Observe(laterObserver);
                Assert.False(gifted.Instance.Ground.IsNewFor(laterObserver, gem));
                Assert.Empty(QuestobjectsRules.CommitTutorialGem(gifted, support, 100, 100));
            }
        }
        finally
        {
            PlayerInventories.Forget(gifted.Character!);
            GameServer.Gateway.Tests.TestEnvironment.ReleaseSaves(path);
        }
    }
}
