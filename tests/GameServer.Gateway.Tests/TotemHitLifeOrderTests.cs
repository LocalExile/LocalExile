using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using Xunit;

namespace GameServer.Gateway.Tests;

public sealed class TotemHitLifeOrderTests
{
    // A life above zero for a monster the server has already killed must not be written: two totems' arrows
    // can hit one monster in the same frame, and the earlier hit's life must not revive it after the killing hit.
    [Fact]
    public void A_life_above_zero_for_a_killed_monster_is_dropped_from_the_frame()
    {
        const uint target = 0x8000043, other = 0x8000044;
        var monsters = new Dictionary<uint, SpawnedMonster>();
        var monster = new SpawnedMonster("vendigo", new(10, 10, 0), 0, 0, 99068) { Hostile = true };
        monsters[target] = monster;
        byte[] Life(uint life)
        {
            var record = new byte[21];
            BinaryPrimitives.WriteUInt16BigEndian(record, InstanceProtocol.LifeUpdateId);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(6), 0x400000c);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(12), life);
            record[20] = 0;
            return record;
        }
        // While it lives every arrow's life is written, the sliver included.
        monster.Life = 300;
        Assert.Null(Game.ProjectileSession.RevivingLife(monsters, target, Life(300)));
        // At Life 0 but not dead (a boss between phases): its life is still written.
        monster.Life = 0;
        Assert.Null(Game.ProjectileSession.RevivingLife(monsters, target, Life(300)));
        // Killed by the same frame's other arrow: the older life would revive it, its own death stays.
        monster.Dead = true;
        Assert.Equal(300u, Game.ProjectileSession.RevivingLife(monsters, target, Life(300)));
        Assert.Null(Game.ProjectileSession.RevivingLife(monsters, target, Life(0)));
        // Another object's record, another pool's (mana, energy shield) and a shorter message are never touched.
        Assert.Null(Game.ProjectileSession.RevivingLife(monsters, other, Life(300)));
        var mana = Life(300); mana[20] = 1;
        Assert.Null(Game.ProjectileSession.RevivingLife(monsters, target, mana));
        Assert.Null(Game.ProjectileSession.RevivingLife(monsters, target, new byte[10]));
    }
}
