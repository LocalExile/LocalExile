using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Recent_hit_life_loss_integrates_windows_and_qualified_sources(ClientState conn, Func<string, byte, InventoryItem> wear)
    {
        var interval = new RecentHitLifeLossState { At = 0, Started = 0, Until = 4, PercentPerMinute = 120 };
        interval.Settle(1, 1000); Assert.Equal(20, interval.Pending);
        interval.Settle(1, 1000); Assert.Equal(20, interval.Pending);
        interval.Settle(20, 1000); Assert.Equal(80, interval.Pending);
        interval.Clear(0); interval.PercentPerMinute = 120; interval.Until = 4;
        interval.Settle(2, 1000); interval.Started = 2; interval.Until = 6;
        interval.Settle(20, 1000); Assert.Equal(120, interval.Pending);
        interval.Clear(0); interval.PercentPerMinute = 120; interval.Until = 4;
        for (int i = 1; i <= 400; i++) interval.Settle(i / 100d, 1);
        Assert.Equal(.08, interval.Pending, 10);
        interval.Clear(0); interval.PercentPerMinute = 120; interval.Until = 4;
        interval.Settle(1, 1000); interval.PercentPerMinute = 60;
        interval.Settle(2, 1000); interval.PercentPerMinute = 0;
        interval.Settle(20, 1000); Assert.Equal(30, interval.Pending);

        var inventory = Game.ItemsOf(conn);
        var saved = inventory.EquipmentSnapshot().ToArray();
        var overrides = conn.PlayerStats.ToArray();
        uint priorLife = conn.PlayerLife; bool priorImmune = conn.Immune;
        foreach (var (row, item) in saved) inventory.Remove(row, item.Id);
        conn.PlayerStats.Clear(); conn.RecentEvents.Clear(); conn.ElementalAilments.Clear();
        conn.RetireResourceEffects();
        try
        {
            conn.PlayerLife = 1000;
            wear("Astramentis", 5);
            var source = wear("Replica Ambu's Charge", PlayerInventories.BodyArmour);
            Dictionary<uint, int> Read() => Game.CharacterPlayerStats(conn);
            int Value(IReadOnlyDictionary<uint, int> stats, string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
            var stats = Read(); var state = Game.RecentHitLifeLossOf(conn);
            Assert.Equal(120, ItemModStats.Sum([source.Item], Program.Items.Mods).GetValueOrDefault(Game.LifeLossIfHitRecently));
            Assert.Equal(120, state.PercentPerMinute); Assert.Contains(source.Id, state.Hosts);
            Assert.Equal(0, Value(stats, "life_loss_%_per_minute"));
            wear("Crest of Perandus", PlayerInventories.Offhand1);
            conn.RecentEvents.Clear(); conn.Pools = Game.PoolsFor(conn, 100);
            conn.PlayerLife = conn.PlayerMaxLife; conn.IncomingHitRandom = new PhasingRoll(0);
            bool running = conn.Clock.IsRunning;
            try
            {
                if (!running) conn.Clock.Start();
                Assert.True(Game.TrySyntheticEnemyHit(conn, DamageType.Physical, 10, true, false, false, out var hit));
                Assert.True(hit.Blocked);
            }
            finally { if (!running) conn.Clock.Stop(); }
            stats = Read(); double at = Game.ResourceEffectsTime(conn);
            Assert.Equal(120, Value(stats, "life_loss_%_per_minute"));
            Assert.True(state.Until > at);
            var window = conn.RecentEvents.LastWindow(StatConditions.BeenHit);
            conn.LeavingArea = true;
            Assert.False(Game.TrySyntheticEnemyHit(conn, DamageType.Physical, 10, false, false, false, out _));
            Assert.Equal(window, conn.RecentEvents.LastWindow(StatConditions.BeenHit)); conn.LeavingArea = false;

            // Accrue from the actual source; transport that debt to the current clock so unrelated timed owners
            // never move forward and back in this shared connection fixture.
            state.Pending = 0; state.At = at;
            state.Settle(at + .25, conn.PlayerMaxLife);
            double accrued = conn.PlayerMaxLife * .005;
            Assert.Equal(accrued, state.Pending, 6);
            var carried = Game.CaptureRecentHitLifeLoss(conn, at + .25);
            Game.RestoreRecentHitLifeLoss(conn, carried, at);
            stats = Read();
            conn.EnergyShield.SetMaximum(200, refill: true);
            conn.Ward.SetMaximum(100); conn.Ward.Restore();
            conn.PlayerMana = 50; conn.PlayerLife = conn.PlayerMaxLife; conn.Immune = true;
            uint life = conn.PlayerLife, es = conn.EnergyShield.Current, ward = conn.Ward.Current;
            double manaCarry = 0;
            Assert.True(Game.CompleteResourceRecoveryLocked(conn, at, at, stats, 0, 50, 100, ref manaCarry));
            Assert.Equal((uint)Math.Floor(accrued), life - conn.PlayerLife);
            Assert.Equal(es, conn.EnergyShield.Current); Assert.Equal(ward, conn.Ward.Current); Assert.Equal(50u, conn.PlayerMana);
            Assert.Equal(window, conn.RecentEvents.LastWindow(StatConditions.BeenHit));
            Assert.Equal(0u, Game.AdvanceRecentHitLifeLossLocked(conn, at));

            var resources = Game.ResourceEffectsOf(conn);
            resources.Freeze(at); double pending = state.Pending;
            state.Settle(at + 20, conn.PlayerMaxLife); Assert.Equal(pending, state.Pending);
            resources.Resume(at + 20); Assert.False(state.Frozen);
            Assert.Equal(0u, Game.AdvanceRecentHitLifeLossLocked(conn, at + 21));
            Assert.Equal(pending, state.Pending);
            // Reinitialise at the fixture clock without charging the loading interval.
            Game.RestoreRecentHitLifeLoss(conn, null, at); Read();
            state.Pending = 3.5;
            inventory.Remove(PlayerInventories.BodyArmour, source.Id); Read();
            Assert.Empty(state.Hosts); Assert.Equal(0, state.PercentPerMinute);
            Assert.Equal(3u, Game.AdvanceRecentHitLifeLossLocked(conn, at + 1));
            Assert.Equal(0u, Game.AdvanceRecentHitLifeLossLocked(conn, at + 10));
            Game.RestoreRecentHitLifeLoss(conn, null, at);
            var replacement = inventory.Add(PlayerInventories.BodyArmour, 0, 0, source.Item);
            Read(); Assert.Contains(replacement.Id, state.Hosts); Assert.DoesNotContain(source.Id, state.Hosts);
            conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
            Read(); Assert.Empty(state.Hosts); Assert.Equal(0, state.PercentPerMinute);
            conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
            Read(); Assert.Contains(replacement.Id, state.Hosts);
            conn.RecentEvents.Clear(); Game.RestoreRecentHitLifeLoss(conn, new(2.5), at);
            Read(); Assert.Equal(2u, Game.AdvanceRecentHitLifeLossLocked(conn, at + 20));
            Assert.Equal(.5, state.Pending, 6); // No invented recent history in a new area.
            Game.RestoreRecentHitLifeLoss(conn, new(2), at); Read();
            conn.PlayerLife = 1; conn.PlayerMana = 0; manaCarry = .5;
            Assert.False(Game.CompleteResourceRecoveryLocked(conn, at, at, stats, 100, 0, 100, ref manaCarry));
            Assert.Equal(0u, conn.PlayerMana); Assert.Equal(0, manaCarry); Assert.Equal(0, state.Pending);
            conn.PlayerLife = 100; Read(); state.Pending = 10;
            conn.ProjectileEpoch++;
            Assert.Equal(0u, Game.AdvanceRecentHitLifeLossLocked(conn, at)); Assert.Equal(100u, conn.PlayerLife);
            Read(); state.Pending = 10; conn.RetireResourceEffects(); Assert.Equal(0, state.Pending);
        }
        finally
        {
            conn.LeavingArea = false; conn.Immune = priorImmune; conn.PlayerLife = Math.Max(1u, priorLife);
            conn.Experience = CharacterStats.ExperienceFor(100); conn.RecentEvents.Clear(); conn.RetireResourceEffects();
            foreach (var (row, item) in inventory.EquipmentSnapshot()) inventory.Remove(row, item.Id);
            foreach (var (row, item) in saved) inventory.Put(row, item);
            conn.PlayerStats.Clear(); foreach (var entry in overrides) conn.PlayerStats[entry.Key] = entry.Value;
            conn.Pools = Game.PoolsFor(conn, 100); Game.CharacterPlayerStats(conn);
        }
    }
}
