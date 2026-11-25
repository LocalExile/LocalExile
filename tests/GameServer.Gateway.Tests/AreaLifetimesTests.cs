using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class AreaLifetimesTests
{
    [Fact]
    public void Timed_buff_refresh_preserves_remaining_time_and_expiry_removes_only_its_current_instance()
    {
        var area = new GameArea(1);
        var viewer = new Player(new(1, "buff-test", 100, 100, 0, 0, null, []), 1, 1);
        const uint entity = 420;
        const ushort timedRow = 336, untimedRow = 337;
        var first = new Buff(timedRow, U68: Buff.Seconds(2), F6c: 2);
        var refreshed = first with { U68 = Buff.Seconds(4) };

        area.Step(1, 0);
        ushort original = area.Buffs.Next;
        area.Buffs.Set(entity, first);
        area.Buffs.Set(entity, new(untimedRow));
        area.Buffs.Publish();
        Assert.Equal(2, area.Delta.Take().Count);

        area.Step(2, .5);
        ushort replacement = area.Buffs.Next;
        area.Buffs.Set(entity, refreshed);
        area.Buffs.Publish();
        var changes = area.Delta.Take();
        Assert.Equal(InstanceEntities.RemoveBuff(entity, original), changes[0].Bytes);
        Assert.Equal(refreshed.Add(entity, replacement), changes[1].Bytes);

        area.Step(3, 1.5);
        area.Buffs.Publish();
        Assert.Empty(area.Delta.Take());
        Assert.NotNull(area.Buffs.Find(entity, replacement));

        area.Buffs.Join(viewer);
        var remaining = refreshed with { U68 = Buff.Seconds(2) };
        Assert.Contains(area.Delta.Take(), record => ReferenceEquals(record.Only, viewer)
            && record.Bytes.SequenceEqual(remaining.Add(entity, replacement)));

        area.Step(4, 2.5);
        area.Buffs.Publish();
        Assert.Equal(InstanceEntities.RemoveBuff(entity, replacement), Assert.Single(area.Delta.Take()).Bytes);
        Assert.False(area.Buffs.Has(entity, timedRow));
        Assert.True(area.Buffs.Has(entity, untimedRow));

        area.Step(5, 3);
        area.Buffs.Publish();
        Assert.Empty(area.Delta.Take());
    }

    [Fact]
    public void Same_duration_timed_buff_refresh_replaces_instance_and_extends_expiry()
    {
        var area = new GameArea(1);
        const uint entity = 420;
        var lifetap = new Buff(Lifetap.BuffRow, entity, U68: Buff.Seconds(4));

        area.Step(1, 0);
        ushort first = area.Buffs.Next;
        area.Buffs.Set(entity, lifetap);
        area.Buffs.Publish();
        Assert.Equal(lifetap.Add(entity, first), Assert.Single(area.Delta.Take()).Bytes);

        // Refreshing with the same 4.0 s duration must replace the instance and extend the expiry deadline.
        area.Step(2, 2.5);
        ushort second = area.Buffs.Next;
        area.Buffs.Set(entity, lifetap);
        area.Buffs.Publish();
        var refreshed = area.Delta.Take();
        Assert.Equal(2, refreshed.Count);
        Assert.Equal(InstanceEntities.RemoveBuff(entity, first), refreshed[0].Bytes);
        Assert.Equal(lifetap.Add(entity, second), refreshed[1].Bytes);

        // Original 4.0 s deadline must NOT remove the refreshed buff.
        area.Step(3, 4.5);
        area.Buffs.Publish();
        Assert.Empty(area.Delta.Take());
        Assert.True(area.Buffs.Has(entity, Lifetap.BuffRow, entity));

        // Extended deadline (2.5 + 4.0 = 6.5 s) removes only the refreshed instance cleanly.
        area.Step(4, 6.6);
        area.Buffs.Publish();
        Assert.Equal(InstanceEntities.RemoveBuff(entity, second), Assert.Single(area.Delta.Take()).Bytes);
        Assert.False(area.Buffs.Has(entity, Lifetap.BuffRow, entity));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_shared_pet_replays_its_latest_components_until_removed(bool direct)
    {
        var area = new GameArea(1);
        var firstViewer = new Player(new(1, "hideout-test", 100, 100, 0, 0, null, []), 1, 1);
        var rejoiningViewer = new Player(new(1, "hideout-test", 100, 100, 0, 0, null, []), 1, 1);
        const uint id = 0x04000001;
        void Write(byte[] bytes, AreaDelta.Kind kind)
        {
            if (direct) area.Objects.RecordDirect(id, bytes, kind, firstViewer);
            else
            {
                area.Delta.Add(id, bytes, kind: kind);
                area.Delta.Take();
            }
        }

        Write(InstanceEntities.CreateObject(id, 42, [0, 0]), AreaDelta.Kind.Create);
        Write(InstanceEntities.ComponentUpdate(id, 3, [1]), AreaDelta.Kind.World);
        var other = InstanceEntities.ComponentUpdate(id, 5, [2]);
        var latest = InstanceEntities.ComponentUpdate(id, 3, [4]);
        Write(other, AreaDelta.Kind.World);
        Write(latest, AreaDelta.Kind.World);

        area.Objects.Join(rejoiningViewer);
        var replay = area.Delta.Take().Concat(area.Delta.Take()).ToArray();
        Assert.All(replay, record => Assert.Same(rejoiningViewer, record.Only));
        Assert.Single(replay, record => record.Kind == AreaDelta.Kind.Create);
        Assert.Equal(new[] { latest, other }, replay.Where(r => r.Kind == AreaDelta.Kind.World).Select(r => r.Bytes));

        area.Delta.Add(id, InstanceProtocol.EnterWakeZone(id));
        var moved = Assert.Single(area.Delta.Take());
        Assert.Null(moved.Only);
        Assert.True(moved.For(firstViewer));
        Assert.True(moved.For(rejoiningViewer));

        var removed = InstanceProtocol.RemoveObject(id);
        area.Delta.RemoveSharedObject(id, removed);
        var record = Assert.Single(area.Delta.Take());
        Assert.Equal(AreaDelta.Kind.Remove, record.Kind);
        Assert.True(record.For(firstViewer));
        Assert.True(record.For(rejoiningViewer));
        Assert.False(area.Objects.Has(id));

        area.Objects.Join(rejoiningViewer);
        Assert.Empty(area.Delta.Take());
    }

    [Fact]
    public void Replaying_a_peer_player_or_dormant_monster_to_a_joiner_does_not_hijack_subsequent_broadcasts()
    {
        var area = new GameArea(1);
        var host = new Player(new(1, "coop-test", 100, 100, 0, 0, null, []), 1, 1);
        var joiner = new Player(new(1, "coop-test", 100, 100, 0, 0, null, []), 1, 1);
        const uint dormantId = 0x04000099;
        area.DormantMonsters[dormantId] = new("dormant", new(120, 120, 0), 0, 0, 100);

        area.Delta.Add(host.Id, InstanceEntities.CreateObject(host.Id, 0x1234, [0, 0]), kind: AreaDelta.Kind.Create, only: joiner);
        area.Delta.Add(dormantId, InstanceEntities.CreateObject(dormantId, 0x5678, [0, 0]), kind: AreaDelta.Kind.Create, only: joiner);
        Assert.Equal(2, area.Delta.Take().Count);

        Assert.Null(area.Delta.ViewerOf(host.Id, joiner));
        Assert.Null(area.Delta.ViewerOf(dormantId, joiner));

        area.Delta.Add(host.Id, InstanceProtocol.ActionStarted(host.Id, 100, 100, 105, 105, 0x2909, 0xffff, 0x0408));
        area.Delta.Add(dormantId, InstanceProtocol.ActionStarted(dormantId, 120, 120, 125, 125, 0x2909, 0xffff, 0x0408));
        var broadcasts = area.Delta.Take();
        Assert.Equal(2, broadcasts.Count);
        Assert.All(broadcasts, record =>
        {
            Assert.Null(record.Only);
            Assert.True(record.For(host));
            Assert.True(record.For(joiner));
        });
    }
}
