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
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
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

}
