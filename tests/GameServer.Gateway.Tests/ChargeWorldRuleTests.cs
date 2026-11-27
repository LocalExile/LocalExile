using GameServer.Core.Protocol.Instance;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public sealed class ChargeWorldRuleTests
{
    [Fact]
    public void Kill_rolls_match_the_original_sequence_after_a_rule_is_renamed()
    {
        InTestWorld(edit =>
        {
            // Move the first basic rule to the end alphabetically, keeping its explicit position.
            edit("""
                PRAGMA defer_foreign_keys = ON;
                UPDATE fx_rules SET id = 'zzz_endurance' WHERE id = 'charge_on_kill_endurance';
                UPDATE fx_rule_sources SET rule = 'zzz_endurance' WHERE rule = 'charge_on_kill_endurance';
                UPDATE fx_rule_when SET rule = 'zzz_endurance' WHERE rule = 'charge_on_kill_endurance';
                UPDATE fx_rule_if SET rule = 'zzz_endurance' WHERE rule = 'charge_on_kill_endurance';
                UPDATE fx_rule_do SET rule = 'zzz_endurance' WHERE rule = 'charge_on_kill_endurance';
                """);
            int Chance(uint stat) => stat switch
            {
                Stat.EnduranceChargeOnKillPct => 17,
                Stat.AddFrenzyChargeOnKillPctChance => 31,
                Stat.AddPowerChargeOnKillPctChance => 43,
                Stat.PowerFrenzyOrEnduranceChargeOnKillPct => 67,
                Stat.EnduranceChargeOnKillPercentChanceWhileHoldingShield => 59,
                Stat.AddFrenzyChargeOnKillPctChanceWhileDualWielding => 29,
                Stat.ChanceToGainPowerChargeOnKillingFrozenEnemyPct => 23,
                Stat.ChanceToGainFrenzyChargeOnKillingFrozenEnemyPct => 71,
                Stat.GainFrenzyChargeOnKillVsEnemiesWith5PlusPoisonsPct => 37,
                Stat.GainPowerChargeOnKillVsEnemiesWithLessThan5PoisonsPct => 53,
                _ => 0
            };
            for (int seed = 0; seed < 64; seed++)
            {
                var expected = new Random(seed);
                var actual = new Random(seed);
                foreach (int poisons in new[] { 0, 4, 5, 6 })
                foreach (bool frozen in new[] { false, true })
                foreach (var weapon in new WeaponProfile[] { default, new() { Shield = true }, new() { DualWield = true } })
                {
                    var kill = new ChargeEvent.Kill(MonsterRarity.Normal, Frozen: frozen, Poisons: poisons);
                    Assert.Equal(ChargeGains.Roll(kill, Chance, weapon, expected, ChargeGains.Rules),
                        ChargeGains.Roll(kill, Chance, weapon, actual));
                    Assert.Equal(expected.Next(), actual.Next());
                }
            }
        });
    }

    [Fact]
    public void Reload_applies_combined_conditions_charge_type_and_enabled_flag()
    {
        InTestWorld(edit =>
        {
            // This world alone requires a shield for the frozen Power source. Frenzy is an independent control.
            edit("""
                INSERT INTO fx_rule_if (rule, ord, condition, args)
                VALUES ('charge_on_kill_vs_frozen_1_power', 2, 'equipment', '{"is":"holding_shield"}');
                """);
            int Chance(uint stat) => stat is Stat.ChanceToGainPowerChargeOnKillingFrozenEnemyPct
                or Stat.ChanceToGainFrenzyChargeOnKillingFrozenEnemyPct ? 100 : 0;
            void Check(bool shield, bool frozen, params ChargeType[] expected)
            {
                var gains = ChargeGains.Roll(new ChargeEvent.Kill(MonsterRarity.Normal, Frozen: frozen),
                    Chance, new() { Shield = shield }, new Random(1));
                Assert.Equal(expected, gains.Select(g => g.Type));
            }
            Check(false, false);
            Check(true, false);
            if (ItemEffectRules.Enabled) Check(false, true, ChargeType.Frenzy);
            else Check(false, true, ChargeType.Power, ChargeType.Frenzy);
            Check(true, true, ChargeType.Power, ChargeType.Frenzy);

            edit("""UPDATE fx_rule_do SET args = '{"charge":"frenzy"}' WHERE rule = 'charge_on_kill_vs_frozen_1_power';""");
            Check(true, true, ItemEffectRules.Enabled ? ChargeType.Frenzy : ChargeType.Power, ChargeType.Frenzy);
            edit("UPDATE fx_rules SET enabled = 0 WHERE id = 'charge_on_kill_vs_frozen_1_power';");
            if (ItemEffectRules.Enabled) Check(true, true, ChargeType.Frenzy);
            else Check(true, true, ChargeType.Power, ChargeType.Frenzy);
        });
    }

    [Fact]
    public void Maligaro_uses_active_poisons_at_death_and_reloads_its_threshold()
    {
        InTestWorld(edit =>
        {
            int Chance(uint stat) => stat is Stat.GainPowerChargeOnKillVsEnemiesWithLessThan5PoisonsPct
                or Stat.GainFrenzyChargeOnKillVsEnemiesWith5PlusPoisonsPct ? 100 : 0;
            void Check(int count, ChargeType expected)
            {
                var kill = new ChargeEvent.Kill(MonsterRarity.Normal, Poisons: count);
                Assert.Equal(expected, Assert.Single(ChargeGains.Roll(kill, Chance, default, new Random(1))).Type);
            }

            var poisons = new MonsterPoisons();
            Check(poisons.Stacks(0), ChargeType.Power);
            for (int i = 0; i < 4; i++) poisons.Add(0, 1, 2);
            Check(poisons.Stacks(0), ChargeType.Power);
            poisons.Add(0, 1, 1);
            Check(poisons.Stacks(0), ChargeType.Frenzy);
            Check(poisons.Stacks(1), ChargeType.Power); // The fifth stack expired before death.
            poisons.Add(1, 1, 2);
            poisons.Add(1, 1, 2);
            Check(poisons.Stacks(1), ChargeType.Frenzy);

            poisons.RememberDeath(1);
            poisons.ClearAtDeath(3);
            Check(poisons.StacksAtDeath(4), ChargeType.Frenzy); // Delayed hit-kill callback.

            Check(3, ChargeType.Power);
            edit("""
                UPDATE fx_rule_if SET args = '{"at_least":3}'
                WHERE rule IN ('maligaro_poison_frenzy', 'maligaro_poison_power') AND condition = 'enemy_poison_count_at_death';
                """);
            Check(2, ChargeType.Power);
            Check(3, ItemEffectRules.Enabled ? ChargeType.Frenzy : ChargeType.Power);
        });
    }

    [Fact]
    public void A_lethal_poison_tick_awards_the_charge_for_the_count_at_death()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var target = new SpawnedMonster("poisoned", new(105, 100, 0), 0, 0, 25) { Hostile = true };
        for (int i = 0; i < 5; i++) target.Poisons.Add(0, 10, 1);
        // The monster dies at 0.5 seconds. By the time the kill callback runs, all five stacks have expired.
        Assert.Equal(50, target.Poisons.Advance(2, 1, lifeAtStart: 25));
        target.Poisons.ClearAtDeath(2, poisonDeath: true);
        Assert.Equal(0, target.Poisons.Stacks(2));

        var player = fixture.Player;
        Game.ChargesOnKill(player, target);
        var charges = Game.ChargesOf(player);
        int Chance(uint stat) => stat is Stat.GainPowerChargeOnKillVsEnemiesWithLessThan5PoisonsPct
            or Stat.GainFrenzyChargeOnKillVsEnemiesWith5PlusPoisonsPct ? 100 : 0;
        charges.Step(2, Chance, () => default, alive: true, moving: false, inTown: false, new Random(1));
        Assert.Equal(1, charges.Effective(ChargeType.Frenzy));
        Assert.Equal(0, charges.Effective(ChargeType.Power));
    }

    static void InTestWorld(Action<Action<string>> test)
    {
        string path = Path.ChangeExtension(TestEnvironment.ScratchWorld, Guid.NewGuid() + ".db");
        try
        {
            GameData.Open(path);
            using var db = SqlUpdater.Open(path);
            void Edit(string sql)
            {
                using var transaction = db.BeginTransaction();
                using var command = db.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.ExecuteNonQuery();
                transaction.Commit();
                GameData.Reload("charge rule test");
            }
            test(Edit);
        }
        finally
        {
            GameData.Open(TestEnvironment.ScratchWorld);
        }
    }
}
