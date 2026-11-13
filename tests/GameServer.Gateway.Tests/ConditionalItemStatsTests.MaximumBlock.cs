using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Qualified_maximum_block_flags_reach_pools_and_real_blocks(ClientState conn, Func<string, byte, InventoryItem> wear)
    {
        uint Key(string name) => Program.Stats.Key(name)!.Value;
        var inventory = Game.ItemsOf(conn);
        byte[] slots = [PlayerInventories.Weapon1, PlayerInventories.Offhand1, 5];
        var saved = inventory.EquipmentSnapshot().Where(e => slots.Contains(e.Row)).ToArray();
        foreach (var (row, item) in saved) inventory.Remove(row, item.Id);
        wear("Astramentis", 5);
        foreach (var (name, spell) in new[] { ("Mistwall", false), ("Replica Mistwall", true) })
        {
            var shield = wear(name, PlayerInventories.Offhand1);
            foreach (int capIncrease in new[] { 0, 10, -10 })
            {
                uint maximum = Key(spell ? "base_maximum_spell_block_%" : "maximum_block_%");
                conn.PlayerStats[maximum] = capIncrease;
                conn.RecentEvents.Clear(); conn.PlayerLife = 1000;
                conn.Pools = Game.PoolsFor(conn, 100);
                int expected = 75 + capIncrease;
                Assert.Equal(expected, spell ? conn.Pools.SpellBlockChance : conn.Pools.BlockChance);
                Assert.Equal(spell, conn.Pools.SpellBlockAtMaximum);
                Assert.Equal(!spell, conn.Pools.AttackBlockAtMaximum);
                var panels = PanelStats.FromPools(n => n is "additional_block_chance_against_projectiles_%"
                    or "base_spell_projectile_block_%" ? -10 : 0, conn.Pools).ToDictionary(p => p.Stat, p => p.Value);
                Assert.Equal(expected, panels[spell ? "spell_projectile_block_%" : "projectile_attack_block_%"]);
                // Pinning the final attack value does not invent overcapped block for Versatile Combatant.
                if (!spell) Assert.True(conn.Pools.BlockChanceUncapped < conn.Pools.BlockChanceMax);
                conn.IncomingHitRandom = new PhasingRoll(0);
                bool running = conn.Clock.IsRunning;
                try
                {
                    if (!running) conn.Clock.Start();
                    Assert.True(Game.TrySyntheticEnemyHit(conn, DamageType.Physical, 10,
                        block: true, spell: spell, suppress: false, out var outcome));
                    Assert.True(outcome.Blocked);
                }
                finally { if (!running) conn.Clock.Stop(); }
                Game.CharacterPlayerStats(conn);
                Assert.False(spell ? conn.Pools.SpellBlockAtMaximum : conn.Pools.AttackBlockAtMaximum);
                Assert.True((spell ? conn.Pools.SpellBlockChance : conn.Pools.BlockChance) < expected);
                // Four-second history expires on the same game clock, without waiting in the test.
                conn.RecentEvents.Clear();
                conn.RecentEvents.Record(StatConditions.Blocked, conn.Clock.Elapsed.TotalSeconds - 4);
                Game.CharacterPlayerStats(conn);
                Assert.Equal(expected, spell ? conn.Pools.SpellBlockChance : conn.Pools.BlockChance);
                conn.PlayerStats.Remove(maximum);
            }
            conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
            Assert.True(Game.IsInactive(conn, shield.Id));
            Assert.False(conn.Pools.AttackBlockAtMaximum || conn.Pools.SpellBlockAtMaximum);
            conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
            Assert.True(spell ? conn.Pools.SpellBlockAtMaximum : conn.Pools.AttackBlockAtMaximum);
            inventory.Remove(PlayerInventories.Offhand1, shield.Id); conn.Pools = Game.PoolsFor(conn, 100);
            Assert.False(conn.Pools.AttackBlockAtMaximum || conn.Pools.SpellBlockAtMaximum);
        }
        foreach (byte row in slots)
            foreach (var item in inventory.Items(row)) inventory.Remove(row, item.Id);
        foreach (var (row, item) in saved) inventory.Add(row, item.X, item.Y, item.Item);
        conn.RecentEvents.Clear(); conn.Pools = Game.PoolsFor(conn, 100);
    }
}
