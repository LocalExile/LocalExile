using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
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

    [Fact]
    public void Ice_Bite_uses_the_minions_supported_skill_and_the_same_world_rule_as_the_player()
    {
        InTestWorld(edit =>
        {
            using var fixture = new TriggeredSummonTests.Fixture();
            var player = fixture.Player;
            var amulet = Game.Uniques.Create("Maligaro's Cruelty", new Random(1))!;
            Game.ItemsOf(player).Add(PlayerInventories.Amulet, 0, 0, amulet);
            ushort unlinked = Assert.Single(fixture.Equip(("Summon Stone Golem", 20)));
            var unlinkedStats = Game.SupportedSkill(player, unlinked).Stats;
            ushort source = Assert.Single(fixture.Equip(("Summon Stone Golem", 20), ("Ice Bite Support", 20),
                ("Power Charge On Critical Support", 20)));
            var stats = Game.SupportedSkill(player, source).Stats;
            int frenzy = stats[Stat.ChanceToGainFrenzyChargeOnKillingFrozenEnemyPct];
            int power = stats[Stat.AddPowerChargeOnCriticalStrikePct];
            Assert.InRange(frenzy, 1, 99);
            Assert.InRange(power, 1, 99);
            Assert.True(Game.CharacterPlayerStats(player).GetValueOrDefault(Stat.GainPowerChargeOnKillVsEnemiesWithLessThan5PoisonsPct) > 0);

            var body = new SpawnedMonster("golem", new(100, 100, 0), 0, 0, 100);
            var fighter = new GolemState.Golem(901, body, Game.GolemData.Find("SummonStoneGolem")!, source);
            var target = new SpawnedMonster("enemy", new(103, 100, 0), 0, 0, 100) { Hostile = true };
            void Check(bool critical, bool killed, IReadOnlyDictionary<uint, int> skill, int expectedFrenzyChance)
            {
                for (int seed = 0; seed < 64; seed++)
                {
                    var expected = new Random(seed);
                    var actual = new Random(seed);
                    bool gainPower = critical && RandomRolls.Chance(skill.GetValueOrDefault(Stat.AddPowerChargeOnCriticalStrikePct), 100, expected);
                    bool gainFrenzy = RandomRolls.Chance(expectedFrenzyChance, 100, expected);
                    fighter.CombatCharges = null;
                    Game.MinionHitChargesLocked(player, fighter, target, skill, critical, killed, 1, actual);
                    Assert.Equal(gainPower ? 1 : 0, fighter.CombatCharges?.Held(ChargeType.Power) ?? 0);
                    Assert.Equal(gainFrenzy ? 1 : 0, fighter.CombatCharges?.Held(ChargeType.Frenzy) ?? 0);
                    Assert.Equal(expected.Next(), actual.Next());
                }
                Assert.False(Game.ChargesOf(player).Pending);
            }
            void PlayerCanGain(bool expected)
            {
                var gains = ChargeGains.Roll(new ChargeEvent.Kill(MonsterRarity.Normal, Frozen: true),
                    key => key == Stat.ChanceToGainFrenzyChargeOnKillingFrozenEnemyPct ? 100 : 0, default, new Random(1));
                Assert.Equal(expected, gains.Any(g => g.Type == ChargeType.Frenzy));
            }
            Check(false, true, stats, 0);
            target.Ailments.Shatter();
            Check(false, false, stats, 0);
            Check(true, true, stats, frenzy);
            Check(false, true, unlinkedStats, 0);
            PlayerCanGain(true);

            edit("UPDATE fx_rules SET enabled = 0 WHERE id = 'charge_on_kill_vs_frozen_2_frenzy';");
            Check(true, true, stats, ItemEffectRules.Enabled ? 0 : frenzy);
            PlayerCanGain(!ItemEffectRules.Enabled);

            edit("""
                UPDATE fx_rules SET enabled = 1 WHERE id = 'charge_on_kill_vs_frozen_2_frenzy';
                DELETE FROM fx_rule_when WHERE rule = 'charge_on_kill_vs_frozen_2_frenzy' AND ord = 1;
                """);
            Check(true, true, stats, ItemEffectRules.Enabled ? 0 : frenzy);
            PlayerCanGain(true);
        });
    }

    [Fact]
    public async Task Player_and_monster_hits_preserve_charge_order_rng_and_recipient()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        int Actor(uint stat) => stat switch
        {
            Stat.AddFrenzyChargeOnSkillHitPct => 20,
            Stat.AddPowerChargeOnHitPct => 36,
            Stat.AddPowerChargeOnSkillHitPct => 50,
            Stat.AddPowerChargeOnCriticalStrikePct => 27,
            _ => 0
        };
        int Skill(uint stat) => stat switch
        {
            Stat.AddFrenzyChargeOnSkillHitPct => 10,
            Stat.AddPowerChargeOnHitPct => 8,
            Stat.AddPowerChargeOnSkillHitPct => 17,
            _ => 0
        };
        for (int seed = 0; seed < 64; seed++)
        {
            var expected = new Random(seed);
            var actual = new Random(seed);
            foreach (bool critical in new[] { false, true })
            {
                var hit = new ChargeEvent.Hit(critical, false, true, MonsterRarity.Normal, Skill: Skill);
                Assert.Equal(ChargeGains.Roll(hit, Actor, default, expected, ChargeGains.Rules),
                    ChargeGains.Roll(hit, Actor, default, actual));
                Assert.Equal(expected.Next(), actual.Next());
            }

            // The generic Power chance belongs to players, even when present on the monster.
            var attacker = HitChargeMonster(31, 67);
            bool frenzy = RandomRolls.Chance(31, 100, expected);
            bool power = RandomRolls.Chance(67, 100, expected);
            Game.MonsterChargesOnHitDealtLocked(fixture.Player, 0, attacker, false, false, 1, actual);
            Assert.Equal((0, frenzy ? 1 : 0, power ? 1 : 0), Game.MonsterChargesOf(attacker));
            Assert.Equal(expected.Next(), actual.Next());

            Game.MonsterChargesOnHitDealtLocked(fixture.Player, 0, attacker, false, true, 2, actual);
            Assert.Equal((0, frenzy ? 1 : 0, power ? 1 : 0), Game.MonsterChargesOf(attacker));
            Assert.Equal(expected.Next(), actual.Next());
        }

        var owner = fixture.Player;
        ushort skill = Assert.Single(fixture.Equip(("Summon Flame Golem", 20)));
        uint id = await Game.SummonGolem(owner, skill, Game.GolemData.Find("SummonFlameGolem")!, (105, 100));
        var golem = owner.Golems.All[id];
        var golemAttacker = HitChargeMonster(100, 100);
        var spell = new MonsterAttack(0x4000, 1, 100, DamagePacket.Of(DamageType.Chaos, 10, 10)) { Spell = true };
        Assert.True(Game.GolemTakeHitLocked(owner, golem, 401, golemAttacker, spell, 2));
        Assert.Equal((0, 1, 1), Game.MonsterChargesOf(golemAttacker));
        Assert.Equal(0, golem.CombatCharges?.Held(ChargeType.Frenzy) ?? 0);
        Assert.Equal(0, golem.CombatCharges?.Held(ChargeType.Power) ?? 0);
        Assert.False(Game.ChargesOf(owner).Pending);
    }

    [Fact]
    public void Disabling_one_hit_rule_on_reload_changes_both_actors()
    {
        InTestWorld(edit =>
        {
            using var fixture = new TriggeredSummonTests.Fixture();
            void Check(bool frenzy)
            {
                // Half the chance comes from the character and half from the skill it actually used.
                int Half(uint stat) => stat is Stat.AddFrenzyChargeOnSkillHitPct or Stat.AddPowerChargeOnHitPct
                    or Stat.AddPowerChargeOnSkillHitPct ? 50 : 0;
                var hit = new ChargeEvent.Hit(false, false, true, MonsterRarity.Normal, Skill: Half);
                var gains = ChargeGains.Roll(hit, Half, default, new Random(2));
                string[] expected = frenzy ? ["add_frenzy_charge_on_skill_hit_%", "add_power_charge_on_hit_%",
                    "add_power_charge_on_skill_hit_%"] : ["add_power_charge_on_hit_%", "add_power_charge_on_skill_hit_%"];
                Assert.Equal(expected, gains.Select(g => g.Source));
                var attacker = HitChargeMonster(100, 100);
                Game.MonsterChargesOnHitDealtLocked(fixture.Player, 0, attacker, false, false, 1, new Random(2));
                Assert.Equal((0, frenzy ? 1 : 0, 1), Game.MonsterChargesOf(attacker));
            }
            Check(true);
            edit("UPDATE fx_rules SET enabled = 0 WHERE id = 'frenzy_on_skill_hit';");
            Check(!ItemEffectRules.Enabled);

            edit("UPDATE fx_rule_when SET roll_order = 40 WHERE rule = 'power_on_hit';");
            var hit = new ChargeEvent.Hit(false, false, true, MonsterRarity.Normal);
            var power = ChargeGains.Roll(hit, stat => stat is Stat.AddPowerChargeOnHitPct
                or Stat.AddPowerChargeOnSkillHitPct ? 100 : 0, default, new Random(2));
            string[] order = ItemEffectRules.Enabled ? ["add_power_charge_on_skill_hit_%", "add_power_charge_on_hit_%"]
                : ["add_power_charge_on_hit_%", "add_power_charge_on_skill_hit_%"];
            Assert.Equal(order, power.Select(g => g.Source));
        });
    }

    static SpawnedMonster HitChargeMonster(int frenzy, int power)
    {
        var monster = new SpawnedMonster("attacker", new(100, 100, 0), 0, 0, 1000) { Hostile = true };
        monster.SetTemporaryStats("on hit", new Dictionary<uint, int>
        {
            [Stat.AddFrenzyChargeOnSkillHitPct] = frenzy,
            [Stat.AddPowerChargeOnHitPct] = 100,
            [Stat.AddPowerChargeOnSkillHitPct] = power
        });
        return monster;
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
