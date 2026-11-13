using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Qualified_low_life_resistances_reach_pools(ClientState conn, Func<string, byte, InventoryItem> wear)
    {
        var inventory = Game.ItemsOf(conn);
        byte[] slots = [PlayerInventories.Offhand1, 5, 6];
        var saved = inventory.EquipmentSnapshot().Where(e => slots.Contains(e.Row)).ToArray();
        wear("Astramentis", 5);
        foreach (var (name, slot, input, fire) in new[]
        {
            ("Lori's Lantern", (byte)6, "chaos_damage_resistance_%_when_on_low_life", false),
            ("Rise of the Phoenix", PlayerInventories.Offhand1, "fire_damage_resistance_%_when_on_low_life", true),
        })
        {
            var source = wear(name, slot);
            int coefficient = ItemModStats.Sum([source.Item], Program.Items.Mods).GetValueOrDefault(input);
            Assert.True(coefficient > 0, name);
            conn.PlayerLife = conn.PlayerMaxLife; conn.Pools = Game.PoolsFor(conn, 100);
            int Before() => fire ? conn.Pools.FireResistance : conn.Pools.ChaosResistance;
            int full = Before();
            foreach (bool low in new[] { true, false, true })
            {
                conn.PlayerLife = low ? conn.PlayerMaxLife / 2 : conn.PlayerMaxLife;
                conn.Pools = Game.PoolsFor(conn, 100);
                Assert.Equal(full + (low ? coefficient : 0), Before());
                var published = Game.CharacterPlayerStats(conn);
                string output = fire ? "fire_damage_resistance_%" : "chaos_damage_resistance_%";
                int maximum = fire ? conn.Pools.FireMaxResistance : conn.Pools.ChaosMaxResistance;
                Assert.Equal(DamageModel.Capped(Before(), maximum), published.GetValueOrDefault(Program.Stats.Key(output)!.Value));
            }
            conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
            Assert.True(Game.IsInactive(conn, source.Id));
            Assert.Equal(0, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(Program.Stats.Key(input)!.Value));
            conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
            Assert.False(Game.IsInactive(conn, source.Id));
            inventory.Remove(slot, source.Id);
            Assert.Equal(0, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(Program.Stats.Key(input)!.Value));
        }
        foreach (byte row in slots) foreach (var item in inventory.Items(row)) inventory.Remove(row, item.Id);
        foreach (var (row, item) in saved) inventory.Add(row, item.X, item.Y, item.Item);
        conn.Pools = Game.PoolsFor(conn, 100); conn.PlayerLife = conn.PlayerMaxLife;
    }
}
