using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Qualified_attribute_and_level_damage_sources_reach_attack_and_spell_consumers(ClientState conn,
        Func<string, byte, InventoryItem> wear)
    {
        var inventory = Game.ItemsOf(conn);
        void Clear(byte row)
        {
            foreach (var item in inventory.Items(row)) inventory.Remove(row, item.Id);
        }
        Clear(PlayerInventories.Offhand1); Clear(PlayerInventories.Weapon1);
        var attributes = wear("Astramentis", 5);
        var cases = new (string Item, DamageType Type, string Minimum, string Maximum, string Attribute, int Group, bool Spell)[]
        {
            ("Brutus' Lead Sprinkler", DamageType.Fire, "attack_minimum_added_fire_damage_per_10_strength",
                "attack_maximum_added_fire_damage_per_10_strength", "strength", 10, false),
            ("Hand of Wisdom and Action", DamageType.Lightning, "attack_minimum_added_lightning_damage_per_10_int",
                "attack_maximum_added_lightning_damage_per_10_int", "intelligence", 10, false),
            ("Oni-Goroshi", DamageType.Physical, "attack_minimum_added_physical_damage_per_level",
                "attack_maximum_added_physical_damage_per_level", "level", 1, false),
            ("Edge of Madness", DamageType.Physical, "attack_minimum_added_physical_damage_per_level",
                "attack_maximum_added_physical_damage_per_level", "level", 1, false),
            ("Jiquani's Potential", DamageType.Lightning, "spell_minimum_added_lightning_damage_per_10_intelligence",
                "spell_maximum_added_lightning_damage_per_10_intelligence", "intelligence", 10, true),
        };
        foreach (var test in cases)
        {
            Clear(PlayerInventories.Weapon1);
            var item = wear(test.Item, PlayerInventories.Weapon1);
            var stats = Game.CharacterPlayerStats(conn);
            int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
            Assert.True(Get(test.Minimum) > 0, test.Item);
            Assert.True(Get(test.Maximum) >= Get(test.Minimum), test.Item);
            int NoCoefficient(string name) => name == test.Minimum || name == test.Maximum ? 0 : Get(name);
            void Check()
            {
                foreach (bool spell in new[] { false, true })
                    foreach (double effectiveness in new[] { .5, 1, 1.7 })
                    {
                        var actual = DamageStats.RawAdded(DamagePacket.Empty, Get, spell, effectiveness)[test.Type];
                        var baseline = DamageStats.RawAdded(DamagePacket.Empty, NoCoefficient, spell, effectiveness)[test.Type];
                        long groups = spell == test.Spell ? Math.Max(0, Get(test.Attribute)) / test.Group : 0;
                        Assert.Equal(groups * Get(test.Minimum) * effectiveness, actual.Min - baseline.Min, 8);
                        Assert.Equal(groups * Get(test.Maximum) * effectiveness, actual.Max - baseline.Max, 8);
                    }
                var hit = test.Spell ? SpellDamage.Packet(DamagePacket.Empty, Get, 1.7)
                    : SupportedAttackDamage.Hit(SkillCatalog.Unarmed.Witch, Get, new HashSet<int> { 19 });
                var prior = test.Spell ? SpellDamage.Packet(DamagePacket.Empty, NoCoefficient, 1.7)
                    : SupportedAttackDamage.Hit(SkillCatalog.Unarmed.Witch, NoCoefficient, new HashSet<int> { 19 });
                Assert.True(hit[test.Type].Min > prior[test.Type].Min, test.Item + " final hit minimum");
                Assert.True(hit[test.Type].Max > prior[test.Type].Max, test.Item + " final hit maximum");
            }
            Check();
            if (test.Attribute != "level")
            {
                int before = Get(test.Attribute);
                var ring = inventory.Items(6).Single();
                inventory.Remove(6, ring.Id); conn.Pools = Game.PoolsFor(conn, 100);
                stats = Game.CharacterPlayerStats(conn);
                Assert.False(Game.IsInactive(conn, item.Id)); Assert.True(Get(test.Attribute) < before); Check();
                inventory.Add(6, ring.X, ring.Y, ring.Item); conn.Pools = Game.PoolsFor(conn, 100);
                stats = Game.CharacterPlayerStats(conn); Assert.Equal(before, Get(test.Attribute)); Check();
            }
            foreach (int attribute in new[] { -1, 0, test.Group - 1, test.Group, test.Group * 2 - 1, test.Group * 2 })
            {
                int Isolated(string name) => name == test.Minimum || name == test.Maximum ? Get(name)
                    : name == test.Attribute ? attribute : 0;
                var raw = DamageStats.RawAdded(DamagePacket.Empty, Isolated, test.Spell)[test.Type];
                Assert.Equal(Math.Max(0, attribute) / test.Group * (double)Get(test.Minimum), raw.Min);
                Assert.Equal(Math.Max(0, attribute) / test.Group * (double)Get(test.Maximum), raw.Max);
            }
            conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
            stats = Game.CharacterPlayerStats(conn);
            Assert.Equal(1, Get("level"));
            Assert.True(Game.IsInactive(conn, item.Id), test.Item);
            Assert.Equal(0, Get(test.Minimum)); Assert.Equal(0, Get(test.Maximum));
            if (test.Item == "Oni-Goroshi")
            {
                // This client's base row has DropLevel2: test its actual admitted low level, not a fabricated waiver.
                conn.Experience = CharacterStats.ExperienceFor(2); conn.Pools = Game.PoolsFor(conn, 2);
                stats = Game.CharacterPlayerStats(conn);
                Assert.False(Game.IsInactive(conn, item.Id));
                Assert.Equal(4, DamageStats.AttributeAdded(Get, DamageType.Physical).Min);
                Assert.Equal(6, DamageStats.AttributeAdded(Get, DamageType.Physical).Max);
                Check();
            }
            conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
            stats = Game.CharacterPlayerStats(conn); Assert.False(Game.IsInactive(conn, item.Id)); Check();
            inventory.Remove(PlayerInventories.Weapon1, item.Id);
            stats = Game.CharacterPlayerStats(conn);
            Assert.Equal(0, Get(test.Minimum)); Assert.Equal(0, Get(test.Maximum));
        }
        inventory.Remove(5, attributes.Id); conn.Pools = Game.PoolsFor(conn, 100);
        // The legacy Poised Prism and Replica Alberon spellings retain their original group sizes.
        var legacy = new Dictionary<string, int>
        {
            ["minimum_added_fire_damage_to_attacks_per_10_strength"] = 1,
            ["maximum_added_fire_damage_to_attacks_per_10_strength"] = 2,
            ["minimum_added_chaos_damage_to_attacks_per_50_strength"] = 1,
            ["maximum_added_chaos_damage_to_attacks_per_50_strength"] = 80,
            ["strength"] = 159,
        };
        Assert.Equal(new DamageRange(15, 30), DamageStats.AttributeAdded(n => legacy.GetValueOrDefault(n), DamageType.Fire));
        Assert.Equal(new DamageRange(1, 80), DamageStats.AttributeAdded(n => legacy.GetValueOrDefault(n), DamageType.Chaos));
    }
}

