using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Qualified_maximum_pool_coefficients_reach_offence(ClientState conn, Func<string, byte, InventoryItem> wear)
    {
        var inventory = Game.ItemsOf(conn);
        var saved = inventory.EquipmentSnapshot();
        try
        {
            foreach (var (row, item) in saved.Where(e => e.Row is not (6 or 7))) inventory.Remove(row, item.Id);
            wear("Astramentis", 5);
            uint Key(string name) => Program.Stats.Key(name)!.Value;
            var cases = new (string Item, string[] Inputs, string[] Outputs, bool Lowest)[]
            {
                ("Rathpith Globe", ["spell_damage_+%_per_100_max_life"], ["spell_damage_+%"], false),
                ("Rathpith Globe", [MaximumPoolItemSources.SpellCriticalPerLife], ["spell_critical_strike_chance_+%"], false),
                ("The Scales of Justice", ["minimum_added_fire_damage_per_100_lowest_of_max_life_mana",
                    "maximum_added_fire_damage_per_100_lowest_of_max_life_mana"],
                    ["global_minimum_added_fire_damage", "global_maximum_added_fire_damage"], true),
            };
            foreach (var test in cases)
            {
                var source = wear(test.Item, PlayerInventories.Offhand1);
                var stats = Game.CharacterPlayerStats(conn);
                var fullPools = conn.Pools;
                int Get(string name) => stats.GetValueOrDefault(Key(name));
                int[] coefficients = test.Inputs.Select(Get).ToArray();
                Assert.All(coefficients, c => Assert.True(c > 0));
                long[] expected = coefficients.Select(c => MaximumPoolItemSources.Amount(c,
                    test.Lowest ? Math.Min(fullPools.Life, fullPools.Mana) : fullPools.Life)).ToArray();
                Assert.All(expected, n => Assert.True(n > 0));
                var original = stats;
                var control = source.Item with { Mods = source.Item.Mods.Where(m => Program.Items.Mods.ByHash(m.Hash)?.Stats
                    .All(s => !test.Inputs.Contains(s.Id)) != false).ToArray() };
                inventory.Remove(PlayerInventories.Offhand1, source.Id);
                var baselineHost = inventory.Add(PlayerInventories.Offhand1, source.X, source.Y, control);
                conn.Pools = Game.PoolsFor(conn, 100);
                var baseline = Game.CharacterPlayerStats(conn);
                var baselinePools = conn.Pools;
                Assert.Equal(fullPools.Life, baselinePools.Life); Assert.Equal(fullPools.Mana, baselinePools.Mana);
                for (int i = 0; i < test.Outputs.Length; i++)
                    Assert.Equal(expected[i], original.GetValueOrDefault(Key(test.Outputs[i])) - baseline.GetValueOrDefault(Key(test.Outputs[i])));
                int Original(string n) => original.GetValueOrDefault(Key(n));
                int Baseline(string n) => baseline.GetValueOrDefault(Key(n));
                if (test.Outputs[0] == "spell_damage_+%")
                {
                    Assert.Equal(expected[0], SpellDamage.Packet(DamagePacket.Of(DamageType.Physical, 100, 100), Original).MinTotal
                        - SpellDamage.Packet(DamagePacket.Of(DamageType.Physical, 100, 100), Baseline).MinTotal, 8);
                    Assert.Equal(DamageStats.AttackIncrease(Baseline, DamageType.Physical, melee: true),
                        DamageStats.AttackIncrease(Original, DamageType.Physical, melee: true));
                }
                if (test.Outputs[0] == "spell_critical_strike_chance_+%")
                {
                    Assert.Equal(expected[0], fullPools.SpellCriticalStrikeChancePct - baselinePools.SpellCriticalStrikeChancePct);
                    Assert.Equal(SpellCriticalStrike.Chance(DamageModel.BaseSpellCriticalStrikeChancePerMyriad, Original),
                        fullPools.SpellCriticalStrikeChance);
                    Assert.Equal(6 * expected[0], SpellCriticalStrike.Chance(600, Original) - SpellCriticalStrike.Chance(600, Baseline));
                    Assert.Equal(fullPools.AttackCriticalStrikeChance, baselinePools.AttackCriticalStrikeChance);
                }
                if (test.Lowest)
                    foreach (bool spell in new[] { false, true })
                        foreach (double effectiveness in new[] { .5, 1, 1.7 })
                        {
                            var actual = DamageStats.RawAdded(DamagePacket.Empty, Original, spell, effectiveness)[DamageType.Fire];
                            var prior = DamageStats.RawAdded(DamagePacket.Empty, Baseline, spell, effectiveness)[DamageType.Fire];
                            Assert.Equal(expected[0] * effectiveness, actual.Min - prior.Min, 8);
                            Assert.Equal(expected[1] * effectiveness, actual.Max - prior.Max, 8);
                            Assert.True(SpellDamage.Packet(DamagePacket.Empty, Original, effectiveness).MinTotal
                                > SpellDamage.Packet(DamagePacket.Empty, Baseline, effectiveness).MinTotal);
                        }
                inventory.Remove(PlayerInventories.Offhand1, baselineHost.Id);
                source = inventory.Add(PlayerInventories.Offhand1, source.X, source.Y, source.Item);
                conn.Pools = Game.PoolsFor(conn, 100); stats = Game.CharacterPlayerStats(conn);
                foreach (string output in test.Outputs) Assert.Equal(original[Key(output)], stats[Key(output)]);
                conn.PlayerLife = 1; conn.PlayerMana = 1;
                stats = Game.CharacterPlayerStats(conn);
                foreach (string output in test.Outputs) Assert.Equal(original[Key(output)], stats[Key(output)]);
                conn.PlayerLife = conn.PlayerMaxLife; conn.PlayerMana = conn.Pools.Mana;
                var counterpart = wear(test.Lowest ? "Cloak of Defiance" : "Kaom's Heart", PlayerInventories.BodyArmour);
                conn.Pools = Game.PoolsFor(conn, 100); stats = Game.CharacterPlayerStats(conn);
                Assert.False(Game.IsInactive(conn, source.Id));
                Assert.True(test.Lowest ? conn.Pools.Mana > fullPools.Mana : conn.Pools.Life > fullPools.Life);
                uint maximum = test.Lowest ? Math.Min(conn.Pools.Life, conn.Pools.Mana) : conn.Pools.Life;
                for (int i = 0; i < test.Outputs.Length; i++)
                    Assert.Equal(MaximumPoolItemSources.Amount(coefficients[i], maximum),
                        stats.GetValueOrDefault(Key(test.Outputs[i])) - baseline.GetValueOrDefault(Key(test.Outputs[i])));
                inventory.Remove(PlayerInventories.BodyArmour, counterpart.Id); conn.Pools = Game.PoolsFor(conn, 100);
                for (int repeat = 0; repeat < 3; repeat++)
                {
                    stats = Game.CharacterPlayerStats(conn);
                    foreach (string output in test.Outputs) Assert.Equal(original[Key(output)], stats[Key(output)]);
                }
                conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
                Assert.True(Game.IsInactive(conn, source.Id)); stats = Game.CharacterPlayerStats(conn);
                foreach (string input in test.Inputs) Assert.Equal(0, Get(input));
                conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
                stats = Game.CharacterPlayerStats(conn);
                foreach (string output in test.Outputs) Assert.Equal(original[Key(output)], stats[Key(output)]);
                inventory.Remove(PlayerInventories.Offhand1, source.Id); stats = Game.CharacterPlayerStats(conn);
                foreach (string input in test.Inputs) Assert.Equal(0, Get(input));
            }
            foreach (uint life in new uint[] { 0, 1, 99, 100, 199, 200, 350 })
                foreach (uint mana in new uint[] { 0, 99, 100, 299, 400 })
                {
                    var values = new Dictionary<uint, int>
                    {
                        [Key("spell_damage_+%_per_100_max_life")] = 3,
                        [Key("minimum_added_fire_damage_per_100_lowest_of_max_life_mana")] = 4,
                    };
                    MaximumPoolItemSources.Apply(values, Program.Stats, life, mana);
                    Assert.Equal((int)(life / 100) * 3, values.GetValueOrDefault(Key("spell_damage_+%")));
                    Assert.Equal((int)(Math.Min(life, mana) / 100) * 4, values.GetValueOrDefault(Key("global_minimum_added_fire_damage")));
                }
            uint outputKey = Key("spell_damage_+%");
            var replaced = new Dictionary<uint, int>
            {
                [Key("spell_damage_+%_per_100_max_life")] = 3, [outputKey] = 77,
            };
            MaximumPoolItemSources.Apply(replaced, Program.Stats, 1000, 1000, new HashSet<uint> { outputKey });
            Assert.Equal(77, replaced[outputKey]);
            var wide = new Dictionary<uint, int>
            {
                [Key("spell_damage_+%_per_100_max_life")] = int.MaxValue, [outputKey] = -int.MaxValue,
            };
            MaximumPoolItemSources.Apply(wide, Program.Stats, 200, 200);
            Assert.Equal(int.MaxValue, wide[outputKey]);
            Assert.Equal(2L * int.MaxValue, MaximumPoolItemSources.Amount(int.MaxValue, 200));
        }
        finally
        {
            foreach (var (row, item) in inventory.EquipmentSnapshot()) inventory.Remove(row, item.Id);
            foreach (var (row, item) in saved) inventory.Add(row, item.X, item.Y, item.Item);
            conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
            conn.PlayerLife = conn.PlayerMaxLife; conn.PlayerMana = conn.Pools.Mana;
        }
    }
}
