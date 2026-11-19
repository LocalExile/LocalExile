using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class JewelPathAttributeTests
{
    static ItemMod Mod(string stat, int value) => new(ModSection.Explicit,
        Program.Items.Mods.All.First(m => m.Stats.Count == 1 && m.Stats[0].Name == stat).Hash, [value]);

    static Player Character(string name, params InventoryItem[] jewels)
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
    public void Split_personality_counts_nested_allocated_rings_and_invalidates_paths_and_item_effects()
    {
        // Native constructors at cell 51: medium rings of 4, 5 and 6 passives, without notables.
        foreach (var (count, ids) in new (int, int[])[]
        {
            (4, [208, 209, 211, 212]), (5, [207, 208, 209, 211, 212]), (6, [207, 208, 209, 210, 211, 212])
        })
        {
            var plain = ClusterJewels.Create(Program.Items.Find("Medium Cluster Jewel")!, null, count, 1)!;
            var mapped = Assert.IsType<ClusterJewelLayout.Layout>(ClusterJewelLayout.For(51, plain));
            Assert.Equal(ids, mapped.ByOffset.Keys.Select(n => mapped.Base + n).Order());
            Assert.Equal(ClusterJewelLayout.Kind.Socket, mapped.ByOffset[5]);
        }
        foreach (var (count, ids) in new (int, int[])[] { (4, [208, 209, 211, 212]), (5, [207, 208, 209, 211, 212]) })
        {
            var mediumWithNotable = ClusterJewels.Create(Program.Items.Find("Medium Cluster Jewel")!, null, count, 1)!;
            mediumWithNotable = mediumWithNotable with { Mods = [.. mediumWithNotable.Mods, Mod("local_affliction_notable_overwhelming_malice", 1)] };
            var mapped = ClusterJewelLayout.For(51, mediumWithNotable)!;
            Assert.Equal(ids, mapped.ByOffset.Keys.Select(n => mapped.Base + n).Order());
            Assert.Equal(ClusterJewelLayout.Kind.Notable, mapped.ByOffset[1]);
        }
        var previous = Program.GameLogicOrNull;
        string name = "SplitPath" + Guid.NewGuid().ToString("N");
        var items = Program.Items;
        var large = ClusterJewels.Create(items.Find("Large Cluster Jewel")!, null, 12, 2)!;
        var medium = ClusterJewels.Create(items.Find("Medium Cluster Jewel")!, null, 5, 1)!;
        medium = medium with { Mods = [.. medium.Mods, Mod("local_affliction_notable_overwhelming_malice", 1)] };
        var split = ItemCatalog.Create(items.Find("Crimson Jewel")!) with { Rarity = ItemRarity.Unique, RequiredLevel = 85,
            Mods = [Mod(Program.Stats.Name(JewelPathEffects.EffectStat), 25), Mod("additional_intelligence", 5)] };
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
            uint intelligence = Stat.AdditionalIntelligence;
            var resolved = Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            Assert.Equal(first + second, resolved.Sources[intelligence]);
            Assert.All(resolved.Worn.Where(w => w.Id is 3 or 4), w => Assert.Equal(85, w.Item.RequiredLevel));
            Assert.All(inventory.Items(PlayerInventories.PassiveJewels).Where(i => i.Id is 3 or 4), i =>
                Assert.Equal(5, ItemModStats.RawAttributes([i.Item], items.Mods)[Stat.AdditionalIntelligence]));
            inventory.ChangeUnsaved(PlayerInventories.PassiveJewels, 3, item => item with
                { Mods = [Mod(Program.Stats.Name(JewelPathEffects.EffectStat), 25), Mod("additional_intelligence", 8)] });
            Assert.Equal(Roll(8, counts[29]) + second,
                Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot()).Sources[intelligence]);
            conn.ClusterPassives.Remove((ushort)(outer.Base + 8)); // Both routes now meet an unallocated bridge.
            Assert.Equal(13, Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot()).Sources[intelligence]);
        }
        finally { PlayerInventories.Forget(name); Program.SetGameLogic(previous!); }
    }
}
