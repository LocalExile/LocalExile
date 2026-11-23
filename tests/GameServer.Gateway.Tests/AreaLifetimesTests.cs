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
    public void Retiring_a_replayed_shared_pet_forgets_it_and_removes_it_for_all_viewers()
    {
        var area = new GameArea(1);
        var firstViewer = new Player(new(1, "hideout-test", 100, 100, 0, 0, null, []), 1, 1);
        var rejoiningViewer = new Player(new(1, "hideout-test", 100, 100, 0, 0, null, []), 1, 1);
        const uint id = 0x04000001;
        area.Delta.Add(id, InstanceEntities.CreateObject(id, 42, [0, 0]), kind: AreaDelta.Kind.Create);
        area.Delta.Take();

        area.Objects.Join(rejoiningViewer);
        Assert.Same(rejoiningViewer, Assert.Single(area.Delta.Take()).Only);

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
}
