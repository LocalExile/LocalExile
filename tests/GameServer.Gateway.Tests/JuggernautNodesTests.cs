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

        var player = new Player(new(1, "stun", 1000, 1000, 100, 100, null, []), 1, 1);
        var charges = Game.ChargesOf(player);
        Func<uint, int> stats = name => name == Stat.ChanceToGainEnduranceChargeWhenYouStunEnemyPct ? 100 : 0;
        charges.Observe(stats);

        var boss = new SpawnedMonster("boss", new(0, 0, 0), 0, 0, 1000) { Rarity = MonsterRarity.Unique, HitAnimations = [new(0, 2)] };
        Assert.True(HitReaction.TryFlinch(boss, new(500, DurationPct: 100, Melee: true, Owner: player), 10, new Random(1)));
        Assert.Equal(11.05, boss.FlinchUntil, 6);   // .35 x 2 x 1.5
        Assert.False(HitReaction.TryFlinch(boss, new(500), 15.04, new Random(1)));   // 4 s after the stun ended
        Assert.True(HitReaction.TryFlinch(boss, new(500), 15.06, new Random(1)));
        Assert.Equal(15.41, boss.FlinchUntil, 6);

        charges.Step(16, stats, () => default, alive: true, moving: false, inTown: false, new Random(1));
        Assert.Equal(1, charges.Effective(ChargeType.Endurance));
        Assert.False(HitReaction.TryFlinch(new("scratch", new(0, 0, 0), 0, 0, 1000) { HitAnimations = [new(0, 2)] }, new(100), 0, new Random(1)));

        var inputs = new Dictionary<uint, int>
        {
            [Stat.BaseStunDurationPlusPct] = 30,
            [Stat.StunDurationOnCriticalStrikePlusPct] = 100,
            [Stat.StaffStunDurationPlusPct] = 10,
            [Stat.BowStunDurationPlusPct] = 50,
            [Stat.BowStunThresholdReductionPlusPct] = 10,
            [Stat.PassiveMasteryStunDurationPlusPctFinalWithTwoHandWeapon] = 15,
        };
        (double Seconds, int Threshold) Contact(string weapon, bool melee, bool spell, bool critical)
        {
            var enemy = new SpawnedMonster("target", new(0, 0, 0), 0, 0, 1000) { HitAnimations = [new(0, 2)] };
            var hit = Game.OwnerStun(player, enemy, 900, DamagePacket.Of(DamageType.Physical, 900, 900),
                melee, spell, critical, inputs, own: null, localStunReduction: 0, weaponClass: weapon);
            Assert.True(HitReaction.TryFlinch(enemy, hit, 0, new LowRolls()));
            return (enemy.FlinchUntil, hit.ThresholdReductionPct);
        }

        var bow = Contact("Bow", melee: false, spell: false, critical: false);
        Assert.Equal(.35 * 1.80 * 1.15, bow.Seconds, 6);
        Assert.Equal(10, bow.Threshold);
        Assert.Equal(.35 * 1.40 * 1.15 * 1.5, Contact("Staff", true, false, false).Seconds, 6);
        Assert.Equal(.35 * 2.30 * 1.5, Contact("One Hand Sword", true, false, true).Seconds, 6);
        Assert.Equal(.35 * 1.30, Contact("Bow", false, true, false).Seconds, 6);
    }
}
