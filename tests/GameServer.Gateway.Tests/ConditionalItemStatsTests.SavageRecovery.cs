using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Savage_hit_facts_survive_immediate_block_recovery(ClientState conn, Func<string, byte, InventoryItem> wear)
    {
        var inventory = Game.ItemsOf(conn); var saved = inventory.EquipmentSnapshot();
        var random = conn.IncomingHitRandom; bool running = conn.Clock.IsRunning; conn.Clock.Stop();
        bool immune = conn.Immune;
        uint glancing = Program.Stats.Key("keystone_glancing_blows")!.Value;
        Game.SyntheticEnemyHit Synthetic(DamageType type, int amount, bool block, bool spell)
        {
            conn.Clock.Start();
            try
            {
                Assert.True(Game.TrySyntheticEnemyHit(conn, type, amount, block, spell, suppress: false, out var hit));
                return hit;
            }
            finally { conn.Clock.Stop(); }
        }
        try
        {
            foreach (var (row, item) in saved) inventory.Remove(row, item.Id);
            wear("Astramentis", 5); wear("Le Heup of All", 6); wear("Le Heup of All", 7);
            wear("Meginord's Girdle", PlayerInventories.Belt);
            var shield = wear("The Surrender", PlayerInventories.Offhand1);
            // Diagnostic Glancing Blows permits blocked damage; recovery and block chance come from the actual shield.
            conn.PlayerStats[glancing] = 1; conn.Immune = false;
            conn.Pools = Game.PoolsFor(conn, 100); conn.PlayerLife = conn.PlayerMaxLife;
            Assert.False(Game.IsInactive(conn, shield.Id));
            var raw = ItemModStats.Sum([shield.Item], Program.Items.Mods);
            Assert.Equal(raw["recover_X_life_on_block"], conn.Pools.LifeGainedOnBlock);
            Assert.True(conn.Pools.LifeGainedOnBlock * 100L >= conn.PlayerMaxLife * 16L);
            conn.IncomingHitRandom = new PhasingRoll(0);
            var monster = new SpawnedMonster("savage recovery", new(100, 100, 0), 0, 0, 1000);
            foreach (bool synthetic in new[] { false, true })
            foreach (bool small in new[] { false, true })
            {
                conn.RecentEvents.Clear(); conn.FlaskAutomation.Discard();
                conn.PlayerLife = conn.PlayerMaxLife; conn.EvasionEntropy = 100;
                conn.LastEvasionAt = conn.Clock.Elapsed.TotalSeconds;
                int amount = (int)(conn.PlayerMaxLife * (small ? .05 : .24));
                int damage;
                if (synthetic)
                {
                    var hit = Synthetic(DamageType.Chaos, amount, block: true, spell: false);
                    Assert.True(hit.Blocked); damage = hit.Damage;
                }
                else
                {
                    var attack = new MonsterAttack(0, 1, 100, DamagePacket.Of(DamageType.Chaos, amount, amount));
                    var hit = Game.CommitMonsterHitLocked("savage-recovery-control", conn, 0x5049, monster, attack,
                        conn.Clock.Elapsed.TotalSeconds, (100, 100), (100, 100), checkRange: false);
                    Assert.NotNull(hit); Assert.True(hit.Blocked); damage = hit.Damage;
                }
                Assert.Equal(!small, damage * 100L >= conn.PlayerMaxLife * 15L);
                Assert.Equal(conn.PlayerMaxLife, conn.PlayerLife); // Recovery masks every point of the immediate loss.
                Assert.Equal(!small, conn.RecentEvents.Happened(StatConditions.TakenSavageHit, conn.Clock.Elapsed.TotalSeconds));
                if (!synthetic)
                    Assert.Equal(!small, conn.FlaskAutomation.Events.Any(e => e.Trigger == FlaskEnchantments.Trigger.SavageHit));
            }
            // Diagnostic ES control: a large mitigated hit that removes no Life is not a Savage Hit.
            conn.RecentEvents.Clear(); conn.FlaskAutomation.Discard(); conn.IncomingHitRandom = new PhasingRoll(99);
            conn.EnergyShield.SetMaximum(10000, refill: true);
            Synthetic(DamageType.Fire, (int)conn.PlayerMaxLife, block: false, spell: true);
            Assert.Equal(conn.PlayerMaxLife, conn.PlayerLife);
            Assert.False(conn.RecentEvents.Happened(StatConditions.TakenSavageHit, conn.Clock.Elapsed.TotalSeconds));
            inventory.Remove(PlayerInventories.Offhand1, shield.Id);
            conn.Pools = Game.PoolsFor(conn, 100); Assert.Equal(0, conn.Pools.LifeGainedOnBlock);
            inventory.Add(PlayerInventories.Offhand1, 0, 0, shield.Item);
            conn.Pools = Game.PoolsFor(conn, 100); Assert.Equal(raw["recover_X_life_on_block"], conn.Pools.LifeGainedOnBlock);
        }
        finally
        {
            conn.PlayerStats.Remove(glancing); conn.Immune = immune; conn.IncomingHitRandom = random;
            conn.RecentEvents.Clear(); conn.FlaskAutomation.Discard(); Game.ChargesOf(conn).Die();
            foreach (var (row, item) in inventory.EquipmentSnapshot()) inventory.Remove(row, item.Id);
            foreach (var (row, item) in saved) inventory.Put(row, item);
            conn.Pools = Game.PoolsFor(conn, 100); conn.PlayerLife = conn.PlayerMaxLife;
            conn.EnergyShield.SetMaximum(conn.Pools.EnergyShield, refill: true);
            if (running) conn.Clock.Start();
        }
    }
}
