using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Qualified_level_sources_reach_final_consumers(ClientState conn, Func<string, byte, InventoryItem> wear)
    {
        var inventory = Game.ItemsOf(conn);
        byte[] slots = [PlayerInventories.Weapon1, PlayerInventories.Offhand1, 5, 6];
        var saved = inventory.EquipmentSnapshot().Where(e => slots.Contains(e.Row)).ToArray();
        foreach (var item in inventory.Items(PlayerInventories.Offhand1)) inventory.Remove(PlayerInventories.Offhand1, item.Id);
        wear("Astramentis", 5);
        uint Key(string name) => Program.Stats.Key(name)!.Value;
        var cases = new (string Item, byte Slot, string Input, string Target)[]
        {
            ("Edge of Madness", PlayerInventories.Weapon1, "chaos_damage_+%_per_level", "chaos_damage_+%"),
            ("The Harvest", PlayerInventories.Weapon1, "critical_strike_chance_+%_per_level", "critical_strike_chance_+%"),
            ("Replica Harvest", PlayerInventories.Weapon1, "critical_strike_chance_+%_per_level", "critical_strike_chance_+%"),
            ("The Dark Seer", PlayerInventories.Weapon1, "energy_shield_per_level", "base_maximum_energy_shield"),
            ("Kikazaru", 6, "life_regeneration_rate_per_minute_per_level", "base_life_regeneration_rate_per_minute"),
        };
        foreach (var test in cases)
        {
            var placed = wear(test.Item, test.Slot);
            foreach (int level in new[] { 100, 80, 100 })
            {
                conn.Experience = CharacterStats.ExperienceFor(level); conn.Pools = Game.PoolsFor(conn, level);
                var full = Game.CharacterStatSources(conn).Stats;
                Assert.False(Game.IsInactive(conn, placed.Id));
                int coefficient = full.GetValueOrDefault(Key(test.Input)); Assert.True(coefficient > 0, test.Item);
                int expected = coefficient * level;
                var pools = conn.Pools;
                var control = placed.Item with { Mods = placed.Item.Mods.Where(m =>
                    Program.Items.Mods.ByHash(m.Hash)?.Stats.All(s => s.Id != test.Input) != false).ToArray() };
                inventory.Remove(test.Slot, placed.Id);
                var without = inventory.Add(test.Slot, placed.X, placed.Y, control);
                conn.Pools = Game.PoolsFor(conn, level);
                var baseline = Game.CharacterStatSources(conn).Stats;
                Assert.Equal(expected, full.GetValueOrDefault(Key(test.Target)) - baseline.GetValueOrDefault(Key(test.Target)));
                if (test.Target == "critical_strike_chance_+%")
                {
                    Assert.Equal(expected, pools.CriticalStrikeChancePct - conn.Pools.CriticalStrikeChancePct);
                    Assert.Equal(expected, pools.SpellCriticalStrikeChancePct - conn.Pools.SpellCriticalStrikeChancePct);
                }
                if (test.Target == "base_maximum_energy_shield") Assert.True(pools.EnergyShield > conn.Pools.EnergyShield);
                if (test.Target == "base_life_regeneration_rate_per_minute")
                {
                    int Full(string name) => Program.Stats.Key(name) is uint key ? full.GetValueOrDefault(key) : 0;
                    int Off(string name) => Program.Stats.Key(name) is uint key ? baseline.GetValueOrDefault(key) : 0;
                    Assert.Equal(expected, PanelStats.LifePerMinute(PanelStats.WhileMoving(Full, false), pools.Life)
                        - PanelStats.LifePerMinute(PanelStats.WhileMoving(Off, false), conn.Pools.Life), 8);
                }
                if (test.Target == "chaos_damage_+%")
                    foreach (bool spell in new[] { false, true })
                    {
                        int Added(string name) => name == test.Target ? expected : 0;
                        var hit = PlayerHitDamage.Resolve(DamagePacket.Of(DamageType.Chaos, 100, 100), Added, new(), spell);
                        Assert.Equal(100 + expected, hit.MinTotal, 8);
                        Assert.Equal(100, PlayerHitDamage.Resolve(DamagePacket.Of(DamageType.Fire, 100, 100), Added, new(), spell).MinTotal, 8);
                    }
                inventory.Remove(test.Slot, without.Id);
                placed = inventory.Add(test.Slot, placed.X, placed.Y, placed.Item);
                conn.Pools = Game.PoolsFor(conn, level);
                for (int repeat = 0; repeat < 3; repeat++)
                    Assert.Equal(full.GetValueOrDefault(Key(test.Target)), Game.CharacterStatSources(conn).Stats.GetValueOrDefault(Key(test.Target)));
            }
            conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
            Assert.True(Game.IsInactive(conn, placed.Id));
            Assert.Equal(0, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(Key(test.Input)));
            conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
            Assert.False(Game.IsInactive(conn, placed.Id));
            inventory.Remove(test.Slot, placed.Id);
            Assert.Equal(0, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(Key(test.Input)));
        }
        var wide = new Dictionary<uint, int> { [Key("chaos_damage_+%_per_level")] = int.MaxValue };
        LevelItemSources.Apply(wide, Program.Stats, 100); Assert.Equal(int.MaxValue, wide[Key("chaos_damage_+%")]);
        wide = new() { [Key("critical_strike_chance_+%_per_level")] = int.MinValue };
        LevelItemSources.Apply(wide, Program.Stats, 100); Assert.Equal(int.MinValue, wide[Key("critical_strike_chance_+%")]);
        foreach (byte slot in slots) foreach (var item in inventory.Items(slot)) inventory.Remove(slot, item.Id);
        foreach (var (slot, item) in saved) inventory.Add(slot, item.X, item.Y, item.Item);
        conn.Pools = Game.PoolsFor(conn, 100); conn.PlayerLife = conn.PlayerMaxLife;
    }
}
