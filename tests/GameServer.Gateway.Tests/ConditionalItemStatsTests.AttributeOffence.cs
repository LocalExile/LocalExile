using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Qualified_attribute_coefficients_reach_final_offence(ClientState conn, Func<string, byte, InventoryItem> wear)
    {
        var inventory = Game.ItemsOf(conn);
        byte[] slots = [PlayerInventories.Weapon1, PlayerInventories.Offhand1, PlayerInventories.BodyArmour,
            PlayerInventories.Helmet, PlayerInventories.Boots, PlayerInventories.Gloves, PlayerInventories.Belt, 5];
        var saved = inventory.EquipmentSnapshot().Where(e => slots.Contains(e.Row)).ToArray();
        void Clear(byte row) { foreach (var item in inventory.Items(row)) inventory.Remove(row, item.Id); }
        foreach (byte row in new byte[] { PlayerInventories.Weapon1, PlayerInventories.Offhand1,
            PlayerInventories.BodyArmour, PlayerInventories.Helmet, PlayerInventories.Boots,
            PlayerInventories.Gloves, PlayerInventories.Belt }) Clear(row);
        var amulet = wear("Astramentis", 5);
        var cases = new (string Item, byte Slot, string Input, string Output, string Attribute, int Group)[]
        {
            ("Pillar of the Caged God", PlayerInventories.Weapon1, "area_of_effect_+%_per_20_int", SkillArea.BaseIncreased, "intelligence", 20),
            ("Pillar of the Caged God", PlayerInventories.Weapon1, "attack_speed_+%_per_10_dex", "attack_speed_+%", "dexterity", 10),
            ("Hand of Wisdom and Action", PlayerInventories.Weapon1, "attack_speed_+%_per_25_dex", "attack_speed_+%", "dexterity", 25),
            ("Hand of Thought and Motion", PlayerInventories.Weapon1, "critical_strike_chance_+%_per_25_intelligence", "critical_strike_chance_+%", "intelligence", 25),
            ("Briskwrap", PlayerInventories.BodyArmour, "damage_+%_per_15_dex", "damage_+%", "dexterity", 15),
            ("Wildwrap", PlayerInventories.BodyArmour, "damage_+%_per_15_dex", "damage_+%", "dexterity", 15),
            ("Fractal Thoughts", PlayerInventories.Helmet, "elemental_damage_+%_per_10_dexterity", "elemental_damage_+%", "dexterity", 10),
            ("Abberath's Hooves", PlayerInventories.Boots, "fire_damage_+%_per_20_strength", "fire_damage_+%", "strength", 20),
            ("The Whispering Ice", PlayerInventories.Weapon1, "spell_damage_+%_per_10_int", "spell_damage_+%", "intelligence", 10),
            ("Shaper's Touch", PlayerInventories.Gloves, "X_accuracy_per_2_intelligence", "accuracy_rating", "intelligence", 2),
            ("Survivor's Guilt", PlayerInventories.Belt, "armour_+%_per_50_str", "physical_damage_reduction_rating_+%", "strength", 50),
            ("The Iron Fortress", PlayerInventories.BodyArmour, "block_chance_%_per_50_strength", "additional_block_%", "strength", 50),
            ("Rive", PlayerInventories.Weapon1, "bleed_duration_per_12_intelligence_+%", "bleed_duration_+%", "intelligence", 12),
            ("Rive", PlayerInventories.Weapon1, "physical_damage_over_time_per_10_dexterity_+%", "physical_damage_over_time_+%", "dexterity", 10),
        };
        uint Key(string name) => Program.Stats.Key(name)!.Value;
        foreach (var test in cases)
        {
            var full = wear(test.Item, test.Slot);
            var stats = Game.CharacterPlayerStats(conn);
            var sources = Game.CharacterStatSources(conn).Stats;
            int coefficient = sources.GetValueOrDefault(Key(test.Input));
            Assert.NotEqual(0, coefficient);
            int attribute = stats.GetValueOrDefault(Key(test.Attribute));
            int expected = attribute / test.Group * coefficient;
            Assert.NotEqual(0, expected);
            // Remove only the actual coefficient's row; preserve the source's other attributes and qualification.
            var control = full.Item with { Mods = full.Item.Mods.Where(m =>
                Program.Items.Mods.ByHash(m.Hash)?.Stats.All(s => s.Id != test.Input) != false).ToArray() };
            inventory.Remove(test.Slot, full.Id);
            var without = inventory.Add(test.Slot, full.X, full.Y, control);
            conn.Pools = Game.PoolsFor(conn, 100);
            var baseline = Game.CharacterStatSources(conn).Stats;
            Assert.False(Game.IsInactive(conn, without.Id));
            Assert.Equal(attribute, Game.CharacterPlayerStats(conn).GetValueOrDefault(Key(test.Attribute)));
            Assert.Equal(expected, sources.GetValueOrDefault(Key(test.Output)) - baseline.GetValueOrDefault(Key(test.Output)));
            var controlPools = conn.Pools;
            inventory.Remove(test.Slot, without.Id);
            var restored = inventory.Add(test.Slot, full.X, full.Y, full.Item);
            conn.Pools = Game.PoolsFor(conn, 100);
            var restoredSources = Game.CharacterStatSources(conn).Stats;
            Assert.Equal(sources.GetValueOrDefault(Key(test.Output)), restoredSources.GetValueOrDefault(Key(test.Output)));
            for (int repeat = 0; repeat < 3; repeat++)
            {
                conn.Pools = Game.PoolsFor(conn, 100);
                Assert.Equal(sources.GetValueOrDefault(Key(test.Output)),
                    Game.CharacterStatSources(conn).Stats.GetValueOrDefault(Key(test.Output)));
            }
            if (test.Item == "Pillar of the Caged God")
            {
                inventory.Remove(5, amulet.Id); conn.Pools = Game.PoolsFor(conn, 100);
                var changed = Game.CharacterPlayerStats(conn);
                Assert.False(Game.IsInactive(conn, restored.Id));
                int lower = changed.GetValueOrDefault(Key(test.Attribute));
                Assert.True(lower < attribute);
                Assert.Equal((attribute / test.Group - lower / test.Group) * coefficient,
                    sources.GetValueOrDefault(Key(test.Output)) - Game.CharacterStatSources(conn).Stats.GetValueOrDefault(Key(test.Output)));
                amulet = inventory.Add(5, amulet.X, amulet.Y, amulet.Item); conn.Pools = Game.PoolsFor(conn, 100);
            }
            if (test.Output == "attack_speed_+%") Assert.Equal(expected, conn.Pools.AttackSpeedPct - controlPools.AttackSpeedPct);
            if (test.Output == "accuracy_rating") Assert.Equal(expected, conn.Pools.AccuracyBase - controlPools.AccuracyBase);
            if (test.Output == "physical_damage_reduction_rating_+%")
                Assert.Equal(expected, conn.Pools.ArmourPct - controlPools.ArmourPct);
            if (test.Output == "additional_block_%")
                Assert.Equal(expected, conn.Pools.BlockChanceUncapped - controlPools.BlockChanceUncapped);
            if (test.Output == "critical_strike_chance_+%")
            {
                Assert.Equal(expected, conn.Pools.CriticalStrikeChancePct - controlPools.CriticalStrikeChancePct);
                Assert.Equal(expected, conn.Pools.SpellCriticalStrikeChancePct - controlPools.SpellCriticalStrikeChancePct);
            }
            int Added(string name) => name == test.Output ? expected : 0;
            if (test.Output is "bleed_duration_+%" or "physical_damage_over_time_+%")
            {
                var physical = DamagePacket.Of(DamageType.Physical, 100, 100);
                var bleedRows = AilmentPanelStats.Compute(new("main_hand", Spell: false), physical, new(), Added);
                var settings = Game.BleedSettings(bleedRows, "main_hand", Added, new Dictionary<string, int>(), melee: true, bow: false);
                Assert.Equal(5 * (1 + (test.Output == "bleed_duration_+%" ? expected : 0) / 100d), settings.Seconds, 8);
                Assert.Equal(70 * (1 + (test.Output == "physical_damage_over_time_+%" ? expected : 0) / 100d), settings.Min, 6);
                Assert.Equal(100, SpellDamage.Packet(physical, Added).MinTotal, 8);
                Assert.Equal(0, DamageStats.AttackIncrease(Added, DamageType.Physical, melee: true));
                var poisonRows = AilmentPanelStats.Compute(new("spell", Spell: true), physical, new(), Added);
                Assert.Equal(1800, poisonRows.Single(r => r.Stat == "minimum_poison_damage_to_deal_per_minute_from_non_critical_spell_damage").Value);
            }
            if (test.Output == SkillArea.BaseIncreased)
            {
                Assert.Equal(1 + expected / 100d, SkillArea.Modifier(Added, spell: false), 8);
                Assert.Equal(1 + expected / 100d, SkillArea.Modifier(Added, spell: true), 8);
            }
            if (test.Output.EndsWith("damage_+%"))
            {
                foreach (var type in new[] { DamageType.Physical, DamageType.Fire, DamageType.Cold, DamageType.Chaos })
                {
                    bool applies = test.Output == "damage_+%" || test.Output == "spell_damage_+%"
                        || test.Output == "elemental_damage_+%" && type is DamageType.Fire or DamageType.Cold
                        || test.Output == "fire_damage_+%" && type == DamageType.Fire;
                    var hit = SpellDamage.Packet(DamagePacket.Of(type, 100, 100), Added);
                    Assert.Equal(100 + (applies ? expected : 0), hit.MinTotal, 8);
                    Assert.Equal(test.Output == "spell_damage_+%" ? 0 : applies ? expected : 0,
                        DamageStats.AttackIncrease(Added, type, melee: true));
                }
            }
            conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
            Assert.True(Game.IsInactive(conn, restored.Id));
            Assert.Equal(0, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(Key(test.Input)));
            conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
            Assert.False(Game.IsInactive(conn, restored.Id));
            inventory.Remove(test.Slot, restored.Id);
            Assert.Equal(0, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(Key(test.Input)));
        }
        inventory.Remove(5, amulet.Id); conn.Pools = Game.PoolsFor(conn, 100);
        foreach (var test in cases)
            foreach (int attribute in new[] { -1, 0, test.Group - 1, test.Group, test.Group * 2 - 1, test.Group * 2 })
            {
                var isolated = new Dictionary<uint, int> { [Key(test.Input)] = 5, [Key(test.Output)] = 7 };
                AttributeOffenceSources.Apply(isolated, Program.Stats,
                    test.Attribute == "strength" ? attribute : 0, test.Attribute == "dexterity" ? attribute : 0,
                    test.Attribute == "intelligence" ? attribute : 0);
                AttributeDefenceSources.Apply(isolated, Program.Stats, test.Attribute == "strength" ? attribute : 0);
                Assert.Equal(7 + Math.Max(0, attribute) / test.Group * 5, isolated[Key(test.Output)]);
            }
        // Shared output sums retain cancellation before the outgoing integer clamp.
        var wide = new Dictionary<uint, int>
        {
            [Key("attack_speed_+%_per_10_dex")] = int.MaxValue,
            [Key("attack_speed_+%_per_25_dex")] = int.MinValue,
            [Key("attack_speed_+%")] = -10,
        };
        AttributeOffenceSources.Apply(wide, Program.Stats, 0, 25, 0);
        Assert.Equal(int.MaxValue - 11, wide[Key("attack_speed_+%")]);
        wide = new() { [Key("spell_damage_+%_per_10_int")] = int.MaxValue };
        AttributeOffenceSources.Apply(wide, Program.Stats, 0, 0, int.MaxValue);
        Assert.Equal(int.MaxValue, wide[Key("spell_damage_+%")]);
        foreach (byte row in slots) Clear(row);
        foreach (var (row, item) in saved) inventory.Add(row, item.X, item.Y, item.Item);
        conn.Pools = Game.PoolsFor(conn, 100);
    }
}
