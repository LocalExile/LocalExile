using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using System.Net;
using System.Net.Sockets;

namespace GameServer.Gateway.Tests;

public class PassiveJewelTests
{
    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public readonly TcpClient Writer = new(), Reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], isClient: false);
        public Wire()
        {
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            Writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            Reader = accepting.GetAwaiter().GetResult();
        }
        public void Dispose() { Writer.Dispose(); Reader.Dispose(); listener.Stop(); }
    }

    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Fact]
    public void The_pane_slots_map_to_the_ordinary_jewel_sockets()
    {
        var tree = new PassiveTree(Data());
        Assert.Equal((ushort)26725, tree.JewelNodeForSlot(0));       // jewel_slot1956, the first normal socket
        Assert.Equal(0, tree.JewelSlotForNode(26725));
        Assert.Equal((ushort)21984, tree.JewelNodeForSlot(20));      // jewel_slot1979, the last normal socket
        Assert.Null(tree.JewelNodeForSlot(21));                      // the first cluster jewel slot
        Assert.True(tree.IsJewelSocket(26725));
    }

    [Fact]
    public void Cluster_jewels_are_not_normal_jewels()
    {
        Assert.True(Program.IsNormalJewel(ItemCatalog.Create(Program.Items.Find("Prismatic Jewel")!)));
        Assert.False(Program.IsNormalJewel(ItemCatalog.Create(Program.Items.Find("Large Cluster Jewel")!)));
    }

    [Fact]
    public void A_jewel_in_an_allocated_socket_adds_its_global_mods()
    {
        TestSaves.Ensure();
        string character = "PassiveJewel" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            var mods = new ModCatalog(Data());
            var jewel = ItemCatalog.Create(Program.Items.Find("Prismatic Jewel")!) with
            {
                Mods = [mods.ById("FireResist1")!.With(ModSection.Explicit, [15], Random.Shared)],
            };
            var items = PlayerInventories.For(character, Program.Items);
            items.Add(PlayerInventories.PassiveJewels, 0, 0, jewel);
            conn.Passives = [Program.Passives.JewelNodeForSlot(0)!.Value];
            int withJewel = Program.PoolsFor(conn, 1).FireResistance;
            var placed = items.Items(PlayerInventories.PassiveJewels).Single();
            items.Remove(PlayerInventories.PassiveJewels, placed.Id);
            int withoutJewel = Program.PoolsFor(conn, 1).FireResistance;
            Assert.Equal(15, withJewel - withoutJewel);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void A_percentage_strength_jewel_scales_the_character_pools()
    {
        TestSaves.Ensure();
        string character = "StrengthJewel" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [Program.Passives.JewelNodeForSlot(0)!.Value];
            conn.Pools = Program.PoolsFor(conn, 20);
            int before = conn.Pools.Strength;
            var items = PlayerInventories.For(character, Program.Items);
            var jewel = ItemCatalog.Create(Program.Items.Find("Prismatic Jewel")!) with
            {
                Mods = [new ItemMod(ModSection.Explicit,
                    Program.Items.Mods.ById("PercentageStrengthUniqueJewel29")!.Hash, [100])],
            };
            items.Add(PlayerInventories.PassiveJewels, 0, 0, jewel);
            conn.Pools = Program.PoolsFor(conn, 20);
            var shown = Program.CharacterPlayerStats(conn);
            uint percentKey = Program.Stats.Key("strength_+%")!.Value;
            Assert.Equal(100, shown.GetValueOrDefault(percentKey));
            Assert.True(shown.ContainsKey(percentKey));
            Assert.Equal((int)Math.Floor(before * 2.0), conn.Pools.Strength);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void A_jewel_with_additional_block_raises_attack_and_spell_block()
    {
        // Reckless Defence and Replica Reckless Defence: +6% additional chance to Block Attack Damage and to Block
        // Spell Damage (max rolls). The spell one used to be unread, so only the attack half moved the pools.
        TestSaves.Ensure();
        string character = "BlockJewel" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [Program.Passives.JewelNodeForSlot(0)!.Value];
            conn.Pools = Program.PoolsFor(conn, 20);
            int attackBefore = conn.Pools.BlockChanceUncapped, spellBefore = conn.Pools.SpellBlockChanceUncapped;
            var items = PlayerInventories.For(character, Program.Items);
            items.Add(PlayerInventories.PassiveJewels, 0, 0, Program.CreateUniqueJewel("Reckless Defence")!);
            conn.Pools = Program.PoolsFor(conn, 20);
            Assert.Equal(6, conn.Pools.BlockChanceUncapped - attackBefore);
            Assert.Equal(6, conn.Pools.SpellBlockChanceUncapped - spellBefore);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void The_tree_knows_which_sockets_take_large_cluster_jewels()
    {
        var tree = new PassiveTree(Data());
        var large = Enumerable.Range(0, 21).Where(tree.IsLargeJewelSlot).ToArray();
        Assert.Equal([11, 12, 13, 15, 17, 20], large);
        Assert.False(tree.IsLargeJewelSlot(21));   // an expansion cell, not a tree socket
        Assert.Equal((ushort)7960, tree.JewelNodeForSlot(11));
    }

    [Fact]
    public void A_large_cluster_socket_creates_three_medium_expansion_cells()
    {
        var tree = new PassiveTree(Data());
        Assert.Equal([21, 22, 23], tree.ClusterSlots(11));
        Assert.Equal((ushort)29712, tree.ClusterNodeForSlot(21));
        Assert.Equal((ushort)48679, tree.ClusterNodeForSlot(22));
        Assert.Equal((ushort)9408, tree.ClusterNodeForSlot(23));
        // A medium expansion cell creates one small one, and an ordinary socket creates nothing.
        Assert.Equal([24], tree.ClusterSlots(21));
        Assert.Empty(tree.ClusterSlots(0));
    }

    [Fact]
    public void Voices_comes_in_the_four_empty_passive_variants()
    {
        foreach (int empty in new[] { 1, 3, 5, 7 })
        {
            var jewel = Program.CreateVoices(empty);
            Assert.NotNull(jewel);
            Assert.Equal(ItemRarity.Unique, jewel!.Rarity);
            Assert.True(jewel.Corrupted);
            Assert.Equal(Program.LargeClusterJewelId, Program.Items.ByHash(jewel.TypeHash)!.Id);
            Assert.Equal(Program.Items.Visual("UniqueJewel147"), jewel.VisualIdentity);
            Assert.Equal(UniqueJewelVisuals.Of("Voices"), jewel.VisualIdentity);
            Assert.Equal((ushort)44654, jewel.VisualIdentity);
            var stats = jewel.Mods.SelectMany(m => Program.Items.Mods.ByHash(m.Hash)!.Stats).ToList();
            Assert.Contains(stats, s => s.Id == "local_jewel_expansion_jewels_count_override" && jewel.Mods[0].Values[0] == 3);
            Assert.Contains(stats, s => s.Id == "local_unique_jewel_grants_x_empty_passives");
            Assert.Equal(empty, jewel.Mods[1].Values[0]);
        }
        Assert.Null(Program.CreateVoices(2));
        var voices = Program.CreateVoices(7)!;
        Assert.False(Program.IsNormalJewel(voices));
        Assert.True(Program.IsLargeClusterJewel(voices));
    }

    [Fact]
    public void Every_unique_jewel_has_its_own_art()
    {
        // The generated map covers every unique jewel the stash layout lists, not only the Global catalogue, so the
        // cluster, radius and timeless jewels get their icon as soon as their mechanic exists.
        Assert.Equal((ushort)44654, UniqueJewelVisuals.Of("Voices"));
        Assert.Equal((ushort)32130, UniqueJewelVisuals.Of("The Adorned"));
        Assert.NotNull(UniqueJewelVisuals.Of("Megalomaniac"));
        Assert.Null(UniqueJewelVisuals.Of("No Such Jewel"));
        foreach (var entry in UniqueJewelCatalogue.Global)
            Assert.True(UniqueJewelVisuals.Of(entry.Name) is not null, entry.Name);
    }

    [Fact]
    public void A_jewel_in_a_cluster_socket_counts_and_stops_when_the_cluster_leaves()
    {
        TestSaves.Ensure();
        string character = "ClusterJewel" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            // Pane cell 20 is the large tree socket; a large cluster jewel there opens its three expansion cells (51-53).
            conn.Passives = [Program.Passives.JewelNodeForSlot(20)!.Value];
            var items = PlayerInventories.For(character, Program.Items);
            items.Add(PlayerInventories.PassiveJewels, 20, 0, Program.CreateVoices(1)!);
            var mods = new ModCatalog(Data());
            var jewel = ItemCatalog.Create(Program.Items.Find("Prismatic Jewel")!) with
            {
                Mods = [mods.ById("FireResist1")!.With(ModSection.Explicit, [15], Random.Shared)],
            };
            items.Add(PlayerInventories.PassiveJewels, 51, 0, jewel);
            Assert.True(Program.ClusterSlotOpen(conn, 51));
            Assert.Equal(15, Program.PoolsFor(conn, 1).FireResistance);
            // Without the cluster jewel the cell closes and its jewel stops counting.
            var voices = items.Items(PlayerInventories.PassiveJewels).First(i => i.X == 20);
            items.Remove(PlayerInventories.PassiveJewels, voices.Id);
            Assert.False(Program.ClusterSlotOpen(conn, 51));
            Assert.Equal(0, Program.PoolsFor(conn, 1).FireResistance);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void Cluster_jewels_fit_a_socket_by_size()
    {
        var large = ItemCatalog.Create(Program.Items.Find("Large Cluster Jewel")!) with { Rarity = ItemRarity.Unique };
        var medium = ItemCatalog.Create(Program.Items.Find("Medium Cluster Jewel")!) with { Rarity = ItemRarity.Magic };
        var small = ItemCatalog.Create(Program.Items.Find("Small Cluster Jewel")!) with { Rarity = ItemRarity.Normal };
        var jewel = ItemCatalog.Create(Program.Items.Find("Prismatic Jewel")!);
        Assert.Equal(2, Program.ClusterSize(large));
        Assert.Equal(1, Program.ClusterSize(medium));
        Assert.Equal(0, Program.ClusterSize(small));
        Assert.Null(Program.ClusterSize(jewel));
        // A cluster jewel fits a socket of its own size or a larger one (the client pads the cell with empty nodes);
        // a normal jewel fits everywhere, a basic socket included.
        Assert.True(Program.JewelFitsCell(11, large));
        Assert.True(Program.JewelFitsCell(11, medium));
        Assert.True(Program.JewelFitsCell(11, small));
        Assert.True(Program.JewelFitsCell(0, jewel));
        Assert.False(Program.JewelFitsCell(0, small));
        Assert.True(Program.JewelFitsCell(51, medium));
        Assert.True(Program.JewelFitsCell(51, small));
        Assert.True(Program.JewelFitsCell(51, jewel));
        Assert.False(Program.JewelFitsCell(51, large));
        Assert.True(Program.JewelFitsCell(54, small));
        Assert.True(Program.JewelFitsCell(54, jewel));
        Assert.False(Program.JewelFitsCell(54, medium));
    }

    [Fact]
    public void A_medium_cluster_jewel_opens_its_small_socket()
    {
        TestSaves.Ensure();
        string character = "ClusterNest" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [Program.Passives.JewelNodeForSlot(20)!.Value];
            var items = PlayerInventories.For(character, Program.Items);
            items.Add(PlayerInventories.PassiveJewels, 20, 0, Program.CreateVoices(1)!);
            var medium = ItemCatalog.Create(Program.Items.Find("Medium Cluster Jewel")!) with { Rarity = ItemRarity.Magic };
            items.Add(PlayerInventories.PassiveJewels, 51, 0, medium);
            Assert.True(Program.ClusterSlotOpen(conn, 51));
            Assert.True(Program.ClusterSlotOpen(conn, 54));   // the medium jewel's own small socket
            Assert.False(Program.ClusterSlotOpen(conn, 55));  // its sibling's, empty
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void A_small_cluster_in_a_medium_socket_is_mapped_on_half_the_ring()
    {
        TestSaves.Ensure();
        string character = "ClusterSmallLayout" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [Program.Passives.JewelNodeForSlot(20)!.Value];
            var items = PlayerInventories.For(character, Program.Items);
            items.Add(PlayerInventories.PassiveJewels, 20, 0, Program.CreateClusterJewel(2, 8, 2)!);
            items.Add(PlayerInventories.PassiveJewels, 51, 0, Program.CreateClusterJewel(1, 4, 1)!);
            int cell = Program.Passives.ClusterSlots(51)[0];
            items.Add(PlayerInventories.PassiveJewels, (byte)cell, 0,
                Program.CreateClusterJewel(0, 3, 0, "AfflictionJewelSmallPassivesGrantLife2_")!);
            var small = items.Items(PlayerInventories.PassiveJewels).First(i => i.X == cell).Item;
            var layout = ClusterJewelLayout.For(cell, small)!;
            // Cell 54: base 207 + 6, start 2; with no notable the three ring positions (0, 2, 4) are theme smalls.
            Assert.Equal(213, layout.Base);
            Assert.Equal(ClusterJewelLayout.Kind.Small, layout.ByOffset[0]);
            Assert.Equal(ClusterJewelLayout.Kind.Small, layout.ByOffset[1]);
            Assert.Equal(ClusterJewelLayout.Kind.Small, layout.ByOffset[2]);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void A_small_cluster_with_notables_puts_one_notable_and_two_theme_smalls()
    {
        TestSaves.Ensure();
        string character = "ClusterSmallNotables" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [Program.Passives.JewelNodeForSlot(20)!.Value];
            var items = PlayerInventories.For(character, Program.Items);
            items.Add(PlayerInventories.PassiveJewels, 20, 0, Program.CreateClusterJewel(2, 8, 2)!);
            items.Add(PlayerInventories.PassiveJewels, 51, 0, Program.CreateClusterJewel(1, 4, 1)!);
            var small = Program.CreateClusterJewel(0, 3, 0, "AfflictionJewelSmallPassivesGrantLife2_",
                ["AfflictionNotableFettle", "AfflictionNotableProdigiousDefense__", "AfflictionNotableAdvanceGuard"])!;
            items.Add(PlayerInventories.PassiveJewels, 54, 0, small);
            var layout = ClusterJewelLayout.For(54, small)!;
            // The client's measured small graph: base 213, the first notable at 213 and the two theme smalls at 214/215.
            Assert.Equal(213, layout.Base);
            Assert.Equal(ClusterJewelLayout.Kind.Notable, layout.ByOffset[0]);
            Assert.Equal(ClusterJewelLayout.Kind.Small, layout.ByOffset[1]);
            Assert.Equal(ClusterJewelLayout.Kind.Small, layout.ByOffset[2]);
            conn.ClusterPassives = [];
            int none = (int)Program.PoolsFor(conn, 1).Life;
            // Two theme smalls (7 life each) and Prodigious Defence, which has no life.
            conn.ClusterPassives = [213, 214, 215];
            Assert.Equal(none + 14, (int)Program.PoolsFor(conn, 1).Life);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void A_small_cluster_in_a_large_tree_socket_uses_its_own_ring()
    {
        TestSaves.Ensure();
        string character = "ClusterSmallTree" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [Program.Passives.JewelNodeForSlot(17)!.Value];
            var items = PlayerInventories.For(character, Program.Items);
            var small = Program.CreateClusterJewel(0, 3, 0, "AfflictionJewelSmallPassivesGrantLife2_",
                ["AfflictionNotableFettle", "AfflictionNotableProdigiousDefense__", "AfflictionNotableAdvanceGuard"])!;
            items.Add(PlayerInventories.PassiveJewels, 17, 0, small);
            var layout = ClusterJewelLayout.For(17, small)!;
            // Cell 17 (ClusterIndex 4) has base 156 and keeps the full ring; the small's own 6-ring indices 0/2/4 with
            // the cell's start 5 give smalls 157/161 and the first notable 159.
            Assert.Equal(156, layout.Base);
            Assert.Equal(ClusterJewelLayout.Kind.Small, layout.ByOffset[1]);
            Assert.Equal(ClusterJewelLayout.Kind.Notable, layout.ByOffset[3]);
            Assert.Equal(ClusterJewelLayout.Kind.Small, layout.ByOffset[5]);
            conn.ClusterPassives = [];
            int none = (int)Program.PoolsFor(conn, 1).Life;
            // Two theme smalls (7 life each) and Prodigious Defence, which has no life.
            conn.ClusterPassives = [157, 159, 161];
            Assert.Equal(none + 14, (int)Program.PoolsFor(conn, 1).Life);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void A_nested_cluster_leaves_while_the_outer_jewel_keeps_its_passives()
    {
        TestSaves.Ensure();
        string character = "ClusterNestedLeave" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100, RefundPoints = 1 };
            conn.Passives = [Program.Passives.JewelNodeForSlot(20)!.Value];
            var items = PlayerInventories.For(character, Program.Items);
            items.Add(PlayerInventories.PassiveJewels, 20, 0, Program.CreateClusterJewel(2, 8, 2)!);
            items.Add(PlayerInventories.PassiveJewels, 51, 0, Program.CreateClusterJewel(1, 4, 1)!);
            var outer = items.Items(PlayerInventories.PassiveJewels).First(i => i.X == 20);
            var nested = items.Items(PlayerInventories.PassiveJewels).First(i => i.X == 51);
            // The outer jewel has every passive allocated: that is no reason to keep the nested jewel in.
            conn.ClusterPassives = [195, 196, 197, 198, 200, 202, 204, 206];
            Assert.Null(Program.ClusterRemovalRefusal(conn, nested));
            // Its own socket is a node of the nested jewel, so it blocks the removal until refunded.
            conn.ClusterPassives = [212];
            Assert.Equal("refund the cluster jewel's passives first", Program.ClusterRemovalRefusal(conn, nested));
            // A stale id the client no longer builds (230 belongs to no mapped position) does not block it.
            conn.ClusterPassives = [195, 196, 197, 198, 200, 202, 204, 206, 230];
            Assert.Null(Program.ClusterRemovalRefusal(conn, nested));
            conn.ClusterPassives = [195, 196, 197, 198, 200, 202, 204, 206];
            // The outer jewel still cannot leave over the nested one, and then over its own passives.
            Assert.Equal("take the jewels out of the cluster first", Program.ClusterRemovalRefusal(conn, outer));
            items.Remove(PlayerInventories.PassiveJewels, nested.Id);
            Assert.Equal("refund the cluster jewel's passives first", Program.ClusterRemovalRefusal(conn, outer));
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void A_cluster_jewel_opens_only_the_sockets_it_has()
    {
        TestSaves.Ensure();
        string character = "ClusterSockets" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [Program.Passives.JewelNodeForSlot(20)!.Value];
            var items = PlayerInventories.For(character, Program.Items);
            // Voices adds three sockets (its own override); a plain large cluster jewel has two.
            items.Add(PlayerInventories.PassiveJewels, 20, 0, Program.CreateVoices(1)!);
            Assert.Equal(3, Program.ClusterSocketCount(Program.CreateVoices(7)!));
            Assert.True(Program.ClusterSlotOpen(conn, 53));
            var voices = items.Items(PlayerInventories.PassiveJewels).Single();
            items.Remove(PlayerInventories.PassiveJewels, voices.Id);
            var plain = ItemCatalog.Create(Program.Items.Find("Large Cluster Jewel")!) with { Rarity = ItemRarity.Rare };
            items.Add(PlayerInventories.PassiveJewels, 20, 0, plain);
            Assert.Equal(2, Program.ClusterSocketCount(plain));
            // The client's socket-to-cell numbering does not follow the socket count: it offers cell 53 for a
            // two-socket large jewel too, so the server accepts any of the parent's expansion cells and lets the
            // client's layout decide which one it opens.
            Assert.True(Program.ClusterSlotOpen(conn, 51));
            Assert.True(Program.ClusterSlotOpen(conn, 52));
            Assert.True(Program.ClusterSlotOpen(conn, 53));
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void A_made_cluster_jewel_carries_its_enchantments()
    {
        var medium = Program.CreateClusterJewel(1, 4, 1)!;
        Assert.Equal("Medium Cluster Jewel", Program.Items.ByHash(medium.TypeHash)!.Name);
        var stats = medium.Mods.SelectMany(m => Program.Items.Mods.ByHash(m.Hash)!.Stats).Select(s => s.Id).ToList();
        Assert.Contains("local_jewel_expansion_passive_node_count", stats);
        Assert.Contains("local_jewel_expansion_jewels_count", stats);
        Assert.Contains(stats, s => s.StartsWith("local_affliction_jewel_small_nodes_grant_", StringComparison.Ordinal));
        // A small cluster jewel has no socket enchantment.
        var small = Program.CreateClusterJewel(0, 3, 0)!;
        Assert.DoesNotContain(small.Mods.SelectMany(m => Program.Items.Mods.ByHash(m.Hash)!.Stats),
            s => s.Id == "local_jewel_expansion_jewels_count");
        // Notables come as explicit prefixes ("1 Added Passive Skill is ...").
        var withNotable = Program.CreateClusterJewel(2, 8, 2, null, ["AfflictionNotableProdigiousDefense__"])!;
        Assert.Contains(withNotable.Mods, m => Program.Items.Mods.ByHash(m.Hash)!.Stats.Any(
            s => s.Id == "local_affliction_notable_prodigious_defense"));
    }

    [Fact]
    public void A_cluster_jewel_cannot_leave_while_jewels_are_inside_it()
    {
        TestSaves.Ensure();
        string character = "ClusterLeave" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100, RefundPoints = 1 };
            conn.Passives = [Program.Passives.JewelNodeForSlot(20)!.Value];
            var items = PlayerInventories.For(character, Program.Items);
            var voices = Program.CreateVoices(1)!;
            items.Add(PlayerInventories.PassiveJewels, 20, 0, voices);
            var placed = items.Items(PlayerInventories.PassiveJewels).Single();
            Assert.Null(Program.ClusterRemovalRefusal(conn, placed));
            items.Add(PlayerInventories.PassiveJewels, 51, 0, ItemCatalog.Create(Program.Items.Find("Prismatic Jewel")!)!);
            Assert.Equal("take the jewels out of the cluster first", Program.ClusterRemovalRefusal(conn, placed));
            // With the jewel gone, the removal only needs its refund point.
            var inner = items.Items(PlayerInventories.PassiveJewels).First(i => i.X == 51);
            items.Remove(PlayerInventories.PassiveJewels, inner.Id);
            Assert.Null(Program.ClusterRemovalRefusal(conn, placed));
            conn.RefundPoints = 0;
            Assert.Contains("refund point", Program.ClusterRemovalRefusal(conn, placed)!);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void Removing_a_cluster_jewel_spends_one_refund_point()
    {
        TestSaves.Ensure();
        string character = "ClusterRefund" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100, RefundPoints = 2 };
            Program.SpendClusterRemoval(conn);
            Assert.Equal((ushort)1, conn.RefundPoints);
            // The cluster's own nodes are NOT refunded yet: a tree message that drops them crashes the client, so they
            // stay with the jewel until the client's node ids are mapped (Agents/docs/game/cluster-jewels.md).
            conn.ClusterPassives = [196, 198];
            Assert.Equal(2, conn.ClusterPassives.Count);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void A_cluster_jewel_of_any_size_that_is_socketed_has_allocatable_nodes()
    {
        TestSaves.Ensure();
        string character = "ClusterAnySize" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [Program.Passives.JewelNodeForSlot(20)!.Value];
            var items = PlayerInventories.For(character, Program.Items);
            Assert.False(Program.ClusterJewelSocketed(conn));
            // A normal jewel is not a cluster jewel, whatever its socket.
            items.Add(PlayerInventories.PassiveJewels, 20, 0, ItemCatalog.Create(Program.Items.Find("Prismatic Jewel")!)!);
            Assert.False(Program.ClusterJewelSocketed(conn));
            var placed = items.Items(PlayerInventories.PassiveJewels).Single();
            items.Remove(PlayerInventories.PassiveJewels, placed.Id);
            // Pane cell 20 is a large tree socket: it takes a large cluster jewel.
            items.Add(PlayerInventories.PassiveJewels, 20, 0, Program.CreateClusterJewel(2, 8, 2)!);
            Assert.True(Program.ClusterJewelSocketed(conn));
            // A cluster jewel nested in another cluster jewel's socket counts as well.
            int mediumCell = Program.Passives.ClusterSlots(20)[0];
            items.Add(PlayerInventories.PassiveJewels, (byte)mediumCell, 0, Program.CreateClusterJewel(1, 4, 1)!);
            int smallCell = Program.Passives.ClusterSlots(mediumCell)[0];
            items.Add(PlayerInventories.PassiveJewels, (byte)smallCell, 0, Program.CreateClusterJewel(0, 2, 0)!);
            Assert.True(Program.ClusterJewelSocketed(conn));
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void A_cluster_jewels_node_budget_counts_its_sockets_as_passives()
    {
        TestSaves.Ensure();
        string character = "ClusterNodes" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [Program.Passives.JewelNodeForSlot(20)!.Value];
            var items = PlayerInventories.For(character, Program.Items);
            Assert.Equal(0, Program.ClusterNodeBudget(conn));
            // "Adds N Passive Skills" counts the sockets too: an 8-passive large jewel with 2 sockets is 8 nodes, of
            // which 6 are small/notable passives.
            var large = Program.CreateClusterJewel(2, 8, 2)!;
            Assert.Equal(6, Program.ClusterPassiveCount(large));
            Assert.Equal(2, Program.ClusterSocketCount(large));
            items.Add(PlayerInventories.PassiveJewels, 20, 0, large);
            Assert.Equal(8, Program.ClusterNodeBudget(conn));
            var placed = items.Items(PlayerInventories.PassiveJewels).Single();
            items.Remove(PlayerInventories.PassiveJewels, placed.Id);
            var medium = Program.CreateClusterJewel(1, 4, 1)!;
            Assert.Equal(3, Program.ClusterPassiveCount(medium));
            items.Add(PlayerInventories.PassiveJewels, 20, 0, medium);
            Assert.Equal(4, Program.ClusterNodeBudget(conn));
            placed = items.Items(PlayerInventories.PassiveJewels).Single();
            items.Remove(PlayerInventories.PassiveJewels, placed.Id);
            // Voices: 7 empty passives and its three sockets.
            items.Add(PlayerInventories.PassiveJewels, 20, 0, Program.CreateVoices(7)!);
            Assert.Equal(10, Program.ClusterNodeBudget(conn));
            // The theme is an enchantment, like the count and the sockets ("Added Small Passive Skills grant").
            Assert.Equal(3, medium.Mods.Count(m => m.Section == ModSection.Enchant));
            // A jewel's only affixes are its notables: none makes it Normal (a magic item with no affix makes the
            // client draw the whole cluster as empty "Nothingness" nodes), one or two Magic, three Rare.
            Assert.Equal(ItemRarity.Normal, Program.CreateClusterJewel(2, 8, 2)!.Rarity);
            Assert.Equal(ItemRarity.Magic,
                Program.CreateClusterJewel(2, 8, 2, null, ["AfflictionNotableProdigiousDefense__"])!.Rarity);
            Assert.Equal(ItemRarity.Rare, Program.CreateClusterJewel(2, 8, 2, null,
                ["AfflictionNotableProdigiousDefense__", "AfflictionNotableAdvanceGuard",
                 "AfflictionNotableGladiatorialCombat"])!.Rarity);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void A_tree_socket_cluster_gives_the_theme_on_its_smalls_as_they_are_taken()
    {
        TestSaves.Ensure();
        string character = "ClusterTheme" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [Program.Passives.JewelNodeForSlot(20)!.Value];
            var items = PlayerInventories.For(character, Program.Items);
            // The default theme is the low life tier (2-3, max-rolled 3). Cell 20's 8/2 layout has smalls at 195, 196,
            // 197, 200, 202 and 204 and sockets at 198 and 206.
            items.Add(PlayerInventories.PassiveJewels, 20, 0, Program.CreateClusterJewel(2, 8, 2)!);
            int none = (int)Program.PoolsFor(conn, 1).Life;
            conn.ClusterPassives = [198];
            Assert.Equal(none, (int)Program.PoolsFor(conn, 1).Life);
            conn.ClusterPassives = [195];
            Assert.Equal(none + 3, (int)Program.PoolsFor(conn, 1).Life);
            conn.ClusterPassives = [195, 197];
            Assert.Equal(none + 6, (int)Program.PoolsFor(conn, 1).Life);
            conn.ClusterPassives = [195, 197, 196];
            Assert.Equal(none + 9, (int)Program.PoolsFor(conn, 1).Life);
            conn.ClusterPassives = [195, 196, 197, 198, 200, 202, 204, 206];
            Assert.Equal(none + 18, (int)Program.PoolsFor(conn, 1).Life);
            conn.ClusterPassives = [];
            Assert.Equal(none, (int)Program.PoolsFor(conn, 1).Life);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void Nested_cluster_jewels_both_give_their_theme()
    {
        TestSaves.Ensure();
        string character = "ClusterNestedStats" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [Program.Passives.JewelNodeForSlot(20)!.Value];
            var items = PlayerInventories.For(character, Program.Items);
            // An outer large cluster (theme 2-3 life, 6 smalls) with a medium one in its first socket (4-7, 3 smalls).
            items.Add(PlayerInventories.PassiveJewels, 20, 0, Program.CreateClusterJewel(2, 8, 2)!);
            int mediumCell = Program.Passives.ClusterSlots(20)[0];
            items.Add(PlayerInventories.PassiveJewels, (byte)mediumCell, 0,
                Program.CreateClusterJewel(1, 4, 1, "AfflictionJewelSmallPassivesGrantLife2_")!);
            Assert.Equal(12, Program.ClusterNodeBudget(conn));
            conn.ClusterPassives = [];
            int none = (int)Program.PoolsFor(conn, 1).Life;
            // The outer jewel fully allocated: its six small passives are 195,196,197,200,202,204 (the cell-20
            // layout), 6 x 3.
            conn.ClusterPassives = [195, 196, 197, 198, 200, 202, 204, 206];
            Assert.Equal(none + 18, (int)Program.PoolsFor(conn, 1).Life);
            // The medium's ids are not mapped, so it counts when its own four nodes are all allocated: 3 smalls x 7.
            conn.ClusterPassives = [195, 196, 197, 198, 200, 202, 204, 206, 208, 209, 210, 211];
            Assert.Equal(none + 39, (int)Program.PoolsFor(conn, 1).Life);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void A_fully_allocated_cluster_adds_its_notables_on_top_of_the_theme()
    {
        TestSaves.Ensure();
        string character = "ClusterStats" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [Program.Passives.JewelNodeForSlot(20)!.Value];
            var items = PlayerInventories.For(character, Program.Items);
            var jewel = Program.CreateClusterJewel(2, 8, 2, "AfflictionJewelSmallPassivesGrantLife2_",
                ["AfflictionNotableFettle"])!;
            items.Add(PlayerInventories.PassiveJewels, 20, 0, jewel);
            conn.ClusterPassives = [];
            int none = (int)Program.PoolsFor(conn, 1).Life;
            // The smalls of this 8/2/1 layout are 195,197,200,202,204 (5 x 7 life); the notable is 196 and its row
            // (+20 life, +10% maximum life) comes once all 6 passives are allocated.
            conn.ClusterPassives = [195];
            Assert.Equal(none + 7, (int)Program.PoolsFor(conn, 1).Life);
            // The notable sits at 196 and applies its own row as soon as it is allocated.
            conn.ClusterPassives = [195, 196];
            Assert.True((int)Program.PoolsFor(conn, 1).Life - none >= 7 + 20,
                $"expected the small and Fettle, got {(int)Program.PoolsFor(conn, 1).Life - none}");
            conn.ClusterPassives = [195, 196, 197, 198, 200, 202, 204, 206];
            int full = (int)Program.PoolsFor(conn, 1).Life;
            Assert.True(full - none >= 35 + 20, $"expected the theme and Fettle, got {full - none}");
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void A_medium_cluster_counts_once_its_passives_are_allocated()
    {
        TestSaves.Ensure();
        string character = "ClusterMediumLayout" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [Program.Passives.JewelNodeForSlot(20)!.Value];
            var items = PlayerInventories.For(character, Program.Items);
            items.Add(PlayerInventories.PassiveJewels, 20, 0, Program.CreateClusterJewel(2, 8, 2)!);
            int cell = Program.Passives.ClusterSlots(20)[0];
            items.Add(PlayerInventories.PassiveJewels, (byte)cell, 0,
                Program.CreateClusterJewel(1, 4, 1, "AfflictionJewelSmallPassivesGrantLife2_")!);
            conn.ClusterPassives = [];
            int none = (int)Program.PoolsFor(conn, 1).Life;
            // The client's measured layout: a medium 4/1 in cell 51 uses 207 as its block base, smalls at 208, 209
            // and 211 (7 life each) and the jewel socket at 212 (nothing). Two cells with different starts prove the
            // start rotation: the same jewel in cell 53 uses 225 the same way (225, 228, 229 and the socket 226).
            var medium = items.Items(PlayerInventories.PassiveJewels).First(i => i.X == cell).Item;
            var layout = ClusterJewelLayout.For(cell, medium)!;
            Assert.Equal(207, layout.Base);
            Assert.Equal(ClusterJewelLayout.Kind.Small, layout.ByOffset[208 - 207]);
            Assert.Equal(ClusterJewelLayout.Kind.Small, layout.ByOffset[209 - 207]);
            Assert.Equal(ClusterJewelLayout.Kind.Small, layout.ByOffset[211 - 207]);
            Assert.Equal(ClusterJewelLayout.Kind.Socket, layout.ByOffset[212 - 207]);
            conn.ClusterPassives = [208];
            Assert.Equal(none + 7, (int)Program.PoolsFor(conn, 1).Life);
            conn.ClusterPassives = [208, 209];
            Assert.Equal(none + 14, (int)Program.PoolsFor(conn, 1).Life);
            conn.ClusterPassives = [208, 209, 211];
            Assert.Equal(none + 21, (int)Program.PoolsFor(conn, 1).Life);
            conn.ClusterPassives = [208, 209, 211, 212];
            Assert.Equal(none + 21, (int)Program.PoolsFor(conn, 1).Life);
            // The same jewel in the large's third cell (the client's second socket) rotates to base 225.
            int third = Program.Passives.ClusterSlots(20)[2];
            items.Add(PlayerInventories.PassiveJewels, (byte)third, 0,
                Program.CreateClusterJewel(1, 4, 1, "AfflictionJewelSmallPassivesGrantLife2_")!);
            var other = ClusterJewelLayout.For(third, items.Items(PlayerInventories.PassiveJewels).First(i => i.X == third).Item)!;
            Assert.Equal(225, other.Base);
            Assert.Equal(ClusterJewelLayout.Kind.Small, other.ByOffset[0]);
            Assert.Equal(ClusterJewelLayout.Kind.Socket, other.ByOffset[1]);
            Assert.Equal(ClusterJewelLayout.Kind.Small, other.ByOffset[3]);
            Assert.Equal(ClusterJewelLayout.Kind.Small, other.ByOffset[4]);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void Every_notable_is_added_once_the_cluster_is_allocated()
    {
        TestSaves.Ensure();
        string character = "ClusterNotablesOnce" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [Program.Passives.JewelNodeForSlot(20)!.Value];
            var items = PlayerInventories.For(character, Program.Items);
            var jewel = Program.CreateClusterJewel(2, 8, 2, "AfflictionJewelSmallPassivesGrantLife2_",
                ["AfflictionNotableFettle", "AfflictionNotableProdigiousDefense__", "AfflictionNotableAdvanceGuard"])!;
            items.Add(PlayerInventories.PassiveJewels, 20, 0, jewel);
            conn.ClusterPassives = [];
            int none = (int)Program.PoolsFor(conn, 1).Life;
            // 8 nodes, 2 sockets, 3 notables: 3 smalls (3 x 7) and Fettle's 20 life + 10% increased maximum life.
            conn.ClusterPassives = [195, 196, 197, 198, 200, 202, 204, 206];
            Assert.True((int)Program.PoolsFor(conn, 1).Life - none >= 21 + 20,
                $"expected the 3 smalls and Fettle, got {(int)Program.PoolsFor(conn, 1).Life - none}");
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void The_global_unique_catalogue_has_the_expected_entries()
    {
        Assert.Equal(42, UniqueJewelCatalogue.Global.Length);
        Assert.Contains("Brawn", UniqueJewelCatalogue.Names);
        Assert.Contains("Poacher's Aim", UniqueJewelCatalogue.Names);
        Assert.Contains("Rational Doctrine", UniqueJewelCatalogue.Names);
    }

    [Fact]
    public void Rational_Doctrine_is_creatable_with_its_four_ground_mods()
    {
        TestSaves.Ensure();
        string character = "RationalDoctrine" + Guid.NewGuid().ToString("N");
        try
        {
            var jewel = Program.CreateUniqueJewel("Rational Doctrine");
            Assert.NotNull(jewel);
            Assert.Equal(4, jewel!.Mods.Count);
            Assert.Single(jewel.NameWords);
            Assert.Equal("Cobalt Jewel", Program.Items.ByHash(jewel.TypeHash)?.Name);
            Assert.NotNull(UniqueJewelVisuals.Of("Rational Doctrine"));

            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [60735];
            var items = PlayerInventories.For(character, Program.Items);
            items.Add(PlayerInventories.PassiveJewels, 4, 0, jewel);
            Program.PoolsFor(conn, 20);
            var sources = conn.StatSourcesCache!.Sources;
            Assert.Equal(18, sources.GetValueOrDefault(Program.Stats.Key(
                "consecrated_ground_while_stationary_radius_if_highest_attribute_is_strength")!.Value));
            Assert.Equal(25, sources.GetValueOrDefault(Program.Stats.Key(
                "profane_ground_on_crit_chance_%_if_highest_attribute_is_intelligence")!.Value));
            Assert.Equal(4000, sources.GetValueOrDefault(Program.Stats.Key(
                "your_consecrated_ground_effect_lingers_for_ms_after_leaving_the_area")!.Value));
            Assert.Equal(4000, sources.GetValueOrDefault(Program.Stats.Key(
                "your_profane_ground_effect_lingers_for_ms_after_leaving_the_area")!.Value));
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public void Global_unique_jewels_create_unique_items_with_their_visuals_and_mods()
    {
        var brawn = Program.CreateUniqueJewel("Brawn");
        Assert.NotNull(brawn);
        Assert.Equal(ItemRarity.Unique, brawn!.Rarity);
        Assert.Equal(36666, brawn.VisualIdentity);
        Assert.Equal(3, brawn.Mods.Count);
        var fragility = Program.CreateUniqueJewel("Fragility");
        Assert.NotNull(fragility);
        Assert.True(fragility!.Corrupted);
        Assert.Single(fragility.Mods);
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(100, 30)]
    [InlineData(1000, 165)]
    public void The_Adorned_scales_a_corrupted_magic_jewel_in_another_socket(int percent, int expectedFire)
    {
        TestSaves.Ensure();
        string character = "Adorned" + Guid.NewGuid().ToString("N");
        try
        {
            var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
                { Character = character, ClassRow = 1, PlayerLife = 100 };
            conn.Passives = [Program.Passives.JewelNodeForSlot(0)!.Value, Program.Passives.JewelNodeForSlot(1)!.Value];
            conn.Pools = Program.PoolsFor(conn, 20);
            var items = PlayerInventories.For(character, Program.Items);
            var mods = new ModCatalog(Data());
            var magic = ItemCatalog.Create(Program.Items.Find("Prismatic Jewel")!) with
            {
                Rarity = ItemRarity.Magic, Corrupted = true, ItemLevel = 20,
                Mods = [mods.ById("FireResist1")!.With(ModSection.Explicit, [15], Random.Shared)],
            };
            int baseline = conn.Pools.FireResistance;
            items.Add(PlayerInventories.PassiveJewels, 1, 0, magic);
            conn.Pools = Program.PoolsFor(conn, 20);
            Assert.Equal(baseline + 15, conn.Pools.FireResistance);

            var adorned = ItemCatalog.Create(Program.Items.Find("Crimson Jewel")!) with
            {
                Rarity = ItemRarity.Unique, Corrupted = true, ItemLevel = 20,
                Mods = [new ItemMod(ModSection.Explicit, Program.Items.Mods.ById("CorruptedMagicJewelModEffectUnique__1")!.Hash, [percent])],
            };
            items.Add(PlayerInventories.PassiveJewels, 0, 0, adorned);
            conn.Pools = Program.PoolsFor(conn, 20);
            Assert.Equal(baseline + expectedFire, conn.Pools.FireResistance);
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public async Task Placing_a_jewel_in_an_allocated_socket_moves_it_to_the_passive_pane()
    {
        TestSaves.Ensure();
        string character = "PassiveJewelMove" + Guid.NewGuid().ToString("N");
        using var wire = new Wire();
        var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
            { Character = character, ClassRow = 1, PlayerLife = 100 };
        var items = PlayerInventories.For(character, Program.Items);
        var jewel = ItemCatalog.Create(Program.Items.Find("Prismatic Jewel")!);
        try
        {
            var inBag = items.Add(PlayerInventories.MainInventory, 0, 0, jewel);
            Assert.NotNull(items.PickUp(PlayerInventories.MainInventory, inBag.Id));
            conn.Passives = [Program.Passives.JewelNodeForSlot(0)!.Value];
            conn.Pools = Program.PoolsFor(conn, 1);
            var packet = new LoginWriter().U16(InstanceItems.PlaceItemId)
                .U32(PlayerInventories.InventoryId(PlayerInventories.PassiveJewels)).U32(0).U32(0)
                .U8(0).U8(3).U8(0).U8(0).ToArray();
            await Program.OnClientMessages("jewel-test", wire.Writer.GetStream(), wire.Ciphers,
                [(InstanceItems.PlaceItemId, packet)], [], conn).WaitAsync(TimeSpan.FromSeconds(5));
            var socketed = Assert.Single(items.Items(PlayerInventories.PassiveJewels));
            Assert.Equal(0, socketed.X);
            Assert.Empty(items.Items(PlayerInventories.Cursor));
        }
        finally { PlayerInventories.Forget(character); }
    }

    [Fact]
    public async Task A_cluster_jewel_is_refused_by_a_passive_socket()
    {
        TestSaves.Ensure();
        string character = "PassiveJewelCluster" + Guid.NewGuid().ToString("N");
        using var wire = new Wire();
        var conn = new ClientState(new(ushort.MaxValue, "Jewel", 128, 128, 32, 32, null, []), 0, 1)
            { Character = character, ClassRow = 1, PlayerLife = 100 };
        var items = PlayerInventories.For(character, Program.Items);
        var jewel = ItemCatalog.Create(Program.Items.Find("Large Cluster Jewel")!);
        try
        {
            var inBag = items.Add(PlayerInventories.MainInventory, 0, 0, jewel);
            Assert.NotNull(items.PickUp(PlayerInventories.MainInventory, inBag.Id));
            conn.Passives = [Program.Passives.JewelNodeForSlot(0)!.Value];
            conn.Pools = Program.PoolsFor(conn, 1);
            var packet = new LoginWriter().U16(InstanceItems.PlaceItemId)
                .U32(PlayerInventories.InventoryId(PlayerInventories.PassiveJewels)).U32(0).U32(0)
                .U8(0).U8(3).U8(0).U8(0).ToArray();
            await Program.OnClientMessages("jewel-test", wire.Writer.GetStream(), wire.Ciphers,
                [(InstanceItems.PlaceItemId, packet)], [], conn).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(items.Items(PlayerInventories.PassiveJewels));
            Assert.Single(items.Items(PlayerInventories.Cursor));
        }
        finally { PlayerInventories.Forget(character); }
    }
}
