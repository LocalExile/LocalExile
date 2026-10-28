using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>a1q5 "Mercy Mission": Hailrake's Medicine Chest, its flag, Nessa taking it and her flask reward.</summary>
[Collection(TestSaves.FailWriteCollection)]
public class MercyMissionTests : IDisposable
{
    const uint ChestHash = 370857295, FlaskHash = 0x0f1a5c02, GemHash = 0x0e3e0001, RobeHash = 0x0a0b0003;
    readonly string folder = TestSaves.Catalog(new TestSaves.Type(ChestHash, MercyMission.MedicineChest, 2, 3), new TestSaves.Type(123, "Other"));
    readonly string character = "Mercy" + Guid.NewGuid().ToString("N")[..12];
    static Item Chest => new(ChestHash, ["Base"], 1);
    static Item Other => new(123, ["Base"], 1);

    public MercyMissionTests() => TestSaves.Ensure();
    public void Dispose() { Saves.FailWrite = null; PlayerInventories.Forget(character); Directory.Delete(folder, true); }

    PlayerInventories Inventory() => PlayerInventories.For(character, new ItemCatalog(folder));
    HashSet<string> SavedFlags() => CharacterSaves.Load(character)?.QuestFlags?.ToHashSet() ?? [];

    // The repository's extracted game data, or null where it is missing (CI).
    static string? RealData()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data", "tables", "WorldAreas.json"))) return Path.Combine(dir.FullName, "data");
        return null;
    }

    [Fact] public void TheGameDataNamesHailrakeTheTidalIslandsBossAndTheChestAndRewardsAsUsed()
    {
        if (RealData() is not { } data) return;
        JsonElement Table(string name) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data, "tables", name + ".json"))).RootElement;
        var area = Table("WorldAreas").EnumerateArray().Single(a => a.GetProperty("Id").GetString() == MercyMission.Area);
        var bosses = area.GetProperty("Bosses_MonsterVarietiesKeys").EnumerateArray().Select(b => b.GetInt32()).ToList();
        var varieties = Table("MonsterVarieties");
        Assert.Equal(MercyMission.Hailrake, varieties[Assert.Single(bosses)].GetProperty("Id").GetString());
        Assert.Contains(Table("BaseItemTypes").EnumerateArray(), b => b.GetProperty("Id").GetString() == MercyMission.MedicineChest
            && b.GetProperty("Name").GetString() == "Medicine Chest");
        var catalog = QuestRewardCatalog.Load(data);
        Assert.Equal(MercyMission.QuestRewardTaken, catalog.Find(MercyMission.RewardOffer)!.TakenFlag);
        Assert.Equal("a1q5-QuestReward2Taken", catalog.Find(MercyMission.SupportRewardOffer)!.TakenFlag);
        Assert.All(catalog.Find(MercyMission.RewardOffer)!.Choices, c => Assert.Equal((4, 0), (c.Level, c.Rarity)));
    }

    [Fact] public void HailrakeDropsTheChestOnlyWhileThisCharacterHasNoneWithinReach()
    {
        var inventory = Inventory();
        var world = new InstanceState();
        Assert.True(MercyMission.HailrakeDrops(ChestHash, false, inventory, world.Ground));
        // Nessa has it: never again.
        Assert.False(MercyMission.HailrakeDrops(ChestHash, true, inventory, world.Ground));
        // A copy on this instance's ground: not twice.
        uint id = world.Ground.Add(new WorldItemObject(5, 5, Chest));
        Assert.False(MercyMission.HailrakeDrops(ChestHash, false, inventory, world.Ground));
        world.Ground.Take(id);
        // Carried, in the main inventory or on the cursor: not twice.
        var carried = inventory.Add(PlayerInventories.MainInventory, 0, 0, Chest);
        Assert.False(MercyMission.HailrakeDrops(ChestHash, false, inventory, world.Ground));
        inventory.PickUp(PlayerInventories.MainInventory, carried.Id);
        Assert.False(MercyMission.HailrakeDrops(ChestHash, false, inventory, world.Ground));
        inventory.Remove(PlayerInventories.Cursor, inventory.Items(PlayerInventories.Cursor).Single().Id);
        // Other items do not count, nor the account's stash (not part of the rule); a chest lost elsewhere drops again.
        inventory.Add(PlayerInventories.MainInventory, 0, 0, Other);
        Assert.True(MercyMission.HailrakeDrops(ChestHash, false, inventory, world.Ground));
    }

    [Fact] public void OnlyHailrakeOnTheTidalIslandDropsTheChest()
    {
        Assert.True(MercyMission.DropsChest("1_1_2a", "Metadata/Monsters/Cannibal/CannibalMaleBossHailrake"));
        Assert.False(MercyMission.DropsChest("1_1_2a", "Metadata/Monsters/Cannibal/CannibalMaleBossHailrakeGauntletNoQuest"));
        Assert.False(MercyMission.DropsChest("1_1_2", "Metadata/Monsters/Cannibal/CannibalMaleBossHailrake"));
    }

    [Fact] public void APickedChestIsKeptUntilNessaHasOneAndThenRemovedOtherItemsAreNotTouched()
    {
        Assert.Equal(MercyMission.Pickup.NotQuestItem, MercyMission.PickupOf(isChest: false, done: false));
        Assert.Equal(MercyMission.Pickup.NotQuestItem, MercyMission.PickupOf(isChest: false, done: true));
        Assert.Equal(MercyMission.Pickup.PickUp, MercyMission.PickupOf(isChest: true, done: false));
        Assert.Equal(MercyMission.Pickup.Remove, MercyMission.PickupOf(isChest: true, done: true));
    }

    [Fact] public void OnlyNessasMedicineChestTalkHandsTheChestInAndOnlyOnce()
    {
        Assert.True(MercyMission.HandsIn(MercyMission.NessaCongratulated, done: false));
        Assert.False(MercyMission.HandsIn(MercyMission.NessaCongratulated, done: true));
        Assert.False(MercyMission.HandsIn(MercyMission.ReceivedQuest, done: false));
        Assert.False(MercyMission.HandsIn(null, done: false));
    }

    [Fact] public void TheMessagePathSyncsAfterReadsThatMoveItemsOnly()
    {
        foreach (ushort id in new ushort[] { InstanceItems.DropItemId, InstanceItems.PlaceItemId, InstanceItems.PickUpItemId,
            InstanceStash.MoveToTabId, InstanceStash.TakeFromTabId, InstanceNpcs.TradeAcceptId, InstanceChat.ChatId })
            Assert.True(MercyMission.MovesItems(id), $"0x{id:x}");
        foreach (ushort id in new ushort[] { InstanceTransfer.UseSkillOnObjectId, InstanceProtocol.UseSkillAtLocationId, InstanceNpcs.OptionId })
            Assert.False(MercyMission.MovesItems(id), $"0x{id:x}");
    }

    [Fact] public void TheFlagSnapshotIsACopyTakenUnderTheWritersLock()
    {
        var conn = new ClientState(new AreaLayout(61641, "1_1_2a", 10, 10, 1, 1, null, []), 1, 1);
        conn.QuestFlags.Add(MercyMission.HaveMedicineChest);
        var snapshot = conn.QuestFlagsSnapshot();
        lock (conn.Monsters) conn.QuestFlags.Add(MercyMission.NessaCongratulated);
        Assert.DoesNotContain(MercyMission.NessaCongratulated, snapshot);
        Assert.True(conn.HasQuestFlag(MercyMission.NessaCongratulated));
    }

    [Fact] public void TheFlagFollowsTheCarriedChestUntilNessaHasIt()
    {
        Assert.Equal((true, false), MercyMission.Sync(flagged: false, done: false, carried: true));
        Assert.Equal(((bool?)null, false), MercyMission.Sync(flagged: true, done: false, carried: true));
        Assert.Equal((false, false), MercyMission.Sync(flagged: true, done: false, carried: false));
        Assert.Equal(((bool?)null, false), MercyMission.Sync(flagged: false, done: false, carried: false));
        // Nessa has one: the flag goes and a chest still carried (a copy from another instance) is taken.
        Assert.Equal((false, true), MercyMission.Sync(flagged: true, done: true, carried: true));
        Assert.Equal(((bool?)null, true), MercyMission.Sync(flagged: false, done: true, carried: true));
        Assert.Equal(((bool?)null, false), MercyMission.Sync(flagged: false, done: true, carried: false));
    }

    [Fact] public void DoneMeansNessaHasTheChestOrTheRewardIsTaken()
    {
        Assert.False(MercyMission.Done(_ => false));
        Assert.True(MercyMission.Done(f => f == MercyMission.NessaCongratulated));
        Assert.True(MercyMission.Done(f => f == MercyMission.QuestRewardTaken));
        Assert.False(MercyMission.Done(f => f == MercyMission.HaveMedicineChest));
    }

    [Fact] public void NessaTakesTheChestWithItsFlagsOnceAndTheyStaySavedAfterReload()
    {
        var inventory = Inventory();
        CharacterSaves.ChangeQuestFlags(character, [MercyMission.HaveMedicineChest], true);
        var chest = inventory.Add(PlayerInventories.MainInventory, 3, 1, Chest);
        inventory.Add(PlayerInventories.MainInventory, 0, 0, Other);
        var handed = MercyMission.HandIn(character, inventory, ChestHash);
        Assert.Equal((PlayerInventories.MainInventory, chest.Id), (handed!.Value.Row, handed.Value.Item.Id));
        Assert.Null(MercyMission.HandIn(character, inventory, ChestHash));   // a second, quick choice finds none
        PlayerInventories.Forget(character);
        var reloaded = Inventory();
        Assert.DoesNotContain(MercyMission.Carried(reloaded), c => c.Item.Item.TypeHash == ChestHash);
        Assert.Single(reloaded.Items(PlayerInventories.MainInventory));
        Assert.Contains(MercyMission.NessaCongratulated, SavedFlags());
        Assert.DoesNotContain(MercyMission.HaveMedicineChest, SavedFlags());
    }

    [Fact] public void WithoutAChestNessaTakesNothingAndSetsNoFlag()
    {
        var inventory = Inventory();
        inventory.Add(PlayerInventories.MainInventory, 0, 0, Other);
        Assert.Null(MercyMission.HandIn(character, inventory, ChestHash));
        Assert.DoesNotContain(MercyMission.NessaCongratulated, SavedFlags());
        Assert.Single(inventory.Items(PlayerInventories.MainInventory));
    }

    [Fact] public void TheHeldChestCountsAsCarriedAndIsTakenFromTheCursor()
    {
        var inventory = Inventory();
        var chest = inventory.Add(PlayerInventories.MainInventory, 0, 0, Chest);
        var held = inventory.PickUp(PlayerInventories.MainInventory, chest.Id)!;
        Assert.Contains(MercyMission.Carried(inventory), c => c.Row == PlayerInventories.Cursor && c.Item.Id == held.Id);
        Assert.Equal(PlayerInventories.Cursor, MercyMission.HandIn(character, inventory, ChestHash)!.Value.Row);
        Assert.Empty(inventory.Items(PlayerInventories.Cursor));
    }

    [Fact] public void AFailedSaveKeepsTheChestAndSetsNoFlag()
    {
        var inventory = Inventory();
        inventory.Add(PlayerInventories.MainInventory, 0, 0, Chest);
        int writes = 0;
        Saves.FailWrite = key => key.Equals(character, StringComparison.OrdinalIgnoreCase) && ++writes == 2;
        Assert.ThrowsAny<Exception>(() => MercyMission.HandIn(character, inventory, ChestHash));
        Saves.FailWrite = null;
        PlayerInventories.Forget(character);
        Assert.Contains(MercyMission.Carried(Inventory()), c => c.Item.Item.TypeHash == ChestHash);
        Assert.DoesNotContain(MercyMission.NessaCongratulated, SavedFlags());
    }

    [Fact] public void AChestOnOfferToAVendorStaysAndNothingChanges()
    {
        var inventory = Inventory();
        var chest = inventory.Add(PlayerInventories.MainInventory, 0, 0, Chest);
        Assert.NotNull(inventory.Offer(PlayerInventories.MainInventory, chest.Id, 0, 0, _ => new PlayerInventories.Shape(2, 3, "")));
        Assert.Null(MercyMission.HandIn(character, inventory, ChestHash));
        Assert.DoesNotContain(MercyMission.NessaCongratulated, SavedFlags());
        inventory.CancelOffers();
        Assert.Contains(MercyMission.Carried(inventory), c => c.Item.Id == chest.Id);
    }

    [Fact] public void RemoveAllTakesEveryCarriedChestAndLeavesTheRest()
    {
        var inventory = Inventory();
        inventory.Add(PlayerInventories.MainInventory, 0, 0, Chest);
        inventory.Add(PlayerInventories.MainInventory, 2, 0, Chest);
        inventory.Add(PlayerInventories.MainInventory, 4, 0, Other);
        Assert.Equal(2, MercyMission.RemoveAll(inventory, ChestHash).Count);
        PlayerInventories.Forget(character);
        var reloaded = Inventory();
        Assert.Equal(123u, Assert.Single(reloaded.Items(PlayerInventories.MainInventory)).Item.TypeHash);
    }

    // A data folder with two sparse rooms in one area (two doodads each, too few to fit), both captured in rooms.json:
    // a medicine boat room and another room with a mapboss and a start point.
    string SparseRooms()
    {
        string data = Path.Combine(folder, "rooms");
        Directory.CreateDirectory(Path.Combine(data, "walkability"));
        const string boat = "Metadata/Terrain/Act1/Area2a/Rooms/medicine_boat_1.arm", other = "Metadata/Terrain/Act1/Area3/Rooms/miniboss5.arm";
        object Room(string tag, params (string Name, int X, int Y)[] points) => new
        {
            tag, points = points.Select(p => new { name = p.Name, x = p.X, y = p.Y, angle = 0 }),
            doodads = new[] { new { model = "snow.ao", x = 49, y = 120 }, new { model = "snow.ao", x = 100, y = 118 } },
        };
        File.WriteAllText(Path.Combine(data, "room-points.json"), JsonSerializer.Serialize(new
        {
            note = "test",
            areas = new Dictionary<string, string[]> { ["9_9_9"] = [boat, other] },
            rooms = new Dictionary<string, object>
            {
                [boat] = Room("medicineboat", ("mapboss", 45, 127), ("%area%-bosspack", 62, 154)),
                [other] = Room("miniboss", ("mapboss", 10, 10), ("default", 20, 20)),
            },
        }));
        File.WriteAllText(Path.Combine(data, "walkability", "9_9_9_seed1.doodads.json"), JsonSerializer.Serialize(new
        {
            area = "9_9_9", seed = 1, models = new[] { "snow.ao" }, doodads = new[] { new[] { 0, 300, 300, 0 } },
        }));
        File.WriteAllText(Path.Combine(data, "walkability", "9_9_9_seed1.rooms.json"), JsonSerializer.Serialize(new
        {
            area = "9_9_9", seed = 1, rooms = new[]
            {
                new { path = boat, x = 138, y = 345, width = 138, height = 207, code = 3 },
                new { path = other, x = 600, y = 600, width = 46, height = 46, code = 0 },
            },
        }));
        return data;
    }

    [Fact] public void OnlyTheMedicineBoatRoomsTakeTheirCapturedPlacement()
    {
        var notes = new List<string>();
        var (points, objects, captured) = AreaPopulation.FitRooms("9_9_9", 1, notes, SparseRooms());
        Assert.Equal("Metadata/Terrain/Act1/Area2a/Rooms/medicine_boat_1.arm", Assert.Single(captured));
        // The seed-1 capture's numbers: medicine_boat_1 at (138, 345), code 3, puts mapboss (45, 127) at (265, 437).
        Assert.Contains(points, p => p is { Name: "mapboss", X: 265, Y: 437 });
        Assert.Contains(points, p => p is { Name: "%area%-bosspack", X: 292, Y: 420 });
        // The other sparse room stays unplaced, as before: no extra boss, no new start point.
        Assert.Equal(2, points.Count);
        Assert.DoesNotContain(points, p => p.Name == "default");
        Assert.Empty(objects);
    }

    [Fact] public void ABrokenRoomCaptureMeansNoPlacementNotAnError()
    {
        string data = SparseRooms();
        File.WriteAllText(Path.Combine(data, "walkability", "9_9_9_seed1.rooms.json"), "{ \"area\": \"9_9_9\", \"seed\": 1, \"rooms\": [ { \"path\": 3 } ] ");
        var (points, _, captured) = AreaPopulation.FitRooms("9_9_9", 1, [], data);
        Assert.Empty(points);
        Assert.Empty(captured);
    }

    [Fact] public void OnTheRealCapturesOnlyTheTidalIslandsBoatRoomTakesTheFallback()
    {
        if (RealData() is not { } data || !Directory.Exists(Path.Combine(data, "walkability"))) return;
        var files = Directory.GetFiles(Path.Combine(data, "walkability"), "*_seed*.doodads.json");
        // Height-only test fixtures can create this directory without installing any real room captures.
        if (files.Length == 0) return;
        int areas = 0;
        foreach (var file in files)
        {
            string name = Path.GetFileName(file)[..^".doodads.json".Length];
            int at = name.LastIndexOf("_seed", StringComparison.Ordinal);
            if (!uint.TryParse(name[(at + 5)..], out uint seed)) continue;
            string area = name[..at];
            var (points, _, captured) = AreaPopulation.FitRooms(area, seed, [], data);
            Assert.All(captured, c => Assert.Contains(c, MercyMission.BoatRooms));
            if (area == MercyMission.Area && seed == 1) Assert.Contains(points, p => p is { Name: "mapboss", X: 265, Y: 437 });
            else Assert.Empty(captured);
            areas++;
        }
        Assert.True(areas > 0);
    }

    // QuestRewardOffers 1 and its QuestRewards rows, as the game data has them (columns __col1 index, __col5 rarity).
    string RewardTables()
    {
        string data = Path.Combine(folder, "rewards");
        Directory.CreateDirectory(Path.Combine(data, "tables"));
        void Write(string name, object value) => File.WriteAllText(Path.Combine(data, "tables", name + ".json"), JsonSerializer.Serialize(value));
        Write("QuestFlags", new[] { new { Id = "a1q1-QuestRewardTaken" }, new { Id = MercyMission.QuestRewardTaken } });
        Write("QuestRewardOffers", new object[] { new { QuestFlag = 0 }, new { QuestFlag = 1 } });
        Write("QuestRewards", new object[]
        {
            new { RewardOffer = 1, __col1 = 0, Reward = 0, RewardLevel = 4, RewardStack = 1, Characters = Array.Empty<int>(), __col5 = (int?)0 },
            new { RewardOffer = 1, __col1 = 1, Reward = 2, RewardLevel = 8, RewardStack = 1, Characters = Array.Empty<int>(), __col5 = (int?)1 },
            new { RewardOffer = 0, __col1 = 0, Reward = 1, RewardLevel = 1, RewardStack = 1, Characters = Array.Empty<int>(), __col5 = (int?)null },
        });
        return data;
    }

    // A catalog whose rows are a flask (Charges, Flask), a gem and a body armour.
    string ItemTables()
    {
        string data = TestSaves.Catalog(new TestSaves.Type(FlaskHash, "Metadata/Items/Flasks/FlaskLife2"),
            new TestSaves.Type(GemHash, "Metadata/Items/Gems/SkillGemTest"), new TestSaves.Type(RobeHash, "Metadata/Items/Armours/BodyArmours/Robe"));
        File.WriteAllText(Path.Combine(data, "item-types.json"), JsonSerializer.Serialize(new
        {
            types = new object[]
            {
                new { type = "Metadata/Items/Flasks/FlaskLife2", components = new[] { "Base", "Quality", "LocalStats", "Charges", "Mods", "Usable", "Flask" }, read = 7 },
                new { type = "Metadata/Items/Gems/SkillGemTest", components = new[] { "Base" }, read = 1 },
                new { type = "Metadata/Items/Armours/BodyArmours/Robe", components = new[] { "Base" }, read = 1 },
            },
        }));
        return data;
    }

    [Fact] public void NessasRewardIsANormalFlaskAtItsItemLevelAndMagicArmoursStayRefused()
    {
        string tables = RewardTables(), itemData = ItemTables();
        try
        {
            var catalog = QuestRewardCatalog.Load(tables);
            var offer = catalog.Find(MercyMission.RewardOffer)!;
            Assert.Equal(MercyMission.QuestRewardTaken, offer.TakenFlag);
            var items = new ItemCatalog(itemData);
            var flask = QuestRewardCatalog.Create(offer.Choices[0], items);
            Assert.NotNull(flask);
            Assert.Equal((FlaskHash, (byte)4), (flask!.TypeHash, flask.ItemLevel));
            Assert.Equal(0, offer.Choices[0].Rarity);
            // A magic item at level 8 (offer 8's armours) needs affixes: still refused.
            Assert.Null(QuestRewardCatalog.Create(offer.Choices[1], items));
            // Gems: level 1 only, as before.
            var gem = QuestRewardCatalog.Create(catalog.Find(0)!.Choices[0], items);
            Assert.Equal((GemHash, (byte)1), (gem!.TypeHash, gem.ItemLevel));
            Assert.Null(QuestRewardCatalog.Create(catalog.Find(0)!.Choices[0] with { Level = 4 }, items));
            // A non-flask normal item above level 1 is not a modelled reward either.
            Assert.Null(QuestRewardCatalog.Create(new QuestRewardCatalog.Choice(9, 2, 4, 1, []) { Rarity = 0 }, items));
        }
        finally { Directory.Delete(itemData, true); }
    }
}
