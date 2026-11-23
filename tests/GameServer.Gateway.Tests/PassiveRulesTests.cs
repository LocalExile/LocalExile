using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class PassiveRulesTests
{
    const string Hound = "Metadata/Monsters/Hound/Hound",                                    // ObjectSize 4
        Soldier = "Metadata/Monsters/LeagueHeist/Military/MilitaryMelee1";                    // ObjectSize 2

    static SpawnedMonster Monster(string variety) =>
        new("monster", new(100, 100, 0), 0, 0, 100) { Variety = Game.Monsters.Find(variety), Hostile = true, Life = 100 };

    [Fact]
    public void Master_Toxicist_spreads_poison_to_each_enemys_body_and_doubles_poisons_during_a_flask_effect()
    {
        // 1.5 metres is measured to the receiver's body: a wide enemy is reached 15 cells from its edge.
        Assert.Equal(15 + 3, Game.DuringFlaskSpreadReach(Monster(Hound)));
        Assert.Equal(15 + 1, Game.DuringFlaskSpreadReach(Monster(Soldier)));
        // The spread poison lasts the default 2 s with the character's poison and ailment duration increases.
        Assert.Equal(2.8, Game.SpreadPoisonSeconds(new Dictionary<uint, int>
            { [Stat.BasePoisonDurationPlusPct] = 25, [Stat.BaseAllAilmentDurationPlusPct] = 15 }.GetValueOrDefault), 6);

        // Each poison applied during any flask effect rolls the passive's chance to deal 100% more damage.
        var player = new Player(new(1, "toxicist", 300, 300, 100, 100, null, []), 1, 1);
        var physical = DamagePacket.Of(DamageType.Physical, 100, 100);
        var rows = new MonsterPoisons.Settings(100, Min: 10, Max: 10, CriticalMin: 10, CriticalMax: 10, Seconds: 2);
        double Dps(int chance)
        {
            var stats = new Dictionary<uint, int>
                { [Stat.BaseChanceToPoisonOnHitPct] = 100, [Stat.PathfinderPoisonDamagePlus100PctFinalChanceDuringFlaskEffect] = chance };
            var target = Monster(Soldier);
            Game.PoisonOnHitLocked(player, target, physical, spell: false, critical: false, stats, null, rows, now: 10, new Random(1));
            return target.Poisons.TotalDps(10.5);
        }
        Assert.Equal(10, Dps(100));
        player.FlaskEffects.Start(new Flasks.Effect(0, 1, 0, 10, 0, 0, 100));
        Assert.Equal(10, Dps(0));
        Assert.Equal(20, Dps(100));
    }

    [Fact]
    public void Chaos_pair_resistances_count_the_shield_opt_out_and_recoup_hold_and_Call_to_Arms_doubles_duration()
    {
        // Each chaos pair adds to both resistances it names.
        var bonus = Game.PassiveBonus(new Dictionary<uint, int>
        {
            [Stat.FireAndChaosDamageResistancePct] = 10, [Stat.ColdAndChaosDamageResistancePct] = 20,
            [Stat.LightningAndChaosDamageResistancePct] = 30,
        }, default, default);
        Assert.Equal((10, 20, 30, 60), (bonus.FireResistance, bonus.ColdResistance, bonus.LightningResistance, bonus.ChaosResistance));

        using var f = new TriggeredSummonTests.Fixture();
        var player = f.Player;
        player.EnergyShield.SetMaximum(200, refill: true);
        var chaos = DamagePacket.Of(DamageType.Chaos, 60, 60);
        var stats = new Dictionary<uint, int>();
        // Chaos bypasses Energy Shield unless a mod opts out of it.
        Assert.Equal(60, Game.DamagePlayerHitTyped(player, 0, 0, 60, 1, stats, chaos));
        Assert.Equal(200u, player.EnergyShield.Current);
        stats[Stat.ChaosDamageDoesNotBypassEnergyShield] = 1;
        Assert.Equal(0, Game.DamagePlayerHitTyped(player, 0, 0, 60, 2, stats, chaos));
        Assert.Equal(140u, player.EnergyShield.Current);

        // Mana recoup counts the damage Ward takes, and pays it over three seconds with Nine Lives' recoup stat.
        player.Ward.SetMaximum(1000);
        stats[Stat.DamageTakenGoesToManaPct] = 25;
        Assert.Equal(0, Game.DamagePlayerHitTyped(player, 100, 0, 0, 3, stats, DamagePacket.Of(DamageType.Physical, 100, 100)));
        Assert.Equal(25, Game.ResourceEffectsOf(player).Mana.Take(7), 6);
        Assert.Equal(4, Game.ManaRecoupSeconds(_ => 0));
        Assert.Equal(3, Game.ManaRecoupSeconds(key => key == Stat.RecoupEffectsApplyOver3SecondsInstead ? 1 : 0));
        var quick = new ManaRecoupState();
        quick.Hit(0, 400, 25, 3);
        Assert.Equal(33, quick.Take(1), 6);

        // Call to Arms: 100% more Warcry Duration.
        Assert.Equal(1, SkillDurationModifiers.CallToArmsMore(_ => 0));
        Assert.Equal(2, SkillDurationModifiers.CallToArmsMore(key => key == Stat.KeystoneCallToArms ? 1 : 0));
    }
}
