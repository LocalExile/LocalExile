using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class MonsterAggroMinMaxTests
{
    static SpawnedMonster Monster(int min, int max) => new("aggro", new(100, 100, 0), max, 0, 100)
    {
        Variety = new(0, "Aggro", "Aggro", "Aggro", null, null, AggroRange: max) { MinAgroRange = min },
    };

    [Fact]
    public void A_monster_notices_at_MinAgroRange_and_lets_go_past_MaxAgroRange()
    {
        Assert.Equal((40, 105), (Game.MonsterNoticeRange(Monster(40, 105)), Game.MonsterLeashRange(Monster(40, 105))));
        Assert.Equal((105, 105), (Game.MonsterNoticeRange(Monster(105, 105)), Game.MonsterLeashRange(Monster(105, 105))));
        // No MinAgroRange (portals, traps) or one above MaxAgroRange: the single range and twice it.
        Assert.Equal((105, 210), (Game.MonsterNoticeRange(Monster(0, 105)), Game.MonsterLeashRange(Monster(0, 105))));
        Assert.Equal((105, 210), (Game.MonsterNoticeRange(Monster(120, 105)), Game.MonsterLeashRange(Monster(120, 105))));
        var plain = new SpawnedMonster("plain", new(100, 100, 0), 105, 0, 100);
        Assert.Equal((105, 210), (Game.MonsterNoticeRange(plain), Game.MonsterLeashRange(plain)));
    }
}
