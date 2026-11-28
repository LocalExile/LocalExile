using System.Buffers.Binary;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modules.Breach;

namespace GameServer.Gateway.Tests;

/// <summary>
/// Integration tests for Breach ("Brequel" / Keepers of the Flame): binary-verified 3.29.3c/d protocol encoding,
/// BrequelFruit Component 170 and Foulborn Mods bit 9 serialization/persistence, Foulborn currency orbs and
/// MutatedUniqueCatalog mapping, Genesis Tree PSG graph/allocation/cost/reward flow, Wombgift Locker validation,
/// and Breach boss drop tables.
/// </summary>
public sealed class BreachMetaProgressionTests
{
    static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    [Fact]
    public void Brequel_protocol_item_components_foulborn_orbs_and_save_persistence_round_trip()
    {
        // 1. S->C 0x7b OpenUi kinds (0x62 Genesis Tree, 0x64 Wombgift Locker) and S->C 0x02d2 Hiveblood.
        Assert.Equal((byte)0x62, InstanceBrequel.OpenGenesisTree()[2]);
        byte[] openLocker = InstanceBrequel.OpenWombgiftLocker(0x1234);
        Assert.Equal((byte)0x64, openLocker[2]);
        Assert.Equal(0x1234u, BinaryPrimitives.ReadUInt32BigEndian(openLocker.AsSpan(3, 4)));

        BreachModule.LoadData();
        byte[] hb = BreachHiveblood.BuildHivebloodPacket(25_000);
        Assert.Equal((ushort)0x02d2, BinaryPrimitives.ReadUInt16BigEndian(hb));
        var expectedCompact = new LoginWriter();
        InstanceProtocol.CompactUInt(expectedCompact, 25_000u);
        Assert.Equal(expectedCompact.ToArray(), hb[2..]);

        // 2. S->C 0x02cf Genesis Tree state: allowed points, pointsOnly=0, u16 BE count, sorted ascending u16 LE node IDs.
        //    The start and womb nodes are implicit on the client and left out (33919 and 41240 are wombs).
        var tree = new BreachGenesisTree.SavedTreeState { Allocated = [41099, 8017, 33919, 41240, 8017] };
        byte[] treeFull = BreachGenesisTree.BuildTreeStatePacket(tree, [10, 5, 3, 2]);
        Assert.Equal(BreachGenesisTree.TreeStatePacketId, BinaryPrimitives.ReadUInt16BigEndian(treeFull));
        Assert.Equal((byte)0, treeFull[6]); // pointsOnly = 0
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16BigEndian(treeFull.AsSpan(7, 2)));
        Assert.Equal((ushort)8017, BinaryPrimitives.ReadUInt16LittleEndian(treeFull.AsSpan(9, 2)));
        Assert.Equal((ushort)41099, BinaryPrimitives.ReadUInt16LittleEndian(treeFull.AsSpan(11, 2)));

        byte[] treePtsOnly = BreachGenesisTree.BuildTreeStatePacket(tree, [4, 1, 0, 0], pointsOnly: true);
        Assert.Equal((byte)1, treePtsOnly[^1]);

        // 3. C->S 0x010d (8B) and the 0x02ca Ruleset #165 skill activation payload (5B).
        var move = InstanceBrequel.ReadLockerMove(new LoginWriter().U16(InstanceBrequel.LockerMoveId).U32(99).U8(0).U8(1).ToArray());
        Assert.Equal((99u, false, true), (move.ItemId, move.FromExpanded, move.Into));
        Assert.True(BreachRulesets.TryReadActivateSkillAction([0x00, 0x78, 0x56, 0x34, 0x12], out uint skillEntityId));
        Assert.Equal(0x12345678u, skillEntityId);

        // 4. BrequelFruit Component 170 (u32 LE fleshgraftLevel), Foulborn Mods bit 9 (0x0200), SQLite save round-trip,
        //    UniqueCatalog.Reforge refusal, Foulborn orbs, and MutatedUniqueCatalog (365 mods).
        var items = Program.Items;
        var fruitType = items.ById("Metadata/Items/Chayula/EquipmentFruit");
        Assert.NotNull(fruitType);
        Assert.True(fruitType!.Supported);
        var fruit = ItemCatalog.Create(fruitType) with { ItemLevel = 83, BrequelFruitLevel = 83u };
        Assert.EndsWith("53000000", Hex(fruit.Payload())); // u32 LE 83 = 0x53000000

        var uniques = new UniqueCatalog(items);
        Assert.NotEmpty(MutatedUniqueCatalog.EligibleUniques(items.Mods));
        string mutatedUniqueName = MutatedUniqueCatalog.EligibleUniques(items.Mods)
            .First(n => uniques.Find(n) is not null);
        var baseUnique = uniques.Create(mutatedUniqueName, new Random(7))!;
        var foulborn = MutatedUniqueCatalog.Mutate(baseUnique, mutatedUniqueName, 1, items.Mods, new Random(7));
        Assert.NotNull(foulborn);
        Assert.True(foulborn!.Foulborn);
        Assert.Null(uniques.Reforge(foulborn, _ => 1.0, new Random(1)));

        // Verify Foulborn currency orbs recognize and roll valid Magic/Rare items.
        var ringType = items.Find("Coral Ring")!;
        var magicRing = Program.Rolls.Transmute(ItemCatalog.Create(ringType) with { ItemLevel = 84 }, new Random(11))!;
        var rareFromFoulborn = CurrencyEffects.FoulbornRegal(magicRing, Program.Rolls, new Random(12));
        Assert.NotNull(rareFromFoulborn);
        Assert.Equal(ItemRarity.Rare, rareFromFoulborn!.Rarity);
        var exaltedFromFoulborn = CurrencyEffects.FoulbornExalted(rareFromFoulborn, Program.Rolls, new Random(13));
        Assert.NotNull(exaltedFromFoulborn);
        Assert.True(exaltedFromFoulborn!.Mods.Count > rareFromFoulborn.Mods.Count);

        // SQLite persistence round-trip for BrequelFruitLevel, Foulborn flag, and BrequelStorage1 (row 101).
        var dbPath = Path.Combine(Path.GetTempPath(), $"brequel-save-{Guid.NewGuid():N}.db");
        const string charName = "BrequelPersistence";
        Saves.Open(dbPath);
        try
        {
            var inv = PlayerInventories.For(charName, items);
            inv.Put(PlayerInventories.BrequelStorage, new(inv.NewItemId(), 0, 0, fruit));
            inv.Put(PlayerInventories.MainInventory, new(inv.NewItemId(), 0, 0, foulborn));
            PlayerInventories.Forget(charName);

            var reloaded = PlayerInventories.For(charName, items);
            var reloadedFruit = Assert.Single(reloaded.Items(PlayerInventories.BrequelStorage));
            Assert.Equal(83u, reloadedFruit.Item.BrequelFruitLevel);
            var reloadedFoulborn = Assert.Single(reloaded.Items(PlayerInventories.MainInventory), i => i.Item.TypeHash == foulborn.TypeHash);
            Assert.True(reloadedFoulborn.Item.Foulborn);
        }
        finally { PlayerInventories.Forget(charName); TestEnvironment.ReleaseSaves(dbPath); }

        // 6. BrequelPortalFortress & BrequelPortalBoss (trailing 0-byte ControlZone) pass AreaTransitionObject.CanSerialize.
        foreach (string portalType in new[]
        {
            "Metadata/MiscellaneousObjects/Brequel/BrequelPortalFortress",
            "Metadata/MiscellaneousObjects/Brequel/BrequelPortalBoss",
        })
        {
            var info = QuestObjectTypes.Find(portalType);
            Assert.NotNull(info);
            Assert.True(AreaTransitionObject.CanSerialize(info!.Components), $"{portalType} should serialize as AreaTransitionObject");
        }
    }

    [Fact]
    public async Task Genesis_tree_graph_wombgift_locker_rewards_and_breach_boss_drops_integrate_end_to_end()
    {
        BreachModule.LoadData();
        BreachGenesisTree.AttachFruitComponent = static (item, level) => item with { BrequelFruitLevel = level };
        BreachGenesisTree.ReadFruitLevel = static item => item.BrequelFruitLevel is uint l ? (int)l : null;
        BreachGenesisTree.MarkFoulbornItem = static item => item with { Foulborn = true };

        try
        {
            // 1. Extracted ChayulaTreePassiveSkillGraph ("brequel") has 134 nodes and all 4 root nodes.
            var links = PassiveGraphs.Links("brequel");
            Assert.Equal(134, links.Count);
            foreach (ushort root in new ushort[] { 19625, 22476, 42956, 47967 })
                Assert.True(links.ContainsKey(root), $"missing Genesis Tree root node {root}");

            // 2. Hiveblood cost formula (0x1424ad630) uses FleshgraftLevel 1..100 (row = level - 1).
            int level80Storage = BreachHiveblood.ResourceValueForLevel(80);
            Assert.Equal(1137, level80Storage);
            var equipFruitDef = BreachGenesisTree.FindFruitBySubTree(0);
            Assert.True(equipFruitDef.HasValue);
            Assert.Equal(level80Storage * 90 / 100, BreachGenesisTree.RequiredHivebloodFor(equipFruitDef!.Value, 80));

            // 3. Wombgift creation, Wombgift Locker refusal, and Genesis Tree reward generation across all 4 sub-trees.
            await using var area = await BreachArea.Open();
            var player = area.A;
            var wombgift = BreachGenesisTree.CreateWombgift(player, "Metadata/Items/Chayula/EquipmentFruit", 80, new Random(21));
            Assert.NotNull(wombgift);
            Assert.Equal(80u, wombgift!.BrequelFruitLevel);

            var plainRing = ItemCatalog.Create(Program.Items.Find("Coral Ring")!);
            Assert.Null(BreachLocker.Refusal(player, PlayerInventories.BrequelStorage, wombgift));
            Assert.NotNull(BreachLocker.Refusal(player, PlayerInventories.BrequelStorage, plainRing));

            var emptyStats = new Dictionary<uint, int>();
            for (int subTree = 0; subTree < 4; subTree++)
            {
                var rewards = await BreachGenesisTree.RollRewardsAsync(player, subTree, 80, emptyStats, new Random(100 + subTree));
                Assert.NotEmpty(rewards);
            }

            // 4. Breach boss drop tables (world.db): Tul, Esh, and Vruun (guaranteed ring pool + Hivebrain Gland).
            var bossItems = DropDataProvider.Default.Current.BossItems;
            foreach (string bossId in new[]
            {
                "Metadata/Monsters/BreachBossFallenKnight/FallenKnight",
                "Metadata/Monsters/BreachBossHandMage/HandMage",
                "Metadata/Monsters/Breach/WildBreachBoss",
            })
            {
                Assert.True(bossItems.Values.Any(r => r.BossId == bossId), $"missing drop_boss_items for {bossId}");
            }
            Assert.True(bossItems.ContainsKey(("Metadata/Monsters/Breach/WildBreachBoss", "Metadata/Items/MapFragments/CurrencyBrequelBossKey")));
        }
        finally
        {
            BreachGenesisTree.AttachFruitComponent = null;
            BreachGenesisTree.ReadFruitLevel = null;
            BreachGenesisTree.MarkFoulbornItem = null;
        }
    }
}
