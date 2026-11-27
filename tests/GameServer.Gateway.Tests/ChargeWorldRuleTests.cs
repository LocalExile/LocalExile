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
                _ => 0
            };
            for (int seed = 0; seed < 64; seed++)
            {
                var expected = new Random(seed);
                var actual = new Random(seed);
                foreach (bool frozen in new[] { false, true })
                foreach (var weapon in new WeaponProfile[] { default, new() { Shield = true }, new() { DualWield = true } })
                {
                    var kill = new ChargeEvent.Kill(MonsterRarity.Normal, Frozen: frozen);
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
