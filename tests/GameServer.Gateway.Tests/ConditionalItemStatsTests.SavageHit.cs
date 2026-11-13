using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Savage_hit_item_speed_reaches_final_attack_timing(ClientState conn, Func<string, byte, InventoryItem> wear)
    {
        var inventory = Game.ItemsOf(conn); var saved = inventory.EquipmentSnapshot();
        var replacements = conn.PlayerStats.ToArray(); bool immune = conn.Immune;
        bool running = conn.Clock.IsRunning; conn.Clock.Stop();
        uint Key(string name) => Program.Stats.Key(name)!.Value;
        Dictionary<uint, int> stats = [];
        int Get(string name) => stats.GetValueOrDefault(Key(name));
        void Read() => stats = Game.CharacterPlayerStats(conn);
        void Rebuild()
        {
            conn.Pools = Game.PoolsFor(conn, 100); conn.PlayerLife = conn.PlayerMaxLife;
            conn.PlayerMana = conn.Pools.Mana; Read();
        }
        void Hit(int damage)
        {
            conn.Clock.Start();
            try { Assert.True(Game.TrySyntheticEnemyHit(conn, DamageType.Physical, damage,
                block: false, spell: false, suppress: false, out _)); }
            finally { conn.Clock.Stop(); }
        }
        try
        {
            conn.PlayerStats.Clear(); conn.RecentEvents.Clear(); conn.Immune = false;
            foreach (var (row, item) in saved) inventory.Remove(row, item.Id);
            wear("Astramentis", 5); wear("Le Heup of All", 6); wear("Le Heup of All", 7);
            wear("Meginord's Girdle", PlayerInventories.Belt);
            var source = wear("Hezmana's Bloodlust", PlayerInventories.Weapon1); Rebuild();
            Assert.False(Game.IsInactive(conn, source.Id)); Assert.Equal(40, Get(Game.SavageHitAttackSpeed));
            int baseline = Get("attack_speed_+%");
            double baselineAttack = Game.WeaponFor(conn).AttackSeconds(stats);
            Assert.False(Game.ItemConditionFlags(conn, conn.Clock.Elapsed.TotalSeconds)[Game.RecentSavageHitItemFlag]);
            uint before = conn.PlayerLife;
            Hit((int)(conn.PlayerMaxLife / 10));
            Assert.True((long)(before - conn.PlayerLife) * 100 < (long)conn.PlayerMaxLife * 15);
            Read(); Assert.Equal(baseline, Get("attack_speed_+%"));
            conn.PlayerLife = conn.PlayerMaxLife; before = conn.PlayerLife;
            Hit((int)(conn.PlayerMaxLife / 2));
            Assert.True((long)(before - conn.PlayerLife) * 100 >= (long)conn.PlayerMaxLife * 15);
            double now = conn.Clock.Elapsed.TotalSeconds;
            Assert.True(conn.RecentEvents.Happened(StatConditions.TakenSavageHit, now));
            Read(); Assert.Equal(baseline + 40, Get("attack_speed_+%"));
            Assert.Equal(baseline + 40, conn.Pools.AttackSpeedPct);
            Assert.True(Game.WeaponFor(conn).AttackSeconds(stats) < baselineAttack);
            inventory.Remove(PlayerInventories.Weapon1, source.Id); Read();
            Assert.Equal(0, Get(Game.SavageHitAttackSpeed)); Assert.Equal(baseline, Get("attack_speed_+%"));
            var restored = inventory.Add(PlayerInventories.Weapon1, 0, 0, source.Item); Read();
            Assert.Equal(baseline + 40, Get("attack_speed_+%"));
            conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1); Read();
            Assert.True(Game.IsInactive(conn, restored.Id)); Assert.Equal(0, Get(Game.SavageHitAttackSpeed));
            Assert.Equal(0, Get("attack_speed_+%"));
            conn.Experience = CharacterStats.ExperienceFor(100); Rebuild();
            Assert.Equal(baseline + 40, Get("attack_speed_+%"));
            conn.RecentEvents.Record(StatConditions.TakenSavageHit, now - 4); Read();
            Assert.Equal(baseline, Get("attack_speed_+%"));
            Assert.Equal(baselineAttack, Game.WeaponFor(conn).AttackSeconds(stats), 8);
            conn.RecentEvents.Record(StatConditions.TakenSavageHit, now - 3.999); Read();
            Assert.Equal(baseline + 40, Get("attack_speed_+%"));
            conn.PlayerStats[Key(Game.SavageHitAttackSpeed)] = 7; Read();
            Assert.Equal(baseline + 7, Get("attack_speed_+%"));
            conn.PlayerStats[Key("attack_speed_+%")] = 99; Read();
            Assert.Equal(99, Get("attack_speed_+%"));
            conn.PlayerStats.Clear(); conn.PlayerLife = 0; Read();
            Assert.Equal(baseline, Get("attack_speed_+%"));
            conn.PlayerLife = conn.PlayerMaxLife; conn.RecentEvents.Clear(); Read();
            Assert.Equal(baseline, Get("attack_speed_+%"));
        }
        finally
        {
            conn.Immune = immune; conn.Experience = CharacterStats.ExperienceFor(100);
            conn.PlayerStats.Clear(); foreach (var p in replacements) conn.PlayerStats[p.Key] = p.Value;
            conn.RecentEvents.Clear();
            foreach (var (row, item) in inventory.EquipmentSnapshot()) inventory.Remove(row, item.Id);
            foreach (var (row, item) in saved) inventory.Put(row, item);
            Rebuild();
            if (running) conn.Clock.Start();
        }
    }
}
