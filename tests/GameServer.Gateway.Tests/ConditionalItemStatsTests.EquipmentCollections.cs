using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Equipment_collections_reach_actual_helmet_life_and_regeneration(ClientState conn)
    {
        uint Key(string name) => Program.Stats.Key(name)!.Value;
        var inventory = Game.ItemsOf(conn);
        var saved = inventory.EquipmentSnapshot().ToArray();
        foreach (var (row, item) in saved) inventory.Remove(row, item.Id);
        var unique = new UniqueCatalog(Program.Items);
        var amulet = unique.Create("Astramentis", new Random(504))!;
        var counterpart = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Rings/Ring1")!);
        var cases = new (string Name, bool All, string Input, string Output)[]
        {
            ("Voll's Vision", false, "life_+%_with_no_corrupted_equipped_items", "maximum_life_+%"),
            ("Voll's Vision", false, "life_regeneration_per_minute_with_no_corrupted_equipped_items", "base_life_regeneration_rate_per_minute"),
            ("Malachai's Vision", true, "energy_shield_recharge_rate_per_minute_with_all_corrupted_equipped_items", "base_energy_shield_regeneration_rate_per_minute"),
            ("Malachai's Vision", true, "mana_recharge_rate_per_minute_with_all_corrupted_equipped_items", "base_mana_regeneration_rate_per_minute"),
        };
        foreach (var test in cases)
        {
            var source = unique.Create(test.Name, new Random(504))!;
            int value = ItemModStats.Sum([source], Program.Items.Mods).GetValueOrDefault(test.Input);
            Assert.True(value > 0, test.Input);
            var neck = inventory.Add(5, 0, 0, amulet with { Corrupted = test.All });
            var ring = inventory.Add(6, 0, 0, counterpart with { Corrupted = test.All });
            var helmet = inventory.Add(PlayerInventories.Helmet, 0, 0, source);
            conn.Pools = Game.PoolsFor(conn, 100);
            var active = Game.CharacterPlayerStats(conn); var activePools = conn.Pools;
            Assert.False(Game.IsInactive(conn, helmet.Id));
            // Same helmet with only this coefficient removed controls the final consumer independently of its local defences.
            var control = source with { Mods = source.Mods.Where(m =>
                Program.Items.Mods.ByHash(m.Hash)?.Stats.All(s => s.Id != test.Input) != false).ToArray() };
            inventory.Remove(PlayerInventories.Helmet, helmet.Id);
            var disabled = inventory.Add(PlayerInventories.Helmet, 0, 0, control);
            conn.Pools = Game.PoolsFor(conn, 100);
            var baseline = Game.CharacterPlayerStats(conn);
            int Get(IReadOnlyDictionary<uint, int> stats, string name) => stats.GetValueOrDefault(Key(name));
            Assert.Equal(value, Get(active, test.Output) - Get(baseline, test.Output));
            switch (test.Output)
            {
                case "maximum_life_+%": Assert.True(activePools.Life > conn.Pools.Life); break;
                case "base_life_regeneration_rate_per_minute":
                    Assert.Equal(value, PanelStats.LifePerMinute(n => Get(active, n), 1000)
                        - PanelStats.LifePerMinute(n => Get(baseline, n), 1000), 7); break;
                case "base_energy_shield_regeneration_rate_per_minute":
                    Assert.Equal(value, EnergyShield.RegenPerMinute(1000, n => Get(active, n))
                        - EnergyShield.RegenPerMinute(1000, n => Get(baseline, n)), 7); break;
                case "base_mana_regeneration_rate_per_minute":
                    Assert.Equal(value, PanelStats.ManaPerMinute(n => Get(active, n), 1000)
                        - PanelStats.ManaPerMinute(n => Get(baseline, n), 1000), 7); break;
            }
            inventory.Remove(PlayerInventories.Helmet, disabled.Id);
            helmet = inventory.Add(PlayerInventories.Helmet, 0, 0, source);
            inventory.Remove(6, ring.Id);
            ring = inventory.Add(6, 0, 0, counterpart with { Corrupted = !test.All });
            var off = Game.CharacterPlayerStats(conn);
            Assert.Equal(Get(baseline, test.Output), Get(off, test.Output));
            inventory.Remove(6, ring.Id);
            Assert.Equal(Get(active, test.Output), Get(Game.CharacterPlayerStats(conn), test.Output));
            var highLevel = ItemCatalog.Create(Program.Items.Find("Steel Ring")!) with { Corrupted = !test.All };
            ring = inventory.Add(6, 0, 0, highLevel);
            conn.Experience = CharacterStats.ExperienceFor(70); conn.Pools = Game.PoolsFor(conn, 70);
            Assert.True(Game.IsInactive(conn, ring.Id));
            Assert.False(Game.IsInactive(conn, helmet.Id));
            Assert.Equal(value, Get(Game.CharacterPlayerStats(conn), test.Output));
            conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
            Assert.False(Game.IsInactive(conn, ring.Id));
            Assert.Equal(0, Get(Game.CharacterPlayerStats(conn), test.Output));
            inventory.Remove(6, ring.Id);
            inventory.Remove(PlayerInventories.Helmet, helmet.Id); inventory.Remove(5, neck.Id);
        }
        foreach (var (row, item) in saved) inventory.Add(row, item.X, item.Y, item.Item);
        conn.Pools = Game.PoolsFor(conn, 100);
        foreach (bool all in new[] { false, true })
        {
            string id = all ? cases[2].Input : cases[0].Input;
            foreach (var (corrupted, total) in new[] { (0, 0), (0, 3), (1, 3), (3, 3) })
                Assert.Equal((all ? total > 0 && corrupted == total : corrupted == 0)
                    ? StatConditions.State.Holds : StatConditions.State.Fails,
                    StatConditions.Of(id, new(Value: n => n == "equipped_corrupted" ? corrupted : n == "equipped_total" ? total : null)));
            Assert.Equal(StatConditions.State.NotModelled, StatConditions.Of(id, StatConditions.Subject.Unknown));
        }
        const string composed = "damage_+%_while_on_low_life_with_no_corrupted_equipped_items";
        Assert.Equal("damage_+%", StatConditions.Fold(composed, Program.Stats.Has));
        Assert.Equal(StatConditions.State.Fails, StatConditions.Of(composed,
            new(Value: n => n == "equipped_corrupted" ? 0 : null,
                Flags: new Dictionary<string, bool> { ["_while_on_low_life"] = false })));
    }
}
