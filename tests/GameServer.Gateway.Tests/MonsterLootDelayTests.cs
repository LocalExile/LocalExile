using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class MonsterLootDelayTests
{
    [Fact]
    public void Native_delays_gate_visibility_and_pickup_and_survive_pause_and_reentry()
    {
        var monsters = new MonsterCatalog();
        Assert.Equal(3500, monsters.Find("Metadata/Monsters/VaalSpiderGod/Arakaali")!.ItemDropDelayMillis);
        Assert.Equal(12000, monsters.Find("Metadata/Monsters/AtlasBosses/TheElder/TheElderUber")!.ItemDropDelayMillis);
        Assert.Equal(0, monsters.Find("Hillock")!.ItemDropDelayMillis);
        var time = new ManualTime();
        var store = new InstanceStore(time, TimeSpan.FromMinutes(6));
        var state = store.Enter(42, 1);
        var item = new WorldItemObject(3, 4, new Item(0, [], 0));
        uint ordinary = state.Ground.Add(item);
        var batch = state.Ground.AddDelayedBatch([item, item], 3500);
        uint boss = batch[0];
        uint elder = state.Ground.AddDelayed(item, 12000);
        Assert.NotNull(state.Ground.Get(ordinary));
        Assert.Null(state.Ground.Take(boss));
        Assert.Single(state.Ground.All());
        Assert.Single(state.Ground.Within(3, 4, 1, _ => true));
        time.Advance(3499);
        Assert.Empty(state.Ground.Delayed());
        state.Ground.SetPaused(true);
        time.Advance(10000);
        Assert.Null(state.Ground.Get(boss));
        state.Ground.SetPaused(false);
        time.Advance(1);
        Assert.Equal(batch, state.Ground.Delayed().Select(e => e.Id));
        Assert.Same(item, state.Ground.Take(boss));
        Assert.Same(item, state.Ground.Take(batch[1]));
        Assert.Empty(state.Ground.Delayed());
        store.Leave(state);
        time.Advance(8499);
        var reentered = store.Enter(42, 1);
        Assert.Same(state, reentered);
        Assert.Null(reentered.Ground.Get(elder));
        time.Advance(1);
        Assert.Equal(elder, Assert.Single(reentered.Ground.Delayed()).Id);
        Assert.Same(item, reentered.Ground.Take(elder));
        Assert.Null(reentered.Ground.Take(elder));
    }

    sealed class ManualTime : TimeProvider
    {
        long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void Advance(int milliseconds) => ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }
}
