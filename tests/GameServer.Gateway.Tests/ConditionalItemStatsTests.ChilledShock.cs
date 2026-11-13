using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Chilled_staff_sources_reach_existing_shock_avoidance(ClientState conn, Func<string, byte, InventoryItem> wear)
    {
        var inventory = Game.ItemsOf(conn);
        var saved = inventory.EquipmentSnapshot().ToArray();
        var savedOverrides = conn.PlayerStats.ToArray(); conn.PlayerStats.Clear();
        foreach (var (row, item) in saved) inventory.Remove(row, item.Id);
        wear("Astramentis", 5);
        foreach (string name in new[] { "The Stormheart", "The Stormwall" })
        {
            var weapon = wear(name, PlayerInventories.Weapon1);
            const string source = "avoid_shock_%_while_chilled";
            Assert.Equal(100, ItemModStats.Sum([weapon.Item], Program.Items.Mods).GetValueOrDefault(source));
            var stats = Game.CharacterPlayerStats(conn);
            int Get(string id) => Program.Stats.Key(id) is uint key ? stats.GetValueOrDefault(key) : 0;
            void Read() => stats = Game.CharacterPlayerStats(conn);
            double at = Game.ResourceEffectsTime(conn);
            void Inflict(PlayerElementalAilments.Kind kind) =>
                conn.ElementalAilments.Inflict(kind, 20, 4, at, Get, new PhasingRoll(99));
            conn.ElementalAilments.Clear(); Read();
            Assert.Equal(0, Get("avoid_shock_%"));
            Inflict(PlayerElementalAilments.Kind.Shock);
            Assert.Equal(20, conn.ElementalAilments.Snapshot(at).Shock);
            Inflict(PlayerElementalAilments.Kind.Chill); Read();
            Assert.Equal(100, Get("avoid_shock_%"));
            Assert.Equal(20, conn.ElementalAilments.Snapshot(at).Shock); // Avoidance is admission, not existing-shock removal.
            conn.ElementalAilments.Clear();
            Inflict(PlayerElementalAilments.Kind.Chill); Read();
            Inflict(PlayerElementalAilments.Kind.Shock);
            Assert.Equal(0, conn.ElementalAilments.Snapshot(at).Shock);
            Assert.True(conn.ElementalAilments.Snapshot(at).Chill > 0);
            conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1); Read();
            Assert.True(Game.IsInactive(conn, weapon.Id)); Assert.Equal(0, Get("avoid_shock_%"));
            Inflict(PlayerElementalAilments.Kind.Shock);
            Assert.Equal(20, conn.ElementalAilments.Snapshot(at).Shock);
            conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100); Read();
            Assert.Equal(100, Get("avoid_shock_%"));
            inventory.Remove(PlayerInventories.Weapon1, weapon.Id); Read();
            Assert.Equal(0, Get("avoid_shock_%")); Assert.True(conn.ElementalAilments.Snapshot(at).Chill > 0);
            weapon = inventory.Add(PlayerInventories.Weapon1, 0, 0, weapon.Item); Read();
            Assert.Equal(100, Get("avoid_shock_%"));
            Assert.Equal(0, conn.ElementalAilments.Snapshot(at + 4).Chill);
            conn.ElementalAilments.Advance(at + 3, at + 4, _ => 0, 0, 75, fireImmune: false); Read();
            Assert.Equal(0, Get("avoid_shock_%"));
            Assert.Equal(StatConditions.State.NotModelled, StatConditions.Of(source, StatConditions.Subject.Unknown));
            inventory.Remove(PlayerInventories.Weapon1, weapon.Id);
        }
        conn.ElementalAilments.Clear();
        foreach (var (row, item) in inventory.EquipmentSnapshot()) inventory.Remove(row, item.Id);
        foreach (var (row, item) in saved) inventory.Add(row, item.X, item.Y, item.Item);
        foreach (var (key, value) in savedOverrides) conn.PlayerStats[key] = value;
        conn.Pools = Game.PoolsFor(conn, 100);
    }
}
