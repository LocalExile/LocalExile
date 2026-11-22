using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class DoubleStunDurationTests
{
    [Fact]
    public void Goliath_rolls_double_duration_once_for_a_successful_stun()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var damage = DamagePacket.Of(DamageType.Physical, 1000, 1000);
        uint modifier = Program.Stats.Key("chance_to_double_stun_duration_%")!.Value;
        double Duration(int chance, int roll)
        {
            var target = new SpawnedMonster("stun target", new(10, 10, 0), 0, 0, 1000)
                { HitAnimations = [new(0, 2)] };
            var stats = new Dictionary<uint, int> { [modifier] = chance };
            var random = new DurationRoll(roll);
            lock (f.Conn.Monsters)
                Assert.True(HitReaction.TryFlinch(target,
                    Game.OwnerStun(f.Conn, target, 1000, damage, melee: true, spell: false, critical: false, stats, null),
                    20, random));
            Assert.Equal(chance is > 0 and < 100 ? 1 : 0, random.DurationRolls);
            return target.FlinchUntil - 20;
        }

        Assert.Equal(.525, Duration(0, 0), 6);
        Assert.Equal(1.05, Duration(50, 49), 6);
        Assert.Equal(.525, Duration(50, 50), 6);
        Assert.Equal(1.05, Duration(100, 99), 6);
    }

    sealed class DurationRoll(int outcome) : Random(11)
    {
        public int DurationRolls { get; private set; }
        public override double NextDouble() => 0;
        public override int Next(int minValue, int maxValue)
        {
            DurationRolls++;
            return outcome;
        }
    }
}
