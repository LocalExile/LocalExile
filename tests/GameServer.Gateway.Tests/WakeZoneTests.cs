using GameServer.Core.Protocol.Instance;
using Xunit;

namespace GameServer.Gateway.Tests;

/// <summary>The official wake zone: a monster or a corpse is in the wake zone while
/// max(|dx|, |dy|) &lt;= 172 around the player -- a square, not the old Euclid circle (171 out, 163 back).
/// The diagonal is where the square matters: (172, 172) is still awake, where the circle had already put it
/// to sleep at (121, 121). Pure geometry and area wake step; nothing here waits on real time.</summary>
public sealed class WakeZoneTests
{
    [Fact]
    public void Square_half_side_172_holds_the_diagonal_and_stops_at_173()
    {
        Assert.True(Game.InWakeZone((172, 172), (0, 0)));
        Assert.True(Game.InWakeZone((172, 0), (0, 0)));
        Assert.True(Game.InWakeZone((0, 172), (0, 0)));
        Assert.False(Game.InWakeZone((173, 0), (0, 0)));
        Assert.False(Game.InWakeZone((173, 173), (0, 0)));
        Assert.True(Game.InWakeZone((200, 100), (28, 100)));
        Assert.True(Game.InWakeZone((28, 272), (28, 100)));
        Assert.False(Game.InWakeZone((201, 100), (28, 100)));
        Assert.False(Game.InWakeZone((28, 273), (28, 100)));
    }

    [Fact]
    public void StepAreaWake_wakes_and_sleeps_live_monsters_and_corpses_but_skips_consumed_or_shattered_corpses()
    {
        var player = new Player(new(1, "wake-test", 1000, 1000, 100, 100, null, []), 1, 1) { Instance = new() };
        var area = player.Area;
        var live = new SpawnedMonster("live", new(300, 300, 10), 0, 0, 100) { Life = 100 };
        var corpse = new SpawnedMonster("corpse", new(300, 300, 0), 0, 0, 100) { Dead = true, Life = 0 };
        var consumed = new SpawnedMonster("consumed", new(300, 300, 0), 0, 0, 100) { Dead = true, Life = 0, CorpseConsumed = true };
        var shattered = new SpawnedMonster("shattered", new(300, 300, 0), 0, 0, 100) { Dead = true, Life = 0 };
        shattered.Ailments.Shatter();
        area.Monsters[1] = live;
        area.Monsters[2] = corpse;
        area.Monsters[3] = consumed;
        area.Monsters[4] = shattered;

        // Diagonal inside the square (dx=172, dy=172): all stay awake, nothing emitted.
        Game.StepAreaWake(player, [(128, 128)], 0);
        Assert.Empty(area.Delta.Take());
        Assert.False(live.Asleep);
        Assert.False(corpse.Asleep);

        // One cell outside (dx=173): live and unconsumed corpse sleep; consumed and shattered corpses are ignored.
        Game.StepAreaWake(player, [(127, 128)], 0);
        var sleepRecords = area.Delta.Take();
        Assert.True(live.Asleep);
        Assert.True(corpse.Asleep);
        Assert.False(consumed.Asleep);
        Assert.False(shattered.Asleep);
        Assert.Equal(InstanceProtocol.LeaveWakeZone(1), Assert.Single(sleepRecords, r => r.Entity == 1).Bytes);
        Assert.Equal(InstanceProtocol.LeaveWakeZone(2), Assert.Single(sleepRecords, r => r.Entity == 2).Bytes);
        Assert.DoesNotContain(sleepRecords, r => r.Entity is 3 or 4);

        // Back inside at (128, 128): live gets EnterWakeZone + Teleport; corpse gets EnterWakeZone only.
        Game.StepAreaWake(player, [(128, 128)], 0);
        var wakeRecords = area.Delta.Take();
        Assert.False(live.Asleep);
        Assert.False(corpse.Asleep);
        Assert.Equal([InstanceProtocol.EnterWakeZone(1), InstanceProtocol.Teleport(1, 300, 300)],
            wakeRecords.Where(r => r.Entity == 1).Select(r => r.Bytes).ToArray());
        Assert.Equal(InstanceProtocol.EnterWakeZone(2), Assert.Single(wakeRecords, r => r.Entity == 2).Bytes);
        Assert.DoesNotContain(wakeRecords, r => r.Entity is 3 or 4);
    }
}
