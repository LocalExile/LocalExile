using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class JuggernautNodesTests
{
    // Block chance caps at 90%, so a certain block needs a certain roll.
    sealed class LowRolls : Random
    {
        public override double NextDouble() => 0;
    }

    // Expected numbers: wiki Stun (200 x damage / threshold, 20% or less ignored, reduction above 75 diminishes, 350 ms)
    // and Metadata/Characters/Character.ot's stun multipliers and melee duration.
    [Fact]
    public void Stuns_follow_the_wiki_and_grant_unyieldings_charge()
    {
        Assert.Equal(0, HitReaction.Chance(new(100), 1000));
        Assert.Equal(40, HitReaction.Chance(new(200), 1000), 6);
        Assert.Equal(100, HitReaction.Chance(new(500), 1000));
        Assert.Equal(40, HitReaction.Chance(new(150, ThresholdReductionPct: 25), 1000), 6);
        Assert.Equal(40, HitReaction.Chance(new(25, ThresholdReductionPct: 100), 1000), 6);   // 100 -> 87.5

        Assert.Equal(125, HitReaction.StunningDamage(100, physicalShare: 1, melee: true), 6);
        Assert.Equal(100, HitReaction.StunningDamage(100, physicalShare: 0, melee: true), 6);
        Assert.Equal(75, HitReaction.StunningDamage(100, physicalShare: 1, melee: false), 6);
        Assert.Equal(56.25, HitReaction.StunningDamage(100, physicalShare: 0, melee: false), 6);

        var conn = new Player(new(1, "stun", 1000, 1000, 100, 100, null, []), 1, 1);
        var charges = Game.ChargesOf(conn);
        Func<string, int> stats = name => name == "chance_to_gain_endurance_charge_when_you_stun_enemy_%" ? 100 : 0;
        charges.Observe(stats);

        var boss = new SpawnedMonster("boss", new(0, 0, 0), 0, 0, 1000) { Rarity = MonsterRarity.Unique, HitAnimations = [new(0, 2)] };
        Assert.True(HitReaction.TryFlinch(boss, new(500, DurationPct: 100, Melee: true, Owner: conn), 10, new Random(1)));
        Assert.Equal(11.05, boss.FlinchUntil, 6);   // .35 x 2 x 1.5
        Assert.False(HitReaction.TryFlinch(boss, new(500), 15.04, new Random(1)));   // 4 s after the stun ended
        Assert.True(HitReaction.TryFlinch(boss, new(500), 15.06, new Random(1)));
        Assert.Equal(15.41, boss.FlinchUntil, 6);

        charges.Step(16, stats, () => default, alive: true, moving: false, inTown: false, new Random(1));
        Assert.Equal(1, charges.Effective(ChargeType.Endurance));
        Assert.False(HitReaction.TryFlinch(new("scratch", new(0, 0, 0), 0, 0, 1000) { HitAnimations = [new(0, 2)] }, new(100), 0, new Random(1)));
    }
}
