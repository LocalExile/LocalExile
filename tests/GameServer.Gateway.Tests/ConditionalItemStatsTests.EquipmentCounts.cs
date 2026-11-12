using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Each_equipped_item_uses_qualified_counts_for_cost_and_recovery(ClientState conn)
    {
        const string cost = "skill_mana_cost_+_for_each_equipped_corrupted_item";
        const string recovery = "life_regeneration_rate_per_minute_for_each_equipped_uncorrupted_item";
        uint Key(string name) => Program.Stats.Key(name)!.Value;
        var inventory = Game.ItemsOf(conn);
        var saved = inventory.EquipmentSnapshot().Where(e => e.Row is 6 or 7).ToArray();
        foreach (var (row, item) in saved) inventory.Remove(row, item.Id);
        var source = new UniqueCatalog(Program.Items).Create("Vivinsect", new Random(504))!;
        var raw = ItemModStats.Sum([source], Program.Items.Mods);
        Assert.Equal(-2, raw[cost]); Assert.Equal(900, raw[recovery]);
        var counterpart = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Rings/Ring1")!);
        var own = new Dictionary<uint, int> { [Key("mana_cost")] = 100 };
        foreach (bool sourceCorrupted in new[] { false, true })
            foreach (bool otherCorrupted in new[] { false, true })
            {
                var ring = inventory.Add(6, 0, 0, source with { Corrupted = sourceCorrupted });
                var other = inventory.Add(7, 0, 0, counterpart with { Corrupted = otherCorrupted });
                conn.Pools = Game.PoolsFor(conn, 100);
                var enabled = Game.CharacterPlayerStats(conn);
                Assert.False(Game.IsInactive(conn, ring.Id));
                var qualified = inventory.EquipmentSnapshot().Where(e => !Game.IsInactive(conn, e.Item.Id))
                    .Select(e => e.Item.Item).ToArray();
                var counts = Game.EquipmentConditionValues(inventory.EquipmentSnapshot(), Program.Items, qualified);
                int corrupted = counts["equipped_corrupted"], uncorrupted = counts["equipped_uncorrupted"];
                // Preserve attributes, corruption, sockets and every other modifier in the control item.
                var control = ring.Item with { Mods = ring.Item.Mods.Where(m =>
                    Program.Items.Mods.ByHash(m.Hash)?.Stats.All(s => s.Id != cost && s.Id != recovery) != false).ToArray() };
                inventory.Remove(6, ring.Id);
                var disabled = inventory.Add(6, 0, 0, control);
                conn.Pools = Game.PoolsFor(conn, 100);
                var baseline = Game.CharacterPlayerStats(conn);
                Assert.Equal(raw[cost] * corrupted,
                    enabled.GetValueOrDefault(Key("skill_mana_cost_+")) - baseline.GetValueOrDefault(Key("skill_mana_cost_+")));
                Assert.Equal(raw[recovery] * uncorrupted,
                    enabled.GetValueOrDefault(Key("base_life_regeneration_rate_per_minute"))
                        - baseline.GetValueOrDefault(Key("base_life_regeneration_rate_per_minute")));
                int Get(IReadOnlyDictionary<uint, int> stats, string name) => stats.GetValueOrDefault(Key(name));
                Assert.Equal(raw[recovery] * uncorrupted * PanelStats.LifeRegenerationMultiplier(n => Get(enabled, n)),
                    PanelStats.LifePerMinute(n => Get(enabled, n), 1000) - PanelStats.LifePerMinute(n => Get(baseline, n), 1000), 7);
                int without = Game.MergeSkillStats(baseline, own, false)[Key("mana_cost")];
                int with = Game.MergeSkillStats(enabled, own, false)[Key("mana_cost")];
                Assert.Equal(Math.Max(0, without + raw[cost] * corrupted), with);
                inventory.Remove(6, disabled.Id);
                ring = inventory.Add(6, 0, 0, source with { Corrupted = sourceCorrupted });
                for (int repeat = 0; repeat < 3; repeat++)
                    Assert.Equal(enabled.GetValueOrDefault(Key("skill_mana_cost_+")),
                        Game.CharacterPlayerStats(conn).GetValueOrDefault(Key("skill_mana_cost_+")));
                inventory.Remove(6, ring.Id); inventory.Remove(7, other.Id);
            }
        foreach (var (row, item) in saved) inventory.Add(row, item.X, item.Y, item.Item);
        conn.Pools = Game.PoolsFor(conn, 100);
        foreach (var (id, target, countKey, value) in new[]
                 { (cost, "skill_mana_cost_+", "equipped_corrupted", -2),
                   (recovery, "base_life_regeneration_rate_per_minute", "equipped_uncorrupted", 900) })
        {
            var row = Program.Items.Mods.All.Single(m => m.Stats.Any(s => s.Id == id));
            foreach (int count in new[] { -1, 0, 1, 3, int.MaxValue })
            {
                var subject = new StatConditions.Subject(Value: name => name == countKey ? count : null);
                var values = ItemModStats.Sum([WithMod(row.Id, value)], Program.Items.Mods,
                    subject: subject, statExists: Program.Stats.Has);
                Assert.Equal(ActorScale.Saturate((long)value * Math.Max(0, count)), values.GetValueOrDefault(target));
                Assert.Equal(StatConditions.State.NotModelled, StatConditions.Of(id, StatConditions.Subject.Unknown));
            }
        }
        const string composed = "damage_+%_while_on_low_life_for_each_equipped_corrupted_item";
        Assert.Equal("damage_+%", StatConditions.Fold(composed, Program.Stats.Has));
        foreach (bool low in new[] { false, true })
            Assert.Equal(low ? StatConditions.State.Holds : StatConditions.State.Fails, StatConditions.Of(composed,
                new(Value: n => n == "equipped_corrupted" ? 2 : null,
                    Flags: new Dictionary<string, bool> { ["_while_on_low_life"] = low })));
    }
}
