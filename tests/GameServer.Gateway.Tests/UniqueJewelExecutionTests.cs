using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class UniqueJewelExecutionTests
{
    [Fact]
    public void Ordinary_unique_rolls_produce_readable_timeless_jewels_for_each_faction()
    {
        var items = Program.Items;
        var uniques = new UniqueCatalog(items);
        var random = new Random(318);
        Assert.NotEmpty(TimelessJewelCatalog.All);
        foreach (var family in TimelessJewelCatalog.All)
        {
            var conquerors = new HashSet<int>();
            for (var i = 0; i < 64; i++)
            {
                var jewel = Assert.IsType<Item>(uniques.Create(family.Name, random));
                var spec = Assert.IsType<TimelessSpec>(TimelessJewelCatalog.Read(jewel, items.Mods));
                Assert.Equal(family.Name, spec.Name);
                Assert.InRange(spec.Seed, family.MinSeed, family.MaxSeed);
                Assert.Equal(spec.Seed * family.DisplayMultiplier, spec.DisplayedSeed);
                conquerors.Add(spec.ConquerorIndex);
                Assert.Equal(family.Name == "Militant Faith" ? 3 : 1, jewel.Mods.Count);
            }
            Assert.True(conquerors.SetEquals(family.Conquerors.Select(c => c.Index)));
        }
    }

    [Fact]
    public void Impossible_escape_depends_on_its_socket_and_excludes_its_named_keystone()
    {
        var path = Path.Combine(Path.GetTempPath(), $"escape-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        try
        {
            var player = new Player(new(1, "escape", 100, 100, 100, 100, null, []), 1, 1)
                { Character = { Name = $"Escape{Guid.NewGuid():N}" } };
            var tree = Game.PassivesFor(player);
            var socket = tree.JewelNodeForSlot(2)!.Value;
            var inventory = Game.ItemsOf(player);
            Assert.NotEmpty(ImpossibleEscapeCatalogue.All);
            foreach (var target in ImpossibleEscapeCatalogue.All)
            {
                var jewel = Assert.IsType<Item>(ImpossibleEscapeCatalogue.Create(Program.Items, target.Name));
                inventory.Put(PlayerInventories.PassiveJewels, new(2, 2, 0, jewel));
                var available = Game.AllocationBypassNodes(player, [socket]);
                Assert.NotEmpty(available);
                Assert.DoesNotContain(target.GraphId, available.Keys);
                Assert.All(available, pair => Assert.Contains(socket, pair.Value));
                Assert.Empty(Game.AllocationBypassNodes(player, [socket], skipItemId: 2));
                Assert.Empty(Game.AllocationBypassNodes(player, [target.GraphId]));
                if (target.Name == "Supreme Ego") Assert.Contains((ushort)9015, available.Keys);
                inventory.Remove(PlayerInventories.PassiveJewels, 2);
            }
        }
        finally
        {
            TestEnvironment.ReleaseSaves(path);
        }
    }

    [Fact]
    public void Thread_of_hope_creation_variants_divine_and_gulf_preserve_ring_and_omit_base_radius()
    {
        var items = Program.Items;
        var uniques = new UniqueCatalog(items);
        var rolls = new ItemRolls(items);
        var rng = new Random(1337);

        // Normal UniqueCatalog creation (Sirus / /unique) rolls Variable rings 1..4 with donut mod, no base radius, no implicits.
        var rolledRings = new HashSet<int>();
        for (int i = 0; i < 48; i++)
        {
            var jewel = Assert.IsType<Item>(uniques.Create("Thread of Hope", rng));
            Assert.DoesNotContain(jewel.Mods, m => m.Section == ModSection.Implicit);
            var ids = jewel.Mods.Select(m => items.Mods.ByHash(m.Hash)?.Id).ToArray();
            Assert.Contains("JewelRingRadiusValuesUnique__1", ids);
            Assert.Contains("AllocateDisconnectedPassivesDonutUnique__1", ids);
            Assert.Contains("AllResistancesUnique__6", ids);
            Assert.DoesNotContain("JewelUniqueAllocateDisconnectedPassives", ids);
            Assert.False(ItemModStats.Local(jewel, items.Mods).ContainsKey(Stat.LocalJewelEffectBaseRadius));
            var conv = Assert.IsType<RadiusJewelCatalogue.Conversion>(RadiusJewelCatalogue.ReadConversion(jewel, items.Mods));
            Assert.True(conv.AllocateDisconnected);
            Assert.Equal(0, conv.Radius);
            Assert.InRange(conv.Ring, 1, 4);
            Assert.Equal("Thread of Hope", RadiusJewelCatalogue.EntryOf(jewel, items.Mods)?.Name);
            rolledRings.Add(conv.Ring);
        }
        Assert.Equal([1, 2, 3, 4], rolledRings.OrderBy(r => r).ToArray());

        // Massive variant rolls ring 5 via JewelRingRadiusValuesUnique__2 and keeps ring 5 across Divine Orbs.
        var massive = Assert.IsType<Item>(uniques.Create("Thread of Hope (Massive)", rng));
        Assert.DoesNotContain(massive.Mods, m => m.Section == ModSection.Implicit);
        Assert.Equal(5, RadiusJewelCatalogue.ReadConversion(massive, items.Mods)?.Ring);
        Assert.Null(rolls.Bless(massive, rng, uniques.RollsOf(massive)));
        var divinedMassive = Assert.IsType<Item>(rolls.Reforge(massive, rng, uniques.RollsOf(massive)));
        Assert.Equal(5, RadiusJewelCatalogue.ReadConversion(divinedMassive, items.Mods)?.Ring);
        Assert.False(ItemModStats.Local(divinedMassive, items.Mods).ContainsKey(Stat.LocalJewelEffectBaseRadius));

        // RadiusJewelCatalogue.Create creates all 5 rings cleanly with explicit ring + donut mod, and Divine rerolls Variable rings.
        foreach (var (variant, expectedRing) in new[]
        {
            ("Small Ring", 1), ("Medium Ring", 2), ("Large Ring", 3), ("Very Large Ring", 4), ("Massive Ring", 5),
        })
        {
            var created = Assert.IsType<Item>(RadiusJewelCatalogue.Create(items, "Thread of Hope", variant: variant));
            Assert.DoesNotContain(created.Mods, m => m.Section == ModSection.Implicit);
            Assert.False(ItemModStats.Local(created, items.Mods).ContainsKey(Stat.LocalJewelEffectBaseRadius));
            var conv = Assert.IsType<RadiusJewelCatalogue.Conversion>(RadiusJewelCatalogue.ReadConversion(created, items.Mods));
            Assert.True(conv.AllocateDisconnected);
            Assert.Equal(expectedRing, conv.Ring);
            Assert.Equal("Thread of Hope", RadiusJewelCatalogue.EntryOf(created, items.Mods)?.Name);
        }

        var variable = Assert.IsType<Item>(RadiusJewelCatalogue.Create(items, "Thread of Hope", variant: "Small Ring"));
        var divinedRings = new HashSet<int>();
        for (int i = 0; i < 48; i++)
        {
            variable = Assert.IsType<Item>(rolls.Reforge(variable, rng, uniques.RollsOf(variable)));
            Assert.False(ItemModStats.Local(variable, items.Mods).ContainsKey(Stat.LocalJewelEffectBaseRadius));
            var conv = Assert.IsType<RadiusJewelCatalogue.Conversion>(RadiusJewelCatalogue.ReadConversion(variable, items.Mods));
            Assert.InRange(conv.Ring, 1, 4);
            divinedRings.Add(conv.Ring);
        }
        Assert.Equal([1, 2, 3, 4], divinedRings.OrderBy(r => r).ToArray());

        // The Gulf (Two-Implicit Corrupted Thread of Hope) adds two corrupted implicits while keeping the explicit ring and donut mod.
        var gulf = Assert.IsType<Item>(DivinationRewards.Create(
            DivinationRewards.Find("Metadata/Items/DivinationCards/DivinationCardTheGulf")!,
            items, uniques, rolls, new Random(77)));
        Assert.True(gulf.Corrupted);
        Assert.Equal(2, gulf.Mods.Count(m => m.Section == ModSection.Implicit));
        Assert.All(gulf.Mods.Where(m => m.Section == ModSection.Implicit), m => Assert.Equal(5, items.Mods.ByHash(m.Hash)?.GenerationType));
        Assert.False(ItemModStats.Local(gulf, items.Mods).ContainsKey(Stat.LocalJewelEffectBaseRadius));
        var gulfConv = Assert.IsType<RadiusJewelCatalogue.Conversion>(RadiusJewelCatalogue.ReadConversion(gulf, items.Mods));
        Assert.True(gulfConv.AllocateDisconnected);
        Assert.InRange(gulfConv.Ring, 1, 4);
    }

    [Fact]
    public void Thread_of_hope_ring_allocation_removal_refusal_and_legacy_migration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"toh-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        try
        {
            var items = Program.Items;
            var rolls = new ItemRolls(items);
            var uniques = new UniqueCatalog(items);
            var player = new Player(new(1, "toh", 100, 100, 100, 100, null, []), 1, 1)
                { Character = { Name = $"Toh{Guid.NewGuid():N}" } };
            var tree = Game.PassivesFor(player);
            byte slot = 3;
            ushort socket = tree.JewelNodeForSlot(slot)!.Value;
            var inventory = Game.ItemsOf(player);

            var large = Assert.IsType<Item>(RadiusJewelCatalogue.Create(items, "Thread of Hope", variant: "Large Ring"));
            var placed = new InventoryItem(10, slot, 0, large);
            inventory.Put(PlayerInventories.PassiveJewels, placed);

            var (inner, outer) = RadiusJewelCatalogue.RingRadii(3);
            var ringNodes = tree.NodesInRing(socket, inner, outer)
                .Where(n => tree.Find(n) is { IsAscendancy: false, IsStart: false, IsMastery: false,
                    IsProxy: false, IsExpansion: false, IsJewelSocket: false })
                .ToArray();
            Assert.NotEmpty(ringNodes);

            var bypass = Game.AllocationBypassNodes(player, [socket]);
            Assert.Equal(ringNodes.OrderBy(n => n), bypass.Keys.OrderBy(n => n));
            Assert.All(bypass.Keys, n => Assert.False(tree.Find(n)!.IsJewelSocket));
            // A node strictly inside the inner hole of the Large ring must not be bypassed.
            var holeNode = tree.NodesInRadius(socket, inner - 1)
                .First(n => tree.Find(n) is { IsAscendancy: false, IsStart: false, IsMastery: false,
                    IsProxy: false, IsExpansion: false, IsJewelSocket: false });
            Assert.DoesNotContain(holeNode, bypass.Keys);

            // Allocating a path to the socket plus a disconnected ring node passes validation and blocks jewel removal.
            byte baseClass = (byte)Game.SkillsFor(player).ClassFor(player.Character.ClassRow).IntegerId;
            var prev = new Dictionary<ushort, ushort>();
            var queue = new Queue<ushort>();
            foreach (ushort seed in Game.OrdinaryPassives.Frontier(baseClass, AscendancyFamily.Normal, []))
                if (prev.TryAdd(seed, 0)) queue.Enqueue(seed);
            while (queue.Count > 0 && !prev.ContainsKey(socket))
            {
                ushort cur = queue.Dequeue();
                foreach (ushort next in Game.OrdinaryPassives.Adjacent(cur, AscendancyFamily.Normal))
                    if (tree.Find(next) is { IsStart: false, IsAscendancy: false, IsMastery: false, IsProxy: false, IsExpansion: false }
                        && prev.TryAdd(next, cur))
                        queue.Enqueue(next);
            }
            Assert.True(prev.ContainsKey(socket));
            var connectedToSocket = new List<ushort>();
            for (ushort cur = socket; cur != 0; cur = prev[cur]) connectedToSocket.Add(cur);
            ushort disconnectedRingNode = ringNodes.First(n => !connectedToSocket.Contains(n)
                && !Game.OrdinaryPassives.Adjacent(n, AscendancyFamily.Normal).Any(connectedToSocket.Contains));
            player.Character.Passives = [.. connectedToSocket, disconnectedRingNode];
            Game.ValidateOrdinaryPassives(player, player.Character.Passives, player.Character.MainAscendancy);
            Assert.NotNull(Game.AllocationBypassRemovalRefusal(player, placed));

            // A disconnected node inside the ring's inner hole must be refused by validation.
            ushort disconnectedHoleNode = tree.NodesInRadius(socket, inner - 1)
                .First(n => tree.Find(n) is { IsAscendancy: false, IsStart: false, IsMastery: false,
                    IsProxy: false, IsExpansion: false, IsJewelSocket: false }
                    && !connectedToSocket.Contains(n)
                    && !Game.OrdinaryPassives.Adjacent(n, AscendancyFamily.Normal).Any(connectedToSocket.Contains));
            Assert.Throws<AscendancyRuleException>(() =>
                Game.ValidateOrdinaryPassives(player, [.. connectedToSocket, disconnectedHoleNode], player.Character.MainAscendancy));

            // Refunding the disconnected node allows removing the jewel.
            player.Character.Passives = [.. connectedToSocket];
            Assert.Null(Game.AllocationBypassRemovalRefusal(player, placed));

            // Legacy saved unique Thread of Hope (Explicit Massive + Intuitive Leap 800 mod) migrates cleanly on load.
            var legacyUnique = large with
            {
                Mods =
                [
                    new ItemMod(ModSection.Explicit, items.Mods.ById("JewelRingRadiusValuesUnique__2")!.Hash, [5]),
                    new ItemMod(ModSection.Explicit, items.Mods.ById("JewelUniqueAllocateDisconnectedPassives")!.Hash, [1, 800]),
                    new ItemMod(ModSection.Explicit, items.Mods.ById("AllResistancesUnique__6")!.Hash, [-15]),
                ],
            };
            var migratedUnique = CharacterSaves.FromSaved(CharacterSaves.ToSaved(legacyUnique, items.Mods), items);
            Assert.DoesNotContain(migratedUnique.Mods, m => m.Section == ModSection.Implicit);
            Assert.Contains(migratedUnique.Mods, m => items.Mods.ByHash(m.Hash)?.Id == "AllocateDisconnectedPassivesDonutUnique__1");
            Assert.DoesNotContain(migratedUnique.Mods, m => items.Mods.ByHash(m.Hash)?.Id == "JewelUniqueAllocateDisconnectedPassives");
            Assert.False(ItemModStats.Local(migratedUnique, items.Mods).ContainsKey(Stat.LocalJewelEffectBaseRadius));
            Assert.Equal(5, RadiusJewelCatalogue.ReadConversion(migratedUnique, items.Mods)?.Ring);
            Assert.Equal(-15, ItemModStats.Global([migratedUnique], items.Mods, Program.Stats)[Stat.BaseResistAllElementsPct]);

            // Legacy /radius_jewel Thread of Hope (Implicit ring + zeroed Intuitive Leap mod) migrates to explicit ring + donut mod.
            var legacyRadius = large with
            {
                Mods =
                [
                    new ItemMod(ModSection.Implicit, items.Mods.ById("JewelRingRadiusValuesUnique__1")!.Hash, [3]),
                    new ItemMod(ModSection.Explicit, items.Mods.ById("JewelUniqueAllocateDisconnectedPassives")!.Hash, [1, 0]),
                    new ItemMod(ModSection.Explicit, items.Mods.ById("AllResistancesUnique__6")!.Hash, [-10]),
                ],
            };
            var migratedRadius = CharacterSaves.FromSaved(CharacterSaves.ToSaved(legacyRadius, items.Mods), items);
            Assert.DoesNotContain(migratedRadius.Mods, m => m.Section == ModSection.Implicit);
            Assert.Equal(3, RadiusJewelCatalogue.ReadConversion(migratedRadius, items.Mods)?.Ring);
            var divinedAfterMigration = Assert.IsType<Item>(rolls.Reforge(migratedRadius, new Random(9), uniques.RollsOf(migratedRadius)));
            Assert.False(ItemModStats.Local(divinedAfterMigration, items.Mods).ContainsKey(Stat.LocalJewelEffectBaseRadius));

            // Intuitive Leap (no ring mod) must NOT be rewritten by Thread of Hope migration.
            var intuitiveLeap = Assert.IsType<Item>(uniques.Create("Intuitive Leap", new Random(5)));
            var reloadedLeap = CharacterSaves.FromSaved(CharacterSaves.ToSaved(intuitiveLeap, items.Mods), items);
            Assert.Contains(reloadedLeap.Mods, m => items.Mods.ByHash(m.Hash)?.Id == "JewelUniqueAllocateDisconnectedPassives");
            Assert.Equal(800, ItemModStats.Local(reloadedLeap, items.Mods)[Stat.LocalJewelEffectBaseRadius]);
        }
        finally
        {
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
