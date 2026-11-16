using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Canonical_culling_items_keep_the_dedicated_hit_owner(ClientState conn, Func<string, byte, InventoryItem> wear)
    {
        var inventory = Game.ItemsOf(conn); var saved = inventory.EquipmentSnapshot();
        uint key = Program.Stats.Key("kill_enemy_on_hit_if_under_10%_life")!.Value;
        Dictionary<uint, int> Stats() => Game.CharacterPlayerStats(conn);
        Game.Cull Cull(bool spell = false) => Game.Cull.Of(Stats(), null, critical: false, spell);
        SpawnedMonster Target(uint life) => new("culling source", new(100, 100, 1), 1, 1, 100)
            { Hostile = true, Life = life };
        try
        {
            foreach (var (row, item) in saved) inventory.Remove(row, item.Id);
            wear("Astramentis", 5); wear("Le Heup of All", 6); wear("Le Heup of All", 7);
            wear("Meginord's Girdle", PlayerInventories.Belt);
            foreach (string name in new[] { "Kaom's Primacy", "Limbsplit", "Reaper's Pursuit", "Relentless Fury", "The Cauteriser" })
            {
                var source = wear(name, PlayerInventories.Weapon1);
                conn.PlayerLife = conn.PlayerMaxLife;
                Assert.False(Game.IsInactive(conn, source.Id)); Assert.Equal(1, Stats().GetValueOrDefault(key));
                var accepted = Cull(); Assert.Equal(10, accepted.Threshold); Assert.Equal(10, Cull(spell: true).Threshold);
                Assert.Equal(11u, Game.TakeOwnerHitLocked(conn, Target(12), 1, accepted).Life);
                var culled = Target(11);
                Assert.Equal(0u, Game.TakeOwnerHitLocked(conn, culled, 1, accepted).Life);
                Assert.Equal(0, culled.Overkill);
                var protectedTarget = Target(11); protectedTarget.KingGrace = true;
                Assert.Equal(11u, Game.TakeOwnerHitLocked(conn, protectedTarget, 1, accepted).Life);
                inventory.Remove(PlayerInventories.Weapon1, source.Id);
                Assert.Equal(0, Stats().GetValueOrDefault(key)); Assert.Equal(0, Cull().Threshold);
                Assert.Equal(10u, Game.TakeOwnerHitLocked(conn, Target(11), 1, Cull()).Life);
                var restored = inventory.Add(PlayerInventories.Weapon1, 0, 0, source.Item);
                Assert.Equal(10, Cull().Threshold);
                conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
                Assert.True(Game.IsInactive(conn, restored.Id)); Assert.Equal(0, Cull().Threshold);
                conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
                Assert.Equal(10, Cull().Threshold);
                inventory.Remove(PlayerInventories.Weapon1, restored.Id);
            }
        }
        finally
        {
            conn.Experience = CharacterStats.ExperienceFor(100);
            foreach (var (row, item) in inventory.EquipmentSnapshot()) inventory.Remove(row, item.Id);
            foreach (var (row, item) in saved) inventory.Put(row, item);
            conn.Pools = Game.PoolsFor(conn, 100); conn.PlayerLife = conn.PlayerMaxLife;
        }
    }
}
