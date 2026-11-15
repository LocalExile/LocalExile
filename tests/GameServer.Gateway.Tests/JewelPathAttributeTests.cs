using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class JewelPathAttributeTests
{
    static ItemMod Mod(string stat, int value) => new(ModSection.Explicit,
        Program.Items.Mods.All.First(m => m.Stats.Count == 1 && m.Stats[0].Id == stat).Hash, [value]);

    static ClientState Character(string name, params InventoryItem[] jewels)
    {
        TestEnvironment.OpenOwnSaves(name);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        CharacterSaves.Write(name, new(new() { [PlayerInventories.PassiveJewels] =
            [.. jewels.Select(i => new CharacterSaves.SavedPlacement(i.Id, i.X, i.Y, CharacterSaves.ToSaved(i.Item, Program.Items.Mods)))] }));
        int row = Program.Skills.RowOfClass(1);
        return new(new(1, "jewel-attributes", 100, 100, 100, 100, null, []), 1, 1)
            { Character = name, ClassRow = row, Pools = CharacterStats.For(Program.Skills.ClassFor(row), 100) };
    }

    [Fact]
    public void Cluster_attributes_reach_requirement_buckets_and_refresh_after_refund_and_raw_override()
    {
        var previous = Program.GameLogicOrNull;
        string name = "ClusterAttr" + Guid.NewGuid().ToString("N");
        var type = Program.Items.Find("Large Cluster Jewel")!;
        var jewel = ClusterJewels.Create(type, ClusterJewels.FindTheme(type.Cluster!, "Fire Damage"), 8, 2)!;
        jewel = jewel with { Mods = [.. jewel.Mods, Mod("local_affliction_jewel_small_nodes_grant_all_attributes", 4)] };
        var conn = Character(name, new InventoryItem(1, 20, 0, jewel));
        try
        {
            conn.Passives.Add(Program.Passives.JewelNodeForSlot(20)!.Value);
            var layout = ClusterJewelLayout.For(20, jewel)!;
            ushort small = (ushort)(layout.Base + layout.ByOffset.First(p => p.Value == ClusterJewelLayout.Kind.Small).Key);
            var inventory = Game.ItemsOf(conn);
            var baseline = Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot()).Attributes;
            conn.ClusterPassives.Add(small);
            var granted = Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            Assert.Equal(baseline.Intelligence + 4, granted.Attributes.Intelligence);
            Assert.Equal(baseline.Strength + 4, granted.Attributes.Strength);
            Assert.Equal(baseline.Dexterity + 4, granted.Attributes.Dexterity);
            Assert.Equal(4, granted.Sources[Program.Stats.Key("additional_all_attributes")!.Value]);
            conn.ClusterPassives.Remove(small);
            Assert.Equal(baseline, Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot()).Attributes);
            conn.ClusterPassives.Add(small);
            conn.PlayerStats[Program.Stats.Key("additional_all_attributes")!.Value] = 7;
            Assert.Equal(baseline.Intelligence + 7,
                Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot()).Attributes.Intelligence);
        }
        finally { PlayerInventories.Forget(name); Program.SetGameLogic(previous!); }
    }

    [Fact]
    public void Split_personality_counts_nested_allocated_rings_and_invalidates_paths_and_item_effects()
    {
        var previous = Program.GameLogicOrNull;
        string name = "SplitPath" + Guid.NewGuid().ToString("N");
        var items = Program.Items;
        var large = ClusterJewels.Create(items.Find("Large Cluster Jewel")!, null, 12, 2)!;
        var medium = ClusterJewels.Create(items.Find("Medium Cluster Jewel")!, null, 5, 1)!;
        medium = medium with { Mods = [.. medium.Mods, Mod("local_affliction_notable_overwhelming_malice", 1)] };
        var split = ItemCatalog.Create(items.Find("Crimson Jewel")!) with { Rarity = ItemRarity.Unique, RequiredLevel = 85,
            Mods = [Mod(JewelPathEffects.EffectStat, 25), Mod("additional_intelligence", 5)] };
        InventoryItem[] jewels = [new(1, 12, 0, large), new(2, 27, 0, medium), new(3, 29, 0, split), new(4, 30, 0, split)];
        var conn = Character(name, jewels);
        try
        {
            ushort root = Program.Passives.JewelNodeForSlot(12)!.Value;
            ushort start = (ushort)ClientTables.Table("PassiveSkills").Single(p => p.Ints("Characters")
                .Any(c => ClientTables.Table("Characters")[c].Int("IntegerId") == 1)).Int("PassiveSkillGraphId");
            var graph = PassiveGraphs.Links(AscendancyFamily.Normal);
            var previousNode = new Dictionary<ushort, ushort> { [start] = start };
            var queue = new Queue<ushort>(); queue.Enqueue(start);
            while (!previousNode.ContainsKey(root) && queue.TryDequeue(out ushort node))
                foreach (ushort next in graph.GetValueOrDefault(node) ?? [])
                    if (Program.Passives.Find(next) is { IsAscendancy: false, IsMastery: false,
                        IsProxy: false, IsExpansion: false, IsStart: false } && previousNode.TryAdd(next, node)) queue.Enqueue(next);
            int rootDistance = 0;
            for (ushort node = root; node != start; node = previousNode[node]) { conn.Passives.Add(node); rootDistance++; }
            var outer = ClusterJewelLayout.For(12, large)!;
            var inner = ClusterJewelLayout.For(27, medium)!;
            Assert.Equal(7, outer.ChildSockets[29]); // Right socket, rather than the uncreated middle one.
            Assert.Equal(1, inner.ChildSockets[30]);
            Assert.Equal(ClusterJewelLayout.Kind.Notable, inner.ByOffset[3]);
            foreach (int offset in new[] { 3, 4, 5, 6, 7, 8, 9, 10, 11 }) conn.ClusterPassives.Add((ushort)(outer.Base + offset));
            foreach (int offset in new[] { 1, 2, 3, 4 }) conn.ClusterPassives.Add((ushort)(inner.Base + offset));
            var inventory = Game.ItemsOf(conn);
            // Exercise calculation-time metadata independently of the save format used by this server revision.
            foreach (uint id in new uint[] { 3, 4 })
                inventory.ChangeUnsaved(PlayerInventories.PassiveJewels, id, item => item with { RequiredLevel = 85 });
            var counts = JewelPathEffects.Counts(Program.Passives, AscendancyFamily.Normal, 1, conn.Passives, conn.ClusterPassives, jewels);
            Assert.Equal(rootDistance + 4, counts[29]);
            Assert.Equal(rootDistance + 12, counts[30]);
            int Roll(int value, int count) => (int)decimal.Floor(value * (100m + 25m * count) / 100m);
            int first = Roll(5, counts[29]), second = Roll(5, counts[30]);
            uint intelligence = Program.Stats.Key("additional_intelligence")!.Value;
            var resolved = Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            Assert.Equal(first + second, resolved.Sources[intelligence]);
            Assert.All(resolved.Worn.Where(w => w.Id is 3 or 4), w => Assert.Equal(85, w.Item.RequiredLevel));
            Assert.All(inventory.Items(PlayerInventories.PassiveJewels).Where(i => i.Id is 3 or 4), i =>
                Assert.Equal(5, ItemModStats.RawAttributes([i.Item], items.Mods)["additional_intelligence"]));
            inventory.ChangeUnsaved(PlayerInventories.PassiveJewels, 3, item => item with
                { Mods = [Mod(JewelPathEffects.EffectStat, 25), Mod("additional_intelligence", 8)] });
            Assert.Equal(Roll(8, counts[29]) + second,
                Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot()).Sources[intelligence]);
            conn.ClusterPassives.Remove((ushort)(outer.Base + 8)); // Both routes now meet an unallocated bridge.
            Assert.Equal(13, Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot()).Sources[intelligence]);
        }
        finally { PlayerInventories.Forget(name); Program.SetGameLogic(previous!); }
    }
}
