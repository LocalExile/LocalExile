using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class WeaponPassiveTreeTests : IDisposable
{
    public void Dispose() => TestEnvironment.ReleaseOwnSaves();

    [Fact]
    public void Weapon_passive_tree_wire_packets_and_saves_round_trip()
    {
        // 1. Mods bit-7 wire layout: unrevealed (HasTree = false) and revealed (HasTree = true).
        var unrevealedWire = new Item(1, ["Mods"], 0)
        {
            ItemLevel = 68, MemoryStrands = 0x11, Intangibility = 0x22,
            WeaponPassiveTree = new WeaponPassiveTreeState(BalanceItemLevel: 68, Experience: 1234, HasTree: false),
        };
        Assert.Equal(
            "01000000" + "0000" + "44" + "00" + "8000" + "0800" + "00" +
            "44000000" + "D2040000" + "00" + "11" + "22",
            Convert.ToHexString(unrevealedWire.Payload()));

        int w0 = WeaponPassiveTreeState.BitIndex(0, 0), w1 = WeaponPassiveTreeState.BitIndex(1, 0), w4 = WeaponPassiveTreeState.BitIndex(2, 1);
        Assert.Equal((0, 0), WeaponPassiveTreeState.Coordinates(w0));
        Assert.Equal((2, 1), WeaponPassiveTreeState.Coordinates(w4));
        var n0 = new WeaponPassiveTreeNode(0x1234, [5, -3]);
        var n1 = new WeaponPassiveTreeNode(0x5678, [10]);
        var n4 = new WeaponPassiveTreeNode(0x9abc, []);
        var wireTree = new WeaponPassiveTreeState(
            BalanceItemLevel: 84, Experience: 50000, HasTree: true,
            Layout: (ushort)((1 << w0) | (1 << w1) | (1 << w4)), Allocation: (ushort)((1 << w0) | (1 << w1)),
            LeftParentConnections: (ushort)(1 << w4), RightParentConnections: (ushort)((1 << w1) | (1 << w4)),
            PendingCol2: 1, Nodes: [n0, n1, n4]);
        var revealedWire = new Item(1, ["Mods"], 0)
        {
            ItemLevel = 84, MemoryStrands = 0x33, Intangibility = 0x44, WeaponPassiveTree = wireTree,
        };
        Assert.Equal(
            "01000000" + "0000" + "54" + "00" + "8000" + "0800" + "00" +
            "54000000" + "50C30000" + "01" + "1300" + "0300" + "1000" + "1200" + "FFFF01FFFF" +
            "3412" + "05" + "7D" + "7856" + "0A" + "BC9A" + "33" + "44",
            Convert.ToHexString(revealedWire.Payload()));
        Assert.Equal(n4, wireTree.NodeAt(2, 1));
        Assert.Null(wireTree.NodeAt(1, 1));
        Assert.Equal([n0, n1], wireTree.AllocatedNodes().ToArray());
        Assert.Throws<InvalidDataException>(() => (new Item(1, ["Mods"], 0)
            { WeaponPassiveTree = new(1, 0, true, Layout: 0x8001, Nodes: [n0, n1]) }).Payload());
        Assert.Throws<InvalidDataException>(() => (new Item(1, ["Mods"], 0)
            { WeaponPassiveTree = new(1, 0, true, Layout: 0x0003, Nodes: [n0]) }).Payload());

        // 2. Crucible C→S framing (0x0116, 0x0299, 0x029b), S→C (0x007b 0x4a/0x4b, 0x029a, 0x029c, 0x02c9) and ControlZone blob.
        byte[] unalloc = Convert.FromHexString("0116" + "000000010000002A00000002000000070000000300000002");
        byte[] combineReq = Convert.FromHexString("0299" + "00000001000000100000000100000020");
        byte[] uniqueReq = Convert.FromHexString("029B" + "0000000100000033");
        Assert.Equal(new InstanceCrucible.WeaponPassiveUnallocate(1, 0x2A, 2, 7, 3, 2), InstanceCrucible.ReadWeaponPassiveUnallocate(unalloc));
        Assert.Equal(new InstanceCrucible.CombineRequest(1, 0x10, 1, 0x20), InstanceCrucible.ReadCombineRequest(combineReq));
        Assert.Equal(new InstanceCrucible.UniqueForgeRequest(1, 0x33), InstanceCrucible.ReadUniqueForgeRequest(uniqueReq));
        var split = InstanceProtocol.SplitClientMessages([.. unalloc, .. combineReq, .. uniqueReq], out var partial, out var unknown);
        Assert.Equal([26, 18, 10], split.Select(m => m.Bytes.Length).ToArray());
        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Equal("007B4A00000001000000000000000000000000", Convert.ToHexString(InstanceCrucible.OpenCombiningUi(uber: true)));
        Assert.Equal("007B4B00000002000012340000000000000000", Convert.ToHexString(InstanceCrucible.OpenUniqueForgeUi(InstanceCrucible.ScouringForgeMode, 0x1234)));
        Assert.Equal(
            "029A000000010000002002000000000000000001000000020000000106",
            Convert.ToHexString(InstanceCrucible.CombineResult(1, 0x20,
            [
                new(0, 0, InstanceCrucible.NodeMutationFlags.UpgradedTier),
                new(2, 1, InstanceCrucible.NodeMutationFlags.DowngradedTier | InstanceCrucible.NodeMutationFlags.Mutated),
            ])));
        Assert.Equal("029C0000000100000033", Convert.ToHexString(InstanceCrucible.UniqueForgeResult(1, 0x33)));
        Assert.Equal("02C9890100", Convert.ToHexString(InstanceCrucible.ForgeEnterZone()));
        Assert.Equal("02C9890101", Convert.ToHexString(InstanceCrucible.ForgeLeaveZone()));
        Assert.Equal("02C9890102", Convert.ToHexString(InstanceCrucible.ForgeLockWeapon()));
        Assert.Equal("02C98905030000003F", Convert.ToHexString(InstanceCrucible.ForgeCharge(0.5f)));
        Assert.Equal("02C989020401", Convert.ToHexString(InstanceCrucible.ForgeChannelling(true)));
        Assert.Equal("02C989020502", Convert.ToHexString(InstanceCrucible.ForgeState(2)));
        Assert.Equal("02C989050678563412", Convert.ToHexString(InstanceCrucible.ForgeOpenGamepadPopup(0x12345678)));
        byte[] longRuleset = InstanceProtocol.RulesetMessage(InstanceCrucible.CrucibleMapRulesetRow, new byte[128]);
        Assert.Equal("02C98C8080", Convert.ToHexString(longRuleset, 0, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => InstanceProtocol.RulesetMessage(0x89, new byte[65536]));
        string[] crucibleDevice = ["Positioned", "BaseEvents", "InteractionAction", "Animated", "TriggerableBlockage", "MinimapIcon", "StateMachine", "ControlZone"];
        Assert.True(QuestObject.CanSerialize(crucibleDevice, haveStateMachine: true));
        Assert.Equal(
            new QuestObject(10, 20, crucibleDevice[..^1]) { StateMachine = new([0, 0, 0], 0) }.Blob(),
            new QuestObject(10, 20, crucibleDevice) { StateMachine = new([0, 0, 0], 0) }.Blob());

        // 3. Character save (equipment + inventory) and account stash round-trip.
        TestEnvironment.OpenOwnSaves("weapon-passive-tree");
        var catalog = Program.Items;
        var bowType = catalog.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!;
        var physMod = catalog.Mods.ById("WeaponTreeAddedPhysical1")!;
        var notableMod = catalog.Mods.ById("WeaponTreeNotableBowAvatarOfTheHunt")!;

        int b0 = WeaponPassiveTreeState.BitIndex(0, 0);
        int b1 = WeaponPassiveTreeState.BitIndex(1, 0);
        ushort layout = (ushort)((1 << b0) | (1 << b1));
        var revealedTree = new WeaponPassiveTreeState(
            BalanceItemLevel: 84, Experience: 12345, HasTree: true,
            Layout: layout, Allocation: (ushort)(1 << b0),
            LeftParentConnections: 0, RightParentConnections: (ushort)(1 << b1),
            PendingCol1: 0,
            Nodes:
            [
                new(physMod.Hash, [2, 6]),
                new(notableMod.Hash, [-28849]),
            ]);
        var unrevealedTree = new WeaponPassiveTreeState(BalanceItemLevel: 68, Experience: 500, HasTree: false);

        var revealedBow = ItemCatalog.Create(bowType) with { ItemLevel = 84, WeaponPassiveTree = revealedTree };
        var unrevealedBow = ItemCatalog.Create(bowType) with { ItemLevel = 68, WeaponPassiveTree = unrevealedTree };
        var oreType = catalog.Find("Metadata/Items/Currency/CurrencyAddCrucibleExperience")!;
        var magmaticOre = ItemCatalog.Create(oreType) with { StoredExperience = 25000 };
        Assert.EndsWith("A8610000", Convert.ToHexString(magmaticOre.Payload()));

        string character = "CrucibleSave" + Guid.NewGuid().ToString("N");
        var saved = new CharacterSaves.Save(new Dictionary<byte, List<CharacterSaves.SavedPlacement>>
        {
            [PlayerInventories.Weapon1] = [new(1, 0, 0, CharacterSaves.ToSaved(revealedBow, catalog.Mods))],
            [PlayerInventories.MainInventory] =
            [
                new(2, 0, 0, CharacterSaves.ToSaved(unrevealedBow, catalog.Mods)),
                new(3, 2, 0, CharacterSaves.ToSaved(magmaticOre, catalog.Mods)),
            ],
        });
        CharacterSaves.Write(character, saved);
        var loaded = CharacterSaves.Load(character)!;
        var loadedRevealed = CharacterSaves.FromSaved(loaded.Inventories![PlayerInventories.Weapon1][0].Item, catalog);
        var loadedUnrevealed = CharacterSaves.FromSaved(loaded.Inventories![PlayerInventories.MainInventory][0].Item, catalog);
        var loadedOre = CharacterSaves.FromSaved(loaded.Inventories![PlayerInventories.MainInventory][1].Item, catalog);

        Assert.Equal(revealedTree, loadedRevealed.WeaponPassiveTree);
        Assert.Equal(revealedBow.Payload(), loadedRevealed.Payload());
        Assert.Equal(unrevealedTree, loadedUnrevealed.WeaponPassiveTree);
        Assert.Equal(unrevealedBow.Payload(), loadedUnrevealed.Payload());
        Assert.Equal(25000u, loadedOre.StoredExperience);
        Assert.Equal(magmaticOre.Payload(), loadedOre.Payload());

        string account = "CrucibleStash" + Guid.NewGuid().ToString("N");
        PlayerInventories.Shape? Shape(Item i) => catalog.ByHash(i.TypeHash) is { } t ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;
        var stash = AccountStash.For(account, catalog);
        Assert.Equal(2, stash.FillCells(0, [revealedBow, unrevealedBow], i => i.WeaponPassiveTree!.HasTree.ToString(), Shape, out _)!.Added.Count);
        AccountStash.Forget(account);
        var reloadedItems = AccountStash.For(account, catalog).Inventory(0).Items.Select(p => p.Item).ToArray();
        Assert.Contains(reloadedItems, i => Equals(i.WeaponPassiveTree, revealedTree));
        Assert.Contains(reloadedItems, i => Equals(i.WeaponPassiveTree, unrevealedTree));
        AccountStash.Forget(account);
    }

    [Fact]
    public void Allocated_weapon_passive_nodes_apply_stats_granted_passives_and_framework_hooks()
    {
        TestEnvironment.OpenOwnSaves("weapon-passive-stats");
        var catalog = Program.Items;
        var bowType = catalog.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!;
        var physMod = catalog.Mods.ById("WeaponTreeAddedPhysical1")!;
        var notableMod = catalog.Mods.ById("WeaponTreeNotableBowAvatarOfTheHunt")!;

        int b0 = WeaponPassiveTreeState.BitIndex(0, 0);
        int b1 = WeaponPassiveTreeState.BitIndex(1, 0);
        ushort layout = (ushort)((1 << b0) | (1 << b1));
        var tree = new WeaponPassiveTreeState(
            BalanceItemLevel: 84, Experience: 50000, HasTree: true,
            Layout: layout, Allocation: layout,
            RightParentConnections: (ushort)(1 << b1),
            Nodes:
            [
                new(physMod.Hash, [2, 6]),
                new(notableMod.Hash, [-28849]),
            ]);

        var bow = ItemCatalog.Create(bowType) with { ItemLevel = 84, WeaponPassiveTree = tree };
        var unallocatedBow = bow with { WeaponPassiveTree = tree with { Allocation = 0 } };

        // Allocated local weapon stats apply; unallocated nodes do not.
        var localAlloc = ItemModStats.Local(bow, catalog.Mods);
        var localUnalloc = ItemModStats.Local(unallocatedBow, catalog.Mods);
        Assert.Equal(2, localAlloc.GetValueOrDefault("local_minimum_added_physical_damage"));
        Assert.Equal(6, localAlloc.GetValueOrDefault("local_maximum_added_physical_damage"));
        Assert.Equal(0, localUnalloc.GetValueOrDefault("local_minimum_added_physical_damage"));

        // Granted passive hash resolves signed int16 (-28849) -> ushort 36687 (Avatar of the Hunt).
        ushort avatarOfTheHunt = unchecked((ushort)-28849);
        Assert.Equal([avatarOfTheHunt], ItemModStats.GrantedPassives([bow], catalog.Mods).ToArray());
        Assert.Empty(ItemModStats.GrantedPassives([unallocatedBow], catalog.Mods));

        var conn = new ClientState(new(1, "crucible", 100, 100, 100, 100, null, []), 1, 1)
        {
            Character = "CrucibleStats" + Guid.NewGuid().ToString("N"),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
        };
        try
        {
            var passiveTree = Game.PassivesFor(conn);
            var grantedNode = passiveTree.Find(avatarOfTheHunt)!;
            var (statKey, statVal) = grantedNode.Stats[0];

            int baseline = Game.CharacterStatSources(conn).Stats.GetValueOrDefault(statKey);
            Game.ItemsOf(conn).Put(PlayerInventories.Weapon1, new(50, 0, 0, bow));
            Assert.Equal(baseline + statVal, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(statKey));

            // Already allocating the same passive on the character's tree must not double-count it.
            conn.Passives.Add(avatarOfTheHunt);
            Assert.Equal(baseline + statVal, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(statKey));

            // Removing the weapon passive allocation removes the granted notable once not allocated on the tree.
            conn.Passives.Remove(avatarOfTheHunt);
            Game.ItemsOf(conn).Remove(PlayerInventories.Weapon1, 50);
            Game.ItemsOf(conn).Put(PlayerInventories.Weapon1, new(50, 0, 0, unallocatedBow));
            Assert.Equal(baseline, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(statKey));

            // Generic framework hooks: GameEvents.VendorQuote and GameEvents.CurrencyApply.
            string owner = Program.LeagueFor(conn).Modules.First();
            var rewards = new List<Item>();
            var recipes = new List<string>();
            using (GameEvents.VendorQuote.Add(owner, q => { q.Rewards.Add(unallocatedBow); q.Recipes.Add("test-sell-bonus"); }))
            {
                GameEvents.RunVendorQuote(new VendorSaleQuote(conn, [new(50, 0, 0, bow)], rewards, recipes, basicSale: true));
            }
            Assert.Single(rewards);
            Assert.Equal(["test-sell-bonus"], recipes);

            var oreType = catalog.Find("Metadata/Items/Currency/CurrencyAddCrucibleExperience")!;
            var oreItem = ItemCatalog.Create(oreType) with { StoredExperience = 12345 };
            Item? applied;
            using (GameEvents.CurrencyApply.Add(owner, (_, target, _, currencyItem, _) =>
                target with { Quality = (byte)(currencyItem?.StoredExperience == 12345 ? 17 : 0) }))
            {
                applied = GameEvents.RunCurrencyApply(conn, bow, oreType, oreItem, new Random(1));
            }
            Assert.NotNull(applied);
            Assert.Equal(17, applied!.Quality);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
        }
    }
}
